namespace CpuEmulator.Core.Specification;

/// <summary>Status-register flags. Values are the 6502 P-register bit positions
/// (bit 7→0: N V - B D I Z C), so <c>1 &lt;&lt; (int)flag</c> yields the hardware mask.</summary>
public enum Flag
{
    C = 0,
    Z = 1,
    I = 2,
    D = 3,
    V = 6,
    N = 7,
}
