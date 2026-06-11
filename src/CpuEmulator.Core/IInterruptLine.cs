namespace CpuEmulator.Core;

/// <summary>A level-sensitive interrupt line input, as seen by the device asserting it.</summary>
public interface IInterruptLine
{
    bool IsAsserted { get; }
    void Assert();
    void Release();
}
