using System.Reflection;
using System.Reflection.Emit;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>The CPU-agnostic block compiler, generic over the interpreter CPU type (J1): walks the
/// generated <see cref="OpcodeDescriptor"/> table from an entry PC into a straight-line run, then
/// emits one <see cref="DynamicMethod"/> per block (the descriptor-interpreter-that-emits-IL — the
/// Pydgin "walk the IR → emit" arm). The interpreter is the oracle: every emitted instruction
/// mirrors the proven CpuEmitter body one-for-one, and any NeedsFallback opcode emits a callout to
/// the inner interpreter's Step. The CPU-specific reflection (status/PC/accumulator fields +
/// Step/AdvanceCycles/CycleCount/InterruptPending + decode) is resolved through the injected
/// <see cref="IJitTarget"/> (the per-CPU seam), so the compiler NEVER names a concrete CPU type
/// while still emitting DIRECT field access against it (the baked-<see cref="FieldInfo"/> speed
/// premise — NOT an ICpuCore-virtual rewrite).</summary>
internal sealed partial class BlockCompiler<TCpu> where TCpu : class
{
    private readonly TCpu _cpu;
    private readonly IJitTarget _target;        // the per-CPU seam (J1)
    private readonly AddressSpace _bus;
    private readonly Fastmem _fastmem;
    private readonly JitOptions _opts;
    internal int CompileCount { get; private set; }   // test seam (Block_cache_hits pin)

    /// <summary>Test seam (Task 6): how many interpreter-Step fallbacks the LAST Compile emitted.
    /// Reset at the start of each Compile; incremented by <see cref="EmitFallbackStep"/>. The
    /// emit-not-fallback probe reads this: an ADC/SBC block emits 0 (they emit now); a BRK block
    /// emits 1 (BRK/RTI/undefined stay fallbacks).</summary>
    internal int FallbackEmitCount { get; private set; }

    // BlockDelegate arg indices (M2-ii — after inserting ChainDispatch as the 5th parameter;
    // M3.2 appended ioBus as the 8th so no existing index shifted):
    //   0 = cpu, 1 = bus, 2 = fastmem, 3 = dirty, 4 = chain (ChainDispatch),
    //   5 = ref long budget, 6 = out BlockExit exit, 7 = ioBus (IAddressSpace — the Port callout).
    // Named so the next signature change is one edit, not a scattered Ldarg_S hunt.
    private const byte ArgChain = 4;
    private const byte ArgBudget = 5;
    private const byte ArgExit = 6;
    private const byte ArgIoBus = 7;   // M3.2: the second IAddressSpace the Port arm calls (never fastmem)

    // J2 (M3.1a + M3.5-3a): the register file is DATA. The OPERAND registers (the ones a micro-op
    // descriptor's RegA/RegB name) resolve through a per-compile name→FieldInfo map built from the
    // CPU's declared RegisterNames — no baked A=0/X=1/… index switch. The CPU TYPE is now the generic
    // TCpu (J1 done, M3.5-3a); the map is built BY NAME against TCpu's concrete type (resolved from the
    // injected IJitTarget.CpuType), which is exactly the J2 shape.
    private readonly System.Collections.Generic.Dictionary<string, FieldInfo> _regFields;

    // PR-0 (M6): the WIDE (16-bit) register file. Two shapes a structured CPU presents:
    //   (a) a real ushort FIELD (the Z80's SP/PC/WZ; the 8086's IP) — direct Ldfld/Stfld.
    //   (b) a field-less pair-view PROPERTY (the Z80's AF/BC/DE/HL/IX/IY + shadows; the 8086's
    //       AX/BX/CX/DX) over two byte HALF-fields — the emit arm composes hi<<8|lo / decomposes.
    // _regFields (the 8-bit map) SKIPS the (b) pairs by design (:96-107); these two members are how
    // an emit arm reaches them. Built per-compile from the same target.RegisterNames + target.CpuType
    // introspection — no generator change, no new ctor arg (Decision 2: the register file stays data).
    private readonly System.Collections.Generic.Dictionary<string, FieldInfo> _regWideFields;
    private readonly System.Collections.Generic.Dictionary<string, (FieldInfo Hi, FieldInfo Lo)> _regPairFields;

    // P (Status), PC (ProgramCounter), and A (the accumulator) are NOT operand-driven — the
    // flow/flag/PC arms and the ALU/RMW/decimal A-convention arms reference them directly, by the
    // 6502 convention baked into those templates (NOT from a descriptor's RegA/RegB). They are now
    // per-CPU INSTANCE handles (J2: was static, baked to typeof(Mos6502Cpu)) — resolved from the
    // injected IJitTarget's StatusField/ProgramCounterField/AccumulatorField (by NAME on the CPU
    // type). The OPERAND registers — the ones a descriptor's RegA/RegB actually name (Load/Store/
    // Transfer/Compare/Increment/Decrement/SetNZ/Push/Pull, plus the indexed-mode X/Y and stack S)
    // — go through _regFields (the J2 win).
    private readonly FieldInfo _fa;
    private readonly FieldInfo _fp;
    private readonly FieldInfo _fpc;

    // M6 PR-1: the Z80's Q (the byte last-flag-write tracker) and WZ (the ushort MEMPTR) fields,
    // resolved once per compile from the CPU type by name. Null for non-Z80 CPUs (the LD emit arm only
    // runs for the Z80 — TargetIsZ80 — so they are non-null wherever they are dereferenced). Every
    // base-plane LD clears Q; the (nn) and (BC)/(DE) indirect forms set WZ (the MEMPTR side-effects).
    private readonly FieldInfo? _z80Q;
    private readonly FieldInfo? _z80WZ;
    // M6 PR-2: the Z80 flag register (byte F). Set in the ctor; non-null wherever dereferenced (the ALU
    // arm only runs when TargetIsZ80). The ALU family computes the full SZ5H3PNC word inline into it.
    private readonly FieldInfo? _z80F;
    // M6 PR-1: the Z80's R (memory-refresh) byte field. The interpreter's Step bumps R once per opcode
    // fetch via OnInstructionFetched (R = (R & 0x80) | ((R + 1) & 0x7F) — bit 7 preserved, bits 0..6
    // incremented mod 128). An EMITTED instruction skips Step, so the emit path must replicate the bump;
    // a fallback op keeps its inner.Step bump. Null for non-Z80 CPUs.
    private readonly FieldInfo? _z80R;

