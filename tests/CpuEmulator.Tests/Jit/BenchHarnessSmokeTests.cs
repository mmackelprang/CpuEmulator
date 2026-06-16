using CpuEmulator.Benchmarks;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 8: smoke tests for the comparative-benchmark harness CORE (the wiring-choice-(b)
/// library the runner + this test share). These pin that the two-tier measurement runs + self-verifies
/// and that the graceful-degradation seam (an absent adapter is skipped-with-note, never a crash)
/// works — NOT perf assertions. They use W2 (the short arithmetic kernel) so they stay fast; W1
/// (Klaus, 96M cycles) is the runner's job, not the routine suite's.</summary>
public class BenchHarnessSmokeTests
{
    [Fact]
    public void Two_tier_measurement_runs_and_reports_positive_cycles_per_second()
    {
        var w2 = Workloads.ArithmeticKernel();

        // Both tiers run the W2 kernel to its fixed cycle cap and self-verify (the runner throws on a
        // cycle/trap divergence); each returns the cycle count run.
        long interpCycles = Tier0.Run(w2);
        long jitCycles = Tier1.Run(w2);

        Assert.True(interpCycles >= w2.FixedCycleCap, $"interpreter ran {interpCycles}, expected >= the cap");
        Assert.True(jitCycles >= w2.FixedCycleCap, $"jit ran {jitCycles}, expected >= the cap");

        // The harness facade reports a positive cycles/sec for both (the smoke check — it RAN).
        var t0 = BenchHarness.MeasureTier("interpreter", Tier0.Run, w2);
        var t1 = BenchHarness.MeasureTier("jit", Tier1.Run, w2);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"Tier-1 row: {t1}");
    }

    [Fact]
    public void The_two_tiers_agree_on_the_W2_cycle_count()
    {
        // Chaining is transparent to cycle accounting (Task 2 pin), and the JIT charges the same
        // templates as the interpreter — so both tiers reach the same cycle count on the deterministic
        // kernel (bounded by the one-instruction overshoot at the cap boundary, identical for both).
        var w2 = Workloads.ArithmeticKernel();
        Assert.Equal(Tier0.Run(w2), Tier1.Run(w2));
    }

    [Fact]
    public void The_two_Z80_tiers_run_and_agree_on_the_W2_cycle_count()
    {
        // The Z80-W2 kernel on BOTH our tiers: a wiring smoke (Ran==true + the two tiers agree on the
        // cycle count) — NOT a throughput assertion (D5). Uses the real Z80ArithmeticKernel; both tiers
        // run it to the same fixed cap, so they reach the same T-state count (bounded by the
        // one-instruction overshoot at the cap boundary, identical for both — mirrors the 6502 W2 pin).
        var z80w2 = Z80Workloads.Z80ArithmeticKernel();

        long interpCycles = Tier0.Run(z80w2);
        long jitCycles = Tier1.Run(z80w2);
        Assert.True(interpCycles >= z80w2.FixedCycleCap, $"Z80 interpreter ran {interpCycles}, expected >= the cap");
        Assert.Equal(interpCycles, jitCycles);

        var t0 = BenchHarness.MeasureTier("z80 interpreter", Tier0.Run, z80w2);
        var t1 = BenchHarness.MeasureTier("z80 jit", Tier1.Run, z80w2);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"Z80 Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"Z80 Tier-1 row: {t1}");
    }

    [Fact]
    public void The_two_Z80_tiers_compose_on_the_W1_prefix_when_the_binary_is_present()
    {
        // Z80-W1 (ZEXDOC prefix) is gated on the ZEX binary at runtime (no custom attribute): absent =>
        // the test still passes (the W1 wiring is the runner's job; the routine suite must not depend on
        // a fetched binary). When present, run a TINY bounded window on both tiers and assert the wiring
        // composes (Ran==true) — NOT throughput, NOT the full 2B-T-state window (that would be far too
        // slow for the routine suite).
        var w1 = Z80Workloads.Z80ZexPrefixOrNull();
        if (w1 is null) return;   // skip-with-note equivalent: ZEX binary not fetched

        // A tiny window: clone the workload with a small cap so the smoke stays fast. The driver runs
        // real ZEX code + services BDOS; we only need it to compose + advance, not to close the full
        // committed window.
        var tiny = w1 with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };

        var t0 = BenchHarness.MeasureTier("z80 interpreter", Tier0.Run, tiny);
        var t1 = BenchHarness.MeasureTier("z80 jit", Tier1.Run, tiny);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"Z80-W1 Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"Z80-W1 Tier-1 row: {t1}");
    }

    [Fact]
    public void An_absent_adapter_is_skipped_with_a_note()
    {
        var w2 = Workloads.ArithmeticKernel();
        var rows = BenchHarness.MeasureAdapters([new AbsentAdapter()], w2);

        var row = Assert.Single(rows);
        Assert.False(row.Result.Ran);
        Assert.Contains("deliberately absent", row.Result.Note);
    }

    [Fact]
    public void Report_generator_writes_our_two_tiers_and_marks_absent_subjects()
    {
        var w2 = Workloads.ArithmeticKernel();
        var tierRows = new List<BenchHarness.Row>
        {
            new("our Tier-0 interpreter", w2.Name, BenchHarness.MeasureTier("our Tier-0 interpreter", Tier0.Run, w2)),
            new("our Tier-1 JIT", w2.Name, BenchHarness.MeasureTier("our Tier-1 JIT", Tier1.Run, w2)),
        };
        var adapterRows = BenchHarness.MeasureAdapters([new AbsentAdapter()], w2);

        string md = ReportWriter.Render(tierRows, adapterRows, klausAvailable: false);

        Assert.Contains("our Tier-0 interpreter", md);
        Assert.Contains("our Tier-1 JIT", md);
        Assert.Contains("_not run_", md);                  // the absent adapter's row
        Assert.Contains("deliberately absent", md);        // its reason
        Assert.Contains("cycles per host-second", md);     // the results table header
        Assert.Contains("Regenerate", md);                 // the populate instructions
    }

    /// <summary>A fake adapter that always probes absent — the graceful-degradation seam under test.</summary>
    private sealed class AbsentAdapter : IEmulatorAdapter
    {
        public string Name => "fake-absent (test)";
        public bool Probe(out string reason) { reason = "deliberately absent (test fixture)"; return false; }
        public AdapterResult Measure(BenchWorkload workload) =>
            throw new InvalidOperationException("Measure must not be called when Probe returns false");
    }
}
