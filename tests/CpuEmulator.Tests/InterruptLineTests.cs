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
}
