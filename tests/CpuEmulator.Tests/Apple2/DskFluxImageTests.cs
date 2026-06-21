using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class DskFluxImageTests
{
    [Fact]
    public void Dos33_order_is_a_16_entry_permutation()
    {
        int[] map = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33);
        Assert.Equal(16, map.Length);
        Assert.Equal(Enumerable.Range(0, 16).ToHashSet(), map.ToHashSet()); // a permutation of 0..15
        Assert.Equal(0, map[0]);    // physical 0 == logical 0 (the DOS 3.3 anchor)
        Assert.Equal(15, map[15]);  // physical 15 == logical 15
    }

    [Fact]
    public void ProDos_order_is_a_16_entry_permutation_distinct_from_Dos33()
    {
        int[] po = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.ProDos);
        int[] dos = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33);
        Assert.Equal(16, po.Length);
        Assert.Equal(Enumerable.Range(0, 16).ToHashSet(), po.ToHashSet());
        Assert.NotEqual(dos, po);   // the two interleaves differ (that is the .dsk vs .po distinction)
    }

    // A 143,360-byte DOS 3.3 image (35 tracks x 16 sectors x 256). Each sector is filled with a byte
    // that encodes its (track, sector) so a recovered sector identifies itself.
    private static byte[] BuildDos33Image()
    {
        var img = new byte[35 * 16 * 256];
        for (int t = 0; t < 35; t++)
        for (int logical = 0; logical < 16; logical++)
        {
            int lba = t * 16 + logical;
            for (int i = 0; i < 256; i++)
                img[lba * 256 + i] = (byte)((t * 16 + logical + i) & 0xFF);
        }
        return img;
    }

    private static DskFluxImage Dos33Flux()
    {
        var block = new DiskImage(BuildDos33Image(), sectorSize: 256, isReadOnly: true);
        return new DskFluxImage(block, SectorOrderKind.Dos33);
    }

    [Fact]
    public void Track_count_is_sectors_over_16()
    {
        DskFluxImage flux = Dos33Flux();
        Assert.Equal(35, flux.TrackCount);   // 560 sectors / 16
    }

    [Fact]
    public void Every_byte_of_a_synthesized_track_is_a_valid_on_disk_byte()
    {
        DskFluxImage flux = Dos33Flux();
        ReadOnlySpan<byte> bits = flux.TrackBits(17);   // an arbitrary middle track
        Assert.True(flux.TrackBitLength(17) == bits.Length * 8);
        foreach (byte b in bits)
            Assert.True((b & 0x80) != 0, $"every nibble byte must have bit 7 set; got ${b:X2}");
    }

    [Fact]
    public void The_PR_F_head_reads_a_known_sector_back_out_of_a_renibblized_track()
    {
        // Drive the UNCHANGED PR-F controller over the re-nibblized track and software-decode a sector
        // the way RWTS does: scan for the data prologue D5 AA AD, pull 343 nibbles, 6-and-2 decode them.
        DskFluxImage flux = Dos33Flux();
        var disk = new Apple2DiskII(flux);
        disk.MotorOnForTest();

        // Pull a long run of nibbles off track 0 and find a D5 AA AD data field, then decode it.
        var stream = new List<byte>();
        for (int i = 0; i < 20_000; i++)
        {
            byte b = disk.ReadDataLatch();
            if ((b & 0x80) != 0) stream.Add(b);
        }
        Assert.True(TryReadFirstDataField(stream, out byte[] decoded),
            "expected a D5 AA AD data field of 343 GCR bytes that 6-and-2 decodes");

        // The decoded 256 bytes match SOME sector of track 0 (any of the 16 — we found the first one).
        Assert.Equal(256, decoded.Length);
        Assert.True(MatchesAnyTrack0Sector(decoded), "the decoded sector must match a real track-0 sector");
    }

    private static bool TryReadFirstDataField(List<byte> stream, out byte[] decoded)
    {
        decoded = [];
        for (int i = 0; i + 3 + 343 <= stream.Count; i++)
        {
            if (stream[i] == 0xD5 && stream[i + 1] == 0xAA && stream[i + 2] == 0xAD)
            {
                var gcr = stream.GetRange(i + 3, 343).ToArray();
                if (Apple2SectorCodec.TryDecodeData(gcr, out decoded)) return true;
            }
        }
        return false;
    }

    private static bool MatchesAnyTrack0Sector(byte[] decoded)
    {
        byte[] img = BuildDos33Image();
        for (int logical = 0; logical < 16; logical++)
        {
            var sector = new byte[256];
            Array.Copy(img, logical * 256, sector, 0, 256);
            if (sector.AsSpan().SequenceEqual(decoded)) return true;
        }
        return false;
    }
}
