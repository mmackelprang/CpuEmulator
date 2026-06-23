using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Videx Videoterm 80-column card (ADR 0016 Decision 3, research §8): a slot-3 card whose
/// 6845 CRTC is programmed via $C0B0 (register-select) / $C0B1 (data), whose 2 KiB on-card VRAM is banked
/// as 4 x 512-byte pages into the $CC00-$CDFF window of the $C800 expansion space (firmware ROM at
/// $C800-$CBFF), and which walks that VRAM through a 2 KiB char ROM into an 80x24 monochrome RGBA frame.
/// It is BOTH an IPeripheral (the CRTC + bank registers, delegated from the IOU at $C0Bx) AND an
/// IDisplayDevice (one source of the host DisplayMultiplexer, PR-M). The $C800 window is mapped with the
/// SHIPPED IAddressSpace.Remap (PR-A) — the SECOND Remap consumer after the Language Card: the firmware
/// window $C800-$CBFF is Remapped to ROM, the VRAM window $CC00-$CDFF to the active 512-byte bank (plain
/// writable RAM, so the guest's hot character writes ride the fastmem fast path — the ADR 0009 Decision 1
/// fast-RAM intent realized through Remap, since IFastMemoryProvider is ADR-designed but not shipped). The
/// guest-driven active-display signal (ADR 0016 Decision 2) is ActiveChanged(bool): the Videx is the
/// WRITER (its $C800-enable state), the host DisplayMultiplexer the READER (PR-O wires ActiveChanged ->
/// SetActive). Timing: the present tick is scheduled in Realize (the Apple2Video precedent); no IRQ.</summary>
public sealed class VidexVideoterm : IPeripheral, IDisplayDevice
{
    // --- $C800 expansion-window geometry (research §8) ---
    public const uint FirmwareWindowBase = 0xC800;   // $C800-$CBFF firmware ROM (1 KiB)
    public const uint FirmwareWindowLength = 0x0400;
    public const uint VramWindowBase = 0xCC00;       // $CC00-$CDFF banked VRAM (512 B)
    public const uint VramWindowLength = 0x0200;     // 512 bytes = one bank
    public const int BankSize = 512;
    public const int BankCount = 4;                  // 4 x 512 B = 2 KiB on-card VRAM
    public const int VramSize = BankSize * BankCount; // 2 KiB linear VRAM (the 6845 scanout space)

    private const long CyclesPerFrame = 17030;       // ~60 Hz present cadence (the Apple2Video value)

    // --- 6845 CRTC register file (R0-R17) ---
    private readonly byte[] _crtc = new byte[18];
    private int _crtcAddr;                            // the register the next $C0B1 access targets

    // --- 2 KiB VRAM as 4 x 512 B bank arrays (the Remap-a-byte[] model; the guest writes the live bank) ---
    private readonly byte[][] _vramBanks;
    private int _bank;                               // the active $CC00-$CDFF bank (0-3)

    private readonly byte[] _charRom;                // 256 x 8; the synthetic VidexFont unless a real ROM is injected
    private readonly byte[] _firmwareRom;            // 1 KiB $C800-$CBFF firmware (synthetic unless injected)

    private IAddressSpace _bus = default!;           // the live program bus, captured in Realize (for Remap)
    private bool _active;                            // the $C800-window enable (the active-display state)

    public string Name => "videx";

    /// <summary>True when the render is using the legible synthetic <see cref="VidexFont.Fallback"/> because no
    /// VALID real character ROM was supplied — either none was injected, or the injected image failed
    /// <see cref="VidexFont.LooksLikeFont"/> (a firmware/garbage dump mis-placed at the char-ROM path). Exposed
    /// as an observable diagnostic so a caller/surface can see WHY the exact-fidelity char ROM is not in use
    /// (the peripheral layer has no logging dependency by design — this property is the substitution signal).</summary>
    public bool UsingSyntheticFont { get; }

    public event Action? FrameReady;
    /// <summary>The guest-driven active-display signal (ADR 0016 Decision 2): true when the Videx becomes
    /// the live terminal (its $C800 window enabled), false when the Apple video is re-selected. The host
    /// DisplayMultiplexer subscribes this and calls SetActive (PR-O). Engagement is raised either by a VRAM
    /// bank-select ($C0B8-$C0BF) OR by a CRTC-data write ($C0B1) -- the latter is the firmware bringing the
    /// 80-col display online for a CRT80 build (apl2cpm3) that paints VRAM linearly and never bank-selects
    /// (ADR 0018-C OQ1 / V80-3 -- the auto-engage trigger). The transition no-op guard means it fires once.</summary>
    public event Action<bool>? ActiveChanged;

