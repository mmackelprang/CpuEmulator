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
}
