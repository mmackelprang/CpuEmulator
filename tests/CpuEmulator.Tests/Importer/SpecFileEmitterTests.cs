using System.IO;
using CpuEmulator.SpecImporter;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Tests for the SpecFileEmitter and SpecImportEngine (Task 4 / updated in Task 8).
///
/// Expected emitted-row count derivation (pinned constant):
///   Filter the 151-row dataset to rows where:
///     (1) mnemonic ∈ the 56-entry semantics map, AND
///     (2) mode ∈ the 13 supported DSL modes (all AddrMode members)
///   Running that filter against the real data files yields EXACTLY 151 rows:
///     every dataset mnemonic maps now (BRK/RTI landed in 3b-ii); 56-mnemonic map ×
///     13 supported modes covers all 151 rows.
///   The test below also derives this count independently at runtime and asserts it
///   matches both the filter result and the engine's reported emitted count.
/// </summary>
public class SpecFileEmitterTests
{
    private static string DatasetPath   => DataPath.Get("mos6502-opcodes.json");
    private static string SemanticsPath => DataPath.Get("mos6502-semantics.json");

    // The 13 addressing modes supported by the DSL (all AddrMode enum members as of Task 8).
    // SYNC HAZARD: if the DSL gains new modes this set must expand in concert with AddrMode.cs.
    private static readonly HashSet<string> SupportedModes =
    [
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative",
    ];

    private (string source, ImportReport report) RunEngine()
    {
        var dataset  = OpcodeDataset.Load(DatasetPath);
        var map      = SemanticsMap.Load(SemanticsPath);
        return SpecImportEngine.Run(dataset, map);
    }

    // ─── counts report ──────────────────────────────────────────────────────

    [Fact]
    public void Report_Total_Is_151()
    {
        var (_, report) = RunEngine();
        Assert.Equal(151, report.Total);
    }

    [Fact]
    public void Report_Counts_Sum_To_Total()
    {
        var (_, report) = RunEngine();
        Assert.Equal(report.Total, report.Emitted + report.TodoSemantics + report.TodoMode);
    }

    [Fact]
    public void Report_Emitted_Matches_Filter_Derivation()
    {
        // Derive independently in the test (not from the engine) then compare.
        // This pins the constant at 33 with a clear derivation trail.
        // If the dataset or semantics map changes, this test will catch the drift.
        var dataset  = OpcodeDataset.Load(DatasetPath);
        var map      = SemanticsMap.Load(SemanticsPath);

        int derivedCount = dataset.Count(
            e => map.Mnemonics.ContainsKey(e.Mnemonic) && SupportedModes.Contains(e.Mode));

        // Pinned constant: 151 rows (derived 2026-06-12 from the 56-mnemonic map ×
        // the 13 supported modes intersected with the 151-row dataset; all mnemonics map now).
        const int ExpectedEmitted = 151;
        Assert.Equal(ExpectedEmitted, derivedCount);

        var (_, report) = SpecImportEngine.Run(dataset, map);
        Assert.Equal(ExpectedEmitted, report.Emitted);
    }

    // ─── per-mnemonic missing-semantics inventory (3b-ii: every mnemonic maps) ──

    [Fact]
    public void Report_Inventory_Is_Empty()
    {
        var (_, report) = RunEngine();
        Assert.Empty(report.MissingSemanticsInventory);
        Assert.Equal(0, report.TodoSemantics);
    }

    // ─── 11-row regression anchor ───────────────────────────────────────────
    // Each of the 11 live Mos6502Spec.cs rows must appear verbatim in the
    // importer output (whitespace-normalized to single-space for comparison
    // because the live spec uses alignment padding — the importer is opcode-ordered
    // and does not pad).

