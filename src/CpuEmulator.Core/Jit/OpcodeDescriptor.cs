namespace CpuEmulator.Core.Jit;

/// <summary>How an opcode affects block flow — the compiler decision to continue, end,
/// or bail the block. Mirrors the generator InstructionClass plus the JIT-only
/// distinction the interpreter does not need (whether the op ends a block).</summary>
public enum JitOpClass
{
    Load, Store, Register, Transfer,    // straight-line; block continues
    Alu, Rmw,                           // straight-line (Alu rows for ADC/SBC carry NeedsFallback)
    Branch,                             // conditional control flow; ends a block
    Jump, Jsr, Rts,                     // unconditional control flow; ends a block
    Flow,                               // BRK/RTI — interrupt/vector machinery; fallback + ends block
    Undefined,                          // not in the dispatch table; fallback + ends block
}

/// <summary>Addressing mode — the same closed set as Core.Specification.AddrMode, copied into
/// the JIT data layer so the descriptor table has no dependency on the spec-authoring types
/// (which are generator-facing). One-to-one with AddrMode; the generator maps across.</summary>
public enum JitMode
{
    Implied, Accumulator, Immediate,
    ZeroPage, ZeroPageX, ZeroPageY,
    Absolute, AbsoluteX, AbsoluteY,
    IndirectX, IndirectY, Indirect, Relative,
    IoPortImmediate,   // (n) — Z80 IN A,(n)/OUT (n),A. Additive (M3.2); no 6502 row names it.
    IoPortIndirect,    // (C) — Z80 IN r,(C)/OUT (C),r. Additive (M3.2); no 6502 row names it.
}

/// <summary>One micro-op the compiler emits, in spec order. Kind is the interpreter OpModel
/// kind string ("Ora", "ShiftLeft", "Compare", "BranchIf", "SetFlag", ...) — the SAME closed
/// vocabulary the CpuEmitter switches on; RegA/RegB carry register operands as register-NAME
/// strings (e.g. Compare carries the compared register's name; Transfer carries source+target;
/// BranchIf carries the flag bit in FlagBit + the When sense in BoolArg). An empty string ""
/// marks "no register operand" (the zero-arg ops). The descriptorized form of OpModel.
///
/// M3.1a (J2): RegA/RegB are NAMES, not byte indices. The register file is DATA — a name
/// resolves against whatever register set the spec declared (BlockCompiler builds a per-compile
/// name→FieldInfo map), so adding a register needs no fixed-ordering edit.</summary>
public readonly record struct JitOp(string Kind, string RegA, string RegB, byte FlagBit, bool BoolArg);

/// <summary>How the decode walk computes this instruction's byte length. The 6502 is Fixed
/// (length is a constant per addressing mode — the degenerate case). A length-determining
/// mid-stream byte (the 8086 ModR/M case, the synthetic CPU's proof) is ModRmDetermined: the
/// walk reads one more byte and that byte's value sets how many MORE follow. This enum is the
/// seam that lets Length be a genuine computation while the 6502 stays trivially Fixed.</summary>
public enum LengthRule
{
    Fixed,            // length = FixedLength (6502: 1/2/3 per mode)
    ModRmDetermined,  // length = base + f(the next consumed byte) — the synthetic/8086 case
}

/// <summary>One opcode row. Immutable value data; the whole table is a static readonly array.</summary>
public sealed record OpcodeDescriptor(
    byte Opcode,
    string Mnemonic,            // for diagnostics + the disassembler cross-check
    JitMode Mode,
    JitOpClass Class,
    LengthRule LengthRule,      // how the walk computes length (REPLACES the static int Length)
    int FixedLength,            // the per-mode constant (LengthRule.Fixed); the base before the
                                // variable tail (LengthRule.ModRmDetermined). This is the walk's
                                // INPUT for the easy case — the walk consumes FixedLength units and
                                // returns UnitsConsumed (still a computation, not a bypass; GT B).
    int BaseCycles,             // the ComputeCycles value for (mode, class) — the cycle template
    bool PageCrossPenalty,      // true => +1 cycle when the indexed read crosses a page (GT F)
    bool NeedsFallback,         // true => the compiler emits an interpreter-Step callout, not IL
    bool EndsBlock,             // true => discovery stops AFTER this opcode (control flow / fallback)
    System.Collections.Immutable.ImmutableArray<JitOp> Ops)
{
    /// <summary>The sentinel for an opcode absent from the dispatch table. NeedsFallback +
    /// EndsBlock so an undefined opcode in a run is serviced by the interpreter (whose
    /// HandleUndefinedOpcode owns the policy) and ends the block.</summary>
    public static OpcodeDescriptor Undefined(byte opcode) => new(
        opcode, "???", JitMode.Implied, JitOpClass.Undefined,
        LengthRule.Fixed, FixedLength: 1, BaseCycles: 0, PageCrossPenalty: false,
        NeedsFallback: true, EndsBlock: true, Ops: []);
}
