using System.Diagnostics;
using CpuEmulator.Benchmarks;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;

namespace CpuEmulator.Profiler;

/// <summary>Profiles the bench W-kernels (always available). For each kernel it runs the INTERPRETER one
/// instruction at a time for the hot-op histogram + cycles/sec, then constructs a JittedCpu directly (the
/// way the bench tier drivers do) and runs it for the same instruction budget to read the IJitMetrics
/// counters. The mnemonic-recovery helpers (MnemonicAt + the 68000 field-grammar scan) are copied from the
/// old bench/hotop-profiler/Profiler.cs (ADR 0022 §4: reuse, don't re-derive).</summary>
public static class KernelProfiler
{
    private const int TopN = 15;

    // The kernels declare no nominal clock (they are not a board), so realtimeRatio is null for kernels.
    public static void RunAll(string commit, HostInfo host, Action<SystemProfile> emit,
                              Action<string, string, string> skip, long instrBudget)
    {
        // 6502
        Kernel(commit, host, emit, skip, instrBudget, "bench-6502", "W1-klaus",
            Workloads.KlausOrNull(), 1, Mos6502Cpu.JitTarget, Build6502);
        Kernel(commit, host, emit, skip, instrBudget, "bench-6502", "W2-arithmetic",
            Workloads.ArithmeticKernel(), 1, Mos6502Cpu.JitTarget, Build6502);
        Kernel(commit, host, emit, skip, instrBudget, "bench-6502", "W3-sieve",
            Workloads.SieveKernel(), 1, Mos6502Cpu.JitTarget, Build6502);

        // Z80
        Kernel(commit, host, emit, skip, instrBudget, "bench-z80", "W1-zexdoc-prefix",
            Z80Workloads.Z80ZexPrefixOrNull(), 1, Z80Cpu.JitTarget, BuildZ80);
        Kernel(commit, host, emit, skip, instrBudget, "bench-z80", "W2-arithmetic",
            Z80Workloads.Z80ArithmeticKernel(), 1, Z80Cpu.JitTarget, BuildZ80);
        Kernel(commit, host, emit, skip, instrBudget, "bench-z80", "W3-sieve",
            Z80Workloads.Z80SieveKernel(), 1, Z80Cpu.JitTarget, BuildZ80);

        // 68000 (unit = 2 bytes; mnemonic recovered via the field-grammar scan)
        Kernel(commit, host, emit, skip, instrBudget, "bench-68000", "W1-mixed",
            M68000Workloads.MixedKernel(), 2, M68000Cpu.JitTarget, Build68000);
        Kernel(commit, host, emit, skip, instrBudget, "bench-68000", "W2-arithmetic",
            M68000Workloads.ArithmeticKernel(), 2, M68000Cpu.JitTarget, Build68000);
        Kernel(commit, host, emit, skip, instrBudget, "bench-68000", "W3-sieve",
            M68000Workloads.SieveKernel(), 2, M68000Cpu.JitTarget, Build68000);

        // 8086 (PC = (CS<<4)+IP on a 20-bit bus)
        Kernel(commit, host, emit, skip, instrBudget, "bench-8086", "W1-mixed",
            M8086Workloads.MixedKernel(), 8086, M8086Cpu.JitTarget, Build8086);
        Kernel(commit, host, emit, skip, instrBudget, "bench-8086", "W2-arithmetic",
            M8086Workloads.ArithmeticKernel(), 8086, M8086Cpu.JitTarget, Build8086);
        Kernel(commit, host, emit, skip, instrBudget, "bench-8086", "W3-sieve",
            M8086Workloads.SieveKernel(), 8086, M8086Cpu.JitTarget, Build8086);
    }

