using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

internal static class Apple2RomVectors
{
    public static string? TryGetRomPath()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "apple2", "apple2plus.rom");
        return File.Exists(path) ? path : null;
    }
}

/// <summary>Skip-with-note when the Apple ][+ system ROM is absent (the SpectrumRomFact pattern) so
/// ROM-free CI stays green.</summary>
public sealed class Apple2RomFactAttribute : FactAttribute
{
    public Apple2RomFactAttribute()
    {
        if (Apple2RomVectors.TryGetRomPath() is null)
            Skip = "Apple ][+ system ROM not found — run tools/get-apple2-roms.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}

public sealed class Apple2RomTheoryAttribute : TheoryAttribute
{
    public Apple2RomTheoryAttribute()
    {
        if (Apple2RomVectors.TryGetRomPath() is null)
            Skip = "Apple ][+ system ROM not found — run tools/get-apple2-roms.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}

public class Apple2RomLoaderTests
{
    [Fact]
    public void Load_rejects_a_wrong_length_system_rom()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"apple2-bad-{Guid.NewGuid():N}.rom");
        File.WriteAllBytes(tmp, new byte[0x100]);   // not 12 KiB
        try
        {
            Assert.Throws<InvalidDataException>(() => Apple2Rom.Load(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Load_accepts_an_exact_12KiB_system_rom()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"apple2-ok-{Guid.NewGuid():N}.rom");
        File.WriteAllBytes(tmp, new byte[Apple2Rom.SystemRomLength]);
        try
        {
            byte[] rom = Apple2Rom.Load(tmp);
            Assert.Equal(Apple2Rom.SystemRomLength, rom.Length);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void A_missing_char_rom_is_non_fatal_TryGetCharRomPath_is_null_when_absent()
    {
        // With no char.rom under a throwaway empty cache root, the optional char-ROM path is simply null
        // (the surface uses Apple2Font.Fallback) — NOT an exception. Uses the explicit-root test seam so
        // this never mutates the process-wide CPUEMULATOR_TESTVECTORS (which would race the parallel
        // vector-gated theories — see M68000TomHarteVectors for the same guidance).
        string emptyRoot = Path.Combine(Path.GetTempPath(), $"empty-cache-{Guid.NewGuid():N}");
        Assert.Null(Apple2Rom.TryGetCharRomPath(emptyRoot));
    }
}
