namespace CpuEmulator.Core.Specification;

/// <summary>One architectural register. <paramref name="Bits"/> must be 8 or 16 (wider
/// registers arrive with a 16/32-bit CPU). Exactly one register must have the
/// <see cref="RegisterRole.ProgramCounter"/> role.</summary>
public sealed record RegisterDef(string Name, int Bits, RegisterRole Role = RegisterRole.General);
