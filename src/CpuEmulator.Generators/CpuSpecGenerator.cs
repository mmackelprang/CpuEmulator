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
            transform: static (ctx, _) => SpecParser.Parse(ctx))
            .WithTrackingName("Specs");

        var collected = specs.Collect().WithTrackingName("Collected");

        context.RegisterSourceOutput(collected, static (spc, parsedSpecs) =>
        {
            foreach (var parsed in parsedSpecs)
                foreach (var info in parsed.Diagnostics)
                    spc.ReportDiagnostic(info.ToDiagnostic());

            var models = parsedSpecs
                .Where(p => p.Model is not null)
                .Select(p => p.Model!)
                .ToList();

            // Collision keys are Ordinal (case-sensitive). Roslyn compares AddSource hint
            // names case-INsensitively, so two keys differing only by case could still
            // collide in AddSource — accepted as pathological (namespaces/class names
            // differing only by case); revisit with OrdinalIgnoreCase if it ever bites.
            var collided = models
                .GroupBy(m => $"{m.Namespace}.{m.CpuName}")
                .Where(g => g.Count() > 1)
                .ToList();
            foreach (var group in collided)
                foreach (var model in group)
                    spc.ReportDiagnostic(Diagnostic.Create(
                        SpecDiagnostics.InvalidSpecMetadata,
                        model.IdentifierLocation.ToLocation(),
                        $"multiple specs generate the same CPU class '{group.Key}'"));

            var collidedKeys = new HashSet<string>(collided.Select(g => g.Key));
            foreach (var model in models)
                if (!collidedKeys.Contains($"{model.Namespace}.{model.CpuName}"))
                    spc.AddSource($"{model.Namespace}.{model.CpuName}.g.cs", CpuEmitter.Emit(model));
        });
    }
}
