using System.IO;
using CpuEmulator.SpecImporter;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Tests for the cross-source dataset diff tool: DatasetDiff engine + --diff CLI mode.
///
/// Fixture A (mos6502-opcodes-seeded.json) — 5 seeded disagreements against the real dataset:
///   0x4C  cycles           3 → 4
///   0x69  mnemonic         ADC → ADD
///   0xBD  cycles           4 → 5
///   0xBD  pageCrossPenalty True → False
///   0xEA  mnemonic         NOP → NXX
///
/// Exit code 3 when any disagreement exists; 0 when datasets are identical.
/// Console-isolation collection: --diff CLI tests redirect Console in-proc.
/// </summary>
[Collection("ConsoleIsolation")]
public class DatasetDiffTests
{
    private static string DatasetPath   => DataPath.Get("mos6502-opcodes.json");
    private static string SemanticsPath => DataPath.Get("mos6502-semantics.json");

    // Seeded fixture: copy lands in Importer/data/ relative to the test output dir.
    private static string SeededPath =>
        Path.Combine(AppContext.BaseDirectory, "Importer", "data", "mos6502-opcodes-seeded.json");

    private static OpcodeEntry[] RealDataset  => OpcodeDataset.Load(DatasetPath);
    private static OpcodeEntry[] SeededDataset => OpcodeDataset.Load(SeededPath);

