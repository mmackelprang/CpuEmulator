namespace CpuEmulator.Core;

/// <summary>
/// A portable physical-key identifier (USB-HID-usage-like), independent of any one machine's
/// scan matrix. The browser maps DOM key events to these; each machine's keyboard chip owns the
/// translation to its native scan codes (POKEY scan / 8255 PPI). Unknown keys map to
/// <see cref="None"/> (a no-op for the machine). SP0 covers the printable-ASCII + a few control
/// keys the demo program needs; real machines extend this as required (additive only).
/// </summary>
public enum KeyCode
{
    None = 0,

    // Letters
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    // Digits (top row)
    Digit0, Digit1, Digit2, Digit3, Digit4,
    Digit5, Digit6, Digit7, Digit8, Digit9,

    // Whitespace / editing
    Space,
    Enter,
    Backspace,
    Tab,
    Escape,

    // Arrows (the demo's moving cursor)
    ArrowLeft,
    ArrowRight,
    ArrowUp,
    ArrowDown,

    // ZX Spectrum modifier keys (additive; real machines extend KeyCode as needed).
    CapsShift,
    SymbolShift,
}
