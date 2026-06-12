namespace CpuEmulator.Tests.Generators;

public class RegisterParsingTests
{
    private static string WithRegisters(string registersBody) =>
        GeneratorTestHost.ReplaceSection(
            GeneratorHappyPathTests.ValidSpecSource,
            """
                public static readonly RegisterDef[] Registers =
                [
                    new("A", 8),
                    new("X", 8),
                    new("S", 8, RegisterRole.StackPointer),
                    new("P", 8, RegisterRole.Status),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """,
            registersBody);

    [Fact]
    public void Registers_emit_fields_and_names_in_table_order()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("public byte A;", result.GeneratedText);
        Assert.Contains("public ushort PC;", result.GeneratedText);
        Assert.Contains("""["A", "X", "S", "P", "PC"]""", result.GeneratedText);
    }

    [Fact]
    public void Missing_registers_field_reports_CPUGEN001()
    {
        var result = GeneratorTestHost.Run(WithRegisters(""));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN001");
        Assert.Empty(result.GeneratedTrees); // no model -> nothing generated
    }

    [Fact]
    public void Non_literal_register_entry_reports_CPUGEN002()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new(System.Environment.MachineName, 8),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN002");
    }

    [Fact]
    public void Unsupported_register_width_reports_CPUGEN002()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("A", 12),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN002");
    }

    [Fact]
    public void Missing_program_counter_reports_CPUGEN007()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("A", 8),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN007");
    }

    [Fact]
    public void Two_program_counters_report_CPUGEN007()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("PC", 16, RegisterRole.ProgramCounter),
                    new("PC2", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN007");
    }

    [Fact]
    public void Duplicate_register_name_reports_CPUGEN002()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("A", 8),
                    new("A", 8),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN002");
    }

    [Fact]
    public void Non_identifier_register_name_reports_CPUGEN002()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("A B", 8),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN002");
    }

    [Theory]
    [InlineData("data")]
    [InlineData("lo")]
    [InlineData("temp")]
    public void Register_named_after_an_emitted_local_reports_CPUGEN002(string name)
    {
        string source = GeneratorTestHost.ReplaceSection(
            GeneratorHappyPathTests.ValidSpecSource,
            """new("A", 8),""",
            $"""new("{name}", 8), new("A", 8),""");

        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN002");
        Assert.Contains("emitted local name", diagnostic.GetMessage());
    }

    [Fact]
    public void Generated_truncation_casts_are_unchecked()
    {
        // Generated code must stay correct under consumer CheckForOverflowUnderflow=true.
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("unchecked((byte)value)", result.GeneratedText);
        Assert.Contains("PC = unchecked((ushort)(PC + 1));", result.GeneratedText);
    }
}
