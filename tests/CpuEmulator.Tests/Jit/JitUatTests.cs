using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Host;
using CpuEmulator.Jit;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;
using CpuEmulator.Tests.Klaus;

namespace CpuEmulator.Tests.Jit;

/// <summary>
/// Task 7 Step 3: the 8 <c>Category=UAT</c> acceptance sessions re-run through a Tier-1
/// <see cref="JittedCpu"/> instead of the interpreter, asserting the SAME transcripts. The cheap
/// honest mechanism (the recorded devices-intake duplication choice): each session builds the
/// SAME <see cref="Machine"/> geometry as its interpreter twin, but the <c>WithCpu</c> factory
/// returns a <see cref="JittedCpu"/> wrapping the inner <see cref="Mos6502Cpu"/>. Because the
/// JITted CPU IS the machine's <c>Cpu</c>, the SAME <see cref="MonitorEngine"/> /
/// <see cref="MonitorRepl"/> / <see cref="Machine.Run"/> path drives it — the entire session runs
/// on the block machinery, exercising the fastmem split's MMIO classification (UART + timer pages),
/// the budget exit, the block-entry interrupt check, and dirty-page invalidation end-to-end over
/// each board's real bus map.
///
/// Every UAT asserts on transcripts (tx output, register dumps, target-reached messages) — these
/// are state + output, NOT bus traces, so fastmem ON is correct for all of them (Ground truth E).
/// </summary>
[Trait("Category", "UAT")]
public class JitUatTests
{
    // ── JIT-wrapped board fixtures (mirror Breadboard6502 / IrqBoard / MonitorUatTests) ─────────