    // unitKind: 1 = single-byte PC walk (6502/Z80); 2 = 68000 operword field-grammar; 8086 = (CS<<4)+IP.
    private static void Kernel(string commit, HostInfo host, Action<SystemProfile> emit,
                               Action<string, string, string> skip, long instrBudget,
                               string system, string workload, BenchWorkload? w, int unitKind,
                               IJitTarget target, Func<BenchWorkload, (ICpuCore cpu, AddressSpace mem, AddressSpace? io)> build)
    {
        if (w is null) { skip(system, workload, "workload source absent"); return; }

        var notes = new List<string>
        {
            "kernel hot-op histogram is interpreter-tier, stepped per-instruction (OFFLINE).",
            "fallbackByOpcode is empty + emitCoverage null — full execution-weighted per-opcode attribution is ADR 0022 item D (deferred).",
            "kernels declare no nominal clock, so realtimeRatio is null.",
        };

        // ── interpreter tier: per-instruction hot-op histogram + cycles/sec ──
        var (icpu, imem, _) = build(w);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long allocBefore = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();
        for (long i = 0; i < instrBudget; i++)
        {
            string m = MnemonicAtPc(icpu, imem, target, unitKind);
            counts[m] = counts.TryGetValue(m, out long c) ? c + 1 : 1;
            icpu.Step();
        }
        sw.Stop();
        long iAlloc = GC.GetTotalAllocatedBytes() - allocBefore;
        double iCps = icpu.CycleCount / sw.Elapsed.TotalSeconds;

        var interp = new InterpreterTierProfile
        {
            InstructionsRetired = instrBudget,
            CyclesPerSecond = iCps,
            RealtimeRatio = null,
            HotOps = TopOps(counts),
            AllocBytesPerWindow = iAlloc,
        };

        // ── JIT tier: a BULK-Run window (like the real boots + bench Tier1), NOT budget-1 ──
        // Budget-1 Runs exited the dispatcher after one block before RunChain could follow any chain
        // edge, so chainEdgesTaken was STRUCTURALLY 0 for every kernel (a harness artifact). Driving the
        // JIT with a large per-slice cycle budget lets it follow chains, so the chain/dispatch counters
        // reflect real chaining behavior (the honest ADR-0012 floor signal).
        var (jcpu, jmem, jio) = build(w);
        IJitMetrics jit = NewJit(jcpu, target, jmem, jio);
        long jAllocBefore = GC.GetTotalAllocatedBytes();
        var jsw = Stopwatch.StartNew();
        long jStart = jcpu.CycleCount;
        bool jitWindowCapped = RunJitBulk((ICpuCore)jit, jcpu, jsw);
        jsw.Stop();
        long jAlloc = GC.GetTotalAllocatedBytes() - jAllocBefore;
        double jCps = (jcpu.CycleCount - jStart) / jsw.Elapsed.TotalSeconds;

        notes.Add(jitWindowCapped
            ? $"JIT tier is a bulk-Run window (chaining exercised honestly); window CAPPED at {JitWallCapSeconds}s wall " +
              "(SMC-thrash floor — Klaus/zexdoc are genuinely slow on the JIT, ADR-0012/0022)."
            : "JIT tier is a bulk-Run window (chaining exercised honestly, not budget-1); " +
              "instructionsRetired is left 0 (not attributed for the bulk window — the chain/dispatch/cache counters are the signal).");

        var jitProfile = new JitTierProfile
        {
            InstructionsRetired = 0,   // not attributed for the bulk-Run window (see note)
            CyclesPerSecond = jCps,
            RealtimeRatio = null,
            EmitCoverage = null,
            FallbackByOpcode = [],
            CompileCount = jit.CompileCount,
            TotalRecompiles = jit.TotalRecompiles,
            TotalEvictions = jit.TotalEvictions,
            SmcHotPcCount = jit.SmcHotPcCount,
            ChainEdgesTaken = jit.ChainEdgesTaken,
            DispatcherEntries = jit.DispatcherEntries,
            BlockCacheHits = jit.BlockCacheHits,
            BlockCacheMisses = jit.BlockCacheMisses,
            AllocBytesPerWindow = jAlloc,
        };

        emit(new SystemProfile
        {
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Commit = commit,
            Host = host,
            System = system,
            Workload = workload,
            FrozenBudget = instrBudget,
            BudgetUnit = "instructions",
            Tiers = new TierSet { Interpreter = interp, Jit = jitProfile },
            PerPeripheralFrameCostNs = null,
            Notes = notes,
        });
    }

    // ── JIT construction (mirrors the bench tier drivers) ──
    private static IJitMetrics NewJit(ICpuCore cpu, IJitTarget target, AddressSpace mem, AddressSpace? io) => cpu switch
    {
        Mos6502Cpu c => new JittedCpu<Mos6502Cpu>(c, target, mem, options: new JitOptions()),
        Z80Cpu c => new JittedCpu<Z80Cpu>(c, target, mem, io, new JitOptions()),
        M68000Cpu c => new JittedCpu<M68000Cpu>(c, target, mem, options: new JitOptions()),
        M8086Cpu c => new JittedCpu<M8086Cpu>(c, target, mem, options: new JitOptions()),
        _ => throw new InvalidOperationException($"no JIT construction for {cpu.GetType().Name}"),
    };

