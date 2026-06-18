namespace CpuEmulator.Cpus.M8086;

public sealed partial class M8086Cpu
{
    /// <summary>The cycle-charge seam the JIT's emitted fast path calls (mirrors the Z80/6502/68000
    /// AdvanceCycles partial). The generated <c>JitTarget</c> resolves this by name (NonPublic | Instance);
    /// it was unresolved (→ NRE in the BlockCompiler ctor) until this partial supplied it. In M5 every 8086
    /// op falls back to the interpreter Step (the empty-Ops, NeedsFallback descriptors), so NO emitted 8086
    /// op calls this yet — but the handle MUST resolve for the BlockCompiler ctor (it reads
    /// <c>target.AdvanceCyclesMethod</c>). The generated <c>_cycles</c> field stays private; this is the
    /// explicit named seam the cross-arch IL-emission phase (M6) will use to keep CycleCount in step.</summary>
    internal void AdvanceCycles(long n) => _cycles += n;

    // The 8086 handles IN/OUT (E4-E7/EC-EF) inside the interpreter body as an open-bus read / no-op (the
    // 8088 data-axis corpus has no peripheral attached), NOT as a JIT Port callout — so, unlike the Z80,
    // there is NO IoBus accessor: the JittedCpu<M8086Cpu> ctor's `ioBus ?? bus` default routes the
    // (never-taken in all-fallback) Port callout at the memory bus, a harmless placeholder.
}
