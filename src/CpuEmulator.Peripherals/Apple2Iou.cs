using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ I/O Unit: one IPeripheral owning the $C000 page ($C000-$C0FF) and decoding
/// every soft switch by offset (ADR 0014 Decision 2). The load-bearing ][+ rule: the video / speaker /
/// (later) Language-Card switches toggle on ANY access — read OR write (the inverse of the IIe). So
/// Read and Write both call the SAME ApplyAnyAccessSideEffect(offset); only the returned bus value
/// differs. TryPeek (the debugger's side-effect-free path) calls the parallel BusValue path and applies
/// NO side effect — a class of "the monitor changed the video mode by looking at it" bugs is structurally
/// impossible. The keyboard latch + speaker live in the shared Apple2VideoState the video/speaker chips
/// read. The Language Card ($C080-$C08F) is delegated to the optional Apple2LanguageCard (PR-E) — a $C08x
/// READ's side effect rides BusValue and a WRITE's rides ApplyAnyAccessSideEffect, so the LC's Access
/// fires exactly once per bus access, while TryPeek short-circuits $C08x (peek-free). Disk II
/// ($C0E0-$C0EF) is delegated to the optional Apple2DiskII (PR-F) the SAME way — a $C0Ex READ's side
/// effect (e.g. $C0EC shifting a nibble) rides BusValue, a WRITE's rides ApplyAnyAccessSideEffect, and
/// TryPeek short-circuits $C0Ex (peek-free: a debugger peek of $C0EC never advances the head).</summary>
public sealed class Apple2Iou : IPeripheral
{
    private readonly Apple2VideoState _state;
    private readonly Apple2LanguageCard? _lc;   // PR-E: $C080-$C08F delegate (null on the bare board)
    private readonly Apple2DiskII? _disk2;      // PR-F: $C0E0-$C0EF delegate (null on the bare board)
    private readonly VidexVideoterm? _videx;    // PR-N: $C0B0-$C0BF delegate (null on the bare board)

    public Apple2Iou(Apple2VideoState state) : this(state, null, null) { }

    public Apple2Iou(Apple2VideoState state, Apple2LanguageCard? lc) : this(state, lc, null) { }

    public Apple2Iou(Apple2VideoState state, Apple2DiskII? disk2) : this(state, null, disk2) { }

    public Apple2Iou(Apple2VideoState state, Apple2LanguageCard? lc, Apple2DiskII? disk2)
        : this(state, lc, disk2, null) { }

