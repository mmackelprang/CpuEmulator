using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2LanguageCardTests
{
    // A 12 KiB system ROM with a recognisable byte at $D000 and a reset-vector NOP loop.
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x0000] = 0xA5;                                         // a MARKER byte at $D000 (ROM)
        rom[0x1000] = 0x5C;                                         // a marker at $E000 (ROM)
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;                     // reset -> $D000
        return rom;
    }

    // Build a real ][+ board WITH the Language Card wired (the PR-E overload).
    private static (Machine machine, IAddressSpace bus, Apple2LanguageCard lc) BuildWithLc(
        ExecutionTier tier = ExecutionTier.Interpreter)
    {
        byte[] rom = SystemRom();
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(rom);
        var iou = new Apple2Iou(state, lc);                        // PR-E: the IOU holds the LC
        BoardSpec spec = Apple2Board.SpecWithLanguageCard(rom, iou, lc);
        Machine machine = BoardMachineFactory.Build(spec, tier);
        return (machine, machine.Space(AddressSpaceKind.Program), lc);
    }

    [Fact]
    public void At_reset_D000_reads_the_system_ROM()
    {
        var (_, bus, _) = BuildWithLc();
        Assert.Equal(0xA5, bus.Read8(0xD000));   // power-on: read-ROM
        Assert.Equal(0x5C, bus.Read8(0xE000));
    }

    [Fact]
    public void A_C08x_access_reaches_the_LC_through_the_IOU()
    {
        var (_, bus, lc) = BuildWithLc();
        long before = lc.AccessCount;            // a test-only counter on the LC
        _ = bus.Read8(0xC080);                   // a bus read of $C080 must route IOU -> LC
        Assert.Equal(before + 1, lc.AccessCount);
    }

    [Fact]
    public void C083_selects_read_RAM_bank1_and_C081_keeps_read_ROM()
    {
        var (_, bus, _) = BuildWithLc();
        // First arm + enable bank-1 RAM (two consecutive reads of $C083 arm write; one read selects
        // read-RAM immediately). We do the two reads so the RAM is also writable for the poke.
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);    // read-RAM, bank 1, write-enabled
        bus.Write8(0xD000, 0x11);                         // poke RAM bank 1 (write-enabled)
        bus.Write8(0xE000, 0x22);                         // poke shared $E000
        Assert.Equal(0x11, bus.Read8(0xD000));            // reads now see RAM bank 1
        Assert.Equal(0x22, bus.Read8(0xE000));

        // $C081 = read ROM again (the marker bytes reappear).
        _ = bus.Read8(0xC081);
        Assert.Equal(0xA5, bus.Read8(0xD000));            // ROM marker at $D000
        Assert.Equal(0x5C, bus.Read8(0xE000));            // ROM marker at $E000
    }

    [Fact]
    public void Bank2_is_a_distinct_D000_region_from_bank1_but_E000_is_shared()
    {
        var (_, bus, _) = BuildWithLc();
        // Bank 1: read-RAM + write-enable via two $C083 reads; poke a distinct byte.
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);
        bus.Write8(0xD000, 0xB1);                          // bank-1 $D000
        bus.Write8(0xE000, 0xEE);                          // shared $E000

        // Bank 2: read-RAM + write-enable via two $C08B reads; poke a DIFFERENT byte at $D000.
        _ = bus.Read8(0xC08B); _ = bus.Read8(0xC08B);
        bus.Write8(0xD000, 0xB2);                          // bank-2 $D000 (distinct backing)
        Assert.Equal(0xB2, bus.Read8(0xD000));             // bank 2 shows its own byte
        Assert.Equal(0xEE, bus.Read8(0xE000));             // $E000 is SHARED -> bank-1's poke persists

        // Back to bank 1: its $D000 byte is intact (distinct region).
        _ = bus.Read8(0xC083);
        Assert.Equal(0xB1, bus.Read8(0xD000));
    }
}
