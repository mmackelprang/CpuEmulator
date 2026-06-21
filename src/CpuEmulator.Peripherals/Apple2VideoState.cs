namespace CpuEmulator.Peripherals;

/// <summary>The small mutable state the <see cref="Apple2Iou"/> WRITES (via the $C0xx soft-switch
/// decode) and the Apple2Video chip + Apple2Speaker READ — one object both hold a reference to (ADR
/// 0014 Decision 3's writer/reader split), so a $C057 HIRES access is visible to the next render with
/// no plumbing. Flags default to the ][+ power-on state: text, page 1, full (not mixed), lo-res.</summary>
public sealed class Apple2VideoState
{
    // --- Video mode (the $C050-$C057 soft switches) ---
    /// <summary>$C050 TXTCLR sets true (graphics on); $C051 TXTSET sets false (text).</summary>
    public bool GraphicsOn { get; set; }
    /// <summary>$C052 MIXCLR sets false (full); $C053 MIXSET sets true (mixed text+gfx).</summary>
    public bool Mixed { get; set; }
    /// <summary>$C054 LOWSCR sets false (page 1); $C055 HISCR sets true (page 2).</summary>
    public bool Page2 { get; set; }
    /// <summary>$C056 LORES sets false; $C057 HIRES sets true.</summary>
    public bool HiRes { get; set; }

    // --- Keyboard latch ($C000 read / $C010 strobe clear) ---
    private byte _keyCode;     // 7-bit code (no strobe)
    private bool _strobe;      // bit 7

    /// <summary>The $C000 read value: bit 7 = strobe (a key is waiting), bits 6-0 = the code.</summary>
    public byte KeyboardByte => (byte)((_strobe ? 0x80 : 0x00) | (_keyCode & 0x7F));

    /// <summary>Latch a 7-bit ][+ key code and raise the strobe (a key arrived).</summary>
    public void LatchKey(byte code) { _keyCode = (byte)(code & 0x7F); _strobe = true; }

    /// <summary>$C010: clear the strobe (the program acknowledged the key); the code is retained.</summary>
    public void ClearStrobe() => _strobe = false;

    // --- Speaker ($C030: any access toggles the 1-bit flip-flop) ---
    /// <summary>How many times the speaker flip-flop has toggled (the Apple2Speaker reads + resets
    /// this each frame to rebuild the 1-bit waveform). One increment per $C030 bus access.</summary>
    public long SpeakerToggles { get; private set; }
    public void ToggleSpeaker() => SpeakerToggles++;
}
