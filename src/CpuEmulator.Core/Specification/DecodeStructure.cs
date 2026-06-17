namespace CpuEmulator.Core.Specification;

/// <summary>A spec's decode structure. ABSENT (the 6502 default) ⇒ single-byte opcode, key ==
/// opcode, length fixed per addressing mode — the degenerate walk. Declaring it opts into the
/// multi-byte / mid-stream-length / sub-field-key properties (Z80 prefixes, 8086 ModR/M). M3.1b
/// ships the SHAPE + the synthetic proof; no shipped CPU declares one yet (the 6502 doesn't).
/// These types are inert syntax carriers for the generator — record equality and array mutation
/// are unsupported usage.</summary>
public sealed record DecodeStructure(
    PrefixByte[] Prefixes,        // bytes that switch "page" (Z80 CB/ED/DD/FD) — property 1+2
    byte[] ModRmOpcodes,          // opcodes carrying a length-determining mid-stream byte — property 1
    byte[] SubFieldOpcodes);      // opcodes whose operation is refined by a non-first-byte sub-field — property 3

/// <summary>A prefix byte the decode walk switches "page" on. M3.4e-1b (Z80 IX/IY) extends it to express
/// a COMPOUND prefix: <see cref="CompoundWith"/> names a second prefix byte that, when it FOLLOWS this
/// one, forms a compound page (the Z80 <c>DD CB</c>/<c>FD CB</c>), and <see cref="DisplacementBeforeOpcode"/>
/// declares that the compound consumes a DISPLACEMENT byte BEFORE the final opcode (the <c>DD CB d op</c>
/// shape — ADR 0001 Decision 1: "no single-byte decoder can express it"). A plain prefix (the 6502 has
/// none; the Z80 <c>CB</c>/<c>ED</c>) leaves both at their defaults, so the existing declarations +
/// the degenerate walk are unchanged.</summary>
public sealed record PrefixByte(
    byte Value,
    byte? CompoundWith = null,
    bool DisplacementBeforeOpcode = false);

// ── M4.3a (ADR 0004 Decision 1): the word-granular, field-decomposed decode SHAPE the 68000 needs. ──
// These are a SIBLING carrier to DecodeStructure (a declared FieldGrammar is structurally different from
// the prefix/ModRm/sub-field byte arrays; the 68000 declares a field grammar INSTEAD OF prefixes). ABSENT
// (6502/Z80) ⇒ the byte/prefix walk is unchanged. Inert syntax carriers for the generator — record
// equality and array mutation are unsupported usage.

/// <summary>The fetch unit the decode walk reads through (Ground truth D). Byte (6502/Z80/8086) is the
/// default; Word is the 68000's 16-bit big-endian operword (M4.3a). Carried on a declared FieldGrammar.</summary>
public enum FetchUnit { Byte, Word }

/// <summary>How a field op's size bits map to an OperandSize (M4.3a / RECON-FINDING C4). Standard is the
/// common 68000 encoding (00=b, 01=w, 10=l); Move is the MOVE outlier (01=b, 11=w, 10=l). Per-op because
/// MOVE differs — the carrier expresses both so the M4.4 dataset needs no reshaping.</summary>
public enum SizeEncoding { Standard, Move }

/// <summary>An effective-address category (the classic 68000 legality buckets) — M4.3a carries it as a TAG
/// on each field op (the legality MATRIX that consumes it is M4.3b). Names the addressing-mode set an op's
/// EA may use (data / memory / control / alterable …); M4.3a's count-only walk does not yet branch on it.</summary>
public enum EaCategory { DataAddressing, MemoryAlterable, DataAlterable, Control, Alterable, All }

/// <summary>One operation's word-granular field decomposition (M4.3a, ADR 0004 Decision 1). The operword is
/// matched by (Mask, Match) — (operword &amp; Mask) == Match selects this op; the size is extracted from
/// bits [SizeShift, SizeShift+SizeWidth) via SizeEncoding; the 6-bit EA field (mode:register) is at
/// EaShift (mode = bits 5-3, register = bits 2-0 of the 6-bit field). LegalEa tags the EA category for the
/// M4.3b legality matrix. These types are inert syntax carriers for the generator.</summary>
public sealed record FieldOp(
    ushort Mask, ushort Match, string Operation,
    int SizeShift, int SizeWidth, SizeEncoding SizeEncoding,
    int EaShift, EaCategory LegalEa);

/// <summary>A word-granular, field-decomposed decode grammar (ADR 0004 Decision 1) — the 68000's decode
/// SHAPE. ABSENT (6502/Z80) ⇒ the byte/prefix walk (unchanged). Declaring it opts into FetchUnit.Word +
/// field extraction + operand-computed length. The Ops are matched in order; first (Mask, Match) hit wins;
/// no hit ⇒ the illegal-instruction Undefined sentinel (the vector is M4.5).</summary>
public sealed record FieldGrammar(FetchUnit FetchUnit, FieldOp[] Ops);

