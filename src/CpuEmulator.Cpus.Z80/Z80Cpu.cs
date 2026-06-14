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

    /// <summary>The interrupt-enable latches (M3.4a). DI clears both; EI sets both; observable in the
    /// TomHarte final state's iff1/iff2. Interrupt ACKNOWLEDGE/vectoring is M3.4b.</summary>
    private bool _iff1;
    private bool _iff2;

    /// <summary>IFF1 — the master interrupt-enable latch (observable Z80 state; the TomHarte vectors
    /// check it for DI/EI). Settable so a harness can establish the initial state.</summary>
    public bool Iff1 { get => _iff1; set => _iff1 = value; }

    /// <summary>IFF2 — the shadow interrupt-enable latch (saved by an interrupt, restored by RETN).</summary>
    public bool Iff2 { get => _iff2; set => _iff2 = value; }

    /// <summary>The Q pseudo-register (M3.4a) — the documented SCF/CCF X/Y quirk. After an instruction
    /// that modified the flags, Q = F; after one that did not, Q = 0. SCF/CCF compute their X/Y bits
    /// from <c>(Q ^ F) | A</c> (TomHarte's `q` field). The generated SCF/CCF body reads <c>Q</c>; the
    /// harness sets the INITIAL q so the single-instruction vector's X/Y is exact. (Maintaining Q
    /// across instructions lands with the block ops, M3.4b.)</summary>
    public byte Q;

    /// <summary>The interrupt mode (M3.4c) — 0, 1, or 2, set by the ED <c>IM 0/1/2</c> ops. Observable in
    /// the TomHarte final state's <c>im</c>. Interrupt SERVICING (vectoring per this mode) is M3.5; this
    /// field is the mode STATE only. Settable so the harness can establish the initial mode.</summary>
    public int Im;

    /// <summary>The M3.2 two-bus ctor: the program/data bus + the I/O AddressSpace(Io, 16). A null
    /// I/O bus defaults to a fresh 16-bit Io space (the Z80 port range).</summary>
    public Z80Cpu(IAddressSpace bus, IAddressSpace? io = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _io  = io ?? new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
    }

    /// <summary>The documented Z80 reset state (M3.4a): PC=0, I=0, R=0, IFF1=IFF2=0, SP=0xFFFF. The
    /// TomHarte runner sets every register explicitly, so reset's exact values are not on the vector
    /// critical path — but the real reset is now modeled. Also clears the halted latch.</summary>
    public void Reset()
    {
        PC = 0;
        I = 0;
        R = 0;
        SP = 0xFFFF;
        _iff1 = false;
        _iff2 = false;
        _halted = false;
    }

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

    /// <summary>No interrupt servicing in the base plane (M3.4b owns IM 0/1/2 + the latch wake).</summary>
    private partial bool TryServiceInterrupt() => false;

    /// <summary>The R-refresh increment (M3.4a, Ground truth F). The low 7 bits of R increment on each
    /// opcode-fetch M1 cycle (bit 7 is preserved). The generated Step calls this once per instruction
    /// with the count of key bytes fetched (1 for a base-plane opcode; a prefix adds an M1 — M3.4b). The
    /// base plane fetches one opcode byte, so R bumps by 1. TomHarte's `r` field checks this.</summary>
    partial void OnInstructionFetched(int keyBytes)
    {
        for (int i = 0; i < keyBytes; i++)
            R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
    }
}
