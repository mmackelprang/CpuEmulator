using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Resolves the SingleStepTests 680x0 vector directory (&lt;cache&gt;/680x0/v1) plus the skip-at-discovery
/// attribute, mirroring <see cref="Z80TomHarteVectors"/>. The 680x0 set is mnemonic+size-keyed gzip
/// (*.json.gz) — fetch with tools/get-test-vectors-68000.ps1, or set CPUEMULATOR_TESTVECTORS.
/// </summary>
internal static class M68000TomHarteVectors
{
    public static string? TryGetVectorDirectory()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string dir = Path.Combine(root, "680x0", "v1");
        return Directory.Exists(dir) ? dir : null;
    }
}

/// <summary>TheoryAttribute that skips the whole theory at discovery when the 680x0 vectors are absent —
/// the same skip-when-absent discipline as the 6502/Z80 harness (and Klaus).</summary>
public sealed class M68000TomHarteTheoryAttribute : TheoryAttribute
{
    public M68000TomHarteTheoryAttribute()
    {
        if (M68000TomHarteVectors.TryGetVectorDirectory() is null)
            Skip = "680x0 TomHarte vectors not found — run tools/get-test-vectors-68000.ps1, " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