    // The JIT bulk-Run window. Drive jit.Run(ref budget) with a LARGE per-slice budget (the way the real
    // boots' RunSlices and the bench Tier1 driver do) so the dispatcher runs many blocks per Run and
    // RunChain follows chain edges — making chainEdgesTaken/dispatcherEntries HONEST. We bound the window
    // by a target CYCLE count (≈ the same work as the interpreter's instruction window) AND by a
    // wall-clock cap, so an SMC-pathological kernel (Klaus / zexdoc — genuinely ~1e6 cyc/s on the JIT)
    // can't run the profiler for minutes. Returns true if the wall-clock cap stopped the window early.
    private const double JitWallCapSeconds = 12.0;     // SMC-thrash guard (Klaus/zexdoc are slow on the JIT)
    private const long JitTargetCycles = 40_000_000;   // ≈ the interpreter instruction window's worth of work
    private const long JitSliceCycles = 4_000_000;     // bulk slice — many blocks per Run so chains are followed

    private static bool RunJitBulk(ICpuCore jit, ICpuCore inner, Stopwatch wall)
    {
        long start = inner.CycleCount;
        while (inner.CycleCount - start < JitTargetCycles)
        {
            if (wall.Elapsed.TotalSeconds >= JitWallCapSeconds)
                return true;   // SMC-thrash cap — stop the window early (noted)
            long prev = inner.CycleCount;
            long budget = JitSliceCycles;
            jit.Run(ref budget);
            if (inner.CycleCount == prev)
                break;   // diverged/0-cycle guard — stop the window honestly (never spin)
        }
        return false;
    }

