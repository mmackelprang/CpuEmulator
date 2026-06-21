using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2KeyboardTests
{
    // The keyboard chip + the IOU share ONE Apple2VideoState (ADR 0014 Decision 3). PostKey drives the
    // latch the IOU reads at $C000 — so we assert through the IOU, exactly as the guest would.
    private static (Apple2Keyboard kbd, Apple2Iou iou, Apple2VideoState state) Build()
    {
        var state = new Apple2VideoState();
        return (new Apple2Keyboard(state), new Apple2Iou(state), state);
    }

    [Fact]
    public void PostKey_lowercase_a_latches_uppercase_with_the_strobe_at_C000()
    {
        var (kbd, iou, _) = Build();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a'));   // host typed lowercase 'a'
        // $C000 read: bit7 strobe set + uppercase $41 => $C1.
        Assert.Equal(0xC1u, iou.Read(0x00, AccessWidth.Byte));
    }

    [Fact]
    public void C010_clears_the_strobe_but_keeps_the_code()
    {
        var (kbd, iou, _) = Build();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Z, 'z'));
        Assert.Equal(0xDAu, iou.Read(0x00, AccessWidth.Byte));       // strobe + $5A
        iou.Read(0x10, AccessWidth.Byte);                            // $C010: clear strobe
        Assert.Equal(0x5Au, iou.Read(0x00, AccessWidth.Byte) & 0xFF); // strobe gone, $5A retained
    }

    [Fact]
    public void Key_up_is_a_no_op_the_latch_holds_the_last_key()
    {
        var (kbd, iou, _) = Build();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a'));
        kbd.PostKey(new KeyEvent(KeyAction.Up, KeyCode.A, null));    // release: the ][+ latch is unchanged
        Assert.Equal(0xC1u, iou.Read(0x00, AccessWidth.Byte));       // still $C1 (strobe + 'A')
    }

    [Fact]
    public void An_unmapped_key_does_not_disturb_the_latch()
    {
        var (kbd, iou, state) = Build();
        state.LatchKey(0x42);                                        // 'B' already waiting
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Tab, null)); // no ][+ Tab code -> no-op
        Assert.Equal(0xC2u, iou.Read(0x00, AccessWidth.Byte));       // strobe + $42 unchanged
    }
}
