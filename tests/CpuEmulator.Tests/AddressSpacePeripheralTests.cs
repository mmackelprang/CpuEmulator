using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class AddressSpacePeripheralTests
{
    private static AddressSpace NewSpace() =>
        new(AddressSpaceKind.Program, addressBits: 16);

    [Fact]
    public void Read_routes_to_peripheral_with_mapping_relative_offset()
    {
        var space = NewSpace();
        var device = new RecordingPeripheral { NextReadValue = 0x5A };
        space.MapPeripheral(0xD000, 0x100, device);

        byte value = space.Read8(0xD010);

        Assert.Equal(0x5A, value);
        Assert.Equal((0x10u, AccessWidth.Byte), Assert.Single(device.Reads));
    }

    [Fact]
    public void Write_routes_to_peripheral_with_offset_width_and_value()
    {
        var space = NewSpace();
        var device = new RecordingPeripheral();
        space.MapPeripheral(0xD000, 0x100, device);

        space.Write8(0xD012, 0xCD);

        Assert.Equal((0x12u, AccessWidth.Byte, 0xCDu), Assert.Single(device.Writes));
    }

    [Fact]
    public void Multi_page_mapping_offsets_are_relative_to_mapping_base_not_page_base()
    {
        var space = NewSpace();
        var device = new RecordingPeripheral();
        space.MapPeripheral(0xC000, 0x200, device); // two pages

        space.Read8(0xC180);                        // second page of the mapping

        Assert.Equal(0x180u, Assert.Single(device.Reads).Offset);
    }

    [Fact]
    public void Peripheral_read_value_is_truncated_to_byte_on_a_byte_read()
    {
        var space = NewSpace();
        var device = new RecordingPeripheral { NextReadValue = 0x1FF };
        space.MapPeripheral(0xD000, 0x100, device);

        Assert.Equal(0xFF, space.Read8(0xD000));
    }
}
