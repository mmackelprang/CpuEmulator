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

public sealed record PrefixByte(byte Value);
