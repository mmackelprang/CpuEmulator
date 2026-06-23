using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>Loads the Apple II Pascal (UCSD p-System II.1) distribution disk images from the asset cache
/// (NOT vendored -- owner-supplied, staged on demand by tools/get-apple-pascal.{sh,ps1} from the owner's
/// local D:/prj/ROMs; same fetch-on-demand-never-commit posture as SoftCardCpm / Apl2Cpm3). The cache root
/// is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors); the images live under
/// &lt;root&gt;/pascal/ as APPLE0.dsk .. APPLE3.dsk.
///
/// SECTOR ORDER (the bring-up's prime suspect, root-caused): these `.dsk` images are in DOS&#160;3.3 logical
/// sector order (the standard `.dsk` convention) but contain a UCSD Pascal (ProDOS-interleave) filesystem.
/// So the CORRECT <see cref="SectorOrderKind"/> for <see cref="DskFluxImage"/> is <see cref="SectorOrderKind.Dos33"/>
/// -- NOT ProDOS: the DOS-3.3-order file de-skewed by the Disk II BIOS's ProDOS/Pascal physical interleave
/// composes to the right Pascal logical layout (cross-checked against dmolony/AppleFileSystem's Pascal
/// interleave table -- it equals the DOS&#160;3.3 table once routed through the ProDOS physical mapping).
/// No new ordering was needed.
///
/// BOOT TOPOLOGY: <see cref="BootDriveDiskName"/> (APPLE1) is the BOOT volume -- it carries SYSTEM.APPLE
/// (the p-machine interpreter) AND SYSTEM.PASCAL (the OS). <see cref="ProgramDriveDiskName"/> (APPLE0) is the
/// program/compiler disk (SYSTEM.COMPILER / EDITOR / FILER). The authentic two-drive distribution boots
/// APPLE1 in drive 1 and APPLE0 in drive 2. The interpreter loader uses the Language Card's "read ROM, write
/// RAM" mode ($C081/$C089) to fill the banked $D000-$FFFF -- see <see cref="Apple2LanguageCard"/>.</summary>
public static class Pascal
{
    public const int DiskLength = 35 * 16 * 256;   // 143,360 bytes (16-sector 5.25" disk)
    public const int SectorSize = 256;

    /// <summary>The boot volume (drive 1): SYSTEM.APPLE (the p-machine interpreter) + SYSTEM.PASCAL.</summary>
    public const string BootDriveDiskName = "APPLE1.dsk";

    /// <summary>The program/compiler volume (drive 2): SYSTEM.COMPILER / EDITOR / FILER / LIBRARY.</summary>
    public const string ProgramDriveDiskName = "APPLE0.dsk";

    /// <summary>The DOS-3.3-order-on-disk, Pascal-filesystem-inside ordering (root-caused above). This is
    /// what <see cref="DskFluxImage"/> must use to re-nibblize an Apple Pascal `.dsk`.</summary>
    public const SectorOrderKind Order = SectorOrderKind.Dos33;

    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    /// <summary>The cached path of a Pascal `.dsk` (e.g. "APPLE1.dsk"), or null if absent. <paramref
    /// name="root"/> is a test seam so a test never mutates the process-wide env var.</summary>
    public static string? TryGetDiskPath(string diskName, string? root = null)
    {
        string path = Path.Combine(root ?? CacheRoot, "pascal", diskName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The cached BOOT volume (APPLE1, drive 1) -- REQUIRED for the boot gate -- or null if absent
    /// (the boot-gate skip-with-note trigger).</summary>
    public static string? TryGetBootDiskPath(string? root = null) => TryGetDiskPath(BootDriveDiskName, root);

    /// <summary>The cached program/compiler volume (APPLE0, drive 2), or null if absent.</summary>
    public static string? TryGetProgramDiskPath(string? root = null) => TryGetDiskPath(ProgramDriveDiskName, root);

    /// <summary>Load + length-validate a Pascal `.dsk` (from <paramref name="path"/>) as a read-only
    /// 256-byte-sector IBlockDevice. Throws if absent or the wrong length.</summary>
    public static IBlockDevice LoadBlockDevice(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] image = File.ReadAllBytes(path);
        if (image.Length != DiskLength)
            throw new InvalidDataException(
                $"Apple Pascal .dsk at {path} must be exactly {DiskLength} bytes "
              + $"(35 tracks x 16 sectors x 256); got {image.Length}.");
        return new DiskImage(image, SectorSize, isReadOnly: true);
    }

    /// <summary>The flux-image wrapper for a Pascal `.dsk` at <paramref name="path"/>, re-nibblized with the
    /// correct <see cref="Order"/> (DOS&#160;3.3 on-disk order) onto the unchanged Disk II head.</summary>
    public static DskFluxImage LoadFluxImage(string path) => new(LoadBlockDevice(path), Order);
}
