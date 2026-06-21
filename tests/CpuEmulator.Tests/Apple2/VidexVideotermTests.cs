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
}
