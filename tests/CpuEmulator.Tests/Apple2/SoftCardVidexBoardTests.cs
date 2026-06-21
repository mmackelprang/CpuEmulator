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

    [Fact]
    public void VidexRom_char_path_is_null_when_absent_under_an_empty_root()
    {
        string emptyRoot = Path.Combine(Path.GetTempPath(), $"empty-videx-{Guid.NewGuid():N}");
        Assert.Null(VidexRom.TryGetCharRomPath(emptyRoot));
        Assert.Null(VidexRom.TryGetFirmwarePath(emptyRoot));
    }

    [Fact]
    public void VidexRom_loads_an_exact_2KiB_char_rom_and_rejects_a_wrong_length()
    {
        string root = Path.Combine(Path.GetTempPath(), $"videx-ok-{Guid.NewGuid():N}");
        string dir = Path.Combine(root, "videx");
        Directory.CreateDirectory(dir);
        try
        {
            string good = Path.Combine(dir, "videx-char.rom");
            File.WriteAllBytes(good, new byte[VidexRom.CharLength]);   // 2048
            byte[]? rom = VidexRom.TryLoadCharRom(root);
            Assert.NotNull(rom);
            Assert.Equal(VidexRom.CharLength, rom!.Length);

            File.WriteAllBytes(good, new byte[100]);                   // wrong length
            Assert.Throws<InvalidDataException>(() => VidexRom.TryLoadCharRom(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SoftCardVidexSurface_constructs_renders_and_wires_the_auto_switch()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        var bootRom = DiskBootRom();
        IBlockDevice cpm = new DiskImage(new byte[SoftCardCpm.DiskLength], 256, isReadOnly: true);

        byte[]? lastFrame = null;
        CpuEmulator.Surface.Web.SoftCardVidexSurface surface =
            CpuEmulator.Surface.Web.SoftCardVidexSurface.Create(rom, bootRom, charRom: null,
                videxCharRom: null, videxFirmware: null, cpmDisk: cpm, f => lastFrame = f, _ => { });

        surface.Host.RunHeadless(totalCycles: 40_000, sliceCycles: 17_030);

        // At boot the Apple 40-col video is the active display source (index 0): a 280x192 frame.
        Assert.NotNull(lastFrame);
        Assert.Equal((byte)'F', lastFrame![0]);
        Assert.Equal((byte)'B', lastFrame[1]);
        int width = lastFrame[4] | (lastFrame[5] << 8);
        int height = lastFrame[6] | (lastFrame[7] << 8);
        Assert.Equal(280, width);
        Assert.Equal(192, height);
        Assert.Equal(0, surface.Display.ActiveIndex);   // Apple-40 active at boot
        Assert.NotNull(surface.Machine.Coprocessor);    // the Z80 is wired

        // The auto-switch is wired: when the Videx signals active, the multiplexer follows (the same path
        // CP/M's terminal driver drives). This proves the ActiveChanged -> SetActive wiring without a boot.
        surface.Videx.SetActiveForTest(true);
        Assert.Equal(1, surface.Display.ActiveIndex);   // now the Videx 80-col is active
    }
}
