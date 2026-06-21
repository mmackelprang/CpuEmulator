namespace CpuEmulator.Surface.Web;

/// <summary>Pushes the <c>ST</c> status frame to a sink only when the machine's status snapshot changes
/// (design D14 — the surface is a dumb reflector of REAL state, pushed on change). Reads the snapshot via
/// a provider each <see cref="Tick"/>, encodes it, and compares the encoded bytes to the last sent frame
/// (equal snapshots -> equal bytes, by FrameCodec.EncodeStatus's deterministic JSON). The first Tick
/// always pushes (the initial state). Kept separate from MachineHost: the host owns FB/AU pixels/audio;
/// status is a session-level overlay on the pump's tick.</summary>
public sealed class StatusPusher
{
    private readonly Func<MachineStatus> _provider;
    private readonly Action<byte[]> _sink;
    private byte[]? _last;

    public StatusPusher(Func<MachineStatus> provider, Action<byte[]> sink)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sink);
        _provider = provider;
        _sink = sink;
    }

    /// <summary>Snapshot the live status; push it only if its encoded bytes differ from the last sent
    /// frame (or this is the first push).</summary>
    public void Tick()
    {
        byte[] frame = FrameCodec.EncodeStatus(_provider());
        if (_last is not null && _last.AsSpan().SequenceEqual(frame))
            return;
        _last = frame;
        _sink(frame);
    }
}
