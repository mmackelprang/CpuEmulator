namespace CpuEmulator.Tests.Generators;

public class RegisterParsingTests
{
    private static string WithRegisters(string registersBody) =>
        GeneratorHappyPathTests.ValidSpecSource.Replace(
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

        Assert.Empty(result.GeneratorDiagnostics); // CS gap closes in Task 5
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
}