    // J1: the CPU-typed method handles are now per-CPU INSTANCE fields (was: static baked to
    // typeof(Mos6502Cpu)). Resolved from the injected target's reflection handles.
    private readonly MethodInfo _mAdvance;
    private readonly MethodInfo _mStep;
    private readonly MethodInfo _mCycleCount;
    private readonly MethodInfo _mInterruptPending;

    // CPU-AGNOSTIC handles SURVIVE as static (the positive J4/J6/J8 finding — these never named the
    // CPU). Resolved from IAddressSpace so the bus arm works against either the concrete AddressSpace
    // (fastmem mode) or a TracingAddressSpace (trace mode) — see the BlockDelegate deviation note.
    private static readonly MethodInfo MRead = typeof(IAddressSpace).GetMethod("Read8")!;
    private static readonly MethodInfo MWrite = typeof(IAddressSpace).GetMethod("Write8")!;

    private static readonly MethodInfo MPageBacking = typeof(Fastmem).GetProperty("PageBacking")!.GetGetMethod()!;
    private static readonly MethodInfo MPageOffset = typeof(Fastmem).GetProperty("PageOffset")!.GetGetMethod()!;
    private static readonly MethodInfo MPageWritable = typeof(Fastmem).GetProperty("PageWritable")!.GetGetMethod()!;
    private static readonly MethodInfo MDirtyMark = typeof(DirtyMap).GetMethod("Mark")!;

    // Chaining (M2-ii): the Dirty.Any backstop getter + the ChainDispatch.Invoke the emitted chain
    // edge calls (Ground truth A/B). Resolved once, reused across every chainable exit.
    private static readonly MethodInfo MDirtyAny = typeof(DirtyMap).GetProperty("Any")!.GetGetMethod()!;
    private static readonly MethodInfo MChainInvoke = typeof(ChainDispatch).GetMethod("Invoke")!;

    // The hi/lo half-field names for each composed pair-view. A small, fixed ISA fact (NOT a generator
    // output) — adding a CPU's pairs is a one-line entry here, never a CpuEmitter.cs change. The halves
    // must be the byte FIELD names the generated CPU declares (verified: Z80 g.cs:30-67; 8086 g.cs AX..DX).
    private static readonly System.Collections.Generic.Dictionary<string, (string Hi, string Lo)> PairHalves =
        new(System.StringComparer.Ordinal)
        {
            // Z80 main set
            ["AF"] = ("A", "F"), ["BC"] = ("B", "C"), ["DE"] = ("D", "E"), ["HL"] = ("H", "L"),
            ["IX"] = ("IXh", "IXl"), ["IY"] = ("IYh", "IYl"),
            // Z80 shadow set
            ["AF_"] = ("A_", "F_"), ["BC_"] = ("B_", "C_"), ["DE_"] = ("D_", "E_"), ["HL_"] = ("H_", "L_"),
            // 8086 16-bit GP pair-views over byte halves (reused by PR-B; harmless to register now)
            ["AX"] = ("AH", "AL"), ["BX"] = ("BH", "BL"), ["CX"] = ("CH", "CL"), ["DX"] = ("DH", "DL"),
        };

    public BlockCompiler(TCpu cpu, IJitTarget target, AddressSpace bus, Fastmem fastmem, JitOptions opts)
    {
        (_cpu, _target, _bus, _fastmem, _opts) = (cpu, target, bus, fastmem, opts);
        _fa = target.AccumulatorField;
        _fp = target.StatusField;
        _fpc = target.ProgramCounterField;
        _mAdvance = target.AdvanceCyclesMethod;
        _mStep = target.StepMethod;
        _mCycleCount = target.CycleCountGetter;
        _mInterruptPending = target.InterruptPendingGetter;

        // Build the register-name → FieldInfo map from the CPU's declared register names (the
        // introspection the generator already emits — IJitTarget.RegisterNames). J2: the names come
        // from data, not a baked enum. RECORDED J2 FINDING: the 6502's register file is all FIELDS;
        // the Z80's is fields + composed pair-view PROPERTIES (AF/BC/DE/HL/IX/IY + the alt set,
        // which on the generated CPU are properties over the 8-bit half fields, NOT fields). The map
        // covers the directly-emittable (field-backed) registers and SKIPS the field-less pair-views
        // — no emitted op references a pair in 5-3a (everything falls back), and an emitted op that
        // needs a pair (5-3b) resolves it via a dedicated 16-bit register helper then.
        _regFields = new System.Collections.Generic.Dictionary<string, FieldInfo>(System.StringComparer.Ordinal);
        foreach (string name in target.RegisterNames)
            if (target.CpuType.GetField(name) is { } f)   // 8-bit halves + I/R + WZ/SP/PC fields
                _regFields[name] = f;                       // pair-view PROPERTIES are skipped (5-3b owns them)

        // PR-0: build the wide-register maps from the same introspection. A name that GetField resolves
        // to a ushort field is a direct wide field; a name in PairHalves whose two halves are byte fields
        // is a composed pair-view. (Names absent from both stay 8-bit-only, exactly as before.)
        _regWideFields = new System.Collections.Generic.Dictionary<string, FieldInfo>(System.StringComparer.Ordinal);
        _regPairFields = new System.Collections.Generic.Dictionary<string, (FieldInfo, FieldInfo)>(System.StringComparer.Ordinal);
        foreach (string name in target.RegisterNames)
        {
            if (target.CpuType.GetField(name) is { } wf && wf.FieldType == typeof(ushort))
            {
                _regWideFields[name] = wf;                       // SP/PC/WZ (Z80), IP (8086) — real ushort
            }
            else if (PairHalves.TryGetValue(name, out var halves)
                     && target.CpuType.GetField(halves.Hi) is { } hf
                     && target.CpuType.GetField(halves.Lo) is { } lf)
            {
                _regPairFields[name] = (hf, lf);                 // AF/BC/DE/HL/IX/IY + shadows; AX/BX/CX/DX
            }
        }

        // M6 PR-1: the Z80 LD emit arm's Q/WZ side-effect handles. Resolved by name on the CPU type;
        // null for non-Z80 CPUs (GetField returns null when the field is absent), which is harmless —
        // the arm that dereferences them only runs when TargetIsZ80. Q is a byte field (Z80Cpu.cs), WZ a
        // ushort field (the generated Z80Cpu.g.cs); both are public, so GetField with the default binding
        // flags finds them.
        _z80Q = target.CpuType.GetField("Q");
        _z80WZ = target.CpuType.GetField("WZ");
        _z80R = target.CpuType.GetField("R");
        _z80F = target.CpuType.GetField("F");   // M6 PR-2: the Z80 flag register (byte F)
    }

