using CpuEmulator.Core.Specification;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M5.1 — the three 8086 Flag-enum additions (ADR 0005 Decision 3). T (trap) and Df
/// (direction) are genuinely new vocabulary; AF reuses H (the BCD half-carry). These are
/// compile-time / enum-value assertions: the additive members keep every prior member's value
/// unchanged, so the existing 6502/Z80/68000 emitters stay byte-identical.</summary>
public class M8086FlagEnumTests
{
    [Fact]
    public void T_and_Df_exist_with_their_assigned_values()
    {
        Assert.Equal(13, (int)Flag.T);
        Assert.Equal(14, (int)Flag.Df);
    }

    [Fact]
    public void Df_is_distinct_from_the_6502_decimal_flag_D()
    {
        // ADR 0005 Decision 3: DF is a SEPARATE member from the 6502 decimal flag D — different
        // bit, different meaning — never aliased onto it.
        Assert.NotEqual(Flag.D, Flag.Df);
        Assert.NotEqual((int)Flag.D, (int)Flag.Df);
    }

    [Fact]
    public void The_6502_decimal_flag_D_is_unchanged()
    {
        // The additive members must not perturb any prior member's value (the byte-identity invariant).
        Assert.Equal(3, (int)Flag.D);
    }

    [Fact]
    public void AF_reuses_the_existing_H_member()
    {
        // The 8086 AF (BCD half-carry) reuses H — semantically identical to the Z80 half-carry, so
        // no new member is introduced for it.
        Assert.Equal((Flag)9, Flag.H);
    }
}