    /// <summary>The breadboard6502 geometry (RAM $0000-$CFFF, UART $D000, Timer $D100, unmapped
    /// $D200-$DFFF, demo ROM $E000) but JIT-wrapped. Mirrors <see cref="Breadboard6502"/>, which
    /// lives in the AOT-clean Host assembly and therefore cannot itself reference the JIT.</summary>
    private static (Machine Machine, SimpleUart Uart, IntervalTimer Timer) NewJitBreadboard()
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var machine = Machine.Create("jit-breadboard6502")
            .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, Breadboard6502.UartBase, 0x0100, uart)
            .WithPeripheral(AddressSpaceKind.Program, Breadboard6502.TimerBase, 0x0100, timer)
            .WithRom(AddressSpaceKind.Program, 0xE000, DemoRom.Build())
            .WithCpu(ctx =>
            {
                var space = (AddressSpace)ctx.Space(AddressSpaceKind.Program);
                return new JittedCpu(new Mos6502Cpu(space), space);
            })
            .Build();
        return (machine, uart, timer);
    }

    /// <summary>The RAM-vector IRQ board geometry, JIT-wrapped. Mirrors the test-project
    /// <c>IrqBoard</c> (RAM $0000-$CFFF, UART $D000, Timer $D100, RAM $D200-$FFFF).</summary>
    private static (Machine Machine, SimpleUart Uart, IntervalTimer Timer) NewJitIrqBoard()
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var machine = Machine.Create("jit-irq-board")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x0100, uart)
            .WithPeripheral(AddressSpaceKind.Program, 0xD100, 0x0100, timer)
            .WithRam(AddressSpaceKind.Program, 0xD200, 0x10000 - 0xD200)
            .WithCpu(ctx =>
            {
                var space = (AddressSpace)ctx.Space(AddressSpaceKind.Program);
                return new JittedCpu(new Mos6502Cpu(space), space);
            })
            .Build();
        return (machine, uart, timer);
    }

    /// <summary>A bare full-RAM monitor machine, JIT-wrapped. Mirrors
    /// <c>MonitorUatTests.NewMachine</c> (16-bit, full RAM, S=$FD), driven through Machine.Run so
    /// the monitor g/s path ticks the scheduler identically to the interpreter twin.</summary>
    private static MonitorEngine NewJitMonitorMachine()
    {
        Mos6502Cpu inner = null!;
        var machine = Machine.Create("jit-monitor")
            .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx =>
            {
                var space = (AddressSpace)ctx.Space(AddressSpaceKind.Program);
                inner = new Mos6502Cpu(space);
                return new JittedCpu(inner, space);
            })
            .Build();
        inner.S = 0xFD;
        var jit = machine.Cpu;
        return new MonitorEngine(jit, machine.Space(AddressSpaceKind.Program),
                                 (IMonitorSupport)jit, machine.Run);
    }

    private static string RunSession(MonitorEngine engine, string session,
                                     Action<byte>? inject = null)
    {
        var output = new StringWriter();
        new MonitorRepl(engine, new StringReader(session), output, inject: inject).Run();
        return output.ToString();
    }

    // ── 1) MonitorUatTests.Countdown (JIT) ───────────────────────────────────────────────────
    [Fact]
    public void Countdown_session_assemble_run_inspect_disassemble_under_the_JIT()
    {
        var engine = NewJitMonitorMachine();
        string text = RunSession(engine, """
            a $0200 LDX #$05
            a STX $0300
            a DEX
            a BNE $0205
            a STX $0301
            a JMP $020B
            g $0200 until $020B 10000
            r
            m $0300 2
            d $0200 6
            q
            """);

        Assert.Contains("0206: D0 FD     BNE *-3", text);
        Assert.Contains("target $020B reached after 34 cycles", text);
        Assert.Contains("X=00", text);
        Assert.Contains("0300: 05 00", text);
        Assert.Contains("0200: A2 05     LDX #$05", text);
        Assert.Contains("020B: 4C 0B 02  JMP $020B", text);
        Assert.DoesNotContain("? ", text);
    }

    // ── 2) MonitorUatTests.Klaus-via-monitor (JIT) ───────────────────────────────────────────
    [KlausFact]
    public void Klaus_via_monitor_runs_a_bounded_slice_without_trapping_under_the_JIT()
    {
        var engine = NewJitMonitorMachine();
        string text = RunSession(engine, $"""
            l 0000 {KlausVectors.TryGetBinaryPath()!}
            g $0400 until $3469 1000000
            q
            """);

        Assert.Contains("loaded $10000 bytes at $0000", text);
        Assert.Contains("budget exhausted", text);
        Assert.DoesNotContain("trapped", text);
    }

    // ── 3) HostUatTests.Demo-rom-hello (JIT) ─────────────────────────────────────────────────
    [Fact]
    public void Demo_rom_hello_arrives_on_the_uart_exactly_under_the_JIT()
    {
        var (machine, uart, _) = NewJitBreadboard();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);
        machine.Reset();
        var engine = new MonitorEngine(machine.Cpu, machine.Space(AddressSpaceKind.Program),
                                       (IMonitorSupport)machine.Cpu, machine.Run);

        string text = RunSession(engine, """
            g 1000
            q
            """, inject: uart.FeedInput);

        Assert.Equal(DemoRom.Message, tx.ToString());
        Assert.Contains("budget exhausted", text);
        Assert.DoesNotContain("? ", text);
    }

    // ── 4) HostUatTests.Echo-session (JIT) ───────────────────────────────────────────────────
    [Fact]
    public void Echo_session_transmits_injected_input_back_exactly_under_the_JIT()
    {
        var (machine, uart, _) = NewJitBreadboard();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);
        machine.Reset();
        var engine = new MonitorEngine(machine.Cpu, machine.Space(AddressSpaceKind.Program),
                                       (IMonitorSupport)machine.Cpu, machine.Run);

        string text = RunSession(engine, """
            g 1000
            i HI
            g 200
            q
            """, inject: uart.FeedInput);

        Assert.Contains("injected $2 bytes", text);
        Assert.Equal(DemoRom.Message + "HI", tx.ToString());
        Assert.DoesNotContain("? ", text);
    }

    // ── 5) HostUatTests.Klaus-through-the-host-breadboard (JIT) ──────────────────────────────
    [KlausFact]
    public void Klaus_runs_through_the_host_breadboard_under_the_JIT()
    {
        var (machine, uart, _) = NewJitBreadboard();
        uart.OnTransmit = _ => { };
        machine.Reset();
        var engine = new MonitorEngine(machine.Cpu, machine.Space(AddressSpaceKind.Program),
                                       (IMonitorSupport)machine.Cpu, machine.Run);

        string text = RunSession(engine, $"""
            l 0000 {KlausVectors.TryGetBinaryPath()!}
            g $0400 until $3469 1000000
            q
            """, inject: uart.FeedInput);

        Assert.Contains("loaded $10000 bytes at $0000", text);
        Assert.Contains("budget exhausted", text);
        Assert.DoesNotContain("trapped", text);
    }

    // ── 6) DeviceIrqUatTests.Interrupt-driven echo (JIT) ─────────────────────────────────────
    [Fact]
    public void Interrupt_driven_echo_session_under_the_JIT()
    {
        var (machine, uart, _) = NewJitIrqBoard();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);
        machine.Reset();
        var engine = new MonitorEngine(machine.Cpu, machine.Space(AddressSpaceKind.Program),
                                       (IMonitorSupport)machine.Cpu, machine.Run);

        string text = RunSession(engine, """
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
            """, inject: uart.FeedInput);

        Assert.Equal("HI", tx.ToString());
        Assert.Contains("injected $2 bytes", text);
        Assert.DoesNotContain("? ", text);
    }

    // ── 7) DeviceIrqUatTests.Timer-irq-counting (JIT) ────────────────────────────────────────
    [Fact]
    public void Timer_irq_handler_counting_session_under_the_JIT()
    {
        var (machine, _, _) = NewJitIrqBoard();
        machine.Reset();
        var engine = new MonitorEngine(machine.Cpu, machine.Space(AddressSpaceKind.Program),
                                       (IMonitorSupport)machine.Cpu, machine.Run);

        string text = RunSession(engine, """
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
            """);

        Assert.Contains("target $0216 reached", text);
        Assert.Contains("0010: 05", text);
        Assert.DoesNotContain("? ", text);
    }

    // ── 8) TerminalSessionTests.Terminal-session (JIT) ───────────────────────────────────────
    [Fact]
    public void Terminal_session_echoes_typed_keys_and_exits_on_ctrl_rbracket_under_the_JIT()
    {
        var (machine, uart, _) = NewJitBreadboard();
        machine.Reset();
        var console = new ScriptedConsole();
        console.Type('A');
        console.Type('B');
        console.Pause();          // one empty poll: a Run slice executes before the exit key
        console.TypeControl(']'); // KeyChar 0x1D — the telnet escape

        var session = new TerminalSession(machine, uart, console,
                                          sliceCycles: 10_000, maxCycles: 1_000_000);
        TerminalExit exit = session.Run();

        Assert.Equal(TerminalExit.UserEscape, exit);
        Assert.Equal(DemoRom.Message + "AB", console.Output.ToString());
    }

    /// <summary>A scripted console for the terminal UAT (cheap honest duplication of the private
    /// double in TerminalSessionTests — null entries are one "no key" poll each).</summary>
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
                    _script.Dequeue();
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
}
