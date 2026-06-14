using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedVocabularyTests
{
    [Fact]
    public void Indexed_ld_factory_carries_op_and_reg()
    {
        var ld = (DdFdLdIndexedOp)DdFdLdIndexed("LOAD", "A");
        Assert.Equal("LOAD", ld.Op); Assert.Equal("A", ld.Reg);
        var st = (DdFdLdIndexedOp)DdFdLdIndexed("STORE", "B");
        Assert.Equal("STORE", st.Op); Assert.Equal("B", st.Reg);
    }

    [Fact]
    public void Indexed_alu_and_incdec_and_storeimm_build()
    {
        Assert.Equal("ADD", ((DdFdAluIndexedOp)DdFdAluIndexed("ADD")).Op);
        Assert.True(((DdFdIncDecIndexedOp)DdFdIncDecIndexed(true)).IsDec);
        Assert.IsType<DdFdStoreImmIndexedOp>(DdFdStoreImmIndexed());
    }
}
