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

    [Fact]
    public void One_read_of_an_odd_C08x_does_NOT_write_enable_LC_RAM()
    {
        var (_, bus, _) = BuildWithLc();
        _ = bus.Read8(0xC083);                 // ONE arm-read: read-RAM selected, but write NOT enabled
        bus.Write8(0xD000, 0x99);              // write to $D000 RAM -> should be IGNORED (write-protected)
        Assert.NotEqual(0x99, bus.Read8(0xD000));  // the poke did not take (RAM still write-protected)
    }

    [Fact]
    public void Two_consecutive_reads_of_an_odd_C08x_write_enable_LC_RAM()
    {
        var (_, bus, _) = BuildWithLc();
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);   // TWO consecutive arm-reads -> write-enabled
        bus.Write8(0xD000, 0x99);
        Assert.Equal(0x99, bus.Read8(0xD000));          // the poke took (RAM now writable)
    }

    [Fact]
    public void A_write_between_the_reads_resets_the_pre_write_flip_flop()
    {
        var (_, bus, _) = BuildWithLc();
        _ = bus.Read8(0xC083);                 // arm 1
        bus.Write8(0xC083, 0x00);              // a WRITE to $C083 resets the counter (not a qualifying read)
        _ = bus.Read8(0xC083);                 // arm 1 again (not 2) -> still write-protected
        bus.Write8(0xD000, 0x77);
        Assert.NotEqual(0x77, bus.Read8(0xD000));
    }

    [Fact]
    public void Presence_detection_a_write_test_to_D000_RAM_reads_back_when_64K()
    {
        var (_, bus, _) = BuildWithLc();
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);   // read-RAM + write-enable
        bus.Write8(0xD000, 0x3C);
        Assert.Equal(0x3C, bus.Read8(0xD000));          // write-then-read-back succeeds => 64K present
    }

    [Theory]
    [InlineData(ExecutionTier.Interpreter)]   // the oracle: correct with no listener
    [InlineData(ExecutionTier.Jit)]           // exercises PR-A's OnRemap -> reclassify + evict
    public void A_real_program_runs_code_out_of_LC_RAM(ExecutionTier tier)
    {
        var (machine, bus, _) = BuildWithLc(tier);

        // 1) Arm + write-enable read-RAM bank 1 via two $C083 reads (done from RAM-resident setup code).
        //    For the test we drive the banking through the bus directly, then load a routine into LC RAM,
        //    then jump to it.
        _ = bus.Read8(0xC083); _ = bus.Read8(0xC083);     // read-RAM, bank 1, write-enabled

        // 2) Write a tiny routine into LC RAM at $D000:  LDA #$42 ; STA $0400 ; JMP $D005 (spin)
        //    $D000: A9 42      LDA #$42
        //    $D002: 8D 00 04   STA $0400
        //    $D005: 4C 05 D0   JMP $D005   (spin in LC RAM)
        bus.Write8(0xD000, 0xA9); bus.Write8(0xD001, 0x42);
        bus.Write8(0xD002, 0x8D); bus.Write8(0xD003, 0x00); bus.Write8(0xD004, 0x04);
        bus.Write8(0xD005, 0x4C); bus.Write8(0xD006, 0x05); bus.Write8(0xD007, 0xD0);

        // 3) Execute from LC RAM.
        machine.Cpu.SetRegister("PC", 0xD000);
        machine.Run(50);

        // The routine, fetched + run FROM the remapped LC RAM page, wrote $42 to $0400.
        Assert.Equal(0x42, bus.Read8(0x0400));
    }
}
