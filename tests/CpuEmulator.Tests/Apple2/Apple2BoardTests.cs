using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2BoardTests
{
    // A 12 KiB "system ROM" whose reset vector $FFFC/$FFFD points into the ROM (a NOP-loop).
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];                 // $D000-$FFFF
        rom[0x0000] = 0xEA;                          // NOP at $D000
        rom[0x0001] = 0x4C; rom[0x0002] = 0x00; rom[0x0003] = 0xD0; // JMP $D000
        // RESET vector at $FFFC/$FFFD (offset 0x2FFC/0x2FFD) -> $D000.
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        return rom;
    }

    private static (BoardSpec spec, Apple2Iou iou, Apple2VideoState state) BuildSpec()
    {
        var state = new Apple2VideoState();
        var iou = new Apple2Iou(state);
        return (Apple2Board.Spec(SystemRom(), iou), iou, state);
    }

    [Fact]
    public void The_board_validates_with_no_diagnostics()
    {
        var (spec, _, _) = BuildSpec();
        Assert.Empty(BoardSpecValidator.Validate(spec));
    }

    [Fact]
    public void Build_maps_ram_the_C000_hole_and_the_system_rom()
    {
        var (spec, _, _) = BuildSpec();
        Machine m = BoardMachineFactory.Build(spec);
        var bus = m.Space(AddressSpaceKind.Program);

        Assert.IsType<Mos6502Cpu>(m.Cpu);
        bus.Write8(0x0000, 0x5A); Assert.Equal(0x5A, bus.Read8(0x0000)); // RAM low writable
        bus.Write8(0xBFFF, 0x3C); Assert.Equal(0x3C, bus.Read8(0xBFFF)); // RAM top writable
        Assert.Equal(0xEA, bus.Read8(0xD000));                            // ROM byte present
        bus.Write8(0xD000, 0xFF); Assert.Equal(0xEA, bus.Read8(0xD000));  // ROM read-only
    }

    [Fact]
    public void Reset_loads_PC_from_the_rom_vector()
    {
        var (spec, _, _) = BuildSpec();
        Machine m = BoardMachineFactory.Build(spec);
        m.Reset();
        Assert.Equal(0xD000u, m.Cpu.GetRegister("PC"));
    }

    [Fact]
    public void The_IOU_is_reachable_through_the_bus_at_C057_and_C030()
    {
        var (spec, _, state) = BuildSpec();
        Machine m = BoardMachineFactory.Build(spec);
        var bus = m.Space(AddressSpaceKind.Program);

        Assert.False(state.HiRes);
        _ = bus.Read8(0xC057);          // a bus read of $C057 routes to the IOU -> HIRES on
        Assert.True(state.HiRes);

        long before = state.SpeakerToggles;
        _ = bus.Read8(0xC030);          // $C030 toggles the speaker
        Assert.Equal(before + 1, state.SpeakerToggles);
    }

    [Fact]
    public void A_real_STA_C030_double_toggles_the_speaker_via_the_bus()
    {
        // Build a board whose RAM at $0300 holds: STA $C030 ; JMP $0300, and reset there.
        var state = new Apple2VideoState();
        var spec = Apple2Board.Spec(SystemRom(), new Apple2Iou(state));
        Machine m = BoardMachineFactory.Build(spec);
        var bus = m.Space(AddressSpaceKind.Program);
        // STA $C030 = 8D 30 C0 ; JMP $0300 = 4C 00 03
        bus.Write8(0x0300, 0x8D); bus.Write8(0x0301, 0x30); bus.Write8(0x0302, 0xC0);
        bus.Write8(0x0303, 0x4C); bus.Write8(0x0304, 0x00); bus.Write8(0x0305, 0x03);
        m.Cpu.SetRegister("PC", 0x0300);

        long before = state.SpeakerToggles;
        m.Run(8);                                   // run ~one STA (4 cyc) + part of the JMP
        // One STA $C030 must have toggled the speaker TWICE (the RMW dummy read + the store).
        Assert.True(state.SpeakerToggles >= before + 2,
            $"expected >= {before + 2} toggles after one STA $C030; got {state.SpeakerToggles}");
    }
}
