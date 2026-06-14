namespace CpuEmulator.Core.Specification;

/// <summary>One architectural register. <paramref name="Bits"/> must be 8 or 16 (wider
/// registers arrive with a 16/32-bit CPU). Exactly one register must have the
/// <see cref="RegisterRole.ProgramCounter"/> role.
///
/// M3.4a (additive): a 16-bit register MAY declare <paramref name="HighHalf"/> + <paramref
/// name="LowHalf"/> naming two declared 8-bit registers — this makes it a bidirectional VIEW (the
/// Z80 pairs BC/DE/HL/AF over the 8-bit halves, Ground truth A). A view has NO backing field; the
/// generator emits a computed property <c>get => (high&lt;&lt;8)|low; set { high=value&gt;&gt;8;
/// low=value; }</c>. Both null (the 6502 default) ⇒ a plain stored register — byte-identical.</summary>
public sealed record RegisterDef(
    string Name, int Bits, RegisterRole Role = RegisterRole.General,
    string? HighHalf = null, string? LowHalf = null);
