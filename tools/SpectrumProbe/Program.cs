using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

// Headless diagnostic for the ZX Spectrum 48K cold-boot copyright screen.
// Boots the real 16 KB ROM exactly like SpectrumBootTests (200k cycles), on BOTH tiers,
// then dumps: the full RGBA histogram of the ink area + border, the actual "paper" value
// vs. SpectrumPalette.Colors[7] and Colors[15], the attribute bytes the renderer reads,
// the Z80 PC / a few RAM probes, and writes a PPM screenshot of the full frame.

const long BootCycles = 200_000;

string romPath = SpectrumRom.TryGetPath()
    ?? throw new InvalidOperationException("spectrum/48.rom not cached");
byte[] rom = SpectrumRom.Load(romPath);
Console.WriteLine($"ROM: {romPath}  ({rom.Length} bytes)");
Console.WriteLine($"palette Colors[0]  (base black)  = 0x{SpectrumPalette.Colors[0]:X8}");
Console.WriteLine($"palette Colors[7]  (base white)  = 0x{SpectrumPalette.Colors[7]:X8}   <-- test's 'whitePaper' match");
Console.WriteLine($"palette Colors[15] (BRIGHT white) = 0x{SpectrumPalette.Colors[15]:X8}");
Console.WriteLine();

static string Hex(uint v) => $"0x{v:X8}";

static string RegName(Machine m, params string[] cands)
{
    foreach (var c in cands)
        foreach (var n in m.Cpu.RegisterNames)
            if (string.Equals(n, c, StringComparison.OrdinalIgnoreCase)) return n;
    return cands[0];
}

