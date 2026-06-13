using System.IO;
using CpuEmulator.SpecImporter;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Tests for the --review-report generator: markdown structure and content assertions.
/// Also tests the CLI wiring (--review-report path).
/// Console-isolation collection: CLI tests redirect Console in-proc.
/// </summary>
[Collection("ConsoleIsolation")]
public class ReviewReportTests
{
    private static string DatasetPath   => DataPath.Get("mos6502-opcodes.json");
    private static string SemanticsPath => DataPath.Get("mos6502-semantics.json");

    private static string SeededPath =>
        Path.Combine(AppContext.BaseDirectory, "Importer", "data", "mos6502-opcodes-seeded.json");

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

    // Build a minimal report using the real dataset (0/151 provenance, 0 missing semantics).
    private static string BuildRealReport(DiffResult? diff = null)
    {
        var dataset = OpcodeDataset.Load(DatasetPath);
        var map     = SemanticsMap.Load(SemanticsPath);
        var (_, rpt) = SpecImportEngine.Run(dataset, map);
        return ReviewReportGenerator.Generate(map.Architecture, dataset, rpt, diff);
    }

    // ── structure ────────────────────────────────────────────────────────

    [Fact]
    public void Report_has_provenance_table_heading()
    {
        var content = BuildRealReport();
        Assert.Contains("## Provenance Coverage", content);
    }

    [Fact]
    public void Report_provenance_shows_zero_of_151()
    {
        var content = BuildRealReport();
        // Real dataset has 0 source citations; report must reflect that.
        Assert.Contains("0/151", content);
    }

    [Fact]
    public void Report_lists_rows_lacking_source_heading()
    {
        var content = BuildRealReport();
        Assert.Contains("## Rows Lacking Source", content);
    }

    [Fact]
    public void Report_all_151_listed_when_no_source()
    {
        var content = BuildRealReport();
        // Count markdown table rows: each opcode row starts with "| 0x"
        var rowCount = content.Split('\n')
            .Count(line => line.TrimStart().StartsWith("| 0x"));
        Assert.Equal(151, rowCount);
    }

    [Fact]
    public void Report_no_disagreement_section_without_diff()
    {
        var content = BuildRealReport(diff: null);
        Assert.DoesNotContain("## Disagreements", content);
    }

    [Fact]
    public void Report_disagreement_section_present_when_diff_has_disagreements()
    {
        var real   = OpcodeDataset.Load(DatasetPath);
        var seeded = OpcodeDataset.Load(SeededPath);
        var diff   = DatasetDiff.Compare(real, seeded);
        var content = BuildRealReport(diff);
        Assert.Contains("## Disagreements", content);
    }

    [Fact]
    public void Report_disagreement_table_has_five_rows()
    {
        var real   = OpcodeDataset.Load(DatasetPath);
        var seeded = OpcodeDataset.Load(SeededPath);
        var diff   = DatasetDiff.Compare(real, seeded);
        var content = BuildRealReport(diff);

        // Disagreement rows are table rows that start with "| 0x" in the Disagreements section.
        // Count them: there should be exactly 5 (the five seeded cells).
        var lines   = content.Split('\n');
        var inDisagreements = false;
        var rowCount = 0;
        foreach (var line in lines)
        {
            if (line.Contains("## Disagreements")) { inDisagreements = true; continue; }
            if (inDisagreements && line.StartsWith("## "))  break; // next section
            if (inDisagreements && line.TrimStart().StartsWith("| 0x")) rowCount++;
        }
        Assert.Equal(5, rowCount);
    }

    [Fact]
    public void Report_has_missing_semantics_section()
    {
        var content = BuildRealReport();
        Assert.Contains("## Missing Semantics", content);
    }

    [Fact]
    public void Report_missing_semantics_says_all_defined_when_none_missing()
    {
        // Real dataset has all 151 mnemonics mapped; the Missing Semantics section
        // should say "All mnemonics have semantics defined."
        var content = BuildRealReport();
        Assert.Contains("All mnemonics have semantics defined", content);
    }

    [Fact]
    public void Report_missing_semantics_lists_items_when_present()
    {
        // Build a minimal dataset with a mnemonic not in the semantics map.
        var dataset = OpcodeDataset.Parse("""
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "Immediate",
                "bytes": 2, "cycles": 2, "pageCrossPenalty": false },
              { "opcode": "0xEA", "mnemonic": "NOP", "mode": "Implied",
                "bytes": 1, "cycles": 2, "pageCrossPenalty": false }
            ]
            """);
        // Only LDA is in the semantics map (minimal map).
        var map = SemanticsMap.Parse("""
            { "architecture": "test", "namespace": "T", "specClassName": "TSpec",
              "registers": [],
              "mnemonics": { "LDA": "[Load(\"A\"), SetNZ(\"A\")]" } }
            """);
        var (_, rpt) = SpecImportEngine.Run(dataset, map);
        var content = ReviewReportGenerator.Generate("test", dataset, rpt);

        // NOP is missing semantics.
        Assert.Contains("NOP", content);
        Assert.DoesNotContain("All mnemonics have semantics defined", content);
    }

    // ── timestamp format ─────────────────────────────────────────────────

    [Fact]
    public void Report_timestamp_is_date_only()
    {
        var content = BuildRealReport();
        // Format: "Generated: yyyy-MM-dd" — no time component (reproducible in tests).
        var generated = content.Split('\n')
            .FirstOrDefault(l => l.StartsWith("Generated:"));
        Assert.NotNull(generated);
        // Should be exactly "Generated: YYYY-MM-DD" (10-digit date).
        var datePart = generated!.Replace("Generated:", "").Trim();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", datePart);
    }

    // ── CLI wiring ───────────────────────────────────────────────────────

    [Fact]
    public void Report_written_to_file()
    {
        var outFile = Path.GetTempFileName();
        try
        {
            RunMain("--validate-only",
                "--dataset", DatasetPath, "--semantics", SemanticsPath,
                "--review-report", outFile);
            Assert.True(File.Exists(outFile));
            Assert.True(new FileInfo(outFile).Length > 0);
        }
        finally { if (File.Exists(outFile)) File.Delete(outFile); }
    }

    [Fact]
    public void Report_file_contains_heading()
    {
        var outFile = Path.GetTempFileName();
        try
        {
            RunMain("--validate-only",
                "--dataset", DatasetPath, "--semantics", SemanticsPath,
                "--review-report", outFile);
            var content = File.ReadAllText(outFile);
            Assert.Contains("# Extraction Review", content);
        }
        finally { if (File.Exists(outFile)) File.Delete(outFile); }
    }

    [Fact]
    public void Report_with_seeded_diff_contains_disagreements_section()
    {
        var outFile = Path.GetTempFileName();
        try
        {
            RunMain("--validate-only",
                "--dataset", DatasetPath, "--semantics", SemanticsPath,
                "--diff", SeededPath,
                "--review-report", outFile);
            var content = File.ReadAllText(outFile);
            Assert.Contains("## Disagreements", content);
        }
        finally { if (File.Exists(outFile)) File.Delete(outFile); }
    }
}
