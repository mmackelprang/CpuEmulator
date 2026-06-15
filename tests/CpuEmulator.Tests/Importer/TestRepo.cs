using System.IO;
using Xunit;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Shared test helper: locate the repo root by walking up from the test assembly's base directory
/// until CpuEmulator.slnx is found. Extracted from RegeneratedSpecTests so the FieldGrammar-arm tests
/// (the dataset loader, the CLI test, the regen guard) share the SAME root-finding behavior.
/// </summary>
internal static class TestRepo
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CpuEmulator.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
