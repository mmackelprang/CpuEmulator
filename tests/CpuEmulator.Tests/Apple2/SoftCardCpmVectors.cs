using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

internal static class SoftCardCpmVectors
{
    /// <summary>Both the Apple system ROM (the boot path is 6502-ROM-driven) AND the CP/M .dsk are needed.
    /// Returns (systemRomPath, cpmDiskPath) when BOTH are present, else null.</summary>
    public static (string systemRom, string cpmDisk)? TryGetAssets()
    {
        string? sys = Apple2RomVectors.TryGetRomPath();
        string? cpm = SoftCardCpm.TryGetDiskPath();
        return sys is not null && cpm is not null ? (sys, cpm) : null;
    }
}

/// <summary>Skip-with-note when the SoftCard CP/M boot assets (system ROM + CP/M .dsk) are absent — the
/// PR-H Apple2RomFact discipline, so asset-free CI stays GREEN (a skipped gate is green). Owner sign-off
/// for the CP/M asset fetch is GIVEN (ADR 0016 Decision 5).</summary>
public sealed class SoftCardCpmFactAttribute : FactAttribute
{
    public SoftCardCpmFactAttribute()
    {
        if (SoftCardCpmVectors.TryGetAssets() is null)
            Skip = "SoftCard CP/M boot assets not found — run tools/get-apple2-roms and " +
                   "tools/get-softcard-cpm (.ps1 or .sh), or set CPUEMULATOR_TESTVECTORS " +
                   "(default ~/.cache/cpuemulator/vectors).";
    }
}

public sealed class SoftCardCpmTheoryAttribute : TheoryAttribute
{
    public SoftCardCpmTheoryAttribute()
    {
        if (SoftCardCpmVectors.TryGetAssets() is null)
            Skip = "SoftCard CP/M boot assets not found — run tools/get-apple2-roms and " +
                   "tools/get-softcard-cpm (.ps1 or .sh), or set CPUEMULATOR_TESTVECTORS " +
                   "(default ~/.cache/cpuemulator/vectors).";
    }
}
