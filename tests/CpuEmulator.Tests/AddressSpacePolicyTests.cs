using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class AddressSpacePolicyTests
{
    private static AddressSpace NewSpace(AddressSpaceOptions? options = null) =>
        new(AddressSpaceKind.Program, addressBits: 16, options);

    // --- open bus (guest-world behavior, never throws by default) ---

    [Fact]
    public void Unmapped_read_returns_default_open_bus_value()
    {
        Assert.Equal(0xFF, NewSpace().Read8(0x8000));
    }

    [Fact]
    public void Open_bus_value_is_configurable()
    {
        var space = NewSpace(new AddressSpaceOptions { OpenBusValue = 0x00 });
        Assert.Equal(0x00, space.Read8(0x8000));
    }

    [Fact]
    public void Unmapped_write_is_silently_ignored()
    {
        var space = NewSpace();
        space.Write8(0x8000, 0xAB); // must not throw
    }

    // --- strict mode (opt-in host-visible failures) ---

    [Fact]
    public void Strict_read_from_unmapped_address_throws()
    {
        var space = NewSpace(new AddressSpaceOptions { Strict = true });
        Assert.Throws<StrictBusViolationException>(() => space.Read8(0x8000));
    }

    [Fact]
    public void Strict_write_to_unmapped_address_throws()
    {
        var space = NewSpace(new AddressSpaceOptions { Strict = true });
        Assert.Throws<StrictBusViolationException>(() => space.Write8(0x8000, 0x01));
    }

    [Fact]
    public void Strict_write_to_rom_throws()
    {
        var space = NewSpace(new AddressSpaceOptions { Strict = true });
        space.MapMemory(0xFF00, new byte[0x100], writable: false);
        Assert.Throws<StrictBusViolationException>(() => space.Write8(0xFF00, 0x01));
    }

    // --- mapping validation (host-world configuration errors) ---

    [Fact]
    public void Misaligned_mapping_start_throws()
    {
        Assert.Throws<MachineConfigurationException>(
            () => NewSpace().MapMemory(0x0080, new byte[0x100], writable: true));
    }

    [Fact]
    public void Non_page_multiple_length_throws()
    {
        Assert.Throws<MachineConfigurationException>(
            () => NewSpace().MapMemory(0x0000, new byte[0x80], writable: true));
    }

    [Fact]
    public void Overlapping_mappings_throw()
    {
        var space = NewSpace();
        space.MapMemory(0x0000, new byte[0x200], writable: true);
        Assert.Throws<MachineConfigurationException>(
            () => space.MapPeripheral(0x0100, 0x100, new RecordingPeripheral()));
    }

    [Fact]
    public void Mapping_beyond_address_space_throws()
    {
        Assert.Throws<MachineConfigurationException>(
            () => NewSpace().MapMemory(0xFF00, new byte[0x200], writable: true));
    }

    [Fact]
    public void Address_bits_outside_8_to_24_throw()
    {
        Assert.Throws<MachineConfigurationException>(
            () => new AddressSpace(AddressSpaceKind.Program, addressBits: 25));
        Assert.Throws<MachineConfigurationException>(
            () => new AddressSpace(AddressSpaceKind.Program, addressBits: 7));
    }
}
