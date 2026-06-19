using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Host;
using CpuEmulator.Machines;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

/// <summary>The un-fakeable zero-behavior-change gate (spec section 8): the Breadboard6502-via-
/// BoardSpec must reproduce, byte for byte and cycle for cycle, the hand-wired Breadboard6502 over
/// the EXACT host sessions HostUatTests exercises. Both machines run the same monitor script from
/// the same reset; the test asserts the two transmit streams and the two cycle counts are equal to
/// EACH OTHER (not to a constant), so a behavioral drift cannot be hidden by editing an expectation.</summary>
[Trait("Category", "UAT")]
public class Breadboard6502GateTests
{
    private sealed record Rig(Machine Machine, SimpleUart Uart, MonitorEngine Engine, StringBuilder Tx);

    private static Rig HandWired()
    {
        var board = new Breadboard6502();
        var tx = new StringBuilder();
        board.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();
        return new Rig(board.Machine, board.Uart, board.NewMonitor(), tx);
    }

    private static Rig BoardSpecRig()
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);
        Machine machine = BoardMachineFactory.Build(Breadboard6502Board.Spec(DemoRom.Build(), uart, timer));
        machine.Reset();
        var engine = new MonitorEngine(
            (CpuEmulator.Cpus.Mos6502.Mos6502Cpu)machine.Cpu,
            machine.Space(AddressSpaceKind.Program),
            (CpuEmulator.Cpus.Mos6502.Mos6502Cpu)machine.Cpu,
            machine.Run);
        return new Rig(machine, uart, engine, tx);
    }

    private static string RunSession(Rig rig, string session)
    {
        var output = new StringWriter();
        new MonitorRepl(rig.Engine, new StringReader(session), output, inject: rig.Uart.FeedInput).Run();
        return output.ToString();
    }

    [Fact]
    public void Hello_stream_and_cycles_match_the_hand_wired_board()
    {
        Rig hand = HandWired();
        Rig spec = BoardSpecRig();

        const string session = """
            g 1000
            q
            """;
        RunSession(hand, session);
        RunSession(spec, session);

        Assert.Equal(hand.Tx.ToString(), spec.Tx.ToString());          // byte-identical UART stream
        Assert.Equal(DemoRom.Message, spec.Tx.ToString());             // and it IS the hello message
        Assert.Equal(hand.Machine.Cpu.CycleCount, spec.Machine.Cpu.CycleCount); // cycle-identical
    }

    [Fact]
    public void Echo_stream_and_cycles_match_the_hand_wired_board()
    {
        Rig hand = HandWired();
        Rig spec = BoardSpecRig();

        const string session = """
            g 1000
            i HI
            g 200
            q
            """;
        RunSession(hand, session);
        RunSession(spec, session);

        Assert.Equal(hand.Tx.ToString(), spec.Tx.ToString());
        Assert.Equal(DemoRom.Message + "HI", spec.Tx.ToString());
        Assert.Equal(hand.Machine.Cpu.CycleCount, spec.Machine.Cpu.CycleCount);
    }
}
