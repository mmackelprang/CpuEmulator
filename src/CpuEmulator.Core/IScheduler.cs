namespace CpuEmulator.Core;

/// <summary>
/// The machine's clock as seen by devices: a cycle counter plus an event queue.
/// Deliberately minimal in M1; grows with the timer milestone. Defining it now prevents
/// peripherals from inventing their own notion of time.
/// Advancing time is the machine driver's job — the concrete CycleScheduler.AdvanceTo —
/// and is intentionally absent from this consumer-facing contract.
/// </summary>
public interface IScheduler
{
    long CurrentCycle { get; }

    /// <summary>Schedule a callback at an absolute cycle. Scheduling in the past throws
    /// <see cref="ArgumentOutOfRangeException"/> (argument precondition, not an emulation fault);
    /// scheduling at the current cycle is allowed and fires on the next advance — or within the
    /// current one if called from a callback. Same-cycle callbacks fire in FIFO order.</summary>
    void ScheduleAt(long cycle, Action callback);
}
