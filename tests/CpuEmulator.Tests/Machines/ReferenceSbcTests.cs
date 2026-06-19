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
    public void Build_68000_places_rom_low_with_vectors_and_ram_high()
    {
        var rom = new byte[0x1_0000];           // 64 KiB low ROM (vectors + program live here)
        BoardSpec spec = ReferenceSbc.Build(CpuKind.M68000, new SimpleUart(), new IntervalTimer(), rom);

        Assert.Equal(CpuKind.M68000, spec.Cpu);
        Assert.Equal(24, spec.AddressBits);                       // 16 MB 24-bit bus
        // ROM is the LOW region (starts at $0, so the reset vectors at $0/$4 are in ROM).
        MemoryRegion romRegion = Assert.Single(spec.Memory, r => r.Kind == RegionKind.Rom);
        Assert.Equal(0x0000_0000u, romRegion.Start);
        // RAM sits ABOVE the ROM (a higher start address).
        MemoryRegion ramRegion = Assert.Single(spec.Memory, r => r.Kind == RegionKind.Ram);
        Assert.True(ramRegion.Start > romRegion.Start);
        // The UART/timer slots land in a declared Mmio region (validated by BoardSpecValidator).
        Assert.Equal(2, spec.Peripherals.Count);
    }

    [Fact]
    public void Build_8086_places_rom_high_at_the_reset_entry_with_ram_low()
    {
        var rom = new byte[0x1_0000];           // 64 KiB high ROM (covers the 0xFFFF0 entry)
        BoardSpec spec = ReferenceSbc.Build(CpuKind.I8086, new SimpleUart(), new IntervalTimer(), rom);

        Assert.Equal(CpuKind.I8086, spec.Cpu);
        Assert.Equal(20, spec.AddressBits);                       // 1 MB 20-bit bus
        MemoryRegion romRegion = Assert.Single(spec.Memory, r => r.Kind == RegionKind.Rom);
        // ROM ends at the top of the 20-bit space (0x100000), so 0xFFFF0 is inside it.
        Assert.Equal(0x10_0000u, romRegion.Start + romRegion.Length);
        Assert.True(0xF_FFF0u >= romRegion.Start && 0xF_FFF0u < romRegion.Start + romRegion.Length);
        // RAM is the LOW region.
        MemoryRegion ramRegion = Assert.Single(spec.Memory, r => r.Kind == RegionKind.Ram);
        Assert.Equal(0x0000_0000u, ramRegion.Start);
    }

    [Fact]
    public void Build_68000_and_8086_specs_validate_clean()
    {
        var rom = new byte[0x1_0000];
        BoardSpec m68k = ReferenceSbc.Build(CpuKind.M68000, new SimpleUart(), new IntervalTimer(), rom);
        BoardSpec i86 = ReferenceSbc.Build(CpuKind.I8086, new SimpleUart(), new IntervalTimer(), rom);
        Assert.Empty(BoardSpecValidator.Validate(m68k));
        Assert.Empty(BoardSpecValidator.Validate(i86));
    }
}
