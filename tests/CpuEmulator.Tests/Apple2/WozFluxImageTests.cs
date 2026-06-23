using CpuEmulator.Core;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class WozFluxImageTests
{
    // A 40-track image, but we only populate track 0 with a known bitstream; the rest are absent ($FF TMAP).
    private static byte[] OneTrackWoz(byte[] track0Bits, int track0BitLen, bool writeProtected = false,
                                      bool corruptCrc = false, bool wrongMagic = false, byte diskType = 1)
        => WozTestImage.Build(
            trackBits: [track0Bits],
            trackBitLengths: [track0BitLen],
            writeProtected: writeProtected,
            corruptCrc: corruptCrc, wrongMagic: wrongMagic, diskType: diskType);

    [Fact]
    public void Parses_track0_bits_and_bit_length_round_trip()
    {
        byte[] bits = [0xFF, 0xD5, 0xAA, 0x96, 0xDE, 0xAA, 0xEB, 0xFF];   // 8 bytes, 64 bits
        byte[] file = OneTrackWoz(bits, track0BitLen: 64, writeProtected: true);

        var woz = new WozFluxImage(file);

        Assert.True(woz.TrackCount >= 1);
        Assert.True(woz.IsWriteProtected);
        Assert.Equal(64, woz.TrackBitLength(0));
        Assert.True(woz.TrackBits(0).Slice(0, bits.Length).SequenceEqual(bits));
    }

    [Fact]
    public void An_absent_track_reads_empty()
    {
        byte[] file = OneTrackWoz([0xFF], track0BitLen: 8);
        var woz = new WozFluxImage(file);
        // Track 1 has TMAP[4] == $FF (no track) -> length 0, empty bits.
        Assert.Equal(0, woz.TrackBitLength(1));
        Assert.True(woz.TrackBits(1).IsEmpty);
    }
}
