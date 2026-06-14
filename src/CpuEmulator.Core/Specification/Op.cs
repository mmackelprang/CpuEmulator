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

// ── M3.4a Z80 base-plane micro-ops (additive; the 6502 uses none) ───────────────────────────
// 8-bit flag-correct ALU — A-implicit; the source is resolved by the addressing mode.
public sealed record Add8Op : Op;
public sealed record Adc8Op : Op;
public sealed record Sub8Op : Op;
public sealed record Sbc8Op : Op;
public sealed record And8Op : Op;
public sealed record Or8Op : Op;
public sealed record Xor8Op : Op;
public sealed record Cp8Op : Op;
// 8-bit INC/DEC (C preserved).
public sealed record IncRegOp(string Target) : Op;
public sealed record DecRegOp(string Target) : Op;
public sealed record IncMem8Op : Op;   // INC (HL)
public sealed record DecMem8Op : Op;   // DEC (HL)
// 16-bit ALU.
public sealed record Add16Op(string Target, string Source) : Op;  // ADD HL,rr
public sealed record Inc16Op(string Target) : Op;                 // INC rr (no flags)
public sealed record Dec16Op(string Target) : Op;                 // DEC rr (no flags)
// 16-bit LD.
public sealed record Load16Op(string Target) : Op;     // LD rr,nn
public sealed record Store16Op(string Source) : Op;    // LD (nn),rr
public sealed record LoadMem16Op(string Target) : Op;  // LD rr,(nn)
public sealed record StoreImm8Op : Op;                 // LD (HL),n
// Pair stack.
public sealed record Push16Op(string Pair) : Op;
public sealed record Pop16Op(string Pair) : Op;
// Exchange.
public sealed record ExDeHlOp : Op;
public sealed record ExAfAfOp : Op;
public sealed record ExxOp : Op;
public sealed record ExSpHlOp : Op;
// Flow (conditional + relative). cc = Flag + sense pair.
public sealed record JumpIfOp(Flag Cc, bool Sense) : Op;
public sealed record CallIfOp(Flag Cc, bool Sense) : Op;
public sealed record RetCcOp(Flag Cc, bool Sense) : Op;
public sealed record RelJumpOp : Op;
public sealed record RelJumpIfOp(Flag Cc, bool Sense) : Op;
public sealed record DjnzOp(string Counter) : Op;
public sealed record RstOp : Op;            // RST n — vector from the opcode
public sealed record JumpIndirectOp : Op;   // JP (HL)
public sealed record JumpAbsOp : Op;        // JP nn
public sealed record CallAbsOp : Op;        // CALL nn
public sealed record RetOp : Op;            // RET
// Misc.
public sealed record DaaOp : Op;
public sealed record CplOp : Op;
public sealed record ScfOp : Op;
public sealed record CcfOp : Op;
public sealed record DiOp : Op;
public sealed record EiOp : Op;

// ── M3.4b CB plane + rotate-accumulators (additive; the 6502 + base plane name none) ──
// Base-plane rotate-accumulators (RLCA/RRCA/RLA/RRA) — share the rotate math but PRESERVE S/Z/P-V.
public sealed record RlcaOp : Op;
public sealed record RrcaOp : Op;
public sealed record RlaOp : Op;
public sealed record RraOp : Op;
// CB rotate/shift on reg[z] (Op ∈ RLC/RRC/RL/RR/SLA/SRA/SLL/SRL). Target = "B".."A" or "(HL)".
public sealed record CbRotateOp(string Op, string Target) : Op;
// CB BIT/RES/SET — Op ∈ BIT/RES/SET, Bit = 0..7, Target = "B".."A" or "(HL)".
public sealed record CbBitOp(string Op, int Bit, string Target) : Op;

// ── M3.4c ED-core plane (additive; the 6502 + base + CB name none) ──
// IN r,(C) / OUT (C),r. Target/Source ∈ "B".."A"; "(none)"/"(zero)" for the y=6 IN (C)/OUT (C),0 forms.
public sealed record EdInOp(string Target) : Op;
public sealed record EdOutOp(string Source) : Op;
// 16-bit ADC/SBC HL,rp. Op ∈ "ADC"/"SBC"; Pair ∈ "BC"/"DE"/"HL"/"SP".
public sealed record EdAdcSbc16Op(string Op, string Pair) : Op;
// LD (nn),rp / LD rp,(nn). Op ∈ "STORE"/"LOAD"; Pair ∈ "BC"/"DE"/"HL"/"SP".
public sealed record EdLdNnRpOp(string Op, string Pair) : Op;
// NEG (A = 0 - A; canonical 0x44 + the undoc duplicates).
public sealed record EdNegOp : Op;
// RETN/RETI — pop PC, IFF1 = IFF2. IsReti distinguishes the disassembly/cycle (both do IFF1←IFF2).
public sealed record EdRetnOp(bool IsReti) : Op;
// IM 0/1/2 — set the interrupt mode.
public sealed record EdImOp(int Mode) : Op;
// LD I,A / R,A / A,I / A,R. Op ∈ "I_A"/"R_A"/"A_I"/"A_R" (A,I and A,R set P/V from IFF2).
public sealed record EdLdIaRaOp(string Op) : Op;
// RRD/RLD — nibble rotate between A's low nibble and (HL).
public sealed record EdRrdRldOp(bool IsRld) : Op;
// The undocumented ED NOP (0x77/0x7F) — no effect.
public sealed record EdNopOp : Op;
