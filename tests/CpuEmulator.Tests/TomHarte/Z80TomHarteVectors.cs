using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Resolves the Z80 SingleStepTests vector directory (&lt;cache&gt;/z80/v1) and provides the
/// skip-at-discovery attribute, mirroring the 6502 <see cref="TomHarteVectors"/>. Fetch with
/// tools/get-test-vectors-z80.ps1 (or .sh), or set CPUEMULATOR_TESTVECTORS.
/// </summary>
internal static class Z80TomHarteVectors
{
    public static string? TryGetVectorDirectory()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string dir = Path.Combine(root, "z80", "v1");
        return Directory.Exists(dir) ? dir : null;
    }
}

/// <summary>TheoryAttribute that skips the whole theory at discovery when the Z80 vectors are
/// absent — the same skip-when-absent discipline as the 6502 harness (and Klaus).</summary>
public sealed class Z80TomHarteTheoryAttribute : TheoryAttribute
{
    public Z80TomHarteTheoryAttribute()
    {
        if (Z80TomHarteVectors.TryGetVectorDirectory() is null)
            Skip = "Z80 TomHarte vectors not found — run tools/get-test-vectors-z80.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
