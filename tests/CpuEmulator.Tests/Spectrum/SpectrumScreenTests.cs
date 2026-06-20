using CpuEmulator.Core;
using CpuEmulator.Machines;
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

    private const int Border = 32;
    private const int FullW = 256 + 2 * Border;   // 320
    private const int FullH = 192 + 2 * Border;   // 256
    private const int InkOriginX = Border;        // top-left of the 256x192 ink area
    private const int InkOriginY = Border;

    /// <summary>A bare RAM space ($4000-$FFFF backed) the ULA reads. The ULA decodes screen at $4000.</summary>
    private static (SpectrumUla ula, AddressSpace ram) BuildBareUla()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x4000, new byte[0xC000], writable: true); // $4000-$FFFF (48K)
        var ula = new SpectrumUla(space);
        return (ula, space);
    }

    /// <summary>The Spectrum screen-address bit-shuffle for pixel (x,y).</summary>
    private static uint ScreenAddr(int x, int y) =>
        0x4000u | ((uint)(y & 0xC0) << 5) | ((uint)(y & 0x07) << 8) | ((uint)(y & 0x38) << 2) | (uint)(x >> 3);

    [Fact]
    public void Ula_render_size_is_320x256_with_a_32px_border()
    {
        var (ula, _) = BuildBareUla();
        Assert.Equal(FullW, ula.Width);
        Assert.Equal(FullH, ula.Height);
    }

    [Fact]
    public void Top_left_pixel_uses_the_bit_shuffled_screen_byte_and_its_attribute()
    {
        var (ula, ram) = BuildBareUla();
        // Pixel (0,0): set the top bit of the byte at ScreenAddr(0,0) so pixel x=0 is INK.
        ram.Write8(ScreenAddr(0, 0), 0x80); // bit 7 = leftmost pixel = INK
        // Attribute for cell (0,0) at $5800: INK=red(2), PAPER=white(7), BRIGHT=0, FLASH=0.
        ram.Write8(0x5800, (byte)((2) | (7 << 3)));

        var rgba = new uint[FullW * FullH];
        ula.RenderInto(rgba);

        int px = InkOriginX + 0, py = InkOriginY + 0;
        uint ink = rgba[py * FullW + px];
        Assert.Equal(SpectrumPalette.Colors[2], ink);   // red ink

        // The next pixel (x=1) had bit6=0 → PAPER = white (base).
        uint paper = rgba[py * FullW + (px + 1)];
        Assert.Equal(SpectrumPalette.Colors[7], paper); // white paper
    }

    [Fact]
    public void Bright_attribute_selects_the_bright_palette_half()
    {
        var (ula, ram) = BuildBareUla();
        // A pixel at (8,0): byte at ScreenAddr(8,0), bit 7 set (x=8 → bit 7 of its byte).
        ram.Write8(ScreenAddr(8, 0), 0x80);
        // Cell (1,0) attribute at $5800+1: INK=blue(1), PAPER=black(0), BRIGHT=1.
        ram.Write8(0x5801, (byte)(1 | (0 << 3) | (1 << 6)));

        var rgba = new uint[FullW * FullH];
        ula.RenderInto(rgba);

        uint ink = rgba[(InkOriginY + 0) * FullW + (InkOriginX + 8)];
        Assert.Equal(SpectrumPalette.Colors[8 + 1], ink); // BRIGHT blue
    }

    [Fact]
    public void A_line_far_down_the_screen_uses_the_transposed_address()
    {
        var (ula, ram) = BuildBareUla();
        // y=64 exercises the y7y6 bits (0x40). x=0.
        ram.Write8(ScreenAddr(0, 64), 0x80);
        ram.Write8(0x5800 + (64 / 8) * 32, (byte)(2 | (0 << 3))); // cell row 8: INK red, PAPER black

        var rgba = new uint[FullW * FullH];
        ula.RenderInto(rgba);

        uint ink = rgba[(InkOriginY + 64) * FullW + (InkOriginX + 0)];
        Assert.Equal(SpectrumPalette.Colors[2], ink);
    }

    [Fact]
    public void Spectrum_board_builds_with_z80_rom_ram_and_the_ula_io_slot()
    {
        var blankRom = new byte[SpectrumRom.RomLength]; // a HALT-at-0 ROM is enough to build/run
        blankRom[0] = 0x76; // HALT at $0000

        // The ULA needs the program space to read screen RAM; build the spec, then the machine wires it.
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        var ula = new SpectrumUla(program); // standalone ULA over a throwaway space for the spec shape
        BoardSpec spec = SpectrumBoard.Spec(blankRom, ula);

        Assert.Empty(BoardSpecValidator.Validate(spec));
        Assert.Equal(16, spec.IoAddressBits);
        Assert.Contains(spec.Peripherals, p => p.Space == PeripheralSpace.Io && p.Name == "ula");
        Assert.Contains(spec.Memory, m => m.Kind == RegionKind.Rom && m.Start == 0x0000 && m.Length == 0x4000);
        Assert.Contains(spec.Memory, m => m.Kind == RegionKind.Ram && m.Start == 0x4000 && m.Length == 0xC000);
    }

    [Fact]
    public void A_built_spectrum_machine_renders_ram_the_guest_wrote()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        blankRom[0] = 0x76; // HALT

        // Two-phase: build the machine, THEN point a ULA at its program space, THEN build the real spec.
        // The supported pattern (see SpectrumSurface): construct the ULA over the machine's program space.
        Machine machine = SpectrumMachine.Build(blankRom, out SpectrumUla ula);
        machine.Reset();

        // The guest "wrote" screen byte + attribute via the program space (simulating ROM/game output).
        var prog = machine.Space(AddressSpaceKind.Program);
        prog.Write8(0x4000, 0x80);  // pixel (0,0) ink
        prog.Write8(0x5800, (byte)(2 | (7 << 3))); // red ink on white paper

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        int px = SpectrumUla.BorderPx, py = SpectrumUla.BorderPx;
        Assert.Equal(SpectrumPalette.Colors[2], rgba[py * SpectrumUla.FullWidth + px]); // red ink
    }
}
