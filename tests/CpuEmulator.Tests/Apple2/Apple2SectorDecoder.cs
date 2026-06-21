using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

/// <summary>Test-only inverse of the DskFluxImage synthesis: walks a synthesized track's nibble stream,
/// finds the Nth physical sector's address field (D5 AA 96 ... with the 4-and-4 physical-sector number),
/// then decodes the first byte of its 6-and-2 data field. Used to prove WHICH logical sector landed at a
/// given physical slot (the per-track skew gate, ADR 0017 Decision 1).</summary>
internal static class Apple2SectorDecoder
{
    public static int FirstDataByteOfPhysicalSector(byte[] nibbles, int physSector)
    {
        // Find the address-field prologue D5 AA 96 whose 4-and-4 sector number == physSector, then the
        // following data-field prologue D5 AA AD, then decode the first 256-byte payload byte.
        for (int i = 0; i + 3 < nibbles.Length; i++)
        {
            if (nibbles[i] != 0xD5 || nibbles[i + 1] != 0xAA || nibbles[i + 2] != 0x96) continue;
            // 4-and-4: vol(2) track(2) sector(2) chk(2) -> the sector pair is bytes [i+7, i+8]. Guard the
            // address-field span (defensive — synthesized tracks are always complete, but a truncated stream
            // must not IndexOutOfRange this test-only walk).
            if (i + 8 >= nibbles.Length) break;
            int sector = Decode44(nibbles[i + 7], nibbles[i + 8]);
            if (sector != physSector) continue;
            int d = FindDataPrologue(nibbles, i + 3);
            if (d < 0 || d + 3 + 343 > nibbles.Length) return -1;
            // The data field is D5 AA AD | 343 GCR bytes | DE AA EB; TryDecodeData is the shipped inverse
            // of Apple2SectorCodec.EncodeData (returns the full 256-byte sector).
            var gcr = nibbles.AsSpan(d + 3, 343).ToArray();
            return Apple2SectorCodec.TryDecodeData(gcr, out byte[] data) ? data[0] : -1;
        }
        return -1;
    }

    private static int FindDataPrologue(byte[] n, int from)
    {
        for (int i = from; i + 3 < n.Length; i++)
            if (n[i] == 0xD5 && n[i + 1] == 0xAA && n[i + 2] == 0xAD) return i;
        return -1;
    }

    private static int Decode44(byte hi, byte lo) => ((hi << 1) | 1) & lo;
}
