using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CpuEmulator.Generators;

internal sealed record SpecModel(
    string Namespace,
    string CpuName,
    string Architecture,
    LocationInfo IdentifierLocation,
    EquatableArray<RegisterModel> Registers,
    EquatableArray<InstructionModel> Instructions);

internal sealed record RegisterModel(string Name, int Bits, string Role);

internal sealed record InstructionModel(byte Opcode, string Mnemonic, string Mode, EquatableArray<OpModel> Ops);

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
