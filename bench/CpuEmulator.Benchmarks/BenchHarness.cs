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
    /// <summary>The per-measurement wall-clock ceiling (default). A tier run that would exceed this is
    /// STOPPED at the deadline and reported as a VALID-but-CAPPED measurement: cycles/sec is computed
    /// over the cycles ACTUALLY executed in the bounded window (same rate, bounded time), flagged
    /// <see cref="AdapterResult.Capped"/>. This bounds an SMC-pathological run (the 6502 W1 Klaus JIT
    /// thrashes the recompiler — ~37.5s uncapped) so the whole benchmark always completes in a few
    /// minutes. A fast workload (a W2/W3 kernel, sub-second) never reaches the deadline, so it is
    /// byte-for-byte unchanged by the cap.</summary>
    public static readonly TimeSpan PerMeasurementWallCap = TimeSpan.FromSeconds(10);

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
        // The 68000 reference set: Musashi (kstenerud/Musashi, MIT) is the HEAD-TO-HEAD C reference —
        // a C subprocess (compiled-once-cached, including Musashi's m68kmake opcode-table codegen) run
        // on the SAME workload bytes + host as our tiers. It skip-with-notes when its source (run
        // bench/third-party/fetch-subjects) or a C compiler is absent, leaving the cited published
        // placeholder in place; when it runs, the merged comparison-table generator promotes this
        // measured ‡ row over the cited Musashi row automatically (it gates cited rows on
        // cpuHasHeadToHead — no generator change needed).
        "m68000" => [ new MusashiAdapter() ],
        "m8086" => [],   // M6 PR-A: our-tiers-only baseline. A head-to-head 8086 C reference is the M6
                         // plan §8 Q3 evaluation (deferred); a cited row can be added to reference-numbers.json
                         // when chosen. [] = no third-party adapter -> the comparison "best existing" cell is
                         // empty/cited, never a mis-matched 6502 shim.
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

    /// <summary>Measure one tier capturing BOTH cycles AND the guest instruction count, with a
    /// per-measurement WALL-CLOCK CAP (Task 1). Identical to the uncapped
    /// <see cref="MeasureTierCounted(string, Func{BenchWorkload, TierRunResult}, BenchWorkload)"/>
    /// except the cap-aware <paramref name="run"/> stops at the wall deadline and returns
    /// <see cref="TierRunResult.Capped"/>; the resulting row carries cycles/sec over the bounded window
    /// (the SAME rate, just time-bounded — no data lost) plus the <see cref="AdapterResult.Capped"/>
    /// flag. The cap is applied to BOTH the warmup pass (short workloads) and the timed pass, so a
    /// pathological warmup cannot stall either. A null <paramref name="wallCap"/> defaults to
    /// <see cref="PerMeasurementWallCap"/>.</summary>
    /// <param name="tierName">The subject label recorded as the row's version note.</param>
    /// <param name="run">The cap-aware tier run (e.g. <c>Tier0.RunCounted</c> / <c>Tier1.RunCounted</c>):
    /// it takes the workload + the wall cap and returns the cycles/instructions executed + whether it
    /// was capped.</param>
    /// <param name="w">The workload to measure.</param>
    /// <param name="wallCap">The wall-clock ceiling; null ⇒ <see cref="PerMeasurementWallCap"/>.</param>
    public static AdapterResult MeasureTierCounted(string tierName,
                                                   Func<BenchWorkload, TimeSpan?, TierRunResult> run,
                                                   BenchWorkload w,
                                                   TimeSpan? wallCap)
    {
        try
        {
            TimeSpan cap = wallCap ?? PerMeasurementWallCap;
            bool warmup = w.FixedCycleCap is not null;   // W2 (short) warms; W1 (long) self-warms
            if (warmup) run(w, cap);                      // the warmup pass respects the cap too
            var sw = Stopwatch.StartNew();
            TierRunResult r = run(w, cap);
            sw.Stop();
            double wall = sw.Elapsed.TotalSeconds;
            // cycles/sec = (cycles ACTUALLY executed) / (wall elapsed): correct whether or not the run
            // was capped — a capped run executed r.Cycles in the bounded window the outer Stopwatch timed.
            return r.Instructions > 0
                ? AdapterResult.MeasuredWithInstructions(r.Cycles, r.Instructions, wall, tierName, capped: r.Capped)
                : AdapterResult.Measured(r.Cycles, wall, tierName, capped: r.Capped);
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