    [Theory]
    [InlineData("""Insn(0xA9, "LDA", AddrMode.Immediate, [Load("A"), SetNZ("A")]),""")]
    [InlineData("""Insn(0xA5, "LDA", AddrMode.ZeroPage, [Load("A"), SetNZ("A")]),""")]
    [InlineData("""Insn(0xAD, "LDA", AddrMode.Absolute, [Load("A"), SetNZ("A")]),""")]
    [InlineData("""Insn(0x85, "STA", AddrMode.ZeroPage, [Store("A")]),""")]
    [InlineData("""Insn(0x8D, "STA", AddrMode.Absolute, [Store("A")]),""")]
    [InlineData("""Insn(0xA2, "LDX", AddrMode.Immediate, [Load("X"), SetNZ("X")]),""")]
    [InlineData("""Insn(0xAA, "TAX", AddrMode.Implied, [Transfer("A", "X"), SetNZ("X")]),""")]
    [InlineData("""Insn(0xE8, "INX", AddrMode.Implied, [Increment("X"), SetNZ("X")]),""")]
    [InlineData("""Insn(0x4C, "JMP", AddrMode.Absolute, [Jump()]),""")]
    [InlineData("""Insn(0xD0, "BNE", AddrMode.Relative, [BranchIf(Flag.Z, false)]),""")]
    [InlineData("""Insn(0xEA, "NOP", AddrMode.Implied, []),""")]
    [InlineData("""Insn(0x00, "BRK", AddrMode.Implied, [Brk()]),""")]
    [InlineData("""Insn(0x40, "RTI", AddrMode.Implied, [Rti()]),""")]
    public void Output_Contains_Anchor_Row(string expectedRow)
    {
        var (source, _) = RunEngine();
        // Normalize all whitespace runs to single spaces for comparison — the
        // live spec uses alignment padding but the importer uses single spaces.
        var normalizedSource = NormalizeWhitespace(source);
        Assert.Contains(expectedRow, normalizedSource);
    }

    // ─── TODO rows ──────────────────────────────────────────────────────────

    [Fact]
    public void Output_Contains_No_TODO_Semantics()
    {
        // All 56 dataset mnemonics now map (BRK/RTI landed in 3b-ii) — no TODO rows remain.
        var (source, report) = RunEngine();
        Assert.DoesNotContain("TODO(semantics)", source);
        Assert.Equal(0, report.TodoSemantics);
    }

    [Fact]
    public void Output_Contains_No_TODO_Mode_And_Report_TodoMode_Is_Zero()
    {
        // Every dataset mode is now expressible by the DSL (all 13 AddrMode members supported).
        var (source, report) = RunEngine();
        Assert.DoesNotContain("TODO(mode)", source);
        Assert.Equal(0, report.TodoMode);
    }

    // ─── opcode ordering ─────────────────────────────────────────────────────

    [Fact]
    public void Output_Is_In_Ascending_Opcode_Order_0x4C_Before_0x85()
    {
        // 0x4C (JMP Absolute) must appear before 0x85 (STA ZeroPage) in the output.
        // Note: the hand-written spec groups by mnemonic; the importer is opcode-ordered.
        var (source, _) = RunEngine();
        var idx4C = source.IndexOf("0x4C", StringComparison.Ordinal);
        var idx85 = source.IndexOf("0x85", StringComparison.Ordinal);
        Assert.True(idx4C >= 0, "0x4C not found in output");
        Assert.True(idx85 >= 0, "0x85 not found in output");
        Assert.True(idx4C < idx85, $"0x4C (at {idx4C}) should come before 0x85 (at {idx85})");
    }

    // ─── file scaffold ───────────────────────────────────────────────────────

