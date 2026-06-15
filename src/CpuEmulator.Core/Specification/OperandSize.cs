namespace CpuEmulator.Core.Specification;

/// <summary>The 68000 operand-size axis (.b/.w/.l), M4.1 (ADR 0003 Decision 1). The size is a property of
/// the (instruction × micro-op), NOT of the register declaration: the SAME D0 is operated on at three
/// widths by the instruction, with partial-write semantics for data registers (.b/.w preserve the upper
/// bits) and whole-register-sign-extend for address registers (An.w writes all 32 bits, sign-extended).
///
/// M4.1 declares the type and stakes the name; it is NOT yet threaded onto any <see cref="Op"/>. The
/// size-bearing micro-ops (the 68000's Move/ALU family, with the partial-write / sign-extend / no-CCR-on-
/// An semantics in the op body) arrive with the first 68000 ALU-family PR (M4.5a), when real encodings
/// settle the extensible-operand-model shape (ADR 0003 §4 item 2 left this just-in-time). Byte/Word/Long
/// map naturally onto <see cref="CpuEmulator.Core.AccessWidth"/> (1/2/4) for the wide bus (M4.2).</summary>
public enum OperandSize
{
    Byte,
    Word,
    Long,
}
