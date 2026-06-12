namespace CpuEmulator.Core.Specification;

/// <summary>Status-register flags (6502 P-register layout: N V - B D I Z C).</summary>
public enum Flag
{
    C,
    Z,
    I,
    D,
    V,
    N,
}
