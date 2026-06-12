using System.Collections.Immutable;
using CpuEmulator.Core;
using CpuEmulator.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CpuEmulator.Tests.Generators;

internal sealed record GeneratorRunResult(
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<SyntaxTree> GeneratedTrees,
    ImmutableArray<Diagnostic> CompilationDiagnostics)
{
    public string GeneratedText => string.Concat(GeneratedTrees.Select(t => t.ToString()));

    public ImmutableArray<Diagnostic> AllErrors =>
        [.. GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
         .. CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
}

internal static class GeneratorTestHost
{
    /// <summary>
    /// Line-ending-agnostic section replace for spec-source mutation in tests.
    /// Raw string literals inherit their source file's line endings, so a CRLF test file
    /// produces needles that silently fail to match an LF ValidSpecSource (and vice versa),
    /// turning rejection tests vacuous. Normalizes both sides to LF and fails loudly if
    /// the needle did not match.
    /// </summary>
    public static string ReplaceSection(string source, string needle, string replacement)
    {
        string normalizedSource = source.Replace("\r\n", "\n");
        string result = normalizedSource.Replace(
            needle.Replace("\r\n", "\n"),
            replacement.Replace("\r\n", "\n"));
        Assert.NotEqual(normalizedSource, result); // guard: the Replace must fire
        return result;
    }

    private static readonly ImmutableArray<MetadataReference> s_references = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // Reference the full TPA closure of the running test process (net10 BCL facades)
        // plus CpuEmulator.Core, so spec sources compile exactly like a real consumer.
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
        return [.. tpa, MetadataReference.CreateFromFile(typeof(ICpuCore).Assembly.Location)];
    }

    public static GeneratorRunResult Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "SpecUnderTest",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CpuSpecGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        var runResult = driver.GetRunResult();

        return new GeneratorRunResult(
            runResult.Diagnostics,
            [.. runResult.GeneratedTrees],
            updated.GetDiagnostics());
    }

    /// <summary>
    /// Runs the generator with step tracking, then runs it AGAIN on the same compilation
    /// with the source replaced by a REPARSED (reference-distinct, textually identical)
    /// syntax tree, and returns the second run's tracked result. Pins value-equality of
    /// the pipeline state: if ParsedSpec compared by reference, every step would report
    /// Modified and incremental caching would be dead.
    /// </summary>
    public static GeneratorDriverRunResult RunTwiceWithReparse(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "SpecUnderTest",
            [tree],
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new CpuSpecGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);

        var reparsed = CSharpSyntaxTree.ParseText(source, parseOptions);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, reparsed));

        return driver.GetRunResult();
    }
}
