using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// Drives a <see cref="Machine"/> for a surface (design spec §5). Subscribes the display's
/// <see cref="IDisplayDevice.FrameReady"/>, pulls RGBA via <see cref="IDisplayDevice.RenderInto"/>,
/// encodes a frame (<see cref="FrameCodec"/>), and hands it to a transport-agnostic frame sink.
/// OPTIONALLY does the same for audio: subscribes <see cref="IAudioSink.AudioReady"/>, pulls S16 via
/// <see cref="IAudioSink.RenderAudio"/>, encodes an AU frame, and hands it to an audio sink. Inbound
/// keys route to <see cref="IKeyboardSink.PostKey"/>. Frame/audio pushes are coalesced: at most one of
/// each per Step, using the latest render.
/// </summary>
public sealed class MachineHost
{
    private readonly Machine _machine;
    private readonly IDisplayDevice _display;
    private readonly IKeyboardSink _keyboard;
    private readonly Action<byte[]> _frameSink;
    private uint[] _rgba;   // re-sized when the active display source's dimensions change (ADR 0016 Decision 1)
    private volatile bool _frameDirty;

    private readonly IAudioSink? _audio;
    private readonly Action<byte[]>? _audioSink;
    private readonly short[]? _pcm;
    private volatile bool _audioDirty;

    public MachineHost(Machine machine, IDisplayDevice display, IKeyboardSink keyboard,
                       Action<byte[]> frameSink)
        : this(machine, display, keyboard, frameSink, null, null) { }

    public MachineHost(Machine machine, IDisplayDevice display, IKeyboardSink keyboard,
                       Action<byte[]> frameSink, IAudioSink? audio, Action<byte[]>? audioSink)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(frameSink);
        _machine = machine;
        _display = display;
        _keyboard = keyboard;
        _frameSink = frameSink;
        _rgba = new uint[display.Width * display.Height];
        _display.FrameReady += () => _frameDirty = true;

        _audio = audio;
        _audioSink = audioSink;
        if (audio is not null && audioSink is not null)
        {
            _pcm = new short[audio.SamplesPerFrame * audio.ChannelCount];
            audio.AudioReady += () => _audioDirty = true;
        }
    }

    /// <summary>Push a key into the machine's keyboard.</summary>
    public void PostKey(in KeyEvent e) => _keyboard.PostKey(e);

    /// <summary>Run one slice of <paramref name="cycles"/>, then — if a vblank / audio tick fired during
    /// it — render + push the latest frame and PCM buffer (coalesced: one of each per Step).</summary>
    public void Step(long cycles)
    {
        _machine.Run(cycles);

        if (_frameDirty)
        {
            _frameDirty = false;
            EnsureFrameBuffer();                       // follow the active source's geometry (re-size on change)
            _display.RenderInto(_rgba);
            _frameSink(FrameCodec.EncodeFrame(_display.Width, _display.Height, _rgba));
        }

        if (_audioDirty && _audio is not null && _audioSink is not null && _pcm is not null)
        {
            _audioDirty = false;
            _audio.RenderAudio(_pcm);
            _audioSink(FrameCodec.EncodeAudio(_audio.SampleRate, _audio.ChannelCount, _pcm));
        }
    }

    /// <summary>Headless/fast run (no wall-clock throttle): step in <paramref name="sliceCycles"/>
    /// chunks until <paramref name="totalCycles"/> is spent. For tests + batch.</summary>
    public void RunHeadless(long totalCycles, long sliceCycles)
    {
        if (sliceCycles <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceCycles), "Slice must be positive.");
        for (long run = 0; run < totalCycles; run += sliceCycles)
            Step(Math.Min(sliceCycles, totalCycles - run));
    }

    /// <summary>Re-size the RGBA frame buffer to the active display's current geometry if it changed
    /// (ADR 0016 Decision 1). A no-op for every single-display board (the dimensions never change), so
    /// the single-source path is byte-for-byte unchanged; a one-time reallocation on the rare active-
    /// source switch (e.g. 40-col Apple -> 80-col Videx behind a DisplayMultiplexer). The wire frame's
    /// width/height come from _display.Width/_display.Height per frame (FrameCodec.EncodeFrame), so the
    /// client re-sizes its canvas automatically — only this host-side buffer needs to follow.</summary>
    private void EnsureFrameBuffer()
    {
        int needed = _display.Width * _display.Height;
        if (_rgba.Length != needed)
            _rgba = new uint[needed];
    }
}
