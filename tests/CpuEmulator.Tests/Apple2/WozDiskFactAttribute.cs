using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

/// <summary>An xUnit [Fact] that skips-with-note when no cached .woz asset is present (xUnit v2.9.3 has no
/// Assert.SkipWhen; the skip is set at attribute construction).</summary>
public sealed class WozDiskFactAttribute : FactAttribute
{
    public WozDiskFactAttribute()
    {
        if (WozAsset.TryGetPath() is null)
            Skip = "No .woz asset cached — run tools/get-woz-disks.ps1 (or .sh), or set "
                 + "CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
