using CpuEmulator.Core;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests;

public class CoprocessorValidationTests
{
    private sealed class Identity : IAddressTranslation
    {
        public uint ToPhysical(uint logical) => logical;
    }

    private sealed class NullPort : IPeripheral
    {
        public string Name => "ctl";
        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) => 0;
        public void Write(uint offset, AccessWidth width, uint value) { }
    }

    private static BoardSpec BaseSpec(CoprocessorSpec copro) =>
        new("v", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),
                new MemoryRegion(0xC000, 0x0100, RegionKind.Mmio),
            ],
            Peripherals: [ new PeripheralSlot("ctl", new NullPort(), 0xC000, 0x0100) ],
            Irq: IrqWiring.None, Reset: ResetConfig.None, Coprocessor: copro);

    [Fact]
    public void A_well_formed_coprocessor_spec_validates_clean()
    {
        var spec = BaseSpec(new CoprocessorSpec(CpuKind.Z80, new Identity(), "ctl", 2.0));
        Assert.Empty(BoardSpecValidator.Validate(spec));
    }

    [Fact]
    public void Control_port_naming_a_missing_slot_is_flagged()
    {
        var spec = BaseSpec(new CoprocessorSpec(CpuKind.Z80, new Identity(), "nope", 2.0));
        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "copro-control-port-unwired");
    }

    [Fact]
    public void A_non_positive_clock_ratio_is_flagged()
    {
        var spec = BaseSpec(new CoprocessorSpec(CpuKind.Z80, new Identity(), "ctl", 0.0));
        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "copro-bad-clock-ratio");
    }

    [Fact]
    public void A_null_translation_is_flagged()
    {
        // The record param is non-nullable; null! exercises the validator's defensive guard
        // (a future nullable-ref relaxation would otherwise reach a silent mis-wire).
        var spec = BaseSpec(new CoprocessorSpec(CpuKind.Z80, null!, "ctl", 2.0));
        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "copro-no-translation");
    }
}
