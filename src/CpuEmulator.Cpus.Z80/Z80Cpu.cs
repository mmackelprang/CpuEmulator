using CpuEmulator.Core;

namespace CpuEmulator.Cpus.Z80;

/// <summary>The MINIMAL hand-written half of the Z80 — bus/IO wiring + the required policy hooks, with
/// NO real semantics. This is the M3.3 structural-generation SKELETON's partial: it exists only to make
/// the generated decode skeleton COMPILE through the Roslyn generator (the Rung-4 gate), proving the
/// dataset + the M3.1b generic decoder + the M3.1a data-driven register file accommodate the Z80's
/// seven-plane prefix structure end-to-end.
///
/// What this is NOT: it is NOT a working Z80 interpreter. There is NO reset vector / R-refresh /
/// IM 0/1/2 / NMI / IFF1/IFF2 logic, NO block-op self-repeat, NO DAA, NO EX/EXX, NO flag model. The
/// covered ops the generator emitted bodies for are NOT flag-correct (the 6502 ALU flag convention is
/// wrong for the Z80 — every Z80 flag effect is TODO(vocab)). Real semantics + the new micro-op
/// vocabulary + the TomHarte/ZEXALL behavioral gate are M3.4. The skeleton is unverified-pending-M3.4.</summary>
public sealed partial class Z80Cpu
{
    // The program/data bus (Von Neumann — the Z80 shares program + data). The generated decode walk's
    // Step reads it via _bus (the M3.1b AddressSpaceFetchStream). The separate I/O space (16-bit port
    // address range) backs the M3.2 Port-class ops (IN/OUT) via ReadIo/WriteIo.
    private readonly IAddressSpace _bus;
    private readonly IAddressSpace _io;
    private bool _halted;

    /// <summary>The M3.2 two-bus ctor: the program/data bus + the I/O AddressSpace(Io, 16). A null
    /// I/O bus defaults to a fresh 16-bit Io space (the Z80 port range).</summary>
    public Z80Cpu(IAddressSpace bus, IAddressSpace? io = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _io  = io ?? new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
    }

    /// <summary>Reset stub — NO real Z80 reset sequence (PC/I/R/IFF clearing is M3.4). Clears only the
    /// skeleton's halted latch so a fresh run starts un-halted.</summary>
    public void Reset() => _halted = false;

    /// <summary>Interrupt-line setters — stubs. The Z80 interrupt policy (IM 0/1/2, NMI, IFF1/IFF2) is
    /// M3.4; the skeleton wires the lines so ICpuCore is satisfied but services nothing.</summary>
    public void SetIrqLine(bool asserted) { }
    public void SetNmiLine(bool asserted) { }

    /// <summary>No interrupt is ever pending in the skeleton (M3.4 owns the real policy).</summary>
    public partial bool InterruptPending => false;

    /// <summary>The halted latch the generated Step consults (set by the Halt() micro-op via DoHalt).
    /// M3.4 owns the wake (clearing it on a serviced interrupt); the skeleton never wakes.</summary>
    public partial bool Halted => _halted;

    /// <summary>Program/data-bus read; charges one cycle (the cycle invariant lives here).</summary>
    private byte ReadBus(uint address)
    {
        _cycles++;
        return _bus.Read8(address);
    }

    /// <summary>Program/data-bus write; charges one cycle.</summary>
    private void WriteBus(uint address, byte value)
    {
        _cycles++;
        _bus.Write8(address, value);
    }

    /// <summary>I/O-bus read (the M3.2 Io analogue of ReadBus) — the generated PortIn body calls this,
    /// so IN hits the Io space, never the program/data space. Charges one cycle.</summary>
    private byte ReadIo(uint port)
    {
        _cycles++;
        return _io.Read8(port);
    }

    /// <summary>I/O-bus write — the generated PortOut body's target. Charges one cycle.</summary>
    private void WriteIo(uint port, byte value)
    {
        _cycles++;
        _io.Write8(port, value);
    }

    /// <summary>Advances one cycle while halted (the "NOP while halted" the generated Step idles on).</summary>
    private void IdleCycle() => _cycles++;

    /// <summary>The Halt() micro-op body's latch-setter (the generated HALT body calls this).</summary>
    private void DoHalt() => _halted = true;

    /// <summary>Undefined-opcode hook — stub. The Z80 has no illegal-opcode trap (most undocumented
    /// bytes alias documented ops); the real policy is M3.4. Charges one cycle.</summary>
    private void HandleUndefinedOpcode(byte opcode) => _cycles++;

    /// <summary>No interrupt servicing in the skeleton (M3.4 owns IM 0/1/2 + the latch wake).</summary>
    private partial bool TryServiceInterrupt() => false;
}
