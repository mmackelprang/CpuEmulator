using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// Drives a <see cref="Machine"/> for a surface (design spec §5). Subscribes the display's
/// <see cref="IDisplayDevice.FrameReady"/>, pulls RGBA via <see cref="IDisplayDevice.RenderInto"/>,
/// encodes a frame (<see cref="FrameCodec"/>), and hands it to a transport-agnostic frame sink.
/// Inbound keys route to <see cref="IKeyboardSink.PostKey"/>. Transport-agnostic on purpose: the
/// WebSocket server (Program.cs) supplies the frame sink and calls <see cref="Step"/> on a
/// wall-clock-paced loop; tests drive <see cref="RunHeadless"/> with no throttle. One machine per
/// host (multi-machine is YAGNI). Frame pushes are coalesced: at most one frame per Step, using the
/// latest RenderInto — so a slow sink never backs up the pump.
/// </summary>
public sealed class MachineHost
{
    private readonly Machine _machine;
    private readonly IDisplayDevice _display;
    private readonly IKeyboardSink _keyboard;
    private readonly Action<byte[]> _frameSink;
    private readonly uint[] _rgba;
    private volatile bool _frameDirty;

    public MachineHost(Machine machine, IDisplayDevice display, IKeyboardSink keyboard,
                       Action<byte[]> frameSink)
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
    }

    /// <summary>Push a key into the machine's keyboard.</summary>
    public void PostKey(in KeyEvent e) => _keyboard.PostKey(e);

    /// <summary>Run one slice of <paramref name="cycles"/>, then — if a vblank fired during it —
    /// render the latest frame and push it to the sink (coalesced: one frame per Step).</summary>
    public void Step(long cycles)
    {
        _machine.Run(cycles);
        if (!_frameDirty)
            return;
        _frameDirty = false;
        _display.RenderInto(_rgba);
        _frameSink(FrameCodec.EncodeFrame(_display.Width, _display.Height, _rgba));
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
}
