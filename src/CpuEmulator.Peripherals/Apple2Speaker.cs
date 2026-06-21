using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ 1-bit speaker (ADR 0014 Decision 3): a host-facing IAudioSink that resamples
/// the $C030 toggle stream into S16 PCM, reusing the SpectrumUla beeper sink approach (1-bit DAC: each
/// toggle flips the level; level 1 -> +amp, level 0 -> -amp; the ending level carries into the next
/// frame). The IOU increments Apple2VideoState.SpeakerToggles on every $C030 bus access — so an RMW
/// opcode (INC $C030) double-toggles via its phantom read + store, while STA/LDA $C030 each produce one
/// toggle per instruction (the 6502 STA is a pure write, no dummy read). This chip reads that
/// monotonic count, derives how many NEW toggles happened this frame, spreads them evenly across the
/// frame (the IOU exposes a count, not timestamps — the same pragmatic approximation the Spectrum
/// beeper uses; only relative spacing + the carried level matter for an audible square wave), and emits
/// the frame. It owns no bus page (the IOU owns $C030); it is IPeripheral only to schedule the ~60 Hz
/// AudioReady tick in Realize (the SpectrumUla precedent). One shared Apple2VideoState, no duplication.</summary>
public sealed class Apple2Speaker : IPeripheral, IAudioSink
{
    private const int HostSampleRate = 44100;
    private const int FrameRate = 60;                          // the ][+ present cadence (PR-C)
    private const int SamplesFrame = HostSampleRate / FrameRate; // 735
    private const long CyclesPerFrame = 17030;                 // ~1.0205 MHz / 60 Hz (matches Apple2Video)
    private const short Amp = 12000;

    private readonly Apple2VideoState _state;
    private long _lastConsumed;   // SpeakerToggles value at the end of the previous frame
    private int _level;           // the current flip-flop level (0/1), carried across frames

    public Apple2Speaker(Apple2VideoState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _lastConsumed = state.SpeakerToggles;
    }

    public string Name => "apple2speaker";
    public int SampleRate => HostSampleRate;
    public int ChannelCount => 1;
    public int SamplesPerFrame => SamplesFrame;
    public event Action? AudioReady;

    public void Realize(IMachineContext context)
    {
        // Schedule the per-frame audio pull tick; no IRQ on the bare ][+ (the SpectrumUla precedent).
        context.Scheduler.ScheduleEvery(CyclesPerFrame, () => AudioReady?.Invoke());
    }

    // The chip maps no page; these are unreachable but must satisfy IPeripheral.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    /// <summary>Test-only: stand in for the scheduler tick so a unit test can assert AudioReady without
    /// building a full Machine.</summary>
    internal void RaiseAudioForTest() => AudioReady?.Invoke();

    // ── IAudioSink: reconstruct the 1-bit waveform from this frame's toggles into S16 PCM. ──
    public void RenderAudio(Span<short> samples)
    {
        if (samples.Length < SamplesFrame)
            throw new ArgumentException($"need {SamplesFrame} samples; got {samples.Length}.", nameof(samples));

        long now = _state.SpeakerToggles;
        long newToggles = now - _lastConsumed;   // toggles since the previous frame
        _lastConsumed = now;

        if (newToggles <= 0)
        {
            // Steady level all frame (a flat line — no square wave).
            short flat = _level != 0 ? Amp : (short)-Amp;
            for (int s = 0; s < SamplesFrame; s++)
                samples[s] = flat;
            return;
        }

        // Spread the toggles evenly across the frame: toggle k (0-based) lands at sample
        // floor((k + 1) * SamplesFrame / (newToggles + 1)). Walk samples, flipping the level as each
        // toggle boundary is crossed. (Relative spacing + the carried level are what matter audibly.)
        long nextToggle = 0;   // long: a saturated audio thread can accumulate > int.MaxValue toggles
        for (int s = 0; s < SamplesFrame; s++)
        {
            while (nextToggle < newToggles &&
                   s >= (int)((nextToggle + 1L) * SamplesFrame / (newToggles + 1)))
            {
                _level ^= 1;   // flip the 1-bit flip-flop
                nextToggle++;
            }
            samples[s] = _level != 0 ? Amp : (short)-Amp;
        }
    }
}
