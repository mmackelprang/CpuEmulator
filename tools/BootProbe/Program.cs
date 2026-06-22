using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

// --- CP/M screenshot mode (the human-visible A> proof for the morning report) ----------------------
//   tools/BootProbe --cpm-screenshot <out.png> [diskPath]
// Boots the real SoftCard CP/M board (the SAME wiring as the Cpm_boots_to_the_A_prompt gate), renders
// the 40-col Apple text frame to RGBA, and writes it to a PNG. Defaults the disk to the cached
// softcard-cpm.dsk. This is a dev tool, not a gate — the headless test is the arbiter, this is proof.
if (args.Length >= 2 && args[0] == "--cpm-screenshot")
{
    CpmScreenshot.Run(args[1], args.Length >= 3 ? args[2] : null);
    return;
}

// Boots the Apple ][+ in BOTH board configurations and dumps the live text-page screen +
// the exact MonoOn ink-pixel count (the same metric the headless gate asserts on).
//
//   Config A = the HEADLESS TEST board: SpecWithSystem + a fake 256B boot ROM carrying the
//              slot-6 disk signature ($Cn01=$20,$Cn03=$00,$Cn05=$03,$Cn07=$3C). The cold
//              Autostart scan FINDS slot 6 and JMP ($C600)s into this non-functional ROM.
//   Config B = the WEB-SURFACE board (boot ROM absent): SpecWithDiskII, NO $C600 window. The
//              cold scan finds no bootable slot and falls through to the Applesoft ] prompt.
//
// This reproduces, headless, exactly why the live surface shows a clean ] prompt while the
// gate sees only ~40 ink pixels.

string romPath = Apple2Rom.TryGetPath()
    ?? throw new InvalidOperationException("apple2plus.rom not cached");
byte[] systemRom = Apple2Rom.Load(romPath);
byte[]? charRom = Apple2Rom.TryLoadCharRom();   // null in this session -> Fallback font
Console.WriteLine($"system ROM: {systemRom.Length} B   charRom: {(charRom is null ? "null (Fallback font)" : "present")}");
Console.WriteLine($"reset vector $FFFC/$FFFD -> ${systemRom[0x2FFD]:X2}{systemRom[0x2FFC]:X2}");
Console.WriteLine();

static byte[] FakeDiskBootRom()
{
    var rom = new byte[Apple2Rom.DiskRomLength];   // 256 B
    rom[0x00] = 0xA9;                                // LDA #  (recognizable first opcode)
    rom[0x01] = 0x20; rom[0x03] = 0x00; rom[0x05] = 0x03; rom[0x07] = 0x3C;  // slot-6 signature
    return rom;
}

long BootCycles = 500_000;   // the gate's window (overridable per probe)

