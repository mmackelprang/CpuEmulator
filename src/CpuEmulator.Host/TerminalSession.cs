using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Host;

/// <summary>How a terminal session ended.</summary>
public enum TerminalExit
{
    /// <summary>The user pressed Ctrl-] (the telnet escape) — back to the monitor prompt.</summary>
    UserEscape,

    /// <summary>The optional <c>maxCycles</c> test seam tripped (the real host runs unbounded).</summary>
    CycleLimit,
}

/// <summary>
/// Raw-mode terminal loop (Ground truth F): drain all available keys into the UART rx
/// queue (an IRQ-enabled UART asserts as bytes land), run a machine slice, repeat —
/// single-threaded and deterministic by design (no cross-thread IRQ writes). Exit on
/// Ctrl-] (KeyChar 0x1D, the telnet convention) or the optional cycle-limit test seam.
///
/// Key mapping (see <see cref="TryMapKey"/>): printable 0x20–0x7E pass through;
/// Enter maps to CR (0x0D) via ConsoleKey.Enter, NOT KeyChar — ReadKey reports '\r' on
/// Windows but '\n' on POSIX, and mapping by key keeps guest input platform-identical.
/// Backspace → 0x08, Tab → 0x09, Esc → 0x1B (a byte to the guest; the exit key is
/// Ctrl-], not Esc). Ctrl+A…Ctrl+Z arrive as KeyChar 0x01–0x1A in raw mode — including
/// Ctrl+C = 0x03 when the host sets TreatControlCAsInput. KeyChar 0 (arrows, F-keys)
/// is dropped silently.
///
/// UART tx routes to ITerminalConsole.Write((char)b); the prior OnTransmit sink is
/// restored on exit. Encoding caveat (recorded): the byte→char cast is Latin-1-identity;
/// the real console renders through its codepage — honest for printable ASCII + CR/LF,
/// documented for the rest. The injectable console keeps automated tests byte-exact.
/// </summary>
public sealed class TerminalSession
{
    private const char ExitKeyChar = '\x1d'; // Ctrl-] — the telnet escape

    private readonly Machine _machine;
    private readonly SimpleUart _uart;
    private readonly ITerminalConsole _console;
    private readonly long _sliceCycles;
    private readonly long _maxCycles;

    public TerminalSession(Machine machine, SimpleUart uart, ITerminalConsole console,
                           long sliceCycles = 10_000, long maxCycles = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(uart);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sliceCycles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCycles);
        _machine = machine;
        _uart = uart;
        _console = console;
        _sliceCycles = sliceCycles;
        _maxCycles = maxCycles;
    }

    /// <summary>True when the key is Ctrl-] (KeyChar 0x1D) — exit to the monitor prompt.</summary>
    public static bool IsExitKey(ConsoleKeyInfo key) => key.KeyChar == ExitKeyChar;

    /// <summary>Map one raw keystroke to its guest byte per Ground truth F. False for
    /// unmapped keys (KeyChar 0 — arrows, F-keys — and the Ctrl-] exit key).</summary>
    public static bool TryMapKey(ConsoleKeyInfo key, out byte value)
    {
        value = 0;
        if (IsExitKey(key))
            return false; // the escape hatch is not guest input
        switch (key.Key)
        {
            case ConsoleKey.Enter:     // '\r' on Windows, '\n' on POSIX — map by key,
                value = 0x0D;          // not KeyChar, so the guest sees CR everywhere
                return true;
            case ConsoleKey.Backspace:
                value = 0x08;
                return true;
        }
        char c = key.KeyChar;
        if (c == '\0')
            return false; // arrows, F-keys, dead keys: dropped silently
        if (c <= '\x7e') // printable ASCII 0x20–0x7E + control bytes 0x01–0x1A (Tab 0x09,
        {                // Esc 0x1B, Ctrl+C 0x03 with TreatControlCAsInput) pass through
            value = unchecked((byte)c);
            return true;
        }
        return false; // non-ASCII KeyChar: outside the UART's byte world, dropped
    }

    /// <summary>Run the terminal loop until Ctrl-] or the cycle-limit seam trips.</summary>
    public TerminalExit Run()
    {
        Action<byte>? priorSink = _uart.OnTransmit;
        _uart.OnTransmit = b => _console.Write((char)b); // Latin-1-identity cast (caveat above)
        try
        {
            long total = 0;
            while (true)
            {
                while (_console.KeyAvailable)
                {
                    ConsoleKeyInfo key = _console.ReadKey();
                    if (IsExitKey(key))
                        return TerminalExit.UserEscape;
                    if (TryMapKey(key, out byte b))
                        _uart.FeedInput(b);
                }
                total += _machine.Run(_sliceCycles);
                if (total >= _maxCycles)
                    return TerminalExit.CycleLimit;
            }
        }
        finally
        {
            _uart.OnTransmit = priorSink; // the monitor's sink comes back on exit
        }
    }
}
