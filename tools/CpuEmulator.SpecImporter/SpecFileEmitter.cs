using System.Text;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// A report returned by the emitter summarising what was emitted versus deferred.
/// </summary>
public sealed record ImportReport(
    int Total,
    int Emitted,
    int TodoSemantics,
    int TodoMode)
{
    /// <summary>
    /// Per-mnemonic inventory of dataset rows lacking semantics
    /// (mnemonic → dataset-row count), ordered by mnemonic for stable output.
    /// Printed by the CLI under <c>--report</c>.
    /// </summary>
    public IReadOnlyList<(string Mnemonic, int Rows)> MissingSemanticsInventory { get; init; } = [];

    /// <summary>Returns the single-line summary written to stdout by the CLI.</summary>
    public override string ToString() =>
        $"total={Total} emitted={Emitted} todoSemantics={TodoSemantics} todoMode={TodoMode}";
}

/// <summary>
/// Emits a complete spec-class source file from a loaded dataset + semantics map.
///
/// Emission rules (dataset opcode order throughout — diffs against regenerations are stable):
///   mnemonic in map AND mode in DSL's 13 → real Insn(...) row
///   mnemonic in map, mode NOT supported   → // TODO(mode): ...
///   mnemonic NOT in map                   → // TODO(semantics): ...
///
/// The 13 DSL-supported modes mirror AddrMode in CpuEmulator.Core.Specification.
/// SYNC HAZARD: if AddrMode gains new members this set must expand in concert.
/// See also the MIRROR TABLES block in SpecParser.cs (s_addrModes mirror).
/// </summary>
public static class SpecFileEmitter
{
    // The modes expressible by the DSL (mirrors the AddrMode enum — all members, INCLUDING the M3.2
    // IoPort* members). The emitter writes `AddrMode.{Mode}` literally, so a mode here MUST be an
    // AddrMode enum member or the generated source will not compile. SYNC HAZARD: if AddrMode gains
    // new members this set must expand in concert. See the MIRROR TABLES block in SpecParser.cs.
    //
    // M3.4 (Z80): the Z80 register-shape modes (Register/RegisterIndirect/ImmediateExtended/
    // ExtendedAddress/RelativeJump — M3.4a), the CB-plane Bit mode (M3.4b), and the indexed
    // (IX+d)/(IY+d) Indexed mode (M3.4e-1a) are now AddrMode members and SupportedModes here. The M3.3
    // enumerated finding (the AddrMode vocabulary must grow to express the Z80 modes) is resolved. A row
    // in a mode NOT listed here still emits `// TODO(mode):`. NOTE: a SupportedModes member must also be
    // a JitMode member — the emitter writes `JitMode.{mode}` literally, so the two move together.
    private static readonly HashSet<string> SupportedModes =
    [
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative",
        "IoPortImmediate", "IoPortIndirect",   // M3.2 (additive): the Z80 IN/OUT port-operand modes.
        // M3.4a (additive): the Z80 register-shape modes the base plane needs.
        "Register", "RegisterIndirect", "ImmediateExtended", "ExtendedAddress", "RelativeJump",
        "Bit",   // M3.4b (CB plane): BIT/RES/SET + rotate/shift
        // M3.4e-1a (additive): the indexed (IX+d)/(IY+d) mode. The DD/FD-core indexed rows emit as
        // Indexed; the DDCB/FDCB compound rows decode via the compound-prefix walk (M3.4e-1b).
        "Indexed",
    ];

