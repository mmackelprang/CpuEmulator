using CpuEmulator.Machines;
using CpuEmulator.Monitor;

namespace CpuEmulator.Host;

/// <summary>Console host: boots ANY registered board (default 6502) from a BoardSpec via
/// BoardMachineFactory, wires the board's UART to the console, and either runs the boot
/// program on a bounded budget (--demo), or drops into the CPU-agnostic monitor REPL on
/// stdio (default; --load preloads a binary first). '--board list' prints the catalog.</summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (!HostOptions.TryParse(args, out HostOptions options, out string? error))
        {
            Console.Error.WriteLine($"? {error}");
            Console.Error.WriteLine(HostOptions.Usage);
            return 2;
        }

        if (options.ListBoards)
        {
            Console.WriteLine("available boards:");
            foreach (string name in BoardRegistry.AvailableBoards)
                Console.WriteLine($"  {name}");
            return 0;
        }

        if (!BoardRegistry.TryBoot(options.Board, ExecutionTier.Interpreter,
                                   out BootedBoard? booted, out string? bootError))
        {
            Console.Error.WriteLine($"? {bootError}");
            Console.Error.WriteLine(HostOptions.Usage);
            return 2;
        }
        BootedBoard board = booted!;

        board.Uart.OnTransmit = b => Console.Write((char)b); // raw passthrough
        board.Machine.Reset();                               // CPU lands at its reset entry

        if (options.Demo)
        {
            board.Machine.Run(10_000); // bounded; the boot program completes, then exit
            return 0;
        }

        MonitorEngine engine = board.NewMonitor();
        Console.WriteLine(board.Banner);
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
            Console.WriteLine(
                $"loaded ${count:X} bytes at ${options.LoadAt.ToString("X" + engine.AddressDigits)}");
            if (options.Pc is uint pc)
                engine.ProgramCounter = pc;
        }

        if (options.Terminal)
        {
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

        new MonitorRepl(engine, Console.In, Console.Out,
                        prompt: true, inject: board.Uart.FeedInput).Run();
        return 0;
    }
}
