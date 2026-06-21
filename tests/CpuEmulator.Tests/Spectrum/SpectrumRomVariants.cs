using CpuEmulator.Core;
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

/// <summary>xUnit theory-data source for the (variant × tier) boot sweep. Enumerates the present 16384-byte
/// variant ROMs via the production SpectrumRomVariants.Discover, crossed with both execution tiers. Empty when
/// no variant (and no canonical 48.rom) is cached — the [SpectrumRomVariantTheory] attribute then skips-with-note
/// so ROM-free CI stays green (mirrors SpectrumRomTheoryAttribute).</summary>
internal static class SpectrumRomVariantData
{
    public static IReadOnlyList<SpectrumRomVariants.Variant> Present() => SpectrumRomVariants.Discover();

    /// <summary>(name, romPath, tier) rows for [MemberData]. name is the stable variant id used to key the
    /// committed per-variant hash. xUnit evaluates [MemberData] at discovery time and reports a zero-row theory
    /// as a hard failure ("No data found"), so when nothing is cached this yields a single "(none)" sentinel row
    /// (the test body early-returns on it) to keep ROM-free CI green.</summary>
    public static IEnumerable<object[]> VariantTierRows()
    {
        var present = Present();
        if (present.Count == 0)
        {
            yield return new object[] { "(none)", "", ExecutionTier.Interpreter }; // sentinel; body skips
            yield break;
        }
        foreach (var v in present)
        {
            yield return new object[] { v.Name, v.Path, ExecutionTier.Interpreter };
            yield return new object[] { v.Name, v.Path, ExecutionTier.Jit };
        }
    }
}

/// <summary>Skip-with-note when NO Spectrum 48K ROM (canonical or variant) is cached.</summary>
public sealed class SpectrumRomVariantTheoryAttribute : TheoryAttribute
{
    public SpectrumRomVariantTheoryAttribute()
    {
        if (SpectrumRomVariantData.Present().Count == 0)
            Skip = "No Spectrum 48K ROM cached — run tools/get-spectrum-rom.ps1 (canonical) and/or " +
                   "tools/get-spectrum-rom-variants.ps1 (the six variants), or set CPUEMULATOR_TESTVECTORS.";
    }
}
