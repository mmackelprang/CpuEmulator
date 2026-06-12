using System.Text;
using CpuEmulator.Monitor;
using CpuEmulator.Tests.Peripherals;

namespace CpuEmulator.Tests.Host;

/// <summary>
/// Device-layer UAT sessions (spec §8): interrupt-driven flows exercised end-to-end
/// through the REPL the user types into, over the RAM-vector IrqBoard (the Breadboard's
/// ROM owns the vectors, so handler experiments need writable $FFFE — see
/// docs/user-guide/breadboard6502.md "vector honesty"). These are the Task 4/5
/// end-to-ends, relocated and tagged per the devices-intake plan (exactly one
/// REPL-driven copy each).
/// </summary>
[Trait("Category", "UAT")]
public class DeviceIrqUatTests
{
    [Fact]
    public void Interrupt_driven_echo_session()
    {
        // Ground truth H: interrupt-driven echo on an IrqBoard (RAM vectors, writable
        // $FFFE). The main loop holds no live registers — the handler may clobber A
        // (contrast the timer handler, which must PHA/PLA).
        //
        // Walk: 'i' asserts the level while the CPU is stopped → the next 'g' services
        // at its first instruction boundary → the handler drains one byte per service
        // (the level drops when the queue empties) → the spin resumes. Budgets from the
        // plan's cycle ledger: g $0200 100 covers setup (8) + ~30 spins; g 200 covers
        // two service+echo sequences (21 each) + spins.
        var board = IrqBoard.Create();
        var tx = new StringBuilder();
        board.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();

        var engine = board.NewMonitor();
        var output = new StringWriter();

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

    [Fact]
    public void Timer_irq_handler_counting_session()
    {
        // Ground truth I on IrqBoard (timer at $D100): period $40 = 64 cycles, repeat,
        // handler increments $10 per fire and write-1-clears STATUS; the main loop polls
        // the counter and parks at $0216 when it reaches 5. Asserts TargetReached + the
        // counter dump — both exact and independent of the enable-write timing (the loop
        // exits *because* the counter hit 5, whenever that was).
        var board = IrqBoard.Create();
        board.Machine.Reset();

        var engine = board.NewMonitor();
        var output = new StringWriter();

        new MonitorRepl(engine, new StringReader("""
            m 0010: 00
            a $0200 LDA #$40
            a STA $D101
            a LDA #$00
            a STA $D102
            a LDA #$07
            a STA $D100
            a CLI
            a LDA $10
            a CMP #$05
            a BNE $0210
            a JMP $0216
            a $0300 PHA
            a INC $10
            a LDA #$01
            a STA $D103
            a PLA
            a RTI
            m FFFE: 00 03
            g $0200 until $0216 2000
            m 10 1
            q
            """), output).Run();

        string text = output.ToString();

        Assert.Contains("target $0216 reached", text);
        Assert.Contains("0010: 05", text);
        Assert.DoesNotContain("? ", text);
    }
}
