using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

/// <summary>The un-fakeable gate for the apl2cpm3 80-col CP/M-3 SCROLL COLUMN-SHIFT bug (the Videx
/// read-path bank-select fix). CONFIRMED a real bug by a MAME 0.287 reference run of the identical
/// apl2cpm3 software: after 30/60 Returns at `A&gt;`, MAME renders the prompts as a CLEAN, perfectly
/// column-aligned vertical stack — zero horizontal shift. CpuEmulator sheared them (older prompts at one
/// column, newer prompts at another) once the console scrolled past the 512-byte bank-0 boundary.
///
/// ROOT CAUSE (traced live + MAME-anchored, NOT the scanout base): the Videx firmware (ROM 2.4) banks its
/// 2 KiB VRAM into the 512-byte $CC00 window by READING $C0B0/$C0B4/$C0B8/$C0BC (an `LDA $C0B0,X`, helper
/// $CA59/$CA69) before each character store — the high address bits pick the bank. Our $C0Bx handler only
/// bank-selected on WRITES (and only on $C0B8-$C0BF), so the firmware's bank-select READ was a no-op and
/// `_bank` froze at 0: every character piled into bank 0 while the 6845 scanout (correctly) read the full
/// 2 KiB. Once the scroll base advanced past 512, scanout read empty banks 1-3 and the columns sheared.
/// MAME emits the IDENTICAL R12/R13 free-run-to-2048 start-address sequence we do (measured via an I/O tap:
/// boot 960, then +80/Return wrapping mod 2048 — 1920, 2000, 32, ...), so the divergence was never the
/// start address. The fix (MAME a2videoterm read_c0nx/write_c0nx): EVERY $C0Bx access — read or write, any
/// offset — sets bank = (offset&gt;&gt;2)&amp;3.
///
/// THIS gate drives the real apl2cpm3 boot, posts 30 Returns at `A&gt;` (the same keystrokes the MAME
/// reference used), renders the LIVE frame through <see cref="VidexVideoterm.RenderInto"/> (the scanout
/// path, NOT the raw VRAM), OCRs the 80x24 grid against the deterministic synthetic <see cref="VidexFont"/>
/// (the char ROM governs only render pixels, never boot logic — so the VRAM is identical to a real-char-ROM
/// boot and the pixels OCR exactly), finds every row whose first ink is the `A&gt;` prompt, and asserts
/// EVERY such prompt's first-ink column is identical (with at least 3 stacked). It FAILS red on current main
/// (the bank-0-pileup shear: prompts split across two columns) and PASSES green after the read-path
/// bank-select fix (a single column for the whole stack — MAME's clean scroll).</summary>
public class VidexScrollColumnAlignmentTests
{
    [Apl2Cpm3VidexFact]
    public void Scrolling_30_returns_keeps_every_A_prompt_in_one_column_like_MAME()
    {
        var (systemRomPath, disk1Path, videxFirmware, _) =
            Apl2Cpm3Vectors.TryGetVidexAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the apl2cpm3 scroll gate");
        IBlockDevice disk1 = Apl2Cpm3.LoadBootDisk(disk1Path);

        // Build the SoftCard+Videx board exactly as the sibling render gate (slot 4, real firmware), but
        // render through the SYNTHETIC font so the streamed pixels OCR exactly (the char ROM is host-side
        // render data only -- the guest never reads it, so the VRAM content is identical to a real boot).
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(disk1, SectorOrderKind.Cpm3);
        var disk = new Apple2DiskII(drive1);
        var videx = new VidexVideoterm(charRom: null, videxFirmware);
        var iou = new Apple2Iou(state, lc, disk, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx,
            controlPortBase: SoftCardBoard.ControlPortBaseSlot4);
        Machine machine = BoardMachineFactory.Build(spec);
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state);

        var mux = new DisplayMultiplexer([video, videx], initialActive: 0);
        videx.ActiveChanged += active => mux.SetActive(active ? 1 : 0);

        machine.Reset();
        machine.Run(12_000_000L);
        Assert.Equal(1, mux.ActiveIndex);   // the Videx auto-engaged (the 80-col terminal is live)

