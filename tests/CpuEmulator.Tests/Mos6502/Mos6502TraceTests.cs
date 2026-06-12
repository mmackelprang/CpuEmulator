using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

public class Mos6502TraceTests
{
    /// <summary>CPU with 64 KiB RAM, program bytes at 0x0200, PC set there, tracing bus.</summary>
    private static (Mos6502Cpu Cpu, TracingAddressSpace Bus) NewCpu(params byte[] program)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        for (uint i = 0; i < program.Length; i++)
            inner.Write8(0x0200 + i, program[i]);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x0200);
        return (cpu, bus);
    }

    /// <summary>CPU with program at a specified origin address.</summary>
    private static (Mos6502Cpu Cpu, TracingAddressSpace Bus) NewCpuAt(ushort origin, params byte[] program)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        for (uint i = 0; i < program.Length; i++)
            inner.Write8((uint)(origin + i), program[i]);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", origin);
        return (cpu, bus);
    }

    private static void AssertTrace(TracingAddressSpace bus, params BusAccess[] expected) =>
        Assert.Equal(expected, bus.Trace);

    private static void AssertNZ(Mos6502Cpu cpu, bool n, bool z)
    {
        ulong p = cpu.GetRegister("P");
        Assert.Equal(n, (p & 0x80) != 0);
        Assert.Equal(z, (p & 0x02) != 0);
    }

    // ---- Task 3: Load class ----

    [Fact]
    public void LDA_immediate_2_cycles()
    {
        var (cpu, bus) = NewCpu(0xA9, 0x42);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Assert.Equal(0x0202ul, cpu.GetRegister("PC"));
        AssertNZ(cpu, n: false, z: false);
        AssertTrace(bus,
            new BusAccess(0x0200, 0xA9, true),
            new BusAccess(0x0201, 0x42, true));
    }

    [Fact]
    public void LDA_immediate_zero_sets_Z()
    {
        var (cpu, bus) = NewCpu(0xA9, 0x00);

        cpu.Step();

        Assert.Equal(0x00ul, cpu.GetRegister("A"));
        AssertNZ(cpu, n: false, z: true);
    }

    [Fact]
    public void LDA_immediate_negative_sets_N()
    {
        var (cpu, bus) = NewCpu(0xA9, 0x80);

        cpu.Step();

        Assert.Equal(0x80ul, cpu.GetRegister("A"));
        AssertNZ(cpu, n: true, z: false);
    }

    [Fact]
    public void LDA_zero_page_3_cycles()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        inner.Write8(0x0200, 0xA5);
        inner.Write8(0x0201, 0x10);
        inner.Write8(0x0010, 0x5A);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x0200);

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0x5Aul, cpu.GetRegister("A"));
        AssertTrace(bus,
            new BusAccess(0x0200, 0xA5, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x5A, true));
    }

    [Fact]
    public void LDA_absolute_4_cycles()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        inner.Write8(0x0200, 0xAD);
        inner.Write8(0x0201, 0x34);
        inner.Write8(0x0202, 0x12);
        inner.Write8(0x1234, 0x77);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x0200);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x77ul, cpu.GetRegister("A"));
        AssertTrace(bus,
            new BusAccess(0x0200, 0xAD, true),
            new BusAccess(0x0201, 0x34, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1234, 0x77, true));
    }

    [Fact]
    public void LDX_immediate_2_cycles()
    {
        var (cpu, bus) = NewCpu(0xA2, 0x07);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x07ul, cpu.GetRegister("X"));
        AssertTrace(bus,
            new BusAccess(0x0200, 0xA2, true),
            new BusAccess(0x0201, 0x07, true));
    }
}
