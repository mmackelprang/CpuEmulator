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

    /// <summary>The Videx 80-col CP/M-3 gate ALSO needs the REAL Videx firmware ROM + char ROM: the apl2cpm3
    /// CRT80 build's console primitives (?icrt / ?odcrt) JMP INTO the $C800 firmware window, so the synthetic
    /// all-zero firmware paints NOTHING -- the genuine CP/M-3 sign-on only renders on the Videx VRAM when the
    /// real firmware programs the CRTC and drives ?odcrt (ADR 0018 §1.3 / V80-3). So the firmware ROM is
    /// load-bearing for the Videx console and the gate skips cleanly without it. Returns the boot assets PLUS
    /// the real firmware + char ROM bytes when ALL are present, else null.</summary>
    public static (string systemRom, string disk1, byte[] videxFirmware, byte[] videxCharRom)? TryGetVidexAssets()
    {
        if (TryGetAssets() is not { } a) return null;
        byte[]? fw = VidexRom.TryLoadFirmware();
        byte[]? ch = VidexRom.TryLoadCharRom();
        return fw is not null && ch is not null ? (a.systemRom, a.disk1, fw, ch) : null;
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

/// <summary>Skip-with-note when the apl2cpm3 boot assets OR the REAL Videx firmware/char ROM are absent. The
/// Videx 80-col CP/M-3 console is rendered by the real $C800 firmware (the synthetic image is empty), so the
/// firmware ROM is load-bearing for THIS gate -- it skips cleanly (green) without it, keeping CI safe.</summary>
public sealed class Apl2Cpm3VidexFactAttribute : FactAttribute
{
    public Apl2Cpm3VidexFactAttribute()
    {
        if (Apl2Cpm3Vectors.TryGetVidexAssets() is null)
            Skip = "apl2cpm3 + REAL Videx firmware assets not found -- run tools/get-apple2-roms, "
                 + "tools/get-apl2cpm3, and tools/get-videx-roms (.ps1 or .sh), or set "
                 + "CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors). The real Videx firmware "
                 + "ROM is load-bearing -- the CRT80 console JMPs into the $C800 firmware window.";
    }
}
