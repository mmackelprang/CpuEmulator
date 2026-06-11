using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class AddressSpaceMemoryTests
{
    private static AddressSpace NewSpace(AddressSpaceOptions? options = null) =>
        new(AddressSpaceKind.Program, addressBits: 16, options);

    [Fact]
    public void Ram_read_returns_what_was_written()
    {
        var space = NewSpace();
        space.MapMemory(0x0000, new byte[0x1000], writable: true);

        space.Write8(0x0123, 0xAB);

        Assert.Equal(0xAB, space.Read8(0x0123));
    }

    [Fact]
    public void Rom_exposes_image_contents()
    {
        var space = NewSpace();
        var image = new byte[0x100];
        image[0x10] = 0x42;
        space.MapMemory(0xFF00, image, writable: false);

        Assert.Equal(0x42, space.Read8(0xFF10));
    }

    [Fact]
    public void Rom_write_is_silently_ignored()
    {
        var space = NewSpace();
        var image = new byte[0x100];
        image[0x10] = 0x42;
        space.MapMemory(0xFF00, image, writable: false);

        space.Write8(0xFF10, 0x00); // authentic bus behavior: write to ROM does nothing

        Assert.Equal(0x42, space.Read8(0xFF10));
    }

    [Fact]
    public void Multi_page_ram_addresses_correct_backing_byte()
    {
        var space = NewSpace();
        var ram = new byte[0x1000];           // 16 pages
        space.MapMemory(0x2000, ram, writable: true);

        space.Write8(0x2ABC, 0x77);           // page 10 of the mapping

        Assert.Equal(0x77, ram[0x0ABC]);      // backing offset is mapping-relative
    }

    [Fact]
    public void Address_above_space_width_wraps()
    {
        var space = NewSpace();
        space.MapMemory(0x0000, new byte[0x100], writable: true);

        space.Write8(0x0042, 0x99);

        Assert.Equal(0x99, space.Read8(0x1_0042)); // 17-bit address masks to 16 bits
    }
}
