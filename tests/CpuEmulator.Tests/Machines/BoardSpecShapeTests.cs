using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class BoardSpecShapeTests
{
    [Fact]
    public void BoardSpec_composes_its_parts()
    {
        var uart = new SimpleUart();
        var spec = new BoardSpec(
            Name: "demo",
            Cpu: CpuKind.Mos6502,
            AddressBits: 16,
            Memory: [new MemoryRegion(0x0000, 0x1000, RegionKind.Ram)],
            Peripherals: [new PeripheralSlot("uart", uart, 0xD000, 0x0100)],
            Irq: new IrqWiring([new PeripheralIrq("uart", CpuInterrupt.Irq)]),
            Reset: ResetConfig.None);

        Assert.Equal("demo", spec.Name);
        Assert.Equal(CpuKind.Mos6502, spec.Cpu);
        Assert.Equal(RegionKind.Ram, spec.Memory[0].Kind);
        Assert.Same(uart, spec.Peripherals[0].Device);
        Assert.Equal(CpuInterrupt.Irq, spec.Irq.Lines[0].Target);
        Assert.Empty(spec.Reset.VectorPatches);
    }
}
