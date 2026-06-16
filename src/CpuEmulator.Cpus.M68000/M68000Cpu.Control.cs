namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5d-1 (ADR 0008 §3.1): the control-flow core — Bcc/BSR/BRA, DBcc, JMP/JSR/RTS/RTR/RTE, LINK/UNLK. The most
/// M4.5c-like work in the arc: reuses EvaluateCondition (the shared cc evaluator, M68000Cpu.Scc.cs), pushes/pops
/// A7 exactly like the proven PEA, and the data-axis result (the landed PC, the pushed/popped stack, the
/// decremented Dn for DBcc) is fully diffed by the existing runner. RTE is privileged (vector 8 via
/// RaiseException when !SupervisorMode); writing the popped SR re-banks A7 (the USP/SSP swap is free). The TIMING
/// axis (final.pc/prefetch/trace) is M4.5d-2. Seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>Bcc/BSR/BRA share the dataset row Bcc (0xF000/0x6000); the condition field (bits 11-8)
    /// sub-dispatches: 0000=BRA (always), 0001=BSR (push the return PC then branch), 0010-1111 = the 14
    /// conditionals via EvaluateCondition. Disp = bits 7-0 (sign-extended .b), or, when 0x00, a following 16-bit
    /// disp word (.w form). 0xFF (.l) is 68020+ → ILLEGAL on the 68000. The branch base = PcForEa = the operword
    /// address + 2 (the displacement origin; the generated Step set _eaPcBase there before advancing PC).</summary>
    partial void BccExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint cc = (operword >> 8) & 0xFu;
        uint disp8 = operword & 0xFFu;
        int disp;
        if (disp8 == 0x00u)            // Bcc.w : the 16-bit displacement word follows
            disp = (short)r.ExtensionWords[0];
        else
            disp = (sbyte)(byte)disp8; // Bcc.b : the 8-bit displacement (0xFF = -1 is a NORMAL .b disp on the
                                       //         68000 — the .l form is 68020+, so the 68000 does NOT trap it;
                                       //         vector-confirmed: 0x__FF cases land at base + (-1), not ILLEGAL)

        uint branchBase = PcForEa;     // = operword + 2 (the displacement origin)
        uint target = unchecked(branchBase + (uint)disp);

        if (cc == 0x1u)                // BSR: push the RETURN pc (the post-advance PC), then branch
        {
            uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, PC);
            PC = target; return;
        }
        if (cc == 0x0u || EvaluateCondition(cc, (byte)(SR & 0xFF)))   // BRA (cc 0) or a taken conditional
            PC = target;
        // a NOT-taken conditional: PC already points past the instruction (the generated Step advanced it).
    }

    /// <summary>DBcc (0xF0F8/0x50C8): if the condition is FALSE, decrement Dn.w and branch unless it ran out
    /// (Dn.w hit -1 = 0xFFFF — the off-by-one: -1 terminates, NOT 0); if TRUE, fall through. +1 disp word.</summary>
    partial void DBccExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint cc = (operword >> 8) & 0xFu;
        uint dn = operword & 7u;
        uint branchBase = PcForEa;                 // = operword + 2 (the disp word's origin)
        int disp = (short)r.ExtensionWords[0];     // the +1 displacement word

        if (EvaluateCondition(cc, (byte)(SR & 0xFF)))
            return;                                // condition true: fall through (PC already past the insn)

        ushort counter = (ushort)(DataReg(dn) & 0xFFFFu);
        counter = (ushort)(counter - 1);           // decrement Dn.w
        SetDataRegPartial(dn, counter, 1u);        // .w partial write (upper word preserved)
        if (counter != 0xFFFFu)                    // branch unless the counter ran out (-1, NOT 0)
            PC = unchecked(branchBase + (uint)disp);
        // counter == 0xFFFF: loop terminates, PC stays past the instruction.
    }

    // ── Task 4: JMP/JSR — unconditional jump + call (a control EA, never dereferenced — the LEA/PEA precedent) ──
    partial void JmpExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint ea = ComputeEa(srcMode, srcReg, 2u, r.ExtensionWords, pureEa: true);   // control EA (no deref)
        PC = ea;
    }

    partial void JsrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint ea = ComputeEa(srcMode, srcReg, 2u, r.ExtensionWords, pureEa: true);
        uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, PC);   // push the RETURN pc (post-advance PC) -(A7)
        PC = ea;
    }

    // ── Task 5: RTS/RTR/RTE — the return family ─────────────────────────────────────────────────────────────
    partial void RtsExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint sp = A7;
        PC = ReadLongBus(sp);          // pop PC
        A7 = sp + 4u;
    }

    partial void RtrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint sp = A7;
        ushort w = ReadWordBus(sp);    // pop the CCR word; only the low byte (X N Z V C) is restored
        Ccr = (byte)(w & 0x1Fu);
        PC = ReadLongBus(sp + 2u);     // then pop PC
        A7 = sp + 6u;
    }

    partial void RteExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if (!SupervisorMode) { RaiseException(Vector.Privilege, FrameKind.Small, (ushort)(SR & 0xFFFF), PC); return; }
        uint sp = SSP;                 // RTE always un-stacks from the SSP (supervisor)
        ushort restoredSr = ReadWordBus(sp);    // pop SR (full 16 bits — mode + CCR)
        uint restoredPc = ReadLongBus(sp + 2u); // pop PC
        SSP = sp + 6u;                 // advance the supervisor stack BEFORE writing SR (which may flip the bank)
        SR = (ushort)(restoredSr & SrValidMask);    // the SR_VALID mask (MOVE-to-SR precedent); re-banks A7
        PC = restoredPc;
    }

    // ── Task 6: LINK/UNLK — the stack frame (the PEA push mechanism) ────────────────────────────────────────
    partial void LinkExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint an = operword & 7u;
        int disp = (short)r.ExtensionWords[0];     // the +1 signed displacement word
        // The 68000 sequence: SP -= 4; (SP) = An; An = SP; SP += disp. The push reads An AFTER the predecrement,
        // so for the An==A7 edge it pushes the DECREMENTED SP (vector-confirmed: LINK A7 pushes A7-4, not A7).
        uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, Areg(an));   // push An -(A7) (An read after the decrement)
        SetAreg(an, A7);                           // An = the new A7 (the frame pointer)
        A7 = unchecked(A7 + (uint)disp);           // allocate the frame (disp is typically negative)
    }

    partial void UnlkExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint an = operword & 7u;
        uint sp = Areg(an);          // A7 = An (deallocate the frame)
        uint popped = ReadLongBus(sp);
        A7 = sp + 4u;                // advance the stack FIRST (the An==A7 edge: the register load below wins)
        SetAreg(an, popped);         // pop the saved An from (A7)+ — for an==7 this overwrites A7 with the pop
    }
}
