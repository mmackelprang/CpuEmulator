namespace CpuEmulator.Core.Specification;

/// <summary>Base of the closed micro-op vocabulary. The generator pattern-matches the
/// factory calls in <see cref="Spec"/> by name; these records exist so spec tables
/// type-check and tooling can navigate them.</summary>
public abstract record Op;

public sealed record LoadRegOp(string Target) : Op;
public sealed record StoreRegOp(string Source) : Op;
public sealed record TransferOp(string Source, string Target) : Op;
public sealed record IncrementOp(string Target) : Op;
public sealed record SetNZOp(string Source) : Op;
public sealed record JumpOp : Op;
public sealed record BranchIfOp(Flag Flag, bool When) : Op;

// ── ALU class (Task 5) ───────────────────────────────────────────────────────
public sealed record AdcOp : Op;
public sealed record SbcOp : Op;
public sealed record AndOp : Op;
public sealed record OraOp : Op;
public sealed record EorOp : Op;
public sealed record CompareOp(string Source) : Op;
public sealed record BitOp : Op;

// ── RMW class (Task 6) ───────────────────────────────────────────────────────
public sealed record ShiftLeftOp : Op;
public sealed record ShiftRightOp : Op;
public sealed record RotateLeftOp : Op;
public sealed record RotateRightOp : Op;
public sealed record IncrementMemOp : Op;
public sealed record DecrementMemOp : Op;
public sealed record DecrementOp(string Target) : Op;

// ── Stack / flag / flow class (Task 7) ──────────────────────────────────────
public sealed record PushOp(string Source) : Op;
public sealed record PullOp(string Target) : Op;
public sealed record PushPOp : Op;
public sealed record PullPOp : Op;
public sealed record SetFlagOp(Flag Flag, bool Value) : Op;
public sealed record JsrOp : Op;
public sealed record RtsOp : Op;

// ── BRK/RTI flow class (Task 8 / 3b-ii) ────────────────────────────────────
public sealed record BrkOp : Op;
public sealed record RtiOp : Op;
