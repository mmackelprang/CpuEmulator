using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using CpuEmulator.Surface.Web;
using CpuEmulator.Tests.Apple2;
using Xunit;

namespace CpuEmulator.Tests.Surface;

/// <summary>The CLEAN-LEGIBILITY gate for the live browser stream. The shipped <c>WebApl2Cpm3RenderTests</c>
/// and <c>VidexRenderedConsoleTests</c> proved (a) the VRAM holds the right text and (b) the SYNTHETIC-font
/// scanout decodes — but they do NOT exercise the PRODUCTION render font: the web server passes whatever
/// <c>VidexRom.TryLoadCharRom()</c> returns straight into the Videx, and the live <c>RenderInto</c> streams
/// those glyphs to the browser's <c>FB</c> frame. When that cached char-ROM file is a firmware/garbage dump
/// (a 6502 driver image mis-placed at the char-ROM path — what the owner actually had), the OLD code decoded
/// its bytes as glyphs and the browser showed a STIPPLE FIELD, not a console (this gate's red state).
///
/// THIS gate boots through the PRODUCTION factory <see cref="SoftCardVidexSurface.CreateApl2Cpm3"/> with the
/// REAL cached char-ROM bytes (the exact <c>videxChar</c> the web branch passes), renders the LIVE
/// <see cref="VidexVideoterm.RenderInto"/> frame the browser streams, and asserts the rendered PIXELS are a
/// cleanly-legible CP/M-3 console: the <c>CP/M Version 3.0</c> sign-on + <c>A&gt;</c> + <c>BIOS</c> + the
/// <c>46K TPA</c> line OCR back out of the streamed RGBA, AND the empty rows below the console are ALL-BLACK
/// (no stipple ink). It FAILS red on the garbage-char-ROM stipple (no legible text + inked blank rows) and
/// PASSES green once the invalid char ROM is rejected for the legible synthetic font + the high bit is masked.
/// Gated by <see cref="Apl2Cpm3VidexFactAttribute"/> — asset-free CI skips it cleanly (green).</summary>
public class WebVidexLegibleConsoleTests
{
    [Apl2Cpm3VidexFact]
    public void Browser_stream_renders_a_cleanly_legible_CPM3_console_from_the_production_font()
    {
        var (systemRomPath, disk1Path, videxFirmware, videxCharRom) =
            Apl2Cpm3Vectors.TryGetVidexAssets()!.Value;

        byte[] sys = Apple2Rom.Load(systemRomPath);
        byte[] bootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the legibility gate");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();
        IBlockDevice disk1 = Apl2Cpm3.LoadBootDisk(disk1Path);

        // The PRODUCTION factory — the EXACT call the web server's Apl2Cpm3Videx branch makes, passing the real
        // cached char-ROM bytes (videxCharRom) as videxChar. This is the live browser render path end-to-end.
        var surface = SoftCardVidexSurface.CreateApl2Cpm3(
            sys, bootRom, charRom, videxCharRom, videxFirmware, disk1, _ => { }, _ => { });
        surface.Host.RunHeadless(totalCycles: 12_000_000, sliceCycles: 17_030);

        Assert.Equal(1, surface.Display.ActiveIndex);   // the Videx is the live streamed source

        // Render the EXACT frame the browser receives (the same RenderInto the MachineHost pulls for the FB
        // frame), then OCR it against the legible synthetic font.
        VidexVideoterm videx = surface.Videx;
        int width = videx.Width, height = videx.Height;
        Assert.Equal(80 * VidexFont.CellWidth, width);   // 560
        Assert.Equal(24 * 9, height);                    // 216
        var rgba = new uint[width * height];
        videx.RenderInto(rgba);

        (string[] grid, int cellLines) = DecodeRenderedGrid(rgba, width, height);
        string console = string.Join("\n", grid).TrimEnd();

        // (1) The genuine CP/M-3 sign-on is LEGIBLE in the STREAMED pixels (not just the VRAM).
        Assert.True(console.Contains("CP/M Version 3.0", StringComparison.Ordinal),
            $"expected the legible CP/M-3 sign-on decoded from the browser-streamed RGBA; decoded frame:\n{console}");
        Assert.Contains("BIOS", console, StringComparison.Ordinal);
        Assert.Contains("46K TPA", console, StringComparison.Ordinal);

        // (2) The headline: the `A>` CCP prompt is LEGIBLE in the streamed pixels.
        Assert.True(console.Contains("A>", StringComparison.Ordinal),
            $"expected the decoded `A>` CCP prompt in the browser-streamed RGBA; decoded frame:\n{console}");

        // (3) NO STIPPLE: every row that decodes to EMPTY text must be entirely BLACK in the streamed pixels.
        //     A clean terminal inks only the cells that carry a glyph; a stipple field inks blank cells too.
        //     This is position-independent (the CRTC start-address scanout places the sign-on mid-screen) and
        //     the un-fakeable "clean terminal" discriminator the garbage-char-ROM render fails — it inks the
        //     ~17 blank rows. We require the MAJORITY of rows to be blank (a 7-line console on a 24-row screen)
        //     AND every blank-text row to carry ZERO ink.
        int blankRows = 0, stippledBlankRows = 0;
        long inkInBlankRows = 0;
        for (int r = 0; r < grid.Length; r++)
        {
            if (grid[r].Length != 0) continue;   // a row with decoded text — skip (it legitimately carries ink)
            blankRows++;
            long rowInk = 0;
            for (int gy = 0; gy < cellLines; gy++)
                for (int px = 0; px < width; px++)
                    if (rgba[(r * cellLines + gy) * width + px] == Apple2Palette.MonoOn) rowInk++;
            if (rowInk != 0) stippledBlankRows++;
            inkInBlankRows += rowInk;
        }
        Assert.True(blankRows >= 16,
            $"expected most of the 24 rows blank for a 7-line CP/M-3 console; only {blankRows} decoded blank. "
          + $"Decoded frame:\n{console}");
        Assert.True(inkInBlankRows == 0,
            $"expected every blank-text row to be ALL-BLACK in the streamed RGBA (a clean terminal); observed "
          + $"{inkInBlankRows} inked pixels across {stippledBlankRows} 'blank' rows — a stipple field. "
          + $"Decoded frame:\n{console}");
    }

