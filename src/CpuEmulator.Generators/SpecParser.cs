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
    //   • s_flagMembers      ↔ Flag members
    //   • s_microOpSignatures (names+arity+arg kinds) ↔ Spec factory methods / Op records
    //   • op-kind class sets ↔ CpuEmitter's per-class emission switches
    // External mirrors of the same surface: CpuEmitter.FlagBit (Flag bit values),
    // tools/CpuEmulator.SpecImporter SemanticsMap.FactoryArity + SpecFileEmitter.SupportedModes.
    // The syntax-only generator cannot see the real enums; these tables ARE its truth.
    //
    // M3.1a: register identity is no longer a fixed enum. A register-arg micro-op argument is a
    // register-NAME string literal (CPUGEN011 if not a string literal) cross-checked against the
    // spec's OWN Registers table (CPUGEN008 — the primary, per-spec register-name gate). There is
    // no s_regMembers whitelist to mirror; the spec's declared register set IS the truth.
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Per-op argument signatures; arity is the signature length. Each argument is
    /// parsed against its EXPECTED kind only (CPUGEN011 on mismatch) — no coalescing chain,
    /// so e.g. SetNZ(Flag.Z) or BranchIf("A", ...) cannot reach the emitter.
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
        ["Brk"] = System.Array.Empty<ArgKind>(),
        ["Rti"] = System.Array.Empty<ArgKind>(),
        // I/O-port + halt (M3.2 — additive). PortIn/PortOut name a register; Halt takes nothing.
        ["PortIn"] = new[] { ArgKind.Reg },
        ["PortOut"] = new[] { ArgKind.Reg },
        ["Halt"] = System.Array.Empty<ArgKind>(),
    };

    private static readonly HashSet<string> s_addrModes = new(System.StringComparer.Ordinal)
    {
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative",
        "IoPortImmediate", "IoPortIndirect",   // M3.2 (additive): the Z80 IN/OUT port-operand modes.
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
        "opcode", "before", "value", "ptr", "temp", "sum",
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
        "Jsr", "Rts", "Brk", "Rti",
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

        // Optional decode structure (Ground truth G). ABSENT (the 6502) ⇒ the degenerate walk.
        var decode = ParseDecodeStructure(classDecl, instructions, diagnostics);

        if (diagnostics.Count > 0)
            return new ParsedSpec(null, diagnostics.ToImmutable());

        var model = new SpecModel(ns, cpuName, architecture,
            LocationInfo.From(classDecl.Identifier.GetLocation()), registers, instructions, decode);
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
                InvokedName(invocation) != "Insn")
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "expected Insn(opcode, \"MNEMONIC\", AddrMode.X, [micro-ops])"));
                continue;
            }

            var args = invocation.ArgumentList.Arguments;

            // Resolve which Insn overload this is (Ground truth G — the 6502 single-byte form is the
            // default; the prefixed/opcode-group overloads are M3.1b, default-off):
            //   Insn(opcode, mnemonic, mode, ops)                      — 4 args, KeyShape.OpcodeByte
            //   Insn(prefix, opcode, mnemonic, mode, ops)              — 5 args, KeyShape.PrefixedOpcode
            //   Insn(opcode, subfield: n, mnemonic, mode, ops)         — 5 args (2nd named 'subfield'),
            //                                                             KeyShape.OpcodeGroup
            int? opcode, prefix = null, subField = null;
            KeyShape keyShape;
            int mnemonicIdx, modeIdx, opsIdx;

            if (args.Count == 4)
            {
                keyShape = KeyShape.OpcodeByte;
                opcode = LiteralInt(args[0].Expression);
                mnemonicIdx = 1; modeIdx = 2; opsIdx = 3;
            }
            else if (args.Count == 5 && args[1].NameColon?.Name.Identifier.Text == "subfield")
            {
                keyShape = KeyShape.OpcodeGroup;
                opcode = LiteralInt(args[0].Expression);
                subField = LiteralInt(args[1].Expression);
                mnemonicIdx = 2; modeIdx = 3; opsIdx = 4;
                if (subField is null or < 0 or > 7)
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                        element.GetLocation(), Truncate(element.ToString()),
                        "subfield must be a literal in 0-7"));
                    continue;
                }
            }
            else if (args.Count == 5)
            {
                keyShape = KeyShape.PrefixedOpcode;
                prefix = LiteralInt(args[0].Expression);
                opcode = LiteralInt(args[1].Expression);
                mnemonicIdx = 2; modeIdx = 3; opsIdx = 4;
                if (prefix is null or < 0 or > 0xFF)
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                        element.GetLocation(), Truncate(element.ToString()),
                        "prefix must be a literal in 0x00-0xFF"));
                    continue;
                }
            }
            else
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "expected Insn(opcode, \"MNEMONIC\", AddrMode.X, [micro-ops])"));
                continue;
            }

            string? mnemonic = LiteralString(args[mnemonicIdx].Expression);
            string? mode = EnumMemberName(args[modeIdx].Expression, "AddrMode");

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
                    "addressing-mode argument must be a known AddrMode member"));
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

            // The opaque operation-key (Ground truth C): the generated Decode packs it; here we
            // compute the value the descriptor table is keyed on. Single-byte: key == opcode (≤ 0xFF,
            // dense [256] index). Prefixed: (prefix << 8) | opcode. Opcode-group: (opcode << 3) | sub.
            uint operationKey = keyShape switch
            {
                KeyShape.PrefixedOpcode => (uint)((prefix!.Value << 8) | opcode.Value),
                KeyShape.OpcodeGroup => (uint)((opcode.Value << 3) | subField!.Value),
                _ => (uint)opcode.Value,
            };

            // Duplicate detection is on the full operation-key (so a prefixed 0x10 and a bare 0x10 are
            // distinct rows — the 256-table-cannot-express case, Ground truth C / 0001-…:119).
            if (!seenOpcodes.Add((int)operationKey))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.DuplicateOpcode,
                    element.GetLocation(), operationKey.ToString("X2")));
                continue;
            }

            if (args[opsIdx].Expression is not CollectionExpressionSyntax opsCollection ||
                ParseOps(opsCollection, registerNames, diagnostics) is not { } ops)
            {
                continue; // ParseOps reported the diagnostic
            }

            // Mode/op class validation (CPUGEN010); returns the class on success.
            if (!ValidateModeOpClass(element.GetLocation(), mnemonic, mode, ops, registerNames,
                    hasStackPointer, hasStatus, diagnostics, out var instructionClass))
                continue;

            instructions.Add(new InstructionModel(
                (byte)opcode.Value, mnemonic, mode, instructionClass, ops,
                operationKey, keyShape, prefix ?? -1, subField ?? -1));
        }

        return instructions.ToImmutable();
    }

    /// <summary>Parse the optional <c>Decode</c> field (Ground truth G). ABSENT ⇒ null (the 6502
    /// degenerate walk). Present ⇒ a <c>new(Prefixes: [...], ModRmOpcodes: [...], SubFieldOpcodes:
    /// [...])</c> creation whose three byte/PrefixByte collections are parsed into the model. A
    /// malformed structure reports CPUGEN012. A prefix byte must have a matching prefixed Insn row;
    /// a ModRm/sub-field opcode must have a matching row — the cross-checks that keep the structure
    /// and the instruction table consistent.</summary>
    private static DecodeStructureModel? ParseDecodeStructure(
        ClassDeclarationSyntax classDecl,
        ImmutableArray<InstructionModel> instructions,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var field = FindArrayField(classDecl, "Decode");
        if (field is null)
            return null;   // ABSENT — the 6502 default.

        Location loc = field.GetLocation();
        if (field.Declaration.Variables[0].Initializer?.Value is not { } init ||
            GetCreationArguments(init) is not { } args)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidDecodeStructure, loc,
                "expected new(Prefixes: [..], ModRmOpcodes: [..], SubFieldOpcodes: [..])"));
            return null;
        }

        var prefixes = ImmutableArray.CreateBuilder<byte>();
        var modRm = ImmutableArray.CreateBuilder<byte>();
        var subField = ImmutableArray.CreateBuilder<byte>();

        // The three args are positional or named; parse each collection by position
        // (Prefixes, ModRmOpcodes, SubFieldOpcodes — the DecodeStructure record order).
        bool ok = true;
        if (args.Count != 3)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidDecodeStructure, loc,
                "DecodeStructure takes exactly Prefixes, ModRmOpcodes, SubFieldOpcodes"));
            return null;
        }

        // Prefixes: a collection of PrefixByte(0xNN) creations.
        if (args[0] is CollectionExpressionSyntax prefixColl)
        {
            foreach (var e in prefixColl.Elements)
            {
                if (e is ExpressionElementSyntax pe &&
                    GetCreationArguments(pe.Expression) is { Count: 1 } pargs &&
                    LiteralInt(pargs[0]) is { } pv and >= 0 and <= 0xFF)
                    prefixes.Add((byte)pv);
                else { ok = false; }
            }
        }
        else { ok = false; }

        ok &= ParseByteCollection(args[1], modRm);
        ok &= ParseByteCollection(args[2], subField);

        if (!ok)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidDecodeStructure, loc,
                "Prefixes must be PrefixByte(0xNN); ModRmOpcodes/SubFieldOpcodes must be 0xNN byte literals"));
            return null;
        }

        // Cross-check: every declared prefix byte must back at least one prefixed Insn row, and every
        // ModRm/sub-field opcode must back at least one matching row (so the structure and the table
        // cannot drift). A malformed/orphan declaration is CPUGEN012.
        foreach (byte p in prefixes)
            if (!instructions.Any(i => i.KeyShape == KeyShape.PrefixedOpcode && i.Prefix == p))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidDecodeStructure, loc,
                    $"prefix 0x{p:X2} has no prefixed Insn row"));
                return null;
            }
        foreach (byte m in modRm)
            if (!instructions.Any(i => i.Opcode == m && i.KeyShape == KeyShape.OpcodeByte))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidDecodeStructure, loc,
                    $"ModRm opcode 0x{m:X2} has no Insn row"));
                return null;
            }
        foreach (byte s in subField)
            if (!instructions.Any(i => i.Opcode == s && i.KeyShape == KeyShape.OpcodeGroup))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidDecodeStructure, loc,
                    $"sub-field opcode 0x{s:X2} has no opcode-group Insn row"));
                return null;
            }

        return new DecodeStructureModel(prefixes.ToImmutable(), modRm.ToImmutable(), subField.ToImmutable());
    }

    /// <summary>Parse a collection of 0xNN byte literals into the builder; false on any non-literal
    /// or out-of-range element.</summary>
    private static bool ParseByteCollection(ExpressionSyntax expr, ImmutableArray<byte>.Builder into)
    {
        if (expr is not CollectionExpressionSyntax coll)
            return false;
        foreach (var e in coll.Elements)
        {
            if (e is ExpressionElementSyntax ee && LiteralInt(ee.Expression) is { } v and >= 0 and <= 0xFF)
                into.Add((byte)v);
            else
                return false;
        }
        return true;
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
        // PushP/PullP move P itself). BRK/RTI also touch P: BRK stacks P|0x30 and sets I,
        // RTI restores P from the stack — the emitter writes the Status NAME into both
        // templates, so a Status-role register is mandatory (CS0103 otherwise).
        bool flowTouchesStatus = instructionClass == InstructionClass.Flow
            && firstOpKind is "Brk" or "Rti";
        if ((instructionClass is InstructionClass.Alu or InstructionClass.Rmw or InstructionClass.Stack
                || flowTouchesStatus)
            && !hasStatus)
        {
            string message = flowTouchesStatus
                ? $"flow op '{firstOpKind}' requires a Status-role register"
                : $"{instructionClass.ToString().ToLowerInvariant()} class requires a Status-role register";
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic, message));
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
                error = "flow class must contain exactly one op (Jsr/Rts/Brk/Rti)";
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

            // flow class: per-OP matrix — Jsr requires Absolute; Rts/Brk/Rti require Implied.
            // ClassifyOps guarantees flow has exactly one op of kind Jsr/Rts/Brk/Rti.
            InstructionClass.Flow when firstOpKind == "Jsr" =>
                mode == "Absolute" ? null : "Jsr requires Absolute mode",
            InstructionClass.Flow when firstOpKind == "Brk" =>
                mode == "Implied" ? null : "Brk requires Implied mode",
            InstructionClass.Flow when firstOpKind == "Rti" =>
                mode == "Implied" ? null : "Rti requires Implied mode",
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
                    ArgKind.Reg => LiteralString(argument.Expression),            // register arg is a STRING LITERAL
                    ArgKind.Flag => EnumMemberName(argument.Expression, "Flag"),  // Flag UNCHANGED (out of scope)
                    _ => BoolLiteral(argument.Expression),
                };
                if (value is null)
                {
                    string description = expected switch
                    {
                        ArgKind.Reg => "register-name string literal",
                        ArgKind.Flag => "Flag member",
                        _ => "bool literal",
                    };
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidMicroOpArgument,
                        argument.GetLocation(), (i + 1).ToString(), kind, description));   // CPUGEN011 (kind)
                    return null;
                }

                // CPUGEN008 — THE primary register-name check (was a two-stage enum-then-table check).
                // The register name must name a row in the spec's OWN Registers table. With no enum
                // pre-filter, an undeclared name is a hard stop here (the model nulls in Parse).
                if (expected == ArgKind.Reg && !registerNames.Contains(value))
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownRegisterInOp,
                        argument.GetLocation(), value));
                    return null;
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
