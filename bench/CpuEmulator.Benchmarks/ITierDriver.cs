namespace CpuEmulator.Benchmarks;

using CpuEmulator.Jit;

/// <summary>A live, seeded tier instance ready to be advanced + measured. Wraps an <c>ICpuCore</c>
/// (the interpreter inner) and, for the JIT tier, the <c>JittedCpu&lt;T&gt;</c> that drives it — but
/// exposes only what <see cref="TierRunner"/> needs: advance-one-slice, the cycle count, the
/// post-slice PC, and whether the PC parked across that slice. This is the per-CPU seam that makes
/// <see cref="TierRunner"/> CPU-agnostic: the 6502 PC-trap and the Z80 CP/M warm-boot are both just
/// implementations behind <see cref="AdvanceSlice"/> + <see cref="ParkedThisSlice"/>.
/// <para>The shared budgeted loop lives in <see cref="TierRunner"/> (one place owns the
/// BulkSlice budget, the before/after-PC parked detection, and the <c>VerifyTrap</c> divergence
/// throw), so every driver reproduces the SAME termination semantics. A driver decides only the
/// slice granularity (6502 interp = one Step; 6502 JIT = one BulkSlice Run; Z80 = its own
/// per-instruction / budgeted advance with the optional BDOS service) and reports whether the PC
/// advanced.</para></summary>
public interface ITierInstance
{
    /// <summary>The emulated cycle count consumed so far (6502 machine cycles; Z80 T-states).</summary>
    long CycleCount { get; }

    /// <summary>Advance ONE slice, bounded by <paramref name="maxCycles"/> emulated cycles (the
    /// runner passes <c>min(BulkSlice, target - CycleCount)</c>). A Tier-0 instance may step a single
    /// instruction; a Tier-1 instance runs <c>JittedCpu.Run</c> with that budget. After the call,
    /// <see cref="CurrentPc"/> + <see cref="ParkedThisSlice"/> reflect this slice. Implementations that
    /// need a host-side boundary (the Z80 BDOS CALL) service it inside this call.</summary>
    void AdvanceSlice(long maxCycles);

    /// <summary>True when the PC did NOT advance across the last <see cref="AdvanceSlice"/> — the
    /// trap-park signal (6502 W1 success trap; Z80 W1 warm-boot). The runner gates a W1 stop on this
    /// + a below-target cycle count, exactly as the original RunInterpreter/RunJit did.</summary>
    bool ParkedThisSlice { get; }

    /// <summary>The PC after the last <see cref="AdvanceSlice"/> (the value <c>VerifyTrap</c> checks
    /// against the success trap).</summary>
    ushort CurrentPc { get; }
}

/// <summary>Per-CPU factory: builds a Tier-0 (interpreter) or Tier-1 (JIT) live instance for a
/// workload. One driver per architecture; the runner never names a concrete CPU type. The 6502
/// driver reproduces the existing <see cref="TierRunner"/> construction EXACTLY (same AddressSpace,
/// same <c>S=0xFD/P=0x34</c> seed, same PC trap), so the committed 6502 numbers do not move.</summary>
public interface ITierDriver
{
    /// <summary>The architecture key this driver serves (matches <c>BenchWorkload.Architecture</c>).</summary>
    string Architecture { get; }

    /// <summary>Build a Tier-0 (interpreter) live instance, seeded + ready to advance.</summary>
    ITierInstance CreateTier0(BenchWorkload w);

    /// <summary>Build a Tier-1 (JIT) live instance with the given options, seeded + ready to advance.</summary>
    ITierInstance CreateTier1(BenchWorkload w, JitOptions options);
}
