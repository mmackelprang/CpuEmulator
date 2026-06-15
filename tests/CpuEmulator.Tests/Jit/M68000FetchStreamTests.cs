using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

public class M68000FetchStreamTests
{
    private static AddressSpace BigEndianBus()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        return bus;
    }

    [Fact]
    public void Reads_words_big_endian_from_the_origin()
    {
        var bus = BigEndianBus();
        // operword 0x1234 at 0x1000, next word 0x5678 at 0x1002 (big-endian: high byte first).
        bus.Write8(0x1000, 0x12); bus.Write8(0x1001, 0x34);
        bus.Write8(0x1002, 0x56); bus.Write8(0x1003, 0x78);
        var s = new M68000FetchStream(bus, origin: 0x1000);
        Assert.Equal(2, s.UnitBytes);
        Assert.Equal(0x1234u, s.NextUnit());   // first word, big-endian
        Assert.Equal(0x5678u, s.PeekUnit());   // peek does not advance
        Assert.Equal(0x5678u, s.NextUnit());
        Assert.Equal(2, s.UnitsConsumed);      // two words consumed
    }

    [Fact]
    public void Origin_is_uint_wide_not_ushort()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        bus.Write8(0x123456, 0xAB); bus.Write8(0x123457, 0xCD);
        var s = new M68000FetchStream(bus, origin: 0x123456);   // > 0xFFFF — would overflow a ushort origin
        Assert.Equal(0xABCDu, s.NextUnit());
    }
}
