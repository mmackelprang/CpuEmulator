using System.Diagnostics;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Profiler;

/// <summary>Profiles the REAL machine boots through the SAME board/surface factories the live machines
/// use (ADR 0022 §4.1) — asset-gated, skip-with-note. Captures throughput + the IJitMetrics counters
/// (the headline) via machine.Run(budget) on both tiers where applicable, and a best-effort interpreter
/// hot-op histogram via a per-instruction PC walk for the single-CPU boots (DOS 3.3, Spectrum). For the
/// dual-CPU SoftCard boots the hot-op histogram is skipped (the coprocessor arbitration is bypassed by
/// stepping the primary directly) — a note records it; throughput + counters are still captured via the
/// bulk Run.</summary>
public sealed class RealBootProfiler(string commit, HostInfo host,
                                     Action<SystemProfile> emit, Action<string, string, string> skip)
{
    private const int TopN = 15;

    // ── DOS 3.3 (Apple ][+, 6502 — JIT-capable both tiers) ──
    public void ProfileDos33(long cycleBudget)
    {
        string? romPath = Apple2Rom.TryGetPath();
        if (romPath is null) { skip("apple2-dos33", "boot-to-basic", "apple2plus.rom absent"); return; }
        byte[] systemRom = Apple2Rom.Load(romPath);

        Machine BuildApple2(ExecutionTier tier)
        {
            var state = new Apple2VideoState();
            var lc = new Apple2LanguageCard(systemRom);
            var image = new SyntheticFluxImage(trackCount: 35);
            var disk = new Apple2DiskII(image);
            var iou = new Apple2Iou(state, lc, disk);
            // SpecWithDiskII == cold-boot to BASIC (no $C600 boot rom), the live web-surface cold path.
            BoardSpec spec = Apple2Board.SpecWithDiskII(systemRom, iou, disk);
            return BoardMachineFactory.Build(spec, tier);
        }

        var notes = new List<string>
        {
            "DOS 3.3 cold-boot to the BASIC prompt via SpecWithDiskII (no $C600 boot ROM — the live web-surface cold path).",
            "synthetic flux image (no real DOS 3.3 disk wired); a boot-to-BASIC profile does not need one.",
            "interpreter hot-op histogram is a per-instruction PC walk (OFFLINE); JIT tier captures throughput + IJitMetrics counters via machine.Run.",
            "fallbackByOpcode empty + emitCoverage null — ADR 0022 item D (deferred).",
        };

        InterpreterTierProfile interp = ProfileInterpreterWithHotOps(
            BuildApple2(ExecutionTier.Interpreter), Mos6502Cpu.JitTarget, "PC", unit8086: false, cycleBudget);
        JitTierProfile jit = ProfileJitBulk(BuildApple2(ExecutionTier.Jit), cycleBudget);

        emit(Profile("apple2-dos33", "boot-to-basic", cycleBudget, interp, jit, notes));
    }

    // ── Spectrum (Z80 — JIT-capable both tiers) ──
    public void ProfileSpectrum(long cycleBudget)
    {
        string? romPath = SpectrumRom.TryGetPath();
        if (romPath is null) { skip("spectrum-48k", "boot-to-copyright", "48.rom absent"); return; }
        byte[] rom = SpectrumRom.Load(romPath);

        var notes = new List<string>
        {
            "ZX Spectrum 48K ROM boot to the copyright screen.",
            "interpreter hot-op histogram is a per-instruction PC walk (OFFLINE); JIT tier captures throughput + IJitMetrics counters via machine.Run.",
            "fallbackByOpcode empty + emitCoverage null — ADR 0022 item D (deferred).",
        };

        Machine interpM = SpectrumMachine.Build(rom, out _, ExecutionTier.Interpreter);
        InterpreterTierProfile interp = ProfileInterpreterWithHotOps(
            interpM, Z80Cpu.JitTarget, "PC", unit8086: false, cycleBudget);
        Machine jitM = SpectrumMachine.Build(rom, out _, ExecutionTier.Jit);
        JitTierProfile jit = ProfileJitBulk(jitM, cycleBudget);

        emit(Profile("spectrum-48k", "boot-to-copyright", cycleBudget, interp, jit, notes));
    }

    // ── CP/M 2.2 SoftCard (6502 primary + Z80 coprocessor; coprocessor always interpreter) ──
    public void ProfileSoftCardCpm(long cycleBudget)
    {
        string? romPath = Apple2Rom.TryGetPath();
        string? diskPath = SoftCardCpm.TryGetDiskPath();
        byte[]? diskBootRom = Apple2Rom.TryLoadDiskRom();
        if (romPath is null) { skip("softcard-cpm22", "boot-to-A", "apple2plus.rom absent"); return; }
        if (diskPath is null) { skip("softcard-cpm22", "boot-to-A", "softcard-cpm.dsk absent"); return; }
        if (diskBootRom is null) { skip("softcard-cpm22", "boot-to-A", "disk2.rom (slot-6 boot ROM) absent"); return; }

        byte[] systemRom = Apple2Rom.Load(romPath);

        Machine BuildSoftCard(ExecutionTier tier)
        {
            IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(diskPath);
            var state = new Apple2VideoState();
            var lc = new Apple2LanguageCard(systemRom);
            var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
            var disk = new Apple2DiskII(drive1);
            var iou = new Apple2Iou(state, lc, disk);
            BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
            return BoardMachineFactory.Build(spec, tier);
        }

        var notes = new List<string>
        {
            "CP/M 2.2 SoftCard: 6502 primary (JIT-capable) + Z80 coprocessor (always interpreter, ADR 0015).",
            "dual-CPU boot driven via machine.Run slices; hot-op histogram is SKIPPED for dual-CPU boots (stepping the primary directly bypasses coprocessor arbitration) — coprocessor hot-ops deferred.",
            "JIT counters are 0 by design here: the dual-CPU scheduler (Machine.RunDualCpu) drives the primary via Cpu.Step() one instruction at a time, and JittedCpu.Step() runs the inner interpreter (never the block compiler) — so the JIT tier provides no JIT benefit during a SoftCard boot. This is the measured truth, not a profiler bug (it is itself an ADR-0022 finding: the JIT does not engage under the dual-CPU run loop).",
            "fallbackByOpcode empty + emitCoverage null — ADR 0022 item D (deferred).",
            "instructionsRetired left 0 for the bulk-Run path (no per-instruction count).",
        };

        InterpreterTierProfile interp = ProfileInterpreterBulk(BuildSoftCard(ExecutionTier.Interpreter), cycleBudget);
        JitTierProfile jit = ProfileJitBulk(BuildSoftCard(ExecutionTier.Jit), cycleBudget);

        emit(Profile("softcard-cpm22", "boot-to-A", cycleBudget, interp, jit, notes));
    }

    // ── apl2cpm3 CP/M 3.1 (SoftCard+Videx, slot 4) ──
    public void ProfileApl2Cpm3(long cycleBudget)
    {
        string? romPath = Apple2Rom.TryGetPath();
        string? diskPath = Apl2Cpm3.TryGetBootDiskPath();
        byte[]? diskBootRom = Apple2Rom.TryLoadDiskRom();
        byte[]? videxFw = VidexRom.TryLoadFirmware();
        byte[]? videxChar = VidexRom.TryLoadCharRom();
        if (romPath is null) { skip("apl2cpm3-cpm31", "boot-window", "apple2plus.rom absent"); return; }
        if (diskPath is null) { skip("apl2cpm3-cpm31", "boot-window", "CPM3.1_Disk_1.dsk absent"); return; }
        if (diskBootRom is null) { skip("apl2cpm3-cpm31", "boot-window", "disk2.rom (slot-6 boot ROM) absent"); return; }
        if (videxFw is null) { skip("apl2cpm3-cpm31", "boot-window", "videx-firmware.rom absent (the Videx boot needs real firmware)"); return; }

        byte[] systemRom = Apple2Rom.Load(romPath);

        Machine BuildApl2Cpm3(ExecutionTier tier)
        {
            IBlockDevice disk1 = Apl2Cpm3.LoadBootDisk(diskPath);
            var state = new Apple2VideoState();
            var lc = new Apple2LanguageCard(systemRom);
            var drive1 = new DskFluxImage(disk1, SectorOrderKind.Cpm3);
            var disk = new Apple2DiskII(drive1);
            var videx = new VidexVideoterm(videxChar, videxFw);
            var iou = new Apple2Iou(state, lc, disk, videx);
            BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx,
                controlPortBase: SoftCardBoard.ControlPortBaseSlot4);
            return BoardMachineFactory.Build(spec, tier);
        }

        var notes = new List<string>
        {
            "apl2cpm3 CP/M 3.1 (SoftCard+Videx, slot 4): 6502 primary (JIT-capable) + Z80 coprocessor (interpreter).",
            "the full boot to A> is long (~tens of M cycles); this is a FIXED representative steady window, NOT a reached-prompt run.",
            "dual-CPU boot driven via machine.Run slices; hot-op histogram SKIPPED for dual-CPU boots — coprocessor hot-ops deferred.",
            "JIT counters are 0 by design here: the dual-CPU scheduler steps the primary via Cpu.Step() (JittedCpu.Step() runs the inner interpreter, never the block compiler) — the JIT does not engage under the dual-CPU run loop (an ADR-0022 finding, not a profiler bug).",
            "fallbackByOpcode empty + emitCoverage null — ADR 0022 item D (deferred).",
            "instructionsRetired left 0 for the bulk-Run path.",
        };

        InterpreterTierProfile interp = ProfileInterpreterBulk(BuildApl2Cpm3(ExecutionTier.Interpreter), cycleBudget);
        JitTierProfile jit = ProfileJitBulk(BuildApl2Cpm3(ExecutionTier.Jit), cycleBudget);

        emit(Profile("apl2cpm3-cpm31", "boot-window", cycleBudget, interp, jit, notes));
    }

    // ── Apple Pascal (6502) — interpreter-only (Pascal.CreateBoard takes no tier) ──
    public void ProfilePascal(long cycleBudget)
    {
        string? romPath = Apple2Rom.TryGetPath();
        string? bootPath = Pascal.TryGetBootDiskPath();
        string? progPath = Pascal.TryGetProgramDiskPath();
        byte[]? diskBootRom = Apple2Rom.TryLoadDiskRom();
        if (romPath is null) { skip("apple2-pascal", "boot-window", "apple2plus.rom absent"); return; }
        if (bootPath is null) { skip("apple2-pascal", "boot-window", "APPLE1.dsk (Pascal boot disk) absent"); return; }
        if (diskBootRom is null) { skip("apple2-pascal", "boot-window", "disk2.rom (slot-6 boot ROM) absent"); return; }

        byte[] systemRom = Apple2Rom.Load(romPath);
        Machine machine = Pascal.CreateBoard(systemRom, diskBootRom, bootPath, progPath).Machine;

        var notes = new List<string>
        {
            "Apple Pascal (UCSD p-System, 6502): Pascal.CreateBoard takes NO ExecutionTier, so this arm is INTERPRETER-ONLY — JIT tier is null (not applicable).",
            "the full boot to COMMAND: is ~75-90M cycles; this is a SHORTER representative steady window (representative metrics, not a reached-prompt run).",
            "interpreter hot-op histogram is a per-instruction PC walk (OFFLINE).",
        };

        InterpreterTierProfile interp = ProfileInterpreterWithHotOps(
            machine, Mos6502Cpu.JitTarget, "PC", unit8086: false, cycleBudget);

        emit(Profile("apple2-pascal", "boot-window", cycleBudget, interp, jit: null, notes));
    }

    // ── shared capture paths ──────────────────────────────────────────────────────────────────────────

    /// <summary>Interpreter tier with a per-instruction hot-op histogram (single-CPU boots). Steps the
    /// primary CPU one instruction at a time up to a cycle budget, recovering the mnemonic at each live PC
    /// via the per-CPU JitTarget decode, then computes cycles/sec + realtimeRatio.</summary>
    private InterpreterTierProfile ProfileInterpreterWithHotOps(Machine machine, IJitTarget target,
                                                                string pcReg, bool unit8086, long cycleBudget)
    {
        machine.Reset();
        ICpuCore cpu = machine.Cpu;
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        long allocBefore = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();
        long retired = 0;
        long start = cpu.CycleCount;
        while (cpu.CycleCount - start < cycleBudget)
        {
            ushort pc = (ushort)(cpu.GetRegister(pcReg) & 0xFFFF);
            string m = MnemonicViaDecode(target, () => new ByteFetchStream(bus, pc));
            counts[m] = counts.TryGetValue(m, out long c) ? c + 1 : 1;
            long prev = cpu.CycleCount;
            cpu.Step();
            retired++;
            if (cpu.CycleCount == prev) break;   // 0-cycle guard
        }
        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        double cps = (cpu.CycleCount - start) / sw.Elapsed.TotalSeconds;

        return new InterpreterTierProfile
        {
            InstructionsRetired = retired,
            CyclesPerSecond = cps,
            RealtimeRatio = RealtimeRatio(machine, cps),
            HotOps = TopOps(counts),
            AllocBytesPerWindow = alloc,
        };
    }

    /// <summary>Interpreter tier via bulk machine.Run (dual-CPU boots): throughput + realtimeRatio only,
    /// no per-instruction hot-op histogram (the coprocessor arbitration must not be bypassed).</summary>
    private InterpreterTierProfile ProfileInterpreterBulk(Machine machine, long cycleBudget)
    {
        machine.Reset();
        long allocBefore = GC.GetTotalAllocatedBytes();
        long start = machine.Cpu.CycleCount;
        var sw = Stopwatch.StartNew();
        RunSlices(machine, cycleBudget);
        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        double cps = (machine.Cpu.CycleCount - start) / sw.Elapsed.TotalSeconds;

        return new InterpreterTierProfile
        {
            InstructionsRetired = 0,
            CyclesPerSecond = cps,
            RealtimeRatio = RealtimeRatio(machine, cps),
            HotOps = [],
            AllocBytesPerWindow = alloc,
        };
    }

    /// <summary>JIT tier via bulk machine.Run: throughput + realtimeRatio + the IJitMetrics counters read
    /// off machine.JitMetrics (the seam item C extended).</summary>
    private JitTierProfile ProfileJitBulk(Machine machine, long cycleBudget)
    {
        machine.Reset();
        IJitMetrics? jm = machine.JitMetrics;
        long allocBefore = GC.GetTotalAllocatedBytes();
        long start = machine.Cpu.CycleCount;
        var sw = Stopwatch.StartNew();
        RunSlices(machine, cycleBudget);
        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        double cps = (machine.Cpu.CycleCount - start) / sw.Elapsed.TotalSeconds;

        return new JitTierProfile
        {
            InstructionsRetired = 0,
            CyclesPerSecond = cps,
            RealtimeRatio = RealtimeRatio(machine, cps),
            EmitCoverage = null,
            FallbackByOpcode = [],
            CompileCount = jm?.CompileCount ?? 0,
            TotalRecompiles = jm?.TotalRecompiles ?? 0,
            TotalEvictions = jm?.TotalEvictions ?? 0,
            SmcHotPcCount = jm?.SmcHotPcCount ?? 0,
            ChainEdgesTaken = jm?.ChainEdgesTaken ?? 0,
            DispatcherEntries = jm?.DispatcherEntries ?? 0,
            BlockCacheHits = jm?.BlockCacheHits ?? 0,
            BlockCacheMisses = jm?.BlockCacheMisses ?? 0,
            AllocBytesPerWindow = alloc,
        };
    }

    // Drive machine.Run in slices so a dual-CPU boot makes coprocessor hand-offs (a single coarse Run
    // collapses handback+handoff pairs); for single-CPU boots the slicing is harmless.
    private static void RunSlices(Machine machine, long cycleBudget)
    {
        const long slice = 100_000;
        long ran = 0;
        while (ran < cycleBudget)
        {
            long want = Math.Min(slice, cycleBudget - ran);
            long did = machine.Run(want);
            if (did <= 0) break;
            ran += did;
        }
    }

    private static double? RealtimeRatio(Machine machine, double cyclesPerSecond)
        => machine.NominalClockHz is double hz && hz > 0 ? cyclesPerSecond / hz : null;

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

    private SystemProfile Profile(string system, string workload, long cycleBudget,
                                  InterpreterTierProfile? interp, JitTierProfile? jit, List<string> notes)
        => new()
        {
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Commit = commit,
            Host = host,
            System = system,
            Workload = workload,
            FrozenBudget = cycleBudget,
            BudgetUnit = "cycles",
            Tiers = new TierSet { Interpreter = interp, Jit = jit },
            PerPeripheralFrameCostNs = null,
            Notes = notes,
        };
}
