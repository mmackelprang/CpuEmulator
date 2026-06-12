namespace CpuEmulator.Host;

/// <summary>
/// The console seam for terminal mode: key polling, raw key reads, and character output.
/// Injectable so the terminal session is deterministic and byte-exact under test
/// (FakeTerminalConsole); the real console adapter is <see cref="SystemTerminalConsole"/>.
/// </summary>
public interface ITerminalConsole
{
    /// <summary>True when a key is available to read without blocking.</summary>
    bool KeyAvailable { get; }

    /// <summary>Read one key (raw — the host must not echo it).</summary>
    ConsoleKeyInfo ReadKey();

    /// <summary>Write one output character (UART tx passthrough).</summary>
    void Write(char c);
}
