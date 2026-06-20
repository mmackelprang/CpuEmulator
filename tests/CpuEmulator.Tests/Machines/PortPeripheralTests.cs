using CpuEmulator.Core;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Machines;

public class PortPeripheralTests
{
    /// <summary>A synthetic Io-space device: records the last 16-bit port written, returns a value
    /// derived from the port read. Proves a board Io slot sees the FULL port address (A0..A15), not
    /// just the low byte. Partial decode: only answers EVEN ports (bit 0 == 0), the real ULA decode.</summary>
    private sealed class PortEchoDevice : IPeripheral
    {
        public uint LastWritePort = 0xFFFFFFFF;
        public byte LastWriteValue;
        public string Name => "port-echo";
        public void Realize(IMachineContext context) { }

        public uint Read(uint offset, AccessWidth width)
        {
            // offset IS the full 16-bit port address (HandlerBase == 0). Bit 0 == 1 → not decoded.
            if ((offset & 0x0001) != 0) return 0xFF;
            return (byte)(offset >> 8); // return the high address byte so A8..A15 visibility is provable
        }

        public void Write(uint offset, AccessWidth width, uint value)
        {
            if ((offset & 0x0001) != 0) return;
            LastWritePort = offset;
            LastWriteValue = (byte)value;
        }
    }

    [Fact]
    public void Slot_defaults_to_the_program_space()
    {
        var dev = new PortEchoDevice();
        var slot = new PeripheralSlot("port-echo", dev, 0x0000, 0x0100);
        Assert.Equal(PeripheralSpace.Program, slot.Space);
    }

    [Fact]
    public void Slot_can_target_the_io_space()
    {
        var dev = new PortEchoDevice();
        var slot = new PeripheralSlot("port-echo", dev, 0x0000, 0x10000, PeripheralSpace.Io);
        Assert.Equal(PeripheralSpace.Io, slot.Space);
    }

    [Fact]
    public void Region_defaults_to_the_program_space()
    {
        var region = new MemoryRegion(0x0000, 0x0100, RegionKind.IoMmio);
        Assert.Equal(PeripheralSpace.Program, region.Space);
    }
}
