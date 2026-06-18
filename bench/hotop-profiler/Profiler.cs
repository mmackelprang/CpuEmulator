// THROWAWAY hot-op profiling harness for ADR 0011 (M6 JIT hot-op emission).
// NOT part of any runtime/test graph. Instruments the tier-0 interpreter over the frozen
// benchmark workloads (W1/W2/W3 per CPU) and counts op-execution frequency by mnemonic, so
// the emit effort is ordered by what actually dominates execution. Identifies each instruction
// at its live PC via the generated JIT descriptor table (the same per-CPU Decode/DescriptorFor
// the JIT consumes), then Steps the interpreter. Reports the top-N hot ops per CPU × workload.
//
// Run:  dotnet run -c Release --project bench/hotop-profiler
// (Set CPUEMULATOR_TESTVECTORS or have ~/.cache/cpuemulator/vectors populated for the W1 streams;
//  W1 is skipped-with-note when its fetched binary is absent — exactly like the real bench.)

using System.Text;
using CpuEmulator.Benchmarks;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;

const long InstrCap = 20_000_000;   // instructions to profile per workload (plenty to stabilize a hot-op histogram)

var report = new StringBuilder();
void Line(string s = "") { Console.WriteLine(s); report.Append(s).Append('\n'); }

Line("# Hot-op profiling — tier-0 interpreter, frozen benchmark workloads");
Line($"# instruction cap per workload = {InstrCap:N0}");
Line();

// Identify the mnemonic of the instruction at `pc` using the CPU's generated decode + descriptor
// table. Works for every CPU (the descriptor carries the real Mnemonic even when NeedsFallback).
string MnemonicAt(IJitTarget target, Func<IFetchStream> freshStream)
{
    try
    {
        DecodeResult dr = target.Decode(freshStream());
        OpcodeDescriptor d = target.DescriptorFor(dr.OperationKey);
        return string.IsNullOrEmpty(d.Mnemonic) ? "???" : d.Mnemonic;
    }
    catch { return "<decode-err>"; }
}

// Map a 68000 operword to its operation mnemonic via the fieldgrammar dataset (mask/match scan,
// exactly what the decode walk does). The 68000 JitDescriptorsByKey table is EMPTY (every op is the
// all-fallback Undefined sentinel — that is HOW the 68000 is all-fallback), so the descriptor's
// Mnemonic is "???"; recover the real op name from the dataset instead. The kernels are tiny so the
// distinct-opword set is small. Returns null when no field-op matches.
List<(ushort Mask, ushort Match, string Op)> LoadM68kFieldOps()
{
    string[] roots = {
        Path.Combine("..", "..", "tools", "CpuEmulator.SpecImporter", "data", "m68000-fieldgrammar.json"),
        Path.Combine("tools", "CpuEmulator.SpecImporter", "data", "m68000-fieldgrammar.json"),
    };
    string? path = roots.FirstOrDefault(File.Exists);
    var list = new List<(ushort, ushort, string)>();
    if (path is null) return list;
    foreach (var line in File.ReadAllLines(path)) { /* parsed below via simple JSON */ }
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        ushort mask = Convert.ToUInt16(e.GetProperty("mask").GetString(), 16);
        ushort match = Convert.ToUInt16(e.GetProperty("match").GetString(), 16);
        string op = e.GetProperty("operation").GetString() ?? "?";
        list.Add((mask, match, op));
    }
    return list;
}
var m68kFieldOps = LoadM68kFieldOps();
string M68kMnemonic(ushort opword)
{
    foreach (var (mask, match, op) in m68kFieldOps)
        if ((opword & mask) == match) return op;
    return "<unmatched>";
}

