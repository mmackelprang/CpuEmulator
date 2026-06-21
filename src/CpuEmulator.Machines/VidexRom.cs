namespace CpuEmulator.Machines;

/// <summary>Loads the Videx Videoterm ROMs from the asset cache (NOT vendored — fetched on demand by
/// tools/get-videx-roms.{sh,ps1} from the Asimov mirror, asimov.net/emulators/rom_images/videx/; ADR 0016
/// Decision 4). The cache root is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors); the ROMs
/// live at &lt;root&gt;/videx/. Both ROMs are OPTIONAL — the synthetic VidexFont.Fallback (PR-N) covers the
/// char ROM and an all-zero synthetic image covers the firmware window (CP/M's terminal driver writes the
/// 6845 CRTC directly, research §8 — the firmware is fidelity, not required to boot CP/M to A>). The twin
/// of Apple2Rom: a TryGet*Path cache probe + exact-length validation, returning null when absent (the
/// surface falls back to the synthetic assets — never an exception on absence).</summary>
public static class VidexRom
{
    public const int FirmwareLength = 0x0400;   // 1 KiB $C800-$CBFF firmware
    public const int CharLength = 0x0800;       // 2 KiB char ROM (256 glyphs x 8 rows)

    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    private static string? PathIfExists(string name, string? root)
    {
        string path = Path.Combine(root ?? CacheRoot, "videx", name);
        return File.Exists(path) ? path : null;
    }

    public static string? TryGetCharRomPath(string? root = null) => PathIfExists("videx-char.rom", root);
    public static string? TryGetFirmwarePath(string? root = null) => PathIfExists("videx-firmware.rom", root);

    /// <summary>The 2 KiB char ROM, or null when absent (the surface uses VidexFont.Fallback). Throws on a
    /// wrong-length file (a corrupt fetch).</summary>
    public static byte[]? TryLoadCharRom(string? root = null) =>
        TryGetCharRomPath(root) is { } p ? LoadExact(p, CharLength, "Videx char") : null;

    /// <summary>The 1 KiB firmware ROM, or null when absent (the surface uses an all-zero synthetic image).</summary>
    public static byte[]? TryLoadFirmware(string? root = null) =>
        TryGetFirmwarePath(root) is { } p ? LoadExact(p, FirmwareLength, "Videx firmware") : null;

    private static byte[] LoadExact(string path, int length, string what)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length != length)
            throw new InvalidDataException(
                $"{what} ROM at {path} must be exactly {length} bytes; got {bytes.Length}.");
        return bytes;
    }
}
