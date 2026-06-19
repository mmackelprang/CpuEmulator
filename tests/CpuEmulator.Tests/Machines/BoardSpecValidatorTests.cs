using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class BoardSpecValidatorTests
{
    private static BoardSpec Valid(
        IReadOnlyList<MemoryRegion>? memory = null,
        IReadOnlyList<PeripheralSlot>? peripherals = null,
        IrqWiring? irq = null,
        ResetConfig? reset = null,
        int addressBits = 16) =>
        new("test", CpuKind.Mos6502, addressBits,
            memory ?? [new MemoryRegion(0x0000, 0x1000, RegionKind.Ram)],
            peripherals ?? [],
            irq ?? IrqWiring.None,
            reset ?? ResetConfig.None);

    [Fact]
    public void Clean_spec_has_no_diagnostics()
    {
        Assert.Empty(BoardSpecValidator.Validate(Valid()));
    }

    [Fact]
    public void Overlapping_regions_are_flagged()
    {
        var spec = Valid(memory:
        [
            new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
            new MemoryRegion(0x0800, 0x1000, RegionKind.Ram),
        ]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "region-overlap");
    }

    [Fact]
    public void Region_past_address_width_is_flagged()
    {
        // addressBits 16 => top is 0xFFFF; a region ending at 0x1_0000 exceeds it.
        var spec = Valid(memory: [new MemoryRegion(0xF000, 0x2000, RegionKind.Ram)]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "region-out-of-range");
    }

    [Fact]
    public void Misaligned_region_start_is_flagged()
    {
        var spec = Valid(memory: [new MemoryRegion(0x0080, 0x0100, RegionKind.Ram)]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "region-misaligned");
    }

    [Fact]
    public void Region_length_not_a_page_multiple_is_flagged()
    {
        var spec = Valid(memory: [new MemoryRegion(0x0000, 0x0080, RegionKind.Ram)]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "region-misaligned");
    }

    [Fact]
    public void Peripheral_slot_outside_an_mmio_region_is_flagged()
    {
        // RAM at 0x0000-0x0FFF; the slot at 0xD000 lands in no Mmio region.
        var spec = Valid(
            memory: [new MemoryRegion(0x0000, 0x1000, RegionKind.Ram)],
            peripherals: [new PeripheralSlot("uart", new SimpleUart(), 0xD000, 0x0100)]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "slot-not-in-mmio");
    }

    [Fact]
    public void Peripheral_slot_inside_an_mmio_region_is_clean()
    {
        var spec = Valid(
            memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
                new MemoryRegion(0xD000, 0x1000, RegionKind.Mmio),
            ],
            peripherals: [new PeripheralSlot("uart", new SimpleUart(), 0xD000, 0x0100)]);

        Assert.DoesNotContain(BoardSpecValidator.Validate(spec), d => d.Code == "slot-not-in-mmio");
    }

    [Fact]
    public void Irq_line_naming_an_unknown_peripheral_is_flagged()
    {
        var spec = Valid(
            memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
                new MemoryRegion(0xD000, 0x1000, RegionKind.Mmio),
            ],
            peripherals: [new PeripheralSlot("uart", new SimpleUart(), 0xD000, 0x0100)],
            irq: new IrqWiring([new PeripheralIrq("nonexistent", CpuInterrupt.Irq)]));

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "irq-unwired");
    }

    [Fact]
    public void Rom_image_size_mismatch_is_flagged()
    {
        // Rom region declares length 0x1000 but the image is only 0x0800 bytes.
        var spec = Valid(memory:
        [
            new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
            new MemoryRegion(0xF000, 0x1000, RegionKind.Rom, new byte[0x0800]),
        ]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "rom-image-mismatch");
    }

    [Fact]
    public void Rom_region_without_an_image_is_flagged()
    {
        var spec = Valid(memory:
        [
            new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
            new MemoryRegion(0xF000, 0x1000, RegionKind.Rom),
        ]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "rom-image-mismatch");
    }

    [Fact]
    public void Vector_patch_outside_mapped_memory_is_flagged()
    {
        var spec = Valid(
            memory: [new MemoryRegion(0x0000, 0x1000, RegionKind.Ram)],
            reset: new ResetConfig([new VectorPatch(0xFFFC, 0x00)]));

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "vector-unmapped");
    }
}
