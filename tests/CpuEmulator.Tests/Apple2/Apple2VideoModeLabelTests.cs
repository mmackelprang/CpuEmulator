using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2VideoModeLabelTests
{
    private static Apple2Video Video(out Apple2VideoState state)
    {
        state = new Apple2VideoState();
        var ram = new AddressSpace(AddressSpaceKind.Program, 16);
        ram.MapMemory(0x0000, new byte[0x10000], writable: true);
        return new Apple2Video(ram, state, charRom: null);
    }

    [Fact]
    public void Mode_label_reflects_the_live_video_state_flags()
    {
        Apple2Video video = Video(out Apple2VideoState state);

        // Power-on default: text, page 1, full, lo-res.
        Assert.Equal("TEXT · 40×24 · page 1", video.ModeLabel);

        state.GraphicsOn = true; state.HiRes = true; state.Page2 = true;
        Assert.Equal("HIRES · 280×192 · page 2", video.ModeLabel);

        state.HiRes = false;                                   // lo-res graphics
        Assert.Equal("LORES · 40×48 · page 2", video.ModeLabel);

        state.GraphicsOn = true; state.HiRes = true; state.Mixed = true; state.Page2 = false;
        Assert.Equal("MIXED · text+gfx · page 1", video.ModeLabel);
    }
}
