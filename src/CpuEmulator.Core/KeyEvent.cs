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
/// </summary>
public readonly record struct KeyEvent(KeyAction Action, KeyCode Key, char? Char);
