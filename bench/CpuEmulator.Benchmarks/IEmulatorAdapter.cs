namespace CpuEmulator.Benchmarks;

/// <summary>The result of attempting to benchmark one subject on one workload (Ground truth G).</summary>
/// <param name="Ran">false =&gt; skipped (runtime/source absent, or the subject diverged); see Note.</param>
/// <param name="CyclesPerSecond">valid only when Ran: emulated cycles / host wall-second.</param>
/// <param name="WallSeconds">the measured window in seconds (valid only when Ran).</param>
/// <param name="Note">a version string when Ran; the skip reason + populate-instruction otherwise.</param>
/// <param name="InstructionsPerSecond">Task B2 — guest INSTRUCTIONS / host wall-second over the same
/// window; the cycle-axis-independent metric the 68000 baseline leads with (its cycle axis is partial
/// on `main`, M4.5d-2 gating). 0 means "not reported" — a cycle-only subject (a subprocess that prints
/// only <c>CYCLES</c>, or the 6502/Z80 W2 JIT path that advances by one large budgeted Run and so does
/// not attribute a per-instruction count). guest-MIPS (M1) normalizes off this field.</param>
public readonly record struct AdapterResult(
    bool Ran,
    double CyclesPerSecond,
    double WallSeconds,
    string Note,
    double InstructionsPerSecond = 0)
{
    /// <summary>A skipped/absent subject: Ran=false with a clear reason + how to populate it.</summary>
    public static AdapterResult Skipped(string reason) => new(false, 0, 0, reason);

    /// <summary>A measured subject: cycles/host-second over the warmed window. No instruction count
    /// (InstructionsPerSecond stays 0 — "not reported"), so guest-MIPS renders "—" for this row.</summary>
    public static AdapterResult Measured(long cycles, double wallSeconds, string note) =>
        new(true, wallSeconds > 0 ? cycles / wallSeconds : 0, wallSeconds, note);

    /// <summary>A measured subject reporting BOTH cycles and instructions over the window (our tiers
    /// that drive by a budget-1 advance — the 68000 — and any reference core that surfaces an
    /// instruction count). The 68000 baseline leads with instructions/sec because the cycle axis is
    /// partial (M4.5d-2 gating); guest-MIPS (M1) normalizes off the instructions field.</summary>
    public static AdapterResult MeasuredWithInstructions(long cycles, long instructions, double wallSeconds, string note) =>
        new(true, wallSeconds > 0 ? cycles / wallSeconds : 0, wallSeconds, note,
            wallSeconds > 0 ? instructions / wallSeconds : 0);
}

/// <summary>A portable benchmark workload: the memory image, where it loads + starts, and how it
/// ends — either by parking at a success-trap PC (W1 Klaus) or by running a fixed cycle cap (W2
/// kernel). Exactly one of <see cref="SuccessTrapPc"/> / <see cref="FixedCycleCap"/> is the
/// termination condition. <see cref="ExpectedCycles"/> lets an adapter verify the subject actually
/// did the work (a diverged subject that finishes at a different cycle count is caught + reported as
/// Ran=false, never a fast-but-wrong number).
/// <para><see cref="Architecture"/> selects the per-CPU <c>ITierDriver</c> ("mos6502" / "z80"); it
/// also keys the third-party adapter set (<c>BenchHarness.AdaptersFor</c>) + the report grouping +
/// the cycle-unit label. <see cref="UsesCpmBdos"/> tells the Z80 driver to service the CP/M BDOS
/// CALL boundary (fn-2/fn-9 + host RET) + honor the warm-boot sentinel — true only for the ZEXDOC
/// prefix workload; the Z80 arithmetic kernel + the two 6502 workloads leave it false. Both new
/// params default to the 6502's values so the two existing 6502 workloads are unchanged.</para></summary>
public sealed record BenchWorkload(
    string Name,
    byte[] Image,
    ushort LoadAddress,
    ushort StartPc,
    ushort SuccessTrapPc,
    long? FixedCycleCap,
    long ExpectedCycles,
    string Architecture = "mos6502",
    bool UsesCpmBdos = false);

/// <summary>A benchmark subject: our two tiers and each third-party emulator implement this. The
/// harness calls <see cref="Probe"/> first; only if it returns true does it call <see cref="Measure"/>.
/// This is the graceful-degradation seam — an adapter whose runtime/source is absent returns
/// Probe()==false with a clear reason, and the harness records the row as "not run" without failing
/// the run (the vector-fetch / AOT-host pattern: absence is a skip-with-note, never a crash).</summary>
public interface IEmulatorAdapter
{
    /// <summary>Subject name + language for the report (e.g. "py65 (Python)").</summary>
    string Name { get; }

    /// <summary>Cheap, side-effect-free check: is this subject's runtime + source present? Returns
    /// false (with a populated reason) when not — the harness then records a "not run — {reason}"
    /// row and continues. MUST NOT throw.</summary>
    bool Probe(out string reason);

    /// <summary>Run <paramref name="workload"/> to its termination condition, measuring
    /// cycles/host-second over a warmed window. Called only when <see cref="Probe"/> returned true.
    /// Verifies the subject reached <see cref="BenchWorkload.ExpectedCycles"/>; on mismatch returns
    /// Ran=false ("subject diverged"), so a wrong emulator never contributes a misleading number.</summary>
    AdapterResult Measure(BenchWorkload workload);
}
