using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

internal static class PascalVectors
{
    /// <summary>The Apple Pascal boot gate needs the Apple system ROM (the boot path is 6502-ROM-driven), the
    /// slot-6 Disk II boot ROM (the cold Autostart entry), the BOOT volume (APPLE1, drive 1 -- carries
    /// SYSTEM.APPLE + SYSTEM.PASCAL) AND the program volume (APPLE0, drive 2 -- the compiler/editor set).
    /// Returns (systemRomPath, bootDiskPath, programDiskPath) when ALL are present, else null.</summary>
    public static (string systemRom, string bootDisk, string programDisk)? TryGetAssets()
    {
        string? sys = Apple2RomVectors.TryGetRomPath();
        string? boot = Pascal.TryGetBootDiskPath();
        string? prog = Pascal.TryGetProgramDiskPath();
        bool diskRom = CpuEmulator.Machines.Apple2Rom.TryGetDiskRomPath() is not null;
        return sys is not null && boot is not null && prog is not null && diskRom
            ? (sys, boot, prog)
            : null;
    }
}

/// <summary>Skip-with-note when the Apple Pascal boot assets are absent -- the Apl2Cpm3Fact / SoftCardCpmFact
/// discipline, so asset-free CI stays GREEN (a skipped gate is green). The Pascal `.dsk` images are
/// owner-supplied + staged on demand by tools/get-apple-pascal (never vendored).</summary>
public sealed class PascalBootFactAttribute : FactAttribute
{
    public PascalBootFactAttribute()
    {
        if (PascalVectors.TryGetAssets() is null)
            Skip = "Apple Pascal boot assets not found -- run tools/get-apple2-roms and tools/get-apple-pascal "
                 + "(.ps1 or .sh), or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors). The "
                 + "gate needs the Apple system ROM, the slot-6 disk2.rom, APPLE1.dsk (boot) and APPLE0.dsk.";
    }
}
