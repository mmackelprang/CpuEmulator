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

    [Fact]
    public void A_translated_view_routes_writes_to_the_shared_6502_physical_address()
    {
        var ram = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        ram.MapMemory(0x0000, new byte[0x10000], writable: true);   // 64 KiB shared 6502 RAM
        var z80View = new TranslatingAddressSpace(ram, new SoftCardTranslation());

        // Branch 6: Z80 $F000 -> 6502 $0000 (CP/M's zero page lands on the Apple's low RAM).
        z80View.Write8(0xF000, 0xCA);
        Assert.Equal(0xCA, ram.Read8(0x0000));

        // Branch 2: Z80 $B000 -> 6502 $D000 (CP/M high RAM lands on the Language Card region).
        z80View.Write8(0xB000, 0x5A);
        Assert.Equal(0x5A, ram.Read8(0xD000));

        // And a Z80 read sees what the 6502 wrote at the translated address.
        ram.Write8(0x1000, 0x99);            // 6502 $1000 == Z80 $0000 (branch 1, +$1000)
        Assert.Equal(0x99, z80View.Read8(0x0000));
    }
}
