using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumBorderTests
{
    private static SpectrumUla BareUla()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x4000, new byte[0xC000], writable: true);
        return new SpectrumUla(space);
    }

    [Theory]
    [InlineData(0)] // black
    [InlineData(2)] // red
    [InlineData(6)] // yellow
    public void Out_FE_sets_the_border_colour_in_the_border_region(int color)
    {
        var ula = BareUla();
        ula.Write(0xFEu, AccessWidth.Byte, (byte)color); // border = low 3 bits

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        // The top-left corner (0,0) is in the border band.
        Assert.Equal(SpectrumPalette.Colors[color], rgba[0]);
        // A pixel mid-top-border (row 5, col 100) is also border.
        Assert.Equal(SpectrumPalette.Colors[color], rgba[5 * SpectrumUla.FullWidth + 100]);
    }

    [Fact]
    public void Changing_the_border_changes_the_rendered_border_but_not_the_ink_area_default()
    {
        var ula = BareUla();
        ula.Write(0xFEu, AccessWidth.Byte, 0x01); // blue border

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);
        Assert.Equal(SpectrumPalette.Colors[1], rgba[0]); // border blue

        // The ink area centre (RAM all zero → attr 0 → INK black on PAPER black → black) is NOT blue.
        int cx = SpectrumUla.BorderPx + 128;
        int cy = SpectrumUla.BorderPx + 96;
        Assert.Equal(SpectrumPalette.Colors[0], rgba[cy * SpectrumUla.FullWidth + cx]);
    }
}
