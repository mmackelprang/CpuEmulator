using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class VidexVideotermTests
{
    [Fact]
    public void VidexFont_fallback_is_256x8_with_a_blank_space_and_inked_letters()
    {
        Assert.Equal(256 * 8, VidexFont.Fallback.Length);

        // The space glyph ($20) is blank — all 8 rows zero (no ink).
        for (int row = 0; row < 8; row++)
            Assert.Equal(0, VidexFont.Fallback[0x20 * 8 + row]);

        // A printable letter ('A' = $41) has ink in at least one row (a non-blank glyph).
        int aInk = 0;
        for (int row = 0; row < 8; row++)
            aInk += System.Numerics.BitOperations.PopCount((uint)VidexFont.Fallback[0x41 * 8 + row]);
        Assert.True(aInk > 0, "the 'A' glyph must carry ink");

        Assert.Equal(7, VidexFont.CellWidth);   // 7-px Videx cell
        Assert.Equal(8, VidexFont.GlyphRows);   // 8 active glyph rows (the char ROM is 256x8)
    }

    // Program the standard Videx 80x24 init (research §8 / ADR 0016): R1=$50 (80 cols), R6=$18 (24 rows),
    // R9=$08 (9 lines/char). Writes go reg#->$C0B0 (offset 0), value->$C0B1 (offset 1).
    private static void Program80x24(VidexVideoterm videx)
    {
        void SetReg(byte reg, byte val)
        {
            videx.Write(0x00, AccessWidth.Byte, reg);   // register-select ($C0B0)
            videx.Write(0x01, AccessWidth.Byte, val);   // data ($C0B1)
        }
        SetReg(1, 0x50);   // R1 = 80 chars/row
        SetReg(6, 0x18);   // R6 = 24 displayed rows
        SetReg(9, 0x08);   // R9 = 9 scan lines/char minus 1 -> 9 lines
        SetReg(12, 0x00);  // R12 = start address high
        SetReg(13, 0x00);  // R13 = start address low
    }

    [Fact]
    public void Crtc_programming_yields_80x24_geometry()
    {
        var videx = new VidexVideoterm();
        Program80x24(videx);
        Assert.Equal(80 * VidexFont.CellWidth, videx.Width);   // 80 cols x 7-px cell = 560
        Assert.Equal(24 * 9, videx.Height);                    // 24 rows x 9-line cell = 216
    }

    [Fact]
    public void Vram_of_known_codes_renders_structural_ink_through_the_synthetic_char_rom()
    {
        var videx = new VidexVideoterm();           // null char ROM -> VidexFont.Fallback
        Program80x24(videx);

        // Write a row of 'A' ($41, inked) into the scanout bank's first 80 cells; the rest stay $00.
        // (Bank 0 is the scanout base when R12/R13 = 0.)
        for (int c = 0; c < 80; c++)
            videx.PokeVramForTest(0, c, (byte)'A');

        var rgba = new uint[videx.Width * videx.Height];
        videx.RenderInto(rgba);

        int on = 0, off = 0;
        foreach (uint p in rgba)
        {
            if (p == Apple2Palette.MonoOn) on++;
            else if (p == Apple2Palette.MonoOff) off++;
        }
        Assert.Equal(rgba.Length, on + off);        // monochrome — every pixel is on or off
        Assert.True(off > rgba.Length / 2, "a mostly-blank terminal screen");
        Assert.True(on > 80, "the row of 'A's must paint ink (a dead render is all-off)");
    }

    [Fact]
    public void An_unprogrammed_videx_reports_a_valid_default_size_never_zero()
    {
        var videx = new VidexVideoterm();           // no CRTC programming yet
        Assert.True(videx.Width > 0 && videx.Height > 0,
            "Width/Height must never be zero (the multiplexer/host divide by them)");
    }

    [Fact]
    public void Enabling_the_videx_raises_ActiveChanged_true_exactly_once_on_the_transition()
    {
        var videx = new VidexVideoterm();
        var events = new List<bool>();
        videx.ActiveChanged += active => events.Add(active);

        videx.SetActiveForTest(true);    // the guest turns the Videx on (the $C800-window enable)
        videx.SetActiveForTest(true);    // idempotent — no second event on no transition
        videx.SetActiveForTest(false);   // the Apple re-selects its video

        Assert.Equal(new[] { true, false }, events);   // one transition each way, no duplicates
    }
}
