using CpuEmulator.Core;

namespace CpuEmulator.Tests.Surface;

public class AudioSinkContractTests
{
    /// <summary>A synthetic audio source: a fixed-amplitude square wave, one channel, fires AudioReady
    /// when Pulse() is called (the test's stand-in for a scheduler audio tick).</summary>
    private sealed class SquareWaveAudio : IAudioSink
    {
        private bool _high = true;
        public int SampleRate => 44100;
        public int ChannelCount => 1;
        public int SamplesPerFrame => 882; // 44100 / 50 Hz
        public event Action? AudioReady;

        public void Pulse() => AudioReady?.Invoke();

        public void RenderAudio(Span<short> samples)
        {
            if (samples.Length < SamplesPerFrame)
                throw new ArgumentException($"need {SamplesPerFrame} samples; got {samples.Length}.", nameof(samples));
            short v = _high ? (short)8000 : (short)-8000;
            for (int i = 0; i < SamplesPerFrame; i++)
                samples[i] = v;
            _high = !_high;
        }
    }

    [Fact]
    public void Render_fills_the_frame_with_the_expected_amplitude()
    {
        var src = new SquareWaveAudio();
        var buf = new short[src.SamplesPerFrame];
        src.RenderAudio(buf);
        Assert.All(buf.ToArray(), s => Assert.Equal(8000, s));
    }

    [Fact]
    public void AudioReady_is_observable()
    {
        var src = new SquareWaveAudio();
        bool fired = false;
        src.AudioReady += () => fired = true;
        src.Pulse();
        Assert.True(fired);
    }
}
