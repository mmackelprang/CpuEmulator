using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    /// <summary>Members of the Reg enum; micro-op args matching these must also appear in the
    /// spec's Registers table (CPUGEN008 otherwise).</summary>
    private static readonly HashSet<string> s_regMembers = new(System.StringComparer.Ordinal)
    {
        "A", "X", "Y", "S",
    };

    /// <summary>Valid Flag enum members for BranchIf (CPUGEN006 for anything else).</summary>
    private static readonly HashSet<string> s_flagMembers = new(System.StringComparer.Ordinal)
    {
        "C", "Z", "I", "D", "V", "N",
    };

    public static ParsedSpec Parse(GeneratorAttributeSyntaxContext context)
    {
        // Predicate was widened to TypeDeclarationSyntax; reject non-class kinds here.
        if (context.TargetNode is not ClassDeclarationSyntax classDecl ||
            context.TargetNode is RecordDeclarationSyntax)
        {
            var typeDecl = (TypeDeclarationSyntax)context.TargetNode;
            var earlyDiag = new DiagnosticInfo(
                SpecDiagnostics.InvalidSpecMetadata,
                typeDecl.Identifier.GetLocation(),
                "spec must be a non-record class declaration");
            return new ParsedSpec(null, ImmutableArray.Create(earlyDiag));
        }

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (context.TargetSymbol.ContainingNamespace.IsGlobalNamespace)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidSpecMetadata,
                classDecl.Identifier.GetLocation(),
                "spec class must be declared inside a namespace"));
            return new ParsedSpec(null, diagnostics.ToImmutable());
        }

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

        if (!SyntaxFacts.IsValidIdentifier(cpuName))
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidSpecMetadata,
                classDecl.Identifier.GetLocation(),
                $"generated CPU class name '{cpuName}' is not a valid C# identifier"));
        }

        var registers = ParseRegisters(classDecl, specName, diagnostics);

        if (diagnostics.Any(d => d.DescriptorId is "CPUGEN001" or "CPUGEN002" or "CPUGEN007" or "CPUGEN009"))
            return new ParsedSpec(null, diagnostics.ToImmutable());

        var registerNames = new HashSet<string>(registers.Select(r => r.Name), System.StringComparer.Ordinal);
        var instructions = ParseInstructions(classDecl, specName, registerNames, diagnostics);

        if (diagnostics.Any(d => d.DescriptorId is
            "CPUGEN003" or "CPUGEN004" or "CPUGEN005" or
            "CPUGEN006" or "CPUGEN007" or "CPUGEN008" or "CPUGEN010"))
            return new ParsedSpec(null, diagnostics.ToImmutable());

        var model = new SpecModel(ns, cpuName, architecture, registers, instructions);
        return new ParsedSpec(model, diagnostics.ToImmutable());
    }

    private static ImmutableArray<RegisterModel> ParseRegisters(
        ClassDeclarationSyntax classDecl,
        string specName,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var field = FindArrayField(classDecl, "Registers");
        if (field?.Declaration.Variables[0].Initializer?.Value is not CollectionExpressionSyntax collection)
        {
            diagnostics.Add(new DiagnosticInfo(
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
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), element.ToString(),
                    "expected new(\"NAME\", bits[, RegisterRole.X]) with literal arguments"));
                continue;
            }

            if (!SyntaxFacts.IsValidIdentifier(name))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "register name must be a valid C# identifier"));
                continue;
            }

            if (bits is not (8 or 16))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "register width must be 8 or 16 bits"));
                continue;
            }

            if (!seenNames.Add(name))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "duplicate register name"));
                continue;
            }

            string role = "General";
            if (args.Count == 3)
            {
                if (EnumMemberName(args[2], "RegisterRole") is not { } parsedRole)
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                        element.GetLocation(), name, "third argument must be a RegisterRole member"));
                    continue;
                }
                role = parsedRole;
            }

            registers.Add(new RegisterModel(name, bits, role));
        }

        int pcCount = registers.Count(r => r.Role == "ProgramCounter");
        if (pcCount != 1)
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.RoleViolation,
                classDecl.Identifier.GetLocation(),
                $"spec must declare exactly one ProgramCounter register (found {pcCount})"));
        if (registers.Count(r => r.Role == "Status") > 1)
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.RoleViolation,
                classDecl.Identifier.GetLocation(), "spec declares more than one Status register"));

        return registers.ToImmutable();
    }

    private static ImmutableArray<InstructionModel> ParseInstructions(
        ClassDeclarationSyntax classDecl,
        string specName,
        HashSet<string> registerNames,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var field = FindArrayField(classDecl, "Instructions");
        if (field?.Declaration.Variables[0].Initializer?.Value is not CollectionExpressionSyntax collection)
        {
            diagnostics.Add(new DiagnosticInfo(
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
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
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
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "opcode and mnemonic must be literals"));
                continue;
            }
            if (mode is null || !s_addrModes.Contains(mode))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "third argument must be a known AddrMode member"));
                continue;
            }
            if (opcode is < 0 or > 0xFF)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    $"opcode 0x{opcode.Value:X} is outside 0x00-0xFF"));
                continue;
            }

            // Mnemonic validation: must match ^[A-Z][A-Z0-9]{0,7}$
            if (!IsValidMnemonic(mnemonic))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "mnemonic must be 1-8 uppercase letters/digits"));
                continue;
            }

            if (!seenOpcodes.Add(opcode.Value))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.DuplicateOpcode,
                    element.GetLocation(), opcode.Value.ToString("X2")));
                continue;
            }

            if (args[3].Expression is not CollectionExpressionSyntax opsCollection ||
                ParseOps(opsCollection, registerNames, diagnostics) is not { } ops)
            {
                continue; // ParseOps reported the diagnostic
            }

            // Mode/op class validation (CPUGEN010)
            if (!ValidateModeOpClass(element.GetLocation(), mnemonic, mode, ops, diagnostics))
                continue;

            instructions.Add(new InstructionModel((byte)opcode.Value, mnemonic, mode, ops));
        }

        return instructions.ToImmutable();
    }

    /// <summary>
    /// Validates that the instruction's addressing mode is consistent with its op class.
    /// Returns false (and emits CPUGEN010) on violation.
    /// </summary>
    private static bool ValidateModeOpClass(
        Location location,
        string mnemonic,
        string mode,
        ImmutableArray<OpModel> ops,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        // Determine op class from ops array.
        // register class: all ops are Transfer/Increment/SetNZ (empty is also register class)
        // load class: first op is Load, remaining are register ops
        // store class: exactly one Store
        // jump class: exactly one Jump
        // branch class: exactly one BranchIf

        string? opClass = ClassifyOps(ops, out string? classError);
        if (opClass is null)
        {
            // ops themselves are ambiguous/invalid — report CPUGEN010
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic, classError!));
            return false;
        }

        string? modeError = ValidateModeForClass(mode, opClass);
        if (modeError is not null)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic, modeError));
            return false;
        }

        return true;
    }

    private static readonly HashSet<string> s_registerOpKinds = new(System.StringComparer.Ordinal)
    {
        "Transfer", "Increment", "SetNZ",
    };

    private static string? ClassifyOps(ImmutableArray<OpModel> ops, out string? error)
    {
        error = null;
        if (ops.Length == 0)
            return "register"; // empty = implied/register class

        string first = ops[0].Kind;

        if (first == "Load")
        {
            // Remaining must all be register ops
            for (int i = 1; i < ops.Length; i++)
            {
                if (!s_registerOpKinds.Contains(ops[i].Kind))
                {
                    error = $"Load must be the first op and remaining ops must be register ops (Transfer/Increment/SetNZ), but found '{ops[i].Kind}' after Load";
                    return null;
                }
            }
            return "load";
        }

        if (first == "Store")
        {
            if (ops.Length != 1)
            {
                error = "Store class must contain exactly one Store op";
                return null;
            }
            return "store";
        }

        if (first == "Jump")
        {
            if (ops.Length != 1)
            {
                error = "Jump class must contain exactly one Jump op";
                return null;
            }
            return "jump";
        }

        if (first == "BranchIf")
        {
            if (ops.Length != 1)
            {
                error = "Branch class must contain exactly one BranchIf op";
                return null;
            }
            return "branch";
        }

        // All must be register ops
        foreach (var op in ops)
        {
            if (!s_registerOpKinds.Contains(op.Kind))
            {
                error = $"op '{op.Kind}' is not valid here; expected a load/store/jump/branch op first, or only register ops (Transfer/Increment/SetNZ)";
                return null;
            }
        }
        return "register";
    }

    private static string? ValidateModeForClass(string mode, string opClass) => (opClass, mode) switch
    {
        ("register", "Implied") => null,
        ("register", _) => "register-class ops (Transfer/Increment/SetNZ or empty) require Implied mode",
        ("load", "Immediate") => null,
        ("load", "ZeroPage") => null,
        ("load", "Absolute") => null,
        ("load", _) => "Load requires Immediate, ZeroPage, or Absolute mode",
        ("store", "ZeroPage") => null,
        ("store", "Absolute") => null,
        ("store", _) => "Store requires ZeroPage or Absolute mode",
        ("jump", "Absolute") => null,
        ("jump", _) => "Jump requires Absolute mode",
        ("branch", "Relative") => null,
        ("branch", _) => "BranchIf requires Relative mode",
        _ => $"unrecognised op class '{opClass}'"
    };

    /// <summary>Mnemonic must match ^[A-Z][A-Z0-9]{0,7}$ (1–8 uppercase letters/digits, first must be letter).</summary>
    private static bool IsValidMnemonic(string mnemonic)
    {
        if (mnemonic.Length == 0 || mnemonic.Length > 8)
            return false;
        if (mnemonic[0] < 'A' || mnemonic[0] > 'Z')
            return false;
        for (int i = 1; i < mnemonic.Length; i++)
        {
            char c = mnemonic[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                return false;
        }
        return true;
    }

    private static ImmutableArray<OpModel>? ParseOps(
        CollectionExpressionSyntax collection,
        HashSet<string> registerNames,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var ops = ImmutableArray.CreateBuilder<OpModel>();
        foreach (var element in collection.Elements)
        {
            if (element is not ExpressionElementSyntax { Expression: InvocationExpressionSyntax invocation } ||
                InvokedName(invocation) is not { } kind)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownMicroOp,
                    element.GetLocation(), Truncate(element.ToString())));
                return null;
            }

            if (!s_microOpArity.TryGetValue(kind, out int arity))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownMicroOp,
                    element.GetLocation(), kind));
                return null;
            }

            if (invocation.ArgumentList.Arguments.Count != arity)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    $"micro-op '{kind}' expects {arity} argument(s)"));
                return null;
            }

            var opArgs = ImmutableArray.CreateBuilder<string>();
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                string? regName = EnumMemberName(argument.Expression, "Reg");
                string? flagName = EnumMemberName(argument.Expression, "Flag");
                string? value =
                    regName ??
                    flagName ??
                    BoolLiteral(argument.Expression);
                if (value is null)
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownMicroOp,
                        argument.GetLocation(), Truncate(argument.ToString())));
                    return null;
                }

                if (regName is not null && s_regMembers.Contains(regName) &&
                    !registerNames.Contains(regName))
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownRegisterInOp,
                        argument.GetLocation(), regName));
                    // Keep parsing: error gating in Parse nulls the model.
                }

                // Flag whitelist: only C, Z, I, D, V, N are allowed (CPUGEN006)
                if (flagName is not null && !s_flagMembers.Contains(flagName))
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownMicroOp,
                        argument.GetLocation(), flagName));
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
