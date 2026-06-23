using System.Reflection;

namespace CpuEmulator.Core.Jit;

/// <summary>The per-CPU static seam the generic block compiler resolves its CPU-specific reflection +
/// decode through (J1). Lets <c>BlockCompiler&lt;TCpu&gt;</c> stay generic while still emitting DIRECT
/// field access against the concrete CPU type (the baked-<see cref="FieldInfo"/> speed premise — NOT an
/// <c>ICpuCore</c>-virtual rewrite). AOT-clean: carries reflection handles + a decode delegate, never
/// <c>Reflection.Emit</c> (that is the JIT assembly's job). One generated implementation per CPU
/// (CpuEmitter emits <c>JitTarget</c>); a CPU with no DecodeStructure (6502) and one with (Z80) both fit.</summary>
public interface IJitTarget
{
    /// <summary>The concrete interpreter CPU type — the emitted block's first parameter type.</summary>
    System.Type CpuType { get; }

    /// <summary>The status/flags register field (6502 "P", Z80 "F"), resolved by name on the CPU type —
    /// the data-driven replacement for the baked <c>FP</c>.</summary>
    FieldInfo StatusField { get; }

    /// <summary>The program-counter field ("PC"), resolved by name — the baked <c>FPC</c> replacement.</summary>
    FieldInfo ProgramCounterField { get; }

    /// <summary>The accumulator field (6502 "A", Z80 "A") — the baked <c>FA</c> replacement. The 6502
    /// ALU/decimal arms reference it directly; Z80 emitted arms (5-3b) resolve their own targets via the
    /// operand register map, so this is the 6502-convention handle the shared arms use.</summary>
    FieldInfo AccumulatorField { get; }

    /// <summary>The interpreter <c>Step()</c> — the fallback callout target.</summary>
    MethodInfo StepMethod { get; }

    /// <summary>The internal <c>AdvanceCycles(long)</c> — the cycle-charge target (skipVisibility reaches it).</summary>
    MethodInfo AdvanceCyclesMethod { get; }

    /// <summary>The <c>CycleCount</c> getter — the fallback's consumed-cycle delta math.</summary>
    MethodInfo CycleCountGetter { get; }

    /// <summary>The <c>InterruptPending</c> getter — the chain-edge interrupt sample.</summary>
    MethodInfo InterruptPendingGetter { get; }

    /// <summary>Run the generated decode walk from a fetch stream (the J3 seam): returns the key + the
    /// COMPUTED length. Wraps the static <c>Decode</c> so the compiler never names a concrete CPU type.</summary>
    DecodeResult Decode(IFetchStream stream);

    /// <summary>Resolve an operation-key to its descriptor (the keyed table for a structured CPU; the
    /// dense [256] array for the 6502). Wraps the static <c>DescriptorFor</c>.</summary>
    OpcodeDescriptor DescriptorFor(uint operationKey);

    /// <summary>The CPU's declared register names — the operand register map (<c>_regFields</c>) is built
    /// from these (J2). The 6502 names A/X/Y/S/P/PC; the Z80 names its full 36-entry file.</summary>
    System.Collections.Generic.IReadOnlyList<string> RegisterNames { get; }

    /// <summary>Project the CPU's current execution point to the 32-bit block-cache key (ADR 0019).
    /// For a flat-PC CPU this is <c>(uint)PC</c> — the identity, byte-identical to the old ushort key.
    /// For the 8086 it folds the segmented origin: <c>((CS&lt;&lt;4)+IP)&amp;0xFFFFF</c> — the same physical
    /// the decode/fetch already compute (the linear entry, ADR 0019 Decision 1). Read once per block
    /// dispatch (NOT per instruction — chaining stays inside the emitted block). Reads the CPU through
    /// <see cref="ICpuCore.GetRegister(string)"/> — interface-only, AOT-clean, no per-dispatch reflection.</summary>
    uint ProjectBlockKey(CpuEmulator.Core.ICpuCore cpu);
}
