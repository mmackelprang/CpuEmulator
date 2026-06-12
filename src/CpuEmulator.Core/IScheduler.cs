namespace CpuEmulator.Core;

/// <summary>
/// The machine's clock as seen by devices: a cycle counter plus an event queue.
/// Grown to its planned shape in the devices chunk (PR #11). Advancing time is the
/// machine driver's job — the concrete CycleScheduler.AdvanceTo — and is intentionally
/// absent from this consumer-facing contract.
/// </summary>
public interface IScheduler
{
    /// <summary>Device-honest "now": committed time, OR the CPU's live cycle count when
    /// the machine has bound one (mid-slice device accesses see real time), OR — during
    /// event dispatch — the firing event's exact cycle (callbacks observe their own fire
    /// time).</summary>
    long CurrentCycle { get; }

    /// <summary>Schedule a callback at an absolute cycle. Scheduling in the past throws
    /// <see cref="ArgumentOutOfRangeException"/> (argument precondition, not an emulation fault);
    /// scheduling at the current cycle is allowed and fires on the next advance — or within the
    /// current one if called from a callback. Same-cycle callbacks fire in FIFO order.
    /// Returns a cancellation handle.</summary>
    ScheduledEvent ScheduleAt(long cycle, Action callback);

    /// <summary>Schedule a repeating callback. First fire at CurrentCycle + interval,
    /// then every interval thereafter. <paramref name="interval"/> &lt;= 0 throws
    /// <see cref="ArgumentOutOfRangeException"/>. One handle cancels the whole chain.
    /// Returns a cancellation handle.</summary>
    ScheduledEvent ScheduleEvery(long interval, Action callback);
}