        // Post 30 Returns at the A> prompt -- the exact stimulus the MAME reference used. Each Return: latch
        // the carriage-return key code ($0D) at $C000, then run a slice so the CP/M-3 console reads the key,
        // echoes a fresh `A>` prompt, and scrolls (the firmware advances the 6845 start address by one row).
        const int returns = 30;
        for (int i = 0; i < returns; i++)
        {
            state.LatchKey(0x0D);
            machine.Run(400_000L);
        }

        // Render the live 80x24 frame the browser streams (the SAME RenderInto scanout path), then OCR it.
        int width = videx.Width, height = videx.Height;
        Assert.Equal(80 * VidexFont.CellWidth, width);   // 560
        Assert.Equal(24 * 9, height);                    // 216
        var rgba = new uint[width * height];
        videx.RenderInto(rgba);
        string[] grid = DecodeRenderedGrid(rgba, width, height);
        string frame = string.Join("\n", grid).TrimEnd();

        // Collect the first-ink column of every row whose prompt is `A>` (the CCP prompt -- 'A' then '>').
        // A clean scroll stacks them in ONE column; the shear bug splits them across two.
        var promptColumns = new List<int>();
        foreach (string row in grid)
        {
            int firstInk = -1;
            for (int c = 0; c < row.Length; c++)
            {
                if (row[c] != ' ') { firstInk = c; break; }
            }
            if (firstInk >= 0 && firstInk + 1 < row.Length
                && row[firstInk] == 'A' && row[firstInk + 1] == '>')
            {
                promptColumns.Add(firstInk);
            }
        }

        // There must be a real stack of prompts to make the alignment assertion meaningful (30 Returns paint
        // far more than 3; this guards against a vacuous pass if the boot regressed and printed nothing).
        Assert.True(promptColumns.Count >= 3,
            $"expected at least 3 stacked `A>` prompts after {returns} Returns (a meaningful column-alignment "
          + $"sample); found {promptColumns.Count}. Rendered frame:\n{frame}");

        // THE HEADLINE: every `A>` prompt sits in the SAME first-ink column -- MAME's clean column-aligned
        // scroll. On current main the read-path bank-select is missing, so the console shears across the
        // 512-byte bank-0 boundary and the prompts split into (at least) two distinct columns -> RED. After
        // the fix the whole stack shares one column -> GREEN.
        int distinctColumns = promptColumns.Distinct().Count();
        Assert.True(distinctColumns == 1,
            $"expected EVERY `A>` prompt in ONE column (MAME's clean scroll); found {distinctColumns} distinct "
          + $"columns: [{string.Join(", ", promptColumns.Distinct().OrderBy(x => x))}] across "
          + $"{promptColumns.Count} prompts. The shear means the console scrolled past the 512-byte bank-0 "
          + $"boundary and scanout read empty banks 1-3 (the missing read-path bank-select). Rendered frame:\n{frame}");
    }

    /// <summary>OCR an 80x24 monochrome RGBA Videx frame into a text grid by matching each cell bitmap
    /// against the synthetic font (the VidexRenderedConsoleTests convention: bit 6 leftmost, 8 active glyph
    /// rows + blank descenders). Rows are NOT trimmed-right here so a prompt's column index is preserved.</summary>
    private static string[] DecodeRenderedGrid(uint[] rgba, int width, int height)
    {
        byte[] charRom = VidexFont.Fallback;
        int cols = width / VidexFont.CellWidth;        // 80
        int cellLines = height / 24;                    // 9
        int rowsCount = height / cellLines;             // 24

        var codeForBitmap = new Dictionary<string, int>();
        for (int code = 0x00; code <= 0xFF; code++)
            codeForBitmap.TryAdd(GlyphBitmap(code, cellLines, charRom), code);

        var rows = new string[rowsCount];
        for (int r = 0; r < rowsCount; r++)
        {
            var line = new StringBuilder(cols);
            for (int c = 0; c < cols; c++)
            {
                string bmp = CellBitmap(rgba, width, c, r, cellLines);
                int code = (codeForBitmap.TryGetValue(bmp, out int hit) ? hit : 0x00) & 0x7F;   // 7-bit ASCII
                line.Append(code is >= 0x20 and <= 0x7E ? (char)code : ' ');
            }
            rows[r] = line.ToString();
        }
        return rows;
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
