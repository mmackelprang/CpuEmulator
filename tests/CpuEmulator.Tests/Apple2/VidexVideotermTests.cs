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

    // Independent letterform oracle (closes the "decode-against-itself" gap): the expected Videx-packed
    // bitmaps below are derived BY HAND from the canonical font8x8_basic shapes (bit 0 = leftmost in font8x8;
    // re-packed to the Videx cell where bit 6 = leftmost via dst|=(0x40>>col)) — NOT read back from
    // VidexFont. If Build()'s glyphs ever scramble (e.g. a reversed bit map, or a regression back to the old
    // box-outline stipple), these exact-byte assertions fail even though an OCR-against-itself gate would
    // still pass. 'A' visibly reads .XX.. / XXXX. / XX..XX / XX..XX / XXXXXX / XX..XX / XX..XX.
    [Fact]
    public void VidexFont_renders_real_legible_letterforms_not_box_outlines()
    {
        AssertGlyph('A', [0x18, 0x3C, 0x66, 0x66, 0x7E, 0x66, 0x66, 0x00]);
        AssertGlyph('C', [0x1E, 0x33, 0x60, 0x60, 0x60, 0x33, 0x1E, 0x00]);
        AssertGlyph('>', [0x30, 0x18, 0x0C, 0x06, 0x0C, 0x18, 0x30, 0x00]);   // the CCP prompt's bracket
        AssertGlyph(' ', [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);   // space stays all-black

        static void AssertGlyph(char ch, byte[] expected)
        {
            int @base = (ch & 0x7F) * VidexFont.GlyphRows;
            for (int row = 0; row < VidexFont.GlyphRows; row++)
                Assert.Equal(expected[row], VidexFont.Fallback[@base + row]);
        }
    }

    [Fact]
    public void LooksLikeFont_accepts_the_synthetic_font_and_rejects_a_non_font_dump()
    {
        // The synthetic font is, by construction, a valid font.
        Assert.True(VidexFont.LooksLikeFont(VidexFont.Fallback));

        // A firmware/garbage 2 KiB image (non-blank space glyph, no real letterforms) is rejected — the
        // exact failure that put a stipple field in the browser. A deterministic pseudo-random fill stands
        // in for the mis-placed firmware dump.
        var garbage = new byte[256 * VidexFont.GlyphRows];
        for (int i = 0; i < garbage.Length; i++) garbage[i] = (byte)((i * 37 + 11) & 0xFF);
        Assert.False(VidexFont.LooksLikeFont(garbage));

        // A blank image (all-zero) is rejected — the A-Z letters carry no ink.
        Assert.False(VidexFont.LooksLikeFont(new byte[256 * VidexFont.GlyphRows]));

        // Wrong length and null are rejected.
        Assert.False(VidexFont.LooksLikeFont(new byte[100]));
        Assert.False(VidexFont.LooksLikeFont(null));
    }

    [Fact]
    public void A_non_font_char_rom_falls_back_to_the_synthetic_font_and_flags_it()
    {
        // Null -> synthetic (flagged).
        Assert.True(new VidexVideoterm().UsingSyntheticFont);

        // A valid-length-but-not-a-font image -> synthetic (flagged), NOT used as glyphs (no throw).
        var garbage = new byte[256 * VidexFont.GlyphRows];
        for (int i = 0; i < garbage.Length; i++) garbage[i] = (byte)((i * 37 + 11) & 0xFF);
        Assert.True(new VidexVideoterm(garbage).UsingSyntheticFont);

        // A real font image -> used as-is (NOT flagged synthetic).
        Assert.False(new VidexVideoterm(VidexFont.Fallback).UsingSyntheticFont);
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

    [Fact]
    public void Selecting_a_vram_bank_remaps_the_CC00_window_to_that_bank_via_the_shipped_Remap()
    {
        // Build a real Apple+Videx machine so $CC00-$CDFF is a mappable window the Videx Remaps.
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000
        (Machine machine, VidexVideoterm videx) = BuildAppleWithVidex(rom);
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // Select bank 1, then write a byte to $CC00 — it must land in the Videx's bank-1 array.
        videx.SelectBankForTest(1);
        bus.Write8(0xCC00, 0x5A);
        Assert.Equal(0x5A, videx.PeekVramForTest(1, 0));   // the guest write reached the live bank-1 array

        // Select bank 2 and write again — bank 1's byte is untouched (the window re-pointed).
        videx.SelectBankForTest(2);
        bus.Write8(0xCC00, 0x3C);
        Assert.Equal(0x3C, videx.PeekVramForTest(2, 0));
        Assert.Equal(0x5A, videx.PeekVramForTest(1, 0));   // bank 1 still holds its earlier byte
    }

    [Fact]
    public void SpecWithVidex_validates_and_builds_with_the_C800_window_mapped()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        (Machine machine, VidexVideoterm videx) = BuildAppleWithVidex(rom);
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // The $CC00 VRAM window is writable RAM (the Videx Remapped it to bank 0 in Realize): a guest write
        // round-trips through the live bank-0 array.
        bus.Write8(0xCC00, 0x77);
        Assert.Equal(0x77, videx.PeekVramForTest(0, 0));

        // The $C800 firmware window is read-only ROM (the Videx Remapped it read-only): a write is ignored.
        byte before = bus.Read8(0xC800);
        bus.Write8(0xC800, 0xAB);
        Assert.Equal(before, bus.Read8(0xC800));   // ROM — the write did not take
    }

    [Fact]
    public void DisplayMultiplexer_switches_to_the_Videx_80col_when_it_signals_active()
    {
        // A 40-col Apple video source (PR-C) + the 80-col Videx (this PR) behind the host multiplexer (PR-M).
        var apple = new Apple2Video(
            ApplePlaceholderBus(), new Apple2VideoState());     // 280x192 (the 40-col render)
        var videx = new VidexVideoterm();                       // NOT yet programmed -> still dormant (_active=false)

        var mux = new DisplayMultiplexer([apple, videx], initialActive: 0);

        // Initially the Apple 40-col source is active (the Videx has not engaged yet).
        Assert.Equal(0, mux.ActiveIndex);
        Assert.Equal(Apple2Video.Width280, mux.Width);
        Assert.Equal(Apple2Video.Height192, mux.Height);

        // Wire the guest-driven active-display signal exactly as PR-O's surface will, BEFORE the guest
        // programs the card: ActiveChanged -> SetActive (index 1 = the Videx; index 0 = the Apple video).
        // Wiring first lets the engagement that happens during CRTC programming actually drive the switch.
        videx.ActiveChanged += active => mux.SetActive(active ? 1 : 0);

        int frames = 0;
        mux.FrameReady += () => frames++;

        // The guest brings the 80-col display online by programming the 6845 CRTC ($C0B0/$C0B1). Under
        // V80-3 (ADR 0018-C OQ1) the FIRST $C0B1 data write IS the active-display engagement trigger
        // (the auto-engage for a CRT80 build like apl2cpm3 that paints VRAM linearly and never bank-selects):
        // it raises ActiveChanged(true) -> mux.SetActive(1) -> the multiplexer switches to the 80-col geometry.
        Program80x24(videx);                                    // 5 x $C0B1 writes, but the no-op guard means ONE transition
        Assert.Equal(1, mux.ActiveIndex);                       // the CRTC program engaged the Videx
        Assert.Equal(1, frames);                                // exactly one switch fired FrameReady (the guard works -> stronger proof)
        Assert.Equal(videx.Width, mux.Width);                   // now the Videx 80x24 geometry (560)
        Assert.Equal(videx.Height, mux.Height);                 // (216)
        Assert.Equal(80 * VidexFont.CellWidth, mux.Width);

        // And the multiplexer now renders the Videx frame (structural ink against the synthetic char ROM).
        var rgba = new uint[mux.Width * mux.Height];
        for (int c = 0; c < 80; c++) videx.PokeVramForTest(0, c, (byte)'A');
        mux.RenderInto(rgba);
        int on = 0;
        foreach (uint p in rgba) if (p == Apple2Palette.MonoOn) on++;
        Assert.True(on > 80, "the multiplexer renders the Videx's inked 80-col frame");

        // The guest hands back to the Apple video: the multiplexer switches back to 40-col. (SetActiveForTest
        // is the explicit deactivate seam -- "hand back to Apple" has no production $C0Bx trigger.)
        videx.SetActiveForTest(false);
        Assert.Equal(0, mux.ActiveIndex);
        Assert.Equal(Apple2Video.Width280, mux.Width);
        Assert.Equal(2, frames);                                // the switch-back also fired FrameReady
    }

    [Fact]
    public void Programming_the_CRTC_through_the_bus_C0B0_C0B1_sets_the_geometry()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000
        (Machine machine, VidexVideoterm videx) = BuildAppleWithVidex(rom);
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // Program a NON-default geometry through the bus so the test fails if the write value is dropped
        // (dropping the value leaves every CRTC reg 0 -> the 80x24 DEFAULT, which would hide the bug).
        void SetReg(byte reg, byte val) { bus.Write8(0xC0B0, reg); bus.Write8(0xC0B1, val); }
        SetReg(1, 40);    // R1 = 40 chars/row (NOT the 80 default)
        SetReg(6, 20);    // R6 = 20 displayed rows (NOT the 24 default)
        SetReg(9, 0x08);  // R9 = 9 lines/char

        Assert.Equal(40 * VidexFont.CellWidth, videx.Width);   // 40*7 = 280 (proves R1 took the written value)
        Assert.Equal(20 * 9, videx.Height);                    // 20*9 = 180 (proves R6 took the written value)
    }

    private static IAddressSpace ApplePlaceholderBus()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    private static (Machine, VidexVideoterm) BuildAppleWithVidex(byte[] systemRom)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);   // the Videx delegate (Task 2 IOU change)
        BoardSpec spec = Apple2Board.SpecWithVidex(systemRom, iou, disk, videx);  // Task 3
        Machine machine = BoardMachineFactory.Build(spec);
        return (machine, videx);
    }
}