    public Apple2Iou(Apple2VideoState state, Apple2LanguageCard? lc, Apple2DiskII? disk2,
                     VidexVideoterm? videx)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _lc = lc;
        _disk2 = disk2;
        _videx = videx;
    }

    public string Name => "iou";

    public void Realize(IMachineContext context)
    {
        _lc?.Realize(context);      // PR-E: the LC owns no page, so the IOU (a mapped peripheral) Realizes it
        _disk2?.Realize(context);   // PR-F: same — the Disk II captures the scheduler for the motor-off delay
    }

    public uint Read(uint offset, AccessWidth width)
    {
        ApplyAnyAccessSideEffect(offset, isRead: true);
        return BusValue(offset);
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        ApplyAnyAccessSideEffect(offset, isRead: false);
        // Soft switches ignore the written value; the side effect is the access itself.
    }

    public bool TryPeek(uint offset, out byte value)
    {
        byte o = (byte)offset;
        // PEEK-FREE for $C08x: a debugger looking at a Language-Card soft switch must NOT bank-switch.
        // BusValue owns the $C08x READ side effect (the LC remap + arm) for the real bus-read path, so
        // routing a peek through BusValue would silently drive the LC — a breach of the ][+ peek-free
        // invariant (ADR 0014 Decision 2) and the IPeripheral.TryPeek contract. Short-circuit it here.
        if (o is >= 0x80 and <= 0x8F)
        {
            value = 0x00;   // open-bus; no LC.Access, no remap, no arm-count change
            return true;
        }
        // PEEK-FREE for $C0Bx: a Videx CRTC/bank access has side effects (register-select, bank Remap,
        // ActiveChanged). Short-circuit a peek to open-bus 0 BEFORE BusValue, like $C08x/$C0Ex.
        if (o is >= 0xB0 and <= 0xBF)
        {
            value = 0x00;
            return true;
        }
        // PEEK-FREE for $C0Ex: $C0EC is the Disk II data latch — a real read SHIFTS A NIBBLE (advances the
        // bitstream head). BusValue owns that $C0Ex READ side effect, so routing a peek through BusValue
        // would silently advance the head + relatch (a peek-free breach of the same class PR-E's review
        // caught on $C08x). Short-circuit it to a side-effect-free open-bus 0 BEFORE calling BusValue.
        if (o is >= 0xE0 and <= 0xEF)
        {
            value = 0x00;   // open-bus; no Disk2.Access, no head advance, no latch change
            return true;
        }
        value = BusValue(offset);   // the would-be read value, with NO side effect
        return true;
    }

    /// <summary>The any-access (read OR write) side effects. The single source of truth both Read and
    /// Write call — and TryPeek deliberately does NOT. <paramref name="isRead"/> is threaded so the
    /// Language Card can distinguish reads (which arm its pre-write flip-flop) from writes (which reset
    /// it). To call the LC's Access EXACTLY ONCE per bus access, this handles $C08x for WRITES only (a
    /// read's $C08x side effect rides BusValue — Read calls both, so $C08x routes through one path).</summary>
    private void ApplyAnyAccessSideEffect(uint offset, bool isRead)
    {
        byte o = (byte)offset;
        switch (o)
        {
            // --- Video mode $C050-$C057 (any access toggles) ---
            case 0x50: _state.GraphicsOn = true; break;   // TXTCLR -> graphics
            case 0x51: _state.GraphicsOn = false; break;  // TXTSET -> text
            case 0x52: _state.Mixed = false; break;       // MIXCLR -> full
            case 0x53: _state.Mixed = true; break;        // MIXSET -> mixed
            case 0x54: _state.Page2 = false; break;       // LOWSCR -> page 1
            case 0x55: _state.Page2 = true; break;        // HISCR  -> page 2
            case 0x56: _state.HiRes = false; break;       // LORES
            case 0x57: _state.HiRes = true; break;        // HIRES

            // --- Keyboard ---
            case 0x10: _state.ClearStrobe(); break;       // $C010: clear the strobe

            // --- Speaker $C030 (any reference toggles the 1-bit flip-flop) ---
            case 0x30: _state.ToggleSpeaker(); break;

            // $C000 (keyboard read) has no side effect on access; the value is in BusValue.
            // --- Language Card $C080-$C08F (delegated to the LC mapper; WRITES only here — a $C08x
            // read's Access is owned by BusValue so the LC's Access fires exactly once per bus access). ---
            case >= 0x80 and <= 0x8F:
                if (!isRead) _lc?.Access(o, isRead: false);
                break;

            // --- Videx CRTC $C0B0-$C0BF (delegated; WRITES only here — a read's Access is owned by
            // BusValue so the Videx's Access fires exactly once per bus access). ---
            case >= 0xB0 and <= 0xBF:
                if (!isRead) _videx?.Access(o, isRead: false);
                break;

            // --- Disk II $C0E0-$C0EF (delegated to the controller; WRITES only here — a $C0Ex read's
            // Access is owned by BusValue so the controller's Access fires exactly once per bus access,
            // and $C0EC advances the head exactly once per read). ---
            case >= 0xE0 and <= 0xEF:
                if (!isRead) _disk2?.Access(o, isRead: false);
                break;

            default: break;
        }
    }

    /// <summary>The bus value a READ returns for an offset. Side-effect-free EXCEPT for $C08x, whose READ
    /// side effect (the LC bank remap + arm) is owned here so the LC's Access fires exactly once per bus
    /// access (Read calls ApplyAnyAccessSideEffect AND BusValue, and ApplyAnyAccessSideEffect skips $C08x
    /// reads). NOTE: TryPeek short-circuits $C08x BEFORE calling this, so a debugger peek never drives the
    /// LC — the peek-free invariant holds.</summary>
    private byte BusValue(uint offset)
    {
        byte o = (byte)offset;
        if (o is >= 0x80 and <= 0x8F)
            return _lc?.Access(o, isRead: true) ?? 0x00;
        // $C0B0-$C0BF (Videx CRTC): a READ's side effect rides here ($C0B1 returns the selected CRTC
        // register). TryPeek short-circuits $C0Bx before reaching this, so a debugger peek never programs
        // the CRTC or switches banks — the peek-free invariant holds.
        if (o is >= 0xB0 and <= 0xBF)
            return _videx?.Access(o, isRead: true) ?? 0x00;
        // $C0E0-$C0EF (Disk II): a READ's side effect rides here ($C0EC returns the latched nibble AND
        // advances the head exactly once per read). TryPeek short-circuits $C0Ex before reaching this, so
        // a debugger peek never advances the head — the peek-free invariant holds.
        if (o is >= 0xE0 and <= 0xEF)
            return _disk2?.Access(o, isRead: true) ?? 0x00;
        return o switch
        {
            0x00 => _state.KeyboardByte,   // $C000: bit7 strobe + 7-bit code
            // Most soft switches float the bus; return open-bus high-ish. The ][+ commonly leaves the
            // data bus with the high byte of the last fetch; a stable 0x00 here is adequate until a
            // switch-read-value gate needs more (a build-time fidelity dial, ADR 0014 Decision 8).
            _ => 0x00,
        };
    }
}
