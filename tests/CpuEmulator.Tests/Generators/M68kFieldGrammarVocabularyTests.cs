using CpuEmulator.Core.Specification;
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kFieldGrammarVocabularyTests
{
    [Fact]
    public void FieldOp_carries_its_grammar_fields()
    {
        // operation "ADD.size Dn,<ea>" sketch: match bits, size in 7-6 (standard b/w/l), EA in 5-0.
        var op = new FieldOp(
            Mask: 0xF100, Match: 0xD000, Operation: "ADD",
            SizeShift: 6, SizeWidth: 2, SizeEncoding: SizeEncoding.Standard,
            EaShift: 0, LegalEa: EaCategory.DataAddressing);
        Assert.Equal((ushort)0xF100, op.Mask);
        Assert.Equal((ushort)0xD000, op.Match);
        Assert.Equal("ADD", op.Operation);
        Assert.Equal(6, op.SizeShift);
        Assert.Equal(SizeEncoding.Standard, op.SizeEncoding);
        Assert.Equal(0, op.EaShift);
    }

    [Fact]
    public void FieldGrammar_carries_fetch_unit_and_ops()
    {
        var op = new FieldOp(
            Mask: 0xF100, Match: 0xD000, Operation: "ADD",
            SizeShift: 6, SizeWidth: 2, SizeEncoding: SizeEncoding.Standard,
            EaShift: 0, LegalEa: EaCategory.DataAddressing);
        var grammar = new FieldGrammar(FetchUnit.Word, [op]);
        Assert.Equal(FetchUnit.Word, grammar.FetchUnit);
        Assert.Single(grammar.Ops);
        Assert.Same(op, grammar.Ops[0]);
    }

    [Fact]
    public void A_spec_declaring_a_field_grammar_and_word_fetch_parses_clean()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("fg")]
        public static class FgSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32), new("A0", 32),
                new("SP", 32, RegisterRole.StackPointer), new("PC", 32, RegisterRole.ProgramCounter),
                new("SR", 16, RegisterRole.Status),
            ];
            public static readonly FlagLayout Flags = new([
                new("C", 0), new("V", 1), new("Z", 2), new("N", 3), new("X", 4), new("S", 13)]);
            // A word-granular field grammar: ONE op, standard size encoding, data-addressing EA.
            public static readonly FieldGrammar Decode68k = new(
                FetchUnit.Word,
                [ FieldOp(Mask: 0xF100, Match: 0xD000, Operation: "ADD",
                          SizeShift: 6, SizeWidth: 2, SizeEncoding: SizeEncoding.Standard,
                          EaShift: 0, LegalEa: EaCategory.DataAddressing) ]);
            public static readonly InstructionDef[] Instructions = [];
        }
        """;
}
