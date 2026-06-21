namespace CpuEmulator.Core;

/// <summary>A display device that delegates to whichever underlying <see cref="IDisplayDevice"/> is
/// currently ACTIVE (ADR 0016 Decision 1). The surface pulls from this as an ordinary IDisplayDevice
/// (unchanged <c>MachineHost</c> apart from its per-frame buffer re-size); the active source is selected
/// by guest state — e.g. the Videx being the live terminal — via <see cref="SetActive"/>, so the user
/// sees what the guest drives, not a UI toggle. <see cref="Width"/>/<see cref="Height"/>/<see
/// cref="RenderInto"/> delegate to the active source; <see cref="FrameReady"/> forwards the ACTIVE
/// source's FrameReady AND fires on a <see cref="SetActive"/> switch (so the surface re-pulls — and
/// re-sizes — at the new geometry, e.g. 280x192 Apple hi-res vs a wider Videx 80x24 raster). A dormant
/// source's FrameReady is dropped (the host only ever pulls the active source; rendering a dormant
/// source's frame would write the wrong geometry). With one source the multiplexer is transparent.</summary>
public sealed class DisplayMultiplexer : IDisplayDevice
{
    private readonly IReadOnlyList<IDisplayDevice> _sources;
    private int _active;

    public DisplayMultiplexer(IReadOnlyList<IDisplayDevice> sources, int initialActive = 0)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            throw new ArgumentException("At least one display source is required.", nameof(sources));
        if (initialActive < 0 || initialActive >= sources.Count)
            throw new ArgumentOutOfRangeException(nameof(initialActive));
        _sources = sources;
        _active = initialActive;

        // Subscribe every source: a source raises its own vblank, but only the ACTIVE source's frames
        // are forwarded (the host only pulls the active source). Capturing the index keeps the check O(1).
        for (int i = 0; i < sources.Count; i++)
        {
            int index = i;
            sources[i].FrameReady += () => { if (index == _active) FrameReady?.Invoke(); };
        }
    }

    /// <summary>Select which source is live (called by the guest-driven active-display signal — PR-N's
    /// Videx drives it from its $C800-enable state). On an ACTUAL change, raises <see cref="FrameReady"/>
    /// so the surface re-pulls at the new source's geometry (the MachineHost re-size). A no-op (and no
    /// FrameReady) when the index is unchanged.</summary>
    public void SetActive(int index)
    {
        if (index < 0 || index >= _sources.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _active)
            return;
        _active = index;
        FrameReady?.Invoke();   // the source changed: the host re-pulls + re-sizes at the new geometry
    }

    /// <summary>The current active source index.</summary>
    public int ActiveIndex => _active;

    public int Width => _sources[_active].Width;
    public int Height => _sources[_active].Height;
    public void RenderInto(Span<uint> rgba) => _sources[_active].RenderInto(rgba);
    public event Action? FrameReady;
}
