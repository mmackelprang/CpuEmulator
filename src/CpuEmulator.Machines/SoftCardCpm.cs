using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>Loads the Microsoft Z-80 SoftCard CP/M 2.2 disk image from the asset cache (NOT vendored —
/// fetched on demand by tools/get-softcard-cpm.{sh,ps1} from the Asimov mirror; ADR 0016 Decisions 4/5,
/// owner sign-off GIVEN). The cache root is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors);
/// the image lives at &lt;root&gt;/cpm/softcard-cpm.dsk. A 16-sector Apple CP/M format (research §4): 35
/// tracks x 16 sectors x 256 bytes = 143,360 bytes; first 3 tracks reserved for the CP/M system. The image
/// is wrapped as a read-only 256-byte-sector IBlockDevice, re-nibblized by DskFluxImage with the CP/M
/// data-track skew (SectorOrderKind.Cpm, research §5) onto the unchanged Disk II head.</summary>
public static class SoftCardCpm
{
    public const int DiskLength = 35 * 16 * 256;   // 143,360 bytes (16-sector Apple CP/M, research §4)
    public const int SectorSize = 256;

    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    /// <summary>The cached CP/M .dsk path, or null if absent (the boot-gate skip-with-note trigger). The
    /// optional <paramref name="root"/> is a test seam so a test never mutates the process-wide env var.</summary>
    public static string? TryGetDiskPath(string? root = null)
    {
        string path = Path.Combine(root ?? CacheRoot, "cpm", "softcard-cpm.dsk");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Load + length-validate the CP/M .dsk (from <paramref name="path"/>, or the cache) as a
    /// read-only 256-byte-sector IBlockDevice. Throws if absent or the wrong length.</summary>
    public static IBlockDevice LoadBlockDevice(string? path = null)
    {
        path ??= TryGetDiskPath();
        if (path is null)
            throw new FileNotFoundException(
                "SoftCard CP/M .dsk not found in the asset cache. Run tools/get-softcard-cpm.ps1 (or .sh), "
              + "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).");
        byte[] image = File.ReadAllBytes(path);
        if (image.Length != DiskLength)
            throw new InvalidDataException(
                $"SoftCard CP/M .dsk at {path} must be exactly {DiskLength} bytes "
              + $"(35 tracks x 16 sectors x 256); got {image.Length}.");
        return new DiskImage(image, SectorSize, isReadOnly: true);
    }
}
