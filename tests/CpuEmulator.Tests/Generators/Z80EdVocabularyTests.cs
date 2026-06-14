using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdVocabularyTests
{
    [Fact]
    public void Ed_io_factories_carry_their_operand()
    {
        Assert.Equal("B", ((EdInOp)EdIn("B")).Target);
        Assert.Equal("C", ((EdOutOp)EdOut("C")).Source);
    }

    [Fact]
    public void Ed_alu16_and_ld_carry_op_and_pair()
    {
        var adc = (EdAdcSbc16Op)EdAdcSbc16("ADC", "HL");
        Assert.Equal("ADC", adc.Op); Assert.Equal("HL", adc.Pair);
        var st = (EdLdNnRpOp)EdLdNnRp("STORE", "BC");
        Assert.Equal("STORE", st.Op); Assert.Equal("BC", st.Pair);
    }

    [Fact]
    public void Ed_misc_factories_build_their_ops()
    {
        Assert.IsType<EdNegOp>(EdNeg());
        Assert.True(((EdRetnOp)EdRetn(true)).IsReti);
        Assert.Equal(2, ((EdImOp)EdIm(2)).Mode);
        Assert.Equal("A_I", ((EdLdIaRaOp)EdLdIaRa("A_I")).Op);
        Assert.True(((EdRrdRldOp)EdRrdRld(true)).IsRld);
        Assert.IsType<EdNopOp>(EdNop());
    }
}
