using System.IO;
using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

/// <summary>M5.4 — the x86 importer-arm dataset parser + emitter unit tests (the load-time validation
/// mirrors the generator's CPUGEN016 cross-checks so the importer fails loudly rather than emitting a spec
/// the generator rejects), plus a SECOND-REFERENCE cross-source diff: the committed dataset's decode
/// metadata is diffed against an INDEPENDENTLY-encoded reference table of well-known 8086 opcode facts
/// (ADR 0001 Decision 6 — extraction-as-acceptance-test; a disagreement is a dataset bug, surfaced here).</summary>
public class X86DatasetTests
{
    private const string DatasetRel = "tools/CpuEmulator.SpecImporter/data/m8086-opcodes.json";

    // ── parser happy path ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Parses_prefixes_and_opcodes_with_metadata()
    {
        const string json = """
            { "prefixes": [ { "value": "0xF3", "role": "Repeat" } ],
              "opcodes":  [ { "opcode": "0x88", "mnemonic": "MOV", "hasModRm": true },
                            { "opcode": "0x80", "mnemonic": "ADD", "subfield": 0, "hasModRm": true,
                              "regIsExtension": true, "wBit": 0, "immediate": "WBit" } ] }
            """;
        var ds = X86Dataset.Parse(json);
        Assert.Single(ds.Prefixes);
        Assert.Equal(X86PrefixKind.Repeat, ds.Prefixes[0].Role);
        Assert.Equal(2, ds.Opcodes.Length);
        Assert.True(ds.Opcodes[0].HasModRm);
        Assert.True(ds.Opcodes[1].RegIsExtension);
        Assert.Equal(0, ds.Opcodes[1].Subfield);
        Assert.Equal(X86ImmediateKind.WBit, ds.Opcodes[1].Immediate);
    }

