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
    public void Loads_56_Mnemonics()
    {
        // 24 original + 30 (ALU 9, RMW 7+DEX/DEY, stack 4, flag 7, flow 2)
        // + 2 (BRK, RTI; landed in 3b-ii) = 56 total.
        var map = SemanticsMap.Load(SemanticsPath);
        Assert.Equal(56, map.Mnemonics.Count);
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
        // Must contain Transfer("X", "S") …
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
    public void Ops_text_accepts_a_quoted_register_name()
    {
        // M3.1a: the register-arg form is a double-quoted register-name string.
        var json = """
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonics": {
                "LDA": "[Load(\"A\"), SetNZ(\"A\")]"
              }
            }
            """;
        var map = SemanticsMap.Parse(json);   // no throw
        Assert.Equal("[Load(\"A\"), SetNZ(\"A\")]", map.Mnemonics["LDA"]);
    }

    [Fact]
    public void Ops_text_rejects_a_bare_unquoted_register_token()
    {
        // A bare unquoted register token (the OLD-ish form without quotes) is no longer a valid
        // register argument — it must be a quoted string (mirrors the parser's CPUGEN011).
        var json = """
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonics": {
                "LDA": "[Load(A)]"
              }
            }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
        Assert.Contains("invalid argument", ex.Message);
    }

    [Fact]
    public void Ops_text_still_accepts_Flag_and_bool()
    {
        // Flag args + bool literals are out of M3.1a's register-only scope — unchanged.
        var json = """
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonics": {
                "BNE": "[BranchIf(Flag.Z, false)]"
              }
            }
            """;
        var map = SemanticsMap.Parse(json);   // no throw
        Assert.Equal("[BranchIf(Flag.Z, false)]", map.Mnemonics["BNE"]);
    }

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
                "LDA": "[Explode(\"A\")]"
              }
            }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
        Assert.Contains("unknown factory", ex.Message);
    }

    [Fact]
    public void Rejects_Non_Reg_Flag_Bool_Argument()
    {
        // "SomeRandom.A" is not a quoted register name, Flag.X, or a bool literal
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
        var ex = Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
        Assert.Contains("invalid argument", ex.Message);
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
                "LDA": "Load(\"A\"), SetNZ(\"A\")"
              }
            }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
        Assert.Contains("bracketed", ex.Message);
    }

    [Fact]
    public void Accepts_Empty_Bracket_List()
    {
        // NOP has "[]" — valid empty ops list
        var map = SemanticsMap.Load(SemanticsPath);
        var ops = map.Mnemonics["NOP"];
        Assert.Equal("[]", ops);
    }

    // ─── loader strictness (quality-review items 1–4) ────────────────────

    [Fact]
    public void Rejects_Empty_Config()
    {
        // "{}" deserializes but every config field is empty — Task 4 would
        // emit "namespace ;"-grade garbage far from the cause. Fail at load.
        var ex = Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse("{}"));
        Assert.Contains("architecture", ex.Message);
    }

    [Fact]
    public void Rejects_Unknown_Json_Member()
    {
        // "mnemonic" (singular) is a typo for "mnemonics" — must be rejected,
        // not silently ignored leaving the map empty.
        var json = """
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonic": {
                "LDA": "[Load(\"A\")]"
              }
            }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
        Assert.Contains("mnemonic", ex.Message);
    }

    [Fact]
    public void Rejects_Missing_Comma_Between_Calls()
    {
        var json = """
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonics": {
                "LDA": "[Load(\"A\") SetNZ(\"A\")]"
              }
            }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
        Assert.Contains("','", ex.Message);
    }

    [Theory]
    [InlineData("[Jump(true)]")]        // Jump takes 0 args (bool arg keeps the JSON well-formed)
    [InlineData("[BranchIf(Flag.Z)]")]  // BranchIf takes 2 args
    [InlineData("[Load()]")]            // Load takes 1 arg
    public void Rejects_Wrong_Arity(string opsText)
    {
        var json = $$"""
            {
              "architecture": "mos6502",
              "namespace": "CpuEmulator.Cpus.Mos6502",
              "specClassName": "Mos6502Spec",
              "registers": [],
              "mnemonics": {
                "XXX": "{{opsText}}"
              }
            }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => SemanticsMap.Parse(json));
        Assert.Contains("argument", ex.Message);
    }

    // ─── M3.3 Task 5: the Z80 register/flag declarations + covered map ──────

    private static string Z80SemanticsPath => DataPath.Get("z80-semantics.json");

    [Fact]
    public void Z80_registers_load_as_declared()
    {
        // The 35 Z80 register configs: 22 8-bit STORAGE (main A F B C D E H L + alternate A_..L_ +
        // I R + the M3.4e-1a index halves IXh IXl IYh IYl) + 3 16-bit storage (WZ SP PC) + 10 16-bit
        // pair VIEWS (AF/BC/DE/HL + the alt pairs + IX/IY). M3.4b adds WZ/MEMPTR (read by BIT y,(HL)
        // for its X/Y). M3.4e-1a (D2 storage inversion): IX/IY become computed pair-views over the new
        // 8-bit halves (storage moved off the IX/IY fields onto IXh/IXl/IYh/IYl). F is Status;
        // SP StackPointer; PC ProgramCounter.
        var map = SemanticsMap.Load(Z80SemanticsPath);
        Assert.Equal("z80", map.Architecture);
        Assert.Equal("CpuEmulator.Cpus.Z80", map.Namespace);
        Assert.Equal("Z80Spec", map.SpecClassName);
        Assert.Equal(35, map.Registers.Length);

        var byName = map.Registers.ToDictionary(r => r.Name);
        Assert.Equal("Status", byName["F"].Role);
        Assert.Equal("StackPointer", byName["SP"].Role);
        Assert.Equal("ProgramCounter", byName["PC"].Role);
        // the alternate set is declared as eight more 8-bit generals
        foreach (var alt in new[] { "A_", "F_", "B_", "C_", "D_", "E_", "H_", "L_" })
            Assert.True(byName.ContainsKey(alt), $"alternate register {alt} missing");
        // I/R 8-bit; SP/PC 16-bit
        Assert.Equal(8, byName["I"].Bits);
        Assert.Equal(8, byName["R"].Bits);
        // M3.4e-1a (D2): the index halves are 8-bit STORAGE; IX/IY are 16-bit VIEWS over them.
        foreach (var half in new[] { "IXh", "IXl", "IYh", "IYl" })
            Assert.Equal(8, byName[half].Bits);
        Assert.Equal(16, byName["IX"].Bits);
        Assert.Equal(16, byName["IY"].Bits);
        Assert.Equal("IXh", byName["IX"].HighHalf);
        Assert.Equal("IXl", byName["IX"].LowHalf);
        Assert.Equal("IYh", byName["IY"].HighHalf);
        Assert.Equal("IYl", byName["IY"].LowHalf);
        // M3.4a: the 8 pair VIEWS carry HighHalf/LowHalf over the 8-bit halves (bidirectional aliasing).
        Assert.Equal("B", byName["BC"].HighHalf);
        Assert.Equal("C", byName["BC"].LowHalf);
        Assert.Equal("A", byName["AF"].HighHalf);
        Assert.Equal("F", byName["AF"].LowHalf);
        Assert.Equal("B_", byName["BC_"].HighHalf);
        Assert.Equal("L_", byName["HL_"].LowHalf);
    }

    [Fact]
    public void Z80_semantics_map_and_flag_layout_load()
    {
        // M3.4a: the Z80 base-plane per-opcode ops are now computed ALGORITHMICALLY from the opcode
        // byte (Z80BaseSemantics), so the per-mnemonic map shrank to NOP/HALT/NEG (the prefixed ED NEG
        // still rides the map; the base plane no longer uses it). The map's ops text validates against
        // FactoryArity (loading without throwing IS the validation). The Z80 flag layout (S=7..C=0) is
        // declared and loaded.
        var map = SemanticsMap.Load(Z80SemanticsPath);
        Assert.NotEmpty(map.Mnemonics);
        Assert.Equal("[]", map.Mnemonics["NOP"]);
        Assert.Equal("[Halt()]", map.Mnemonics["HALT"]);
        Assert.Equal("[]", map.Mnemonics["NEG"]);
        // The Z80 flag layout: 8 bits, S at bit 7, Z at bit 6, C at bit 0 (the per-spec map).
        Assert.Equal(8, map.Flags.Length);
        var flagBit = map.Flags.ToDictionary(b => b.Name, b => b.Bit);
        Assert.Equal(7, flagBit["S"]);
        Assert.Equal(6, flagBit["Z"]);
        Assert.Equal(4, flagBit["H"]);
        Assert.Equal(0, flagBit["C"]);
    }
}
