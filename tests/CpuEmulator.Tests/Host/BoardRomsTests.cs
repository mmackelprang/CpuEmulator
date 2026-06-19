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

    [Fact]
    public void Z80_boot_rom_is_8_kib_and_blank_the_program_runs_from_ram()
    {
        // The Z80 boots from RAM at $0000; its ROM image is unused by the boot, but the
        // recipe requires an 8 KiB image. The registry pokes the program into RAM at boot.
        byte[] rom = BoardRoms.Z80Boot();
        Assert.Equal(0x2000, rom.Length);
    }

    [Fact]
    public void Z80_boot_program_is_the_OK_writer()
    {
        // LD A,'O' / LD ($C000),A ... ends with HALT (0x76).
        byte[] prog = BoardRoms.Z80BootProgram();
        Assert.Equal(0x3E, prog[0]);          // LD A,imm
        Assert.Equal((byte)'O', prog[1]);
        Assert.Equal(0x76, prog[^1]);         // HALT
    }

    [Fact]
    public void M68000_boot_rom_is_64_kib_with_reset_vectors()
    {
        byte[] rom = BoardRoms.M68000Boot();
        Assert.Equal(0x1_0000, rom.Length);
        // PC vector (big-endian long at $4) -> program entry $00000008.
        Assert.Equal(0x00, rom[0x4]);
        Assert.Equal(0x00, rom[0x5]);
        Assert.Equal(0x00, rom[0x6]);
        Assert.Equal(0x08, rom[0x7]);
    }

    [Fact]
    public void I8086_boot_rom_is_64_kib_with_far_jmp_at_the_reset_entry()
    {
        byte[] rom = BoardRoms.I8086Boot();
        Assert.Equal(0x1_0000, rom.Length);
        // Reset entry at image offset 0xFFF0 = physical 0xFFFF0: FAR JMP F000:0000 = EA 00 00 00 F0.
        Assert.Equal(0xEA, rom[0xFFF0]);
        Assert.Equal(0x00, rom[0xFFF1]);
        Assert.Equal(0x00, rom[0xFFF2]);
        Assert.Equal(0x00, rom[0xFFF3]);
        Assert.Equal(0xF0, rom[0xFFF4]);
    }
}
