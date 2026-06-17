using System.Diagnostics;
using CpuEmulator.Benchmarks.Adapters;

namespace CpuEmulator.Benchmarks;

/// <summary>The harness facade the runner + the smoke test share (wiring-choice (b)): it measures
/// our two tiers (always), probes + measures each third-party adapter (skip-with-note when absent),
/// and renders the regenerable report. Our-tiers timing here uses a single warmed Stopwatch pass —
/// the BenchmarkDotNet harness (TierBenchmarks, in the runner) is the statistically-rigorous twin;
/// this facade's numbers are the report's headline rows + the smoke test's "it runs + self-verifies"
/// check, NOT a replacement for BDN's warmup/measurement windows.</summary>
public static class BenchHarness
{
    /// <summary>One measured row in the report. <see cref="Architecture"/> groups + labels the row by
    /// CPU in the report (6502 → "cycles", Z80 → "T-states"); it defaults to "mos6502" so existing
    /// callers + tests that do not thread an architecture stay on the 6502 path.</summary>
    public sealed record Row(string Subject, string Workload, AdapterResult Result,
                             string Architecture = "mos6502");

    /// <summary>The default third-party adapter set (the four 6502 shims) — kept for back-compat;
    /// delegates to <see cref="AdaptersFor"/> for the 6502.</summary>
    public static IReadOnlyList<IEmulatorAdapter> DefaultAdapters() => AdaptersFor("mos6502");

    /// <summary>The third-party adapter set for an architecture. The 6502 returns the existing four
    /// shims; the Z80 returns the cross-language set (Z80dotNet C# in-process, superzazu/z80 C
    /// subprocess, DrGoldfire Z80.js node subprocess — Tasks A6/A7/A8). Each adapter Probes for its
    /// runtime/source + degrades to a skip-with-note row when absent (the load-bearing invariant: a
    /// missing toolchain degrades exactly one row, never blocks the baseline).</summary>
    public static IReadOnlyList<IEmulatorAdapter> AdaptersFor(string architecture) => architecture switch
    {
        "z80" =>
        [
            new Z80SharpAdapter(),   // A6 — C# in-process (Z80dotNet)
            new Z80CAdapter(),       // A7 — C subprocess (superzazu/z80, compiled-once-cached)
            new Z80JsAdapter(),      // A8 — JS node subprocess (DrGoldfire/Z80.js) — optional
        ],
        // The 68000 reference set (Milestone B has NO third-party 68000 subject yet — the our-tiers
        // baseline always commits, R10). The Musashi head-to-head reference (or its cited fallback) is
        // Task M4 of the comparison-framework milestone; it adds `new MusashiAdapter()` here, which
        // skip-with-notes when its source/compiler is absent. Until then the 68000 section is our two
        // tiers only — exactly the "before" baseline this milestone captures.
        "m68000" => [],
        _ =>
        [
            new Asm6502Adapter(),
            new Fake6502Adapter(),
            new Py65Adapter(),
            new JsEmulatorAdapter(),
        ],
    };

    /// <summary>Measure one tier on one workload with a Stopwatch pass; self-verifies via the tier
    /// runner (which throws on a cycle/trap divergence — so a wrong number never reaches a row). A
    /// warmup pass runs first for SHORT workloads (W2 — RyuJIT + the block cache need warming before
    /// the timed pass); for the LONG W1 run a separate warmup pass would double a ~minute-long run for
    /// no benefit (a 96M-cycle run self-warms in its first slices, then runs steady-state), so warmup
    /// is skipped there.</summary>
    public static AdapterResult MeasureTier(string tierName, Func<BenchWorkload, long> run, BenchWorkload w)
    {
        try
        {
            bool warmup = w.FixedCycleCap is not null;   // W2 (short) warms; W1 (long) self-warms
            if (warmup) run(w);
            var sw = Stopwatch.StartNew();
            long cycles = run(w);
            sw.Stop();
            return AdapterResult.Measured(cycles, sw.Elapsed.TotalSeconds, tierName);
        }
        catch (Exception ex)
        {
            return AdapterResult.Skipped($"tier run failed: {ex.Message}");
        }
    }

    /// <summary>Measure one tier capturing BOTH cycles AND the guest instruction count (Task B2). Same
    /// warmed-Stopwatch pass as <see cref="MeasureTier"/>; the row carries instructions/sec when the
    /// driver attributes a per-instruction count (the 68000) and 0 ("not reported") otherwise (the
    /// 6502/Z80 W2 JIT path) — additive, so the existing cycles/sec is unchanged. The 68000 baseline
    /// leads with instructions/sec because its cycle axis is partial on `main` (M4.5d-2 gating).</summary>
    public static AdapterResult MeasureTierCounted(string tierName, Func<BenchWorkload, TierRunResult> run, BenchWorkload w)
    {
        try
        {
            bool warmup = w.FixedCycleCap is not null;   // W2 (short) warms; W1 (long) self-warms
            if (warmup) run(w);
            var sw = Stopwatch.StartNew();
            TierRunResult r = run(w);
            sw.Stop();
            double wall = sw.Elapsed.TotalSeconds;
            return r.Instructions > 0
                ? AdapterResult.MeasuredWithInstructions(r.Cycles, r.Instructions, wall, tierName)
                : AdapterResult.Measured(r.Cycles, wall, tierName);
        }
        catch (Exception ex)
        {
            return AdapterResult.Skipped($"tier run failed: {ex.Message}");
        }
    }

    /// <summary>Probe + measure every adapter on a workload, producing rows. An adapter whose Probe
    /// returns false yields a skip-with-note row; the run never throws on an absent subject.</summary>
    public static List<Row> MeasureAdapters(IEnumerable<IEmulatorAdapter> adapters, BenchWorkload w)
    {
        var rows = new List<Row>();
        foreach (var a in adapters)
        {
            AdapterResult r;
            try
            {
                r = a.Probe(out string reason) ? a.Measure(w) : AdapterResult.Skipped(reason);
            }
            catch (Exception ex)
            {
                r = AdapterResult.Skipped($"adapter threw in Probe/Measure: {ex.Message}");
            }
            rows.Add(new Row(a.Name, w.Name, r, w.Architecture));
        }
        return rows;
    }
}
