using CpuEmulator.SpecImporter;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Tests for the hand-authored mnemonic semantics map and its validating loader.
/// </summary>
public class SemanticsMapTests
{
    private static string SemanticsPath => DataPath.Get("mos6502-semantics.json");

    // ─── config fields ───────────────────────────────────────────────────

    [Fact]
    public void Loads_Architecture()
    {
        var map = SemanticsMap.Load(SemanticsPath);
        Assert.Equal("mos6502", map.Architecture);
    }

    [Fact]
    public void Loads_Namespace()
    {
        var map = SemanticsMap.Load(SemanticsPath);
        Assert.Equal("CpuEmulator.Cpus.Mos6502", map.Namespace);
    }

    [Fact]
    public void Loads_SpecClassName()
    {
        var map = SemanticsMap.Load(SemanticsPath);
        Assert.Equal("Mos6502Spec", map.SpecClassName);
    }

    [Fact]
    public void Loads_Six_Registers()
    {
        var map = SemanticsMap.Load(SemanticsPath);
        Assert.Equal(6, map.Registers.Length);
    }

    [Fact]
    public void Registers_Have_Expected_Names()
    {
        var map = SemanticsMap.Load(SemanticsPath);
        var names = map.Registers.Select(r => r.Name).ToArray();
        Assert.Equal(["A", "X", "Y", "S", "P", "PC"], names);
    }

    // ─── mnemonic count ──────────────────────────────────────────────────

    [Fact]
    public void Loads_24_Mnemonics()
    {
        var map = SemanticsMap.Load(SemanticsPath);
        Assert.Equal(24, map.Mnemonics.Count);
    }

    // ─── TXS pin — deliberate absence of SetNZ ───────────────────────────
    // TXS (Transfer X → Stack Pointer, 0x9A) is the ONE register transfer
    // on the 6502 that does NOT affect any flags.  All other transfers call
    // SetNZ on the destination; TXS must not.  This is a documented 6502
    // architectural quirk, not an omission.

    [Fact]
    public void TXS_Has_No_SetNZ()
    {
        var map = SemanticsMap.Load(SemanticsPath);
        var ops = map.Mnemonics["TXS"];
        // Must contain Transfer(Reg.X, Reg.S) …
        Assert.Contains("Transfer", ops);
        // … and must NOT contain SetNZ (the 6502 quirk: TXS sets no flags).
        Assert.DoesNotContain("SetNZ", ops);
    }

    // ─── branch table — all 8 branches correct flag + polarity ──────────

    [Theory]
    [InlineData("BNE", "Flag.Z", "false")]
    [InlineData("BEQ", "Flag.Z", "true")]
    [InlineData("BCC", "Flag.C", "false")]
    [InlineData("BCS", "Flag.C", "true")]
    [InlineData("BPL", "Flag.N", "false")]
    [InlineData("BMI", "Flag.N", "true")]
    [InlineData("BVC", "Flag.V", "false")]
    [InlineData("BVS", "Flag.V", "true")]
    public void Branch_Has_Correct_Flag_And_Polarity(string mnemonic, string flag, string polarity)
    {
        var map = SemanticsMap.Load(SemanticsPath);
        var ops = map.Mnemonics[mnemonic];
        Assert.Contains(flag, ops);
        Assert.Contains(polarity, ops);
        Assert.Contains("BranchIf", ops);
    }

    // ─── ops-text vocabulary validation ─────────────────────────────────

    [Fact]
    public void Rejects_Unknown_Factory_Name()
    {
        // "Explode" is not a known DSL factory
        var json = """
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonics": {
                "LDA": "[Explode(Reg.A)]"
              }
            }
            """;
        Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
    }

    [Fact]
    public void Rejects_Non_Reg_Flag_Bool_Argument()
    {
        // "SomeRandom.X" is not Reg.X, Flag.X, or a bool literal
        var json = """
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonics": {
                "LDA": "[Load(SomeRandom.A)]"
              }
            }
            """;
        Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
    }

    [Fact]
    public void Rejects_Unbracketed_Text()
    {
        // Missing the outer [ ... ] brackets
        var json = """
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonics": {
                "LDA": "Load(Reg.A), SetNZ(Reg.A)"
              }
            }
            """;
        Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
    }

    [Fact]
    public void Accepts_Empty_Bracket_List()
    {
        // NOP has "[]" — valid empty ops list
        var map = SemanticsMap.Load(SemanticsPath);
        var ops = map.Mnemonics["NOP"];
        Assert.Equal("[]", ops);
    }
}
