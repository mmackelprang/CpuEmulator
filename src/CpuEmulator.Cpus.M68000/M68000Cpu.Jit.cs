namespace CpuEmulator.Cpus.M68000;

public sealed partial class M68000Cpu
{
    /// <summary>The cycle-charge seam the JIT's emitted fast path calls (mirrors the Z80/6502
    /// AdvanceCycles partial). The generated <c>JitTarget</c> resolves this by name
    /// (NonPublic | Instance). In M4 every 68000 op falls back to the interpreter Step (the
    /// fallback uses the CycleCount delta), so NO emitted 68000 op calls this yet — but the handle
    /// must RESOLVE for the BlockCompiler ctor (it reads <c>target.AdvanceCyclesMethod</c>). The
    /// generated <c>_cycles</c> field stays private; this is the explicit named seam the cross-arch
    /// IL-emission phase (M6) will use to keep CycleCount in step with the interpreter.</summary>
    internal void AdvanceCycles(long n) => _cycles += n;

    // The 68000 has NO separate I/O space (IO is memory-mapped — von Neumann), so unlike the Z80
    // there is NO IoBus accessor: the JittedCpu<M68000Cpu> ctor's `ioBus ?? bus` default routes the
    // (never-taken) Port callout at the memory bus, a harmless placeholder. No 68000 op is a Port op.
}
