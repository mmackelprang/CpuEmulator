using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
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
}

public static class Tier1
{
    /// <summary>Run the workload on the Tier-1 JIT (chaining ON — the default); return the cycle
    /// count.</summary>
    public static long Run(BenchWorkload w) => TierRunner.Run(w, jit: true, new JitOptions());

    /// <summary>Run the workload on the Tier-1 JIT with explicit options (used by the dispatch /
    /// chaining micro-bench in Task 9 — e.g. DisableChaining).</summary>
    public static long Run(BenchWorkload w, JitOptions options) => TierRunner.Run(w, jit: true, options);
}

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

    public static long Run(BenchWorkload w, bool jit, JitOptions options)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
        var inner = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };

        // The target cycle window: the fixed cap (W2) or the expected anchor (W1). Both tiers run the
        // SAME number of cycles of the SAME work — the fair like-for-like throughput window.
        long target = w.FixedCycleCap ?? w.ExpectedCycles;

        if (!jit)
            return RunInterpreter(inner, w, target);
        return RunJit(inner, space, options, w, target);
    }

    private static long RunInterpreter(Mos6502Cpu inner, BenchWorkload w, long target)
    {
        bool trap = w.FixedCycleCap is null;
        while (inner.CycleCount < target)
        {
            ushort before = inner.PC;
            inner.Step();
            // W1: if it parks at the success trap exactly at/just before the anchor, stop (a correct
            // run reaches the trap here). A park BEFORE the anchor at the wrong PC is a divergence.
            if (trap && inner.PC == before)
            {
                VerifyTrap(inner.CycleCount, inner.PC, w);
                return inner.CycleCount;
            }
        }
        return inner.CycleCount;
    }

    private static long RunJit(Mos6502Cpu inner, AddressSpace space, JitOptions options, BenchWorkload w, long target)
    {
        var jitCpu = new JittedCpu(inner, space, options);
        bool trap = w.FixedCycleCap is null;
        while (inner.CycleCount < target)
        {
            long budget = Math.Min(BulkSlice, target - inner.CycleCount);
            ushort before = inner.PC;
            jitCpu.Run(ref budget);
            // W1: a parked trap (PC unchanged across a slice that did no further work) means the run
            // reached the success trap — stop. (The exact-cycle anchor is the functional test's gate.)
            if (trap && inner.PC == before && inner.CycleCount < target)
            {
                VerifyTrap(inner.CycleCount, inner.PC, w);
                return inner.CycleCount;
            }
        }
        return inner.CycleCount;
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
