using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CpuEmulator.Generators;

/// <summary>Instruction op-class. Classified ONCE in SpecParser (CPUGEN010 validation) and
/// carried on the model — the emitter consumes this and has no classifier of its own, so
/// parser and emitter cannot drift (2b-review carry-forward).</summary>
internal enum InstructionClass
{
    Register, Load, Store, Jump, Branch,
    Alu,    // Task 5: ADC/SBC/AND/ORA/EOR/CMP/CPX/CPY/BIT
    Rmw,    // Task 6: ASL/LSR/ROL/ROR/INC/DEC (memory and accumulator forms)
    Stack,  // Task 7: PHA/PLA/PHP/PLP
    Flow,   // Task 7: JSR/RTS
    Port,   // M3.2: IN/OUT — an Io-bus access (PortIn/PortOut); the 6502 uses none
    // ── M3.4a (Z80 base plane — additive; the 6502 uses none of these) ──
    Z80Alu,       // 8-bit flag-correct ALU (Add8..Cp8), INC/DEC (IncReg/DecReg), 16-bit (Add16/Inc16/Dec16)
    Z80Ld,        // 16-bit LD (Load16/Store16 — LD rr,nn ; LD (nn),HL ; LD HL,(nn))
    Z80Stack,     // 16-bit pair PUSH/POP (Push16/Pop16)
    Z80Exchange,  // EX DE,HL / EX AF,AF' / EXX / EX (SP),HL
    Z80Flow,      // conditional+relative flow: JumpIf/CallIf/RetCc/Rst/RelJump/RelJumpIf/Djnz
    Z80Misc,      // DAA/CPL/SCF/CCF/DI/EI — Implied register-class ops with bespoke flag effects
    Z80Rot,       // M3.4b: rotate-accumulators (RLCA/RRCA/RLA/RRA) + CB rotate/shift (CbRotate)
    Z80Bit,       // M3.4b: CB BIT/RES/SET (CbBit)
    Z80EdIo,      // M3.4c: ED IN r,(C) / OUT (C),r (touches the I/O bus + the ports array)
    Z80EdOp,      // M3.4c: the rest of the ED-core (ADC/SBC HL,rp; LD (nn),rp; NEG; RETN/RETI; IM; LD I/R/A; RRD/RLD; NOP)
    Z80EdBlock,   // M3.4d: ED block ops (LDI/LDD/LDIR/LDDR/CPI/.../OTDR) — memory/port transfer + repeat
    Z80Indexed,   // M3.4e-2: the (IX+d)/(IY+d) memory ops (LD/ALU/INC-DEC/LD-imm on the indexed EA)
    Z80DdCb,      // M3.4e-3: the compound DDCB/FDCB bit/rotate/shift on (IX+d)/(IY+d) + the undoc store-copy
}

/// <summary>The operation-key packing a row declared (Ground truth C). OpcodeByte is the 6502
/// degenerate case (key == opcode). PrefixedOpcode packs (prefix &lt;&lt; 8) | opcode. OpcodeGroup
/// packs (opcode &lt;&lt; 3) | subfield (a non-first-byte sub-field). The generated Decode realizes
/// the packing; the consumers treat the key as opaque.</summary>
internal enum KeyShape { OpcodeByte, PrefixedOpcode, OpcodeGroup, Compound }

/// <summary>The fetch unit the decode walk reads through (Ground truth D). Byte is the default
/// (6502/Z80/8086). Word is the 68000 (M4) — wired but no shipped M3 spec sets it.</summary>
internal enum FetchUnit { Byte, Word }

internal sealed record SpecModel(
    string Namespace,
    string CpuName,
    string Architecture,
    LocationInfo IdentifierLocation,
    EquatableArray<RegisterModel> Registers,
    EquatableArray<InstructionModel> Instructions,
    DecodeStructureModel? Decode = null,          // ABSENT (the 6502) ⇒ the degenerate walk
    FetchUnit FetchUnit = FetchUnit.Byte,
    EquatableArray<FlagBitModel> Flags = default);  // ABSENT (the 6502) ⇒ FlagBit enum fallback

/// <summary>One flag name → hardware bit position parsed from a declared <c>FlagLayout</c>
/// (Ground truth B). ABSENT on the model (empty array) ⇒ the 6502 enum-fallback FlagBit map.</summary>
internal sealed record FlagBitModel(string Name, int Bit);

internal sealed record RegisterModel(string Name, int Bits, string Role, string? HighHalf = null, string? LowHalf = null);

/// <summary>One declared prefix's compound metadata (M3.4e-1b). CompoundWith is the byte this prefix
/// compounds with (-1 ⇒ a plain prefix like CB/ED); DisplacementBeforeOpcode declares the DD CB d op
/// shape (the displacement consumed before the final opcode).</summary>
internal sealed record PrefixByteModel(byte Value, int CompoundWith = -1, bool DisplacementBeforeOpcode = false);

/// <summary>The parsed decode structure (Ground truth G). ABSENT on the model means the 6502
/// degenerate walk. Carries the prefix bytes, the ModR/M (length-determining) opcodes, and the
/// opcode-group (sub-field-key) opcodes the synthetic spec declares.</summary>
internal sealed record DecodeStructureModel(
    EquatableArray<byte> Prefixes,
    EquatableArray<byte> ModRmOpcodes,
    EquatableArray<byte> SubFieldOpcodes,
    EquatableArray<PrefixByteModel> PrefixDetails = default);   // M3.4e-1b: per-prefix compound metadata

internal sealed record InstructionModel(
    byte Opcode, string Mnemonic, string Mode, InstructionClass Class, EquatableArray<OpModel> Ops,
    uint OperationKey = 0,                         // the opaque key the walk computes (0 ⇒ defaulted to opcode)
    KeyShape KeyShape = KeyShape.OpcodeByte,
    int Prefix = -1,                               // the prefix byte (PrefixedOpcode rows); -1 if none
    int Prefix2 = -1,                              // M3.4e-1b: the second prefix byte (Compound rows); -1 if none
    int SubField = -1);                            // the sub-field (OpcodeGroup rows); -1 if none

internal sealed record OpModel(string Kind, EquatableArray<string> Args);

/// <summary>Parser output: a model (null when errors prevented one) plus diagnostics.</summary>
internal sealed record ParsedSpec(SpecModel? Model, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
/// Tree-free, equatable source location (the same FilePath/Span/LineSpan trio
/// <see cref="DiagnosticInfo"/> stores). Holds only value data so pipeline state never
/// roots a syntax tree; re-materializes a <see cref="Location"/> at report time.
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo From(Location location) => new(
        location.SourceTree?.FilePath ?? location.GetLineSpan().Path ?? string.Empty,
        location.SourceSpan,
        location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}
