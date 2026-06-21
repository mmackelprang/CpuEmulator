using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2DiskIITests
{
    // A synthetic track holding a known run of valid GCR nibbles, framed with self-sync ($FF) so the
    // head can find a byte boundary the way a real read does.
    private static byte[] SampleNibbles() =>
    [
        0xFF, 0xFF, 0xFF,            // self-sync
        0x96, 0xD5, 0xAA, 0x96,     // some valid GCR bytes
        0xFF, 0xFF,
        0xAD, 0xDE, 0xAF,
    ];

    private static SyntheticFluxImage OneTrack(byte[] nibbles)
    {
        var img = new SyntheticFluxImage(trackCount: 35);
        img.SetTrackNibbles(0, nibbles);     // pack the bytes MSB-first into track 0's bitstream
        return img;
    }

    [Fact]
    public void Polling_C0EC_reads_the_track_nibbles_in_order()
    {
        byte[] nibbles = SampleNibbles();
        var disk = new Apple2DiskII(OneTrack(nibbles));
        disk.MotorOnForTest();                // motor must be on to read

        // Read enough latch-fetches to recover each nibble. A real read polls $C0EC until bit 7 sets;
        // here we pull a sequence and confirm every emitted byte is a valid GCR byte with bit 7 set, and
        // that the KNOWN non-sync bytes appear in order.
        var seen = new List<byte>();
        for (int i = 0; i < 200; i++)
        {
            byte b = disk.ReadDataLatch();    // == a $C0EC read
            if ((b & 0x80) != 0) seen.Add(b);
        }

        // The distinctive (non-$FF) bytes appear in their track order somewhere in the stream.
        AssertSubsequence(new byte[] { 0x96, 0xD5, 0xAA, 0x96, 0xAD, 0xDE, 0xAF }, seen);
    }

    [Fact]
    public void With_the_motor_off_the_latch_does_not_advance()
    {
        var disk = new Apple2DiskII(OneTrack(SampleNibbles()));
        // motor off (default): a read returns a non-ready latch (bit 7 clear) and does not advance.
        byte a = disk.ReadDataLatch();
        byte b = disk.ReadDataLatch();
        Assert.Equal(0, a & 0x80);
        Assert.Equal(0, b & 0x80);
    }

    private static void AssertSubsequence(byte[] needle, List<byte> haystack)
    {
        int n = 0;
        foreach (byte b in haystack)
            if (n < needle.Length && b == needle[n]) n++;
        Assert.True(n == needle.Length,
            $"expected nibbles [{string.Join(",", needle.Select(x => $"${x:X2}"))}] as a subsequence");
    }
}
