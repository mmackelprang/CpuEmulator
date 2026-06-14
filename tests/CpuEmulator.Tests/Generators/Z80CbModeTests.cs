using CpuEmulator.Core.Jit;
using CpuEmulator.Core.Specification;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CbModeTests
{
    [Fact]
    public void AddrMode_has_Bit_member()
    {
        Assert.True(System.Enum.IsDefined(typeof(AddrMode), "Bit"));
    }

    [Fact]
    public void JitMode_has_Bit_member()
    {
        Assert.True(System.Enum.IsDefined(typeof(JitMode), "Bit"));
    }
}
