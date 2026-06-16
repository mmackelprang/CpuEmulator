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
}
