using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using CpuEmulator.Peripherals.Woz;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

/// <summary>Builds a minimal-but-valid WOZ2 image in memory for the asset-free parser gates. Layout:
/// 8-byte header ("WOZ2" + FF 0A 0D 0A) + 4-byte CRC32 (LE, over all bytes after it) + INFO(60) + TMAP(160)
/// + TRKS (160 x 8-byte TRK records, then the bitstream blocks at 512-byte block granularity).
///
/// WOZ2 addresses a track's bits by the ABSOLUTE file offset starting_block*512 (the real rule the parser
/// uses). So the builder computes where the TRKS bit-block region actually lands in the assembled file and
/// pins each track's starting_block to that 512-aligned offset — the builder and the parser agree on the
/// absolute-offset convention. (For the canonical INFO(60)/TMAP(160)/TRKS-records(1280) layout the region
/// already lands on block 3 = byte 1536; the explicit computation keeps the fixture honest if that ever
/// drifts.)</summary>
internal static class WozTestImage
{
    public static byte[] Build(byte[][] trackBits, int[] trackBitLengths, bool writeProtected,
                               bool corruptCrc = false, bool wrongMagic = false, byte diskType = 1)
    {
        Assert.Equal(trackBits.Length, trackBitLengths.Length);

        // --- INFO chunk (60-byte payload) ---
        var info = new byte[60];
        info[0] = 2;                                   // INFO version 2
        info[1] = diskType;                            // 1 = 5.25"
        info[2] = (byte)(writeProtected ? 1 : 0);      // write-protected flag

        // --- TMAP chunk (160-byte payload): whole track t -> TRKS index t at quarter-track t*4 ---
        var tmap = new byte[160];
        for (int i = 0; i < 160; i++) tmap[i] = 0xFF;
        for (int t = 0; t < trackBits.Length; t++) tmap[t * 4] = (byte)t;

        // --- TRKS records: 160 x 8-byte TRK records. The bitstream blocks (512 each) follow the records in
        // the TRKS payload; their ABSOLUTE file offset is what starting_block*512 must equal. We resolve that
        // offset below (after we know the header+INFO+TMAP+TRKS-header sizes), then fill in starting_block. ---
        var trkRecords = new byte[160 * 8];
        var trkBlocks = new List<byte>();

        // The absolute byte offset of the first bitstream block = 12-byte header+CRC, then the INFO and TMAP
        // chunks (each 8-byte id+size header + payload), then the TRKS chunk's 8-byte id+size header, then the
        // 1280-byte record table. Compute it explicitly and 512-align it (padding the file if it ever isn't).
        int trksRecordTableBytes = trkRecords.Length;                            // 1280
        int bitRegionOffset = 12                                                 // header + CRC
                            + (8 + info.Length)                                  // INFO chunk
                            + (8 + tmap.Length)                                  // TMAP chunk
                            + 8                                                  // TRKS id + size
                            + trksRecordTableBytes;                              // TRKS record table
        // Pad the record table region up to a 512 boundary so the bit blocks land on block*512.
        int bitRegionPad = (512 - (bitRegionOffset % 512)) % 512;
        int firstBitBlock = (bitRegionOffset + bitRegionPad) / 512;
        if (bitRegionPad != 0) trkBlocks.AddRange(new byte[bitRegionPad]);       // left-pad to the block boundary

        int nextBlock = firstBitBlock;
        for (int t = 0; t < trackBits.Length; t++)
        {
            byte[] bits = trackBits[t];
            int blockCount = (bits.Length + 511) / 512;
            var padded = new byte[blockCount * 512];
            System.Array.Copy(bits, padded, bits.Length);
            trkBlocks.AddRange(padded);

            int off = t * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(trkRecords.AsSpan(off, 2), (ushort)nextBlock);     // starting_block
            BinaryPrimitives.WriteUInt16LittleEndian(trkRecords.AsSpan(off + 2, 2), (ushort)blockCount); // block_count
            BinaryPrimitives.WriteUInt32LittleEndian(trkRecords.AsSpan(off + 4, 4), (uint)trackBitLengths[t]); // bit_count
            nextBlock += blockCount;
        }

        // --- assemble the chunk stream (after the 12-byte header+CRC) ---
        var body = new List<byte>();
        AppendChunk(body, "INFO", info);
        AppendChunk(body, "TMAP", tmap);
        // TRKS payload = the 1280-byte record table + the (block-aligned) bitstream blocks.
        var trks = new List<byte>(trkRecords);
        trks.AddRange(trkBlocks);
        AppendChunk(body, "TRKS", trks.ToArray());

        return FinishFile(body.ToArray(), corruptCrc, wrongMagic);

        static void AppendChunk(List<byte> dst, string id, byte[] payload)
        {
            dst.AddRange(Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
            dst.AddRange(size);
            dst.AddRange(payload);
        }
    }

    private static byte[] FinishFile(byte[] body, bool corruptCrc, bool wrongMagic)
    {
        var header = new byte[8];
        byte[] magic = wrongMagic
            ? Encoding.ASCII.GetBytes("WOZ1")
            : Encoding.ASCII.GetBytes("WOZ2");
        System.Array.Copy(magic, header, 4);
        header[4] = 0xFF; header[5] = 0x0A; header[6] = 0x0D; header[7] = 0x0A;

        uint crc = WozCrc32.Compute(body);
        if (corruptCrc) crc ^= 0x1u;        // flip a bit so verification must fail

        var file = new List<byte>(header);
        var crcb = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcb, crc);
        file.AddRange(crcb);
        file.AddRange(body);
        return file.ToArray();
    }
}
