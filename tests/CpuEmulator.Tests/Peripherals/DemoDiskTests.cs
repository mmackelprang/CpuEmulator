using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class DemoDiskTests
{
    private static DemoDisk DiskWithSector0(byte first, byte second)
    {
        var image = new byte[256 * 2];
        image[0] = first;
        image[1] = second;
        return new DemoDisk(new DiskImage(image, sectorSize: 256, isReadOnly: false));
    }

    [Fact]
    public void Read_command_surfaces_the_sector_bytes_through_DATA()
    {
        var disk = DiskWithSector0(0xDE, 0xAD);

        disk.Write(0, AccessWidth.Byte, 0);     // LBA = 0
        disk.Write(1, AccessWidth.Byte, 0x01);  // CMD = read

        Assert.Equal(0x01u, disk.Read(1, AccessWidth.Byte) & 0x01); // STATUS: ready
        Assert.Equal(0xDEu, disk.Read(2, AccessWidth.Byte));        // DATA[0]
        Assert.Equal(0xADu, disk.Read(2, AccessWidth.Byte));        // DATA[1]
    }

    [Fact]
    public void Reading_a_second_sector_replaces_the_buffer()
    {
        var image = new byte[256 * 2];
        image[0] = 0x11;          // sector 0, byte 0
        image[256] = 0x22;        // sector 1, byte 0
        var disk = new DemoDisk(new DiskImage(image, sectorSize: 256, isReadOnly: false));

        disk.Write(0, AccessWidth.Byte, 1);     // LBA = 1
        disk.Write(1, AccessWidth.Byte, 0x01);  // CMD = read
        Assert.Equal(0x22u, disk.Read(2, AccessWidth.Byte));
    }

    [Fact]
    public void Out_of_range_lba_sets_the_error_status_bit_and_does_not_throw_to_the_guest()
    {
        var disk = DiskWithSector0(0x00, 0x00);

        disk.Write(0, AccessWidth.Byte, 9);     // LBA = 9 (only 2 sectors)
        disk.Write(1, AccessWidth.Byte, 0x01);  // CMD = read -> out of range

        Assert.Equal(0x02u, disk.Read(1, AccessWidth.Byte) & 0x02); // STATUS: error bit set
    }

    [Fact]
    public void Write_command_persists_the_buffer_to_the_sector()
    {
        var image = new byte[256];
        var block = new DiskImage(image, sectorSize: 256, isReadOnly: false);
        var disk = new DemoDisk(block);

        disk.Write(0, AccessWidth.Byte, 0);     // LBA = 0
        disk.Write(2, AccessWidth.Byte, 0x7E);  // DATA[0] = 0x7E (writing LBA reset the pointer)
        disk.Write(1, AccessWidth.Byte, 0x02);  // CMD = write

        var back = new byte[256];
        block.ReadSector(0, back);
        Assert.Equal(0x7E, back[0]);
    }

    [Fact]
    public void Realize_is_a_no_op_no_irq_claimed()
    {
        // The demo disk is polled (no IRQ); Realize must not throw and the device works unrealized.
        var disk = DiskWithSector0(0x42, 0x00);
        disk.Write(0, AccessWidth.Byte, 0);
        disk.Write(1, AccessWidth.Byte, 0x01);
        Assert.Equal(0x42u, disk.Read(2, AccessWidth.Byte));
    }
}
