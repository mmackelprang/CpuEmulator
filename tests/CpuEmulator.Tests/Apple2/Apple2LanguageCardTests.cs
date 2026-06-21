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
}
