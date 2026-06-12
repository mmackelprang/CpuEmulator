namespace CpuEmulator.Host;

/// <summary>
/// The real-console adapter for terminal mode: trivially thin over System.Console —
/// manual-smoke only, by design (the one untested seam; everything behavioral lives in
/// TerminalSession against the injectable ITerminalConsole). ReadKey intercepts so
/// keystrokes are not locally echoed — the guest's echo is the only echo.
/// </summary>
public sealed class SystemTerminalConsole : ITerminalConsole
{
    public bool KeyAvailable => Console.KeyAvailable;
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);
    public void Write(char c) => Console.Write(c);
}
