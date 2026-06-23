namespace CpuEmulator.Machines;

/// <summary>Loads a real .woz disk image from the asset cache (NOT vendored — fetched on demand by
/// tools/get-woz-disks.{sh,ps1}, like the Spectrum/CP/M assets). Cache root is $CPUEMULATOR_TESTVECTORS
/// (default ~/.cache/cpuemulator/vectors); .woz images live at &lt;root&gt;/woz/&lt;name&gt;.woz. Callers in
/// tests skip-with-note via WozDiskFactAttribute when absent.</summary>
public static class WozAsset
{
    public const string DefaultName = "demo";   // tools/get-woz-disks fetches <root>/woz/demo.woz

    public static string? TryGetPath(string? name = null, string? root = null)
    {
        root ??= Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "woz", (name ?? DefaultName) + ".woz");
        return File.Exists(path) ? path : null;
    }

    public static byte[] Load(string? path = null)
    {
        path ??= TryGetPath()
            ?? throw new FileNotFoundException(
                "No .woz asset found in the cache. Run tools/get-woz-disks.ps1 (or .sh), or set "
              + "CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).");
        return File.ReadAllBytes(path);
    }
}
