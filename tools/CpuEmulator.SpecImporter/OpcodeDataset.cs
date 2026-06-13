using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// A single row from a vendored opcode dataset (6502 or Z80).
/// Field names match the JSON camelCase keys exactly.
///
/// M3.3 (Z80): adds the optional <see cref="Prefix"/> + <see cref="SubField"/> fields and the
/// plane-qualified <see cref="Key"/>. A 6502/base-plane row leaves both null — its <see cref="Key"/>
/// is its bare <see cref="Opcode"/>, byte-identical to the pre-M3.3 shape.
/// </summary>
public sealed record OpcodeEntry(
    string  Opcode,            // the FINAL opcode byte "0xNN" (the op within its plane)
    string  Mnemonic,
    string  Mode,
    int     Bytes,
    int     Cycles,            // Z80: T-states (total clock periods). 6502: machine cycles.
    bool    PageCrossPenalty,  // always false for the Z80 (recorded)
    string? Source = null,
    string? Prefix = null,     // M3.3: "0xCB"/"0xED"/"0xDD"/"0xFD"/"0xDDCB"/"0xFDCB"; null = base plane
    int?    SubField = null)   // M3.3: reserved for opcode-group encodings; null for the Z80 bit plane
{
    /// <summary>The plane-qualified identity used for uniqueness + the cross-source diff.
    /// Base plane: "0xNN". Prefixed: "0xPREFIX:0xNN" (e.g. "0xED:0xB0"). This is what makes
    /// ED B0 (LDIR) distinct from 0xB0 (OR B) — the single-byte key cannot.</summary>
    public string Key => Prefix is null ? Opcode : $"{Prefix}:{Opcode}";
}

/// <summary>
/// Loads and validates the vendored 6502 opcode dataset.
///
/// Valid mode vocabulary (13 modes — full 6502 documented set):
///   Implied, Accumulator, Immediate,
///   ZeroPage, ZeroPageX, ZeroPageY,
///   Absolute, AbsoluteX, AbsoluteY,
///   IndirectX, IndirectY, Indirect, Relative
///
/// Byte-count rules (derived from 6502 encoding):
///   Implied/Accumulator           → 1 byte
///   Immediate/ZeroPage/ZeroPageX/ZeroPageY/IndirectX/IndirectY/Relative → 2 bytes
///   Absolute/AbsoluteX/AbsoluteY/Indirect → 3 bytes
/// </summary>
public static class OpcodeDataset
{
    // The fixed-length 6502 modes — every documented 6502 row uses one of these, and each has a
    // statically-known byte count enforced below. UNCHANGED by M3.1b (the 6502 rules are intact).
    private static readonly HashSet<string> FixedLengthModes =
    [
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative"
    ];

    // M3.1b seam (Ground truth, notion (4)): modes whose length is COMPUTED by the decode walk from
    // a mid-stream byte (the 8086 ModR/M case), NOT a static byte count. A row marked with one of
    // these is ACCEPTED with its declared base byte count and the byte-count equality is SKIPPED —
    // the importer does not yet CONSUME the computed tail (no Z80/8086 dataset in M3.1b); it only
    // stops the schema from FORBIDDING such a row. Empty of any 6502 mode by construction.
    private static readonly HashSet<string> ComputedLengthModes =
    [
        "ModRm"
    ];

    // M3.3 (Z80): the Z80-extended mode vocabulary (Ground truth E.1). ACCEPTED by the dataset loader
    // (dataset truth, like the 6502 loader accepts all 13 modes even when the DSL emitted fewer). The
    // fixed-length Z80 modes carry a base byte count enforced in Task 2's ExpectedBytes; the
    // prefix-determined-length modes (Indexed) route through the computed-length seam. The shared
    // Implied/Immediate are already in FixedLengthModes above. Additive — the 6502 modes are UNCHANGED.
    private static readonly HashSet<string> Z80FixedLengthModes =
    [
        "Register", "RegisterIndirect", "ImmediateExtended",
        "ExtendedAddress", "RelativeJump", "Bit",
        // The Z80 I/O-port modes. "IoPort" is the generic dataset spelling; "IoPortImmediate"/
        // "IoPortIndirect" are the AddrMode-backed names (M3.2) used for the base-plane IN A,(n)/
        // OUT (n),A so the emitter can emit a real port Insn. All carry the op + port-byte base = 2.
        "IoPort", "IoPortImmediate", "IoPortIndirect",
    ];

    // The Z80 indexed mode (IX+d)/(IY+d) is length-determined by its prefix (DD/FD op d = 3 bytes;
    // a DDCB compound = 4). Routed through the computed-length seam so the fixed-length byte equality
    // is SKIPPED (the prefix + the dataset's declared `bytes` carry the truth).
    private static readonly HashSet<string> Z80ComputedLengthModes =
    [
        "Indexed",
    ];

