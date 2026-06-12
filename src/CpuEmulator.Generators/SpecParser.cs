using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CpuEmulator.Generators;

internal static class SpecParser
{
    /// <summary>Expected argument kind for each micro-op parameter position.</summary>
    private enum ArgKind { Reg, Flag, Bool }

    // ───────────────────────── MIRROR TABLES ─────────────────────────
    // These sets mirror, by name, surface defined elsewhere and MUST be updated together:
    //   • s_addrModes        ↔ CpuEmulator.Core.Specification.AddrMode members
    //   • s_regMembers       ↔ Reg members            • s_flagMembers ↔ Flag members
    //   • s_microOpSignatures (names+arity+arg kinds) ↔ Spec factory methods / Op records
    //   • op-kind class sets ↔ CpuEmitter's per-class emission switches
    // External mirrors of the same surface: CpuEmitter.FlagBit (Flag bit values),
    // tools/CpuEmulator.SpecImporter SemanticsMap.FactoryArity + SpecFileEmitter.SupportedModes.
    // The syntax-only generator cannot see the real enums; these tables ARE its truth.
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Per-op argument signatures; arity is the signature length. Each argument is
    /// parsed against its EXPECTED kind only (CPUGEN011 on mismatch) — no coalescing chain,
    /// so e.g. SetNZ(Flag.Z) or BranchIf(Reg.A, ...) cannot reach the emitter.
    /// Allowed op names: Load, Store, Transfer, Increment, SetNZ, Jump, BranchIf,
    /// Adc, Sbc, And, Ora, Eor, Compare, Bit,
    /// ShiftLeft, ShiftRight, RotateLeft, RotateRight, IncrementMem, DecrementMem, Decrement,
    /// Push, Pull, PushP, PullP, SetFlag, Jsr, Rts.</summary>
    private static readonly Dictionary<string, ArgKind[]> s_microOpSignatures = new(System.StringComparer.Ordinal)
    {
        // Load/store/transfer/register
        ["Load"] = new[] { ArgKind.Reg },
        ["Store"] = new[] { ArgKind.Reg },
        ["Transfer"] = new[] { ArgKind.Reg, ArgKind.Reg },
        ["Increment"] = new[] { ArgKind.Reg },
        ["SetNZ"] = new[] { ArgKind.Reg },
        // Jump/branch
        ["Jump"] = System.Array.Empty<ArgKind>(),
        ["BranchIf"] = new[] { ArgKind.Flag, ArgKind.Bool },
        // ALU (Task 5)
        ["Adc"] = System.Array.Empty<ArgKind>(),
        ["Sbc"] = System.Array.Empty<ArgKind>(),
        ["And"] = System.Array.Empty<ArgKind>(),
        ["Ora"] = System.Array.Empty<ArgKind>(),
        ["Eor"] = System.Array.Empty<ArgKind>(),
        ["Compare"] = new[] { ArgKind.Reg },
        ["Bit"] = System.Array.Empty<ArgKind>(),
        // RMW (Task 6)
        ["ShiftLeft"] = System.Array.Empty<ArgKind>(),
        ["ShiftRight"] = System.Array.Empty<ArgKind>(),
        ["RotateLeft"] = System.Array.Empty<ArgKind>(),
        ["RotateRight"] = System.Array.Empty<ArgKind>(),
        ["IncrementMem"] = System.Array.Empty<ArgKind>(),
        ["DecrementMem"] = System.Array.Empty<ArgKind>(),
        ["Decrement"] = new[] { ArgKind.Reg },
        // Stack / flag / flow (Task 7)
        ["Push"] = new[] { ArgKind.Reg },
        ["Pull"] = new[] { ArgKind.Reg },
        ["PushP"] = System.Array.Empty<ArgKind>(),
        ["PullP"] = System.Array.Empty<ArgKind>(),
        ["SetFlag"] = new[] { ArgKind.Flag, ArgKind.Bool },
        ["Jsr"] = System.Array.Empty<ArgKind>(),
        ["Rts"] = System.Array.Empty<ArgKind>(),
    };

    private static readonly HashSet<string> s_addrModes = new(System.StringComparer.Ordinal)
    {
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative",
    };

    /// <summary>Members of the Reg enum. A micro-op Reg argument MUST be one of these
    /// (CPUGEN011 if not) AND must be declared in the spec's Registers table (CPUGEN008).</summary>
    private static readonly HashSet<string> s_regMembers = new(System.StringComparer.Ordinal)
    {
        "A", "X", "Y", "S",
    };

    /// <summary>Valid Flag enum members for BranchIf (CPUGEN006 for anything else).</summary>
    private static readonly HashSet<string> s_flagMembers = new(System.StringComparer.Ordinal)
    {
        "C", "Z", "I", "D", "V", "N",
    };