// ── M5.2 (ADR 0006 Decision 1): the 8086's byte-granular, VARIABLE-LENGTH, prefix-stacking decode SHAPE. ──
// A THIRD sibling carrier beside DecodeStructure (the Z80 one-prefix/synthetic-ModRm byte walk) and
// FieldGrammar (the 68000 word-field walk). The 8086 length is the most input-dependent of the three CPUs:
//   length = [0..N prefix bytes] + opcode(1) + [ModR/M(1) + disp(0/1/2)] + [imm(0/1/2)],
// where the disp size comes from the ModR/M mod+r/m fields (the real 16-bit table — NOT the synthetic
// `modrm & 3` placeholder DecodeStructure carries) and the immediate size comes from the opcode + its
// w/s bits. ABSENT (6502/Z80/68000) ⇒ the existing byte or field walk, BYTE-IDENTICAL (this variant is
// opt-in exactly as FetchUnit.Word is). These types are inert syntax carriers for the generator — record
// equality and array mutation are unsupported usage.

/// <summary>An x86 prefix byte's role (ADR 0006 Decision 1 / ADR 0005 Decision 2). The decode walk stacks
/// 0..N prefix bytes, accumulating the segment-override / repeat / lock state the EA layer (M5.3) + the
/// string-op body (M5.5d) consume. SegmentOverride: 26=ES, 2E=CS, 36=SS, 3E=DS. Lock: F0 (and the alias
/// F1). Repeat: F3 (REP/REPE), F2 (REPNE).</summary>
public enum X86PrefixRole { SegmentOverride, Lock, Repeat }

/// <summary>One x86 prefix byte + its role (M5.2). Carried on an <see cref="X86DecodeStructure"/>; the
/// decode walk treats any byte in this set as a prefix to stack BEFORE the opcode.</summary>
public sealed record X86Prefix(byte Value, X86PrefixRole Role);

/// <summary>How an opcode's immediate-operand length is determined (M5.2, ADR 0006 Decision 1). The 8086
/// immediate length is opcode-driven and, for many ALU/MOV forms, modulated by the w / s bits packed in
/// the opcode byte's low bits.</summary>
public enum X86ImmediateRule
{
    None,        // no immediate operand (the operand is reg/mem/implied)
    Fixed8,      // exactly 1 immediate byte regardless of w/s (e.g. INT n, the by-imm8 shift count)
    Fixed16,     // exactly 2 immediate bytes (e.g. a near CALL/JMP rel16, RET imm16)
    WBit,        // w=0 ⇒ 1 byte, w=1 ⇒ 2 bytes — the imm size follows the operand size (MOV/ALU imm)
    SWBit,       // the ALU-group sign-extend form: s=1 ⇒ 1 byte (sign-extended), else w drives 1/2 bytes
}

/// <summary>One opcode row's x86 decode metadata (M5.2). HasModRm: the opcode carries a ModR/M byte (so the
/// walk reads it + the mod/rm-derived displacement). RegIsExtension: the ModR/M reg field EXTENDS the opcode
/// (the 80/81/83/F6/F7/FE/FF/D0-D3/C0/C1/8F groups) — the key becomes (opcode&lt;&lt;3)|reg, reusing the
/// existing OpcodeGroup key shape. WBit names the bit position of the operand-size w bit (-1 ⇒ none); SBit
/// the sign-extend s bit (-1 ⇒ none); Immediate the immediate-length rule.
///
/// <para><b>M5.5b — the F6/F7 split-immediate carrier (<see cref="ImmediateRegMask"/>).</b> The 8086
/// immediate rule is normally PER-OPCODE-BYTE, but the F6/F7 unary group is the lone exception: reg=0/1 (TEST)
/// take an immediate (per the <see cref="Immediate"/> rule), while reg=2..7 (NOT/NEG/MUL/IMUL/DIV/IDIV) take
/// NONE. <see cref="ImmediateRegMask"/> is a bitmask of ModR/M reg values: bit <c>r</c> set ⇒ reg <c>r</c>
/// consumes the immediate per the <see cref="Immediate"/> rule; an unset bit ⇒ that reg consumes no immediate
/// regardless of the rule. The default <c>-1</c> means "all regs / not reg-gated" (the existing per-opcode-byte
/// behavior used by EVERY non-F6/F7 opcode — byte-identical). F6/F7 declare
/// <c>ImmediateRegMask: 0b00000011</c> (= 3 — reg 0 and 1 only). It is the ONLY consumer; without it the walk
/// would consume a phantom immediate byte for NOT/NEG/MUL/IMUL/DIV/IDIV and corrupt the decode length.</para></summary>
public sealed record X86Opcode(
    byte Value,
    bool HasModRm = false,
    bool RegIsExtension = false,
    int WBit = -1,
    int SBit = -1,
    X86ImmediateRule Immediate = X86ImmediateRule.None,
    int ImmediateRegMask = -1);

/// <summary>The 8086's byte-granular, variable-length, prefix-stacking decode SHAPE (ADR 0006 Decision 1).
/// A sibling to <see cref="DecodeStructure"/> / <see cref="FieldGrammar"/>; declaring it opts the CPU into
/// the <c>EmitX86DecodeWalk</c> arm (prefix stacking → opcode → real ModR/M disp-length table → immediate)
/// while REUSING the opaque-key / computed-length / DecodeResult back-end unchanged. ABSENT (6502/Z80/68000)
/// ⇒ the byte or field walk is BYTE-IDENTICAL. Inert syntax carrier for the generator.</summary>
public sealed record X86DecodeStructure(X86Prefix[] Prefixes, X86Opcode[] Opcodes);
