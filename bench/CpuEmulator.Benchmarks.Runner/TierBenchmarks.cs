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

    [GlobalSetup]
    public void Setup()
    {
        _w1 = Workloads.KlausOrNull();
        _w2 = Workloads.ArithmeticKernel();
    }

    // W2 — the arithmetic kernel (always available; the headline emit/chaining comparison).
    [Benchmark(Baseline = true)]
    public long Interpreter_ArithKernel() => Tier0.Run(_w2);

    [Benchmark]
    public long Jit_ArithKernel() => Tier1.Run(_w2);

    // W1 — Klaus (only when the image is present; otherwise these throw + BDN flags them).
    [Benchmark]
    public long Interpreter_Klaus() => Tier0.Run(RequireW1());

    [Benchmark]
    public long Jit_Klaus() => Tier1.Run(RequireW1());

    private BenchWorkload RequireW1() =>
        _w1 ?? throw new InvalidOperationException(
            "W1 Klaus image not in the vector cache — run tools/get-klaus.ps1 (or .sh).");
}