    /// <summary>Local variable names the emitter writes into opcode bodies (and Step/Run).
    /// A register with one of these names would shadow/collide in generated code, so it is
    /// rejected at parse time (CPUGEN002).</summary>
    private static readonly HashSet<string> s_reservedLocalNames = new(System.StringComparer.Ordinal)
    {
        "data", "addr", "lo", "hi", "ea", "offset", "target",
        "opcode", "before", "value", "ptr", "temp",
    };

    private static readonly HashSet<string> s_registerOpKinds = new(System.StringComparer.Ordinal)
    {
        "Transfer", "Increment", "SetNZ",
        "Decrement",  // register-class use: DEX/DEY (op is register class, not rmw class)
        "SetFlag",    // register-class use: CLC/SEC/CLI/SEI/CLV/CLD/SED
    };

    private static readonly HashSet<string> s_aluOpKinds = new(System.StringComparer.Ordinal)
    {
        "Adc", "Sbc", "And", "Ora", "Eor", "Compare", "Bit",
    };

    private static readonly HashSet<string> s_rmwOpKinds = new(System.StringComparer.Ordinal)
    {
        "ShiftLeft", "ShiftRight", "RotateLeft", "RotateRight", "IncrementMem", "DecrementMem",
    };

    private static readonly HashSet<string> s_stackOpKinds = new(System.StringComparer.Ordinal)
    {
        "Push", "Pull", "PushP", "PullP",
    };

    private static readonly HashSet<string> s_flowOpKinds = new(System.StringComparer.Ordinal)
    {
        "Jsr", "Rts",
    };

    // Per-class allowed modes: load/alu share the same 9 modes; rmw has its own 5.
    private static readonly HashSet<string> s_loadAluModes = new(System.StringComparer.Ordinal)
    {
        "Immediate", "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY", "IndirectX", "IndirectY",
    };

    private static readonly HashSet<string> s_storeModes = new(System.StringComparer.Ordinal)
    {
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY", "IndirectX", "IndirectY",
    };

    private static readonly HashSet<string> s_rmwModes = new(System.StringComparer.Ordinal)
    {
        "ZeroPage", "ZeroPageX", "Absolute", "AbsoluteX", "Accumulator",
    };

