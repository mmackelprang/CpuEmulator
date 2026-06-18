using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuEmulator.SpecImporter;

// ── M5.4 (ADR 0006 Decision 1 / Decision 5): the THIRD disjoint importer arm — the x86 opcode-table
//    pipeline. It is structurally distinct from BOTH the byte-hex OpcodeEntry arm (6502/Z80, single-byte
//    opcode rows) AND the field-grammar arm (68000, field-encoded family rows): an x86 row carries the
//    VARIABLE-LENGTH decode metadata the X86DecodeStructure carrier needs (has-ModR/M, the reg-extends-
//    opcode group flag, the w/s immediate-length bits, the immediate rule) plus the prefix set with roles.
//    The output spec (M8086Spec.cs) declares a live X86DecodeStructure the generator's EmitX86DecodeWalk arm
//    expands; the op BODIES are M5.5 (the Insn rows emit with an empty Op[] — every byte still routes to
//    HandleUndefinedOpcode until the bodies land). This arm NEVER touches the opcode-row or field-grammar
//    arms — 6502/Z80/68000 stay byte-identical.

/// <summary>One x86 prefix-byte row: a byte + its <see cref="X86PrefixKind"/> role. Mirrors the
/// <c>X86Prefix(Value, Role)</c> carrier 1:1 — the emitter writes <c>new X86Prefix(0xNN, X86PrefixRole.*)</c>.
/// <see cref="Source"/> is the provenance citation (carried, not emitted — like the opcode rows).</summary>
public sealed record X86PrefixRow(byte Value, X86PrefixKind Role, string? Source = null);

/// <summary>The importer's mirror of the generator's <c>X86PrefixRole</c> enum (kept in the tool so the
/// SpecImporter does not reference the Core carrier type — the FieldGrammar arm follows the same string-DSL
/// convention). SYNC HAZARD: if X86PrefixRole gains members, add here + in the parse switch below.</summary>
public enum X86PrefixKind { SegmentOverride, Lock, Repeat }

/// <summary>The importer's mirror of the generator's <c>X86ImmediateRule</c> enum. SYNC HAZARD: keep in
/// step with <c>CpuEmulator.Core.Specification.X86ImmediateRule</c>.</summary>
public enum X86ImmediateKind { None, Fixed8, Fixed16, WBit, SWBit, Fixed32 }

/// <summary>One x86 opcode row of the dataset. <see cref="Opcode"/> is the primary byte. When
/// <see cref="RegIsExtension"/> is set the row is an opcode-GROUP member: <see cref="Subfield"/> (0..7) is
/// the ModR/M reg field that selects the operation, and the decode key becomes <c>(opcode&lt;&lt;3)|reg</c>.
/// <see cref="HasModRm"/>/<see cref="WBit"/>/<see cref="SBit"/>/<see cref="Immediate"/> are the
/// variable-length decode metadata; <see cref="Mnemonic"/> is the disassembly label the Insn row carries
/// (the op BODY is empty — M5.5). The DECODE metadata (HasModRm/RegIsExtension/WBit/SBit/Immediate) is
/// authored ONCE per primary opcode and validated identical across a group's members.</summary>
public sealed record X86OpcodeRow(
    byte Opcode,
    string Mnemonic,
    int Subfield,          // -1 ⇒ a plain (non-group) opcode; 0..7 ⇒ a group member's reg field
    bool HasModRm,
    bool RegIsExtension,
    int WBit,              // -1 ⇒ none
    int SBit,              // -1 ⇒ none
    X86ImmediateKind Immediate,
    int ImmediateRegMask = -1,   // M5.5b: the F6/F7 split-immediate gate (-1 ⇒ all regs / not gated)
    string? Source = null);

