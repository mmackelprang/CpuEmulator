using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>
/// M5.5b — vector-free synthetic proof of the 8086 integer-ALU execute pipeline + the flag-computation core.
/// Each test hand-assembles a tiny program into the 20-bit RAM at CS:IP, Steps the real <see cref="M8086Cpu"/>
/// once, and asserts the register/FLAGS result. Targets the flag EDGES the TomHarte vectors are unforgiving on:
/// AF (the BCD half-carry), CF/borrow, ZF, parity (low-byte), OF on signed overflow, CF-preservation on INC/DEC,
/// plus a MUL and a valid DIV. Mirrors <see cref="M8086MovExecuteTests"/>'s construction exactly.
/// </summary>
public class M8086AluExecuteTests
{
    private static M8086Cpu NewCpu(out AddressSpace bus)
    {
        bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        return new M8086Cpu(bus);
    }

    private static void LoadCode(AddressSpace bus, ushort cs, ushort ip, params byte[] code)
    {
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (uint i = 0; i < code.Length; i++)
            bus.Write8((phys + i) & 0xFFFFF, code[i]);
    }

    // FLAGS bit positions (the M8086Spec layout).
    private const ushort CF = 1 << 0, PF = 1 << 2, AF = 1 << 4, ZF = 1 << 6, SF = 1 << 7, OF = 1 << 11;

    private static bool Flag(M8086Cpu cpu, ushort bit) => ((ushort)cpu.GetRegister("FLAGS") & bit) != 0;

