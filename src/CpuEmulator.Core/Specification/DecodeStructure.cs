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
