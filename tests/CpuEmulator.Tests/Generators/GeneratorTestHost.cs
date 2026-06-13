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

    /// <summary>Run the generator over <paramref name="source"/>, compile the original source +
    /// the generated trees into an in-memory assembly, load it, and return the named type. Lets a
    /// test DRIVE the generated walk at runtime (invoke Decode/DescriptorFor via reflection) rather
    /// than only asserting generated text — the load-bearing proof for the synthetic decode CPU.</summary>
    public static Type CompileAndLoadType(string source, string fullTypeName)
    {
        var compilation = CSharpCompilation.Create(
            "SyntheticDecodeCpu_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CpuSpecGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var errors = updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "compilation errors: " + string.Join("\n", errors));

        using var ms = new MemoryStream();
        var emit = updated.Emit(ms);
        Assert.True(emit.Success, "emit failed: " + string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        var asm = System.Reflection.Assembly.Load(ms.ToArray());
        return asm.GetType(fullTypeName) ?? throw new InvalidOperationException($"type '{fullTypeName}' not found in generated assembly");
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
