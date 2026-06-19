using System.Diagnostics;
using CpuEmulator.Benchmarks.Drivers;
using CpuEmulator.Jit;

namespace CpuEmulator.Benchmarks;

/// <summary>The two-tier (our interpreter + our JIT) measurement core — the wiring-choice-(b)
/// library shared by the BenchmarkDotNet runner AND the test suite's smoke test. Both tiers run the
/// SAME portable workload image, terminate on the SAME condition (the success trap for W1, the fixed
/// cycle cap for W2), and SELF-VERIFY against <see cref="BenchWorkload.ExpectedCycles"/> (W1 only —
/// W2's cap IS its expected count). A divergent run that reaches a different cycle count throws, so a
/// wrong number never reaches the report.</summary>
public static class Tier0
{
    /// <summary>Run the workload on the Tier-0 interpreter; return the emulated cycle count.</summary>
    public static long Run(BenchWorkload w) => TierRunner.Run(w, jit: false, new JitOptions());

    /// <summary>Run the workload on the Tier-0 interpreter; return BOTH the cycle count and the guest
    /// instruction count (Task B2). InstructionCount is 0 for drivers that do not attribute a
    /// per-instruction count (the 6502/Z80 W2 JIT path; the interpreter path may also leave it 0).</summary>
    public static TierRunResult RunCounted(BenchWorkload w) => TierRunner.RunCounted(w, jit: false, new JitOptions());

    /// <summary>Run the workload on the Tier-0 interpreter with a per-measurement WALL-CLOCK CAP
    /// (Task 1): the run stops at <paramref name="wallCap"/> and reports <see cref="TierRunResult.Capped"/>
    /// when it did. This is the cap-aware <c>Func&lt;BenchWorkload, TimeSpan?, TierRunResult&gt;</c> the
    /// runner threads through <see cref="BenchHarness.MeasureTierCounted(string, Func{BenchWorkload, System.TimeSpan?, TierRunResult}, BenchWorkload, System.TimeSpan?)"/>.</summary>
    public static TierRunResult RunCounted(BenchWorkload w, TimeSpan? wallCap) =>
        TierRunner.RunCounted(w, jit: false, new JitOptions(), wallCap);
}

public static class Tier1
{
    /// <summary>Run the workload on the Tier-1 JIT (chaining ON — the default); return the cycle
    /// count.</summary>
    public static long Run(BenchWorkload w) => TierRunner.Run(w, jit: true, new JitOptions());

    /// <summary>Run the workload on the Tier-1 JIT with explicit options (used by the dispatch /
    /// chaining micro-bench in Task 9 — e.g. DisableChaining).</summary>
    public static long Run(BenchWorkload w, JitOptions options) => TierRunner.Run(w, jit: true, options);

    /// <summary>Run the workload on the Tier-1 JIT (chaining ON); return BOTH the cycle count and the
    /// guest instruction count (Task B2).</summary>
    public static TierRunResult RunCounted(BenchWorkload w) => TierRunner.RunCounted(w, jit: true, new JitOptions());

    /// <summary>Run the workload on the Tier-1 JIT (chaining ON) with a per-measurement WALL-CLOCK CAP
    /// (Task 1): the run stops at <paramref name="wallCap"/> and reports <see cref="TierRunResult.Capped"/>
    /// when it did — the SMC-pathological 6502 W1 JIT bounds here. The cap-aware
    /// <c>Func&lt;BenchWorkload, TimeSpan?, TierRunResult&gt;</c> the runner threads through
    /// <see cref="BenchHarness.MeasureTierCounted(string, Func{BenchWorkload, System.TimeSpan?, TierRunResult}, BenchWorkload, System.TimeSpan?)"/>.</summary>
    public static TierRunResult RunCounted(BenchWorkload w, TimeSpan? wallCap) =>
        TierRunner.RunCounted(w, jit: true, new JitOptions(), wallCap);
}

/// <summary>The outcome of one counted tier run (Task B2): the emulated cycle count AND the guest
/// instruction count over the same window. <see cref="Instructions"/> is 0 when the driver does not
/// attribute a per-instruction count (the 6502/Z80 W2 JIT path advances by one large budgeted Run);
/// the 68000 driver reports a real count (it advances by a budget-1 Run / Step).
/// <para><see cref="Capped"/> (Task 1) is true when the run STOPPED at the per-measurement wall-clock
/// deadline rather than reaching its full cycle budget/trap — <see cref="Cycles"/> is then whatever was
/// actually executed in the bounded window (the downstream rate = Cycles / wall stays correct, just
/// time-bounded). It defaults to false so every existing <c>new TierRunResult(c, i)</c> is unchanged.</para></summary>
public readonly record struct TierRunResult(long Cycles, long Instructions, bool Capped = false);

