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

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public void ScheduleAt_returns_a_non_null_handle()
    {
        var scheduler = new CycleScheduler();
        ScheduledEvent handle = scheduler.ScheduleAt(10, () => { });
        Assert.NotNull(handle);
        Assert.False(handle.IsCanceled);
    }

    [Fact]
    public void Canceled_event_never_runs()
    {
        var scheduler = new CycleScheduler();
        bool fired = false;
        ScheduledEvent handle = scheduler.ScheduleAt(10, () => fired = true);
        handle.Cancel();
        scheduler.AdvanceTo(100);
        Assert.False(fired);
    }

    [Fact]
    public void Cancel_is_idempotent_twice()
    {
        var scheduler = new CycleScheduler();
        ScheduledEvent handle = scheduler.ScheduleAt(10, () => { });
        handle.Cancel();
        handle.Cancel(); // must not throw
        Assert.True(handle.IsCanceled);
    }

    [Fact]
    public void Cancel_after_one_shot_fired_is_a_no_op()
    {
        var scheduler = new CycleScheduler();
        ScheduledEvent handle = scheduler.ScheduleAt(10, () => { });
        scheduler.AdvanceTo(100);
        handle.Cancel(); // must not throw
        Assert.True(handle.IsCanceled);
    }

    [Fact]
    public void Canceled_event_among_live_same_cycle_events_preserves_survivor_FIFO()
    {
        var scheduler = new CycleScheduler();
        var log = new List<string>();
        var cancelMe = scheduler.ScheduleAt(10, () => log.Add("canceled"));
        scheduler.ScheduleAt(10, () => log.Add("a"));
        scheduler.ScheduleAt(10, () => log.Add("b"));
        cancelMe.Cancel();

        scheduler.AdvanceTo(10);

        Assert.Equal(["a", "b"], log);
    }

    [Fact]
    public void Canceled_head_does_not_advance_committed_time()
    {
        // "Moves no time": the lazy head-discard (TryPeekNextEventCycle) must not commit
        // time — scheduling BELOW the discarded event's cycle stays legal afterwards.
        // If the discard committed time to 50, ScheduleAt(10) would throw.
        var scheduler = new CycleScheduler();
        var canceled = scheduler.ScheduleAt(50, () => { });
        canceled.Cancel();

        Assert.False(scheduler.TryPeekNextEventCycle(out _)); // canceled head discarded

        bool fired = false;
        scheduler.ScheduleAt(10, () => fired = true); // 10 < 50: legal — no time moved
        scheduler.AdvanceTo(20);

        Assert.True(fired);
        Assert.Equal(20, scheduler.CurrentCycle);
    }

    // ── ScheduleEvery ─────────────────────────────────────────────────────────

    [Fact]
    public void ScheduleEvery_fires_at_interval_multiples()
    {
        var scheduler = new CycleScheduler();
        var cycles = new List<long>();
        scheduler.ScheduleEvery(10, () => cycles.Add(scheduler.CurrentCycle));

        scheduler.AdvanceTo(35);

        Assert.Equal([10L, 20L, 30L], cycles);
    }

    [Fact]
    public void ScheduleEvery_cancel_stops_the_chain()
    {
        var scheduler = new CycleScheduler();
        var log = new List<long>();
        var handle = scheduler.ScheduleEvery(10, () => log.Add(scheduler.CurrentCycle));

        scheduler.AdvanceTo(15);
        handle.Cancel();
        scheduler.AdvanceTo(100);

        Assert.Equal([10L], log);
    }

    [Fact]
    public void ScheduleEvery_cancel_inside_its_own_callback_stops_the_chain()
    {
        var scheduler = new CycleScheduler();
        var log = new List<long>();
        ScheduledEvent? handle = null;
        handle = scheduler.ScheduleEvery(10, () =>
        {
            log.Add(scheduler.CurrentCycle);
            if (log.Count >= 2) handle!.Cancel();
        });

        scheduler.AdvanceTo(100);

        Assert.Equal([10L, 20L], log);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ScheduleEvery_non_positive_interval_throws(long interval)
    {
        var scheduler = new CycleScheduler();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => scheduler.ScheduleEvery(interval, () => { }));
    }

    [Fact]
    public void ScheduleAt_and_ScheduleEvery_same_cycle_fire_in_FIFO_order()
    {
        var scheduler = new CycleScheduler();
        var log = new List<string>();
        scheduler.ScheduleAt(10, () => log.Add("once"));
        scheduler.ScheduleEvery(10, () => log.Add("every"));

        scheduler.AdvanceTo(10);

        Assert.Equal(["once", "every"], log);
    }

    // ── Time source ───────────────────────────────────────────────────────────

    [Fact]
    public void After_BindTimeSource_CurrentCycle_reflects_the_live_source()
    {
        var scheduler = new CycleScheduler();
        long fakeNow = 42;
        scheduler.BindTimeSource(() => fakeNow);

        Assert.Equal(42, scheduler.CurrentCycle);
    }

    [Fact]
    public void CurrentCycle_is_max_of_committed_and_live_source()
    {
        var scheduler = new CycleScheduler();
        long fakeNow = 5;
        scheduler.BindTimeSource(() => fakeNow);
        scheduler.AdvanceTo(20);
        fakeNow = 15; // source behind committed

        Assert.Equal(20, scheduler.CurrentCycle); // committed wins
    }

    [Fact]
    public void ScheduleAt_validates_against_device_honest_now()
    {
        var scheduler = new CycleScheduler();
        long fakeNow = 50;
        scheduler.BindTimeSource(() => fakeNow);

        // scheduling at 49 is in the past (source says 50)
        Assert.Throws<ArgumentOutOfRangeException>(
            () => scheduler.ScheduleAt(49, () => { }));
    }

    [Fact]
    public void During_dispatch_CurrentCycle_reports_the_firing_event_cycle()
    {
        // Plan contract: during dispatch, _dispatchCycle (the event's exact cycle) takes
        // precedence over the live time source, even when the source is far ahead.
        // Setup: source starts at 0 so ScheduleAt(10) is valid, then jumps to 200 before
        // AdvanceTo — verifying the dispatch-time wins over the source.
        var scheduler = new CycleScheduler();
        long fakeNow = 0;
        scheduler.BindTimeSource(() => fakeNow);
        long seenDuringCallback = -1;
        scheduler.ScheduleAt(10, () => seenDuringCallback = scheduler.CurrentCycle);
        fakeNow = 200; // source jumps ahead AFTER scheduling; dispatch-time must still win

        scheduler.AdvanceTo(100);

        Assert.Equal(10, seenDuringCallback); // dispatch-time contract
    }
}
