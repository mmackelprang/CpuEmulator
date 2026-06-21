namespace CpuEmulator.Peripherals;

/// <summary>The Apple Disk II 6-and-2 GCR translate table (research §8): 64 valid on-disk bytes
/// ($96..$FF), each with the MSB set and at most two consecutive zero bits (the AGC noise-floor
/// constraint). WriteTable[v] maps a 6-bit value (0..63) to its on-disk byte; TryDecode inverts it.
/// A 256-byte sector encodes to 342 6-and-2 bytes + 1 checksum = 343 (the sector framing lives in the
/// .dsk adapter, PR-G; PR-F uses raw nibble streams). Pure + separately gated by the invariant.</summary>
public static class Apple2Gcr
{
    /// <summary>The 64 canonical 6-and-2 on-disk bytes, in 6-bit-value order (index = the source 6-bit
    /// value, value = the byte written to disk). This is the standard DOS 3.3 / Beneath-Apple-DOS table.</summary>
    public static readonly byte[] WriteTable =
    [
        0x96, 0x97, 0x9A, 0x9B, 0x9D, 0x9E, 0x9F, 0xA6,
        0xA7, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF, 0xB2, 0xB3,
        0xB4, 0xB5, 0xB6, 0xB7, 0xB9, 0xBA, 0xBB, 0xBC,
        0xBD, 0xBE, 0xBF, 0xCB, 0xCD, 0xCE, 0xCF, 0xD3,
        0xD6, 0xD7, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE,
        0xDF, 0xE5, 0xE6, 0xE7, 0xE9, 0xEA, 0xEB, 0xEC,
        0xED, 0xEE, 0xEF, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6,
        0xF7, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF,
    ];

    private static readonly int[] ReadTable = BuildReadTable();

    /// <summary>Map an on-disk byte back to its 6-bit value; false if the byte is not a valid GCR byte.</summary>
    public static bool TryDecode(byte diskByte, out int value)
    {
        value = ReadTable[diskByte];
        return value >= 0;
    }

    private static int[] BuildReadTable()
    {
        var t = new int[256];
        Array.Fill(t, -1);
        for (int v = 0; v < WriteTable.Length; v++)
            t[WriteTable[v]] = v;
        return t;
    }
}
