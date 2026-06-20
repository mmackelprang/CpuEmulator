using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumScreenTests
{
    [Fact]
    public void Palette_has_16_entries_with_the_canonical_colours()
    {
        // RGBA8888 as 0xAABBGGRR in memory? No — the codebase uses uint 0xFFrrggbb (see DemoFramebuffer).
        // Black = 0xFF000000; bright white = 0xFFFFFFFF; base blue = 0xFF0000D7; bright blue = 0xFF0000FF.
        Assert.Equal(16, SpectrumPalette.Colors.Length);
        Assert.Equal(0xFF000000u, SpectrumPalette.Colors[0]);  // black
        Assert.Equal(0xFF0000D7u, SpectrumPalette.Colors[1]);  // blue (base)
        Assert.Equal(0xFFD70000u, SpectrumPalette.Colors[2]);  // red (base)
        Assert.Equal(0xFFD7D7D7u, SpectrumPalette.Colors[7]);  // white (base)
        Assert.Equal(0xFF0000FFu, SpectrumPalette.Colors[9]);  // bright blue
        Assert.Equal(0xFFFFFFFFu, SpectrumPalette.Colors[15]); // bright white
    }
}
