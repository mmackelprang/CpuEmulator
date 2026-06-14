using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// M3.4e-1a — the importer declares IXh/IXl/IYh/IYl as 8-bit half-views and re-declares IX/IY as
/// computed pair-views over them (the D2 storage inversion, RECON-FINDING A1). The halves must be
/// emitted BEFORE the pair (the H/L-before-HL convention — the pair-view property references the half
/// fields), so the generated Z80Spec.cs compiles. Asserts the emitted RegisterDef shape + ordering
/// straight from the in-process import engine over the committed z80 data files.
/// </summary>
public class Z80IndexRegisterTests
{
    private static string Z80DatasetPath   => DataPath.Get("z80-opcodes.json");
    private static string Z80SemanticsPath => DataPath.Get("z80-semantics.json");

    private static string RegenerateZ80SpecText()
    {
        var dataset = OpcodeDataset.Load(Z80DatasetPath);
        var map     = SemanticsMap.Load(Z80SemanticsPath);
        var (source, _) = SpecImportEngine.Run(
            dataset, map, "z80-opcodes.json", "z80-semantics.json",
            "src/CpuEmulator.Cpus.Z80/Z80Spec.cs");
        return source;
    }

    [Fact]
    public void Ix_iy_emit_as_views_over_8bit_halves_declared_first()
    {
        string spec = RegenerateZ80SpecText();

        // The 8-bit halves are declared (storage); IX/IY are views over them (D2 / A1).
        Assert.Contains("new(\"IXh\", 8)", spec);
        Assert.Contains("new(\"IXl\", 8)", spec);
        Assert.Contains("new(\"IYh\", 8)", spec);
        Assert.Contains("new(\"IYl\", 8)", spec);
        Assert.Contains("new(\"IX\", 16, HighHalf: \"IXh\", LowHalf: \"IXl\")", spec);
        Assert.Contains("new(\"IY\", 16, HighHalf: \"IYh\", LowHalf: \"IYl\")", spec);

        // The bare 16-bit IX/IY declarations are GONE — storage moved to the halves.
        Assert.DoesNotContain("new(\"IX\", 16),", spec);
        Assert.DoesNotContain("new(\"IY\", 16),", spec);
    }

    [Fact]
    public void Each_index_half_is_declared_before_its_pair_view()
    {
        string spec = RegenerateZ80SpecText();

        // Ordering: each half is declared BEFORE the pair view that references it (the emitter emits
        // register declarations in list order; the pair view references the half fields).
        Assert.True(spec.IndexOf("\"IXh\"") < spec.IndexOf("HighHalf: \"IXh\""),
            "IXh must be declared before the IX view that references it");
        Assert.True(spec.IndexOf("\"IYl\"") < spec.IndexOf("LowHalf: \"IYl\""),
            "IYl must be declared before the IY view that references it");
    }
}
