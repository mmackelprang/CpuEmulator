using Microsoft.CodeAnalysis;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CbClassifyTests
{
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("cbz")]
        public static class CbzSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xCB)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x07, "RLCA", AddrMode.Implied, [Rlca()]),
                Insn(0xCB, 0x00, "RLC", AddrMode.Bit, [CbRotate("RLC", "B")]),
                Insn(0xCB, 0x40, "BIT", AddrMode.Bit, [CbBit("BIT", 0, "B")]),
            ];
        }
        """;

    [Fact]
    public void Cb_shaped_rows_classify_and_compile()
    {
        var result = GeneratorTestHost.Run(Spec);
        // The synthetic CB spec must generate WITHOUT a generator OR compilation error. (The partial
        // CpuCbz is not provided, so the compilation will report missing-partial errors — filter to the
        // GENERATOR diagnostics only, which prove classification + emission succeeded.)
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        // The three rows produced per-op methods (the dispatch keys: 0x07, 0xCB00, 0xCB40).
        Assert.Contains("private void Op07()", result.GeneratedText);
        Assert.Contains("private void OpCB00()", result.GeneratedText);
        Assert.Contains("private void OpCB40()", result.GeneratedText);
    }
}
