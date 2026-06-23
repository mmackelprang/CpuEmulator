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

    [Fact]
    public void Rejects_a_woz_with_a_wrong_crc32()
    {
        byte[] file = OneTrackWoz([0xFF, 0xD5, 0xAA], track0BitLen: 24, corruptCrc: true);
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => new WozFluxImage(file));
        Assert.Contains("CRC32", ex.Message);
    }

    [Fact]
    public void Rejects_woz1_magic()
    {
        byte[] file = OneTrackWoz([0xFF], track0BitLen: 8, wrongMagic: true);   // "WOZ1"
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => new WozFluxImage(file));
        Assert.Contains("WOZ1", ex.Message);
    }

    [Fact]
    public void Rejects_a_non_525_disk_type()
    {
        byte[] file = OneTrackWoz([0xFF], track0BitLen: 8, diskType: 2);   // 2 = 3.5"
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => new WozFluxImage(file));
        Assert.Contains("5.25", ex.Message);
    }

    [Fact]
    public void DiskImageFactory_builds_a_WozFluxImage_from_woz_bytes()
    {
        byte[] file = OneTrackWoz([0xFF, 0xD5, 0xAA, 0xAD], track0BitLen: 32);
        IFluxImage flux = CpuEmulator.Surface.Web.DiskImageFactory.FromBytes(
            file, CpuEmulator.Surface.Web.DiskFormat.Woz);
        Assert.IsType<WozFluxImage>(flux);
        Assert.Equal(32, flux.TrackBitLength(0));
    }

    [WozDiskFact]
    public void A_real_woz_boots_through_the_live_disk_ii_head()
    {
        // The un-fakeable gate: a REAL fetch-on-demand (never-vendored) .woz is parsed, its CRC32 verified on
        // the real bytes (the WozFluxImage ctor throws on mismatch), and its track-0 bitstream is read by the
        // LIVE Apple2DiskII head — the controller finds a real address-field prologue D5 AA 96 in the nibble
        // stream it shifts out (the same proof DskFluxImageTests uses, but over real .woz bytes).
        byte[] file = CpuEmulator.Machines.WozAsset.Load();
        var woz = new WozFluxImage(file);

        var disk = new CpuEmulator.Peripherals.Apple2DiskII(woz);
        disk.MotorOnForTest();

        var stream = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < 60_000; i++)
        {
            byte b = disk.ReadDataLatch();
            if ((b & 0x80) != 0) stream.Add(b);
        }
        // A real Apple disk has an address-field prologue D5 AA 96 on track 0 (every RWTS-readable disk does).
        bool foundAddrPrologue = false;
        for (int i = 0; i + 2 < stream.Count; i++)
            if (stream[i] == 0xD5 && stream[i + 1] == 0xAA && stream[i + 2] == 0x96) { foundAddrPrologue = true; break; }
        Assert.True(foundAddrPrologue,
            "the live Disk II head must find a D5 AA 96 address prologue in the real .woz track-0 bitstream");
    }
}
