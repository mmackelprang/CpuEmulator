namespace CpuEmulator.Machines;

/// <summary>Loads the 16 KB ZX Spectrum 48K ROM image from the asset cache (NOT vendored — Amstrad's
/// copyright; fetched on demand by tools/get-spectrum-rom.{sh,ps1}, exactly like the ZEX/Klaus assets).
/// The cache root is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors); the ROM lives at
/// &lt;root&gt;/spectrum/48.rom. A missing ROM throws a clear, actionable exception (callers in tests
/// skip-with-note instead via SpectrumRomFactAttribute).</summary>
public static class SpectrumRom
{
    public const int RomLength = 0x4000; // 16 KiB

    /// <summary>Resolve the cached ROM path, or null if absent.</summary>
    public static string? TryGetPath()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "spectrum", "48.rom");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Load + validate the 16 KB ROM from <paramref name="path"/> (or the cache when null).</summary>
    public static byte[] Load(string? path = null)
    {
        path ??= TryGetPath()
            ?? throw new FileNotFoundException(
                "Spectrum 48K ROM not found in the asset cache. Run tools/get-spectrum-rom.ps1 (or .sh), "
              + "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).");
        byte[] rom = File.ReadAllBytes(path);
        if (rom.Length != RomLength)
            throw new InvalidDataException(
                $"Spectrum ROM at {path} must be exactly {RomLength} bytes; got {rom.Length}.");
        return rom;
    }
}
