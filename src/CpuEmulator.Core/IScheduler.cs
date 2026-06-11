namespace CpuEmulator.Core;

/// <summary>
/// The machine's clock: a cycle counter plus an event queue. Deliberately minimal in M1;
/// grows with the timer milestone. Defining it now prevents peripherals from inventing
/// their own notion of time.
/// </summary>
public interface IScheduler
{
    long CurrentCycle { get; }

    /// <summary>Schedule a callback at an absolute cycle (must not be in the past).</summary>
    void ScheduleAt(long cycle, Action callback);

    /// <summary>Advance time to <paramref name="cycle"/>, firing due callbacks in cycle order.</summary>
    void AdvanceTo(long cycle);
}
