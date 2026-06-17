using System.Runtime.CompilerServices;

// M4.6: the 68000 now goes through the IL-JIT (CpuEmulator.Jit). The JIT reaches the internal
// AdvanceCycles charge seam (on the hand-written M68000Cpu.Jit partial) via reflection +
// DynamicMethod(skipVisibility: true). The generated _cycles field stays private; AdvanceCycles is
// the explicit named seam — mirroring the Z80 + Mos6502 partials.
[assembly: InternalsVisibleTo("CpuEmulator.Jit")]
