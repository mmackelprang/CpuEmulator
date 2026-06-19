namespace CpuEmulator.Machines;

/// <summary>Which CPU core a board targets. The CpuCoreFactory maps each to its concrete core
/// (and, on the Jit tier, the JittedCpu&lt;TCpu&gt; wrapper + generated JitTarget).</summary>
public enum CpuKind
{
    Mos6502,
    Z80,
    M68000,
    I8086,
}
