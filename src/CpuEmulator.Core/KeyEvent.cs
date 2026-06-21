namespace CpuEmulator.Core;

/// <summary>Whether a key went down or came up.</summary>
public enum KeyAction
{
    Down,
    Up,
}

/// <summary>
/// One normalized keyboard event the host pushes to a machine's keyboard chip. <see cref="Key"/>
/// is the portable physical-key id; <see cref="Char"/> is the typed character when the host could
/// resolve one (e.g. 'A' for Shift+A) and null otherwise (key-up, or a non-printing key).
/// <see cref="Ctrl"/> is the Control-modifier state at the time of the event (the browser's
/// <c>KeyboardEvent.ctrlKey</c>) — defaulted false so every pre-existing 3-arg call site is
/// unchanged; the Apple ][+ keyboard chip ANDs a letter code with $1F when it is set (ADR 0014
/// Decision 3 / interactions §2.4), so Ctrl+B/Ctrl+C produce real control codes. Machines that
/// ignore Ctrl (e.g. the Spectrum, which uses its own CapsShift/SymbolShift) simply never read it.
/// </summary>
public readonly record struct KeyEvent(KeyAction Action, KeyCode Key, char? Char, bool Ctrl = false);
