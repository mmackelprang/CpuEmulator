using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdBlockVocabularyTests
{
    [Fact]
    public void EdBlock_factory_carries_its_mnemonic()
    {
        Assert.Equal("LDIR", ((EdBlockOp)EdBlock("LDIR")).Mnemonic);
        Assert.Equal("CPD", ((EdBlockOp)EdBlock("CPD")).Mnemonic);
        Assert.Equal("OTDR", ((EdBlockOp)EdBlock("OTDR")).Mnemonic);
    }

    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edblk")]
        public static class EdblkSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
                new("DE", 16, HighHalf: "D", LowHalf: "E"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0xA0, "LDI",  AddrMode.Implied, [EdBlock("LDI")]),
                Insn(0xED, 0xB0, "LDIR", AddrMode.Implied, [EdBlock("LDIR")]),
            ];
        }
        """;

    [Fact]
    public void EdBlock_rows_classify_and_compile()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("private void OpEDA0()", result.GeneratedText);
        Assert.Contains("private void OpEDB0()", result.GeneratedText);
    }
}
