using System.IO;
using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Byte-equality anchor (M5.4): the committed M8086Spec.cs must be exactly a fresh x86-arm run
/// (line-ending normalized). The 8086 spec is dataset-driven (the x86 opcode dataset + the x86 config),
/// not hand-edited — it SUPERSEDES the M5.1 hand-authored state-only stub with the fuller dataset
/// (registers + flags + a live X86DecodeStructure + the Instructions table). The byte-identity guard
/// pins it the way M68000RegeneratedSpecTests pins the FieldGrammar-arm output. Regenerate via:
///   dotnet run --project tools/CpuEmulator.SpecImporter -- \
///     --x86 tools/CpuEmulator.SpecImporter/data/m8086-opcodes.json \
///     --config tools/CpuEmulator.SpecImporter/data/m8086-x86-config.json \
///     --out src/CpuEmulator.Cpus.M8086/M8086Spec.cs
/// </summary>
public class M8086RegeneratedSpecTests
{
    private const string DatasetRel = "tools/CpuEmulator.SpecImporter/data/m8086-opcodes.json";
    private const string ConfigRel  = "tools/CpuEmulator.SpecImporter/data/m8086-x86-config.json";
    private const string SpecRel    = "src/CpuEmulator.Cpus.M8086/M8086Spec.cs";

    [Fact]
    public void Committed_M8086Spec_is_exactly_the_tool_output()
    {
        string repoRoot = TestRepo.FindRepoRoot();
        var dataset = X86Dataset.Load(Path.Combine(repoRoot, DatasetRel));
        var config  = X86Config.Load(Path.Combine(repoRoot, ConfigRel));

        var (source, report) = SpecImportEngine.RunX86(dataset, config, DatasetRel, ConfigRel, SpecRel);

        string committed = File.ReadAllText(Path.Combine(repoRoot, SpecRel));

        Assert.Equal(source.Replace("\r\n", "\n"), committed.Replace("\r\n", "\n"));

        // Counts pinned — bump deliberately when the dataset grows (the M68000 precedent). M5.5b: the F6/F7
        // unary group (TEST/NOT/NEG/MUL/IMUL/DIV/IDIV) is now RESTORED. Its immediate is split per subfield
        // (only TEST /0,/1 take an immediate; NOT/NEG/MUL/IMUL/DIV/IDIV /2..7 take none) — which the M5.4
        // carrier's per-OPCODE-byte immediate rule could not express without consuming a phantom byte for the
        // non-TEST members. M5.5b adds the ImmediateRegMask carrier extension (a bitmask of ModR/M reg values
        // for which the Immediate rule applies; F6/F7 declare 0b00000011 = 3), so the decode length is correct
        // for every reg and the group is back in the dataset (+2 opcodes 213→215, +16 Insn rows 265→281).
        Assert.Equal(report.Instructions, dataset.Opcodes.Length);
        Assert.True(dataset.Prefixes.Length >= 6, $"expected >=6 prefixes, got {dataset.Prefixes.Length}");
        Assert.Equal(8, report.Prefixes);        // 26/2E/36/3E segment overrides + F0/F1 lock + F2/F3 repeat
        // M5.5d: the direct FAR CALL (9A) + JMP (EA) ptr16:16 forms join the dataset (Immediate: Fixed32 — a
        // 4-byte far pointer = offset16 + segment16, the new immediate rule). +2 opcodes 215→217, +2 Insn rows
        // 281→283. (No group fan-out — both are plain, non-group opcodes.)
        Assert.Equal(217, report.Opcodes);       // unique primary opcodes (M5.5d: +2 for 9A/EA far CALL/JMP)
        Assert.Equal(283, report.Instructions);  // Insn rows (M5.5d: +2 for 9A/EA)

        // M5.5b: the F6/F7 split-immediate group is now PRESENT as two reg-extension groups (each 8 members),
        // every member declaring immediateRegMask: 3 (the per-subfield split immediate the carrier expresses).
        foreach (byte group in new byte[] { 0xF6, 0xF7 })
        {
            var members = dataset.Opcodes.Where(o => o.Opcode == group).ToArray();
            Assert.Equal(8, members.Length);                                       // reg 0..7
            Assert.All(members, o => Assert.True(o.HasModRm && o.RegIsExtension));
            Assert.All(members, o => Assert.Equal(3, o.ImmediateRegMask));         // the split-immediate gate
        }
    }

    [Fact]
    public void The_dataset_declares_the_three_prefix_roles_and_the_group_opcodes()
    {
        string repoRoot = TestRepo.FindRepoRoot();
        var dataset = X86Dataset.Load(Path.Combine(repoRoot, DatasetRel));

        // The three prefix roles (ADR 0006 Decision 1) — every role appears.
        Assert.Contains(dataset.Prefixes, p => p.Role == X86PrefixKind.SegmentOverride && p.Value == 0x26);
        Assert.Contains(dataset.Prefixes, p => p.Role == X86PrefixKind.Lock && p.Value == 0xF0);
        Assert.Contains(dataset.Prefixes, p => p.Role == X86PrefixKind.Repeat && p.Value == 0xF3);

        // The canonical opcode-group primaries (the reg-extends-opcode rows). M5.5b restored F6/F7 (their
        // split immediate is now expressed by the ImmediateRegMask carrier — see the note above).
        foreach (byte group in new byte[] { 0x80, 0x81, 0x83, 0xF6, 0xF7, 0xFE, 0xFF, 0xD0, 0xD1, 0xD2, 0xD3, 0x8F })
            Assert.Contains(dataset.Opcodes, o => o.Opcode == group && o.RegIsExtension);

        // A group's members carry CONSISTENT decode metadata + distinct subfields (the importer enforces this).
        var grp80 = dataset.Opcodes.Where(o => o.Opcode == 0x80).ToArray();
        Assert.Equal(8, grp80.Length);                                  // reg 0..7
        Assert.Equal(8, grp80.Select(o => o.Subfield).Distinct().Count());
        Assert.All(grp80, o => Assert.True(o.HasModRm && o.RegIsExtension));
    }
}
