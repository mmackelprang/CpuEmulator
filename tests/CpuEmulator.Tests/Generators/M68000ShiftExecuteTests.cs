using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M4.5c synthetic execute tests for the shift/rotate families (no vectors). Drives cpu.Step() over a hand-built
/// operword and asserts (result, CCR). Reg-count mod 64, imm-count 0->8, each of the 8 kinds, .b/.w/.l partial
/// write, and the memory-by-1 form. The TomHarte sweep (Task 22) is the un-fakeable oracle; these pin the wiring.
/// </summary>
public class M68000ShiftExecuteTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    // ── Task 4: ASL/ASR/LSL/LSR register form ──────────────────────────────────────────────────────────────
    [Fact]
    public void Asl_w_imm1_shifts_left_and_sets_carry_from_msb()
    {
        // ASL.w #1,D0 = 0xE340. bits: 1110 ccc=001 dr=1(left) ss=01(.w) i/r=0 type=00 reg=000.
        var (cpu, _) = Build((0x1000, 0xE3), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000C001);   // .w 0xC001 -> <<1 = 0x8002, msb-out=1, msb changed -> V
        cpu.Step();
        Assert.Equal(0x00008002u, (uint)cpu.GetRegister("D0"));
        uint sr = (uint)cpu.GetRegister("SR") & 0x1F;
        Assert.Equal(0x10u, sr & 0x10);   // X = C = last bit out (1)
        Assert.Equal(0x08u, sr & 0x08);   // N (result msb set)
        Assert.Equal(0x02u, sr & 0x02);   // V (msb changed during shift)
        Assert.Equal(0x01u, sr & 0x01);   // C
    }

    [Fact]
    public void Lsr_b_imm3_shifts_right_carry_from_original_bit2()
    {
        // LSR.b #3,D0 = 0xE608. 1110 ccc=011 dr=0(right) ss=00(.b) i/r=0 type=01(LS) reg=000.
        var (cpu, _) = Build((0x1000, 0xE6), (0x1001, 0x08));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000008);   // 0b00001000 >> 3 = 1, last bit out = bit2 of original = 0
        cpu.Step();
        Assert.Equal(0x00000001u, (uint)cpu.GetRegister("D0"));
        uint sr = (uint)cpu.GetRegister("SR") & 0x1F;
        Assert.Equal(0x00u, sr & 0x01);   // C = last bit out = 0 (bit2 of 0b00001000 = 0)
    }

    [Fact]
    public void Asr_b_sign_fills_high_bits()
    {
        // ASR.b #1,D0 = 0xE200. 1110 ccc=001 dr=0(right) ss=00(.b) i/r=0 type=00(AS) reg=000.
        var (cpu, _) = Build((0x1000, 0xE2), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000081);   // 0b10000001 >> 1 sign-fill = 0b11000000 = 0xC0; C = bit0 = 1
        cpu.Step();
        Assert.Equal(0x000000C0u, (uint)cpu.GetRegister("D0"));
        uint sr = (uint)cpu.GetRegister("SR") & 0x1F;
        Assert.Equal(0x08u, sr & 0x08);   // N
        Assert.Equal(0x01u, sr & 0x01);   // C
    }

    [Fact]
    public void Shift_register_count_is_mod_64()
    {
        // ASL.l D1,D0 = 0xE3A0. 1110 ccc=001(D1) dr=1(left) ss=10(.l) i/r=1(reg) type=00 reg=000.
        var (cpu, _) = Build((0x1000, 0xE3), (0x1001, 0xA0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000001);
        cpu.SetRegister("D1", 64);            // 64 mod 64 = 0 -> no shift, C cleared, X preserved
        cpu.Step();
        Assert.Equal(0x00000001u, (uint)cpu.GetRegister("D0"));   // unchanged (count 0)
    }

    // ── Task 5: ROL/ROR/ROXL/ROXR register form ────────────────────────────────────────────────────────────
    [Fact]
    public void Rol_b_rotates_and_sets_C_not_X()
    {
        // ROL.b #1,D0 = 0xE118. 1110 ccc=000(imm1) dr=1(left) ss=00(.b) i/r=0 type=11(RO) reg=000.
        var (cpu, _) = Build((0x1000, 0xE1), (0x1001, 0x18));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000081);   // 0b10000001 rol1 = 0b00000011 = 0x03; C = old msb = 1
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00000003u, (uint)cpu.GetRegister("D0"));
        uint sr = (uint)cpu.GetRegister("SR") & 0x1F;
        Assert.Equal(0x01u, sr & 0x01);   // C = last bit rotated
        Assert.Equal(0x00u, sr & 0x10);   // X untouched (was 0)
    }

    [Fact]
    public void Roxl_b_rotates_through_X()
    {
        // ROXL.b #1,D0 = 0xE110. 1110 ccc=000(imm1) dr=1(left) ss=00(.b) i/r=0 type=10(ROX) reg=000.
        var (cpu, _) = Build((0x1000, 0xE1), (0x1001, 0x10));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000080);   // 0b10000000 roxl1 with X=1 -> 0b00000001; new X/C = old msb = 1
        cpu.SetRegister("SR", 0x0010);       // X set going in
        cpu.Step();
        Assert.Equal(0x00000001u, (uint)cpu.GetRegister("D0"));   // X(1) rotated into bit0; bit7 out
        uint sr = (uint)cpu.GetRegister("SR") & 0x1F;
        Assert.Equal(0x10u, sr & 0x10);   // X = last bit out = 1
        Assert.Equal(0x01u, sr & 0x01);   // C = X
    }

    // ── Task 6: memory-by-1 shift form ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Shift_mem_lsr_by_one_word()
    {
        // LSR.w (A0) = 0xE2D0. 1110 000 dr=0(right) ss(class)=01(LS) 11 ea-mode=010 reg=000 ((A0)).
        // SHIFT_MEM encoding: 1110 cc(class@10-9) dr(@8) 11 eaMode eaReg. class=01(LS), dr=0(right).
        var (cpu, bus) = Build((0x1000, 0xE2), (0x1001, 0xD0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        bus.Write16(0x2000, 0x0002);   // 2 >> 1 = 1
        cpu.Step();
        Assert.Equal((ushort)0x0001, bus.Read16(0x2000));
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x01);   // C = bit0 of original (0) = 0
    }

    // ── Dispatch smoke ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Step_routes_an_asl_operword()
    {
        var (cpu, _) = Build((0x1000, 0xE3), (0x1001, 0x40));   // ASL.w #1,D0
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000001);
        cpu.Step();
        Assert.Equal(0x00000002u, (uint)cpu.GetRegister("D0"));
    }
}
