using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Machines;

public class CpuCoreFactoryTests
{
    // A 16-bit space suits the 6502/Z80; the 68000/8086 need their own widths (24 / 20).
    private static Machine MachineFor(CpuKind kind, ExecutionTier tier, int addressBits = 16)
    {
        MachineBuilder b = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, addressBits)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x1000)
            .WithCpu(CpuCoreFactory.ForKind(kind, AddressSpaceKind.Program, tier));
        return b.Build();
    }

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
    public void Interpreter_tier_68000_builds_a_bare_core()
    {
        var machine = MachineFor(CpuKind.M68000, ExecutionTier.Interpreter, addressBits: 24);
        Assert.IsType<M68000Cpu>(machine.Cpu);
        Assert.Equal("m68000", machine.Cpu.Architecture);
    }

    [Fact]
    public void Interpreter_tier_8086_builds_a_bare_core()
    {
        var machine = MachineFor(CpuKind.I8086, ExecutionTier.Interpreter, addressBits: 20);
        Assert.IsType<M8086Cpu>(machine.Cpu);
        Assert.Equal("m8086", machine.Cpu.Architecture);
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

    [Fact]
    public void Jit_tier_68000_builds_a_JittedCpu()
    {
        var machine = MachineFor(CpuKind.M68000, ExecutionTier.Jit, addressBits: 24);
        Assert.IsType<JittedCpu<M68000Cpu>>(machine.Cpu);
        Assert.Equal("m68000", machine.Cpu.Architecture);
    }

    [Fact]
    public void Jit_tier_8086_builds_a_JittedCpu()
    {
        var machine = MachineFor(CpuKind.I8086, ExecutionTier.Jit, addressBits: 20);
        Assert.IsType<JittedCpu<M8086Cpu>>(machine.Cpu);
        Assert.Equal("m8086", machine.Cpu.Architecture);
    }

    [Fact]
    public void Z80_factory_routes_ports_to_the_supplied_io_space()
    {
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        var io = new AddressSpace(AddressSpaceKind.Io, 16);
        // Place a one-byte "device" in the Io space at port 0x00FE by backing one page and seeding it.
        io.MapMemory(0xFE00, new byte[0x0100], writable: true);
        io.Write8(0xFEFE, 0xA5); // port 0xFEFE

        var ctx = new StubContext(program, io);
        ICpuCore core = CpuCoreFactory.ForKind(CpuKind.Z80, AddressSpaceKind.Program, ExecutionTier.Interpreter)(ctx);
        var z80 = Assert.IsType<Z80Cpu>(core);

        // IN A,(0xFE) with A=0xFE forms port 0xFEFE; the core must read the supplied io space.
        Assert.Same(io, z80.IoBus);
    }

    private sealed class StubContext : IMachineContext
    {
        private readonly AddressSpace _program;
        private readonly AddressSpace? _io;
        public StubContext(AddressSpace program, AddressSpace? io = null) { _program = program; _io = io; }
        public IScheduler Scheduler => throw new NotSupportedException();
        public IAddressSpace Space(AddressSpaceKind kind) => kind switch
        {
            AddressSpaceKind.Program => _program,
            AddressSpaceKind.Io => _io ?? throw new InvalidOperationException("no io space"),
            _ => throw new NotSupportedException(),
        };
        public IInterruptLine IrqLine => throw new NotSupportedException();
        public IInterruptLine NmiLine => throw new NotSupportedException();
    }
}
