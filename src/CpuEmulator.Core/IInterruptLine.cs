namespace CpuEmulator.Core;

/// <summary>
/// A wired-OR interrupt line input, as seen by the device asserting it. Each device
/// obtains its own handle via <see cref="Source"/>; the line stays high while any
/// handle (or the line itself) is asserted.
/// </summary>
public interface IInterruptLine
{
    bool IsAsserted { get; }
    void Assert();
    void Release();

    /// <summary>Create an independent per-device handle on this line. A source's
    /// Assert/Release sets only its own state; the line stays high while any input is.
    /// <c>source.Source()</c> joins the same wired-OR.</summary>
    IInterruptLine Source();
}
