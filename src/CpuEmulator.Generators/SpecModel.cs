using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CpuEmulator.Generators;

/// <summary>Instruction op-class. Classified ONCE in SpecParser (CPUGEN010 validation) and
/// carried on the model — the emitter consumes this and has no classifier of its own, so
/// parser and emitter cannot drift (2b-review carry-forward).</summary>
internal enum InstructionClass
{
    Register, Load, Store, Jump, Branch,
    // Added by later tasks in this plan: Alu (Task 5), Rmw (Task 6), Stack, Flow (Task 7).
}

internal sealed record SpecModel(
    string Namespace,
    string CpuName,
    string Architecture,
    LocationInfo IdentifierLocation,
    EquatableArray<RegisterModel> Registers,
    EquatableArray<InstructionModel> Instructions);

internal sealed record RegisterModel(string Name, int Bits, string Role);

internal sealed record InstructionModel(
    byte Opcode, string Mnemonic, string Mode, InstructionClass Class, EquatableArray<OpModel> Ops);

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
