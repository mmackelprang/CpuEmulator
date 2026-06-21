using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Microsoft Z-80 SoftCard's Z80-logical -> Apple-physical address translation (ADR 0015
/// Decision 3; research §2, the MAME-verified a2softcard.cpp dma_r/dma_w table). A 6-way branch on the
/// top nibble of the 16-bit logical address. ONLY branch 1 ($0000-$AFFF) is a true additive +$1000;
/// branches 2-6 mask the low 12 bits and add a 4 KiB-window base (page-wrap) so CP/M's zero page/TPA land
/// on usable RAM while the Apple's immovable regions (6502 zero page/stack, the $0400 screen, the $C0xx
/// I/O) shuffle to the top of the Z80 map.
/// <para>The "+$1000 mod 64K" shortcut is REFUTED (research §2, 1-2): it is correct only for branch 1 and
/// the coincidental branch 6, and WRONG for branches 2-5. The boundary regression test guards against
/// re-introducing it.</para>
/// <para>The DIP switch S1-1 (research §2) disables translation: when <c>translationEnabled</c> is false,
/// ToPhysical is the identity (the Z80 sees the raw 6502 space). Construction-time config, defaulted on.</para></summary>
public sealed class SoftCardTranslation : IAddressTranslation
{
    private readonly bool _translationEnabled;

    public SoftCardTranslation(bool translationEnabled = true) => _translationEnabled = translationEnabled;

    public uint ToPhysical(uint logical)
    {
        logical &= 0xFFFF;
        if (!_translationEnabled)
            return logical;                 // DIP S1-1 ON: identity (no translation)

        uint nibble = logical >> 12;        // the top nibble selects the branch
        uint low = logical & 0x0FFF;        // the in-window offset (branches 2-6)
        return nibble switch
        {
            <= 0xA => logical + 0x1000,      // branch 1: $0000-$AFFF -> $1000-$BFFF (additive)
            0xB    => low + 0xD000,          // branch 2: $B000-$BFFF -> $D000-$DFFF (LC bank 2)
            0xC    => low + 0xE000,          // branch 3: $C000-$CFFF -> $E000-$EFFF
            0xD    => low + 0xF000,          // branch 4: $D000-$DFFF -> $F000-$FFFF (ROM / LC $F000)
            0xE    => low + 0xC000,          // branch 5: $E000-$EFFF -> $C000-$CFFF (6502 I/O space)
            _      => low + 0x0000,          // branch 6: $F000-$FFFF -> $0000-$0FFF (ZP/stack/screen/RWTS)
        };
    }
}
