namespace CpuEmulator.Core.Jit;

/// <summary>The output of one decode walk: the operation selected, how many bytes the
/// instruction occupies (COMPUTED by the walk — NOT a static descriptor field), and the
/// decoded operand bytes the consumers (interpreter dispatch / disassembler) need.
///
/// The 6502 is the degenerate case: OperationKey == the single opcode byte, Length == a
/// fixed function of the opcode's addressing mode (1/2/3), and Operands carries the 0/1/2
/// operand bytes the mode's bus pattern reads. A prefixed CPU (Z80) packs prefix+opcode into
/// OperationKey and Length counts prefix+opcode+disp+imm. A ModR/M CPU (8086) computes Length
/// from a mid-stream byte and packs (opcode &lt;&lt; 3 | modrm.reg) into OperationKey for the
/// opcode-group encodings. The walk decides; this struct only carries the result.</summary>
public readonly record struct DecodeResult(
    uint OperationKey,   // opaque — "whatever bits/bytes select the operation" (Ground truth C)
    int Length,          // COMPUTED OUTPUT: total bytes consumed by the walk (Ground truth B)
    DecodedOperands Operands);

/// <summary>The operand bytes the walk consumed, in a fixed-capacity inline buffer (no
/// allocation in the hot loop). For the 6502 this is operandLo/operandHi (the 0/1/2 bytes the
/// disassembler + interpreter already use, CpuEmitter.cs:1251 takes operandLo/operandHi). For a
/// ModR/M CPU it additionally carries the modrm byte + the disp/imm bytes the walk consumed.
/// M3.1b keeps this minimal (Lo/Hi + a Count) — the 6502 needs exactly that; the synthetic CPU's
/// length-determining byte is carried in walk-local state and surfaced only as Length. Wider
/// operand carriage (the 8086's full disp/imm) is M5 work — the shape is extensible (a fixed
/// inline tuple) but M3 fills only what the 6502 + synthetic CPU need.</summary>
public readonly record struct DecodedOperands(byte Lo, byte Hi, byte Count)
{
    public static readonly DecodedOperands None = new(0, 0, 0);
}

/// <summary>The operation-key packing, declared by the spec's decode structure and realized by
/// the generated Decode function. The 6502 declares KeyShape.OpcodeByte (key == opcode). A
/// prefixed CPU declares PrefixedOpcode (prefix in the high bits). A sub-field CPU declares
/// OpcodeGroup (a sub-field of a non-first byte refines the opcode). The key is OPAQUE to the
/// consumers — they index a table with it; only the generated Decode function knows the packing.</summary>
public enum KeyShape { OpcodeByte, PrefixedOpcode, OpcodeGroup }
