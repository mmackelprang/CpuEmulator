using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;

namespace CpuEmulator.Tests.Monitor;

/// <summary>
/// Tests for MonitorEngine's run-delegate seam (Task 3).
/// Verifies that g/s route through the injected delegate and that scheduler
/// events fire when Machine.Run is the delegate.
/// </summary>
public class MonitorRunDelegateTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (Mos6502Cpu Cpu, IAddressSpace Space) NewCpu()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0x0200);
        cpu.S = 0xFD;
        return (cpu, space);
    }

    // ── Run routes through delegate ───────────────────────────────────────────

    [Fact]
    public void Run_routes_through_the_delegate()
    {
        var (cpu, space) = NewCpu();
        // Fill NOPs so Run doesn't fault
        for (uint i = 0; i < 0x100; i++) space.Write8(0x0200 + i, 0xEA);

        int callCount = 0;
        long recordedBudget = -1;
        long RunDelegate(long budget)
        {
            callCount++;
            recordedBudget = budget;
            long b = budget;
            cpu.Run(ref b);
            return budget - b;
        }

        long cyclesBefore = cpu.CycleCount;
        var engine = new MonitorEngine(cpu, space, cpu, RunDelegate);
        long consumed = engine.Run(10);

        Assert.Equal(1, callCount);
        Assert.Equal(10, recordedBudget);
        Assert.Equal(cpu.CycleCount - cyclesBefore, consumed);
    }

    // ── Step routes through delegate at budget one ────────────────────────────

    [Fact]
    public void Step_routes_through_the_delegate_at_budget_one()
    {
        var (cpu, space) = NewCpu();
        space.Write8(0x0200, 0xEA); // NOP

        var budgets = new List<long>();
        long RunDelegate(long budget)
        {
            budgets.Add(budget);
            long b = budget;
            cpu.Run(ref b);
            return budget - b;
        }

        var engine = new MonitorEngine(cpu, space, cpu, RunDelegate);
        MonitorStepReport report = engine.Step();

        Assert.Equal([1L], budgets);
        Assert.Equal(2L, report.Cycles);
        Assert.Equal("NOP", report.Disassembly);
    }

    // ── RunUntil routes every instruction at budget one ───────────────────────

    [Fact]
    public void RunUntil_routes_every_instruction_at_budget_one()
    {
        var (cpu, space) = NewCpu();
        // 3 NOPs: $0200, $0201, $0202 — target is $0202
        space.Write8(0x0200, 0xEA);
        space.Write8(0x0201, 0xEA);
        space.Write8(0x0202, 0xEA);

        var budgets = new List<long>();
        long RunDelegate(long budget)
        {
            budgets.Add(budget);
            long b = budget;
            cpu.Run(ref b);
            return budget - b;
        }

        var engine = new MonitorEngine(cpu, space, cpu, RunDelegate);
        RunReport report = engine.RunUntil(0x0202, 100);

        // Should have called delegate twice: once for NOP@0200, once for NOP@0201
        Assert.Equal([1L, 1L], budgets);
        Assert.Equal(RunStopReason.TargetReached, report.Reason);
        Assert.Equal(4L, report.CyclesRun); // 2 NOPs × 2 cycles each
    }

    // ── RunUntil trap detection survives the delegate ─────────────────────────

    [Fact]
    public void RunUntil_trap_detection_survives_the_delegate()
    {
        var (cpu, space) = NewCpu();
        // JMP $0200: $4C $00 $02
        space.Write8(0x0200, 0x4C);
        space.Write8(0x0201, 0x00);
        space.Write8(0x0202, 0x02);

        long RunDelegate(long budget)
        {
            long b = budget;
            cpu.Run(ref b);
            return budget - b;
        }

        var engine = new MonitorEngine(cpu, space, cpu, RunDelegate);
        RunReport report = engine.RunUntil(0xFFFF, 1000);

        Assert.Equal(RunStopReason.Trapped, report.Reason);
        Assert.Equal(0x0200u, report.Pc);
    }

    // ── Null delegate is byte-identical ──────────────────────────────────────

    [Fact]
    public void Null_delegate_is_byte_identical()
    {
        // Engine with no delegate
        var (cpu1, space1) = NewCpu();
        space1.Write8(0x0200, 0xEA);
        var engineNo = new MonitorEngine(cpu1, space1, cpu1);
        MonitorStepReport reportNo = engineNo.Step();

        // Engine with null delegate explicitly
        var (cpu2, space2) = NewCpu();
        space2.Write8(0x0200, 0xEA);
        var engineNull = new MonitorEngine(cpu2, space2, cpu2, null);
        MonitorStepReport reportNull = engineNull.Step();

        Assert.Equal(reportNo.Disassembly, reportNull.Disassembly);
        Assert.Equal(reportNo.Cycles, reportNull.Cycles);
        Assert.Equal(reportNo.PcBefore, reportNull.PcBefore);
    }

    // ── Scheduled event fires under g over Machine.Run ────────────────────────

    [Fact]
    public void Scheduled_event_fires_under_g_over_Machine_Run()
    {
        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();

        var space = machine.Space(AddressSpaceKind.Program);
        // NOPs from $0200, then JMP-self at $020A
        for (uint i = 0; i < 10; i++) space.Write8(0x0200 + i, 0xEA);
        space.Write8(0x020A, 0x4C); // JMP $020A
        space.Write8(0x020B, 0x0A);
        space.Write8(0x020C, 0x02);

        var cpu = (Mos6502Cpu)machine.Cpu;
        cpu.SetRegister("PC", 0x0200);
        cpu.S = 0xFD;

        bool fired = false;
        machine.Scheduler.ScheduleAt(5, () => { fired = true; });

        var engine = new MonitorEngine(cpu, machine.Space(AddressSpaceKind.Program), cpu, machine.Run);
        engine.Run(20);

        Assert.True(fired);
    }

    // ── Step advances the scheduler over Machine.Run ──────────────────────────

    [Fact]
    public void Step_advances_the_scheduler_over_Machine_Run()
    {
        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();

        var space = machine.Space(AddressSpaceKind.Program);
        space.Write8(0x0200, 0xEA); // NOP (2 cycles)
        space.Write8(0x0201, 0xEA); // NOP (2 cycles)

        var cpu = (Mos6502Cpu)machine.Cpu;
        cpu.SetRegister("PC", 0x0200);
        cpu.S = 0xFD;

        bool fired = false;
        // Schedule at cycle 3 — fires after the second Step (cycles 0→2, then 2→4 crossing 3)
        machine.Scheduler.ScheduleAt(3, () => { fired = true; });

        var engine = new MonitorEngine(cpu, machine.Space(AddressSpaceKind.Program), cpu, machine.Run);

        engine.Step(); // cycles 0→2, scheduler advances to 2 — not yet
        Assert.False(fired);

        engine.Step(); // cycles 2→4, scheduler advances to 4 — fires at 3
        Assert.True(fired);
    }
}
