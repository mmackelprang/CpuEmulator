using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Peripherals;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests.Peripherals;

public class IntervalTimerTests
{
    /// <summary>Machine-backed fixture: FakeCpu (consumes exactly its budget) + timer at
    /// $D100. Registers are poked directly between Run slices for cycle-exact arrangements;
    /// the machine's BindTimeSource wiring makes the timer's "now" the FakeCpu cycle count.</summary>
    private static (Machine machine, IntervalTimer timer, FakeCpu cpu) MakeTimerMachine()
    {
        var timer = new IntervalTimer();
        var cpu = new FakeCpu();
        var machine = Machine.Create("test-timer")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, 0xD100, 0x0100, timer)
            .WithCpu(_ => cpu)
            .Build();
        return (machine, timer, cpu);
    }

    private static uint Status(IntervalTimer timer) => timer.Read(3, AccessWidth.Byte);

    // ── Exact-cycle fire ──────────────────────────────────────────────────────

    [Fact]
    public void Enable_fires_at_exactly_period_cycles()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 32); // PERIODL = 32
        timer.Write(0, AccessWidth.Byte, 0x01); // enable at committed cycle 0

        machine.Run(31);
        Assert.Equal(0x00u, Status(timer)); // 31 < 32: not yet

        machine.Run(1);
        Assert.Equal(0x01u, Status(timer)); // the chunked Run lands the event at exactly 32
    }

    [Fact]
    public void Enable_write_timestamp_matches_the_bus_cycle()
    {
        // CPU-programmed enable. Program at $0200: NOP NOP NOP (6) + LDA #$01 (2) +
        // STA $D100 (4) = 12 cycles; the STA's write transaction is its 4th and final
        // bus cycle. ORDERING PIN (the plan's ±1 question, answered): the generated core
        // increments _cycles BEFORE dispatching the bus write (Mos6502Cpu.WriteBus:
        // _cycles++ then _bus.Write8), so the timer's Write sees CycleCount == 12 exactly
        // — the fire schedules at write-cycle + PERIOD with no off-by-one.
        var timer = new IntervalTimer();
        var machine = Machine.Create("timestamp")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, 0xD100, 0x0100, timer)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();
        var space = machine.Space(AddressSpaceKind.Program);

        timer.Write(1, AccessWidth.Byte, 16); // PERIOD = 16 (set before the program runs)

        // $0200: NOP NOP NOP LDA #$01 STA $D100 JMP $0208 (park)
        byte[] program = [0xEA, 0xEA, 0xEA, 0xA9, 0x01, 0x8D, 0x00, 0xD1, 0x4C, 0x08, 0x02];
        for (int i = 0; i < program.Length; i++)
            space.Write8((uint)(0x0200 + i), program[i]);
        machine.Cpu.SetRegister("PC", 0x0200);

        machine.Run(12); // exactly through the STA's write cycle (no overshoot: 6+2+4)

        var scheduler = (CycleScheduler)machine.Scheduler;
        Assert.True(scheduler.TryPeekNextEventCycle(out long fireCycle));
        Assert.Equal(12 + 16, fireCycle); // write bus-cycle 12 + PERIOD 16 = 28

        machine.Run(20); // park loop spins past 28; the chunked Run fires the event
        Assert.Equal(0x01u, Status(timer));
    }

    // ── STATUS ────────────────────────────────────────────────────────────────

    [Fact]
    public void Fired_bit_reads_back()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 8);
        timer.Write(0, AccessWidth.Byte, 0x01);

        machine.Run(8);

        Assert.Equal(0x01u, Status(timer));
    }

    [Fact]
    public void Write_1_clears_fired()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 8);
        timer.Write(0, AccessWidth.Byte, 0x01);
        machine.Run(8);

        timer.Write(3, AccessWidth.Byte, 0x01);

        Assert.Equal(0x00u, Status(timer));
    }

    [Fact]
    public void Write_0_does_not_clear()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 8);
        timer.Write(0, AccessWidth.Byte, 0x01);
        machine.Run(8);

        timer.Write(3, AccessWidth.Byte, 0x00); // bit0 clear: ignored
        timer.Write(3, AccessWidth.Byte, 0xFE); // bit0 clear, others set: still ignored

        Assert.Equal(0x01u, Status(timer));
    }

    // ── IRQ level ─────────────────────────────────────────────────────────────

    [Fact]
    public void Fired_with_irq_enable_asserts_source()
    {
        var (machine, timer, cpu) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 8);
        timer.Write(0, AccessWidth.Byte, 0x03); // enable | irq-enable

        machine.Run(8);

        Assert.True(cpu.IrqAsserted);
    }

    [Fact]
    public void Clearing_fired_deasserts()
    {
        var (machine, timer, cpu) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 8);
        timer.Write(0, AccessWidth.Byte, 0x03);
        machine.Run(8);
        Assert.True(cpu.IrqAsserted);

        timer.Write(3, AccessWidth.Byte, 0x01); // write-1-clear

        Assert.False(cpu.IrqAsserted);
    }

    [Fact]
    public void Clearing_irq_enable_while_fired_deasserts()
    {
        var (machine, timer, cpu) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 8);
        timer.Write(0, AccessWidth.Byte, 0x03);
        machine.Run(8); // one-shot fired; enable self-cleared, CTRL now 0x02
        Assert.True(cpu.IrqAsserted);

        timer.Write(0, AccessWidth.Byte, 0x00); // clear irq-enable; fired still set

        Assert.False(cpu.IrqAsserted);
        Assert.Equal(0x01u, Status(timer)); // fired bit itself is untouched
    }

    [Fact]
    public void Fired_without_irq_enable_stays_low()
    {
        var (machine, timer, cpu) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 8);
        timer.Write(0, AccessWidth.Byte, 0x01); // enable only — no irq-enable

        machine.Run(8);

        Assert.Equal(0x01u, Status(timer));
        Assert.False(cpu.IrqAsserted);
    }

    // ── Enable/disable ────────────────────────────────────────────────────────

    [Fact]
    public void Disable_cancels_the_pending_fire()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 32);
        timer.Write(0, AccessWidth.Byte, 0x01);
        machine.Run(16); // halfway

        timer.Write(0, AccessWidth.Byte, 0x00); // disable: cancels the pending fire
        machine.Run(32); // run well past the would-be fire at 32

        Assert.Equal(0x00u, Status(timer)); // never fired
    }

    // ── Repeat ────────────────────────────────────────────────────────────────

    [Fact]
    public void Repeat_fires_at_every_period()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 32);
        timer.Write(0, AccessWidth.Byte, 0x05); // enable | repeat

        machine.Run(32);
        Assert.Equal(0x01u, Status(timer)); // fire 1 at 32
        timer.Write(3, AccessWidth.Byte, 0x01); // write-1-clear between fires

        machine.Run(31);
        Assert.Equal(0x00u, Status(timer)); // 63 < 64: not yet
        machine.Run(1);
        Assert.Equal(0x01u, Status(timer)); // fire 2 at exactly 64
    }

    [Fact]
    public void One_shot_clears_its_own_enable_and_does_not_refire()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 32);
        timer.Write(0, AccessWidth.Byte, 0x01); // one-shot

        machine.Run(32);
        Assert.Equal(0x01u, Status(timer));
        Assert.Equal(0x00u, timer.Read(0, AccessWidth.Byte)); // enable bit self-cleared

        timer.Write(3, AccessWidth.Byte, 0x01); // clear fired
        machine.Run(64); // run two more periods

        Assert.Equal(0x00u, Status(timer)); // no refire
    }

    [Fact]
    public void Clearing_repeat_midflight_makes_the_next_fire_the_last()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 32);
        timer.Write(0, AccessWidth.Byte, 0x05); // enable | repeat
        machine.Run(10);

        timer.Write(0, AccessWidth.Byte, 0x01); // clear repeat, keep enable — no retime

        machine.Run(22); // reach 32
        Assert.Equal(0x01u, Status(timer)); // the next fire happened (the last)
        Assert.Equal(0x00u, timer.Read(0, AccessWidth.Byte)); // fire path cleared enable

        timer.Write(3, AccessWidth.Byte, 0x01); // clear fired
        machine.Run(64);
        Assert.Equal(0x00u, Status(timer)); // chain canceled — no further fires
    }

    // ── Period ────────────────────────────────────────────────────────────────

    [Fact]
    public void Period_zero_means_65536()
    {
        var (machine, timer, _) = MakeTimerMachine();
        // PERIOD left at its 0 default — the wrap convention
        timer.Write(0, AccessWidth.Byte, 0x01);

        machine.Run(65535);
        Assert.Equal(0x00u, Status(timer));
        machine.Run(1);
        Assert.Equal(0x01u, Status(timer)); // fires at exactly 65536
    }

    [Fact]
    public void Period_bytes_read_back()
    {
        var (_, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 0x34);
        timer.Write(2, AccessWidth.Byte, 0x12);

        Assert.Equal(0x34u, timer.Read(1, AccessWidth.Byte));
        Assert.Equal(0x12u, timer.Read(2, AccessWidth.Byte));
    }

    [Fact]
    public void Period_write_while_enabled_does_not_retime()
    {
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 32);
        timer.Write(0, AccessWidth.Byte, 0x01); // pending fire at 32
        machine.Run(8);

        timer.Write(1, AccessWidth.Byte, 0x05); // PERIOD = 5 — must NOT retime to 8+5=13

        machine.Run(5); // now at 13
        Assert.Equal(0x00u, Status(timer)); // a retime would have fired at 13
        machine.Run(19); // now at 32
        Assert.Equal(0x01u, Status(timer)); // the original schedule held
    }

    // ── Mirrors ───────────────────────────────────────────────────────────────

    [Fact]
    public void Registers_mirror_through_the_page()
    {
        var (_, timer, _) = MakeTimerMachine();
        timer.Write(5, AccessWidth.Byte, 0x42); // offset 5 & 0x03 == 1 == PERIODL
        timer.Write(6, AccessWidth.Byte, 0x01); // offset 6 & 0x03 == 2 == PERIODH

        Assert.Equal(0x42u, timer.Read(1, AccessWidth.Byte));
        Assert.Equal(0x42u, timer.Read(0xFD, AccessWidth.Byte)); // 0xFD & 3 == 1
        Assert.Equal(0x01u, timer.Read(2, AccessWidth.Byte));
        Assert.Equal(timer.Read(0, AccessWidth.Byte), timer.Read(4, AccessWidth.Byte));
        Assert.Equal(timer.Read(3, AccessWidth.Byte), timer.Read(7, AccessWidth.Byte));
    }

    // ── TryPeek identity ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void TryPeek_is_the_identity_for_all_four_registers(uint offset)
    {
        // The write-1-clear payoff: every read is side-effect-free, so peek == read —
        // and peeking STATUS does not clear it.
        var (machine, timer, _) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 0x34);
        timer.Write(2, AccessWidth.Byte, 0x12);
        timer.Write(0, AccessWidth.Byte, 0x03); // enable | irq-enable
        machine.Run(0x1234); // fire — STATUS bit set; one-shot clears enable (CTRL=0x02)

        uint read = timer.Read(offset, AccessWidth.Byte);
        Assert.True(timer.TryPeek(offset, out byte peek1));
        Assert.True(timer.TryPeek(offset, out byte peek2));

        Assert.Equal((byte)read, peek1);
        Assert.Equal(peek1, peek2);                              // peeking changes nothing
        Assert.Equal(read, timer.Read(offset, AccessWidth.Byte)); // reads stay stable too
    }

    // ── Realize / composition ─────────────────────────────────────────────────

    [Fact]
    public void Realize_claims_scheduler_and_irq_source()
    {
        // Machine composition: Realize claims Scheduler + IrqLine.Source() — an enabled
        // timer schedules and its fire reaches the CPU's IRQ input.
        var (machine, timer, cpu) = MakeTimerMachine();
        timer.Write(1, AccessWidth.Byte, 4);
        timer.Write(0, AccessWidth.Byte, 0x03);

        machine.Run(4);

        Assert.True(cpu.IrqAsserted);
    }

    [Fact]
    public void Enable_before_realize_throws()
    {
        // Host-world composition error: a machine-composed timer is always realized.
        var timer = new IntervalTimer();
        timer.Write(1, AccessWidth.Byte, 32);

        Assert.Throws<MachineConfigurationException>(
            () => timer.Write(0, AccessWidth.Byte, 0x01));
    }

    // The Ground truth I end-to-end (Timer_irq_handler_counting_session) lives in
    // Host/DeviceIrqUatTests.cs with [Category=UAT] — relocated there per the plan
    // (exactly one REPL-driven copy).
}