/// <summary>The x86 dataset: the prefix set + the opcode rows. Loaded from a single JSON object so the
/// prefix roles and the opcode metadata live in one authored file (the extraction-as-acceptance-test
/// artifact — ADR 0001 Decision 6). Order is preserved (the regen byte-identity guard pins it).</summary>
public sealed record X86Dataset(X86PrefixRow[] Prefixes, X86OpcodeRow[] Opcodes)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class Dto
    {
        public PrefixDto[]? Prefixes { get; set; }
        public OpcodeDto[]? Opcodes { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class PrefixDto
    {
        public string? Value { get; set; }
        public string? Role { get; set; }
        public string? Source { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class OpcodeDto
    {
        public string? Opcode { get; set; }
        public string? Mnemonic { get; set; }
        public int? Subfield { get; set; }
        public bool? HasModRm { get; set; }
        public bool? RegIsExtension { get; set; }
        public int? WBit { get; set; }
        public int? SBit { get; set; }
        public string? Immediate { get; set; }
        public int? ImmediateRegMask { get; set; }   // M5.5b: the F6/F7 split-immediate gate
        public string? Source { get; set; }
    }

    public static X86Dataset Load(string path) => Parse(File.ReadAllText(path));

    public static X86Dataset Parse(string json)
    {
        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"x86 dataset JSON is malformed: {ex.Message}", ex);
        }
        if (dto is null) throw new InvalidDataException("x86 dataset is null.");
        if (dto.Prefixes is null || dto.Prefixes.Length == 0)
            throw new InvalidDataException("x86 dataset: 'prefixes' must be non-empty.");
        if (dto.Opcodes is null || dto.Opcodes.Length == 0)
            throw new InvalidDataException("x86 dataset: 'opcodes' must be non-empty.");

        var prefixes = new X86PrefixRow[dto.Prefixes.Length];
        var seenPrefix = new HashSet<byte>();
        for (int i = 0; i < dto.Prefixes.Length; i++)
        {
            var p = dto.Prefixes[i];
            string ctx = $"prefix {i}";
            if (p.Value is null) throw new InvalidDataException($"Missing 'value' at {ctx}.");
            if (p.Role is null) throw new InvalidDataException($"Missing 'role' at {ctx}.");
            byte value = ParseHex8(p.Value, $"value at {ctx}");
            X86PrefixKind role = p.Role switch
            {
                "SegmentOverride" => X86PrefixKind.SegmentOverride,
                "Lock"            => X86PrefixKind.Lock,
                "Repeat"          => X86PrefixKind.Repeat,
                _ => throw new InvalidDataException(
                    $"Unknown prefix role '{p.Role}' at {ctx}: expected SegmentOverride, Lock, or Repeat."),
            };
            if (!seenPrefix.Add(value))
                throw new InvalidDataException($"Duplicate prefix byte 0x{value:X2} at {ctx}.");
            prefixes[i] = new X86PrefixRow(value, role, p.Source);
        }

        var opcodes = new X86OpcodeRow[dto.Opcodes.Length];
        // (opcode, subfield) identity: a plain opcode is (op, -1); a group member is (op, reg). No two rows
        // may collide — the keyed-descriptor table takes the first, so a duplicate is dead authoring.
        var seenKey = new HashSet<(byte, int)>();
        // The per-primary-opcode DECODE metadata must be CONSISTENT across a group's members (they share
        // one X86Opcode declaration). Record the first row's metadata for each opcode; later rows must match.
        // M5.5b: ImmediateRegMask joins the tuple so a group's members must all declare it identically (e.g.
        // all 8 F6 rows must carry immediateRegMask: 3 — they share the one X86Opcode declaration).
        var metaByOpcode = new Dictionary<byte, (bool HasModRm, bool RegIsExtension, int WBit, int SBit, X86ImmediateKind Imm, int ImmRegMask)>();
        // An opcode byte is EITHER plain (one non-group row) OR a group (>=1 reg-extension row) — never both.
        // Mixing them is incoherent (the X86Opcode is declared once; the generator's CPUGEN016 then can't both
        // require a non-group Insn row AND a group Insn row for the one byte). Track each byte's group-ness.
        var groupedByOpcode = new Dictionary<byte, bool>();
        for (int i = 0; i < dto.Opcodes.Length; i++)
        {
            var o = dto.Opcodes[i];
            string ctx = $"opcode {i} ({o.Mnemonic ?? "?"})";
            if (o.Opcode is null) throw new InvalidDataException($"Missing 'opcode' at {ctx}.");
            if (string.IsNullOrWhiteSpace(o.Mnemonic))
                throw new InvalidDataException($"Missing 'mnemonic' at {ctx}.");
            byte opcode = ParseHex8(o.Opcode, $"opcode at {ctx}");
            int subfield = o.Subfield ?? -1;
            bool hasModRm = o.HasModRm ?? false;
            bool regIsExtension = o.RegIsExtension ?? false;
            int wBit = o.WBit ?? -1;
            int sBit = o.SBit ?? -1;
            int immRegMask = o.ImmediateRegMask ?? -1;   // M5.5b
            X86ImmediateKind imm = (o.Immediate ?? "None") switch
            {
                "None"    => X86ImmediateKind.None,
                "Fixed8"  => X86ImmediateKind.Fixed8,
                "Fixed16" => X86ImmediateKind.Fixed16,
                "WBit"    => X86ImmediateKind.WBit,
                "SWBit"   => X86ImmediateKind.SWBit,
                "Fixed32" => X86ImmediateKind.Fixed32,
                _ => throw new InvalidDataException(
                    $"Unknown immediate rule '{o.Immediate}' at {ctx}: expected None/Fixed8/Fixed16/WBit/SWBit/Fixed32."),
            };

            // Coherence rules mirroring the generator's CPUGEN016 cross-checks — fail loudly at load time
            // rather than emitting a spec the generator rejects.
            if (regIsExtension && !hasModRm)
                throw new InvalidDataException(
                    $"RegIsExtension requires HasModRm at {ctx} (the reg field that extends the opcode IS the ModR/M reg).");
            if (regIsExtension && subfield is < 0 or > 7)
                throw new InvalidDataException(
                    $"a group (RegIsExtension) row needs a 'subfield' in 0..7 at {ctx}, got {subfield}.");
            if (!regIsExtension && subfield != -1)
                throw new InvalidDataException(
                    $"'subfield' is only valid on a RegIsExtension (group) row at {ctx}.");
            if (imm is X86ImmediateKind.WBit && wBit < 0)
                throw new InvalidDataException(
                    $"Immediate.WBit needs a WBit position at {ctx} (the walk cannot size the immediate otherwise).");
            if (imm is X86ImmediateKind.SWBit && (wBit < 0 || sBit < 0))
                throw new InvalidDataException(
                    $"Immediate.SWBit needs both a WBit and an SBit position at {ctx}.");
            if (wBit is < -1 or > 7) throw new InvalidDataException($"WBit {wBit} out of range [-1,7] at {ctx}.");
            if (sBit is < -1 or > 7) throw new InvalidDataException($"SBit {sBit} out of range [-1,7] at {ctx}.");
            if (immRegMask is < -1 or > 255)
                throw new InvalidDataException($"ImmediateRegMask {immRegMask} out of range [-1,255] at {ctx}.");

            if (!seenKey.Add((opcode, subfield)))
                throw new InvalidDataException(
                    $"Duplicate (opcode 0x{opcode:X2}, subfield {subfield}) at {ctx}: a second row can never fire.");

            if (groupedByOpcode.TryGetValue(opcode, out bool wasGrouped) && wasGrouped != regIsExtension)
                throw new InvalidDataException(
                    $"opcode 0x{opcode:X2} mixes plain and group (RegIsExtension) rows at {ctx}: a primary " +
                    "opcode byte is EITHER plain OR a reg-extension group, never both.");
            groupedByOpcode[opcode] = regIsExtension;

            var thisMeta = (hasModRm, regIsExtension, wBit, sBit, imm, immRegMask);
            if (metaByOpcode.TryGetValue(opcode, out var firstMeta))
            {
                if (firstMeta != thisMeta)
                    throw new InvalidDataException(
                        $"opcode 0x{opcode:X2} group members carry INCONSISTENT decode metadata at {ctx} " +
                        "(HasModRm/RegIsExtension/WBit/SBit/Immediate/ImmediateRegMask must be identical across a " +
                        "group's rows — they share one X86Opcode declaration).");
            }
            else
            {
                metaByOpcode[opcode] = thisMeta;
            }

            opcodes[i] = new X86OpcodeRow(
                opcode, o.Mnemonic, subfield, hasModRm, regIsExtension, wBit, sBit, imm, immRegMask, o.Source);
        }

        return new X86Dataset(prefixes, opcodes);
    }

    private static byte ParseHex8(string text, string what)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            byte.TryParse(text.AsSpan(2), NumberStyles.HexNumber, null, out var v))
            return v;
        throw new InvalidDataException($"{what}: expected '0xNN' (8-bit hex), got '{text}'.");
    }
}
