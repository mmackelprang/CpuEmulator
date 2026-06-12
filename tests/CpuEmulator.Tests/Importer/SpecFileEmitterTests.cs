using System.IO;
using CpuEmulator.SpecImporter;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Tests for the SpecFileEmitter and SpecImportEngine (Task 4).
///
/// Expected emitted-row count derivation (pinned constant):
///   Filter the 151-row dataset to rows where:
///     (1) mnemonic ∈ the 24-entry semantics map, AND
///     (2) mode ∈ {Implied, Immediate, ZeroPage, Absolute, Relative} (DSL's 5 supported modes)
///   Running that filter against the real data files yields EXACTLY 33 rows.
///   The test below also derives this count independently at runtime and asserts it
///   matches both the filter result and the engine's reported emitted count.
/// </summary>
public class SpecFileEmitterTests
{
    private static string DatasetPath   => DataPath.Get("mos6502-opcodes.json");
    private static string SemanticsPath => DataPath.Get("mos6502-semantics.json");

    // The 5 addressing modes supported by the DSL today (matches AddrMode enum in CpuEmulator.Core).
    // SYNC HAZARD: if the DSL gains new modes this set must expand in concert with AddrMode.cs.
    private static readonly HashSet<string> SupportedModes =
        ["Implied", "Immediate", "ZeroPage", "Absolute", "Relative"];

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

        // Pinned constant: 33 rows (derived 2026-06-12 from the 24-mnemonic map ×
        // the 5 supported modes intersected with the 151-row dataset).
        const int ExpectedEmitted = 33;
        Assert.Equal(ExpectedEmitted, derivedCount);

        var (_, report) = SpecImportEngine.Run(dataset, map);
        Assert.Equal(ExpectedEmitted, report.Emitted);
    }

    // ─── per-mnemonic missing-semantics inventory (plan: report includes it) ──

    [Fact]
    public void Report_Inventory_Lists_Missing_Semantics_Per_Mnemonic()
    {
        var (_, report) = RunEngine();
        var inv = report.MissingSemanticsInventory;

        // 56 distinct mnemonics in the dataset − 24 in the semantics map = 32 missing.
        Assert.Equal(32, inv.Count);
        // Row counts must reconcile with the todoSemantics total (101).
        Assert.Equal(report.TodoSemantics, inv.Sum(x => x.Rows));
        // Spot entries: ADC has 8 dataset rows, BRK has 1.
        Assert.Contains(("ADC", 8), inv);
        Assert.Contains(("BRK", 1), inv);
        // Mapped mnemonics must NOT appear (LDA has semantics).
        Assert.DoesNotContain(inv, x => x.Mnemonic == "LDA");
        // Stable mnemonic ordering for reproducible report output.
        Assert.Equal(inv.OrderBy(x => x.Mnemonic, StringComparer.Ordinal), inv);
    }

    // ─── 11-row regression anchor ───────────────────────────────────────────
    // Each of the 11 live Mos6502Spec.cs rows must appear verbatim in the
    // importer output (whitespace-normalized to single-space for comparison
    // because the live spec uses alignment padding — the importer is opcode-ordered
    // and does not pad).

    [Theory]
    [InlineData("""Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),""")]
    [InlineData("""Insn(0xA5, "LDA", AddrMode.ZeroPage, [Load(Reg.A), SetNZ(Reg.A)]),""")]
    [InlineData("""Insn(0xAD, "LDA", AddrMode.Absolute, [Load(Reg.A), SetNZ(Reg.A)]),""")]
    [InlineData("""Insn(0x85, "STA", AddrMode.ZeroPage, [Store(Reg.A)]),""")]
    [InlineData("""Insn(0x8D, "STA", AddrMode.Absolute, [Store(Reg.A)]),""")]
    [InlineData("""Insn(0xA2, "LDX", AddrMode.Immediate, [Load(Reg.X), SetNZ(Reg.X)]),""")]
    [InlineData("""Insn(0xAA, "TAX", AddrMode.Implied, [Transfer(Reg.A, Reg.X), SetNZ(Reg.X)]),""")]
    [InlineData("""Insn(0xE8, "INX", AddrMode.Implied, [Increment(Reg.X), SetNZ(Reg.X)]),""")]
    [InlineData("""Insn(0x4C, "JMP", AddrMode.Absolute, [Jump()]),""")]
    [InlineData("""Insn(0xD0, "BNE", AddrMode.Relative, [BranchIf(Flag.Z, false)]),""")]
    [InlineData("""Insn(0xEA, "NOP", AddrMode.Implied, []),""")]
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
    public void Output_Contains_TODO_Semantics_For_ADC_0x69()
    {
        // 0x69 is ADC Immediate — ADC has no semantics in the map yet
        var (source, _) = RunEngine();
        Assert.Contains("TODO(semantics)", source);
        Assert.Contains("0x69", source);
        Assert.Contains("ADC", source);
    }

    [Fact]
    public void Output_Contains_TODO_Mode_For_LDA_AbsoluteX_0xBD()
    {
        // 0xBD is LDA AbsoluteX — LDA has semantics but AbsoluteX is not a supported DSL mode
        var (source, _) = RunEngine();
        Assert.Contains("TODO(mode)", source);
        Assert.Contains("0xBD", source);
        Assert.Contains("AbsoluteX", source);
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

    // ─── helper ──────────────────────────────────────────────────────────────

    /// <summary>Collapses all whitespace runs (spaces, tabs) to single spaces on each line.</summary>
    private static string NormalizeWhitespace(string source)
    {
        var lines = source.Split('\n');
        return string.Join('\n', lines.Select(line =>
            System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"\s+", " ")));
    }
}
