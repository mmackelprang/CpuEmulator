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

/// <summary>Like <see cref="KlausFactAttribute"/> (skips when the Klaus binary is absent) but ALSO env-gates the
/// HEAVY through-JIT functional run behind CPUEMULATOR_KLAUS=full — it is a periodic / pre-arc / pre-merge gate,
/// NOT a per-PR cost (lever 5, mirroring the CPUEMULATOR_ZEX=full precedent). The per-run JIT coverage is carried
/// by the differential fuzzer (DifferentialFuzzTests) + the sampled JIT TomHarte sweeps + the interpreter Klaus
/// pin (KlausFunctionalTests), all of which still run every invocation.</summary>
public sealed class KlausJitFactAttribute : FactAttribute
{
    public KlausJitFactAttribute()
    {
        if (KlausVectors.TryGetBinaryPath() is null)
            Skip = "Klaus functional-test binary not found — run tools/get-klaus.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS.";
        else if (Environment.GetEnvironmentVariable("CPUEMULATOR_KLAUS") != "full")
            Skip = "Klaus-through-JIT is a periodic gate — set CPUEMULATOR_KLAUS=full to run it.";
    }
}
