using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Machines;

public class CpuCoreFactoryTests
{
    private static Machine MachineFor(CpuKind kind, ExecutionTier tier) =>
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x1000)
            .WithCpu(CpuCoreFactory.ForKind(kind, AddressSpaceKind.Program, tier))
            .Build();

    [Fact]
    public void Interpreter_tier_6502_builds_a_bare_core()
    {
        var machine = MachineFor(CpuKind.Mos6502, ExecutionTier.Interpreter);
        Assert.IsType<Mos6502Cpu>(machine.Cpu);
    }

    [Fact]
    public void Interpreter_tier_z80_builds_a_bare_core()
    {
        var machine = MachineFor(CpuKind.Z80, ExecutionTier.Interpreter);
        Assert.IsType<Z80Cpu>(machine.Cpu);
    }

    [Fact]
    public void Unsupported_kind_on_a_runnable_tier_throws()
    {
        // The 68000/8086 cores have no-op Reset stubs and cannot boot a board yet (piece #2).
        Assert.Throws<MachineConfigurationException>(() =>
            MachineFor(CpuKind.M68000, ExecutionTier.Interpreter));
    }

    [Fact]
    public void Jit_tier_6502_builds_a_JittedCpu()
    {
        var machine = MachineFor(CpuKind.Mos6502, ExecutionTier.Jit);
        Assert.IsType<JittedCpu<Mos6502Cpu>>(machine.Cpu);
        Assert.Equal("mos6502", machine.Cpu.Architecture);
    }

    [Fact]
    public void Jit_tier_z80_builds_a_JittedCpu()
    {
        var machine = MachineFor(CpuKind.Z80, ExecutionTier.Jit);
        Assert.IsType<JittedCpu<Z80Cpu>>(machine.Cpu);
    }
}
