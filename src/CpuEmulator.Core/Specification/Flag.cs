namespace CpuEmulator.Core.Specification;

/// <summary>Status-register flag NAMES. The numeric VALUE of the 6502 members is the 6502
/// P-register bit position (bit 7→0: N V - B D I Z C), so <c>1 &lt;&lt; (int)flag</c> yields the
/// hardware mask for a spec that declares NO <see cref="FlagLayout"/> (the 6502 default).
///
/// M3.4a (additive): the Z80 flag NAMES (S/H/P/Y/X) join the enum. Their numeric values are NOT
/// load-bearing — a spec that uses them declares a <see cref="FlagLayout"/> that resolves each
/// name's bit position per-spec (the Z80 maps S=7 H=4 P=2 N=1 Y=5 X=3 Z=6 C=0). Values are chosen
/// to not collide with the 6502 members so the enum stays well-formed. Adding members does not
/// change existing members' values, so the 6502 emitter (which reads only C/Z/I/D/V/N) is
/// byte-identical.</summary>
public enum Flag
{
    // 6502 P-register bit positions (UNCHANGED — the 6502 emitter reads these directly).
    C = 0,
    Z = 1,
    I = 2,
    D = 3,
    V = 6,
    N = 7,
    // Z80 flag NAMES (M3.4a, additive). Numeric value is NOT the Z80 bit position — the Z80
    // declares a FlagLayout that overrides per-spec. Chosen distinct from the 6502 members.
    S = 8,
    H = 9,
    P = 10,
    Y = 11,
    X = 12,
    // ── M5 (8086) additions (ADR 0005 Decision 3). Six 8086 flags reuse existing members (C=CF,
    //    P=PF, Z=ZF, S=SF, V=OF, I=IF) at 8086 bit positions assigned per-spec via FlagLayout; AF
    //    reuses H (the BCD half-carry — semantically identical). Two are genuinely new: ──────────────
    /// <summary>8086 TF — the trap (single-step) flag. New vocabulary; no prior CPU has it.</summary>
    T = 13,
    /// <summary>8086 DF — the direction flag (string-op address step: 0 ⇒ inc, 1 ⇒ dec). Distinct
    /// from the 6502 decimal flag D (different bit, different meaning) — a separate member by
    /// design (ADR 0005 Decision 3).</summary>
    Df = 14,
}
