using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class MachineRunTests
{
    private static Machine MachineWith(ICpuCore cpu) =>
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => cpu)
            .Build();

    [Fact]
    public void Run_passes_the_full_budget_when_no_events_pend()
    {
        // Authorized change #1 (2026-06-12-devices-intake plan): the empty-queue path is
        // one full-budget slice, byte-identical to pre-PR-#11 — the old name pinned a
        // contract that the chunked Run falsifies the moment an event pends.
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(100);

        Assert.Equal([100L], cpu.RunBudgets);
        Assert.Equal(100, cpu.CycleCount);
    }

    [Fact]
    public void Run_chunks_the_slice_at_the_next_pending_event()
    {
        // Authorized change #1 (2026-06-12-devices-intake plan): Machine.Run chunks CPU slices
        // to the next live event. Event at 50, budget 100 => two slices [50, 50].
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);
        bool fired = false;
        machine.Scheduler.ScheduleAt(50, () => fired = true);

        machine.Run(100);

        Assert.Equal([50L, 50L], cpu.RunBudgets);
        Assert.Equal(100, cpu.CycleCount);
        Assert.True(fired);
    }

    [Fact]
    public void Run_fires_a_chunked_event_at_its_exact_committed_cycle()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);
        long seenCycle = -1;
        machine.Scheduler.ScheduleAt(50, () => seenCycle = machine.Scheduler.CurrentCycle);

        machine.Run(100);

        Assert.Equal(50, seenCycle);
    }

    [Fact]
    public void Repeating_event_under_Run_fires_at_exact_intervals()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);
        var log = new List<long>();
        machine.Scheduler.ScheduleEvery(30, () => log.Add(machine.Scheduler.CurrentCycle));

        machine.Run(100);

        Assert.Equal([30L, 60L, 90L], log);
    }

    [Fact]
    public void Canceled_event_does_not_chunk_the_slice()
    {
        // Canceled head discarded by TryPeekNextEventCycle — full budget slice.
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);
        var handle = machine.Scheduler.ScheduleAt(50, () => { });
        handle.Cancel();

        machine.Run(100);

        Assert.Equal([100L], cpu.RunBudgets);
    }

    [Fact]
    public void Run_advances_the_scheduler_to_the_cpu_cycle_count()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(100);

        Assert.Equal(100, machine.Scheduler.CurrentCycle);
    }

    [Fact]
    public void Run_fires_events_scheduled_within_the_budget()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);
        bool fired = false;
        machine.Scheduler.ScheduleAt(50, () => fired = true);

        machine.Run(100);

        Assert.True(fired);
    }

    [Fact]
    public void Consecutive_runs_accumulate_cycles()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(60);
        machine.Run(40);

        Assert.Equal(100, cpu.CycleCount);
        Assert.Equal(100, machine.Scheduler.CurrentCycle);
    }

    [Fact]
    public void Run_with_zero_or_negative_cycles_is_a_no_op()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(0);
        machine.Run(-5);

        Assert.Empty(cpu.RunBudgets);
    }

    [Fact]
    public void Run_with_a_stuck_cpu_throws_instead_of_hanging()
    {
        var machine = MachineWith(new StuckCpu());

        var ex = Assert.Throws<EmulationException>(() => machine.Run(100));
        Assert.Contains("no progress", ex.Message);
    }

    [Fact]
    public void Run_with_an_overshooting_cpu_terminates_and_reports_actual_cycles()
    {
        var cpu = new OvershootingCpu();
        var machine = MachineWith(cpu);
        bool fired = false;
        machine.Scheduler.ScheduleAt(103, () => fired = true);

        long executed = machine.Run(100);

        Assert.Equal(105, executed);                       // 15 × 7-cycle instructions
        Assert.Equal(105, cpu.CycleCount);
        Assert.Equal(105, machine.Scheduler.CurrentCycle); // scheduler lands on actual count
        Assert.True(fired);                                // event past the budget edge still fires
    }

    [Fact]
    public void Run_returns_cycles_executed_for_an_exact_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        Assert.Equal(100, machine.Run(100));
    }
}
