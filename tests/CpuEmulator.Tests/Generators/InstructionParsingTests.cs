namespace CpuEmulator.Tests.Generators;

public class InstructionParsingTests
{
    private static string WithInstructions(string instructionsBody) =>
        GeneratorTestHost.ReplaceSection(
            GeneratorHappyPathTests.ValidSpecSource,
            """
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
                    Insn(0xEA, "NOP", AddrMode.Implied, []),
                ];
            """,
            instructionsBody);

    [Fact]
    public void Valid_spec_compiles_and_implements_ICpuCore()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("public void Step()", result.GeneratedText);
        Assert.Contains("public void Run(ref long cycleBudget)", result.GeneratedText);
    }

    [Fact]
    public void Instruction_table_is_summarized_in_generated_output()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        // The summary comment is review/debug aid AND pins that parsing saw both rows.
        Assert.Contains("0xA9 LDA Immediate", result.GeneratedText);
        Assert.Contains("0xEA NOP Implied", result.GeneratedText);
    }

    [Fact]
    public void Missing_instructions_field_reports_CPUGEN003()
    {
        var result = GeneratorTestHost.Run(WithInstructions(""));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN003");
    }

    [Fact]
    public void Duplicate_opcode_reports_CPUGEN005()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A)]),
                    Insn(0xA9, "LDA", AddrMode.ZeroPage, [Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN005");
    }

    [Fact]
    public void Unknown_micro_op_factory_reports_CPUGEN006()
    {
        // 'Frobnicate' type-checks nowhere, but the generator must report ITS diagnostic
        // (not just let the compile error stand) so spec authors get a spec-level message.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Frobnicate(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN006");
    }

    [Fact]
    public void Non_literal_opcode_reports_CPUGEN004()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                private static byte Op() => 0xA9;
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(Op(), "LDA", AddrMode.Immediate, [Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN004");
    }

    [Fact]
    public void Unknown_addressing_mode_reports_CPUGEN004()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", (AddrMode)99, [Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN004");
    }

    [Fact]
    public void Micro_op_referencing_undeclared_register_reports_CPUGEN008()
    {
        string source = WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA0, "LDY", AddrMode.Immediate, [Load(Reg.Y), SetNZ(Reg.Y)]),
                ];
            """);
        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN008");

        // The diagnostic points at the offending argument, not the spec class.
        // Location is an external-file location (no SourceTree) after the DiagnosticInfo
        // conversion — read span text from the original source string instead.
        var diagnostic = result.GeneratorDiagnostics.First(d => d.Id == "CPUGEN008");
        string locationText = source.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length);
        Assert.Contains("Reg.Y", locationText);
    }

    [Fact]
    public void Out_of_range_opcode_reports_CPUGEN004_not_CPUGEN005()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x100, "LDA", AddrMode.Immediate, [Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN004");
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "CPUGEN005");
    }

    [Fact]
    public void Known_micro_op_with_wrong_arity_reports_CPUGEN004()
    {
        // Wrong arity on a KNOWN factory is an invalid instruction (CPUGEN004), not the
        // misleading "not recognized" CPUGEN006. The consumer compile error (CS1501) is
        // expected alongside, so assert GeneratorDiagnostics only.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A, Reg.X)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN004");
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "CPUGEN006");
    }

    [Fact]
    public void Duplicate_opcode_diagnostic_points_at_the_duplicate_row()
    {
        string source = WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A)]),
                    Insn(0xA9, "LDA", AddrMode.ZeroPage, [Load(Reg.A)]),
                ];
            """);
        var result = GeneratorTestHost.Run(source);

        var diagnostic = result.GeneratorDiagnostics.First(d => d.Id == "CPUGEN005");
        // Location is an external-file location (no SourceTree) after the DiagnosticInfo
        // conversion — read span text from the original source string instead.
        string locationText = source.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length);
        Assert.StartsWith("Insn", locationText);
        Assert.Contains("ZeroPage", locationText); // the second row, not the first
    }
}
