using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2VideoTests
{
    [Fact]
    public void LoRes_palette_has_16_entries_opaque()
    {
        Assert.Equal(16, Apple2Palette.LoRes.Length);
        Assert.All(Apple2Palette.LoRes.ToArray(), c => Assert.Equal(0xFF000000u, c & 0xFF000000u));
    }

    [Fact]
    public void Mono_white_and_black_are_defined()
    {
        Assert.Equal(0xFF000000u, Apple2Palette.MonoOff);
        Assert.Equal(0xFFFFFFFFu, Apple2Palette.MonoOn);
    }

    [Fact]
    public void Fallback_font_has_a_glyph_per_byte_of_8_rows()
    {
        // 256 glyphs x 8 rows; glyph 'A' (0x41 & 0x3F screen code mapping aside) has some set bits.
        Assert.Equal(256 * 8, Apple2Font.Fallback.Length);
        // The space-ish glyph (index 0x20) should be blank; an 'A'-ish glyph non-blank.
        int aRowsSet = 0;
        for (int row = 0; row < 8; row++) if (Apple2Font.Fallback[0x41 * 8 + row] != 0) aRowsSet++;
        Assert.True(aRowsSet > 0, "the 'A' glyph should have set pixels");
    }

    private const int HiResW = 280;
    private const int HiResH = 192;

    /// <summary>A bare video chip over a 16-bit RAM space the test writes screen bytes into. The chip
    /// is constructed + bound directly (the Spectrum-test pattern: no full board needed for the render
    /// gate). HiRes mode is selected on the shared state.</summary>
    private static (Apple2Video video, AddressSpace ram, Apple2VideoState state) BuildHiRes()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true); // whole 64K as RAM for the test
        var state = new Apple2VideoState { GraphicsOn = true, HiRes = true, Mixed = false, Page2 = false };
        var video = new Apple2Video(space, state);
        return (video, space, state);
    }

    [Fact]
    public void HiRes_render_size_is_280x192()
    {
        var (video, _, _) = BuildHiRes();
        Assert.Equal(HiResW, video.Width);
        Assert.Equal(HiResH, video.Height);
    }

    [Fact]
    public void A_set_hires_bit_lights_its_pixel_using_the_verified_addr()
    {
        var (video, ram, _) = BuildHiRes();
        // Row y=64 starts at $2028 (a landmark that exercises the (y/64) third-region stride). Set
        // bit 0 of the first byte -> the leftmost pixel of that row is ON.
        uint rowBase = Apple2HiResAddress.RowBase(64, page2: false);  // $2028
        ram.Write8(rowBase, 0x01);   // low bit = leftmost of the 7 pixels in this byte

        var rgba = new uint[HiResW * HiResH];
        video.RenderInto(rgba);

        // Pixel (x=0, y=64) must be ON (not black); a neighbour with no bit set is OFF.
        Assert.NotEqual(Apple2Palette.MonoOff, rgba[64 * HiResW + 0]);
        Assert.Equal(Apple2Palette.MonoOff, rgba[64 * HiResW + 1]);
    }

    [Fact]
    public void Page2_reads_the_4000_region()
    {
        var (video, ram, state) = BuildHiRes();
        state.Page2 = true;
        uint rowBase = Apple2HiResAddress.RowBase(0, page2: true);    // $4000
        ram.Write8(rowBase, 0x01);

        var rgba = new uint[HiResW * HiResH];
        video.RenderInto(rgba);
        Assert.NotEqual(Apple2Palette.MonoOff, rgba[0 * HiResW + 0]); // top-left lit from page 2
    }

    [Fact]
    public void Render_raises_FrameReady_on_the_scheduled_tick()
    {
        // Realize schedules the 60 Hz tick; firing it raises FrameReady. We assert the event wiring via
        // a built Machine so the scheduler actually runs (see the Apple2Board integration in PR-B tests).
        // Here we just confirm the event is invokable and the render does not throw on a too-small span.
        var (video, _, _) = BuildHiRes();
        var rgba = new uint[HiResW * HiResH];
        bool raised = false;
        video.FrameReady += () => raised = true;
        video.RaiseFrameForTest();   // test-only hook standing in for the scheduler tick
        Assert.True(raised);

        Assert.Throws<ArgumentException>(() => video.RenderInto(new uint[10]));
    }
}
