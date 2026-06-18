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
    DecodedOperands Operands,
    ExtensionWords ExtensionWords = default,   // M4.3b: the 68000 EA extension words (empty for 6502/Z80)
    ushort Operword = 0,   // M4.5a: the 68000 operword the field walk read (0 for 6502/Z80 byte walks) —
                            // lets the FieldGrammar Step dispatch without a second Read16 of PC (the field
                            // walk reads the operword exactly once via the fetch stream).
    X86Operands X86 = default);   // M5.5a: the 8086's full disp/imm/segment-override carriage the x86 decode
                                  // walk captured (the ModR/M byte + the sign-extended disp16 + the immediate +
                                  // the raw segment-override prefix byte). Empty (X86Operands.None) for the
                                  // 6502/Z80/68000 walks — they never set it, so their generation is byte-identical.

/// <summary>The 8086's full per-instruction operand carriage the x86 decode walk captured (M5.5a). The
/// byte/prefix (6502/Z80) and field (68000) walks never produce this — it defaults to <see cref="None"/>, so
/// their generated Decode is byte-identical. <see cref="ModRm"/> is the raw ModR/M byte (also surfaced on
/// <see cref="DecodedOperands.Lo"/>); <see cref="Disp"/> is the displacement, disp8 SIGN-EXTENDED to 16 bits
/// (or the raw disp16, or 0 when none); <see cref="Imm"/> is the immediate, zero-extended (the body knows
/// byte vs word — also the moffs disp16 for the accumulator-direct A0–A3 opcodes, which carry it in the
/// immediate slot); <see cref="SegOverride"/> is the raw segment-override prefix byte (0x26/0x2E/0x36/0x3E)
/// or 0 when no override is in force; <see cref="RepPrefix"/> is the raw repeat-prefix byte (0xF3 REP/REPE,
/// 0xF2 REPNE) or 0 when no repeat prefix is in force — the M5.5d string-op body drives the CX-counted,
/// DF-directed loop from it (F3 ⇒ REP/REPE, F2 ⇒ REPNE; the ZF-termination differs for CMPS/SCAS).</summary>
public readonly record struct X86Operands(byte ModRm, ushort Disp, ushort Imm, byte SegOverride, byte RepPrefix = 0)
{
    public static readonly X86Operands None = default;
}

/// <summary>The 68000 EA extension words the field-decode walk consumed (M4.3b). A fixed inline buffer of
/// up to 4 16-bit words (MOVE's two EAs at .l = 2 + 2). Empty (Count == 0) for the 6502/Z80 byte walks.
/// The EA-compute (CpuEmitter EmitM68kEa) reads d16/abs.w/abs.l/#imm/brief-index from here.</summary>
public readonly record struct ExtensionWords(ushort W0, ushort W1, ushort W2, ushort W3, int Count)
{
    public static readonly ExtensionWords None = default;
    public ushort this[int i] => i switch { 0 => W0, 1 => W1, 2 => W2, 3 => W3, _ => 0 };
}

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