    // The full accepted vocabulary: the fixed-length 6502 modes + the Z80 fixed-length modes plus the
    // computed-length markers (the 6502 ModRm seam + the Z80 Indexed mode).
    private static readonly HashSet<string> ValidModes =
        [.. FixedLengthModes, .. Z80FixedLengthModes, .. ComputedLengthModes, .. Z80ComputedLengthModes];

    private static readonly Regex OpcodeFormat =
        new("^0x[0-9A-Fa-f]{2}$", RegexOptions.Compiled);

    // M3.3 (Z80): the recognized prefix tokens. A row's `prefix` (when present) must be one of these.
    // The four single-byte prefixes (CB/ED/DD/FD) plus the two compound forms (DDCB/FDCB — the
    // displacement-then-opcode planes). A null prefix = base plane (the 6502 shape). Ground truth A.
    private static readonly HashSet<string> RecognizedPrefixes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "0xCB", "0xED", "0xDD", "0xFD", "0xDDCB", "0xFDCB",
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Strict deserialization DTO. This is a curated, hand-edited file:
    // - unknown members (typo'd keys) must fail loudly, not be silently
    //   skipped leaving the real field at its default (Disallow);
    // - missing members surface as nulls which the validation below rejects
    //   with row context, instead of flowing into the emitter.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class OpcodeEntryDto
    {
        public string? Opcode { get; set; }
        public string? Mnemonic { get; set; }
        public string? Mode { get; set; }
        public int?    Bytes { get; set; }
        public int?    Cycles { get; set; }
        public bool?   PageCrossPenalty { get; set; }
        /// <summary>Optional provenance citation (e.g. "MOS hardware manual p.143, table A-1").</summary>
        public string? Source { get; set; }
        /// <summary>M3.3 (Z80): optional prefix plane ("0xCB"/"0xED"/.../"0xDDCB"); null = base plane.</summary>
        public string? Prefix { get; set; }
        /// <summary>M3.3 (Z80): optional opcode-group sub-field; null for the Z80 bit plane.</summary>
        public int?    SubField { get; set; }
    }

    /// <summary>Loads the dataset from a file path.</summary>
    public static OpcodeEntry[] Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    /// <summary>
    /// Parses and validates the dataset from a JSON string.
    /// Throws <see cref="InvalidDataException"/> on any violation.
    /// </summary>
    public static OpcodeEntry[] Parse(string json)
    {
        OpcodeEntryDto[]? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<OpcodeEntryDto[]>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Opcode dataset JSON is malformed: {ex.Message}", ex);
        }

        if (dtos is null || dtos.Length == 0)
            throw new InvalidDataException("Opcode dataset is empty.");

