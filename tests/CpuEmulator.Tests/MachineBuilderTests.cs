using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class MachineBuilderTests
{
    private static MachineBuilder MinimalBuilder() =>
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => new FakeCpu());

    [Fact]
    public void Build_requires_a_cpu()
    {
        var builder = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16);

        Assert.Throws<MachineConfigurationException>(() => builder.Build());
    }

    [Fact]
    public void Build_requires_a_program_space()
    {
        var builder = Machine.Create("test").WithCpu(_ => new FakeCpu());

        Assert.Throws<MachineConfigurationException>(() => builder.Build());
    }

    [Fact]
    public void Build_may_only_be_called_once()
    {
        var builder = MinimalBuilder();
        builder.Build();

        Assert.Throws<MachineConfigurationException>(() => builder.Build());
    }

    [Fact]
    public void Duplicate_space_declaration_throws()
    {
        Assert.Throws<MachineConfigurationException>(() =>
            Machine.Create("test")
                .WithAddressSpace(AddressSpaceKind.Program, 16)
                .WithAddressSpace(AddressSpaceKind.Program, 16));
    }

    [Fact]
    public void Cpu_factory_receives_context_with_memory_already_mapped()
    {
        var rom = new byte[0x100];
        rom[0] = 0xEA;
        IAddressSpace? seenSpace = null;

        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRom(AddressSpaceKind.Program, 0xFF00, rom)
            .WithCpu(ctx => { seenSpace = ctx.Space(AddressSpaceKind.Program); return new FakeCpu(); })
            .Build();

        Assert.NotNull(seenSpace);
        Assert.Equal(0xEA, seenSpace.Read8(0xFF00));
    }

    [Fact]
    public void Ram_and_rom_are_mapped_with_correct_writability()
    {
        var rom = new byte[0x100];
        rom[0] = 0x42;
        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x1000)
            .WithRom(AddressSpaceKind.Program, 0xFF00, rom)
            .WithCpu(_ => new FakeCpu())
            .Build();

        var space = machine.Space(AddressSpaceKind.Program);
        space.Write8(0x0010, 0x55);
        Assert.Equal(0x55, space.Read8(0x0010)); // RAM is writable
        space.Write8(0xFF00, 0x00);
        Assert.Equal(0x42, space.Read8(0xFF00)); // ROM is not
    }

    [Fact]
    public void Peripherals_are_mapped_then_realized_in_registration_order()
    {
        var log = new List<string>();
        var first = new RecordingPeripheral { Name = "first", RealizeLog = log };
        var second = new RecordingPeripheral { Name = "second", RealizeLog = log };

        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x100, first)
            .WithPeripheral(AddressSpaceKind.Program, 0xD100, 0x100, second)
            .WithCpu(_ => new FakeCpu())
            .Build();

        Assert.Equal(["first", "second"], log);
        Assert.Equal(1, first.RealizeCount);
        Assert.Same(machine, first.RealizedWith); // context IS the machine

        first.NextReadValue = 0x77;
        Assert.Equal(0x77, machine.Space(AddressSpaceKind.Program).Read8(0xD000));
    }

    [Fact]
    public void Irq_line_asserted_by_a_peripheral_reaches_the_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.IrqLine.Assert();
        Assert.True(cpu.IrqAsserted);
        machine.IrqLine.Release();
        Assert.False(cpu.IrqAsserted);
    }

    [Fact]
    public void Nmi_line_asserted_by_a_peripheral_reaches_the_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.NmiLine.Assert();
        Assert.True(cpu.NmiAsserted);
    }

    [Fact]
    public void Irq_asserted_during_cpu_construction_is_replayed_at_bind()
    {
        FakeCpu? cpu = null;
        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(ctx => { ctx.IrqLine.Assert(); cpu = new FakeCpu(); return cpu; })
            .Build();

        Assert.True(cpu!.IrqAsserted);
        Assert.True(machine.IrqLine.IsAsserted);
    }

    [Fact]
    public void Space_lookup_for_undeclared_kind_throws()
    {
        var machine = MinimalBuilder().Build();

        Assert.Throws<MachineConfigurationException>(
            () => machine.Space(AddressSpaceKind.Io));
    }

    [Fact]
    public void Reset_resets_the_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Reset();

        Assert.Equal(1, cpu.ResetCount);
    }

    [Fact]
    public void Irq_asserted_during_realize_reaches_the_cpu()
    {
        var cpu = new FakeCpu();
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x100, new IrqAssertingPeripheral())
            .WithCpu(_ => cpu)
            .Build();

        Assert.True(cpu.IrqAsserted);
    }

    private sealed class IrqAssertingPeripheral : IPeripheral
    {
        public string Name => "irq-asserter";
        public void Realize(IMachineContext context) => context.IrqLine.Assert();
        public uint Read(uint offset, AccessWidth width) => 0;
        public void Write(uint offset, AccessWidth width, uint value) { }
    }

    private static Machine MachineWith(FakeCpu cpu) =>
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => cpu)
            .Build();
}