    /// <param name="charRom">Optional 256x8 char-gen ROM. A null OR a STRUCTURALLY-INVALID image (one that is
    /// not actually a font — e.g. a firmware-ROM dump mis-placed at the char-ROM cache path, which decodes to a
    /// stipple field in the browser) falls back to the legible synthetic <see cref="VidexFont.Fallback"/>. Only
    /// a real character ROM that passes <see cref="VidexFont.LooksLikeFont"/> (a blank space glyph + distinct,
    /// inked A-Z letterforms) is used as-is — giving exact fidelity when present, and a cleanly-legible console
    /// otherwise. A valid-length-but-wrong-shape ROM is silently rejected; only a wrong-LENGTH image throws.</param>
    /// <param name="firmwareRom">Optional 1 KiB $C800 firmware ROM; null uses an all-zero synthetic image
    /// (the PR-N gate does not execute the firmware; the real firmware is the PR-O asset).</param>
    public VidexVideoterm(byte[]? charRom = null, byte[]? firmwareRom = null)
    {
        if (charRom is not null && charRom.Length != 256 * VidexFont.GlyphRows)
            throw new ArgumentException("Videx char ROM must be 256x8 = 2048 bytes.", nameof(charRom));
        // Use the supplied char ROM ONLY when it is a real font; otherwise (null, or a firmware/garbage dump
        // landed at the char-ROM path) use the legible synthetic font so the streamed console stays readable.
        bool realFont = VidexFont.LooksLikeFont(charRom);
        _charRom = realFont ? charRom! : VidexFont.Fallback;
        UsingSyntheticFont = !realFont;
        _firmwareRom = firmwareRom ?? new byte[(int)FirmwareWindowLength];
        if (_firmwareRom.Length != (int)FirmwareWindowLength)
            throw new ArgumentException("Videx firmware ROM must be 1 KiB ($C800-$CBFF).", nameof(firmwareRom));

        _vramBanks = new byte[BankCount][];
        for (int i = 0; i < BankCount; i++)
            _vramBanks[i] = new byte[BankSize];
    }

    public void Realize(IMachineContext context)
    {
        _bus = context.Space(AddressSpaceKind.Program);   // the live bus we Remap (the LC/Apple2Video precedent)
        // Map the $C800 expansion window: firmware ROM ($C800-$CBFF, read-only) + VRAM bank 0 ($CC00-$CDFF,
        // writable). The board carved these as mappable regions (SpecWithVidex), so the page table has
        // entries to re-point. This is the second Remap consumer (the Language Card is the first).
        _bus.Remap(FirmwareWindowBase, _firmwareRom, writable: false);
        _bus.Remap(VramWindowBase, _vramBanks[_bank], writable: true);
        context.Scheduler.ScheduleEvery(CyclesPerFrame, () => FrameReady?.Invoke());
    }

    // --- IPeripheral: the $C0B0/$C0B1 register window (delegated by the IOU; offsets relative to $C0B0) ---
    public uint Read(uint offset, AccessWidth width) => ReadReg((byte)offset);
    public void Write(uint offset, AccessWidth width, uint value) => WriteReg((byte)offset, (byte)value);

    /// <summary>The IOU delegate entry for $C0B0-$C0BF (mirrors the LC's $C08x Access, but the Videx CRTC
    /// needs the WRITTEN value — unlike a value-agnostic soft switch — so writeValue is threaded from the
    /// IOU's Write). A read's side effect rides the returned value; a write programs the selected register
    /// or selects a VRAM bank. offset is the low byte ($B0-$BF).</summary>
    public byte Access(byte offset, bool isRead, byte writeValue = 0x00)
    {
        byte o = (byte)(offset & 0x0F);   // $C0B0-$C0BF low nibble
        if (isRead) return ReadReg(o);
        WriteReg(o, writeValue);
        return 0x00;
    }

    private byte ReadReg(byte o)
    {
        // offset 1 ($C0B1) reads the selected CRTC register (only R14-R17 are truly readable on a 6845;
        // returning the stored value is adequate for the cursor/status the firmware polls). The register
        // index is guarded to [0,17] (_crtcAddr is masked to 0-31 on write; % 18 keeps the array index
        // in range — the 6845 has 18 registers).
        return o == 1 ? _crtc[_crtcAddr % 18] : (byte)0x00;
    }

    private void WriteReg(byte o, byte value)
    {
        switch (o)
        {
            case 0x00:                        // $C0B0: register-select
                _crtcAddr = value & 0x1F;     // 6845 has 18 regs; mask to 0-31, index guarded on use
                break;
            case 0x01:                        // $C0B1: data -- the firmware programming a 6845 register
                if (_crtcAddr < _crtc.Length)
                    _crtc[_crtcAddr] = value;
                // Programming the CRTC is the firmware bringing the 80-col display online -- it is the
                // active-display engagement signal for a CRT80 build (apl2cpm3) that paints VRAM linearly
                // and never does a $C0B8-$C0BF bank-select (ADR 0018-C OQ1 -- the auto-engage trigger).
                // SetActive has a transition no-op guard, so ActiveChanged(true) fires exactly once. A 40-col
                // SoftCard master (CP/M 2.2) issues ZERO $C0Bx, so it never engages -- ActiveIndex stays 0
                // (the CPM-5 gate's load-bearing invariant, kept byte-for-byte).
                SetActive(true);
                break;
            default:
                // $C0B8-$C0BF region: VRAM bank select (research §8: bank = ((offset>>2)&3)). A bank-select
                // access also enables the Videx (the active-display signal): the first enable raises
                // ActiveChanged(true).
                if (o is >= 0x08 and <= 0x0F)
                {
                    SelectBank((o >> 2) & 0x03);
                    SetActive(true);
                }
                break;
        }
    }