    public static ParsedSpec Parse(GeneratorAttributeSyntaxContext context)
    {
        // Predicate was widened to TypeDeclarationSyntax; reject non-class kinds here.
        // Note: RecordDeclarationSyntax derives from TypeDeclarationSyntax, NOT from
        // ClassDeclarationSyntax, so this single check also rejects record declarations.
        if (context.TargetNode is not ClassDeclarationSyntax classDecl)
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

        // Invariant: every CPUGEN descriptor is Error severity, so ANY diagnostic nulls
        // the model. If a warning-severity descriptor is ever added, gate on severity here.
        if (diagnostics.Count > 0)
            return new ParsedSpec(null, diagnostics.ToImmutable());

        var registerNames = new HashSet<string>(registers.Select(r => r.Name), System.StringComparer.Ordinal);
        bool hasStackPointer = registers.Any(r => r.Role == "StackPointer");
        bool hasStatus = registers.Any(r => r.Role == "Status");
        var instructions = ParseInstructions(
            classDecl, specName, registerNames, hasStackPointer, hasStatus, diagnostics);

        if (diagnostics.Count > 0)
            return new ParsedSpec(null, diagnostics.ToImmutable());

        var model = new SpecModel(ns, cpuName, architecture,
            LocationInfo.From(classDecl.Identifier.GetLocation()), registers, instructions);
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

            // Reserved emitted-local names: reject to prevent collision in generated code (CPUGEN002).
            if (s_reservedLocalNames.Contains(name))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name,
                    $"register name '{name}' collides with an emitted local name"));
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
        bool hasStackPointer,
        bool hasStatus,
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

            // Mode/op class validation (CPUGEN010); returns the class on success.
            if (!ValidateModeOpClass(element.GetLocation(), mnemonic, mode, ops, registerNames,
                    hasStackPointer, hasStatus, diagnostics, out var instructionClass))
                continue;

            instructions.Add(new InstructionModel((byte)opcode.Value, mnemonic, mode, instructionClass, ops));
        }

        return instructions.ToImmutable();
    }

    /// <summary>
    /// Validates that the instruction's addressing mode is consistent with its op class,
    /// that indexed modes have their index register, and that classes touching the stack
    /// or status flags have the required role registers declared.
    /// Returns false (and emits CPUGEN010) on violation; returns the class via out on success.
    /// </summary>
    private static bool ValidateModeOpClass(
        Location location,
        string mnemonic,
        string mode,
        ImmutableArray<OpModel> ops,
        HashSet<string> registerNames,
        bool hasStackPointer,
        bool hasStatus,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        out InstructionClass instructionClass)
    {
        instructionClass = InstructionClass.Register;

        InstructionClass? opClass = ClassifyOps(ops, out string? classError);
        if (opClass is null)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic, classError!));
            return false;
        }

        instructionClass = opClass.Value;

        string? firstOpKind = ops.Length > 0 ? ops[0].Kind : null;
        string? modeError = ValidateModeForClass(mode, instructionClass, firstOpKind);
        if (modeError is not null)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic, modeError));
            return false;
        }

        // Index-register requirement: X-indexed modes require a register named 'X';
        // Y-indexed modes require 'Y'. This is the 6502 convention baked into the templates.
        if (RequiredIndexRegister(mode) is { } index && !registerNames.Contains(index))
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic, $"mode '{mode}' requires a register named '{index}'"));
            return false;
        }

        // Stack-touching classes need a StackPointer-role register — the emitter writes its
        // NAME into the templates; without it the generated code would not compile (CS0103).
        if (instructionClass is InstructionClass.Stack or InstructionClass.Flow && !hasStackPointer)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic,
                $"{(instructionClass == InstructionClass.Stack ? "stack" : "flow")} class requires a StackPointer-role register"));
            return false;
        }

        // Flag-writing classes need a Status register (alu/rmw write C/V/N/Z; Pull bakes NZ,
        // PushP/PullP move P itself).
        if (instructionClass is InstructionClass.Alu or InstructionClass.Rmw or InstructionClass.Stack
            && !hasStatus)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic,
                $"{instructionClass.ToString().ToLowerInvariant()} class requires a Status-role register"));
            return false;
        }

        return true;
    }

    private static InstructionClass? ClassifyOps(ImmutableArray<OpModel> ops, out string? error)
    {
        error = null;
        if (ops.Length == 0)
            return InstructionClass.Register; // empty = implied/register class

        string first = ops[0].Kind;

        if (first == "Load")
        {
            // Remaining must all be register ops
            for (int i = 1; i < ops.Length; i++)
            {
                if (!s_registerOpKinds.Contains(ops[i].Kind))
                {
                    error = $"Load must be the first op and remaining ops must be register ops (Transfer/Increment/SetNZ/Decrement/SetFlag), but found '{ops[i].Kind}' after Load";
                    return null;
                }
            }
            return InstructionClass.Load;
        }

        if (first == "Store")
        {
            if (ops.Length != 1)
            {
                error = "Store class must contain exactly one Store op";
                return null;
            }
            return InstructionClass.Store;
        }

        if (first == "Jump")
        {
            if (ops.Length != 1)
            {
                error = "Jump class must contain exactly one Jump op";
                return null;
            }
            return InstructionClass.Jump;
        }

        if (first == "BranchIf")
        {
            if (ops.Length != 1)
            {
                error = "Branch class must contain exactly one BranchIf op";
                return null;
            }
            return InstructionClass.Branch;
        }

        // ALU class: exactly one ALU op, nothing else
        if (s_aluOpKinds.Contains(first))
        {
            if (ops.Length != 1)
            {
                error = "alu class must contain exactly one op (Adc/Sbc/And/Ora/Eor/Compare/Bit)";
                return null;
            }
            return InstructionClass.Alu;
        }

        // RMW class: exactly one RMW op, nothing else
        if (s_rmwOpKinds.Contains(first))
        {
            if (ops.Length != 1)
            {
                error = "rmw class must contain exactly one op (ShiftLeft/ShiftRight/RotateLeft/RotateRight/IncrementMem/DecrementMem)";
                return null;
            }
            return InstructionClass.Rmw;
        }

        // Stack class: exactly one stack op
        if (s_stackOpKinds.Contains(first))
        {
            if (ops.Length != 1)
            {
                error = "stack class must contain exactly one op (Push/Pull/PushP/PullP) — NZ is baked into Pull";
                return null;
            }
            return InstructionClass.Stack;
        }

        // Flow class: exactly one flow op
        if (s_flowOpKinds.Contains(first))
        {
            if (ops.Length != 1)
            {
                error = "flow class must contain exactly one op (Jsr/Rts)";
                return null;
            }
            return InstructionClass.Flow;
        }

        // All must be register ops
        foreach (var op in ops)
        {
            if (!s_registerOpKinds.Contains(op.Kind))
            {
                error = $"op '{op.Kind}' is not valid here; expected a load/store/jump/branch/alu/rmw/stack/flow op first, or only register ops (Transfer/Increment/SetNZ/Decrement/SetFlag)";
                return null;
            }
        }
        return InstructionClass.Register;
    }

    /// <summary>Returns the index register NAME required for this mode, or null if none.</summary>
    private static string? RequiredIndexRegister(string mode) => mode switch
    {
        "ZeroPageX" or "AbsoluteX" or "IndirectX" => "X",
        "ZeroPageY" or "AbsoluteY" or "IndirectY" => "Y",
        _ => null,
    };

    private static string? ValidateModeForClass(string mode, InstructionClass opClass, string? firstOpKind)
    {
        return opClass switch
        {
            // register class: Implied only
            InstructionClass.Register =>
                mode == "Implied" ? null
                : "register-class ops (Transfer/Increment/SetNZ/Decrement/SetFlag or empty) require Implied mode",

            // load class: Immediate + all 8 memory modes (9 total)
            InstructionClass.Load =>
                s_loadAluModes.Contains(mode) ? null
                : "Load requires a memory or immediate addressing mode",

            // alu class: same 9 modes as load
            InstructionClass.Alu =>
                s_loadAluModes.Contains(mode) ? null
                : "Alu requires a memory or immediate addressing mode",

            // store class: 8 memory modes (no Immediate)
            InstructionClass.Store =>
                s_storeModes.Contains(mode) ? null
                : "Store requires a memory addressing mode (ZeroPage/Absolute/Indirect families)",

            // rmw class: ZeroPage/ZeroPageX/Absolute/AbsoluteX/Accumulator
            InstructionClass.Rmw =>
                s_rmwModes.Contains(mode) ? null
                : "Rmw requires ZeroPage/ZeroPageX/Absolute/AbsoluteX/Accumulator mode",

            // jump class: Absolute and Indirect
            InstructionClass.Jump =>
                (mode == "Absolute" || mode == "Indirect") ? null
                : "Jump requires Absolute or Indirect mode",

            // branch class: Relative only
            InstructionClass.Branch =>
                mode == "Relative" ? null
                : "BranchIf requires Relative mode",

            // stack class: Implied only
            InstructionClass.Stack =>
                mode == "Implied" ? null
                : "stack class (Push/Pull/PushP/PullP) requires Implied mode",

            // flow class: per-OP matrix — Jsr requires Absolute, Rts requires Implied.
            // ClassifyOps guarantees flow has exactly one op of kind Jsr or Rts.
            InstructionClass.Flow when firstOpKind == "Jsr" =>
                mode == "Absolute" ? null : "Jsr requires Absolute mode",
            InstructionClass.Flow =>
                mode == "Implied" ? null : "Rts requires Implied mode",

            _ => $"unrecognised op class '{opClass}'",
        };
    }

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

            if (!s_microOpSignatures.TryGetValue(kind, out var signature))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownMicroOp,
                    element.GetLocation(), kind));
                return null;
            }

            if (invocation.ArgumentList.Arguments.Count != signature.Length)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    $"micro-op '{kind}' expects {signature.Length} argument(s)"));
                return null;
            }

            var opArgs = ImmutableArray.CreateBuilder<string>();
            for (int i = 0; i < signature.Length; i++)
            {
                var argument = invocation.ArgumentList.Arguments[i];
                ArgKind expected = signature[i];

                string? value = expected switch
                {
                    ArgKind.Reg => EnumMemberName(argument.Expression, "Reg"),
                    ArgKind.Flag => EnumMemberName(argument.Expression, "Flag"),
                    _ => BoolLiteral(argument.Expression),
                };
                if (value is null)
                {
                    string description = expected switch
                    {
                        ArgKind.Reg => "Reg member",
                        ArgKind.Flag => "Flag member",
                        _ => "bool literal",
                    };
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidMicroOpArgument,
                        argument.GetLocation(), (i + 1).ToString(), kind, description));
                    return null;
                }

                // Reg hardening: FIRST check that the value is a known Reg enum member (CPUGEN011).
                // Then check it's declared in the spec's Registers table (CPUGEN008).
                if (expected == ArgKind.Reg)
                {
                    if (!s_regMembers.Contains(value))
                    {
                        // Not a Reg enum member — report CPUGEN011 via the kind-mismatch path.
                        diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidMicroOpArgument,
                            argument.GetLocation(), (i + 1).ToString(), kind, "Reg member"));
                        return null;
                    }

                    if (!registerNames.Contains(value))
                    {
                        diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownRegisterInOp,
                            argument.GetLocation(), value));
                        // Keep parsing: error gating in Parse nulls the model.
                    }
                }

                // Flag whitelist: only C, Z, I, D, V, N are allowed (CPUGEN006)
                if (expected == ArgKind.Flag && !s_flagMembers.Contains(value))
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownMicroOp,
                        argument.GetLocation(), value));
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
