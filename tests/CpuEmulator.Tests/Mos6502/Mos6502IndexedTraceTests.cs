using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>
/// Cycle-exact trace tests for the old-vocabulary rows (LDY/STX/STY/transfers/INY/branches)
/// and for all indexed zero-page / absolute addressing modes added in Task 3.
/// Silicon ground truth: see the cycle-template table in the implementation plan.
/// </summary>
public class Mos6502IndexedTraceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────
    private static (Mos6502Cpu Cpu, TracingAddressSpace Bus, CpuEmulator.Core.AddressSpace Inner)
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

    // ── Old-vocabulary spot tests ─────────────────────────────────────────────

    [Fact]
    public void LDY_immediate_2_cycles()
    {
        var (cpu, bus) = Mos6502TestHarness.NewCpu(0xA0, 0x07);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x07ul, cpu.GetRegister("Y"));
        Mos6502TestHarness.AssertNZ(cpu, n: false, z: false);
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xA0, true),
            new BusAccess(0x0201, 0x07, true));
    }

    [Fact]
    public void STX_zero_page_writes_X()
    {
        var (cpu, bus, inner) = NewCpuWithInner(0x86, 0x10);
        cpu.SetRegister("X", 0x55);

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0x55, inner.Read8(0x0010));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x86, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x55, false));
    }

    [Fact]
    public void TSX_sets_NZ_from_S()
    {
        // TSX = 0xBA; copies S into X; sets NZ
        var (cpu, bus) = Mos6502TestHarness.NewCpu(0xBA);
        cpu.SetRegister("S", 0x80);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x80ul, cpu.GetRegister("X"));
        Mos6502TestHarness.AssertNZ(cpu, n: true, z: false);
    }

    [Fact]
    public void TXS_sets_no_flags()
    {
        // TXS = 0x9A; copies X into S; does NOT set NZ
        var (cpu, _) = Mos6502TestHarness.NewCpu(0x9A);
        cpu.SetRegister("X", 0x00);
        cpu.SetRegister("P", 0x01); // C=1, Z=0 — should remain unchanged

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x00ul, cpu.GetRegister("S"));
        Assert.Equal(0x01ul, cpu.GetRegister("P")); // flags untouched — no Z!
    }

    [Fact]
    public void BCS_taken_3_cycles()
    {
        // BCS = 0xB0; taken when C=1; operand 0x05 => target 0x0200+2+5=0x0207
        var (cpu, bus) = Mos6502TestHarness.NewCpu(0xB0, 0x05);
        cpu.SetRegister("P", 0x01); // C=1

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0x0207ul, cpu.GetRegister("PC"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xB0, true),
            new BusAccess(0x0201, 0x05, true),
            new BusAccess(0x0202, 0x00, true)); // dummy at post-operand PC
    }

    // ── ZeroPage,X indexed trace tests ───────────────────────────────────────

    [Fact]
    public void LDA_zpX_4_cycles()
    {
        // LDA ($10,X); X=5 → effective = 0x15; dummy read at 0x10 first
        var (cpu, bus, inner) = NewCpuWithInner(0xB5, 0x10);
        inner.Write8(0x0010, 0x99); // value at unindexed addr (returned as dummy)
        inner.Write8(0x0015, 0x42); // actual data
        cpu.SetRegister("X", 5);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xB5, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x99, true),  // dummy read at unindexed zp
            new BusAccess(0x0015, 0x42, true));
    }

    [Fact]
    public void LDA_zpX_wraps_in_zero_page()
    {
        // addr=0x80, X=0xFF => (0x80+0xFF)&0xFF=0x7F — zero-page wrap, no escape to page 1
        var (cpu, bus, inner) = NewCpuWithInner(0xB5, 0x80);
        inner.Write8(0x0080, 0x99); // dummy read value
        inner.Write8(0x007F, 0x42); // data at wrapped address
        cpu.SetRegister("X", 0xFF);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xB5, true),
            new BusAccess(0x0201, 0x80, true),
            new BusAccess(0x0080, 0x99, true),  // dummy at unindexed
            new BusAccess(0x007F, 0x42, true)); // (0x80+0xFF)&0xFF = 0x7F
    }

    [Fact]
    public void LDX_zpY_4_cycles()
    {
        // LDX $10,Y (0xB6); Y=5 => effective = 0x15
        var (cpu, bus, inner) = NewCpuWithInner(0xB6, 0x10);
        inner.Write8(0x0015, 0x07);
        cpu.SetRegister("Y", 5);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x07ul, cpu.GetRegister("X"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xB6, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x00, true),  // dummy read at unindexed zp
            new BusAccess(0x0015, 0x07, true));
    }

    [Fact]
    public void STA_zpX_dummy_then_write()
    {
        // STA $10,X (0x95); X=5, A=0x99 => write 0x99 to 0x15; dummy read at 0x10 first
        var (cpu, bus, inner) = NewCpuWithInner(0x95, 0x10);
        cpu.SetRegister("X", 5);
        cpu.SetRegister("A", 0x99);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x99, inner.Read8(0x0015));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x95, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x00, true),  // dummy read at unindexed zp
            new BusAccess(0x0015, 0x99, false)); // write
    }

    // ── Absolute,X / Absolute,Y indexed trace tests ───────────────────────────

    [Fact]
    public void LDA_absX_no_cross_4_cycles()
    {
        // LDA $1200,X (0xBD); X=5 => ea=0x1205; no page cross => the cycle-4 read IS the data
        var (cpu, bus, inner) = NewCpuWithInner(0xBD, 0x00, 0x12);
        inner.Write8(0x1205, 0x42);
        cpu.SetRegister("X", 5);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xBD, true),
            new BusAccess(0x0201, 0x00, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1205, 0x42, true)); // wrong==right on no-cross, is the data
    }

    [Fact]
    public void LDA_absX_cross_5_cycles()
    {
        // LDA $12FE,X (0xBD); X=5 => lo+X=0xFE+5=0x103 => cross; wrong=0x1203 (dummy), ea=0x1303
        var (cpu, bus, inner) = NewCpuWithInner(0xBD, 0xFE, 0x12);
        inner.Write8(0x1203, 0x99); // value at wrong (dummy) address
        inner.Write8(0x1303, 0x42); // actual data at ea
        cpu.SetRegister("X", 5);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xBD, true),
            new BusAccess(0x0201, 0xFE, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1203, 0x99, true),  // wrong-page dummy
            new BusAccess(0x1303, 0x42, true)); // real ea
    }

    [Fact]
    public void LDA_absY_cross_5_cycles()
    {
        // LDA $12FE,Y (0xB9); Y=5 => same cross scenario
        var (cpu, bus, inner) = NewCpuWithInner(0xB9, 0xFE, 0x12);
        inner.Write8(0x1203, 0x99); // dummy
        inner.Write8(0x1303, 0x42); // data
        cpu.SetRegister("Y", 5);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xB9, true),
            new BusAccess(0x0201, 0xFE, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1203, 0x99, true),  // wrong-page dummy
            new BusAccess(0x1303, 0x42, true));
    }

    [Fact]
    public void STA_absX_always_5_cycles_with_cross()
    {
        // STA $12FE,X (0x9D); X=5 => dummy read at wrong=0x1203 (ALWAYS), write at ea=0x1303
        var (cpu, bus, inner) = NewCpuWithInner(0x9D, 0xFE, 0x12);
        cpu.SetRegister("X", 5);
        cpu.SetRegister("A", 0x99);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x99, inner.Read8(0x1303));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x9D, true),
            new BusAccess(0x0201, 0xFE, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1203, 0x00, true),  // dummy read at wrong addr
            new BusAccess(0x1303, 0x99, false)); // write at ea
    }

    [Fact]
    public void STA_absX_no_cross_still_5_cycles()
    {
        // STA $1200,X (0x9D); X=5; no page cross but dummy read still happens at ea=0x1205
        var (cpu, bus, inner) = NewCpuWithInner(0x9D, 0x00, 0x12);
        cpu.SetRegister("X", 5);
        cpu.SetRegister("A", 0x99);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x99, inner.Read8(0x1205));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x9D, true),
            new BusAccess(0x0201, 0x00, true),
            new BusAccess(0x0202, 0x12, true),
            new BusAccess(0x1205, 0x00, true),  // dummy read at right addr (no-cross, still happens)
            new BusAccess(0x1205, 0x99, false)); // write
    }
}
