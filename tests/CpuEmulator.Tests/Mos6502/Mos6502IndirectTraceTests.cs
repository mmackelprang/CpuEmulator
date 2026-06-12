using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>
/// Cycle-exact trace tests for IndirectX and IndirectY addressing modes, and JMP (indirect)
/// including the NMOS page-wrap bug. Silicon ground truth: see the cycle-template table
/// in the 3b-i implementation plan.
/// </summary>
public class Mos6502IndirectTraceTests
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

    // ── IndirectX (zp,X) load ────────────────────────────────────────────────

    [Fact]
    public void LDA_indX_6_cycles()
    {
        // LDA ($20,X) = 0xA1; X=4 => pointer at (0x24,0x25), ea=0x1234, data=0x42
        var (cpu, bus, inner) = NewCpuWithInner(0xA1, 0x20);
        inner.Write8(0x0024, 0x34); // lo of ea
        inner.Write8(0x0025, 0x12); // hi of ea
        inner.Write8(0x1234, 0x42);
        cpu.SetRegister("X", 4);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xA1, true),
            new BusAccess(0x0201, 0x20, true),
            new BusAccess(0x0020, 0x00, true),  // dummy read at unindexed ptr
            new BusAccess(0x0024, 0x34, true),  // lo byte of pointer
            new BusAccess(0x0025, 0x12, true),  // hi byte of pointer
            new BusAccess(0x1234, 0x42, true)); // data
    }

    [Fact]
    public void LDA_indX_pointer_wraps()
    {
        // LDA ($80,X); X=0xFF => pointer at (0x7F,0x80); both wrap in page 0
        var (cpu, bus, inner) = NewCpuWithInner(0xA1, 0x80);
        inner.Write8(0x007F, 0x34); // lo byte of ea (at (0x80+0xFF)&0xFF=0x7F)
        inner.Write8(0x0080, 0x12); // hi byte of ea (at (0x80+0xFF+1)&0xFF=0x80)
        inner.Write8(0x1234, 0x42);
        cpu.SetRegister("X", 0xFF);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xA1, true),
            new BusAccess(0x0201, 0x80, true),
            new BusAccess(0x0080, 0x12, true),  // dummy at unindexed 0x80
            new BusAccess(0x007F, 0x34, true),  // lo at (0x80+0xFF)&0xFF=0x7F
            new BusAccess(0x0080, 0x12, true),  // hi at (0x80+0xFF+1)&0xFF=0x80
            new BusAccess(0x1234, 0x42, true));
    }

    // ── IndirectX (zp,X) store ───────────────────────────────────────────────

    [Fact]
    public void STA_indX_6_cycles()
    {
        // STA ($20,X) = 0x81; X=4, A=0x99 => pointer at (0x24,0x25), write to 0x1234
        var (cpu, bus, inner) = NewCpuWithInner(0x81, 0x20);
        inner.Write8(0x0024, 0x34);
        inner.Write8(0x0025, 0x12);
        cpu.SetRegister("X", 4);
        cpu.SetRegister("A", 0x99);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x99, inner.Read8(0x1234));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x81, true),
            new BusAccess(0x0201, 0x20, true),
            new BusAccess(0x0020, 0x00, true),  // dummy at unindexed ptr
            new BusAccess(0x0024, 0x34, true),
            new BusAccess(0x0025, 0x12, true),
            new BusAccess(0x1234, 0x99, false)); // write
    }

    // ── IndirectY (zp),Y load ────────────────────────────────────────────────

    [Fact]
    public void LDA_indY_no_cross_5_cycles()
    {
        // LDA ($20),Y = 0xB1; Y=5; base ptr at (0x20,0x21)=0x1234; ea=0x1239; no page cross
        var (cpu, bus, inner) = NewCpuWithInner(0xB1, 0x20);
        inner.Write8(0x0020, 0x34); // lo
        inner.Write8(0x0021, 0x12); // hi
        inner.Write8(0x1239, 0x42);
        cpu.SetRegister("Y", 5);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xB1, true),
            new BusAccess(0x0201, 0x20, true),
            new BusAccess(0x0020, 0x34, true),  // lo byte of base ptr
            new BusAccess(0x0021, 0x12, true),  // hi byte of base ptr
            new BusAccess(0x1239, 0x42, true)); // ea = 0x1234+5
    }

    [Fact]
    public void LDA_indY_cross_6_cycles()
    {
        // LDA ($20),Y; Y=5; base ptr at (0x20,0x21)=0x12FE; ea=0x1303; wrong=0x1203 (dummy)
        var (cpu, bus, inner) = NewCpuWithInner(0xB1, 0x20);
        inner.Write8(0x0020, 0xFE); // lo
        inner.Write8(0x0021, 0x12); // hi  → base=0x12FE
        inner.Write8(0x1203, 0x99); // wrong-page dummy
        inner.Write8(0x1303, 0x42); // real data at ea=0x12FE+5=0x1303
        cpu.SetRegister("Y", 5);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xB1, true),
            new BusAccess(0x0201, 0x20, true),
            new BusAccess(0x0020, 0xFE, true),
            new BusAccess(0x0021, 0x12, true),
            new BusAccess(0x1203, 0x99, true),  // wrong-page dummy
            new BusAccess(0x1303, 0x42, true));
    }

    [Fact]
    public void LDA_indY_zp_pointer_wraps()
    {
        // LDA ($FF),Y; Y=5; hi byte of pointer fetched from $0000 (wraps in page 0, not $0100)
        var (cpu, bus, inner) = NewCpuWithInner(0xB1, 0xFF);
        inner.Write8(0x00FF, 0x34); // lo
        inner.Write8(0x0000, 0x12); // hi (wraps — NOT 0x0100!)
        inner.Write8(0x1239, 0x42);
        cpu.SetRegister("Y", 5);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x42ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xB1, true),
            new BusAccess(0x0201, 0xFF, true),
            new BusAccess(0x00FF, 0x34, true),
            new BusAccess(0x0000, 0x12, true),  // hi from $0000, not $0100
            new BusAccess(0x1239, 0x42, true));
    }

    // ── IndirectY (zp),Y store ───────────────────────────────────────────────

    [Fact]
    public void STA_indY_always_6_cycles()
    {
        // STA ($20),Y = 0x91; Y=5; base=0x1234; ea=0x1239; ALWAYS 6 cycles (dummy read at ea)
        var (cpu, bus, inner) = NewCpuWithInner(0x91, 0x20);
        inner.Write8(0x0020, 0x34);
        inner.Write8(0x0021, 0x12);
        cpu.SetRegister("Y", 5);
        cpu.SetRegister("A", 0x99);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x99, inner.Read8(0x1239));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x91, true),
            new BusAccess(0x0201, 0x20, true),
            new BusAccess(0x0020, 0x34, true),
            new BusAccess(0x0021, 0x12, true),
            new BusAccess(0x1239, 0x00, true),  // dummy read at ea (always, even no cross)
            new BusAccess(0x1239, 0x99, false)); // write
    }

    // ── JMP (Indirect) ───────────────────────────────────────────────────────

    [Fact]
    public void JMP_indirect_5_cycles()
    {
        // JMP ($0320); ptr-lo=0x20, ptr-hi=0x03; read lo at 0x0320=0x00, hi at 0x0321=0x80
        var (cpu, bus, inner) = NewCpuWithInner(0x6C, 0x20, 0x03);
        inner.Write8(0x0320, 0x00);
        inner.Write8(0x0321, 0x80);

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x8000ul, cpu.GetRegister("PC"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x6C, true),
            new BusAccess(0x0201, 0x20, true),
            new BusAccess(0x0202, 0x03, true),
            new BusAccess(0x0320, 0x00, true),
            new BusAccess(0x0321, 0x80, true));
    }

    [Fact]
    public void JMP_indirect_page_wrap_bug()
    {
        // JMP ($03FF): NMOS bug — pointer at $03FF takes hi byte from $0300, NOT $0400
        // A correct CPU would read hi from $0400 and land at $9900 (0x0400=0x99).
        // The 6502 reads hi from $0300=0x80 and lands at $8000.
        var (cpu, bus, inner) = NewCpuWithInner(0x6C, 0xFF, 0x03);
        inner.Write8(0x03FF, 0x00); // lo byte of target
        inner.Write8(0x0300, 0x80); // hi byte from $0300 (bug: $03FF wraps to $0300, not $0400)
        inner.Write8(0x0400, 0x99); // hi byte a correct CPU would read (should NOT be accessed)

        cpu.Step();

        Assert.Equal(5, cpu.CycleCount);
        Assert.Equal(0x8000ul, cpu.GetRegister("PC")); // lands at $8000, NOT $9900
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x6C, true),
            new BusAccess(0x0201, 0xFF, true),
            new BusAccess(0x0202, 0x03, true),
            new BusAccess(0x03FF, 0x00, true),
            new BusAccess(0x0300, 0x80, true)); // hi from $0300 NOT $0400
    }
}
