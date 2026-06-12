using System.IO;

namespace CpuEmulator.SpecImporter;

/// <summary>
/// Resolves data-file paths relative to the tool's output directory.
/// Tests and the CLI use the same helper; data files are content-copied
/// via the csproj <Content CopyToOutputDirectory="PreserveNewest" /> item,
/// which flows transitively into the test project's output too.
/// Resolution is a single probe of <c>AppContext.BaseDirectory/data/</c> —
/// no ancestor walk (a walk was dead code here and risked silently picking
/// up a stale shadow copy from an unrelated ancestor data/ directory).
/// </summary>
public static class DataPath
{
    /// <summary>Returns the absolute path to a named data file.</summary>
    public static string Get(string filename)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "data", filename);
        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException(
            $"Data file '{filename}' not found at '{candidate}'. " +
            "Ensure the project's <Content CopyToOutputDirectory> is set.", filename);
    }
}
