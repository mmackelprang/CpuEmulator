namespace CpuEmulator.Core;

/// <summary>
/// An audio output a chip exposes to the host, IN ADDITION TO <see cref="IPeripheral"/> (which faces
/// the guest CPU) — the audio analogue of <see cref="IDisplayDevice"/>. The host PULLS a finished PCM
/// frame: the chip writes signed 16-bit samples (S16), interleaved by channel, at its own fixed host
/// <see cref="SampleRate"/> — so the surface is a dumb player that never knows the chip's internal
/// waveform model. The chip raises <see cref="AudioReady"/> once per audio frame, scheduled via
/// <see cref="IScheduler"/> (typically the same vblank cadence as the display).
/// </summary>
public interface IAudioSink
{
    /// <summary>The fixed host sample rate in Hz (e.g. 44100). The chip resamples its internal stream
    /// to this rate inside <see cref="RenderAudio"/>.</summary>
    int SampleRate { get; }

    /// <summary>Channel count (1 = mono — the Spectrum beeper). Samples are interleaved when &gt; 1.</summary>
    int ChannelCount { get; }

    /// <summary>The number of SAMPLES PER CHANNEL one frame produces (= SampleRate / frame rate, e.g.
    /// 44100 / 50 = 882). The host sizes its buffer to <c>SamplesPerFrame * ChannelCount</c>.</summary>
    int SamplesPerFrame { get; }

    /// <summary>Write the finished S16 frame into <paramref name="samples"/>. The destination must hold
    /// at least <see cref="SamplesPerFrame"/> * <see cref="ChannelCount"/> samples; a too-small span
    /// throws <see cref="System.ArgumentException"/>.</summary>
    void RenderAudio(Span<short> samples);

    /// <summary>Raised once per audio frame (scheduler-driven); may have no subscribers.</summary>
    event Action? AudioReady;
}
