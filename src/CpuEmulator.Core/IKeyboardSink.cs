namespace CpuEmulator.Core;

/// <summary>
/// A keyboard input a chip exposes to the host, IN ADDITION TO <see cref="IPeripheral"/>. The
/// host PUSHES normalized <see cref="KeyEvent"/>s; the chip owns the translation to its native
/// scan matrix and raises IRQ as appropriate. An unknown <see cref="KeyCode"/> (or
/// <see cref="KeyCode.None"/>) is ignored (no-op).
/// </summary>
public interface IKeyboardSink
{
    void PostKey(in KeyEvent e);
}
