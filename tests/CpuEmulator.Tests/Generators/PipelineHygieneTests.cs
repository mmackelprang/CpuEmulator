using CpuEmulator.Generators;
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

        // One CPUGEN009 per collider, each pointing at that spec's class identifier.
        var collisions = result.GeneratorDiagnostics.Where(d => d.Id == "CPUGEN009").ToList();
        Assert.Equal(2, collisions.Count);
        Assert.Contains(collisions, d => SpanText(source, d) == "Tiny6502Spec");
        Assert.Contains(collisions, d => SpanText(source, d) == "OtherSpec");
        Assert.DoesNotContain(result.CompilationDiagnostics, d => d.Id == "CS8785");
        Assert.Empty(result.GeneratedTrees); // neither emitted on collision
    }

    [Fact]
    public void Emitter_throws_loudly_when_a_template_is_handed_an_unknown_mode()
    {
        // Construct a model the parser could never produce: jump class with ZeroPage mode.
        // The template must throw (failing generation as CS8785) rather than mis-emit.
        var model = new SpecModel(
            "TestCpu", "BadCpu", "test",
            new LocationInfo("test.cs", default, default),
            new EquatableArray<RegisterModel>(
                System.Collections.Immutable.ImmutableArray.Create(
                    new RegisterModel("PC", 16, "ProgramCounter"))),
            new EquatableArray<InstructionModel>(
                System.Collections.Immutable.ImmutableArray.Create(
                    new InstructionModel(0x4C, "JMP", "ZeroPage", InstructionClass.Jump,
                        new EquatableArray<OpModel>(
                            System.Collections.Immutable.ImmutableArray.Create(
                                new OpModel("Jump", new EquatableArray<string>(
                                    System.Collections.Immutable.ImmutableArray<string>.Empty))))))));

        Assert.Throws<System.InvalidOperationException>(() => CpuEmitter.Emit(model));
    }

    [Fact]
    public void Brk_and_Rti_emit_load_bearing_bus_patterns()
    {
        string source = GeneratorTestHost.ReplaceSection(
            GeneratorHappyPathTests.ValidSpecSource,
            """
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
                    Insn(0xEA, "NOP", AddrMode.Implied, []),
                ];
            """,
            """
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x00, "BRK", AddrMode.Implied, [Brk()]),
                    Insn(0x40, "RTI", AddrMode.Implied, [Rti()]),
                ];
            """);

        var result = GeneratorTestHost.Run(source);

        Assert.Empty(result.AllErrors);
        Assert.Contains("ReadBus(0xFFFE)", result.GeneratedText);
        Assert.Contains("(byte)(P | 0x30)", result.GeneratedText);
        Assert.Contains("(ReadBus(0x100u + S) | 0x20) & 0xEF", result.GeneratedText);
    }

    [Fact]
    public void Generated_step_declares_and_calls_the_interrupt_hook()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("private partial bool TryServiceInterrupt();", result.GeneratedText);
        Assert.Contains("if (TryServiceInterrupt())", result.GeneratedText);
    }

    private static string SpanText(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

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

    [Fact]
    public void Identical_reparse_hits_the_incremental_cache()
    {
        // Pins value-equality of the whole pipeline state (ParsedSpec → SpecModel →
        // EquatableArray fields): a reparsed-but-identical source must yield Cached or
        // Unchanged step outputs, never Modified/New. Without element-wise equality,
        // raw ImmutableArray fields compare by reference and every keystroke would
        // re-run the Collect node and re-emit every source.
        var runResult = GeneratorTestHost.RunTwiceWithReparse(GeneratorHappyPathTests.ValidSpecSource);
        var result = Assert.Single(runResult.Results);

        // The FAWMN transform re-runs on the new tree but must produce an EQUAL ParsedSpec:
        var specSteps = result.TrackedSteps["Specs"];
        Assert.NotEmpty(specSteps);
        Assert.All(specSteps, step => Assert.All(step.Outputs, output =>
            Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                $"'Specs' output reason was {output.Reason}")));

        // ...so the Collect node and the source output are reused, not re-executed:
        var collectedSteps = result.TrackedSteps["Collected"];
        Assert.NotEmpty(collectedSteps);
        Assert.All(collectedSteps, step => Assert.All(step.Outputs, output =>
            Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                $"'Collected' output reason was {output.Reason}")));

        Assert.NotEmpty(result.TrackedOutputSteps);
        Assert.All(result.TrackedOutputSteps.SelectMany(kv => kv.Value),
            step => Assert.All(step.Outputs, output =>
                Assert.True(
                    output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                    $"output step reason was {output.Reason}")));
    }
}