void Probe(string name, bool withFakeBootRom)
{
    var state = new Apple2VideoState();
    var lc = new Apple2LanguageCard(systemRom);
    var image = new SyntheticFluxImage(trackCount: 35);
    var disk = new Apple2DiskII(image);
    var iou = new Apple2Iou(state, lc, disk);

    BoardSpec spec = withFakeBootRom
        ? Apple2Board.SpecWithSystem(systemRom, iou, disk, FakeDiskBootRom())   // the TEST board
        : Apple2Board.SpecWithDiskII(systemRom, iou, disk);                     // the SURFACE board

    Machine machine = BoardMachineFactory.Build(spec, ExecutionTier.Interpreter);
    var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state, charRom);
    machine.Reset();
    machine.Run(BootCycles);

    // Render + count ink the way the gate does.
    var rgba = new uint[Apple2Video.Width280 * Apple2Video.Height192];
    video.RenderInto(rgba);
    int on = 0, off = 0;
    foreach (uint p in rgba) { if (p == Apple2Palette.MonoOn) on++; else if (p == Apple2Palette.MonoOff) off++; }

    // Dump the live text page as ASCII (strip the high bit; show printable, '.' for space, '?' for ctrl).
    IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
    var sb = new StringBuilder();
    int nonBlankCells = 0;
    for (int r = 0; r < 24; r++)
    {
        uint rowBase = Apple2HiResAddress.TextRowBase(r, page2: false);
        sb.Append($"  r{r,2} |");
        for (int c = 0; c < 40; c++)
        {
            byte b = bus.Read8(rowBase + (uint)c);
            int g = b & 0x7F;
            char ch = (g >= 0x20 && g <= 0x7E) ? (char)g : (g == 0x00 ? '@' : '?');
            if (g != 0x20 && !(g == 0x00)) nonBlankCells++;
            else if (g == 0x00) { /* $00 = inverse @ -> often the cleared-but-inverse fill */ }
            sb.Append(ch == ' ' ? '.' : ch);
        }
        sb.Append("|\n");
    }

    var hashBytes = new byte[rgba.Length * 4];
    Buffer.BlockCopy(rgba, 0, hashBytes, 0, hashBytes.Length);
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(hashBytes));

    Console.WriteLine($"=== {name} (withFakeBootRom={withFakeBootRom}) ===");
    Console.WriteLine($"  mode: {video.ModeLabel}");
    Console.WriteLine($"  MonoOn (ink) pixels = {on}   MonoOff = {off}   (gate threshold: onPixels > 100)");
    Console.WriteLine($"  RGBA frame SHA-256 = {hash}");
    Console.WriteLine($"  non-blank text cells (raw, high-bit stripped) = {nonBlankCells}");
    Console.WriteLine("  live text page ($0400, page 1) — '.'=space  '@'=$00  printable shown literally:");
    Console.Write(sb.ToString());

    // Raw hex of every non-blank cell ($A0 = a normal space with the high "normal-video" bit set;
    // Applesoft writes characters with bit7 SET in normal video, so a real ']' = $DD, '>' = $BE, etc.)
    Console.WriteLine("  non-blank cells (raw byte @ row,col):");
    for (int r = 0; r < 24; r++)
    {
        uint rowBase = Apple2HiResAddress.TextRowBase(r, page2: false);
        for (int c = 0; c < 40; c++)
        {
            byte b = bus.Read8(rowBase + (uint)c);
            int g = b & 0x7F;
            if (g != 0x20)   // anything that isn't a normal space
            {
                char asc = (g >= 0x20 && g <= 0x7E) ? (char)g : '?';
                Console.WriteLine($"    [r{r,2},c{c,2}] raw=${b:X2}  &7F=${g:X2} '{asc}'  (hi-bit {(b >= 0x80 ? "set=normal" : "clear=inv/flash")})");
            }
        }
    }
    Console.WriteLine();
}

Probe("CONFIG A: HEADLESS-TEST board (fake boot ROM, slot-6 sig present)", withFakeBootRom: true);
Probe("CONFIG B: WEB-SURFACE board (no boot ROM -> SpecWithDiskII)", withFakeBootRom: false);
BootCycles = 5_000_000;   // ~5 M cycles: well past any settling
Probe("CONFIG B-long: WEB-SURFACE board, 5M cycles (does '>' settle to ']'?)", withFakeBootRom: false);

// Decode a known reference: what byte does the ROM use for ']' and '>'? The Applesoft prompt is ']'
// ($DD = 0x5D|0x80) and the Monitor prompt is '*' ($AA). Print the ASCII map for clarity.
Console.WriteLine("reference: ']' normal-video = $DD (0x5D|0x80);  '>' = $BE;  '*' (Monitor) = $AA;  '@' inverse = $00");

