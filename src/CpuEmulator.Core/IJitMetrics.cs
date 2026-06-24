namespace CpuEmulator.Core;

/// <summary>A read-only, NON-generic view of the IL-JIT tier's run-lifetime instrumentation — the
/// public forwarding seam the perf-overlay HUD reads (design handoff 2026-06-23-perf-overlay §7 item 2).
/// The concrete <c>JittedCpu&lt;TCpu&gt;</c> is generic, so a host/surface can't do <c>cpu is
/// JittedCpu&lt;T&gt;</c> without naming every TCpu; this non-generic interface lets the host detect the
/// JIT tier and read its four stats with one type test (<c>cpu is IJitMetrics</c>). Every value forwards
/// an already-computed counter (CompileCount on the compiler, the rest on the block cache); the interface
/// adds no new abstraction and no AOT-affecting type — the interpreter cores simply don't implement it,
/// so <c>cpu is IJitMetrics</c> is false on the interpreter tier (the HUD then omits the jit rows).</summary>
public interface IJitMetrics
{
    /// <summary>Blocks compiled since construction (run-lifetime, monotonic).</summary>
    int CompileCount { get; }

    /// <summary>Every evict-then-recompile across the run (the SMC/recompile-cost signal).</summary>
    long TotalRecompiles { get; }

    /// <summary>Every block dropped from the cache across the run.</summary>
    long TotalEvictions { get; }

    /// <summary>How many distinct PCs ever tripped the recompile cap (the SMC-hot-PC count).</summary>
    int SmcHotPcCount { get; }

    /// <summary>Chain edges followed without a dispatcher round-trip (the ADR-0012 floor signal —
    /// high vs DispatcherEntries means chaining is carrying the hot path). Run-lifetime, monotonic, FREE.</summary>
    long ChainEdgesTaken { get; }

    /// <summary>Dispatcher round-trips — block dispatches via GetOrCompile (the cost a chain edge avoids).
    /// The ADR-0012 floor: a low ChainEdgesTaken : DispatcherEntries ratio means the hot path is
    /// dispatcher-bound. Run-lifetime, monotonic, FREE.</summary>
    long DispatcherEntries { get; }

    /// <summary>Block-cache hits — a dispatch found the block already compiled (no recompile). FREE.</summary>
    long BlockCacheHits { get; }

    /// <summary>Block-cache misses — a dispatch had to compile (first compile OR recompile). FREE.</summary>
    long BlockCacheMisses { get; }
}
