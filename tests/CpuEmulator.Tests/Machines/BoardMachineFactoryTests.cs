using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class BoardMachineFactoryTests
{
    private static byte[] Rom8k()
    {
        var rom = new byte[0x2000];
        rom[0] = 0xEA;                       // NOP at $E000
        rom[0x1FFC] = 0x00; rom[0x1FFD] = 0xE0; // RESET vector $FFFC/$FFFD -> $E000
        return rom;
    }

    private static BoardSpec MiniSpec(byte[] rom) =>
        new("mini", CpuKind.Mos6502, 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xD000, RegionKind.Ram),
                new MemoryRegion(0xD000, 0x1000, RegionKind.Mmio),
                new MemoryRegion(0xE000, 0x2000, RegionKind.Rom, rom),
            ],
            Peripherals: [new PeripheralSlot("uart", new SimpleUart(), 0xD000, 0x0100)],
            Irq: new IrqWiring([new PeripheralIrq("uart", CpuInterrupt.Irq)]),
            Reset: ResetConfig.None);

    [Fact]
    public void Build_maps_ram_rom_and_the_cpu()
    {
        Machine machine = BoardMachineFactory.Build(MiniSpec(Rom8k()));
        var space = machine.Space(AddressSpaceKind.Program);

        Assert.IsType<Mos6502Cpu>(machine.Cpu);
        space.Write8(0x0000, 0x5A);
        Assert.Equal(0x5A, space.Read8(0x0000));   // RAM writable
        Assert.Equal(0xEA, space.Read8(0xE000));   // ROM byte present
        space.Write8(0xE000, 0xFF);
        Assert.Equal(0xEA, space.Read8(0xE000));   // ROM read-only
    }

    [Fact]
    public void Build_resets_the_cpu_to_the_rom_vector()
    {
        Machine machine = BoardMachineFactory.Build(MiniSpec(Rom8k()));
        machine.Reset();

        Assert.Equal(0xE000u, machine.Cpu.GetRegister("PC"));
    }

    [Fact]
    public void Build_on_an_invalid_spec_throws_with_diagnostics()
    {
        var bad = MiniSpec(Rom8k()) with
        {
            Memory = [new MemoryRegion(0x0000, 0x2000, RegionKind.Ram),
                      new MemoryRegion(0x0800, 0x2000, RegionKind.Ram)], // overlap
        };

        var ex = Assert.Throws<BoardValidationException>(() => BoardMachineFactory.Build(bad));
        Assert.Contains(ex.Diagnostics, d => d.Code == "region-overlap");
    }
}
