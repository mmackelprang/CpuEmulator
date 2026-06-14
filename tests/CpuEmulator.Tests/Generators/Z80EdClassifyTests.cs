using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdClassifyTests
{
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edz")]
        public static class EdzSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("I", 8), new("R", 8), new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0x40, "IN",  AddrMode.Register, [EdIn("B")]),
                Insn(0xED, 0x41, "OUT", AddrMode.Register, [EdOut("B")]),
                Insn(0xED, 0x42, "SBC", AddrMode.Register, [EdAdcSbc16("SBC", "BC")]),
                Insn(0xED, 0x43, "LD",  AddrMode.ExtendedAddress, [EdLdNnRp("STORE", "BC")]),
                Insn(0xED, 0x44, "NEG", AddrMode.Implied, [EdNeg()]),
                Insn(0xED, 0x45, "RETN", AddrMode.Implied, [EdRetn(false)]),
                Insn(0xED, 0x46, "IM",  AddrMode.Implied, [EdIm(0)]),
                Insn(0xED, 0x47, "LD",  AddrMode.Implied, [EdLdIaRa("I_A")]),
                Insn(0xED, 0x67, "RRD", AddrMode.RegisterIndirect, [EdRrdRld(false)]),
                Insn(0xED, 0x77, "NOP", AddrMode.Implied, [EdNop()]),
            ];
        }
        """;

    [Fact]
    public void Ed_shaped_rows_classify_and_compile()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("private void OpED40()", result.GeneratedText);
        Assert.Contains("private void OpED44()", result.GeneratedText);
        Assert.Contains("private void OpED67()", result.GeneratedText);
    }
}
