using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class DemoFramebufferTests
{
    [Fact]
    public void Dimensions_are_256_by_192()
    {
        var fb = new DemoFramebuffer();
        Assert.Equal(256, fb.Width);
        Assert.Equal(192, fb.Height);
    }

    [Fact]
    public void Written_vram_byte_renders_through_the_grayscale_palette()
    {
        var fb = new DemoFramebuffer();
        // pixel (0,0) = palette index 0x00 (black); pixel (1,0) = index 0xFF (white)
        fb.Write(0, AccessWidth.Byte, 0x00);
        fb.Write(1, AccessWidth.Byte, 0xFF);

        var rgba = new uint[fb.Width * fb.Height];
        fb.RenderInto(rgba);

        Assert.Equal(0xFF000000u, rgba[0]); // black, opaque
        Assert.Equal(0xFFFFFFFFu, rgba[1]); // white, opaque
    }

    [Fact]
    public void A_mid_index_maps_to_a_gray_ramp_entry()
    {
        var fb = new DemoFramebuffer();
        fb.Write(10, AccessWidth.Byte, 0x80);

        var rgba = new uint[fb.Width * fb.Height];
        fb.RenderInto(rgba);

        Assert.Equal(0xFF808080u, rgba[10]);
    }

    [Fact]
    public void Reads_return_the_stored_vram_byte()
    {
        var fb = new DemoFramebuffer();
        fb.Write(5, AccessWidth.Byte, 0x3C);
        Assert.Equal(0x3Cu, fb.Read(5, AccessWidth.Byte));
    }

    [Fact]
    public void RenderInto_throws_on_a_too_small_span()
    {
        var fb = new DemoFramebuffer();
        Assert.Throws<ArgumentException>(() => fb.RenderInto(new uint[10]));
    }

    // Re-enabled in Task 8 once DemoSurfaceFixture.BuildMachineWith exists.
    // [Fact]
    // public void FrameReady_fires_on_the_scheduled_vblank_tick()
    // {
    //     var fb = new DemoFramebuffer();
    //     bool fired = false;
    //     fb.FrameReady += () => fired = true;
    //
    //     // Drive a Machine so the scheduler advances past one 60 Hz vblank interval.
    //     var machine = CpuEmulator.Tests.Surface.DemoSurfaceFixture.BuildMachineWith(fb);
    //     machine.Reset();
    //     machine.Run(machine.Cpu is null ? 0 : 100_000); // > one vblank interval at the demo clock
    //
    //     Assert.True(fired);
    // }
}