    [Fact]
    public void Add_byte_sets_AF_on_a_low_nibble_carry()
    {
        // 04 01 = ADD AL, 0x01 with AL=0x0F → 0x10. AF set (carry out of bit 3); ZF clear; CF clear.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x000F);   // AL=0x0F
        LoadCode(bus, 0, 0, 0x04, 0x01);
        cpu.Step();
        Assert.Equal(0x10u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, AF), "AF should be set on the 0x0F+0x01 half-carry");
        Assert.False(Flag(cpu, ZF));
        Assert.False(Flag(cpu, CF));
    }

    [Fact]
    public void Add_byte_carry_out_sets_CF_and_ZF_on_wrap_to_zero()
    {
        // 04 01 = ADD AL, 0x01 with AL=0xFF → 0x00. CF set, ZF set, AF set.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x00FF);
        LoadCode(bus, 0, 0, 0x04, 0x01);
        cpu.Step();
        Assert.Equal(0x00u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, ZF));
        Assert.True(Flag(cpu, AF));
    }

    [Fact]
    public void Sub_byte_borrow_sets_CF()
    {
        // 2C 05 = SUB AL, 0x05 with AL=0x03 → 0xFE. CF set (borrow), SF set, ZF clear.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0003);
        LoadCode(bus, 0, 0, 0x2C, 0x05);
        cpu.Step();
        Assert.Equal(0xFEu, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, SF));
        Assert.False(Flag(cpu, ZF));
    }

    [Fact]
    public void Cmp_equal_sets_ZF_and_writes_no_result()
    {
        // 3C 42 = CMP AL, 0x42 with AL=0x42 → ZF set, CF clear, AL unchanged (CMP is flags-only).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0042);
        LoadCode(bus, 0, 0, 0x3C, 0x42);
        cpu.Step();
        Assert.Equal(0x42u, cpu.GetRegister("AL"));   // unchanged
        Assert.True(Flag(cpu, ZF));
        Assert.False(Flag(cpu, CF));
    }

    [Fact]
    public void Parity_flag_tracks_the_low_byte_bit_count()
    {
        // 0x03 (0b11) has EVEN parity → PF set. OR AL,0 keeps AL; PF reflects the low byte.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0003);
        LoadCode(bus, 0, 0, 0x0C, 0x00);   // OR AL, 0x00
        cpu.Step();
        Assert.True(Flag(cpu, PF), "0x03 has even parity");

        // 0x01 (0b1) has ODD parity → PF clear.
        var cpu2 = NewCpu(out var bus2);
        cpu2.SetRegister("AX", 0x0001);
        LoadCode(bus2, 0, 0, 0x0C, 0x00);
        cpu2.Step();
        Assert.False(Flag(cpu2, PF), "0x01 has odd parity");
    }

    [Fact]
    public void Add_byte_signed_overflow_sets_OF_and_SF()
    {
        // 04 01 = ADD AL, 0x01 with AL=0x7F → 0x80. Signed overflow (+127 + 1 = -128): OF set, SF set, CF clear.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x007F);
        LoadCode(bus, 0, 0, 0x04, 0x01);
        cpu.Step();
        Assert.Equal(0x80u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, OF));
        Assert.True(Flag(cpu, SF));
        Assert.False(Flag(cpu, CF));
    }

    [Fact]
    public void Inc_reg16_preserves_CF()
    {
        // Pre-set CF=1 via STC-equivalent (we set FLAGS directly). 40 = INC AX. CF must be PRESERVED.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x00FF);
        cpu.SetRegister("FLAGS", (ushort)(cpu.GetRegister("FLAGS") | CF));   // CF=1 going in
        LoadCode(bus, 0, 0, 0x40);   // INC AX
        cpu.Step();
        Assert.Equal(0x0100u, cpu.GetRegister("AX"));
        Assert.True(Flag(cpu, CF), "INC must NOT modify CF");
        Assert.True(Flag(cpu, AF), "0x00FF+1 carries out of bit 3 → AF set");
    }

    [Fact]
    public void Dec_reg16_preserves_CF_and_sets_ZF()
    {
        // 48 = DEC AX with AX=1 → 0. CF preserved (here started clear), ZF set.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0001);
        LoadCode(bus, 0, 0, 0x48);
        cpu.Step();
        Assert.Equal(0x0000u, cpu.GetRegister("AX"));
        Assert.True(Flag(cpu, ZF));
        Assert.False(Flag(cpu, CF));
    }

    [Fact]
    public void Group80_add_rm8_imm8_sets_flags()
    {
        // 80 C3 01 = ADD BL, 0x01 (group /0, mod=11 reg=000(ADD) r/m=011(BL)). BL=0xFF → 0x00, CF+ZF+AF.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("BX", 0x00FF);   // BL=0xFF
        LoadCode(bus, 0, 0, 0x80, 0xC3, 0x01);
        cpu.Step();
        Assert.Equal(0x0000u, cpu.GetRegister("BX"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, ZF));
    }

    [Fact]
    public void Group83_sign_extends_the_imm8_for_a_16bit_op()
    {
        // 83 C0 FF = ADD AX, -1 (group /0, mod=11 r/m=000(AX), imm8=0xFF sign-extends to 0xFFFF). AX=0x0001 → 0x0000.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0001);
        LoadCode(bus, 0, 0, 0x83, 0xC0, 0xFF);
        cpu.Step();
        Assert.Equal(0x0000u, cpu.GetRegister("AX"));   // 1 + (-1) = 0
        Assert.True(Flag(cpu, ZF));
        Assert.True(Flag(cpu, CF));   // carry out (1 + 0xFFFF wraps)
    }

    [Fact]
    public void Test_does_not_write_and_clears_CF_OF()
    {
        // A8 0F = TEST AL, 0x0F with AL=0xF0 → AND result 0x00 (ZF set), AL unchanged, CF=OF=0.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x00F0);
        cpu.SetRegister("FLAGS", (ushort)(cpu.GetRegister("FLAGS") | CF | OF));   // dirty CF/OF going in
        LoadCode(bus, 0, 0, 0xA8, 0x0F);
        cpu.Step();
        Assert.Equal(0xF0u, cpu.GetRegister("AL"));   // unchanged
        Assert.True(Flag(cpu, ZF));
        Assert.False(Flag(cpu, CF));
        Assert.False(Flag(cpu, OF));
    }

    [Fact]
    public void Neg_byte_sets_CF_when_operand_nonzero()
    {
        // F6 D8 = NEG AL (group /3, mod=11 r/m=000). AL=0x01 → 0xFF. CF set (operand != 0), SF set.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0001);
        LoadCode(bus, 0, 0, 0xF6, 0xD8);
        cpu.Step();
        Assert.Equal(0xFFu, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, SF));
    }

    [Fact]
    public void Not_byte_sets_no_flags()
    {
        // F6 D0 = NOT AL (group /2). AL=0x0F → 0xF0. No flags change.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x000F);
        cpu.SetRegister("FLAGS", 0x0000);
        LoadCode(bus, 0, 0, 0xF6, 0xD0);
        cpu.Step();
        Assert.Equal(0xF0u, cpu.GetRegister("AL"));
        Assert.Equal(0, ((ushort)cpu.GetRegister("FLAGS")) & (CF | PF | AF | ZF | SF | OF));
    }

    [Fact]
    public void Mul_byte_sets_CF_OF_when_high_half_nonzero()
    {
        // F6 E3 = MUL BL (group /4, r/m=011(BL)). AL=0x10 * BL=0x10 = 0x0100 → AX. AH=0x01 != 0 ⇒ CF=OF=1.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0010);   // AL=0x10
        cpu.SetRegister("BX", 0x0010);   // BL=0x10
        LoadCode(bus, 0, 0, 0xF6, 0xE3);
        cpu.Step();
        Assert.Equal(0x0100u, cpu.GetRegister("AX"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, OF));
    }

    [Fact]
    public void Mul_byte_clears_CF_OF_when_high_half_zero()
    {
        // F6 E3 = MUL BL. AL=0x03 * BL=0x04 = 0x0C → AX. AH=0 ⇒ CF=OF=0.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0003);
        cpu.SetRegister("BX", 0x0004);
        LoadCode(bus, 0, 0, 0xF6, 0xE3);
        cpu.Step();
        Assert.Equal(0x000Cu, cpu.GetRegister("AX"));
        Assert.False(Flag(cpu, CF));
        Assert.False(Flag(cpu, OF));
    }

    [Fact]
    public void Div_byte_valid_quotient_computes_AL_and_AH()
    {
        // F6 F3 = DIV BL (group /6, r/m=011(BL)). AX=0x0011 (17) / BL=0x05 → AL=3 (quot), AH=2 (rem).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0011);   // 17
        cpu.SetRegister("BX", 0x0005);   // divisor 5
        LoadCode(bus, 0, 0, 0xF6, 0xF3);
        cpu.Step();
        Assert.Equal(0x03u, cpu.GetRegister("AL"));   // 17 / 5 = 3
        Assert.Equal(0x02u, cpu.GetRegister("AH"));   // 17 % 5 = 2
    }

    [Fact]
    public void Div_word_valid_quotient_computes_AX_and_DX()
    {
        // F7 F3 = DIV BX (group /6, r/m=011(BX)). DX:AX = 0x0001_0000 (65536) / BX=0x0003 → AX=21845, DX=1.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DX", 0x0001);
        cpu.SetRegister("AX", 0x0000);   // DX:AX = 0x10000 = 65536
        cpu.SetRegister("BX", 0x0003);
        LoadCode(bus, 0, 0, 0xF7, 0xF3);
        cpu.Step();
        Assert.Equal(21845u, cpu.GetRegister("AX"));   // 65536 / 3 = 21845
        Assert.Equal(1u, cpu.GetRegister("DX"));       // 65536 % 3 = 1
    }

    [Fact]
    public void Add_reg_to_mem_word_writes_back_through_the_EA()
    {
        // 01 1E 00 10 = ADD [0x1000], BX (mod=00 reg=011(BX) r/m=110 disp16). DS:0x1000 += BX.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x2000);
        cpu.SetRegister("BX", 0x0001);
        uint phys = (uint)((0x2000 << 4) + 0x1000);
        bus.Write8(phys, 0xFF); bus.Write8(phys + 1, 0xFF);   // mem word = 0xFFFF
        LoadCode(bus, 0, 0, 0x01, 0x1E, 0x00, 0x10);
        cpu.Step();
        Assert.Equal((byte)0x00, bus.Read8(phys));        // 0xFFFF + 1 = 0x0000
        Assert.Equal((byte)0x00, bus.Read8(phys + 1));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, ZF));
    }
}
