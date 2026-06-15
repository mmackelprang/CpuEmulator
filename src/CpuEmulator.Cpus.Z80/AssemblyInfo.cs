using System.Runtime.CompilerServices;

// The IL-JIT (CpuEmulator.Jit) reaches the internal AdvanceCycles seam on the hand-written
// Z80Cpu.Jit partial via reflection + DynamicMethod(skipVisibility: true). The generated
// _cycles field stays private (the interpreter's cycle bookkeeping); AdvanceCycles is the
// explicit, named seam the emitted fastmem fast path will use (5-3b) to keep CycleCount in step
// with the interpreter's notion — mirroring the Mos6502 partial's AdvanceCycles seam.
[assembly: InternalsVisibleTo("CpuEmulator.Jit")]
