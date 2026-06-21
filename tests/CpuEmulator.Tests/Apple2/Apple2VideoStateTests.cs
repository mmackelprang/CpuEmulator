using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2VideoStateTests
{
    [Fact]
    public void Defaults_are_text_page1_full_lores()
    {
        var s = new Apple2VideoState();
        Assert.False(s.GraphicsOn);   // TXTSET default (text)
        Assert.False(s.Mixed);
        Assert.False(s.Page2);
        Assert.False(s.HiRes);
    }

    [Fact]
    public void Mode_flags_round_trip()
    {
        var s = new Apple2VideoState
        {
            GraphicsOn = true,
            Mixed = true,
            Page2 = true,
            HiRes = true,
        };
        Assert.True(s.GraphicsOn && s.Mixed && s.Page2 && s.HiRes);
    }

    [Fact]
    public void Keyboard_latch_holds_code_with_strobe_bit_and_clears()
    {
        var s = new Apple2VideoState();
        s.LatchKey(0x41);                       // 'A'
        Assert.Equal(0xC1, s.KeyboardByte);     // bit7 strobe set + 0x41
        s.ClearStrobe();
        Assert.Equal(0x41, s.KeyboardByte);     // strobe cleared, code retained
    }

    [Fact]
    public void Speaker_toggle_count_increments_per_access()
    {
        var s = new Apple2VideoState();
        Assert.Equal(0, s.SpeakerToggles);
        s.ToggleSpeaker();
        s.ToggleSpeaker();
        Assert.Equal(2, s.SpeakerToggles);
    }
}
