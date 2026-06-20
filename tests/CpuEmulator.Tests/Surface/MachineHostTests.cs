using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class MachineHostTests
{
    [Fact]
    public void Step_pushes_an_encoded_frame_after_a_vblank()
    {
        DemoSurfaceFixture fix = DemoSurfaceFixture.Build();
        fix.Machine.Reset();

        var frames = new List<byte[]>();
        var host = new MachineHost(fix.Machine, fix.Framebuffer, fix.Keyboard, frames.Add);

        // Run past at least one 60 Hz vblank interval so FrameReady fires and a frame is pushed.
        host.Step(100_000);

        Assert.NotEmpty(frames);
        // The pushed frame is a valid FB frame with the framebuffer's dimensions.
        byte[] frame = frames[0];
        Assert.Equal((byte)'F', frame[0]);
        Assert.Equal((byte)'B', frame[1]);
        int w = frame[4] | (frame[5] << 8);
        int h = frame[6] | (frame[7] << 8);
        Assert.Equal(fix.Framebuffer.Width, w);
        Assert.Equal(fix.Framebuffer.Height, h);
        Assert.Equal(8 + w * h * 4, frame.Length);
    }

    [Fact]
    public void PostKey_routes_to_the_keyboard_and_the_guest_observes_it()
    {
        DemoSurfaceFixture fix = DemoSurfaceFixture.Build();
        fix.Machine.Reset();
        var host = new MachineHost(fix.Machine, fix.Framebuffer, fix.Keyboard, _ => { });

        host.RunHeadless(20_000, 5_000);                                   // paint + enter poll loop
        host.PostKey(new KeyEvent(KeyAction.Down, KeyCode.J, 'J'));
        host.RunHeadless(20_000, 5_000);                                   // guest echoes the key

        var rgba = new uint[fix.Framebuffer.Width * fix.Framebuffer.Height];
        fix.Framebuffer.RenderInto(rgba);
        uint j = 0xFF000000u | ((uint)'J' << 16) | ((uint)'J' << 8) | (uint)'J';
        Assert.Equal(j, rgba[0x0100]); // VRAM $8100 echo cell
    }

    [Fact]
    public void RunHeadless_pushes_at_least_one_frame_over_a_multi_vblank_run()
    {
        DemoSurfaceFixture fix = DemoSurfaceFixture.Build();
        fix.Machine.Reset();
        var frames = new List<byte[]>();
        var host = new MachineHost(fix.Machine, fix.Framebuffer, fix.Keyboard, frames.Add);

        host.RunHeadless(100_000, 10_000);

        Assert.NotEmpty(frames);
    }
}
