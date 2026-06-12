using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>
/// Cycle-exact trace tests for the RMW instruction class: ASL, LSR, ROL, ROR, INC, DEC
/// (memory and accumulator forms) and the register Decrement ops DEX/DEY.
/// The defining invariant: every memory RMW emits a DUMMY WRITE of the unmodified value
/// before the write of the modified value. Silicon ground truth: plan cycle-template table.
/// </summary>
public class Mos6502RmwTraceTests
{
    private static (Mos6502Cpu Cpu, TracingAddressSpace Bus, AddressSpace Inner)
        NewCpuWithInner(params byte[] program)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        for (uint i = 0; i < program.Length; i++)
            inner.Write8(0x0200 + i, program[i]);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x0200);
        return (cpu, bus, inner);
    }

    // ── ASL: shift left ────────────────────────────────────────────────────────

    [Fact]
    public void ASL_abs_double_write_6_cycles()
    {
        // ASL $1234 (0x0E); RAM[0x1234]=0x81 => result=0x02, C=1 (bit7 was 1), N=0, Z=0
        var (cpu, bus, inner) = NewCpuWithInner(0x0E, 0x34, 0x12);
        inner.Write8(0x1234, 0x81);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x02, inner.Read8(0x1234));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x0E, true),
            new BusAccess(0x0201, 0x34, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1234, 0x81, true),
            new BusAccess(0x1234, 0x81, false), // dummy write of the unmodified value
            new BusAccess(0x1234, 0x02, false)); // write modified
        Mos6502TestHarness.AssertFlags(cpu, c: true, v: false, n: false, z: false);
    }

    [Fact]
    public void ASL_zp_5_cycles()
    {
        // ASL $10 (0x06); RAM[0x10]=0x40 => result=0x80, N=1, C=0
        var (cpu, bus, inner) = NewCpuWithInner(0x06, 0x10);
        inner.Write8(0x0010, 0x40);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x80, inner.Read8(0x0010));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x06, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x40, true),
            new BusAccess(0x0010, 0x40, false), // dummy write
            new BusAccess(0x0010, 0x80, false));
        Mos6502TestHarness.AssertFlags(cpu, c: false, v: false, n: true, z: false);
    }

    // ── ROL ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROL_zp_carry_in_and_out()
    {
        // ROL $10 (0x26); P=C=1; RAM[0x10]=0x80 => result=(0x80<<1)|1=0x01, C=1 (old bit7)
        var (cpu, bus, inner) = NewCpuWithInner(0x26, 0x10);
        inner.Write8(0x0010, 0x80);
        cpu.SetRegister("P", 0x01); // C=1

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x01, inner.Read8(0x0010));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x26, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x80, true),
            new BusAccess(0x0010, 0x80, false), // dummy write
            new BusAccess(0x0010, 0x01, false));
        Mos6502TestHarness.AssertFlags(cpu, c: true, v: false, n: false, z: false);
    }

    // ── ROR ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ROR_zp_carry_in()
    {
        // ROR $10 (0x66); P=C=1; RAM[0x10]=0x01 => result=(0x01>>1)|(1<<7)=0x80, C=1 (old bit0)
        var (cpu, bus, inner) = NewCpuWithInner(0x66, 0x10);
        inner.Write8(0x0010, 0x01);
        cpu.SetRegister("P", 0x01); // C=1

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x80, inner.Read8(0x0010));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x66, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x01, true),
            new BusAccess(0x0010, 0x01, false), // dummy write
            new BusAccess(0x0010, 0x80, false));
        Mos6502TestHarness.AssertFlags(cpu, c: true, v: false, n: true, z: false);
    }

    // ── LSR ──────────────────────────────────────────────────────────────────

    [Fact]
    public void LSR_zp_clears_N_always()
    {
        // LSR $10 (0x46); RAM[0x10]=0x01 => result=0x00, C=1, Z=1, N=0 (always 0 for LSR)
        var (cpu, bus, inner) = NewCpuWithInner(0x46, 0x10);
        inner.Write8(0x0010, 0x01);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x00, inner.Read8(0x0010));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x46, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x01, true),
            new BusAccess(0x0010, 0x01, false), // dummy write
            new BusAccess(0x0010, 0x00, false));
        Mos6502TestHarness.AssertFlags(cpu, c: true, v: false, n: false, z: true);
    }

    // ── INC/DEC ──────────────────────────────────────────────────────────────

    [Fact]
    public void INC_zpX_6_cycles()
    {
        // INC $10,X (0xF6); X=5; RAM[0x15]=0xFF => result=0x00, Z=1; dummy read at $10 first
        var (cpu, bus, inner) = NewCpuWithInner(0xF6, 0x10);
        inner.Write8(0x0015, 0xFF);
        cpu.SetRegister("X", 5);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x00, inner.Read8(0x0015));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xF6, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x00, true),  // dummy read at unindexed zp
            new BusAccess(0x0015, 0xFF, true),
            new BusAccess(0x0015, 0xFF, false), // dummy write
            new BusAccess(0x0015, 0x00, false));
        Mos6502TestHarness.AssertFlags(cpu, c: false, v: false, n: false, z: true);
    }

    [Fact]
    public void DEC_zp_5_cycles()
    {
        // DEC $10 (0xC6); RAM[0x10]=0x01 => result=0x00, Z=1
        var (cpu, bus, inner) = NewCpuWithInner(0xC6, 0x10);
        inner.Write8(0x0010, 0x01);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x00, inner.Read8(0x0010));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xC6, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x01, true),
            new BusAccess(0x0010, 0x01, false), // dummy write
            new BusAccess(0x0010, 0x00, false));
        Mos6502TestHarness.AssertFlags(cpu, c: false, v: false, n: false, z: true);
    }

    [Fact]
    public void INC_absX_7_cycles_dummy_read_always()
    {
        // INC $1200,X (0xFE); X=5; RAM[0x1205]=0x41 => result=0x42
        // abs,X RMW always does dummy read at wrong addr before real read — even if no page cross
        var (cpu, bus, inner) = NewCpuWithInner(0xFE, 0x00, 0x12);
        inner.Write8(0x1205, 0x41);
        cpu.SetRegister("X", 5);

        cpu.Step();

        Assert.Equal(7, cpu.CycleCount);
        Assert.Equal(0x42, inner.Read8(0x1205));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xFE, true),
            new BusAccess(0x0201, 0x00, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1205, 0x41, true),  // dummy read at wrong (== right on no-cross) addr
            new BusAccess(0x1205, 0x41, true),  // real read at ea
            new BusAccess(0x1205, 0x41, false), // dummy write
            new BusAccess(0x1205, 0x42, false));
    }

    // ── Accumulator form ──────────────────────────────────────────────────────

    [Fact]
    public void ASL_accumulator_2_cycles()
    {
        // ASL A (0x0A); A=0x81 => result=0x02, C=1, N=0; dummy read at PC (no increment)
        var (cpu, bus) = Mos6502TestHarness.NewCpu(0x0A, 0x00);
        cpu.SetRegister("A", 0x81);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x02ul, cpu.GetRegister("A"));
        Assert.Equal(0x0201ul, cpu.GetRegister("PC")); // PC only advanced by opcode fetch
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x0A, true),
            new BusAccess(0x0201, 0x00, true)); // dummy read at PC (does not advance PC)
        Mos6502TestHarness.AssertFlags(cpu, c: true, v: false, n: false, z: false);
    }

    // ── Register decrements DEX/DEY ───────────────────────────────────────────

    [Fact]
    public void DEX_wraps_0_to_FF_sets_N()
    {
        // DEX (0xCA); X=0 => X=0xFF, N=1, Z=0; dummy read at PC
        var (cpu, bus) = Mos6502TestHarness.NewCpu(0xCA, 0x00);
        cpu.SetRegister("X", 0x00);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0xFFul, cpu.GetRegister("X"));
        Mos6502TestHarness.AssertNZ(cpu, n: true, z: false);
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xCA, true),
            new BusAccess(0x0201, 0x00, true)); // dummy read
    }

    [Fact]
    public void DEY_2_cycles()
    {
        // DEY (0x88); Y=5 => Y=4
        var (cpu, bus) = Mos6502TestHarness.NewCpu(0x88, 0x00);
        cpu.SetRegister("Y", 5);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(4ul, cpu.GetRegister("Y"));
        Mos6502TestHarness.AssertNZ(cpu, n: false, z: false);
    }
}
