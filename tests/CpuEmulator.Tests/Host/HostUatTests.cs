using System.Text;
using CpuEmulator.Host;
using CpuEmulator.Machines;
using CpuEmulator.Monitor;
using CpuEmulator.Tests.Klaus;

namespace CpuEmulator.Tests.Host;

/// <summary>End-to-end UAT over the REAL host composition (spec §8): the 6502 board
/// the user boots through the registry, the REPL the user types into, the run-delegate
/// wiring the host ships — headless, with the UART's sinks captured.</summary>
[Trait("Category", "UAT")]
public class HostUatTests
{
    private static (BootedBoard Board, MonitorEngine Engine, StringBuilder Tx) NewBoard()
    {
        Assert.True(BoardRegistry.TryBoot("6502", ExecutionTier.Interpreter,
            out BootedBoard? booted, out string? error), error);
        BootedBoard board = booted!;
        var tx = new StringBuilder();
        board.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset(); // PC = $E000 via the ROM reset vector — the host's boot path
        return (board, board.NewMonitor(), tx);
    }

    private static string RunSession(BootedBoard board, MonitorEngine engine, string session)
    {
        var output = new StringWriter();
        new MonitorRepl(engine, new StringReader(session), output,
                        inject: board.Uart.FeedInput).Run();
        return output.ToString();
    }

    [Fact]
    public void Demo_rom_hello_arrives_on_the_uart_exactly()
    {
        // From the reset entry ($E000), the print loop finishes the 28-byte hello at
        // cycle 436 and parks in the polled echo loop — so g 1000 prints the whole
        // message then exhausts its budget (the echo loop never traps). Exact equality:
        // one missing or extra transmitted byte fails.
        var (board, engine, tx) = NewBoard();
        string text = RunSession(board, engine, """
            g 1000
            q
            """);

        Assert.Equal(DemoRom.Message, tx.ToString());
        Assert.Contains("budget exhausted", text);
        Assert.DoesNotContain("? ", text);
    }

    [Fact]
    public void Echo_session_transmits_injected_input_back_exactly()
    {
        // Past the hello (g 1000), inject "HI" while the CPU is stopped (the polled UART
        // holds the queue), then run a slice: two echoed bytes cost 19 cycles each — 200
        // is ample. Exact equality: hello + "HI", no CR, no LF, nothing else.
        var (board, engine, tx) = NewBoard();
        string text = RunSession(board, engine, """
            g 1000
            i HI
            g 200
            q
            """);

        Assert.Contains("injected $2 bytes", text);
        Assert.Equal(DemoRom.Message + "HI", tx.ToString());
        Assert.DoesNotContain("? ", text);
    }

    /// <summary>
    /// Klaus-through-the-host smoke: the same bounded slice as the monitor-level Klaus UAT,
    /// but over the breadboard's REAL bus map and the Machine.Run delegate (one instruction
    /// per slice — ~350K calls; the empty scheduler queue makes AdvanceTo trivial).
    /// Load geometry: 'l 0000' streams all 64 KiB over the breadboard, so ROM-region bytes
    /// ($E000+) are silently dropped (read-only mapping), the unmapped hole $D100–$DFFF
    /// drops its bytes (open-bus writes ignored), and the UART page tees its 64 DATA-mirror
    /// bytes ($D000 page, every 4th offset) to the tx sink during the load — harmless
    /// garbage on the tx sink, which is why this test never asserts on tx. The 1M-cycle
    /// slice executes entirely in low RAM (execution stays below $4000), so none of the
    /// dropped bytes matter. A passing full run needs ~96M cycles to reach the success trap
    /// ($3469), so the slice legitimately exhausts its budget without trapping — that IS
    /// the assertion.
    /// </summary>
    [KlausFact]
    public void Klaus_loads_and_runs_through_the_host_breadboard()
    {
        var (board, engine, _) = NewBoard();
        string text = RunSession(board, engine, $"""
            l 0000 {KlausVectors.TryGetBinaryPath()!}
            g $0400 until $3469 1000000
            q
            """);

        Assert.Contains("loaded $10000 bytes at $0000", text);
        Assert.Contains("budget exhausted", text);
        Assert.DoesNotContain("trapped", text);
    }
}
