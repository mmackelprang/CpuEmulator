namespace CpuEmulator.Machines;

/// <summary>Loads the Apple ][+ ROM images from the asset cache (NOT vendored — Apple's copyright;
/// fetched on demand by tools/get-apple2-roms.{sh,ps1}, exactly like the Spectrum/ZEX/Klaus assets, ADR
/// 0014 Decision 7). The cache root is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors);
/// the ROMs live under &lt;root&gt;/apple2/. Three images: the 12 KiB SYSTEM ROM (Applesoft + Monitor,
/// $D000-$FFFF) is REQUIRED to boot a real Apple; the 256 B slot-6 DISK II BOOT ROM ($C600) is needed to
/// boot a disk; the 2 KiB CHAR-GEN ROM is OPTIONAL (Apple2Font.Fallback covers it). A missing system ROM
/// triggers the demo fallback; a missing char ROM is non-fatal.</summary>
public static class Apple2Rom
{
    public const int SystemRomLength = 0x3000;   // 12 KiB $D000-$FFFF (Applesoft + Monitor)
    public const int DiskRomLength = 0x100;      // 256 B slot-6 P5/P6 boot ROM ($C600)
    public const int CharRomLength = 0x800;      // 2 KiB char-gen (256 glyphs x 8 rows)

    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    private static string? PathIfExists(string fileName, string? root = null)
    {
        string path = Path.Combine(root ?? CacheRoot, "apple2", fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The 12 KiB system ROM path, or null if absent (the demo-fallback trigger).</summary>
    public static string? TryGetPath() => PathIfExists("apple2plus.rom");

    /// <summary>The 256 B slot-6 Disk II boot ROM path, or null if absent.</summary>
    public static string? TryGetDiskRomPath() => PathIfExists("disk2.rom");

    /// <summary>The optional 2 KiB char-gen ROM path, or null (non-fatal — Apple2Font.Fallback is used).
    /// The optional <paramref name="root"/> is a test seam (override the cache root) so a test never has
    /// to mutate the process-wide CPUEMULATOR_TESTVECTORS env var; production callers pass nothing.</summary>
    public static string? TryGetCharRomPath(string? root = null) => PathIfExists("char.rom", root);

    /// <summary>Load + validate the 12 KiB system ROM (from <paramref name="path"/>, or the cache).</summary>
    public static byte[] Load(string? path = null) =>
        LoadExact(path ?? TryGetPath(), SystemRomLength, "Apple ][+ system");

    /// <summary>Load + validate the 256 B Disk II boot ROM, or null if absent.</summary>
    public static byte[]? TryLoadDiskRom() =>
        TryGetDiskRomPath() is { } p ? LoadExact(p, DiskRomLength, "Apple ][+ Disk II boot") : null;

    /// <summary>Load + validate the optional 2 KiB char-gen ROM, or null if absent (non-fatal).</summary>
    public static byte[]? TryLoadCharRom() =>
        TryGetCharRomPath() is { } p ? LoadExact(p, CharRomLength, "Apple ][+ char-gen") : null;

    private static byte[] LoadExact(string? path, int length, string which)
    {
        if (path is null)
            throw new FileNotFoundException(
                $"{which} ROM not found in the asset cache. Run tools/get-apple2-roms.ps1 (or .sh), "
              + "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).");
        byte[] rom = File.ReadAllBytes(path);
        if (rom.Length != length)
            throw new InvalidDataException(
                $"{which} ROM at {path} must be exactly {length} bytes; got {rom.Length}.");
        return rom;
    }
}
