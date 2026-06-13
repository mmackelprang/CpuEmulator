namespace CpuEmulator.Core.Specification;

/// <summary>Addressing modes supported by the full 6502 vocabulary. Each mode is a fixed
/// cycle-by-cycle bus pattern the generator expands (spec §5: modes are micro-op templates).
/// The first 13 members cover all modes in the mos6502-opcodes.json dataset; the two IoPort*
/// members (M3.2, additive) carry the Z80 IN/OUT port-operand shape — the 6502 names neither.</summary>
public enum AddrMode
{
    Implied, Accumulator, Immediate,
    ZeroPage, ZeroPageX, ZeroPageY,
    Absolute, AbsoluteX, AbsoluteY,
    IndirectX, IndirectY, Indirect, Relative,
    IoPortImmediate,   // (n)  — an 8-bit port-number operand byte (Z80 IN A,(n) / OUT (n),A)
    IoPortIndirect,    // (C)  — the port number comes from a register (Z80 IN r,(C) / OUT (C),r)
}
