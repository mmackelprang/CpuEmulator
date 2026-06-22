using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class Apl2Cpm3Tests
{
    [Fact]
    public void Boot_disk_path_is_null_under_an_empty_root_skip_with_note_trigger()
    {
        string emptyRoot = Path.Combine(Path.GetTempPath(), $"empty-apl2cpm3-{Guid.NewGuid():N}");
        Assert.Null(Apl2Cpm3.TryGetBootDiskPath(emptyRoot));
        Assert.Null(Apl2Cpm3.TryGetDiskPath(1, emptyRoot));
    }

    [Fact]
    public void Boot_disk_path_resolves_disk_1_under_the_apl2cpm3_subdir_not_the_2_2_path()
    {
        // The distinct cache subdir cpm/apl2cpm3/ guarantees the working 2.2 cpm/softcard-cpm.dsk is never
        // clobbered (ADR 0018 Decision 5 / the prompt's explicit constraint).
        string root = Path.Combine(Path.GetTempPath(), $"apl2cpm3-{Guid.NewGuid():N}");
        string dir = Path.Combine(root, "cpm", "apl2cpm3");
        Directory.CreateDirectory(dir);
        try
        {
            string disk1 = Path.Combine(dir, "CPM3.1_Disk_1.dsk");
            File.WriteAllBytes(disk1, new byte[Apl2Cpm3.DiskLength]);   // 143,360
            Assert.Equal(disk1, Apl2Cpm3.TryGetBootDiskPath(root));
            Assert.Equal(disk1, Apl2Cpm3.TryGetDiskPath(1, root));
            // The 2.2 disk path is a DIFFERENT subdir -- never touched. Assert directly on the resolved
            // path (a vacuous NotEqual-vs-null would pass even if Apl2Cpm3 resolved to the 2.2 path).
            string? resolved = Apl2Cpm3.TryGetBootDiskPath(root);
            Assert.Contains(Path.Combine("cpm", "apl2cpm3"), resolved!);
            Assert.DoesNotContain("softcard-cpm.dsk", resolved!);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Load_boot_disk_rejects_a_wrong_length_image()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"apl2cpm3-bad-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(tmp, new byte[1024]);   // not 143,360
        try { Assert.Throws<InvalidDataException>(() => Apl2Cpm3.LoadBootDisk(tmp)); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Load_boot_disk_accepts_an_exact_140KiB_image_as_a_256_byte_sector_block_device()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"apl2cpm3-ok-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(tmp, new byte[Apl2Cpm3.DiskLength]);   // 143,360 = 35*16*256
        try
        {
            IBlockDevice block = Apl2Cpm3.LoadBootDisk(tmp);
            Assert.Equal(256, block.SectorSize);
            Assert.Equal(560, block.SectorCount);   // 35 tracks * 16 sectors
            Assert.True(block.IsReadOnly);
            // And it re-nibblizes onto the shipped DskFluxImage with the CP/M order (no new skew -- Decision 2).
            var flux = new DskFluxImage(block, SectorOrderKind.Cpm);
            Assert.Equal(35, flux.TrackCount);
        }
        finally { File.Delete(tmp); }
    }
}
