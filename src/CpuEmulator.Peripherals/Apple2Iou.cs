using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ I/O Unit: one IPeripheral owning the $C000 page ($C000-$C0FF) and decoding
/// every soft switch by offset (ADR 0014 Decision 2). The load-bearing ][+ rule: the video / speaker /
/// (later) Language-Card switches toggle on ANY access — read OR write (the inverse of the IIe). So
/// Read and Write both call the SAME ApplyAnyAccessSideEffect(offset); only the returned bus value
/// differs. TryPeek (the debugger's side-effect-free path) calls the parallel BusValue path and applies
/// NO side effect — a class of "the monitor changed the video mode by looking at it" bugs is structurally
/// impossible. The keyboard latch + speaker live in the shared Apple2VideoState the video/speaker chips
/// read. The Language Card ($C080-$C08F) and Disk II ($C0E0-$C0EF) are delegated in later PRs (E, F);
/// for now those offsets are inert open-bus.</summary>
public sealed class Apple2Iou : IPeripheral
{
    private readonly Apple2VideoState _state;

    public Apple2Iou(Apple2VideoState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public string Name => "iou";

    public void Realize(IMachineContext context) { /* no IRQ/schedule on the bare IOU */ }

    public uint Read(uint offset, AccessWidth width)
    {
        ApplyAnyAccessSideEffect(offset);
        return BusValue(offset);
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        ApplyAnyAccessSideEffect(offset);
        // Soft switches ignore the written value; the side effect is the access itself.
    }

    public bool TryPeek(uint offset, out byte value)
    {
        value = BusValue(offset);   // the would-be read value, with NO side effect
        return true;
    }

    /// <summary>The any-access (read OR write) side effects. The single source of truth both Read and
    /// Write call — and TryPeek deliberately does NOT.</summary>
    private void ApplyAnyAccessSideEffect(uint offset)
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
            // $C080-$C08F (Language Card) and $C0E0-$C0EF (Disk II) are delegated in PR-E / PR-F.
            default: break;
        }
    }

    /// <summary>The bus value a READ (or a peek) returns for an offset, WITHOUT side effects.</summary>
    private byte BusValue(uint offset)
    {
        byte o = (byte)offset;
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
