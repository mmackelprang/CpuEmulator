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
}

/// <summary>One micro-op the compiler emits, in spec order. Kind is the interpreter OpModel
/// kind string ("Ora", "ShiftLeft", "Compare", "BranchIf", "SetFlag", ...) — the SAME closed
/// vocabulary the CpuEmitter switches on; RegA/RegB carry register operands (e.g. Compare
/// carries the compared register; Transfer carries source+target; BranchIf carries the flag
/// bit in FlagBit + the When sense in BoolArg). The descriptorized form of OpModel.</summary>
public readonly record struct JitOp(string Kind, byte RegA, byte RegB, byte FlagBit, bool BoolArg);

/// <summary>One opcode row. Immutable value data; the whole table is a static readonly array.</summary>
public sealed record OpcodeDescriptor(
    byte Opcode,
    string Mnemonic,            // for diagnostics + the disassembler cross-check
    JitMode Mode,
    JitOpClass Class,
    int Length,                 // 1-3, the InstructionLength value (discovery advances PC by this)
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
        Length: 1, BaseCycles: 0, PageCrossPenalty: false,
        NeedsFallback: true, EndsBlock: true, Ops: []);
}
