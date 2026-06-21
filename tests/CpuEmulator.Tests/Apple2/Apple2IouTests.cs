using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2IouTests
{
    private static (Apple2Iou iou, Apple2VideoState state) Build()
    {
        var state = new Apple2VideoState();
        return (new Apple2Iou(state), state);
    }

    [Fact]
    public void A_READ_of_C057_HIRES_turns_hires_on()
    {
        var (iou, state) = Build();
        Assert.False(state.HiRes);
        iou.Read(0x57, AccessWidth.Byte);   // offset 0x57 == $C057 HIRES, READ
        Assert.True(state.HiRes);
    }

    [Fact]
    public void A_WRITE_of_C056_LORES_turns_hires_off_identically()
    {
        var (iou, state) = Build();
        iou.Read(0x57, AccessWidth.Byte);   // HIRES on
        Assert.True(state.HiRes);
        iou.Write(0x56, AccessWidth.Byte, 0x00); // $C056 LORES, WRITE — same any-access toggle
        Assert.False(state.HiRes);
    }

    [Theory]
    [InlineData(0x50, nameof(Apple2VideoState.GraphicsOn), true)]   // TXTCLR -> graphics on
    [InlineData(0x51, nameof(Apple2VideoState.GraphicsOn), false)]  // TXTSET -> text
    [InlineData(0x52, nameof(Apple2VideoState.Mixed), false)]       // MIXCLR -> full
    [InlineData(0x53, nameof(Apple2VideoState.Mixed), true)]        // MIXSET -> mixed
    [InlineData(0x54, nameof(Apple2VideoState.Page2), false)]       // LOWSCR -> page1
    [InlineData(0x55, nameof(Apple2VideoState.Page2), true)]        // HISCR -> page2
    [InlineData(0x56, nameof(Apple2VideoState.HiRes), false)]       // LORES
    [InlineData(0x57, nameof(Apple2VideoState.HiRes), true)]        // HIRES
    public void Every_video_switch_sets_its_flag_on_any_access(int offset, string flag, bool expected)
    {
        var (iou, state) = Build();
        // Seed the opposite so the assertion is meaningful for the "false" cases.
        if (!expected)
            iou.Read((uint)(offset ^ 1), AccessWidth.Byte); // the paired ON switch first
        iou.Read((uint)offset, AccessWidth.Byte);
        bool actual = flag switch
        {
            nameof(Apple2VideoState.GraphicsOn) => state.GraphicsOn,
            nameof(Apple2VideoState.Mixed) => state.Mixed,
            nameof(Apple2VideoState.Page2) => state.Page2,
            nameof(Apple2VideoState.HiRes) => state.HiRes,
            _ => throw new ArgumentOutOfRangeException(nameof(flag)),
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryPeek_of_a_video_switch_has_NO_side_effect()
    {
        var (iou, state) = Build();
        Assert.False(state.HiRes);
        bool ok = iou.TryPeek(0x57, out _);   // the debugger looks at $C057
        Assert.True(ok);
        Assert.False(state.HiRes);            // ... and HIRES stays OFF (peek-free)
    }

    [Fact]
    public void C000_read_returns_the_latched_key_and_C010_clears_the_strobe()
    {
        var (iou, state) = Build();
        state.LatchKey(0x41);                            // host pushed 'A'
        uint c000 = iou.Read(0x00, AccessWidth.Byte);   // $C000
        Assert.Equal(0xC1u, c000);                       // bit7 strobe + 0x41

        iou.Read(0x10, AccessWidth.Byte);                // $C010 clears strobe (any access)
        Assert.Equal(0x41u, iou.Read(0x00, AccessWidth.Byte) & 0xFF); // strobe gone, code retained
    }

    [Fact]
    public void A_single_C030_access_toggles_the_speaker_once()
    {
        var (iou, state) = Build();
        iou.Read(0x30, AccessWidth.Byte);   // one access (e.g. LDA $C030)
        Assert.Equal(1, state.SpeakerToggles);
    }

    [Fact]
    public void A_write_opcodes_read_before_write_double_toggles_the_speaker()
    {
        // A 6502 STA $C030 issues a dummy READ then the WRITE at the bus — TWO accesses. Modelled at
        // the bus-access level, that is two ToggleSpeaker() calls. We simulate the bus pattern here
        // (the real core issues it; the cross-check against the live Mos6502Cpu is Task 5).
        var (iou, state) = Build();
        iou.Read(0x30, AccessWidth.Byte);                // the dummy read
        iou.Write(0x30, AccessWidth.Byte, 0x00);         // the store
        Assert.Equal(2, state.SpeakerToggles);           // double toggle, as on real hardware
    }
}
