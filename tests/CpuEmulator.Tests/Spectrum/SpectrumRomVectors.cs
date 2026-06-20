using Xunit;

namespace CpuEmulator.Tests.Spectrum;

internal static class SpectrumRomVectors
{
    public static string? TryGetRomPath()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "spectrum", "48.rom");
        return File.Exists(path) ? path : null;
    }
}

public sealed class SpectrumRomFactAttribute : FactAttribute
{
    public SpectrumRomFactAttribute()
    {
        if (SpectrumRomVectors.TryGetRomPath() is null)
            Skip = "Spectrum 48K ROM not found — run tools/get-spectrum-rom.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}

public sealed class SpectrumRomTheoryAttribute : TheoryAttribute
{
    public SpectrumRomTheoryAttribute()
    {
        if (SpectrumRomVectors.TryGetRomPath() is null)
            Skip = "Spectrum 48K ROM not found — run tools/get-spectrum-rom.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
