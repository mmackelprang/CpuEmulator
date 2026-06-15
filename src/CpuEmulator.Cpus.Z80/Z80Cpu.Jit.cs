using System.Reflection;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Cpus.Z80;

public sealed partial class Z80Cpu
{
    /// <summary>The Io-bus (16-bit port space) the JIT dispatcher routes Port-op callouts to (5-3b). In
    /// 5-3a every Z80 op falls back to the interpreter Step (which writes the ports directly), but the
    /// <see cref="CpuEmulator.Jit"/> <c>JittedCpu&lt;Z80Cpu&gt;</c> ctor still needs the bus handle. The Z80
    /// HAS an Io space (unlike the 6502), so this is a real second callout bus.</summary>
    public IAddressSpace IoBus => _io;

    /// <summary>The cycle-charge seam the JIT's emitted fastmem fast path calls (the 6502 has the same
    /// internal <c>AdvanceCycles</c> on its hand-written partial). The generated <c>JitTarget</c> resolves
    /// this by name (NonPublic | Instance). NO emitted Z80 op calls it in 5-3a (every op falls back, and
    /// the fallback uses the CycleCount delta), but the handle must RESOLVE — a recorded J1 finding: the
    /// Z80's generated cycle bookkeeping increments <c>_cycles</c> inline, so the per-CPU seam supplies this
    /// named charge method (the 6502 convention) for the emitted arms 5-3b adds.</summary>
    internal void AdvanceCycles(long n) => _cycles += n;

    /// <summary>The per-CPU JIT seam (J1). HAND-WRITTEN STUB for 5-3a Task 1; CpuEmitter emits the
    /// production version in Task 6 (so the 6502 + Z80 + synthetic CPUs all carry one). Resolves the
    /// reflection handles by name on the Z80 type — the data-driven replacement for the 6502's baked
    /// FA/FP/FPC/MStep/etc.</summary>
    public static readonly IJitTarget JitTarget = new Z80JitTarget();

    private sealed class Z80JitTarget : IJitTarget
    {
        public System.Type CpuType => typeof(Z80Cpu);
        public FieldInfo StatusField => typeof(Z80Cpu).GetField("F")!;
        public FieldInfo ProgramCounterField => typeof(Z80Cpu).GetField("PC")!;
        public FieldInfo AccumulatorField => typeof(Z80Cpu).GetField("A")!;
        public MethodInfo StepMethod => typeof(Z80Cpu).GetMethod("Step")!;
        public MethodInfo AdvanceCyclesMethod =>
            typeof(Z80Cpu).GetMethod("AdvanceCycles", BindingFlags.NonPublic | BindingFlags.Instance)!;
        public MethodInfo CycleCountGetter => typeof(Z80Cpu).GetProperty("CycleCount")!.GetGetMethod()!;
        public MethodInfo InterruptPendingGetter =>
            typeof(Z80Cpu).GetProperty("InterruptPending")!.GetGetMethod()!;
        public DecodeResult Decode(IFetchStream stream) => Z80Cpu.Decode(stream);
        public OpcodeDescriptor DescriptorFor(uint operationKey) => Z80Cpu.DescriptorFor(operationKey);
        // RegisterNames is an INSTANCE property (the ICpuCore surface) backed by the private static
        // s_registerNames array; the nested type reaches the private static directly (no instance needed).
        public System.Collections.Generic.IReadOnlyList<string> RegisterNames => s_registerNames;
    }
}
