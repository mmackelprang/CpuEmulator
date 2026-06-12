namespace CpuEmulator.Core.Specification;

/// <summary>Addressing modes supported by the chunk-2 subset. Each mode is a fixed
/// cycle-by-cycle bus pattern the generator expands (spec §5: modes are micro-op templates).</summary>
public enum AddrMode
{
    Implied,
    Immediate,
    ZeroPage,
    Absolute,
    Relative,
}
