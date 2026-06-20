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

    private static BoardSpec Z80IoSpec(IPeripheral ioDevice) =>
        new("io-board", CpuKind.Z80, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Rom, new byte[0x1000]),
                new MemoryRegion(0x1000, 0xF000, RegionKind.Ram),
                new MemoryRegion(0x0000, 0x10000, RegionKind.IoMmio, Space: PeripheralSpace.Io),
            ],
            Peripherals:
            [
                new PeripheralSlot("io-dev", ioDevice, 0x0000, 0x10000, PeripheralSpace.Io),
            ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            IoAddressBits: 16);

    [Fact]
    public void A_well_formed_io_board_has_no_diagnostics()
    {
        Assert.Empty(BoardSpecValidator.Validate(Z80IoSpec(new PortEchoDevice())));
    }

    [Fact]
    public void An_io_slot_without_a_declared_io_space_is_flagged()
    {
        BoardSpec spec = Z80IoSpec(new PortEchoDevice()) with { IoAddressBits = 0 };
        IReadOnlyList<BoardDiagnostic> diags = BoardSpecValidator.Validate(spec);
        Assert.Contains(diags, d => d.Code == "io-space-undeclared");
    }

    [Fact]
    public void An_io_slot_outside_any_iommio_region_is_flagged()
    {
        BoardSpec spec = Z80IoSpec(new PortEchoDevice()) with
        {
            Memory =
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Rom, new byte[0x1000]),
                new MemoryRegion(0x1000, 0xF000, RegionKind.Ram),
                // no IoMmio region declared
            ],
        };
        IReadOnlyList<BoardDiagnostic> diags = BoardSpecValidator.Validate(spec);
        Assert.Contains(diags, d => d.Code == "io-slot-not-in-iommio");
    }
}
