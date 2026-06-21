using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ video chip (ADR 0014 Decision 3): a host-facing IDisplayDevice that reads
/// LIVE main RAM for scanout (no VRAM — the SpectrumUla pattern) and renders the current mode (driven
/// by the shared Apple2VideoState the IOU writes) into RGBA. It is an IPeripheral only to receive
/// Realize (bind the program bus + schedule the ~60 Hz present tick); it maps no page (the IOU owns
/// $C000), so its Read/Write are never reached. The bare ][+ raises NO interrupt — the tick is the
/// host-present trigger only. Ships correct mono + basic-artifact hi-res, the lo-res 16-colour palette,
/// and a built-in fallback font (the real char-gen ROM is injected in PR-H). Timing tier: Coarse.</summary>
public sealed class Apple2Video : IPeripheral, IDisplayDevice
{
    public const int Width280 = 280;
    public const int Height192 = 192;

    private const long CyclesPerFrame = 17030; // ~1.0205 MHz / 60 Hz (the present cadence; Coarse)

    private IAddressSpace _ram;     // the program bus; (re)bound authoritatively in Realize
    private readonly Apple2VideoState _state;
    private readonly byte[] _charRom;   // 256x8; the fallback font unless a real ROM is injected

    public string Name => "apple2video";
    public int Width => Width280;
    public int Height => Height192;
    public event Action? FrameReady;

    /// <summary>A read-only human label of the current video mode, derived from the SAME live
    /// <see cref="Apple2VideoState"/> flags the renderer reads (design D1 / interactions §1.1). Mixed
    /// takes precedence in the label (it is the visible-on-screen split). The host reads this for the
    /// <c>ST</c> status frame; it is never a control.</summary>
    public string ModeLabel
    {
        get
        {
            string page = _state.Page2 ? "page 2" : "page 1";
            if (!_state.GraphicsOn)
                return $"TEXT · 40×24 · {page}";
            if (_state.Mixed)
                return $"MIXED · text+gfx · {page}";
            return _state.HiRes
                ? $"HIRES · 280×192 · {page}"
                : $"LORES · 40×48 · {page}";
        }
    }

    /// <param name="ram">The program bus the chip reads $0400/$2000 etc. live from BEFORE Realize (the
    /// unit-test path passes a built space and renders without a Machine). When wired into a Machine,
    /// Realize re-binds this to the live program space — so a board peripheral need not pre-bind it.</param>
    /// <param name="state">The shared mode/page state the IOU writes.</param>
    /// <param name="charRom">Optional 256x8 char-gen ROM; null uses the built-in fallback font.</param>
    public Apple2Video(IAddressSpace ram, Apple2VideoState state, byte[]? charRom = null)
    {
        ArgumentNullException.ThrowIfNull(ram);
        ArgumentNullException.ThrowIfNull(state);
        _ram = ram;
        _state = state;
        _charRom = charRom ?? Apple2Font.Fallback;
        if (_charRom.Length != 256 * 8)
            throw new ArgumentException("char ROM must be 256x8 = 2048 bytes.", nameof(charRom));
    }

    public void Realize(IMachineContext context)
    {
        // Bind the LIVE program bus (the SpectrumUla precedent): when a Machine realizes this chip as a
        // board peripheral (PR-H), Realize authoritatively re-points _ram at the machine's program space,
        // overriding whatever space was supplied at construction (a test stub in the unit gates). Then
        // schedule the present tick only — no IRQ on the bare ][+ (IrqWiring.None).
        _ram = context.Space(AddressSpaceKind.Program);
        context.Scheduler.ScheduleEvery(CyclesPerFrame, () => FrameReady?.Invoke());
    }

    // The chip maps no page; these are unreachable but must satisfy IPeripheral.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    /// <summary>Test-only: stand in for the scheduler tick so a unit test can assert FrameReady without
    /// building a full Machine.</summary>
    internal void RaiseFrameForTest() => FrameReady?.Invoke();

    public void RenderInto(Span<uint> rgba)
    {
        if (rgba.Length < Width280 * Height192)
            throw new ArgumentException(
                $"Destination needs {Width280 * Height192} pixels; got {rgba.Length}.", nameof(rgba));

        if (_state.GraphicsOn && _state.HiRes)
            RenderHiRes(rgba);
        else if (_state.GraphicsOn) // lo-res
            RenderLoRes(rgba);
        else
            RenderText(rgba);
    }

    private void RenderHiRes(Span<uint> rgba)
    {
        for (int y = 0; y < Height192; y++)
        {
            uint rowBase = Apple2HiResAddress.RowBase(y, _state.Page2);
            int destRow = y * Width280;
            int x = 0;
            for (int b = 0; b < 40; b++)        // 40 bytes per row, 7 pixels each
            {
                byte data = _ram.Read8(rowBase + (uint)b);
                for (int bit = 0; bit < 7 && x < Width280; bit++, x++)
                {
                    bool on = (data & (1 << bit)) != 0; // bit 0 = leftmost (the dot order)
                    rgba[destRow + x] = on ? Apple2Palette.MonoOn : Apple2Palette.MonoOff;
                }
            }
        }
    }

    private void RenderLoRes(Span<uint> rgba)
    {
        // 40x24 byte grid; each byte = two stacked 4-bit colour blocks (low nibble top, high nibble
        // bottom). Rendered onto the 280x192 grid: each lo-res cell is 7px wide x 4px tall (48 rows).
        for (int r = 0; r < 24; r++)
        {
            uint rowBase = Apple2HiResAddress.TextRowBase(r, _state.Page2);
            for (int c = 0; c < 40; c++)
            {
                byte data = _ram.Read8(rowBase + (uint)c);
                uint top = Apple2Palette.LoRes[data & 0x0F];
                uint bottom = Apple2Palette.LoRes[(data >> 4) & 0x0F];
                FillCell(rgba, c * 7, r * 8, 7, 4, top);
                FillCell(rgba, c * 7, r * 8 + 4, 7, 4, bottom);
            }
        }
    }

    private void RenderText(Span<uint> rgba)
    {
        for (int r = 0; r < 24; r++)
        {
            uint rowBase = Apple2HiResAddress.TextRowBase(r, _state.Page2);
            for (int c = 0; c < 40; c++)
            {
                byte ch = _ram.Read8(rowBase + (uint)c);
                int glyph = ch & 0x7F;          // strip the inverse/flash high bits (basic render)
                for (int gy = 0; gy < 8; gy++)
                {
                    byte rowBits = _charRom[glyph * 8 + gy];
                    for (int gx = 0; gx < 7; gx++)
                    {
                        bool on = (rowBits & (0x40 >> gx)) != 0; // bit 6 = leftmost
                        int px = c * 7 + gx, py = r * 8 + gy;
                        rgba[py * Width280 + px] = on ? Apple2Palette.MonoOn : Apple2Palette.MonoOff;
                    }
                }
            }
        }
    }

    private static void FillCell(Span<uint> rgba, int x0, int y0, int w, int h, uint color)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
            {
                int px = x0 + dx, py = y0 + dy;
                if (px < Width280 && py < Height192)
                    rgba[py * Width280 + px] = color;
            }
    }
}
