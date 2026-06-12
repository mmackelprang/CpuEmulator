using System.Collections.Immutable;

namespace CpuEmulator.Generators;

internal sealed record SpecModel(
    string Namespace,
    string CpuName,
    string Architecture,
    ImmutableArray<RegisterModel> Registers,
    ImmutableArray<InstructionModel> Instructions);

internal sealed record RegisterModel(string Name, int Bits, string Role);

internal sealed record InstructionModel(byte Opcode, string Mnemonic, string Mode, ImmutableArray<OpModel> Ops);

internal sealed record OpModel(string Kind, ImmutableArray<string> Args);

/// <summary>Parser output: a model (null when errors prevented one) plus diagnostics.</summary>
internal sealed record ParsedSpec(SpecModel? Model, ImmutableArray<DiagnosticInfo> Diagnostics);
