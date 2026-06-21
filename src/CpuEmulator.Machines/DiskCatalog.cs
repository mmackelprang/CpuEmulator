namespace CpuEmulator.Machines;

/// <summary>One entry in the cached disk library exposed by <c>GET /disks</c> (design D11 / T-C). The
/// <see cref="Id"/> is the opaque key the client echoes back on a library insert; <see cref="Format"/> is
/// one of "dsk"/"po"/"woz"; <see cref="Cpm"/> groups the SoftCard CP/M disk last; <see cref="Supported"/>
/// is false for ".woz" until a thin WozFluxImage parser ships (a separable IFluxImage follow-on — the
/// runtime <see cref="CpuEmulator.Surface.Web"/> DiskImageFactory.FromBytes throws NotSupportedException for
/// raw .woz bytes today). The UI lists .woz disabled-with-note; it never inserts one.</summary>
public sealed record DiskCatalogEntry(string Id, string Name, string Format, bool Cpm, bool Supported);

/// <summary>Lists the cached disk-library images for the surface's per-drive [ Library ▾] select (design
/// D11 / T-C). The cache root mirrors <see cref="Apple2Rom"/>/<see cref="SoftCardCpm"/>
/// ($CPUEMULATOR_TESTVECTORS, default ~/.cache/cpuemulator/vectors). Library images live under
/// &lt;root&gt;/disks/ (*.dsk, *.po, *.woz); the SoftCard CP/M boot disk is the already-cached
/// &lt;root&gt;/cpm/softcard-cpm.dsk, listed last + flagged. Pure file-system read — no ASP.NET dependency,
/// so it tests headless; the optional <paramref name="root"/> is the same test seam Apple2Rom uses.</summary>
public static class DiskCatalog
{
    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    private static readonly string[] LibraryExtensions = { ".dsk", ".po", ".woz" };

    /// <summary>The cached library, sorted by name with the CP/M disk grouped last. Absent dir -> empty.</summary>
    public static IReadOnlyList<DiskCatalogEntry> List(string? root = null)
    {
        string baseRoot = root ?? CacheRoot;
        var entries = new List<DiskCatalogEntry>();

        string disksDir = Path.Combine(baseRoot, "disks");
        if (Directory.Exists(disksDir))
        {
            foreach (string file in Directory.EnumerateFiles(disksDir)
                                             .Where(f => LibraryExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();   // ".dsk"/".po"/".woz"
                string format = ext.TrimStart('.');                        // "dsk"/"po"/"woz"
                entries.Add(new DiskCatalogEntry(
                    Id: "lib/" + Path.GetFileName(file),
                    Name: Path.GetFileNameWithoutExtension(file),
                    Format: format,
                    Cpm: false,
                    Supported: format != "woz"));
            }
        }

        // The SoftCard CP/M boot disk (already cached under <root>/cpm/) — grouped last, flagged CP/M.
        string? cpm = SoftCardCpm.TryGetDiskPath(baseRoot);
        if (cpm is not null)
            entries.Add(new DiskCatalogEntry(
                Id: "cpm",
                Name: "SoftCard CP/M 2.2",
                Format: "dsk",
                Cpm: true,
                Supported: true));

        return entries;
    }

    /// <summary>Map a catalog id back to its absolute path + format for the server-side insert. False if
    /// the id is unknown or its file has been removed from the cache since the catalog was listed.</summary>
    public static bool TryResolve(string id, out string path, out string format, string? root = null)
    {
        path = string.Empty;
        format = string.Empty;
        if (string.IsNullOrEmpty(id))
            return false;
        string baseRoot = root ?? CacheRoot;

        if (id == "cpm")
        {
            string? cpm = SoftCardCpm.TryGetDiskPath(baseRoot);
            if (cpm is null) return false;
            path = cpm; format = "dsk"; return true;
        }

        const string prefix = "lib/";
        if (!id.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        string fileName = id[prefix.Length..];
        // Guard against path traversal: the id carries a bare file name only.
        if (fileName != Path.GetFileName(fileName))
            return false;
        string candidate = Path.Combine(baseRoot, "disks", fileName);
        if (!File.Exists(candidate))
            return false;
        path = candidate;
        format = Path.GetExtension(candidate).TrimStart('.').ToLowerInvariant();
        return true;
    }
}
