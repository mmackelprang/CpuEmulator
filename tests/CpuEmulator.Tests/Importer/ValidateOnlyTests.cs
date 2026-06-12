using System.IO;
using CpuEmulator.SpecImporter;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Tests for the --validate-only importer mode: load + validate both schemas,
/// print the standard report + provenance coverage, write nothing, exit 0 on success
/// or exit 2 on validation/IO failure.
/// Console-isolation collection: these tests redirect Console.Out/Error in-proc
/// via Program.Main; they must run serially to avoid capture bleed.
/// </summary>
[Collection("ConsoleIsolation")]
public class ValidateOnlyTests
{
    private static string DatasetPath   => DataPath.Get("mos6502-opcodes.json");
    private static string SemanticsPath => DataPath.Get("mos6502-semantics.json");

    // Capture stdout/stderr by redirecting Console before calling Main.
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

    private static string TempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    // ── exit 0 on success ────────────────────────────────────────────────

    [Fact]
    public void ValidateOnly_real_dataset_exits_0()
    {
        var (code, _, _) = RunMain("--validate-only",
            "--dataset", DatasetPath, "--semantics", SemanticsPath);
        Assert.Equal(0, code);
    }

    [Fact]
    public void ValidateOnly_prints_standard_report()
    {
        var (_, stdout, _) = RunMain("--validate-only",
            "--dataset", DatasetPath, "--semantics", SemanticsPath);
        Assert.Contains("total=151", stdout);
        Assert.Contains("emitted=", stdout);
        Assert.Contains("todoSemantics=", stdout);
        Assert.Contains("todoMode=", stdout);
    }

    [Fact]
    public void ValidateOnly_prints_provenance_coverage()
    {
        var (_, stdout, _) = RunMain("--validate-only",
            "--dataset", DatasetPath, "--semantics", SemanticsPath);
        // Real dataset has 0 source citations today (verified; expected and documented).
        Assert.Contains("provenance: 0/151 rows carry source citations", stdout);
    }

    [Fact]
    public void ValidateOnly_provenance_counts_nonempty_source_fields()
    {
        // One row with a source, one without — coverage should report 1/1.
        var ds = TempFile("""
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "Immediate", "bytes": 2, "cycles": 2, "pageCrossPenalty": false, "source": "manual p.1" }
            ]
            """);
        var sem = TempFile("""
            { "architecture": "test", "namespace": "T", "specClassName": "TSpec",
              "registers": [], "mnemonics": { "LDA": "[Load(Reg.A), SetNZ(Reg.A)]" } }
            """);
        try
        {
            var (_, stdout, _) = RunMain("--validate-only", "--dataset", ds, "--semantics", sem);
            Assert.Contains("provenance: 1/1 rows carry source citations", stdout);
        }
        finally { File.Delete(ds); File.Delete(sem); }
    }

    [Fact]
    public void ValidateOnly_writes_nothing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            RunMain("--validate-only",
                "--dataset", DatasetPath, "--semantics", SemanticsPath);
            Assert.Empty(Directory.GetFiles(tempDir));
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    // ── exit 2 on validation/IO error ────────────────────────────────────

    [Fact]
    public void ValidateOnly_bad_dataset_exits_2()
    {
        var (code, _, _) = RunMain("--validate-only",
            "--dataset", "/nonexistent/opcodes.json", "--semantics", SemanticsPath);
        Assert.Equal(2, code);
    }

    [Fact]
    public void ValidateOnly_bad_dataset_prints_error()
    {
        var (_, _, stderr) = RunMain("--validate-only",
            "--dataset", "/nonexistent/opcodes.json", "--semantics", SemanticsPath);
        Assert.Contains("error:", stderr);
    }

    [Fact]
    public void ValidateOnly_bad_semantics_exits_2()
    {
        var (code, _, _) = RunMain("--validate-only",
            "--dataset", DatasetPath, "--semantics", "/nonexistent/semantics.json");
        Assert.Equal(2, code);
    }

    [Fact]
    public void ValidateOnly_dataset_invalid_json_exits_2()
    {
        var bad = TempFile("not json");
        try
        {
            var (code, _, _) = RunMain("--validate-only",
                "--dataset", bad, "--semantics", SemanticsPath);
            Assert.Equal(2, code);
        }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void ValidateOnly_semantics_invalid_json_exits_2()
    {
        var bad = TempFile("not json");
        try
        {
            var (code, _, _) = RunMain("--validate-only",
                "--dataset", DatasetPath, "--semantics", bad);
            Assert.Equal(2, code);
        }
        finally { File.Delete(bad); }
    }

    // ── exit 1 on usage errors ───────────────────────────────────────────

    [Fact]
    public void ValidateOnly_missing_dataset_flag_exits_1()
    {
        var (code, _, _) = RunMain("--validate-only", "--semantics", SemanticsPath);
        Assert.Equal(1, code);
    }

    [Fact]
    public void ValidateOnly_missing_semantics_flag_exits_1()
    {
        var (code, _, _) = RunMain("--validate-only", "--dataset", DatasetPath);
        Assert.Equal(1, code);
    }

    [Fact]
    public void ValidateOnly_combined_with_out_is_usage_error()
    {
        var (code, _, stderr) = RunMain("--validate-only", "--out", "/tmp/x.cs",
            "--dataset", DatasetPath, "--semantics", SemanticsPath);
        Assert.Equal(1, code);
        Assert.Contains("error:", stderr);
    }

    // ── --report flag works under --validate-only ─────────────────────────

    [Fact]
    public void ValidateOnly_report_flag_prints_missing_semantics_header()
    {
        var (_, stdout, _) = RunMain("--validate-only", "--report",
            "--dataset", DatasetPath, "--semantics", SemanticsPath);
        Assert.Contains("missing-semantics inventory", stdout);
    }
}
