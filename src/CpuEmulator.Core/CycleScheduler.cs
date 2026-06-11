namespace CpuEmulator.Core;

/// <summary>Minimal M1 scheduler: a cycle counter plus a priority-queue event list.</summary>
public sealed class CycleScheduler : IScheduler
{
    private readonly PriorityQueue<Action, long> _queue = new();

    public long CurrentCycle { get; private set; }

    public void ScheduleAt(long cycle, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, CurrentCycle);
        _queue.Enqueue(callback, cycle);
    }

    /// <summary>Advance time to <paramref name="cycle"/>, firing due callbacks in cycle order.
    /// Machine-driver only — not part of <see cref="IScheduler"/>.</summary>
    public void AdvanceTo(long cycle)
    {
        while (_queue.TryPeek(out _, out long due) && due <= cycle)
        {
            _queue.TryDequeue(out Action? callback, out long at);
            CurrentCycle = at;
            callback!();
        }
        if (cycle > CurrentCycle)
            CurrentCycle = cycle;
    }
}