    // ── the validation rules (mirroring CPUGEN016) ───────────────────────────────────────────────────
    [Theory]
    [InlineData(
        """{ "prefixes": [{ "value": "0xF3", "role": "Repeat" }], "opcodes": [{ "opcode": "0x80", "mnemonic": "ADD", "subfield": 0, "regIsExtension": true }] }""",
        "RegIsExtension requires HasModRm")]
    [InlineData(
        """{ "prefixes": [{ "value": "0xF3", "role": "Repeat" }], "opcodes": [{ "opcode": "0x90", "mnemonic": "NOP", "immediate": "WBit" }] }""",
        "Immediate.WBit needs a WBit position")]
    [InlineData(
        """{ "prefixes": [{ "value": "0xF3", "role": "Repeat" }], "opcodes": [{ "opcode": "0x88", "mnemonic": "MOV", "hasModRm": true, "subfield": 2 }] }""",
        "'subfield' is only valid on a RegIsExtension")]
    [InlineData(
        """{ "prefixes": [{ "value": "0xF3", "role": "Repeat" }], "opcodes": [{ "opcode": "0x88", "mnemonic": "MOV", "hasModRm": true }, { "opcode": "0x88", "mnemonic": "MOV", "hasModRm": true }] }""",
        "Duplicate (opcode 0x88, subfield -1)")]
    [InlineData(
        """{ "prefixes": [{ "value": "0xF3", "role": "Spurious" }], "opcodes": [{ "opcode": "0x90", "mnemonic": "NOP" }] }""",
        "Unknown prefix role")]
    [InlineData(
        """{ "prefixes": [{ "value": "0xF3", "role": "Repeat" }], "opcodes": [{ "opcode": "0x80", "mnemonic": "ADD", "subfield": 0, "hasModRm": true, "regIsExtension": true, "wBit": 0, "immediate": "WBit" }, { "opcode": "0x80", "mnemonic": "OR", "subfield": 1, "hasModRm": true, "regIsExtension": true, "wBit": 0, "immediate": "Fixed8" }] }""",
        "INCONSISTENT decode metadata")]
    public void Rejects_incoherent_rows(string json, string expectedFragment)
    {
        var ex = Assert.Throws<InvalidDataException>(() => X86Dataset.Parse(json));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Empty_dataset_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => X86Dataset.Parse("""{ "prefixes": [], "opcodes": [] }"""));
        Assert.Throws<InvalidDataException>(() => X86Dataset.Parse("""{ "opcodes": [{ "opcode":"0x90","mnemonic":"NOP" }] }"""));
    }

    [Fact]
    public void Rejects_mixing_plain_and_group_rows_for_one_opcode()
    {
        // 0x80 declared once as a plain row and once as a group row is incoherent (the X86Opcode is declared
        // once; the generator's CPUGEN016 cannot both require a non-group AND a group Insn row for the byte).
        const string json = """{ "prefixes": [{ "value": "0xF3", "role": "Repeat" }], "opcodes": [{ "opcode": "0x80", "mnemonic": "ADD", "hasModRm": true }, { "opcode": "0x80", "mnemonic": "OR", "subfield": 1, "hasModRm": true, "regIsExtension": true }] }""";
        var ex = Assert.Throws<InvalidDataException>(() => X86Dataset.Parse(json));
        Assert.Contains("mixes plain and group", ex.Message);
    }

    // ── the x86 config flag-layout validation ──────────────────────────────────────────────────────────
    [Theory]
    [InlineData(16, "out of range")]   // bit 16 exceeds the 16-bit FLAGS register
    [InlineData(-1, "out of range")]
    public void Config_rejects_out_of_range_flag_bits(int bit, string fragment)
    {
        string json = $$"""
            { "architecture": "x", "namespace": "N", "specClassName": "S",
              "registers": [ { "name": "IP", "bits": 16, "role": "ProgramCounter" } ],
              "flags": [ { "name": "C", "bit": {{bit}} } ] }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => X86Config.Parse(json));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Config_rejects_duplicate_flag_bits()
    {
        const string json = """
            { "architecture": "x", "namespace": "N", "specClassName": "S",
              "registers": [ { "name": "IP", "bits": 16, "role": "ProgramCounter" } ],
              "flags": [ { "name": "C", "bit": 0 }, { "name": "P", "bit": 0 } ] }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => X86Config.Parse(json));
        Assert.Contains("duplicate flag bit", ex.Message);
    }

    // ── enum-drift guards: the importer's mirror enums must stay in step with the Core carrier enums ─────
    [Fact]
    public void X86PrefixKind_mirrors_the_core_X86PrefixRole()
    {
        Assert.Equal(
            Enum.GetNames<CpuEmulator.Core.Specification.X86PrefixRole>().OrderBy(n => n),
            Enum.GetNames<X86PrefixKind>().OrderBy(n => n));
    }

    [Fact]
    public void X86ImmediateKind_mirrors_the_core_X86ImmediateRule()
    {
        Assert.Equal(
            Enum.GetNames<CpuEmulator.Core.Specification.X86ImmediateRule>().OrderBy(n => n),
            Enum.GetNames<X86ImmediateKind>().OrderBy(n => n));
    }

    // ── the emitter shape ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Emitter_writes_a_unique_X86Opcode_per_primary_and_an_Insn_per_row()
    {
        const string json = """
            { "prefixes": [ { "value": "0xF3", "role": "Repeat" } ],
              "opcodes":  [ { "opcode": "0x80", "mnemonic": "ADD", "subfield": 0, "hasModRm": true, "regIsExtension": true, "wBit": 0, "immediate": "WBit" },
                            { "opcode": "0x80", "mnemonic": "SUB", "subfield": 5, "hasModRm": true, "regIsExtension": true, "wBit": 0, "immediate": "WBit" },
                            { "opcode": "0x90", "mnemonic": "NOP" } ] }
            """;
        var ds = X86Dataset.Parse(json);
        var cfg = new X86Config
        {
            Architecture = "m8086demo", Namespace = "Demo", SpecClassName = "DemoSpec",
            Registers = [new RegisterConfig("IP", 16, "ProgramCounter")],
        };
        var (source, report) = X86Emitter.Emit(ds, cfg);

        // One X86Opcode per UNIQUE primary (0x80 declared once though it backs two group rows).
        Assert.Equal(2, report.Opcodes);
        Assert.Equal(3, report.Instructions);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"new X86Opcode\(0x80,"));
        // The group rows emit Insn(0xNN, subfield: N, ...); the plain row emits Insn(0xNN, "...", ...).
        Assert.Contains("Insn(0x80, subfield: 0, \"ADD\", AddrMode.Implied, [])", source);
        Assert.Contains("Insn(0x80, subfield: 5, \"SUB\", AddrMode.Implied, [])", source);
        Assert.Contains("Insn(0x90, \"NOP\", AddrMode.Implied, [])", source);
    }

    // ── the SECOND-REFERENCE cross-source diff (the dataset's correctness burden) ──────────────────────
    /// <summary>An independently-encoded reference table of well-known 8086 opcode facts (a SECOND source
    /// vs the committed dataset): (opcode, hasModRm, regIsExtension, immediate-rule). Hand-checked against
    /// the Intel 8086 Family User's Manual encoding tables. A disagreement with the committed dataset is a
    /// dataset bug — surfaced as a test failure (the cross-source diff, ADR 0001 Decision 6).</summary>
    public static IEnumerable<object[]> SecondReference()
    {
        // opcode, hasModRm, regIsExtension, immediate
        yield return [(byte)0x88, true,  false, X86ImmediateKind.None];    // MOV r/m8,r8
        yield return [(byte)0x8B, true,  false, X86ImmediateKind.None];    // MOV r16,r/m16
        yield return [(byte)0x04, false, false, X86ImmediateKind.WBit];    // ADD AL,imm8 (w drives 1 byte)
        yield return [(byte)0x05, false, false, X86ImmediateKind.WBit];    // ADD AX,imm16
        yield return [(byte)0xB0, false, false, X86ImmediateKind.WBit];    // MOV AL,imm8
        yield return [(byte)0xB8, false, false, X86ImmediateKind.WBit];    // MOV AX,imm16
        yield return [(byte)0x80, true,  true,  X86ImmediateKind.WBit];    // ALU r/m8,imm8 group
        yield return [(byte)0x81, true,  true,  X86ImmediateKind.WBit];    // ALU r/m16,imm16 group
        yield return [(byte)0x83, true,  true,  X86ImmediateKind.SWBit];   // ALU r/m16,imm8 sign-ext group
        // (F6/F7 unary group deferred to M5.5b — split immediate the per-opcode carrier can't express.)
        yield return [(byte)0xFE, true,  true,  X86ImmediateKind.None];    // INC/DEC r/m8 group
        yield return [(byte)0xFF, true,  true,  X86ImmediateKind.None];    // INC/DEC/CALL/JMP/PUSH group
        yield return [(byte)0xD0, true,  true,  X86ImmediateKind.None];    // shift/rotate r/m8,1 group
        yield return [(byte)0xD3, true,  true,  X86ImmediateKind.None];    // shift/rotate r/m16,CL group
        yield return [(byte)0xCD, false, false, X86ImmediateKind.Fixed8];  // INT imm8
        yield return [(byte)0xE9, false, false, X86ImmediateKind.Fixed16]; // JMP rel16
        yield return [(byte)0xEB, false, false, X86ImmediateKind.Fixed8];  // JMP rel8 short
        yield return [(byte)0x90, false, false, X86ImmediateKind.None];    // NOP
        yield return [(byte)0xA4, false, false, X86ImmediateKind.None];    // MOVSB
        yield return [(byte)0xE4, false, false, X86ImmediateKind.Fixed8];  // IN AL,imm8
    }

    [Theory]
    [MemberData(nameof(SecondReference))]
    public void Committed_dataset_agrees_with_the_second_reference(
        byte opcode, bool hasModRm, bool regIsExtension, X86ImmediateKind immediate)
    {
        string repoRoot = TestRepo.FindRepoRoot();
        var ds = X86Dataset.Load(Path.Combine(repoRoot, DatasetRel));
        var row = Assert.Single(ds.Opcodes, o => o.Opcode == opcode && o.Subfield == (regIsExtension ? 0 : -1));
        Assert.Equal(hasModRm, row.HasModRm);
        Assert.Equal(regIsExtension, row.RegIsExtension);
        Assert.Equal(immediate, row.Immediate);
    }
}
