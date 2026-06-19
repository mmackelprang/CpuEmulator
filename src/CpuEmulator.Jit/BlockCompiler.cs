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

    /// <summary>M6 PR-4a: a per-instance count of how many times an M68000Move row was DISPATCHED to
    /// <see cref="EmitM68kMove"/> across this compiler's Compile calls (the arm-selection probe). Pre-PR-4a this
    /// was always 0 — the byte-stream decode mis-matched the table so no MOVE descriptor ever reached the emit
    /// switch (the dead-arm blocker). A test asserts it is &gt; 0 after a MOVE block compiles, so the 68000 MOVE
    /// parity gate is proven NON-vacuous (the emit IL actually ran), not interpreter-vs-interpreter. Distinct from
    /// <see cref="FallbackEmitCount"/> (which resets per Compile); this ACCUMULATES across Compiles so a sweep can
    /// assert one positive total.</summary>
    internal int M68kMoveEmitSelections { get; private set; }

    /// <summary>M6 PR-5: the ALU analogue of <see cref="M68kMoveEmitSelections"/> — a per-instance count of how many
    /// times an M68000Alu row was DISPATCHED to <see cref="EmitM68kAlu"/>. A test asserts it is &gt; 0 after an ALU
    /// block compiles, so the 68000 ALU parity gate is proven NON-vacuous (the emit IL actually ran). Accumulates
    /// across Compiles (unlike <see cref="FallbackEmitCount"/>, which resets per Compile).</summary>
    internal int M68kAluEmitSelections { get; private set; }

    /// <summary>M6 PR-6: the shift analogue of <see cref="M68kAluEmitSelections"/> — a per-instance count of how
    /// many times an M68000Shift row was DISPATCHED to <see cref="EmitM68kShift"/>. A test asserts it is &gt; 0
    /// after a shift block compiles, so the 68000 shift parity gate is proven NON-vacuous. Accumulates across
    /// Compiles (unlike <see cref="FallbackEmitCount"/>, which resets per Compile).</summary>
    internal int M68kShiftEmitSelections { get; private set; }

    /// <summary>M6 PR-6: the control-flow analogue of <see cref="M68kAluEmitSelections"/> — a per-instance count
    /// of how many times an M68000Flow row was DISPATCHED to <see cref="EmitM68kFlow"/>. A test asserts it is
    /// &gt; 0 after a flow block compiles, so the 68000 flow parity gate is proven NON-vacuous. Accumulates
    /// across Compiles.</summary>
    internal int M68kFlowEmitSelections { get; private set; }

    /// <summary>M6 PR-B: how many times an 8086 MOV row was DISPATCHED to <see cref="EmitM8086Mov"/> (the
    /// dead-arm-now-live probe). A test asserts it is &gt; 0 after a MOV block compiles, so the MOV parity gate is
    /// NON-vacuous (the emit IL actually ran, not interpreter-vs-interpreter). Accumulates across Compiles
    /// (unlike <see cref="FallbackEmitCount"/>, which resets per Compile).</summary>
    internal int M8086MovEmitSelections { get; private set; }

    /// <summary>M6 PR-C: how many times an 8086 ALU row was DISPATCHED to <see cref="EmitM8086Alu"/> (the ALU
    /// analogue of <see cref="M8086MovEmitSelections"/> — the dead-arm-now-live probe). A test asserts it is
    /// &gt; 0 after an ALU block compiles, so the ALU + FLAGS parity gate is NON-vacuous (the emit IL actually
    /// ran). Accumulates across Compiles (unlike <see cref="FallbackEmitCount"/>, which resets per Compile).</summary>
    internal int M8086AluEmitSelections { get; private set; }

    /// <summary>M6 PR-D: how many times an 8086 NEAR control-flow row was DISPATCHED to <see cref="EmitM8086Flow"/>
    /// (the flow analogue of <see cref="M8086AluEmitSelections"/> — the dead-arm-now-live probe). A test asserts it
    /// is &gt; 0 after a flow block compiles, so the branch (Jcc/JMP/CALL/RET/LOOP) parity gate is NON-vacuous (the
    /// emit IL actually ran). Accumulates across Compiles (unlike <see cref="FallbackEmitCount"/>, which resets per
    /// Compile).</summary>
    internal int M8086FlowEmitSelections { get; private set; }

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

    // M6 PR-4 (Task 3): the 32-bit register file (GAP G2). The 68000's D0-D7/A0-A6/USP/SSP/PC are uint
    // FIELDS (M68000Cpu.g.cs:51-68); the ushort-gated _regWideFields above SKIPS them, so a parallel
    // uint-gated map reaches them. A7 is NOT here — it is a banked PROPERTY (USP/SSP by the SR S-bit,
    // M68000Cpu.cs:60-64), handled by the EA resolver's EmitLoadAreg/EmitStoreAreg (Task 4, DECISION A7).
    // Built per-compile from the same target.RegisterNames + CpuType introspection (no generator change).
    private readonly System.Collections.Generic.Dictionary<string, FieldInfo> _regWide32Fields;

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

    // M6 PR-4: the 68000's status register (ushort SR). The EA resolver's A7-banking branch reads its S-bit
    // ((SR>>13)&1) to choose USP/SSP for A7, and the MOVE CCR helper writes N/Z into its low byte. Resolved
    // by name on the CPU type; null for non-68000 CPUs (harmless — only dereferenced under TargetIsM68000).
    private readonly FieldInfo? _m68kSR;

    // M6 PR-B (DECISION B-0): the 8086's CODE-segment base (CS<<4) for the block under compile. A JIT block is
    // keyed on the 16-bit IP and compiled against ONE CS (the CS live at Compile time); Discover + every emit-time
    // const read fetch from (CS<<4)+IP physical, exactly as the runtime AddressSpaceFetchStream(_bus, IP, CS) does.
    // Re-read per Compile from the live CS field (the CS-aliasing invariant: PR-B's MOV scope never changes CS, so
    // the 16-bit IP key is exact for the block's life; far flow that changes CS is PR-D, which owns widening the key).
    private readonly FieldInfo? _m8086CS;   // the ushort CS field; null for non-8086 CPUs
    private readonly FieldInfo? _m8086FLAGS;   // M6 PR-C: the ushort FLAGS word (the 8086 ALU flag core); null for non-8086
    private uint _m8086CodePhysBase;        // (CS << 4) & 0xFFFFF — set at the head of each Discover/Compile

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

    // M6 PR-4 (Task 2): the wide big-endian bus accessors — the 68000 needs word/long access (the JIT had
    // BYTE-ONLY bus helpers, a hard GAP). IAddressSpace.Read16/Read32/Write16/Write32 are big-endian on the
    // 68000 bus (high word first — Core/IAddressSpace.cs:46-89, matching the interpreter's ReadLongBus/
    // WriteLongBus high-word-first decomposition, M68000Cpu.cs:199-210). Resolved on IAddressSpace so the
    // callvirt binds against the same bus arg (Ldarg_1) the byte helpers use.
    private static readonly MethodInfo MRead16 = typeof(IAddressSpace).GetMethod("Read16")!;
    private static readonly MethodInfo MRead32 = typeof(IAddressSpace).GetMethod("Read32")!;
    private static readonly MethodInfo MWrite16 = typeof(IAddressSpace).GetMethod("Write16")!;
    private static readonly MethodInfo MWrite32 = typeof(IAddressSpace).GetMethod("Write32")!;

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
        // M6 PR-4: the parallel 32-bit (uint-field) map — the 68000's D/A/USP/SSP/PC. Gated on uint so it
        // captures EXACTLY the 68000's wide fields and is empty for the 6502/Z80/8086 (whose register fields
        // are byte/ushort), so no other CPU's emit path is affected.
        _regWide32Fields = new System.Collections.Generic.Dictionary<string, FieldInfo>(System.StringComparer.Ordinal);
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
            if (target.CpuType.GetField(name) is { } uf && uf.FieldType == typeof(uint))
                _regWide32Fields[name] = uf;                     // D0-D7/A0-A6/USP/SSP/PC (68000) — real uint
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
        _m68kSR = target.CpuType.GetField("SR");   // M6 PR-4: the 68000 SR (A7 banking + the MOVE CCR)
        _m8086CS = target.CpuType.GetField("CS");   // M6 PR-B: the 8086 code segment (CS<<4 = the fetch base)
        _m8086FLAGS = target.CpuType.GetField("FLAGS");   // M6 PR-C: the 8086 FLAGS word (the ALU flag core)
    }

    /// <summary>M6 PR-B: is the compiled CPU the 8086? Routes the MOV rows to EmitM8086Mov and selects the
    /// SEGMENTED Discover fetch stream + the (CS&lt;&lt;4)+IP emit-time physical-PC origin. No other CPU produces
    /// the 8086 mnemonics, so the (TargetIsM8086, mnemonic) pair is the unambiguous arm discriminator (mirrors
    /// the Z80's (TargetIsZ80, op-kind) keying — the 8086 MOV rows ride JitOpClass.Register, NOT a dedicated class).</summary>
    private bool TargetIsM8086 => _target.CpuType.Name == "M8086Cpu";

    /// <summary>M6 PR-C: is this an in-scope 8086 integer-ALU mnemonic? The gate-flip whitelist (CpuEmitter
    /// IsEmittableX86Family) admits exactly these by mnemonic; MUL/IMUL/DIV/IDIV (F6/F7 /4../7) + CALL/JMP/PUSH
    /// (FF /2../6) carry NON-ALU mnemonics, so they are auto-excluded (stay interpreter-fallback). The
    /// (TargetIsM8086, mnemonic) pair is the unambiguous arm discriminator — no opcode-level exclusion needed.</summary>
    private static bool IsM8086AluMnemonic(string m) =>
        m is "ADD" or "OR" or "ADC" or "SBB" or "AND" or "SUB" or "XOR" or "CMP" or "TEST" or "INC" or "DEC" or "NOT" or "NEG";

    /// <summary>M6 PR-D: is this an in-scope NEAR 8086 control-flow row (the EmitM8086Flow family)? The plain
    /// opcodes 70-7F/EB/E9/E8/C3/C2/E0-E3 (matched on the BYTE d.Opcode), PLUS the FF-group NEAR indirect CALL/JMP
    /// (d.Opcode == 0xFF AND mnemonic "CALL"/"JMP"). The descriptor's d.Opcode is the BYTE for EVERY row (the
    /// FF-group dictionary KEY 0x7FA/0x7FC is NOT carried on d — only the OperationKey the dictionary is keyed on
    /// is, and it is not surfaced here), so the FF-group rows are keyed by (opcode, mnemonic). The ALU dispatch
    /// runs FIRST and catches the FF /0 /1 INC/DEC rows (mnemonic "INC"/"DEC"); this catches the FF /2 /4 CALL/JMP
    /// rows. The FAR forms (9A/EA/CB/CA + the far FF /3 /5, which ALSO carry "CALL"/"JMP" with opcode 0xFF) change
    /// CS and are EXCLUDED BY THE GATE (IsEmittableX86Family admits only the near 0x7FA/0x7FC keys), so a far FF row
    /// never reaches dispatch — this predicate need only separate near-flow from the (already gated-in) ALU rows.</summary>
    private static bool IsM8086FlowOpcode(OpcodeDescriptor d) =>
        d.Opcode is (>= 0x70 and <= 0x7F) or 0xEB or 0xE9 or 0xE8 or 0xC3 or 0xC2 or 0xE0 or 0xE1 or 0xE2 or 0xE3
        || (d.Opcode == 0xFF && d.Mnemonic is "CALL" or "JMP");

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

        // M6 PR-3: the control-flow family's PC-operand footprint (beyond the 1-byte opcode). An EMITTED flow
        // row ends the block, but its `length` is still threaded into the arm for the conditional not-taken
        // fall-through PC (pc + length), so the footprint must be exact. JP/CALL (absolute, 16-bit target) read
        // 2; JR/DJNZ (relative, 1-byte displacement) read 1; RET/RST read 0. (PUSH/POP ride the Register class
        // and read 0 PC operands — they fall through to the default below.)
        if (IsZ80FlowKind(d))
            return d.Ops[0].Kind switch
            {
                "JumpAbs" or "JumpIf" or "CallAbs" or "CallIf" => 2,
                "RelJump" or "RelJumpIf" or "Djnz" => 1,
                _ => 0,   // Ret / RetCc / Rst
            };

        return 0;
    }

    /// <summary>M6 PR-3: is this descriptor an emittable Z80 control-flow row (the EmitZ80Flow family —
    /// NOT the stack kinds)? PUSH/POP (Push16/Pop16) ride JitOpClass.Register and read 0 PC operands, so they
    /// are deliberately excluded here (Z80EmitOperandBytes's default-0 covers them).</summary>
    private static bool IsZ80FlowKind(OpcodeDescriptor d) =>
        d.Ops.Length > 0 && d.Ops[0].Kind is "JumpAbs" or "JumpIf" or "CallAbs" or "CallIf"
            or "Ret" or "RetCc" or "RelJump" or "RelJumpIf" or "Djnz" or "Rst";

    /// <summary>M6 PR-2b: is this an EMITTED Z80 PREFIXED row (a 2-byte opcode key: prefix + opcode)? The
    /// ONLY emitted prefixed family in PR-2b is the ED ADC/SBC HL,rr lane (op-kind EdAdcSbc16); keying on the
    /// emitted kind is exact and self-documenting (OpcodeDescriptor carries no KeyShape). EmitInstruction uses
    /// this to charge the 2nd opcode-fetch cycle, advance PC past the 2nd key byte, and bump R twice (the
    /// interpreter's Step charges/bumps once per opcode byte). Every other emitted row is base-plane (1 byte).</summary>
    private static bool IsZ80PrefixedEmittedRow(OpcodeDescriptor d) =>
        !d.NeedsFallback && d.Ops.Length > 0 && d.Ops[0].Kind == "EdAdcSbc16";

    /// <summary>Decode from pc until an EndsBlock opcode or the block-length cap, running the
    /// generated decode walk (Ground truth B) — NOT a static descriptor Length field. The walk
    /// reads opcode/operand bytes through a BusFetchStream (a debugger-view decode; never executes,
    /// charges no cycle) and returns (key, COMPUTED length); Discover advances the cursor by that
    /// returned length and SeekTo's the next instruction. The discovered run is a list of
    /// (pc, descriptor, computed-length) tuples — the length is the walk's output, the only length
    /// source the rest of Compile/PagesSpanned reads.</summary>
    public System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D, int Length, byte X86Seg)> Discover(ushort pc)
    {
        var run = new System.Collections.Generic.List<(ushort, OpcodeDescriptor, int, byte)>();
        // M6 PR-B (DECISION B-0): the 8086 fetches code from (CS<<4)+IP 20-bit physical, NOT flat 16-bit IP.
        // Bake the block's code-segment base once (the live CS at Compile time — the CS-aliasing invariant), so
        // Discover AND the emit-time const reads (M8086CodePhys) walk the SAME physical bytes the runtime
        // AddressSpaceFetchStream(_bus, IP, CS) executes. For non-8086 CPUs this stays 0 (unused).
        _m8086CodePhysBase = TargetIsM8086 && _m8086CS is not null
            ? ((uint)(ushort)_m8086CS.GetValue(_cpu)! << 4) & 0xFFFFFu
            : 0u;
        // M6 PR-4a: the decode-walk fetch stream is per-target GRANULAR. The 6502/Z80/8086 generated Decode()
        // walks are BYTE-granular (BusFetchStream, UnitBytes==1, Read8) — authored against a byte stream. The
        // 68000's generated Decode() is WORD-granular (M68000FetchStream, UnitBytes==2, big-endian — it reads
        // `uint operword = stream.NextUnit()` as a 16-bit word, M68000Cpu.g.cs:748). Fed the byte stream the
        // 68000 read only the operword's HIGH byte, mis-matched the field-op table, and DescriptorFor returned
        // Undefined/NeedsFallback — so EVERY 68000 block fell back and the MOVE emit arm never dispatched (the
        // PR-4 blocker). The ternary keys on the SAME TargetIsM68000 discriminator that already routes the MOVE
        // arm (EmitInstruction). The non-68000 branch is byte-for-byte the pre-PR-4a construction, so the
        // byte-granular CPUs see an IDENTICAL Discover (proven by their empty-diff descriptor tables + unchanged
        // FallbackEmitCount + green JIT sweeps — the PR-4a regression gate).
        // Per-target GRANULAR + ORIGIN fetch stream. The 68000 is word-granular (M68000FetchStream); the 8086 is
        // byte-granular but SEGMENTED — its physical fetch origin is (CS<<4)+IP, so it uses the Core
        // AddressSpaceFetchStream segmented ctor (the interpreter's own walk — maximal oracle fidelity). The
        // 6502/Z80 stay flat byte-granular BusFetchStream (UNCHANGED — empty-diff regression-safe).
        IFetchStream NewStream(ushort at) =>
              TargetIsM68000 ? new M68000FetchStream(_bus, at)   // word-granular: Seeds the queue from the two physical words at `at`
            : TargetIsM8086  ? new CpuEmulator.Core.Jit.AddressSpaceFetchStream(
                                   _bus, at, (ushort)(_m8086CodePhysBase >> 4))   // segmented: (CS<<4)+at physical
            : new BusFetchStream(_bus, at);     // byte-granular: == the pre-PR-4a `new BusFetchStream(_bus, pc)`
        IFetchStream stream = NewStream(pc);                // positioned at pc (byte- or word-granular per target)
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
            // M6 PR-4: NO 68000 footprint correction here. Unlike the Z80 (whose r.Length is the opcode-KEY
            // length, so the emitted-LD operand bytes must be added back via Z80EmitOperandBytes), the 68000's
            // generated Decode() consumes the operword AND every extension word — source AND dest — and returns
            // `UnitsConsumed * UnitBytes` as r.Length (g.cs Decode, the `int len = ...` line). So r.Length is
            // ALREADY the exact next-instruction footprint for an emitted block-continuing MOVE; adding
            // M68kEmitOperandBytes would DOUBLE-count. M68kEmitOperandBytes exists only as the standalone
            // footprint oracle the FallbackEmitCount discovery unit tests assert against (it must equal r.Length).
            int length = r.Length + Z80EmitOperandBytes(d);
            // M6 PR-B (Task 3b): thread the captured segment-override prefix byte (26/2E/36/3E, 0 if none) into
            // the run tuple so the 8086 MOV emit arm can re-form the override-displaced segment (the principled
            // fix that removes the override scope cut). Non-8086 CPUs carry 0 (their X86 slot is default).
            byte x86Seg = TargetIsM8086 ? r.X86.SegOverride : (byte)0;
            run.Add((pc, d, length, x86Seg));
            if (d.EndsBlock) break;
            pc = unchecked((ushort)(pc + length));           // advance by the FULL footprint
            // M6 PR-4a: reposition at the next instruction. The byte stream supports in-place SeekTo (its IL is
            // UNCHANGED from pre-PR-4a — same instance reused, same reset); the word stream (a stateful queue
            // with no SeekTo) is re-CONSTRUCTED fresh at the new pc — its stateless decode-walk ctor re-Seeds
            // the queue from `pc`, the correct per-instruction decode start (the runtime Seed/Reseed/refill
            // machinery is irrelevant to the never-executing discovery walk). Re-constructing the BYTE stream
            // would be equivalent to SeekTo, but keeping SeekTo on the byte path leaves its IL untouched.
            if (stream is BusFetchStream bfs) bfs.SeekTo(pc);
            else stream = NewStream(pc);                     // 68000: fresh word-granular stream re-Seeded at pc
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

        var (lastPc, lastD, lastLen, _) = run[^1];
        foreach (var (pc, d, length, x86Seg) in run)
        {
            EmitInstruction(ctx, pc, d, length, x86Seg);   // M6 PR-B (Task 3b): thread the captured override byte
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
        System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D, int Length, byte X86Seg)> run)
    {
        var pages = new System.Collections.Generic.HashSet<int>();
        foreach (var (pc, d, length, _) in run)
            for (int b = 0; b < length; b++)        // the walk's COMPUTED length, not a field
                pages.Add(((pc + b) & 0xFFFF) >> 8);
        return pages;
    }

    /// <summary>Emit one instruction. NeedsFallback rows emit a callout to inner.Step() (the
    /// safety valve). Each emit arm mirrors the proven CpuEmitter body. <paramref name="length"/> is
    /// the walk's COMPUTED instruction length — threaded to the branch arm (the only arm that needs
    /// the post-operand fall-through PC) so no static descriptor Length field is read.
    /// <paramref name="x86Seg"/> (M6 PR-B / Task 3b) is the captured 8086 segment-override prefix byte
    /// (26/2E/36/3E, 0 if none / non-8086) the MOV emit arm threads into its EA segment selection.</summary>
    private void EmitInstruction(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg = 0)
    {
        if (d.NeedsFallback) { EmitFallbackStep(ctx); return; }
        // M6 PR-4a: a 68000 MOVE is BLOCK-CONTINUING (NeedsFallback=false), so Discover walks PAST it into the
        // next word. In the single-instruction TomHarte corpus that next word is the prefetch-queue LOOKAHEAD
        // (pf[1]), NOT a real instruction — and ~1-in-5 of those words classify as a MOVE-family descriptor with
        // an ARBITRARY dest EA, including the PC-relative / immediate modes (mode 7 reg 2/3/4) that are ILLEGAL
        // MOVE destinations and that EmitM68kEaWrite cannot emit. (In a real program the next word IS a real
        // instruction, but discovery can still walk into data / illegal-EA words at a block boundary.) Rather
        // than ABORT the whole block compile with the EmitM68kEaWrite throw (which kills the REAL first MOVE too),
        // fall back to inner.Step for the unhandled op — exactly as every other unemittable 68000 op does. The
        // fallback ENDS the block (EmitNormalExit), which is correct: at runtime the budget exit after the first
        // instruction means this op never executes anyway. NOT counted in M68kMoveEmitSelections (it did not emit
        // MOVE IL). MOVEQ has no dest EA (EmitM68kMoveQ handles it wholly), so it is always emittable.
        if (TargetIsM68000 && d.Class == JitOpClass.M68000Move && !CanEmitM68kMove(pc, d))
        {
            EmitFallbackStep(ctx);
            return;
        }
        // M6 PR-5: the same fallback valve for the ALU rows. The block walk can decode a LOOKAHEAD garbage word as
        // an ALU-family descriptor with an EA the emit arm cannot resolve (an EA the EA resolver does not handle for
        // the form's dest, or a contract-illegal mode). Rather than throw mid-compile (killing the real first op),
        // fall back to inner.Step for the unhandled ALU op — exactly like the MOVE valve. NOT counted in
        // M68kAluEmitSelections (it did not emit ALU IL).
        if (TargetIsM68000 && d.Class == JitOpClass.M68000Alu && !CanEmitM68kAlu(pc, d))
        {
            EmitFallbackStep(ctx);
            return;
        }
        // M6 PR-6: the shift valve — a SHIFT_MEM lookahead garbage word can decode with a non-addressable EA
        // (register-direct / PC-relative). Fall back rather than throw in EmitM68kResolveEaAddr. The register
        // shift forms have no EA matrix, so CanEmitM68kShift returns true for them (always emittable).
        if (TargetIsM68000 && d.Class == JitOpClass.M68000Shift && !CanEmitM68kShift(pc, d))
        {
            EmitFallbackStep(ctx);
            return;
        }
        // M6 PR-6: the flow valve — a JMP/JSR with an EA mode the arm does not handle falls back (Bcc/DBcc/RTS
        // and BRA/BSR via the Bcc mnemonic are always emittable). Decode the ea mode at compile time.
        if (TargetIsM68000 && d.Class == JitOpClass.M68000Flow && !CanEmitM68kFlow(pc, d))
        {
            EmitFallbackStep(ctx);
            return;
        }
        // Mirror the interpreter Step's opcode fetch EXACTLY: charge the opcode-fetch cycle FIRST
        // (the interpreter does `ReadBus(PC)` — which does `_cycles++` — then `PC++` in Step,
        // then `Execute` resolves operands). Charging the fetch cycle up-front (rather than as a
        // trailing per-arm charge) is load-bearing for Ground truth F(a): a mid-instruction MMIO
        // store must see a CycleCount that already counts the opcode fetch, so the device's view
        // is byte-identical to the interpreter's WriteBus ordering. The per-arm operand/access
        // charges follow, in order, after this.
        EmitChargeOneCycle(ctx);   // opcode-fetch cycle (was: trailing in each arm — moved up for GT-F(a))
        EmitIncrementPC(ctx, 1);
        // M6 PR-2b: a PREFIXED emitted row (the ED ADC/SBC HL,rr lane — the only emitted prefixed family)
        // fetches a SECOND opcode byte (the 0xED prefix + the 0x4A.. opcode), so it charges a second fetch
        // cycle, advances PC past the second key byte, AND bumps R a second time (the interpreter's Step
        // charges/bumps once per opcode byte). keyBytes = 2 for an emitted PrefixedOpcode row, 1 otherwise.
        // PR-1: base-plane single-byte fetch (keyBytes == 1); PR-2b: prefixed rows pass the prefix-byte count.
        // The base-plane path (every PR-1/PR-2 row) keeps keyBytes == 1, so it stays byte-identical to before.
        int keyBytes = IsZ80PrefixedEmittedRow(d) ? 2 : 1;
        if (keyBytes == 2)
        {
            EmitChargeOneCycle(ctx);   // the second (prefix) fetch cycle
            EmitIncrementPC(ctx, 1);   // consume the second key byte
        }
        // M6 PR-1: the Z80 memory-refresh (R) bump. The interpreter's Step calls OnInstructionFetched
        // once per M1 opcode fetch, which bumps R; an EMITTED Z80 instruction never runs Step, so the
        // emit path must replicate the bump itself (a fallback op keeps its own Step bump). keyBytes is the
        // opcode-byte count (1 base-plane, 2 for the PR-2b ED prefix+opcode); EmitZ80RefreshR's
        // (R + keyBytes) & 0x7F single bump is identical to the interpreter's per-byte +1 bumps (mod 128).
        if (TargetIsZ80)
            EmitZ80RefreshR(ctx, keyBytes);
        // Reset the SMC "wrote page" marker before any instruction that might write RAM, so the
        // intra-block guard only trips on this instruction's own writable-RAM store.
        // M6 PR-1: the Z80 LD store-to-memory forms LD (HL),n (StoreImm8) and LD (nn),HL (Store16) ride
        // the Register JitOpClass (not Store), so the class check alone misses them — include them so the
        // intra-block SMC guard arms for their writes (LD (HL),r / LD (nn),A already ride Store).
        // M6 PR-3: PUSH rr (Push16) rides the Register class but writes RAM (two stack pushes), so include it
        // here to ARM the intra-block SMC guard for a PUSH onto a code page. CALL/RST also push, but they END
        // the block and self-terminate via EmitChainOrExit, whose dirty.Any gate is the coarse SMC backstop for
        // their stack writes — so NO mayWriteRam entry for the flow kinds (only Push16, the block-continuing one).
        // M6 PR-4: a 68000 MOVE/MOVEA to a memory EA writes RAM, so the intra-block SMC guard must arm (a MOVE
        // onto its own code page must recompile). MOVEQ (Dn dest) and reg-dest MOVE never write RAM, but the
        // class-level gate is conservative-correct: an instruction that does NOT write RAM never trips the guard
        // (SmcPageLocal stays -1), so arming it on every M68000Move row is harmless and keeps the gate simple.
        // M6 PR-5: a memory-dest ALU (toEa RegEa, an ImmEa/QuickEa memory dest) or an ADDX/SUBX -(An) writes RAM,
        // so the intra-block SMC guard must arm. Like the MOVE clause, the class-level gate is conservative-correct:
        // a reg-dest ALU never writes RAM (SmcPageLocal stays -1), so arming it on every M68000Alu row is harmless.
        // M6 PR-6: a SHIFT_MEM memory-RMW form writes RAM, so the intra-block SMC guard must arm (M68000Shift).
        // The register shift forms never write RAM, so the class-level gate is conservative-correct (a no-store
        // op leaves SmcPageLocal at -1). M68000Flow is NOT here: it ends the block; its BSR/JSR stack push is
        // backstopped by EmitChainOrExit's dirty.Any gate (the same reasoning as the Z80 CALL/RST flow ops).
        // M6 PR-B: a memory-dest 8086 MOV (r/m,r 88/89 ; r/m,Sreg 8C ; moffs A2/A3 ; r/m,imm C6/C7) writes RAM,
        // so the intra-block SMC guard must arm. The reg-dest forms (8A/8B/8E/A0/A1/B0-BF) never write RAM, so
        // listing the memory-dest opcodes is precise and self-documenting (a non-store MOV leaves SmcPageLocal -1).
        // M6 PR-C: a memory-dest 8086 ALU (the r/m-dest forms 00/01/08/09/…, the 80/81/83 group, FE/FF INC/DEC,
        // F6/F7 NOT/NEG) writes RAM, so the intra-block SMC guard must arm. The conservative class-level gate
        // (TargetIsM8086 && ALU-mnemonic) is correct: a reg-dest ALU (and CMP/TEST, which never write back) leaves
        // SmcPageLocal at -1, so arming on every ALU row is harmless (a no-store op never trips the guard).
        bool mayWriteRam = d.Class is JitOpClass.Store or JitOpClass.Rmw
            || (TargetIsZ80 && d.Ops.Length > 0 && d.Ops[0].Kind is "StoreImm8" or "Store16" or "Push16")
            || (TargetIsM68000 && d.Class is JitOpClass.M68000Move or JitOpClass.M68000Alu or JitOpClass.M68000Shift)
            || (TargetIsM8086 && d.Mnemonic == "MOV" && d.Opcode is 0x88 or 0x89 or 0x8C or 0xA2 or 0xA3 or 0xC6 or 0xC7)
            || (TargetIsM8086 && IsM8086AluMnemonic(d.Mnemonic));
        if (mayWriteRam)
        {
            ctx.Il.Emit(OpCodes.Ldc_I4_M1);
            ctx.Il.Emit(OpCodes.Stloc, ctx.SmcPageLocal);
        }
        switch (d.Class)
        {
            case JitOpClass.Load: EmitLoad(ctx, d); break;
            case JitOpClass.Store: EmitStore(ctx, d); break;
            case JitOpClass.Register:
                if (TargetIsM8086 && d.Mnemonic == "MOV")
                {
                    M8086MovEmitSelections++;        // M6 PR-B: the dead-arm-now-live probe (asserted > 0 in the non-vacuous gate)
                    EmitM8086Mov(ctx, pc, d, length, x86Seg);   // M6 PR-B (DECISION B-1/B-2/Task 3b)
                    break;
                }
                if (TargetIsM8086 && IsM8086AluMnemonic(d.Mnemonic))
                {
                    M8086AluEmitSelections++;        // M6 PR-C: the dead-arm-now-live probe (asserted > 0 in the non-vacuous gate)
                    EmitM8086Alu(ctx, pc, d, length, x86Seg);   // M6 PR-C (DECISION C-1..C-4)
                    break;
                }
                // M6 PR-D: the NEAR control-flow family (Jcc/JMP/CALL/RET/LOOP + FF /2 /4 near indirect). The ALU
                // check ran FIRST and caught the FF /0 /1 INC/DEC rows (mnemonic "INC"/"DEC"); the flow check below
                // catches the FF "CALL"/"JMP" rows. The gate (IsEmittableX86Family) admits ONLY the near FF keys
                // (0x7FA/0x7FC), so a 0xFF "CALL"/"JMP" row reaching here is guaranteed near (far 0x7FB/0x7FD stay
                // fallback and never reach dispatch). The arm self-terminates (sets IP + EmitChainOrExit/Exit + ret).
                if (TargetIsM8086 && IsM8086FlowOpcode(d))
                {
                    M8086FlowEmitSelections++;       // M6 PR-D: the dead-arm-now-live probe (asserted > 0 in the non-vacuous gate)
                    EmitM8086Flow(ctx, pc, d, length, x86Seg);   // M6 PR-D (DECISION D-1/D-2)
                    break;
                }
                EmitRegister(ctx, d);
                break;
            case JitOpClass.Alu: EmitAlu(ctx, d); break;
            case JitOpClass.Rmw: EmitRmw(ctx, d); break;
            case JitOpClass.Branch: EmitBranch(ctx, pc, d, length); break;
            case JitOpClass.Jump: EmitJump(ctx, pc, d); break;
            case JitOpClass.Jsr: EmitJsr(ctx, pc, d); break;
            case JitOpClass.Rts: EmitRts(ctx, d); break;
            case JitOpClass.Z80Flow: EmitZ80Flow(ctx, pc, d, length); break;   // M6 PR-3 (DECISION H2)
            case JitOpClass.M68000Move:
                M68kMoveEmitSelections++;   // M6 PR-4a: the dead-arm-now-live probe (asserted > 0 in the non-vacuous gate)
                EmitM68kMove(ctx, pc, d);   // M6 PR-4 (DECISION P3)
                break;
            case JitOpClass.M68000Alu:
                M68kAluEmitSelections++;    // M6 PR-5: the dead-arm-now-live probe (asserted > 0 in the non-vacuous gate)
                EmitM68kAlu(ctx, pc, d);    // M6 PR-5 (DECISION P3)
                break;
            case JitOpClass.M68000Shift:
                M68kShiftEmitSelections++;        // M6 PR-6: the dead-arm-now-live probe
                EmitM68kShift(ctx, pc, d);        // M6 PR-6 (DECISION P3)
                break;
            case JitOpClass.M68000Flow:
                M68kFlowEmitSelections++;         // M6 PR-6: the dead-arm-now-live probe
                EmitM68kFlow(ctx, pc, d, length); // M6 PR-6 (DECISION C/D — needs length for the fall-through PC)
                break;
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

    /// <summary>M6 PR-B (DECISION B-0): the 20-bit PHYSICAL address of the code byte at 16-bit IP
    /// <paramref name="pc"/> for the block under compile: (CS&lt;&lt;4 + pc) &amp; 0xFFFFF. The emit arm reads its
    /// operword / ModR/M / disp / imm as COMPILE-TIME constants from _bus.Read8/Read16 at THIS physical address
    /// (the 68000 NextExtWord pattern, but segmented). _m8086CodePhysBase is the block's CS&lt;&lt;4 (baked in
    /// Discover). The 16-bit IP wraps within the segment BEFORE the segment add (the 8086 wrap quirk), matching
    /// AddressSpaceFetchStream.FetchAddress.</summary>
    private uint M8086CodePhys(ushort pc) => (_m8086CodePhysBase + (ushort)pc) & 0xFFFFFu;

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
        EmitMaskEaLocalToBus(ctx);             // PR-4b: clamp to the bus width before the fastmem page index (see EmitMarkWidePagesDirty)
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
        EmitMaskEaLocalToBus(ctx);               // PR-4b: clamp to the bus width before the fastmem page index (see EmitMarkWidePagesDirty)
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

    // ── M6 PR-4 (Task 2): wide big-endian bus emit helpers ───────────────────────────────────
    // The 68000 needs word/long bus access; the JIT had only byte helpers (GAP G1). These do a BUS-ONLY
    // callvirt (Ldarg_1 = the IAddressSpace bus, the same arg the byte helpers' MMIO arm uses) — NOT the
    // fastmem page-split fast path. Rationale: the interpreter itself just calls _bus.Read16/Write16 for
    // wide access (M68000Cpu.cs:185-210); the fastmem fast path is byte-keyed (256-byte pages) and a wide
    // access can straddle a page boundary, so replicating the page-split for wide access is not worth it in
    // PR-4. Each helper charges 0 cycles (DECISION C-jit: the whole-op BaseCycles is charged ONCE by the
    // MOVE arm via EmitChargeCycles; the data-axis parity gate ignores CycleCount — T2). The store helpers
    // mark the touched page(s) dirty (SMC) exactly as EmitStoreByte does so a MOVE onto its own code page
    // recompiles. Read16/Read32/Write16/Write32 are big-endian high-word-first (Core/IAddressSpace.cs),
    // matching the interpreter's ReadLongBus/WriteLongBus store order.

    /// <summary>Read a big-endian word from the bus. Stack: ..., address(uint) -> ..., value(int 0..0xFFFF).
    /// Charges 0 cycles. Stashes the address in AddrLocal (NOT EaLocal, which the byte helpers clobber).</summary>
    private void LoadWordFromBus(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // address
        il.Emit(OpCodes.Ldarg_1);                // bus (IAddressSpace)
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Callvirt, MRead16);      // bus.Read16(addr) -> ushort (pushed as int 0..0xFFFF)
    }

    /// <summary>Read a big-endian long from the bus (high word first — ReadLongBus order). Stack: ...,
    /// address(uint) -> ..., value(uint). Charges 0 cycles.</summary>
    private void LoadLongFromBus(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // address
        il.Emit(OpCodes.Ldarg_1);                // bus
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Callvirt, MRead32);      // bus.Read32(addr) -> uint
    }

    /// <summary>Write a big-endian word to the bus. Stack: ..., address(uint), value(int) -> .... Marks the
    /// spanned page(s) dirty (SMC). Charges 0 cycles.</summary>
    private void EmitStoreWord(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.DataLocal);   // value
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // address
        EmitMarkWidePagesDirty(ctx, byteSpan: 2);
        il.Emit(OpCodes.Ldarg_1);                // bus
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Callvirt, MWrite16);     // bus.Write16(addr, (ushort)value)
    }

    /// <summary>Write a big-endian long to the bus, HIGH WORD FIRST (the MOVE.l store order — WriteLongBus,
    /// M68000Cpu.cs:206-210; the RMW low-word-first WriteLongBusRmw is PR-5, not MOVE). Stack: ...,
    /// address(uint), value(uint) -> .... Marks the spanned page(s) dirty (SMC). Charges 0 cycles.</summary>
    private void EmitStoreLong(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.DataLocal);   // value (held as int; the bit pattern is the uint long)
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // address
        EmitMarkWidePagesDirty(ctx, byteSpan: 4);
        il.Emit(OpCodes.Ldarg_1);                // bus
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Callvirt, MWrite32);     // bus.Write32(addr, (uint)value) — Write32 is high-word-first
    }

    /// <summary>M6 PR-5: write a long as two .w transactions LOW WORD FIRST (the 68000 read-modify-write write-back
    /// order — WriteLongBusRmw, M68000Cpu.cs:218-222 / WriteResolvedDest, M68000Cpu.Alu.cs:199-201; the REVERSE of
    /// EmitStoreLong's high-word-first MOVE.l store). Stack: ..., address(uint), value(uint) -> .... The DATA-axis
    /// result is IDENTICAL to EmitStoreLong (same final memory); only the per-word trace order differs (which the
    /// JIT parity gate ignores — PR-4 T2). Emitted anyway for correctness-by-construction (the memory-dest ALU RMW
    /// forms use it). Mirrors EmitStoreLong's address-mask + dirty-mark exactly, then writes W(addr+2) then W(addr).</summary>
    private void EmitStoreLongRmw(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.DataLocal);   // value (held as int; the bit pattern is the uint long)
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // address
        EmitMarkWidePagesDirty(ctx, byteSpan: 4);   // also clamps AddrLocal to the bus width (mirrors EmitStoreLong)
        // low word at addr+2 FIRST: bus.Write16((addr+2) & mask, (ushort)value)
        il.Emit(OpCodes.Ldarg_1);                // bus
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)_bus.AddressMask));
        il.Emit(OpCodes.And);                    // (addr+2) & AddressMask (wrap the low word at the top of the space)
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Conv_U2);                // (ushort)value — the low 16 bits
        il.Emit(OpCodes.Callvirt, MWrite16);
        // high word at addr: bus.Write16(addr, (ushort)(value >> 16))
        il.Emit(OpCodes.Ldarg_1);                // bus
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Ldc_I4, 16);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Conv_U2);                // (ushort)(value >> 16) — the high 16 bits
        il.Emit(OpCodes.Callvirt, MWrite16);
    }

    /// <summary>M6 PR-4: mark the page(s) a wide write touches dirty (SMC) + record the SMC page for the
    /// intra-block guard. A word/long spans 2/4 bytes and may cross a 256-byte page boundary, so this marks
    /// the BASE page and, when it differs, the (base + byteSpan - 1) page. Mirrors EmitStoreByte's
    /// dirty.Mark(page) idiom (Ldarg_3 = DirtyMap, MDirtyMark). The address is read from AddrLocal (the wide
    /// store helpers stash it there). Unconditional (unlike EmitStoreByte, this does NOT gate on
    /// PageWritable): a wide MOVE to ROM/MMIO marking a spurious dirty page is harmless — the store itself is
    /// dropped/handled by the bus, and a non-code page being flagged dirty only triggers a (cheap) re-decode
    /// check, never a correctness issue. Keeping it unconditional avoids the fastmem page-class branch this
    /// bus-only path deliberately omits.</summary>
    private void EmitMarkWidePagesDirty(EmitContext ctx, int byteSpan)
    {
        ILGenerator il = ctx.Il;
        // PR-4b: clamp the address to the bus width BEFORE deriving any page index. The resolved EA can carry
        // bits above the address bus (the 68000's A-registers / displacements are full 32-bit, but the bus is
        // 24-bit), and the page derived from `addr >> 8` indexes the DirtyMap's bool[PageCount] (PageCount =
        // 2^addressBits / 256). An unmasked high address overruns that array (IndexOutOfRangeException) — the
        // interpreter never hits it because every bus access masks `address & AddressMask` first. Masking
        // AddrLocal here keeps the page in range and is a no-op for the byte CPUs (whose EA is already in range).
        EmitMaskAddrLocalToBus(ctx);
        // dirty.Mark(addr >> 8)  — the base page
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Callvirt, MDirtyMark);
        // SmcPageLocal = base page (the intra-block SMC guard reads this after the instruction)
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Stloc, ctx.SmcPageLocal);
        // If the access crosses a page boundary, also mark the END page ((base + byteSpan - 1) & mask) >> 8 when
        // it differs from the base page. Emitted as a runtime compare so an aligned access marks one page only.
        // PR-4b: the end address is re-clamped to the bus width — a wide access whose base is at the very top of the
        // space (e.g. MOVE.l to 0x00FFFFFF on the 24-bit 68000 bus) wraps the trailing bytes to low addresses
        // exactly as the interpreter does (each component Write8 masks with AddressMask), so the end page must wrap
        // to 0 rather than overrun the DirtyMap. Masking only the base (above) is NOT sufficient for this term.
        Label sameEndPage = il.DefineLabel();
        // basePage
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        // endPage = ((addr + byteSpan - 1) & AddressMask) >> 8
        EmitWideEndPage(ctx, byteSpan);
        il.Emit(OpCodes.Beq, sameEndPage);       // basePage == endPage -> nothing more to mark
        // dirty.Mark(endPage)
        il.Emit(OpCodes.Ldarg_3);
        EmitWideEndPage(ctx, byteSpan);
        il.Emit(OpCodes.Callvirt, MDirtyMark);
        il.MarkLabel(sameEndPage);
    }

    /// <summary>PR-4b: push <c>((AddrLocal + byteSpan - 1) &amp; _bus.AddressMask) &gt;&gt; 8</c> — the page index of
    /// the LAST byte a wide access touches, clamped to the bus width so a top-of-space access wraps (matching the
    /// interpreter's per-component <c>Write8</c> masking) instead of overrunning the DirtyMap. Stack: ... -> ...,
    /// endPage(int).</summary>
    private void EmitWideEndPage(EmitContext ctx, int byteSpan)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldc_I4, byteSpan - 1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)_bus.AddressMask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
    }

    /// <summary>PR-4b: <c>AddrLocal = AddrLocal &amp; _bus.AddressMask</c> — clamp the wide-store address local to
    /// the bus address width before any <c>addr &gt;&gt; 8</c> page derivation. The mask is a COMPILE-TIME constant
    /// (the concrete bus's width: 0xFFFF for the 16-bit boards, 0xFFFFF for the 8086, 0xFFFFFF for the 68000), so
    /// this is a single <c>Ldc_I4; And</c>. For the byte CPUs the resolved EA is already within the width (their EA
    /// arms mask to the address width as part of EA computation), so this And is the identity — it changes no
    /// emitted behaviour for 6502/Z80/8086, and only clamps the 68000's full-width A-register-relative addresses.</summary>
    private void EmitMaskAddrLocalToBus(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)_bus.AddressMask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);
    }

    /// <summary>PR-4b: <c>EaLocal = EaLocal &amp; _bus.AddressMask</c> — the byte-path counterpart of
    /// <see cref="EmitMaskAddrLocalToBus"/>. The byte read/store helpers index the fastmem page arrays
    /// (<c>PageBacking</c>/<c>PageOffset</c>/<c>PageWritable</c>, each sized to PageCount) and the DirtyMap with
    /// <c>ea &gt;&gt; 8</c>; a 68000 MOVE.b to/from a full-width address would overrun those arrays. Identity for the
    /// already-in-range byte CPUs (see <see cref="EmitMaskAddrLocalToBus"/>).</summary>
    private void EmitMaskEaLocalToBus(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)_bus.AddressMask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.EaLocal);
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

    // ── M6 PR-4 (Task 3): 32-bit + size-aware register emit helpers (GAP G2) ──────────────────
    // The 68000's D0-D7/A0-A6/USP/SSP/PC are uint fields (resolved into _regWide32Fields). MOVE operates on
    // the .b/.w/.l slice of a uint register, preserving the upper bits on a narrow write (the SetDataRegPartial
    // oracle, M68000Cpu.Move.cs:41-49); MOVEA writes the WHOLE An (with .w sign-extension — a distinct path
    // the caller handles, NOT a partial write).

    /// <summary>M6 PR-4: load the full 32-bit register value. Stack: ... -> ..., value(uint).</summary>
    private void EmitLoadReg32(EmitContext ctx, string name)
    {
        if (!_regWide32Fields.TryGetValue(name, out var f))
            throw new EmulationException(
                $"compiled descriptor names 32-bit register '{name}' which the CPU type does not declare as a "
              + "uint field (PR-4 32-bit register helper)");
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldfld, f);
    }

    /// <summary>M6 PR-4: store the full 32-bit register. Stack: ..., value(uint) -> .... The value arrives
    /// BELOW the receiver, so stage it through M68kStoreStageLocal (uint), push cpu, reload, Stfld.
    /// The staging local is the DEDICATED register-store stage — NOT M68kValueLocal — so a register
    /// write-back (e.g. EmitAdvanceAreg on a dest (An)+/-(An)) never clobbers a live MOVE operand parked in
    /// M68kValueLocal. (Pre-merge review HIGH finding, M6 PR-4.)</summary>
    private void EmitStoreReg32(EmitContext ctx, string name)
    {
        if (!_regWide32Fields.TryGetValue(name, out var f))
            throw new EmulationException(
                $"compiled descriptor names 32-bit register '{name}' which the CPU type does not declare as a "
              + "uint field (PR-4 32-bit register helper)");
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.M68kStoreStageLocal);   // value
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.M68kStoreStageLocal);
        il.Emit(OpCodes.Stfld, f);
    }

    /// <summary>M6 PR-4: size-aware register READ. Loads the full 32-bit register, then masks to the low
    /// byte (.b) / word (.w); .l leaves the whole value. Stack: ... -> ..., value (the masked slice as uint).
    /// size: 0=.b, 1=.w, 2=.l. Mirrors ReadEaOperand's <c>DataReg(reg) &amp; SizeMask(size)</c> for Dn
    /// (M68000Cpu.Move.cs:26); for An (mode 1) the source read is the WHOLE register, so the caller uses
    /// .l/EmitLoadReg32 for An, not this masked read.</summary>
    private void EmitLoadDataRegSized(EmitContext ctx, string name, int size)
    {
        EmitLoadReg32(ctx, name);
        if (size == 0) { ctx.Il.Emit(OpCodes.Ldc_I4, 0xFF); ctx.Il.Emit(OpCodes.And); }
        else if (size == 1) { ctx.Il.Emit(OpCodes.Ldc_I4, 0xFFFF); ctx.Il.Emit(OpCodes.And); }
        // size 2 (.l): leave the whole 32-bit value
    }

    /// <summary>M6 PR-4: size-aware data-register WRITE preserving the upper bits on .b/.w (the
    /// SetDataRegPartial oracle, M68000Cpu.Move.cs:41-49). Stack: ..., value -> ....
    ///   .b: reg = (reg &amp; ~0xFF)   | (value &amp; 0xFF);
    ///   .w: reg = (reg &amp; ~0xFFFF) | (value &amp; 0xFFFF);
    ///   .l: reg = value   (whole 32 — delegates to EmitStoreReg32).
    /// NOTE: this is the Dn-dest partial write. MOVEA's An dest is the WHOLE register with .w sign-extension
    /// — a DISTINCT path the MOVE arm emits via EmitStoreReg32 (Task 5), never through here.</summary>
    private void EmitStoreDataRegSized(EmitContext ctx, string name, int size)
    {
        if (size == 2) { EmitStoreReg32(ctx, name); return; }
        if (!_regWide32Fields.TryGetValue(name, out var f))
            throw new EmulationException(
                $"compiled descriptor names 32-bit register '{name}' which the CPU type does not declare as a "
              + "uint field (PR-4 32-bit register helper)");
        ILGenerator il = ctx.Il;
        uint mask = size == 0 ? 0xFFu : 0xFFFFu;
        il.Emit(OpCodes.Stloc, ctx.M68kStoreStageLocal);            // new value (dedicated store stage — NOT
        il.Emit(OpCodes.Ldarg_0);                                   //   M68kValueLocal, so a live MOVE operand
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, f);        //   parked there survives this write)
        il.Emit(OpCodes.Ldc_I4, unchecked((int)~mask)); il.Emit(OpCodes.And);   // old & ~mask  (keep upper bits)
        il.Emit(OpCodes.Ldloc, ctx.M68kStoreStageLocal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)mask)); il.Emit(OpCodes.And);    // value & mask
        il.Emit(OpCodes.Or);                                        // (old & ~mask) | (value & mask)
        il.Emit(OpCodes.Stfld, f);                                  // reg = merged
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
