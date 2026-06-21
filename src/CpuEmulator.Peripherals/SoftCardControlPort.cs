using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Z-80 SoftCard control register at the slot's $CN00 page (ADR 0015 Decision 3 as amended by
/// ADR 0017 Decision 2; research §1). A WRITE toggles which CPU is the bus master: from 6502 mode a $CN00
/// write hands control to the Z80 (and DMA-suspends the 6502); the Z80's matching write (which it sees as
/// $EN00, translated back to $CN00 by SoftCardTranslation branch 5) hands control back to the 6502. A READ
/// is a bus read of a register-less slot -> OPEN-BUS (0x00) with NO toggle: the control semantics are
/// write-only. (ADR 0015 said "fire on any access"; ADR 0017's live boot proved a read-toggle livelocks the
/// SoftCard-detect poll -> CAN'T FIND Z80 SOFTCARD; the real card has no readable status -- research §9 has
/// no onboard ROM/RAM.) The toggle is performed through ICoprocessorControl, captured from the Realize
/// context (the dual-CPU Machine IS the IMachineContext and implements ICoprocessorControl). On a single-CPU
/// board the cast fails and the port is inert (never an exception).
/// <para>PEEK-FREE (the ][+ invariant, ADR 0014 Decision 2): a debugger LOOKING at the control register
/// must NOT switch CPUs -- TryPeek returns open-bus 0 with no side effect.</para></summary>
public sealed class SoftCardControlPort : IPeripheral
{
    private ICoprocessorControl? _ctl;
    private bool _coprocessorActive;

    public string Name => "softcard";

    public void Realize(IMachineContext context)
    {
        // The dual-CPU Machine is the IMachineContext and implements ICoprocessorControl; capture it so a
        // bus access can flip the active CPU. A single-CPU context fails the cast -> the port is inert.
        if (context is ICoprocessorControl ctl)
            _ctl = ctl;
    }

    public uint Read(uint offset, AccessWidth width) => 0x00;   // open-bus, NO Toggle (ADR 0017 Decision 2)

    public void Write(uint offset, AccessWidth width, uint value) => Toggle();

    public bool TryPeek(uint offset, out byte value)
    {
        // PEEK-FREE: a debugger view must not switch CPUs. No Toggle().
        value = 0x00;
        return true;
    }

    private void Toggle()
    {
        _coprocessorActive = !_coprocessorActive;
        _ctl?.SetCoprocessorActive(_coprocessorActive);
    }
}
