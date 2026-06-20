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

    /// <summary>Build a Z80 board whose ROM at $0000 executes:
    ///   LD A,0x12 ; OUT (0x34),A  → port (A&lt;&lt;8)|n = 0x1234, value 0x12
    ///   LD A,0xFE ; IN  A,(0x00)  → port 0xFE00; PortEchoDevice returns high byte 0xFE
    ///   LD (0x8000),A ; HALT
    /// and assert the device saw port 0x1234 / value 0x12, and A==0xFE landed in RAM.</summary>
    private static byte[] PortProgramRom()
    {
        var rom = new byte[0x1000];
        int p = 0;
        rom[p++] = 0x3E; rom[p++] = 0x12;        // LD A,0x12
        rom[p++] = 0xD3; rom[p++] = 0x34;        // OUT (0x34),A   ; port 0x1234
        rom[p++] = 0x3E; rom[p++] = 0xFE;        // LD A,0xFE
        rom[p++] = 0xDB; rom[p++] = 0x00;        // IN  A,(0x00)   ; port 0xFE00
        rom[p++] = 0x32; rom[p++] = 0x00; rom[p++] = 0x80; // LD ($8000),A
        rom[p++] = 0x76;                         // HALT
        return rom;
    }

    private static BoardSpec PortProgramSpec(PortEchoDevice dev) =>
        new("port-prog", CpuKind.Z80, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Rom, PortProgramRom()),
                new MemoryRegion(0x1000, 0xF000, RegionKind.Ram),
                new MemoryRegion(0x0000, 0x10000, RegionKind.IoMmio, Space: PeripheralSpace.Io),
            ],
            Peripherals: [new PeripheralSlot("io-dev", dev, 0x0000, 0x10000, PeripheralSpace.Io)],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            IoAddressBits: 16);

    [Theory]
    [InlineData(ExecutionTier.Interpreter)]
    [InlineData(ExecutionTier.Jit)]
    public void Z80_out_and_in_route_the_full_16bit_port_to_the_io_device(ExecutionTier tier)
    {
        var dev = new PortEchoDevice();
        Machine machine = BoardMachineFactory.Build(PortProgramSpec(dev), tier);
        machine.Reset(); // Z80 resets to PC=0 (ROM)
        machine.Run(200);

        // OUT (0x34),A with A=0x12 → port (0x12<<8)|0x34 = 0x1234, value 0x12.
        Assert.Equal(0x1234u, dev.LastWritePort);
        Assert.Equal(0x12, dev.LastWriteValue);

        // IN A,(0x00) with A=0xFE → port 0xFE00; the device returns the high byte 0xFE; stored at $8000.
        Assert.Equal(0xFE, machine.Space(AddressSpaceKind.Program).Read8(0x8000));
    }

    [Fact]
    public void Io_device_partial_decode_ignores_odd_ports()
    {
        // A program that OUTs to an ODD port (bit 0 == 1) must NOT reach the device (ULA decode).
        var rom = new byte[0x1000];
        int p = 0;
        rom[p++] = 0x3E; rom[p++] = 0x99;        // LD A,0x99
        rom[p++] = 0xD3; rom[p++] = 0x01;        // OUT (0x01),A  ; port 0x9901, ODD → ignored
        rom[p++] = 0x76;                         // HALT
        var dev = new PortEchoDevice();
        var spec = new BoardSpec("odd-port", CpuKind.Z80, 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Rom, rom),
                new MemoryRegion(0x1000, 0xF000, RegionKind.Ram),
                new MemoryRegion(0x0000, 0x10000, RegionKind.IoMmio, Space: PeripheralSpace.Io),
            ],
            Peripherals: [new PeripheralSlot("io-dev", dev, 0x0000, 0x10000, PeripheralSpace.Io)],
            Irq: IrqWiring.None, Reset: ResetConfig.None, IoAddressBits: 16);

        Machine machine = BoardMachineFactory.Build(spec);
        machine.Reset();
        machine.Run(100);

        Assert.Equal(0xFFFFFFFFu, dev.LastWritePort); // never written — odd port not decoded
    }
}
