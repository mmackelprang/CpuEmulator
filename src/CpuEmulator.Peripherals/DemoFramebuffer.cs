using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// The SP0 demo display: a 256×192, 8-bits-per-pixel palettized linear framebuffer. One byte of
/// VRAM per pixel, row-major; <see cref="RenderInto"/> looks each byte up in a fixed 256-entry
/// palette to produce RGBA8888 (so the surface is a dumb blitter — see <see cref="IDisplayDevice"/>).
/// The palette is a deterministic 256-level grayscale ramp (A=0xFF, R=G=B=index), letting tests
/// assert exact pixels without a palette file; a real machine supplies its own palette behind the
/// same contract. <see cref="FrameReady"/> fires on a scheduler-driven 60 Hz vblank tick (claimed
/// in <see cref="Realize"/>); VRAM reads/writes are memory-mapped (<see cref="IPeripheral"/>).
/// </summary>
public sealed class DemoFramebuffer : IPeripheral, IDisplayDevice
{
    private const int WidthPx = 256;
    private const int HeightPx = 192;
    private const int VramLength = WidthPx * HeightPx; // 49,152 bytes, one per pixel

    // 60 frames/sec at the demo's nominal 1 MHz 6502 clock = one vblank every 16,667 cycles.
    private const long VblankIntervalCycles = 16_667;

    private readonly byte[] _vram = new byte[VramLength];
    private static readonly uint[] Palette = BuildGrayscalePalette();

    public string Name => "framebuffer";
    public int Width => WidthPx;
    public int Height => HeightPx;
    public event Action? FrameReady;

    /// <summary>Schedule the recurring vblank tick that raises <see cref="FrameReady"/>.</summary>
    public void Realize(IMachineContext context) =>
        context.Scheduler.ScheduleEvery(VblankIntervalCycles, () => FrameReady?.Invoke());

    public uint Read(uint offset, AccessWidth width) =>
        offset < VramLength ? _vram[offset] : 0x00u;

    public void Write(uint offset, AccessWidth width, uint value)
    {
        if (offset < VramLength)
            _vram[offset] = unchecked((byte)value);
    }

    public bool TryPeek(uint offset, out byte value)
    {
        value = offset < VramLength ? _vram[offset] : (byte)0x00;
        return true;
    }

    public void RenderInto(Span<uint> rgba)
    {
        if (rgba.Length < VramLength)
            throw new ArgumentException(
                $"Destination needs {VramLength} pixels; got {rgba.Length}.", nameof(rgba));
        for (int i = 0; i < VramLength; i++)
            rgba[i] = Palette[_vram[i]];
    }

    private static uint[] BuildGrayscalePalette()
    {
        var p = new uint[256];
        for (int i = 0; i < 256; i++)
            p[i] = 0xFF000000u | (uint)(i << 16) | (uint)(i << 8) | (uint)i; // 0xFFrrggbb, r=g=b=i
        return p;
    }
}
