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
}
