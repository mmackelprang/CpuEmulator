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

    // ---- Task 4: Register, store, jump classes ----

    [Fact]
    public void TAX_2_cycles_with_dummy_read()
    {
        var (cpu, bus) = NewCpu(0xAA, 0xEA); // TAX then NOP (deterministic dummy byte)
        cpu.SetRegister("A", 0x42);

        cpu.Step(); // execute TAX only

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("X"));
        Assert.Equal(0x0201ul, cpu.GetRegister("PC")); // dummy read does NOT advance PC
        AssertTrace(bus,
            new BusAccess(0x0200, 0xAA, true),
            new BusAccess(0x0201, 0xEA, true)); // dummy read at PC
    }

    [Fact]
    public void INX_wraps_FF_to_00_sets_Z()
    {
        var (cpu, bus) = NewCpu(0xE8); // INX; next byte is 0x00 (zeroed RAM)
        cpu.SetRegister("X", 0xFF);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x00ul, cpu.GetRegister("X"));
        AssertNZ(cpu, n: false, z: true);
    }

    [Fact]
    public void NOP_2_cycles()
    {
        var (cpu, bus) = NewCpu(0xEA); // NOP; next byte is 0x00

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        AssertTrace(bus,
            new BusAccess(0x0200, 0xEA, true),
            new BusAccess(0x0201, 0x00, true)); // dummy read at PC
    }

    [Fact]
    public void STA_zero_page_writes_A()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        inner.Write8(0x0200, 0x85);
        inner.Write8(0x0201, 0x10);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x0200);
        cpu.SetRegister("A", 0x99);

        // capture initial P to verify P unchanged
        ulong pBefore = cpu.GetRegister("P");

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0x99ul, cpu.GetRegister("A"));
        Assert.Equal(pBefore, cpu.GetRegister("P")); // store does not affect flags
        AssertTrace(bus,
            new BusAccess(0x0200, 0x85, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x99, false)); // write
        // verify RAM was actually written
        Assert.Equal(0x99, inner.Read8(0x0010));
    }

    [Fact]
    public void STA_absolute_writes_A()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        inner.Write8(0x0200, 0x8D);
        inner.Write8(0x0201, 0x34);
        inner.Write8(0x0202, 0x12);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x0200);
        cpu.SetRegister("A", 0x99);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        AssertTrace(bus,
            new BusAccess(0x0200, 0x8D, true),
            new BusAccess(0x0201, 0x34, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1234, 0x99, false));
    }

    [Fact]
    public void JMP_absolute_3_cycles()
    {
        var (cpu, bus) = NewCpu(0x4C, 0x00, 0x80);

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0x8000ul, cpu.GetRegister("PC")); // jumped, no read at 0x8000
        AssertTrace(bus,
            new BusAccess(0x0200, 0x4C, true),
            new BusAccess(0x0201, 0x00, true),
            new BusAccess(0x0202, 0x80, true));
    }

    // ---- Task 5: Branch class ----

    [Fact]
    public void BNE_not_taken_2_cycles()
    {
        // Z set => branch not taken; D0 05
        // P = 0x36 has Z set (bit 1 = 0x02)
        var (cpu, bus) = NewCpu(0xD0, 0x05);
        cpu.SetRegister("P", 0x36); // Z set

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x0202ul, cpu.GetRegister("PC")); // no branch
        AssertTrace(bus,
            new BusAccess(0x0200, 0xD0, true),
            new BusAccess(0x0201, 0x05, true));
    }

    [Fact]
    public void BNE_taken_3_cycles()
    {
        // Z clear => branch taken; D0 05; after operand PC=0x0202; target=0x0202+5=0x0207
        // Same page (0x02xx), so no page-cross dummy read.
        var (cpu, bus) = NewCpu(0xD0, 0x05);
        cpu.SetRegister("P", 0x34); // Z clear

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0x0207ul, cpu.GetRegister("PC"));
        // 3 accesses: opcode, offset, dummy at PC=0x0202
        Assert.Equal(3, bus.Trace.Count);
        Assert.Equal(new BusAccess(0x0200, 0xD0, true), bus.Trace[0]);
        Assert.Equal(new BusAccess(0x0201, 0x05, true), bus.Trace[1]);
        Assert.Equal(new BusAccess(0x0202, bus.Trace[2].Value, true), bus.Trace[2]); // dummy
    }

    [Fact]
    public void BNE_taken_backward_same_page_3_cycles()
    {
        // Place D0 FC at 0x0210; after operand read PC=0x0212; target=0x0212+(sbyte)0xFC=0x0212-4=0x020E
        // Both 0x0212 and 0x020E are in page 0x02xx => no page cross => 3 cycles.
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        inner.Write8(0x0210, 0xD0);
        inner.Write8(0x0211, 0xFC);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x0210);
        cpu.SetRegister("P", 0x34); // Z clear

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0x020Eul, cpu.GetRegister("PC"));
        Assert.Equal(3, bus.Trace.Count);
    }

    [Fact]
    public void BNE_taken_page_cross_4_cycles()
    {
        // Place D0 20 at 0x02F0; after operand read PC=0x02F2; target=0x02F2+0x20=0x0312
        // 0x02F2 is page 0x02xx; 0x0312 is page 0x03xx => page cross.
        // Wrong-page dummy read address = (PC & 0xFF00) | (target & 0x00FF) = 0x0200 | 0x0012 = 0x0212
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        inner.Write8(0x02F0, 0xD0);
        inner.Write8(0x02F1, 0x20);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x02F0);
        cpu.SetRegister("P", 0x34); // Z clear

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x0312ul, cpu.GetRegister("PC"));
        Assert.Equal(4, bus.Trace.Count);
        Assert.Equal(new BusAccess(0x02F0, 0xD0, true), bus.Trace[0]);
        Assert.Equal(new BusAccess(0x02F1, 0x20, true), bus.Trace[1]);
        Assert.Equal(new BusAccess(0x02F2, bus.Trace[2].Value, true), bus.Trace[2]); // dummy at PC
        Assert.Equal(new BusAccess(0x0212, bus.Trace[3].Value, true), bus.Trace[3]); // wrong-page dummy
    }
}