void Probe(ExecutionTier tier)
{
    Console.WriteLine($"================= TIER: {tier} =================");
    Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
    machine.Reset();

    Console.WriteLine($"  CPU={machine.Cpu.Architecture}  registers: {string.Join(",", machine.Cpu.RegisterNames)}");
    string pcReg = RegName(machine, "PC");
    string spReg = RegName(machine, "SP");
    ulong PC() => machine.Cpu.GetRegister(pcReg);
    Console.WriteLine($"  PC after Reset = 0x{PC():X4}  SP=0x{machine.Cpu.GetRegister(spReg):X4}  cycles={machine.Cpu.CycleCount}");

    // Trace PC + key registers at intervals; step in tiny single-Step bursts to watch the loop.
    string hlReg = RegName(machine, "HL"); string deReg = RegName(machine, "DE");
    string aReg = RegName(machine, "A"); string bcReg = RegName(machine, "BC");
    string iReg = RegName(machine, "I");
    ulong HL() => machine.Cpu.GetRegister(hlReg);
    long stepChunk = BootCycles / 10;
    for (int i = 0; i < 10; i++)
    {
        machine.Run(stepChunk);
        Console.WriteLine($"    after {(i + 1) * stepChunk,8} cyc: PC=0x{PC():X4} HL=0x{HL():X4} DE=0x{machine.Cpu.GetRegister(deReg):X4} A=0x{machine.Cpu.GetRegister(aReg):X2} BC=0x{machine.Cpu.GetRegister(bcReg):X4} I=0x{machine.Cpu.GetRegister(iReg):X2} SP=0x{machine.Cpu.GetRegister(spReg):X4}");
    }
    // Single-step micro-trace: 24 instructions from here, show PC + HL each step.
    Console.WriteLine("    micro-trace (24 single steps): PC HL DE A");
    for (int s = 0; s < 24; s++)
    {
        machine.Cpu.Step();
        Console.WriteLine($"      PC=0x{PC():X4} HL=0x{HL():X4} DE=0x{machine.Cpu.GetRegister(deReg):X4} A=0x{machine.Cpu.GetRegister(aReg):X2}");
    }

    var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
    ula.RenderInto(rgba);

    // ---- Full-frame histogram (every distinct RGBA value, by count) ----
    var hist = new Dictionary<uint, int>();
    foreach (uint p in rgba) hist[p] = hist.TryGetValue(p, out int c) ? c + 1 : 1;
    Console.WriteLine($"  distinct colors in FULL frame ({SpectrumUla.FullWidth}x{SpectrumUla.FullHeight} = {rgba.Length} px):");
    foreach (var kv in hist.OrderByDescending(k => k.Value))
        Console.WriteLine($"    {Hex(kv.Key)}  x {kv.Value,7}   ({100.0 * kv.Value / rgba.Length:F2}%)");

    // ---- Ink-area histogram (exactly the region the test scans) ----
    var inkHist = new Dictionary<uint, int>();
    int inkTotal = 0;
    for (int y = 0; y < SpectrumUla.InkHeight; y++)
    for (int x = 0; x < SpectrumUla.InkWidth; x++)
    {
        uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
        inkHist[p] = inkHist.TryGetValue(p, out int c) ? c + 1 : 1;
        inkTotal++;
    }
    Console.WriteLine($"  INK AREA histogram ({SpectrumUla.InkWidth}x{SpectrumUla.InkHeight} = {inkTotal} px, the test's scan region):");
    uint paperGuess = 0; int paperGuessCount = -1;
    foreach (var kv in inkHist.OrderByDescending(k => k.Value))
    {
        Console.WriteLine($"    {Hex(kv.Key)}  x {kv.Value,7}   ({100.0 * kv.Value / inkTotal:F2}%)");
        if (kv.Value > paperGuessCount) { paperGuessCount = kv.Value; paperGuess = kv.Key; }
    }

    // ---- The test's two counters, computed identically ----
    int whitePaper = 0, blackInk = 0;
    for (int y = 0; y < SpectrumUla.InkHeight; y++)
    for (int x = 0; x < SpectrumUla.InkWidth; x++)
    {
        uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
        if (p == SpectrumPalette.Colors[7]) whitePaper++;
        else if (p == SpectrumPalette.Colors[0]) blackInk++;
    }
    Console.WriteLine($"  TEST COUNTERS: whitePaper(==Colors[7]={Hex(SpectrumPalette.Colors[7])}) = {whitePaper};  blackInk(==Colors[0]={Hex(SpectrumPalette.Colors[0])}) = {blackInk}");
    Console.WriteLine($"  dominant ink-area color (the actual 'paper') = {Hex(paperGuess)}  ({paperGuessCount} px)");
    Console.WriteLine($"    -> matches Colors[7] (base white) ?  {paperGuess == SpectrumPalette.Colors[7]}");
    Console.WriteLine($"    -> matches Colors[15] (BRIGHT white)? {paperGuess == SpectrumPalette.Colors[15]}");

    // ---- Border color ----
    uint borderTopLeft = rgba[0];
    Console.WriteLine($"  border (frame[0,0]) = {Hex(borderTopLeft)}  matchesColors[7]={borderTopLeft == SpectrumPalette.Colors[7]} matchesColors[15]={borderTopLeft == SpectrumPalette.Colors[15]}");

    // ---- Inspect the raw attribute area the renderer reads ($5800-$5AFF) ----
    IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
    var attrHist = new Dictionary<byte, int>();
    for (uint a = 0x5800; a < 0x5B00; a++)
    {
        byte v = bus.Read8(a);
        attrHist[v] = attrHist.TryGetValue(v, out int c) ? c + 1 : 1;
    }
    Console.WriteLine($"  ATTR RAM $5800-$5AFF histogram (ink=bits0-2, paper=bits3-5, bright=bit6, flash=bit7):");
    foreach (var kv in attrHist.OrderByDescending(k => k.Value).Take(8))
    {
        int ink = kv.Key & 0x07, paper = (kv.Key >> 3) & 0x07, bright = (kv.Key >> 6) & 1, flash = (kv.Key >> 7) & 1;
        Console.WriteLine($"    attr=0x{kv.Key:X2}  x {kv.Value,5}   ink={ink} paper={paper} bright={bright} flash={flash}");
    }

    // ---- Inspect the pixel/bitmap area: how many non-zero bytes in $4000-$57FF? ----
    int nonZeroBitmap = 0;
    for (uint a = 0x4000; a < 0x5800; a++) if (bus.Read8(a) != 0) nonZeroBitmap++;
    Console.WriteLine($"  BITMAP RAM $4000-$57FF non-zero bytes = {nonZeroBitmap} / 6144");

    // ---- A few well-known system vars / the copyright string presence in RAM ----
    // The ROM message "© 1982 Sinclair Research Ltd" lives in ROM; after boot it's printed to the
    // bitmap. We can't easily OCR, but report whether the bottom 2 character rows have ink set.
    int bottomInk = 0;
    for (int y = SpectrumUla.InkHeight - 16; y < SpectrumUla.InkHeight; y++)
    for (int x = 0; x < SpectrumUla.InkWidth; x++)
    {
        uint addr = 0x4000u
            | ((uint)(y & 0xC0) << 5) | ((uint)(y & 0x07) << 8)
            | ((uint)(y & 0x38) << 2) | (uint)(x >> 3);
        byte bits = bus.Read8(addr);
        if ((bits & (0x80 >> (x & 7))) != 0) bottomInk++;
    }
    Console.WriteLine($"  bottom-2-rows set bitmap pixels (copyright line region) = {bottomInk}");

    // ---- Write a PPM screenshot of the full frame (P6 binary, 320x256) ----
    string outDir = Environment.GetEnvironmentVariable("SPECTRUM_PROBE_OUT") ?? ".";
    Directory.CreateDirectory(outDir);
    string ppm = Path.Combine(outDir, $"spectrum-boot-{tier}.ppm");
    using (var fs = new FileStream(ppm, FileMode.Create))
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{SpectrumUla.FullWidth} {SpectrumUla.FullHeight}\n255\n");
        fs.Write(header);
        var row = new byte[SpectrumUla.FullWidth * 3];
        for (int y = 0; y < SpectrumUla.FullHeight; y++)
        {
            for (int x = 0; x < SpectrumUla.FullWidth; x++)
            {
                uint p = rgba[y * SpectrumUla.FullWidth + x];
                row[x * 3 + 0] = (byte)((p >> 16) & 0xFF); // R
                row[x * 3 + 1] = (byte)((p >> 8) & 0xFF);  // G
                row[x * 3 + 2] = (byte)(p & 0xFF);         // B
            }
            fs.Write(row);
        }
    }
    Console.WriteLine($"  screenshot written: {Path.GetFullPath(ppm)}");

    // ---- ASCII thumbnail of the ink area (downsampled 4x), '#'=ink-dark, '.'=paper-light ----
    Console.WriteLine("  ASCII thumbnail of ink area (64x48, '#'=dark pixel, ' '=light, by luma):");
    for (int ty = 0; ty < 48; ty++)
    {
        var sb = new StringBuilder("    ");
        for (int tx = 0; tx < 64; tx++)
        {
            int sx = tx * SpectrumUla.InkWidth / 64;
            int sy = ty * SpectrumUla.InkHeight / 48;
            uint p = rgba[(SpectrumUla.BorderPx + sy) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + sx)];
            int r = (int)((p >> 16) & 0xFF), g = (int)((p >> 8) & 0xFF), b = (int)(p & 0xFF);
            int luma = (r * 30 + g * 59 + b * 11) / 100;
            sb.Append(luma < 128 ? '#' : ' ');
        }
        Console.WriteLine(sb.ToString());
    }
    Console.WriteLine();
}

