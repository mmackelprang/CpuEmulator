using System.Text;
using CpuEmulator.Host;

namespace CpuEmulator.Tests.Host;

/// <summary>
/// Terminal-mode tests over the injectable console: key mapping (Ground truth F),
/// the deterministic session loop, and the terminal UAT (the brief-required literal).
/// </summary>
public class TerminalSessionTests
{
    /// <summary>Scripted console: a queue of keys where null entries are one
    /// "no key available" poll each — letting a Run slice pass between keystrokes
    /// deterministically (KeyAvailable consumes the null and reports false for that
    /// poll). ReadKey on an exhausted script throws: a misconfigured script fails
    /// loudly, never spins.</summary>
    private sealed class ScriptedConsole : ITerminalConsole
    {
        private readonly Queue<ConsoleKeyInfo?> _script = new();
        public StringBuilder Output { get; } = new();

        public void Type(char c) =>
            _script.Enqueue(new ConsoleKeyInfo(c, ConsoleKey.Oem1, false, false, false));

        public void TypeControl(char c) =>
            _script.Enqueue(new ConsoleKeyInfo((char)(c == ']' ? 0x1D : char.ToUpperInvariant(c) - 'A' + 1),
                ConsoleKey.Oem1, false, false, false));

        public void Pause() => _script.Enqueue(null);

        public bool KeyAvailable
        {
            get
            {
                if (_script.Count == 0)
                    return false;
                if (_script.Peek() is null)
                {
                    _script.Dequeue(); // consume the pause: this poll reports "no key"
                    return false;
                }
                return true;
            }
        }

        public ConsoleKeyInfo ReadKey()
        {
            if (_script.Count == 0 || _script.Peek() is not ConsoleKeyInfo key)
                throw new InvalidOperationException("ScriptedConsole script exhausted or paused.");
            _script.Dequeue();
            return key;
        }

        public void Write(char c) => Output.Append(c);
    }

    // ── Key mapping (Ground truth F) ──────────────────────────────────────────

    [Theory]
    [InlineData('A', 0x41)]                 // printable
    [InlineData('z', 0x7A)]                 // printable
    [InlineData(' ', 0x20)]                 // printable edge
    [InlineData('~', 0x7E)]                 // printable edge
    [InlineData('\t', 0x09)]                // Tab
    [InlineData('\x1b', 0x1B)]              // Esc passes through as a byte
    [InlineData('\x03', 0x03)]              // Ctrl+C = guest byte 0x03 in raw mode
    [InlineData('\x01', 0x01)]              // Ctrl+A
    [InlineData('\x1a', 0x1A)]              // Ctrl+Z
    public void Printable_and_control_keychars_map_to_their_byte(char keyChar, byte expected)
    {
        var key = new ConsoleKeyInfo(keyChar, ConsoleKey.Oem1, false, false, false);

        Assert.True(TerminalSession.TryMapKey(key, out byte value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData('\r')] // Windows ReadKey reports '\r'
    [InlineData('\n')] // POSIX ReadKey reports '\n'
    public void Enter_maps_to_CR_regardless_of_platform_keychar(char keyChar)
    {
        var key = new ConsoleKeyInfo(keyChar, ConsoleKey.Enter, false, false, false);

        Assert.True(TerminalSession.TryMapKey(key, out byte value));
        Assert.Equal(0x0D, value); // mapped via ConsoleKey.Enter, NOT KeyChar
    }

    [Fact]
    public void Backspace_maps_to_0x08()
    {
        var key = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false);

        Assert.True(TerminalSession.TryMapKey(key, out byte value));
        Assert.Equal(0x08, value);
    }

    [Fact]
    public void Zero_keychar_keys_are_dropped_silently()
    {
        var arrow = new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false);

        Assert.False(TerminalSession.TryMapKey(arrow, out _));
    }

    [Fact]
    public void Ctrl_rbracket_is_the_exit_key_and_not_guest_input()
    {
        var key = new ConsoleKeyInfo('\x1d', ConsoleKey.Oem6, false, false, true);

        Assert.True(TerminalSession.IsExitKey(key));
        Assert.False(TerminalSession.TryMapKey(key, out _)); // unmapped as input
    }

    // ── Session loop ──────────────────────────────────────────────────────────

    [Fact]
    public void Session_returns_cycle_limit_when_the_seam_trips()
    {
        var board = new Breadboard6502();
        board.Machine.Reset();
        var console = new ScriptedConsole();
        console.Pause(); // one empty poll — a Run slice executes, then the seam trips

        var session = new TerminalSession(board.Machine, board.Uart, console,
                                          sliceCycles: 1_000, maxCycles: 1_000);
        TerminalExit exit = session.Run();

        Assert.Equal(TerminalExit.CycleLimit, exit);
    }

    [Fact]
    public void Session_restores_the_previous_transmit_sink_on_exit()
    {
        var board = new Breadboard6502();
        board.Machine.Reset();
        var prior = new StringBuilder();
        Action<byte> priorSink = b => prior.Append((char)b);
        board.Uart.OnTransmit = priorSink;

        var console = new ScriptedConsole();
        console.TypeControl(']'); // immediate exit
        var session = new TerminalSession(board.Machine, board.Uart, console);
        session.Run();

        Assert.Same(priorSink, board.Uart.OnTransmit); // the monitor's sink came back
    }

    [Fact]
    [Trait("Category", "UAT")]
    public void Terminal_session_echoes_typed_keys_and_exits_on_ctrl_rbracket()
    {
        // The user's --terminal flow, headless: boot the breadboard, let the demo ROM
        // print its hello, type "AB" at the echo loop, leave with Ctrl-]. The injectable
        // console keeps this byte-exact (the encoding caveat never enters); the real
        // console is covered by the captured manual-smoke transcript.
        var board = new Breadboard6502();
        board.Machine.Reset();
        var console = new ScriptedConsole();
        console.Type('A');
        console.Type('B');
        console.Pause(); // one empty poll: a Run slice executes before the exit key
        console.TypeControl(']'); // KeyChar 0x1D — the telnet escape

        var session = new TerminalSession(board.Machine, board.Uart, console,
                                          sliceCycles: 10_000, maxCycles: 1_000_000);
        TerminalExit exit = session.Run();

        Assert.Equal(TerminalExit.UserEscape, exit);
        Assert.Equal(DemoRom.Message + "AB", console.Output.ToString());
    }
}
