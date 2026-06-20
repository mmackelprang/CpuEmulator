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

    private static SpectrumUla BareUla()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x4000, new byte[0xC000], writable: true);
        return new SpectrumUla(space);
    }

    [Fact]
    public void Pressing_A_pulls_its_bit_low_only_on_the_FDFE_half_row()
    {
        var ula = BareUla();
        ula.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a')); // row 1 (A9), bit 0

        // IN A,(0xFE) with A=0xFD selects the FDFE half-row (A9 low) → bit 0 reads 0 (pressed).
        uint fdfe = ula.Read(0xFDFEu, AccessWidth.Byte);
        Assert.Equal(0u, fdfe & 0x01);          // 'A' pressed → bit 0 low
        Assert.Equal(0x1Eu, fdfe & 0x1F);       // the other 4 keys of the row still high

        // A different half-row (FEFE = CAPS..V) is unaffected: all 5 bits high.
        uint fefe = ula.Read(0xFEFEu, AccessWidth.Byte);
        Assert.Equal(0x1Fu, fefe & 0x1F);

        // Releasing A restores the bit.
        ula.PostKey(new KeyEvent(KeyAction.Up, KeyCode.A, null));
        Assert.Equal(0x1Fu, ula.Read(0xFDFEu, AccessWidth.Byte) & 0x1F);
    }

    [Fact]
    public void Selecting_all_rows_with_port_00FE_ANDs_every_pressed_key()
    {
        var ula = BareUla();
        ula.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Space, ' ')); // row 7, bit 0
        // Port 0x00FE: high byte 0x00 → every address line low → all 8 rows selected, ANDed.
        uint all = ula.Read(0x00FEu, AccessWidth.Byte);
        Assert.Equal(0u, all & 0x01); // SPACE pressed shows through (bit 0 of row 7)
    }

    [Fact]
    public void Odd_ports_are_not_decoded_by_the_ULA()
    {
        var ula = BareUla();
        ula.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Space, ' '));
        Assert.Equal(0xFFu, ula.Read(0xFFFFu, AccessWidth.Byte)); // odd port → open bus
    }
}
