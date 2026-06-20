namespace CpuEmulator.Core;

/// <summary>
/// A display output a chip exposes to the host, IN ADDITION TO <see cref="IPeripheral"/>
/// (which faces the guest CPU). The host PULLS final RGBA pixels: the chip writes RGBA8888,
/// row-major, doing any palette/mode lookup itself — so the surface is a dumb blitter that
/// never knows about modes or palettes (this is what lets one surface serve both ANTIC and CGA).
/// The chip raises <see cref="FrameReady"/> at its own vblank, scheduled via
/// <see cref="IScheduler"/> at the real refresh rate.
/// </summary>
public interface IDisplayDevice
{
    /// <summary>Native pixel width; may change with video mode.</summary>
    int Width { get; }

    /// <summary>Native pixel height; may change with video mode.</summary>
    int Height { get; }

    /// <summary>Write the final RGBA8888 frame, row-major, into <paramref name="rgba"/>.
    /// The destination must hold at least <see cref="Width"/> * <see cref="Height"/> pixels;
    /// a too-small span throws <see cref="ArgumentException"/>.</summary>
    void RenderInto(Span<uint> rgba);

    /// <summary>Raised at the chip's vblank (scheduler-driven), signalling a complete frame.</summary>
    event Action FrameReady;
}
