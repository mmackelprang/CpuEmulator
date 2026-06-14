namespace CpuEmulator.Core.Specification;

/// <summary>How an instruction row's operation-key is shaped (Ground truth C). OpcodeByte is the
/// 6502 degenerate case (key == opcode). PrefixedOpcode keys a row by (prefix, opcode). OpcodeGroup
/// keys a row by (opcode, sub-field of a non-first byte). The generator realizes the packing; this
/// only records which shape the spec authored.</summary>
public enum DecodeKeyShape { OpcodeByte, PrefixedOpcode, OpcodeGroup, Compound }

/// <summary>One instruction-table row: opcode byte, mnemonic, addressing mode, and the
/// micro-op sequence executed after the mode's bus pattern resolves the operand. M3.1b adds the
/// optional decode-key carriers (Prefix / SubField / KeyShape) — ABSENT for the 6502 single-byte
/// form (KeyShape.OpcodeByte, key == opcode), so Mos6502Spec.cs is byte-identical. These types are
/// inert syntax carriers for the generator — record equality and array mutation are unsupported
/// usage.</summary>
public sealed record InstructionDef(
    byte Opcode,
    string Mnemonic,
    AddrMode Mode,
    Op[] Ops,
    int? Prefix = null,        // the prefix byte for a prefixed row (KeyShape.PrefixedOpcode)
    int? Prefix2 = null,       // the second prefix byte for a compound row (KeyShape.Compound)
    int? SubField = null,      // the non-first-byte sub-field for an opcode-group row (KeyShape.OpcodeGroup)
    DecodeKeyShape KeyShape = DecodeKeyShape.OpcodeByte);
