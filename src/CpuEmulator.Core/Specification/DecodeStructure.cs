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
