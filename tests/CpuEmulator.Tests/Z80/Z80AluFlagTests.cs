using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using Xunit;

namespace CpuEmulator.Tests.Z80;

/// <summary>
/// M3.4a Task 5 — the flag-correct Z80 8-bit ALU + INC/DEC + ADD HL,rr, proven by driving the REAL
/// generated <see cref="Z80Cpu"/>. Each case sets A (and the operand register / carry-in), places the
/// opcode at PC=0, Steps once, and asserts the result + the full F byte (incl. the undocumented X/Y
/// bits 3/5). The Z80 flag word: S=0x80 Z=0x40 Y=0x20 H=0x10 X=0x08 P/V=0x04 N=0x02 C=0x01.
/// </summary>
public class Z80AluFlagTests
{
    private static Z80Cpu NewCpu(params (ushort addr, byte val)[] mem)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        foreach (var (addr, val) in mem) space.Write8(addr, val);
        return new Z80Cpu(space);
    }

    // ── ADD A,B (0x80) — 8-bit add, all flags. ──
    [Theory]
    // A, B → result, F
    [InlineData(0x44, 0x11, 0x55, 0x00)]   // 0x55: no flags (P/V is OVERFLOW for ADD — none here; bit3/5 of 0x55 clear)
    [InlineData(0x0F, 0x01, 0x10, 0x10)]   // half-carry: H set (0x10); no overflow
    [InlineData(0x7F, 0x01, 0x80, 0x94)]   // overflow: S(0x80)+H(0x10)+P/V(0x04)=0x94
    [InlineData(0xFF, 0x01, 0x00, 0x51)]   // wraps to 0: Z(0x40)+H(0x10)+C(0x01)=0x51
    public void Add_A_B_sets_flags(int a, int b, int result, int f)
    {
        var cpu = NewCpu((0x0000, 0x80));   // ADD A,B
        cpu.A = (byte)a; cpu.B = (byte)b; cpu.F = 0; cpu.PC = 0;
        cpu.Step();
        Assert.Equal((byte)result, cpu.A);
        Assert.Equal((byte)f, cpu.F);
    }

    // ── SUB B (0x90) — 8-bit subtract, N set, borrow → C. ──
    [Theory]
    [InlineData(0x10, 0x01, 0x0F, 0x1A)]   // 0x0F: H(borrow from bit4)=0x10, N=0x02, P? 4 ones even → 0x04? → 0x16... computed below
    [InlineData(0x00, 0x01, 0xFF, 0xBB)]   // underflow: S(0x80)+Y(0x20)+H(0x10)+X(0x08)+N(0x02)+C(0x01)=0xBB
    [InlineData(0x50, 0x50, 0x00, 0x42)]   // equal: Z(0x40)+N(0x02)=0x42
    public void Sub_B_sets_flags(int a, int b, int result, int f)
    {
        var cpu = NewCpu((0x0000, 0x90));   // SUB B
        cpu.A = (byte)a; cpu.B = (byte)b; cpu.F = 0; cpu.PC = 0;
        cpu.Step();
        Assert.Equal((byte)result, cpu.A);
        Assert.Equal((byte)f, cpu.F);
    }

    // ── AND B (0xA0) — logic, H=1, P=parity, C=0. ──
    [Fact]
    public void And_B_sets_H_and_parity()
    {
        var cpu = NewCpu((0x0000, 0xA0));   // AND B
        cpu.A = 0x0F; cpu.B = 0x3C; cpu.F = 0xFF; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x0C, cpu.A);          // 0x0F & 0x3C = 0x0C
        // 0x0C: S=0, Z=0, Y=0, H=1(0x10), X=1(0x08), P (2 ones even)=0x04, N=0, C=0 → 0x1C
        Assert.Equal(0x1C, cpu.F);
    }

    // ── OR B (0xB0) — logic, H=0, P=parity, C=0. ──
    [Fact]
    public void Or_B_sets_parity_clears_HNC()
    {
        var cpu = NewCpu((0x0000, 0xB0));   // OR B
        cpu.A = 0x00; cpu.B = 0x00; cpu.F = 0xFF; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x00, cpu.A);
        // 0x00: Z(0x40)+P(0 ones even, 0x04)=0x44
        Assert.Equal(0x44, cpu.F);
    }

    // ── CP B (0xB8) — compare: like SUB but A unchanged, X/Y from the OPERAND (the Z80 quirk). ──
    [Fact]
    public void Cp_B_takes_XY_from_operand_not_result()
    {
        var cpu = NewCpu((0x0000, 0xB8));   // CP B
        // A=0x00, B=0x28 (bits 5+3 set). Result of 0-0x28 = 0xD8, but X/Y come from B (0x28).
        cpu.A = 0x00; cpu.B = 0x28; cpu.F = 0; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x00, cpu.A);          // CP does not change A
        // diff=0xD8: S(0x80). Y/X from OPERAND 0x28: Y(0x20)+X(0x08). H: borrow from bit4 → H(0x10).
        // P/V: (0x00^0x28)&(0x00^0xD8)&0x80 = 0x28&0xD8&0x80 = 0x08&0x80 = 0 → no overflow.
        // N(0x02), C: diff<0 → C(0x01). Total: 0x80|0x20|0x10|0x08|0x02|0x01 = 0xBB.
        Assert.Equal(0xBB, cpu.F);
    }

    // ── INC B (0x04) — H + overflow, C PRESERVED. ──
    [Fact]
    public void Inc_B_overflow_at_0x7F_preserves_carry()
    {
        var cpu = NewCpu((0x0000, 0x04));   // INC B
        cpu.B = 0x7F; cpu.F = 0x01; cpu.PC = 0;   // C set going in
        cpu.Step();
        Assert.Equal(0x80, cpu.B);
        // 0x80: S(0x80), H(0x10, bit3 carry from 0x7F), P/V(0x04, overflow 0x7F→0x80), N=0, C PRESERVED(0x01).
        Assert.Equal(0x80 | 0x10 | 0x04 | 0x01, cpu.F);
    }

    [Fact]
    public void Dec_B_sets_N_and_preserves_carry()
    {
        var cpu = NewCpu((0x0000, 0x05));   // DEC B
        cpu.B = 0x01; cpu.F = 0x01; cpu.PC = 0;   // C set
        cpu.Step();
        Assert.Equal(0x00, cpu.B);
        // 0x00: Z(0x40), N(0x02), C preserved(0x01). H: (1&0xF)==0? no → H clear. → 0x43
        Assert.Equal(0x43, cpu.F);
    }

    // ── ADD HL,BC (0x09) — 16-bit, H from bit 11, C from bit 15, S/Z/P-V preserved. ──
    [Fact]
    public void Add_HL_BC_sets_H_C_preserves_SZPV()
    {
        var cpu = NewCpu((0x0000, 0x09));   // ADD HL,BC
        cpu.HL = 0x0FFF; cpu.BC = 0x0001; cpu.F = 0xC4; cpu.PC = 0;   // S+Z+P/V set going in
        cpu.Step();
        Assert.Equal(0x1000, cpu.HL);
        // H set (bit 11 carry, 0x10); C clear; N=0. S/Z/P-V preserved (0xC4 = S+Z+P/V).
        // X/Y from high byte 0x10: bit5=0, bit3=0 → no X/Y. F = 0xC4 | 0x10 = 0xD4.
        Assert.Equal(0xD4, cpu.F);
    }

    // ── INC BC (0x03) — 16-bit, NO flags. ──
    [Fact]
    public void Inc_BC_sets_no_flags()
    {
        var cpu = NewCpu((0x0000, 0x03));   // INC BC
        cpu.BC = 0x1234; cpu.F = 0x55; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x1235, cpu.BC);
        Assert.Equal(0x55, cpu.F);          // F untouched (the Z80 16-bit INC/DEC quirk)
    }

    // ── Cycle counts (a representative sample of the T-state totals). ──
    [Fact]
    public void Cycle_counts_match_dataset()
    {
        var add = NewCpu((0x0000, 0x80)); add.PC = 0; add.Step();
        Assert.Equal(4, add.CycleCount);                 // ADD A,B = 4

        var addhl = NewCpu((0x0000, 0x86)); addhl.PC = 0; addhl.Step();
        Assert.Equal(7, addhl.CycleCount);               // ADD A,(HL) = 7

        var add16 = NewCpu((0x0000, 0x09)); add16.PC = 0; add16.Step();
        Assert.Equal(11, add16.CycleCount);              // ADD HL,BC = 11

        var incbc = NewCpu((0x0000, 0x03)); incbc.PC = 0; incbc.Step();
        Assert.Equal(6, incbc.CycleCount);               // INC BC = 6

        var incmem = NewCpu((0x0000, 0x34)); incmem.PC = 0; incmem.Step();
        Assert.Equal(11, incmem.CycleCount);             // INC (HL) = 11
    }
}
