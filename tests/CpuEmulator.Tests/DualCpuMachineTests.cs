using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class DualCpuMachineTests
{
    private sealed class Identity : IAddressTranslation
    {
        public uint ToPhysical(uint logical) => logical;
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
}
