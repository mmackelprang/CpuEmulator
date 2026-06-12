namespace CpuEmulator.Core.Specification;

/// <summary>One instruction-table row: opcode byte, mnemonic, addressing mode, and the
/// micro-op sequence executed after the mode's bus pattern resolves the operand.
/// These types are inert syntax carriers for the generator — record equality and array
/// mutation are unsupported usage.</summary>
public sealed record InstructionDef(byte Opcode, string Mnemonic, AddrMode Mode, Op[] Ops);
