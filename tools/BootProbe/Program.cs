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

// --- Videx 80-col CP/M discovery (ADR 0017 Decision 6/7 / OQ2) -------------------------------------
//   tools/BootProbe --videx-discover <diskPath> [out.png]
// Boots the given CP/M master on the SoftCard + Videx board (Videx in the slot) and reports whether the
// master auto-engages the Videx: it counts $C0Bx CRTC accesses, reports whether the Videx ActiveChanged
// signal fired (the DisplayMultiplexer would switch to index 1), dumps the 40-col Apple console + whether
// the Videx VRAM ever took content, and optionally screenshots the active display. Discovery, not a gate.
if (args.Length >= 2 && args[0] == "--videx-discover")
{
    VidexDiscover.Run(args[1], args.Length >= 3 ? args[2] : null);
    return;
}

// --- apl2cpm3 CP/M 3.1 Videx 80-col boot screenshot (V80-2/V80-3, ADR 0018) ------------------------
//   tools/BootProbe --apl2cpm3-videx <out.png>
// Boots the REAL apl2cpm3 Disk 1 on the SoftCard+Videx board at slot 4 (controlPortBase $C400) with the
// SectorOrderKind.Cpm3 raw-DOS33 skew (ADR 0018-A) and the REAL Videx firmware + char ROM, then renders the
// Videx 80x24 active source to a PNG -- the human-visible proof that apl2cpm3's CRT80 console (the genuine
// "CP/M Version 3.0, 56K BIOS R6/89" sign-on) paints on the Videx via the real $C800 firmware. The headless
// gate (Apl2Cpm3BootTests) is the arbiter; this is the owner-UAT artifact. (The boot renders the CP/M-3
// sign-on but does not reach `A>` -- a fifth layer in the banked BDOS/CCP execution; see the gate comment.)
if (args.Length >= 2 && args[0] == "--apl2cpm3-videx")
{
    Apl2Cpm3VidexShot.Run(args[1]);
    return;
}

// --- Direct Videx 80x24 render (the asset-free proof, ADR 0017 Decision 6) -------------------------
//   tools/BootProbe --videx-80col-render <out.png>
// Programs the Videx CRTC for 80x24 through the real SoftCardVidexBoard bus ($C0B0/$C0B1), writes a CP/M
// sign-on into the $CC00 VRAM window via the bus, and renders the Videx 80x24 frame to a PNG. This is the
// direct-render proof (the Videx renders 80x24 from VRAM) the VidexVideotermTests gate asserts, made
// human-visible. No copyrighted 80-col CP/M master needed.
if (args.Length >= 2 && args[0] == "--videx-80col-render")
{
    VidexDiscover.RenderDirect80x24(args[1]);
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
        ms.WriteByte(0x78); ms.WriteByte(0x01);   // zlib header (CM=8/32K window; FLEVEL informational, divisible-by-31)
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

    // Exposed so VidexDiscover reuses the exact PNG path.
    internal static void WritePngScaled(string path, uint[] rgba, int width, int height, int scale)
        => WritePng(path, rgba, width, height, scale);
}

