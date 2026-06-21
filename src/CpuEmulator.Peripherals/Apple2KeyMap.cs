using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>Maps a portable <see cref="KeyCode"/> + typed <see cref="char"/> to the Apple ][+'s
/// uppercase-only 7-bit key code (research §6, ADR 0014 Decision 3) — the analogue of
/// <see cref="SpectrumKeyMatrix"/>. The ][+ has no lowercase: letters fold to their UPPERCASE ASCII
/// ($41..$5A); digits + symbols latch as their ASCII; Enter=$0D (CR), Space=$20, Backspace/left-arrow
/// =$08, Escape=$1B. The strobe (bit 7) is added by the latch, not here. An unmapped key (no dedicated
/// arm AND no printable Char) is a no-op — the host's unknown-key behaviour (the SpectrumKeyMatrix
/// contract). Pure + separately gated.</summary>
public static class Apple2KeyMap
{
    /// <summary>Translate one host key to a ][+ 7-bit code. Returns false (code 0) for a key the ][+
    /// keyboard does not produce. <paramref name="ch"/> is the host-resolved typed character (null for
    /// non-printing keys); when a key has no dedicated arm but carried a printable Char, that Char's
    /// uppercase ASCII is used (so host-localised symbols still reach the guest).</summary>
    public static bool TryMap(KeyCode key, char? ch, out byte code)
    {
        switch (key)
        {
            // Dedicated control keys (Char is typically null for these).
            case KeyCode.Enter: code = 0x0D; return true;        // CR
            case KeyCode.Backspace: code = 0x08; return true;    // BS / left-arrow
            case KeyCode.Escape: code = 0x1B; return true;
            case KeyCode.Space: code = 0x20; return true;
        }

        // Letters: fold to uppercase ASCII regardless of the typed case.
        if (key is >= KeyCode.A and <= KeyCode.Z)
        {
            code = (byte)('A' + (key - KeyCode.A));   // $41..$5A
            return true;
        }

        // Digits: the top-row 0..9 -> ASCII '0'..'9'.
        if (key is >= KeyCode.Digit0 and <= KeyCode.Digit9)
        {
            code = (byte)('0' + (key - KeyCode.Digit0));   // $30..$39
            return true;
        }

        // No dedicated key arm: if the host resolved a printable Char, latch its UPPERCASE ASCII.
        if (ch is char c && c is >= ' ' and <= '~')
        {
            code = (byte)char.ToUpperInvariant(c);
            return true;
        }

        code = 0;
        return false;   // unmapped -> no-op (the host's unknown-key contract)
    }
}
