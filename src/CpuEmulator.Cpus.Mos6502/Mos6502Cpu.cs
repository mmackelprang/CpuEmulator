using CpuEmulator.Core;
using CpuEmulator.Core.Specification;

namespace CpuEmulator.Cpus.Mos6502;

/// <summary>Hand-written half of the 6502: bus wiring, reset, undefined-opcode policy, and
/// interrupt-line recording. The generated half (see obj/generated/) owns state,
/// introspection, and the Step/Run/Execute pipeline.</summary>
public sealed partial class Mos6502Cpu
{
    private readonly IAddressSpace _bus;
    private readonly UndefinedOpcodePolicy _undefinedPolicy;
    private bool _irqLine;
    private bool _nmiLine;

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
        P = 0x34; // I and the always-set bit; matches power-on convention
        _cycles += 5;
    }

    /// <summary>Lines are recorded but not yet serviced — the interrupt sequence lands in
    /// chunk 3 with the full instruction set.</summary>
    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    public void SetNmiLine(bool asserted) => _nmiLine = asserted;

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
