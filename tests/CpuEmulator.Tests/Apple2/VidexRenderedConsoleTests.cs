using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

/// <summary>The rendered-output gate that closes the ORACLE GAP the apl2cpm3 80-col browser UAT exposed
/// (ADR 0018 / V80 -- the Videx render bug). The shipped Apl2Cpm3BootTests + WebApl2Cpm3RenderTests gates
/// decode the console off the <c>$CC00</c> VRAM (the bus window, via <c>PeekVramForTest</c>) -- they never
/// exercise the CRTC-start-relative scanout the LIVE <see cref="VidexVideoterm.RenderInto"/> walks to build
/// the streamed RGBA. The bug: the apl2cpm3 firmware programs the 6845 scanout base to 960 (R12=3/R13=192)
/// and paints the 1920-char console across the 2 KiB VRAM with wrap, but the old <c>RenderInto</c> sourced
/// ONE 512-byte bank, so every cell &gt;= 512 read $00 and the char ROM's $00 glyph tiled all 1920 cells --
/// the browser saw an illegible uniform-glyph field (measured: 28.6% ink, exactly 1 distinct cell bitmap).
/// The headless VRAM gates passed anyway because the text happens to sit at bank-0 offsets 0-511.
///
/// THIS gate asserts on <c>RenderInto</c>'s RGBA OUTPUT (the streamed pixels), not the VRAM. It drives the
/// real apl2cpm3 boot for the genuine console CONTENT, but renders through the deterministic synthetic
/// <see cref="VidexFont"/> (the asset-free PR-N render font) so the streamed pixels OCR back EXACTLY -- the
/// char ROM only governs pixel shapes at render time, never the boot logic, so the synthetic font yields the
/// identical VRAM and an un-ambiguous pixel oracle (the real char ROM's glyph-decode fidelity is a separate
/// concern from the scanout bug under test). It OCRs the 80x24 text grid out of the rendered RGBA by matching
/// each cell bitmap against the synthetic font, and asserts the decoded <c>A&gt;</c> + the
/// <c>CP/M Version 3.0</c> sign-on are LEGIBLE -- AND that there are MANY distinct cell bitmaps (the
/// un-fakeable discriminator: the scanout bug produces exactly 1; a real console paints dozens). It FAILS on
/// the one-bank bug (red) and PASSES after the full-2-KiB-scanout fix (green).</summary>
public class VidexRenderedConsoleTests
{
    [Apl2Cpm3VidexFact]
    public void RenderInto_streams_a_legible_80col_A_prompt_from_the_full_2KiB_scanout()
    {
        var (systemRomPath, disk1Path, videxFirmware, _) =
            Apl2Cpm3Vectors.TryGetVidexAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the apl2cpm3 render gate");
        IBlockDevice disk1 = Apl2Cpm3.LoadBootDisk(disk1Path);

        // Build the SoftCard+Videx board exactly as Apl2Cpm3BootTests (slot 4, the REAL firmware that drives
        // the 80-col console), but render through the SYNTHETIC font so the streamed pixels OCR exactly. The
        // char ROM is host-side render data only -- the guest never reads it, so the VRAM content is identical
        // to a real-char-ROM boot; this isolates the scanout bug under test from char-ROM glyph fidelity.
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(disk1, SectorOrderKind.Cpm3);
        var disk = new Apple2DiskII(drive1);
        var videx = new VidexVideoterm(charRom: null, videxFirmware);   // null -> VidexFont.Fallback (OCR oracle)
        var iou = new Apple2Iou(state, lc, disk, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx,
            controlPortBase: SoftCardBoard.ControlPortBaseSlot4);
        Machine machine = BoardMachineFactory.Build(spec);
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state);

        var mux = new DisplayMultiplexer([video, videx], initialActive: 0);
        videx.ActiveChanged += active => mux.SetActive(active ? 1 : 0);

        machine.Reset();
        machine.Run(12_000_000L);

        Assert.Equal(1, mux.ActiveIndex);   // the Videx auto-engaged (the 80-col terminal is the live source)

        // --- Render the live 80x24 frame the browser streams (the SAME RenderInto path the FB frame uses) ---
        int width = videx.Width, height = videx.Height;
        Assert.Equal(80 * VidexFont.CellWidth, width);   // 560
        Assert.Equal(24 * 9, height);                    // 216
        var rgba = new uint[width * height];
        videx.RenderInto(rgba);

        // --- OCR the rendered RGBA back to a text grid by matching each cell bitmap against the synthetic font.
        // distinctCells counts the distinct cell BITMAPS in the rendered frame -- the un-fakeable discriminator
        // (the scanout bug yields exactly 1; a real console paints dozens). ---
        (string[] grid, int distinctCells) = DecodeRenderedGrid(rgba, width, height);
        string console = string.Join("\n", grid).TrimEnd();

