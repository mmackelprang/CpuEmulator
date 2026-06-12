using System.IO;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// Resolves data-file paths relative to the tool's output directory.
/// Tests and the CLI use the same helper; data files are content-copied
/// via the csproj <Content CopyToOutputDirectory="PreserveNewest" /> item.
/// </summary>
public static class DataPath
{
    /// <summary>
    /// Returns the absolute path to a named data file by walking from
    /// <see cref="AppContext.BaseDirectory"/> up to the nearest ancestor that
    /// contains a <c>data/</c> sub-directory with the requested file.
    /// Falls back to repo-relative resolution for tests running under
    /// <c>dotnet test</c> whose BaseDirectory is deep in <c>bin/Debug/…</c>.
    /// </summary>
    public static string Get(string filename)
    {
        // Fast path: content-copy puts it right next to the executable.
        var candidate = Path.Combine(AppContext.BaseDirectory, "data", filename);
        if (File.Exists(candidate))
            return candidate;

        // Walk up the tree – covers edge cases such as test-project output layouts.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "data", filename);
            if (File.Exists(probe))
                return probe;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Data file '{filename}' not found relative to '{AppContext.BaseDirectory}'. " +
            "Ensure the project's <Content CopyToOutputDirectory> is set.", filename);
    }
}
