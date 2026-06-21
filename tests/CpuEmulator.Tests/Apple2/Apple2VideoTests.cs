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
}
