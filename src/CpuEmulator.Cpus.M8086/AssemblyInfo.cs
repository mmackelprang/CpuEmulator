using System.Runtime.CompilerServices;

// M5.3: the segment-resolution layer (M8086Cpu.Ea.cs — Physical / ResolveSegment / ResolveEaPhysical +
// the EA read/write helpers) is `internal`, exposed to the test assembly so the EA/segmentation synthetic
// + unit proofs can drive it directly (the M68000 EA helper used the same InternalsVisibleTo pattern).
[assembly: InternalsVisibleTo("CpuEmulator.Tests")]

// M5.1 note (still valid): the 8086 does NOT yet go through the IL-JIT — that is M5.6. When M5.6 routes the
// 8086 through CpuEmulator.Jit, add [assembly: InternalsVisibleTo("CpuEmulator.Jit")] (the M4.6 pattern) so
// the JIT can reach the internal AdvanceCycles charge seam by reflection.
