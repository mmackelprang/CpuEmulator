using System.Reflection.Emit;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>M6 PR-4: the 68000 MOVE/MOVEA/MOVEQ emit arm — the first net-new-descriptor structured CPU to
/// emit real IL (the Z80/8086 were gate-flips; the 68000's JitDescriptorsByKey was empty). This partial
/// mirrors BlockCompiler.Z80.cs: a per-CPU discriminator (TargetIsM68000), the dual-EA resolver over the
/// 68000's 12 effective-address modes, the A7-banking register helpers (DECISION A7), and the MOVE/MOVEA/
/// MOVEQ arm. Each helper transcribes the GENERATED interpreter oracle one-for-one:
///   • ComputeEa (M68000Cpu.M68000Cpu.g.cs) — the 12-mode address math (mag/A7-word-align; (An)+/-(An)
///     write-back; d16/d8/abs/PC-rel/#imm). The JIT reads the extension words as COMPILE-TIME constants
///     from _bus.Read16 (the operword + ext words are a code-stream constant exactly as the Z80 flow arms
///     read their 16-bit targets), so the per-mode displacement/abs/index-flag bits are all baked Ldc_I4;
///     only the index-register VALUE (d8(An,Xn)/d8(PC,Xn)) is a runtime load.
///   • ReadEaOperand / WriteEaOperand (M68000Cpu.Move.cs) — Dn = sized masked read / SetDataRegPartial;
///     An = whole 32; #imm = the ext words; memory = ComputeEa + the wide bus.
///   • SetMoveCcr (M68000Cpu.Move.cs:64) — N/Z from value, V=C=0, X UNTOUCHED.
///   • MOVEA (Move.cs:113) — whole An, .w sign-extends, NO CCR.  • MOVEQ (SystemMisc.cs:20) — imm8 sign-
///     extended to .l into Dn, Logic CCR (== the MOVE N/Z at .l).
///
/// DECISION P3 (the descriptor shape): the EA matrix is decoded at EMIT TIME from the operword (a code-
/// stream constant: ushort operword = _bus.Read16(pc)), NOT carried in the descriptor (which holds only a
/// coarse Mnemonic + JitOpClass.M68000Move + BaseCycles). The size is re-decoded from the operword too
/// (the descriptor's Opcode byte is 0x00 and it carries no key) — MOVE/MOVEA use the Move size encoding
/// (operword bits 13-12 → 01=.b/11=.w/10=.l), MOVEQ is always .l.
///
/// DECISION A7 (G3): A7 (reg 7) is a banked PROPERTY (SupervisorMode ? SSP : USP — M68000Cpu.cs:60), NOT a
/// field. EmitLoadAreg/EmitStoreAreg emit the SR S-bit branch ((SR>>13)&1) over USP/SSP for reg 7 and the
/// plain A0-A6 field access otherwise.
///
/// THE ADDRESS-ONCE / dual-EA ORDERING crux: the source EA is resolved+read FIRST (so its (An)+/-(An)
/// mutation of An lands before the dest is touched), the operand stashed in M68kValueLocal, THEN the dest
/// EA is resolved (into M68kAddr2Local — a survivor local distinct from AddrLocal, which the source read's
/// wide-bus helpers clobber) and written. This mirrors ReadEaOperand-then-WriteEaOperand (Move.cs:84-86).
///
/// DECISION T: the parity gate is the DATA axis only (regs/SR/RAM, NOT cycles/pc/prefetch), so the arm
/// charges a COARSE d.BaseCycles once (keeping the >=1-cycle budget invariant) and the wide-bus helpers
/// charge 0 — it does NOT model the prefetch queue.</summary>
internal sealed partial class BlockCompiler<TCpu> where TCpu : class
{
    /// <summary>M6 PR-4: is the compiled CPU the 68000? Routes the M68000Move rows to EmitM68kMove (mirrors
    /// TargetIsZ80, BlockCompiler.cs). No other CPU produces the M68000Move class, so this is the unambiguous
    /// per-CPU discriminator.</summary>
    private bool TargetIsM68000 => _target.CpuType.Name == "M68000Cpu";

    // ── A7 banking (DECISION A7) ───────────────────────────────────────────────────────────────────────────

    /// <summary>M6 PR-4: push An (uint). reg 0-6 = the plain uint A{reg} field; reg 7 = the BANKED A7 property
    /// (((SR>>13)&1)!=0 ? SSP : USP — the SupervisorMode bank, M68000Cpu.cs:60-64). Stack: ... -> ..., An(uint).</summary>
    private void EmitLoadAreg(EmitContext ctx, int reg)
    {
        if (reg < 7) { EmitLoadReg32(ctx, $"A{reg}"); return; }
        ILGenerator il = ctx.Il;
        Label useUsp = il.DefineLabel();
        Label done = il.DefineLabel();
        EmitLoadSupervisorBit(ctx);                 // (SR>>13)&1
        il.Emit(OpCodes.Brfalse, useUsp);
        EmitLoadReg32(ctx, "SSP");
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(useUsp);
        EmitLoadReg32(ctx, "USP");
        il.MarkLabel(done);
    }

