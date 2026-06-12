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
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, _) => SpecParser.Parse(ctx));

        context.RegisterSourceOutput(specs, static (spc, parsed) =>
        {
            foreach (var diagnostic in parsed.Diagnostics)
                spc.ReportDiagnostic(diagnostic);
            if (parsed.Model is { } model)
                spc.AddSource($"{model.Namespace}.{model.CpuName}.g.cs", CpuEmitter.Emit(model));
        });
    }
}
