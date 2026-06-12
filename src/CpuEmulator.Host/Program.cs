using CpuEmulator.Monitor;

namespace CpuEmulator.Host;

/// <summary>Console host: boots a Breadboard6502, wires the UART to the console, and
/// either runs the demo ROM on a bounded budget (--demo), or drops into the monitor
/// REPL on stdio (default; --load preloads a binary first).</summary>
internal static class Program
{
    /// <summary>The banner for REPL mode. The README transcript and the manual smoke quote it.</summary>
    private const string Banner =
        """
        CpuEmulator — Breadboard6502
        6502 · RAM $0000-$CFFF · UART $D000 (DATA/STATUS/CTRL) · timer $D100 · ROM $E000-$FFFF (demo)
        UART output prints inline; 'i TEXT' feeds UART input; 'g' runs (reset entry $E000); '?' help; 'q' quit.
        """;

    public static int Main(string[] args)
    {
        if (!HostOptions.TryParse(args, out HostOptions options, out string? error))
        {
            Console.Error.WriteLine($"? {error}");
            Console.Error.WriteLine(HostOptions.Usage);
            return 2;
        }
        var board = new Breadboard6502();
        board.Uart.OnTransmit = b => Console.Write((char)b); // raw passthrough
        board.Machine.Reset();                               // PC = $E000 via the ROM vector
        if (options.Demo)
        {
            board.Machine.Run(10_000); // hello completes at cycle 436; bounded, then exit
            return 0;
        }
        MonitorEngine engine = board.NewMonitor();
        Console.WriteLine(Banner);
        if (options.LoadPath is not null)
        {
            int count;
            try
            {
                count = engine.LoadFile(options.LoadAt, options.LoadPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"? {ex.Message}");
                return 2;
            }
            Console.WriteLine($"loaded ${count:X} bytes at ${options.LoadAt:X4}");
            if (options.Pc is uint pc)
                engine.ProgramCounter = pc;
        }
        if (options.Terminal)
        {
            // Raw-mode terminal: per-keystroke loop into the UART; Ctrl-] falls through
            // to the monitor prompt below. TreatControlCAsInput makes Ctrl+C a guest
            // byte (0x03) for the session; the prior value is restored in finally.
            // Under redirected stdin the console raw facilities throw IOException —
            // terminal mode is interactive-only by nature: clear error, exit 2.
            Console.WriteLine("(terminal — Ctrl-] exits to the monitor)");
            try
            {
                bool priorCtrlC = Console.TreatControlCAsInput;
                Console.TreatControlCAsInput = true;
                try
                {
                    new TerminalSession(board.Machine, board.Uart, new SystemTerminalConsole())
                        .Run();
                }
                finally
                {
                    Console.TreatControlCAsInput = priorCtrlC;
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"? --terminal needs an interactive console: {ex.Message}");
                return 2;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"? --terminal needs an interactive console: {ex.Message}");
                return 2;
            }
        }
        // Ctrl/EOF posture (REPL mode, deliberately not engineered): Ctrl+C terminates
        // the process — no CancelKeyPress handler; runaway-guest protection is the
        // bounded 'g' budget (default 1M cycles), which always returns to the prompt.
        // EOF (Ctrl+Z+Enter / Ctrl+D) ends the REPL like 'q' via the null-ReadLine path.
        new MonitorRepl(engine, Console.In, Console.Out,
                        prompt: true, inject: board.Uart.FeedInput).Run();
        return 0;
    }
}
