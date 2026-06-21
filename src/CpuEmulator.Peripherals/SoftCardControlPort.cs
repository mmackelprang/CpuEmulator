using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Z-80 SoftCard control register at the slot's $CN00 page (ADR 0015 Decision 3; research
/// §1). A write toggles which CPU is the bus master: from 6502 mode a $CN00 write hands control to the
/// Z80 (and DMA-suspends the 6502); the Z80's matching write (which it sees as $EN00, translated back to
/// $CN00 by SoftCardTranslation branch 5) hands control back to the 6502. Modeled as a FLIP on each
/// access (the single-register toggle; research §1 "the decoder likely fires on any access" — so Read
/// mirrors Write). The flip is performed through ICoprocessorControl, captured from the Realize context
/// (the dual-CPU Machine IS the IMachineContext and implements ICoprocessorControl). On a single-CPU
/// board the cast fails and the port is inert (never an exception).
/// <para>PEEK-FREE (the ][+ invariant, ADR 0014 Decision 2): a debugger LOOKING at the control register
/// must NOT switch CPUs — TryPeek returns open-bus 0 with no side effect, mirroring Apple2Iou's peek-free
/// short-circuits on its side-effecting switches.</para></summary>
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

    public uint Read(uint offset, AccessWidth width)
    {
        Toggle();                 // any access fires the toggle (research §1)
        return 0x00;
    }

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
