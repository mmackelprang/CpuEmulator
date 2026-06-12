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
}
