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
}

/// <summary>The outcome of one counted tier run (Task B2): the emulated cycle count AND the guest
/// instruction count over the same window. <see cref="Instructions"/> is 0 when the driver does not
/// attribute a per-instruction count (the 6502/Z80 W2 JIT path advances by one large budgeted Run);
/// the 68000 driver reports a real count (it advances by a budget-1 Run / Step).</summary>
public readonly record struct TierRunResult(long Cycles, long Instructions);

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

    // The per-architecture drivers. Each later CPU (68000, 8086) adds one line here, never re-touches
    // the shared loop below. Keyed by BenchWorkload.Architecture.
    private static readonly IReadOnlyDictionary<string, ITierDriver> Drivers =
        new Dictionary<string, ITierDriver>(StringComparer.OrdinalIgnoreCase)
        {
            ["mos6502"] = new Mos6502TierDriver(),
            ["z80"] = new Z80TierDriver(),
            ["m68000"] = new M68000TierDriver(),   // Milestone B — the 68000 tier driver (the reserved seam)
        };

    /// <summary>Run a tier to its termination window, returning the emulated cycle count (back-compat
    /// entry point — the smoke test + the BDN harness use it).</summary>
    public static long Run(BenchWorkload w, bool jit, JitOptions options) => RunCounted(w, jit, options).Cycles;

    /// <summary>Run a tier to its termination window, returning BOTH the cycle count and the guest
    /// instruction count (Task B2). The instruction count is harvested from the live instance after
    /// the same window the cycle count measures — additive, never changing the cycle math.</summary>
    public static TierRunResult RunCounted(BenchWorkload w, bool jit, JitOptions options)
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

        while (instance.CycleCount < target)
        {
            instance.AdvanceSlice(Math.Min(BulkSlice, target - instance.CycleCount));
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