// RAM write/read-back probe across the whole address space — the RAM-CHECK loop at $11DC walks
// DOWN writing $02 and reading back; if writes to RAM don't stick, the loop never terminates.
void RamProbe()
{
    Console.WriteLine("========== RAM write/read-back probe ==========");
    Machine machine = SpectrumMachine.Build(rom, out _, ExecutionTier.Interpreter);
    machine.Reset();
    IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
    uint[] addrs = { 0x0000, 0x3FFF, 0x4000, 0x4001, 0x5800, 0x7FFF, 0x8000, 0xC000, 0xFFFF };
    foreach (uint a in addrs)
    {
        byte before = bus.Read8(a);
        bus.Write8(a, 0xA5);
        byte after = bus.Read8(a);
        bus.Write8(a, 0x02);
        byte after2 = bus.Read8(a);
        string kind = a < 0x4000 ? "ROM" : "RAM?";
        Console.WriteLine($"  {a:X4} ({kind}): before=0x{before:X2}  write 0xA5 -> read 0x{after:X2} {(after==0xA5?"OK":"STUCK")};  write 0x02 -> read 0x{after2:X2} {(after2==0x02?"OK":"STUCK")}");
    }
    Console.WriteLine();
}
RamProbe();

Probe(ExecutionTier.Interpreter);
Probe(ExecutionTier.Jit);

// Long-run sanity: does the screen EVER initialize given far more cycles than the test allows?
void LongProbe(ExecutionTier tier, long cycles)
{
    Console.WriteLine($"========== LONG RUN: {tier}  {cycles} cycles ==========");
    Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
    machine.Reset();
    string pcReg = RegName(machine, "PC");
    machine.Run(cycles);
    var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
    ula.RenderInto(rgba);
    IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
    int nonZeroBitmap = 0; for (uint a = 0x4000; a < 0x5800; a++) if (bus.Read8(a) != 0) nonZeroBitmap++;
    var attrHist = new Dictionary<byte, int>();
    for (uint a = 0x5800; a < 0x5B00; a++) { byte v = bus.Read8(a); attrHist[v] = attrHist.TryGetValue(v, out int c) ? c + 1 : 1; }
    int white = 0; foreach (uint p in rgba) if (p == SpectrumPalette.Colors[7]) white++;
    int bright = 0; foreach (uint p in rgba) if (p == SpectrumPalette.Colors[15]) bright++;
    Console.WriteLine($"  PC=0x{machine.Cpu.GetRegister(pcReg):X4}  bitmapNonZero={nonZeroBitmap}/6144  whiteColors7={white} brightWhiteColors15={bright}");
    Console.WriteLine($"  attr top: {string.Join(", ", attrHist.OrderByDescending(k => k.Value).Take(4).Select(k => $"0x{k.Key:X2}x{k.Value}"))}");
    Console.WriteLine();
}
LongProbe(ExecutionTier.Interpreter, 1_000_000);
LongProbe(ExecutionTier.Interpreter, 5_000_000);
LongProbe(ExecutionTier.Jit, 5_000_000);

