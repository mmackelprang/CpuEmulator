namespace CpuEmulator.Peripherals;

/// <summary>The DOS 3.3 6-and-2 sector framing (research §8): a 256-byte sector encodes to 342 6-and-2
/// bytes + 1 running-XOR checksum = 343 on-disk bytes, and the 4-and-4 address-field encoding for the
/// volume/track/sector/checksum prologue bytes. Composes the SHIPPED <see cref="Apple2Gcr"/> table (it
/// does NOT re-derive it). Pure + separately gated by round-trips. PR-G's <see cref="DskFluxImage"/>
/// uses this to re-nibblize an unprotected .dsk/.po logical-sector image into the IFluxImage track
/// bitstream the PR-F controller already reads — the controller is unchanged (the seam's whole point).</summary>
public static class Apple2SectorCodec
{
    public const int DataFieldNibbles = 343;   // 342 6-and-2 bytes + 1 checksum

    // ── 4-and-4: a data byte -> two on-disk bytes (odd bits then even bits, OR'd with 0xAA). ──
    /// <summary>Encode <paramref name="value"/> as the 4-and-4 pair (high = odd bits, low = even bits).
    /// Each output byte has bit 7 set and never more than two consecutive zero bits, so the head reads
    /// them as ordinary on-disk bytes.</summary>
    public static (byte hi, byte lo) Encode44(byte value)
    {
        byte hi = (byte)((value >> 1) | 0xAA);
        byte lo = (byte)(value | 0xAA);
        return (hi, lo);
    }

    /// <summary>Decode a 4-and-4 pair back to the original byte: ((hi &lt;&lt; 1) | 1) &amp; lo.</summary>
    public static byte Decode44(byte hi, byte lo) => (byte)(((hi << 1) | 1) & lo);

    // ── 6-and-2 data field: 256 bytes -> 343 on-disk bytes (the Beneath-Apple-DOS nibblize). ──
    /// <summary>Encode a 256-byte sector into the 343-byte 6-and-2 GCR data field. The low 2 bits of each
    /// byte (bit-reversed, packed three groups to a nibble) fill the first 86 entries; the high 6 bits
    /// fill the next 256; a running XOR over the 342 6-bit values yields the 343rd checksum value. Each
    /// 6-bit value is mapped through <see cref="Apple2Gcr.WriteTable"/> to its on-disk byte.</summary>
    public static byte[] EncodeData(byte[] sector)
    {
        ArgumentNullException.ThrowIfNull(sector);
        if (sector.Length != 256)
            throw new ArgumentException($"sector must be 256 bytes; got {sector.Length}.", nameof(sector));

        // 1) Build the 342 6-bit values: 86 "low 2 bits" values, then 256 "high 6 bits" values.
        var sixBit = new int[342];
        for (int i = 0; i < 256; i++)
            sixBit[86 + i] = (sector[i] >> 2) & 0x3F;     // high 6 bits

        // The first 86 values pack the low 2 bits of three source bytes each, bit-reversed (b0<->b1).
        for (int i = 0; i < 86; i++)
        {
            int v = 0;
            v |= Rev2(sector[i] & 0x03);                  // group A: bytes 0..85
            if (i + 86 < 256) v |= Rev2(sector[i + 86] & 0x03) << 2;   // group B: bytes 86..171
            if (i + 172 < 256) v |= Rev2(sector[i + 172] & 0x03) << 4; // group C: bytes 172..255
            sixBit[i] = v & 0x3F;
        }

        // 2) Running-XOR the 342 values into 342 "pre-nibblized" values, then append the final accumulator.
        var prenib = new int[343];
        int acc = 0;
        for (int i = 0; i < 342; i++)
        {
            prenib[i] = acc ^ sixBit[i];
            acc = sixBit[i];
        }
        prenib[342] = acc;                                // the checksum value (the last accumulator)

        // 3) Map each 6-bit value through the GCR write table to its on-disk byte.
        var gcr = new byte[343];
        for (int i = 0; i < 343; i++)
            gcr[i] = Apple2Gcr.WriteTable[prenib[i] & 0x3F];
        return gcr;
    }

    /// <summary>Decode a 343-byte 6-and-2 data field back to 256 bytes; false if any byte is not valid
    /// GCR or the running-XOR checksum does not reconcile. The exact inverse of <see cref="EncodeData"/>.</summary>
    public static bool TryDecodeData(byte[] gcr, out byte[] sector)
    {
        sector = [];
        if (gcr is null || gcr.Length != 343) return false;

        // Reverse the GCR table, then undo the running XOR.
        var sixBit = new int[343];
        for (int i = 0; i < 343; i++)
        {
            if (!Apple2Gcr.TryDecode(gcr[i], out int v)) return false;
            sixBit[i] = v;
        }
        var values = new int[342];
        int acc = 0;
        for (int i = 0; i < 342; i++)
        {
            acc ^= sixBit[i];
            values[i] = acc;
        }
        // The 343rd byte is the XOR checksum: after consuming all 342, acc must equal sixBit[342].
        if ((acc & 0x3F) != (sixBit[342] & 0x3F)) return false;

        // Reassemble the 256 bytes: high 6 bits from values[86..341], low 2 bits from values[0..85].
        var outBytes = new byte[256];
        for (int i = 0; i < 256; i++)
            outBytes[i] = (byte)((values[86 + i] & 0x3F) << 2);
        for (int i = 0; i < 86; i++)
        {
            int low = values[i] & 0x3F;
            outBytes[i] |= (byte)Rev2(low & 0x03);
            if (i + 86 < 256) outBytes[i + 86] |= (byte)Rev2((low >> 2) & 0x03);
            if (i + 172 < 256) outBytes[i + 172] |= (byte)Rev2((low >> 4) & 0x03);
        }
        sector = outBytes;
        return true;
    }

    /// <summary>Reverse a 2-bit value (swap bit 0 and bit 1) — the DOS 3.3 low-bit ordering.</summary>
    private static int Rev2(int b) => ((b & 1) << 1) | ((b >> 1) & 1);
}
