using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardTranslationTests
{
    // The REFUTED shortcut (research §2, refuted 1-2): correct only for the low region. Used here ONLY
    // to prove the boundary test rejects it — branches 2-5 differ from the real table.
    private static uint Shortcut(uint logical) => (logical + 0x1000) & 0xFFFF;

    [Theory]
    // Branch 1 (additive) — the shortcut AGREES here, so this alone cannot detect the shortcut.
    [InlineData(0x0000u, 0x1000u)]
    [InlineData(0xAFFFu, 0xBFFFu)]
    // Branch 2 ($B000-$BFFF -> $D000-$DFFF) — SHORTCUT-KILLER (shortcut gives $C000/$CFFF).
    [InlineData(0xB000u, 0xD000u)]
    [InlineData(0xBFFFu, 0xDFFFu)]
    // Branch 3 ($C000-$CFFF -> $E000-$EFFF) — SHORTCUT-KILLER (shortcut gives $D000/$DFFF).
    [InlineData(0xC000u, 0xE000u)]
    [InlineData(0xCFFFu, 0xEFFFu)]
    // Branch 4 ($D000-$DFFF -> $F000-$FFFF) — SHORTCUT-KILLER (shortcut gives $E000/$EFFF).
    [InlineData(0xD000u, 0xF000u)]
    [InlineData(0xDFFFu, 0xFFFFu)]
    // Branch 5 ($E000-$EFFF -> $C000-$CFFF) — SHORTCUT-KILLER (shortcut gives $F000/$FFFF).
    [InlineData(0xE000u, 0xC000u)]
    [InlineData(0xEFFFu, 0xCFFFu)]
    // Branch 6 ($F000-$FFFF -> $0000-$0FFF) — the shortcut AGREES here (($F000+$1000) mod 64K = $0000),
    // so it pins the map but does NOT detect the shortcut on its own.
    [InlineData(0xF000u, 0x0000u)]
    [InlineData(0xFFFFu, 0x0FFFu)]
    public void ToPhysical_matches_the_six_branch_table_at_boundaries(uint logical, uint expected)
    {
        var t = new SoftCardTranslation();
        Assert.Equal(expected, t.ToPhysical(logical));
    }

    [Theory]
    // The four shortcut-killer boundaries: the real table MUST NOT equal the refuted shortcut here.
    // This is the structural guard against re-introducing the refuted map.
    [InlineData(0xB000u)]
    [InlineData(0xC000u)]
    [InlineData(0xD000u)]
    [InlineData(0xE000u)]
    public void ToPhysical_differs_from_the_refuted_shortcut_on_branches_2_through_5(uint logical)
    {
        var t = new SoftCardTranslation();
        Assert.NotEqual(Shortcut(logical), t.ToPhysical(logical));
    }

    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0xB000u)]
    [InlineData(0xE000u)]
    [InlineData(0xFFFFu)]
    public void DIP_disabled_translation_is_the_identity(uint logical)
    {
        var t = new SoftCardTranslation(translationEnabled: false);
        Assert.Equal(logical, t.ToPhysical(logical)); // identity: the Z80 sees the raw 6502 space
    }
}
