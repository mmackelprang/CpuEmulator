namespace CpuEmulator.Peripherals;

/// <summary>The 16-colour ZX Spectrum palette as RGBA8888 (0xFFrrggbb, matching the codebase's
/// IDisplayDevice convention — see DemoFramebuffer). Index 0-7 = the base colours (BRIGHT 0, value
/// 0xD7); index 8-15 = the bright colours (BRIGHT 1, value 0xFF). Colour bits are GRB-ordered on the
/// real ULA (bit0=blue, bit1=red, bit2=green); this table is pre-resolved per index, INK/PAPER 0-7.</summary>
public static class SpectrumPalette
{
    public static readonly uint[] Colors = BuildPalette();

    private static uint[] BuildPalette()
    {
        var p = new uint[16];
        for (int i = 0; i < 8; i++)
        {
            byte level = (byte)0xD7;               // base intensity
            byte blue  = (i & 0x01) != 0 ? level : (byte)0;
            byte red   = (i & 0x02) != 0 ? level : (byte)0;
            byte green = (i & 0x04) != 0 ? level : (byte)0;
            p[i] = Rgba(red, green, blue);
        }
        for (int i = 0; i < 8; i++)
        {
            byte level = (byte)0xFF;               // bright intensity
            byte blue  = (i & 0x01) != 0 ? level : (byte)0;
            byte red   = (i & 0x02) != 0 ? level : (byte)0;
            byte green = (i & 0x04) != 0 ? level : (byte)0;
            p[8 + i] = Rgba(red, green, blue);
        }
        return p;
    }

    private static uint Rgba(byte r, byte g, byte b) =>
        0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | b;
}
