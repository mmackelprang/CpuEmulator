using Microsoft.CodeAnalysis;

namespace CpuEmulator.Tests.Generators;

public class PipelineHygieneTests
{
    [Fact]
    public void Two_specs_colliding_on_namespace_and_cpu_name_report_CPUGEN009_not_a_crash()
    {
        // Same namespace + same derived CPU name — previously crashed AddSource with CS8785.
        // Using bracketed namespaces so both classes are valid in a single compilation unit.
        string source = """
            using CpuEmulator.Core;
            using CpuEmulator.Core.Specification;
            using static CpuEmulator.Core.Specification.Spec;

            namespace TestCpu
            {
                [CpuSpecification("test6502")]
                public static class Tiny6502Spec
                {
                    public static readonly RegisterDef[] Registers =
                    [
                        new("PC", 16, RegisterRole.ProgramCounter),
                    ];

                    public static readonly InstructionDef[] Instructions =
                    [
                        Insn(0xEA, "NOP", AddrMode.Implied, []),
                    ];
                }

                [CpuSpecification("other", CpuName = "Tiny6502Cpu")]
                public static class OtherSpec
                {
                    public static readonly RegisterDef[] Registers =
                    [
                        new("PC", 16, RegisterRole.ProgramCounter),
                    ];

                    public static readonly InstructionDef[] Instructions =
                    [
                        Insn(0xEA, "NOP", AddrMode.Implied, []),
                    ];
                }
            }
            """;

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN009");
        Assert.DoesNotContain(result.CompilationDiagnostics, d => d.Id == "CS8785");
        Assert.Empty(result.GeneratedTrees); // neither emitted on collision
    }

    [Fact]
    public void Record_spec_class_reports_CPUGEN009()
    {
        string source = GeneratorHappyPathTests.ValidSpecSource
            .Replace("public static class Tiny6502Spec", "public record Tiny6502Spec");

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN009");
    }

    [Fact]
    public void Diagnostics_still_carry_precise_locations_after_the_DiagnosticInfo_conversion()
    {
        // Duplicate-opcode location must still point at the duplicate row (regression
        // guard: the DiagnosticInfo round-trip must preserve file path + span).
        // Location is an external-file location (no SourceTree) after the DiagnosticInfo
        // conversion — read span text from the original source string.
        string source = GeneratorHappyPathTests.ValidSpecSource.Replace(
            "Insn(0xEA, \"NOP\", AddrMode.Implied, []),",
            "Insn(0xA9, \"NOP\", AddrMode.Implied, []),");

        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN005");
        string locationText = source.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length);
        Assert.StartsWith("Insn(0xA9, \"NOP\"", locationText);
    }
}
