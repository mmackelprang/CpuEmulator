namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5d-1 (ADR 0008 §3.2 — decision B): the 68000 exception model. ONE RaiseException(vector, frameKind)
/// routine funnels EVERY synchronous exception source (TRAP/TRAPV/CHK/ILLEGAL/÷0/privilege/address-error) and
/// the IPL interrupt acknowledge (DD5) — "integrate WITHOUT scattering". The sequence: capture SR-at-fault →
/// enter supervisor + clear trace (writing SR re-banks A7 to SSP automatically — the USP/SSP swap is free) →
/// push the frame on -(A7) (= -(SSP), the proven PEA mechanism) → PC = Read32(4·vector). Small frame (group
/// 1/2: PC long + SR word, 6 bytes) covers TRAP/TRAPV/CHK/ILLEGAL/÷0/privilege; large frame (group 0:
/// address error) adds access-info words whose exact contents may defer to M4.5d-2 (DD4 — assert trap-taken).
/// The TIMING axis (the exact cycle count of the sequence) is M4.5d-2; the DATA result here is frame + mode +
/// handler PC. Reuses the merged substrate (A7, WriteLongBus/WriteWordBus, ReadLongBus, SupervisorMode,
/// SrSupervisorBit); the fetch/bus SEAM is untouched (ADR 0007 §5.4).
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>The 68000 trace bit (SR bit 15). RaiseException clears it on entry.</summary>
    private const ushort SrTraceBit = 1 << 15;

    /// <summary>The 68000 SR valid-bit mask (T S I2..I0 X N Z V C implemented; the rest read as 0). Matches the
    /// M4.5a MOVE-to-SR precedent (M68000Cpu.Move.cs:131) — every full-SR write masks through this.</summary>
    private const ushort SrValidMask = 0xA71F;

    /// <summary>The 68000 exception vector assignments (ADR 0004 §2 Decision 3). The vector NUMBER; the table
    /// entry is at byte address 4·vector. reset(0/1) is not exercised by single-step vectors.</summary>
    private static class Vector
    {
        public const uint BusError = 2;
        public const uint AddressError = 3;
        public const uint Illegal = 4;
        public const uint DivideByZero = 5;
        public const uint Chk = 6;
        public const uint TrapV = 7;
        public const uint Privilege = 8;
        public const uint Trace = 9;
        public const uint TrapBase = 32;       // TRAP #n -> 32 + n
        public const uint AutovectorBase = 24; // IPL level L (1-7) -> 24 + L (DD5)
    }

    /// <summary>Small frame (group 1/2): PC + SR, 6 bytes. Large frame (group 0: address/bus error): the access-
    /// info words too (DD4 — M4.5d-1 asserts trap-taken; the precise contents may defer to M4.5d-2).</summary>
    private enum FrameKind { Small, Large }

    /// <summary>The 68000 exception sequence (decision B — ONE routine for EVERY synchronous source + the IPL
    /// acknowledge). srAtFault = the SR captured at the point of fault (BEFORE the mode change); pcAtFault = the
    /// PC to stack (the post-advance PC for software traps; the faulting-instruction PC for group-0 — DD4).
    /// Steps: (1) enter supervisor + clear trace (writing SR re-banks A7 to SSP automatically); (2) push the
    /// frame on -(A7) (= -(SSP)); (3) PC = Read32(4·vector). The TIMING axis (the exact cycle count) is M4.5d-2;
    /// the DATA result is frame + mode + handler PC.</summary>
    private void RaiseException(uint vector, FrameKind frameKind, ushort srAtFault, uint pcAtFault)
        => RaiseException(vector, frameKind, srAtFault, pcAtFault, accessAddress: 0u,
                          instructionRegister: (ushort)0, specialStatusWord: (ushort)0);

    /// <summary>The group-0 (address/bus error) overload — pushes the 14-byte large frame
    /// <c>[SSW, accessAddress(long), IR, SR, PC]</c>. The small-frame overload above forwards with the
    /// group-0 words zeroed (they are unused for FrameKind.Small).</summary>
    private void RaiseException(uint vector, FrameKind frameKind, ushort srAtFault, uint pcAtFault,
                                uint accessAddress, ushort instructionRegister, ushort specialStatusWord)
    {
        // 1. Enter supervisor mode + clear the trace bit. Writing SR re-banks A7 -> SSP (the USP/SSP swap is
        //    free; ADR 0008 §1.1). The CCR (low byte) of srAtFault carries forward — only S is forced, T cleared.
        SR = (ushort)((srAtFault | SrSupervisorBit) & ~SrTraceBit);

        if (frameKind == FrameKind.Large)
        {
            // M4.5d-2a (DD3/F, plan T3): the 68000 group-0 (address/bus error) 14-byte frame, pushed on -(A7)
            // (= -(SSP)). Empirically (the MOVE.w/.l address-error corpus) the in-memory layout, lowest address
            // first, is:
            //   [SSP+0x0] special status word (SSW)   — the in-progress bus-cycle state (R/W, FC, I/N)
            //   [SSP+0x2] access address  (high word)  — the faulting bus address
            //   [SSP+0x4] access address  (low  word)
            //   [SSP+0x6] instruction register (IR)    — the operword being executed
            //   [SSP+0x8] SR                           — the SR captured at fault
            //   [SSP+0xa] PC (high word)               — the stacked PC
            //   [SSP+0xc] PC (low  word)
            // pushed high-address-first (PC first, SSW last) as A7 decrements by 0xE total.
            //
            // HONEST 2a SCOPE (ADR 0008 §5.2/§8.1, DD3): of these words ONLY the IR (== the operword) is cleanly
            // derivable from 2a's queue/formal-PC + data model. The SSW + access address encode WHICH HALF of the
            // bus cycle faulted — trace-coupled, M4.5d-2b. The empirically-observed stacked PC and SR also vary
            // with how far the (partially-executed) instruction progressed before faulting — also trace-coupled
            // in 2a. So 2a provides the FRAME-PUSH MACHINERY + pins the IR, with PC/SR/access-address/SSW left to
            // the caller (which in 2a passes the model's best values); the runner therefore still DEFERS the
            // address-error cases on the data + PC/prefetch axes (IsAddressErrorCase) — 2a asserts trap-taken +
            // the IR word, and 2b finalizes the trace-coupled words and turns the deferral into a green assertion.
            uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, pcAtFault);             // PC (long)
            sp = A7 - 2u; A7 = sp; WriteWordBus(sp, srAtFault);                  // SR (word)
            sp = A7 - 2u; A7 = sp; WriteWordBus(sp, instructionRegister);        // IR (word) — the operword (pinned)
            sp = A7 - 4u; A7 = sp; WriteLongBus(sp, accessAddress);             // access address (long)
            sp = A7 - 2u; A7 = sp; WriteWordBus(sp, specialStatusWord);         // SSW (word) — trace-coupled (2b)
            PC = ReadLongBus(4u * vector);                                      // vector through the table
            return;
        }

        // 2. Small frame (group 1/2): PC (long) then SR (word) = 6 bytes. PC is pushed FIRST (it lands at the
        //    higher address A7-4); SR is pushed SECOND (lands at the lowest address A7-6). So the in-memory
        //    layout is [SR @ SSP, PC @ SSP+2] — the 68000 small frame.
        uint sp0 = A7 - 4u; A7 = sp0; WriteLongBus(sp0, pcAtFault);   // push PC (long)
        sp0 = A7 - 2u; A7 = sp0; WriteWordBus(sp0, srAtFault);        // push SR (word) -> SR at the lowest address

        // 3. Vector through the table (VectorBase = 0): PC = the 32-bit handler at 4·vector.
        PC = ReadLongBus(4u * vector);
    }

    /// <summary>Test seam: drive RaiseException from a synthetic unit test (the ComputeEaProbe precedent). The
    /// small-frame form zeros the group-0 words; the large-frame form (M4.5d-2a) pins the 14-byte frame incl.
    /// the IR + access address + SSW so a synthetic test can assert the layout (the corpus address-error frame
    /// stays runner-deferred in 2a — see RaiseException's FrameKind.Large note).</summary>
    public void RaiseExceptionProbe(uint vector, bool large, ushort srAtFault, uint pcAtFault)
        => RaiseException(vector, large ? FrameKind.Large : FrameKind.Small, srAtFault, pcAtFault);

    /// <summary>Test seam (M4.5d-2a): exercise the group-0 large-frame push with the full word set, so a
    /// synthetic unit test can pin the 14-byte layout + the IR pinning without the trace-coupled corpus.</summary>
    public void RaiseLargeFrameProbe(uint vector, ushort srAtFault, uint pcAtFault,
                                     uint accessAddress, ushort instructionRegister, ushort specialStatusWord)
        => RaiseException(vector, FrameKind.Large, srAtFault, pcAtFault, accessAddress, instructionRegister,
                          specialStatusWord);

    /// <summary>The privilege gate: if in user mode, raise a privilege violation (vector 8) and return true (the
    /// caller must NOT execute). Centralizes the "integrate without scattering" privilege check (ADR 0008 §3.2).
    /// </summary>
    private bool TrapIfUserMode()
    {
        if (SupervisorMode) return false;
        RaiseException(Vector.Privilege, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);
        return true;
    }

    // ── Task 8: TRAP/TRAPV/CHK/ILLEGAL + NOP/RESET/STOP (the software/check vectors + the privileged no-ops) ──
    partial void TrapExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => RaiseException(Vector.TrapBase + (operword & 0xFu), FrameKind.Small, (ushort)(SR & 0xFFFF), PC);

    partial void TrapVExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if ((Ccr & 0x02) != 0)   // V set -> trap (vector 7)
            RaiseException(Vector.TrapV, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);
        // V clear: no-op.
    }

    partial void ChkExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint dn = (operword >> 9) & 7u;
        int value = (short)(ushort)(DataReg(dn) & 0xFFFFu);                       // CHK is .w on the 68000
        int bound = (short)(ushort)(ReadEaOperand(srcMode, srcReg, 1u, r.ExtensionWords) & 0xFFFFu);
        if (value < 0 || value > bound)
        {
            // On a CHK trap the 68000 CCR is DETERMINISTIC (vector-confirmed across all trap cases): keep X,
            // clear Z/V/C, set N iff value < 0 (N is cleared when value > bound). Then vector 6. The stacked SR
            // carries this CCR. (On the IN-RANGE path the 68000 CHK CCR is documented "undefined" and the
            // vectors confirm it is NOT a clean function of the operands — those cases are a corpus artifact the
            // d-1 sweep filters, the M4.5c inconsistent-vector precedent. So CHK touches the CCR ONLY on trap.)
            byte ccr = (byte)(Ccr & 0x10);          // keep X; clear N Z V C
            if (value < 0) ccr |= 0x08;             // N set when below 0; cleared when above the bound (PRM)
            Ccr = ccr;
            RaiseException(Vector.Chk, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);
        }
        // in range [0, bound]: no trap; the CCR is left UNCHANGED (the hardware value is unpredictable here).
    }

    partial void IllegalExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => RaiseException(Vector.Illegal, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);

    partial void NopExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    { /* no state change on the data axis (the only observable effect is timing/prefetch — M4.5d-2). */ }

    partial void ResetExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if (TrapIfUserMode()) return;   // PRIVILEGED
        // RESET asserts the external reset line — no CPU-register data-axis effect (peripheral reset is a
        // device concern). Data-axis no-op in supervisor mode.
    }

    partial void StopExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if (TrapIfUserMode()) return;   // PRIVILEGED
        SR = (ushort)(r.ExtensionWords[0] & SrValidMask);   // STOP loads the +1 imm word into SR, then halts.
        // The halt/wake-on-interrupt is a timing/IPL concern (M4.5d-2 / DD5); the data-axis effect is the SR load.
    }

    // ── Task 10: ANDI/ORI/EORI to CCR (unprivileged — the low byte of SR) ─────────────────────────────────────
    partial void OriCcrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Ccr = (byte)((Ccr | (byte)(r.ExtensionWords[0] & 0xFFu)) & 0x1Fu);
    partial void AndiCcrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Ccr = (byte)((Ccr & (byte)(r.ExtensionWords[0] & 0xFFu)) & 0x1Fu);
    partial void EoriCcrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Ccr = (byte)((Ccr ^ (byte)(r.ExtensionWords[0] & 0xFFu)) & 0x1Fu);

    // ── Task 10: ANDI/ORI/EORI to SR (PRIVILEGED — the full 16-bit SR; writing SR may flip S and re-bank A7) ──
    partial void OriSrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    { if (TrapIfUserMode()) return; SR = (ushort)((SR | r.ExtensionWords[0]) & SrValidMask); }
    partial void AndiSrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    { if (TrapIfUserMode()) return; SR = (ushort)((SR & r.ExtensionWords[0]) & SrValidMask); }
    partial void EoriSrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    { if (TrapIfUserMode()) return; SR = (ushort)((SR ^ r.ExtensionWords[0]) & SrValidMask); }
}
