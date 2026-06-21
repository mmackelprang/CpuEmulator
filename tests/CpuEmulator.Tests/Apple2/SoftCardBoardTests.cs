using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardBoardTests
{
    [Fact]
    public void Cpm_sector_order_is_the_documented_data_track_skew()
    {
        // research §5: the canonical CP/M data-track skew (apple-do order).
        int[] expected = [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];
        int[] actual = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Cpm_order_is_a_permutation_distinct_from_dos33_and_prodos()
    {
        int[] cpm = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm);
        // A valid interleave is a permutation of 0..15.
        Assert.Equal(Enumerable.Range(0, 16), cpm.OrderBy(x => x));
        // And it is genuinely a third ordering (distinct from the two shipped tables).
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33), cpm);
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.ProDos), cpm);
    }

    [Fact]
    public void Cpm_skew_is_per_track_boot_table_for_system_tracks_data_table_for_the_rest()
    {
        // ADR 0017 Decision 1 (live-verified): system tracks 0-2 use the BOOT interleave (p*11)%16;
        // data tracks 3-34 use the existing CP/M-logical (apple-do) table. A single all-tracks table
        // was the first, fatal defect (boot2's $0F7D loaded as $00/BRK).
        int[] boot = [0, 11, 6, 1, 12, 7, 2, 13, 8, 3, 14, 9, 4, 15, 10, 5];   // (p*11) mod 16
        int[] data = [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];

        // tracks 0, 1, 2 -> boot table
        Assert.Equal(boot, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 0));
        Assert.Equal(boot, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 1));
        Assert.Equal(boot, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 2));
        // track 3+ -> data table
        Assert.Equal(data, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 3));
        Assert.Equal(data, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 34));

        // The boot table is a genuine 0..15 permutation distinct from the data table.
        Assert.Equal(Enumerable.Range(0, 16), boot.OrderBy(x => x));
        Assert.NotEqual(data, boot);
    }

    [Fact]
    public void Single_skew_orders_ignore_the_track_argument_dos33_and_prodos_unchanged()
    {
        // DOS 3.3 / ProDOS are single-skew: the (kind, track) overload returns the same table for every track,
        // byte-for-byte equal to the legacy single-arg call (the regression guard for the additive overload).
        foreach (SectorOrderKind kind in new[] { SectorOrderKind.Dos33, SectorOrderKind.ProDos })
        {
            int[] legacy = Apple2SectorOrder.PhysicalToLogical(kind);
            Assert.Equal(legacy, Apple2SectorOrder.PhysicalToLogical(kind, 0));
            Assert.Equal(legacy, Apple2SectorOrder.PhysicalToLogical(kind, 3));
            Assert.Equal(legacy, Apple2SectorOrder.PhysicalToLogical(kind, 34));
        }
    }

    [Fact]
    public void DskFluxImage_cpm_uses_the_boot_skew_on_track_0_and_the_data_skew_on_track_3()
    {
        // A 35-track .dsk where each 256-byte sector is filled with a byte == its absolute LBA (mod 256).
        // The synthesized track's 6-and-2 data fields therefore encode WHICH logical sector landed at each
        // physical slot; decoding the first data byte of each physical sector recovers physToLog[phys].
        const int tracks = 35, spt = 16;
        var bytes = new byte[tracks * spt * 256];
        for (int lba = 0; lba < tracks * spt; lba++)
            Array.Fill(bytes, (byte)(lba % 256), lba * 256, 256);
        IBlockDevice block = new DiskImage(bytes, 256, isReadOnly: true);

        var flux = new DskFluxImage(block, SectorOrderKind.Cpm);

        // The boot table for track 0 maps physical 1 -> logical 11; the data table maps physical 1 -> logical 6.
        // Decode physical sector 1's first payload byte on track 0 (boot) and track 3 (data) and assert the
        // logical sector each carries.
        Assert.Equal(11, FirstPayloadLogical(flux, track: 0, phys: 1));   // boot table: (1*11)%16 = 11
        Assert.Equal(6,  FirstPayloadLogical(flux, track: 3, phys: 1));   // data table: 6
    }

    // Decode the LBA byte the synthesized data field carries for (track, phys); the test image fills each
    // sector with (track*16 + logical) % 256, so payload % 16 (for track < 16) recovers `logical`.
    private static int FirstPayloadLogical(DskFluxImage flux, int track, int phys)
    {
        byte[] nibbles = flux.TrackBits(track).ToArray();
        int payload = Apple2SectorDecoder.FirstDataByteOfPhysicalSector(nibbles, phys);  // see Task 2c
        return payload % 16;   // track < 16 in this test, so the low nibble is the logical sector
    }

    private static byte[] DiskBootRom()
    {
        var rom = new byte[Apple2Rom.DiskRomLength];   // 256 B
        rom[0x01] = 0x20; rom[0x03] = 0x00; rom[0x05] = 0x03; rom[0x07] = 0x3C;  // slot-6 signature
        rom[0x00] = 0xA9;
        return rom;
    }

    private static Machine BuildSoftCard(byte[] systemRom, IFluxImage? drive1 = null)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(drive1 ?? new SyntheticFluxImage(trackCount: 35));
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, DiskBootRom());
        return BoardMachineFactory.Build(spec);   // interpreter tier; the coprocessor is always interpreter
    }

    [Fact]
    public void The_softcard_board_builds_a_6502_primary_and_a_dormant_Z80_coprocessor()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000
        Machine machine = BuildSoftCard(rom);

        Assert.NotNull(machine.Coprocessor);          // the Z80 coprocessor is wired (PR-I)
        Assert.False(machine.CoprocessorActive);      // the 6502 is the bus master at reset (Z80 dormant)
    }

    [Fact]
    public void The_softcard_board_carries_a_control_port_named_to_match_the_coprocessor_spec()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        BoardSpec spec = SoftCardBoard.Spec(
            rom, new Apple2Iou(new Apple2VideoState(), new Apple2LanguageCard(rom),
                               new Apple2DiskII(new SyntheticFluxImage(trackCount: 35))),
            new Apple2DiskII(new SyntheticFluxImage(trackCount: 35)), DiskBootRom());

        Assert.NotNull(spec.Coprocessor);
        // The control-port slot's Name must equal the CoprocessorSpec.ControlPortPeripheral (PR-I's
        // copro-control-port-unwired validator contract) — the wiring is self-consistent.
        Assert.Equal(spec.Coprocessor!.ControlPortPeripheral,
            spec.Peripherals.Single(p => p.Name == "softcard").Name);
        Assert.Equal(CpuKind.Z80, spec.Coprocessor.Cpu);
    }

    [Fact]
    public void SoftCardCpm_load_rejects_a_wrong_length_image()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"cpm-bad-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(tmp, new byte[1024]);   // not 143,360
        try { Assert.Throws<InvalidDataException>(() => SoftCardCpm.LoadBlockDevice(tmp)); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SoftCardCpm_load_accepts_an_exact_140KiB_image_as_a_256_byte_sector_block_device()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"cpm-ok-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(tmp, new byte[SoftCardCpm.DiskLength]);   // 143,360 = 35*16*256
        try
        {
            IBlockDevice block = SoftCardCpm.LoadBlockDevice(tmp);
            Assert.Equal(256, block.SectorSize);
            Assert.Equal(560, block.SectorCount);   // 35 tracks * 16 sectors
            Assert.True(block.IsReadOnly);
            // And it re-nibblizes onto the shipped DskFluxImage with the CP/M order (the adapter is unchanged).
            var flux = new DskFluxImage(block, SectorOrderKind.Cpm);
            Assert.Equal(35, flux.TrackCount);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SoftCardSurface_constructs_and_renders_a_280x192_frame()
    {
        // A synthetic (all-zero) system ROM + a synthetic CP/M block device: the surface must construct,
        // reset, and produce a 280x192 FB frame (the Apple video tick). No real asset is needed for THIS
        // smoke test — the boot-to-A> assertion is the separate asset-gated test.
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        IBlockDevice cpm = new DiskImage(new byte[SoftCardCpm.DiskLength], 256, isReadOnly: true);
        var bootRom = new byte[Apple2Rom.DiskRomLength];
        bootRom[0x01] = 0x20; bootRom[0x03] = 0x00; bootRom[0x05] = 0x03; bootRom[0x07] = 0x3C;
        bootRom[0x00] = 0xA9;

        byte[]? lastFrame = null;
        CpuEmulator.Surface.Web.SoftCardSurface surface =
            CpuEmulator.Surface.Web.SoftCardSurface.Create(rom, bootRom, charRom: null,
                cpmDisk: cpm, f => lastFrame = f, _ => { });

        surface.Host.RunHeadless(totalCycles: 40_000, sliceCycles: 17_030);

        Assert.NotNull(lastFrame);
        Assert.Equal((byte)'F', lastFrame![0]);
        Assert.Equal((byte)'B', lastFrame[1]);
        int width = lastFrame[4] | (lastFrame[5] << 8);
        int height = lastFrame[6] | (lastFrame[7] << 8);
        Assert.Equal(280, width);
        Assert.Equal(192, height);
        Assert.NotNull(surface.Machine.Coprocessor);   // the Z80 is wired even on the synthetic board
    }

    // Generous budget for the CP/M cold boot: the 6502 reads the 3 system tracks, hands off to the Z80,
    // and CP/M runs to the A> prompt. Tune down on the first green run with the real asset.
    private const long CpmBootCycles = 10_000_000;

    [SoftCardCpmFact]
    public void Cpm_boots_to_the_A_prompt_on_the_interpreter()
    {
        var (systemRomPath, cpmDiskPath) = SoftCardCpmVectors.TryGetAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the CP/M boot gate");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();   // null -> Apple2Font.Fallback (still renders A>)
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(cpmDiskPath);

        // Build the real SoftCard machine with the CP/M .dsk re-nibblized into drive 1.
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
        Machine machine = BoardMachineFactory.Build(spec);   // interpreter tier (coprocessor is interpreter)
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state, charRom);

        machine.Reset();
        machine.Run(CpmBootCycles);                          // the real $C600 -> tracks -> $CnXX -> CP/M boot

        var rgba = new uint[Apple2Video.Width280 * Apple2Video.Height192];
        video.RenderInto(rgba);

        // Un-fakeable structural assertion: CP/M's sign-on + the A> prompt paint ink on a mostly-blank
        // text screen. A dead/garbage boot is all-off (no prompt) or noisy (no clear background).
        int offPixels = 0, onPixels = 0;
        foreach (uint p in rgba)
        {
            if (p == Apple2Palette.MonoOff) offPixels++;
            else if (p == Apple2Palette.MonoOn) onPixels++;
        }
        int total = Apple2Video.Width280 * Apple2Video.Height192;
        Assert.True(offPixels > total / 2,
            $"expected a mostly-blank CP/M text screen; got {offPixels}/{total} off pixels");
        Assert.True(onPixels > 50,
            $"expected the A> prompt + CP/M sign-on ink; got {onPixels} on pixels");
        // The Z80 ran: it became the bus master during the boot (the $CnXX handoff fired).
        Assert.True(machine.CoprocessorActive,
            "expected the Z80 to be the active bus master after the CP/M boot handoff");

        // Tighter gate: a committed RGBA hash. On the FIRST green run with the real asset, capture the
        // hash (uncomment the print), paste it below, then re-run.
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[cpm boot frame hash] {hash}");  // <-- uncomment once to capture
        string ExpectedBootHash = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN";
        if (ExpectedBootHash != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
            Assert.Equal(ExpectedBootHash, hash);
    }

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
