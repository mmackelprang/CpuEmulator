using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

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

    [Fact]
    public void MachineHost_pushes_an_audio_frame_when_the_sink_signals_ready()
    {
        // A bare machine with no real devices: drive the audio path directly by pulsing the source.
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        program.MapMemory(0x0000, new byte[0x10000], writable: true);
        program.Write8(0x0000, 0x76); // HALT — the CPU makes no progress demands here

        var fb = new TestDisplay();
        var audio = new SquareWaveAudio();
        byte[]? lastAudio = null;

        Machine machine = Machine.Create("audio-host")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx => new CpuEmulator.Cpus.Z80.Z80Cpu((AddressSpace)ctx.Space(AddressSpaceKind.Program)))
            .Build();

        var host = new MachineHost(machine, fb, new NullKeyboard(),
            frame => { }, audio, a => lastAudio = a);

        audio.Pulse();      // mark an audio frame ready
        host.Step(10);      // the host should drain it
        Assert.NotNull(lastAudio);
        Assert.Equal((byte)'A', lastAudio![0]);
        Assert.Equal((byte)'U', lastAudio![1]);
    }

    private sealed class TestDisplay : IDisplayDevice
    {
        public int Width => 1;
        public int Height => 1;
        public event Action? FrameReady { add { } remove { } }
        public void RenderInto(Span<uint> rgba) => rgba[0] = 0xFF000000u;
    }

    private sealed class NullKeyboard : IKeyboardSink
    {
        public void PostKey(in KeyEvent e) { }
    }
}