/// <summary>Boots the real SoftCard CP/M board and writes the 40-col A&gt; boot frame to a PNG — the
/// human-visible proof for the CPM-4 deliverable. Mirrors the Cpm_boots_to_the_A_prompt gate's wiring.</summary>
internal static class CpmScreenshot
{
    public static void Run(string outPath, string? diskPath)
    {
        const long CpmBootCycles = 10_000_000;   // the gate's budget — the screen is settled well before this

        string romPath = Apple2Rom.TryGetPath()
            ?? throw new InvalidOperationException("apple2plus.rom not cached — run tools/get-apple2-roms");
        byte[] systemRom = Apple2Rom.Load(romPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("disk2.rom (slot-6 boot ROM) not cached");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();   // null -> Apple2Font.Fallback (still renders A>)

        string disk = diskPath ?? SoftCardCpm.TryGetDiskPath()
            ?? throw new InvalidOperationException("softcard-cpm.dsk not cached — run tools/get-softcard-cpm");
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(disk);

        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
        var disk2 = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk2);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk2, diskBootRom);
        Machine machine = BoardMachineFactory.Build(spec);   // interpreter tier (the coprocessor is interpreter)
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state, charRom);

        machine.Reset();
        machine.Run(CpmBootCycles);                          // the real $C600 -> tracks -> $CnXX -> CP/M boot

        // Echo the decoded console so the run output proves the A> landed (the same oracle the gate asserts).
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
        Console.WriteLine($"CoprocessorActive = {machine.CoprocessorActive}");
        Console.WriteLine("decoded 40-col console (high-bit stripped):");
        bool sawPrompt = false;
        for (int r = 0; r < 24; r++)
        {
            uint rowBase = Apple2HiResAddress.TextRowBase(r, page2: false);
            var sb = new StringBuilder(40);
            for (int c = 0; c < 40; c++)
            {
                int g = bus.Read8(rowBase + (uint)c) & 0x7F;
                sb.Append(g is >= 0x20 and <= 0x7E ? (char)g : ' ');
            }
            string line = sb.ToString();
            if (line.Contains("A>")) sawPrompt = true;
            Console.WriteLine($"  r{r,2} |{line}|");
        }
        Console.WriteLine($"\"A>\" present = {sawPrompt}");

        var rgba = new uint[Apple2Video.Width280 * Apple2Video.Height192];
        video.RenderInto(rgba);

        // Upscale 2x so the 280x192 frame is comfortably readable as a PNG.
        WritePng(outPath, rgba, Apple2Video.Width280, Apple2Video.Height192, scale: 2);
        Console.WriteLine($"wrote {outPath} ({Apple2Video.Width280 * 2}x{Apple2Video.Height192 * 2})");
    }

    /// <summary>Minimal RGBA8 PNG encoder (one IDAT, zlib/deflate). rgba is 0xAARRGGBB packed.</summary>
    private static void WritePng(string path, uint[] rgba, int width, int height, int scale)
    {
        int w = width * scale, h = height * scale;
        // Build raw scanlines: each row prefixed by a 0 filter byte; pixels as R,G,B,A.
        var raw = new byte[h * (1 + w * 4)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;   // filter type 0 (None)
            int srcY = y / scale;
            for (int x = 0; x < w; x++)
            {
                uint p = rgba[srcY * width + (x / scale)];
                raw[o++] = (byte)(p >> 16);   // R
                raw[o++] = (byte)(p >> 8);    // G
                raw[o++] = (byte)p;           // B
                raw[o++] = (byte)(p >> 24);   // A
            }
        }

        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });   // PNG signature

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, (uint)w);
        WriteBe(ihdr, 4, (uint)h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type 6 = RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;   // deflate / no filter / no interlace
        WriteChunk(fs, "IHDR", ihdr);

        WriteChunk(fs, "IDAT", Zlib(raw));
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x01);   // zlib header (CM=8, no preset dict, fastest)
        using (var df = new System.IO.Compression.DeflateStream(
                   ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            df.Write(data, 0, data.Length);
        uint adler = Adler32(data);
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));  ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        foreach (byte d in data) { a = (a + d) % Mod; b = (b + a) % Mod; }
        return (b << 16) | a;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
        var crcb = new byte[4];
        WriteBe(crcb, 0, crc);
        s.Write(crcb, 0, 4);
    }

    private static void WriteBe(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        crc = Crc32Update(crc, type);
        crc = Crc32Update(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Crc32Update(uint crc, byte[] data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc;
    }
}
