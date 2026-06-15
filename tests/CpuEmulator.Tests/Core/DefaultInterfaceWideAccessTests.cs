using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Core;

/// <summary>M4.2 (ADR 0003 Decision 2 / Decision D1) — the default-interface-method wide path. A bespoke
/// IAddressSpace that implements ONLY Read8/Write8 gets a CORRECT wide path from the interface defaults,
/// with the LittleEndian default. This is what lets the test TracingAddressSpace (and any future double)
/// gain the wide path for free; the defaults compose over Read8/Write8.</summary>
public class DefaultInterfaceWideAccessTests
{
    /// <summary>A 64 KiB byte-array bus implementing ONLY the abstract IAddressSpace members. It does NOT
    /// override Read16/Read32/Write16/Write32 or Endianness, so those resolve to the interface DEFAULTS.</summary>
    private sealed class ByteArrayBus : IAddressSpace
    {
        private readonly byte[] _mem = new byte[0x10000];
        public AddressSpaceKind Kind => AddressSpaceKind.Program;
        public int AddressBits => 16;
        public byte Read8(uint address) => _mem[address & 0xFFFF];
        public void Write8(uint address, byte value) => _mem[address & 0xFFFF] = value;
        public bool TryPeek8(uint address, out byte value) { value = _mem[address & 0xFFFF]; return true; }
        public void MapMemory(uint start, byte[] backing, bool writable) { }
        public void MapPeripheral(uint start, uint length, IPeripheral peripheral) { }
    }

    [Fact]
    public void Default_endianness_is_little_endian()
    {
        // The Endianness DIM is reached through the interface (default interface members are not surfaced on
        // the concrete type unless re-declared — ByteArrayBus deliberately re-declares nothing).
        IAddressSpace bus = new ByteArrayBus();
        Assert.Equal(Endianness.LittleEndian, bus.Endianness);
    }

    [Fact]
    public void Default_Write16_is_little_endian_and_round_trips()
    {
        IAddressSpace bus = new ByteArrayBus();
        bus.Write16(0x0100, 0xABCD);
        Assert.Equal(0xCD, bus.Read8(0x0100));     // LE: low byte at the lower address
        Assert.Equal(0xAB, bus.Read8(0x0101));
        Assert.Equal((ushort)0xABCD, bus.Read16(0x0100));
    }

    [Fact]
    public void Default_Write32_is_little_endian_and_round_trips()
    {
        IAddressSpace bus = new ByteArrayBus();
        bus.Write32(0x0200, 0xDEADBEEF);
        Assert.Equal(0xEF, bus.Read8(0x0200));     // LE: least significant at the lower address
        Assert.Equal(0xDE, bus.Read8(0x0203));
        Assert.Equal(0xDEADBEEFu, bus.Read32(0x0200));
    }
}
