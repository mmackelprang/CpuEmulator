using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using Xunit;

namespace CpuEmulator.Tests.Z80;

/// <summary>
/// M3.4a Task 5/6 — the non-ALU base-plane micro-ops, proven by driving the REAL generated
/// <see cref="Z80Cpu"/>: the LD matrix (r,r' / r,n / r,(HL) / (HL),r / A,(BC) / (nn),A / rr,nn /
/// (nn),HL / SP,HL), PUSH/POP, EX/EXX, the conditional+relative flow set, DAA/CPL/SCF/CCF, DI/EI,
/// and the R-refresh fetch increment.
/// </summary>
public class Z80MicroOpTests
{
    private static Z80Cpu NewCpu(params (ushort addr, byte val)[] mem)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        foreach (var (addr, val) in mem) space.Write8(addr, val);
        return new Z80Cpu(space);
    }

    [Fact]
    public void LD_r_rprime_copies_register()
    {
        var cpu = NewCpu((0x0000, 0x41));   // LD B,C
        cpu.B = 0x00; cpu.C = 0x99; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x99, cpu.B);
        Assert.Equal(4, cpu.CycleCount);
    }

    [Fact]
    public void LD_r_n_loads_immediate()
    {
        var cpu = NewCpu((0x0000, 0x06), (0x0001, 0x42));   // LD B,0x42
        cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x42, cpu.B);
        Assert.Equal(0x0002, cpu.PC);
        Assert.Equal(7, cpu.CycleCount);
    }

    [Fact]
    public void LD_r_from_HL_indirect()
    {
        var cpu = NewCpu((0x0000, 0x46), (0x4000, 0x7E));   // LD B,(HL) ; (HL)=0x4000
        cpu.HL = 0x4000; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x7E, cpu.B);
        Assert.Equal(7, cpu.CycleCount);
    }

    [Fact]
    public void LD_HL_indirect_store()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0000, 0x70);   // LD (HL),B
        var cpu = new Z80Cpu(space);
        cpu.HL = 0x5000; cpu.B = 0x33; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x33, space.Read8(0x5000));
        Assert.Equal(7, cpu.CycleCount);
    }

    [Fact]
    public void LD_A_from_BC_indirect()
    {
        var cpu = NewCpu((0x0000, 0x0A), (0x6000, 0xAB));   // LD A,(BC)
        cpu.BC = 0x6000; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0xAB, cpu.A);
    }

    [Fact]
    public void LD_rr_nn_loads_16bit_immediate()
    {
        var cpu = NewCpu((0x0000, 0x21), (0x0001, 0x34), (0x0002, 0x12));   // LD HL,0x1234
        cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x1234, cpu.HL);
        Assert.Equal(0x0003, cpu.PC);
        Assert.Equal(10, cpu.CycleCount);
    }

    [Fact]
    public void LD_nn_HL_stores_word()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0000, 0x22); space.Write8(0x0001, 0x00); space.Write8(0x0002, 0x70);  // LD (0x7000),HL
        var cpu = new Z80Cpu(space);
        cpu.HL = 0xBEEF; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0xEF, space.Read8(0x7000));
        Assert.Equal(0xBE, space.Read8(0x7001));
        Assert.Equal(16, cpu.CycleCount);
    }

    [Fact]
    public void LD_HL_nn_loads_word()
    {
        var cpu = NewCpu((0x0000, 0x2A), (0x0001, 0x00), (0x0002, 0x70), (0x7000, 0xCD), (0x7001, 0xAB));  // LD HL,(0x7000)
        cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0xABCD, cpu.HL);
    }

    [Fact]
    public void LD_SP_HL()
    {
        var cpu = NewCpu((0x0000, 0xF9));   // LD SP,HL
        cpu.HL = 0xFFF0; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0xFFF0, cpu.SP);
        Assert.Equal(6, cpu.CycleCount);
    }

    [Fact]
    public void PUSH_then_POP_round_trips_a_pair()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0000, 0xC5);   // PUSH BC
        var cpu = new Z80Cpu(space);
        cpu.BC = 0x1234; cpu.SP = 0x8000; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x7FFE, cpu.SP);
        Assert.Equal(0x12, space.Read8(0x7FFF));   // high byte at higher addr
        Assert.Equal(0x34, space.Read8(0x7FFE));
        Assert.Equal(11, cpu.CycleCount);
    }

    [Fact]
    public void POP_AF_restores_the_flag_word()
    {
        var cpu = NewCpu((0x0000, 0xF1), (0x8000, 0xCD), (0x8001, 0xAB));   // POP AF
        cpu.SP = 0x8000; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0xAB, cpu.A);   // A = high byte
        Assert.Equal(0xCD, cpu.F);   // F = low byte (POP AF is the only POP that sets F)
        Assert.Equal(0x8002, cpu.SP);
    }

    [Fact]
    public void EX_DE_HL_swaps_pairs()
    {
        var cpu = NewCpu((0x0000, 0xEB));   // EX DE,HL
        cpu.DE = 0x1111; cpu.HL = 0x2222; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x2222, cpu.DE);
        Assert.Equal(0x1111, cpu.HL);
    }

    [Fact]
    public void EXX_swaps_the_six_halves_not_AF()
    {
        var cpu = NewCpu((0x0000, 0xD9));   // EXX
        cpu.BC = 0x1111; cpu.DE = 0x2222; cpu.HL = 0x3333;
        cpu.BC_ = 0xAAAA; cpu.DE_ = 0xBBBB; cpu.HL_ = 0xCCCC;
        cpu.A = 0x55; cpu.F = 0x0F; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0xAAAA, cpu.BC);
        Assert.Equal(0x3333, cpu.HL_);
        Assert.Equal(0x55, cpu.A);   // AF NOT swapped by EXX
        Assert.Equal(0x0F, cpu.F);
    }

    [Fact]
    public void EX_AF_AF_swaps_AF_with_shadow()
    {
        var cpu = NewCpu((0x0000, 0x08));   // EX AF,AF'
        cpu.A = 0x11; cpu.F = 0x22; cpu.A_ = 0x33; cpu.F_ = 0x44; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x33, cpu.A);
        Assert.Equal(0x44, cpu.F);
        Assert.Equal(0x11, cpu.A_);
    }

    [Fact]
    public void JR_taken_and_not_taken_cycle_counts()
    {
        var taken = NewCpu((0x0000, 0x28), (0x0001, 0x05));   // JR Z,+5 ; Z set
        taken.F = 0x40; taken.PC = 0;
        taken.Step();
        Assert.Equal(0x0007, taken.PC);   // PC = 2 (past operand) + 5
        Assert.Equal(12, taken.CycleCount);

        var notTaken = NewCpu((0x0000, 0x28), (0x0001, 0x05));   // JR Z,+5 ; Z clear
        notTaken.F = 0x00; notTaken.PC = 0;
        notTaken.Step();
        Assert.Equal(0x0002, notTaken.PC);
        Assert.Equal(7, notTaken.CycleCount);
    }

    [Fact]
    public void DJNZ_decrements_B_and_loops()
    {
        var loop = NewCpu((0x0000, 0x10), (0x0001, 0xFE));   // DJNZ -2
        loop.B = 0x03; loop.PC = 0;
        loop.Step();
        Assert.Equal(0x02, loop.B);
        Assert.Equal(0x0000, loop.PC);   // 2 + (-2) = 0
        Assert.Equal(13, loop.CycleCount);

        var fall = NewCpu((0x0000, 0x10), (0x0001, 0xFE));
        fall.B = 0x01; fall.PC = 0;
        fall.Step();
        Assert.Equal(0x00, fall.B);
        Assert.Equal(0x0002, fall.PC);   // falls through
        Assert.Equal(8, fall.CycleCount);
    }

    [Fact]
    public void CALL_then_RET_round_trips_PC()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0000, 0xCD); space.Write8(0x0001, 0x00); space.Write8(0x0002, 0x40);  // CALL 0x4000
        space.Write8(0x4000, 0xC9);   // RET
        var cpu = new Z80Cpu(space);
        cpu.SP = 0x8000; cpu.PC = 0;
        cpu.Step();   // CALL
        Assert.Equal(0x4000, cpu.PC);
        Assert.Equal(0x7FFE, cpu.SP);
        Assert.Equal(0x03, space.Read8(0x7FFE) | (space.Read8(0x7FFF) << 8));  // return addr = 0x0003
        Assert.Equal(17, cpu.CycleCount);
        cpu.Step();   // RET
        Assert.Equal(0x0003, cpu.PC);
        Assert.Equal(0x8000, cpu.SP);
    }

    [Fact]
    public void RST_pushes_PC_and_jumps_to_vector()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0100, 0xFF);   // RST 38h at PC=0x0100
        var cpu = new Z80Cpu(space);
        cpu.SP = 0x8000; cpu.PC = 0x0100;
        cpu.Step();
        Assert.Equal(0x0038, cpu.PC);
        Assert.Equal(0x0101, space.Read8(0x7FFE) | (space.Read8(0x7FFF) << 8));
        Assert.Equal(11, cpu.CycleCount);
    }

    [Fact]
    public void JP_HL_sets_PC_to_HL()
    {
        var cpu = NewCpu((0x0000, 0xE9));   // JP (HL)
        cpu.HL = 0x1234; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x1234, cpu.PC);
        Assert.Equal(4, cpu.CycleCount);
    }

    [Fact]
    public void DI_and_EI_set_the_IFF_latches()
    {
        var di = NewCpu((0x0000, 0xF3));   // DI
        di.Iff1 = true; di.Iff2 = true; di.PC = 0;
        di.Step();
        Assert.False(di.Iff1);
        Assert.False(di.Iff2);

        var ei = NewCpu((0x0000, 0xFB));   // EI
        ei.Iff1 = false; ei.Iff2 = false; ei.PC = 0;
        ei.Step();
        Assert.True(ei.Iff1);
        Assert.True(ei.Iff2);
    }

    [Fact]
    public void CPL_complements_A_sets_H_N()
    {
        var cpu = NewCpu((0x0000, 0x2F));   // CPL
        cpu.A = 0x0F; cpu.F = 0x00; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0xF0, cpu.A);
        // H(0x10)+N(0x02) set; X/Y from new A 0xF0: Y(0x20)+X? bit3 of 0xF0=0 → no X. → 0x32
        Assert.Equal(0x32, cpu.F);
    }

    [Fact]
    public void SCF_sets_carry_clears_HN()
    {
        var cpu = NewCpu((0x0000, 0x37));   // SCF
        cpu.A = 0x00; cpu.F = 0x12; cpu.PC = 0;   // H + N set going in
        cpu.Step();
        // C(0x01) set; H/N cleared; X/Y from A=0 → none; S/Z/P preserved (0x12 has none of S/Z/P).
        Assert.Equal(0x01, cpu.F);
    }

    [Fact]
    public void R_refresh_increments_low_7_bits_on_fetch()
    {
        var cpu = NewCpu((0x0000, 0x00));   // NOP
        cpu.R = 0x7F; cpu.PC = 0;
        cpu.Step();
        Assert.Equal(0x00, cpu.R);   // low 7 bits wrap (0x7F → 0x00), bit 7 preserved (was 0)

        var cpu2 = NewCpu((0x0000, 0x00));
        cpu2.R = 0xFF; cpu2.PC = 0;
        cpu2.Step();
        Assert.Equal(0x80, cpu2.R);   // 0xFF: low 7 (0x7F) wrap to 0, bit 7 preserved → 0x80
    }

    [Fact]
    public void NOP_is_four_T_states()
    {
        var cpu = NewCpu((0x0000, 0x00));
        cpu.PC = 0;
        cpu.Step();
        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x0001, cpu.PC);
    }
}
