using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuEmulator.Profiler;

// The versioned, diffable profile.json schema (ADR 0022 §4.2). A plain serializable record set + the
// shared System.Text.Json options. Kept deliberately small + flat so a `git diff` of a profile is the
// before/after of a profiling turn. Real-numbers-only; a tier/field that does not apply is null (with a
// note), never a faked value.

/// <summary>The host stamp — who/what produced this profile (diffability context).</summary>
public sealed record HostInfo(string Cpu, string Os, string Dotnet);

/// <summary>One mnemonic row of the top-N hot-op histogram.</summary>
public sealed record HotOp(string Mnemonic, long Count, double Pct, double CumPct);

/// <summary>One opcode row of the (best-effort / deferred) fallback-by-opcode histogram. Full
/// execution-weighted per-opcode attribution is ADR 0022 item D — a follow-on; this first cut emits an
/// empty list (see <see cref="JitTierProfile.FallbackByOpcode"/>).</summary>
public sealed record FallbackOpcode(string Opcode, string Mnemonic, long Count, double Pct);

/// <summary>The interpreter-tier capture for one run.</summary>
public sealed record InterpreterTierProfile
{
    /// <summary>Guest instructions retired over the window. The hot-op histogram path (kernels + the
    /// single-CPU real-boot interpreter walk) steps one instruction at a time, so it has a REAL count;
    /// the bulk-Run path (dual-CPU SoftCard boots) leaves it 0 (a note records that).</summary>
    public long InstructionsRetired { get; init; }

    /// <summary>CycleCount / wallSeconds over the window.</summary>
    public double CyclesPerSecond { get; init; }

    /// <summary>cyclesPerSecond / NominalClockHz when the board declares a clock, else null.</summary>
    public double? RealtimeRatio { get; init; }

    /// <summary>The top-N mnemonic histogram (empty when the histogram was skipped for this run — see
    /// the notes).</summary>
    public IReadOnlyList<HotOp> HotOps { get; init; } = [];

    /// <summary>GC.GetTotalAllocatedBytes() delta across the window (a coarse SAMPLED-style number).</summary>
    public long AllocBytesPerWindow { get; init; }
}

/// <summary>The JIT-tier capture for one run.</summary>
public sealed record JitTierProfile
{
    public long InstructionsRetired { get; init; }
    public double CyclesPerSecond { get; init; }
    public double? RealtimeRatio { get; init; }

    /// <summary>Fraction of executed instructions that ran emitted IL (vs fell back). Best-effort: null
    /// for this first cut — full emit-coverage attribution rides on the item-D execution-weighted
    /// fallback-by-opcode histogram. A note records the deferral.</summary>
    public double? EmitCoverage { get; init; }

    /// <summary>Execution-weighted fallback-by-opcode — the top-ROI "what to emit next" feed. Deferred to
    /// ADR 0022 item D; emitted empty here (a note records it). NOT blocking for item A.</summary>
    public IReadOnlyList<FallbackOpcode> FallbackByOpcode { get; init; } = [];

    // The IJitMetrics counters, read off the running JIT after the window.
    public int CompileCount { get; init; }
    public long TotalRecompiles { get; init; }
    public long TotalEvictions { get; init; }
    public int SmcHotPcCount { get; init; }
    public long ChainEdgesTaken { get; init; }
    public long DispatcherEntries { get; init; }
    public long BlockCacheHits { get; init; }
    public long BlockCacheMisses { get; init; }

    public long AllocBytesPerWindow { get; init; }
}

/// <summary>Both tiers for one system x workload. A tier that does not apply (e.g. JIT for the
/// Pascal interpreter-only board) is null.</summary>
public sealed record TierSet
{
    public InterpreterTierProfile? Interpreter { get; init; }
    public JitTierProfile? Jit { get; init; }
}

/// <summary>One system x workload profile.json (ADR 0022 §4.2).</summary>
public sealed record SystemProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string GeneratedUtc { get; init; } = "";
    public string Commit { get; init; } = "";
    public HostInfo Host { get; init; } = new("", "", "");

    /// <summary>Stable system id (e.g. "apple2-dos33", "bench-6502").</summary>
    public string System { get; init; } = "";

    /// <summary>The frozen workload window (e.g. "boot-to-basic", "W1-klaus").</summary>
    public string Workload { get; init; } = "";

    /// <summary>The frozen budget the run used; <see cref="BudgetUnit"/> labels cycles vs instructions.</summary>
    public long InstructionBudget { get; init; }

    /// <summary>"cycles" or "instructions" — which unit <see cref="InstructionBudget"/> is in.</summary>
    public string BudgetUnit { get; init; } = "cycles";

    public TierSet Tiers { get; init; } = new();

    /// <summary>SAMPLED per-peripheral frame cost — null for this first cut (ADR 0022 item F infra).</summary>
    public IReadOnlyList<object>? PerPeripheralFrameCostNs { get; init; }

    /// <summary>Free-form notes: what was skipped/deferred/approximated for this run.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The shared serialization surface — one source of truth for the on-disk JSON shape so the
/// smoke test and the profiler agree.</summary>
public static class ProfileJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(SystemProfile profile) => JsonSerializer.Serialize(profile, Options);

    public static SystemProfile Deserialize(string json) =>
        JsonSerializer.Deserialize<SystemProfile>(json, Options)
        ?? throw new InvalidOperationException("profile.json deserialized to null");
}