    /// <summary>OCR an 80x24 monochrome RGBA Videx frame into a text grid by matching each cell bitmap against
    /// the legible synthetic font (the same glyph convention RenderInto uses: bit 6 leftmost, 8 active rows +
    /// blank descenders, high bit masked). Returns the 24 rows + the per-cell line height.</summary>
    private static (string[] rows, int cellLines) DecodeRenderedGrid(uint[] rgba, int width, int height)
    {
        byte[] charRom = VidexFont.Fallback;
        int cols = width / VidexFont.CellWidth;
        int cellLines = height / 24;
        int rowsCount = height / cellLines;

        var codeForBitmap = new Dictionary<string, int>();
        for (int code = 0x20; code <= 0x7E; code++)
            codeForBitmap.TryAdd(GlyphBitmap(code, cellLines, charRom), code);

        var rows = new string[rowsCount];
        for (int r = 0; r < rowsCount; r++)
        {
            var line = new StringBuilder(cols);
            for (int c = 0; c < cols; c++)
            {
                string bmp = CellBitmap(rgba, width, c, r, cellLines);
                int code = codeForBitmap.TryGetValue(bmp, out int hit) ? hit : 0x20;
                line.Append(code is >= 0x20 and <= 0x7E ? (char)code : ' ');
            }
            rows[r] = line.ToString().TrimEnd();
        }
        return (rows, cellLines);
    }

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

    private static string GlyphBitmap(int code, int cellLines, byte[] charRom)
    {
        int glyphBase = (code & 0x7F) * VidexFont.GlyphRows;
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
