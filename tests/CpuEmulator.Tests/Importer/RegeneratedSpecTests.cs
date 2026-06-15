using System.IO;
using CpuEmulator.SpecImporter;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Byte-equality anchor: the committed Mos6502Spec.cs must be identical (content-wise,
/// normalizing line endings) to a fresh run of the importer tool.
///
/// If this test fails, Mos6502Spec.cs was hand-edited out of sync with the semantics map
/// or dataset, or the importer logic changed without re-running the tool.
///
/// The fix is always to re-run the canonical regeneration command (in the file header):
///   dotnet run --project tools/CpuEmulator.SpecImporter -- \
///     --dataset tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
///     --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json \
///     --out src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs
/// </summary>
public class RegeneratedSpecTests
{
    [Fact]
    public void Committed_Mos6502Spec_is_exactly_the_tool_output()
    {
        // Use the exact paths from the canonical regen command — the header line must match.
        const string datasetRelPath   = "tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json";
        const string semanticsRelPath = "tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json";

        string repoRoot = TestRepo.FindRepoRoot();
        var dataset = OpcodeDataset.Load(Path.Combine(repoRoot, datasetRelPath));
        var map     = SemanticsMap.Load(Path.Combine(repoRoot, semanticsRelPath));

        var (source, report) = SpecImportEngine.Run(dataset, map, datasetRelPath, semanticsRelPath);

        string committed = File.ReadAllText(
            Path.Combine(repoRoot, "src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs"));

        // Normalize line endings: AppendLine emits CRLF on Windows; .gitattributes may pin
        // the checkout to LF.  Content equality is the contract, not EOL bytes.
        Assert.Equal(
            source.Replace("\r\n", "\n"),
            committed.Replace("\r\n", "\n"));

        Assert.Equal(151, report.Emitted);
        Assert.Equal(0,   report.TodoSemantics);  // BRK + RTI now map (3b-ii)
        Assert.Equal(0,   report.TodoMode);       // all 13 modes expressible
    }
}