// Run one workload: step the interpreter InstrCap times, counting mnemonic at each PC.
void Profile(string cpuName, string workloadName, ICpuCore cpu, IAddressSpace bus,
             IJitTarget target, int unitBytes)
{
    var counts = new Dictionary<string, long>(StringComparer.Ordinal);
    long steps = 0;
    for (long i = 0; i < InstrCap; i++)
    {
        ushort pc = (ushort)(cpu.GetRegister("PC") & 0xFFFF);
        string m;
        if (unitBytes == 2)
        {
            // 68000: the descriptor table is empty (all-fallback), so recover the op name from the
            // operword via the dataset mask/match scan (the decode walk's own matching logic).
            ushort opword = (ushort)((bus.Read8(pc) << 8) | bus.Read8((uint)(pc + 1)));
            m = M68kMnemonic(opword);
        }
        else
        {
            m = MnemonicAt(target, () => new ByteFetchStream(bus, pc));
        }
        counts[m] = counts.TryGetValue(m, out long c) ? c + 1 : 1;
        cpu.Step();
        steps++;
    }

    Line($"## {cpuName} — {workloadName}   ({steps:N0} instructions profiled)");
    long total = counts.Values.Sum();
    int rank = 1;
    long cumulative = 0;
    foreach (var kv in counts.OrderByDescending(k => k.Value).Take(15))
    {
        double pct = 100.0 * kv.Value / total;
        cumulative += kv.Value;
        double cumPct = 100.0 * cumulative / total;
        Line($"  {rank,2}. {kv.Key,-8} {kv.Value,14:N0}  {pct,6:F2}%   (cum {cumPct,6:F2}%)");
        rank++;
    }
    Line($"  distinct mnemonics executed: {counts.Count}");
    Line();
}

// ── 6502 board (16-bit, exactly the Mos6502TierDriver construction) ──
void Run6502(string wname, BenchWorkload? w)
{
    if (w is null) { Line($"## 6502 — {wname}   SKIPPED (workload source absent)"); Line(); return; }
    var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
    space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
    var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
    Profile("6502", wname, cpu, space, Mos6502Cpu.JitTarget, unitBytes: 1);
}

// ── Z80 board (16-bit + separate 16-bit I/O space, mirroring Z80TierDriver) ──
void RunZ80(string wname, BenchWorkload? w)
{
    if (w is null) { Line($"## Z80 — {wname}   SKIPPED (workload source absent)"); Line(); return; }
    var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
    mem.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
    var io = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
    io.MapMemory(0x0000, new byte[0x10000], writable: true);
    var cpu = new Z80Cpu(mem, io);
    cpu.SetRegister("PC", w.StartPc);
    Profile("Z80", wname, cpu, mem, Z80Cpu.JitTarget, unitBytes: 1);
}

// ── 68000 board (24-bit BigEndian, exactly the M68000TierDriver construction) ──
void Run68000(string wname, BenchWorkload w)
{
    var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
    mem.MapMemory(0x000000, new byte[0x1000000], writable: true);
    for (int i = 0; i < w.Image.Length; i++)
        mem.Write8((uint)((w.LoadAddress + i) & 0xFFFFFF), w.Image[i]);
    var cpu = new M68000Cpu(mem);
    cpu.SetRegister("PC", w.StartPc);
    cpu.SetRegister("SR", 0x2700);
    cpu.SetRegister("SSP", 0x00FFFC);
    Profile("68000", wname, cpu, mem, M68000Cpu.JitTarget, unitBytes: 2);
}

// ── 6502 ──
Run6502("W1 Klaus", Workloads.KlausOrNull());
Run6502("W2 arithmetic-kernel", Workloads.ArithmeticKernel());
Run6502("W3 sieve-kernel", Workloads.SieveKernel());

// ── Z80 ──
RunZ80("W1 ZEXDOC-prefix", Z80Workloads.Z80ZexPrefixOrNull());
RunZ80("W2 arithmetic-kernel", Z80Workloads.Z80ArithmeticKernel());
RunZ80("W3 sieve-kernel", Z80Workloads.Z80SieveKernel());

// ── 68000 ──
Run68000("W1 mixed-kernel", M68000Workloads.MixedKernel());
Run68000("W2 arithmetic-kernel", M68000Workloads.ArithmeticKernel());

File.WriteAllText("hotop-profile-results.txt", report.ToString());
Console.Error.WriteLine("\n[profiler] wrote hotop-profile-results.txt");

// ─────────────────────────────────────────────────────────────────────────────────────────────
// A minimal byte-granular fetch stream over the bus at a fixed PC (the 6502/Z80 unit = 1 byte).
// We decode a FRESH instruction at each live PC (no stateful prefetch needed — we only want the
// mnemonic), so a stateless per-PC stream is correct for identification.
sealed class ByteFetchStream(IAddressSpace bus, ushort origin) : IFetchStream
{
    int _off;
    public int UnitBytes => 1;
    public int UnitsConsumed => _off;
    public uint NextUnit() => bus.Read8((uint)((origin + _off++) & 0xFFFF));
    public uint PeekUnit() => bus.Read8((uint)((origin + _off) & 0xFFFF));
}
