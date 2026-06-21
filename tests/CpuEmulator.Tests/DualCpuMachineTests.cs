using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class DualCpuMachineTests
{
    private sealed class Identity : IAddressTranslation
    {
        public uint ToPhysical(uint logical) => logical;
    }

    // A minimal control-port peripheral for the gate: ANY access flips the active CPU (the SoftCard
    // models it as a write; here a write toggles). It captures the Machine via its Realize context
    // (Machine : IMachineContext, and the Machine implements ICoprocessorControl).
    private sealed class ToyControlPort : IPeripheral
    {
        private ICoprocessorControl? _ctl;
        private bool _active;
        public string Name => "toyctl";
        public void Realize(IMachineContext context)
        {
            if (context is ICoprocessorControl ctl) _ctl = ctl;
        }
        public uint Read(uint offset, AccessWidth width) => 0;
        public void Write(uint offset, AccessWidth width, uint value)
        {
            _active = !_active;
            _ctl?.SetCoprocessorActive(_active);
        }
    }

    [Fact]
    public void A_dual_cpu_machine_builds_a_primary_and_a_coprocessor()
    {
        var primary = new FakeCpu();
        var copro = new FakeCpu();
        var machine = Machine.Create("dual")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => primary)
            .WithCoprocessor(_ => copro, new Identity(), clockRatioToPrimary: 2.0)
            .Build();

        Assert.Same(primary, machine.Cpu);
        Assert.Same(copro, machine.Coprocessor);
        Assert.False(machine.CoprocessorActive); // the primary is active at reset
    }

    [Fact]
    public void A_single_cpu_machine_has_no_coprocessor()
    {
        var machine = Machine.Create("single")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => new FakeCpu())
            .Build();

        Assert.Null(machine.Coprocessor);
        Assert.False(machine.CoprocessorActive);
    }

    [Fact]
    public void Interrupts_route_to_the_primary_only()
    {
        var primary = new FakeCpu();
        var copro = new FakeCpu();
        var machine = Machine.Create("dual")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => primary)
            .WithCoprocessor(_ => copro, new Identity(), clockRatioToPrimary: 2.0)
            .Build();

        machine.IrqLine.Assert();
        Assert.True(primary.IrqAsserted);
        Assert.False(copro.IrqAsserted);   // the coprocessor is never interrupted (ADR 0015 Decision 5)
    }

    [Fact]
    public void Run_drives_the_primary_when_the_coprocessor_is_dormant()
    {
        var primary = new FakeCpu();
        var copro = new FakeCpu();
        var machine = Machine.Create("dual")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => primary)
            .WithCoprocessor(_ => copro, new Identity(), clockRatioToPrimary: 2.0)
            .Build();

        long primaryBefore = primary.CycleCount;
        long coproBefore = copro.CycleCount;
        machine.Run(100);

        Assert.True(primary.CycleCount > primaryBefore); // the primary ran
        Assert.Equal(coproBefore, copro.CycleCount);     // the dormant coprocessor did NOT run
    }

    [Fact]
    public void Run_drives_the_coprocessor_when_it_is_active()
    {
        var primary = new FakeCpu();
        var copro = new FakeCpu();
        var machine = Machine.Create("dual")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => primary)
            .WithCoprocessor(_ => copro, new Identity(), clockRatioToPrimary: 2.0)
            .Build();

        machine.SetCoprocessorActive(true); // hand off to the coprocessor
        long primaryBefore = primary.CycleCount;
        long coproBefore = copro.CycleCount;
        machine.Run(100);

        Assert.True(copro.CycleCount > coproBefore);     // the coprocessor ran
        Assert.Equal(primaryBefore, primary.CycleCount); // the suspended primary did NOT run
    }

    [Fact]
    public void Toy_board_switches_the_active_cpu_on_a_control_port_write_and_never_runs_the_dormant_core()
    {
        // 6502 RAM at $0000-$BFFF; the control port at $C000 (one page); a 12 KiB ROM at $D000 whose
        // reset vector points at a routine that writes $C000 (hand off to the Z80) then spins.
        var rom = new byte[0x3000];
        // $D000: 8D 00 C0   STA $C000   (write the control port -> hand off to the coprocessor)
        // $D003: 4C 03 D0   JMP $D003   (spin)
        rom[0x0000] = 0x8D; rom[0x0001] = 0x00; rom[0x0002] = 0xC0;
        rom[0x0003] = 0x4C; rom[0x0004] = 0x03; rom[0x0005] = 0xD0;
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000

        var ctl = new ToyControlPort();
        var spec = new BoardSpec("toydual", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),
                new MemoryRegion(0xC000, 0x0100, RegionKind.Mmio),   // the control-port page
                new MemoryRegion(0xC100, 0x0F00, RegionKind.Mmio),   // rest of the I/O band (unmapped hole)
                new MemoryRegion(0xD000, 0x3000, RegionKind.Rom, rom),
            ],
            Peripherals: [ new PeripheralSlot("toyctl", ctl, 0xC000, 0x0100) ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            Coprocessor: new CoprocessorSpec(
                CpuKind.Z80, new Identity(), "toyctl", ClockRatioToPrimary: 2.0));

        var machine = BoardMachineFactory.Build(spec); // interpreter tier
        machine.Reset();

        Assert.False(machine.CoprocessorActive);        // the 6502 starts active
        long z80Before = machine.Coprocessor!.CycleCount;

        machine.Run(100);                               // the 6502 runs, hits STA $C000, hands off

        Assert.True(machine.CoprocessorActive);         // the control-port write flipped the active CPU
        long z80After = machine.Coprocessor!.CycleCount;
        long six502After = machine.Cpu.CycleCount;

        machine.Run(100);                               // now the Z80 runs; the 6502 is suspended
        Assert.True(machine.Coprocessor!.CycleCount > z80After);   // the Z80 ran while active
        Assert.Equal(six502After, machine.Cpu.CycleCount);         // the suspended 6502 did NOT advance
    }
}