    // ── per-CPU board construction (mirrors the bench tier drivers) ──
    private static (ICpuCore, AddressSpace, AddressSpace?) Build6502(BenchWorkload w)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
        return (cpu, space, null);
    }

    private static (ICpuCore, AddressSpace, AddressSpace?) BuildZ80(BenchWorkload w)
    {
        var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        mem.MapMemory(0x0000, (byte[])w.Image.Clone(), writable: true);
        var io = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
        var cpu = new Z80Cpu(mem, io);
        cpu.SetRegister("PC", w.StartPc);
        cpu.SetRegister("SP", 0xFFFE);
        return (cpu, mem, io);
    }

    private static (ICpuCore, AddressSpace, AddressSpace?) Build68000(BenchWorkload w)
    {
        var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        mem.MapMemory(0x000000, new byte[0x1000000], writable: true);
        for (int i = 0; i < w.Image.Length; i++)
            mem.Write8((uint)((w.LoadAddress + i) & 0xFFFFFF), w.Image[i]);
        var cpu = new M68000Cpu(mem);
        cpu.SetRegister("PC", w.StartPc);
        cpu.SetRegister("SR", 0x2700);
        cpu.SetRegister("SSP", 0x00FFFC);
        return (cpu, mem, null);
    }

    private static (ICpuCore, AddressSpace, AddressSpace?) Build8086(BenchWorkload w)
    {
        var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        mem.MapMemory(0x00000, new byte[0x100000], writable: true);
        for (int i = 0; i < w.Image.Length; i++)
            mem.Write8((uint)((w.LoadAddress + i) & 0xFFFFF), w.Image[i]);
        var cpu = new M8086Cpu(mem);
        cpu.SetRegister("CS", 0); cpu.SetRegister("DS", 0); cpu.SetRegister("SS", 0); cpu.SetRegister("ES", 0);
        cpu.SetRegister("IP", w.StartPc); cpu.SetRegister("SP", 0xFFFE); cpu.SetRegister("FLAGS", 0x0002);
        return (cpu, mem, null);
    }

    // ── mnemonic recovery (copied from bench/hotop-profiler/Profiler.cs) ──

    private static string MnemonicAtPc(ICpuCore cpu, AddressSpace bus, IJitTarget target, int unitKind)
    {
        if (unitKind == 2)
        {
            // 68000: the descriptor table is all-fallback (empty), so recover the op name from the operword.
            ushort pc = (ushort)(cpu.GetRegister("PC") & 0xFFFF);
            ushort opword = (ushort)((bus.Read8(pc) << 8) | bus.Read8((uint)(pc + 1)));
            return M68kMnemonic(opword);
        }
        if (unitKind == 8086)
        {
            uint phys = (uint)((((uint)cpu.GetRegister("CS") << 4) + ((uint)cpu.GetRegister("IP") & 0xFFFF)) & 0xFFFFF);
            return MnemonicViaDecode(target, () => new ByteFetchStream20(bus, phys));
        }
        // 6502 / Z80: single-byte PC walk.
        ushort pc16 = (ushort)(cpu.GetRegister("PC") & 0xFFFF);
        return MnemonicViaDecode(target, () => new ByteFetchStream(bus, pc16));
    }

    private static string MnemonicViaDecode(IJitTarget target, Func<IFetchStream> freshStream)
    {
        try
        {
            DecodeResult dr = target.Decode(freshStream());
            OpcodeDescriptor d = target.DescriptorFor(dr.OperationKey);
            return string.IsNullOrEmpty(d.Mnemonic) ? "???" : d.Mnemonic;
        }
        catch { return "<decode-err>"; }
    }

    // The 68000 field-grammar dataset (mask/match scan) — the decode walk's own matching logic. The 68000
    // JitDescriptorsByKey table is empty (every op is the all-fallback sentinel), so the real op name is
    // recovered from the dataset instead.
    private static readonly List<(ushort Mask, ushort Match, string Op)> M68kFieldOps = LoadM68kFieldOps();

    private static List<(ushort, ushort, string)> LoadM68kFieldOps()
    {
        string[] roots =
        {
            Path.Combine("..", "..", "tools", "CpuEmulator.SpecImporter", "data", "m68000-fieldgrammar.json"),
            Path.Combine("tools", "CpuEmulator.SpecImporter", "data", "m68000-fieldgrammar.json"),
        };
        // Also try the repo-root-anchored path (the profiler may run from bin/).
        var anchored = new List<string>(roots);
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CpuEmulator.slnx")))
                dir = dir.Parent;
            if (dir is not null)
                anchored.Add(Path.Combine(dir.FullName, "tools", "CpuEmulator.SpecImporter", "data", "m68000-fieldgrammar.json"));
        }
        catch { /* best-effort */ }

        string? path = anchored.FirstOrDefault(File.Exists);
        var list = new List<(ushort, ushort, string)>();
        if (path is null) return list;
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

    private static string M68kMnemonic(ushort opword)
    {
        foreach (var (mask, match, op) in M68kFieldOps)
            if ((opword & mask) == match) return op;
        return "<unmatched>";
    }

    private static IReadOnlyList<HotOp> TopOps(Dictionary<string, long> counts)
    {
        long total = counts.Values.Sum();
        var rows = new List<HotOp>();
        long cumulative = 0;
        foreach (var kv in counts.OrderByDescending(k => k.Value).Take(TopN))
        {
            double pct = total == 0 ? 0 : 100.0 * kv.Value / total;
            cumulative += kv.Value;
            double cumPct = total == 0 ? 0 : 100.0 * cumulative / total;
            rows.Add(new HotOp(kv.Key, kv.Value, Math.Round(pct, 4), Math.Round(cumPct, 4)));
        }
        return rows;
    }
}

// A minimal byte-granular fetch stream over the bus at a fixed PC (copied from the old Profiler.cs).
internal sealed class ByteFetchStream(IAddressSpace bus, ushort origin) : IFetchStream
{
    private int _off;
    public int UnitBytes => 1;
    public int UnitsConsumed => _off;
    public uint NextUnit() => bus.Read8((uint)((origin + _off++) & 0xFFFF));
    public uint PeekUnit() => bus.Read8((uint)((origin + _off) & 0xFFFF));
}

// The 8086 sibling: a 20-bit physical origin + a 20-bit wrap mask.
internal sealed class ByteFetchStream20(IAddressSpace bus, uint origin) : IFetchStream
{
    private int _off;
    public int UnitBytes => 1;
    public int UnitsConsumed => _off;
    public uint NextUnit() => bus.Read8((uint)((origin + _off++) & 0xFFFFF));
    public uint PeekUnit() => bus.Read8((uint)((origin + _off) & 0xFFFFF));
}
