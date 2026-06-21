namespace CpuEmulator.Peripherals;

/// <summary>A SYNTHETIC Videx Videoterm character ROM (256 glyphs x 8 rows = 2048 bytes), the
/// <see cref="Apple2Font.Fallback"/> shape, used when no real Videx char ROM asset is fetched (PR-N's
/// render gate is asset-free; the real 2 KiB char ROM is the PR-O asset, get-videx-roms, injected the
/// same way Apple2Video injects the real Apple char ROM). Each glyph is byte [code*8 + row]; bit 6 is the
/// LEFTMOST of the 7-px cell (the Apple2Font order). The space ($20) is blank; every other printable code
/// ($21-$7E) gets a deterministic non-blank pattern so distinct character codes paint distinct, countable
/// ink — enough for the "VRAM of known codes -> structurally correct 80x24 RGBA" gate. Non-printables are
/// blank.</summary>
public static class VidexFont
{
    /// <summary>The 7-pixel-wide Videx character cell (bit 6..0 of each glyph row).</summary>
    public const int CellWidth = 7;

    /// <summary>The 8 active glyph rows the 2 KiB char ROM stores (the CRTC's 9-line cell, R9=$08, adds
    /// one blank descender line at render time — see VidexVideoterm.RenderInto).</summary>
    public const int GlyphRows = 8;

    /// <summary>256 glyphs x 8 rows = 2048 bytes; built once at type load.</summary>
    public static readonly byte[] Fallback = Build();

    private static byte[] Build()
    {
        var rom = new byte[256 * GlyphRows];
        // Printable ASCII $20-$7E. $20 (space) stays all-zero (blank). Every other printable code gets a
        // deterministic glyph: a centered box outline whose middle rows encode the low bits of the code,
        // so distinct codes carry distinct, countable ink (the render gate counts ink, not exact shapes).
        for (int code = 0x21; code <= 0x7E; code++)
        {
            // Top + bottom rows: a full 7-px bar (bits 6..0 set). Middle rows: a pattern from the code.
            rom[code * GlyphRows + 0] = 0x7F;                 // bits 6..0
            rom[code * GlyphRows + GlyphRows - 1] = 0x7F;
            for (int row = 1; row < GlyphRows - 1; row++)
            {
                // A code-dependent middle pattern (kept within bits 6..0); guarantees ink + per-code variety.
                int pattern = ((code >> (row & 3)) ^ (code << 1)) & 0x7F;
                if (pattern == 0) pattern = 0x08;             // never leave a fully-blank middle row
                rom[code * GlyphRows + row] = (byte)pattern;
            }
        }
        return rom;   // $20 and all non-printables remain blank (all-zero) — the inverse of "inked".
    }
}
