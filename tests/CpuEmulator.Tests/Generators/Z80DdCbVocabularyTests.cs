using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80DdCbVocabularyTests
{
    [Fact]
    public void DdCb_factory_carries_op_index_and_copyreg()
    {
        var rot = (DdCbOp)DdCb("RLC", 0, "B");      // LD B,RLC (IX+d)
        Assert.Equal("RLC", rot.Op); Assert.Equal(0, rot.Index); Assert.Equal("B", rot.CopyReg);

        var rotNoCopy = (DdCbOp)DdCb("RLC", 0, "-"); // RLC (IX+d), z=6
        Assert.Equal("-", rotNoCopy.CopyReg);

        var bit = (DdCbOp)DdCb("BIT", 5, "-");       // BIT 5,(IX+d) — never copies
        Assert.Equal("BIT", bit.Op); Assert.Equal(5, bit.Index); Assert.Equal("-", bit.CopyReg);

        var set = (DdCbOp)DdCb("SET", 3, "H");       // LD H,SET 3,(IX+d)
        Assert.Equal("SET", set.Op); Assert.Equal(3, set.Index); Assert.Equal("H", set.CopyReg);
    }
}
