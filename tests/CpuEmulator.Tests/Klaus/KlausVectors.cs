using Xunit;

namespace CpuEmulator.Tests.Klaus;

internal static class KlausVectors
{
    public static string? TryGetBinaryPath()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "klaus", "6502_functional_test.bin");
        return File.Exists(path) ? path : null;
    }
}

public sealed class KlausFactAttribute : FactAttribute
{
    public KlausFactAttribute()
    {
        if (KlausVectors.TryGetBinaryPath() is null)
            Skip = "Klaus functional-test binary not found — run tools/get-klaus.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
