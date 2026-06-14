using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CbVocabularyTests
{
    [Fact]
    public void Rotate_accumulator_factories_build_their_ops()
    {
        Assert.IsType<RlcaOp>(Rlca());
        Assert.IsType<RrcaOp>(Rrca());
        Assert.IsType<RlaOp>(Rla());
        Assert.IsType<RraOp>(Rra());
    }

    [Fact]
    public void CbRotate_carries_op_and_target()
    {
        var op = (CbRotateOp)CbRotate("RLC", "B");
        Assert.Equal("RLC", op.Op);
        Assert.Equal("B", op.Target);
    }

    [Fact]
    public void CbBit_carries_op_bit_and_target()
    {
        var op = (CbBitOp)CbBit("BIT", 7, "(HL)");
        Assert.Equal("BIT", op.Op);
        Assert.Equal(7, op.Bit);
        Assert.Equal("(HL)", op.Target);
    }
}
