// M5.1: the 8086 does NOT yet go through the IL-JIT — that is M5.6. The M68000's AssemblyInfo adds
// [assembly: InternalsVisibleTo("CpuEmulator.Jit")] (M4.6) so the JIT can reach the internal
// AdvanceCycles charge seam by reflection; the 8086 has no JIT path in M5.1, so no InternalsVisibleTo
// is needed here. When M5.6 routes the 8086 through CpuEmulator.Jit, add the InternalsVisibleTo line
// (and the System.Runtime.CompilerServices using) at that time. Kept as a comment-only file (no using
// directive) so it stays warning-clean under -warnaserror.
