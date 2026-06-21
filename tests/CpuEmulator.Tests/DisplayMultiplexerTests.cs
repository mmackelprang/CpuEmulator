using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class DisplayMultiplexerTests
{
    // A minimal IDisplayDevice test double of a fixed size that records RenderInto + can raise FrameReady.
    private sealed class FakeDisplay(int width, int height, uint fill) : IDisplayDevice
    {
        public int Width => width;
        public int Height => height;
        public int RenderCalls { get; private set; }
        public void RenderInto(Span<uint> rgba)
        {
            if (rgba.Length < Width * Height)
                throw new ArgumentException($"need {Width * Height}; got {rgba.Length}", nameof(rgba));
            RenderCalls++;
            rgba[..(Width * Height)].Fill(fill);
        }
        public event Action? FrameReady;
        public void RaiseFrame() => FrameReady?.Invoke();
    }

    [Fact]
    public void Delegates_dimensions_and_render_to_the_active_source()
    {
        var a = new FakeDisplay(280, 192, 0xFF111111);
        var b = new FakeDisplay(720, 216, 0xFF222222);
        var mux = new DisplayMultiplexer([a, b], initialActive: 0);

        Assert.Equal(280, mux.Width);
        Assert.Equal(192, mux.Height);

        var buf = new uint[720 * 216];
        mux.RenderInto(buf);
        Assert.Equal(1, a.RenderCalls);     // the active source rendered
        Assert.Equal(0, b.RenderCalls);     // the inactive source did not
        Assert.Equal(0xFF111111u, buf[0]);  // a's fill
    }

    [Fact]
    public void SetActive_switches_dimensions_render_target_and_fires_FrameReady()
    {
        var a = new FakeDisplay(280, 192, 0xFF111111);
        var b = new FakeDisplay(720, 216, 0xFF222222);
        var mux = new DisplayMultiplexer([a, b]);

        int frames = 0;
        mux.FrameReady += () => frames++;

        mux.SetActive(1);                   // switch to the 720x216 source
        Assert.Equal(1, frames);            // the switch fires FrameReady (so the host re-pulls at the new size)
        Assert.Equal(720, mux.Width);
        Assert.Equal(216, mux.Height);

        var buf = new uint[720 * 216];
        mux.RenderInto(buf);
        Assert.Equal(1, b.RenderCalls);     // now the second source renders
        Assert.Equal(0xFF222222u, buf[0]);
    }

    [Fact]
    public void Only_the_active_sources_FrameReady_is_forwarded()
    {
        var a = new FakeDisplay(280, 192, 0xFF111111);
        var b = new FakeDisplay(720, 216, 0xFF222222);
        var mux = new DisplayMultiplexer([a, b], initialActive: 0);

        int frames = 0;
        mux.FrameReady += () => frames++;

        a.RaiseFrame();          // the active source's vblank -> forwarded
        Assert.Equal(1, frames);

        b.RaiseFrame();          // a dormant source's vblank -> dropped (the host only pulls the active one)
        Assert.Equal(1, frames);
    }

    [Fact]
    public void A_single_source_multiplexer_is_transparent()
    {
        var only = new FakeDisplay(256, 192, 0xFF333333);
        var mux = new DisplayMultiplexer([only]);

        Assert.Equal(256, mux.Width);
        Assert.Equal(192, mux.Height);

        int frames = 0;
        mux.FrameReady += () => frames++;
        only.RaiseFrame();
        Assert.Equal(1, frames);             // the one source's frames forward

        mux.SetActive(0);                    // switching to the already-active source is a no-op
        Assert.Equal(1, frames);             // no extra FrameReady (index unchanged)
    }

    [Fact]
    public void SetActive_rejects_an_out_of_range_index()
    {
        var mux = new DisplayMultiplexer([new FakeDisplay(8, 8, 0)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => mux.SetActive(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mux.SetActive(-1));
    }

    [Fact]
    public void The_ctor_rejects_an_empty_source_list_and_a_bad_initial_index()
    {
        Assert.Throws<ArgumentException>(() => new DisplayMultiplexer([]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DisplayMultiplexer([new FakeDisplay(8, 8, 0)], initialActive: 2));
    }
}
