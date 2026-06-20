using CpuEmulator.Core;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumKeyboardTests
{
    [Fact]
    public void KeyCode_has_the_two_spectrum_modifier_keys()
    {
        Assert.True(Enum.IsDefined(typeof(KeyCode), KeyCode.CapsShift));
        Assert.True(Enum.IsDefined(typeof(KeyCode), KeyCode.SymbolShift));
    }
}
