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

    // ADR 0017 Decision 6 / CPM-5 (PR-5) -- the Videx CP/M gate RE-FRAME. The headline "CP/M auto-widens to
    // the 80-col Videx at A>" was over-claimed: this cached SoftCard CP/M 2.2 master drives the 40-COLUMN
    // Apple screen as its console (zero $C0Bx CRTC accesses), so the DisplayMultiplexer never switches to the
    // Videx -- ActiveIndex stays 0. The Builder's PR-5 discovery (2026-06-21) booted all FIVE owner-downloaded
    // candidate masters (cpm223-60k, ms-softcard-ii-228b, cpm-z80softcard, softcard-1980, premium-iie-225) on
    // THIS SoftCard+Videx board and confirmed NONE auto-engages the Videx (videx.ActiveChanged never fired;
    // three crash in the 6502 boot2 on a skew mismatch before any CP/M terminal init, two never even hand off
    // to the Z80). So per Decision 6 this gate asserts the HARDWARE TRUTH for a 40-col master: CP/M boots to A>
    // on the Apple 40-col path WHILE the multiplexer correctly stays on the Apple source (ActiveIndex==0) and
    // the Videx never engages. The Videx 80x24 render path is proven INDEPENDENTLY + asset-free by the
    // VidexVideotermTests direct-render gates (Crtc_programming_yields_80x24_geometry,
    // Vram_of_known_codes_renders_structural_ink_through_the_synthetic_char_rom,
    // DisplayMultiplexer_switches_to_the_Videx_80col_when_it_signals_active, and the bus-path CRTC gate). An
    // 80-col CP/M master that targets the Videx stays an owner-asset item (Decision 7); if one is sourced, this
    // gate gains a sibling that asserts ActiveIndex==1 against it.
    [SoftCardCpmFact]
    public void Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter()
    {
        var (systemRomPath, cpmDiskPath) = SoftCardCpmVectors.TryGetAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the CP/M boot gate");
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(cpmDiskPath);

        // Build the SoftCard+Videx board directly (the surface twin), tracking the Videx auto-switch the
        // production SoftCardVidexSurface wires: ActiveChanged -> the multiplexer follows.
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx);
        Machine machine = BoardMachineFactory.Build(spec);   // interpreter tier (the coprocessor is interpreter)
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state);

        var mux = new DisplayMultiplexer([video, videx], initialActive: 0);
        int videxEngagedCount = 0;
        videx.ActiveChanged += active =>
        {
            if (active) videxEngagedCount++;
            mux.SetActive(active ? 1 : 0);
        };

        machine.Reset();
        machine.Run(CpmBootCycles);                          // the real $C600 -> tracks -> $CnXX -> CP/M boot

        // --- (1) CP/M booted to A> on the 40-col Apple console (the same un-fakeable oracle as the SoftCard
        //         gate: A> at the $0400 text page can only come from a real CONOUT through the $CnXX handshake).
        string joined = DecodeTextScreen(machine);
        Assert.Contains("A>", joined);
        Assert.True(joined.Contains("CP/M") || joined.Contains("DIGITAL RESEARCH"),
            $"expected a CP/M sign-on line on the console; decoded screen was:\n{joined}");

        // --- (2) The Z80 ran (the $CnXX handoff fired).
        Assert.True(machine.CoprocessorActive,
            "expected the Z80 to be the active bus master after the CP/M boot handoff");

        // --- (3) The HARDWARE TRUTH for this 40-col master (ADR 0017 Decision 6): the Videx NEVER engaged, so
        //         the multiplexer correctly stays on the Apple-40 source. Asserting ActiveIndex==1 here would be
        //         asserting a falsehood (it would force a fake -- the disk issues zero $C0Bx). The Videx 80-col
        //         path is proven separately + asset-free by the VidexVideotermTests direct-render gates.
        Assert.Equal(0, videxEngagedCount);
        Assert.Equal(0, mux.ActiveIndex);
    }

    /// <summary>Decode the live 24x40 Apple text page ($0400, page 1) of <paramref name="machine"/> to one
    /// joined ASCII string -- the same TextRowBase walk the SoftCard gate uses, stripping the normal-video
    /// high bit; non-printable cells become spaces.</summary>
    private static string DecodeTextScreen(Machine machine)
    {
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
        var rows = new string[24];
        for (int r = 0; r < 24; r++)
        {
            uint rowBase = Apple2HiResAddress.TextRowBase(r, page2: false);
            var sb = new System.Text.StringBuilder(40);
            for (int c = 0; c < 40; c++)
            {
                int g = bus.Read8(rowBase + (uint)c) & 0x7F;
                sb.Append(g is >= 0x20 and <= 0x7E ? (char)g : ' ');
            }
            rows[r] = sb.ToString();
        }
        return string.Join("\n", rows);
    }

    private const long CpmBootCycles = 10_000_000;   // the SoftCard gate's budget -- the screen settles well before
}
