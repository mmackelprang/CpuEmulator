using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumKeyboardTests
{
    [Fact]
    public void KeyCode_has_the_two_spectrum_modifier_keys()
    {
        Assert.True(Enum.IsDefined(typeof(KeyCode), KeyCode.CapsShift));
        Assert.True(Enum.IsDefined(typeof(KeyCode), KeyCode.SymbolShift));
    }

    [Theory]
    // (KeyCode, expected half-row index 0..7, expected bit 0..4)
    // Half-row 0 = FEFE (A8): CAPS,Z,X,C,V ; bit0=CAPS.
    [InlineData(KeyCode.CapsShift, 0, 0)]
    [InlineData(KeyCode.Z, 0, 1)]
    [InlineData(KeyCode.V, 0, 4)]
    // Half-row 1 = FDFE (A9): A,S,D,F,G ; bit0=A.
    [InlineData(KeyCode.A, 1, 0)]
    [InlineData(KeyCode.G, 1, 4)]
    // Half-row 3 = F7FE (A11): 1,2,3,4,5 ; bit0=1.
    [InlineData(KeyCode.Digit1, 3, 0)]
    [InlineData(KeyCode.Digit5, 3, 4)]
    // Half-row 4 = EFFE (A12): 0,9,8,7,6 ; bit0=0.
    [InlineData(KeyCode.Digit0, 4, 0)]
    [InlineData(KeyCode.Digit6, 4, 4)]
    // Half-row 6 = BFFE (A14): ENTER,L,K,J,H ; bit0=ENTER.
    [InlineData(KeyCode.Enter, 6, 0)]
    // Half-row 7 = 7FFE (A15): SPACE,SYMSHIFT,M,N,B ; bit0=SPACE.
    [InlineData(KeyCode.Space, 7, 0)]
    [InlineData(KeyCode.SymbolShift, 7, 1)]
    public void Matrix_maps_keys_to_the_correct_half_row_and_bit(KeyCode key, int halfRow, int bit)
    {
        Assert.True(SpectrumKeyMatrix.TryMap(key, out int row, out int b));
        Assert.Equal(halfRow, row);
        Assert.Equal(bit, b);
    }

    [Fact]
    public void Unknown_keys_do_not_map()
    {
        Assert.False(SpectrumKeyMatrix.TryMap(KeyCode.None, out _, out _));
        Assert.False(SpectrumKeyMatrix.TryMap(KeyCode.Tab, out _, out _)); // no Spectrum key
    }
}
