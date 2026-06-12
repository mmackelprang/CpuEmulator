namespace CpuEmulator.Tests.Generators;

public class InstructionParsingTests
{
    private static string WithInstructions(string instructionsBody) =>
        GeneratorHappyPathTests.ValidSpecSource.Replace(
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
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA0, "LDY", AddrMode.Immediate, [Load(Reg.Y), SetNZ(Reg.Y)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN008");
    }
}
