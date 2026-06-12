namespace CpuEmulator.Core;

/// <summary>
/// A wired-OR interrupt line: asserted while ANY input is — its own direct Assert/Release
/// (one implicit source; single-source behavior preserved exactly) or any per-device
/// handle from <see cref="Source"/>. Every input transition forwards the COMPUTED level
/// (call-per-event; the OR lives in the value). Re-presenting a high level is safe:
/// level consumers store it idempotently; edge consumers (the 6502 NMI latch) edge-detect
/// against their own previous line state.
/// </summary>
public sealed class InterruptLine : IInterruptLine
{
    private readonly Action<bool> _setLine;
    private readonly List<SourceHandle> _sources = [];
    private bool _direct;

    public InterruptLine(Action<bool> setLine)
    {
        ArgumentNullException.ThrowIfNull(setLine);
        _setLine = setLine;
    }

    /// <summary>The computed wired-OR level.</summary>
    public bool IsAsserted { get; private set; }

    public void Assert() { _direct = true; Forward(); }
    public void Release() { _direct = false; Forward(); }

    /// <summary>Create an independent per-device handle on this line. A source's
    /// Assert/Release sets only its own state; the line stays high while any input is.</summary>
    public IInterruptLine Source()
    {
        var handle = new SourceHandle(this);
        _sources.Add(handle);
        return handle;
    }

    private void Forward()
    {
        bool level = _direct;
        foreach (SourceHandle source in _sources)
            level |= source.IsAsserted;
        IsAsserted = level;
        _setLine(level);
    }

    private sealed class SourceHandle(InterruptLine line) : IInterruptLine
    {
        public bool IsAsserted { get; private set; }
        public void Assert() { IsAsserted = true; line.Forward(); }
        public void Release() { IsAsserted = false; line.Forward(); }
        public IInterruptLine Source() => line.Source();
    }
}
