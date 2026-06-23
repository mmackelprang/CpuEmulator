namespace CpuEmulator.Surface.Web;

/// <summary>The IL-JIT run-lifetime counters carried in the <c>PF</c> frame's <c>jit</c> object — present
/// only when the machine runs on the JIT tier (design handoff 2026-06-23-perf-overlay §6.2). Mirrors
/// <see cref="CpuEmulator.Core.IJitMetrics"/>; the client formats it to <c>c&lt;N&gt; r&lt;N&gt;
/// e&lt;N&gt; smc&lt;N&gt;</c>.</summary>
public sealed record JitStats(int Compiled, long Recompiled, long Evicted, int SmcHot);

/// <summary>The host→client read-only PERFORMANCE/telemetry snapshot carried by the <c>PF</c> text frame
/// (design handoff 2026-06-23-perf-overlay §6.2) — the perf-overlay HUD's only server-side data source.
/// Distinct from <see cref="MachineStatus"/> (the <c>ST</c> frame): <c>ST</c> is machine STATE pushed
/// on-change (drives/mode/board the drive panels bind to); <c>PF</c> is TELEMETRY pushed unconditionally at
/// ~3 Hz (rates that always move). Every field is REAL machine/host state read at push time — nothing is
/// fabricated. FPS is intentionally absent: it is measured client-side from FB-frame arrivals (§4), so the
/// server never owns it. The ips (instructions/sec) row is deferred to a follow-on (an honest retired-
/// instruction counter needs generator + JIT-IL work — Architect call) and is omitted from the wire here.
/// <para><see cref="NominalClockHz"/> is null when the board declares no clock — the client then shows the
/// guest rate with NO ratio suffix (never a faked <c>· NaN×</c>). <see cref="Jit"/> is null on the
/// interpreter tier (the client omits the jit row). <see cref="Coprocessor"/> is null on single-CPU boards
/// (the client omits the cpu2 row).</para></summary>
public sealed record PerfStats(
    string Board,
    double CyclesPerSecond,
    double? NominalClockHz,
    long RamBytes,
    long HostWorkingSetBytes,
    bool IsJitted,
    JitStats? Jit,
    CoprocessorStatus? Coprocessor);

/// <summary>The coprocessor row of the <c>PF</c> frame (the SoftCard Z80): its display name + whether it is
/// the live bus master. Present only when the board has a coprocessor; the client renders <c>&lt;name&gt;
/// active</c> / <c>&lt;name&gt; idle</c> (fallback <c>coproc active/idle</c> when the name is unknown).</summary>
public sealed record CoprocessorStatus(string Name, bool Active);