    /// <summary>M6 PR-1: is the compiled CPU the structured Z80? Routes the LD rows to the Z80 emit arm
    /// (EmitZ80Ld). The 6502 never produces the Z80-shape modes/op-kinds, so this is the unambiguous
    /// per-CPU discriminator; when PR-B adds the 8086 it generalizes to a switch on _target.CpuType.</summary>
    private bool TargetIsZ80 => _target.CpuType.Name == "Z80Cpu";

    /// <summary>M6 PR-1/PR-2: the operand bytes an EMITTED Z80 row's body consumes from PC (beyond the
    /// opcode byte the decode walk already counted) — the footprint correction Discover adds so block
    /// discovery + the static nextPc match the PC the emit arm actually leaves. Mode-driven, exactly
    /// mirroring the interpreter op bodies.
    ///
    /// PR-1 (LD family): Immediate (LD r,n / LD (HL),n) reads 1; ImmediateExtended (LD rr,nn) and
    /// ExtendedAddress (LD A,(nn) / LD (nn),A / LD (nn),HL / LD HL,(nn)) read 2; Register (LD r,r') and
    /// RegisterIndirect (LD r,(HL) etc.) read 0.
    ///
    /// PR-2 (ALU family — Add8..Cp8 / IncReg/DecReg/IncMem8/DecMem8 / Add16): only the Immediate forms
    /// (ADD A,n / ADC A,n / SUB A,n / SBC A,n / AND n / XOR n / OR n / CP n — 0xC6/0xCE/0xD6/0xDE/0xE6/
    /// 0xEE/0xF6/0xFE) read 1 PC operand byte. The Register / RegisterIndirect (HL) forms and ADD HL,rr
    /// read 0 — they take their operand from a register or (HL), never from PC. Without this the decode
    /// walk under-counts an emitted ALU-immediate's footprint by 1 (FixedLength is the opcode-key length,
    /// 1 for base-plane rows), so Discover mis-decodes the immediate operand byte as the next opcode and
    /// the emitted block's nextPc lands one byte short of the arm's actual PC advance.
    ///
    /// Returns 0 for any non-Z80, fallback, or non-emitted-family row — those keep the walk's length
    /// unchanged (a fallback Z80 op ends the block and self-terminates via inner.Step, so its nextPc is
    /// never read).</summary>
    private int Z80EmitOperandBytes(OpcodeDescriptor d)
    {
        if (!TargetIsZ80 || d.NeedsFallback) return 0;

        // PR-1: the LD family's PC-operand footprint.
        if (d.Mnemonic == "LD")
            return d.Mode switch
            {
                JitMode.Immediate => 1,            // LD r,n / LD (HL),n
                JitMode.ImmediateExtended => 2,    // LD rr,nn
                JitMode.ExtendedAddress => 2,      // LD A,(nn) / LD (nn),A / LD (nn),HL / LD HL,(nn)
                _ => 0,                            // Register / RegisterIndirect — no PC operand bytes
            };

        // PR-2: the ALU family's PC-operand footprint — ONLY the Immediate forms read a PC byte (the
        // Register / RegisterIndirect(HL) forms and ADD HL,rr take their operand from a reg or (HL)).
        if (IsZ80AluKind(d))
            return d.Mode == JitMode.Immediate ? 1 : 0;

        return 0;
    }

