using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

internal static class Apl2Cpm3Vectors
{
    /// <summary>Both the Apple system ROM (the boot path is 6502-ROM-driven) AND apl2cpm3 Disk 1 are needed.
    /// Returns (systemRomPath, disk1Path) when BOTH are present, else null.</summary>
    public static (string systemRom, string disk1)? TryGetAssets()
    {
        string? sys = Apple2RomVectors.TryGetRomPath();
        string? disk1 = Apl2Cpm3.TryGetBootDiskPath();
        return sys is not null && disk1 is not null ? (sys, disk1) : null;
    }
}

/// <summary>Skip-with-note when the apl2cpm3 boot assets (system ROM + apl2cpm3 Disk 1) are absent -- the
/// SoftCardCpmFact discipline, so asset-free CI stays GREEN (a skipped gate is green). Owner sign-off for the
/// apl2cpm3 fetch is COVERED by the existing CP/M-disk sign-off (ADR 0018 Decision 5).</summary>
public sealed class Apl2Cpm3FactAttribute : FactAttribute
{
    public Apl2Cpm3FactAttribute()
    {
        if (Apl2Cpm3Vectors.TryGetAssets() is null)
            Skip = "apl2cpm3 boot assets not found -- run tools/get-apple2-roms and tools/get-apl2cpm3 "
                 + "(.ps1 or .sh), or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