        var entries = new OpcodeEntry[dtos.Length];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < dtos.Length; i++)
        {
            var d = dtos[i];
            var ctx = $"row {i} ({d.Opcode ?? "?"} {d.Mnemonic ?? "?"} {d.Mode ?? "?"})";

            // Required fields — a missing key deserializes to null and must
            // be rejected here, never passed through to the emitter.
            if (d.Opcode is null)
                throw new InvalidDataException($"Missing required field 'opcode' at {ctx}.");
            if (d.Mnemonic is null)
                throw new InvalidDataException($"Missing required field 'mnemonic' at {ctx}.");
            if (d.Mode is null)
                throw new InvalidDataException($"Missing required field 'mode' at {ctx}.");
            if (d.Bytes is null)
                throw new InvalidDataException($"Missing required field 'bytes' at {ctx}.");
            if (d.Cycles is null)
                throw new InvalidDataException($"Missing required field 'cycles' at {ctx}.");
            if (d.PageCrossPenalty is null)
                throw new InvalidDataException($"Missing required field 'pageCrossPenalty' at {ctx}.");

            // Opcode format: exactly "0xNN" hex
            if (!OpcodeFormat.IsMatch(d.Opcode))
                throw new InvalidDataException(
                    $"Opcode format invalid at {ctx}: expected '0xNN' (two hex digits), got '{d.Opcode}'.");

            // M3.3 (Z80): prefix vocabulary gate. A present prefix must be a recognized token; a null
            // prefix = base plane (the 6502 shape). The prefix is part of the plane-qualified Key.
            if (d.Prefix is not null && !RecognizedPrefixes.Contains(d.Prefix))
                throw new InvalidDataException(
                    $"Unrecognized prefix '{d.Prefix}' at {ctx}: expected one of " +
                    $"{string.Join(", ", RecognizedPrefixes)} (or omit for a base-plane row).");

            // The plane-qualified key: "0xNN" for a base-plane row (== Opcode, byte-identical to the
            // 6502 path), "0xPREFIX:0xNN" for a prefixed row. ED B0 (LDIR) is distinct from 0xB0 (OR B).
            var key = d.Prefix is null ? d.Opcode : $"{d.Prefix}:{d.Opcode}";

            // Unique plane-qualified key (Task 2 moved uniqueness from the bare Opcode to the Key, so
            // ED B0 and base B0 coexist; a null prefix makes Key == Opcode — the 6502 path is unchanged).
            if (!seen.Add(key))
                throw new InvalidDataException($"Duplicate opcode {key} at {ctx}.");

            // Mode vocabulary
            if (!ValidModes.Contains(d.Mode))
                throw new InvalidDataException($"Unknown mode '{d.Mode}' at {ctx}.");

            // Byte-count consistency.
            //   • Base-plane (null prefix) FIXED-length 6502 mode → the static 6502 byte count
            //     (UNCHANGED — the regression guard; every 6502 row validates byte-for-byte).
            //   • A prefixed FIXED-length row → prefix-length + the base mode bytes (a CB/ED/DD/FD
            //     prefix adds one byte; a DDCB/FDCB compound adds two). So `ED B0` LDIR (Implied) = 2.
            //   • A computed-length mode (the 6502 ModRm seam, or the Z80 Indexed mode) is accepted
            //     with its declared `bytes` and the equality is SKIPPED — the length is determined by
            //     the prefix/mid-stream byte, not a static rule.
            bool fixedLength = FixedLengthModes.Contains(d.Mode) || Z80FixedLengthModes.Contains(d.Mode);
            if (fixedLength)
            {
                var expectedBytes = ExpectedBytes(d.Mode) + PrefixByteLength(d.Prefix);
                if (d.Bytes.Value != expectedBytes)
                    throw new InvalidDataException(
                        $"Byte count mismatch at {ctx}: mode {d.Mode}" +
                        (d.Prefix is null ? "" : $" with prefix {d.Prefix}") +
                        $" requires {expectedBytes} bytes, got {d.Bytes}.");
            }

            entries[i] = new OpcodeEntry(
                d.Opcode, d.Mnemonic, d.Mode, d.Bytes.Value, d.Cycles.Value, d.PageCrossPenalty.Value,
                d.Source, d.Prefix, d.SubField);
        }

        return entries;
    }

    // The static BASE byte count for a FIXED-length mode (6502 or Z80), EXCLUDING any prefix byte(s)
    // — the prefix length is added separately (PrefixByteLength). Only called for fixed-length modes
    // (a computed-length-marked row skips the byte-count equality entirely — the M3.1b/Indexed seam).
    private static int ExpectedBytes(string mode) => mode switch
    {
        // ── shared / 6502 modes (UNCHANGED) ──
        "Implied" or "Accumulator" => 1,
        "Immediate" or "ZeroPage" or "ZeroPageX" or "ZeroPageY"
            or "IndirectX" or "IndirectY" or "Relative" => 2,
        "Absolute" or "AbsoluteX" or "AbsoluteY" or "Indirect" => 3,
        // ── Z80 fixed-length modes: the BASE bytes (op + own operand), EXCLUDING any prefix byte
        //    (the prefix is added by PrefixByteLength). So a CB-plane Bit op is base 1 (op) + 1 (CB) = 2;
        //    an unprefixed JR is base 2 + 0; an ED-prefixed ExtendedAddress is base 3 + 1 = 4. ──
        "Register" or "RegisterIndirect" or "Bit" => 1,  // OR r ; (HL) ; CB-plane BIT/SET/RES op byte
        "RelativeJump" or "IoPort"
            or "IoPortImmediate" or "IoPortIndirect" => 2,  // JR/DJNZ PC+d ; IN A,(n)/OUT (n),A
        "ImmediateExtended" or "ExtendedAddress" => 3,    // LD HL,nn ; LD (nn),A — 16-bit operand
        _ => throw new InvalidOperationException($"Unhandled mode: {mode}") // unreachable after vocabulary check
    };

    // The number of leading prefix bytes a row carries: 0 (base plane), 1 (CB/ED/DD/FD), or 2
    // (the DDCB/FDCB compound forms — DD CB / FD CB). Added to the mode's base byte count above.
    private static int PrefixByteLength(string? prefix) => prefix switch
    {
        null => 0,
        "0xDDCB" or "0xFDCB" => 2,
        _ => 1,   // a recognized single-byte prefix (the vocabulary gate already ran)
    };
}
