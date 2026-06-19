using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class ReferenceSbcTests
{
    [Fact]
    public void Z80_recipe_validates_clean()
    {
        var rom = new byte[0x2000];
        BoardSpec spec = ReferenceSbc.Build(CpuKind.Z80, new SimpleUart(), new IntervalTimer(), rom);

        Assert.Empty(BoardSpecValidator.Validate(spec));
        Assert.Equal(CpuKind.Z80, spec.Cpu);
        Assert.Equal(16, spec.AddressBits);
    }

    [Fact]
    public void Z80_recipe_puts_ram_low_and_rom_high()
    {
        var rom = new byte[0x2000];
        BoardSpec spec = ReferenceSbc.Build(CpuKind.Z80, new SimpleUart(), new IntervalTimer(), rom);

        Assert.Contains(spec.Memory, r => r.Kind == RegionKind.Ram && r.Start == 0x0000);
        Assert.Contains(spec.Memory, r => r.Kind == RegionKind.Rom && r.Start == 0xE000);
        Assert.Contains(spec.Peripherals, p => p.Name == "uart");
        Assert.Contains(spec.Peripherals, p => p.Name == "timer");
        Assert.Contains(spec.Irq.Lines, l => l.Target == CpuInterrupt.Irq);
    }

    [Fact]
    public void Deferred_cpu_kinds_throw()
    {
        var rom = new byte[0x2000];
        Assert.Throws<NotSupportedException>(() =>
            ReferenceSbc.Build(CpuKind.M68000, new SimpleUart(), new IntervalTimer(), rom));
        Assert.Throws<NotSupportedException>(() =>
            ReferenceSbc.Build(CpuKind.I8086, new SimpleUart(), new IntervalTimer(), rom));
    }
}
