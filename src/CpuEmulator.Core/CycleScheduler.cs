namespace CpuEmulator.Core;

/// <summary>Minimal M1 scheduler: a cycle counter plus a priority-queue event list.
/// Same-cycle events fire in FIFO (scheduling) order.</summary>
public sealed class CycleScheduler : IScheduler
{
    private readonly PriorityQueue<Action, (long Cycle, ulong Seq)> _queue = new();
    private ulong _nextSeq;

    public long CurrentCycle { get; private set; }

    public void ScheduleAt(long cycle, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, CurrentCycle);
        _queue.Enqueue(callback, (cycle, _nextSeq++));
    }

    /// <summary>Advance time to <paramref name="cycle"/>, firing due callbacks in cycle order
    /// (FIFO within a cycle). Machine-driver only — not part of <see cref="IScheduler"/>.
    /// Targets below <see cref="CurrentCycle"/> fire nothing and do not move time; a target at
    /// <see cref="CurrentCycle"/> fires events pending at exactly that cycle.
    /// If a callback throws, its event is already consumed, <see cref="CurrentCycle"/> rests at
    /// that event's cycle, and the remaining queue is intact.</summary>
    public void AdvanceTo(long cycle)
    {
        while (_queue.TryPeek(out _, out (long Cycle, ulong Seq) due) && due.Cycle <= cycle)
        {
            CurrentCycle = due.Cycle;
            _queue.Dequeue()();
        }
        if (cycle > CurrentCycle)
            CurrentCycle = cycle;
    }
}