// Watch the RAM-CHECK loop exit: run until PC leaves the 11DC..11E0 band, then report.
void WatchExit(long maxCycles)
{
    Console.WriteLine($"========== WatchExit (interpreter, up to {maxCycles} cyc) ==========");
    Machine machine = SpectrumMachine.Build(rom, out _, ExecutionTier.Interpreter);
    machine.Reset();
    string pcReg = RegName(machine, "PC"); string hlReg = RegName(machine, "HL");
    string deReg = RegName(machine, "DE"); string aReg = RegName(machine, "A");
    long seen = 0; ulong lastPc = 0; int reports = 0;
    // Coarse run to the loop, then single-step watching for PC outside 0x11DA..0x11E1.
    machine.Run(100_000);
    for (long c = 0; c < maxCycles; c++)
    {
        machine.Cpu.Step();
        ulong pc = machine.Cpu.GetRegister(pcReg);
        if ((pc < 0x11DA || pc > 0x11E1) && pc != lastPc)
        {
            if (reports < 40)
                Console.WriteLine($"    EXIT/branch: PC=0x{pc:X4} HL=0x{machine.Cpu.GetRegister(hlReg):X4} DE=0x{machine.Cpu.GetRegister(deReg):X4} A=0x{machine.Cpu.GetRegister(aReg):X2} cpuCyc={machine.Cpu.CycleCount}");
            reports++;
            if (reports >= 40) { Console.WriteLine("    (40 branch reports reached, stopping)"); break; }
        }
        lastPc = pc;
        seen = c;
    }
    Console.WriteLine($"    stepped {seen} extra steps; final PC=0x{machine.Cpu.GetRegister(pcReg):X4}");
    Console.WriteLine();
}
WatchExit(2_000_000);

