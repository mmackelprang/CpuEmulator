namespace CpuEmulator.Core;

/// <summary>
/// Forwards assert/release to a CPU line input. Single-source in M1; wired-OR sharing
/// between multiple devices arrives with the interrupt-controller milestone.
/// </summary>
public sealed class InterruptLine : IInterruptLine
{
    private readonly Action<bool> _setLine;

    public InterruptLine(Action<bool> setLine)
    {
        ArgumentNullException.ThrowIfNull(setLine);
        _setLine = setLine;
    }

    public bool IsAsserted { get; private set; }

    public void Assert()
    {
        IsAsserted = true;
        _setLine(true);
    }

    public void Release()
    {
        IsAsserted = false;
        _setLine(false);
    }
}
