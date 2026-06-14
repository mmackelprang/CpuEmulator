using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedClassifyTests
{
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("idx")]
        public static class IdxSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8),
                new("IXh", 8), new("IXl", 8), new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("IX", 16, HighHalf: "IXh", LowHalf: "IXl"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0x7E, "LD",  AddrMode.Indexed, [DdFdLdIndexed("LOAD", "A")]),
                Insn(0xDD, 0x70, "LD",  AddrMode.Indexed, [DdFdLdIndexed("STORE", "B")]),
                Insn(0xDD, 0x36, "LD",  AddrMode.Indexed, [DdFdStoreImmIndexed()]),
                Insn(0xDD, 0x86, "ADD", AddrMode.Indexed, [DdFdAluIndexed("ADD")]),
                Insn(0xDD, 0x34, "INC", AddrMode.Indexed, [DdFdIncDecIndexed(false)]),
            ];
        }
        """;

    [Fact]
    public void Indexed_rows_classify_and_compile()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("private void OpDD7E()", result.GeneratedText);
        Assert.Contains("private void OpDD34()", result.GeneratedText);
    }
}
