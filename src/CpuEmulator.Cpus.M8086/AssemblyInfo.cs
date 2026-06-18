using System.Runtime.CompilerServices;

// M5.3: the segment-resolution layer (M8086Cpu.Ea.cs — Physical / ResolveSegment / ResolveEaPhysical +
// the EA read/write helpers) is `internal`, exposed to the test assembly so the EA/segmentation synthetic
// + unit proofs can drive it directly (the M68000 EA helper used the same InternalsVisibleTo pattern).
[assembly: InternalsVisibleTo("CpuEmulator.Tests")]

// M5.6: the 8086 now goes through the IL-JIT (CpuEmulator.Jit). The JIT reaches the internal AdvanceCycles
// charge seam (on the hand-written M8086Cpu.Jit partial) via reflection + DynamicMethod(skipVisibility: true).
// The generated _cycles field stays private; AdvanceCycles is the explicit named seam — mirroring the
// Z80 + Mos6502 + M68000 partials.
[assembly: InternalsVisibleTo("CpuEmulator.Jit")]
