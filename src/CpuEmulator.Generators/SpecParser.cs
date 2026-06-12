using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CpuEmulator.Generators;

internal static class SpecParser
{
    public static ParsedSpec Parse(GeneratorAttributeSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.TargetNode;
        string ns = context.TargetSymbol.ContainingNamespace.ToDisplayString();
        string specName = classDecl.Identifier.Text;

        var attribute = context.Attributes[0];
        string architecture = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? "unknown"
            : "unknown";

        string cpuName = specName.EndsWith("Spec", System.StringComparison.Ordinal)
            ? specName.Substring(0, specName.Length - 4) + "Cpu"
            : specName + "Cpu";
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "CpuName" && named.Value.Value is string explicitName)
                cpuName = explicitName;
        }

        var model = new SpecModel(ns, cpuName, architecture,
            ImmutableArray<RegisterModel>.Empty, ImmutableArray<InstructionModel>.Empty);
        return new ParsedSpec(model, ImmutableArray<Diagnostic>.Empty);
    }

    private static FieldDeclarationSyntax? FindArrayField(ClassDeclarationSyntax classDecl, string name) =>
        classDecl.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Count == 1 &&
                                 f.Declaration.Variables[0].Identifier.Text == name);

    /// <summary>Arguments of new(...) / new T(...) / Factory(...); null when not a creation/invocation.</summary>
    private static IReadOnlyList<ExpressionSyntax>? GetCreationArguments(ExpressionSyntax expression) =>
        expression switch
        {
            ImplicitObjectCreationExpressionSyntax c => c.ArgumentList.Arguments.Select(a => a.Expression).ToList(),
            ObjectCreationExpressionSyntax c => c.ArgumentList?.Arguments.Select(a => a.Expression).ToList(),
            InvocationExpressionSyntax i => i.ArgumentList.Arguments.Select(a => a.Expression).ToList(),
            _ => null,
        };

    private static string? LiteralString(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: string s } ? s : null;

    private static int? LiteralInt(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: int i } ? i : null;

    /// <summary>For 'EnumType.Member' returns "Member" when the type name matches.</summary>
    private static string? EnumMemberName(ExpressionSyntax expression, string enumTypeName) =>
        expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax type,
            Name: IdentifierNameSyntax member,
        } && type.Identifier.Text == enumTypeName
            ? member.Identifier.Text
            : null;
}
