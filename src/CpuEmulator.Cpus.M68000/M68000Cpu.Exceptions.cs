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
    {
        // 1. Enter supervisor mode + clear the trace bit. Writing SR re-banks A7 -> SSP (the USP/SSP swap is
        //    free; ADR 0008 §1.1). The CCR (low byte) of srAtFault carries forward — only S is forced, T cleared.
        SR = (ushort)((srAtFault | SrSupervisorBit) & ~SrTraceBit);

        // 2. Push the frame on -(A7) (= -(SSP)). Small frame (group 1/2): PC (long) then SR (word) = 6 bytes.
        //    PC is pushed FIRST (it lands at the higher address A7-4); SR is pushed SECOND (lands at the lowest
        //    address A7-6). So the in-memory layout is [SR @ SSP, PC @ SSP+2] — the 68000 small frame.
        if (frameKind == FrameKind.Large)
        {
            // DD4 — RESOLVED EMPIRICALLY (Task 13 Step 0). The 68000 group-0 (address/bus error) frame is a
            // 14-byte frame (observed: SSP moves by 0xE in the MOVE.w address-error cases), NOT the small 6-byte
            // PC+SR frame: [special-status-word, access-address(long), instruction-register, SR, PC]. The special
            // status word + access-address encode the IN-PROGRESS bus-cycle state (which half of the bus cycle
            // faulted) — TIMING-coupled and NOT data-axis-stable. So M4.5d-1 does NOT push the precise group-0
            // words; the runner DEFERS the address-error subset (IsAddressErrorCase, vector 3) even under
            // assertExceptions and asserts only trap-taken for it. The precise 14-byte frame is M4.5d-2.
            //
            // No RaiseException caller passes FrameKind.Large in M4.5d-1 (address error is detected+deferred by
            // the runner, not raised here) — this branch is the documented placeholder for M4.5d-2.
        }
        uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, pcAtFault);   // push PC (long)
        sp = A7 - 2u; A7 = sp; WriteWordBus(sp, srAtFault);        // push SR (word) -> SR at the lowest address

        // 3. Vector through the table (VectorBase = 0): PC = the 32-bit handler at 4·vector.
        PC = ReadLongBus(4u * vector);
    }

    /// <summary>Test seam: drive RaiseException from a synthetic unit test (the ComputeEaProbe precedent).</summary>
    public void RaiseExceptionProbe(uint vector, bool large, ushort srAtFault, uint pcAtFault)
        => RaiseException(vector, large ? FrameKind.Large : FrameKind.Small, srAtFault, pcAtFault);

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
        // CHK sets N from the comparison BEFORE the trap (N=1 when below 0, N=0 when above the bound; Z/V/C
        // undefined-but-pinned — the vectors PIN them, reconciled here in Task 14). Set N then raise.
        if (value < 0 || value > bound)
        {
            byte ccr = (byte)(Ccr & ~0x08);
            if (value < 0) ccr |= 0x08;     // N set when below 0; cleared when above the bound (PRM)
            Ccr = ccr;
            RaiseException(Vector.Chk, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);
        }
        // in range [0, bound]: no trap. (N is undefined here; the vectors pin it — confirm Task 14.)
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