/// <summary>ADR 0017 Decision 6/7 / OQ2 discovery: boots a CP/M master on the SoftCard + Videx board and
/// reports whether it auto-engages the Videx 80-col card (the ActiveChanged signal fires + VRAM takes
/// content) or stays a 40-col console (the cached master's behavior). Discovery, not a gate.</summary>
internal static class VidexDiscover
{
    public static void Run(string diskPath, string? outPng)
    {
        const long CpmBootCycles = 10_000_000;

        byte[] systemRom = Apple2Rom.Load(Apple2Rom.TryGetPath()
            ?? throw new InvalidOperationException("apple2plus.rom not cached"));
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("disk2.rom not cached");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(diskPath);

        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx);
        Machine machine = BoardMachineFactory.Build(spec);
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state, charRom);

        // The DisplayMultiplexer auto-switch the surface wires: ActiveChanged -> SetActive (index 1 = Videx).
        var mux = new DisplayMultiplexer([video, videx], initialActive: 0);
        int activeChangedCount = 0;
        bool videxEverEngaged = false;
        videx.ActiveChanged += active =>
        {
            activeChangedCount++;
            if (active) videxEverEngaged = true;
            mux.SetActive(active ? 1 : 0);
        };

        machine.Reset();
        machine.Run(CpmBootCycles);

        // Decode the 40-col Apple console (the same oracle as the SoftCard gate).
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
        bool sawPromptApple = false;
        Console.WriteLine($"=== {Path.GetFileName(diskPath)} ===");
        Console.WriteLine($"  CoprocessorActive      = {machine.CoprocessorActive}");
        Console.WriteLine($"  Videx ActiveChanged    = {activeChangedCount} event(s), engaged={videxEverEngaged}");
        Console.WriteLine($"  DisplayMux ActiveIndex = {mux.ActiveIndex}   (0=Apple-40, 1=Videx-80)");

        Console.WriteLine("  decoded 40-col Apple console (high-bit stripped):");
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
            if (line.Contains("A>")) sawPromptApple = true;
            if (line.TrimEnd().Length > 0) Console.WriteLine($"    r{r,2} |{line}|");
        }
        Console.WriteLine($"  \"A>\" on 40-col Apple   = {sawPromptApple}");

        string verdict = videxEverEngaged
            ? "VIDEX ENGAGED (80-col candidate!)"
            : "40-col only (no Videx engagement)";
        Console.WriteLine($"  VERDICT: {verdict}");
        Console.WriteLine();

        if (outPng is not null)
        {
            // Render the active display source (Videx if engaged, else the Apple 40-col).
            int w = mux.Width, h = mux.Height;
            var rgba = new uint[w * h];
            mux.RenderInto(rgba);
            CpmScreenshot.WritePngScaled(outPng, rgba, w, h, scale: 2);
            Console.WriteLine($"  wrote {outPng} ({w * 2}x{h * 2}, active source index {mux.ActiveIndex})");
        }
    }

    /// <summary>The asset-free direct Videx 80x24 render proof (ADR 0017 Decision 6): program the CRTC for
    /// 80x24 + write a CP/M sign-on into VRAM, both through the real SoftCardVidexBoard bus, then render. No
    /// copyrighted 80-col CP/M master is needed — this proves the Videx renders 80x24 from VRAM.</summary>
    public static void RenderDirect80x24(string outPng)
    {
        // A minimal board to host the Videx $C0Bx delegate + the $CC00 VRAM window (no CP/M disk needed).
        var systemRom = new byte[Apple2Rom.SystemRomLength];
        systemRom[0x2FFC] = 0x00; systemRom[0x2FFD] = 0xD0;   // reset -> $D000
        var diskBootRom = new byte[Apple2Rom.DiskRomLength];
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx);
        Machine machine = BoardMachineFactory.Build(spec);
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // Program the standard Videx 80x24 init through the bus (the $C0B0 register-select / $C0B1 data path).
        void SetReg(byte reg, byte val) { bus.Write8(0xC0B0, reg); bus.Write8(0xC0B1, val); }
        SetReg(1, 0x50);   // R1 = 80 chars/row
        SetReg(6, 0x18);   // R6 = 24 displayed rows
        SetReg(9, 0x08);   // R9 = 9 scan lines/char
        SetReg(12, 0x00);  // R12 = start address high
        SetReg(13, 0x00);  // R13 = start address low

        // Write a CP/M-style 80-col sign-on into the active VRAM bank via the $CC00 window (linear cells).
        string[] lines =
        {
            "APPLE ][ CP/M  VER. 2.20B   (C) 1980 MICROSOFT   [VIDEX 80-COLUMN VIDEOTERM]",
            "",
            "A>DIR",
            "A: PIP      COM : STAT     COM : ASM      COM : LOAD     COM : ED       COM",
            "A: SUBMIT   COM : XSUB     COM : DDT      COM : DUMP     COM : CONFIGIO COM",
            "",
            "A>",
        };
        void PutCell(int index, byte code) => bus.Write8(0xCC00 + (uint)index, code);
        for (int row = 0; row < lines.Length; row++)
            for (int col = 0; col < lines[row].Length && col < 80; col++)
                PutCell(row * 80 + col, (byte)lines[row][col]);

        int w = videx.Width, h = videx.Height;   // 560 x 216 (80x7 x 24x9)
        var rgba = new uint[w * h];
        videx.RenderInto(rgba);
        CpmScreenshot.WritePngScaled(outPng, rgba, w, h, scale: 1);
        Console.WriteLine($"wrote {outPng} ({w}x{h}) — direct Videx 80x24 render (asset-free proof)");
    }
}

