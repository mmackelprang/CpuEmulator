using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Machines;

public class DemoBoardRomTests
{
    [Fact]
    public void Build_returns_an_8kib_image_with_a_reset_vector_into_rom()
    {
        byte[] rom = DemoBoardRom.Build();

        Assert.Equal(0x2000, rom.Length);              // $E000-$FFFF
        // RESET vector at $FFFC/$FFFD points into ROM ($E000..$FFFF)
        ushort reset = (ushort)(rom[0x1FFC] | (rom[0x1FFD] << 8));
        Assert.InRange(reset, (ushort)0xE000, (ushort)0xFFFF);
        Assert.Equal((ushort)DemoBoardRom.Entry, reset);
    }

    [Fact]
    public void Build_is_deterministic()
    {
        Assert.Equal(DemoBoardRom.Build(), DemoBoardRom.Build());
    }
}
