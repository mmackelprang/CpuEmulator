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
    /// 16-bit displacement (baked Ldc_I4). Leaves ea (uint, via the unchecked Add) on the stack.</summary>
    private void EmitAddDisp16(EmitContext ctx, ushort pc, ref int extIndex)
    {
        short disp = unchecked((short)NextExtWord(pc, ref extIndex));
        ctx.Il.Emit(OpCodes.Ldc_I4, disp);           // sign-extended to int
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
}
