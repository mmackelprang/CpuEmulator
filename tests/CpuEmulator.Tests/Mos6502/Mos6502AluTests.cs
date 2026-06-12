using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>
/// Behaviour tests for the ALU instruction class: ADC, SBC, AND, ORA, EOR, CMP/CPX/CPY, BIT.
/// Silicon ground truth for flags: see the micro-op flag semantics table in the 3b-i plan.
/// </summary>
public class Mos6502AluTests
{
    // ── Trace shape (ALU = load shape, no extra cycles) ──────────────────────

    [Fact]
    public void ADC_zero_page_3_cycles_load_shape()
    {
        // ADC $10 (0x65); A=0x10 + data=0x05 => A=0x15
        var (cpu, bus, inner) = NewCpuWithInner(0x65, 0x10);
        inner.Write8(0x0010, 0x05);
        cpu.SetRegister("A", 0x10);

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0x15ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x65, true),
            new BusAccess(0x0201, 0x10, true),
            new BusAccess(0x0010, 0x05, true));
    }

    // ── ADC flag truth table ──────────────────────────────────────────────────

    // [InlineData(a, d, carryIn, result, c, v, n, z)]
    [Theory]
    [InlineData(0x50, 0x10, 0, 0x60, false, false, false, false)] // normal no carry/overflow
    [InlineData(0x50, 0x50, 0, 0xA0, false, true,  true,  false)] // positive+positive = negative (overflow)
    [InlineData(0x90, 0x90, 0, 0x20, true,  true,  false, false)] // negative+negative = positive (overflow, carry)
    [InlineData(0xFF, 0x01, 0, 0x00, true,  false, false, true)]  // carry out, zero result
    [InlineData(0x00, 0x00, 1, 0x01, false, false, false, false)] // carry-in only
    [InlineData(0x7F, 0x00, 1, 0x80, false, true,  true,  false)] // carry-in triggers overflow
    public void ADC_immediate_flag_truth_table(byte a, byte d, int carryIn, byte result, bool c, bool v, bool n, bool z)
    {
        var (cpu, _) = Mos6502TestHarness.NewCpu(0x69, d); // ADC #imm
        cpu.SetRegister("A", a);
        cpu.SetRegister("P", (ulong)carryIn); // C bit

        cpu.Step();

        Assert.Equal((ulong)result, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertFlags(cpu, c: c, v: v, n: n, z: z);
    }

    // ── SBC flag truth table ──────────────────────────────────────────────────

    // Cin=1 means no borrow (carry set = borrow clear, 6502 convention)
    [Theory]
    [InlineData(0x50, 0x10, 1, 0x40, true,  false, false, false)] // normal subtraction
    [InlineData(0x50, 0xB0, 1, 0xA0, false, true,  true,  false)] // positive - negative = negative (overflow)
    [InlineData(0x00, 0x01, 1, 0xFF, false, false, true,  false)] // underflow
    [InlineData(0x42, 0x42, 1, 0x00, true,  false, false, true)]  // A == d, zero result
    [InlineData(0x10, 0x00, 0, 0x0F, true,  false, false, false)] // borrow-in
    public void SBC_immediate_flag_truth_table(byte a, byte d, int carryIn, byte result, bool c, bool v, bool n, bool z)
    {
        var (cpu, _) = Mos6502TestHarness.NewCpu(0xE9, d); // SBC #imm
        cpu.SetRegister("A", a);
        cpu.SetRegister("P", (ulong)carryIn);

        cpu.Step();

        Assert.Equal((ulong)result, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertFlags(cpu, c: c, v: v, n: n, z: z);
    }

    // ── CMP / CPX / CPY ──────────────────────────────────────────────────────

    // Parameters: (opcode, regName, regVal, data, c, z, n)
    [Theory]
    [InlineData(0xC9, "A", 0x42, 0x42, true,  true,  false)] // equal → C1 Z1 N0
    [InlineData(0xC9, "A", 0x10, 0x20, false, false, true)]  // less → C0 Z0 N1 (0x10-0x20=0xF0, bit7=1)
    [InlineData(0xC9, "A", 0x20, 0x10, true,  false, false)] // greater → C1 Z0 N0
    [InlineData(0xE0, "X", 0x42, 0x42, true,  true,  false)] // CPX equal
    [InlineData(0xE0, "X", 0x10, 0x20, false, false, true)]  // CPX less
    [InlineData(0xC0, "Y", 0x42, 0x42, true,  true,  false)] // CPY equal
    [InlineData(0xC0, "Y", 0x20, 0x10, true,  false, false)] // CPY greater
    public void Compare_immediate_flag_cases(byte opcode, string regName, byte regVal, byte data, bool c, bool z, bool n)
    {
        var (cpu, _) = Mos6502TestHarness.NewCpu(opcode, data);
        cpu.SetRegister(regName, regVal);

        cpu.Step();

        ulong p = cpu.GetRegister("P");
        Assert.Equal(c, (p & 0x01) != 0);
        Assert.Equal(z, (p & 0x02) != 0);
        Assert.Equal(n, (p & 0x80) != 0);
    }

    // ── AND / ORA / EOR ──────────────────────────────────────────────────────

    [Fact]
    public void AND_immediate_NZ_case()
    {
        // A=0xF0 & 0x0F => A=0x00, Z=1
        var (cpu, _) = Mos6502TestHarness.NewCpu(0x29, 0x0F); // AND #$0F
        cpu.SetRegister("A", 0xF0);

        cpu.Step();

        Assert.Equal(0x00ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertNZ(cpu, n: false, z: true);
    }

    [Fact]
    public void ORA_immediate_sets_N()
    {
        // A=0x00 | 0x80 => A=0x80, N=1
        var (cpu, _) = Mos6502TestHarness.NewCpu(0x09, 0x80); // ORA #$80
        cpu.SetRegister("A", 0x00);

        cpu.Step();

        Assert.Equal(0x80ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertNZ(cpu, n: true, z: false);
    }

    [Fact]
    public void EOR_immediate_clears_bits()
    {
        // A=0xFF ^ 0xFF => A=0x00, Z=1
        var (cpu, _) = Mos6502TestHarness.NewCpu(0x49, 0xFF); // EOR #$FF
        cpu.SetRegister("A", 0xFF);

        cpu.Step();

        Assert.Equal(0x00ul, cpu.GetRegister("A"));
        Mos6502TestHarness.AssertNZ(cpu, n: false, z: true);
    }

    // ── BIT ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BIT_zp_sets_N_V_Z()
    {
        // BIT $10 (0x24); A=0x0F; data=0xC0 => Z=(0x0F&0xC0==0)=1, N=d7=1, V=d6=1; C untouched
        var (cpu, _, inner) = NewCpuWithInner(0x24, 0x10);
        inner.Write8(0x0010, 0xC0);
        cpu.SetRegister("A", 0x0F);
        cpu.SetRegister("P", 0x01); // C=1 — must remain untouched

        cpu.Step();

        ulong p = cpu.GetRegister("P");
        Assert.True((p & 0x01) != 0, "C should be untouched");
        Assert.True((p & 0x40) != 0, "V should be set (d6)");
        Assert.True((p & 0x80) != 0, "N should be set (d7)");
        Assert.True((p & 0x02) != 0, "Z should be set (A & data == 0)");
    }

    [Fact]
    public void BIT_zp_clears_Z_keeps_V()
    {
        // BIT $10; A=0x01; data=0x41 => Z=(0x01&0x41=0x01≠0)=0, N=0, V=1; C untouched
        var (cpu, _, inner) = NewCpuWithInner(0x24, 0x10);
        inner.Write8(0x0010, 0x41);
        cpu.SetRegister("A", 0x01);
        cpu.SetRegister("P", 0x01); // C=1

        cpu.Step();

        ulong p = cpu.GetRegister("P");
        Assert.True((p & 0x01) != 0, "C should be untouched");
        Assert.True((p & 0x40) != 0, "V should be set (d6)");
        Assert.False((p & 0x80) != 0, "N should be clear (d7=0)");
        Assert.False((p & 0x02) != 0, "Z should be clear (A & data != 0)");
    }

    // ── Decimal deviation pin ──────────────────────────────────────────────────

    [Fact]
    public void ADC_with_D_flag_set_still_adds_binary_3bi_deviation()
    {
        // 3b-i deviation: binary even when D is set (BCD lands in 3b-ii — this test is deleted then)
        // P=0x08 (D set), A=0x09, 69 01 → A=0x0A (binary; real BCD would give 0x10)
        var (cpu, _) = Mos6502TestHarness.NewCpu(0x69, 0x01); // ADC #$01
        cpu.SetRegister("A", 0x09);
        cpu.SetRegister("P", 0x08); // D set

        cpu.Step();

        Assert.Equal(0x0Aul, cpu.GetRegister("A")); // binary result, not BCD 0x10
    }

    // ── Helper ───────────────────────────────────────────────────────────────

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
}
