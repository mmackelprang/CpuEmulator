namespace CpuEmulator.Peripherals;

/// <summary>Apple ][+ colours as RGBA8888 (0xFFrrggbb). LoRes is the 16-colour low-res palette. Mono
/// is the hi-res monochrome pair. Artifact (basic 4-colour) ships per ADR 0014 Decision 8's default —
/// correct mono + basic green/purple/blue/orange; the full 12°-phase NTSC model is a later fidelity
/// dial.</summary>
public static class Apple2Palette
{
    public const uint MonoOff = 0xFF000000u; // black
    public const uint MonoOn  = 0xFFFFFFFFu; // white

    /// <summary>The 16 low-res colours (the standard ][+ lo-res palette, RGBA8888). Index = the 4-bit
    /// nibble value. Values are the widely-used canonical approximations.</summary>
    public static readonly uint[] LoRes =
    [
        0xFF000000, // 0 black
        0xFF8A2140, // 1 magenta/deep red
        0xFF3C22A5, // 2 dark blue
        0xFFC847E4, // 3 purple
        0xFF07653E, // 4 dark green
        0xFF7B7B7B, // 5 grey 1
        0xFF308EF3, // 6 medium blue
        0xFFB9A9FD, // 7 light blue
        0xFF4F5101, // 8 brown
        0xFFF25E00, // 9 orange
        0xFFC0C0C0, // 10 grey 2
        0xFFFF8FAF, // 11 pink
        0xFF38CB00, // 12 green
        0xFFD5CF30, // 13 yellow
        0xFF8AF9BC, // 14 aqua
        0xFFFFFFFF, // 15 white
    ];

    /// <summary>Basic hi-res artifact colours (ADR 0014 Decision 8 default): violet/green (bit7 clear)
    /// and blue/orange (bit7 set). Index by [bit7][evenColumn].</summary>
    public static readonly uint[] Artifact =
    [
        0xFFC847E4, // bit7=0, even -> violet
        0xFF38CB00, // bit7=0, odd  -> green
        0xFF308EF3, // bit7=1, even -> blue
        0xFFF25E00, // bit7=1, odd  -> orange
    ];
}
