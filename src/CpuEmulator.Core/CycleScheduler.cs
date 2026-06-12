namespace CpuEmulator.Core;

/// <summary>Same-cycle events fire in FIFO (scheduling) order.</summary>
public sealed class CycleScheduler : IScheduler
{
    private readonly PriorityQueue<ScheduledEvent, (long Cycle, ulong Seq)> _queue = new();
    private ulong _nextSeq;
    private long _committed;
    private long _dispatchCycle = -1; // ≥ 0 while a callback is running (its exact cycle)
    private Func<long>? _now;

    /// <summary>Device-honest "now": committed time; the CPU's live cycle count when one
    /// is bound (mid-slice device accesses see real time); or, during dispatch, the firing
    /// event's exact cycle (callbacks observe their own fire time).</summary>
    public long CurrentCycle =>
        _dispatchCycle >= 0 ? _dispatchCycle
        : _now is null ? _committed
        : Math.Max(_committed, _now());

    /// <summary>Machine-driver only: bind the CPU's live cycle counter (mid-slice MMIO
    /// scheduling becomes exact).</summary>
    internal void BindTimeSource(Func<long> now) => _now = now;

    public ScheduledEvent ScheduleAt(long cycle, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, CurrentCycle);
        var evt = new ScheduledEvent(callback, interval: 0);
        _queue.Enqueue(evt, (cycle, _nextSeq++));
        return evt;
    }

    public ScheduledEvent ScheduleEvery(long interval, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        var evt = new ScheduledEvent(callback, interval);
        _queue.Enqueue(evt, (CurrentCycle + interval, _nextSeq++));
        return evt;
    }

    /// <summary>Machine-driver only: the cycle of the next live (non-canceled) event,
    /// discarding canceled heads as they surface. False when nothing is pending.</summary>
    internal bool TryPeekNextEventCycle(out long cycle)
    {
        while (_queue.TryPeek(out ScheduledEvent? head, out (long Cycle, ulong Seq) due))
        {
            if (!head.IsCanceled) { cycle = due.Cycle; return true; }
            _queue.Dequeue(); // lazy removal
        }
        cycle = 0;
        return false;
    }

    /// <summary>Advance time, firing due live callbacks in cycle order (FIFO within a
    /// cycle). Repeats re-enqueue BEFORE the callback runs: a throwing repeat callback
    /// leaves its next occurrence queued; a callback canceling its own handle stops the
    /// chain. If a callback throws, its event is consumed, committed time rests at that
    /// event's cycle, and the queue is intact.</summary>
    public void AdvanceTo(long cycle)
    {
        while (_queue.TryPeek(out ScheduledEvent? evt, out (long Cycle, ulong Seq) due)
               && due.Cycle <= cycle)
        {
            _queue.Dequeue();
            if (evt.IsCanceled)
                continue; // canceled: fires nothing, moves no time
            _committed = due.Cycle;
            if (evt.Interval > 0)
                _queue.Enqueue(evt, (due.Cycle + evt.Interval, _nextSeq++));
            _dispatchCycle = due.Cycle;
            try { evt.Callback(); }
            finally { _dispatchCycle = -1; }
        }
        if (cycle > _committed)
            _committed = cycle;
    }
}
