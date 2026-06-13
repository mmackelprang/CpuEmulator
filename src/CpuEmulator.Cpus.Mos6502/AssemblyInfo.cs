using System.Runtime.CompilerServices;

// The IL-JIT (CpuEmulator.Jit) reaches the internal AdvanceCycles seam on the hand-written
// Mos6502Cpu partial via reflection + DynamicMethod(skipVisibility: true). The generated
// _cycles field stays private (its "only ReadBus/WriteBus touch it" invariant preserved for
// the interpreter); AdvanceCycles is the explicit, named seam the emitted fastmem fast path
// uses to keep CycleCount in step with the interpreter's notion.
[assembly: InternalsVisibleTo("CpuEmulator.Jit")]
