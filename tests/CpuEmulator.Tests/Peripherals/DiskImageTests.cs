using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class DiskImageTests
{
    [Fact]
    public void ReadSector_returns_the_image_bytes_for_an_lba()
    {
        var bytes = new byte[256 * 2];
        bytes[256] = 0xAB;           // first byte of sector 1
        bytes[256 + 255] = 0xCD;     // last byte of sector 1
        var disk = new DiskImage(bytes, sectorSize: 256, isReadOnly: false);

        var dst = new byte[256];
        disk.ReadSector(1, dst);

        Assert.Equal(0xAB, dst[0]);
        Assert.Equal(0xCD, dst[255]);
        Assert.Equal(2, disk.SectorCount);
        Assert.Equal(256, disk.SectorSize);
    }

    [Fact]
    public void WriteSector_persists_into_the_image()
    {
        var disk = new DiskImage(new byte[256 * 2], sectorSize: 256, isReadOnly: false);
        var src = new byte[256];
        src[0] = 0x11;
        src[255] = 0x22;

        disk.WriteSector(0, src);

        var back = new byte[256];
        disk.ReadSector(0, back);
        Assert.Equal(0x11, back[0]);
        Assert.Equal(0x22, back[255]);
    }

    [Fact]
    public void WriteSector_throws_when_read_only()
    {
        var disk = new DiskImage(new byte[256], sectorSize: 256, isReadOnly: true);
        Assert.True(disk.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => disk.WriteSector(0, new byte[256]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Out_of_range_lba_throws(long lba)
    {
        var disk = new DiskImage(new byte[256 * 2], sectorSize: 256, isReadOnly: false);
        Assert.Throws<ArgumentOutOfRangeException>(() => disk.ReadSector(lba, new byte[256]));
    }

    [Fact]
    public void Wrong_size_destination_span_throws()
    {
        var disk = new DiskImage(new byte[256], sectorSize: 256, isReadOnly: false);
        Assert.Throws<ArgumentException>(() => disk.ReadSector(0, new byte[128]));
    }
}
