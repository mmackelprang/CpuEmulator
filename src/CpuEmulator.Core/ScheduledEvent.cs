namespace CpuEmulator.Core;

/// <summary>
/// Cancellation handle for a scheduled callback (one-shot or repeating). Cancel is
/// idempotent and safe at any time: before the fire (the event never runs), inside its
/// own callback (a repeating chain stops), or after a one-shot fired (no-op). Lazy: the
/// scheduler discards the entry when it surfaces — it fires nothing, moves no time.
/// </summary>
public sealed class ScheduledEvent
{
    internal ScheduledEvent(Action callback, long interval) =>
        (Callback, Interval) = (callback, interval);

    internal Action Callback { get; }
    internal long Interval { get; }   // repeat interval in cycles; 0 = one-shot
    public bool IsCanceled { get; private set; }
    public void Cancel() => IsCanceled = true;
}
