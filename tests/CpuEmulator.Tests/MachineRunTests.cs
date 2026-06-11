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
    public void Run_passes_the_full_budget_to_the_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(100);

        Assert.Equal([100L], cpu.RunBudgets);
        Assert.Equal(100, cpu.CycleCount);
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
}
