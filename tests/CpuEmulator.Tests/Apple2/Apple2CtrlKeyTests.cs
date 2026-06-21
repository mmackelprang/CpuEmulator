using CpuEmulator.Core;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

/// <summary>D5 (interactions §2.4): the Apple ][+ keyboard produces a control code for Ctrl+letter —
/// the chip ANDs the uppercase letter code with $1F. The un-fakeable proof is at the $C000 latch the
/// IOU reads: a Ctrl+B event latches $02 (not $42); a Ctrl+C latches $03; a plain B latches $42. The
/// keyboard chip is tier-agnostic (it latches into the shared Apple2VideoState), so this is the
/// interpreter-tier gate the queue's T-F row requires.</summary>
public class Apple2CtrlKeyTests
{
    // The pure map fold: a letter with ctrl set returns its ASCII control code.
    [Theory]
    [InlineData(KeyCode.B, 'b', 0x02)]   // Ctrl+B -> STX (enter BASIC)
    [InlineData(KeyCode.C, 'c', 0x03)]   // Ctrl+C -> ETX (break)
    [InlineData(KeyCode.M, 'm', 0x0D)]   // Ctrl+M -> CR (the real ][+ equivalence)
    public void TryMap_folds_a_letter_with_1F_when_ctrl(KeyCode key, char ch, byte expected)
    {
        Assert.True(Apple2KeyMap.TryMap(key, ch, out byte code, ctrl: true));
        Assert.Equal(expected, code);
    }

    [Fact]
    public void TryMap_without_ctrl_is_the_plain_uppercase_letter()
    {
        Assert.True(Apple2KeyMap.TryMap(KeyCode.B, 'b', out byte code, ctrl: false));
        Assert.Equal(0x42, code);   // 'B'
    }

    // The end-to-end latch: a Ctrl+B KeyEvent posted to the real chip latches $02 at $C000 (7-bit code).
    [Fact]
    public void Ctrl_B_latches_02_at_the_keyboard_byte()
    {
        var state = new Apple2VideoState();
        var kbd = new Apple2Keyboard(state);

        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.B, 'b', Ctrl: true));

        Assert.Equal(0x82, state.KeyboardByte);            // strobe (bit7) + $02
        Assert.Equal(0x02, state.KeyboardByte & 0x7F);     // the 7-bit control code (not $42)
    }

    [Fact]
    public void Ctrl_C_latches_03_at_the_keyboard_byte()
    {
        var state = new Apple2VideoState();
        var kbd = new Apple2Keyboard(state);

        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.C, 'c', Ctrl: true));

        Assert.Equal(0x03, state.KeyboardByte & 0x7F);
    }

    [Fact]
    public void Plain_B_without_ctrl_still_latches_42()
    {
        var state = new Apple2VideoState();
        var kbd = new Apple2Keyboard(state);

        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.B, 'b'));   // Ctrl defaults false

        Assert.Equal(0x42, state.KeyboardByte & 0x7F);
    }
}
