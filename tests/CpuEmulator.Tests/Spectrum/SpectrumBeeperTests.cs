using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumBeeperTests
{
    private static SpectrumUla BareUla()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x4000, new byte[0xC000], writable: true);
        return new SpectrumUla(space);
    }

    [Fact]
    public void A_steady_low_beeper_renders_a_constant_negative_waveform()
    {
        var ula = BareUla();
        // No OUT writes → steady level 0 → all samples negative.
        var pcm = new short[ula.SamplesPerFrame];
        ula.RenderAudio(pcm);
        Assert.All(pcm.ToArray(), s => Assert.True(s < 0));
    }

    [Fact]
    public void Toggling_bit4_high_then_low_produces_both_polarities_in_the_frame()
    {
        var ula = BareUla();
        // OUT (0xFE),0x10 → beeper level 1 (high). Logged near frame start.
        ula.Write(0xFEu, AccessWidth.Byte, 0x10);
        // ... (more toggles to spread across the frame) ...
        ula.Write(0xFEu, AccessWidth.Byte, 0x00); // back to low
        ula.Write(0xFEu, AccessWidth.Byte, 0x10); // high again

        var pcm = new short[ula.SamplesPerFrame];
        ula.RenderAudio(pcm);

        bool anyHigh = false, anyLow = false;
        foreach (short s in pcm) { if (s > 0) anyHigh = true; if (s < 0) anyLow = true; }
        Assert.True(anyHigh, "expected some positive (beeper-high) samples");
        Assert.True(anyLow, "expected some negative (beeper-low) samples");
    }

    [Fact]
    public void The_log_resets_between_frames_so_the_steady_level_carries()
    {
        var ula = BareUla();
        ula.Write(0xFEu, AccessWidth.Byte, 0x10); // go high
        var first = new short[ula.SamplesPerFrame];
        ula.RenderAudio(first); // consumes the log; final level high carries

        var second = new short[ula.SamplesPerFrame];
        ula.RenderAudio(second); // no new toggles → steady HIGH this frame
        Assert.All(second.ToArray(), s => Assert.True(s > 0));
    }

    [Fact]
    public void Border_bits_do_not_affect_the_beeper_level()
    {
        var ula = BareUla();
        ula.Write(0xFEu, AccessWidth.Byte, 0x07); // border white (bits 0-2), beeper bit4=0
        var pcm = new short[ula.SamplesPerFrame];
        ula.RenderAudio(pcm);
        Assert.All(pcm.ToArray(), s => Assert.True(s < 0)); // beeper still low
    }
}
