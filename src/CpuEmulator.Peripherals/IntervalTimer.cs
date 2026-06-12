using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// A 16-bit cycle-domain interval timer over a 4-register window, partially decoded
/// (offset &amp; 0x03) like SimpleUart — it mirrors through whatever span it is mapped over.
///
///   offset 0  CTRL     bit0 enable · bit1 irq-enable · bit2 repeat     read: live bits
///   offset 1  PERIODL  period low byte (cycles)                        read: latched value
///   offset 2  PERIODH  period high byte                                read: latched value
///   offset 3  STATUS   bit0 fired                                      write: 1 → clear
///
/// Contracts (Ground truth D): PERIOD is 16-bit cycles; PERIOD == 0 means 65536 (the wrap
/// convention — no dead enable state). Enable 0→1 schedules the fire PERIOD cycles from the
/// device-honest now; enable 1→0 cancels the pending fire. PERIOD writes while enabled do
/// NOT retime a pending fire. One-shot (repeat=0) fires once at enable+PERIOD, sets fired,
/// and clears its own enable bit. Repeat schedules ScheduleEvery(PERIOD) until disabled;
/// clearing the repeat bit mid-flight makes the next fire the last (the fire path cancels
/// the chain). STATUS is write-1-clear so every READ is side-effect-free — TryPeek is the
/// identity. IRQ is level-shaped: asserted while fired &amp;&amp; irq-enable. Realize claims
/// Scheduler + IrqLine.Source(); enabling an unrealized timer throws (host-world
/// composition error — a machine-composed timer is always realized).
/// </summary>
public sealed class IntervalTimer : IPeripheral
{
    private IScheduler? _scheduler;
    private IInterruptLine? _irq;
    private ScheduledEvent? _pending;
    private byte _ctrl;
    private ushort _period;
    private bool _fired;

    public string Name => "timer";

    private bool Enabled => (_ctrl & 0x01) != 0;
    private bool IrqEnabled => (_ctrl & 0x02) != 0;
    private bool Repeat => (_ctrl & 0x04) != 0;
    private long EffectivePeriod => _period == 0 ? 0x10000 : _period;

    public void Realize(IMachineContext context)
    {
        _scheduler = context.Scheduler;
        _irq = context.IrqLine.Source();
    }

    public uint Read(uint offset, AccessWidth width) => (offset & 0x03) switch
    {
        0 => _ctrl,
        1 => (uint)(_period & 0xFF),
        2 => (uint)(_period >> 8),
        _ => _fired ? 0x01u : 0x00u,
    };

    /// <summary>Every timer read is side-effect-free (STATUS is write-1-clear), so the
    /// honest peek is the read itself.</summary>
    public bool TryPeek(uint offset, out byte value)
    {
        value = (byte)Read(offset, AccessWidth.Byte);
        return true;
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        byte b = unchecked((byte)value);
        switch (offset & 0x03)
        {
            case 0: WriteCtrl(b); break;
            case 1: _period = (ushort)((_period & 0xFF00) | b); break;
            case 2: _period = (ushort)((_period & 0x00FF) | (b << 8)); break;
            default: // STATUS: write-1-clear
                if ((b & 0x01) != 0) { _fired = false; UpdateIrqLevel(); }
                break;
        }
    }

    private void WriteCtrl(byte value)
    {
        bool wasEnabled = Enabled;
        _ctrl = (byte)(value & 0x07);
        if (Enabled && !wasEnabled) Schedule();
        else if (!Enabled && wasEnabled) { _pending?.Cancel(); _pending = null; }
        UpdateIrqLevel(); // irq-enable may have changed while fired
    }

    private void Schedule()
    {
        if (_scheduler is null)
            throw new MachineConfigurationException(
                "IntervalTimer enabled before Realize — compose it via Machine.");
        _pending?.Cancel();
        _pending = Repeat
            ? _scheduler.ScheduleEvery(EffectivePeriod, Fire)
            : _scheduler.ScheduleAt(_scheduler.CurrentCycle + EffectivePeriod, Fire);
    }

    private void Fire()
    {
        _fired = true;
        if (!Repeat) // one-shot — or repeat cleared mid-flight: stop the chain,
        {            // and the enable bit clears itself
            _pending?.Cancel();
            _pending = null;
            _ctrl &= 0xFE;
        }
        UpdateIrqLevel();
    }

    private void UpdateIrqLevel()
    {
        if (_irq is null) return;
        if (_fired && IrqEnabled) _irq.Assert();
        else _irq.Release();
    }
}
