using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2KeyMapTests
{
    [Theory]
    // Letters latch as UPPERCASE ASCII regardless of the typed Char's case.
    [InlineData(KeyCode.A, 'a', 0x41)]   // lowercase 'a' folds up -> $41 'A'
    [InlineData(KeyCode.A, 'A', 0x41)]   // shifted 'A' -> $41 'A'
    [InlineData(KeyCode.Z, 'z', 0x5A)]
    // Digits + common symbols latch as ASCII.
    [InlineData(KeyCode.Digit0, '0', 0x30)]
    [InlineData(KeyCode.Digit9, '9', 0x39)]
    // Whitespace / editing.
    [InlineData(KeyCode.Space, ' ', 0x20)]
    [InlineData(KeyCode.Enter, null, 0x0D)]       // CR
    [InlineData(KeyCode.Backspace, null, 0x08)]   // left-arrow / BS
    [InlineData(KeyCode.Escape, null, 0x1B)]
    public void Maps_a_key_to_the_uppercase_2plus_code(KeyCode key, char? ch, int expected)
    {
        Assert.True(Apple2KeyMap.TryMap(key, ch, out byte code));
        Assert.Equal((byte)expected, code);
    }

    [Fact]
    public void A_printable_char_with_no_dedicated_keycode_uses_the_uppercased_char()
    {
        // A symbol the host resolved to a Char (e.g. '/') maps to its ASCII even without a KeyCode arm.
        Assert.True(Apple2KeyMap.TryMap(KeyCode.None, '/', out byte code));
        Assert.Equal((byte)'/', code);
    }

    [Theory]
    [InlineData(KeyCode.None, null)]      // nothing typed
    [InlineData(KeyCode.Tab, null)]       // the ][+ keyboard has no Tab key code we model
    [InlineData(KeyCode.ArrowUp, null)]   // up-arrow: no base-][+ code (it is a later additive arm)
    public void Unmapped_keys_are_a_no_op(KeyCode key, char? ch)
    {
        Assert.False(Apple2KeyMap.TryMap(key, ch, out byte code));
        Assert.Equal(0, code);
    }
}
