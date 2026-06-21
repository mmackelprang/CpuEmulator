using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardVidexBoardTests
{
    private static byte[] DiskBootRom()
    {
        var rom = new byte[Apple2Rom.DiskRomLength];   // 256 B
        rom[0x01] = 0x20; rom[0x03] = 0x00; rom[0x05] = 0x03; rom[0x07] = 0x3C;  // slot-6 signature
        rom[0x00] = 0xA9;
        return rom;
    }

    private static (Machine machine, VidexVideoterm videx) BuildSoftCardVidex(byte[] systemRom)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);   // PR-N's 4-arg ctor (the Videx delegate)
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, DiskBootRom(), videx);
        return (BoardMachineFactory.Build(spec), videx);   // interpreter tier; the coprocessor is interpreter
    }

    [Fact]
    public void The_board_wires_a_Z80_coprocessor_a_control_port_and_the_Videx_window()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;            // reset -> $D000
        (Machine machine, VidexVideoterm videx) = BuildSoftCardVidex(rom);
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // The Z80 coprocessor is wired + dormant at reset (PR-I).
        Assert.NotNull(machine.Coprocessor);
        Assert.False(machine.CoprocessorActive);

        // The Videx $CC00 VRAM window is live writable RAM (the Videx Remapped it in Realize, PR-N).
        bus.Write8(0xCC00, 0x42);
        Assert.Equal(0x42, videx.PeekVramForTest(0, 0));

        // The $C800 firmware window is ROM (Remapped read-only).
        byte before = bus.Read8(0xC800);
        bus.Write8(0xC800, 0x99);
        Assert.Equal(before, bus.Read8(0xC800));
    }

    [Fact]
    public void The_board_carries_both_the_softcard_control_port_and_the_videx_slot()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(rom);
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(rom, iou, disk, DiskBootRom(), videx);

        Assert.NotNull(spec.Coprocessor);
        Assert.Equal(CpuKind.Z80, spec.Coprocessor!.Cpu);
        // The control-port slot name matches the coprocessor's ControlPortPeripheral (PR-I's validator
        // contract), and the Videx slot is present.
        Assert.Equal(spec.Coprocessor.ControlPortPeripheral,
            spec.Peripherals.Single(p => p.Name == "softcard").Name);
        Assert.Contains(spec.Peripherals, p => p.Name == "videx");
        Assert.Contains(spec.Peripherals, p => p.Name == "iou");
    }
}
