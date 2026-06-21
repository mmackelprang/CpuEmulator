using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumRomVariantsTests
{
    [Fact]
    public void Discover_only_returns_present_16384_byte_roms_deterministically_ordered()
    {
        string root = Path.Combine(Path.GetTempPath(), "spec-variants-" + Guid.NewGuid().ToString("N"));
        try
        {
            string vdir = Path.Combine(root, "spectrum", "variants");
            Directory.CreateDirectory(vdir);
            File.WriteAllBytes(Path.Combine(vdir, "spec48-arabic-v1.rom"), new byte[SpectrumRom.RomLength]);
            File.WriteAllBytes(Path.Combine(vdir, "spec48.rom"),           new byte[SpectrumRom.RomLength]);
            File.WriteAllBytes(Path.Combine(vdir, "too-short.rom"),        new byte[100]);  // rejected: wrong len
            File.WriteAllBytes(Path.Combine(vdir, "notarom.bin"),          new byte[SpectrumRom.RomLength]); // not *.rom

            var found = SpectrumRomVariants.Discover(root);

            Assert.Equal(new[] { "spec48", "spec48-arabic-v1" }, found.Select(v => v.Name).ToArray()); // ordinal sort
            Assert.All(found, v => Assert.Equal(SpectrumRom.RomLength, new FileInfo(v.Path).Length));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_folds_in_the_canonical_48rom_under_the_spec48_name()
    {
        string root = Path.Combine(Path.GetTempPath(), "spec-canon-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sdir = Path.Combine(root, "spectrum");
            Directory.CreateDirectory(sdir);
            File.WriteAllBytes(Path.Combine(sdir, "48.rom"), new byte[SpectrumRom.RomLength]); // canonical only

            var found = SpectrumRomVariants.Discover(root);

            var spec48 = Assert.Single(found, v => v.Name == "spec48");
            Assert.EndsWith("48.rom", spec48.Path.Replace('\\', '/'));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_is_empty_when_nothing_is_cached()
    {
        string root = Path.Combine(Path.GetTempPath(), "spec-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Assert.Empty(SpectrumRomVariants.Discover(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