internal static class TierRunner
{
    // The JIT's natural fast mode is a single large Run call (block-cached, chained); the interpreter
    // steps. The MEASURED WINDOW is a like-for-like ~ExpectedCycles bulk run of identical work for both
    // tiers — NOT the exact-trap-detection tail (a budget-1 walk to park is a correctness check, not a
    // throughput measurement, and the interpreter pays no such cost — including it would be unfair to
    // the JIT). Correctness/cycle-exactness is the Klaus functional test's job (KlausJitFunctionalTests
    // + the differential fuzzer), not the bench's; the bench measures throughput on identical work and
    // sanity-checks the run reached approximately the right cycle count (a diverged subject that
    // runs away or stalls is caught by the bound + the parked-early check).
    private const long BulkSlice = 8_000_000;

    // The COARSE wall-clock-cap granularity (Task 1): the Stopwatch is consulted only after at least
    // this many cycles have elapsed since the last check, NOT every loop iteration. This makes the
    // deadline check per-~100K-cycles regardless of slice granularity (the 6502 interp steps ONE
    // instruction (~2-7 cycles) per slice — so a per-iteration check would be per-instruction and would
    // perturb the hot loop's throughput; the JIT runs an 8M-cycle BulkSlice per slice — already coarse).
    // 100K cycles is << the 8M BulkSlice (so the JIT path checks once per slice) and >> a single 6502
    // instruction (so the interp path checks once per ~20-30K instructions, never per-instruction).
    private const long WallCheckCycleInterval = 100_000;

    // The per-slice budget WHEN A WALL CAP IS ACTIVE (Task 1). The deadline can only be consulted at a
    // slice boundary (between AdvanceSlice calls) — a driver whose AdvanceSlice runs the FULL slice
    // budget internally before returning (the Z80-W1 BDOS path steps instruction-by-instruction up to
    // BulkSlice T-states; the 6502/Z80 W2 JIT runs one budgeted jit.Run) cannot be interrupted mid-slice.
    // So when a deadline is set we shrink each slice to this budget, bounding the WORST-CASE overrun to
    // roughly (this budget ÷ the run's cycles-per-second). At the SMC-thrash rate (~200K cycles/sec) a
    // 2M-cycle slice is ~10s — matching the cap granularity — vs the 8M BulkSlice's ~40s overrun. This
    // affects ONLY capped (long, pathological) runs: a fast workload (W2/W3, sub-second) completes inside
    // a single slice either way, so its cycle count is byte-identical and its measured rate is unchanged
    // (verified: the W2/W3 JIT numbers do not move with this bound).
    private const long WallSliceCycles = 2_000_000;

    // The per-architecture drivers. Each later CPU (68000, 8086) adds one line here, never re-touches
    // the shared loop below. Keyed by BenchWorkload.Architecture.
    private static readonly IReadOnlyDictionary<string, ITierDriver> Drivers =
        new Dictionary<string, ITierDriver>(StringComparer.OrdinalIgnoreCase)
        {
            ["mos6502"] = new Mos6502TierDriver(),
            ["z80"] = new Z80TierDriver(),
            ["m68000"] = new M68000TierDriver(),   // Milestone B — the 68000 tier driver (the reserved seam)
            ["m8086"] = new Drivers.M8086TierDriver(),   // M6 PR-A — the 8086 tier driver
        };

    /// <summary>Run a tier to its termination window, returning the emulated cycle count (back-compat
    /// entry point — the smoke test + the BDN harness use it). Uncapped (no wall deadline).</summary>
    public static long Run(BenchWorkload w, bool jit, JitOptions options) => RunCounted(w, jit, options).Cycles;

