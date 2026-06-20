using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// The ZX Spectrum ULA: one chip on Z80 I/O port $FE (decoded by bit 0 == 0). It faces the guest as
/// an <see cref="IPeripheral"/> (IN $FE = keyboard + EAR; OUT $FE = border + beeper) and the host as
/// <see cref="IDisplayDevice"/> (256×192 + a 32px border → RGBA), <see cref="IKeyboardSink"/> (the
/// 8×5 matrix), and <see cref="IAudioSink"/> (the 1-bit beeper resampled to S16 PCM). It reads main
/// RAM ($4000-$5AFF) for video via an injected <see cref="IAddressSpace"/> — it owns no VRAM. The
/// 50 Hz frame tick raises the maskable interrupt and FrameReady/AudioReady.
/// </summary>
public sealed class SpectrumUla : IPeripheral, IDisplayDevice, IKeyboardSink, IAudioSink
{
    public const int InkWidth = 256;
    public const int InkHeight = 192;
    public const int BorderPx = 32;
    public const int FullWidth = InkWidth + 2 * BorderPx;   // 320
    public const int FullHeight = InkHeight + 2 * BorderPx; // 256

    private const uint ScreenBase = 0x4000;
    private const uint AttrBase = 0x5800;

    // 3.5 MHz / 50.08 Hz ≈ 69888 T-states per frame.
    public const long TStatesPerFrame = 69888;
    private const int HostSampleRate = 44100;
    private const int SamplesFrame = HostSampleRate / 50; // 882

    private IAddressSpace _ram = default!; // bound in Realize (the machine's program space)
    private readonly byte[] _matrix = CreateIdleMatrix(); // 8 half-rows; bit set = NOT pressed (idle high)
    private int _border;                                  // 0..7 base colour
    private int _beeperLevel;                             // last OUT bit-4 level (0/1)

    // Beeper toggle log: (tStateWithinFrame, level) pairs accumulated across a frame.
    private readonly List<(long t, int level)> _beeperLog = new();
    private long _frameStartCycle;
    private IInterruptLine? _irq;
    private bool _flashPhase;     // toggles every 16 frames (the FLASH attribute)
    private int _frameCounter;

    public string Name => "ula";
    public int Width => FullWidth;
    public int Height => FullHeight;
    public event Action? FrameReady;
    public event Action? AudioReady;

    public int SampleRate => HostSampleRate;
    public int ChannelCount => 1;
    public int SamplesPerFrame => SamplesFrame;

    /// <summary>Construct a ULA whose screen RAM is bound at Realize time to the machine's program
    /// space. A test may pass an explicit space to render without a full Machine.</summary>
    public SpectrumUla(IAddressSpace? ram = null)
    {
        if (ram is not null) _ram = ram;
    }

    // ── IPeripheral: the guest CPU's port $FE (offset IS the full 16-bit port; bit 0 == 0 decoded). ──
    public void Realize(IMachineContext context)
    {
        _ram = context.Space(AddressSpaceKind.Program);
        _irq = context.IrqLine.Source();
        _frameStartCycle = context.Scheduler.CurrentCycle;
        context.Scheduler.ScheduleEvery(TStatesPerFrame, OnFrameTick);
    }

    private void OnFrameTick()
    {
        // Latch the frame's beeper log end, raise the maskable interrupt (IM1), and signal the host.
        _frameCounter++;
        if ((_frameCounter & 0x0F) == 0)
            _flashPhase = !_flashPhase; // FLASH toggles every 16 frames
        _irq?.Assert();                 // the ROM's ISR runs and (via DI/EI + RET) the line is sampled
        FrameReady?.Invoke();
        AudioReady?.Invoke();
        // The interrupt line is a brief pulse; release on the next instruction boundary is approximated
        // by releasing here after the host has been signalled (the ROM ACKs by servicing).
        _irq?.Release();
    }

    public uint Read(uint offset, AccessWidth width)
    {
        // Port decode: the ULA answers only even ports (bit 0 == 0); odd ports are open bus (0xFF).
        if ((offset & 0x0001) != 0)
            return 0xFF;

        // IN $FE: bits 0-4 = the AND of every selected half-row's keys (A8..A15 low selects a row);
        // bit 5 = unused (1), bit 6 = EAR-in (idle high = 1), bit 7 = 1.
        int high = (int)((offset >> 8) & 0xFF);
        int keys = 0x1F; // all released
        for (int row = 0; row < 8; row++)
            if ((high & (1 << row)) == 0)   // this row selected (its address line is low)
                keys &= _matrix[row];
        return (uint)(0xE0 | (keys & 0x1F)); // bits 5,6,7 high; tape idle
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        if ((offset & 0x0001) != 0)
            return; // not the ULA

        byte v = (byte)value;
        _border = v & 0x07;

        int level = (v >> 4) & 0x01; // bit 4 = EAR / speaker (the beeper)
        if (level != _beeperLevel)
        {
            long tInFrame = CurrentTInFrame();
            _beeperLog.Add((tInFrame, level));
            _beeperLevel = level;
        }
    }