/// <summary>V80-2/V80-3 (ADR 0018): boot the REAL apl2cpm3 Disk 1 on the SoftCard+Videx board at slot 4 with
/// the SectorOrderKind.Cpm3 raw-DOS33 skew + the REAL Videx firmware, and render the Videx 80x24 console to a
/// PNG. The genuine CP/M 3.1 sign-on ("CP/M Version 3.0, 56K BIOS R6/89" / "46K TPA") paints on the Videx via
/// the real $C800 firmware (the synthetic firmware is empty). The headless gate is the arbiter; this is the
/// human-visible UAT artifact. (The boot does not reach `A>` -- a fifth layer; see the gate comment.)</summary>
internal static class Apl2Cpm3VidexShot
{
    public static void Run(string outPng)
    {
        const long CpmBootCycles = 12_000_000L;

        byte[] systemRom = Apple2Rom.Load(Apple2Rom.TryGetPath()
            ?? throw new InvalidOperationException("apple2plus.rom not cached -- run tools/get-apple2-roms"));
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("disk2.rom (slot-6 boot ROM) not cached");
        byte[]? videxCharRom = VidexRom.TryLoadCharRom();
        byte[]? videxFirmware = VidexRom.TryLoadFirmware();
        if (videxFirmware is null)
            throw new InvalidOperationException("the REAL Videx firmware ROM is required -- run tools/get-videx-roms");
        string disk1 = Apl2Cpm3.TryGetBootDiskPath()
            ?? throw new InvalidOperationException("apl2cpm3 Disk 1 not cached -- run tools/get-apl2cpm3");
        IBlockDevice disk = Apl2Cpm3.LoadBootDisk(disk1);

        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(disk, SectorOrderKind.Cpm3);   // ADR 0018-A: raw DOS33 on every track
        var disk2 = new Apple2DiskII(drive1);
        var videx = new VidexVideoterm(videxCharRom, videxFirmware);
        var iou = new Apple2Iou(state, lc, disk2, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk2, diskBootRom, videx,
            controlPortBase: SoftCardBoard.ControlPortBaseSlot4);
        Machine machine = BoardMachineFactory.Build(spec);
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state);

        var mux = new DisplayMultiplexer([video, videx], initialActive: 0);
        int videxEngaged = 0;
        videx.ActiveChanged += active => { if (active) videxEngaged++; mux.SetActive(active ? 1 : 0); };

        machine.Reset();
        machine.Run(CpmBootCycles);

        Console.WriteLine($"CoprocessorActive = {machine.CoprocessorActive}   videxEngaged = {videxEngaged}   ActiveIndex = {mux.ActiveIndex}");

        // Decode the active VRAM bank through the $CC00 window (the firmware painted the sign-on into bank 0,
        // the active bank) and echo it -- the human-readable proof the genuine CP/M-3 console text is on the
        // Videx (the same content the headless gate decodes off PeekVramForTest).
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
        Console.WriteLine("decoded Videx $CC00 VRAM (active bank, 80-col rows, high-bit stripped):");
        for (int row = 0; row * 80 < (int)VidexVideoterm.VramWindowLength; row++)
        {
            var sb = new StringBuilder(80);
            for (int col = 0; col < 80 && row * 80 + col < (int)VidexVideoterm.VramWindowLength; col++)
            {
                int code = bus.Read8(VidexVideoterm.VramWindowBase + (uint)(row * 80 + col)) & 0x7F;
                sb.Append(code is >= 0x20 and <= 0x7E ? (char)code : ' ');
            }
            if (sb.ToString().Trim().Length > 0) Console.WriteLine($"  v{row,2}|{sb.ToString().TrimEnd()}|");
        }

