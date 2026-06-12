using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class PeekTests
{
    private sealed class NoPeekPeripheral : IPeripheral
    {
        public string Name => "noPeek";
        public int ReadCount { get; private set; }
        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) { ReadCount++; return 0x42u; }
        public void Write(uint offset, AccessWidth width, uint value) { }
        // Does NOT override TryPeek — uses the default (returns false)
    }

    private sealed class HonestPeripheral : IPeripheral
    {
        public string Name => "honest";
        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) => 0xDE;
        public void Write(uint offset, AccessWidth width, uint value) { }
        public bool TryPeek(uint offset, out byte value) { value = 0xAD; return true; }
    }

    [Fact]
    public void Default_TryPeek_returns_false()
    {
        IPeripheral p = new NoPeekPeripheral();
        bool result = p.TryPeek(0, out byte value);
        Assert.False(result);
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryPeek8_over_ram_returns_the_byte()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        var ram = new byte[0x100];
        ram[5] = 0xAB;
        space.MapMemory(0x0000, ram, writable: true);

        bool ok = space.TryPeek8(0x0005, out byte value);

        Assert.True(ok);
        Assert.Equal(0xAB, value);
    }

    [Fact]
    public void TryPeek8_over_rom_returns_the_byte()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        var rom = new byte[0x100];
        rom[10] = 0xCD;
        space.MapMemory(0x0200, rom, writable: false);

        bool ok = space.TryPeek8(0x020A, out byte value);

        Assert.True(ok);
        Assert.Equal(0xCD, value);
    }

    [Fact]
    public void TryPeek8_over_unmapped_returns_open_bus()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16,
            new AddressSpaceOptions { OpenBusValue = 0xFF });

        bool ok = space.TryPeek8(0x8000, out byte value);

        Assert.True(ok);
        Assert.Equal(0xFF, value);
    }

    [Fact]
    public void TryPeek8_over_unmapped_in_strict_mode_does_not_throw()
    {
        // A peek is a debugger view, not a bus transaction — no strict-mode throw.
        var space = new AddressSpace(AddressSpaceKind.Program, 16,
            new AddressSpaceOptions { Strict = true, OpenBusValue = 0xFF });

        bool ok = space.TryPeek8(0x8000, out byte value);

        Assert.True(ok);
        Assert.Equal(0xFF, value);
    }

    [Fact]
    public void TryPeek8_over_a_peripheral_without_peek_returns_false()
    {
        var p = new NoPeekPeripheral();
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0xD000], writable: true);
        space.MapPeripheral(0xD000, 0x0100, p);

        bool ok = space.TryPeek8(0xD000, out byte value);

        Assert.False(ok);
        Assert.Equal(0, value);
        Assert.Equal(0, p.ReadCount); // peek never silently falls back to Read
    }

    [Fact]
    public void TryPeek8_over_a_peripheral_with_peek_returns_its_value()
    {
        var p = new HonestPeripheral();
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0xD000], writable: true);
        space.MapPeripheral(0xD000, 0x0100, p);

        bool ok = space.TryPeek8(0xD000, out byte value);

        Assert.True(ok);
        Assert.Equal(0xAD, value);
    }
}
