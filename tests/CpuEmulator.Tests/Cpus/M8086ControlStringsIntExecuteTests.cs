using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>
/// M5.5d — vector-free synthetic proof of the 8086 control-flow + strings/REP + IN/OUT + interrupt execute
/// bodies. Each test hand-assembles a tiny program into the 20-bit RAM at CS:IP, Steps the real
/// <see cref="M8086Cpu"/> once, and asserts the register/FLAGS/RAM result. Targets the edges the TomHarte
/// vectors pin: the relative-jump base (post-advance IP + sign-extended rel), the Jcc condition matrix, the
/// near/far CALL/RET push/pop discipline, the LOOP/JCXZ CX behavior, the REP CX-counted DF-directed string loop
/// (+ REPE/REPNE ZF termination), the IN open-bus / OUT no-op data-axis model, and the INT/IRET IVT push
/// sequence. Mirrors the M5.5c synthetic harness.
/// </summary>
public class M8086ControlStringsIntExecuteTests
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
                         TF = 1 << 8, IF = 1 << 9, DF = 1 << 10, OF = 1 << 11;

    private static bool Flag(M8086Cpu cpu, ushort bit) => ((ushort)cpu.GetRegister("FLAGS") & bit) != 0;

    // ── Control flow ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Jmp_short_adds_sign_extended_rel8_to_the_post_advance_ip()
    {
        // EB 05 at IP=0x100 ⇒ IP advances to 0x102, then +5 = 0x107.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x100);
        LoadCode(bus, 0, 0x100, 0xEB, 0x05);
        cpu.Step();
        Assert.Equal(0x107u, cpu.GetRegister("IP"));
    }

    [Fact]
    public void Jmp_short_backward_uses_a_negative_rel8()
    {
        // EB FB (-5) at IP=0x100 ⇒ IP 0x102 - 5 = 0xFD.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x100);
        LoadCode(bus, 0, 0x100, 0xEB, 0xFB);
        cpu.Step();
        Assert.Equal(0xFDu, cpu.GetRegister("IP"));
    }

    [Fact]
    public void Je_taken_when_zf_set_not_taken_when_clear()
    {
        // 74 10 = JE rel8.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x200); cpu.SetRegister("FLAGS", ZF);
        LoadCode(bus, 0, 0x200, 0x74, 0x10);
        cpu.Step();
        Assert.Equal((uint)(0x202 + 0x10), cpu.GetRegister("IP"));   // taken

        var cpu2 = NewCpu(out var bus2);
        cpu2.SetRegister("CS", 0); cpu2.SetRegister("IP", 0x200); cpu2.SetRegister("FLAGS", 0);
        LoadCode(bus2, 0, 0x200, 0x74, 0x10);
        cpu2.Step();
        Assert.Equal(0x202u, cpu2.GetRegister("IP"));                // not taken
    }

    [Fact]
    public void Jg_signed_greater_uses_zf_sf_of()
    {
        // 7F = JG: taken iff ZF==0 AND SF==OF. SF=OF=0, ZF=0 ⇒ taken.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x10); cpu.SetRegister("FLAGS", 0);
        LoadCode(bus, 0, 0x10, 0x7F, 0x04);
        cpu.Step();
        Assert.Equal((uint)(0x12 + 4), cpu.GetRegister("IP"));
    }

    [Fact]
    public void Near_call_pushes_return_ip_then_jumps()
    {
        // E8 rel16 near CALL. SS=0x100, SP=0x20. At CS=0,IP=0x300: E8 00 01 (rel16=0x100).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x300);
        cpu.SetRegister("SS", 0x100); cpu.SetRegister("SP", 0x20);
        LoadCode(bus, 0, 0x300, 0xE8, 0x00, 0x01);
        cpu.Step();
        // return IP = 0x303 (after the 3-byte CALL); target = 0x303 + 0x100 = 0x403.
        Assert.Equal(0x403u, cpu.GetRegister("IP"));
        Assert.Equal(0x1Eu, cpu.GetRegister("SP"));   // SP -= 2
        // the pushed return IP at SS:SP = 0x100:0x1E = phys 0x101E, little-endian 0x0303.
        Assert.Equal(0x03, bus.Read8(0x101E));
        Assert.Equal(0x03, bus.Read8(0x101F));
    }

    [Fact]
    public void Far_call_direct_pushes_cs_and_ip_then_loads_cs_ip()
    {
        // 9A off lo/hi seg lo/hi = CALL ptr16:16. target 0x5678:0x1234.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0x1000); cpu.SetRegister("IP", 0x10);
        cpu.SetRegister("SS", 0x200); cpu.SetRegister("SP", 0x40);
        LoadCode(bus, 0x1000, 0x10, 0x9A, 0x34, 0x12, 0x78, 0x56);
        cpu.Step();
        Assert.Equal(0x1234u, cpu.GetRegister("IP"));
        Assert.Equal(0x5678u, cpu.GetRegister("CS"));
        Assert.Equal(0x3Cu, cpu.GetRegister("SP"));   // SP -= 4 (CS + IP pushed)
        // pushed CS first (at higher addr), then IP. SS:SP after = 0x200:0x3C ⇒ phys 0x203C holds return IP.
        Assert.Equal(0x15, bus.Read8(0x203C));        // return IP = 0x10+5 = 0x15
        Assert.Equal(0x00, bus.Read8(0x203D));
        Assert.Equal(0x00, bus.Read8(0x203E));        // CS 0x1000 lo
        Assert.Equal(0x10, bus.Read8(0x203F));        // CS 0x1000 hi
    }

    [Fact]
    public void Near_ret_pops_ip()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x500);
        cpu.SetRegister("SS", 0x300); cpu.SetRegister("SP", 0x10);
        bus.Write8(0x3010, 0xCD); bus.Write8(0x3011, 0xAB);   // SS:SP holds 0xABCD
        LoadCode(bus, 0, 0x500, 0xC3);
        cpu.Step();
        Assert.Equal(0xABCDu, cpu.GetRegister("IP"));
        Assert.Equal(0x12u, cpu.GetRegister("SP"));   // SP += 2
    }

    [Fact]
    public void Ret_imm16_pops_ip_and_adjusts_sp()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x500);
        cpu.SetRegister("SS", 0x300); cpu.SetRegister("SP", 0x10);
        bus.Write8(0x3010, 0x34); bus.Write8(0x3011, 0x12);   // popped IP 0x1234
        LoadCode(bus, 0, 0x500, 0xC2, 0x06, 0x00);            // RET 6
        cpu.Step();
        Assert.Equal(0x1234u, cpu.GetRegister("IP"));
        Assert.Equal((uint)(0x10 + 2 + 6), cpu.GetRegister("SP"));   // pop (+2) then +imm16 (6)
    }

    [Fact]
    public void Loop_decrements_cx_and_jumps_while_nonzero()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x100); cpu.SetRegister("CX", 3);
        LoadCode(bus, 0, 0x100, 0xE2, 0xFE);   // LOOP -2
        cpu.Step();
        Assert.Equal(2u, cpu.GetRegister("CX"));
        Assert.Equal(0x100u, cpu.GetRegister("IP"));   // 0x102 - 2 = 0x100 (taken)

        // CX=1 ⇒ becomes 0 ⇒ not taken.
        var cpu2 = NewCpu(out var bus2);
        cpu2.SetRegister("CS", 0); cpu2.SetRegister("IP", 0x100); cpu2.SetRegister("CX", 1);
        LoadCode(bus2, 0, 0x100, 0xE2, 0xFE);
        cpu2.Step();
        Assert.Equal(0u, cpu2.GetRegister("CX"));
        Assert.Equal(0x102u, cpu2.GetRegister("IP"));
    }

    [Fact]
    public void Jcxz_jumps_only_when_cx_zero_and_does_not_decrement()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x100); cpu.SetRegister("CX", 0);
        LoadCode(bus, 0, 0x100, 0xE3, 0x10);   // JCXZ +0x10
        cpu.Step();
        Assert.Equal(0u, cpu.GetRegister("CX"));
        Assert.Equal((uint)(0x102 + 0x10), cpu.GetRegister("IP"));
    }

    // ── Strings + REP ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Movsb_copies_ds_si_to_es_di_and_increments_when_df_clear()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0);
        cpu.SetRegister("DS", 0x100); cpu.SetRegister("ES", 0x200);
        cpu.SetRegister("SI", 0x10); cpu.SetRegister("DI", 0x20);
        cpu.SetRegister("FLAGS", 0);   // DF=0 ⇒ increment
        bus.Write8(0x1010, 0xAB);      // DS:SI = 0x100:0x10
        LoadCode(bus, 0, 0, 0xA4);     // MOVSB
        cpu.Step();
        Assert.Equal(0xAB, bus.Read8(0x2020));   // ES:DI = 0x200:0x20
        Assert.Equal(0x11u, cpu.GetRegister("SI"));
        Assert.Equal(0x21u, cpu.GetRegister("DI"));
    }

    [Fact]
    public void Movsb_decrements_when_df_set()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0);
        cpu.SetRegister("DS", 0x100); cpu.SetRegister("ES", 0x200);
        cpu.SetRegister("SI", 0x10); cpu.SetRegister("DI", 0x20);
        cpu.SetRegister("FLAGS", DF);
        bus.Write8(0x1010, 0xCD);
        LoadCode(bus, 0, 0, 0xA4);
        cpu.Step();
        Assert.Equal(0xCD, bus.Read8(0x2020));
        Assert.Equal(0xFu, cpu.GetRegister("SI"));
        Assert.Equal(0x1Fu, cpu.GetRegister("DI"));
    }

    [Fact]
    public void Rep_movsb_copies_cx_bytes_and_zeroes_cx()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0);
        cpu.SetRegister("DS", 0x100); cpu.SetRegister("ES", 0x200);
        cpu.SetRegister("SI", 0); cpu.SetRegister("DI", 0); cpu.SetRegister("CX", 3);
        cpu.SetRegister("FLAGS", 0);
        bus.Write8(0x1000, 1); bus.Write8(0x1001, 2); bus.Write8(0x1002, 3);
        LoadCode(bus, 0, 0, 0xF3, 0xA4);   // REP MOVSB
        cpu.Step();
        Assert.Equal(1, bus.Read8(0x2000));
        Assert.Equal(2, bus.Read8(0x2001));
        Assert.Equal(3, bus.Read8(0x2002));
        Assert.Equal(0u, cpu.GetRegister("CX"));
        Assert.Equal(3u, cpu.GetRegister("SI"));
        Assert.Equal(3u, cpu.GetRegister("DI"));
    }

    [Fact]
    public void Rep_with_cx_zero_does_nothing()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0);
        cpu.SetRegister("DS", 0x100); cpu.SetRegister("ES", 0x200);
        cpu.SetRegister("SI", 0x10); cpu.SetRegister("DI", 0x20); cpu.SetRegister("CX", 0);
        LoadCode(bus, 0, 0, 0xF3, 0xA4);
        cpu.Step();
        Assert.Equal(0u, cpu.GetRegister("CX"));
        Assert.Equal(0x10u, cpu.GetRegister("SI"));   // unchanged
        Assert.Equal(0x20u, cpu.GetRegister("DI"));
    }

    [Fact]
    public void Stosb_stores_al_and_lodsb_loads_al()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0);
        cpu.SetRegister("ES", 0x200); cpu.SetRegister("DI", 0x30);
        cpu.SetRegister("AX", 0x0042); cpu.SetRegister("FLAGS", 0);
        LoadCode(bus, 0, 0, 0xAA);   // STOSB
        cpu.Step();
        Assert.Equal(0x42, bus.Read8(0x2030));
        Assert.Equal(0x31u, cpu.GetRegister("DI"));

        var cpu2 = NewCpu(out var bus2);
        cpu2.SetRegister("CS", 0); cpu2.SetRegister("IP", 0);
        cpu2.SetRegister("DS", 0x100); cpu2.SetRegister("SI", 0x40); cpu2.SetRegister("FLAGS", 0);
        bus2.Write8(0x1040, 0x99);
        LoadCode(bus2, 0, 0, 0xAC);   // LODSB
        cpu2.Step();
        Assert.Equal(0x99u, cpu2.GetRegister("AL"));
        Assert.Equal(0x41u, cpu2.GetRegister("SI"));
    }

    [Fact]
    public void Repe_scasb_stops_when_byte_mismatches()
    {
        // REPE SCASB: scan ES:DI for the first byte NOT equal to AL. AL=0x00. buffer = 00 00 05.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0);
        cpu.SetRegister("ES", 0x200); cpu.SetRegister("DI", 0); cpu.SetRegister("CX", 5);
        cpu.SetRegister("AX", 0x0000); cpu.SetRegister("FLAGS", 0);
        bus.Write8(0x2000, 0x00); bus.Write8(0x2001, 0x00); bus.Write8(0x2002, 0x05);
        LoadCode(bus, 0, 0, 0xF3, 0xAE);   // REPE SCASB
        cpu.Step();
        // iterations: DI0 (00==AL ZF=1 continue), DI1 (00 continue), DI2 (05 != AL ZF=0 stop). 3 iterations.
        Assert.Equal(2u, cpu.GetRegister("CX"));    // 5 - 3
        Assert.Equal(3u, cpu.GetRegister("DI"));
        Assert.False(Flag(cpu, ZF));                // last compare mismatched
    }

    // ── IN / OUT ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void In_reads_open_bus_ff()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0); cpu.SetRegister("AX", 0x1234);
        LoadCode(bus, 0, 0, 0xE4, 0x10);   // IN AL, 0x10
        cpu.Step();
        Assert.Equal(0xFFu, cpu.GetRegister("AL"));
        Assert.Equal(0x12u, cpu.GetRegister("AH"));   // AH unchanged

        var cpu2 = NewCpu(out var bus2);
        cpu2.SetRegister("CS", 0); cpu2.SetRegister("IP", 0); cpu2.SetRegister("AX", 0x1234);
        LoadCode(bus2, 0, 0, 0xE5, 0x10);   // IN AX, 0x10
        cpu2.Step();
        Assert.Equal(0xFFFFu, cpu2.GetRegister("AX"));
    }

    [Fact]
    public void Out_has_no_data_axis_effect_beyond_ip_advance()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0); cpu.SetRegister("AX", 0x1234);
        LoadCode(bus, 0, 0, 0xE6, 0x20);   // OUT 0x20, AL
        cpu.Step();
        Assert.Equal(0x1234u, cpu.GetRegister("AX"));
        Assert.Equal(2u, cpu.GetRegister("IP"));
    }

    // ── Interrupts ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Int_n_pushes_flags_cs_ip_clears_if_tf_and_vectors_through_the_ivt()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0x1000); cpu.SetRegister("IP", 0x10);
        cpu.SetRegister("SS", 0x300); cpu.SetRegister("SP", 0x40);
        cpu.SetRegister("FLAGS", (ushort)(IF | TF | CF));
        // IVT[0x21] at phys 0x21*4 = 0x84: IP=0xBEEF, CS=0xF000.
        bus.Write8(0x84, 0xEF); bus.Write8(0x85, 0xBE); bus.Write8(0x86, 0x00); bus.Write8(0x87, 0xF0);
        LoadCode(bus, 0x1000, 0x10, 0xCD, 0x21);   // INT 0x21
        ushort flagsBefore = (ushort)cpu.GetRegister("FLAGS");
        cpu.Step();
        Assert.Equal(0xBEEFu, cpu.GetRegister("IP"));
        Assert.Equal(0xF000u, cpu.GetRegister("CS"));
        Assert.Equal(0x3Au, cpu.GetRegister("SP"));   // SP -= 6
        Assert.False(Flag(cpu, IF));                   // IF cleared on entry
        Assert.False(Flag(cpu, TF));                   // TF cleared on entry
        // pushed FLAGS (the pre-clear word) at the highest of the three slots: SS:SP+4 = 0x300:0x3E ⇒ 0x303E.
        Assert.Equal((byte)flagsBefore, bus.Read8(0x303E));
        Assert.Equal((byte)(flagsBefore >> 8), bus.Read8(0x303F));
        // pushed CS (0x1000) at 0x303C, return IP (0x12) at 0x303A.
        Assert.Equal(0x00, bus.Read8(0x303C));
        Assert.Equal(0x10, bus.Read8(0x303D));
        Assert.Equal(0x12, bus.Read8(0x303A));   // return IP = 0x10 + 2
        Assert.Equal(0x00, bus.Read8(0x303B));
    }

    [Fact]
    public void Iret_pops_ip_cs_flags_with_reserved_bit_forcing()
    {
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x800);
        cpu.SetRegister("SS", 0x300); cpu.SetRegister("SP", 0x10);
        // stack: IP=0x1234 @0x3010, CS=0x5678 @0x3012, FLAGS=0x0202 @0x3014.
        bus.Write8(0x3010, 0x34); bus.Write8(0x3011, 0x12);
        bus.Write8(0x3012, 0x78); bus.Write8(0x3013, 0x56);
        bus.Write8(0x3014, 0x02); bus.Write8(0x3015, 0x02);
        LoadCode(bus, 0, 0x800, 0xCF);   // IRET
        cpu.Step();
        Assert.Equal(0x1234u, cpu.GetRegister("IP"));
        Assert.Equal(0x5678u, cpu.GetRegister("CS"));
        Assert.Equal(0x16u, cpu.GetRegister("SP"));   // SP += 6
        // FLAGS = (0x0202 & 0x0FD5) | 0xF002 = 0x0200 | 0xF002 = 0xF202.
        Assert.Equal(0xF202u, cpu.GetRegister("FLAGS"));
    }

    [Fact]
    public void Into_traps_only_when_of_set()
    {
        // OF set ⇒ INTO vectors through IVT[4]; OF clear ⇒ no-op.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("IP", 0x50);
        cpu.SetRegister("SS", 0x300); cpu.SetRegister("SP", 0x40);
        cpu.SetRegister("FLAGS", OF);
        bus.Write8(0x10, 0x00); bus.Write8(0x11, 0x40); bus.Write8(0x12, 0x00); bus.Write8(0x13, 0xF0);  // IVT[4]=F000:4000
        LoadCode(bus, 0, 0x50, 0xCE);   // INTO
        cpu.Step();
        Assert.Equal(0x4000u, cpu.GetRegister("IP"));
        Assert.Equal(0xF000u, cpu.GetRegister("CS"));

        var cpu2 = NewCpu(out var bus2);
        cpu2.SetRegister("CS", 0); cpu2.SetRegister("IP", 0x50);
        cpu2.SetRegister("SS", 0x300); cpu2.SetRegister("SP", 0x40);
        cpu2.SetRegister("FLAGS", 0);   // OF clear
        LoadCode(bus2, 0, 0x50, 0xCE);
        cpu2.Step();
        Assert.Equal(0x51u, cpu2.GetRegister("IP"));   // no-op, just IP advance
        Assert.Equal(0x40u, cpu2.GetRegister("SP"));   // SP unchanged
    }

    [Fact]
    public void Div_by_zero_raises_int0_through_the_ivt()
    {
        // F6 /6 DIV AL by r/m8 = 0 ⇒ INT0. IVT[0] at phys 0: IP=0x0400, CS=0x0000 (the corpus landing).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("CS", 0x1000); cpu.SetRegister("IP", 0x10);
        cpu.SetRegister("SS", 0x300); cpu.SetRegister("SP", 0x40);
        cpu.SetRegister("AX", 0x0010); cpu.SetRegister("BX", 0); cpu.SetRegister("FLAGS", 0);
        bus.Write8(0x00, 0x00); bus.Write8(0x01, 0x04); bus.Write8(0x02, 0x00); bus.Write8(0x03, 0x00);
        // F6 /6 with mod=00 r/m=111 ([BX]) ⇒ ModR/M 0x37. BX=0 ⇒ DS:0 = 0 (divisor 0).
        cpu.SetRegister("DS", 0x400);
        bus.Write8(0x4000, 0x00);   // divisor at DS:BX = 0x400:0 = 0
        LoadCode(bus, 0x1000, 0x10, 0xF6, 0x37);
        cpu.Step();
        Assert.Equal(0x0400u, cpu.GetRegister("IP"));
        Assert.Equal(0x0000u, cpu.GetRegister("CS"));
        Assert.Equal(0x3Au, cpu.GetRegister("SP"));   // SP -= 6 (FLAGS:CS:IP pushed)
    }
}
