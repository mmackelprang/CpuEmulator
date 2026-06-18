using BenchmarkDotNet.Attributes;
using CpuEmulator.Benchmarks;

namespace CpuEmulator.Benchmarks.Runner;

/// <summary>The BenchmarkDotNet harness for our two tiers over the two workloads. BDN supplies the
/// warmup + measurement windows + statistical reporting + environment capture the methodology
/// (Ground truth F) requires. The cross-language cycles/host-second numbers are derived from BDN's
/// mean time + the workload's cycle count. W1 (Klaus) is included only when its image is in the
/// vector cache (else those benchmarks throw at setup — BDN reports them as failed, which the
/// methodology treats as "skipped, run get-klaus"). The interpreter-Klaus run is the baseline.</summary>
[MemoryDiagnoser]
public class TierBenchmarks
{
    private BenchWorkload? _w1;
    private BenchWorkload _w2 = null!;
    private BenchWorkload _sieve = null!;
    private BenchWorkload? _z80w1;
    private BenchWorkload _z80w2 = null!;
    private BenchWorkload _z80sieve = null!;
    private BenchWorkload _m68kw1 = null!;
    private BenchWorkload _m68kw2 = null!;
    private BenchWorkload _m68ksieve = null!;
    private BenchWorkload _8086w1 = null!;
    private BenchWorkload _8086w2 = null!;
    private BenchWorkload _8086w3 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _w1 = Workloads.KlausOrNull();
        _w2 = Workloads.ArithmeticKernel();
        _sieve = Workloads.SieveKernel();
        _z80w1 = Z80Workloads.Z80ZexPrefixOrNull();
        _z80w2 = Z80Workloads.Z80ArithmeticKernel();
        _z80sieve = Z80Workloads.Z80SieveKernel();
        _m68kw1 = M68000Workloads.MixedKernel();
        _m68kw2 = M68000Workloads.ArithmeticKernel();
        _m68ksieve = M68000Workloads.SieveKernel();
        _8086w1 = M8086Workloads.MixedKernel();
        _8086w2 = M8086Workloads.ArithmeticKernel();
        _8086w3 = M8086Workloads.SieveKernel();
    }

    // W2 — the arithmetic kernel (always available; the headline emit/chaining comparison).
    [Benchmark(Baseline = true)]
    public long Interpreter_ArithKernel() => Tier0.Run(_w2);

    [Benchmark]
    public long Jit_ArithKernel() => Tier1.Run(_w2);

    // W3 — the Sieve compute kernel (Dhrystone-class; always available; integer/branch/memory-heavy).
    [Benchmark]
    public long Interpreter_6502Sieve() => Tier0.Run(_sieve);

    [Benchmark]
    public long Jit_6502Sieve() => Tier1.Run(_sieve);

    // W1 — Klaus (only when the image is present; otherwise these throw + BDN flags them).
    [Benchmark]
    public long Interpreter_Klaus() => Tier0.Run(RequireW1());

    [Benchmark]
    public long Jit_Klaus() => Tier1.Run(RequireW1());

    // Z80-W2 — the arithmetic kernel (always available; the all-fallback Z80 emit/chaining comparison).
    [Benchmark]
    public long Interpreter_Z80Kernel() => Tier0.Run(_z80w2);

    [Benchmark]
    public long Jit_Z80Kernel() => Tier1.Run(_z80w2);

    // Z80-W3 — the Sieve compute kernel (Dhrystone-class; always available).
    [Benchmark]
    public long Interpreter_Z80Sieve() => Tier0.Run(_z80sieve);

    [Benchmark]
    public long Jit_Z80Sieve() => Tier1.Run(_z80sieve);

    // Z80-W1 — ZEXDOC prefix (only when the image is present; otherwise these throw + BDN flags them).
    [Benchmark]
    public long Interpreter_Z80Zex() => Tier0.Run(RequireZ80W1());

    [Benchmark]
    public long Jit_Z80Zex() => Tier1.Run(RequireZ80W1());

    // 68000-W2 — the ALU/branch kernel (Milestone B; always available; the all-fallback 68000
    // emit/chaining comparison — the "before" baseline for the later 68000 hot-op emit).
    [Benchmark]
    public long Interpreter_M68000Kernel() => Tier0.Run(_m68kw2);

    [Benchmark]
    public long Jit_M68000Kernel() => Tier1.Run(_m68kw2);

    // 68000-W3 — the Sieve compute kernel (Dhrystone-class; always available; the all-fallback path).
    [Benchmark]
    public long Interpreter_M68000Sieve() => Tier0.Run(_m68ksieve);

    [Benchmark]
    public long Jit_M68000Sieve() => Tier1.Run(_m68ksieve);

    // 68000-W1 — the deterministic mixed stream (Milestone B; always available).
    [Benchmark]
    public long Interpreter_M68000Mixed() => Tier0.Run(_m68kw1);

    [Benchmark]
    public long Jit_M68000Mixed() => Tier1.Run(_m68kw1);

    // 8086-W2 — the ALU/branch kernel (M6 PR-A; always available; the all-fallback 8086 emit/chaining
    // comparison — the "before" baseline for the later 8086 hot-op emit, PR-B/C/D).
    [Benchmark]
    public long Interpreter_M8086Kernel() => Tier0.Run(_8086w2);

    [Benchmark]
    public long Jit_M8086Kernel() => Tier1.Run(_8086w2);

    // 8086-W1 — the deterministic mixed stream (M6 PR-A; always available).
    [Benchmark]
    public long Interpreter_M8086Mixed() => Tier0.Run(_8086w1);

    [Benchmark]
    public long Jit_M8086Mixed() => Tier1.Run(_8086w1);

    // 8086-W3 — the compute kernel (M6 PR-A; always available; the all-fallback path).
    [Benchmark]
    public long Interpreter_M8086Sieve() => Tier0.Run(_8086w3);

    [Benchmark]
    public long Jit_M8086Sieve() => Tier1.Run(_8086w3);

    private BenchWorkload RequireW1() =>
        _w1 ?? throw new InvalidOperationException(
            "W1 Klaus image not in the vector cache — run tools/get-klaus.ps1 (or .sh).");

    private BenchWorkload RequireZ80W1() =>
        _z80w1 ?? throw new InvalidOperationException(
            "Z80-W1 zexdoc.com not in the vector cache — run tools/get-zexall.ps1 (or .sh).");
}
