using CpuEmulator.Core;

namespace CpuEmulator.Cpus.M68000;

/// <summary>The MINIMAL hand-written half of the 68000 (M4.1) — the bus wiring, the A7/USP/SSP banking,
/// the SR/CCR accessors, and the policy hooks the generated partial requires. This is the STATE
/// FOUNDATION: it makes the generated register file compile and proves the register model synthetically
/// (32-bit round-trip, A7 banking by the SR S-bit, the SR/CCR split). It is NOT an interpreter: there is
/// NO decode, NO EA, NO op body, NO wide bus, NO prefetch queue, NO exception/vector machinery — those are
/// M4.2–M4.5. The instruction table is empty, so M4.1 never calls Step. The interrupt hooks are inert
/// (the IPL-level model is M4.5d).</summary>
public sealed partial class M68000Cpu
{
    // The single program/data bus (von Neumann; the 68000 has no separate I/O space — IO is memory-mapped).
    // M4.1 wires Read8/Write8 (the byte path); the wide big-endian Read16/Read32 are M4.2.
    private readonly IAddressSpace _bus;

    /// <summary>The supervisor-stack-bit mask in the 16-bit SR (bit 13). The S-bit selects which physical
    /// stack A7 aliases (USP when clear, SSP when set). Pinned here so the banking logic does not depend on
    /// the FlagLayout's declared bit (the layout names S=13; this constant must match it — guarded by the
    /// SupervisorMode_reflects_the_SR_S_bit test).</summary>
    private const ushort SrSupervisorBit = 1 << 13;

    public M68000Cpu(IAddressSpace bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <summary>True when the SR supervisor (S) bit is set. Selects the SSP bank for A7; the eventual
    /// exception machinery (M4.5d) toggles it on entry/RTE.</summary>
    public bool SupervisorMode => (SR & SrSupervisorBit) != 0;

    /// <summary>Set/clear the SR supervisor (S) bit. A test/host convenience for M4.1 (the real toggle is
    /// the exception/RTE sequence in M4.5d). Keeps the banking tests independent of SR-bit-layout knowledge.</summary>
    public void SetSupervisorMode(bool supervisor) =>
        SR = (ushort)(supervisor ? (SR | SrSupervisorBit) : (SR & ~SrSupervisorBit));

    /// <summary>The Condition Code Register — the low byte of the 16-bit SR (X N Z V C). The 68000's
    /// user-visible flags; the system byte (interrupt mask, S, T) is the SR high byte.</summary>
    public byte Ccr
    {
        get => (byte)(SR & 0xFF);
        set => SR = (ushort)((SR & 0xFF00) | value);
    }

    /// <summary>A7 — the stack pointer, BANKED into USP/SSP by the SR S-bit (ADR 0003 Decision 1). NOT a
    /// spec register (Decision D2): the TomHarte schema names usp/ssp, never a7, so introspection exposes
    /// USP/SSP by name and A7 is this C# convenience view (the same altitude as the Z80 pair-views, but
    /// mode-selected rather than high/low-split, so hand-written rather than generated). The implicit
    /// stack ops of exceptions/BSR/JSR/RTS (M4.5) reference A7; privileged MOVE USP reaches the other bank.</summary>
    public uint A7
    {
        get => SupervisorMode ? SSP : USP;
        set { if (SupervisorMode) SSP = value; else USP = value; }
    }

    /// <summary>Reset — M4.1 stub (the real reset reads the initial SSP + PC from the vector table at
    /// addresses 0/4 via the wide bus; that is M4.5). Sets nothing else (the harness sets registers
    /// explicitly in the M4.5 TomHarte runner).</summary>
    public void Reset() { }

    // ── The policy hooks the generated partial requires (inert in M4.1) ────────────────────────────────
    public void SetIrqLine(bool asserted) { }   // the IPL-level interrupt model is M4.5d
    public void SetNmiLine(bool asserted) { }   // the 68000 has no NMI line; level-7 is non-maskable (M4.5d)

    /// <summary>Program/data-bus byte read; charges one cycle (the cycle invariant). The wide big-endian
    /// Read16/Read32 the 68000 truly needs are M4.2 (this byte path keeps the generated Step compiling).</summary>
    private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
    private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }

    // ── Wide big-endian bus access (M4.2 surface; M4.5a wires it into the MOVE bodies). Each charges the
    //    bus-access cycles. The 16-bit bus decomposes a .l into two .w transactions: ReadLongBus is two
    //    Read16 calls (high word first) — which the tracing bus records as two .w transactions. The cycle
    //    counts here are the BUS portion; the op body adds the instruction's internal cycles so CycleCount
    //    ends == the case's length (Σ transaction cycles — validated by the TomHarte gate). ────────────────
    private const int WordAccessCycles = 4;   // a word bus cycle is 4 clocks on the 68000 (S0-S7)

    private ushort ReadWordBus(uint address)
    {
        _cycles += WordAccessCycles;
        return _bus.Read16(address);
    }

    private void WriteWordBus(uint address, ushort value)
    {
        _cycles += WordAccessCycles;
        _bus.Write16(address, value);
    }

    // A long access is TWO word transactions (high word first) — charge + access each separately so the
    // tracing bus records two .w transactions (the 16-bit-bus decomposition the vectors assert).
    private uint ReadLongBus(uint address)
    {
        ushort hi = ReadWordBus(address);
        ushort lo = ReadWordBus(address + 2);
        return ((uint)hi << 16) | lo;
    }

    private void WriteLongBus(uint address, uint value)
    {
        WriteWordBus(address, (ushort)(value >> 16));
        WriteWordBus(address + 2, (ushort)value);
    }

    // Test seams (mirror the generated ComputeEaProbe) — drive the wide path from synthetic unit tests.
    public ushort ReadWordBusProbe(uint a) => ReadWordBus(a);
    public uint ReadLongBusProbe(uint a) => ReadLongBus(a);
    public void WriteWordBusProbe(uint a, ushort v) => WriteWordBus(a, v);
    public void WriteLongBusProbe(uint a, uint v) => WriteLongBus(a, v);

    /// <summary>Undefined-opcode hook — M4.1 stub (the 68000's illegal-instruction exception is M4.5d). The
    /// instruction table is empty in M4.1, so any Step would route here; M4.1 never calls Step.</summary>
    private void HandleUndefinedOpcode(byte opcode) { _cycles++; }

    /// <summary>No interrupt servicing in M4.1 (the IPL-level policy is M4.5d). Returns false so the
    /// generated Step never vectors.</summary>
    private partial bool TryServiceInterrupt() => false;

    /// <summary>Never pending in M4.1 (the IPL-level + SR-mask comparison is M4.5d).</summary>
    public partial bool InterruptPending => false;
}