    [Fact]
    public void Output_Contains_AutoGenerated_Header()
    {
        var (source, _) = RunEngine();
        Assert.Contains("auto-generated", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SpecImporter", source);
        Assert.Contains("dotnet run", source);
    }

    [Fact]
    public void Output_Contains_CpuSpecification_Attribute()
    {
        var (source, _) = RunEngine();
        Assert.Contains("[CpuSpecification(\"mos6502\")]", source);
    }

    [Fact]
    public void Output_Contains_Registers_Table()
    {
        var (source, _) = RunEngine();
        // Should have all 6 register names from the config
        Assert.Contains("\"A\"", source);
        Assert.Contains("\"X\"", source);
        Assert.Contains("\"Y\"", source);
        Assert.Contains("\"S\"", source);
        Assert.Contains("\"P\"", source);
        Assert.Contains("\"PC\"", source);
        Assert.Contains("RegisterRole.ProgramCounter", source);
        Assert.Contains("RegisterRole.StackPointer", source);
        Assert.Contains("RegisterRole.Status", source);
    }

    [Fact]
    public void Output_Contains_Using_Static_Spec()
    {
        var (source, _) = RunEngine();
        Assert.Contains("using static CpuEmulator.Core.Specification.Spec;", source);
    }

    [Fact]
    public void Output_Contains_Using_CpuSpecification()
    {
        var (source, _) = RunEngine();
        Assert.Contains("using CpuEmulator.Core.Specification;", source);
    }

    [Fact]
    public void Output_Contains_Spec_Class_Name()
    {
        var (source, _) = RunEngine();
        Assert.Contains("Mos6502Spec", source);
    }

    [Fact]
    public void Output_Contains_Namespace()
    {
        var (source, _) = RunEngine();
        Assert.Contains("CpuEmulator.Cpus.Mos6502", source);
    }

    // ─── M3.3 Task 5: Z80 prefixed-Insn + DecodeStructure emission ───────────

    private static string Z80DatasetPath   => DataPath.Get("z80-opcodes.json");
    private static string Z80SemanticsPath => DataPath.Get("z80-semantics.json");

    // The Z80's EMITTABLE modes = the AddrMode-backed subset (Implied/Immediate shared + the M3.2
    // IoPort* members). The Z80 register-shape modes are NOT AddrMode members (the enumerated M3.4
    // finding) so their rows are TODO(mode). This mirrors the emitter's SupportedModes.
    private static readonly HashSet<string> Z80EmittableModes =
    [
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative",
        "IoPortImmediate", "IoPortIndirect",
        // M3.4a: the Z80 register-shape modes (the base plane is now live).
        "Register", "RegisterIndirect", "ImmediateExtended", "ExtendedAddress", "RelativeJump",
        "Bit",   // M3.4b: the CB plane is now emittable.
        "Indexed",   // M3.4e-1a: the (IX+d)/(IY+d) mode is now a SupportedModes member (no DD/FD row live yet).
    ];

    private static bool IsSingleBytePrefix(string? prefix) =>
        prefix is null ||
        (prefix.Length == 4 && prefix.StartsWith("0x", StringComparison.OrdinalIgnoreCase));

    private (string source, ImportReport report) RunZ80Engine()
    {
        var dataset = OpcodeDataset.Load(Z80DatasetPath);
        var map     = SemanticsMap.Load(Z80SemanticsPath);
        return SpecImportEngine.Run(dataset, map, "z80-opcodes.json", "z80-semantics.json");
    }

    [Fact]
    public void Base_row_emits_single_byte_Insn()
    {
        // A covered base-plane row (NOP, 0x00, Implied) emits the EXISTING single-byte Insn form —
        // byte-identical to the 6502 emission shape (the 6502 guard).
        var (source, _) = RunZ80Engine();
        var norm = NormalizeWhitespace(source);
        Assert.Contains("Insn(0x00, \"NOP\", AddrMode.Implied, []),", norm);
    }

    [Fact]
    public void Prefixed_row_emits_prefixed_Insn()
    {
        // A covered ED-plane row (NEG, ED 0x44, Implied) emits the M3.1b prefixed Insn(prefix,opcode,…).
        // M3.4c: ED-core is now routed through Z80EdSemantics, so NEG carries the [EdNeg()] op (was []).
        var (source, _) = RunZ80Engine();
        var norm = NormalizeWhitespace(source);
        Assert.Contains("Insn(0xED, 0x44, \"NEG\", AddrMode.Implied, [EdNeg()]),", norm);
    }

    [Fact]
    public void Live_rows_carry_their_derived_ops()
    {
        // M3.4a: the base-plane OR r is LIVE; M3.4e-2: the DD core rows are LIVE; M3.4e-3: the DDCB
        // compound rows now emit as the Insn(0xDD, 0xCB, finalOp, …) overload via Z80DdCbSemantics.
        var (source, _) = RunZ80Engine();
        Assert.Contains("Insn(0xB0, \"OR\", AddrMode.Register, [Or8()]),", source);
        Assert.Contains("Insn(0xDD, 0x09, \"ADD\", AddrMode.Register, [Add16(\"IX\",\"BC\")]),", source);
        Assert.Contains("Insn(0xDD, 0x86, \"ADD\", AddrMode.Indexed, [DdFdAluIndexed(\"ADD\")]),", source);
        // M3.4d: ED B0 LDIR carries the EdBlock op.
        Assert.Contains("Insn(0xED, 0xB0, \"LDIR\", AddrMode.Implied, [EdBlock(\"LDIR\")]),", source);
        // M3.4e-3: the DDCB compound row (RLC (IX+d), the 0xDDCB:0x06 finalOp) now emits, no longer TODO.
        Assert.Contains("Insn(0xDD, 0xCB, 0x06, \"RLC\", AddrMode.Indexed, [DdCb(\"RLC\",0,\"-\")]),", source);
    }

    [Fact]
    public void DecodeStructure_is_emitted_with_backing_prefix()
    {
        // The skeleton declares a DecodeStructure with the prefix bytes that back EMITTED prefixed
        // rows. ED is backed (NEG) as a plain PrefixByte; M3.4e-3: the DD/FD bytes back BOTH plain core
        // rows AND compound DDCB/FDCB rows, so they emit the compound PrefixByte (CompoundWith + the
        // displacement-before-opcode flag) — the metadata that turns the compound decode routing ON.
        var (source, _) = RunZ80Engine();
        Assert.Contains("DecodeStructure Decode = new(", source);
        Assert.Contains("new PrefixByte(0xED)", source);
        Assert.Contains("new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)", source);
        Assert.Contains("new PrefixByte(0xFD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)", source);
        Assert.Contains("ModRmOpcodes: []", source);
        Assert.Contains("SubFieldOpcodes: []", source);
    }

    [Fact]
    public void DDCB_compound_rows_emit_as_the_compound_Insn_overload()
    {
        // M3.4e-3: a DDCB/FDCB compound row emits the Insn(p1, p2, finalOp, …) overload (the two prefix
        // bytes split apart — 0xDD, 0xCB — NOT a single 0xDDCB literal, which has no Insn overload). The
        // ops are derived by Z80DdCbSemantics. M3.4e-3 added the undocumented store-copy forms, so the
        // documented z=6 forms AND the undoc store-copy forms (z != 6) are all present.
        var (source, _) = RunZ80Engine();
        Assert.Contains("Insn(0xDD, 0xCB, 0x06, \"RLC\", AddrMode.Indexed, [DdCb(\"RLC\",0,\"-\")]),", source);
        Assert.Contains("Insn(0xDD, 0xCB, 0x00, \"RLC\", AddrMode.Indexed, [DdCb(\"RLC\",0,\"B\")]),", source);   // undoc store-copy B
        Assert.Contains("Insn(0xDD, 0xCB, 0x04, \"RLC\", AddrMode.Indexed, [DdCb(\"RLC\",0,\"H\")]),", source);   // undoc store-copy plain H
        Assert.Contains("Insn(0xDD, 0xCB, 0x46, \"BIT\", AddrMode.Indexed, [DdCb(\"BIT\",0,\"-\")]),", source);
        Assert.Contains("Insn(0xDD, 0xCB, 0x86, \"RES\", AddrMode.Indexed, [DdCb(\"RES\",0,\"-\")]),", source);
        Assert.Contains("Insn(0xDD, 0xCB, 0xFF, \"SET\", AddrMode.Indexed, [DdCb(\"SET\",7,\"A\")]),", source);   // y=7,z=7 -> copy A
        Assert.Contains("Insn(0xFD, 0xCB, 0x06, \"RLC\", AddrMode.Indexed, [DdCb(\"RLC\",0,\"-\")]),", source);
        // There is no single-literal 0xDDCB Insn overload — the prefix is always split.
        Assert.DoesNotContain("Insn(0xDDCB", source);
    }

    [Fact]
    public void Z80_covered_vs_TODO_counts()
    {
        // Pin the honest covered/TODO split by deriving the expectation IN THE TEST (the 3a
        // derivation-pin pattern) and asserting it equals the engine's report.
        var dataset = OpcodeDataset.Load(Z80DatasetPath);
        var map     = SemanticsMap.Load(Z80SemanticsPath);

        // M3.4a/b/c: a row HAS semantics iff the base-plane algorithmic decoder owns it (Z80BaseSemantics),
        // OR the CB-plane decoder owns it (Z80CbSemantics, M3.4b), OR the ED-core decoder owns it
        // (Z80EdSemantics, M3.4c — null for the block ops 0xA0–0xBB, which fall back to the map), OR the
        // per-mnemonic map covers it. It EMITS iff it has semantics, its mode is emittable, and its prefix
        // is a single byte (or null).
        string? Ops(OpcodeEntry e) => e.Prefix is null
            ? Z80BaseSemantics.OpsFor(System.Convert.ToInt32(e.Opcode, 16), e.Mnemonic, e.Mode)
              ?? (map.Mnemonics.ContainsKey(e.Mnemonic) ? map.Mnemonics[e.Mnemonic] : null)
            : e.Prefix == "0xCB"
            ? Z80CbSemantics.OpsFor(System.Convert.ToInt32(e.Opcode, 16))
            : e.Prefix == "0xED"
            ? Z80EdSemantics.OpsFor(System.Convert.ToInt32(e.Opcode, 16))
              ?? (map.Mnemonics.ContainsKey(e.Mnemonic) ? map.Mnemonics[e.Mnemonic] : null)
            : e.Prefix == "0xDD"
            ? Z80DdFdSemantics.OpsFor(System.Convert.ToInt32(e.Opcode, 16), e.Mnemonic, e.Mode, isIy: false)
            : e.Prefix == "0xFD"
            ? Z80DdFdSemantics.OpsFor(System.Convert.ToInt32(e.Opcode, 16), e.Mnemonic, e.Mode, isIy: true)
            : e.Prefix is "0xDDCB" or "0xFDCB"
            ? Z80DdCbSemantics.OpsFor(System.Convert.ToInt32(e.Opcode, 16))   // M3.4e-3: the compound plane
            : (map.Mnemonics.ContainsKey(e.Mnemonic) ? map.Mnemonics[e.Mnemonic] : null);

        // M3.4e-3: the compound DDCB/FDCB prefix is NOT a single byte, but it IS emittable (the compound
        // Insn overload). The emittable predicate now accepts a compound prefix too.
        bool IsEmittablePrefix(string? prefix) =>
            IsSingleBytePrefix(prefix) ||
            (prefix is { Length: 6 } && prefix.StartsWith("0x", StringComparison.OrdinalIgnoreCase));

        int derivedEmitted = dataset.Count(e =>
            Ops(e) is not null
            && Z80EmittableModes.Contains(e.Mode)
            && IsEmittablePrefix(e.Prefix));
        int derivedTodoMode = dataset.Count(e =>
            Ops(e) is not null
            && !(Z80EmittableModes.Contains(e.Mode) && IsEmittablePrefix(e.Prefix)));
        int derivedTodoSemantics = dataset.Count(e => Ops(e) is null);

        var (_, report) = SpecImportEngine.Run(dataset, map, "z80-opcodes.json", "z80-semantics.json");

        Assert.Equal(1604, report.Total);   // M3.4e-3: 1154 + the 225 DDCB + 225 FDCB derived rows
        Assert.Equal(derivedEmitted, report.Emitted);
        Assert.Equal(derivedTodoMode, report.TodoMode);
        Assert.Equal(derivedTodoSemantics, report.TodoSemantics);
        Assert.Equal(report.Total, report.Emitted + report.TodoMode + report.TodoSemantics);
        // M3.4a-d: base + CB + ED-core + ED-block LIVE. M3.4e-2: + 252 DD-core + 252 FD-core → 1092.
        // M3.4e-3: + the 256 DDCB + 256 FDCB compound rows via Z80DdCbSemantics → 1604, ZERO TODO. With
        // this the ENTIRE documented + undocumented Z80 instruction set emits.
        Assert.Equal(1604, report.Emitted);
        Assert.Equal(0, report.TodoSemantics);
    }

    [Fact]
    public void Existing_6502_emission_unchanged()
    {
        // The 6502 emission is byte-identical: no DecodeStructure (6502 emits no prefixed rows), and
        // the emitted count is still 151 (adding IoPort* to SupportedModes adds nothing — the 6502
        // dataset has no IoPort* rows).
        var (source, report) = RunEngine();
        Assert.DoesNotContain("DecodeStructure", source);
        Assert.Equal(151, report.Emitted);
        Assert.Equal(0, report.TodoSemantics);
        Assert.Equal(0, report.TodoMode);
    }

    // ─── helper ──────────────────────────────────────────────────────────────

    /// <summary>Collapses all whitespace runs (spaces, tabs) to single spaces on each line.</summary>
    private static string NormalizeWhitespace(string source)
    {
        var lines = source.Split('\n');
        return string.Join('\n', lines.Select(line =>
            System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"\s+", " ")));
    }
}
