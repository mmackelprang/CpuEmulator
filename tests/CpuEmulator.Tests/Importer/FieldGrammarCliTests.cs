using System.IO;
using Xunit;

namespace CpuEmulator.Tests.Importer;

[Collection("ConsoleIsolation")]
public class FieldGrammarCliTests
{
    // Capture stdout/stderr by redirecting Console before calling Main, then restore the
    // originals in finally. Without this, the negative-arg cases below write Program.Fail's
    // "error: ..." to the test host's real stderr, which the parallel-collection runner reads
    // as a host crash during teardown. Mirrors ValidateOnlyTests.RunMain.
    private static int RunMain(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var outSw = new StringWriter();
        var errSw = new StringWriter();
        try { Console.SetOut(outSw); Console.SetError(errSw); return Program.Main(args); }
        finally { Console.SetOut(originalOut); Console.SetError(originalErr); }
    }

    [Fact]
    public void Field_grammar_mode_writes_a_spec_with_a_FieldGrammar()
    {
        string repoRoot = TestRepo.FindRepoRoot();
        string dataset = Path.Combine(repoRoot, "tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar.json");
        string config  = Path.Combine(repoRoot, "tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar-config.json");
        string outPath = Path.Combine(Path.GetTempPath(), $"m68k-spec-{System.Guid.NewGuid():N}.cs");
        try
        {
            int exit = RunMain("--field-grammar", dataset, "--config", config, "--out", outPath);
            Assert.Equal(0, exit);
            string written = File.ReadAllText(outPath);
            Assert.Contains("public static readonly FieldGrammar Decode68k = new(", written);
            Assert.Contains("FetchUnit.Word", written);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Fact]
    public void Field_grammar_requires_config()
    {
        int exit = RunMain("--field-grammar", "x.json", "--out", "y.cs");
        Assert.Equal(1, exit);   // usage error: --config required
    }

    [Fact]
    public void Config_without_field_grammar_is_a_usage_error()
    {
        // --config is only valid in the FieldGrammar arm; the opcode-row arm must reject it rather
        // than silently ignore it (the arg loop's "unknown combination fails loudly" contract).
        int exit = RunMain("--dataset", "a.json", "--semantics", "b.json", "--out", "c.cs",
                           "--config", "x.json");
        Assert.Equal(1, exit);
    }
}
