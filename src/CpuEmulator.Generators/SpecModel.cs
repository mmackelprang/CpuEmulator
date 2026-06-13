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
}

/// <summary>The operation-key packing a row declared (Ground truth C). OpcodeByte is the 6502
/// degenerate case (key == opcode). PrefixedOpcode packs (prefix &lt;&lt; 8) | opcode. OpcodeGroup
/// packs (opcode &lt;&lt; 3) | subfield (a non-first-byte sub-field). The generated Decode realizes
/// the packing; the consumers treat the key as opaque.</summary>
internal enum KeyShape { OpcodeByte, PrefixedOpcode, OpcodeGroup }

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
    FetchUnit FetchUnit = FetchUnit.Byte);

internal sealed record RegisterModel(string Name, int Bits, string Role);

/// <summary>The parsed decode structure (Ground truth G). ABSENT on the model means the 6502
/// degenerate walk. Carries the prefix bytes, the ModR/M (length-determining) opcodes, and the
/// opcode-group (sub-field-key) opcodes the synthetic spec declares.</summary>
internal sealed record DecodeStructureModel(
    EquatableArray<byte> Prefixes,
    EquatableArray<byte> ModRmOpcodes,
    EquatableArray<byte> SubFieldOpcodes);

internal sealed record InstructionModel(
    byte Opcode, string Mnemonic, string Mode, InstructionClass Class, EquatableArray<OpModel> Ops,
    uint OperationKey = 0,                         // the opaque key the walk computes (0 ⇒ defaulted to opcode)
    KeyShape KeyShape = KeyShape.OpcodeByte,
    int Prefix = -1,                               // the prefix byte (PrefixedOpcode rows); -1 if none
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
