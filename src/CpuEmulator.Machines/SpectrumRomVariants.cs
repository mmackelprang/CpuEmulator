namespace CpuEmulator.Machines;

/// <summary>Discovers the owner's ZX Spectrum 48K ROM *variants* in the asset cache (NOT vendored — Amstrad's
/// copyright; the owner copies their six 16 KiB ROMs in, exactly as the canonical 48.rom is fetched on demand).
/// Variants live at &lt;cache&gt;/spectrum/variants/&lt;name&gt;.rom; the canonical UK ROM (&lt;cache&gt;/spectrum/48.rom,
/// fetched by tools/get-spectrum-rom) is also surfaced under the variant name "spec48" so a single (variant ×
/// tier) gate can cover it without a duplicate copy. Every returned path is a present, exactly-16384-byte file;
/// callers skip-with-note when the enumeration is empty.</summary>
public static class SpectrumRomVariants
{
    /// <summary>One discovered variant ROM: a stable short <paramref name="Name"/> (the file stem, e.g.
    /// "spec48", "spec48-arabic-v1") and the absolute <paramref name="Path"/> to a 16384-byte image.</summary>
    public readonly record struct Variant(string Name, string Path);

    /// <summary>The cache subdir holding the variant ROMs (&lt;root&gt;/spectrum/variants).</summary>
    public static string VariantsDir(string? root = null)
    {
        root ??= Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "cpuemulator", "vectors");
        return System.IO.Path.Combine(root, "spectrum", "variants");
    }

    /// <summary>Enumerate the present, valid (exactly 16384-byte) variant ROMs, deterministically ordered by
    /// name. Always includes "spec48" when the canonical 48.rom is cached (even if variants/spec48.rom is not),
    /// so the canonical ROM is part of the (variant × tier) sweep. Returns an empty list when nothing is present
    /// (callers skip-with-note).</summary>
    public static IReadOnlyList<Variant> Discover(string? root = null)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);

        string dir = VariantsDir(root);
        if (System.IO.Directory.Exists(dir))
        {
            foreach (string path in System.IO.Directory.EnumerateFiles(dir, "*.rom"))
            {
                if (new System.IO.FileInfo(path).Length != SpectrumRom.RomLength) continue; // 0x4000 only
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                found[name] = path;
            }
        }

        // Fold in the canonical ROM under "spec48" if a variants/spec48.rom was not already found.
        if (!found.ContainsKey("spec48"))
        {
            string? canonical = SpectrumRom.TryGetPath(root);
            if (canonical is not null) found["spec48"] = canonical;
        }

        var list = new List<Variant>(found.Count);
        foreach (var kv in found) list.Add(new Variant(kv.Key, kv.Value));
        return list;
    }
}
