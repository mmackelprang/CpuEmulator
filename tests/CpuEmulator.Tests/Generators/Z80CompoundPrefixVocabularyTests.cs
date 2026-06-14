using CpuEmulator.Core.Specification;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CompoundPrefixVocabularyTests
{
    [Fact]
    public void Plain_prefix_defaults_to_no_compound()
    {
        var cb = new PrefixByte(0xCB);
        Assert.Equal(0xCB, cb.Value);
        Assert.Null(cb.CompoundWith);
        Assert.False(cb.DisplacementBeforeOpcode);
    }

    [Fact]
    public void Compound_prefix_carries_its_compound_and_displacement_flag()
    {
        var dd = new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true);
        Assert.Equal(0xDD, dd.Value);
        Assert.Equal((byte)0xCB, dd.CompoundWith);
        Assert.True(dd.DisplacementBeforeOpcode);
    }
}
