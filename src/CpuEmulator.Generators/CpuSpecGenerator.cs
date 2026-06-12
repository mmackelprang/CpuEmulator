using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CpuEmulator.Generators;

[Generator]
public sealed class CpuSpecGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var specs = context.SyntaxProvider.ForAttributeWithMetadataName(
            "CpuEmulator.Core.Specification.CpuSpecificationAttribute",
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, _) => SpecParser.Parse(ctx));

        var collected = specs.Collect();

        context.RegisterSourceOutput(collected, static (spc, parsedSpecs) =>
        {
            foreach (var parsed in parsedSpecs)
                foreach (var info in parsed.Diagnostics)
                    spc.ReportDiagnostic(info.ToDiagnostic());

            var models = parsedSpecs
                .Where(p => p.Model is not null)
                .Select(p => p.Model!)
                .ToList();

            var collided = models
                .GroupBy(m => $"{m.Namespace}.{m.CpuName}")
                .Where(g => g.Count() > 1)
                .ToList();
            foreach (var group in collided)
                spc.ReportDiagnostic(Diagnostic.Create(
                    SpecDiagnostics.InvalidSpecMetadata, Location.None,
                    $"multiple specs generate the same CPU class '{group.Key}'"));

            var collidedKeys = new HashSet<string>(collided.Select(g => g.Key));
            foreach (var model in models)
                if (!collidedKeys.Contains($"{model.Namespace}.{model.CpuName}"))
                    spc.AddSource($"{model.Namespace}.{model.CpuName}.g.cs", CpuEmitter.Emit(model));
        });
    }
}
