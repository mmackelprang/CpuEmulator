namespace CpuEmulator.Core.Specification;

/// <summary>Operand-addressable registers referenced by micro-ops. Names must match
/// entries in the spec's Registers table.</summary>
public enum Reg
{
    A,
    X,
    Y,
    S,
}
