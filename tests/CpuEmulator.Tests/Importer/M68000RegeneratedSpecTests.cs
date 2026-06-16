using System.IO;
using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Byte-equality anchor: the committed M68000Spec.cs must be exactly a fresh FieldGrammar-arm run
/// (line-ending normalized). The 68000 spec is dataset-driven (the FieldGrammar dataset + config),
/// not hand-edited. Regenerate via:
///   dotnet run --project tools/CpuEmulator.SpecImporter -- \
///     --field-grammar tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar.json \
///     --config tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar-config.json \
///     --out src/CpuEmulator.Cpus.M68000/M68000Spec.cs
/// </summary>
public class M68000RegeneratedSpecTests
{
    [Fact]
    public void Committed_M68000Spec_is_exactly_the_tool_output()
    {
        const string datasetRel = "tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar.json";
        const string configRel  = "tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar-config.json";

        string repoRoot = TestRepo.FindRepoRoot();
        var families = FieldGrammarDataset.Load(Path.Combine(repoRoot, datasetRel));
        var config   = FieldGrammarConfig.Load(Path.Combine(repoRoot, configRel));

        var (source, report) = SpecImportEngine.RunFieldGrammar(families, config, datasetRel,
            "src/CpuEmulator.Cpus.M68000/M68000Spec.cs");

        string committed = File.ReadAllText(
            Path.Combine(repoRoot, "src/CpuEmulator.Cpus.M68000/M68000Spec.cs"));

        Assert.Equal(source.Replace("\r\n", "\n"), committed.Replace("\r\n", "\n"));
        // Family count pinned (update intentionally when the dataset grows).
        Assert.Equal(report.Families, families.Length);
        Assert.True(families.Length >= 50, $"expected >=50 families, got {families.Length}");
        Assert.Equal(83, families.Length);   // pinned count — bump deliberately when the dataset grows (M4.5c +CMPM)
    }
}