        // Render the human-visible PNG from the DECODED console text (the same $CC00 VRAM the gate's oracle
        // reads, high-bit-stripped to 7-bit codes), drawn with a built-in 8x8 font. The Videx char-ROM glyph
        // render (videx.RenderInto) paints the firmware's $A0 padding cells as filled glyphs -> an illegible
        // noise field; this text-grid render is the legible owner-UAT artifact. SCREENSHOT-ONLY -- the headless
        // gate (PeekVramForTest) is the un-fakeable arbiter; this PNG asserts nothing.
        var grid = new char[24][];
        for (int row = 0; row < 24; row++)
        {
            grid[row] = new char[80];
            for (int col = 0; col < 80; col++)
            {
                int idx = row * 80 + col;
                int code = idx < (int)VidexVideoterm.VramWindowLength
                    ? bus.Read8(VidexVideoterm.VramWindowBase + (uint)idx) & 0x7F : 0x20;
                grid[row][col] = code is >= 0x20 and <= 0x7E ? (char)code : ' ';
            }
        }
        ConsolePng.WriteTextGrid(outPng, grid, scale: 2);
        Console.WriteLine($"wrote {outPng} — apl2cpm3 CP/M 3.1 Videx 80-col console (decoded sign-on, text-grid render)");
    }
}

/// <summary>A dev-tool-only legible text-grid PNG renderer: draws a char grid with a built-in 8x8 font
/// (public-domain font8x8_basic, white ink on black). Used by the apl2cpm3 Videx screenshot so the decoded
/// CP/M-3 console text is human-readable (the Videx char-ROM glyph render paints the firmware's $A0 padding
/// as filled glyphs -> an illegible field). This is NOT a gate -- the headless test is the arbiter.</summary>
internal static class ConsolePng
{
    private const uint Ink = 0xFFFFFFFFu;   // white (0xAARRGGBB)
    private const uint Bg = 0xFF000000u;    // black

