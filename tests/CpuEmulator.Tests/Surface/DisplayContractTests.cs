using CpuEmulator.Core;

namespace CpuEmulator.Tests.Surface;

public class DisplayContractTests
{
    private sealed class StubDisplay : IDisplayDevice
    {
        public int Width => 2;
        public int Height => 1;
        public void RenderInto(Span<uint> rgba)
        {
            if (rgba.Length < Width * Height)
                throw new ArgumentException("span too small", nameof(rgba));
            rgba[0] = 0xFF0000FFu; // red
            rgba[1] = 0xFF00FF00u; // green
        }
        public event Action? FrameReady;
        public void Raise() => FrameReady?.Invoke();
    }

    [Fact]
    public void RenderInto_fills_the_rgba_span_and_FrameReady_fires()
    {
        var d = new StubDisplay();
        bool fired = false;
        d.FrameReady += () => fired = true;

        Span<uint> buf = stackalloc uint[d.Width * d.Height];
        d.RenderInto(buf);
        d.Raise();

        Assert.Equal(0xFF0000FFu, buf[0]);
        Assert.Equal(0xFF00FF00u, buf[1]);
        Assert.True(fired);
    }

    [Fact]
    public void RenderInto_throws_on_a_too_small_span()
    {
        var d = new StubDisplay();
        Assert.Throws<ArgumentException>(() =>
        {
            // local function so the Span isn't captured by the lambda
            void Call() { Span<uint> tiny = stackalloc uint[1]; d.RenderInto(tiny); }
            Call();
        });
    }
}
