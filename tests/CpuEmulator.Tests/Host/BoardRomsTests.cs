using CpuEmulator.Host;
using Xunit;

namespace CpuEmulator.Tests.Host;

public class BoardRomsTests
{
    [Fact]
    public void Mos6502_demo_rom_is_8_kib()
    {
        byte[] rom = BoardRoms.Mos6502Demo();
        Assert.Equal(0x2000, rom.Length);
    }

    [Fact]
    public void Mos6502_demo_rom_carries_the_reset_vector_to_entry()
    {
        // The demo ROM image carries RESET ($FFFC/$FFFD) -> $E000. In the 8 KiB image
        // (base $E000) that is offset $1FFC/$1FFD = 0x00, 0xE0 (little-endian $E000).
        byte[] rom = BoardRoms.Mos6502Demo();
        Assert.Equal(0x00, rom[0x1FFC]);
        Assert.Equal(0xE0, rom[0x1FFD]);
    }
}
