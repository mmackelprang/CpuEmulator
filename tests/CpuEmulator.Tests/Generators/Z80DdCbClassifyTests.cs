using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80DdCbClassifyTests
{
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcbcls")]
        public static class DdCbClsSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8),
                new("IXh", 8), new("IXl", 8), new("IYh", 8), new("IYl", 8), new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("IX", 16, HighHalf: "IXh", LowHalf: "IXl"),
                new("IY", 16, HighHalf: "IYh", LowHalf: "IYl"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [
                    new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true),
                    new PrefixByte(0xFD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)],
                ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0xCB, 0x06, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "-")]),  // no copy
                Insn(0xDD, 0xCB, 0x00, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "B")]),  // store-copy
                Insn(0xDD, 0xCB, 0x46, "BIT", AddrMode.Indexed, [DdCb("BIT", 0, "-")]),
                Insn(0xDD, 0xCB, 0x80, "RES", AddrMode.Indexed, [DdCb("RES", 0, "B")]),
                Insn(0xDD, 0xCB, 0xC6, "SET", AddrMode.Indexed, [DdCb("SET", 0, "-")]),
                Insn(0xFD, 0xCB, 0x06, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "-")]),  // IY plane
            ];
        }
        """;

    [Fact]
    public void DdCb_rows_classify_compile_and_emit_one_body_per_family()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        // One generated body per compound opcode (the 24-bit key in the method name).
        Assert.Contains("private void OpDDCB06()", result.GeneratedText);
        Assert.Contains("private void OpDDCB00()", result.GeneratedText);
        Assert.Contains("private void OpDDCB46()", result.GeneratedText);
        Assert.Contains("private void OpDDCB80()", result.GeneratedText);
        Assert.Contains("private void OpDDCBC6()", result.GeneratedText);
        Assert.Contains("private void OpFDCB06()", result.GeneratedText);
    }

    [Fact]
    public void DdCb_disassembler_arm_discriminates_IX_vs_IY_by_high_word_not_high_byte()
    {
        // The disassembler Indexed arm must key IX/IY off >> 16 for a 24-bit compound key (H1/D11) —
        // using >> 8 would yield 0xDDCB (!= 0xDD) and mis-render the DD compound key as IY.
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        string text = result.GeneratedText;
        // The DD compound key renders IX; the FD compound key renders IY. The arms live in the
        // Disassemble switch; assert both index registers appear against their compound keys.
        Assert.Contains("0xDDCB06 => $\"RLC (IX+", text);
        Assert.Contains("0xFDCB06 => $\"RLC (IY+", text);
    }
}
