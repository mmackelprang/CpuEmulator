using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// A single row from the vendored 6502 opcode dataset.
/// Field names match the JSON camelCase keys exactly.
/// </summary>
public sealed record OpcodeEntry(
    string  Opcode,
    string  Mnemonic,
    string  Mode,
    int     Bytes,
    int     Cycles,
    bool    PageCrossPenalty,
    string? Source = null);

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

    // The full accepted vocabulary: the fixed-length 6502 modes plus the computed-length markers.
    private static readonly HashSet<string> ValidModes =
        [.. FixedLengthModes, .. ComputedLengthModes];

    private static readonly Regex OpcodeFormat =
        new("^0x[0-9A-Fa-f]{2}$", RegexOptions.Compiled);

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

            // Unique opcode
            if (!seen.Add(d.Opcode))
                throw new InvalidDataException($"Duplicate opcode {d.Opcode} at {ctx}.");

            // Mode vocabulary
            if (!ValidModes.Contains(d.Mode))
                throw new InvalidDataException($"Unknown mode '{d.Mode}' at {ctx}.");

            // Byte-count consistency — enforced for the FIXED-length 6502 modes (UNCHANGED). A
            // computed-length-marked row (ModRm) is accepted with its declared base byte count and
            // the equality is SKIPPED (M3.1b seam: the schema must not FORBID a future computed-length
            // row; it does not yet consume the tail).
            if (FixedLengthModes.Contains(d.Mode))
            {
                var expectedBytes = ExpectedBytes(d.Mode);
                if (d.Bytes.Value != expectedBytes)
                    throw new InvalidDataException(
                        $"Byte count mismatch at {ctx}: mode {d.Mode} requires {expectedBytes} bytes, got {d.Bytes}.");
            }

            entries[i] = new OpcodeEntry(
                d.Opcode, d.Mnemonic, d.Mode, d.Bytes.Value, d.Cycles.Value, d.PageCrossPenalty.Value, d.Source);
        }

        return entries;
    }

    // The static byte count for a FIXED-length 6502 mode. Only called for FixedLengthModes (a
    // computed-length-marked row skips the byte-count equality entirely — M3.1b seam).
    private static int ExpectedBytes(string mode) => mode switch
    {
        "Implied" or "Accumulator" => 1,
        "Immediate" or "ZeroPage" or "ZeroPageX" or "ZeroPageY"
            or "IndirectX" or "IndirectY" or "Relative" => 2,
        "Absolute" or "AbsoluteX" or "AbsoluteY" or "Indirect" => 3,
        _ => throw new InvalidOperationException($"Unhandled mode: {mode}") // unreachable after vocabulary check
    };
}