        // (1) MANY distinct cell bitmaps -- the discriminator that fails red on the one-bank bug (1) and passes
        //     green after the fix. A legible 80-col CP/M-3 console paints well over a dozen distinct glyphs.
        Assert.True(distinctCells > 12,
            $"expected many distinct cell bitmaps in the streamed RGBA (a legible console); observed "
          + $"{distinctCells}. The one-bank scanout bug tiles ONE glyph across all 1920 cells (distinct==1) -- "
          + $"the full-2-KiB-scanout fix paints the real console. Decoded frame:\n{console}");

        // (2) The CP/M-3 sign-on is LEGIBLE in the STREAMED pixels (not just the VRAM) -- the oracle the old
        //     VRAM gates never checked.
        Assert.True(
            console.Contains("CP/M Version 3.0", StringComparison.Ordinal),
            $"expected the CP/M-3 sign-on decoded from the streamed RGBA; decoded frame:\n{console}");
        Assert.Contains("BIOS", console, StringComparison.Ordinal);

        // (3) The headline: the `A>` CCP prompt is LEGIBLE in the streamed pixels -- what the browser must show.
        Assert.True(
            console.Contains("A>", StringComparison.Ordinal),
            $"expected the decoded `A>` CCP prompt in the streamed RGBA (the un-fakeable browser arbiter); "
          + $"decoded frame:\n{console}");
    }

    /// <summary>OCR an 80x24 monochrome RGBA Videx frame back into a text grid: for each cell, build its
    /// 7-wide x cellLines-tall on/off bitmap from the RGBA, then match it against every char code's bitmap
    /// rendered through the SAME synthetic char ROM (bit 6 leftmost, 8 active glyph rows + blank descenders --
    /// the VidexVideoterm.RenderInto convention). Returns the 24 decoded rows plus the count of DISTINCT cell
    /// bitmaps in the frame (the un-fakeable discriminator).</summary>
    private static (string[] rows, int distinctCells) DecodeRenderedGrid(uint[] rgba, int width, int height)
    {
        byte[] charRom = VidexFont.Fallback;
        int cols = width / VidexFont.CellWidth;        // 80
        int cellLines = height / 24;                    // 9
        int rowsCount = height / cellLines;             // 24

        // Build the bitmap->code lookup once. The synthetic font has bit-6-leftmost glyph-major glyphs with a
        // blank $20 (space) and distinct $21-$7E patterns. Insert ASCENDING, keep the FIRST writer, so the
        // canonical low code wins a shared bitmap (blank -> $00, then masked to a space at decode time).
        var codeForBitmap = new Dictionary<string, int>();
        for (int code = 0x00; code <= 0xFF; code++)
            codeForBitmap.TryAdd(GlyphBitmap(code, cellLines, charRom), code);

        var distinct = new HashSet<string>();
        var rows = new string[rowsCount];
        for (int r = 0; r < rowsCount; r++)
        {
            var line = new StringBuilder(cols);
            for (int c = 0; c < cols; c++)
            {
                string bmp = CellBitmap(rgba, width, c, r, cellLines);
                distinct.Add(bmp);
                int code = (codeForBitmap.TryGetValue(bmp, out int hit) ? hit : 0x00) & 0x7F;   // 7-bit ASCII
                line.Append(code is >= 0x20 and <= 0x7E ? (char)code : ' ');
            }
            rows[r] = line.ToString().TrimEnd();
        }
        return (rows, distinct.Count);
    }

    /// <summary>The on/off bitmap of cell (c,r) read out of the rendered RGBA (MonoOn -> '1').</summary>
    private static string CellBitmap(uint[] rgba, int width, int c, int r, int cellLines)
    {
        var sb = new StringBuilder(VidexFont.CellWidth * cellLines);
        for (int gy = 0; gy < cellLines; gy++)
            for (int gx = 0; gx < VidexFont.CellWidth; gx++)
            {
                int px = c * VidexFont.CellWidth + gx;
                int py = r * cellLines + gy;
                sb.Append(rgba[py * width + px] == Apple2Palette.MonoOn ? '1' : '0');
            }
        return sb.ToString();
    }

    /// <summary>The expected on/off bitmap a char code paints through the char ROM (the RenderInto rule:
    /// bit 6 leftmost, 8 active glyph rows, the rest blank descender lines).</summary>
    private static string GlyphBitmap(int code, int cellLines, byte[] charRom)
    {
        int glyphBase = (code & 0xFF) * VidexFont.GlyphRows;
        var sb = new StringBuilder(VidexFont.CellWidth * cellLines);
        for (int gy = 0; gy < cellLines; gy++)
        {
            byte rowBits = gy < VidexFont.GlyphRows ? charRom[glyphBase + gy] : (byte)0x00;
            for (int gx = 0; gx < VidexFont.CellWidth; gx++)
                sb.Append((rowBits & (0x40 >> gx)) != 0 ? '1' : '0');
        }
        return sb.ToString();
    }
}
