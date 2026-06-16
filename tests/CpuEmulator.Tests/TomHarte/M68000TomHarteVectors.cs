using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Resolves the SingleStepTests 680x0 vector directory (&lt;cache&gt;/680x0/v1) plus the skip-at-discovery
/// attribute, mirroring <see cref="Z80TomHarteVectors"/>. The 680x0 set is mnemonic+size-keyed gzip
/// (*.json.gz) — fetch with tools/get-test-vectors-68000.ps1, or set CPUEMULATOR_TESTVECTORS.
/// </summary>
internal static class M68000TomHarteVectors
{
    /// <summary>The cache root: the CPUEMULATOR_TESTVECTORS override, else ~/.cache/cpuemulator/vectors.</summary>
    private static string CacheRoot() =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");

    public static string? TryGetVectorDirectory() => ResolveVectorDirectory(CacheRoot());

    /// <summary>Resolve &lt;root&gt;/680x0/v1, returning null when absent. Pure (no env read) so tests can
    /// exercise the path logic with an explicit root WITHOUT mutating the process-global
    /// CPUEMULATOR_TESTVECTORS — a mutation would race the vector-gated theories that read it in parallel.</summary>
    public static string? ResolveVectorDirectory(string root)
    {
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

/// <summary>FactAttribute that skips at discovery when the 680x0 vectors are absent — the Fact-shaped twin of
/// <see cref="M68000TomHarteTheoryAttribute"/>, for the per-file split sweep classes (one vector file == one
/// Fact == one xUnit collection, so the files distribute across cores instead of running serially in one
/// theory class). Identical skip logic; zero semantics change.</summary>
public sealed class M68000TomHarteFactAttribute : FactAttribute
{
    public M68000TomHarteFactAttribute()
    {
        if (M68000TomHarteVectors.TryGetVectorDirectory() is null)
            Skip = "680x0 TomHarte vectors not found — run tools/get-test-vectors-68000.ps1, " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
