using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2SpeakerTests
{
    private static (Apple2Speaker spk, Apple2Iou iou, Apple2VideoState state) Build()
    {
        var state = new Apple2VideoState();
        return (new Apple2Speaker(state), new Apple2Iou(state), state);
    }

    [Fact]
    public void No_toggles_renders_a_constant_waveform()
    {
        var (spk, _, _) = Build();
        var pcm = new short[spk.SamplesPerFrame];
        spk.RenderAudio(pcm);
        // Steady level (no toggles) => every sample is the same value (a flat line, no square wave).
        short first = pcm[0];
        Assert.All(pcm.ToArray(), s => Assert.Equal(first, s));
    }

    [Fact]
    public void Toggling_C030_within_a_frame_produces_both_polarities()
    {
        var (spk, iou, _) = Build();
        // Three $C030 accesses across the frame => the flip-flop visits both 0 and 1.
        iou.Read(0x30, AccessWidth.Byte);
        iou.Read(0x30, AccessWidth.Byte);
        iou.Read(0x30, AccessWidth.Byte);

        var pcm = new short[spk.SamplesPerFrame];
        spk.RenderAudio(pcm);

        bool anyHigh = false, anyLow = false;
        foreach (short s in pcm) { if (s > 0) anyHigh = true; if (s < 0) anyLow = true; }
        Assert.True(anyHigh, "expected some positive (speaker-high) samples");
        Assert.True(anyLow, "expected some negative (speaker-low) samples");
    }

    [Fact]
    public void The_level_carries_into_the_next_frame()
    {
        var (spk, iou, _) = Build();
        iou.Read(0x30, AccessWidth.Byte);          // one toggle: level flips to high and STAYS
        var first = new short[spk.SamplesPerFrame];
        spk.RenderAudio(first);                     // consumes the toggle; ends high

        var second = new short[spk.SamplesPerFrame];
        spk.RenderAudio(second);                    // no new toggles => steady HIGH this frame
        Assert.All(second.ToArray(), s => Assert.True(s > 0));
    }

    [Fact]
    public void RenderAudio_rejects_a_too_small_span()
    {
        var (spk, _, _) = Build();
        Assert.Throws<ArgumentException>(() => spk.RenderAudio(new short[4]));
    }

    [Fact]
    public void Sink_reports_the_host_audio_shape()
    {
        var (spk, _, _) = Build();
        Assert.Equal(44100, spk.SampleRate);
        Assert.Equal(1, spk.ChannelCount);
        Assert.Equal(44100 / 60, spk.SamplesPerFrame);   // 735
    }
}
