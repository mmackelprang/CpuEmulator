namespace CpuEmulator.Core.Specification;

/// <summary>A spec's status-flag bit layout (M3.4a, additive). ABSENT (the 6502 default) ⇒ the
/// generator keys each flag name off the <see cref="Flag"/> enum's numeric value (the 6502 bit
/// positions). Declaring a <c>FlagLayout</c> overrides the bit position PER-SPEC the same way
/// M3.1a made register identity per-spec and M3.1b made decode per-spec: flag identity is a name,
/// layout is spec data. The Z80 declares S=7 Z=6 Y=5 H=4 X=3 P=2 N=1 C=0; the 8086 reuses this
/// seam unchanged for its own layout. These types are inert syntax carriers for the generator —
/// record equality and array mutation are unsupported usage.</summary>
public sealed record FlagLayout(FlagBitDef[] Bits);

/// <summary>One flag name → hardware bit position. <paramref name="Bit"/> is 0–15 (0–7 for a byte
/// status register; the 68000's 16-bit SR uses 0–4 for the CCR and 8–15 for the system byte, M4.1).</summary>
public sealed record FlagBitDef(string Name, int Bit);
