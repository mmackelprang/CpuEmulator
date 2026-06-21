using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

internal static class SpectrumRomVectors
{
    // Delegate to the production resolver so the cache-root + path convention lives in exactly one place
    // (SpectrumRom.TryGetPath gained the optional-root overload in this PR; the no-arg form resolves the
    // canonical <cache>/spectrum/48.rom). Avoids the two copies silently diverging.
    public static string? TryGetRomPath() => SpectrumRom.TryGetPath();
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
