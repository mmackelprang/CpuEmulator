using Microsoft.CodeAnalysis;

namespace CpuEmulator.Tests.Generators;

public class GeneratorHappyPathTests
{
    // A complete, valid spec + the minimal hand-written partial the generated half requires.
    // Shared by parsing/emission tests (later tasks mutate pieces of it).
    public const string ValidSpecSource = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace TestCpu;

        [CpuSpecification("test6502")]
        public static class Tiny6502Spec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("X", 8),
                new("S", 8, RegisterRole.StackPointer),
                new("P", 8, RegisterRole.Status),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
                Insn(0xEA, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class Tiny6502Cpu
        {
            private readonly IAddressSpace _bus;
            public Tiny6502Cpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
        }
        """;

    [Fact]
    public void Valid_spec_generates_a_cpu_class_that_compiles()
    {
        var result = GeneratorTestHost.Run(ValidSpecSource);

        Assert.Empty(result.AllErrors);
        var tree = Assert.Single(result.GeneratedTrees);
        Assert.EndsWith("Tiny6502Cpu.g.cs", tree.FilePath);
        Assert.Contains("partial class Tiny6502Cpu", result.GeneratedText);
    }

    [Fact]
    public void CpuName_named_argument_overrides_derived_name()
    {
        var source = ValidSpecSource
            .Replace("[CpuSpecification(\"test6502\")]",
                     "[CpuSpecification(\"test6502\", CpuName = \"WeirdName\")]")
            .Replace("Tiny6502Cpu", "WeirdName");

        var result = GeneratorTestHost.Run(source);

        Assert.Empty(result.AllErrors);
        Assert.Contains("partial class WeirdName", result.GeneratedText);
    }

    [Fact]
    public void Class_without_attribute_generates_nothing()
    {
        var result = GeneratorTestHost.Run("namespace N; public static class NotASpec { }");

        Assert.Empty(result.GeneratedTrees);
        Assert.Empty(result.GeneratorDiagnostics);
    }

    [Fact]
    public void Global_namespace_spec_reports_CPUGEN009()
    {
        var source = ValidSpecSource.Replace("namespace TestCpu;", "");

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN009");
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void Invalid_CpuName_reports_CPUGEN009()
    {
        var source = ValidSpecSource.Replace("[CpuSpecification(\"test6502\")]",
            "[CpuSpecification(\"test6502\", CpuName = \"1Bad\")]");

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN009");
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void Same_CpuName_in_two_namespaces_generates_both_without_collision()
    {
        var source = $$"""
            using CpuEmulator.Core.Specification;
            using static CpuEmulator.Core.Specification.Spec;

            namespace NsOne
            {
            {{MinimalSpecClass("SameSpec", "one")}}
            }

            namespace NsTwo
            {
            {{MinimalSpecClass("SameSpec", "two")}}
            }
            """;

        var result = GeneratorTestHost.Run(source);

        Assert.Equal(2, result.GeneratedTrees.Length);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.DoesNotContain(result.CompilationDiagnostics, d => d.Id == "CS8785");
    }

    [Fact]
    public void Two_distinct_specs_generate_two_files()
    {
        var source = $$"""
            using CpuEmulator.Core.Specification;
            using static CpuEmulator.Core.Specification.Spec;

            namespace TestCpu
            {
            {{MinimalSpecClass("AlphaSpec", "alpha")}}

            {{MinimalSpecClass("BetaSpec", "beta")}}
            }
            """;

        var result = GeneratorTestHost.Run(source);

        Assert.Equal(2, result.GeneratedTrees.Length);
        Assert.Contains(result.GeneratedTrees, t => t.FilePath.EndsWith("AlphaCpu.g.cs"));
        Assert.Contains(result.GeneratedTrees, t => t.FilePath.EndsWith("BetaCpu.g.cs"));
    }

    private static string MinimalSpecClass(string className, string architecture) => $$"""
            [CpuSpecification("{{architecture}}")]
            public static class {{className}}
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
        """;
}
