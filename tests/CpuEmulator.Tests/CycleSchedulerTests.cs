using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class CycleSchedulerTests
{
    [Fact]
    public void Events_fire_in_cycle_order_regardless_of_scheduling_order()
    {
        var scheduler = new CycleScheduler();
        var log = new List<string>();
        scheduler.ScheduleAt(20, () => log.Add("b"));
        scheduler.ScheduleAt(10, () => log.Add("a"));

        scheduler.AdvanceTo(100);

        Assert.Equal(["a", "b"], log);
    }

    [Fact]
    public void Event_at_exact_advance_boundary_fires()
    {
        var scheduler = new CycleScheduler();
        bool fired = false;
        scheduler.ScheduleAt(50, () => fired = true);

        scheduler.AdvanceTo(50);

        Assert.True(fired);
    }

    [Fact]
    public void Events_beyond_target_do_not_fire()
    {
        var scheduler = new CycleScheduler();
        bool fired = false;
        scheduler.ScheduleAt(51, () => fired = true);

        scheduler.AdvanceTo(50);

        Assert.False(fired);
        scheduler.AdvanceTo(51);
        Assert.True(fired);
    }

    [Fact]
    public void Callback_may_schedule_a_followup_within_the_same_advance()
    {
        var scheduler = new CycleScheduler();
        var log = new List<long>();
        scheduler.ScheduleAt(10, () =>
        {
            log.Add(scheduler.CurrentCycle);
            scheduler.ScheduleAt(20, () => log.Add(scheduler.CurrentCycle));
        });

        scheduler.AdvanceTo(100);

        Assert.Equal([10L, 20L], log);
    }

    [Fact]
    public void Scheduling_in_the_past_throws()
    {
        var scheduler = new CycleScheduler();
        scheduler.AdvanceTo(100);

        Assert.Throws<ArgumentOutOfRangeException>(
            (Action)(() => scheduler.ScheduleAt(99, () => { })));
    }

    [Fact]
    public void CurrentCycle_reaches_target_even_with_no_events()
    {
        var scheduler = new CycleScheduler();
        scheduler.AdvanceTo(42);
        Assert.Equal(42, scheduler.CurrentCycle);
    }

    [Fact]
    public void CurrentCycle_equals_event_cycle_inside_a_callback()
    {
        var scheduler = new CycleScheduler();
        long seen = -1;
        scheduler.ScheduleAt(10, () => seen = scheduler.CurrentCycle);

        scheduler.AdvanceTo(100);

        Assert.Equal(10, seen);
        Assert.Equal(100, scheduler.CurrentCycle);
    }
}
