using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000WideBusTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        return (new M68000Cpu(bus), bus);
    }

    [Fact]
    public void Read_word_big_endian_and_charges_cycles()
    {
        var (cpu, bus) = Build();
        bus.Write8(0x2000, 0x12); bus.Write8(0x2001, 0x34);
        long before = cpu.CycleCount;
        ushort v = cpu.ReadWordBusProbe(0x2000);
        Assert.Equal((ushort)0x1234, v);                 // big-endian
        Assert.True(cpu.CycleCount > before);            // charged at least one cycle
    }

    [Fact]
    public void Read_long_is_two_word_accesses_high_word_first()
    {
        var (cpu, bus) = Build();
        bus.Write8(0x3000, 0x12); bus.Write8(0x3001, 0x34);   // high word
        bus.Write8(0x3002, 0x56); bus.Write8(0x3003, 0x78);   // low word
        Assert.Equal(0x12345678u, cpu.ReadLongBusProbe(0x3000));
    }

    [Fact]
    public void Write_word_then_read_round_trips_big_endian()
    {
        var (cpu, bus) = Build();
        cpu.WriteWordBusProbe(0x4000, 0xABCD);
        Assert.Equal((byte)0xAB, bus.Read8(0x4000));     // high byte at the lower address (BE)
        Assert.Equal((byte)0xCD, bus.Read8(0x4001));
    }
}
