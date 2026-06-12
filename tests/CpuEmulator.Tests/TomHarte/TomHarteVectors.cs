using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Resolves the TomHarte SingleStepTests vector directory and provides the skip-at-discovery
/// attribute for theory tests that require the vectors.
///
/// Vector acquisition: run tools/get-test-vectors.ps1 (or .sh), or set
/// CPUEMULATOR_TESTVECTORS to the clone destination.
/// Default cache directory: ~/.cache/cpuemulator/vectors
/// </summary>
internal static class TomHarteVectors
{
    public static string? TryGetVectorDirectory()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string dir = Path.Combine(root, "6502", "v1");
        return Directory.Exists(dir) ? dir : null;
    }
}

/// <summary>
/// TheoryAttribute subclass that marks the entire theory as skipped at discovery time
/// when the TomHarte vector directory is not present. xUnit 2.9.3 has no Assert.Skip;
/// the skip is recorded as a single skipped entry with an actionable message.
/// </summary>
public sealed class TomHarteTheoryAttribute : TheoryAttribute
{
    public TomHarteTheoryAttribute()
    {
        if (TomHarteVectors.TryGetVectorDirectory() is null)
            Skip = "TomHarte vectors not found — run tools/get-test-vectors.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
