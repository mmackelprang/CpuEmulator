using CpuEmulator.Core;
using CpuEmulator.Core.Specification;

namespace CpuEmulator.Cpus.Mos6502;

/// <summary>Hand-written half of the MOS 6502: bus wiring, reset, interrupt servicing,
/// and undefined-opcode policy. The generated half (see obj/generated/) owns state,
/// introspection, and the Step/Run/Execute pipeline.
///
/// Interrupt policy: NMI is edge-triggered — a rising edge of <see cref="SetNmiLine"/>
/// sets an internal pending latch; the latch clears when serviced and on <see cref="Reset"/>.
/// A held-high line never re-fires until released and re-asserted.
/// IRQ is level-sensitive — serviced at a boundary whenever the line is high and the I flag
/// is clear. NMI beats IRQ when both are pending. The 7-cycle service sequence (two dummy
/// reads at PC, push PCH/PCL/P with B=0, vector fetch, I set) matches 64doc.
/// Mid-instruction sampling quirks (CLI/SEI/PLP delay, branch polling, BRK hijacking)
/// are a recorded M1 deviation — see spec §6.</summary>
public sealed partial class Mos6502Cpu
{
    private readonly IAddressSpace _bus;
    private readonly UndefinedOpcodePolicy _undefinedPolicy;
    private bool _irqLine;
    private bool _nmiLine;
    private bool _nmiPending;

    public Mos6502Cpu(IAddressSpace bus, UndefinedOpcodePolicy undefinedPolicy = UndefinedOpcodePolicy.Throw)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _undefinedPolicy = undefinedPolicy;
    }

    /// <summary>6502 reset: load PC from the vector at $FFFC/$FFFD, S = $FD, I set.
    /// Costs the authentic 7 cycles, charged coarsely (2 vector reads + 5 internal) —
    /// per-cycle reset bus activity is a documented M1 deviation.</summary>
    public void Reset()
    {
        byte lo = ReadBus(0xFFFC);
        byte hi = ReadBus(0xFFFD);
        PC = (ushort)(lo | (hi << 8));
        S = 0xFD;
        P = 0x34; // I + bits 5,4: stored P models the phantom B/unused bits as set (NESdev
                  // power-up convention); chunk 3's PHP/PLP/BRK logic owns the bit-4/5
                  // push/pull conventions.
        _cycles += 5;
        // Reset clears the pending-NMI latch (2a carry-forward); line LEVELS are external
        // and untouched — a held NMI line re-latches only on a fresh rising edge.
        // Second-reset silicon semantics (S-=3, I-only) remain coarsely modeled.
        _nmiPending = false;
    }

    /// <summary>IRQ is level-sensitive: the line is sampled at every instruction boundary
    /// and serviced when high and the I flag is clear.</summary>
    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    /// <summary>NMI is edge-triggered: a rising edge sets the pending latch; the latch
    /// clears when serviced and on Reset. A held-high line never re-fires until released
    /// and re-asserted.</summary>
    public void SetNmiLine(bool asserted)
    {
        if (asserted && !_nmiLine)
            _nmiPending = true;
        _nmiLine = asserted;
    }

    /// <summary>True exactly when the next Step will service an interrupt — the same
    /// predicate <see cref="TryServiceInterrupt"/> gates on (IMonitorSupport contract:
    /// step displays say "interrupt serviced", not the instruction that will not run).</summary>
    public partial bool InterruptPending => _nmiPending || (_irqLine && (P & 0x04) == 0);

    // NOTE (M3.5-3a): the 6502's `Halted` is now GENERATED as a constant-false property (every CPU
    // satisfies IMonitorSupport.Halted uniformly — the generator emits `public bool Halted => false;`
    // for a no-HALT CPU). The former hand-written `Halted => false` hook was removed here to avoid a
    // duplicate member; behavior is identical (the JIT halted branch stays dead for the 6502).

    /// <summary>Instruction-boundary interrupt service (generated Step calls this before
    /// the opcode fetch). NMI beats IRQ. The 7-cycle sequence mirrors 64doc: two dummy
    /// reads at PC (the discarded fetch), push PCH/PCL, push P with B clear
    /// ((P | 0x20) &amp; 0xEF — bit 4 clear distinguishes hardware interrupts from BRK),
    /// vector fetch ($FFFA/$FFFB for NMI, $FFFE/$FFFF for IRQ), I set.
    /// Mid-instruction sampling quirks (CLI/SEI/PLP delay, branch polling, BRK hijacking)
    /// are a recorded M1 deviation — see spec §6.</summary>
    private partial bool TryServiceInterrupt()
    {
        bool nmi = _nmiPending;
        if (!nmi && (!_irqLine || (P & 0x04) != 0))
            return false;
        if (nmi)
            _nmiPending = false;

        _ = ReadBus(PC); // dummy opcode fetch (discarded, PC not incremented)
        _ = ReadBus(PC); // second dummy read at PC
        WriteBus(0x100u + S, unchecked((byte)(PC >> 8)));
        S = unchecked((byte)(S - 1));
        WriteBus(0x100u + S, unchecked((byte)PC));
        S = unchecked((byte)(S - 1));
        WriteBus(0x100u + S, unchecked((byte)((P | 0x20) & 0xEF))); // stacked B=0
        S = unchecked((byte)(S - 1));
        uint vector = nmi ? 0xFFFAu : 0xFFFEu;
        uint lo = ReadBus(vector);
        uint hi = ReadBus(vector + 1);
        P = unchecked((byte)(P | 0x04));
        PC = unchecked((ushort)(lo | (hi << 8)));
        return true;
    }

    /// <summary>The IL-JIT cycle seam (internal — CpuEmulator.Jit only). The emitted fastmem
    /// fast path bypasses the bus (and thus ReadBus/WriteBus, which own _cycles for the
    /// interpreter), so it calls this to advance the cycle counter by the same amount the
    /// interpreter would have charged. Chosen over baking a FieldInfo for the private _cycles
    /// so the generator stays untouched and the interpreter's cycle invariant lives in one
    /// place. Reached via InternalsVisibleTo("CpuEmulator.Jit") + DynamicMethod(skipVisibility:
    /// true). Not part of the interpreter's own execution path.</summary>
    internal void AdvanceCycles(long n) => _cycles += n;

    private byte ReadBus(uint address)
    {
        _cycles++;
        return _bus.Read8(address);
    }

    private void WriteBus(uint address, byte value)
    {
        _cycles++;
        _bus.Write8(address, value);
    }

    private void HandleUndefinedOpcode(byte opcode)
    {
        if (_undefinedPolicy == UndefinedOpcodePolicy.Nop)
        {
            _cycles++; // 2-cycle NOP total: opcode fetch + one internal cycle
            return;
        }
        throw new UndefinedOpcodeException(opcode, (uint)((PC - 1) & 0xFFFF));
    }
}
