using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>Loads the apl2cpm3 / CPM3.1_Z80_Softcard disk images (CP/M 3.1 "Plus" for the Microsoft Z-80
/// SoftCard; Bobbi 2019 / Münchheimer 1989) from the asset cache (NOT vendored -- fetched on demand by
/// tools/get-apl2cpm3.{sh,ps1}; ADR 0018 Decision 5, owner sign-off COVERED by the existing CP/M-disk
/// sign-off -- same fetch-on-demand posture as SoftCardCpm). The cache root is $CPUEMULATOR_TESTVECTORS
/// (default ~/.cache/cpuemulator/vectors); the images live at &lt;root&gt;/cpm/apl2cpm3/CPM3.1_Disk_1.dsk
/// .. _7.dsk -- a DISTINCT subdir so the working 2.2 cpm/softcard-cpm.dsk is never clobbered. Each is a
/// 16-sector Apple CP/M image (35 tracks x 16 sectors x 256 = 143,360 bytes), re-nibblized by DskFluxImage
/// with the SectorOrderKind.Cpm3 skew (raw DOS 3.3 on EVERY track -- the apl2cpm3 BOOTLDR/LDRBIOS re-translate,
/// so a raw presentation composes to identity; ADR 0018-A Decision A2). ONLY Disk 1 is bootable + required for
/// the boot gate; Disks 2-7 are optional data/tool/help disks (no boot sector).</summary>
public static class Apl2Cpm3
{
    public const int DiskLength = 35 * 16 * 256;   // 143,360 bytes (16-sector Apple CP/M)
    public const int SectorSize = 256;

    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    private static string DiskFileName(int n) => $"CPM3.1_Disk_{n}.dsk";

    /// <summary>The cached path of apl2cpm3 disk <paramref name="n"/> (1-7), or null if absent. <paramref
    /// name="root"/> is a test seam so a test never mutates the process-wide env var.</summary>
    public static string? TryGetDiskPath(int n, string? root = null)
    {
        if (n is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(n), "apl2cpm3 has disks 1-7.");
        string path = Path.Combine(root ?? CacheRoot, "cpm", "apl2cpm3", DiskFileName(n));
        return File.Exists(path) ? path : null;
    }

    /// <summary>The cached Disk 1 (the only bootable disk -- REQUIRED for the boot gate), or null if absent
    /// (the boot-gate skip-with-note trigger).</summary>
    public static string? TryGetBootDiskPath(string? root = null) => TryGetDiskPath(1, root);

    /// <summary>Load + length-validate apl2cpm3 Disk 1 (from <paramref name="path"/>, or the cache) as a
    /// read-only 256-byte-sector IBlockDevice. Throws if absent or the wrong length.</summary>
    public static IBlockDevice LoadBootDisk(string? path = null)
    {
        path ??= TryGetBootDiskPath();
        if (path is null)
            throw new FileNotFoundException(
                "apl2cpm3 Disk 1 not found in the asset cache. Run tools/get-apl2cpm3.ps1 (or .sh), or set "
              + "CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).");
        byte[] image = File.ReadAllBytes(path);
        if (image.Length != DiskLength)
            throw new InvalidDataException(
                $"apl2cpm3 disk at {path} must be exactly {DiskLength} bytes "
              + $"(35 tracks x 16 sectors x 256); got {image.Length}.");
        return new DiskImage(image, SectorSize, isReadOnly: true);
    }
}
