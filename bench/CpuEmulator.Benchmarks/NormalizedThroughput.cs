namespace CpuEmulator.Benchmarks;

/// <summary>The normalized throughput of one subject on one workload, for the comparison table.
/// guest-MIPS (millions of GUEST INSTRUCTIONS / host wall-second) is the cross-CPU-comparable
/// headline — an instruction is an instruction regardless of the CPU's cycle model. CyclesPerSecond
/// is the CPU's own cycle unit (machine cycles / T-states / 68000 cycles) — NOT cross-CPU comparable,
/// kept for the within-CPU sanity check + spread. A subject that reports no instruction count
/// (cycle-only subprocess subjects) has GuestMips == null (the table shows "—" for its MIPS cell and
/// ranks it by cycles/sec within its CPU only).</summary>
public readonly record struct NormalizedThroughput(double? GuestMips, double CyclesPerSecond)
{
    /// <summary>Project an <see cref="AdapterResult"/> onto the normalized axes: guest-MIPS when the
    /// subject reports an instruction count (InstructionsPerSecond &gt; 0), else null; cycles/sec
    /// always passes through.</summary>
    public static NormalizedThroughput From(AdapterResult r) =>
        new(r.InstructionsPerSecond > 0 ? r.InstructionsPerSecond / 1_000_000.0 : null, r.CyclesPerSecond);
}
