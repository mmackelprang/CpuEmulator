using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

public class Mos6502SkeletonTests
{
    private static (Mos6502Cpu Cpu, AddressSpace Space) NewCpu(
        UndefinedOpcodePolicy policy = UndefinedOpcodePolicy.Throw)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return (new Mos6502Cpu(space, policy), space);
    }

    [Fact]
    public void Architecture_and_register_names_come_from_the_spec()
    {
        var (cpu, _) = NewCpu();

        Assert.Equal("mos6502", cpu.Architecture);
        Assert.Equal(["A", "X", "Y", "S", "P", "PC"], cpu.RegisterNames);
    }

    [Fact]
    public void Set_and_get_round_trip_with_width_truncation()
    {
        var (cpu, _) = NewCpu();

        cpu.SetRegister("A", 0x1FF);       // truncates to 8 bits
        cpu.SetRegister("PC", 0x1_8000);   // truncates to 16 bits

        Assert.Equal(0xFFul, cpu.GetRegister("A"));
        Assert.Equal(0x8000ul, cpu.GetRegister("PC"));
    }

    [Fact]
    public void Unknown_register_name_throws_ArgumentException()
    {
        var (cpu, _) = NewCpu();

        Assert.Throws<ArgumentException>(() => cpu.GetRegister("Q"));
        Assert.Throws<ArgumentException>(() => cpu.SetRegister("Q", 1));
    }

    [Fact]
    public void Reset_loads_the_vector_and_costs_seven_cycles()
    {
        var (cpu, space) = NewCpu();
        space.Write8(0xFFFC, 0x00);
        space.Write8(0xFFFD, 0x80);

        cpu.Reset();

        Assert.Equal(0x8000ul, cpu.GetRegister("PC"));
        Assert.Equal(0xFDul, cpu.GetRegister("S"));
        Assert.Equal(0x34ul, cpu.GetRegister("P"));
        Assert.Equal(7, cpu.CycleCount);
    }

    [Fact]
    public void Undefined_opcode_with_throw_policy_reports_opcode_and_address()
    {
        var (cpu, space) = NewCpu();
        space.Write8(0x0200, 0xFF);        // 0xFF is not in the subset
        cpu.SetRegister("PC", 0x0200);

        var ex = Assert.Throws<UndefinedOpcodeException>(cpu.Step);

        Assert.Equal(0xFF, ex.Opcode);
        Assert.Equal(0x0200u, ex.Address);
    }

    [Fact]
    public void Undefined_opcode_with_nop_policy_advances_two_cycles()
    {
        var (cpu, _) = NewCpu(UndefinedOpcodePolicy.Nop);   // RAM is zero-filled: opcode 0x00

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x0001ul, cpu.GetRegister("PC"));
    }

    [Fact]
    public void Run_consumes_budget_in_two_cycle_undefined_nops()
    {
        var (cpu, _) = NewCpu(UndefinedOpcodePolicy.Nop);

        long budget = 10;
        cpu.Run(ref budget);

        Assert.Equal(0, budget);
        Assert.Equal(10, cpu.CycleCount);   // 5 steps × 2 cycles
    }

    [Fact]
    public void Cpu_composes_with_Machine_through_the_builder()
    {
        var machine = Machine.Create("breadboard")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program),
                                           UndefinedOpcodePolicy.Nop))
            .Build();
        machine.Space(AddressSpaceKind.Program).Write8(0xFFFC, 0x00);
        machine.Space(AddressSpaceKind.Program).Write8(0xFFFD, 0x02);

        machine.Reset();
        long executed = machine.Run(20);

        Assert.Equal(20, executed);
        Assert.Equal(0x0200ul + 10, machine.Cpu.GetRegister("PC")); // 10 two-cycle NOP-policy steps
    }
}
