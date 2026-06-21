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

    // A 12 KiB system ROM whose reset vector points into a NOP loop (the Apple2BoardTests shape).
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x0000] = 0xEA;                                              // NOP at $D000
        rom[0x0001] = 0x4C; rom[0x0002] = 0x00; rom[0x0003] = 0xD0;      // JMP $D000
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;                          // reset -> $D000
        return rom;
    }

    [Fact]
    public void A_real_STA_C030_loop_makes_the_speaker_render_a_square_wave()
    {
        // Build a real ][+ board; the speaker shares the board's Apple2VideoState (the IOU writes it).
        var state = new Apple2VideoState();
        var iou = new CpuEmulator.Peripherals.Apple2Iou(state);
        var speaker = new Apple2Speaker(state);
        var spec = CpuEmulator.Machines.Apple2Board.Spec(SystemRom(), iou);
        var machine = CpuEmulator.Machines.BoardMachineFactory.Build(spec);   // interpreter tier (the oracle)
        var bus = machine.Space(AddressSpaceKind.Program);

        // $0300: LDA $C030 ; JMP $0300  (LDA = one bus access = one toggle per loop; tight + cheap)
        bus.Write8(0x0300, 0xAD); bus.Write8(0x0301, 0x30); bus.Write8(0x0302, 0xC0); // LDA $C030
        bus.Write8(0x0303, 0x4C); bus.Write8(0x0304, 0x00); bus.Write8(0x0305, 0x03); // JMP $0300
        machine.Cpu.SetRegister("PC", 0x0300);

        long before = state.SpeakerToggles;
        machine.Run(2000);                 // many LDA/JMP iterations -> many $C030 accesses
        Assert.True(state.SpeakerToggles > before + 10,
            $"expected the loop to toggle the speaker many times; got {state.SpeakerToggles - before}");

        var pcm = new short[speaker.SamplesPerFrame];
        speaker.RenderAudio(pcm);
        bool anyHigh = false, anyLow = false;
        foreach (short s in pcm) { if (s > 0) anyHigh = true; if (s < 0) anyLow = true; }
        Assert.True(anyHigh && anyLow,
            "a real STA/LDA $C030 loop on the interpreter must render a non-flat (both-polarity) frame");
    }
}
