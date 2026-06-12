namespace CpuEmulator.Core.Specification;

/// <summary>Addressing modes supported by the full 6502 vocabulary. Each mode is a fixed
/// cycle-by-cycle bus pattern the generator expands (spec §5: modes are micro-op templates).
/// 13 members covering all modes in the mos6502-opcodes.json dataset.</summary>
public enum AddrMode
{
    Implied, Accumulator, Immediate,
    ZeroPage, ZeroPageX, ZeroPageY,
    Absolute, AbsoluteX, AbsoluteY,
    IndirectX, IndirectY, Indirect, Relative,
}