    /// <summary>Decode from pc until an EndsBlock opcode or the block-length cap, running the
    /// generated decode walk (Ground truth B) — NOT a static descriptor Length field. The walk
    /// reads opcode/operand bytes through a BusFetchStream (a debugger-view decode; never executes,
    /// charges no cycle) and returns (key, COMPUTED length); Discover advances the cursor by that
    /// returned length and SeekTo's the next instruction. The discovered run is a list of
    /// (pc, descriptor, computed-length) tuples — the length is the walk's output, the only length
    /// source the rest of Compile/PagesSpanned reads.</summary>
    public System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D, int Length)> Discover(ushort pc)
    {
        var run = new System.Collections.Generic.List<(ushort, OpcodeDescriptor, int)>();
        var stream = new BusFetchStream(_bus, pc);          // byte-granular, positioned at pc
        for (int i = 0; i < _opts.BlockLengthCap; i++)
        {
            DecodeResult r = _target.Decode(stream);        // J3: the per-CPU decode seam (was static)
            OpcodeDescriptor d = _target.DescriptorFor(r.OperationKey);     // 6502: key == opcode → [256]
            // M6 PR-1: the Z80 decode walk's r.Length is the OPCODE-KEY length (1 for base-plane rows) —
            // the interpreter's op body reads the operand bytes itself and advances PC past them, so the
            // walk under-counts an emitted LD's true footprint (LD B,n = 2, LD BC,nn = 3, LD A,(nn) = 3).
            // For an EMITTED Z80 LD the JIT advances PC in the arm (mirroring the body), so block discovery
            // AND the static nextPc (EmitBudgetCheck / the chain edge) must use the FULL footprint, or the
            // walk mis-decodes the operand bytes as the next opcode and nextPc lands mid-instruction. A
            // FALLBACK Z80 op ends the block (Discover stops; its nextPc is never read — it self-terminates
            // via inner.Step), so only emitted LD rows need the correction. The 6502 is unaffected (its walk
            // length already includes operands).
            int length = r.Length + Z80EmitOperandBytes(d);
            run.Add((pc, d, length));
            if (d.EndsBlock) break;
            pc = unchecked((ushort)(pc + length));           // advance by the FULL footprint
            stream.SeekTo(pc);                               // reposition at the next instruction
        }
        return run;
    }

    public CompiledBlock<TCpu> Compile(ushort entryPc)
    {
        CompileCount++;
        FallbackEmitCount = 0;   // reset the per-Compile fallback seam (Task 6 emit-not-fallback probe)
        var run = Discover(entryPc);
        var spannedPages = PagesSpanned(run);
        var dm = new DynamicMethod(
            $"block_{entryPc:X4}", typeof(void),
            [typeof(TCpu), typeof(IAddressSpace), typeof(Fastmem),
             typeof(DirtyMap), typeof(ChainDispatch),
             typeof(long).MakeByRefType(), typeof(BlockExit).MakeByRefType(),
             typeof(IAddressSpace)],   // M3.2: ioBus (arg 7) — the Port callout target (never fastmem)
            typeof(BlockCompiler<TCpu>).Module, skipVisibility: true);   // reach the AdvanceCycles seam
        ILGenerator il = dm.GetILGenerator();
        var ctx = new EmitContext(il, spannedPages);

        var (lastPc, lastD, lastLen) = run[^1];
        foreach (var (pc, d, length) in run)
        {
            EmitInstruction(ctx, pc, d, length);
            if (!d.EndsBlock)
                EmitBudgetCheck(ctx, (ushort)(pc + length));   // the cc_interrupt-style budget exit
        }
        // Terminal exit. A block-ending opcode's arm self-terminates (it sets PC, then emits its own
        // chain-or-normal exit + ret — see the EndsBlock flow arms). The ONLY path that falls through
        // to here is a straight-line run capped at BlockLengthCap (no EndsBlock opcode): its successor
        // PC is the (compile-time-constant) continuation, so it is a chainable fall-through edge.
        if (!lastD.EndsBlock)
            EmitChainOrExit(ctx, (ushort)(lastPc + lastLen));
        else
            EmitNormalExit(ctx);   // safety net (unreachable for self-terminating ending arms)
        var del = (BlockDelegate<TCpu>)dm.CreateDelegate(typeof(BlockDelegate<TCpu>));
        return new CompiledBlock<TCpu>(entryPc, del, spannedPages);
    }

    /// <summary>Test seam (M3.2 Ground truth F.1 — the JIT-side never-fastmem proof). Compiles a
    /// ONE-instruction block over the real EmitPort arm into a BlockDelegate, so a test can invoke
    /// the genuine emitted IL with a stub Io IAddressSpace and assert the callout lands on the Io
    /// bus (arg 7), NEVER LoadByteFromBus/the fastmem'd memory bus. This drives the production
    /// EmitPort directly (the M3.1b synthetic-direct-emit precedent; the live second-CPU JIT run is
    /// M3.5/J1). EntryPc 0 + a single emittable Port row; the arm mirrors EmitInstruction's up-front
    /// opcode-fetch charge + PC increment so the cycle bookkeeping matches a real block.</summary>
    internal BlockDelegate<TCpu> CompilePortProbe(OpcodeDescriptor d)
    {
        var dm = new DynamicMethod(
            "port_probe", typeof(void),
            [typeof(TCpu), typeof(IAddressSpace), typeof(Fastmem),
             typeof(DirtyMap), typeof(ChainDispatch),
             typeof(long).MakeByRefType(), typeof(BlockExit).MakeByRefType(),
             typeof(IAddressSpace)],
            typeof(BlockCompiler<TCpu>).Module, skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        var ctx = new EmitContext(il, new System.Collections.Generic.HashSet<int>());
        EmitChargeOneCycle(ctx);    // opcode-fetch cycle (as EmitInstruction does up-front)
        EmitIncrementPC(ctx, 1);
        EmitPort(ctx, d);           // THE arm under test — an Io-bus callout, no fastmem branch
        EmitNormalExit(ctx);
        return (BlockDelegate<TCpu>)dm.CreateDelegate(typeof(BlockDelegate<TCpu>));
    }

    private static System.Collections.Generic.HashSet<int> PagesSpanned(
        System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D, int Length)> run)
    {
        var pages = new System.Collections.Generic.HashSet<int>();
        foreach (var (pc, d, length) in run)
            for (int b = 0; b < length; b++)        // the walk's COMPUTED length, not a field
                pages.Add(((pc + b) & 0xFFFF) >> 8);
        return pages;
    }

    /// <summary>Emit one instruction. NeedsFallback rows emit a callout to inner.Step() (the
    /// safety valve). Each emit arm mirrors the proven CpuEmitter body. <paramref name="length"/> is
    /// the walk's COMPUTED instruction length — threaded to the branch arm (the only arm that needs
    /// the post-operand fall-through PC) so no static descriptor Length field is read.</summary>
    private void EmitInstruction(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length)
    {
        if (d.NeedsFallback) { EmitFallbackStep(ctx); return; }
        // Mirror the interpreter Step's opcode fetch EXACTLY: charge the opcode-fetch cycle FIRST
        // (the interpreter does `ReadBus(PC)` — which does `_cycles++` — then `PC++` in Step,
        // then `Execute` resolves operands). Charging the fetch cycle up-front (rather than as a
        // trailing per-arm charge) is load-bearing for Ground truth F(a): a mid-instruction MMIO
        // store must see a CycleCount that already counts the opcode fetch, so the device's view
        // is byte-identical to the interpreter's WriteBus ordering. The per-arm operand/access
        // charges follow, in order, after this.
        EmitChargeOneCycle(ctx);   // opcode-fetch cycle (was: trailing in each arm — moved up for GT-F(a))
        EmitIncrementPC(ctx, 1);
        // M6 PR-1: the Z80 memory-refresh (R) bump. The interpreter's Step calls OnInstructionFetched
        // once per M1 opcode fetch, which bumps R; an EMITTED Z80 instruction never runs Step, so the
        // emit path must replicate the bump itself (a fallback op keeps its own Step bump). Every emitted
        // Z80 row in PR-1 is a base-plane single-opcode-byte LD (keyBytes == 1), so the bump is applied
        // once; PR-2+ that emit prefixed rows pass the prefix-byte count.
        if (TargetIsZ80)
            EmitZ80RefreshR(ctx, 1);
        // Reset the SMC "wrote page" marker before any instruction that might write RAM, so the
        // intra-block guard only trips on this instruction's own writable-RAM store.
        // M6 PR-1: the Z80 LD store-to-memory forms LD (HL),n (StoreImm8) and LD (nn),HL (Store16) ride
        // the Register JitOpClass (not Store), so the class check alone misses them — include them so the
        // intra-block SMC guard arms for their writes (LD (HL),r / LD (nn),A already ride Store).
        bool mayWriteRam = d.Class is JitOpClass.Store or JitOpClass.Rmw
            || (TargetIsZ80 && d.Ops.Length > 0 && d.Ops[0].Kind is "StoreImm8" or "Store16");
        if (mayWriteRam)
        {
            ctx.Il.Emit(OpCodes.Ldc_I4_M1);
            ctx.Il.Emit(OpCodes.Stloc, ctx.SmcPageLocal);
        }
        switch (d.Class)
        {
            case JitOpClass.Load: EmitLoad(ctx, d); break;
            case JitOpClass.Store: EmitStore(ctx, d); break;
            case JitOpClass.Register: EmitRegister(ctx, d); break;
            case JitOpClass.Alu: EmitAlu(ctx, d); break;
            case JitOpClass.Rmw: EmitRmw(ctx, d); break;
            case JitOpClass.Branch: EmitBranch(ctx, pc, d, length); break;
            case JitOpClass.Jump: EmitJump(ctx, pc, d); break;
            case JitOpClass.Jsr: EmitJsr(ctx, pc, d); break;
            case JitOpClass.Rts: EmitRts(ctx, d); break;
            case JitOpClass.Port: EmitPort(ctx, d); break;   // M3.2: the Io-bus callout (never fastmem)
            default:
                throw new EmulationException(
                    $"BlockCompiler has no emit arm for class '{d.Class}' (opcode 0x{d.Opcode:X2}); "
                  + "a JIT bug — the descriptor said it was emittable but no arm exists.");
        }
        // Intra-block SMC guard (Task-5 hand-off note #2 + controller directive #2): if this
        // store/RMW wrote a byte on one of THIS block's own pages, the compiled IL ahead of PC may
        // now be stale — end the block (exit Normal, PC already at the next instruction) so the
        // dispatcher's InvalidateIfDirty flushes the cache and the next GetOrCompile re-decodes the
        // modified bytes. Conservative + runtime-precise: only fires when the actual written page
        // is one the block occupies.
        if (mayWriteRam)
            EmitSmcGuard(ctx);
    }

    /// <summary>Emit the intra-block self-modifying-code guard: if <c>ctx.SmcPageLocal</c> (the page
    /// a writable-RAM store just wrote, or -1) is one of the block's own SpannedPages, set
    /// exit=Normal and return — ending the block so the next dispatch re-decodes the modified bytes.
    /// PC is already at the next instruction (operand reads advanced it), so no PC fix-up is needed.
    /// Pages are baked as constants from the block's static page span.</summary>
    private static void EmitSmcGuard(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        Label noSmc = il.DefineLabel();
        Label endBlock = il.DefineLabel();
        // if (SmcPageLocal == P) goto endBlock; for each spanned page P.
        foreach (int page in ctx.SpannedPages)
        {
            il.Emit(OpCodes.Ldloc, ctx.SmcPageLocal);
            il.Emit(OpCodes.Ldc_I4, page);
            il.Emit(OpCodes.Beq, endBlock);
        }
        il.Emit(OpCodes.Br, noSmc);
        il.MarkLabel(endBlock);
        // CHANGED (M2-ii, Task 3): exit = Recompile (was Normal). PC is already at the next
        // instruction. A Recompile exit is NEVER chained past — the guard returns from the MIDDLE
        // of the block (a chainable exit is only at a block-ending opcode), so control never reaches
        // the block's chain edge. The dispatcher's InvalidateIfDirty flushes + the per-page eviction
        // (Task 4) drops the stale block; the next dispatch re-decodes the self-modified bytes. This
        // closes the M2-i carry-forward #2 hazard (the PRECISE signal; the chain edge's !Dirty.Any
        // gate is the COARSE cross-block backstop — Ground truth B).
        EmitRecompileExit(ctx);
        il.MarkLabel(noSmc);
    }

    /// <summary>exit = Recompile; ret. The intra-block SMC guard's exit (M2-ii): the block
    /// self-modified one of its own pages, so the dispatcher MUST InvalidateIfDirty + re-decode.</summary>
    private static void EmitRecompileExit(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_S, ArgExit);                 // out BlockExit exit
        il.Emit(OpCodes.Ldc_I4, (int)BlockExit.Recompile);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ret);
    }

    // ── Cycle bookkeeping (both counters move together: budget-=1 AND cpu._cycles+=1) ──────
    private void EmitChargeOneCycle(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        // cpu.AdvanceCycles(1)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Call, _mAdvance);
        // budget -= 1
        il.Emit(OpCodes.Ldarg_S, ArgBudget);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldind_I8);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stind_I8);
    }

    /// <summary>M6 PR-1: charge N cycles in one shot (cpu.AdvanceCycles(N) + budget -= N).
    /// EmitChargeOneCycle is the N==1 special case; the Z80 LD arm charges the residual T-states (the
    /// interpreter body's <c>_cycles += N</c>) after the up-front fetch + the per-access charges. N must
    /// be &gt;= 0; N &lt;= 0 is a no-op.</summary>
    private void EmitChargeCycles(EmitContext ctx, int n)
    {
        if (n <= 0) return;
        ILGenerator il = ctx.Il;
        // cpu.AdvanceCycles(n)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, n);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Call, _mAdvance);
        // budget -= n
        il.Emit(OpCodes.Ldarg_S, ArgBudget);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldind_I8);
        il.Emit(OpCodes.Ldc_I4, n);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stind_I8);
    }

    // ── PC helpers ─────────────────────────────────────────────────────────────────────────
    /// <summary>Push the current PC (as uint) onto the stack.</summary>
    private void EmitLoadPC(EmitContext ctx)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldfld, _fpc);   // ushort -> I4 (zero-extended)
    }

    /// <summary>PC = (ushort)(PC + n).</summary>
    private void EmitIncrementPC(EmitContext ctx, int n)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _fpc);
        il.Emit(OpCodes.Ldc_I4, n);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);
    }

    /// <summary>Read the byte at the current PC (operand/code fetch), charging 1 cycle; pushes
    /// the byte (as int). Code/operand bytes live in RAM/ROM, so the fastmem-or-bus branch is
    /// correct and charges the same cycle ReadBus(PC) would.</summary>
    private void EmitReadAtPC(EmitContext ctx)
    {
        EmitLoadPC(ctx);                 // push (uint)PC
        ctx.Il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);            // pops addr, pushes byte; charges 1 cycle
    }

    // ── Read a byte at the address on the stack (the fastmem/bus branch — Ground truth G) ────
    /// <summary>Emit a byte read of the (uint) address on the IL stack. Charges 1 cycle, then
    /// branches: fastmem PageBacking[page] null -> bus.Read8 (MMIO/unmapped); else direct array
    /// load <c>backing[PageOffset[page] + (addr &amp; 0xFF)]</c>. Leaves the byte (as int) on the
    /// stack. Under DisableFastmem every PageBacking[p] is null, so the bus arm always runs.</summary>
    private void LoadByteFromBus(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.EaLocal);   // ea = address
        EmitChargeOneCycle(ctx);               // charge BEFORE the access (matches ReadBus)

        Label mmio = il.DefineLabel(), done = il.DefineLabel();

        // backing = fastmem.PageBacking[ea >> 8]
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageBacking);     // byte[]?[]
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);                     // page = ea >> 8
        il.Emit(OpCodes.Ldelem_Ref);                 // backing (byte[] or null)
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, mmio);              // null -> MMIO

        // direct: backing[PageOffset[page] + (ea & 0xFF)]
        // stack: backing
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageOffset);      // int[]
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);                     // page
        il.Emit(OpCodes.Ldelem_I4);                  // PageOffset[page]
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);                        // ea & 0xFF
        il.Emit(OpCodes.Add);                        // index
        il.Emit(OpCodes.Ldelem_U1);                  // backing[index] -> byte (int)
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(mmio);
        il.Emit(OpCodes.Pop);                        // drop the null backing
        il.Emit(OpCodes.Ldarg_1);                    // bus
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Callvirt, MRead);            // bus.Read8(ea) -> byte (int)
        il.MarkLabel(done);
    }

    /// <summary>Emit a byte store: writes the (int) value on the stack to the (uint) address
    /// below it on the stack (stack order: ..., address, value). Charges 1 cycle BEFORE the
    /// access (Ground truth F (a): the device sees a write-cycle-inclusive CycleCount). Branches:
    /// PageBacking[page] null -> bus.Write8 (MMIO/unmapped/ROM-as-bus); else, for a WRITABLE
    /// RAM page, direct array store + dirty.Mark(page); a non-writable (ROM) backing page drops
    /// the write (the interpreter drops it too). Under DisableFastmem PageBacking[p] is null, so
    /// every store routes through the bus (+ dirty.Mark for writable pages, so SMC still works).</summary>
    private void EmitStoreByte(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        // stack: address, value  -> stash both (value on top)
        il.Emit(OpCodes.Stloc, ctx.DataLocal);   // value
        il.Emit(OpCodes.Stloc, ctx.EaLocal);     // address
        EmitChargeOneCycle(ctx);                 // charge BEFORE the access (MMIO ordering)

        Label mmio = il.DefineLabel(), drop = il.DefineLabel(), done = il.DefineLabel();

        // backing = fastmem.PageBacking[page]
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageBacking);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldelem_Ref);             // backing or null
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, mmio);          // null -> bus callout

        // writable? fastmem.PageWritable[page]
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageWritable);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldelem_U1);              // PageWritable[page] (bool as int)
        il.Emit(OpCodes.Brfalse, drop);          // ROM page -> drop the write (stack: backing)

        // direct RAM store: backing[PageOffset[page] + (ea & 0xFF)] = value
        // stack: backing
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageOffset);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldelem_I4);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Add);                    // index
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stelem_I1);              // backing[index] = (byte)value
        // dirty.Mark(page)
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Callvirt, MDirtyMark);
        // record the written page for the intra-block SMC guard (EmitSmcGuard, after the instr).
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Stloc, ctx.SmcPageLocal);
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(drop);
        il.Emit(OpCodes.Pop);                    // drop backing; ROM write is a no-op
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(mmio);
        il.Emit(OpCodes.Pop);                    // drop the null backing
        il.Emit(OpCodes.Ldarg_1);               // bus
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Callvirt, MWrite);       // bus.Write8(ea, value)
        // The bus arm runs for true MMIO pages AND for every writable RAM page under
        // DisableFastmem (PageBacking is suppressed but PageWritable is preserved). A writable
        // page still owns code, so dirty.Mark + record the SMC page so invalidation works in trace
        // mode (Ground truth G, last row). A true MMIO page has PageWritable[page] == false, so
        // this is skipped — MMIO never marks dirty (it cannot hold code).
        Label noMark = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageWritable);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldelem_U1);
        il.Emit(OpCodes.Brfalse, noMark);        // not writable (MMIO) -> no dirty mark
        // dirty.Mark(page)
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Callvirt, MDirtyMark);
        // SmcPageLocal = page
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Stloc, ctx.SmcPageLocal);
        il.MarkLabel(noMark);
        il.MarkLabel(done);
    }

    // ── SetNZ: P = (P & 0x7D) | (src==0 ? 2 : 0) | (src & 0x80) ──────────────────────────────
    /// <summary>Set the N and Z flags from the (int) source byte on the stack (consumes it).</summary>
    private void EmitSetNZFromStack(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.DataLocal);   // src
        il.Emit(OpCodes.Ldarg_0);                // cpu (for the final Stfld)
        // (P & 0x7D)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _fp);
        il.Emit(OpCodes.Ldc_I4, 0x7D);
        il.Emit(OpCodes.And);
        // | (src==0 ? 2 : 0)
        Label nz = il.DefineLabel(), zdone = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Brtrue, nz);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Br, zdone);
        il.MarkLabel(nz);
        il.Emit(OpCodes.Ldc_I4_0);
        il.MarkLabel(zdone);
        il.Emit(OpCodes.Or);
        // | (src & 0x80)
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Ldc_I4, 0x80);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stfld, _fp);
    }

    /// <summary>Resolve an operand register's <see cref="FieldInfo"/> by its declared NAME (J2).
    /// Throws a clear compile-time error if a descriptor ever names a register the CPU type does
    /// not declare — the data-driven replacement for the old "unknown register index" guard.</summary>
    private FieldInfo RegField(string name) => _regFields.TryGetValue(name, out var f)
        ? f
        : throw new EmulationException(
            $"compiled descriptor names register '{name}' which the CPU type does not declare");

    /// <summary>PR-0: push the named 16-bit register's value (as int, 0..0xFFFF) onto the IL stack.
    /// A real ushort field is a direct Ldfld; a composed pair-view is hi&lt;&lt;8 | lo over its two byte
    /// half-fields (the Z80 AF/BC/DE/HL/IX/IY shape; the 8086 AX/BX/CX/DX shape). Throws if the name is
    /// neither — the same fail-loud discipline as RegField.</summary>
    private void EmitLoadReg16(EmitContext ctx, string name)
    {
        ILGenerator il = ctx.Il;
        if (_regWideFields.TryGetValue(name, out var wf))
        {
            // (int)cpu.<ushort field>
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, wf);
            il.Emit(OpCodes.Conv_I4);            // ushort -> int (zero-extended)
            return;
        }
        if (_regPairFields.TryGetValue(name, out var p))
        {
            // (cpu.<hi> << 8) | cpu.<lo>
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, p.Hi);        // byte hi (loaded as int)
            il.Emit(OpCodes.Ldc_I4_8);
            il.Emit(OpCodes.Shl);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, p.Lo);        // byte lo (loaded as int)
            il.Emit(OpCodes.Or);
            return;
        }
        throw new EmulationException(
            $"compiled descriptor names 16-bit register '{name}' which the CPU type declares neither as a "
          + "ushort field nor as a composable pair-view (PR-0 wide-register helper)");
    }

    /// <summary>PR-0: pop an int (0..0xFFFF) off the IL stack into the named 16-bit register. A real ushort
    /// field is Conv_U2 + Stfld; a composed pair-view writes hi=(byte)(v&gt;&gt;8), lo=(byte)v — byte-identical
    /// to the generated property setter. Stages the value through ctx.TmpInt because both halves need it.</summary>
    private void EmitStoreReg16(EmitContext ctx, string name)
    {
        ILGenerator il = ctx.Il;
        if (_regWideFields.TryGetValue(name, out var wf))
        {
            // cpu.<ushort field> = (ushort)value   (stack: ..., value). Stage through TmpInt because the
            // value arrives BELOW the receiver — push cpu, reload the value, truncate, store. The single
            // Conv_U2 before Stfld is the operative truncation (the field is ushort).
            il.Emit(OpCodes.Stloc, ctx.TmpInt);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.TmpInt);
            il.Emit(OpCodes.Conv_U2);
            il.Emit(OpCodes.Stfld, wf);
            return;
        }
        if (_regPairFields.TryGetValue(name, out var p))
        {
            // stash value, then write both halves (stack: ..., value)
            il.Emit(OpCodes.Stloc, ctx.TmpInt);
            // cpu.<hi> = (byte)(value >> 8)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.TmpInt);
            il.Emit(OpCodes.Ldc_I4_8);
            il.Emit(OpCodes.Shr_Un);
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stfld, p.Hi);
            // cpu.<lo> = (byte)value
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.TmpInt);
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stfld, p.Lo);
            return;
        }
        throw new EmulationException(
            $"compiled descriptor names 16-bit register '{name}' which the CPU type declares neither as a "
          + "ushort field nor as a composable pair-view (PR-0 wide-register helper)");
    }

    /// <summary>Test seam (PR-0): compile a one-shot method that writes <paramref name="value"/> into the
    /// 16-bit register <paramref name="name"/> via EmitStoreReg16, reads it back via EmitLoadReg16, and
    /// returns the readback. Proves the wide-register helper round-trips for every pair-view + ushort field,
    /// independent of any op emit. No production caller — exists only for the PR-0 gate.</summary>
    internal int CompileReg16RoundTrip(string name, int value)
    {
        var dm = new DynamicMethod(
            $"reg16_{name}", typeof(int), [_target.CpuType], typeof(BlockCompiler<TCpu>).Module,
            skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        // A minimal EmitContext: it declares the scratch locals (TmpInt, etc.) the helpers stage through.
        var ctx = new EmitContext(il, new System.Collections.Generic.HashSet<int>());
        il.Emit(OpCodes.Ldc_I4, value);
        EmitStoreReg16(ctx, name);
        EmitLoadReg16(ctx, name);
        il.Emit(OpCodes.Ret);
        var fn = (System.Func<TCpu, int>)dm.CreateDelegate(typeof(System.Func<TCpu, int>));
        return fn(_cpu);
    }

    // (Emit arms continue in BlockCompiler.Emit.cs)

    private static void EmitNormalExit(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_S, ArgExit);                 // out BlockExit exit
        il.Emit(OpCodes.Ldc_I4, (int)BlockExit.Normal);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>Emit a statically-known chainable exit (the chaining link/unlink core — Ground
    /// truth A). The block-ending instruction's own emit has ALREADY set cpu.PC to the successor
    /// (JMP set it; a taken branch set it; the cap fall-through left PC at the continuation), so
    /// chaining and the dispatcher fall-back agree on PC. Here we clear the chain-break gates
    /// (Ground truth A/B) and, if clear, call the ChainDispatch with the compile-time-constant
    /// target; on return, ret with the propagated exit. Any gate not clear -> EmitNormalExit (the
    /// dispatcher resumes at PC). A Recompile exit never reaches here — the SMC guard returns from
    /// the MIDDLE of the block (a chainable exit is only at a block-ending opcode), so a
    /// self-modifying block always routes to the dispatcher (Ground truth B).</summary>
    private void EmitChainOrExit(EmitContext ctx, ushort staticTargetPc)
    {
        ILGenerator il = ctx.Il;
        Label toDispatcher = il.DefineLabel();

        // (2) budget <= 0 -> dispatcher (PC already at the target; the next slice resumes there)
        il.Emit(OpCodes.Ldarg_S, ArgBudget);
        il.Emit(OpCodes.Ldind_I8);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ble, toDispatcher);

        // (3) dirty.Any -> dispatcher (the SMC coarse backstop — Ground truth B)
        il.Emit(OpCodes.Ldarg_3);                       // DirtyMap dirty
        il.Emit(OpCodes.Callvirt, MDirtyAny);           // dirty.Any (bool)
        il.Emit(OpCodes.Brtrue, toDispatcher);

        // (4) cpu.InterruptPending -> dispatcher (sample the irq at the chain edge)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _mInterruptPending);
        il.Emit(OpCodes.Brtrue, toDispatcher);

        // (5)-(7) chain.Invoke(targetPc, ref budget, out exit); ret
        il.Emit(OpCodes.Ldarg_S, ArgChain);             // ChainDispatch chain
        il.Emit(OpCodes.Ldc_I4, (int)staticTargetPc);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Ldarg_S, ArgBudget);            // ref long budget
        il.Emit(OpCodes.Ldarg_S, ArgExit);              // out BlockExit exit
        il.Emit(OpCodes.Callvirt, MChainInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(toDispatcher);
        EmitNormalExit(ctx);
    }

    /// <summary>After an instruction, if budget &lt;= 0 set PC = nextPc and exit Budget; else
    /// continue the block.</summary>
    private void EmitBudgetCheck(EmitContext ctx, ushort nextPc)
    {
        ILGenerator il = ctx.Il;
        Label keepGoing = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_S, ArgBudget);
        il.Emit(OpCodes.Ldind_I8);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Bgt, keepGoing);                   // budget > 0 -> continue
        // budget exhausted: PC = nextPc; exit = Budget; ret
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)nextPc);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);
        il.Emit(OpCodes.Ldarg_S, ArgExit);
        il.Emit(OpCodes.Ldc_I4, (int)BlockExit.Budget);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(keepGoing);
    }

    /// <summary>The interpreter-Step fallback for a NeedsFallback opcode (ADC/SBC/BRK/RTI/
    /// undefined). Runs one authentic interpreter Step (which charges its own cycles via
    /// ReadBus/WriteBus and advances PC), subtracts the consumed cycles from budget, then exits
    /// the block (the post-Step PC is dynamic — the block cannot statically continue).</summary>
    private void EmitFallbackStep(EmitContext ctx)
    {
        FallbackEmitCount++;   // test seam (Task 6): count the fallbacks this Compile emitted
        ILGenerator il = ctx.Il;
        // long before = cpu.CycleCount;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _mCycleCount);
        il.Emit(OpCodes.Stloc, ctx.TmpLong);
        // cpu.Step();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _mStep);
        // budget -= (cpu.CycleCount - before);
        il.Emit(OpCodes.Ldarg_S, ArgBudget);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldind_I8);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _mCycleCount);
        il.Emit(OpCodes.Ldloc, ctx.TmpLong);
        il.Emit(OpCodes.Sub);                // consumed
        il.Emit(OpCodes.Sub);                // budget - consumed
        il.Emit(OpCodes.Stind_I8);
        EmitNormalExit(ctx);                 // PC already set by Step
    }
}
