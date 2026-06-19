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
    public void The_two_68000_tiers_run_and_agree_on_the_W2_cycle_count()
    {
        // Milestone B — the 68000 W2 (ALU/branch) kernel on BOTH our tiers: a wiring smoke (Ran==true +
        // the two tiers reach the cap and agree within the coarse-cycle slack), NOT a throughput assertion (D5).
        // Both tiers are dependency-free hand-written kernels (Option A — no external exerciser), so this
        // ALWAYS runs. A TINY bounded window keeps the routine suite fast (the committed 50M-cycle window is
        // the runner's job).
        //
        // ROOT CAUSE of the cycle slack (follow-up #21, the "W2 bench-harness cycle off-by-2", RESOLVED — this
        // is expected coarse-cycle MODEL slack, NOT a boundary bug; see ADR 0011 §4 / DECISION T2):
        //   The 68000 JIT EMITS real ALU/MOVE/Bcc IL charging each descriptor's COARSE BaseCycles plus a uniform
        //   +1 opcode-fetch cycle per emitted instruction (BlockCompiler.cs EmitChargeOneCycle). The tier-0
        //   interpreter instead charges its exact 4-clock-per-consumed-word prefetch model. These two per-
        //   instruction cycle models legitimately DIFFER (e.g. for this kernel the interp inner loop sums 24
        //   cycles/iteration vs the JIT's 31), so the two tiers cross the FixedCycleCap on different instructions
        //   and round UP to different instruction boundaries: the TierRunner loop (Tiers.cs) is symmetric — both
        //   tiers check `CycleCount < target` then advance ONE budget-1 instruction — so each stops at the first
        //   instruction boundary >= the cap. Interp lands on exactly 2_000_000; the JIT overshoots to 2_000_002
        //   (the off-by-2 — note the JIT is the HIGHER tier, not the interpreter). The DATA axis stays byte-
        //   identical across the full corpus (M68000JitAluFamilyTests proves it; the 68000 parity gate never
        //   compares CycleCount). Only the cycle COUNT diverges — the documented coarse-cycle stance.
        //
        // The principled tolerance is therefore "one instruction's worst-case cycle charge": each tier rounds up
        // by AT MOST one instruction past the cap, so the gap is bounded by the largest single per-instruction
        // charge across both tiers (11 for a JIT Bcc here). We assert <= 16 (a small, root-cause-justified margin
        // over that bound) — tight enough to still catch a real divergence (a diverged subject blows far past it),
        // loose enough to not be brittle to the exact kernel mix. NOT exact equality (which would contradict
        // DECISION T2). The observed gap is exactly 2.
        var w2 = M68000Workloads.ArithmeticKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };

        long interpCycles = Tier0.Run(w2);
        long jitCycles = Tier1.Run(w2);
        Assert.True(interpCycles >= w2.FixedCycleCap, $"68000 interpreter ran {interpCycles}, expected >= the cap");
        Assert.True(jitCycles >= w2.FixedCycleCap, $"68000 jit ran {jitCycles}, expected >= the cap");
        Assert.True(System.Math.Abs(interpCycles - jitCycles) <= 16,
            $"68000 W2 tier cycle counts diverge by more than the coarse-cycle instruction-boundary slack " +
            $"(one instruction's worst-case charge): interp={interpCycles}, jit={jitCycles}");

        var t0 = BenchHarness.MeasureTier("m68000 interpreter", Tier0.Run, w2);
        var t1 = BenchHarness.MeasureTier("m68000 jit", Tier1.Run, w2);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"68000 Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"68000 Tier-1 row: {t1}");
    }

    [Fact]
    public void The_two_68000_tiers_compose_on_the_W1_mixed_kernel_and_report_instructions_per_second()
    {
        // Milestone B — the 68000 W1 (deterministic mixed stream) on BOTH our tiers: it composes
        // (Ran==true) AND the cycle-axis-independent instructions/sec metric (Task B2) is reported
        // non-zero. The 68000 is the only wired CPU that attributes a per-instruction count (it advances
        // by a budget-1 Run / Step) — its trustworthy headline (the cycle axis is partial on `main`).
        var w1 = M68000Workloads.MixedKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };

        var t0 = BenchHarness.MeasureTierCounted("m68000 interpreter", Tier0.RunCounted, w1);
        var t1 = BenchHarness.MeasureTierCounted("m68000 jit", Tier1.RunCounted, w1);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"68000-W1 Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"68000-W1 Tier-1 row: {t1}");
        // The B2 seam: the 68000 reports a non-zero guest instructions/sec (the cycle-axis-independent
        // headline). This is what the 6502/Z80 leave 0 ("not reported").
        Assert.True(t0.InstructionsPerSecond > 0, $"68000-W1 Tier-0 should report instructions/sec: {t0}");
        Assert.True(t1.InstructionsPerSecond > 0, $"68000-W1 Tier-1 should report instructions/sec: {t1}");
    }

    [Fact]
    public void The_two_6502_tiers_run_and_agree_on_the_W3_sieve_cycle_count()
    {
        // W3 — the 6502 Sieve compute kernel (Dhrystone-class) on BOTH our tiers: a wiring smoke
        // (Ran==true + the two tiers agree on the cycle count), NOT a throughput assertion. A TINY
        // bounded window keeps the routine suite fast (the committed 50M-cycle window is the runner's
        // job). Both tiers run the same deterministic kernel to the same cap, so they reach the same
        // cycle count (bounded by the one-instruction overshoot at the cap boundary, identical for both).
        var sieve = Workloads.SieveKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };

        long interpCycles = Tier0.Run(sieve);
        long jitCycles = Tier1.Run(sieve);
        Assert.True(interpCycles >= sieve.FixedCycleCap, $"6502 Sieve interpreter ran {interpCycles}, expected >= the cap");
        Assert.Equal(interpCycles, jitCycles);

        var t0 = BenchHarness.MeasureTier("6502 interpreter", Tier0.Run, sieve);
        var t1 = BenchHarness.MeasureTier("6502 jit", Tier1.Run, sieve);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"6502 Sieve Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"6502 Sieve Tier-1 row: {t1}");
    }

    [Fact]
    public void The_two_Z80_tiers_run_and_agree_on_the_W3_sieve_cycle_count()
    {
        // Z80-W3 — the Z80 Sieve compute kernel (Dhrystone-class) on BOTH our tiers: a wiring smoke
        // (Ran==true + the two tiers agree on the T-state count), NOT a throughput assertion. A TINY
        // bounded window keeps the routine suite fast. Both tiers run the same kernel to the same cap,
        // so they reach the same T-state count (one-instruction overshoot at the cap, identical for both).
        var sieve = Z80Workloads.Z80SieveKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };

        long interpCycles = Tier0.Run(sieve);
        long jitCycles = Tier1.Run(sieve);
        Assert.True(interpCycles >= sieve.FixedCycleCap, $"Z80 Sieve interpreter ran {interpCycles}, expected >= the cap");
        Assert.Equal(interpCycles, jitCycles);

        var t0 = BenchHarness.MeasureTier("z80 interpreter", Tier0.Run, sieve);
        var t1 = BenchHarness.MeasureTier("z80 jit", Tier1.Run, sieve);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"Z80 Sieve Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"Z80 Sieve Tier-1 row: {t1}");
    }

    [Fact]
    public void The_two_68000_tiers_run_and_agree_on_the_W3_sieve_cycle_count()
    {
        // m68k-W3 — the 68000 Sieve compute kernel (Dhrystone-class) on BOTH our tiers: a wiring smoke
        // (Ran==true + the two tiers reach the cap and agree within the coarse-cycle slack), NOT a throughput
        // assertion. A TINY bounded window (the committed 50M-cycle window is the runner's job).
        // Same RESOLVED root cause as the W2 smoke above (follow-up #21 / ADR 0011 §4 / DECISION T2): the 68000
        // JIT emits real IL charging COARSE BaseCycles + a uniform per-instruction opcode-fetch cycle, whose
        // per-instruction model legitimately differs from the interpreter's exact word-refill model, so the two
        // tiers round UP to different instruction boundaries at the cap (an instruction-boundary slack, NOT a
        // boundary bug). The DATA axis stays byte-identical (M68000JitAluFamilyTests); only the cycle COUNT
        // diverges. Same principled tolerance — "one instruction's worst-case charge" — so assert <= 16, not
        // exact equality. See the W2 smoke's root-cause comment for the full mechanism.
        var sieve = M68000Workloads.SieveKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };

        long interpCycles = Tier0.Run(sieve);
        long jitCycles = Tier1.Run(sieve);
        Assert.True(interpCycles >= sieve.FixedCycleCap, $"68000 Sieve interpreter ran {interpCycles}, expected >= the cap");
        Assert.True(jitCycles >= sieve.FixedCycleCap, $"68000 Sieve jit ran {jitCycles}, expected >= the cap");
        Assert.True(System.Math.Abs(interpCycles - jitCycles) <= 16,
            $"68000 W3 tier cycle counts diverge by more than the coarse-cycle instruction-boundary slack " +
            $"(one instruction's worst-case charge): interp={interpCycles}, jit={jitCycles}");

        var t0 = BenchHarness.MeasureTier("m68000 interpreter", Tier0.Run, sieve);
        var t1 = BenchHarness.MeasureTier("m68000 jit", Tier1.Run, sieve);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"68000 Sieve Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"68000 Sieve Tier-1 row: {t1}");
    }

    [Fact]
    public void The_two_8086_tiers_run_and_agree_on_the_W2_cycle_count()
    {
        // M6 PR-A — the 8086 W2 (ALU/branch) kernel on BOTH our tiers: a wiring smoke (Ran==true + the
        // two tiers reach the cap and roughly agree on the cycle count), NOT a throughput assertion (D5). Both
        // tiers are dependency-free hand-written kernels (no external exerciser), so this ALWAYS runs. A TINY
        // bounded window keeps the routine suite fast (the committed 50M-cycle window is the runner's job).
        // M6 PR-B: the 8086 JIT now EMITS real MOV IL charging the descriptor's COARSE BaseCycles (DECISION B-4 —
        // the 8086 emit gate is the DATA axis only; cycles are carried-not-asserted, NOT cycle-exact). So the two
        // tiers no longer reach a BIT-IDENTICAL cycle count once the kernel contains MOVs; they agree within a
        // small instruction-boundary slack — EXACTLY the 68000 W2 coarse-cycle stance above (backlog: the W2
        // bench-harness cycle off-by-2). The DATA axis stays byte-identical (the M8088 JIT MOV sweep proves that);
        // only the cycle COUNT diverges. Assert both tiers reach the cap and the JIT is within a small window.
        var w = M8086Workloads.ArithmeticKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };
        long t0 = Tier0.Run(w);
        long t1 = Tier1.Run(w);
        Assert.True(t0 >= 2_000_000, $"8086 W2 interpreter ran {t0}, expected >= the cap");
        Assert.True(t1 >= 2_000_000, $"8086 W2 jit ran {t1}, expected >= the cap");
        Assert.True(System.Math.Abs(t0 - t1) <= 64,
            $"8086 W2 tier cycle counts diverge by more than the coarse-cycle slack: interp={t0}, jit={t1}");
        var r0 = BenchHarness.MeasureTier("our Tier-0 interpreter", Tier0.Run, w);
        var r1 = BenchHarness.MeasureTier("our Tier-1 JIT", Tier1.Run, w);
        Assert.True(r0.Ran && r0.CyclesPerSecond > 0);
        Assert.True(r1.Ran && r1.CyclesPerSecond > 0);
    }

    [Fact]
    public void The_two_8086_tiers_compose_on_the_W1_mixed_kernel_and_report_instructions_per_second()
    {
        // M6 PR-A — the 8086 W1 (deterministic mixed stream) on BOTH our tiers: it composes (Ran==true)
        // AND the cycle-axis-independent instructions/sec metric (Task B2) is reported non-zero. The 8086
        // driver attributes a per-instruction count (it advances by a budget-1 Run / Step) — its
        // trustworthy headline (the cycle axis is rudimentary on `main`).
        var w1 = M8086Workloads.MixedKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };

        var t0 = BenchHarness.MeasureTierCounted("m8086 interpreter", Tier0.RunCounted, w1);
        var t1 = BenchHarness.MeasureTierCounted("m8086 jit", Tier1.RunCounted, w1);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"8086-W1 Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"8086-W1 Tier-1 row: {t1}");
        // The B2 seam: the 8086 reports a non-zero guest instructions/sec (the cycle-axis-independent
        // headline). This is what the 6502/Z80 leave 0 ("not reported").
        Assert.True(t0.InstructionsPerSecond > 0, $"8086-W1 Tier-0 should report instructions/sec: {t0}");
        Assert.True(t1.InstructionsPerSecond > 0, $"8086-W1 Tier-1 should report instructions/sec: {t1}");
    }

    [Fact]
    public void The_two_8086_tiers_run_and_agree_on_the_W3_sieve_cycle_count()
    {
        // 8086-W3 — the 8086 compute kernel on BOTH our tiers: a wiring smoke (Ran==true + the two tiers reach
        // the cap and roughly agree on the cycle count), NOT a throughput assertion. M6 PR-B: the JIT now EMITS
        // real MOV IL with COARSE BaseCycles (DECISION B-4 — the gate is the DATA axis only, cycles carried-not-
        // asserted), so the two tiers no longer reach a BIT-IDENTICAL cycle count on a MOV-bearing kernel; they
        // agree within a small instruction-boundary slack (the same coarse-cycle stance as the 68000 W2/W3 above).
        // The DATA axis stays byte-identical (the M8088 JIT MOV sweep proves that); only the cycle COUNT diverges.
        var sieve = M8086Workloads.SieveKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };

        long interpCycles = Tier0.Run(sieve);
        long jitCycles = Tier1.Run(sieve);
        Assert.True(interpCycles >= sieve.FixedCycleCap, $"8086 Sieve interpreter ran {interpCycles}, expected >= the cap");
        Assert.True(jitCycles >= sieve.FixedCycleCap, $"8086 Sieve jit ran {jitCycles}, expected >= the cap");
        Assert.True(System.Math.Abs(interpCycles - jitCycles) <= 64,
            $"8086 W3 tier cycle counts diverge by more than the coarse-cycle slack: interp={interpCycles}, jit={jitCycles}");

        var t0 = BenchHarness.MeasureTier("m8086 interpreter", Tier0.Run, sieve);
        var t1 = BenchHarness.MeasureTier("m8086 jit", Tier1.Run, sieve);
        Assert.True(t0.Ran && t0.CyclesPerSecond > 0, $"8086 Sieve Tier-0 row: {t0}");
        Assert.True(t1.Ran && t1.CyclesPerSecond > 0, $"8086 Sieve Tier-1 row: {t1}");
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
