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

    /// <summary>The maskable INT line LEVEL (M3.5-1). Level-sensitive: serviced at any instruction
    /// boundary while high AND IFF1 is set. Set by the host/peripheral via SetIrqLine.</summary>
    private bool _irqLine;

    /// <summary>The NMI line level (for edge detection) + the edge-latched pending flag (M3.5-1). NMI is
    /// edge-triggered: a rising edge sets _nmiPending; the latch clears when serviced and on Reset.</summary>
    private bool _nmiLine;
    private bool _nmiPending;

    /// <summary>The byte the device places on the data bus during an interrupt acknowledge (M3.5-1).
    /// IM 0 decodes it as the supplied opcode (the common RST n case); IM 2 uses it as the low byte of the
    /// vector-table pointer. Host/UAT-settable; default 0xFF (IM 0 → RST 38h, the common power-on form).</summary>
    public byte InterruptData { get; set; } = 0xFF;

    /// <summary>The EI one-instruction-delay window (M3.5-1). EI's body sets IFF1/IFF2 IMMEDIATELY (so the
    /// single-step TomHarte EI vector's final iff1/iff2 stay correct — fb.json) but ALSO opens a one-
    /// instruction "do-not-service-yet" window by setting this to 1. Servicing (and InterruptPending)
    /// requires _eiPending == 0, so the instruction immediately AFTER EI runs without being interrupted
    /// (the documented Z80 quirk). Each TryServiceInterrupt boundary decrements it toward 0, so the NEXT
    /// boundary services. Cleared on Reset.</summary>
    private int _eiPending;

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
        _nmiPending = false;
        _nmiLine = false;
        _irqLine = false;
        _eiPending = 0;
    }

    /// <summary>The maskable INT line is level-sensitive: sampled at every instruction boundary and
    /// serviced when high and IFF1 is set (M3.5-1).</summary>
    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    /// <summary>NMI is edge-triggered: a rising edge sets the pending latch; the latch clears when
    /// serviced and on Reset. A held-high line never re-fires until released and re-asserted (M3.5-1).</summary>
    public void SetNmiLine(bool asserted)
    {
        if (asserted && !_nmiLine)
            _nmiPending = true;
        _nmiLine = asserted;
    }

    /// <summary>True exactly when the next Step will service an interrupt — NMI (non-maskable, edge-
    /// latched) or a maskable INT gated by IFF1 AND the EI-delay window being closed (M3.5-1). The JIT
    /// boundary-samples this policy-blind.</summary>
    public partial bool InterruptPending => _nmiPending || (_irqLine && _iff1 && _eiPending == 0);

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

    /// <summary>Instruction-boundary interrupt service (the generated Step calls this before the opcode
    /// fetch — CpuEmitter.cs:147). NMI beats a maskable INT. Returns false when nothing is pending (so
    /// every TomHarte single-step case is unaffected). When it services, it performs the full push/vector
    /// bus sequence itself (charging cycles via ReadBus/WriteBus + the M1-acknowledge internal T-states
    /// via _cycles), saves/clears IFF per the Z80 model, bumps R for the acknowledge M1 cycle, clears the
    /// halted latch (the HALT wake), sets WZ to the vector, and returns true. M3.5-1.</summary>
    private partial bool TryServiceInterrupt()
    {
        // M3.5-1: nothing eligible to service? EI's body set IFF1/IFF2 immediately (the architectural
        // latch, vector-correct — fb.json) AND opened a one-instruction no-service window (_eiPending=1).
        // The maskable INT requires IFF1 AND a CLOSED EI window (_eiPending == 0); NMI is non-maskable and
        // ignores the window. When we cannot service, we close the EI window by one step (so the boundary
        // AFTER the instruction following EI becomes eligible) and return false.
        if (!_nmiPending && !(_irqLine && _iff1 && _eiPending == 0))
        {
            if (_eiPending > 0)
                _eiPending--;   // the instruction after EI ran without service; arm the next boundary
            return false;
        }

        // A serviced boundary closes any open EI window too (it cannot be open here for a maskable INT —
        // the eligibility check above required _eiPending == 0 — but an NMI may interrupt the EI-delay).
        _eiPending = 0;

        _halted = false;        // the HALT wake — resume fetch on the next Step
        BumpRefresh();          // the acknowledge is one M1 cycle → R low-7 += 1

        if (_nmiPending)
        {
            _nmiPending = false;
            _iff2 = _iff1;      // NMI saves IFF1 into IFF2 (RETN restores it)
            _iff1 = false;      // ...and disables maskable interrupts
            PushPc();
            PC = 0x0066;
            WZ = 0x0066;
            _cycles += 11 - 2;  // NMI = 11 T; PushPc charged 2 (two WriteBus)
            return true;
        }

        // Maskable INT acknowledge: clear BOTH flip-flops (nested IRQ masked until EI).
        _iff1 = false;
        _iff2 = false;

        switch (Im)
        {
            case 0: // IM 0: the device supplies an opcode — model the common RST n form.
            {
                // An RST opcode is 11_yyy_111 (0xC7|y<<3); its vector is (y<<3) = opcode & 0x38.
                // Default (InterruptData 0xFF = RST 38h) → 0x0038. Any non-RST byte → 0x0038 fallback.
                ushort vector = (InterruptData & 0xC7) == 0xC7
                    ? (ushort)(InterruptData & 0x38)
                    : (ushort)0x0038;
                PushPc();
                PC = vector;
                WZ = vector;
                _cycles += 13 - 2;  // IM0 RST = 13 T; PushPc charged 2
                return true;
            }

            case 2: // IM 2: I-register high byte + device-byte low byte → table pointer → vector.
            {
                ushort ptr = unchecked((ushort)((I << 8) | (InterruptData & 0xFE)));
                byte vlo = ReadBus(ptr);
                byte vhi = ReadBus(unchecked((ushort)(ptr + 1)));
                ushort vector = unchecked((ushort)(vlo | (vhi << 8)));
                PushPc();
                PC = vector;
                WZ = vector;
                _cycles += 19 - 4;  // IM2 = 19 T; PushPc(2) + two vector ReadBus(2) charged 4
                return true;
            }

            default: // IM 1 (and the IM-1 fallback): fixed RST 38h.
                PushPc();
                PC = 0x0038;
                WZ = 0x0038;
                _cycles += 13 - 2;  // IM1 = 13 T; PushPc charged 2
                return true;
        }
    }

    /// <summary>Push the current PC (PCH then PCL, little-endian on the descending stack), charging the
    /// two write cycles. The pushed PC is the return address — the instruction that would have run next.</summary>
    private void PushPc()
    {
        SP = unchecked((ushort)(SP - 1));
        WriteBus(SP, unchecked((byte)(PC >> 8)));   // PCH
        SP = unchecked((ushort)(SP - 1));
        WriteBus(SP, unchecked((byte)PC));          // PCL
    }

    /// <summary>Bump R's low 7 bits (bit 7 preserved) — the interrupt acknowledge is an M1 cycle, so R
    /// increments exactly as a one-byte opcode fetch does (same formula as OnInstructionFetched).</summary>
    private void BumpRefresh() => R = (byte)((R & 0x80) | ((R + 1) & 0x7F));

    /// <summary>The EI micro-op body calls this (M3.5-1, the one generator touch). EI enables interrupts
    /// architecturally NOW (IFF1/IFF2 := true — vector-correct for the single-step TomHarte EI vector's
    /// final iff1/iff2, fb.json) but opens a one-instruction "do-not-service-yet" window so the
    /// instruction immediately following EI is not interrupted (the documented Z80 quirk). The window
    /// (_eiPending) closes one boundary at a time in TryServiceInterrupt.</summary>
    partial void OnInterruptEnable()
    {
        _iff1 = true;
        _iff2 = true;
        _eiPending = 1;
    }

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
