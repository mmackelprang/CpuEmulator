using System.Reflection;
using System.Reflection.Emit;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>M6 PR-B: the 8086 MOV-family emit arm + the ModR/M effective-address resolver — the 8086's
/// analogue of the 68000's dual-EA resolver (BlockCompiler.M68000.cs). The EA matrix is decoded at EMIT
/// TIME from the ModR/M byte (a code-stream constant read via M8086CodePhys — the SEGMENTED (CS&lt;&lt;4)+IP
/// origin, DECISION B-0); the 16-bit segment-relative offset + the (seg&lt;&lt;4)+offset 20-bit physical mirror
/// ComputeX86Ea + DefaultSegmentForX86Rm + ResolveSegment + Physical one-for-one. MOV sets NO flags (PR-C
/// owns the flag core). The WORD access wraps the SECOND byte's OFFSET at 16 bits within the segment (the
/// 8086 wrap quirk, ReadEaWordWrapped) — NOT the physical, so each byte's physical re-forms from the
/// surviving (seg, offset) pair. Reused by PR-C (ALU) + PR-D (branch) for every memory operand.
///
/// <para>PREFIX / PC-ACCOUNTING crux: the run-tuple `pc` is the INSTRUCTION START (the segment-override
/// prefix byte, if any, lives there). EmitInstruction charged the first fetch + advanced PC by 1; this arm
/// advances PC by the REMAINING footprint (length - 1) so the block's nextPc matches r.Length EXACTLY for
/// every prefix combination. The emit-time const reads (ModR/M / disp / imm) start at `operandPc` = pc +
/// (prefix bytes) + 1 (opcode), scanned the same way the generated Decode walk consumes prefixes.</para>
///
/// <para>EMIT-TIME ADDRESS NOTE: M8086CodePhys returns the FULL 20-bit physical (CS&lt;&lt;4)+pc; it is passed
/// to _bus.Read8(uint) DIRECTLY (NOT truncated to ushort — that would drop the segment and read the wrong
/// flat-IP byte). The IP wrap at 16 bits is already applied INSIDE M8086CodePhys.</para></summary>
internal sealed partial class BlockCompiler<TCpu> where TCpu : class
{
    // The 8086 ModR/M register-index → name tables (a small fixed ISA fact, NOT a generator output —
    // M8086Cpu.Mov.cs:20-22). reg8 interleaves low/high halves; reg16/sreg are the standard orders.
    private static readonly string[] M8086Reg8  = ["AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH"];
    private static readonly string[] M8086Reg16 = ["AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI"];
    private static readonly string[] M8086Sreg  = ["ES", "CS", "SS", "DS"];

    // M6 PR-C: the 8086 FLAGS bit masks (M8086Cpu.Alu.cs:28-33 — the M8086Spec layout).
    private const int M8086FlagCF = 1 << 0;    // carry
    private const int M8086FlagPF = 1 << 2;    // parity (of the low 8 bits)
    private const int M8086FlagAF = 1 << 4;    // auxiliary / BCD half-carry (the spec's `H`)
    private const int M8086FlagZF = 1 << 6;    // zero
    private const int M8086FlagSF = 1 << 7;    // sign (top bit of the width)
    private const int M8086FlagOF = 1 << 11;   // signed overflow (the spec's `V`)

    // The BitOperations.PopCount(uint) handle for the parity bit (resolved once; CPU-agnostic static — the
    // 8086 PF is the even-parity of the LOW 8 bits of the result, ParityEven, M8086Cpu.Alu.cs:44).
    private static readonly MethodInfo MPopCount =
        typeof(System.Numerics.BitOperations).GetMethod("PopCount", [typeof(uint)])!;

    // The 8086 prefix bytes (segment-override 26/2E/36/3E, LOCK F0/F1, REP F2/F3) — mirrors the generated
    // walk's s_x86Prefixes (M8086Cpu.g.cs). The arm scans these at emit time to find the opcode position past
    // any prefix(es), so the const reads land on the real ModR/M / disp / imm bytes.
    private static bool M8086IsPrefixByte(byte b) =>
        b is 0x26 or 0x2E or 0x36 or 0x3E or 0xF0 or 0xF1 or 0xF2 or 0xF3;

    // ── M6 PR-C: the shared 8086 FLAGS helper family (a one-for-one IL transcription of the oracle's
    //    SetFlag/SetSzp/AddFlags/SubFlags/LogicFlags/IncDecFlags, M8086Cpu.Alu.cs:36-111). Each helper reads the
    //    operand survivor locals (M8086ALocal/M8086BLocal/M8086CarryInLocal), writes the six flag bits into the
    //    FLAGS field via a read-modify-write, and leaves the width-masked result on the stack for the arm to
    //    store (or discard, for CMP/TEST). The result also lands in M8086ResultLocal. ───────────────────────

