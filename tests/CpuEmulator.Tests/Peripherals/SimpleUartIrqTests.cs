using System.Text;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

/// <summary>
/// Interrupt-driven echo end-to-end test over IrqBoard. Ground truth H (plan §Ground-truth-H):
/// rx-IRQ enabled by the guest, bytes injected while the CPU is stopped → interrupt-driven echo,
/// no polling anywhere.
///
/// Note: This test will be relocated to Host/DeviceIrqUatTests.cs with [Category=UAT] in Task 8.
/// </summary>
public class SimpleUartIrqTests
{
    [Fact]
    public void Interrupt_driven_echo_round_trips_injected_bytes()
    {
        // Ground truth H: interrupt-driven echo on an IrqBoard (RAM vectors, writable $FFFE).
        // The main loop holds no live registers — the handler may clobber A (contrast the
        // timer handler which must PHA/PLA).
        //
        // Determinism: setup runs in g $0200 100 (covers 8 setup cycles + ~30 spins);
        // 'i HI' injects while the CPU is stopped (IRQ asserts immediately);
        // g 200 covers two service sequences (21 cycles each) + spins.
        var board = IrqBoard.Create();
        var tx = new StringBuilder();
        board.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();

        var engine = board.NewMonitor();
        var output = new StringWriter();

        // Assemble Ground truth H verbatim — one 'a' line per listing row.
        // The REPL assembler advances a cursor from the first '$ADDR' form.
        new MonitorRepl(engine, new StringReader("""
            a $0200 LDA #$01
            a STA $D002
            a CLI
            a JMP $0206
            a $0300 LDA $D000
            a STA $D000
            a RTI
            m FFFE: 00 03
            g $0200 100
            i HI
            g 200
            q
            """), output, inject: board.Uart.FeedInput).Run();

        string text = output.ToString();

        // Exact equality — interrupt-driven, no polling reads anywhere
        Assert.Equal("HI", tx.ToString());
        Assert.Contains("injected $2 bytes", text);
        Assert.DoesNotContain("? ", text);
    }
}
