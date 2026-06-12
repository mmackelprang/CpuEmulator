namespace CpuEmulator.Core.Specification;

/// <summary>Base of the closed micro-op vocabulary. The generator pattern-matches the
/// factory calls in <see cref="Spec"/> by name; these records exist so spec tables
/// type-check and tooling can navigate them.</summary>
public abstract record Op;

public sealed record LoadRegOp(Reg Target) : Op;
public sealed record StoreRegOp(Reg Source) : Op;
public sealed record TransferOp(Reg Source, Reg Target) : Op;
public sealed record IncrementOp(Reg Target) : Op;
public sealed record SetNZOp(Reg Source) : Op;
public sealed record JumpOp : Op;
public sealed record BranchIfOp(Flag Flag, bool When) : Op;
