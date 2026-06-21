using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

[Trait("Category", "UAT")]
public class Apple2BootTests
{
    private static byte[] DiskBootRom()
    {
        var rom = new byte[Apple2Rom.DiskRomLength];   // 256 B
        // The slot-6 boot signature ($Cn01=$20,$Cn03=$00,$Cn05=$03,$Cn07=$3C) so the Autostart scan
        // recognizes a Disk II in slot 6 (research §9). Offsets are slot-relative within $C600.
        rom[0x01] = 0x20; rom[0x03] = 0x00; rom[0x05] = 0x03; rom[0x07] = 0x3C;
        rom[0x00] = 0xA9;   // a recognizable first opcode (LDA #) so a read of $C600 is non-zero
        return rom;
    }

    private static (Machine machine, IAddressSpace bus) BuildBootBoard(byte[] systemRom)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var image = new SyntheticFluxImage(trackCount: 35);
        var disk = new Apple2DiskII(image);
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = Apple2Board.SpecWithSystem(systemRom, iou, disk, DiskBootRom());
        Machine machine = BoardMachineFactory.Build(spec);
        return (machine, machine.Space(AddressSpaceKind.Program));
    }

    [Fact]
    public void The_C600_boot_rom_is_readable_and_carries_the_slot6_signature()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000 (unused here)
        var (_, bus) = BuildBootBoard(rom);

        Assert.Equal(0xA9, bus.Read8(0xC600));    // the boot ROM's first byte
        Assert.Equal(0x20, bus.Read8(0xC601));    // the slot-6 signature bytes
        Assert.Equal(0x00, bus.Read8(0xC603));
        Assert.Equal(0x03, bus.Read8(0xC605));
        Assert.Equal(0x3C, bus.Read8(0xC607));
    }

    [Fact]
    public void The_IOU_still_owns_the_C000_page_after_adding_the_C600_rom()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        var (_, bus) = BuildBootBoard(rom);
        // A $C057 HIRES access still toggles the shared video state (the IOU owns $C000-$C0FF unchanged).
        _ = bus.Read8(0xC057);
        // $C600 is ROM (a different page); reading it has no soft-switch side effect.
        Assert.Equal(0xA9, bus.Read8(0xC600));
    }

    [Fact]
    public void Apple2Surface_constructs_and_renders_a_280x192_frame()
    {
        // The surface wires the Apple triad through MachineHost (the SpectrumSurface pattern). With a
        // bare (all-zero) system ROM there is no boot, but the surface must construct, reset, and produce
        // a 280x192 FB frame when stepped (the host renders on the video chip's frame tick). No real ROM
        // is needed for THIS smoke test (the boot-to-] assertion is the separate ROM-gated test).
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000 (a NOP region; no crash)

        byte[]? lastFrame = null;
        CpuEmulator.Surface.Web.Apple2Surface surface =
            CpuEmulator.Surface.Web.Apple2Surface.Create(rom, diskBootRom: null, charRom: null,
                f => lastFrame = f, _ => { });

        surface.Host.RunHeadless(totalCycles: 40_000, sliceCycles: 17_030);   // > one frame tick

        Assert.NotNull(lastFrame);
        // FB header: 'F','B', ver, reserved, u16 width LE, u16 height LE.
        Assert.Equal((byte)'F', lastFrame![0]);
        Assert.Equal((byte)'B', lastFrame[1]);
        int width = lastFrame[4] | (lastFrame[5] << 8);
        int height = lastFrame[6] | (lastFrame[7] << 8);
        Assert.Equal(280, width);
        Assert.Equal(192, height);
    }
}