    /// <summary>
    /// Emits the spec source and returns (source, report).
    /// </summary>
    public static (string Source, ImportReport Report) Emit(
        OpcodeEntry[]  dataset,
        SemanticsMap   map,
        string         datasetPath   = "mos6502-opcodes.json",
        string         semanticsPath = "mos6502-semantics.json",
        // The header's regenerate-command --out path. Defaults to the 6502 spec path so the committed
        // Mos6502Spec.cs header is byte-identical (the RegeneratedSpecTests anchor). The Z80 run passes
        // its own path so the committed Z80Spec.cs header carries the correct regenerate command.
        string         outputPath    = "src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs")
    {
        var sb = new StringBuilder();
        int emitted       = 0;
        int todoSemantics = 0;
        int todoMode      = 0;
        // mnemonic → count of dataset rows lacking semantics (sorted for stable report output)
        var missingSemantics = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // M3.3 (Z80): the single-byte prefix bytes among the rows we actually EMIT as prefixed Insn
        // rows. The DecodeStructure declares exactly these — the generator cross-checks that every
        // declared prefix backs >=1 emitted prefixed Insn row (CPUGEN012), so we must not over-declare.
        // The compound DDCB/FDCB tokens are NOT single-byte prefixes (the enumerated M3.4 finding); a
        // DDCB/FDCB row reaches the M3.4e-3 compound-emission branch instead (the Insn(p1,p2,finalOp,…)
        // overload) and records its (p1,p2) here so the Decode declaration emits the compound PrefixByte.
        var emittedPrefixBytes = new SortedSet<int>();
        // M3.4e-3: the (p1, p2) pairs of compound DDCB/FDCB prefixes backing >=1 emitted compound row.
        // Drives the compound PrefixByte (CompoundWith + DisplacementBeforeOpcode) in the Decode struct.
        var emittedCompoundPrefixes = new SortedSet<(int p1, int p2)>();

        // ── auto-generated header ────────────────────────────────────────────
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//   Tool    : CpuEmulator.SpecImporter");
        sb.AppendLine($"//   Dataset : {datasetPath}");
        sb.AppendLine($"//   Semantics: {semanticsPath}");
        sb.AppendLine($"//   Total rows : {dataset.Length}");
        sb.AppendLine("//   Regenerate :");
        sb.AppendLine("//     dotnet run --project tools/CpuEmulator.SpecImporter -- \\");
        sb.AppendLine($"//       --dataset {datasetPath} \\");
        sb.AppendLine($"//       --semantics {semanticsPath} \\");
        sb.AppendLine($"//       --out {outputPath}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();

        // ── usings ───────────────────────────────────────────────────────────
        sb.AppendLine("using CpuEmulator.Core.Specification;");
        sb.AppendLine("using static CpuEmulator.Core.Specification.Spec;");
        sb.AppendLine();

        // ── namespace + class declaration ────────────────────────────────────
        sb.AppendLine($"namespace {map.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"[CpuSpecification(\"{map.Architecture}\")]");
        sb.AppendLine($"public static class {map.SpecClassName}");
        sb.AppendLine("{");

        // ── Registers table ───────────────────────────────────────────────────
        sb.AppendLine("    public static readonly RegisterDef[] Registers =");
        sb.AppendLine("    [");
        foreach (var reg in map.Registers)
        {
            var rolePart = reg.Role switch
            {
                "StackPointer"   => ", RegisterRole.StackPointer",
                "Status"         => ", RegisterRole.Status",
                "ProgramCounter" => ", RegisterRole.ProgramCounter",
                _                => ""
            };
            // M3.4a: a pair VIEW carries HighHalf/LowHalf (the Z80 BC/DE/HL/AF over the 8-bit halves).
            var pairPart = reg.HighHalf is not null && reg.LowHalf is not null
                ? $", HighHalf: \"{reg.HighHalf}\", LowHalf: \"{reg.LowHalf}\""
                : "";
            sb.AppendLine($"        new(\"{reg.Name}\", {reg.Bits}{rolePart}{pairPart}),");
        }
        sb.AppendLine("    ];");
        sb.AppendLine();

        // ── FlagLayout (M3.4a) ─────────────────────────────────────────────────
        // Emitted ONLY when the semantics map declares a flag layout (the Z80's S=7..C=0). The 6502
        // declares none, so this block is absent and Mos6502Spec.cs stays byte-identical (the guard).
        if (map.Flags.Length > 0)
        {
            string bits = string.Join(", ", map.Flags.Select(b => $"new(\"{b.Name}\", {b.Bit})"));
            sb.AppendLine($"    public static readonly FlagLayout Flags = new([{bits}]);");
            sb.AppendLine();
        }

        // ── Instructions collection ───────────────────────────────────────────
        // Emitted into a separate buffer first: the DecodeStructure declaration (which must precede
        // Instructions in source order for readability) needs the set of EMITTED prefix bytes, known
        // only after this loop. We assemble registers → DecodeStructure → Instructions at the end.
        var insnSb = new StringBuilder();
        insnSb.AppendLine("    public static readonly InstructionDef[] Instructions =");
        insnSb.AppendLine("    [");

        // M3.4a: for the Z80 BASE plane (no prefix), the per-opcode ops are computed algorithmically
        // from the opcode byte (the regular octal encoding) — the dataset carries no operand field and
        // the same mnemonic (LD/ADD/INC/DEC) maps to many distinct ops by the register/pair the opcode
        // selects. The per-mnemonic map can't express that; Z80BaseSemantics resolves it (recorded
        // deviation, see that file). Other architectures (the 6502) keep the per-mnemonic map untouched.
        bool isZ80 = string.Equals(map.Architecture, "z80", StringComparison.OrdinalIgnoreCase);

        foreach (var entry in dataset)
        {
            // The Z80 base-plane (null prefix), CB-plane (0xCB prefix), AND ED-core (0xED prefix,
            // 0x40–0x7F) algorithmic ops. Z80EdSemantics.OpsFor returns null for ED opcodes OUTSIDE
            // the core (the block ops 0xA0–0xBB) so those rows stay // TODO(semantics) (out of scope).
            string? z80Ops =
                isZ80 && entry.Prefix is null
                    ? Z80BaseSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16), entry.Mnemonic, entry.Mode)
                : isZ80 && entry.Prefix == "0xCB"
                    ? Z80CbSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16))
                : isZ80 && entry.Prefix == "0xED"
                    ? Z80EdSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16))
                : isZ80 && entry.Prefix == "0xDD"
                    ? Z80DdFdSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16), entry.Mnemonic, entry.Mode, isIy: false)
                : isZ80 && entry.Prefix == "0xFD"
                    ? Z80DdFdSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16), entry.Mnemonic, entry.Mode, isIy: true)
                // M3.4e-3: the compound DDCB/FDCB plane — the ops are DERIVED from the FINAL opcode byte
                // by the CB octal encoding re-targeted onto (IX+d)/(IY+d) (Z80DdCbSemantics, total over
                // 0x00..0xFF). The index register (IX vs IY) is read from the compound key's p1 at emit time.
                : isZ80 && (entry.Prefix == "0xDDCB" || entry.Prefix == "0xFDCB")
                    ? Z80DdCbSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16))
                : null;

            string? opsText;
            bool hasSemantics = z80Ops is not null
                ? (opsText = z80Ops) is not null
                : map.Mnemonics.TryGetValue(entry.Mnemonic, out opsText);
            bool modeSupported = SupportedModes.Contains(entry.Mode);
            // A single-byte prefix is emittable as the M3.1b Insn(prefix, opcode, …) overload; the
            // compound DDCB/FDCB tokens are NOT (the enumerated M3.4 finding) — never reached here
            // because their Indexed mode is not in SupportedModes.
            bool singleBytePrefix = entry.Prefix is { } p && TryParsePrefixByte(p, out _);
            // M3.4e-3: a COMPOUND DDCB/FDCB row — detected by the 4-hex prefix token ("0xDDCB"/"0xFDCB").
            bool compoundPrefix = entry.Prefix is { } cp && TryParseCompoundPrefix(cp, out _, out _);

            if (hasSemantics && modeSupported && compoundPrefix)
            {
                // Emit the M3.4e-1b compound Insn(p1, p2, finalOp, …) overload (KeyShape.Compound). The
                // SAME DD/FD prefix byte ALSO backs the plain core rows; declaring it compound here turns
                // the decode walk's compound routing ON for the DD/FD-then-CB stream (H4).
                TryParseCompoundPrefix(entry.Prefix!, out int p1, out int p2);
                insnSb.AppendLine(
                    $"        Insn(0x{p1:X2}, 0x{p2:X2}, {entry.Opcode}, \"{entry.Mnemonic}\", AddrMode.{entry.Mode}, {opsText}),");
                emittedPrefixBytes.Add(p1);              // so the Decode declaration includes the prefix byte
                emittedCompoundPrefixes.Add((p1, p2));   // (DD, CB) / (FD, CB) — drives the compound PrefixByte
                emitted++;
            }
            else if (hasSemantics && modeSupported && (entry.Prefix is null || singleBytePrefix))
            {
                if (entry.Prefix is null)
                {
                    // Base-plane row — the EXISTING single-byte Insn(opcode, …) form (byte-identical
                    // to the 6502 emission shape — the 6502 guard).
                    insnSb.AppendLine(
                        $"        Insn({entry.Opcode}, \"{entry.Mnemonic}\", AddrMode.{entry.Mode}, {opsText}),");
                }
                else
                {
                    // Prefixed row — the M3.1b Insn(prefix, opcode, …) overload (KeyShape.PrefixedOpcode).
                    TryParsePrefixByte(entry.Prefix, out int prefixByte);
                    insnSb.AppendLine(
                        $"        Insn(0x{prefixByte:X2}, {entry.Opcode}, \"{entry.Mnemonic}\", AddrMode.{entry.Mode}, {opsText}),");
                    emittedPrefixBytes.Add(prefixByte);
                }
                emitted++;
            }
            else if (hasSemantics)
            {
                // Semantics known but mode not yet supported by the DSL (a Z80 register-shape mode, or
                // a compound DDCB/FDCB token — the enumerated M3.4 findings). The TODO comment carries
                // the plane-qualified Key so a prefixed row is unambiguous.
                insnSb.AppendLine(
                    $"        // TODO(mode): {entry.Key} {entry.Mnemonic} {entry.Mode} — awaiting AddrMode support");
                todoMode++;
            }
            else
            {
                // No semantics yet — the TODO(vocab) majority. Plane-qualified Key for prefixed rows.
                insnSb.AppendLine(
                    $"        // TODO(semantics): {entry.Key} {entry.Mnemonic} {entry.Mode} — awaiting micro-op vocabulary");
                todoSemantics++;
                missingSemantics[entry.Mnemonic] = missingSemantics.GetValueOrDefault(entry.Mnemonic) + 1;
            }
        }

        insnSb.AppendLine("    ];");

        // ── DecodeStructure declaration (M3.3, Z80) ───────────────────────────
        // Emitted ONLY when there are EMITTED prefixed rows (a prefixed ISA). The 6502 emits no
        // prefixed rows, so this block is absent and Mos6502Spec.cs stays byte-identical (the guard).
        // Declares exactly the prefix bytes backing emitted prefixed Insn rows (the CPUGEN012
        // cross-check). ModRmOpcodes/SubFieldOpcodes empty — the Z80 bit plane enumerates each bit op
        // as its own byte; the DDCB/FDCB compound forms are the enumerated M3.4 finding (not declared).
        if (emittedPrefixBytes.Count > 0)
        {
            sb.AppendLine("    public static readonly DecodeStructure Decode = new(");
            // M3.4e-3: a prefix byte that backs a compound row (DD/FD compounding with CB) emits the
            // compound PrefixByte (CompoundWith + DisplacementBeforeOpcode); a plain prefix (CB/ED) emits
            // the bare PrefixByte. The DD/FD bytes back BOTH plain core rows AND compound rows — the
            // compound metadata turns the walk's compound routing ON; the plain core rows still take the
            // plain-prefix arm because the byte after DD/FD is the opcode, not CB.
            var compoundFirstBytes = emittedCompoundPrefixes.Select(c => c.p1).ToHashSet();
            string prefixList = string.Join(", ", emittedPrefixBytes.Select(b =>
                compoundFirstBytes.Contains(b)
                    ? $"new PrefixByte(0x{b:X2}, CompoundWith: 0x{emittedCompoundPrefixes.First(c => c.p1 == b).p2:X2}, DisplacementBeforeOpcode: true)"
                    : $"new PrefixByte(0x{b:X2})"));
            sb.AppendLine($"        Prefixes: [{prefixList}],");
            sb.AppendLine("        ModRmOpcodes: [],");
            sb.AppendLine("        SubFieldOpcodes: []);");
            sb.AppendLine();
        }

        sb.Append(insnSb);
        sb.AppendLine("}");

        var report = new ImportReport(dataset.Length, emitted, todoSemantics, todoMode)
        {
            MissingSemanticsInventory = [.. missingSemantics.Select(kv => (kv.Key, kv.Value))],
        };
        return (sb.ToString(), report);
    }

    /// <summary>Parses a SINGLE-byte prefix token ("0xCB"/"0xED"/"0xDD"/"0xFD") into its byte value.
    /// Returns false for the compound DDCB/FDCB tokens (value &gt; 0xFF) — those are NOT expressible
    /// as a single M3.1b PrefixByte/Insn prefix (the enumerated M3.4 finding); a compound-prefixed
    /// row is therefore never emitted as a prefixed Insn row.</summary>
    private static bool TryParsePrefixByte(string prefix, out int value)
    {
        value = 0;
        if (prefix.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(prefix.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var v) &&
            v is >= 0 and <= 0xFF)
        {
            value = v;
            return true;
        }
        return false;
    }

    /// <summary>M3.4e-3: parse a COMPOUND prefix token ("0xDDCB"/"0xFDCB", 4 hex digits) into its two
    /// bytes (0xDD, 0xCB). Returns false for a single-byte or null prefix. The compound row emits the
    /// Insn(p1, p2, finalOp, …) overload (KeyShape.Compound) and a compound PrefixByte.</summary>
    private static bool TryParseCompoundPrefix(string prefix, out int p1, out int p2)
    {
        p1 = p2 = 0;
        if (prefix is not { Length: 6 } || !prefix.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(prefix.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out p1)
            && int.TryParse(prefix.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out p2);
    }
}
