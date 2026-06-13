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
    /// <summary>One measured row in the report.</summary>
    public sealed record Row(string Subject, string Workload, AdapterResult Result);

    /// <summary>The default third-party adapter set (the four shims). Our two tiers are measured
    /// separately (always); these are opt-in + degrade gracefully.</summary>
    public static IReadOnlyList<IEmulatorAdapter> DefaultAdapters() =>
    [
        new Asm6502Adapter(),
        new Fake6502Adapter(),
        new Py65Adapter(),
        new JsEmulatorAdapter(),
    ];

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
            rows.Add(new Row(a.Name, w.Name, r));
        }
        return rows;
    }
}