    public static void WriteTextGrid(string outPath, char[][] grid, int scale)
    {
        int rows = grid.Length, cols = grid[0].Length;
        int w = cols * 8, h = rows * 8;
        var rgba = new uint[w * h];
        Array.Fill(rgba, Bg);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                byte[] glyph = Glyph(grid[r][c]);
                for (int gy = 0; gy < 8; gy++)
                {
                    byte bits = glyph[gy];
                    for (int gx = 0; gx < 8; gx++)
                        if ((bits & (1 << gx)) != 0)
                            rgba[(r * 8 + gy) * w + (c * 8 + gx)] = Ink;
                }
            }
        CpmScreenshot.WritePngScaled(outPath, rgba, w, h, scale);
    }

    private static byte[] Glyph(char ch)
    {
        int i = ch - 0x20;
        return i >= 0 && i < Font.Length ? Font[i] : Font[0];
    }

    // font8x8_basic (public domain, Daniel Hepper / Marcel Sondaar), ASCII $20-$7E. Each glyph is 8 rows;
    // bit 0 = leftmost column.
    private static readonly byte[][] Font =
    [
        [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00], // (space)
        [0x18,0x3C,0x3C,0x18,0x18,0x00,0x18,0x00], // !
        [0x36,0x36,0x00,0x00,0x00,0x00,0x00,0x00], // "
        [0x36,0x36,0x7F,0x36,0x7F,0x36,0x36,0x00], // #
        [0x0C,0x3E,0x03,0x1E,0x30,0x1F,0x0C,0x00], // $
        [0x00,0x63,0x33,0x18,0x0C,0x66,0x63,0x00], // %
        [0x1C,0x36,0x1C,0x6E,0x3B,0x33,0x6E,0x00], // &
        [0x06,0x06,0x03,0x00,0x00,0x00,0x00,0x00], // '
        [0x18,0x0C,0x06,0x06,0x06,0x0C,0x18,0x00], // (
        [0x06,0x0C,0x18,0x18,0x18,0x0C,0x06,0x00], // )
        [0x00,0x66,0x3C,0xFF,0x3C,0x66,0x00,0x00], // *
        [0x00,0x0C,0x0C,0x3F,0x0C,0x0C,0x00,0x00], // +
        [0x00,0x00,0x00,0x00,0x00,0x0C,0x0C,0x06], // ,
        [0x00,0x00,0x00,0x3F,0x00,0x00,0x00,0x00], // -
        [0x00,0x00,0x00,0x00,0x00,0x0C,0x0C,0x00], // .
        [0x60,0x30,0x18,0x0C,0x06,0x03,0x01,0x00], // /
        [0x3E,0x63,0x73,0x7B,0x6F,0x67,0x3E,0x00], // 0
        [0x0C,0x0E,0x0C,0x0C,0x0C,0x0C,0x3F,0x00], // 1
        [0x1E,0x33,0x30,0x1C,0x06,0x33,0x3F,0x00], // 2
        [0x1E,0x33,0x30,0x1C,0x30,0x33,0x1E,0x00], // 3
        [0x38,0x3C,0x36,0x33,0x7F,0x30,0x78,0x00], // 4
        [0x3F,0x03,0x1F,0x30,0x30,0x33,0x1E,0x00], // 5
        [0x1C,0x06,0x03,0x1F,0x33,0x33,0x1E,0x00], // 6
        [0x3F,0x33,0x30,0x18,0x0C,0x0C,0x0C,0x00], // 7
        [0x1E,0x33,0x33,0x1E,0x33,0x33,0x1E,0x00], // 8
        [0x1E,0x33,0x33,0x3E,0x30,0x18,0x0E,0x00], // 9
        [0x00,0x0C,0x0C,0x00,0x00,0x0C,0x0C,0x00], // :
        [0x00,0x0C,0x0C,0x00,0x00,0x0C,0x0C,0x06], // ;
        [0x18,0x0C,0x06,0x03,0x06,0x0C,0x18,0x00], // <
        [0x00,0x00,0x3F,0x00,0x00,0x3F,0x00,0x00], // =
        [0x06,0x0C,0x18,0x30,0x18,0x0C,0x06,0x00], // >
        [0x1E,0x33,0x30,0x18,0x0C,0x00,0x0C,0x00], // ?
        [0x3E,0x63,0x7B,0x7B,0x7B,0x03,0x1E,0x00], // @
        [0x0C,0x1E,0x33,0x33,0x3F,0x33,0x33,0x00], // A
        [0x3F,0x66,0x66,0x3E,0x66,0x66,0x3F,0x00], // B
        [0x3C,0x66,0x03,0x03,0x03,0x66,0x3C,0x00], // C
        [0x1F,0x36,0x66,0x66,0x66,0x36,0x1F,0x00], // D
        [0x7F,0x46,0x16,0x1E,0x16,0x46,0x7F,0x00], // E
        [0x7F,0x46,0x16,0x1E,0x16,0x06,0x0F,0x00], // F
        [0x3C,0x66,0x03,0x03,0x73,0x66,0x7C,0x00], // G
        [0x33,0x33,0x33,0x3F,0x33,0x33,0x33,0x00], // H
        [0x1E,0x0C,0x0C,0x0C,0x0C,0x0C,0x1E,0x00], // I
        [0x78,0x30,0x30,0x30,0x33,0x33,0x1E,0x00], // J
        [0x67,0x66,0x36,0x1E,0x36,0x66,0x67,0x00], // K
        [0x0F,0x06,0x06,0x06,0x46,0x66,0x7F,0x00], // L
        [0x63,0x77,0x7F,0x7F,0x6B,0x63,0x63,0x00], // M
        [0x63,0x67,0x6F,0x7B,0x73,0x63,0x63,0x00], // N
        [0x1C,0x36,0x63,0x63,0x63,0x36,0x1C,0x00], // O
        [0x3F,0x66,0x66,0x3E,0x06,0x06,0x0F,0x00], // P
        [0x1E,0x33,0x33,0x33,0x3B,0x1E,0x38,0x00], // Q
        [0x3F,0x66,0x66,0x3E,0x36,0x66,0x67,0x00], // R
        [0x1E,0x33,0x07,0x0E,0x38,0x33,0x1E,0x00], // S
        [0x3F,0x2D,0x0C,0x0C,0x0C,0x0C,0x1E,0x00], // T
        [0x33,0x33,0x33,0x33,0x33,0x33,0x3F,0x00], // U
        [0x33,0x33,0x33,0x33,0x33,0x1E,0x0C,0x00], // V
        [0x63,0x63,0x63,0x6B,0x7F,0x77,0x63,0x00], // W
        [0x63,0x63,0x36,0x1C,0x1C,0x36,0x63,0x00], // X
        [0x33,0x33,0x33,0x1E,0x0C,0x0C,0x1E,0x00], // Y
        [0x7F,0x63,0x31,0x18,0x4C,0x66,0x7F,0x00], // Z
        [0x1E,0x06,0x06,0x06,0x06,0x06,0x1E,0x00], // [
        [0x03,0x06,0x0C,0x18,0x30,0x60,0x40,0x00], // backslash
        [0x1E,0x18,0x18,0x18,0x18,0x18,0x1E,0x00], // ]
        [0x08,0x1C,0x36,0x63,0x00,0x00,0x00,0x00], // ^
        [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xFF], // _
        [0x0C,0x0C,0x18,0x00,0x00,0x00,0x00,0x00], // `
        [0x00,0x00,0x1E,0x30,0x3E,0x33,0x6E,0x00], // a
        [0x07,0x06,0x06,0x3E,0x66,0x66,0x3B,0x00], // b
        [0x00,0x00,0x1E,0x33,0x03,0x33,0x1E,0x00], // c
        [0x38,0x30,0x30,0x3E,0x33,0x33,0x6E,0x00], // d
        [0x00,0x00,0x1E,0x33,0x3F,0x03,0x1E,0x00], // e
        [0x1C,0x36,0x06,0x0F,0x06,0x06,0x0F,0x00], // f
        [0x00,0x00,0x6E,0x33,0x33,0x3E,0x30,0x1F], // g
        [0x07,0x06,0x36,0x6E,0x66,0x66,0x67,0x00], // h
        [0x0C,0x00,0x0E,0x0C,0x0C,0x0C,0x1E,0x00], // i
        [0x30,0x00,0x30,0x30,0x30,0x33,0x33,0x1E], // j
        [0x07,0x06,0x66,0x36,0x1E,0x36,0x67,0x00], // k
        [0x0E,0x0C,0x0C,0x0C,0x0C,0x0C,0x1E,0x00], // l
        [0x00,0x00,0x33,0x7F,0x7F,0x6B,0x63,0x00], // m
        [0x00,0x00,0x1F,0x33,0x33,0x33,0x33,0x00], // n
        [0x00,0x00,0x1E,0x33,0x33,0x33,0x1E,0x00], // o
        [0x00,0x00,0x3B,0x66,0x66,0x3E,0x06,0x0F], // p
        [0x00,0x00,0x6E,0x33,0x33,0x3E,0x30,0x78], // q
        [0x00,0x00,0x3B,0x6E,0x66,0x06,0x0F,0x00], // r
        [0x00,0x00,0x3E,0x03,0x1E,0x30,0x1F,0x00], // s
        [0x08,0x0C,0x3E,0x0C,0x0C,0x2C,0x18,0x00], // t
        [0x00,0x00,0x33,0x33,0x33,0x33,0x6E,0x00], // u
        [0x00,0x00,0x33,0x33,0x33,0x1E,0x0C,0x00], // v
        [0x00,0x00,0x63,0x6B,0x7F,0x7F,0x36,0x00], // w
        [0x00,0x00,0x63,0x36,0x1C,0x36,0x63,0x00], // x
        [0x00,0x00,0x33,0x33,0x33,0x3E,0x30,0x1F], // y
        [0x00,0x00,0x3F,0x19,0x0C,0x26,0x3F,0x00], // z
        [0x38,0x0C,0x0C,0x07,0x0C,0x0C,0x38,0x00], // {
        [0x18,0x18,0x18,0x00,0x18,0x18,0x18,0x00], // |
        [0x07,0x0C,0x0C,0x38,0x0C,0x0C,0x07,0x00], // }
        [0x6E,0x3B,0x00,0x00,0x00,0x00,0x00,0x00], // ~
    ];
}
