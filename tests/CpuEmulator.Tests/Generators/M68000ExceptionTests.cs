using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M4.5d-1 synthetic execute tests for the exception model: RaiseException (the ONE routine — frame layout,
/// the S-bit/USP-SSP swap, the vector fetch), TRAP/TRAPV/CHK/ILLEGAL/NOP/RESET/STOP, the ÷0 vector-5 promotion,
/// the privilege gate (TrapIfUserMode), the to-CCR/SR forms, and the IPL thin stub (DD5 — synthetic-only, no
/// vector exercises it). The TomHarte sweep is the oracle for the vector-gated ops; these pin the new machinery
/// in isolation. CCR bits: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01.
/// </summary>
public class M68000ExceptionTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    // ── Task 7: RaiseException — the ONE routine (driven via the test seam) ─────────────────────────────────
    [Fact]
    public void Raise_from_user_mode_enters_supervisor_pushes_frame_vectors()
    {
        // Vector 5 (÷0). Table entry at 0x14 = 4·5. From user mode (S clear), CCR = some bits.
        var (cpu, bus) = Build();
        cpu.SetRegister("SR", 0x0011);     // user mode, X+C set in the CCR
        cpu.SetRegister("SSP", 0x9000);    // supervisor stack
        cpu.SetRegister("USP", 0x8000);
        bus.Write32(0x14, 0x0000A000);     // the vector-5 handler
        cpu.RaiseExceptionProbe(vector: 5, large: false, srAtFault: 0x0011, pcAtFault: 0x00001234);
        Assert.True(cpu.SupervisorMode);                                 // (a) supervisor entered
        Assert.Equal(0u, (uint)cpu.GetRegister("SR") & 0x8000u);         // (b) trace cleared
        Assert.Equal(0x8FFAu, (uint)cpu.GetRegister("SSP"));             // (c) SSP -= 6
        Assert.Equal(0x00001234u, bus.Read32(0x8FFC));                   // (d) the pushed PC (long, higher addr)
        Assert.Equal((ushort)0x0011, bus.Read16(0x8FFA));               //     the pushed SR (word, lowest addr)
        Assert.Equal(0xA000u, (uint)cpu.GetRegister("PC"));              // (e) PC = Read32(4·5)
        Assert.Equal(0x8000u, (uint)cpu.GetRegister("USP"));             // (f) USP unchanged
    }

    [Fact]
    public void Raise_from_supervisor_keeps_supervisor_uses_ssp()
    {
        var (cpu, bus) = Build();
        cpu.SetRegister("SR", 0x2700);     // already supervisor
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0x14, 0x0000B000);
        cpu.RaiseExceptionProbe(vector: 5, large: false, srAtFault: 0x2700, pcAtFault: 0x00005678);
        Assert.True(cpu.SupervisorMode);
        Assert.Equal(0x8FFAu, (uint)cpu.GetRegister("SSP"));
        Assert.Equal(0x00005678u, bus.Read32(0x8FFC));
        Assert.Equal(0xB000u, (uint)cpu.GetRegister("PC"));
    }

    // ── M4.5d-2a (plan T3, DD3/F): the group-0 (address/bus error) 14-byte large frame ──────────────────────
    [Fact]
    public void Raise_large_frame_pushes_the_14_byte_group0_layout()
    {
        // Vector 3 (address error). Table entry at 0xC = 4·3. The 68000 group-0 frame, lowest address first:
        //   [SSP+0x0] SSW   [SSP+0x2..0x4] access address (long)   [SSP+0x6] IR   [SSP+0x8] SR   [SSP+0xa..0xc] PC
        // pushed high-address-first as A7 decrements by 0xE (14 bytes). 2a pins the IR (the operword); the SSW +
        // access address are trace-coupled (M4.5d-2b) but the LAYOUT + the IR pinning are asserted here.
        var (cpu, bus) = Build();
        cpu.SetRegister("SR", 0x2700);     // supervisor
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0xC, 0x0000D000);      // the vector-3 handler
        cpu.RaiseLargeFrameProbe(vector: 3, srAtFault: 0x2700, pcAtFault: 0x00001234,
            accessAddress: 0x00ABCDEF, instructionRegister: 0x3E34, specialStatusWord: 0x3E35);
        Assert.True(cpu.SupervisorMode);
        Assert.Equal(0x8FF2u, (uint)cpu.GetRegister("SSP"));                  // SSP -= 0xE (14-byte frame)
        Assert.Equal((ushort)0x3E35, bus.Read16(0x8FF2));                    // [+0x0] SSW (trace-coupled — 2b)
        Assert.Equal(0x00ABCDEFu, bus.Read32(0x8FF4));                       // [+0x2] access address (long)
        Assert.Equal((ushort)0x3E34, bus.Read16(0x8FF8));                    // [+0x6] IR == the operword (PINNED)
        Assert.Equal((ushort)0x2700, bus.Read16(0x8FFA));                    // [+0x8] SR at fault
        Assert.Equal(0x00001234u, bus.Read32(0x8FFC));                       // [+0xa] PC (long)
        Assert.Equal(0xD000u, (uint)cpu.GetRegister("PC"));                  // PC = Read32(4·3)
    }

    // ── Task 8: TRAP/TRAPV/CHK/ILLEGAL/NOP/RESET/STOP ───────────────────────────────────────────────────────
    [Fact]
    public void Trap_n_vectors_to_32_plus_n()
    {
        // TRAP #3 = 0x4E43 → vector 35; table entry at 4·35 = 0x8C.
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x43));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2700);
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0x8C, 0x0000C000);
        cpu.Step();
        Assert.Equal(0xC000u, (uint)cpu.GetRegister("PC"));
        Assert.Equal(0x8FFAu, (uint)cpu.GetRegister("SSP"));   // 6-byte frame
    }

    [Fact]
    public void Trapv_traps_when_v_set_noop_when_clear()
    {
        // TRAPV = 0x4E76 → vector 7 (0x1C) when V set.
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x76));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2702);     // supervisor, V set
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0x1C, 0x0000D000);
        cpu.Step();
        Assert.Equal(0xD000u, (uint)cpu.GetRegister("PC"));

        // V clear → no-op.
        var (cpu2, _) = Build((0x1000, 0x4E), (0x1001, 0x76));
        cpu2.SetRegister("PC", 0x1000);
        cpu2.SetRegister("SR", 0x2700);    // V clear
        cpu2.Step();
        Assert.Equal(0x1002u, (uint)cpu2.GetRegister("PC"));   // fell through
    }

    [Fact]
    public void Chk_traps_when_out_of_range_noop_when_in_range()
    {
        // CHK D1,D0 = 0x4081? Encoding: 0100 ddd=000 110 eaMode=000 eaReg=001 = 0x4181 (Dn=D0, bound in D1).
        // In range [0, bound]: no trap.
        var (cpu, _) = Build((0x1000, 0x41), (0x1001, 0x81));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2700);
        cpu.SetRegister("D0", 0x00000005);   // value 5
        cpu.SetRegister("D1", 0x00000010);   // bound 16
        cpu.Step();
        Assert.Equal(0x1002u, (uint)cpu.GetRegister("PC"));    // no trap, fell through

        // value > bound → vector 6 (0x18).
        var (cpu2, bus2) = Build((0x1000, 0x41), (0x1001, 0x81));
        cpu2.SetRegister("PC", 0x1000);
        cpu2.SetRegister("SR", 0x2700);
        cpu2.SetRegister("SSP", 0x9000);
        cpu2.SetRegister("D0", 0x00000020);  // value 32 > bound
        cpu2.SetRegister("D1", 0x00000010);  // bound 16
        bus2.Write32(0x18, 0x0000E000);
        cpu2.Step();
        Assert.Equal(0xE000u, (uint)cpu2.GetRegister("PC"));   // CHK trap
    }

    [Fact]
    public void Chk_sets_n_when_value_negative()
    {
        // value < 0 → N set + vector 6.
        var (cpu, bus) = Build((0x1000, 0x41), (0x1001, 0x81));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2700);
        cpu.SetRegister("SSP", 0x9000);
        cpu.SetRegister("D0", 0x0000FFFF);   // value = -1 (.w)
        cpu.SetRegister("D1", 0x00000010);   // bound 16
        bus.Write32(0x18, 0x0000E000);
        cpu.Step();
        // The pushed SR (at SSP) reflects N set (the comparison set it before the trap).
        ushort pushedSr = bus.Read16(0x8FFA);
        Assert.Equal(0x08, pushedSr & 0x08);   // N set in the stacked SR
    }

    [Fact]
    public void Illegal_vectors_to_4()
    {
        // ILLEGAL = 0x4AFC → vector 4 (0x10).
        var (cpu, bus) = Build((0x1000, 0x4A), (0x1001, 0xFC));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2700);
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0x10, 0x0000F000);
        cpu.Step();
        Assert.Equal(0xF000u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void Nop_changes_nothing()
    {
        var (cpu, _) = Build((0x1000, 0x4E), (0x1001, 0x71));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2715);
        cpu.SetRegister("D0", 0x12345678);
        cpu.Step();
        Assert.Equal(0x12345678u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x2715u, (uint)cpu.GetRegister("SR"));    // unchanged
    }

    [Fact]
    public void Reset_in_user_mode_raises_privilege_violation()
    {
        // RESET = 0x4E70, user mode → vector 8 (0x20).
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x70));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0000);     // user mode
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0x20, 0x0000A000);
        cpu.Step();
        Assert.Equal(0xA000u, (uint)cpu.GetRegister("PC"));
        Assert.True(cpu.SupervisorMode);
    }

    [Fact]
    public void Stop_in_supervisor_loads_sr_from_imm()
    {
        // STOP #imm = 0x4E72, imm word 0x2000 (supervisor, all CCR clear). Supervisor mode → load SR.
        var (cpu, _) = Build((0x1000, 0x4E), (0x1001, 0x72), (0x1002, 0x20), (0x1003, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2700);     // supervisor
        cpu.Step();
        Assert.Equal(0x2000u, (uint)cpu.GetRegister("SR"));    // SR loaded from the imm
    }

    [Fact]
    public void Stop_in_user_mode_raises_privilege_violation()
    {
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x72), (0x1002, 0x20), (0x1003, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0000);     // user mode
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0x20, 0x0000A000);
        cpu.Step();
        Assert.Equal(0xA000u, (uint)cpu.GetRegister("PC"));
    }

    // ── Task 9: the ÷0 vector-5 promotion ───────────────────────────────────────────────────────────────────
    [Fact]
    public void Divu_by_zero_raises_vector_5()
    {
        // DIVU D1,D0 = 0x80C1, divisor D1 = 0 → vector 5 (0x14). Dn unchanged; supervisor entered; frame pushed.
        var (cpu, bus) = Build((0x1000, 0x80), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2700);
        cpu.SetRegister("SSP", 0x9000);
        cpu.SetRegister("D0", 0x00010005);
        cpu.SetRegister("D1", 0x00000000);   // divisor 0
        bus.Write32(0x14, 0x00007000);
        cpu.Step();
        Assert.Equal(0x00010005u, (uint)cpu.GetRegister("D0"));   // Dn unchanged (no write before the trap)
        Assert.Equal(0x7000u, (uint)cpu.GetRegister("PC"));       // vectored
        Assert.Equal(0x8FFAu, (uint)cpu.GetRegister("SSP"));      // 6-byte frame
    }

    [Fact]
    public void Divu_nonzero_is_unchanged_behavior()
    {
        var (cpu, _) = Build((0x1000, 0x80), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00010005);
        cpu.SetRegister("D1", 0x00000010);
        cpu.Step();
        Assert.Equal(0x00051000u, (uint)cpu.GetRegister("D0"));   // the M4.5b green result
    }

    // ── Task 10: ANDI/ORI/EORI to CCR/SR ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Ori_to_ccr_sets_bits()
    {
        // ORItoCCR #0x0F = 0x003C, imm 0x000F.
        var (cpu, _) = Build((0x1000, 0x00), (0x1001, 0x3C), (0x1002, 0x00), (0x1003, 0x0F));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0010);     // X set
        cpu.Step();
        Assert.Equal(0x1Fu, (uint)cpu.GetRegister("SR") & 0x1F);   // X | 0x0F = 0x1F
    }

    [Fact]
    public void Andi_to_ccr_clears_bits()
    {
        // ANDItoCCR #0x00 = 0x023C, imm 0x0000 → clears the CCR.
        var (cpu, _) = Build((0x1000, 0x02), (0x1001, 0x3C), (0x1002, 0x00), (0x1003, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x001F);     // all CCR set
        cpu.Step();
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x1F);
    }

    [Fact]
    public void Eori_to_ccr_toggles()
    {
        // EORItoCCR #0x0F = 0x0A3C, imm 0x000F.
        var (cpu, _) = Build((0x1000, 0x0A), (0x1001, 0x3C), (0x1002, 0x00), (0x1003, 0x0F));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0005);     // Z + C
        cpu.Step();
        Assert.Equal(0x0Au, (uint)cpu.GetRegister("SR") & 0x1F);   // 0x05 ^ 0x0F = 0x0A
    }

    [Fact]
    public void Ori_to_sr_in_supervisor_sets_bits()
    {
        // ORItoSR #0x0700 = 0x007C, imm 0x0700 (set the interrupt mask).
        var (cpu, _) = Build((0x1000, 0x00), (0x1001, 0x7C), (0x1002, 0x07), (0x1003, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2000);     // supervisor, mask 0
        cpu.Step();
        Assert.Equal(0x2700u, (uint)cpu.GetRegister("SR"));    // mask set to 7
    }

    [Fact]
    public void Andi_to_sr_in_user_mode_raises_privilege_violation()
    {
        // ANDItoSR = 0x027C, user mode → vector 8.
        var (cpu, bus) = Build((0x1000, 0x02), (0x1001, 0x7C), (0x1002, 0x00), (0x1003, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0000);     // user mode
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0x20, 0x0000A000);
        cpu.Step();
        Assert.Equal(0xA000u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void Andi_to_sr_clearing_s_rebanks_a7_to_usp()
    {
        // ANDItoSR #0xDFFF (clear S=bit13) from supervisor → A7 re-banks to USP mid-instruction.
        var (cpu, _) = Build((0x1000, 0x02), (0x1001, 0x7C), (0x1002, 0xDF), (0x1003, 0xFF));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2700);     // supervisor
        cpu.SetRegister("USP", 0x8000);
        cpu.SetRegister("SSP", 0x9000);
        cpu.Step();
        Assert.False(cpu.SupervisorMode);  // S cleared
        Assert.Equal(0x8000u, (uint)cpu.GetRegister("USP"));   // unchanged
    }

    // ── Task 12: the IPL thin stub (DD5 — synthetic-only; NO vector exercises it) ───────────────────────────
    [Fact]
    public void Ipl_above_mask_services_interrupt_via_autovector()
    {
        // IPL 5 vs SR mask 3 → pending → acknowledge: supervisor, frame pushed, mask set to 5, PC = autovector
        // 24+5 = 29 (table entry 4·29 = 0x74).
        var (cpu, bus) = Build();
        cpu.SetRegister("SR", 0x2300);     // supervisor, mask 3
        cpu.SetRegister("SSP", 0x9000);
        cpu.SetRegister("PC", 0x00001234);
        bus.Write32(0x74, 0x0000A500);
        cpu.SetInterruptLevel(5);
        Assert.True(cpu.InterruptPending);
        cpu.Step();
        Assert.Equal(0xA500u, (uint)cpu.GetRegister("PC"));    // autovector 29
        Assert.Equal(0x8FFAu, (uint)cpu.GetRegister("SSP"));   // frame pushed
        Assert.Equal(5u, ((uint)cpu.GetRegister("SR") >> 8) & 7u);   // mask set to the serviced level
    }

    [Fact]
    public void Ipl_at_or_below_mask_is_not_pending()
    {
        var (cpu, _) = Build();
        cpu.SetRegister("SR", 0x2300);     // mask 3
        cpu.SetInterruptLevel(2);          // 2 <= 3 → masked
        Assert.False(cpu.InterruptPending);
    }

    [Fact]
    public void Ipl_level_7_is_non_maskable()
    {
        var (cpu, _) = Build();
        cpu.SetRegister("SR", 0x2700);     // mask 7
        cpu.SetInterruptLevel(7);          // level 7 always pending
        Assert.True(cpu.InterruptPending);
    }
}
