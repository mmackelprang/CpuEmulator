using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class InterruptLineTests
{
    [Fact]
    public void Assert_forwards_true_to_target()
    {
        bool? seen = null;
        var line = new InterruptLine(v => seen = v);

        line.Assert();

        Assert.True(seen);
        Assert.True(line.IsAsserted);
    }

    [Fact]
    public void Release_forwards_false_to_target()
    {
        bool? seen = null;
        var line = new InterruptLine(v => seen = v);
        line.Assert();

        line.Release();

        Assert.False(seen);
        Assert.False(line.IsAsserted);
    }

    [Fact]
    public void Reassert_while_asserted_forwards_true_again()
    {
        var calls = new List<bool>();
        var line = new InterruptLine(calls.Add);
        line.Assert();
        line.Assert();

        Assert.Equal([true, true], calls);
        Assert.True(line.IsAsserted);
    }

    [Fact]
    public void Release_without_assert_forwards_false()
    {
        var calls = new List<bool>();
        var line = new InterruptLine(calls.Add);
        line.Release();

        Assert.Equal([false], calls);
        Assert.False(line.IsAsserted);
    }

    // ── Wired-OR: multiple sources ────────────────────────────────────────────

    [Fact]
    public void Two_sources_hold_the_line_while_either_is_asserted()
    {
        var calls = new List<bool>();
        var line = new InterruptLine(calls.Add);
        var a = line.Source();
        var b = line.Source();

        a.Assert();
        b.Assert();
        a.Release();

        Assert.True(line.IsAsserted); // b still asserted
        Assert.True(calls[^1]); // last forwarded was still true after a.Release

        b.Release();

        Assert.False(line.IsAsserted);
    }

    [Fact]
    public void Source_handles_track_their_own_assertion()
    {
        var line = new InterruptLine(_ => { });
        var a = line.Source();
        var b = line.Source();

        a.Assert();

        Assert.True(a.IsAsserted);
        Assert.False(b.IsAsserted);
    }

    [Fact]
    public void Source_of_a_source_joins_the_same_line()
    {
        var calls = new List<bool>();
        var line = new InterruptLine(calls.Add);
        var src = line.Source().Source(); // Source() on a handle joins the same OR

        src.Assert();

        Assert.True(line.IsAsserted);
        Assert.Contains(true, calls);
    }

    [Fact]
    public void Direct_assert_is_an_input_alongside_sources()
    {
        var calls = new List<bool>();
        var line = new InterruptLine(calls.Add);
        var src = line.Source();

        line.Assert();
        src.Assert();
        line.Release(); // direct released, src still asserted

        Assert.True(line.IsAsserted); // OR still high

        src.Release();

        Assert.False(line.IsAsserted);
    }

    [Fact]
    public void Second_source_asserting_a_high_line_does_not_pulse()
    {
        // NMI-edge safety: the level never dips to false between two asserts
        var calls = new List<bool>();
        var line = new InterruptLine(calls.Add);
        var a = line.Source();
        var b = line.Source();

        a.Assert();
        b.Assert();

        // calls should be [true, true] — never a false between
        Assert.Equal([true, true], calls);
    }

    [Fact]
    public void Releasing_one_of_two_asserted_sources_forwards_the_still_high_level()
    {
        var calls = new List<bool>();
        var line = new InterruptLine(calls.Add);
        var a = line.Source();
        var b = line.Source();

        a.Assert(); // calls: [true]
        b.Assert(); // calls: [true, true]
        a.Release(); // calls: [true, true, true] — b still asserted, forwarded true

        Assert.Equal([true, true, true], calls);
        Assert.True(line.IsAsserted);
    }

    [Fact]
    public void Machine_level_two_peripherals_share_the_irq_line()
    {
        var calls = new List<bool>();
        var line = new InterruptLine(calls.Add);

        var srcA = line.Source();
        var srcB = line.Source();

        srcA.Assert();
        Assert.True(line.IsAsserted);

        srcB.Assert();
        srcA.Release();
        Assert.True(line.IsAsserted); // srcB still holds it

        srcB.Release();
        Assert.False(line.IsAsserted);
    }
}
