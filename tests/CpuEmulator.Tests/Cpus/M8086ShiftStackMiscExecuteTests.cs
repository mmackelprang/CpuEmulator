using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>
/// M5.5c — vector-free synthetic proof of the 8086 shift/rotate + stack + misc execute bodies. Each test
/// hand-assembles a tiny program into the 20-bit RAM at CS:IP, Steps the real <see cref="M8086Cpu"/> once, and
/// asserts the register/FLAGS/RAM result. Targets the edges the TomHarte vectors pin: the CF-from-last-shifted-
/// bit, RCL/RCR through CF, the by-CL count (no 5-bit mask on the 8086, count 0 = no-op), the SHR count&gt;1
/// OF=0 rule; the SS:SP push/pop discipline incl. the PUSH SP / POP SP / POPF reserved-bit quirks; and the misc
/// XCHG/LEA/XLAT/LAHF/SAHF/CBW/CWD + flag-control ops. Mirrors <see cref="M8086AluExecuteTests"/>'s harness.
/// </summary>
public class M8086ShiftStackMiscExecuteTests
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

    private const ushort CF = 1 << 0, PF = 1 << 2, AF = 1 << 4, ZF = 1 << 6, SF = 1 << 7,
                         IF = 1 << 9, DF = 1 << 10, OF = 1 << 11;

    private static bool Flag(M8086Cpu cpu, ushort bit) => ((ushort)cpu.GetRegister("FLAGS") & bit) != 0;
    private static void SetFlags(M8086Cpu cpu, ushort v) => cpu.SetRegister("FLAGS", v);

    // ── Shift / rotate ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shl_byte_by_1_sets_CF_from_the_top_bit_and_OF()
    {
        // D0 /4 = SHL r/m8,1. AL=0x81 → 0x02, CF=1 (top bit shifted out), OF = MSB(result)^CF = 0^1 = 1.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0081);
        LoadCode(bus, 0, 0, 0xD0, 0xE0);   // ModR/M E0 = mod11 reg100(SHL) rm000(AL)
        cpu.Step();
        Assert.Equal(0x02u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, OF));
    }

    [Fact]
    public void Shr_byte_by_1_sets_CF_from_the_low_bit_and_OF_from_original_msb()
    {
        // D0 /5 = SHR r/m8,1. AL=0x81 → 0x40, CF=1 (low bit out), OF = original MSB = 1 (count==1).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0081);
        LoadCode(bus, 0, 0, 0xD0, 0xE8);   // E8 = mod11 reg101(SHR) rm000
        cpu.Step();
        Assert.Equal(0x40u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, OF));
    }

    [Fact]
    public void Shr_byte_by_CL_greater_than_one_clears_OF()
    {
        // D2 /5 = SHR r/m8,CL. AL=0x80, CL=2 → 0x20, CF=0, OF=0 (count>1 ⇒ OF defined as 0 on the 8086).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0080);
        cpu.SetRegister("CX", 0x0002);
        LoadCode(bus, 0, 0, 0xD2, 0xE8);   // E8 = mod11 reg101(SHR) rm000(AL)
        cpu.Step();
        Assert.Equal(0x20u, cpu.GetRegister("AL"));
        Assert.False(Flag(cpu, CF));
        Assert.False(Flag(cpu, OF));
    }

    [Fact]
    public void Sar_byte_preserves_sign()
    {
        // D0 /7 = SAR r/m8,1. AL=0x80 → 0xC0 (sign-fill), CF=0, OF=0.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0080);
        LoadCode(bus, 0, 0, 0xD0, 0xF8);   // F8 = mod11 reg111(SAR) rm000
        cpu.Step();
        Assert.Equal(0xC0u, cpu.GetRegister("AL"));
        Assert.False(Flag(cpu, CF));
        Assert.False(Flag(cpu, OF));
    }

    [Fact]
    public void Rol_byte_by_1_carries_the_top_bit_into_CF_and_LSB()
    {
        // D0 /0 = ROL r/m8,1. AL=0x80 → 0x01, CF = LSB(result) = 1.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0080);
        LoadCode(bus, 0, 0, 0xD0, 0xC0);   // C0 = mod11 reg000(ROL) rm000
        cpu.Step();
        Assert.Equal(0x01u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
    }

    [Fact]
    public void Rcl_byte_rotates_through_the_carry()
    {
        // D0 /2 = RCL r/m8,1. AL=0x80, CF=0 → 0x00 with CF=1 (the top bit rotates into CF, the old CF=0 into LSB).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0080);
        SetFlags(cpu, 0x0000);
        LoadCode(bus, 0, 0, 0xD0, 0xD0);   // D0 = mod11 reg010(RCL) rm000
        cpu.Step();
        Assert.Equal(0x00u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
    }

    [Fact]
    public void Rcr_byte_brings_the_old_carry_into_the_top()
    {
        // D0 /3 = RCR r/m8,1. AL=0x00, CF=1 → 0x80 (old CF rotates into bit7), CF=0 (the old LSB).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0000);
        SetFlags(cpu, CF);
        LoadCode(bus, 0, 0, 0xD0, 0xD8);   // D8 = mod11 reg011(RCR) rm000
        cpu.Step();
        Assert.Equal(0x80u, cpu.GetRegister("AL"));
        Assert.False(Flag(cpu, CF));
    }

    [Fact]
    public void Shift_by_CL_zero_is_a_no_op_and_leaves_flags()
    {
        // D2 /4 = SHL r/m8,CL with CL=0 ⇒ no operation, no flag change (the 8086 count-0 rule).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0042);
        cpu.SetRegister("CX", 0x0000);
        SetFlags(cpu, CF | ZF);            // pre-existing flags must survive
        LoadCode(bus, 0, 0, 0xD2, 0xE0);   // E0 = mod11 reg100(SHL) rm000
        cpu.Step();
        Assert.Equal(0x42u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, ZF));
    }

    [Fact]
    public void Shift_word_by_CL_uses_the_full_8bit_count_no_5bit_mask()
    {
        // D3 /5 = SHR r/m16,CL with CX=0x0020 (32). The 8086 does NOT mask to 5 bits, so a count of 32 shifts
        // the whole word out ⇒ result 0 (a 286 would mask 32→0 and leave the operand). AX=0xFFFF → 0x0000.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0xFFFF);
        cpu.SetRegister("CX", 0x0020);
        LoadCode(bus, 0, 0, 0xD3, 0xE8);   // E8 = mod11 reg101(SHR) rm000(AX)
        cpu.Step();
        Assert.Equal(0x0000u, cpu.GetRegister("AX"));
        Assert.True(Flag(cpu, ZF));
    }

    // ── Stack ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Push_pop_round_trips_through_SS_SP()
    {
        // 50 = PUSH AX (SP-=2, write AX at SS:SP). AX=0x1234, SS=0x2000, SP=0x0100.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x1234);
        cpu.SetRegister("SS", 0x2000);
        cpu.SetRegister("SP", 0x0100);
        LoadCode(bus, 0, 0, 0x50);
        cpu.Step();
        Assert.Equal(0x00FEu, cpu.GetRegister("SP"));
        uint phys = (uint)((0x2000 << 4) + 0x00FE);
        Assert.Equal(0x34, bus.Read8(phys));
        Assert.Equal(0x12, bus.Read8(phys + 1));

        // 5B = POP BX from the same stack image → BX=0x1234, SP back to 0x0100.
        cpu.SetRegister("IP", 0);
        LoadCode(bus, 0, 0, 0x5B);
        cpu.Step();
        Assert.Equal(0x1234u, cpu.GetRegister("BX"));
        Assert.Equal(0x0100u, cpu.GetRegister("SP"));
    }

    [Fact]
    public void Push_SP_pushes_the_post_decrement_value()
    {
        // 54 = PUSH SP. The 8086 pushes SP AFTER the decrement: [SS:SP-2] = SP-2.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("SS", 0x3000);
        cpu.SetRegister("SP", 0x0010);
        LoadCode(bus, 0, 0, 0x54);
        cpu.Step();
        Assert.Equal(0x000Eu, cpu.GetRegister("SP"));
        uint phys = (uint)((0x3000 << 4) + 0x000E);
        ushort pushed = (ushort)(bus.Read8(phys) | (bus.Read8(phys + 1) << 8));
        Assert.Equal(0x000E, pushed);   // post-decrement SP, not 0x0010
    }

    [Fact]
    public void Pop_SP_takes_the_popped_value_over_the_increment()
    {
        // 5C = POP SP. The popped word wins (the read sets SP last, not SP+2).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("SS", 0x4000);
        cpu.SetRegister("SP", 0x0020);
        uint phys = (uint)((0x4000 << 4) + 0x0020);
        bus.Write8(phys, 0xCD); bus.Write8(phys + 1, 0xAB);   // stack word 0xABCD
        LoadCode(bus, 0, 0, 0x5C);
        cpu.Step();
        Assert.Equal(0xABCDu, cpu.GetRegister("SP"));   // the popped value, NOT 0x0022
    }

    [Fact]
    public void Pushf_pushes_flags_and_popf_forces_the_reserved_bits()
    {
        // 9C = PUSHF, then 9D = POPF of an arbitrary stack word. POPF forces bits 12-15=1, bit1=1, bits3,5=0.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("SS", 0x5000);
        cpu.SetRegister("SP", 0x0030);
        // Stack word 0x0000 → POPF should yield 0xF002 (only forced bits; all defined flags clear).
        uint phys = (uint)((0x5000 << 4) + 0x0030);
        bus.Write8(phys, 0x00); bus.Write8(phys + 1, 0x00);
        LoadCode(bus, 0, 0, 0x9D);
        cpu.Step();
        Assert.Equal(0xF002u, cpu.GetRegister("FLAGS"));
    }

    // ── Misc ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Xchg_register_swaps_both_operands()
    {
        // 91 = XCHG CX,AX. AX=0x1111, CX=0x2222 → AX=0x2222, CX=0x1111.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x1111);
        cpu.SetRegister("CX", 0x2222);
        LoadCode(bus, 0, 0, 0x91);
        cpu.Step();
        Assert.Equal(0x2222u, cpu.GetRegister("AX"));
        Assert.Equal(0x1111u, cpu.GetRegister("CX"));
    }

    [Fact]
    public void Lea_loads_the_offset_not_the_memory_content()
    {
        // 8D /r LEA. ModR/M = 47 05: mod01 reg000(AX) rm111([BX]+disp8). BX=0x0100, disp8=0x05 ⇒ EA offset 0x0105.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("BX", 0x0100);
        LoadCode(bus, 0, 0, 0x8D, 0x47, 0x05);   // LEA AX, [BX+5]
        cpu.Step();
        Assert.Equal(0x0105u, cpu.GetRegister("AX"));
    }

    [Fact]
    public void Xlat_loads_from_DS_BX_plus_AL()
    {
        // D7 = XLAT. DS=0x1000, BX=0x0010, AL=0x05 ⇒ load [DS:0x0015] into AL.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x1000);
        cpu.SetRegister("BX", 0x0010);
        cpu.SetRegister("AX", 0x0005);
        uint phys = (uint)((0x1000 << 4) + 0x0015);
        bus.Write8(phys, 0x9A);
        LoadCode(bus, 0, 0, 0xD7);
        cpu.Step();
        Assert.Equal(0x9Au, cpu.GetRegister("AL"));
    }

    [Fact]
    public void Lahf_loads_AH_from_the_low_flags_byte()
    {
        // 9F = LAHF. FLAGS low byte canonical: bit1 always 1. With CF|ZF set ⇒ AH = ZF(0x40)|CF(0x01)|0x02 = 0x43.
        var cpu = NewCpu(out var bus);
        SetFlags(cpu, CF | ZF);
        LoadCode(bus, 0, 0, 0x9F);
        cpu.Step();
        Assert.Equal(0x43u, cpu.GetRegister("AH"));
    }

    [Fact]
    public void Sahf_sets_the_low_flags_byte_from_AH()
    {
        // 9E = SAHF. AH=0xC1 (SF|... actually 0x80 SF, 0x40 ZF, 0x01 CF) → SF,ZF,CF set in FLAGS.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0xC100);   // AH=0xC1
        SetFlags(cpu, 0);
        LoadCode(bus, 0, 0, 0x9E);
        cpu.Step();
        Assert.True(Flag(cpu, SF));
        Assert.True(Flag(cpu, ZF));
        Assert.True(Flag(cpu, CF));
    }

    [Fact]
    public void Cbw_sign_extends_AL_into_AH()
    {
        // 98 = CBW. AL=0x80 → AX=0xFF80.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0080);
        LoadCode(bus, 0, 0, 0x98);
        cpu.Step();
        Assert.Equal(0xFF80u, cpu.GetRegister("AX"));
    }

    [Fact]
    public void Cwd_sign_extends_AX_into_DX()
    {
        // 99 = CWD. AX=0x8000 → DX=0xFFFF.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x8000);
        LoadCode(bus, 0, 0, 0x99);
        cpu.Step();
        Assert.Equal(0xFFFFu, cpu.GetRegister("DX"));
    }

    [Fact]
    public void Flag_control_ops_toggle_their_flags()
    {
        // F9 STC sets CF; F8 CLC clears it; F5 CMC complements it; FD STD sets DF; FB STI sets IF.
        var cpu = NewCpu(out var bus);
        SetFlags(cpu, 0);
        LoadCode(bus, 0, 0, 0xF9);   // STC
        cpu.Step();
        Assert.True(Flag(cpu, CF));

        cpu.SetRegister("IP", 0);
        LoadCode(bus, 0, 0, 0xF5);   // CMC ⇒ CF back to 0
        cpu.Step();
        Assert.False(Flag(cpu, CF));

        cpu.SetRegister("IP", 0);
        LoadCode(bus, 0, 0, 0xFD);   // STD ⇒ DF set
        cpu.Step();
        Assert.True(Flag(cpu, DF));

        cpu.SetRegister("IP", 0);
        LoadCode(bus, 0, 0, 0xFB);   // STI ⇒ IF set
        cpu.Step();
        Assert.True(Flag(cpu, IF));
    }
}