// Find how many cycles boot ACTUALLY needs: run in 500k steps up to 50M, report when the
// screen first initializes (attr != 0 / bitmap non-zero / copyright ink appears).
void FindBootCompletion()
{
    Console.WriteLine("========== FindBootCompletion (interpreter, up to 50M cyc) ==========");
    Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, ExecutionTier.Interpreter);
    machine.Reset();
    string pcReg = RegName(machine, "PC");
    IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
    var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
    bool attrSeen = false, bitmapSeen = false;
    long total = 0;
    for (int step = 0; step < 100; step++)   // 100 x 500k = 50M
    {
        machine.Run(500_000);
        total += 500_000;
        // Count attr cells that equal the real "paper white" init value 0x38 (ink0/paper7), not the
        // RAM-fill $02. The copyright screen sets attrs to 0x38 (or 0x07 ink-only variants).
        int attrWhitePaper = 0; for (uint a = 0x5800; a < 0x5B00; a++) { byte v = bus.Read8(a); if (((v>>3)&7)==7) attrWhitePaper++; }
        int bmpNonZero = 0; for (uint a = 0x4000; a < 0x5800; a++) { byte v = bus.Read8(a); if (v != 0 && v != 0x02) bmpNonZero++; }
        ulong pcNow = machine.Cpu.GetRegister(pcReg);
        if (step % 4 == 0 || (pcNow < 0x11DA || pcNow > 0x1300))
            Console.WriteLine($"    ~{total,9} cyc: PC=0x{pcNow:X4} attrPaper7={attrWhitePaper} bmpReal={bmpNonZero}");
        int attrNonZero = attrWhitePaper;
        if (!attrSeen && attrWhitePaper > 100 && pcNow > 0x1300)
        {
            ula.RenderInto(rgba);
            int white = 0, black = 0;
            for (int y = 0; y < SpectrumUla.InkHeight; y++)
            for (int x = 0; x < SpectrumUla.InkWidth; x++)
            {
                uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
                if (p == SpectrumPalette.Colors[7]) white++; else if (p == SpectrumPalette.Colors[0]) black++;
            }
            Console.WriteLine($"  *** ATTR first non-zero at ~{total} cyc: attrNonZero={attrNonZero} PC=0x{machine.Cpu.GetRegister(pcReg):X4} white(Colors7)={white} black(Colors0)={black}");
            attrSeen = true;
        }
        if (!bitmapSeen && bmpNonZero > 0)
        {
            Console.WriteLine($"  *** BITMAP first non-zero at ~{total} cyc: bmpNonZero={bmpNonZero} PC=0x{machine.Cpu.GetRegister(pcReg):X4}");
            bitmapSeen = true;
        }
        if (attrSeen && bitmapSeen) break;
    }
    // Final render after completion + write a screenshot.
    ula.RenderInto(rgba);
    int fw = 0, fb = 0, fbright = 0;
    for (int y = 0; y < SpectrumUla.InkHeight; y++)
    for (int x = 0; x < SpectrumUla.InkWidth; x++)
    {
        uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
        if (p == SpectrumPalette.Colors[7]) fw++; else if (p == SpectrumPalette.Colors[0]) fb++;
        else if (p == SpectrumPalette.Colors[15]) fbright++;
    }
    Console.WriteLine($"  after {total} cyc: ink-area white(Colors7)={fw} black(Colors0)={fb} brightWhite(Colors15)={fbright}  PC=0x{machine.Cpu.GetRegister(pcReg):X4}");
    string outDir = Environment.GetEnvironmentVariable("SPECTRUM_PROBE_OUT") ?? ".";
    string ppm = Path.Combine(outDir, "spectrum-boot-COMPLETED.ppm");
    using (var fs = new FileStream(ppm, FileMode.Create))
    {
        fs.Write(Encoding.ASCII.GetBytes($"P6\n{SpectrumUla.FullWidth} {SpectrumUla.FullHeight}\n255\n"));
        var rowb = new byte[SpectrumUla.FullWidth * 3];
        for (int y = 0; y < SpectrumUla.FullHeight; y++)
        {
            for (int x = 0; x < SpectrumUla.FullWidth; x++)
            {
                uint p = rgba[y * SpectrumUla.FullWidth + x];
                rowb[x*3] = (byte)((p>>16)&0xFF); rowb[x*3+1] = (byte)((p>>8)&0xFF); rowb[x*3+2] = (byte)(p&0xFF);
            }
            fs.Write(rowb);
        }
    }
    Console.WriteLine($"  completed screenshot: {Path.GetFullPath(ppm)}");
    // ASCII thumbnail
    Console.WriteLine("  ASCII thumbnail (ink area 64x48, '#'=dark):");
    for (int ty = 0; ty < 48; ty++)
    {
        var sb = new StringBuilder("    ");
        for (int tx = 0; tx < 64; tx++)
        {
            int sx = tx * SpectrumUla.InkWidth / 64, sy = ty * SpectrumUla.InkHeight / 48;
            uint p = rgba[(SpectrumUla.BorderPx + sy) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + sx)];
            int r=(int)((p>>16)&0xFF),g=(int)((p>>8)&0xFF),b=(int)(p&0xFF);
            sb.Append(((r*30+g*59+b*11)/100) < 128 ? '#' : ' ');
        }
        Console.WriteLine(sb.ToString());
    }
    Console.WriteLine();
}
FindBootCompletion();

// Precise: smallest BootCycles at which BOTH test assertions pass (white>half AND black>50), both tiers.
void FindThreshold(ExecutionTier tier)
{
    Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
    machine.Reset();
    var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
    long granular = 100_000; long total = 0; long firstPass = -1;
    int half = SpectrumUla.InkWidth * SpectrumUla.InkHeight / 2;
    while (total < 12_000_000)
    {
        machine.Run(granular); total += granular;
        ula.RenderInto(rgba);
        int white = 0, black = 0;
        for (int y = 0; y < SpectrumUla.InkHeight; y++)
        for (int x = 0; x < SpectrumUla.InkWidth; x++)
        {
            uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
            if (p == SpectrumPalette.Colors[7]) white++; else if (p == SpectrumPalette.Colors[0]) black++;
        }
        if (white > half && black > 50) { firstPass = total; break; }
    }
    Console.WriteLine($"  [{tier}] BOTH assertions first pass at ~{firstPass} cycles (white>{half} && black>50)");
}
Console.WriteLine("========== Test-assertion threshold ==========");
FindThreshold(ExecutionTier.Interpreter);
FindThreshold(ExecutionTier.Jit);
