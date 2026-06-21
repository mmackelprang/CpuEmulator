namespace CpuEmulator.Peripherals;

/// <summary>A built-in fallback glyph set so the text-render gate runs WITHOUT the (Apple-copyright,
/// build-time-sourced) char-gen ROM — ADR 0014 Decision 8's default. 256 glyphs x 8 rows; each byte is
/// one row, bit 6..0 = the 7 horizontal pixels (bit 6 leftmost). PR-H injects the real 2 KiB char ROM
/// (same 256x8 layout) when fetched; until then this legible 7x8 set drives the render gate. The exact
/// glyph shapes are not load-bearing (the gate asserts cell placement + on/off pixels, not letterforms).</summary>
public static class Apple2Font
{
    /// <summary>256 glyphs * 8 rows = 2048 bytes. Built once at type load.</summary>
    public static readonly byte[] Fallback = Build();

    private static byte[] Build()
    {
        var f = new byte[256 * 8];
        // A minimal vector: uppercase A-Z (0x41-0x5A), digits 0-9 (0x30-0x39), and a few symbols get a
        // simple filled-box-with-hole glyph so they are visibly non-blank and distinct from space; the
        // rest stay blank. This is intentionally crude — the real ROM lands in PR-H.
        for (int code = 0; code < 256; code++)
        {
            bool printable = (code >= 0x20 && code <= 0x7E);
            if (!printable || code == 0x20) continue; // space + non-printables stay blank
            // A 5x7 outline box inside the 7x8 cell: rows 0..6 use bits 5..1.
            for (int row = 0; row < 7; row++)
            {
                byte bits = row is 0 or 6
                    ? (byte)0b0111110          // top/bottom edge
                    : (byte)0b0100010;         // left/right edges
                // Add a code-dependent interior pixel so glyphs are not all identical (distinguishes
                // adjacent codes in a coarse but deterministic way).
                if (row == 3 && (code & 1) != 0) bits |= 0b0001000;
                f[code * 8 + row] = bits;
            }
        }
        return f;
    }
}
