using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CpuEmulator.Generators;

internal static class SpecParser
{
    private static readonly Dictionary<string, int> s_microOpArity = new(System.StringComparer.Ordinal)
    {
        ["Load"] = 1, ["Store"] = 1, ["Transfer"] = 2, ["Increment"] = 1,
        ["SetNZ"] = 1, ["Jump"] = 0, ["BranchIf"] = 2,
    };

    private static readonly HashSet<string> s_addrModes = new(System.StringComparer.Ordinal)
    {
        "Implied", "Immediate", "ZeroPage", "Absolute", "Relative",
    };

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

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var registers = ParseRegisters(classDecl, specName, diagnostics);

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new ParsedSpec(null, diagnostics.ToImmutable());

        var instructions = ParseInstructions(classDecl, specName, diagnostics);

        // Cross-check: micro-op register arguments must match declared register names.
        var registerNames = new HashSet<string>(registers.Select(r => r.Name), System.StringComparer.Ordinal);
        var regMembers = new HashSet<string>(System.StringComparer.Ordinal) { "A", "X", "Y", "S" };
        foreach (var instruction in instructions)
            foreach (var op in instruction.Ops)
                foreach (var arg in op.Args)
                    if (regMembers.Contains(arg) && !registerNames.Contains(arg))
                        diagnostics.Add(Diagnostic.Create(SpecDiagnostics.UnknownRegisterInOp,
                            classDecl.Identifier.GetLocation(), arg));

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new ParsedSpec(null, diagnostics.ToImmutable());

        var model = new SpecModel(ns, cpuName, architecture, registers, instructions);
        return new ParsedSpec(model, diagnostics.ToImmutable());
    }

    private static ImmutableArray<RegisterModel> ParseRegisters(
        ClassDeclarationSyntax classDecl,
        string specName,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var field = FindArrayField(classDecl, "Registers");
        if (field?.Declaration.Variables[0].Initializer?.Value is not CollectionExpressionSyntax collection)
        {
            diagnostics.Add(Diagnostic.Create(
                SpecDiagnostics.MissingRegisters, classDecl.Identifier.GetLocation(), specName));
            return ImmutableArray<RegisterModel>.Empty;
        }

        var registers = ImmutableArray.CreateBuilder<RegisterModel>();
        var seenNames = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var element in collection.Elements)
        {
            if (element is not ExpressionElementSyntax expr ||
                GetCreationArguments(expr.Expression) is not { } args ||
                args.Count is < 2 or > 3 ||
                LiteralString(args[0]) is not { } name ||
                LiteralInt(args[1]) is not { } bits)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), element.ToString(),
                    "expected new(\"NAME\", bits[, RegisterRole.X]) with literal arguments"));
                continue;
            }

            if (bits is not (8 or 16))
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "register width must be 8 or 16 bits"));
                continue;
            }

            if (!seenNames.Add(name))
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "duplicate register name"));
                continue;
            }

            string role = "General";
            if (args.Count == 3)
            {
                if (EnumMemberName(args[2], "RegisterRole") is not { } parsedRole)
                {
                    diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidRegister,
                        element.GetLocation(), name, "third argument must be a RegisterRole member"));
                    continue;
                }
                role = parsedRole;
            }

            registers.Add(new RegisterModel(name, bits, role));
        }

        int pcCount = registers.Count(r => r.Role == "ProgramCounter");
        if (pcCount != 1)
            diagnostics.Add(Diagnostic.Create(SpecDiagnostics.RoleViolation,
                classDecl.Identifier.GetLocation(),
                $"spec must declare exactly one ProgramCounter register (found {pcCount})"));
        if (registers.Count(r => r.Role == "Status") > 1)
            diagnostics.Add(Diagnostic.Create(SpecDiagnostics.RoleViolation,
                classDecl.Identifier.GetLocation(), "spec declares more than one Status register"));

        return registers.ToImmutable();
    }

    private static ImmutableArray<InstructionModel> ParseInstructions(
        ClassDeclarationSyntax classDecl,
        string specName,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var field = FindArrayField(classDecl, "Instructions");
        if (field?.Declaration.Variables[0].Initializer?.Value is not CollectionExpressionSyntax collection)
        {
            diagnostics.Add(Diagnostic.Create(
                SpecDiagnostics.MissingInstructions, classDecl.Identifier.GetLocation(), specName));
            return ImmutableArray<InstructionModel>.Empty;
        }

        var instructions = ImmutableArray.CreateBuilder<InstructionModel>();
        var seenOpcodes = new HashSet<int>();

        foreach (var element in collection.Elements)
        {
            if (element is not ExpressionElementSyntax { Expression: InvocationExpressionSyntax invocation } ||
                InvokedName(invocation) != "Insn" ||
                invocation.ArgumentList.Arguments.Count != 4)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "expected Insn(opcode, \"MNEMONIC\", AddrMode.X, [micro-ops])"));
                continue;
            }

            var args = invocation.ArgumentList.Arguments;
            int? opcode = LiteralInt(args[0].Expression);
            string? mnemonic = LiteralString(args[1].Expression);
            string? mode = EnumMemberName(args[2].Expression, "AddrMode");

            if (opcode is null || mnemonic is null)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "opcode and mnemonic must be literals"));
                continue;
            }
            if (mode is null || !s_addrModes.Contains(mode))
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "third argument must be a known AddrMode member"));
                continue;
            }
            if (opcode is < 0 or > 0xFF || !seenOpcodes.Add(opcode.Value))
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.DuplicateOpcode,
                    element.GetLocation(), opcode.Value.ToString("X2")));
                continue;
            }

            if (args[3].Expression is not CollectionExpressionSyntax opsCollection ||
                ParseOps(opsCollection, diagnostics) is not { } ops)
            {
                continue; // ParseOps reported the diagnostic
            }

            instructions.Add(new InstructionModel((byte)opcode.Value, mnemonic, mode, ops));
        }

        return instructions.ToImmutable();
    }

    private static ImmutableArray<OpModel>? ParseOps(
        CollectionExpressionSyntax collection,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var ops = ImmutableArray.CreateBuilder<OpModel>();
        foreach (var element in collection.Elements)
        {
            if (element is not ExpressionElementSyntax { Expression: InvocationExpressionSyntax invocation } ||
                InvokedName(invocation) is not { } kind)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.UnknownMicroOp,
                    element.GetLocation(), Truncate(element.ToString())));
                return null;
            }

            if (!s_microOpArity.TryGetValue(kind, out int arity) ||
                invocation.ArgumentList.Arguments.Count != arity)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.UnknownMicroOp,
                    element.GetLocation(), kind));
                return null;
            }

            var opArgs = ImmutableArray.CreateBuilder<string>();
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                string? value =
                    EnumMemberName(argument.Expression, "Reg") ??
                    EnumMemberName(argument.Expression, "Flag") ??
                    BoolLiteral(argument.Expression);
                if (value is null)
                {
                    diagnostics.Add(Diagnostic.Create(SpecDiagnostics.UnknownMicroOp,
                        argument.GetLocation(), Truncate(argument.ToString())));
                    return null;
                }
                opArgs.Add(value);
            }

            ops.Add(new OpModel(kind, opArgs.ToImmutable()));
        }
        return ops.ToImmutable();
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax id } => id.Identifier.Text,
            _ => null,
        };

    private static string? BoolLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: bool b } ? (b ? "true" : "false") : null;

    private static string Truncate(string text) =>
        text.Length <= 60 ? text : text.Substring(0, 57) + "...";

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