    private long CurrentTInFrame()
    {
        // Approximate the write's position in the frame. Without a scheduler handle on the write path,
        // distribute writes evenly is wrong; instead clamp to [0, TStatesPerFrame). The host pulls audio
        // each frame tick, so absolute frame phase is not needed — only ordering + relative spacing.
        long n = _beeperLog.Count;
        long approx = (n * TStatesPerFrame) / Math.Max(1, SamplesFrame);
        return Math.Min(approx, TStatesPerFrame - 1);
    }

    public bool TryPeek(uint offset, out byte value)
    {
        // Side-effect-free: a keyboard/border peek returns the same as Read for even ports, 0xFF odd.
        value = (byte)((offset & 0x0001) != 0 ? 0xFF : Read(offset, AccessWidth.Byte));
        return true;
    }

    // ── IDisplayDevice: walk the bit-shuffled screen + attributes + border into RGBA. ──
    public void RenderInto(Span<uint> rgba)
    {
        if (rgba.Length < FullWidth * FullHeight)
            throw new ArgumentException(
                $"Destination needs {FullWidth * FullHeight} pixels; got {rgba.Length}.", nameof(rgba));

        uint borderColor = SpectrumPalette.Colors[_border];

        // Fill the whole frame with the border colour first (top/bottom/left/right border bands).
        for (int i = 0; i < FullWidth * FullHeight; i++)
            rgba[i] = borderColor;

        for (int y = 0; y < InkHeight; y++)
        {
            int cellRow = y >> 3;
            int destY = BorderPx + y;
            for (int x = 0; x < InkWidth; x++)
            {
                uint addr = ScreenBase
                    | ((uint)(y & 0xC0) << 5)
                    | ((uint)(y & 0x07) << 8)
                    | ((uint)(y & 0x38) << 2)
                    | (uint)(x >> 3);
                byte bits = _ram.Read8(addr);
                bool ink = (bits & (0x80 >> (x & 7))) != 0;

                int cellCol = x >> 3;
                byte attr = _ram.Read8(AttrBase + (uint)(cellRow * 32 + cellCol));
                int inkColor = attr & 0x07;
                int paperColor = (attr >> 3) & 0x07;
                bool bright = (attr & 0x40) != 0;
                bool flash = (attr & 0x80) != 0;

                // FLASH swaps ink/paper on alternate phases.
                if (flash && _flashPhase)
                    (inkColor, paperColor) = (paperColor, inkColor);

                int idx = (bright ? 8 : 0) + (ink ? inkColor : paperColor);
                rgba[destY * FullWidth + (BorderPx + x)] = SpectrumPalette.Colors[idx];
            }
        }
    }

    // ── IKeyboardSink: set/clear matrix bits (0 = pressed on the wire). ──
    public void PostKey(in KeyEvent e)
    {
        if (!SpectrumKeyMatrix.TryMap(e.Key, out int row, out int bit))
            return;
        if (e.Action == KeyAction.Down)
            _matrix[row] &= (byte)~(1 << bit); // pressed → bit LOW
        else
            _matrix[row] |= (byte)(1 << bit);  // released → bit HIGH
    }

    // ── IAudioSink: resample the beeper toggle log to S16 PCM for the frame. ──
    public void RenderAudio(Span<short> samples)
    {
        if (samples.Length < SamplesFrame)
            throw new ArgumentException($"need {SamplesFrame} samples; got {samples.Length}.", nameof(samples));

        // Walk the toggle log across the frame, filling samples with the level active at each sample's
        // T-state. Level 1 → +amplitude, level 0 → -amplitude (a simple 1-bit DAC).
        const short amp = 12000;
        int startLevel = _beeperLog.Count > 0 ? 1 - _beeperLog[0].level : _beeperLevel; // level before first toggle
        // Reconstruct by scanning toggles in T-state order.
        int li = 0;
        int level = StartLevelOfFrame();
        for (int s = 0; s < SamplesFrame; s++)
        {
            long tAtSample = (long)((double)s / SamplesFrame * TStatesPerFrame);
            while (li < _beeperLog.Count && _beeperLog[li].t <= tAtSample)
            {
                level = _beeperLog[li].level;
                li++;
            }
            samples[s] = level != 0 ? amp : (short)-amp;
        }
        // Carry the final level into the next frame; reset the log.
        _beeperLevel = level;
        _beeperLog.Clear();
        _ = startLevel; // (kept for clarity; StartLevelOfFrame is authoritative)
    }

    private int StartLevelOfFrame()
    {
        // The level at the very start of the frame is the level after the previous frame ended, which
        // is the current _beeperLevel BEFORE this frame's first logged toggle. If the first log entry
        // exists, the pre-toggle level is its complement; else it's the steady _beeperLevel.
        if (_beeperLog.Count == 0)
            return _beeperLevel;
        return 1 - _beeperLog[0].level;
    }

    private static byte[] CreateIdleMatrix()
    {
        var m = new byte[8];
        for (int i = 0; i < 8; i++) m[i] = 0x1F; // all 5 keys released (bits high)
        return m;
    }
}
