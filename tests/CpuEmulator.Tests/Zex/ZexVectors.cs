using Xunit;

namespace CpuEmulator.Tests.Zex;

/// <summary>
/// Resolves a fetched ZEX .com binary (&lt;cache&gt;/zex/&lt;name&gt;.com) and provides the skip-at-
/// discovery attribute, mirroring the Klaus harness (KlausVectors). The binaries are fetched (never
/// vendored) by tools/get-zexall.ps1 (or .sh) into the same cache root the TomHarte vectors + the Klaus
/// binary use. ZEXDOC/ZEXALL = Frank D. Cringle's Z80 instruction set exerciser (GPL-2.0).
/// </summary>
internal static class ZexVectors
{
    public static string? TryGetBinaryPath(string name)
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "zex", name);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>FactAttribute that skips at discovery when the named ZEX .com binary is absent — the same
/// skip-when-absent discipline as the Klaus harness (and the Z80 TomHarte vectors). A fetch failure is
/// therefore a SKIP, never a build/test failure (the fetch-resilience requirement, M3.5-2).</summary>
public sealed class ZexFactAttribute : FactAttribute
{
    public ZexFactAttribute(string binary)
    {
        if (ZexVectors.TryGetBinaryPath(binary) is null)
            Skip = $"ZEX binary '{binary}' not found — run tools/get-zexall.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