    /// <summary>M6 PR-4: store An. reg 0-6 = the plain uint A{reg} field; reg 7 = the BANKED A7 property
    /// (SSP when supervisor, USP otherwise). Stack: ..., value(uint) -> .... The value arrives on the stack, so
    /// it is stashed in M68kStoreStageLocal (the DEDICATED register-store stage — NOT M68kValueLocal, so an An
    /// write-back never clobbers a live MOVE operand) and re-pushed inside each branch.</summary>
    private void EmitStoreAreg(EmitContext ctx, int reg)
    {
        if (reg < 7) { EmitStoreReg32(ctx, $"A{reg}"); return; }
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.M68kStoreStageLocal);   // value (off the stack — the S-bit test below is stack-clean)
        Label useUsp = il.DefineLabel();
        Label done = il.DefineLabel();
        EmitLoadSupervisorBit(ctx);                        // (SR>>13)&1
        il.Emit(OpCodes.Brfalse, useUsp);
        il.Emit(OpCodes.Ldloc, ctx.M68kStoreStageLocal); EmitStoreReg32(ctx, "SSP");
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(useUsp);
        il.Emit(OpCodes.Ldloc, ctx.M68kStoreStageLocal); EmitStoreReg32(ctx, "USP");
        il.MarkLabel(done);
    }

    /// <summary>Push (SR>>13)&amp;1 as an int (0 or 1) — the SupervisorMode S-bit (S=0x2000).</summary>
    private void EmitLoadSupervisorBit(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _m68kSR!);             // SR (ushort -> int, zero-extended)
        il.Emit(OpCodes.Ldc_I4, 13);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.And);
    }

    // ── The dual-EA resolver (12 modes, address-once) ──────────────────────────────────────────────────────

    /// <summary>M6 PR-4: read an EA operand (sized) and leave it on the stack (.b/.w/.l masked, as uint/int).
    /// Resolves the EA EXACTLY ONCE (the address-once discipline) and reads via the sized bus helper; (An)+ and
    /// -(An) mutate An (via EmitStoreAreg). Extension words are read as COMPILE-TIME constants from the code
    /// stream (_bus.Read16(pc + 2 + 2*extIndex)); `ref extIndex` threads the per-operand ext-word position so
    /// the source and dest don't collide (dest ext words follow the source's). Mirrors ReadEaOperand +
    /// ComputeEa (M68000Cpu.Move.cs:24-31 + g.cs ComputeEa).</summary>
    private void EmitM68kEaRead(EmitContext ctx, ushort pc, int eaMode, int eaReg, int size, ref int extIndex)
    {
        ILGenerator il = ctx.Il;
        switch (eaMode)
        {
            case 0:   // Dn — register direct: DataReg(reg) & SizeMask(size)
                EmitLoadDataRegSized(ctx, $"D{eaReg}", size);
                return;
            case 1:   // An — register direct: Areg(reg), always WHOLE 32 (MOVEA source / An as a MOVE source)
                EmitLoadAreg(ctx, eaReg);
                return;
            case 2:   // (An)
                EmitLoadAreg(ctx, eaReg);
                EmitM68kReadSized(ctx, size);
                return;
            case 3:   // (An)+ : ea = An; An += mag
                EmitLoadAreg(ctx, eaReg);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);                 // ea (survives the An write-back)
                EmitAdvanceAreg(ctx, eaReg, +M68kMag(eaReg, eaMode, size));
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                EmitM68kReadSized(ctx, size);
                return;
            case 4:   // -(An) : An -= mag FIRST, then ea = An
                EmitAdvanceAreg(ctx, eaReg, -M68kMag(eaReg, eaMode, size));
                EmitLoadAreg(ctx, eaReg);
                EmitM68kReadSized(ctx, size);
                return;
            case 5:   // d16(An)
                EmitLoadAreg(ctx, eaReg);
                EmitAddDisp16(ctx, pc, ref extIndex);
                EmitM68kReadSized(ctx, size);
                return;
            case 6:   // d8(An,Xn)
                EmitM68kBriefIndex(ctx, pc, eaReg, isPc: false, ref extIndex);
                EmitM68kReadSized(ctx, size);
                return;
            case 7:
                switch (eaReg)
                {
                    case 0:   // abs.w — sign-extended 16-bit address
                        EmitAbsW(ctx, pc, ref extIndex);
                        EmitM68kReadSized(ctx, size);
                        return;
                    case 1:   // abs.l — two words, high first
                        EmitAbsL(ctx, pc, ref extIndex);
                        EmitM68kReadSized(ctx, size);
                        return;
                    case 2:   // d16(PC)
                        EmitPcRelD16(ctx, pc, ref extIndex);
                        EmitM68kReadSized(ctx, size);
                        return;
                    case 3:   // d8(PC,Xn)
                        EmitM68kBriefIndex(ctx, pc, 0, isPc: true, ref extIndex);
                        EmitM68kReadSized(ctx, size);
                        return;
                    case 4:   // #imm — value is the extension words (NO address)
                        EmitImmOperand(ctx, pc, size, ref extIndex);
                        return;
                }
                break;
        }
        throw new EmulationException($"EmitM68kEaRead: unhandled EA mode {eaMode}/{eaReg}");
    }

    /// <summary>M6 PR-4: write an EA operand (sized) — takes the operand off the stack. Dn = the size-aware
    /// PARTIAL write (preserve upper bits, M68000Cpu.Move.cs:41); An = the WHOLE register (the MOVEA path is
    /// handled by the MOVE arm directly, but a whole-An write is supported here too); memory = resolve the EA
    /// (address-once, into M68kAddr2Local — NOT AddrLocal, which the source read clobbered) then the wide store.
    /// Mirrors WriteEaOperand (M68000Cpu.Move.cs:52-60). The operand to write is expected ALREADY in
    /// M68kValueLocal (the MOVE arm stashes it there after the source read), and the stack is EMPTY of the
    /// operand on entry — so the dest-EA resolution has a clean stack.</summary>
    private void EmitM68kEaWrite(EmitContext ctx, ushort pc, int eaMode, int eaReg, int size, ref int extIndex)
    {
        ILGenerator il = ctx.Il;
        switch (eaMode)
        {
            case 0:   // Dn — partial write (.b/.w preserve upper bits; .l whole)
                il.Emit(OpCodes.Ldloc, ctx.M68kValueLocal);
                EmitStoreDataRegSized(ctx, $"D{eaReg}", size);
                return;
            case 1:   // An — whole 32 (MOVEA-shape direct write; the MOVE arm normally handles MOVEA itself)
                il.Emit(OpCodes.Ldloc, ctx.M68kValueLocal);
                EmitStoreAreg(ctx, eaReg);
                return;
            case 2:   // (An)
                EmitLoadAreg(ctx, eaReg);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                EmitM68kWriteSized(ctx, size);
                return;
            case 3:   // (An)+ : ea = An; An += mag
                EmitLoadAreg(ctx, eaReg);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                EmitAdvanceAreg(ctx, eaReg, +M68kMag(eaReg, eaMode, size));
                EmitM68kWriteSized(ctx, size);
                return;
            case 4:   // -(An) : An -= mag FIRST, then ea = An
                EmitAdvanceAreg(ctx, eaReg, -M68kMag(eaReg, eaMode, size));
                EmitLoadAreg(ctx, eaReg);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                EmitM68kWriteSized(ctx, size);
                return;
            case 5:   // d16(An)
                EmitLoadAreg(ctx, eaReg);
                EmitAddDisp16(ctx, pc, ref extIndex);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                EmitM68kWriteSized(ctx, size);
                return;
            case 6:   // d8(An,Xn)
                EmitM68kBriefIndex(ctx, pc, eaReg, isPc: false, ref extIndex);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                EmitM68kWriteSized(ctx, size);
                return;
            case 7:
                switch (eaReg)
                {
                    case 0:   // abs.w
                        EmitAbsW(ctx, pc, ref extIndex);
                        il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                        EmitM68kWriteSized(ctx, size);
                        return;
                    case 1:   // abs.l
                        EmitAbsL(ctx, pc, ref extIndex);
                        il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                        EmitM68kWriteSized(ctx, size);
                        return;
                    // mode 7 reg 2/3 (PC-relative) and reg 4 (#imm) are NOT legal MOVE destinations. M6 PR-4a:
                    // CanEmitM68kMove (the EmitInstruction guard) routes any MOVE with such a dest EA to the
                    // FALLBACK path before it reaches here, so this throw is now an unreachable can't-happen guard
                    // for a valid caller (it only fires if a future call site skips CanEmitM68kMove).
                }
                break;
        }
        throw new EmulationException($"EmitM68kEaWrite: unhandled / illegal dest EA mode {eaMode}/{eaReg}");
    }

    // ── EA sub-helpers (each transcribes a slice of the ComputeEa / ReadEaOperand oracle) ──────────────────

    /// <summary>The (An)+/-(An) magnitude: 1/2/4 by size, with the A7 stack word-align (mag=2 for (A7)+/-(A7)
    /// at .b — ComputeEa g.cs:891). Static (size + reg + mode are compile-time constants).</summary>
    private static int M68kMag(int reg, int mode, int size)
    {
        int mag = size == 0 ? 1 : size == 1 ? 2 : 4;
        if (reg == 7 && (mode == 3 || mode == 4) && mag == 1) mag = 2;   // A7 ±2 (stack word-align)
        return mag;
    }

    /// <summary>An += delta (the (An)+ post-increment / -(An) pre-decrement write-back). delta is a compile-time
    /// constant (±mag). Reads An (banked for A7), adds, writes An back (banked for A7).</summary>
    private void EmitAdvanceAreg(EmitContext ctx, int reg, int delta)
    {
        ILGenerator il = ctx.Il;
        EmitLoadAreg(ctx, reg);
        il.Emit(OpCodes.Ldc_I4, delta);
        il.Emit(OpCodes.Add);
        EmitStoreAreg(ctx, reg);
    }

    /// <summary>Read the sized operand at the address on the stack (uint). .b = a byte read (LoadByteFromBus —
    /// the ReadByteAt oracle, M68000Cpu.Move.cs:37, does _bus.Read8); .w/.l = the charge-0 wide helpers. Leaves
    /// the masked value (int for .b/.w, uint for .l) on the stack.</summary>
    private void EmitM68kReadSized(EmitContext ctx, int size)
    {
        switch (size)
        {
            case 0: LoadByteFromBus(ctx); break;     // .b — Read8 (charges 1; data axis ignores cycles)
            case 1: LoadWordFromBus(ctx); break;     // .w — big-endian Read16 (charges 0)
            default: LoadLongFromBus(ctx); break;    // .l — big-endian Read32, high word first (charges 0)
        }
    }

    /// <summary>Write the sized operand (held in M68kValueLocal) to the address in M68kAddr2Local. .b =
    /// EmitStoreByte (WriteByteAt oracle); .w/.l = the charge-0 wide stores (big-endian, high word first for
    /// .l). The wide helpers' stack contract is (address, value); EmitStoreByte's is (address, value) too.</summary>
    private void EmitM68kWriteSized(EmitContext ctx, int size)
    {
        ILGenerator il = ctx.Il;
        switch (size)
        {
            case 0:   // .b — EmitStoreByte (stack: address, value)
                il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
                il.Emit(OpCodes.Ldloc, ctx.M68kValueLocal);
                il.Emit(OpCodes.Conv_U1);
                EmitStoreByte(ctx);                  // charges 1; marks the page dirty
                break;
            case 1:   // .w — EmitStoreWord (stack: address, value)
                il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
                il.Emit(OpCodes.Ldloc, ctx.M68kValueLocal);
                EmitStoreWord(ctx);                  // charges 0; marks the page(s) dirty
                break;
            default:  // .l — EmitStoreLong (stack: address, value)
                il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
                il.Emit(OpCodes.Ldloc, ctx.M68kValueLocal);
                EmitStoreLong(ctx);                  // charges 0; marks the page(s) dirty
                break;
        }
    }

    /// <summary>The Nth extension WORD of this instruction, read at COMPILE time from the code stream. The
    /// operword is at pc, the first extension word at pc+2, etc. — all code-stream constants the emit arm reads
    /// from _bus (exactly as the Z80 flow arms read their 16-bit targets). Advances extIndex.</summary>
    private ushort NextExtWord(ushort pc, ref int extIndex)
    {
        ushort w = _bus.Read16((ushort)(pc + 2 + 2 * extIndex));
        extIndex++;
        return w;
    }

    /// <summary>d16(An): ea = An + (int)(short)ext[0]. An is on the stack (uint); add the compile-time signed
    /// 16-bit displacement (baked Ldc_I4). Leaves ea (uint, via the unchecked Add) on the stack.
    /// <para>PR-4b: <paramref name="disp"/> is held as an <c>int</c> (the sign-extended short), NOT a <c>short</c>.
    /// ILGenerator has both an <c>Emit(OpCode, short)</c> (Int16) and an <c>Emit(OpCode, int)</c> overload; passing
    /// a <c>short</c> silently binds to the Int16 overload, which writes the <c>Ldc_I4</c> opcode (0x20, a 4-byte
    /// inline operand) but emits only a TRUNCATED 2-byte operand — a malformed IL stream the CLR rejects at execute
    /// with <c>InvalidProgramException</c>. Sign-extending to <c>int</c> first selects <c>Emit(OpCode, int)</c> and
    /// emits the correct operand (this mirrors <see cref="EmitAbsW"/>, which already uses an <c>int</c>).</para></summary>
    private void EmitAddDisp16(EmitContext ctx, ushort pc, ref int extIndex)
    {
        int disp = unchecked((short)NextExtWord(pc, ref extIndex));   // sign-extend short -> int (selects Emit(OpCode,int))
        ctx.Il.Emit(OpCodes.Ldc_I4, disp);
        ctx.Il.Emit(OpCodes.Add);
    }

    /// <summary>abs.w: ea = (uint)(short)ext[0] — a sign-extended 16-bit absolute address (a compile-time
    /// constant baked as Ldc_I4). Leaves ea (uint) on the stack.</summary>
    private void EmitAbsW(EmitContext ctx, ushort pc, ref int extIndex)
    {
        int ea = unchecked((short)NextExtWord(pc, ref extIndex));   // sign-extend
        ctx.Il.Emit(OpCodes.Ldc_I4, ea);
    }

    /// <summary>abs.l: ea = (ext[0]&lt;&lt;16) | ext[1] — two words, high first (a compile-time constant). Leaves
    /// ea (uint) on the stack.</summary>
    private void EmitAbsL(EmitContext ctx, ushort pc, ref int extIndex)
    {
        uint hi = NextExtWord(pc, ref extIndex);
        uint lo = NextExtWord(pc, ref extIndex);
        uint ea = (hi << 16) | lo;
        ctx.Il.Emit(OpCodes.Ldc_I4, unchecked((int)ea));
    }

    /// <summary>d16(PC): ea = PcForEa + (int)(short)ext[0], where PcForEa = the operword address + 2 (the
    /// address of the first extension word — _eaPcBase, g.cs:152). Both terms are compile-time constants, so the
    /// whole EA is baked as a single Ldc_I4 (the JIT does NOT read the live PC field — its 16-bit-truncated PC
    /// would be wrong for the 68000, but the data axis never observes it because PcForEa is folded here).</summary>
    private void EmitPcRelD16(EmitContext ctx, ushort pc, ref int extIndex)
    {
        short disp = unchecked((short)NextExtWord(pc, ref extIndex));
        uint pcForEa = (uint)(pc + 2);
        uint ea = unchecked(pcForEa + (uint)disp);
        ctx.Il.Emit(OpCodes.Ldc_I4, unchecked((int)ea));
    }

    /// <summary>d8(An,Xn) / d8(PC,Xn): ComputeBriefIndex(base, ext[0]) — base + disp8 + index (g.cs:942). The
    /// base is An (banked for A7) for the An form, or the compile-time PcForEa (operword+2) for the PC form. The
    /// brief word's fields are compile-time constants: disp8 = (sbyte)(low byte); idxReg = bits 14-12; bit 15 =
    /// An-vs-Dn index; bit 11 = .w(sign-extend)/.l(full). Only the index-register VALUE is a runtime load.</summary>
    private void EmitM68kBriefIndex(EmitContext ctx, ushort pc, int anReg, bool isPc, ref int extIndex)
    {
        ILGenerator il = ctx.Il;
        ushort ext = NextExtWord(pc, ref extIndex);
        int disp = unchecked((sbyte)(byte)ext);          // signed 8-bit displacement (low byte)
        int idxReg = (ext >> 12) & 7;                    // bits 14-12: index register number
        bool idxIsAddr = (ext & 0x8000) != 0;            // bit 15: An vs Dn index
        bool idxIsLong = (ext & 0x0800) != 0;            // bit 11: 1 = full long, 0 = .w sign-extended

        // base
        if (isPc) il.Emit(OpCodes.Ldc_I4, unchecked((int)(uint)(pc + 2)));   // PcForEa = operword + 2 (constant)
        else EmitLoadAreg(ctx, anReg);                                       // An (banked for A7)
        // + disp8 (compile-time constant)
        il.Emit(OpCodes.Ldc_I4, disp);
        il.Emit(OpCodes.Add);
        // + index: load the index register VALUE (runtime), then .w-sign-extend if bit 11 == 0
        if (idxIsAddr) EmitLoadAreg(ctx, idxReg);                            // An index (banked for A7)
        else EmitLoadReg32(ctx, $"D{idxReg}");                              // Dn index
        if (!idxIsLong) { il.Emit(OpCodes.Conv_I2); il.Emit(OpCodes.Conv_I4); }   // .w sign-extend (short -> int)
        il.Emit(OpCodes.Add);                                               // base + disp + index (unchecked uint)
    }

    /// <summary>#imm (mode 7 reg 4): the value IS the extension word(s) — .b/.w = ext[0] masked, .l =
    /// (ext[0]&lt;&lt;16)|ext[1] (M68000Cpu.Move.cs:28-29). All compile-time constants, baked as Ldc_I4. Leaves
    /// the immediate (uint) on the stack — NO address.</summary>
    private void EmitImmOperand(EmitContext ctx, ushort pc, int size, ref int extIndex)
    {
        if (size == 2)
        {
            uint hi = NextExtWord(pc, ref extIndex);
            uint lo = NextExtWord(pc, ref extIndex);
            ctx.Il.Emit(OpCodes.Ldc_I4, unchecked((int)((hi << 16) | lo)));
        }
        else
        {
            uint w = NextExtWord(pc, ref extIndex);
            uint mask = size == 0 ? 0xFFu : 0xFFFFu;
            ctx.Il.Emit(OpCodes.Ldc_I4, unchecked((int)(w & mask)));
        }
    }

    // ── The MOVE / MOVEA / MOVEQ emit arm ──────────────────────────────────────────────────────────────────

    /// <summary>M6 PR-4a: can the MOVE-family row at <paramref name="pc"/> be emitted by EmitM68kMove, or must it
    /// fall back? MOVEQ is ALWAYS emittable (EmitM68kMoveQ needs no EA). For MOVE/MOVEA the source EA must be one
    /// EmitM68kEaRead handles (modes 0-6 + mode 7 reg 0-4 — abs.w/abs.l/d16(PC)/d8(PC,Xn)/#imm) and the dest EA
    /// one EmitM68kEaWrite handles (modes 0-6 + mode 7 reg 0/1 ONLY — abs.w/abs.l; mode 7 reg 2/3/4 are the
    /// PC-relative/immediate modes, which are ILLEGAL MOVE destinations on real hardware and which the writer
    /// cannot emit). A row whose EA is outside these sets is a DISCOVERY artifact (the block walk decoded a
    /// non-instruction lookahead word — see EmitInstruction) and must fall back rather than throw. The operword is
    /// a code-stream constant (the same _bus.Read16(pc) EmitM68kMove reads), so this is a pure compile-time
    /// decision.</summary>
    private bool CanEmitM68kMove(ushort pc, OpcodeDescriptor d)
    {
        if (d.Mnemonic == "MOVEQ") return true;            // no EA — EmitM68kMoveQ handles it wholly
        ushort operword = _bus.Read16(pc);
        int srcMode = (operword >> 3) & 7, srcReg = operword & 7;
        int dstMode = (operword >> 6) & 7, dstReg = (operword >> 9) & 7;   // the MOVE swap: mode=8-6, reg=11-9
        return IsM68kSrcEaHandled(srcMode, srcReg) && IsM68kDestEaHandled(dstMode, dstReg);
    }

    /// <summary>The EA modes EmitM68kEaRead emits: modes 0-6, and mode 7 reg 0-4 (abs.w/abs.l/d16(PC)/d8(PC,Xn)/
    /// #imm). Mirrors the switch in EmitM68kEaRead.</summary>
    private static bool IsM68kSrcEaHandled(int mode, int reg) =>
        mode < 7 ? mode <= 6 : reg <= 4;

    /// <summary>The EA modes EmitM68kEaWrite emits: modes 0-6, and mode 7 reg 0/1 ONLY (abs.w/abs.l). Mode 7 reg
    /// 2/3/4 (d16(PC)/d8(PC,Xn)/#imm) are illegal MOVE destinations and are NOT emittable. Mirrors the switch in
    /// EmitM68kEaWrite.</summary>
    private static bool IsM68kDestEaHandled(int mode, int reg) =>
        mode < 7 ? mode <= 6 : reg <= 1;

    /// <summary>M6 PR-5: the MEMORY-alterable dest EAs EmitM68kResolveEaAddr can address: modes 2-6 (no Dn/An
    /// direct) + mode 7 reg 0/1 (abs.w/abs.l). EXCLUDES mode 0 (Dn) and mode 1 (An) — those are register-direct
    /// destinations the ALU arms handle inline (NOT via the memory-EA resolver), and a register-direct toEa RegEa
    /// is illegal/lookahead garbage. Mirrors the switch in EmitM68kResolveEaAddr.</summary>
    private static bool IsM68kMemDestHandled(int mode, int reg) =>
        (mode >= 2 && mode <= 6) || (mode == 7 && reg <= 1);

    /// <summary>M6 PR-4: the 68000 MOVE/MOVEA/MOVEQ emit arm. Decodes the operword's EA matrix at emit time
    /// (DECISION P3), resolves+reads the source EA (FIRST, so its An mutation lands before the dest), stashes
    /// the operand, sets the MOVE CCR (MOVEA/MOVEQ differ), resolves+writes the dest, charges BaseCycles.
    /// Mirrors MoveExecute / MoveAExecute (M68000Cpu.Move.cs:78-120) on the DATA axis.</summary>
    private void EmitM68kMove(EmitContext ctx, ushort pc, OpcodeDescriptor d)
    {
        if (d.Mnemonic == "MOVEQ") { EmitM68kMoveQ(ctx, pc, d); return; }

        ushort operword = _bus.Read16(pc);             // the operword is a code-stream constant
        int size = M68kMoveSize(operword);             // re-decoded from the operword (Move size encoding)
        int srcMode = (operword >> 3) & 7, srcReg = operword & 7;
        int dstReg = (operword >> 9) & 7, dstMode = (operword >> 6) & 7;   // the MOVE swap: mode=8-6, reg=11-9
        bool isMovea = dstMode == 1;                   // MOVE to An = MOVEA (no CCR; whole An; .w sign-extends)

        int extIndex = 0;
        // 1) source operand (sized) -> stash in M68kValueLocal (the dest-EA resolution then has a clean stack)
        EmitM68kEaRead(ctx, pc, srcMode, srcReg, size, ref extIndex);
        ctx.Il.Emit(OpCodes.Conv_U4);                  // normalize .b/.w (int) to uint for the staging local
        ctx.Il.Emit(OpCodes.Stloc, ctx.M68kValueLocal);

        if (isMovea)
        {
            // MOVEA: whole An, .w sign-extends to 32; NO CCR. (Dest register = bits 11-9; A7 banked.)
            ctx.Il.Emit(OpCodes.Ldloc, ctx.M68kValueLocal);
            if (size == 1) { ctx.Il.Emit(OpCodes.Conv_I2); ctx.Il.Emit(OpCodes.Conv_I4); }   // .w -> 32 (sign-extend)
            EmitStoreAreg(ctx, dstReg);
        }
        else
        {
            // 2) MOVE CCR from `value` (N/Z, V=C=0, X untouched) — done BEFORE the dest write (the oracle sets
            //    CCR after the write, but CCR depends only on `value`, which is already staged; order is moot
            //    on the data axis). Computing it here keeps the dest-write stack discipline simple.
            EmitM68kMoveCcr(ctx, ctx.M68kValueLocal, size);
            // 3) dest: size-aware EA write (memory or Dn partial) — value is in M68kValueLocal.
            EmitM68kEaWrite(ctx, pc, dstMode, dstReg, size, ref extIndex);
        }

        // 4) charge the coarse BaseCycles once (DECISION C-jit / T) — keeps the >=1-cycle budget invariant.
        EmitChargeCycles(ctx, d.BaseCycles);
    }

    /// <summary>M6 PR-4: MOVEQ #imm8,Dn — imm8 sign-extended to .l into the WHOLE Dn; Logic CCR (N/Z from the
    /// result, V=C=0, X untouched — identical to the MOVE N/Z at .l). dn = (operword>>9)&amp;7; result =
    /// (int)(sbyte)(operword&amp;0xFF). The imm is a compile-time constant (operword is constant), so the whole
    /// result is baked as Ldc_I4. Charges a fixed 4 clocks. (M68000Cpu.SystemMisc.cs:20-26.)</summary>
    private void EmitM68kMoveQ(EmitContext ctx, ushort pc, OpcodeDescriptor d)
    {
        ushort operword = _bus.Read16(pc);
        int dn = (operword >> 9) & 7;
        int result = unchecked((sbyte)(byte)(operword & 0xFF));   // sign-extend imm8 -> int (.l value)

        ctx.Il.Emit(OpCodes.Ldc_I4, result);
        ctx.Il.Emit(OpCodes.Conv_U4);
        ctx.Il.Emit(OpCodes.Stloc, ctx.M68kValueLocal);           // result (for the CCR helper + the store)
        ctx.Il.Emit(OpCodes.Ldloc, ctx.M68kValueLocal);
        EmitStoreReg32(ctx, $"D{dn}");                            // Dn = result (whole 32)
        EmitM68kMoveCcr(ctx, ctx.M68kValueLocal, 2);              // Logic CCR == MOVE N/Z at size .l
        EmitChargeCycles(ctx, d.BaseCycles);                      // MOVEQ = 4 clocks
    }

    /// <summary>M6 PR-4: the MOVE CCR — SR = (SR &amp; 0xFFF0) | (N?0x08:0) | (Z?0x04:0). N = (value &amp;
    /// signbit(size)) != 0; Z = (value &amp; mask(size)) == 0; V(0x02)=C(0x01)=0; X(0x10) + the system byte are
    /// PRESERVED (the 0xFFF0 mask keeps bits 4-15). Computes N and Z into flat int terms (no conditional-OR
    /// stack juggling), then OR-s into the masked SR. Mirrors SetMoveCcr (M68000Cpu.Move.cs:64-75).</summary>
    private void EmitM68kMoveCcr(EmitContext ctx, LocalBuilder valueLocal, int size)
    {
        ILGenerator il = ctx.Il;
        uint mask = size == 0 ? 0xFFu : size == 1 ? 0xFFFFu : 0xFFFFFFFFu;
        uint signbit = size == 0 ? 0x80u : size == 1 ? 0x8000u : 0x80000000u;

        // newSr = (SR & 0xFFF0)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _m68kSR!);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)0xFFF0));
        il.Emit(OpCodes.And);
        // | (N ? 0x08 : 0):  N = (value & signbit) != 0  ->  ((value & signbit) != 0 ? 1 : 0) * 0x08
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)signbit));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Cgt_Un);                 // (value & signbit) != 0  ->  1/0
        il.Emit(OpCodes.Ldc_I4, 0x08);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Or);
        // | (Z ? 0x04 : 0):  Z = (value & mask) == 0  ->  ((value & mask) == 0 ? 1 : 0) * 0x04
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)mask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);                    // (value & mask) == 0  ->  1/0
        il.Emit(OpCodes.Ldc_I4, 0x04);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Or);
        // SR = (ushort)newSr
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);   // stash the new SR (uint local; receiver-below-value fix)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _m68kSR!);
    }

    // ── Size decode + the footprint oracle (cross-check) ───────────────────────────────────────────────────

    /// <summary>M6 PR-4: the MOVE/MOVEA operand size from the operword — the Move size encoding (operword bits
    /// 13-12: 01=.b, 11=.w, 10=.l), matching the generator's MapSize(enc:1) (g.cs:844-846). The descriptor
    /// carries no key (its Opcode byte is 0x00), so size is re-decoded from the operword exactly as the decode
    /// walk does (DECISION P3).</summary>
    private static int M68kMoveSize(ushort operword)
    {
        uint bits = (uint)((operword >> 12) & 3);
        return bits switch { 1u => 0, 3u => 1, 2u => 2, _ => 0 };   // 01=b, 11=w, 10=l
    }

    /// <summary>M6 PR-4: the PC footprint of a 68000 MOVE/MOVEA/MOVEQ beyond... actually the FULL footprint
    /// (operword 2 bytes + source ext words + dest ext words), in bytes. The 68000's generated Decode() already
    /// returns this exact value as r.Length (UnitsConsumed*2), so the Discover walk does NOT add this (it would
    /// double-count — see Discover). This standalone oracle exists for the FallbackEmitCount discovery unit
    /// tests to assert the per-mode ext-word arithmetic matches r.Length. MOVEQ has no ext words (2 bytes).</summary>
    private static int M68kEmitOperandBytes(ushort operword, string mnemonic)
    {
        if (mnemonic == "MOVEQ") return 2;   // operword only
        int size = M68kMoveSize(operword);
        int srcMode = (operword >> 3) & 7, srcReg = operword & 7;
        int dstReg = (operword >> 9) & 7, dstMode = (operword >> 6) & 7;
        return 2 + 2 * (M68kExtWordCount(srcMode, srcReg, size) + M68kExtWordCount(dstMode, dstReg, size));
    }

    /// <summary>The extension-word count an EA mode/reg/size consumes (mirrors ExtensionWordCount, g.cs:853):
    /// mode 5/6 = 1; mode 7 reg 0/2/3 = 1, reg 1 = 2, reg 4 (#imm) = (.l ? 2 : 1); else 0.</summary>
    private static int M68kExtWordCount(int mode, int reg, int size) => mode switch
    {
        5 or 6 => 1,
        7 => reg switch { 0 => 1, 1 => 2, 2 => 1, 3 => 1, 4 => size == 2 ? 2 : 1, _ => 0 },
        _ => 0,
    };

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════
    // M6 PR-5: the 68000 integer-ALU emit arm + the shared CCR-compute helpers (DECISION X1).
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>M6 PR-5: where xIn comes from for the arithmetic CCR. Zero = plain ADD/SUB/CMP (xIn forced 0 — the
    /// hard-wired false, M68000Cpu.Alu.cs:262-263). LiveX = ADDX/SUBX (the live X is an INPUT to carry/borrow).</summary>
    private enum M68kXIn { Zero, LiveX }

    /// <summary>M6 PR-5: the X-output tail variant. Arith = X = C (the OUTPUT-X tail, plain ADD/SUB). Cmp = X
    /// RESTORED from old (CMP never touches X). ArithX = sticky Z + the Arith X=C tail (ADDX/SUBX).</summary>
    private enum M68kCcrVariant { Arith, Cmp, ArithX }

    /// <summary>M6 PR-5: the per-family ALU op + its CCR rule + shape, decoded from d.Mnemonic.</summary>
    private enum M68kAluFn { Add, Sub, And, Or, Eor }
    private enum M68kAluCcr { Arith, Cmp, ArithX, Logic, None }
    private enum M68kAluShape { RegEa, ImmEa, QuickEa, AddrEa, XAlu }

    private readonly struct M68kAluFam
    {
        public readonly M68kAluFn Fn;
        public readonly M68kAluCcr Ccr;
        public readonly M68kAluShape Shape;
        public readonly bool IsSub;          // true for SUB/CMP/SUBX (the borrow form)
        public readonly bool WritesResult;   // false for CMP/CMPI/CMPA
        public M68kAluFam(M68kAluFn fn, M68kAluCcr ccr, M68kAluShape shape, bool isSub, bool writes)
        { Fn = fn; Ccr = ccr; Shape = shape; IsSub = isSub; WritesResult = writes; }
    }

    /// <summary>M6 PR-5: classify the family from the descriptor mnemonic (the descriptor key already disambiguated
    /// the first-match-wins family, so the mnemonic is authoritative). Mirrors the interpreter registrations
    /// (M68000Cpu.Alu.cs:323-403, 476-479): ADD/SUB/AND/OR/EOR/CMP = RegEa; *I = ImmEa; ADDQ/SUBQ = QuickEa;
    /// ADDA/SUBA/CMPA = AddrEa; ADDX/SUBX = XAlu. The CCR rule + isSub + writesResult per the recon table.</summary>
    private static M68kAluFam M68kAluFamily(string mnemonic) => mnemonic switch
    {
        // RegEa
        "ADD" => new(M68kAluFn.Add, M68kAluCcr.Arith, M68kAluShape.RegEa, false, true),
        "SUB" => new(M68kAluFn.Sub, M68kAluCcr.Arith, M68kAluShape.RegEa, true, true),
        "AND" => new(M68kAluFn.And, M68kAluCcr.Logic, M68kAluShape.RegEa, false, true),
        "OR"  => new(M68kAluFn.Or,  M68kAluCcr.Logic, M68kAluShape.RegEa, false, true),
        "EOR" => new(M68kAluFn.Eor, M68kAluCcr.Logic, M68kAluShape.RegEa, false, true),
        "CMP" => new(M68kAluFn.Sub, M68kAluCcr.Cmp,   M68kAluShape.RegEa, true, false),
        // ImmEa
        "ADDI" => new(M68kAluFn.Add, M68kAluCcr.Arith, M68kAluShape.ImmEa, false, true),
        "SUBI" => new(M68kAluFn.Sub, M68kAluCcr.Arith, M68kAluShape.ImmEa, true, true),
        "ANDI" => new(M68kAluFn.And, M68kAluCcr.Logic, M68kAluShape.ImmEa, false, true),
        "ORI"  => new(M68kAluFn.Or,  M68kAluCcr.Logic, M68kAluShape.ImmEa, false, true),
        "EORI" => new(M68kAluFn.Eor, M68kAluCcr.Logic, M68kAluShape.ImmEa, false, true),
        "CMPI" => new(M68kAluFn.Sub, M68kAluCcr.Cmp,   M68kAluShape.ImmEa, true, false),
        // QuickEa
        "ADDQ" => new(M68kAluFn.Add, M68kAluCcr.Arith, M68kAluShape.QuickEa, false, true),
        "SUBQ" => new(M68kAluFn.Sub, M68kAluCcr.Arith, M68kAluShape.QuickEa, true, true),
        // AddrEa (An dest)
        "ADDA" => new(M68kAluFn.Add, M68kAluCcr.None, M68kAluShape.AddrEa, false, true),
        "SUBA" => new(M68kAluFn.Sub, M68kAluCcr.None, M68kAluShape.AddrEa, true, true),
        "CMPA" => new(M68kAluFn.Sub, M68kAluCcr.Cmp,  M68kAluShape.AddrEa, true, false),
        // XAlu
        "ADDX" => new(M68kAluFn.Add, M68kAluCcr.ArithX, M68kAluShape.XAlu, false, true),
        "SUBX" => new(M68kAluFn.Sub, M68kAluCcr.ArithX, M68kAluShape.XAlu, true, true),
        _ => throw new EmulationException($"M68kAluFamily: not a PR-5 ALU mnemonic '{mnemonic}'"),
    };

    /// <summary>M6 PR-5: the ALU size decode from the operword. RegEa/ImmEa/QuickEa/XAlu use the STANDARD size
    /// field (bits 7-6: 00=.b/01=.w/10=.l). AddrEa (ADDA/SUBA/CMPA) carries the size in opmode bit 8: 0=.w(1)/
    /// 1=.l(2) — matching the decoder's +1 remap (so the body always sees a genuine 1=.w/2=.l index). The
    /// descriptor's Opcode byte is 0x00 and carries no key (exactly like MOVE), so size is re-decoded here.</summary>
    private static int M68kAluSize(ushort operword, M68kAluShape shape)
    {
        if (shape == M68kAluShape.AddrEa)
            return ((operword >> 8) & 1) == 0 ? 1 : 2;   // .w / .l (genuine 1=.w/2=.l index)
        uint bits = (uint)((operword >> 6) & 3);
        return bits switch { 0u => 0, 1u => 1, 2u => 2, _ => 0 };   // standard 00=b,01=w,10=l
    }

    /// <summary>M6 PR-5: can the ALU-family row at <paramref name="pc"/> be emitted, or must it fall back? The EA
    /// resolver (EmitM68kEaRead) handles src modes 0-6 + mode 7 reg 0-4; the DataAlterable dest forms (toEa RegEa,
    /// ImmEa, QuickEa) write modes 0-6 + mode 7 reg 0/1. A row whose decoded EA is outside the set the form needs
    /// is a DISCOVERY artifact (lookahead garbage) and falls back rather than throwing. ADDX/SUBX (XAlu) decode no
    /// EA from the matrix (the regs are bit fields) so they are always emittable.</summary>
    private bool CanEmitM68kAlu(ushort pc, OpcodeDescriptor d)
    {
        var fam = M68kAluFamily(d.Mnemonic);
        if (fam.Shape == M68kAluShape.XAlu) return true;   // no EA matrix — reg/predec bit fields only
        ushort operword = _bus.Read16(pc);
        int srcMode = (operword >> 3) & 7, srcReg = operword & 7;
        switch (fam.Shape)
        {
            case M68kAluShape.RegEa:
            {
                bool toEa = (operword & 0x0100) != 0;
                // toEa=false: the EA is a SOURCE read (DataAddressing — any readable EA). toEa=true: the EA is a
                // MEMORY-alterable DEST (read + RMW write-back). The toEa direction requires a MEMORY EA on real
                // hardware (ADD Dn,<ea> has ea in modes 2-6 + 7/0/1 only — a Dn/An direct toEa is illegal/lookahead
                // garbage the EA resolver cannot address). CMP (writesResult=false) has NO toEa form at all.
                if (!toEa) return IsM68kSrcEaHandled(srcMode, srcReg);
                if (!fam.WritesResult) return false;       // CMP toEa: not a real form
                // toEa dest: a Dn DIRECT (mode 0 — the EOR Dn,Dn form, written to the register inline) OR a MEMORY-
                // alterable EA (modes 2-6 + 7/0/1 — the resolver-addressable RMW set). An direct (mode 1) and the
                // PC-relative/#imm modes (7/2/3/4) are NOT alterable dests -> fall back.
                return srcMode == 0 || IsM68kMemDestHandled(srcMode, srcReg);
            }
            case M68kAluShape.ImmEa:
            case M68kAluShape.QuickEa:
                // The EA is the alterable dest: a Dn DIRECT (mode 0, register write — handled inline), An DIRECT
                // (mode 1: QuickEa's whole-An special case / ImmEa is illegal-to-An, but the QuickEa arm handles it
                // and a lookahead ImmEa-to-An never reaches a real run), or a MEMORY-alterable EA (modes 2-6 + 7/0/1
                // — the resolver-addressable set). Anything else (mode 7 reg 2/3/4: PC-relative/#imm) is not an
                // alterable dest -> fall back.
                return srcMode == 0 || srcMode == 1 || IsM68kMemDestHandled(srcMode, srcReg);
            case M68kAluShape.AddrEa:
                // ADDA/SUBA/CMPA: the EA is a SOURCE read (any readable EA, incl. An direct + #imm + PC-relative).
                return IsM68kSrcEaHandled(srcMode, srcReg);
            default:
                return false;
        }
    }

    /// <summary>M6 PR-5: push the live X bit (0 or 1) — (SR &amp; 0x10) != 0 ? 1 : 0. Leaves an int 0/1 on the
    /// stack. Only ADDX/SUBX read the live X; ADD/SUB/CMP stage a literal 0 (the xIn:false hard-wire).</summary>
    private void EmitM68kLiveX(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _m68kSR!);
        il.Emit(OpCodes.Ldc_I4, 0x10);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Cgt_Un);                   // (SR & 0x10) != 0 -> 1/0
    }

    /// <summary>M6 PR-5 (DECISION X1): the parametrized arithmetic CCR — a VERBATIM IL transcription of
    /// AluCcr.Arith + the ArithAdd/ArithSub/Cmp/ArithX wrappers (M68000Cpu.Alu.cs:227-305). aLocal/bLocal hold the
    /// masked inputs; resultLocal the masked result; size 0/1/2. <paramref name="xInSource"/> picks the xIn source
    /// (Zero = literal 0; LiveX = read SR's X NOW); <paramref name="variant"/> picks the X-output tail (Arith: X=C;
    /// Cmp: restore old X; ArithX: sticky Z). <paramref name="isSub"/> picks the add vs sub carry/borrow + overflow.
    /// Builds the new CCR byte into TmpInt, then writes SR = (ushort)((SR &amp; 0xFF00) | ccr).
    /// <para>Scratch usage: HiLocal=oldCcr, LoLocal=r (masked result), SumLocal=the running ccr int, TmpInt=n/z/v/c
    /// scratch, TmpLong=the ulong carry/borrow accumulator. NONE of these are AddrLocal/EaLocal/DataLocal (the bus-
    /// access clobber set) NOR the dedicated M68kA/B/Result/XIn locals the caller holds the operands in — so the CCR
    /// compute never disturbs a live operand. All bus access is already DONE before the CCR compute is emitted.</para></summary>
    private void EmitM68kArithCcr(EmitContext ctx, LocalBuilder aLocal, LocalBuilder bLocal,
                                  LocalBuilder resultLocal, LocalBuilder xInLocal, int size,
                                  M68kXIn xInSource, M68kCcrVariant variant, bool isSub)
    {
        ILGenerator il = ctx.Il;
        uint m = size == 0 ? 0xFFu : size == 1 ? 0xFFFFu : 0xFFFFFFFFu;
        uint sb = size == 0 ? 0x80u : size == 1 ? 0x8000u : 0x80000000u;

        // xIn (0 or 1) into xInLocal — LiveX reads SR's X NOW; Zero stages a literal 0.
        if (xInSource == M68kXIn.LiveX) EmitM68kLiveX(ctx); else il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, xInLocal);

        // oldCcr = (byte)(SR & 0xFF)  -> HiLocal
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _m68kSR!);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);

        // r = result & m  -> LoLocal
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)m));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);

        // ── ccr = (oldCcr & ~0x1F)   (clear X N Z V C; keep the system-byte-shadow bits) -> SumLocal (the running ccr int)
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)~0x1F));   // ~0x1F = 0xFFFFFFE0; & keeps only bits 5-7 of the byte
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.SumLocal);

        // ── N: if ((r & sb) != 0) ccr |= 0x08
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)sb));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Cgt_Un);                          // (r & sb) != 0 -> 1/0
        il.Emit(OpCodes.Ldc_I4, 0x08);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldloc, ctx.SumLocal);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc, ctx.SumLocal);

        // ── Z: if (r == 0) ccr |= 0x04
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);                             // r == 0 -> 1/0
        il.Emit(OpCodes.Ldc_I4, 0x04);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldloc, ctx.SumLocal);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc, ctx.SumLocal);

        // ── C and V (the carry/borrow + overflow). Compute c (0/1) into TmpInt, then V folds in.
        if (!isSub)
        {
            // full = (ulong)(a & m) + (ulong)(b & m) + (ulong)xIn   -> TmpLong
            il.Emit(OpCodes.Ldloc, aLocal);
            il.Emit(OpCodes.Ldc_I4, unchecked((int)m)); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Conv_U8);
            il.Emit(OpCodes.Ldloc, bLocal);
            il.Emit(OpCodes.Ldc_I4, unchecked((int)m)); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Conv_U8);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldloc, xInLocal);
            il.Emit(OpCodes.Conv_U8);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, ctx.TmpLong);
            // c = (full & ~(ulong)m) != 0
            il.Emit(OpCodes.Ldloc, ctx.TmpLong);
            il.Emit(OpCodes.Ldc_I8, unchecked((long)~(ulong)m));
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I8, 0L);
            il.Emit(OpCodes.Cgt_Un);                      // (full & ~m) != 0 -> 1/0
            il.Emit(OpCodes.Stloc, ctx.TmpInt);           // c -> TmpInt
            // v = (((a ^ r) & (b ^ r)) & sb) != 0
            il.Emit(OpCodes.Ldloc, aLocal);
            il.Emit(OpCodes.Ldloc, ctx.LoLocal);
            il.Emit(OpCodes.Xor);                         // a ^ r
            il.Emit(OpCodes.Ldloc, bLocal);
            il.Emit(OpCodes.Ldloc, ctx.LoLocal);
            il.Emit(OpCodes.Xor);                         // b ^ r
            il.Emit(OpCodes.And);                         // (a^r) & (b^r)
            il.Emit(OpCodes.Ldc_I4, unchecked((int)sb));
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Cgt_Un);                      // v -> 1/0
        }
        else
        {
            // sub = (ulong)(b & m) + (ulong)xIn   -> TmpLong
            il.Emit(OpCodes.Ldloc, bLocal);
            il.Emit(OpCodes.Ldc_I4, unchecked((int)m)); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Conv_U8);
            il.Emit(OpCodes.Ldloc, xInLocal);
            il.Emit(OpCodes.Conv_U8);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, ctx.TmpLong);
            // c = (ulong)(a & m) < sub
            il.Emit(OpCodes.Ldloc, aLocal);
            il.Emit(OpCodes.Ldc_I4, unchecked((int)m)); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Conv_U8);
            il.Emit(OpCodes.Ldloc, ctx.TmpLong);
            il.Emit(OpCodes.Clt_Un);                      // (a&m) < sub -> 1/0
            il.Emit(OpCodes.Stloc, ctx.TmpInt);           // c -> TmpInt
            // v = (((a ^ (b & m)) & (a ^ r)) & sb) != 0
            il.Emit(OpCodes.Ldloc, aLocal);
            il.Emit(OpCodes.Ldloc, bLocal);
            il.Emit(OpCodes.Ldc_I4, unchecked((int)m)); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Xor);                         // a ^ (b & m)
            il.Emit(OpCodes.Ldloc, aLocal);
            il.Emit(OpCodes.Ldloc, ctx.LoLocal);
            il.Emit(OpCodes.Xor);                         // a ^ r
            il.Emit(OpCodes.And);                         // (a ^ (b&m)) & (a ^ r)
            il.Emit(OpCodes.Ldc_I4, unchecked((int)sb));
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Cgt_Un);                      // v -> 1/0
        }
        // stack top = v (0/1). ccr |= v * 0x02
        il.Emit(OpCodes.Ldc_I4, 0x02);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldloc, ctx.SumLocal);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc, ctx.SumLocal);
        // ccr |= c * 0x01
        il.Emit(OpCodes.Ldloc, ctx.TmpInt);
        il.Emit(OpCodes.Ldc_I4, 0x01);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldloc, ctx.SumLocal);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc, ctx.SumLocal);

        // ── The X tail per variant ──────────────────────────────────────────────────────────────────────────
        switch (variant)
        {
            case M68kCcrVariant.Arith:
            case M68kCcrVariant.ArithX:
                // X = C: if (c) ccr |= 0x10 else ccr &= ~0x10. (c in TmpInt as 0/1.)  ccr = (ccr & ~0x10) | (c<<4)
                il.Emit(OpCodes.Ldloc, ctx.SumLocal);
                il.Emit(OpCodes.Ldc_I4, unchecked((int)~0x10));
                il.Emit(OpCodes.And);                     // ccr & ~0x10
                il.Emit(OpCodes.Ldloc, ctx.TmpInt);       // c
                il.Emit(OpCodes.Ldc_I4, 4);
                il.Emit(OpCodes.Shl);                     // c << 4 (== c ? 0x10 : 0)
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Stloc, ctx.SumLocal);
                break;
            case M68kCcrVariant.Cmp:
                // X restored from old: ccr = (ccr & ~0x10) | (oldCcr & 0x10)
                il.Emit(OpCodes.Ldloc, ctx.SumLocal);
                il.Emit(OpCodes.Ldc_I4, unchecked((int)~0x10));
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Ldloc, ctx.HiLocal);      // oldCcr
                il.Emit(OpCodes.Ldc_I4, 0x10);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Stloc, ctx.SumLocal);
                break;
        }

        // ── ArithX sticky Z: clear the freshly-set Z, re-OR the OLD Z only if the result is zero.
        if (variant == M68kCcrVariant.ArithX)
        {
            // ccr &= ~0x04
            il.Emit(OpCodes.Ldloc, ctx.SumLocal);
            il.Emit(OpCodes.Ldc_I4, unchecked((int)~0x04));
            il.Emit(OpCodes.And);
            // | ((r == 0) ? (oldCcr & 0x04) : 0)   ==   | ((r==0 ? 1 : 0) * (oldCcr & 0x04))
            il.Emit(OpCodes.Ldloc, ctx.LoLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ceq);                         // (r == 0) -> 1/0
            il.Emit(OpCodes.Ldloc, ctx.HiLocal);
            il.Emit(OpCodes.Ldc_I4, 0x04);
            il.Emit(OpCodes.And);                         // oldCcr & 0x04
            il.Emit(OpCodes.Mul);                         // (r==0?1:0) * (oldCcr & 0x04)
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Stloc, ctx.SumLocal);
        }

        // ── SR = (ushort)((SR & 0xFF00) | (ccr & 0xFF)). Stage the new SR through a local (receiver-below-value).
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _m68kSR!);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)0xFF00));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, ctx.SumLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);       // new SR (receiver-below-value fix — like EmitM68kMoveCcr)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _m68kSR!);
    }

    /// <summary>M6 PR-5: route to the per-family CCR helper. Arith -> Arith variant, xIn Zero; Cmp -> Cmp variant
    /// (isSub forced true), xIn Zero; ArithX -> ArithX variant, xIn LiveX; Logic -> EmitM68kMoveCcr (the reused
    /// PR-4 Logic rule: N/Z, V=C=0, X untouched); None -> ADDA/SUBA set no CCR. The xInLocal is the live-X local
    /// the caller filled (XAlu) or M68kXInLocal (the helper restages a 0 for the non-X families).</summary>
    private void EmitM68kFamilyCcr(EmitContext ctx, M68kAluFam fam, LocalBuilder a, LocalBuilder b,
                                   LocalBuilder result, LocalBuilder xInLocal, int size)
    {
        switch (fam.Ccr)
        {
            case M68kAluCcr.Arith:
                EmitM68kArithCcr(ctx, a, b, result, xInLocal, size, M68kXIn.Zero, M68kCcrVariant.Arith, fam.IsSub);
                break;
            case M68kAluCcr.Cmp:
                EmitM68kArithCcr(ctx, a, b, result, xInLocal, size, M68kXIn.Zero, M68kCcrVariant.Cmp, isSub: true);
                break;
            case M68kAluCcr.ArithX:
                EmitM68kArithCcr(ctx, a, b, result, xInLocal, size, M68kXIn.LiveX, M68kCcrVariant.ArithX, fam.IsSub);
                break;
            case M68kAluCcr.Logic:
                EmitM68kMoveCcr(ctx, result, size);   // N/Z, V=C=0, X untouched (reused — MOVE and Logic share it)
                break;
            case M68kAluCcr.None:
                break;                                 // ADDA/SUBA set no CCR
        }
    }

    /// <summary>M6 PR-5: the pure ALU op a op b, masked to size, left on the stack as uint. Add/Sub/And/Or/Eor
    /// (M68000Cpu.Alu.cs:30-34). The interpreter computes full-width then masks; the carry/overflow live in the
    /// CCR helper, so here we just compute the value and mask.</summary>
    private void EmitM68kAluOp(EmitContext ctx, M68kAluFam fam, LocalBuilder a, LocalBuilder b, int size)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, a);
        il.Emit(OpCodes.Ldloc, b);
        switch (fam.Fn)
        {
            case M68kAluFn.Add: il.Emit(OpCodes.Add); break;
            case M68kAluFn.Sub: il.Emit(OpCodes.Sub); break;
            case M68kAluFn.And: il.Emit(OpCodes.And); break;
            case M68kAluFn.Or:  il.Emit(OpCodes.Or);  break;
            case M68kAluFn.Eor: il.Emit(OpCodes.Xor); break;
        }
        uint mask = size == 0 ? 0xFFu : size == 1 ? 0xFFFFu : 0xFFFFFFFFu;
        if (size != 2) { il.Emit(OpCodes.Ldc_I4, unchecked((int)mask)); il.Emit(OpCodes.And); }
    }

    /// <summary>M6 PR-5: the 68000 integer-ALU emit arm. Mirrors BinaryAluExecute (M68000Cpu.Alu.cs:55-139) on the
    /// DATA axis (incl. SR/X). Family + shape + size are decoded from d.Mnemonic + the operword (DECISION P3);
    /// each shape has its own sub-form. Charges the coarse BaseCycles once (DECISION T).</summary>
    private void EmitM68kAlu(EmitContext ctx, ushort pc, OpcodeDescriptor d)
    {
        ushort operword = _bus.Read16(pc);
        var fam = M68kAluFamily(d.Mnemonic);
        int size = M68kAluSize(operword, fam.Shape);

        switch (fam.Shape)
        {
            case M68kAluShape.RegEa:   EmitM68kAluRegEa(ctx, pc, operword, size, fam); break;
            case M68kAluShape.ImmEa:   EmitM68kAluImmEa(ctx, pc, operword, size, fam); break;
            case M68kAluShape.QuickEa: EmitM68kAluQuickEa(ctx, pc, operword, size, fam); break;
            case M68kAluShape.AddrEa:  EmitM68kAluAddrEa(ctx, pc, operword, size, fam); break;
            case M68kAluShape.XAlu:    EmitM68kAluX(ctx, pc, operword, size, fam); break;
        }
        EmitChargeCycles(ctx, d.BaseCycles);   // coarse cycle charge (PR-4 DECISION T)
    }

    /// <summary>M6 PR-5: the RegEa form (ADD/SUB/AND/OR/EOR/CMP). Direction bit (operword &amp; 0x100):
    ///   toEa=false: dest=Dn; a=Dn, b=read(ea); result=a op b; write Dn (size-aware); CCR(a,b,result).
    ///   toEa=true:  dest=ea (the EA is operand A AND the dest); a=read(ea), b=Dn; result=a op b; write-back to the
    ///               SAME ea; CCR(a,b,result). For a Dn-DIRECT ea (mode 0) the dest is a data register (the EOR
    ///               Dn,Dn form — EOR is always toEa direction); for memory the ea is resolved ONCE (RMW).
    /// CMP (writesResult=false) computes a-b for the CCR but writes nothing (and only the toEa=false direction is a
    /// real CMP form — CanEmitM68kAlu fell back a toEa CMP). a/b/result live in the dedicated M68kA/B/Result locals
    /// so the bus read/write never clobbers them.</summary>
    private void EmitM68kAluRegEa(EmitContext ctx, ushort pc, ushort operword, int size, M68kAluFam fam)
    {
        ILGenerator il = ctx.Il;
        int dnReg = (operword >> 9) & 7, srcMode = (operword >> 3) & 7, srcReg = operword & 7;
        bool toEa = (operword & 0x0100) != 0;
        int extIndex = 0;

        if (!toEa)
        {
            // b = read(ea); a = Dn(sized)
            EmitM68kEaRead(ctx, pc, srcMode, srcReg, size, ref extIndex);
            il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Stloc, ctx.M68kBLocal);
            EmitLoadDataRegSized(ctx, $"D{dnReg}", size);
            il.Emit(OpCodes.Stloc, ctx.M68kALocal);
            // result = a op b
            EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, size);
            il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
            if (fam.WritesResult)
            {
                il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
                EmitStoreDataRegSized(ctx, $"D{dnReg}", size);
            }
            EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, size);
        }
        else if (srcMode == 0)
        {
            // Dn-DIRECT dest (the EOR Dn,Dn form — EOR is always toEa direction; ADD/SUB/AND/OR with a Dn dest use
            // toEa=false instead, but a Dn-direct toEa is a legal EOR encoding and must emit, not fall back). The EA
            // (srcReg's Dn) is operand A AND the dest; b = the dnReg operand; result -> write Dn(srcReg).
            EmitLoadDataRegSized(ctx, $"D{srcReg}", size);
            il.Emit(OpCodes.Stloc, ctx.M68kALocal);
            EmitLoadDataRegSized(ctx, $"D{dnReg}", size);
            il.Emit(OpCodes.Stloc, ctx.M68kBLocal);
            EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, size);
            il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
            if (fam.WritesResult)
            {
                il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
                EmitStoreDataRegSized(ctx, $"D{srcReg}", size);
            }
            EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, size);
        }
        else
        {
            // memory dest: resolve ea ONCE into M68kAddr2Local; a = read(ea); b = Dn; result; write-back to ea.
            EmitM68kResolveEaAddr(ctx, pc, srcMode, srcReg, size, ref extIndex);   // address -> M68kAddr2Local
            // a = read(ea)  (load the address from the survivor local each time — the read helper clobbers AddrLocal)
            il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
            EmitM68kReadSized(ctx, size);
            il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Stloc, ctx.M68kALocal);
            // b = Dn
            EmitLoadDataRegSized(ctx, $"D{dnReg}", size);
            il.Emit(OpCodes.Stloc, ctx.M68kBLocal);
            // result = a op b
            EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, size);
            il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
            // write-back to the SAME ea (RMW; .l is low-word-first). fam.WritesResult is true here (CMP fell back).
            il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
            il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
            EmitM68kWriteSizedRmw(ctx, size);
            EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, size);
        }
    }

    /// <summary>M6 PR-5: the ImmEa form (ADDI/SUBI/ANDI/ORI/EORI/CMPI). The #imm is the LEADING extension word(s)
    /// (immCount = .l ? 2 : 1), the EA's words FOLLOW (M68000Cpu.Alu.cs:99-110). Consume the imm ext words from
    /// the FRONT (advancing extIndex), THEN resolve+read the dest EA (its own ext words now at ext[immCount..]).
    /// dest=EA: a=read(ea), b=imm; result=a op b; write-back to the SAME ea (RMW; CMPI no write). For a Dn/An
    /// direct dest the EA resolver reads/writes the register; for a memory dest it resolves the address ONCE.</summary>
    private void EmitM68kAluImmEa(EmitContext ctx, ushort pc, ushort operword, int size, M68kAluFam fam)
    {
        ILGenerator il = ctx.Il;
        int srcMode = (operword >> 3) & 7, srcReg = operword & 7;
        int extIndex = 0;

        // 1) read the #imm from the LEADING ext word(s) (advances extIndex past them) -> M68kBLocal
        EmitImmOperand(ctx, pc, size, ref extIndex);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stloc, ctx.M68kBLocal);

        if (srcMode == 0)
        {
            // Dn direct dest: a = Dn(sized); result; write Dn; CCR.
            EmitLoadDataRegSized(ctx, $"D{srcReg}", size);
            il.Emit(OpCodes.Stloc, ctx.M68kALocal);
            EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, size);
            il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
            if (fam.WritesResult)
            {
                il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
                EmitStoreDataRegSized(ctx, $"D{srcReg}", size);
            }
            EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, size);
            return;
        }

        // memory dest: resolve ea ONCE into M68kAddr2Local (its ext words now lead at ext[immCount..]).
        EmitM68kResolveEaAddr(ctx, pc, srcMode, srcReg, size, ref extIndex);
        il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
        EmitM68kReadSized(ctx, size);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stloc, ctx.M68kALocal);
        EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, size);
        il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
        if (fam.WritesResult)
        {
            il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
            il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
            EmitM68kWriteSizedRmw(ctx, size);
        }
        EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, size);
    }

    /// <summary>M6 PR-5: the QuickEa form (ADDQ/SUBQ). imm3 = (operword&gt;&gt;9)&amp;7, 0-&gt;8. An dest (srcMode==1)
    /// is SPECIAL: it operates on the WHOLE An (full-32), NO CCR (QuickAlu, M68000Cpu.Alu.cs:391-403). Else: ride
    /// the QuickEa path (dest=EA, a=read(ea), b=imm3, write-back, set CCR — Arith). A Dn direct or a memory dest.</summary>
    private void EmitM68kAluQuickEa(EmitContext ctx, ushort pc, ushort operword, int size, M68kAluFam fam)
    {
        ILGenerator il = ctx.Il;
        int imm3 = (operword >> 9) & 7; if (imm3 == 0) imm3 = 8;
        int srcMode = (operword >> 3) & 7, srcReg = operword & 7;
        int extIndex = 0;

        if (srcMode == 1)
        {
            // An dest: whole-An full-32 op, NO CCR.  An = An <op> imm3 (full 32, size index 2).
            EmitLoadAreg(ctx, srcReg);
            il.Emit(OpCodes.Stloc, ctx.M68kALocal);
            il.Emit(OpCodes.Ldc_I4, imm3);
            il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Stloc, ctx.M68kBLocal);
            EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, 2);   // full-32 op
            EmitStoreAreg(ctx, srcReg);
            return;
        }

        // b = imm3 (constant)
        il.Emit(OpCodes.Ldc_I4, imm3);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stloc, ctx.M68kBLocal);

        if (srcMode == 0)
        {
            EmitLoadDataRegSized(ctx, $"D{srcReg}", size);
            il.Emit(OpCodes.Stloc, ctx.M68kALocal);
            EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, size);
            il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
            il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
            EmitStoreDataRegSized(ctx, $"D{srcReg}", size);
            EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, size);
            return;
        }

        // memory dest: resolve ea ONCE; a = read(ea); result; write-back; CCR.
        EmitM68kResolveEaAddr(ctx, pc, srcMode, srcReg, size, ref extIndex);
        il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
        EmitM68kReadSized(ctx, size);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stloc, ctx.M68kALocal);
        EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, size);
        il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
        il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
        il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
        EmitM68kWriteSizedRmw(ctx, size);
        EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, size);
    }

    /// <summary>M6 PR-5: the AddrEa form (ADDA/SUBA/CMPA). An dest = bits 11-9. The decoder remapped the size so
    /// the index is genuine (1=.w/2=.l). A .w source SIGN-EXTENDS to 32; the op is ALWAYS full-32 (AddrAlu,
    /// M68000Cpu.Alu.cs:349-361). ADDA/SUBA write An, NO CCR; CMPA sets a full-32 Cmp CCR and writes nothing.</summary>
    private void EmitM68kAluAddrEa(EmitContext ctx, ushort pc, ushort operword, int size, M68kAluFam fam)
    {
        ILGenerator il = ctx.Il;
        int anReg = (operword >> 9) & 7, srcMode = (operword >> 3) & 7, srcReg = operword & 7;
        bool isWord = size == 1;
        int extIndex = 0;

        // b = source EA (read at the genuine size); .w sign-extends to 32.
        EmitM68kEaRead(ctx, pc, srcMode, srcReg, size, ref extIndex);
        if (isWord) { il.Emit(OpCodes.Conv_I2); il.Emit(OpCodes.Conv_I4); }   // .w -> 32 (sign-extend)
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stloc, ctx.M68kBLocal);
        // a = An (whole 32)
        EmitLoadAreg(ctx, anReg);
        il.Emit(OpCodes.Stloc, ctx.M68kALocal);
        // result = a op b (full-32, size index 2)
        EmitM68kAluOp(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, 2);
        il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
        if (fam.WritesResult)
        {
            il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
            EmitStoreAreg(ctx, anReg);
        }
        // CMPA sets a full-32 Cmp CCR (size index 2); ADDA/SUBA set None.
        EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, 2);
    }

    /// <summary>M6 PR-5: the XAlu form (ADDX/SUBX — the X-INPUT family). Read the LIVE X into M68kXInLocal at the
    /// TOP (mirroring the interpreter's read-at-top, M68000Cpu.Alu.cs:484). bit 3 (operword &amp; 0x0008): 0 =
    /// Dx op Dy -&gt; Dy (register); 1 = -(Ax) op -(Ay) -&gt; (Ay) (memory predecrement). yReg = (ow&gt;&gt;9)&amp;7
    /// (dest/A = Dy/Ay), xReg = ow&amp;7 (source/B = Dx/Ax). MEMORY form predecrements SOURCE Ax FIRST then dest Ay
    /// (M68000Cpu.Alu.cs:500-503). aluFn honors the live X; CCR = ArithX (sticky Z, live xIn).</summary>
    private void EmitM68kAluX(EmitContext ctx, ushort pc, ushort operword, int size, M68kAluFam fam)
    {
        ILGenerator il = ctx.Il;
        int yReg = (operword >> 9) & 7;   // Dy / Ay  (dest, operand A)
        int xReg = operword & 7;          // Dx / Ax  (source, operand B)
        bool mem = (operword & 0x0008) != 0;

        // Read the live X into M68kXInLocal at the TOP (the interpreter reads xIn before the op; the op never
        // touches SR, so this equals the pre-op X — held in a local and passed to the CCR helper).
        EmitM68kLiveX(ctx);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stloc, ctx.M68kXInLocal);

        if (!mem)
        {
            // Dx op Dy -> Dy. a = Dy(sized), b = Dx(sized).
            EmitLoadDataRegSized(ctx, $"D{yReg}", size);
            il.Emit(OpCodes.Stloc, ctx.M68kALocal);
            EmitLoadDataRegSized(ctx, $"D{xReg}", size);
            il.Emit(OpCodes.Stloc, ctx.M68kBLocal);
            EmitM68kAluXResult(ctx, fam, size);   // result = a op b (+/- live X), masked -> M68kResultLocal
            // write Dy (partial)
            il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
            EmitStoreDataRegSized(ctx, $"D{yReg}", size);
        }
        else
        {
            // -(Ax) op -(Ay) -> (Ay). Predecrement SOURCE Ax FIRST (b = read(-(Ax))), then dest Ay (a = read(-(Ay))).
            // mode-4 predecrement = An -= mag; mag honors the A7 word-align (M68kMag).
            EmitAdvanceAreg(ctx, xReg, -M68kMag(xReg, 4, size));
            EmitLoadAreg(ctx, xReg);
            EmitM68kReadSized(ctx, size);
            il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Stloc, ctx.M68kBLocal);       // b = read(-(Ax))
            EmitAdvanceAreg(ctx, yReg, -M68kMag(yReg, 4, size));
            EmitLoadAreg(ctx, yReg);
            il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);   // dest address = the predecremented Ay (survives the read)
            il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
            EmitM68kReadSized(ctx, size);
            il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Stloc, ctx.M68kALocal);       // a = read(-(Ay))
            EmitM68kAluXResult(ctx, fam, size);           // result -> M68kResultLocal
            // write-back to the SAME dest address (RMW; .l low-word-first)
            il.Emit(OpCodes.Ldloc, ctx.M68kAddr2Local);
            il.Emit(OpCodes.Ldloc, ctx.M68kResultLocal);
            EmitM68kWriteSizedRmw(ctx, size);
        }
        // CCR = ArithX (sticky Z, live xIn) — pass the X local read at the top.
        EmitM68kFamilyCcr(ctx, fam, ctx.M68kALocal, ctx.M68kBLocal, ctx.M68kResultLocal, ctx.M68kXInLocal, size);
    }

    /// <summary>M6 PR-5: result = aluFn(a, b, LIVE-X) masked, for ADDX/SUBX. AddX = a + b + X; SubX = a - b - X
    /// (M68000Cpu.Alu.cs:37-38). a in M68kALocal, b in M68kBLocal, X in M68kXInLocal. Leaves the result in
    /// M68kResultLocal.</summary>
    private void EmitM68kAluXResult(EmitContext ctx, M68kAluFam fam, int size)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, ctx.M68kALocal);
        il.Emit(OpCodes.Ldloc, ctx.M68kBLocal);
        if (fam.Fn == M68kAluFn.Add)
        {
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldloc, ctx.M68kXInLocal);
            il.Emit(OpCodes.Add);                         // a + b + X
        }
        else
        {
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Ldloc, ctx.M68kXInLocal);
            il.Emit(OpCodes.Sub);                         // a - b - X
        }
        uint mask = size == 0 ? 0xFFu : size == 1 ? 0xFFFFu : 0xFFFFFFFFu;
        if (size != 2) { il.Emit(OpCodes.Ldc_I4, unchecked((int)mask)); il.Emit(OpCodes.And); }
        il.Emit(OpCodes.Stloc, ctx.M68kResultLocal);
    }

    /// <summary>M6 PR-5: resolve a 68000 EA to its memory ADDRESS (no read), leaving the address in M68kAddr2Local
    /// (the survivor local — distinct from AddrLocal which the bus helpers clobber). Mirrors the address-computing
    /// slice of EmitM68kEaWrite (modes 2-6 + mode 7 reg 0/1), so the ALU RMW forms resolve the dest address ONCE
    /// and read-then-write the SAME address ((An)+/-(An) advances An exactly once — the address-once discipline).
    /// Threads extIndex so the EA's ext words follow any leading imm words. Dn/An direct (modes 0/1) are NOT
    /// address EAs and are handled inline by the calling arms; this is the memory-EA resolver only.</summary>
    private void EmitM68kResolveEaAddr(EmitContext ctx, ushort pc, int eaMode, int eaReg, int size, ref int extIndex)
    {
        ILGenerator il = ctx.Il;
        switch (eaMode)
        {
            case 2:   // (An)
                EmitLoadAreg(ctx, eaReg);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                return;
            case 3:   // (An)+ : ea = An; An += mag
                EmitLoadAreg(ctx, eaReg);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                EmitAdvanceAreg(ctx, eaReg, +M68kMag(eaReg, eaMode, size));
                return;
            case 4:   // -(An) : An -= mag FIRST, then ea = An
                EmitAdvanceAreg(ctx, eaReg, -M68kMag(eaReg, eaMode, size));
                EmitLoadAreg(ctx, eaReg);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                return;
            case 5:   // d16(An)
                EmitLoadAreg(ctx, eaReg);
                EmitAddDisp16(ctx, pc, ref extIndex);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                return;
            case 6:   // d8(An,Xn)
                EmitM68kBriefIndex(ctx, pc, eaReg, isPc: false, ref extIndex);
                il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                return;
            case 7:
                switch (eaReg)
                {
                    case 0:   // abs.w
                        EmitAbsW(ctx, pc, ref extIndex);
                        il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                        return;
                    case 1:   // abs.l
                        EmitAbsL(ctx, pc, ref extIndex);
                        il.Emit(OpCodes.Stloc, ctx.M68kAddr2Local);
                        return;
                }
                break;
        }
        throw new EmulationException($"EmitM68kResolveEaAddr: unhandled / non-memory EA mode {eaMode}/{eaReg}");
    }

    /// <summary>M6 PR-5: write the sized operand (on the stack BELOW the address) to a resolved dest address. Stack:
    /// ..., address(uint), value(uint) -> .... Like EmitM68kWriteSized but the .l case uses the low-word-first RMW
    /// store (EmitStoreLongRmw) — the 68000 read-modify-write write-back order. The address is the SAME resolved
    /// dest the read used (held by the caller in M68kAddr2Local and pushed before the value).</summary>
    private void EmitM68kWriteSizedRmw(EmitContext ctx, int size)
    {
        switch (size)
        {
            case 0:
                // .b — EmitStoreByte stack contract is (address, value as int); Conv_U1.
                ctx.Il.Emit(OpCodes.Conv_U1);
                EmitStoreByte(ctx);
                break;
            case 1:
                EmitStoreWord(ctx);     // (address, value) — big-endian Write16
                break;
            default:
                EmitStoreLongRmw(ctx);  // (address, value) — LOW WORD FIRST (RMW order)
                break;
        }
    }
}
