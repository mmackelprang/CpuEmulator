namespace CpuEmulator.Core.Specification;

/// <summary>One instruction-table row: opcode byte, mnemonic, addressing mode, and the
/// micro-op sequence executed after the mode's bus pattern resolves the operand.</summary>
public sealed record InstructionDef(byte Opcode, string Mnemonic, AddrMode Mode, Op[] Ops);
