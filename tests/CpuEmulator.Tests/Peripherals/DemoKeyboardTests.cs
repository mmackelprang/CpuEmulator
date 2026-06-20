using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class DemoKeyboardTests
{
    [Fact]
    public void PostKey_down_with_char_enqueues_a_byte_readable_at_DATA()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'A'));

        Assert.Equal(0x01u, kbd.Read(1, AccessWidth.Byte) & 0x01); // STATUS: key-ready
        Assert.Equal((uint)'A', kbd.Read(0, AccessWidth.Byte));    // DATA: dequeue
        Assert.Equal(0x00u, kbd.Read(1, AccessWidth.Byte) & 0x01); // now empty
    }

    [Fact]
    public void Keys_dequeue_FIFO_in_order()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.H, 'H'));
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.I, 'I'));

        Assert.Equal((uint)'H', kbd.Read(0, AccessWidth.Byte));
        Assert.Equal((uint)'I', kbd.Read(0, AccessWidth.Byte));
    }

    [Fact]
    public void Key_up_events_are_ignored()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Up, KeyCode.A, 'A'));
        Assert.Equal(0x00u, kbd.Read(1, AccessWidth.Byte) & 0x01);
    }

    [Fact]
    public void Events_without_a_char_are_ignored()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.None, null));
        Assert.Equal(0x00u, kbd.Read(1, AccessWidth.Byte) & 0x01);
    }

    [Fact]
    public void Empty_DATA_read_returns_zero()
    {
        var kbd = new DemoKeyboard();
        Assert.Equal(0x00u, kbd.Read(0, AccessWidth.Byte));
    }

    [Fact]
    public void TryPeek_does_not_dequeue()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Z, 'Z'));

        Assert.True(kbd.TryPeek(0, out byte head));
        Assert.Equal((byte)'Z', head);
        Assert.Equal((uint)'Z', kbd.Read(0, AccessWidth.Byte)); // still there to dequeue
    }
}