    /// <summary>M6 PR-C: FLAGS = (FLAGS &amp; ~mask) | (cond ? mask : 0). The boolean condition is on the stack
    /// (any nonzero ⇒ set the bit). Mirrors M8086Cpu.SetFlag (Alu.cs:36).</summary>
    private void EmitM8086SetFlag(EmitContext ctx, int mask)
    {
        ILGenerator il = ctx.Il;
        // stack: cond(int)  ->  bit = (cond!=0 ? mask : 0); FLAGS = (FLAGS & ~mask) | bit
        Label setIt = il.DefineLabel(), done = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, setIt);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Br, done);
        il.MarkLabel(setIt); il.Emit(OpCodes.Ldc_I4, mask);
        il.MarkLabel(done);
        il.Emit(OpCodes.Stloc, ctx.DataLocal);                  // bit (0 or mask) — DataLocal is free here
        il.Emit(OpCodes.Ldarg_0);                               // receiver for the Stfld below
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!);
        il.Emit(OpCodes.Ldc_I4, ~mask); il.Emit(OpCodes.And);   // FLAGS & ~mask
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _m8086FLAGS!);
    }

    /// <summary>M6 PR-C: SF/ZF/PF from the result in M8086ResultLocal (SetSzp, Alu.cs:48). width16 picks the
    /// sign bit (0x8000 vs 0x80) and the zero-width mask (0xFFFF vs 0xFF). PF = even parity of the LOW 8 bits
    /// (for BOTH byte and word — ParityEven, Alu.cs:44).</summary>
    private void EmitM8086SetSzp(EmitContext ctx, bool width16)
    {
        ILGenerator il = ctx.Il;
        int signBit = width16 ? 0x8000 : 0x80;
        int widthMask = width16 ? 0xFFFF : 0xFF;
        // ZF: (result & widthMask) == 0
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Ldc_I4, widthMask); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq);       // (… == 0) ? 1 : 0
        EmitM8086SetFlag(ctx, M8086FlagZF);
        // SF: (result & signBit) != 0
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Ldc_I4, signBit); il.Emit(OpCodes.And);
        EmitM8086SetFlag(ctx, M8086FlagSF);
        // PF: (PopCount((uint)(result & 0xFF)) & 1) == 0  (even ⇒ set)
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Call, MPopCount);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq);
        EmitM8086SetFlag(ctx, M8086FlagPF);
    }

    /// <summary>M6 PR-C: ADD/ADC flag set (AddFlags, Alu.cs:62). a in M8086ALocal, ORIGINAL b in M8086BLocal,
    /// carryIn in M8086CarryInLocal (0 for ADD, FLAGS&amp;CF for ADC). full = a+b+carry (wide, so the carry-out
    /// lands above the width); result = full &amp; widthMask. Sets CF/AF/OF then SF/ZF/PF; stores result in
    /// M8086ResultLocal and leaves it on the stack. The AF/OF predicates read the ORIGINAL b (M8086BLocal),
    /// which is NEVER overwritten — `full` lives in its own M8086FullLocal.</summary>
    private void EmitM8086AddFlags(EmitContext ctx, bool width16)
    {
        ILGenerator il = ctx.Il;
        int widthMask = width16 ? 0xFFFF : 0xFF;
        int signBit = width16 ? 0x8000 : 0x80;
        // full = a + b + carryIn  -> M8086FullLocal
        il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086BLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldloc, ctx.M8086CarryInLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, ctx.M8086FullLocal);
        // result = full & widthMask  -> M8086ResultLocal
        il.Emit(OpCodes.Ldloc, ctx.M8086FullLocal); il.Emit(OpCodes.Ldc_I4, widthMask); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.M8086ResultLocal);
        // CF: (full & (widthMask+1)) != 0
        il.Emit(OpCodes.Ldloc, ctx.M8086FullLocal); il.Emit(OpCodes.Ldc_I4, widthMask + 1); il.Emit(OpCodes.And);
        EmitM8086SetFlag(ctx, M8086FlagCF);
        // AF: ((a ^ b ^ result) & 0x10) != 0   — reads the ORIGINAL b (M8086BLocal)
        il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086BLocal); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Ldc_I4, 0x10); il.Emit(OpCodes.And);
        EmitM8086SetFlag(ctx, M8086FlagAF);
        // OF (ADD form): (~(a ^ b) & (a ^ result) & signBit) != 0
        il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086BLocal); il.Emit(OpCodes.Xor); il.Emit(OpCodes.Not);
        il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Xor); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, signBit); il.Emit(OpCodes.And);
        EmitM8086SetFlag(ctx, M8086FlagOF);
        EmitM8086SetSzp(ctx, width16);
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal);           // leave result on the stack
    }

    /// <summary>M6 PR-C: SUB/SBB/CMP/NEG flag set (SubFlags, Alu.cs:77). a in M8086ALocal, ORIGINAL b in
    /// M8086BLocal, borrowIn in M8086CarryInLocal. full = a-b-borrow; result = full &amp; widthMask; CF=borrow;
    /// AF=(a^b^result)&amp;0x10; OF=((a^b)&amp;(a^result)&amp;signBit) (NO ~, the SUB form); then SetSzp. Stores result
    /// in M8086ResultLocal and leaves it on the stack.</summary>
    private void EmitM8086SubFlags(EmitContext ctx, bool width16)
    {
        ILGenerator il = ctx.Il;
        int widthMask = width16 ? 0xFFFF : 0xFF;
        int signBit = width16 ? 0x8000 : 0x80;
        // full = a - b - borrowIn  -> M8086FullLocal
        il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086BLocal); il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldloc, ctx.M8086CarryInLocal); il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, ctx.M8086FullLocal);
        // result = full & widthMask  -> M8086ResultLocal
        il.Emit(OpCodes.Ldloc, ctx.M8086FullLocal); il.Emit(OpCodes.Ldc_I4, widthMask); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.M8086ResultLocal);
        // CF (borrow): (full & (widthMask+1)) != 0
        il.Emit(OpCodes.Ldloc, ctx.M8086FullLocal); il.Emit(OpCodes.Ldc_I4, widthMask + 1); il.Emit(OpCodes.And);
        EmitM8086SetFlag(ctx, M8086FlagCF);
        // AF: ((a ^ b ^ result) & 0x10) != 0
        il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086BLocal); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Ldc_I4, 0x10); il.Emit(OpCodes.And);
        EmitM8086SetFlag(ctx, M8086FlagAF);
        // OF (SUB form): ((a ^ b) & (a ^ result) & signBit) != 0   — NO ~ (differs from ADD)
        il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086BLocal); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Xor); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, signBit); il.Emit(OpCodes.And);
        EmitM8086SetFlag(ctx, M8086FlagOF);
        EmitM8086SetSzp(ctx, width16);
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal);
    }

    /// <summary>M6 PR-C: AND/OR/XOR/TEST flag set (LogicFlags, Alu.cs:93). The result is ALREADY in
    /// M8086ResultLocal (the caller computed a&amp;b / a|b / a^b). CF=0, OF=0, AF=0; then SetSzp from the
    /// width-masked result. Leaves result on the stack.</summary>
    private void EmitM8086LogicFlags(EmitContext ctx, bool width16)
    {
        ILGenerator il = ctx.Il;
        int widthMask = width16 ? 0xFFFF : 0xFF;
        // result &= widthMask
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Ldc_I4, widthMask); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.M8086ResultLocal);
        il.Emit(OpCodes.Ldc_I4_0); EmitM8086SetFlag(ctx, M8086FlagCF);
        il.Emit(OpCodes.Ldc_I4_0); EmitM8086SetFlag(ctx, M8086FlagOF);
        il.Emit(OpCodes.Ldc_I4_0); EmitM8086SetFlag(ctx, M8086FlagAF);
        EmitM8086SetSzp(ctx, width16);
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal);
    }

    /// <summary>M6 PR-C: INC/DEC flag set (IncDecFlags, Alu.cs:105) — ADD/SUB of 1 but CF is PRESERVED. Save CF,
    /// run the add/sub-1 flag set (b=1, carry/borrow-in=0), restore CF. a in M8086ALocal. Leaves result on the
    /// stack (and in M8086ResultLocal).</summary>
    private void EmitM8086IncDecFlags(EmitContext ctx, bool decrement, bool width16)
    {
        ILGenerator il = ctx.Il;
        // savedCf = FLAGS & CF  -> M8086SavedCfLocal
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagCF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.M8086SavedCfLocal);
        // b = 1 ; carry/borrow-in = 0
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, ctx.M8086CarryInLocal);
        if (decrement) EmitM8086SubFlags(ctx, width16); else EmitM8086AddFlags(ctx, width16);
        il.Emit(OpCodes.Pop);                                   // the helper left result on the stack; it is in M8086ResultLocal
        // restore CF: FLAGS = (FLAGS & ~CF) | savedCf
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, ~M8086FlagCF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, ctx.M8086SavedCfLocal); il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _m8086FLAGS!);
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal);          // leave result on the stack
    }

    /// <summary>The override-prefix enum (mirrors M8086Cpu.X86SegmentOverride) — the compile-time-decoded
    /// segment override the arm threads into EmitM8086SegValue. None=no override.</summary>
    private enum M8086Cpu_Override { None, Es, Cs, Ss, Ds }

    /// <summary>Turn the raw segment-override prefix byte (26/2E/36/3E) the decode captured into the enum
    /// (M8086Cpu.Mov.cs:91 OverrideFromByte). 0 / any other byte ⇒ None.</summary>
    private static M8086Cpu_Override M8086OverrideFromByte(byte b) => b switch
    {
        0x26 => M8086Cpu_Override.Es,
        0x2E => M8086Cpu_Override.Cs,
        0x36 => M8086Cpu_Override.Ss,
        0x3E => M8086Cpu_Override.Ds,
        _    => M8086Cpu_Override.None,
    };

    /// <summary>The resolved segment-register NAME for an override (None ⇒ the caller's default). All inputs are
    /// compile-time constants, so the chosen segment register NAME is decided here and only the VALUE is a runtime
    /// load. Mirrors ResolveSegment (M8086Cpu.Ea.cs:58): the override REPLACES the default.</summary>
    private static string M8086SegName(M8086Cpu_Override over, string defaultSeg) => over switch
    {
        M8086Cpu_Override.Es => "ES",
        M8086Cpu_Override.Cs => "CS",
        M8086Cpu_Override.Ss => "SS",
        M8086Cpu_Override.Ds => "DS",
        _ => defaultSeg,
    };

    /// <summary>Push the 16-bit segment-relative OFFSET (as int) for a (mod, rm, disp) memory operand —
    /// ComputeX86Ea (g.cs:781). base+index per rm (runtime reg loads), + (short)disp (compile-time const),
    /// wrapped to 16 bits. The mod=00,rm=110 disp16-direct exception zeroes the base (offset = disp).</summary>
    private void EmitM8086EaOffset(EmitContext ctx, uint mod, uint rm, ushort disp)
    {
        ILGenerator il = ctx.Il;
        bool disp16Direct = mod == 0u && rm == 6u;
        if (disp16Direct)
        {
            il.Emit(OpCodes.Ldc_I4, (int)disp);                    // offset = disp (no base/index)
            il.Emit(OpCodes.Conv_U2);
            return;
        }
        // base+index sum (each term a runtime 16-bit register load, added as ints):
        switch (rm)
        {
            case 0u: EmitLoadReg16(ctx, "BX"); EmitLoadReg16(ctx, "SI"); il.Emit(OpCodes.Add); break; // BX+SI
            case 1u: EmitLoadReg16(ctx, "BX"); EmitLoadReg16(ctx, "DI"); il.Emit(OpCodes.Add); break; // BX+DI
            case 2u: EmitLoadReg16(ctx, "BP"); EmitLoadReg16(ctx, "SI"); il.Emit(OpCodes.Add); break; // BP+SI
            case 3u: EmitLoadReg16(ctx, "BP"); EmitLoadReg16(ctx, "DI"); il.Emit(OpCodes.Add); break; // BP+DI
            case 4u: EmitLoadReg16(ctx, "SI"); break;                                                 // SI
            case 5u: EmitLoadReg16(ctx, "DI"); break;                                                 // DI
            case 6u: EmitLoadReg16(ctx, "BP"); break;                                                 // BP (not disp16Direct here)
            default: EmitLoadReg16(ctx, "BX"); break;                                                 // BX
        }
        // + (short)disp, then wrap to 16 bits (the offset within the 64 KB segment).
        il.Emit(OpCodes.Ldc_I4, unchecked((int)(short)disp));      // sign-extended disp (int overload)
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2);                                  // (ushort)(base + index + disp)
    }

    /// <summary>Push the SEGMENT VALUE (as int, 0..0xFFFF) for a (mod, rm) memory operand, threaded with the
    /// override — DefaultSegmentForX86Rm (g.cs:805) + ResolveSegment (Ea.cs:58). The default is SS for BP-based
    /// forms (rm ∈ {2,3,6} except the disp16-direct rm=6,mod=0), DS otherwise; an override REPLACES it.</summary>
    private void EmitM8086SegValue(EmitContext ctx, uint mod, uint rm, M8086Cpu_Override over)
    {
        bool disp16Direct = mod == 0u && rm == 6u;
        bool bpBased = !disp16Direct && (rm == 2u || rm == 3u || rm == 6u);
        string defaultSeg = bpBased ? "SS" : "DS";
        EmitLoadReg16(ctx, M8086SegName(over, defaultSeg));   // the resolved segment register VALUE (runtime load)
    }

    /// <summary>Push the 20-bit PHYSICAL address (as uint) for a (seg-value, offset) pair: (seg &lt;&lt; 4 + offset)
    /// &amp; 0xFFFFF — Physical (Ea.cs:48). Stack on entry: ..., segValue(int), offset(int); on exit: ..., phys(uint).</summary>
    private void EmitM8086PhysicalFromSegOffset(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        // stack: segValue, offset  ->  ((segValue << 4) + offset) & 0xFFFFF
        il.Emit(OpCodes.Stloc, ctx.DataLocal);     // offset (reuse DataLocal as scratch; clobbered next anyway)
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Shl);                       // segValue << 4
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Add);                       // + offset
        il.Emit(OpCodes.Ldc_I4, 0xFFFFF);
        il.Emit(OpCodes.And);                       // & 20-bit mask
    }

    /// <summary>M6 PR-B: emit one 8086 MOV-family instruction (DECISION B-1/B-2). Reached only when
    /// TargetIsM8086 &amp;&amp; d.Mnemonic == "MOV". Decodes the ModR/M / disp / imm at emit time from the SEGMENTED
    /// code stream (M8086CodePhys), resolves the EA via the Task-1 helpers, and transcribes the matching
    /// MovExecute case one-for-one. The default throws (the gate↔arm lockstep tripwire). MOV sets no flags.
    /// <paramref name="length"/> is the FULL instruction footprint (incl. any prefix); PC advances by length-1
    /// (EmitInstruction already advanced by 1). <paramref name="x86Seg"/> is the captured override prefix byte.</summary>
    private void EmitM8086Mov(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg)
    {
        ILGenerator il = ctx.Il;
        byte opcode = (byte)d.Opcode;
        uint key = d.Opcode;   // the plain opcode byte; C6/C7 group rows carry Opcode 0xC6/0xC7 (key normalized below)
        M8086Cpu_Override over = M8086OverrideFromByte(x86Seg);

        // The run-tuple `pc` is the INSTRUCTION START. Scan past any prefix byte(s) at the SEGMENTED physical to
        // find the opcode position; the const reads (ModR/M / disp / imm) start at operandPc = (opcode pos)+1.
        // (The generated walk consumes the same prefixes into r.Length, so this aligns the emit-time reads.)
        int opcodePc = pc;
        while (M8086IsPrefixByte(_bus.Read8(M8086CodePhys((ushort)opcodePc)))) opcodePc++;
        int operandPc = opcodePc + 1;   // the byte AFTER the opcode (ModR/M for 88-8E/C6/C7; imm/moffs for A0-BF)

        bool hasModRm = opcode is 0x88 or 0x89 or 0x8A or 0x8B or 0x8C or 0x8E or 0xC6 or 0xC7;
        uint mod = 0, reg = 0, rm = 0; ushort disp = 0;
        if (hasModRm)
        {
            byte modrm = _bus.Read8(M8086CodePhys((ushort)operandPc)); operandPc++;
            mod = (uint)(modrm >> 6) & 3u;
            reg = (uint)(modrm >> 3) & 7u;
            rm  = (uint)modrm & 7u;
            // the disp length per (mod, rm) — the real 8086 table (g.cs:1781): mod=0,rm=6 ⇒ 2; mod=1 ⇒ 1; mod=2 ⇒ 2.
            int dispLen = mod switch { 0u => rm == 6u ? 2 : 0, 1u => 1, 2u => 2, _ => 0 };
            if (dispLen == 1) { disp = unchecked((ushort)(sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc))); operandPc++; }
            else if (dispLen == 2)
            {
                byte lo = _bus.Read8(M8086CodePhys((ushort)operandPc));
                byte hi = _bus.Read8(M8086CodePhys((ushort)(operandPc + 1)));
                disp = (ushort)(lo | (hi << 8)); operandPc += 2;
            }
        }

        // Normalize the C6/C7 group keys to 0x630/0x638 (the interpreter does the same, Mov.cs:149-150). On the
        // 8086 the reg field of C6/C7 is a don't-care (always MOV r/m,imm).
        if (opcode == 0xC6) key = 0x630u;
        else if (opcode == 0xC7) key = 0x638u;

        switch (key)
        {
            case 0x88u:   // MOV r/m8, r8 — store reg8 to the byte rm
                if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[reg])); il.Emit(OpCodes.Stfld, RegField(M8086Reg8[rm])); }
                else EmitM8086StoreByteEa(ctx, mod, rm, disp, over, () => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[reg])); });
                break;

            case 0x8Au:   // MOV r8, r/m8 — load the byte rm into reg8
                il.Emit(OpCodes.Ldarg_0);
                if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[rm])); }
                else EmitM8086LoadByteEa(ctx, mod, rm, disp, over);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(M8086Reg8[reg]));
                break;

            case 0x89u:   // MOV r/m16, r16
                if (mod == 3u) { EmitLoadReg16(ctx, M8086Reg16[reg]); EmitStoreReg16(ctx, M8086Reg16[rm]); }
                else EmitM8086StoreWordEa(ctx, mod, rm, disp, over, () => EmitLoadReg16(ctx, M8086Reg16[reg]));
                break;

            case 0x8Bu:   // MOV r16, r/m16
                if (mod == 3u) { EmitLoadReg16(ctx, M8086Reg16[rm]); EmitStoreReg16(ctx, M8086Reg16[reg]); }
                else { EmitM8086LoadWordEa(ctx, mod, rm, disp, over); EmitStoreReg16(ctx, M8086Reg16[reg]); }
                break;

            case 0x8Cu:   // MOV r/m16, Sreg — store segment register to the word rm
                if (mod == 3u) { EmitLoadReg16(ctx, M8086Sreg[reg & 3u]); EmitStoreReg16(ctx, M8086Reg16[rm]); }
                else EmitM8086StoreWordEa(ctx, mod, rm, disp, over, () => EmitLoadReg16(ctx, M8086Sreg[reg & 3u]));
                break;

            case 0x8Eu:   // MOV Sreg, r/m16 — load the word rm into the segment register
                if (mod == 3u) { EmitLoadReg16(ctx, M8086Reg16[rm]); EmitStoreReg16(ctx, M8086Sreg[reg & 3u]); }
                else { EmitM8086LoadWordEa(ctx, mod, rm, disp, over); EmitStoreReg16(ctx, M8086Sreg[reg & 3u]); }
                break;

            // A0-A3: accumulator-direct (moffs). The disp16 rides the IMMEDIATE slot (no ModR/M); default seg DS,
            // override-replaced (the interpreter resolves through ResolveSegment(DS, over) — Mov.cs:206/212/218/224).
            case 0xA0u: case 0xA1u: case 0xA2u: case 0xA3u:
            {
                ushort moffs = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                string seg = M8086SegName(over, "DS");   // DS default, override-replaced (matches the oracle)
                if (opcode == 0xA0u)   // MOV AL, moffs8
                {
                    il.Emit(OpCodes.Ldarg_0);
                    EmitM8086LoadByteAtSegOff(ctx, seg, moffs);
                    il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stfld, RegField("AL"));
                }
                else if (opcode == 0xA1u)   // MOV AX, moffs16
                {
                    EmitM8086LoadWordAtSegOff(ctx, seg, moffs);
                    EmitStoreReg16(ctx, "AX");
                }
                else if (opcode == 0xA2u)   // MOV moffs8, AL
                    EmitM8086StoreByteAtSegOff(ctx, seg, moffs, () => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("AL")); });
                else                          // 0xA3 MOV moffs16, AX
                    EmitM8086StoreWordAtSegOff(ctx, seg, moffs, () => EmitLoadReg16(ctx, "AX"));
                break;
            }

            // B0-B7 MOV r8, imm8 ; B8-BF MOV r16, imm16. reg = opcode & 7; the imm rides the immediate slot.
            case 0xB0u: case 0xB1u: case 0xB2u: case 0xB3u: case 0xB4u: case 0xB5u: case 0xB6u: case 0xB7u:
            {
                byte imm8 = _bus.Read8(M8086CodePhys((ushort)operandPc));
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)imm8); il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(M8086Reg8[opcode & 7u]));
                break;
            }
            case 0xB8u: case 0xB9u: case 0xBAu: case 0xBBu: case 0xBCu: case 0xBDu: case 0xBEu: case 0xBFu:
            {
                ushort imm16 = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                il.Emit(OpCodes.Ldc_I4, (int)imm16); EmitStoreReg16(ctx, M8086Reg16[opcode & 7u]);
                break;
            }

            // C6/C7 reg=0: MOV r/m, imm. The imm follows the ModR/M + disp (operandPc already past them).
            case 0x630u:   // MOV r/m8, imm8
            {
                byte imm8 = _bus.Read8(M8086CodePhys((ushort)operandPc));
                if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)imm8); il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stfld, RegField(M8086Reg8[rm])); }
                else EmitM8086StoreByteEa(ctx, mod, rm, disp, over, () => { il.Emit(OpCodes.Ldc_I4, (int)imm8); });
                break;
            }
            case 0x638u:   // MOV r/m16, imm16
            {
                ushort imm16 = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                if (mod == 3u) { il.Emit(OpCodes.Ldc_I4, (int)imm16); EmitStoreReg16(ctx, M8086Reg16[rm]); }
                else EmitM8086StoreWordEa(ctx, mod, rm, disp, over, () => il.Emit(OpCodes.Ldc_I4, (int)imm16));
                break;
            }

            default:
                throw new EmulationException(
                    $"BlockCompiler: no 8086 MOV emit branch for key 0x{key:X} (opcode 0x{opcode:X2}); "
                  + "the gate (IsEmittableX86Family) admitted a form the arm does not handle — a lockstep bug.");
        }

        // PC advance: EmitInstruction already advanced PC by 1 (the first/opcode-fetch byte). Advance by the
        // REMAINING footprint (length - 1 = prefix tail + ModR/M + disp + imm) so the block's nextPc == r.Length
        // EXACTLY, for any prefix combination. (length is the walk's full per-instruction stride.)
        int tail = length - 1;
        if (tail > 0) EmitIncrementPC(ctx, tail);
    }

    /// <summary>Push the byte at the (mod, rm, disp, over) memory EA (as int) — resolve the 20-bit physical,
    /// then LoadByteFromBus (the fastmem split; charges 1 cycle). Used by the byte LOAD forms.</summary>
    private void EmitM8086LoadByteEa(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        EmitM8086SegValue(ctx, mod, rm, over);
        EmitM8086EaOffset(ctx, mod, rm, disp);
        EmitM8086PhysicalFromSegOffset(ctx);   // -> phys (uint)
        LoadByteFromBus(ctx);                   // pops addr, pushes byte; charges 1
    }

    /// <summary>Store the byte produced by <paramref name="pushValue"/> to the (mod, rm, disp, over) memory EA —
    /// resolve the physical, push value, EmitStoreByte (fastmem split; charges 1; marks dirty). pushValue leaves a
    /// byte (int) on the stack.</summary>
    private void EmitM8086StoreByteEa(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over, System.Action pushValue)
    {
        EmitM8086SegValue(ctx, mod, rm, over);
        EmitM8086EaOffset(ctx, mod, rm, disp);
        EmitM8086PhysicalFromSegOffset(ctx);   // -> phys (uint) [address]
        pushValue();                            // -> value (int)  [stack: address, value]
        EmitStoreByte(ctx);
    }

    /// <summary>Push the WORD at the (mod, rm, disp, over) memory EA (as int) — the offset-wrap form: stash the
    /// resolved (seg, offset), read the low byte at (seg, offset), the high byte at (seg, (offset+1)&amp;0xFFFF),
    /// compose lo | hi&lt;&lt;8. Each byte's physical re-forms from the survivors (ReadEaWordWrapped, Mov.cs:116).</summary>
    private void EmitM8086LoadWordEa(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        // resolve (seg value, offset) into the survivor locals.
        EmitM8086SegValue(ctx, mod, rm, over); il.Emit(OpCodes.Stloc, ctx.M8086SegLocal);
        EmitM8086EaOffset(ctx, mod, rm, disp); il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
        // lo = byte at Physical(seg, offset)
        EmitM8086PushPhysical(ctx, offsetPlusOne: false); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.DataLocal);
        // hi = byte at Physical(seg, (offset+1)&0xFFFF)
        EmitM8086PushPhysical(ctx, offsetPlusOne: true); LoadByteFromBus(ctx);
        il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Or);   // hi<<8 | lo
    }

    /// <summary>Store the word produced by <paramref name="pushValue"/> to the (mod, rm, disp, over) memory EA
    /// — offset-wrap: low byte at (seg, offset), high byte at (seg, (offset+1)&amp;0xFFFF) (WriteEaWordWrapped,
    /// Mov.cs:125). pushValue leaves a word (int) on the stack.</summary>
    private void EmitM8086StoreWordEa(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over, System.Action pushValue)
    {
        ILGenerator il = ctx.Il;
        EmitM8086SegValue(ctx, mod, rm, over); il.Emit(OpCodes.Stloc, ctx.M8086SegLocal);
        EmitM8086EaOffset(ctx, mod, rm, disp); il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
        // Stage the WORD value in AddrLocal (uint) — EmitStoreByte clobbers DataLocal + EaLocal but NEVER AddrLocal
        // (the Z80/68000 survivor discipline), so the high-byte write below can still read the full word.
        pushValue(); il.Emit(OpCodes.Stloc, ctx.AddrLocal);           // value -> AddrLocal (survivor)
        // write lo: Physical(seg, offset), (byte)value
        EmitM8086PushPhysical(ctx, offsetPlusOne: false);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Conv_U1); EmitStoreByte(ctx);
        // write hi: Physical(seg, (offset+1)&0xFFFF), (byte)(value>>8) — re-reads the SURVIVING word from AddrLocal.
        EmitM8086PushPhysical(ctx, offsetPlusOne: true);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Conv_U1);
        EmitStoreByte(ctx);
    }

    /// <summary>Push Physical(M8086SegLocal, M8086OffsetLocal [+1 wrapped]) — ((seg&lt;&lt;4) + ((offset[+1])&amp;0xFFFF))
    /// &amp; 0xFFFFF — from the survivor locals. The +1 wraps the OFFSET at 16 bits (the segment-relative wrap).</summary>
    private void EmitM8086PushPhysical(EmitContext ctx, bool offsetPlusOne)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, ctx.M8086SegLocal); il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Shl);   // seg<<4
        il.Emit(OpCodes.Ldloc, ctx.M8086OffsetLocal);
        if (offsetPlusOne) { il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.And); }
        il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4, 0xFFFFF); il.Emit(OpCodes.And);
    }

    // The moffs (A0-A3) byte/word forms reuse the survivor-pair machinery with a CONSTANT segment-register name
    // (DS default, override-threaded) and a CONSTANT offset (the moffs16). Thin wrappers over the above:
    private void EmitM8086LoadByteAtSegOff(EmitContext ctx, string seg, ushort offset)
    { EmitLoadReg16(ctx, seg); ctx.Il.Emit(OpCodes.Ldc_I4, (int)offset); EmitM8086PhysicalFromSegOffset(ctx); LoadByteFromBus(ctx); }
    private void EmitM8086StoreByteAtSegOff(EmitContext ctx, string seg, ushort offset, System.Action pushValue)
    { EmitLoadReg16(ctx, seg); ctx.Il.Emit(OpCodes.Ldc_I4, (int)offset); EmitM8086PhysicalFromSegOffset(ctx); pushValue(); EmitStoreByte(ctx); }
    private void EmitM8086LoadWordAtSegOff(EmitContext ctx, string seg, ushort offset)
    { EmitLoadReg16(ctx, seg); ctx.Il.Emit(OpCodes.Stloc, ctx.M8086SegLocal); ctx.Il.Emit(OpCodes.Ldc_I4, (int)offset); ctx.Il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
      EmitM8086PushPhysical(ctx, false); LoadByteFromBus(ctx); ctx.Il.Emit(OpCodes.Stloc, ctx.DataLocal);
      EmitM8086PushPhysical(ctx, true); LoadByteFromBus(ctx); ctx.Il.Emit(OpCodes.Ldc_I4_8); ctx.Il.Emit(OpCodes.Shl); ctx.Il.Emit(OpCodes.Ldloc, ctx.DataLocal); ctx.Il.Emit(OpCodes.Or); }
    private void EmitM8086StoreWordAtSegOff(EmitContext ctx, string seg, ushort offset, System.Action pushValue)
    { EmitLoadReg16(ctx, seg); ctx.Il.Emit(OpCodes.Stloc, ctx.M8086SegLocal); ctx.Il.Emit(OpCodes.Ldc_I4, (int)offset); ctx.Il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
      pushValue(); ctx.Il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // word value -> AddrLocal (survives EmitStoreByte)
      EmitM8086PushPhysical(ctx, false); ctx.Il.Emit(OpCodes.Ldloc, ctx.AddrLocal); ctx.Il.Emit(OpCodes.Conv_U1); EmitStoreByte(ctx);
      EmitM8086PushPhysical(ctx, true); ctx.Il.Emit(OpCodes.Ldloc, ctx.AddrLocal); ctx.Il.Emit(OpCodes.Ldc_I4_8); ctx.Il.Emit(OpCodes.Shr_Un); ctx.Il.Emit(OpCodes.Conv_U1); EmitStoreByte(ctx); }

    // ── M6 PR-C: the 8086 integer-ALU emit arm. ────────────────────────────────────────────────────────────

    /// <summary>The eight ALU operations (mirrors M8086Cpu.AluOp, Alu.cs:192). The flag helper chosen per kind:
    /// Add/Adc → EmitM8086AddFlags; Sub/Sbb/Cmp → EmitM8086SubFlags; And/Or/Xor → EmitM8086LogicFlags.</summary>
    private enum M8086AluKind { Add, Adc, Sub, Sbb, Cmp, And, Or, Xor }

    /// <summary>Map the 80/81/83 + F6/F7 group's ModR/M reg field (the normalized key's low 3 bits) to the op
    /// (AluGroupImm, Alu.cs:291-295): 0=ADD 1=OR 2=ADC 3=SBB 4=AND 5=SUB 6=XOR 7=CMP.</summary>
    private static M8086AluKind M8086GroupOp(uint reg) => reg switch
    {
        0u => M8086AluKind.Add, 1u => M8086AluKind.Or, 2u => M8086AluKind.Adc, 3u => M8086AluKind.Sbb,
        4u => M8086AluKind.And, 5u => M8086AluKind.Sub, 6u => M8086AluKind.Xor, _ => M8086AluKind.Cmp,
    };

    /// <summary>Stage the carry/borrow-in (M8086CarryInLocal) then call the kind's flag helper. a is in
    /// M8086ALocal, ORIGINAL b in M8086BLocal. For ADC/SBB the carry-in is FLAGS&amp;CF (0 or 1) read BEFORE the
    /// helper overwrites CF (DECISION C-3); for ADD/SUB/CMP it is 0. AND/OR/XOR precompute a OP b into
    /// M8086ResultLocal first (LogicFlags reads it). Leaves the result on the stack (and in M8086ResultLocal).</summary>
    private void EmitM8086Compute(EmitContext ctx, M8086AluKind kind, bool width16)
    {
        ILGenerator il = ctx.Il;
        switch (kind)
        {
            case M8086AluKind.Add:
                il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, ctx.M8086CarryInLocal);
                EmitM8086AddFlags(ctx, width16);
                break;
            case M8086AluKind.Adc:
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagCF); il.Emit(OpCodes.And);
                il.Emit(OpCodes.Stloc, ctx.M8086CarryInLocal);
                EmitM8086AddFlags(ctx, width16);
                break;
            case M8086AluKind.Sub:
            case M8086AluKind.Cmp:
                il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, ctx.M8086CarryInLocal);
                EmitM8086SubFlags(ctx, width16);
                break;
            case M8086AluKind.Sbb:
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagCF); il.Emit(OpCodes.And);
                il.Emit(OpCodes.Stloc, ctx.M8086CarryInLocal);
                EmitM8086SubFlags(ctx, width16);
                break;
            default:   // And / Or / Xor — compute a OP b into M8086ResultLocal, then LogicFlags
                il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Ldloc, ctx.M8086BLocal);
                il.Emit(kind == M8086AluKind.And ? OpCodes.And : kind == M8086AluKind.Or ? OpCodes.Or : OpCodes.Xor);
                il.Emit(OpCodes.Stloc, ctx.M8086ResultLocal);
                EmitM8086LogicFlags(ctx, width16);
                break;
        }
    }

    /// <summary>M6 PR-C: emit one 8086 ALU-family instruction (DECISION C-1..C-4). Reached only when
    /// TargetIsM8086 &amp;&amp; d.Mnemonic is an in-scope ALU mnemonic. Decodes the ModR/M / disp / imm at emit time
    /// from the SEGMENTED code stream (M8086CodePhys), reconstructs the interpreter's normalized OperationKey,
    /// and transcribes the matching AluExecute case one-for-one. The default throws (the gate↔arm lockstep
    /// tripwire). <paramref name="length"/> is the FULL instruction footprint (incl. any prefix); PC advances by
    /// length-1 (EmitInstruction already advanced by 1, like EmitM8086Mov). <paramref name="x86Seg"/> is the
    /// captured override prefix byte.</summary>
    private void EmitM8086Alu(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg)
    {
        M8086Cpu_Override over = M8086OverrideFromByte(x86Seg);

        // Scan past any prefix byte(s) at the SEGMENTED physical to find the opcode position; the const reads
        // (ModR/M / disp / imm) start at operandPc = (opcode pos)+1 (the EmitM8086Mov decode preamble verbatim).
        int opcodePc = pc;
        while (M8086IsPrefixByte(_bus.Read8(M8086CodePhys((ushort)opcodePc)))) opcodePc++;
        byte opcode = _bus.Read8(M8086CodePhys((ushort)opcodePc));
        int operandPc = opcodePc + 1;

        // Does this opcode carry a ModR/M? The AluStd forms 0-3 (the 0x00-0x3D family rows with opcode&7 <= 3),
        // TEST 84/85, and EVERY group opcode (80/81/83/F6/F7/FE/FF) do; the acc-imm forms (xx04/xx05, A8/A9) and
        // INC/DEC r16 (40-4F) do NOT. (The eight ALU families are base+{0..5}; base+{6,7} are non-ALU rows the
        // mnemonic gate already excludes — so opcode&7 <= 5 IS the AluStd membership, and <= 3 its ModR/M forms.)
        bool isGroup = opcode is 0x80 or 0x81 or 0x83 or 0xF6 or 0xF7 or 0xFE or 0xFF;
        bool isStdModRm = opcode <= 0x3D && (opcode & 7u) <= 3u;   // AluStd forms 0-3 (r/m,reg + reg,r/m)
        bool hasModRm = isGroup || isStdModRm || opcode is 0x84 or 0x85;

        uint mod = 0, reg = 0, rm = 0; ushort disp = 0;
        if (hasModRm)
        {
            byte modrm = _bus.Read8(M8086CodePhys((ushort)operandPc)); operandPc++;
            mod = (uint)(modrm >> 6) & 3u;
            reg = (uint)(modrm >> 3) & 7u;
            rm  = (uint)modrm & 7u;
            int dispLen = mod switch { 0u => rm == 6u ? 2 : 0, 1u => 1, 2u => 2, _ => 0 };
            if (dispLen == 1) { disp = unchecked((ushort)(sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc))); operandPc++; }
            else if (dispLen == 2)
            {
                byte lo = _bus.Read8(M8086CodePhys((ushort)operandPc));
                byte hi = _bus.Read8(M8086CodePhys((ushort)(operandPc + 1)));
                disp = (ushort)(lo | (hi << 8)); operandPc += 2;
            }
        }

        // Reconstruct the interpreter's normalized OperationKey: group opcodes → (opcode<<3)|reg; plain → opcode.
        uint key = isGroup ? ((uint)opcode << 3) | reg : opcode;

        switch (key)
        {
            // 00-3D the eight ALU families (AluStd). baseOp = the family base; form = key - baseOp.
            case >= 0x00 and <= 0x05: EmitM8086AluStd(ctx, 0x00, key, M8086AluKind.Add, mod, reg, rm, disp, over, operandPc); break;
            case >= 0x08 and <= 0x0D: EmitM8086AluStd(ctx, 0x08, key, M8086AluKind.Or,  mod, reg, rm, disp, over, operandPc); break;
            case >= 0x10 and <= 0x15: EmitM8086AluStd(ctx, 0x10, key, M8086AluKind.Adc, mod, reg, rm, disp, over, operandPc); break;
            case >= 0x18 and <= 0x1D: EmitM8086AluStd(ctx, 0x18, key, M8086AluKind.Sbb, mod, reg, rm, disp, over, operandPc); break;
            case >= 0x20 and <= 0x25: EmitM8086AluStd(ctx, 0x20, key, M8086AluKind.And, mod, reg, rm, disp, over, operandPc); break;
            case >= 0x28 and <= 0x2D: EmitM8086AluStd(ctx, 0x28, key, M8086AluKind.Sub, mod, reg, rm, disp, over, operandPc); break;
            case >= 0x30 and <= 0x35: EmitM8086AluStd(ctx, 0x30, key, M8086AluKind.Xor, mod, reg, rm, disp, over, operandPc); break;
            case >= 0x38 and <= 0x3D: EmitM8086AluStd(ctx, 0x38, key, M8086AluKind.Cmp, mod, reg, rm, disp, over, operandPc); break;

            // 84/85 TEST r/m,reg ; A8/A9 TEST acc,imm.
            case 0x84: EmitM8086TestRm(ctx, false, mod, reg, rm, disp, over); break;
            case 0x85: EmitM8086TestRm(ctx, true,  mod, reg, rm, disp, over); break;
            case 0xA8: EmitM8086TestAccImm(ctx, false, _bus.Read8(M8086CodePhys((ushort)operandPc))); break;
            case 0xA9: EmitM8086TestAccImm(ctx, true,  (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                                                | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8))); break;

            // 40-47 INC r16 ; 48-4F DEC r16 (CF preserved).
            case >= 0x40 and <= 0x47: EmitM8086IncDecReg16(ctx, key & 7u, decrement: false); break;
            case >= 0x48 and <= 0x4F: EmitM8086IncDecReg16(ctx, key & 7u, decrement: true);  break;

            // 80/81/83 group: ALU r/m,imm (keys 0x400-0x41F; reg selects the op; 0x83 sign-extends imm8→16).
            case >= 0x400 and <= 0x407: EmitM8086AluGroupImm(ctx, false, false, mod, reg, rm, disp, over, operandPc); break; // 0x80 r/m8,imm8
            case >= 0x408 and <= 0x40F: EmitM8086AluGroupImm(ctx, true,  false, mod, reg, rm, disp, over, operandPc); break; // 0x81 r/m16,imm16
            case >= 0x418 and <= 0x41F: EmitM8086AluGroupImm(ctx, true,  true,  mod, reg, rm, disp, over, operandPc); break; // 0x83 r/m16,imm8 SX

            // FE/FF /0 /1: INC/DEC r/m (CF preserved). Keys 0x7F0/0x7F1 (r/m8), 0x7F8/0x7F9 (r/m16).
            case 0x7F0: EmitM8086IncDecRm(ctx, false, false, mod, rm, disp, over); break;
            case 0x7F1: EmitM8086IncDecRm(ctx, false, true,  mod, rm, disp, over); break;
            case 0x7F8: EmitM8086IncDecRm(ctx, true,  false, mod, rm, disp, over); break;
            case 0x7F9: EmitM8086IncDecRm(ctx, true,  true,  mod, rm, disp, over); break;

            // F6/F7 /0 /1 TEST imm ; /2 NOT (no flags) ; /3 NEG (SUB-form flags). /4../7 (MUL/DIV) stay fallback.
            case 0x7B0: case 0x7B1: EmitM8086UnaryTestImm(ctx, false, mod, rm, disp, over, _bus.Read8(M8086CodePhys((ushort)operandPc))); break;
            case 0x7B8: case 0x7B9: EmitM8086UnaryTestImm(ctx, true,  mod, rm, disp, over,
                                        (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                                 | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8))); break;
            case 0x7B2: EmitM8086UnaryNot(ctx, false, mod, rm, disp, over); break;
            case 0x7BA: EmitM8086UnaryNot(ctx, true,  mod, rm, disp, over); break;
            case 0x7B3: EmitM8086UnaryNeg(ctx, false, mod, rm, disp, over); break;
            case 0x7BB: EmitM8086UnaryNeg(ctx, true,  mod, rm, disp, over); break;

            default:
                throw new EmulationException(
                    $"BlockCompiler: no 8086 ALU emit branch for key 0x{key:X} (opcode 0x{opcode:X2}); "
                  + "the gate (IsEmittableX86Family) admitted a form the arm does not handle — a lockstep bug.");
        }

        // PC advance: EmitInstruction already advanced PC by 1 (the first/opcode-fetch byte). Advance by the
        // REMAINING footprint (length - 1) so the block's nextPc == r.Length exactly, for any prefix combination
        // (the EmitM8086Mov discipline — the length-1 form is proven correct in PR-B).
        int tail = length - 1;
        if (tail > 0) EmitIncrementPC(ctx, tail);
    }

    /// <summary>M6 PR-C: the 00-3D standard ALU forms (AluStd, Alu.cs:238). form = key - baseOp selects the
    /// operand layout (0=r/m8&lt;-r8 ; 1=r/m16&lt;-r16 ; 2=r8&lt;-r/m8 ; 3=r16&lt;-r/m16 ; 4=AL,imm8 ; 5=AX,imm16).
    /// Reads a + b into the survivor locals, computes via the kind's flag helper, writes the result (unless CMP).
    /// The memory r/m forms 0/1 read `a` from the EA and write `result` back to the SAME EA — re-resolving the
    /// EA is correct (the 8086 r/m EA has NO auto-increment side effect, so re-forming the physical is exact).</summary>
    private void EmitM8086AluStd(EmitContext ctx, uint baseOp, uint key, M8086AluKind kind,
        uint mod, uint reg, uint rm, ushort disp, M8086Cpu_Override over, int operandPc)
    {
        ILGenerator il = ctx.Il;
        uint form = key - baseOp;
        bool width16 = form is 1u or 3u or 5u;

        switch (form)
        {
            case 0u:   // r/m8, r8 — dest = r/m (a + write-back), src = reg8
                EmitM8086LoadRmByteToA(ctx, mod, rm, disp, over);                  // a = r/m8 -> M8086ALocal
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[reg])); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = reg8
                EmitM8086Compute(ctx, kind, false);
                EmitM8086WriteRmByteResult(ctx, kind, mod, rm, disp, over);
                break;
            case 1u:   // r/m16, r16
                EmitM8086LoadRmWordToA(ctx, mod, rm, disp, over);                  // a = r/m16
                EmitLoadReg16(ctx, M8086Reg16[reg]); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = reg16
                EmitM8086Compute(ctx, kind, true);
                EmitM8086WriteRmWordResult(ctx, kind, mod, rm, disp, over);
                break;
            case 2u:   // r8, r/m8 — dest = reg8, src = r/m8
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[reg])); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);   // a = reg8
                EmitM8086LoadRmByteToB(ctx, mod, rm, disp, over);                  // b = r/m8
                EmitM8086Compute(ctx, kind, false);
                EmitM8086WriteReg8Result(ctx, kind, M8086Reg8[reg]);
                break;
            case 3u:   // r16, r/m16
                EmitLoadReg16(ctx, M8086Reg16[reg]); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);   // a = reg16
                EmitM8086LoadRmWordToB(ctx, mod, rm, disp, over);                  // b = r/m16
                EmitM8086Compute(ctx, kind, true);
                EmitM8086WriteReg16Result(ctx, kind, M8086Reg16[reg]);
                break;
            case 4u:   // AL, imm8
            {
                byte imm8 = _bus.Read8(M8086CodePhys((ushort)operandPc));
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("AL")); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);   // a = AL
                il.Emit(OpCodes.Ldc_I4, (int)imm8); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = imm8
                EmitM8086Compute(ctx, kind, false);
                EmitM8086WriteReg8Result(ctx, kind, "AL");
                break;
            }
            default:   // 5u — AX, imm16
            {
                ushort imm16 = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                EmitLoadReg16(ctx, "AX"); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);   // a = AX
                il.Emit(OpCodes.Ldc_I4, (int)imm16); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = imm16
                EmitM8086Compute(ctx, kind, true);
                EmitM8086WriteReg16Result(ctx, kind, "AX");
                break;
            }
        }
    }

    /// <summary>M6 PR-C: the 80/81/83 group — ALU r/m,imm (AluGroupImm, Alu.cs:288). The ModR/M reg field selects
    /// the op. 0x80: r/m8,imm8 (byte). 0x81: r/m16,imm16. 0x83: r/m16, imm8 SIGN-EXTENDED to 16. The r/m is both
    /// the read source (a) and (unless CMP) the write-back dest.</summary>
    private void EmitM8086AluGroupImm(EmitContext ctx, bool width16, bool signExtend,
        uint mod, uint reg, uint rm, ushort disp, M8086Cpu_Override over, int operandPc)
    {
        ILGenerator il = ctx.Il;
        M8086AluKind kind = M8086GroupOp(reg);
        if (!width16)
        {
            byte imm8 = _bus.Read8(M8086CodePhys((ushort)operandPc));
            EmitM8086LoadRmByteToA(ctx, mod, rm, disp, over);                      // a = r/m8
            il.Emit(OpCodes.Ldc_I4, (int)imm8); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = imm8
            EmitM8086Compute(ctx, kind, false);
            EmitM8086WriteRmByteResult(ctx, kind, mod, rm, disp, over);
        }
        else
        {
            // 0x83: sign-extend the imm8 to 16 bits; 0x81: read the imm16 directly.
            ushort imm = signExtend
                ? unchecked((ushort)(sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc)))
                : (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                           | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
            EmitM8086LoadRmWordToA(ctx, mod, rm, disp, over);                      // a = r/m16
            il.Emit(OpCodes.Ldc_I4, (int)imm); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = imm16
            EmitM8086Compute(ctx, kind, true);
            EmitM8086WriteRmWordResult(ctx, kind, mod, rm, disp, over);
        }
    }

    /// <summary>M6 PR-C: 84/85 TEST r/m,reg — AND with the result discarded (flags only; AluTestRm, Alu.cs:313).</summary>
    private void EmitM8086TestRm(EmitContext ctx, bool width16, uint mod, uint reg, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (!width16)
        {
            EmitM8086LoadRmByteToA(ctx, mod, rm, disp, over);                      // a = r/m8
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[reg])); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = reg8
            EmitM8086Compute(ctx, M8086AluKind.And, false);
        }
        else
        {
            EmitM8086LoadRmWordToA(ctx, mod, rm, disp, over);                      // a = r/m16
            EmitLoadReg16(ctx, M8086Reg16[reg]); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = reg16
            EmitM8086Compute(ctx, M8086AluKind.And, true);
        }
        il.Emit(OpCodes.Pop);   // TEST discards the result
    }

    /// <summary>M6 PR-C: A8/A9 TEST acc,imm — AL&amp;imm8 / AX&amp;imm16, flags only (Alu.cs:148-149).</summary>
    private void EmitM8086TestAccImm(EmitContext ctx, bool width16, ushort imm)
    {
        ILGenerator il = ctx.Il;
        if (!width16)
        {
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("AL")); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);   // a = AL
            il.Emit(OpCodes.Ldc_I4, (int)(byte)imm); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = imm8
            EmitM8086Compute(ctx, M8086AluKind.And, false);
        }
        else
        {
            EmitLoadReg16(ctx, "AX"); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);   // a = AX
            il.Emit(OpCodes.Ldc_I4, (int)imm); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);   // b = imm16
            EmitM8086Compute(ctx, M8086AluKind.And, true);
        }
        il.Emit(OpCodes.Pop);
    }

    /// <summary>M6 PR-C: 40-4F INC/DEC r16 (CF preserved; Alu.cs:152-155). a = Reg16(reg); IncDecFlags; store.</summary>
    private void EmitM8086IncDecReg16(EmitContext ctx, uint reg, bool decrement)
    {
        ILGenerator il = ctx.Il;
        EmitLoadReg16(ctx, M8086Reg16[reg]); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);   // a = Reg16(reg)
        EmitM8086IncDecFlags(ctx, decrement, true);                                       // result on stack + in M8086ResultLocal
        EmitStoreReg16(ctx, M8086Reg16[reg]);                                            // store the stack result
    }

    /// <summary>M6 PR-C: FE/FF /0 /1 INC/DEC r/m (CF preserved; AluIncDecRm, Alu.cs:320). a = r/m; IncDecFlags;
    /// write result back to the SAME r/m.</summary>
    private void EmitM8086IncDecRm(EmitContext ctx, bool width16, bool decrement, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (!width16)
        {
            EmitM8086LoadRmByteToA(ctx, mod, rm, disp, over);
            EmitM8086IncDecFlags(ctx, decrement, false);
            il.Emit(OpCodes.Pop);   // result is in M8086ResultLocal
            EmitM8086StoreRmByteResult(ctx, mod, rm, disp, over);
        }
        else
        {
            EmitM8086LoadRmWordToA(ctx, mod, rm, disp, over);
            EmitM8086IncDecFlags(ctx, decrement, true);
            il.Emit(OpCodes.Pop);
            EmitM8086StoreRmWordResult(ctx, mod, rm, disp, over);
        }
    }

    /// <summary>M6 PR-C: F6/F7 /0 /1 TEST r/m,imm — AND with imm, flags only (AluUnaryTestImm, Alu.cs:336).</summary>
    private void EmitM8086UnaryTestImm(EmitContext ctx, bool width16, uint mod, uint rm, ushort disp, M8086Cpu_Override over, ushort imm)
    {
        ILGenerator il = ctx.Il;
        if (!width16)
        {
            EmitM8086LoadRmByteToA(ctx, mod, rm, disp, over);
            il.Emit(OpCodes.Ldc_I4, (int)(byte)imm); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);
            EmitM8086Compute(ctx, M8086AluKind.And, false);
        }
        else
        {
            EmitM8086LoadRmWordToA(ctx, mod, rm, disp, over);
            il.Emit(OpCodes.Ldc_I4, (int)imm); il.Emit(OpCodes.Stloc, ctx.M8086BLocal);
            EmitM8086Compute(ctx, M8086AluKind.And, true);
        }
        il.Emit(OpCodes.Pop);
    }

    /// <summary>M6 PR-C: F6/F7 /2 NOT r/m — bitwise complement, sets NO flags (AluUnaryNot, Alu.cs:343).</summary>
    private void EmitM8086UnaryNot(EmitContext ctx, bool width16, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (!width16)
        {
            EmitM8086LoadRmByteToA(ctx, mod, rm, disp, over);                      // a = r/m8 -> M8086ALocal
            il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Not); il.Emit(OpCodes.Stloc, ctx.M8086ResultLocal);   // ~a
            EmitM8086StoreRmByteResult(ctx, mod, rm, disp, over);
        }
        else
        {
            EmitM8086LoadRmWordToA(ctx, mod, rm, disp, over);
            il.Emit(OpCodes.Ldloc, ctx.M8086ALocal); il.Emit(OpCodes.Not); il.Emit(OpCodes.Stloc, ctx.M8086ResultLocal);
            EmitM8086StoreRmWordResult(ctx, mod, rm, disp, over);
        }
    }

    /// <summary>M6 PR-C: F6/F7 /3 NEG r/m — 0 - operand (SUB-form flags; AluUnaryNeg, Alu.cs:350). a=0, b=operand,
    /// borrow-in=0.</summary>
    private void EmitM8086UnaryNeg(EmitContext ctx, bool width16, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (!width16)
        {
            EmitM8086LoadRmByteToB(ctx, mod, rm, disp, over);                      // b = operand
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);    // a = 0
            EmitM8086Compute(ctx, M8086AluKind.Sub, false);
            EmitM8086WriteRmByteResult(ctx, M8086AluKind.Sub, mod, rm, disp, over);
        }
        else
        {
            EmitM8086LoadRmWordToB(ctx, mod, rm, disp, over);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, ctx.M8086ALocal);
            EmitM8086Compute(ctx, M8086AluKind.Sub, true);
            EmitM8086WriteRmWordResult(ctx, M8086AluKind.Sub, mod, rm, disp, over);
        }
    }

    // ── operand read helpers: push the r/m operand and stash into the named survivor local ──────────────────

    private void EmitM8086LoadRmByteToA(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[rm])); }
        else EmitM8086LoadByteEa(ctx, mod, rm, disp, over);
        il.Emit(OpCodes.Stloc, ctx.M8086ALocal);
    }
    private void EmitM8086LoadRmByteToB(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[rm])); }
        else EmitM8086LoadByteEa(ctx, mod, rm, disp, over);
        il.Emit(OpCodes.Stloc, ctx.M8086BLocal);
    }
    private void EmitM8086LoadRmWordToA(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (mod == 3u) EmitLoadReg16(ctx, M8086Reg16[rm]);
        else EmitM8086LoadWordEa(ctx, mod, rm, disp, over);
        il.Emit(OpCodes.Stloc, ctx.M8086ALocal);
    }
    private void EmitM8086LoadRmWordToB(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (mod == 3u) EmitLoadReg16(ctx, M8086Reg16[rm]);
        else EmitM8086LoadWordEa(ctx, mod, rm, disp, over);
        il.Emit(OpCodes.Stloc, ctx.M8086BLocal);
    }

    // ── result write helpers: pop the helper's stack result (it is also in M8086ResultLocal), then write the
    //    result from M8086ResultLocal to the dest — UNLESS the op is CMP (flags-only, the result is discarded). ──

    private void EmitM8086WriteRmByteResult(EmitContext ctx, M8086AluKind kind, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ctx.Il.Emit(OpCodes.Pop);
        if (kind == M8086AluKind.Cmp) return;
        EmitM8086StoreRmByteResult(ctx, mod, rm, disp, over);
    }
    private void EmitM8086WriteRmWordResult(EmitContext ctx, M8086AluKind kind, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ctx.Il.Emit(OpCodes.Pop);
        if (kind == M8086AluKind.Cmp) return;
        EmitM8086StoreRmWordResult(ctx, mod, rm, disp, over);
    }
    private void EmitM8086WriteReg8Result(EmitContext ctx, M8086AluKind kind, string reg8)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Pop);
        if (kind == M8086AluKind.Cmp) return;
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stfld, RegField(reg8));
    }
    private void EmitM8086WriteReg16Result(EmitContext ctx, M8086AluKind kind, string reg16)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Pop);
        if (kind == M8086AluKind.Cmp) return;
        il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); EmitStoreReg16(ctx, reg16);
    }

    // r/m write-back (used by the RMW dest forms + INC/DEC/NOT/NEG). The memory store re-resolves the EA (no
    // auto-increment side effect, so the re-formed physical is the SAME address — address-once-equivalent).
    private void EmitM8086StoreRmByteResult(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stfld, RegField(M8086Reg8[rm])); }
        else EmitM8086StoreByteEa(ctx, mod, rm, disp, over, () => il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal));
    }
    private void EmitM8086StoreRmWordResult(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        if (mod == 3u) { il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); EmitStoreReg16(ctx, M8086Reg16[rm]); }
        else EmitM8086StoreWordEa(ctx, mod, rm, disp, over, () => il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal));
    }

    // ── M6 PR-D: the 8086 control-flow (NEAR) emit arm — Jcc / JMP / CALL / RET / LOOP + FF /2 /4 indirect.
    //    Mirrors the Z80 PR-3 flow arm (EndsBlock / static-chain / dynamic-exit) and the 68000 PR-6 conditional
    //    taken/not-taken edge shape. Sets NO flags. The far forms (9A/EA/CB/CA + FF /3 /5) + INT stay fallback
    //    (the gate never admits them; DECISION D-1). The decode preamble (find the opcode past any prefix, read
    //    the rel/imm/ModR/M at emit time via M8086CodePhys) mirrors EmitM8086Mov / EmitM8086Alu verbatim. ───────

    /// <summary>M6 PR-D: push a word onto the 8086 SS:SP stack (PushWord, Stack.cs:39). SP -= 2 (16-bit wrap), then
    /// write the word at physical (SS&lt;&lt;4)+SP with the segment-relative OFFSET wrap (the high byte at
    /// (SS&lt;&lt;4)+((SP+1)&amp;0xFFFF)). <paramref name="pushValue"/> leaves the word (int) on the IL stack.</summary>
    private void EmitM8086PushWord(EmitContext ctx, System.Action pushValue)
    {
        ILGenerator il = ctx.Il;
        // SP = (ushort)(SP - 2)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("SP")); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, RegField("SP"));
        // seg = SS, offset = SP (the NEW, post-decrement SP) -> the survivor pair the offset-wrap store reads.
        EmitLoadReg16(ctx, "SS"); il.Emit(OpCodes.Stloc, ctx.M8086SegLocal);
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
        pushValue(); il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // the word to push -> AddrLocal (survives EmitStoreByte)
        // write lo at (SS<<4)+SP, hi at (SS<<4)+((SP+1)&0xFFFF) — the PR-B EmitM8086StoreWordEa offset-wrap shape.
        EmitM8086PushPhysical(ctx, offsetPlusOne: false);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Conv_U1); EmitStoreByte(ctx);
        EmitM8086PushPhysical(ctx, offsetPlusOne: true);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Conv_U1); EmitStoreByte(ctx);
    }

    /// <summary>M6 PR-D: pop a word off the 8086 SS:SP stack (PopWord, Stack.cs:47). Read the word at (SS&lt;&lt;4)+SP
    /// (offset-wrap, low byte then high byte); SP += 2 (16-bit wrap); leave the popped word (int) on the IL stack.</summary>
    private void EmitM8086PopWord(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitLoadReg16(ctx, "SS"); il.Emit(OpCodes.Stloc, ctx.M8086SegLocal);
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
        // word = lo | hi<<8  (re-form each byte's physical from the surviving (seg, offset) pair).
        EmitM8086PushPhysical(ctx, offsetPlusOne: false); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.DataLocal);
        EmitM8086PushPhysical(ctx, offsetPlusOne: true); LoadByteFromBus(ctx); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // stash the popped word in AddrLocal (the SP bump's field-write is fine; AddrLocal survives)
        // SP = (ushort)(SP + 2)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("SP")); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, RegField("SP"));
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);   // leave the popped word on the IL stack
    }

    /// <summary>M6 PR-D: push the Jcc TAKEN predicate (int 0/1) for opcode 0x70-0x7F (JccTaken, Control.cs:26). Reads
    /// CF/PF/ZF/SF/OF from the FLAGS field; the compound conditions (JBE = cf|zf, JL = sf!=of, …) compose the bit
    /// tests inline. Sets NO flags.</summary>
    private void EmitM8086JccTaken(EmitContext ctx, byte opcode)
    {
        ILGenerator il = ctx.Il;
        // helper: push (FLAGS & mask) != 0 as int 0/1.
        void Flag(int mask) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, mask); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); }
        void NotFlag(int mask) { Flag(mask); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }
        switch (opcode)
        {
            case 0x70: Flag(M8086FlagOF); return;                                            // JO
            case 0x71: NotFlag(M8086FlagOF); return;                                         // JNO
            case 0x72: Flag(M8086FlagCF); return;                                            // JB/JC
            case 0x73: NotFlag(M8086FlagCF); return;                                         // JAE/JNB
            case 0x74: Flag(M8086FlagZF); return;                                            // JE/JZ
            case 0x75: NotFlag(M8086FlagZF); return;                                         // JNE/JNZ
            case 0x76: Flag(M8086FlagCF); Flag(M8086FlagZF); il.Emit(OpCodes.Or); return;    // JBE: cf|zf
            case 0x77: NotFlag(M8086FlagCF); NotFlag(M8086FlagZF); il.Emit(OpCodes.And); return; // JA: !cf&!zf
            case 0x78: Flag(M8086FlagSF); return;                                            // JS
            case 0x79: NotFlag(M8086FlagSF); return;                                         // JNS
            case 0x7A: Flag(M8086FlagPF); return;                                            // JP/JPE
            case 0x7B: NotFlag(M8086FlagPF); return;                                         // JNP/JPO
            case 0x7C: EmitM8086SfNeOf(ctx); return;                                         // JL: sf!=of
            case 0x7D: EmitM8086SfNeOf(ctx); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return; // JGE: sf==of
            case 0x7E: Flag(M8086FlagZF); EmitM8086SfNeOf(ctx); il.Emit(OpCodes.Or); return; // JLE: zf|(sf!=of)
            default:   // 0x7F JG: !zf & (sf==of)
                NotFlag(M8086FlagZF);
                EmitM8086SfNeOf(ctx); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq);       // (sf==of)
                il.Emit(OpCodes.And);
                return;
        }
    }

    /// <summary>M6 PR-D: push (SF != OF) as int 0/1 — both flags normalized to 0/1 (Cgt_Un), then compared (Ceq) and
    /// negated (Ceq with 0) for "different". Used by JL/JGE/JLE/JG (Control.cs:47-50).</summary>
    private void EmitM8086SfNeOf(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagSF); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagOF); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un);
        il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq);   // (sf01 == of01) == 0  ⇒  sf != of
    }

    /// <summary>M6 PR-D: push the LOOP-family TAKEN predicate (int 0/1) for opcode E0-E3 (Control.cs:119-141). E0/E1/E2
    /// DECREMENT CX as a side effect (CX = (ushort)(CX-1)) regardless of the predicate, BEFORE the taken test; E3
    /// (JCXZ) does NOT decrement. taken: E2 LOOP = CX!=0; E1 LOOPE = CX!=0 &amp;&amp; ZF; E0 LOOPNE = CX!=0 &amp;&amp; !ZF;
    /// E3 JCXZ = CX==0. Sets NO flags (it READS ZF for E0/E1).</summary>
    private void EmitM8086LoopTaken(EmitContext ctx, byte opcode)
    {
        ILGenerator il = ctx.Il;
        if (opcode == 0xE3)
        {
            // JCXZ: taken = (CX == 0). CX is NOT decremented.
            EmitLoadReg16(ctx, "CX"); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq);
            return;
        }
        // E0/E1/E2: CX = (ushort)(CX - 1)  — the side-effecting decrement (Control.cs:121/127/133).
        EmitLoadReg16(ctx, "CX"); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_U2);
        EmitStoreReg16(ctx, "CX");
        // cxNonZero = (CX != 0)  (the post-decrement CX).
        EmitLoadReg16(ctx, "CX"); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un);   // int 0/1
        switch (opcode)
        {
            case 0xE2: return;   // LOOP: taken = CX != 0
            case 0xE1:           // LOOPE/LOOPZ: taken = CX != 0 && (FLAGS & ZF) != 0
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagZF); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un);
                il.Emit(OpCodes.And);
                return;
            default:             // 0xE0 LOOPNE/LOOPNZ: taken = CX != 0 && (FLAGS & ZF) == 0
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagZF); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.And);
                return;
        }
    }

    /// <summary>ADR 0019 FF-1: project a same-segment near-flow IP target to the linear block key the
    /// dispatcher will compute for it — (_m8086CodePhysBase + ip) &amp; 0xFFFFF. The base is the baked CS&lt;&lt;4
    /// (set at the head of Discover/Compile from the live CS), so for a compile-time-constant IP this is a
    /// compile-time-constant uint key. Used ONLY for the near arm's static chain edges (a near transfer
    /// cannot change CS, so the successor is in the SAME baked segment). The inverse of the Discover offset
    /// recovery (entryKey - base): here base + ip rebuilds the linear key from the same-segment IP.</summary>
    private uint M8086NearChainKey(ushort ip) => (_m8086CodePhysBase + ip) & 0xFFFFFu;

    /// <summary>M6 PR-D: emit one 8086 NEAR control-flow instruction (DECISION D-1/D-2). Reached when TargetIsM8086
    /// &amp;&amp; the row is an in-scope flow opcode. STATIC targets (Jcc/JMP/CALL rel, LOOP*) chain via EmitChainOrExit
    /// (the target is the compile-time constant (pc+length) + rel); DYNAMIC targets (RET pop, FF /2 /4 indirect) set
    /// IP from a runtime value and EmitNormalExit. Conditional forms chain BOTH the taken (static) and not-taken
    /// (pc+length) edges. The arm SELF-TERMINATES (it sets the IP field then exits via EmitChainOrExit/EmitNormalExit,
    /// each of which ends with ret) — it does NOT use the MOV/ALU length-1 tail (flow leaves IP at the successor, not
    /// the next-instruction base). Sets NO flags. The default throws (the gate↔arm lockstep tripwire).
    /// <paramref name="length"/> is the walk's exact footprint (the fall-through base); <paramref name="x86Seg"/> is
    /// the captured segment-override prefix byte (used only by the FF-group indirect memory operand).</summary>
    private void EmitM8086Flow(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg)
    {
        ILGenerator il = ctx.Il;
        M8086Cpu_Override over = M8086OverrideFromByte(x86Seg);
        ushort fallThrough = (ushort)(pc + length);   // the post-instruction IP (== the interpreter's pre-body IP).

        // Scan past any prefix byte(s) at the SEGMENTED physical to find the opcode; the const operand reads start at
        // operandPc = (opcode pos)+1 (the EmitM8086Mov / EmitM8086Alu decode preamble verbatim).
        int opcodePc = pc;
        while (M8086IsPrefixByte(_bus.Read8(M8086CodePhys((ushort)opcodePc)))) opcodePc++;
        byte opcode = _bus.Read8(M8086CodePhys((ushort)opcodePc));
        int operandPc = opcodePc + 1;

        switch (opcode)
        {
            // ── 70-7F Jcc rel8: conditional; both edges static (chainable). ───────────────────────────────────────
            case >= 0x70 and <= 0x7F:
            {
                short rel = (sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc));   // sign-extended rel8
                ushort target = (ushort)(fallThrough + rel);
                Label notTaken = il.DefineLabel();
                EmitM8086JccTaken(ctx, opcode);                // push taken? (0/1)
                il.Emit(OpCodes.Brfalse, notTaken);
                EmitM8086SetIp(ctx, target);                   // IP = target
                EmitChainOrExit(ctx, M8086NearChainKey(target));   // STATIC taken edge — linear key (FF-1)
                il.MarkLabel(notTaken);
                EmitM8086SetIp(ctx, fallThrough);              // IP = fall-through
                EmitChainOrExit(ctx, M8086NearChainKey(fallThrough));   // STATIC not-taken edge — linear key (FF-1)
                return;
            }

            // ── EB JMP rel8: unconditional static. ────────────────────────────────────────────────────────────────
            case 0xEB:
            {
                short rel = (sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc));   // sign-extended rel8
                ushort target = (ushort)(fallThrough + rel);
                EmitM8086SetIp(ctx, target);
                EmitChainOrExit(ctx, M8086NearChainKey(target));   // STATIC JMP rel8 — linear key (FF-1)
                return;
            }

            // ── E9 JMP rel16: unconditional static. ──────────────────────────────────────────────────────────────
            case 0xE9:
            {
                short rel = (short)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                    | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));   // (short) rel16
                ushort target = (ushort)(fallThrough + rel);
                EmitM8086SetIp(ctx, target);
                EmitChainOrExit(ctx, M8086NearChainKey(target));   // STATIC JMP rel16 — linear key (FF-1)
                return;
            }

            // ── E8 CALL rel16: push the return IP (== fallThrough), then jump to the static target (chainable). ────
            case 0xE8:
            {
                short rel = (short)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                    | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));   // (short) rel16
                ushort target = (ushort)(fallThrough + rel);
                EmitM8086PushWord(ctx, () => il.Emit(OpCodes.Ldc_I4, (int)fallThrough));   // PushWord(IP) — the return IP
                EmitM8086SetIp(ctx, target);
                EmitChainOrExit(ctx, M8086NearChainKey(target));   // STATIC call entry — linear key (FF-1)
                return;
            }

            // ── C3 RET / C2 RET imm16: pop IP (dynamic target → exit), C2 also adds imm16 to SP. ──────────────────
            case 0xC3: case 0xC2:
            {
                EmitM8086PopWord(ctx); EmitM8086SetIpFromStack(ctx);   // IP = PopWord()
                if (opcode == 0xC2)                                     // SP += imm16
                {
                    ushort imm16 = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                            | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("SP")); il.Emit(OpCodes.Ldc_I4, (int)imm16); il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, RegField("SP"));
                }
                EmitNormalExit(ctx);                                   // DYNAMIC popped target — NOT chainable
                return;
            }

            // ── E0/E1/E2/E3 LOOP family: CX-conditioned static short jump (both edges chainable). ─────────────────
            case 0xE0: case 0xE1: case 0xE2: case 0xE3:
            {
                short rel = (sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc));   // sign-extended rel8
                ushort target = (ushort)(fallThrough + rel);
                Label notTaken = il.DefineLabel();
                EmitM8086LoopTaken(ctx, opcode);               // push taken? (decrements CX for E0-E2; reads ZF/CX)
                il.Emit(OpCodes.Brfalse, notTaken);
                EmitM8086SetIp(ctx, target); EmitChainOrExit(ctx, M8086NearChainKey(target));   // LOOP taken — linear key (FF-1)
                il.MarkLabel(notTaken);
                EmitM8086SetIp(ctx, fallThrough); EmitChainOrExit(ctx, M8086NearChainKey(fallThrough));   // LOOP not-taken — linear key (FF-1)
                return;
            }

            // ── FF /2 CALL r/m16 near (key 0x7FA) / FF /4 JMP r/m16 near (key 0x7FC): dynamic target. The GATE admits
            //    ONLY the near /2 /4 keys (far /3 /5 stay fallback), so a 0xFF row reaching here is guaranteed near —
            //    re-decode the ModR/M reg field to pick CALL (/2) vs JMP (/4). The descriptor's d.Opcode is the BYTE
            //    0xFF for EVERY FF-group row (the dictionary key 0x7FA/0x7FC is NOT carried on d), so reg is the only
            //    in-arm discriminator. ─────────────────────────────────────────────────────────────────────────────
            case 0xFF:
            {
                byte modrm = _bus.Read8(M8086CodePhys((ushort)operandPc)); operandPc++;
                uint mod = (uint)(modrm >> 6) & 3u;
                uint reg = (uint)(modrm >> 3) & 7u;
                uint rm  = (uint)modrm & 7u;
                int dispLen = mod switch { 0u => rm == 6u ? 2 : 0, 1u => 1, 2u => 2, _ => 0 };
                ushort disp = 0;
                if (dispLen == 1) disp = unchecked((ushort)(sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc)));
                else if (dispLen == 2)
                    disp = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                    | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));

                if (reg == 2u)        // FF /2 CALL r/m16 near (key 0x7FA): IP = r/m16 (read FIRST), then push the
                {                     // return IP. The oracle (Control.cs:148-150) reads the target BEFORE PushWord —
                                      // load-bearing for `CALL SP` (mod=11,rm=4): the target must be the PRE-push SP,
                                      // not the post-decrement SP. So stash the target, then push, then set IP.
                    EmitM8086LoadRmWordTarget(ctx, mod, rm, disp, over);   // push the r/m16 target (the PRE-push value)
                    il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stloc, ctx.M8086ResultLocal);   // stash target (survives the push)
                    EmitM8086PushWord(ctx, () => il.Emit(OpCodes.Ldc_I4, (int)fallThrough));   // PushWord(IP)
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.M8086ResultLocal); il.Emit(OpCodes.Stfld, _fpc);   // IP = target
                    EmitNormalExit(ctx);                                  // DYNAMIC — NOT chainable
                    return;
                }
                if (reg == 4u)        // FF /4 JMP r/m16 near (key 0x7FC): IP = r/m16.
                {
                    EmitM8086LoadRmWordTarget(ctx, mod, rm, disp, over);
                    EmitM8086SetIpFromStack(ctx);
                    EmitNormalExit(ctx);
                    return;
                }
                goto default;   // any other FF /reg is far/PUSH — the gate excludes it; a lockstep bug if reached.
            }

            default:
                throw new EmulationException(
                    $"BlockCompiler: no 8086 flow emit branch for opcode 0x{opcode:X2} (key 0x{d.Opcode:X}); "
                  + "the gate (IsEmittableX86Family) admitted a form the arm does not handle — a lockstep bug.");
        }
    }

    /// <summary>ADR 0019 FF-2: the far-transfer arm (9A/EA/CB/CA + FF /3 /5). Returns true if it EMITTED the op;
    /// false for an FF /reg it does not own (the near arm then handles /2 /4, or interpreter fallback). Reached
    /// when TargetIsM8086 &amp;&amp; IsM8086FarFlowOpcode(d); runs BEFORE the near arm. Like the near arm it self-
    /// terminates (sets CS:IP, then EmitChainOrExit for the constant 9A/EA targets / EmitNormalExit for the
    /// dynamic CB/CA + FF /3 /5) and sets NO flags. <paramref name="length"/> is the walk's exact footprint (the
    /// far-CALL return IP = pc+length); <paramref name="x86Seg"/> is the captured segment-override prefix byte
    /// (used only by the FF-group far-indirect memory operand). Filled per-opcode across FF-2 Tasks 3/4/6.</summary>
    private bool EmitM8086FarFlow(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg)
    {
        byte opcode = d.Opcode;
        switch (opcode)
        {
            // Tasks 3/4/6 fill these. Until then, fall through to false (fallback / near arm).
            default:
                return false;
        }
    }

    /// <summary>M6 PR-D: IP = (ushort)target (a compile-time constant) — the resolved IP field (_fpc, "IP").</summary>
    private void EmitM8086SetIp(EmitContext ctx, ushort target)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)target); il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
    }

    /// <summary>M6 PR-D: IP = (ushort)(the value on the IL stack) — stash through DataLocal so the Stfld receiver
    /// (Ldarg_0) is loaded after the value is consumed.
    /// <para>CLOBBERS <c>ctx.DataLocal</c>: the IL-stack value is staged through that local before the store to
    /// <c>_fpc</c>, so its prior contents do not survive this call — callers must not rely on DataLocal across it.</para></summary>
    private void EmitM8086SetIpFromStack(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stloc, ctx.DataLocal);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Stfld, _fpc);
    }

    /// <summary>ADR 0019 FF-2: write the CS field to a compile-time-constant segment (the far-direct
    /// 9A/EA target's CS). Mirrors EmitM8086SetIp but stores _m8086CS. The far arm calls this BEFORE
    /// EmitChainOrExit/EmitNormalExit so the next dispatch's ProjectBlockKey keys/decodes the successor
    /// under the new segment (the linear-key payoff). 8086-gated (the caller runs only when TargetIsM8086,
    /// where _m8086CS is non-null, resolved in the ctor).</summary>
    private void EmitM8086SetCs(EmitContext ctx, ushort target)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)target); il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _m8086CS!);
    }

    /// <summary>ADR 0019 FF-2: write the CS field from the IL-stack top (the far-indirect / far-RET dynamic
    /// CS — popped off SS:SP or read from memory). Mirrors EmitM8086SetIpFromStack: narrows the stack value
    /// to ushort, stages it through ctx.DataLocal, then stores _m8086CS.
    /// <para>CLOBBERS <c>ctx.DataLocal</c> (same discipline as EmitM8086SetIpFromStack).</para></summary>
    private void EmitM8086SetCsFromStack(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stloc, ctx.DataLocal);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Stfld, _m8086CS!);
    }

    /// <summary>M6 PR-D: push the FF-group r/m16 target word (int) on the IL stack — ReadRmWord (Control.cs:148/157).
    /// For mod==3 the target is the 16-bit register M8086Reg16[rm] (a register operand is valid only for the NEAR
    /// forms, the contract the gate enforces); else resolve the EA + read the word (the PR-B offset-wrap
    /// EmitM8086LoadWordEa).</summary>
    private void EmitM8086LoadRmWordTarget(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        if (mod == 3u) EmitLoadReg16(ctx, M8086Reg16[rm]);
        else EmitM8086LoadWordEa(ctx, mod, rm, disp, over);
    }
}