    private void SelectBank(int bank)
    {
        if ((uint)bank >= BankCount) return;
        if (bank == _bank) return;
        _bank = bank;
        // Re-point $CC00-$CDFF to the newly selected 512-byte bank (the second Remap consumer). The guest
        // then writes the live bank array; the render reads the same array.
        _bus?.Remap(VramWindowBase, _vramBanks[_bank], writable: true);
    }

    private void SetActive(bool active)
    {
        if (active == _active) return;        // only on an actual transition (the SetActive no-op-guard shape)
        _active = active;
        ActiveChanged?.Invoke(active);
    }

    // --- IDisplayDevice: 80x24 RGBA from the VRAM through the char ROM ---
    private int Cols => Math.Max(1, _crtc[1] == 0 ? 80 : _crtc[1]);          // R1 (chars/row), default 80
    private int Rows => Math.Max(1, _crtc[6] == 0 ? 24 : _crtc[6]);          // R6 (displayed rows), default 24
    private int CellLines => Math.Max(1, ((_crtc[9] & 0x1F) == 0 ? 8 : (_crtc[9] & 0x1F)) + 1); // R9+1, default 9

    public int Width => Cols * VidexFont.CellWidth;
    public int Height => Rows * CellLines;

    public void RenderInto(Span<uint> rgba)
    {
        int width = Width, height = Height;
        if (rgba.Length < width * height)
            throw new ArgumentException($"Destination needs {width * height} pixels; got {rgba.Length}.",
                nameof(rgba));

        int cols = Cols, rows = Rows, cellLines = CellLines;
        // The 6845 scans the FULL 2 KiB on-card VRAM as one linear character space (the four 512-byte banks
        // concatenated: bank b, offset o == linear b*512 + o), starting at the CRTC scanout base address
        // (R12 high / R13 low) and wrapping within the 2 KiB as the 6845's address counter does (it is a
        // 14-bit counter masked to the on-card VRAM). A CRT80 build (apl2cpm3) programs a non-zero start
        // (e.g. 960) and paints the 80x24 = 1920-char console linearly across the wrap; an in-range,
        // start-at-0 config (the PR-N render gate) is the same code path with no wrap. Sourcing only one
        // 512-byte bank — the prior bug — left every cell >= 512 reading $00, tiling one glyph (the bug the
        // rendered-output gate now catches).
        int startAddr = ((_crtc[12] << 8) | _crtc[13]) % VramSize;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int lin = (startAddr + r * cols + c) % VramSize;   // wrap within the 2 KiB scanout space
                byte code = _vramBanks[lin / BankSize][lin % BankSize];
                // Mask to 7-bit ASCII: the firmware pads cells with $A0 (space | high bit) and writes text
                // with the high "normal-video" bit set; the char ROM is a 128-glyph set, so the high bit is
                // not a glyph index. Stripping it renders $A0 padding as a black space and $C1 ('A'|$80) as
                // the 'A' glyph — the difference between a clean console and a stipple field.
                int glyphBase = (code & 0x7F) * VidexFont.GlyphRows;
                for (int gy = 0; gy < cellLines; gy++)
                {
                    // 8 active glyph rows + (cellLines-8) blank descender lines.
                    byte rowBits = gy < VidexFont.GlyphRows ? _charRom[glyphBase + gy] : (byte)0x00;
                    for (int gx = 0; gx < VidexFont.CellWidth; gx++)
                    {
                        bool on = (rowBits & (0x40 >> gx)) != 0;   // bit 6 = leftmost (the Apple2Font order)
                        int px = c * VidexFont.CellWidth + gx;
                        int py = r * cellLines + gy;
                        rgba[py * width + px] = on ? Apple2Palette.MonoOn : Apple2Palette.MonoOff;
                    }
                }
            }
        }
    }

    // --- Test seams (mirror Apple2Video.RaiseFrameForTest; no production caller) ---
    internal void PokeVramForTest(int bank, int offset, byte value) => _vramBanks[bank][offset] = value;
    internal byte PeekVramForTest(int bank, int offset) => _vramBanks[bank][offset];
    internal void SelectBankForTest(int bank) => SelectBank(bank);
    internal void SetActiveForTest(bool active) => SetActive(active);
}
