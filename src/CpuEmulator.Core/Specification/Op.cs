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

// ── I/O-port + halt class (M3.2 — additive; the 6502 uses none) ─────────────
public sealed record PortInOp(string Target) : Op;   // IN reg,(port) — read the Io bus into reg
public sealed record PortOutOp(string Source) : Op;  // OUT (port),reg — write reg to the Io bus
public sealed record HaltOp : Op;                     // HALT (Z80) / STOP (68000) — the generic halted state

// ── Composable flag micro-ops (M3.4a — general, 8086-reusable; the 6502 uses none) ──────────
// Each modifies the Status register's named-flag bits via the per-spec FlagBit map (Ground truth C).
public sealed record SetSZOp(string Source) : Op;      // S = src bit7; Z = (src == 0)
public sealed record SetParityOp(string Source) : Op;  // P/V = even parity of src (logic ops, LD A,I/R)
public sealed record SetXYOp(string Source) : Op;      // X = src bit3; Y = src bit5 (the undocumented copies)
public sealed record SetAddSubOp(bool Subtract) : Op;  // N = 0 (add) or 1 (subtract)
