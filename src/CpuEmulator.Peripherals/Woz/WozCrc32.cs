namespace CpuEmulator.Peripherals.Woz;

/// <summary>The standard zlib/PNG CRC32 (polynomial 0xEDB88320, init/final XOR 0xFFFFFFFF) — the algorithm
/// the WOZ container's header CRC field uses. src-resident (the only other copy in the tree is in the
/// tools/BootProbe PNG encoder, a dev tool we cannot reference from a shipped assembly).</summary>
public static class WozCrc32
{
    public static uint Compute(System.ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
