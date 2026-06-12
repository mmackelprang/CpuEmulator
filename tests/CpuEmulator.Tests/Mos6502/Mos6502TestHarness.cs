using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>Shared test helpers for 6502 trace and behaviour tests.</summary>
internal static class Mos6502TestHarness
{
    /// <summary>CPU with 64 KiB RAM, program bytes at 0x0200, PC set there, tracing bus.</summary>
    public static (Mos6502Cpu Cpu, TracingAddressSpace Bus) NewCpu(params byte[] program)
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

    /// <summary>CPU with program at a specified origin address, tracing bus.</summary>
    public static (Mos6502Cpu Cpu, TracingAddressSpace Bus) NewCpuAt(ushort origin, params byte[] program)
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

    public static void AssertTrace(TracingAddressSpace bus, params BusAccess[] expected) =>
        Assert.Equal(expected, bus.Trace);

    public static void AssertNZ(Mos6502Cpu cpu, bool n, bool z)
    {
        ulong p = cpu.GetRegister("P");
        Assert.Equal(n, (p & 0x80) != 0);
        Assert.Equal(z, (p & 0x02) != 0);
    }

    /// <summary>Asserts the C, V, N, Z bits of the P register.</summary>
    public static void AssertFlags(Mos6502Cpu cpu, bool c, bool v, bool n, bool z)
    {
        ulong p = cpu.GetRegister("P");
        Assert.Equal(c, (p & 0x01) != 0);
        Assert.Equal(v, (p & 0x40) != 0);
        Assert.Equal(n, (p & 0x80) != 0);
        Assert.Equal(z, (p & 0x02) != 0);
    }
}
