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
    private enum ArgKind
    {
        Reg, Flag, Bool,
        Str,    // M3.4b: a bare string literal arg (the CB op name "RLC"/"BIT"/… or the target "(HL)")
        Int,    // M3.4b: a bare integer literal arg (the CB bit index 0..7)
    }

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
        // Composable flag micro-ops (M3.4a — general). SetSZ/SetParity/SetXY name a result register;
        // SetAddSub takes a bool (true = subtract).
        ["SetSZ"] = new[] { ArgKind.Reg },
        ["SetParity"] = new[] { ArgKind.Reg },
        ["SetXY"] = new[] { ArgKind.Reg },
        ["SetAddSub"] = new[] { ArgKind.Bool },
        // ── M3.4a Z80 base-plane micro-ops ──
        // 8-bit flag-correct ALU — A is implicit; the SOURCE is resolved by the mode (a register in
        // Register mode, (HL) in RegisterIndirect, n in Immediate). Arity 0.
        ["Add8"] = System.Array.Empty<ArgKind>(),
        ["Adc8"] = System.Array.Empty<ArgKind>(),
        ["Sub8"] = System.Array.Empty<ArgKind>(),
        ["Sbc8"] = System.Array.Empty<ArgKind>(),
        ["And8"] = System.Array.Empty<ArgKind>(),
        ["Or8"] = System.Array.Empty<ArgKind>(),
        ["Xor8"] = System.Array.Empty<ArgKind>(),
        ["Cp8"] = System.Array.Empty<ArgKind>(),
        // 8-bit INC/DEC — the target register (Register), or (HL) implied by RegisterIndirect (no arg).
        ["IncReg"] = new[] { ArgKind.Reg },
        ["DecReg"] = new[] { ArgKind.Reg },
        ["IncMem8"] = System.Array.Empty<ArgKind>(),   // INC (HL) — target is the pair-EA byte
        ["DecMem8"] = System.Array.Empty<ArgKind>(),   // DEC (HL)
        // 16-bit ALU.
        ["Add16"] = new[] { ArgKind.Reg, ArgKind.Reg },  // Add16("HL","BC")
        ["Inc16"] = new[] { ArgKind.Reg },
        ["Dec16"] = new[] { ArgKind.Reg },
        // 16-bit LD (Load16 reg ← operand; Store16 reg → memory; LoadMem16 reg ← (nn)).
        ["Load16"] = new[] { ArgKind.Reg },      // LD rr,nn (ImmediateExtended)
        ["Store16"] = new[] { ArgKind.Reg },     // LD (nn),rr (ExtendedAddress)
        ["LoadMem16"] = new[] { ArgKind.Reg },   // LD rr,(nn) (ExtendedAddress)
        ["StoreImm8"] = System.Array.Empty<ArgKind>(),   // LD (HL),n
        // 16-bit pair stack.
        ["Push16"] = new[] { ArgKind.Reg },
        ["Pop16"] = new[] { ArgKind.Reg },
        // Exchange.
        ["ExDeHl"] = System.Array.Empty<ArgKind>(),
        ["ExAfAf"] = System.Array.Empty<ArgKind>(),
        ["Exx"] = System.Array.Empty<ArgKind>(),
        ["ExSpHl"] = System.Array.Empty<ArgKind>(),
        // Conditional + relative flow. cc is a Flag + bool sense pair (like BranchIf).
        ["JumpIf"] = new[] { ArgKind.Flag, ArgKind.Bool },
        ["CallIf"] = new[] { ArgKind.Flag, ArgKind.Bool },
        ["RetCc"] = new[] { ArgKind.Flag, ArgKind.Bool },
        ["RelJump"] = System.Array.Empty<ArgKind>(),
        ["RelJumpIf"] = new[] { ArgKind.Flag, ArgKind.Bool },
        ["Djnz"] = new[] { ArgKind.Reg },         // Djnz("B")
        ["Rst"] = System.Array.Empty<ArgKind>(),  // vector derived from the opcode (opcode & 0x38)
        ["JumpIndirect"] = System.Array.Empty<ArgKind>(),  // JP (HL) — PC = HL
        ["JumpAbs"] = System.Array.Empty<ArgKind>(),       // JP nn — Z80 16-bit absolute jump
        ["CallAbs"] = System.Array.Empty<ArgKind>(),       // CALL nn — Z80 unconditional call
        ["Ret"] = System.Array.Empty<ArgKind>(),           // RET — Z80 unconditional return
        // Misc.
        ["Daa"] = System.Array.Empty<ArgKind>(),
        ["Cpl"] = System.Array.Empty<ArgKind>(),
        ["Scf"] = System.Array.Empty<ArgKind>(),
        ["Ccf"] = System.Array.Empty<ArgKind>(),
        ["Di"] = System.Array.Empty<ArgKind>(),
        ["Ei"] = System.Array.Empty<ArgKind>(),
        // M3.4b: rotate-accumulators (zero-arg) + the CB rotate/shift + BIT/RES/SET ops.
        ["Rlca"] = System.Array.Empty<ArgKind>(),
        ["Rrca"] = System.Array.Empty<ArgKind>(),
        ["Rla"] = System.Array.Empty<ArgKind>(),
        ["Rra"] = System.Array.Empty<ArgKind>(),
        ["CbRotate"] = new[] { ArgKind.Str, ArgKind.Str },  // CbRotate("RLC", "B")  (op name, target)
        ["CbBit"] = new[] { ArgKind.Str, ArgKind.Int, ArgKind.Str },  // CbBit("BIT", 7, "(HL)")
        // M3.4c: the ED-core ops.
        ["EdIn"]       = new[] { ArgKind.Str },                 // EdIn("B")
        ["EdOut"]      = new[] { ArgKind.Str },                 // EdOut("C")
        ["EdAdcSbc16"] = new[] { ArgKind.Str, ArgKind.Str },    // EdAdcSbc16("ADC", "HL")
        ["EdLdNnRp"]   = new[] { ArgKind.Str, ArgKind.Str },    // EdLdNnRp("STORE", "BC")
        ["EdNeg"]      = System.Array.Empty<ArgKind>(),
        ["EdRetn"]     = new[] { ArgKind.Bool },                // EdRetn(true)  (IsReti)
        ["EdIm"]       = new[] { ArgKind.Int },                 // EdIm(2)
        ["EdLdIaRa"]   = new[] { ArgKind.Str },                 // EdLdIaRa("A_I")
        ["EdRrdRld"]   = new[] { ArgKind.Bool },                // EdRrdRld(true) (IsRld)
        ["EdNop"]      = System.Array.Empty<ArgKind>(),
        // M3.4d: the ED block ops.
        ["EdBlock"]    = new[] { ArgKind.Str },                 // EdBlock("LDIR")
        // M3.4e-2: the DD/FD indexed ops.
        ["DdFdLdIndexed"]       = new[] { ArgKind.Str, ArgKind.Str },   // DdFdLdIndexed("LOAD", "A")
        ["DdFdStoreImmIndexed"] = System.Array.Empty<ArgKind>(),
        ["DdFdAluIndexed"]      = new[] { ArgKind.Str },               // DdFdAluIndexed("ADD")
        ["DdFdIncDecIndexed"]   = new[] { ArgKind.Bool },              // DdFdIncDecIndexed(true)
        // M3.4e-3: the DDCB/FDCB compound op.
        ["DdCb"] = new[] { ArgKind.Str, ArgKind.Int, ArgKind.Str },    // DdCb("RLC", 0, "B")
    };

    private static readonly HashSet<string> s_addrModes = new(System.StringComparer.Ordinal)
    {
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative",
        "IoPortImmediate", "IoPortIndirect",   // M3.2 (additive): the Z80 IN/OUT port-operand modes.
        // M3.4a (additive): the Z80 register-shape modes.
        "Register", "RegisterIndirect", "ImmediateExtended", "ExtendedAddress", "RelativeJump",
        "Bit",   // M3.4b (CB plane)
        "Indexed",   // M3.4e-1a (Z80 IX/IY): (IX+d)/(IY+d)
    };

    /// <summary>Valid Flag enum members for BranchIf/SetFlag/cc args (CPUGEN006 for anything else).
    /// M3.4a (additive): the Z80 names S/H/P/Y/X join so SetFlag(Flag.H, …)/JumpIf(Flag.P, …) parse.
    /// The 6502 names C/Z/I/D/V/N are unchanged — the 6502 spec uses only those.</summary>
    private static readonly HashSet<string> s_flagMembers = new(System.StringComparer.Ordinal)
    {
        "C", "Z", "I", "D", "V", "N",
        "S", "H", "P", "Y", "X",   // M3.4a: Z80 flag names (additive)
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
        "Halt",       // M3.2: HALT/STOP — an Implied/Register-class op that sets the halted latch
        // M3.4a: composable flag micro-ops — register-class (they only modify the Status register).
        "SetSZ", "SetParity", "SetXY", "SetAddSub",
    };

    // M3.2 (additive): the I/O-port op kinds + their legal modes. A Port-class row's first op is
    // PortIn/PortOut and its mode MUST be one of s_portModes (CPUGEN010 otherwise — a port op in,
    // say, Absolute is rejected). The 6502 declares no port op, so this gate is never reached by it.
    private static readonly HashSet<string> s_portOpKinds = new(System.StringComparer.Ordinal)
    {
        "PortIn", "PortOut",
    };

    private static readonly HashSet<string> s_portModes = new(System.StringComparer.Ordinal)
    {
        "IoPortImmediate", "IoPortIndirect",
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

    // ── M3.4a Z80 op-kind class sets (additive; the 6502 names none) ──
    private static readonly HashSet<string> s_z80AluOpKinds = new(System.StringComparer.Ordinal)
    {
        "Add8", "Adc8", "Sub8", "Sbc8", "And8", "Or8", "Xor8", "Cp8",
        "IncReg", "DecReg", "IncMem8", "DecMem8",
        "Add16", "Inc16", "Dec16",
    };

    private static readonly HashSet<string> s_z80LdOpKinds = new(System.StringComparer.Ordinal)
    {
        "Load16", "Store16", "LoadMem16", "StoreImm8",
    };

    private static readonly HashSet<string> s_z80StackOpKinds = new(System.StringComparer.Ordinal)
    {
        "Push16", "Pop16",
    };

    private static readonly HashSet<string> s_z80ExchangeOpKinds = new(System.StringComparer.Ordinal)
    {
        "ExDeHl", "ExAfAf", "Exx", "ExSpHl",
    };

    private static readonly HashSet<string> s_z80FlowOpKinds = new(System.StringComparer.Ordinal)
    {
        "JumpIf", "CallIf", "RetCc", "RelJump", "RelJumpIf", "Djnz", "Rst", "JumpIndirect",
        "JumpAbs", "CallAbs", "Ret",
    };

    private static readonly HashSet<string> s_z80MiscOpKinds = new(System.StringComparer.Ordinal)
    {
        "Daa", "Cpl", "Scf", "Ccf", "Di", "Ei",
    };

    // ── M3.4b Z80 CB-plane + rotate-accumulator op-kind class sets (additive) ──
    private static readonly HashSet<string> s_z80RotOpKinds = new(System.StringComparer.Ordinal)
    {
        "Rlca", "Rrca", "Rla", "Rra",   // base-plane rotate-accumulators (Implied)
        "CbRotate",                      // CB rotate/shift (Bit mode)
    };

    private static readonly HashSet<string> s_z80BitOpKinds = new(System.StringComparer.Ordinal)
    {
        "CbBit",   // CB BIT/RES/SET (Bit mode)
    };

    // ── M3.4c ED-core op-kind class sets (additive) ──
    private static readonly HashSet<string> s_z80EdIoOpKinds = new(System.StringComparer.Ordinal)
    {
        "EdIn", "EdOut",
    };

    private static readonly HashSet<string> s_z80EdOpKinds = new(System.StringComparer.Ordinal)
    {
        "EdAdcSbc16", "EdLdNnRp", "EdNeg", "EdRetn", "EdIm", "EdLdIaRa", "EdRrdRld", "EdNop",
    };

    // ── M3.4d ED block-op kind set (additive) ──
    private static readonly HashSet<string> s_z80EdBlockOpKinds = new(System.StringComparer.Ordinal)
    {
        "EdBlock",
    };

    // ── M3.4e-2 DD/FD indexed op-kind class set (additive) ──
    private static readonly HashSet<string> s_z80IndexedOpKinds = new(System.StringComparer.Ordinal)
    {
        "DdFdLdIndexed", "DdFdStoreImmIndexed", "DdFdAluIndexed", "DdFdIncDecIndexed",
    };

    // ── M3.4e-3 DDCB/FDCB compound op-kind class set (additive) ──
    private static readonly HashSet<string> s_z80DdCbOpKinds = new(System.StringComparer.Ordinal) { "DdCb" };

    // Legal modes per Z80 class (additive). The 8-bit ALU source is a register/(HL)/immediate; the
    // 16-bit ALU is Register only. INC/DEC (HL) is RegisterIndirect.
    private static readonly HashSet<string> s_z80AluModes = new(System.StringComparer.Ordinal)
    {
        "Register", "RegisterIndirect", "Immediate",
    };

    private static readonly HashSet<string> s_z80LdModes = new(System.StringComparer.Ordinal)
    {
        "ImmediateExtended", "ExtendedAddress", "RegisterIndirect",
        "Immediate",   // LD (HL),n — StoreImm8 reads the immediate, writes to (HL)
    };

    private static readonly HashSet<string> s_z80FlowModes = new(System.StringComparer.Ordinal)
    {
        "ExtendedAddress", "RelativeJump", "Implied", "RegisterIndirect",
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

        // Optional flag layout (M3.4a Ground truth B). ABSENT (the 6502) ⇒ the FlagBit enum fallback.
        var flags = ParseFlagLayout(classDecl, diagnostics);

        if (diagnostics.Count > 0)
            return new ParsedSpec(null, diagnostics.ToImmutable());

        var model = new SpecModel(ns, cpuName, architecture,
            LocationInfo.From(classDecl.Identifier.GetLocation()), registers, instructions, decode,
            FetchUnit.Byte, flags);
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
                GetCreationArgumentSyntaxes(expr.Expression) is not { } cargs ||
                cargs.Count < 2 ||
                LiteralString(cargs[0].Expression) is not { } name ||
                LiteralInt(cargs[1].Expression) is not { } bits)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), element.ToString(),
                    "expected new(\"NAME\", bits[, RegisterRole.X][, HighHalf: \"H\", LowHalf: \"L\"]) with literal arguments"));
                continue;
            }

            // Parse the optional 3rd positional RegisterRole + the optional named HighHalf/LowHalf
            // (M3.4a pair view). A 3rd POSITIONAL arg (no NameColon) is the RegisterRole; named
            // args set the pair-view halves.
            string role = "General";
            string? highHalf = null, lowHalf = null;
            bool argError = false;
            for (int i = 2; i < cargs.Count; i++)
            {
                var a = cargs[i];
                string? argName = a.NameColon?.Name.Identifier.Text;
                if (argName is null)
                {
                    if (EnumMemberName(a.Expression, "RegisterRole") is { } parsedRole)
                        role = parsedRole;
                    else { argError = true; break; }
                }
                else if (argName == "HighHalf")
                    highHalf = LiteralString(a.Expression) ?? Sentinel(ref argError);
                else if (argName == "LowHalf")
                    lowHalf = LiteralString(a.Expression) ?? Sentinel(ref argError);
                else { argError = true; break; }
            }
            if (argError)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name,
                    "extra arguments must be a RegisterRole member and/or HighHalf:/LowHalf: string literals"));
                continue;
            }

            if (!SyntaxFacts.IsValidIdentifier(name))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "register name must be a valid C# identifier"));
                continue;
            }

            // M4.1 (ADR 0003 Decision 1): widen the register-width cap to admit 32-bit registers (the
            // 68000's D0–D7/A0–A6/USP/SSP/PC). 8/16 are unchanged, so the 6502/Z80 emit byte-identically
            // (the field-type selection's 8/16 arms are untouched — CpuEmitter FieldType()).
            if (bits is not (8 or 16 or 32))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "register width must be 8, 16, or 32 bits"));
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

            registers.Add(new RegisterModel(name, bits, role, highHalf, lowHalf));
        }

        // M3.4a (Ground truth A.3): validate every pair-view RegisterDef. A view (HighHalf/LowHalf
        // set) must be 16-bit and name two DECLARED 8-bit registers (CPUGEN014). Validated after the
        // full table is read so a half declared later in the table still resolves.
        var byName = new Dictionary<string, RegisterModel>(System.StringComparer.Ordinal);
        foreach (var r in registers)
            byName[r.Name] = r;
        foreach (var r in registers)
        {
            if (r.HighHalf is null && r.LowHalf is null)
                continue;
            if (r.HighHalf is null || r.LowHalf is null)
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidPairView,
                    classDecl.Identifier.GetLocation(), r.Name,
                    "a pair view must declare BOTH HighHalf and LowHalf"));
            else if (r.Bits != 16)
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidPairView,
                    classDecl.Identifier.GetLocation(), r.Name,
                    "a pair view must be 16-bit"));
            else
            {
                foreach (string half in new[] { r.HighHalf, r.LowHalf })
                    if (!byName.TryGetValue(half, out var hr) || hr.Bits != 8)
                        diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidPairView,
                            classDecl.Identifier.GetLocation(), r.Name,
                            $"half '{half}' must name a declared 8-bit register"));
            }
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
            int? opcode, prefix = null, prefix2 = null, subField = null;
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
            else if (args.Count == 6)
            {
                keyShape = KeyShape.Compound;
                prefix = LiteralInt(args[0].Expression);
                prefix2 = LiteralInt(args[1].Expression);
                opcode = LiteralInt(args[2].Expression);   // the FINAL opcode
                mnemonicIdx = 3; modeIdx = 4; opsIdx = 5;
                if (prefix is null or < 0 or > 0xFF || prefix2 is null or < 0 or > 0xFF)
                {
                    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidInstruction,
                        element.GetLocation(), Truncate(element.ToString()),
                        "compound prefix bytes must be literals in 0x00-0xFF"));
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
                KeyShape.Compound => (uint)((prefix!.Value << 16) | (prefix2!.Value << 8) | opcode.Value),
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
                operationKey, keyShape, prefix ?? -1, prefix2 ?? -1, subField ?? -1));
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
        var prefixDetails = ImmutableArray.CreateBuilder<PrefixByteModel>();   // M3.4e-1b: per-prefix compound metadata
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

        // Prefixes: a collection of PrefixByte(0xNN [, CompoundWith: 0xNN] [, DisplacementBeforeOpcode: true])
        // creations. M3.4e-1b: arg 0 is always the Value byte; the optional CompoundWith / Displacement-
        // BeforeOpcode args may be named or positional (arg1 ⇒ CompoundWith, arg2 ⇒ DisplacementBeforeOpcode).
        if (args[0] is CollectionExpressionSyntax prefixColl)
        {
            foreach (var e in prefixColl.Elements)
            {
                if (e is ExpressionElementSyntax pe &&
                    GetCreationArgumentSyntaxes(pe.Expression) is { } pargs && pargs.Count >= 1 &&
                    LiteralInt(pargs[0].Expression) is { } pv and >= 0 and <= 0xFF)
                {
                    int compoundWith = -1;
                    bool dispBefore = false;
                    bool argsOk = true;
                    for (int ai = 1; ai < pargs.Count; ai++)
                    {
                        string? name = pargs[ai].NameColon?.Name.Identifier.Text;
                        ExpressionSyntax expr = pargs[ai].Expression;
                        // Named ⇒ route by name; positional ⇒ route by position (arg1 = CompoundWith,
                        // arg2 = DisplacementBeforeOpcode), mirroring the PrefixByte record order.
                        bool isCompoundWith = name == "CompoundWith" || (name is null && ai == 1);
                        bool isDispBefore = name == "DisplacementBeforeOpcode" || (name is null && ai == 2);
                        if (isCompoundWith)
                        {
                            if (LiteralInt(expr) is { } cw and >= 0 and <= 0xFF) compoundWith = cw;
                            else argsOk = false;
                        }
                        else if (isDispBefore)
                        {
                            if (expr.IsKind(SyntaxKind.TrueLiteralExpression)) dispBefore = true;
                            else if (expr.IsKind(SyntaxKind.FalseLiteralExpression)) dispBefore = false;
                            else argsOk = false;
                        }
                        else { argsOk = false; }
                    }

                    if (argsOk)
                    {
                        prefixes.Add((byte)pv);
                        prefixDetails.Add(new PrefixByteModel((byte)pv, compoundWith, dispBefore));
                    }
                    else { ok = false; }
                }
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
            if (!instructions.Any(i =>
                    (i.KeyShape == KeyShape.PrefixedOpcode && i.Prefix == p) ||
                    (i.KeyShape == KeyShape.Compound && i.Prefix == p)))
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

        return new DecodeStructureModel(prefixes.ToImmutable(), modRm.ToImmutable(), subField.ToImmutable(), prefixDetails.ToImmutable());
    }

    /// <summary>Parse the optional <c>Flags</c> field (M3.4a Ground truth B). ABSENT ⇒ empty (the
    /// 6502 FlagBit enum-fallback). Present ⇒ a <c>new([ new("S", 7), new("Z", 6), … ])</c> /
    /// <c>new FlagLayout([...])</c> creation whose collection of <c>FlagBitDef("NAME", bit)</c>
    /// entries is parsed into the model. A malformed structure or a bit outside 0–7 reports
    /// CPUGEN013. Each name must be a known Flag member (CPUGEN013 otherwise).</summary>
    private static ImmutableArray<FlagBitModel> ParseFlagLayout(
        ClassDeclarationSyntax classDecl,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var field = FindArrayField(classDecl, "Flags");
        if (field is null)
            return ImmutableArray<FlagBitModel>.Empty;   // ABSENT — the 6502 default.

        Location loc = field.GetLocation();
        if (field.Declaration.Variables[0].Initializer?.Value is not { } init ||
            GetCreationArguments(init) is not { Count: 1 } args ||
            args[0] is not CollectionExpressionSyntax bitsColl)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidFlagLayout, loc,
                "expected new([ new(\"NAME\", bit), ... ]) with literal arguments"));
            return ImmutableArray<FlagBitModel>.Empty;
        }

        var bits = ImmutableArray.CreateBuilder<FlagBitModel>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var element in bitsColl.Elements)
        {
            if (element is not ExpressionElementSyntax expr ||
                GetCreationArguments(expr.Expression) is not { Count: 2 } bargs ||
                LiteralString(bargs[0]) is not { } name ||
                LiteralInt(bargs[1]) is not { } bit)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidFlagLayout,
                    element.GetLocation(), "expected new(\"NAME\", bit) with literal arguments"));
                return ImmutableArray<FlagBitModel>.Empty;
            }
            if (!s_flagMembers.Contains(name))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidFlagLayout,
                    element.GetLocation(), $"'{name}' is not a known Flag member"));
                return ImmutableArray<FlagBitModel>.Empty;
            }
            if (bit is < 0 or > 7)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidFlagLayout,
                    element.GetLocation(), $"bit {bit} for flag '{name}' is outside 0–7"));
                return ImmutableArray<FlagBitModel>.Empty;
            }
            if (!seen.Add(name))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidFlagLayout,
                    element.GetLocation(), $"duplicate flag '{name}' in layout"));
                return ImmutableArray<FlagBitModel>.Empty;
            }
            bits.Add(new FlagBitModel(name, bit));
        }

        return bits.ToImmutable();
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
        // M3.4a: the Z80 stack/flow/exchange classes also push/pop or exchange via SP.
        bool touchesStack = instructionClass is InstructionClass.Stack or InstructionClass.Flow
            or InstructionClass.Z80Stack or InstructionClass.Z80Flow or InstructionClass.Z80Exchange;
        if (touchesStack && !hasStackPointer)
        {
            diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnsupportedModeOpCombination,
                location, mnemonic,
                $"{instructionClass.ToString().ToLowerInvariant()} class requires a StackPointer-role register"));
            return false;
        }

        // Flag-writing classes need a Status register (alu/rmw write C/V/N/Z; Pull bakes NZ,
        // PushP/PullP move P itself). BRK/RTI also touch P: BRK stacks P|0x30 and sets I,
        // RTI restores P from the stack — the emitter writes the Status NAME into both
        // templates, so a Status-role register is mandatory (CS0103 otherwise).
        // M3.4a: the Z80 ALU + misc (DAA/SCF/CCF/CPL) classes write the Status register too.
        bool flowTouchesStatus = instructionClass == InstructionClass.Flow
            && firstOpKind is "Brk" or "Rti";
        if ((instructionClass is InstructionClass.Alu or InstructionClass.Rmw or InstructionClass.Stack
                or InstructionClass.Z80Alu or InstructionClass.Z80Misc
                or InstructionClass.Z80Rot or InstructionClass.Z80Bit
                or InstructionClass.Z80EdIo or InstructionClass.Z80EdOp
                or InstructionClass.Z80EdBlock
                or InstructionClass.Z80Indexed
                or InstructionClass.Z80DdCb
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

        // Port class (M3.2): exactly one port op (PortIn/PortOut) — an Io-bus access.
        if (s_portOpKinds.Contains(first))
        {
            if (ops.Length != 1)
            {
                error = "port class must contain exactly one op (PortIn/PortOut)";
                return null;
            }
            return InstructionClass.Port;
        }

        // ── M3.4a Z80 classes (additive — the 6502 names none of these op kinds) ──
        // Each Z80 class is a single bespoke op (the flag/EA logic is in the emitter body). No
        // trailing register ops (the flag-setting is inline in the ALU/INC arm, not composed here).
        if (s_z80AluOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 ALU class must contain exactly one op"; return null; }
            return InstructionClass.Z80Alu;
        }
        if (s_z80LdOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 16-bit LD class must contain exactly one op"; return null; }
            return InstructionClass.Z80Ld;
        }
        if (s_z80StackOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 stack class must contain exactly one op (Push16/Pop16)"; return null; }
            return InstructionClass.Z80Stack;
        }
        if (s_z80ExchangeOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 exchange class must contain exactly one op"; return null; }
            return InstructionClass.Z80Exchange;
        }
        if (s_z80FlowOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 flow class must contain exactly one op"; return null; }
            return InstructionClass.Z80Flow;
        }
        if (s_z80MiscOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 misc class must contain exactly one op"; return null; }
            return InstructionClass.Z80Misc;
        }
        if (s_z80RotOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 rotate class must contain exactly one op"; return null; }
            return InstructionClass.Z80Rot;
        }
        if (s_z80BitOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 bit class (CbBit) must contain exactly one op"; return null; }
            return InstructionClass.Z80Bit;
        }
        if (s_z80EdIoOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 ED I/O class must contain exactly one op"; return null; }
            return InstructionClass.Z80EdIo;
        }
        if (s_z80EdOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 ED-op class must contain exactly one op"; return null; }
            return InstructionClass.Z80EdOp;
        }
        if (s_z80EdBlockOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 ED block class must contain exactly one op"; return null; }
            return InstructionClass.Z80EdBlock;
        }
        if (s_z80IndexedOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 indexed class must contain exactly one op"; return null; }
            return InstructionClass.Z80Indexed;
        }
        if (s_z80DdCbOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 DDCB class must contain exactly one op"; return null; }
            return InstructionClass.Z80DdCb;
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
            // register class: Implied (6502) OR the Z80 register-shape modes (LD r,r' is Register;
            // LD SP,HL is Register). Additive — the 6502 register-class rows stay Implied.
            InstructionClass.Register =>
                mode is "Implied" or "Register" ? null
                : "register-class ops require Implied (6502) or Register (Z80 LD r,r') mode",

            // load class: Immediate + all 8 6502 memory modes (9 total), PLUS the Z80 register-shape
            // load modes (RegisterIndirect for LD r,(HL); ExtendedAddress for LD A,(nn); Register for
            // LD r,r' authored as Load — though Z80 LD r,r' uses Transfer/Register class). Additive.
            InstructionClass.Load =>
                s_loadAluModes.Contains(mode) || mode is "RegisterIndirect" or "ExtendedAddress" or "Register" ? null
                : "Load requires a memory/immediate or Z80 register-shape addressing mode",

            // alu class: same 9 modes as load (6502 — UNCHANGED).
            InstructionClass.Alu =>
                s_loadAluModes.Contains(mode) ? null
                : "Alu requires a memory or immediate addressing mode",

            // store class: 8 6502 memory modes (no Immediate), PLUS the Z80 RegisterIndirect (LD (HL),r;
            // LD (BC),A) and ExtendedAddress (LD (nn),A). Additive — the 6502 store rows unchanged.
            InstructionClass.Store =>
                s_storeModes.Contains(mode) || mode is "RegisterIndirect" or "ExtendedAddress" ? null
                : "Store requires a 6502 memory mode or a Z80 register-shape store mode",

            // rmw class: ZeroPage/ZeroPageX/Absolute/AbsoluteX/Accumulator
            InstructionClass.Rmw =>
                s_rmwModes.Contains(mode) ? null
                : "Rmw requires ZeroPage/ZeroPageX/Absolute/AbsoluteX/Accumulator mode",

            // jump class: Absolute/Indirect (6502) OR the Z80 ExtendedAddress (JP nn) /
            // RegisterIndirect (JP (HL)). Additive — the 6502 jump rows stay Absolute/Indirect.
            InstructionClass.Jump =>
                mode is "Absolute" or "Indirect" or "ExtendedAddress" or "RegisterIndirect" ? null
                : "Jump requires Absolute/Indirect (6502) or ExtendedAddress/RegisterIndirect (Z80) mode",

            // branch class: Relative only
            InstructionClass.Branch =>
                mode == "Relative" ? null
                : "BranchIf requires Relative mode",

            // stack class: Implied only
            InstructionClass.Stack =>
                mode == "Implied" ? null
                : "stack class (Push/Pull/PushP/PullP) requires Implied mode",

            // flow class: per-OP matrix — Jsr requires Absolute (6502) or ExtendedAddress (Z80 CALL nn);
            // Rts requires Implied (6502 RTS / Z80 RET); Brk/Rti require Implied.
            // ClassifyOps guarantees flow has exactly one op of kind Jsr/Rts/Brk/Rti.
            InstructionClass.Flow when firstOpKind == "Jsr" =>
                mode is "Absolute" or "ExtendedAddress" ? null : "Jsr requires Absolute (6502) or ExtendedAddress (Z80 CALL) mode",
            InstructionClass.Flow when firstOpKind == "Brk" =>
                mode == "Implied" ? null : "Brk requires Implied mode",
            InstructionClass.Flow when firstOpKind == "Rti" =>
                mode == "Implied" ? null : "Rti requires Implied mode",
            InstructionClass.Flow =>
                mode == "Implied" ? null : "Rts requires Implied mode",

            // port class (M3.2): IoPortImmediate ((n)) or IoPortIndirect ((C)) — the Io-bus
            // port-operand modes. A port op in any other mode is rejected here.
            InstructionClass.Port =>
                s_portModes.Contains(mode) ? null
                : "port class (PortIn/PortOut) requires IoPortImmediate or IoPortIndirect mode",

            // ── M3.4a Z80 classes ──
            InstructionClass.Z80Alu =>
                s_z80AluModes.Contains(mode) ? null
                : "Z80 ALU class requires Register/RegisterIndirect/Immediate mode",
            InstructionClass.Z80Ld =>
                s_z80LdModes.Contains(mode) ? null
                : "Z80 16-bit LD class requires ImmediateExtended/ExtendedAddress/RegisterIndirect mode",
            InstructionClass.Z80Stack =>
                mode == "Register" ? null : "Z80 stack class (Push16/Pop16) requires Register mode",
            InstructionClass.Z80Exchange =>
                mode is "Implied" or "Register" or "RegisterIndirect" ? null
                : "Z80 exchange class requires Implied/Register/RegisterIndirect mode",
            InstructionClass.Z80Flow =>
                s_z80FlowModes.Contains(mode) ? null
                : "Z80 flow class requires ExtendedAddress/RelativeJump/Implied/RegisterIndirect mode",
            InstructionClass.Z80Misc =>
                mode == "Implied" ? null : "Z80 misc class (DAA/CPL/SCF/CCF/DI/EI) requires Implied mode",

            // ── M3.4b Z80 CB-plane + rotate-accumulator classes ──
            // Z80Rot: the rotate-accumulators are Implied (RLCA etc.); CB rotate/shift is Bit.
            InstructionClass.Z80Rot =>
                mode is "Implied" or "Bit" ? null
                : "Z80 rotate class requires Implied (RLCA/…) or Bit (CB rotate/shift) mode",
            // Z80Bit: CB BIT/RES/SET — Bit mode only.
            InstructionClass.Z80Bit =>
                mode == "Bit" ? null
                : "Z80 bit class (CbBit) requires Bit mode",

            // ── M3.4c ED-core classes ──
            InstructionClass.Z80EdIo =>
                mode == "Register" ? null : "Z80 ED I/O class (IN/OUT (C)) requires Register mode",
            InstructionClass.Z80EdOp =>
                mode is "Register" or "ExtendedAddress" or "RegisterIndirect" or "Implied" ? null
                : "Z80 ED-op class requires Register/ExtendedAddress/RegisterIndirect/Implied mode",

            // M3.4d: the block ops are all Implied (operands are the implicit BC/DE/HL/A/C registers).
            InstructionClass.Z80EdBlock =>
                mode == "Implied" ? null : "Z80 ED block class requires Implied mode",

            // M3.4e-2: the indexed (IX+d)/(IY+d) memory ops are all Indexed mode.
            InstructionClass.Z80Indexed =>
                mode == "Indexed" ? null : "Z80 indexed class ((IX+d)/(IY+d)) requires Indexed mode",

            // M3.4e-3: the compound DDCB/FDCB ops are all Indexed mode (KeyShape.Compound at the row level).
            InstructionClass.Z80DdCb =>
                mode == "Indexed" ? null : "Z80 DDCB class (compound (IX+d)/(IY+d)) requires Indexed mode",

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
                    ArgKind.Str => LiteralRaw(argument.Expression),               // M3.4b: CB op name / "(HL)" target (quoted)
                    ArgKind.Int => CbBitIndex(argument.Expression),               // M3.4b: CB bit index 0..7 → its digit text
                    _ => BoolLiteral(argument.Expression),
                };
                if (value is null)
                {
                    string description = expected switch
                    {
                        ArgKind.Reg => "register-name string literal",
                        ArgKind.Flag => "Flag member",
                        ArgKind.Str => "string literal",
                        ArgKind.Int => "integer 0..7",
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

    // M3.4b: the raw quoted-string literal (INCLUDING quotes) — the CB op name "RLC" or the "(HL)"
    // target. Distinct from LiteralString (which strips quotes and is register-table-checked).
    private static string? LiteralRaw(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: string } lit ? lit.Token.Text : null;

    // M3.4b: the CB bit index 0..7 (reusing the existing LiteralInt helper). Returns the digit text the
    // model stores, or null if not an int 0..7 (drives the InvalidMicroOpArgument diagnostic).
    private static string? CbBitIndex(ExpressionSyntax expression) =>
        LiteralInt(expression) is { } n && n is >= 0 and <= 7
            ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

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

    /// <summary>Argument SYNTAXES (carrying NameColon) of a creation — needed to distinguish a
    /// positional RegisterRole arg from the named HighHalf:/LowHalf: pair-view args (M3.4a).</summary>
    private static IReadOnlyList<ArgumentSyntax>? GetCreationArgumentSyntaxes(ExpressionSyntax expression) =>
        expression switch
        {
            ImplicitObjectCreationExpressionSyntax c => c.ArgumentList.Arguments,
            ObjectCreationExpressionSyntax { ArgumentList: { } al } => al.Arguments,
            InvocationExpressionSyntax i => i.ArgumentList.Arguments,
            _ => null,
        };

    /// <summary>Set the error flag and return null (for an inline "non-literal half" reject).</summary>
    private static string? Sentinel(ref bool error) { error = true; return null; }

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