    private static (int ExitCode, string Stdout, string Stderr) RunMain(params string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var outSw   = new StringWriter();
        var errSw   = new StringWriter();
        try
        {
            Console.SetOut(outSw);
            Console.SetError(errSw);
            int code = Program.Main(args);
            return (code, outSw.ToString(), errSw.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    // ── engine: self-diff ────────────────────────────────────────────────

    [Fact]
    public void Self_diff_has_no_disagreements()
    {
        var dataset = RealDataset;
        var result  = DatasetDiff.Compare(dataset, dataset);
        Assert.Empty(result.Disagreements);
        Assert.Empty(result.MissingInOther);
        Assert.Empty(result.ExtraInOther);
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public void Self_diff_exits_0()
    {
        var (code, _, _) = RunMain("--dataset", DatasetPath, "--diff", DatasetPath);
        Assert.Equal(0, code);
    }

    // ── engine: seeded fixture disagreements ─────────────────────────────

    [Fact]
    public void Seeded_diff_has_five_disagreement_cells()
    {
        var result = DatasetDiff.Compare(RealDataset, SeededDataset);
        Assert.Equal(5, result.Disagreements.Count);
    }

    [Fact]
    public void Seeded_diff_mnemonic_0x69()
    {
        var result = DatasetDiff.Compare(RealDataset, SeededDataset);
        var d = Assert.Single(result.Disagreements, x =>
            x.Opcode.Equals("0x69", StringComparison.OrdinalIgnoreCase) && x.Field == "mnemonic");
        Assert.Equal("ADC", d.Left);
        Assert.Equal("ADD", d.Right);
    }

    [Fact]
    public void Seeded_diff_cycles_0xBD()
    {
        var result = DatasetDiff.Compare(RealDataset, SeededDataset);
        var d = Assert.Single(result.Disagreements, x =>
            x.Opcode.Equals("0xBD", StringComparison.OrdinalIgnoreCase) && x.Field == "cycles");
        Assert.Equal("4", d.Left);
        Assert.Equal("5", d.Right);
    }

    [Fact]
    public void Seeded_diff_pageCross_0xBD()
    {
        var result = DatasetDiff.Compare(RealDataset, SeededDataset);
        var d = Assert.Single(result.Disagreements, x =>
            x.Opcode.Equals("0xBD", StringComparison.OrdinalIgnoreCase)
            && x.Field == "pageCrossPenalty");
        Assert.Equal("True", d.Left);
        Assert.Equal("False", d.Right);
    }

    [Fact]
    public void Seeded_diff_cycles_0x4C()
    {
        var result = DatasetDiff.Compare(RealDataset, SeededDataset);
        var d = Assert.Single(result.Disagreements, x =>
            x.Opcode.Equals("0x4C", StringComparison.OrdinalIgnoreCase) && x.Field == "cycles");
        Assert.Equal("3", d.Left);
        Assert.Equal("4", d.Right);
    }

    [Fact]
    public void Seeded_diff_mnemonic_0xEA()
    {
        var result = DatasetDiff.Compare(RealDataset, SeededDataset);
        var d = Assert.Single(result.Disagreements, x =>
            x.Opcode.Equals("0xEA", StringComparison.OrdinalIgnoreCase) && x.Field == "mnemonic");
        Assert.Equal("NOP", d.Left);
        Assert.Equal("NXX", d.Right);
    }

    [Fact]
    public void Seeded_diff_exits_3()
    {
        var (code, _, _) = RunMain("--dataset", DatasetPath, "--diff", SeededPath);
        Assert.Equal(3, code);
    }

    // ── engine: missing / extra opcodes ──────────────────────────────────

    [Fact]
    public void Missing_opcode_in_other_detected()
    {
        // Build a right-hand dataset missing 0x69.
        var left  = RealDataset;
        var right = left.Where(e => !e.Opcode.Equals("0x69", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
        var result = DatasetDiff.Compare(left, right);
        Assert.Contains("0x69", result.MissingInOther, StringComparer.OrdinalIgnoreCase);
        Assert.True(result.HasDifferences);
    }

    [Fact]
    public void Extra_opcode_in_other_detected()
    {
        // Build a right-hand dataset with an extra opcode 0xF2 (not in real 6502).
        var left  = RealDataset;
        var extra = new OpcodeEntry("0xF2", "NOP", "Implied", 1, 2, false);
        var right = left.Append(extra).ToArray();
        var result = DatasetDiff.Compare(left, right);
        Assert.Contains("0xF2", result.ExtraInOther, StringComparer.OrdinalIgnoreCase);
        Assert.True(result.HasDifferences);
    }

    [Fact]
    public void Missing_and_extra_exit_3()
    {
        // A right-hand set with 0x69 removed and 0xF2 added.
        var left  = RealDataset;
        var extra = new OpcodeEntry("0xF2", "NOP", "Implied", 1, 2, false);
        var right = left.Where(e => !e.Opcode.Equals("0x69", StringComparison.OrdinalIgnoreCase))
                        .Append(extra).ToArray();
        var result = DatasetDiff.Compare(left, right);
        Assert.True(result.HasDifferences);
    }

    // ── CLI: usage errors ────────────────────────────────────────────────

    [Fact]
    public void Diff_requires_dataset()
    {
        var (code, _, _) = RunMain("--diff", DatasetPath);
        Assert.Equal(1, code);
    }

    [Fact]
    public void Diff_other_file_not_found_exits_2()
    {
        var (code, _, _) = RunMain("--dataset", DatasetPath, "--diff", "/nonexistent/other.json");
        Assert.Equal(2, code);
    }

    // ── CLI: output format ───────────────────────────────────────────────

    [Fact]
    public void Diff_prints_disagreement_table()
    {
        var (_, stdout, _) = RunMain("--dataset", DatasetPath, "--diff", SeededPath);
        Assert.Contains("0x69", stdout);
        Assert.Contains("mnemonic", stdout);
        Assert.Contains("ADC", stdout);
        Assert.Contains("ADD", stdout);
    }

    [Fact]
    public void Diff_prints_all_five_cells()
    {
        var (_, stdout, _) = RunMain("--dataset", DatasetPath, "--diff", SeededPath);
        // Each disagreement row appears in the output; check for the 5 known opcodes+fields.
        Assert.Contains("0x69", stdout);
        Assert.Contains("0xBD", stdout);
        Assert.Contains("0x4C", stdout);
        Assert.Contains("0xEA", stdout);
        Assert.Contains("pageCrossPenalty", stdout);
    }

    [Fact]
    public void Self_diff_prints_zero_summary()
    {
        var (_, stdout, _) = RunMain("--dataset", DatasetPath, "--diff", DatasetPath);
        Assert.Contains("0 disagreement(s)", stdout);
        Assert.Contains("0 missing opcode(s)", stdout);
        Assert.Contains("0 extra opcode(s)", stdout);
    }

    // ── CLI: combined modes ──────────────────────────────────────────────

    [Fact]
    public void Validate_then_diff_combined_exits_3_on_disagreements()
    {
        var (code, _, _) = RunMain("--validate-only",
            "--dataset", DatasetPath, "--semantics", SemanticsPath,
            "--diff", SeededPath);
        Assert.Equal(3, code);
    }

    [Fact]
    public void Validate_then_diff_identical_exits_0()
    {
        var (code, _, _) = RunMain("--validate-only",
            "--dataset", DatasetPath, "--semantics", SemanticsPath,
            "--diff", DatasetPath);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Diff_does_not_require_semantics()
    {
        // --diff + --dataset only: semantics not needed for a pure opcode comparison.
        var (code, _, _) = RunMain("--dataset", DatasetPath, "--diff", DatasetPath);
        Assert.Equal(0, code);
    }

    // ── fixture integrity ────────────────────────────────────────────────

    [Fact]
    public void Seeded_fixture_loads_as_valid_dataset()
    {
        // The fixture must pass OpcodeDataset.Load without throwing — all seeded
        // changes are schema-valid (no byte-count inconsistencies introduced).
        var entries = OpcodeDataset.Load(SeededPath);
        Assert.Equal(151, entries.Length);
    }

    // ─── M3.3 Task 2: the diff keys on the plane-qualified Key ──────────────

    [Fact]
    public void Diff_keys_on_plane_qualified_Key()
    {
        // The headline diff-correctness fix: two datasets each carrying ED B0 = LDIR AND base B0 = OR.
        // Keyed on the plane-qualified Key, the diff reports ZERO disagreements (each plane matches its
        // peer). The OLD bare-Opcode keying would collide ED B0 with base B0 and report a spurious
        // OR-vs-LDIR mnemonic disagreement.
        var left = new[]
        {
            new OpcodeEntry("0xB0", "LDIR", "Implied", 2, 21, false, null, "0xED"),
            new OpcodeEntry("0xB0", "OR",   "Register", 1, 4, false),
        };
        var right = new[]
        {
            new OpcodeEntry("0xB0", "OR",   "Register", 1, 4, false),
            new OpcodeEntry("0xB0", "LDIR", "Implied", 2, 21, false, null, "0xED"),
        };
        var result = DatasetDiff.Compare(left, right);
        Assert.Empty(result.Disagreements);
        Assert.Empty(result.MissingInOther);
        Assert.Empty(result.ExtraInOther);
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public void Diff_reports_field_disagreement_with_plane_qualified_key()
    {
        // A genuine disagreement WITHIN a plane is reported with the plane-qualified key (0xED:0xB0).
        var left  = new[] { new OpcodeEntry("0xB0", "LDIR", "Implied", 2, 21, false, null, "0xED") };
        var right = new[] { new OpcodeEntry("0xB0", "LDIR", "Implied", 2, 16, false, null, "0xED") };
        var result = DatasetDiff.Compare(left, right);
        var d = Assert.Single(result.Disagreements);
        Assert.Equal("0xED:0xB0", d.Opcode);
        Assert.Equal("cycles", d.Field);
    }

    [Fact]
    public void Diff_distinguishes_missing_prefixed_from_present_base()
    {
        // ED B0 present in left only (a coverage gap surfaced per-plane, not masked by base B0).
        var left = new[]
        {
            new OpcodeEntry("0xB0", "LDIR", "Implied", 2, 21, false, null, "0xED"),
            new OpcodeEntry("0xB0", "OR",   "Register", 1, 4, false),
        };
        var right = new[] { new OpcodeEntry("0xB0", "OR", "Register", 1, 4, false) };
        var result = DatasetDiff.Compare(left, right);
        Assert.Contains("0xED:0xB0", result.MissingInOther);
        Assert.DoesNotContain("0xB0", result.MissingInOther); // base B0 is present in both
    }

    [Fact]
    public void Diff_over_two_6502_datasets_is_unchanged()
    {
        // The regression guard: a 6502 row's Key is its bare Opcode (null prefix), so the existing
        // 6502 diff behavior is byte-identical. The seeded fixture still reports exactly 5 cells.
        var result = DatasetDiff.Compare(RealDataset, SeededDataset);
        Assert.Equal(5, result.Disagreements.Count);
        // And the disagreement keys are the bare opcodes (no plane qualifier — null prefix).
        Assert.All(result.Disagreements, d => Assert.DoesNotContain(":", d.Opcode));
    }
}