    /// <summary>Run a tier to its termination window, returning BOTH the cycle count and the guest
    /// instruction count (Task B2). The instruction count is harvested from the live instance after
    /// the same window the cycle count measures — additive, never changing the cycle math.
    /// <para><paramref name="wallCap"/> (Task 1, optional) is the per-measurement wall-clock deadline: a
    /// run that would exceed it STOPS at the deadline (coarsely — checked per <see cref="WallCheckCycleInterval"/>
    /// cycles, never per-instruction, so a fast workload is byte-for-byte unchanged) and returns with
    /// <see cref="TierRunResult.Capped"/> = true. A capped run does NOT VerifyTrap (it intentionally did
    /// not reach the trap — that is the honest, bounded outcome). null ⇒ no deadline (the original
    /// behavior).</para></summary>
    public static TierRunResult RunCounted(BenchWorkload w, bool jit, JitOptions options, TimeSpan? wallCap = null)
    {
        if (!Drivers.TryGetValue(w.Architecture, out var driver))
            throw new InvalidOperationException(
                $"{w.Name}: no tier driver registered for architecture '{w.Architecture}'.");

        ITierInstance instance = jit ? driver.CreateTier1(w, options) : driver.CreateTier0(w);

        // The target cycle window: the fixed cap (W2 / the Z80 windows) or the expected anchor (6502
        // W1). Both tiers run the SAME number of cycles of the SAME work — the fair like-for-like
        // throughput window. `trap` (a parked-PC success trap that VerifyTrap gates) is the 6502 W1
        // termination; capped workloads (everything else) park only as an early-stop signal.
        long target = w.FixedCycleCap ?? w.ExpectedCycles;
        bool trap = w.FixedCycleCap is null;

        // The wall-clock cap (Task 1): start the Stopwatch only when a deadline is set, and consult it
        // COARSELY (every WallCheckCycleInterval cycles of progress) so a fast workload's hot loop is
        // never perturbed by a per-iteration Elapsed read. lastCheckCycles throttles the consult.
        Stopwatch? wallClock = wallCap is null ? null : Stopwatch.StartNew();
        long lastCheckCycles = 0;

        // When a deadline is set, bound each slice to WallSliceCycles so the loop returns to the deadline
        // check often enough to honor the cap even for a driver whose AdvanceSlice runs its whole budget
        // internally (the Z80-W1 BDOS path; the W2/W3 JIT one-Run path). A fast workload finishes inside
        // one slice either way, so this never changes its cycle count or measured rate.
        long sliceBudget = wallCap is null ? BulkSlice : Math.Min(BulkSlice, WallSliceCycles);

        while (instance.CycleCount < target)
        {
            instance.AdvanceSlice(Math.Min(sliceBudget, target - instance.CycleCount));
            // A parked slice ends the run: for a trap workload (6502 W1) VerifyTrap gates the park PC
            // (a park elsewhere is a divergence-throw); for a capped workload (Z80 W1's warm-boot) it
            // is just an early stop below the window. The per-tier park CONDITION (interp: PC
            // unchanged; JIT: PC unchanged AND below target) lives in the instance, preserving the
            // original RunInterpreter / RunJit semantics byte-for-byte.
            if (instance.ParkedThisSlice)
            {
                if (trap)
                    VerifyTrap(instance.CycleCount, instance.CurrentPc, w);
                return new TierRunResult(instance.CycleCount, instance.InstructionCount);
            }

            // Coarse wall-clock-deadline check (Task 1): only consult the Stopwatch after enough cycles
            // have elapsed since the last consult (so the interp path checks ~once per 100K cycles, not
            // per instruction). A deadline hit returns a VALID-but-CAPPED result over the cycles actually
            // executed — NO VerifyTrap (a capped run did not reach the trap; that is the bounded outcome).
            if (wallClock is not null && instance.CycleCount - lastCheckCycles >= WallCheckCycleInterval)
            {
                lastCheckCycles = instance.CycleCount;
                if (wallClock.Elapsed >= wallCap!.Value)
                    return new TierRunResult(instance.CycleCount, instance.InstructionCount, Capped: true);
            }
        }
        return new TierRunResult(instance.CycleCount, instance.InstructionCount);
    }

    /// <summary>Sanity-gate a W1 park: it must be at the success trap. A park elsewhere means the
    /// subject diverged — throw so a wrong run never yields a throughput number.</summary>
    private static void VerifyTrap(long cycles, ushort pc, BenchWorkload w)
    {
        if (pc != w.SuccessTrapPc)
            throw new InvalidOperationException(
                $"{w.Name}: parked at 0x{pc:X4} after {cycles} cycles, not the success trap " +
                $"0x{w.SuccessTrapPc:X4} (subject diverged)");
    }
}
