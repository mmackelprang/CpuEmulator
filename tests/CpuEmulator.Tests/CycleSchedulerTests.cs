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
            () => scheduler.ScheduleAt(99, () => { }));
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

    [Fact]
    public void Same_cycle_events_fire_in_scheduling_order()
    {
        var scheduler = new CycleScheduler();
        var log = new List<string>();
        scheduler.ScheduleAt(10, () => log.Add("a"));
        scheduler.ScheduleAt(10, () => log.Add("b"));
        scheduler.ScheduleAt(10, () => log.Add("c"));
        scheduler.ScheduleAt(10, () => log.Add("d"));

        scheduler.AdvanceTo(10);

        Assert.Equal(["a", "b", "c", "d"], log);
    }

    [Fact]
    public void Scheduling_at_the_current_cycle_fires_on_the_next_advance()
    {
        var scheduler = new CycleScheduler();
        scheduler.AdvanceTo(100);
        bool fired = false;
        scheduler.ScheduleAt(100, () => fired = true);

        scheduler.AdvanceTo(100);

        Assert.True(fired);
    }

    [Fact]
    public void Callback_scheduling_at_its_own_cycle_fires_within_the_same_advance()
    {
        var scheduler = new CycleScheduler();
        var log = new List<string>();
        scheduler.ScheduleAt(10, () =>
        {
            log.Add("first");
            scheduler.ScheduleAt(10, () => log.Add("second"));
        });

        scheduler.AdvanceTo(100);

        Assert.Equal(["first", "second"], log);
        Assert.Equal(100, scheduler.CurrentCycle);
    }

    [Fact]
    public void AdvanceTo_below_current_cycle_is_a_no_op()
    {
        var scheduler = new CycleScheduler();
        scheduler.AdvanceTo(50);
        bool fired = false;
        scheduler.ScheduleAt(60, () => fired = true);

        scheduler.AdvanceTo(40);

        Assert.False(fired);
        Assert.Equal(50, scheduler.CurrentCycle);
    }
}
