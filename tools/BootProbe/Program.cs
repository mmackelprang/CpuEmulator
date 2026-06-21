using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

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
