using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M4.5d-1 synthetic execute tests for the control-flow core (Bcc/BSR/BRA, DBcc, JMP/JSR/RTS/RTR/RTE, LINK/UNLK).
/// No vectors here — the TomHarte sweep (M68000M45d1TomHarteTests) is the oracle; these pin the wiring + the
/// decode (the EA-less control-op leading-word fix) + the trickier off-by-ones (DBcc -1 terminate, the An==A7
/// LINK/UNLK edge) in isolation. Build(...)/Step() mirror M68000SystemMiscExecuteTests.
/// </summary>
public class M68000ControlExecuteTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    private static DecodeResult Dec(ushort ow, ushort w1 = 0, ushort w2 = 0)
    {
        var buf = new byte[] { (byte)(ow >> 8), (byte)ow, (byte)(w1 >> 8), (byte)w1, (byte)(w2 >> 8), (byte)w2, 0, 0 };
        return M68000Cpu.Decode(new BufferFetchStream(buf, unitBytes: 2, bigEndian: true));
    }

    // ── Task 2 Step 0: the decode shape for the EA-less control ops (the leading-word fix) ──────────────────
    [Theory]
    // op, expected length, expected ext count
    [InlineData(0x6002, 2, 0)]   // Bcc.b — operword only
    [InlineData(0x6000, 4, 1)]   // Bcc.w — disp byte 0 → a disp word follows
    [InlineData(0x50C8, 4, 1)]   // DBcc  — +1 disp word
    [InlineData(0x4E50, 4, 1)]   // LINK  — +1 disp word
    [InlineData(0x4E58, 2, 0)]   // UNLK  — operword only
    [InlineData(0x4E75, 2, 0)]   // RTS   — operword only (was over-read as length 4)
    [InlineData(0x4E77, 2, 0)]   // RTR
    [InlineData(0x4E73, 2, 0)]   // RTE
    [InlineData(0x4E71, 2, 0)]   // NOP   — operword only (was over-read)
    [InlineData(0x4E70, 2, 0)]   // RESET
    [InlineData(0x4E76, 2, 0)]   // TRAPV
    [InlineData(0x4E40, 2, 0)]   // TRAP #0
    [InlineData(0x4AFC, 2, 0)]   // ILLEGAL (was over-read as length 4)
    [InlineData(0x4E72, 4, 1)]   // STOP  — +1 imm word
    [InlineData(0x4ED0, 2, 0)]   // JMP (A0) — a real EA (no leading word)
    [InlineData(0x4EE8, 4, 1)]   // JMP d16(A0) — a real EA extension word
    public void Decode_length_and_ext_count_match_the_real_encoding(ushort ow, int expLen, int expExt)
    {
        var r = Dec(ow, 0x1234);
        Assert.NotEqual(0xFFFFFFFFu, r.OperationKey);
        Assert.Equal(expLen, r.Length);
        Assert.Equal(expExt, r.ExtensionWords.Count);
    }

    // ── Task 2: Bcc/BSR/BRA ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Bra_lands_at_base_plus_disp8()
    {
        // BRA *+0x10 = 0x600E (cc 0, disp 0x0E). base = operword+2 = 0x1002; target = 0x1010.
        var (cpu, _) = Build((0x1000, 0x60), (0x1001, 0x0E));
        cpu.SetRegister("PC", 0x1000);
        cpu.Step();
        Assert.Equal(0x1010u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void Bsr_pushes_return_pc_then_branches()
    {
        // BSR *+0x10 = 0x610E (cc 1). The return PC = the post-advance PC = 0x1002 (operword len 2).
        var (cpu, bus) = Build((0x1000, 0x61), (0x1001, 0x0E));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("USP", 0x8000);
        cpu.Step();
        Assert.Equal(0x1010u, (uint)cpu.GetRegister("PC"));   // base 0x1002 + 0x0E
        Assert.Equal(0x7FFCu, (uint)cpu.GetRegister("USP"));  // -(A7) by 4
        Assert.Equal(0x1002u, bus.Read32(0x7FFC));            // the return PC pushed
    }

    [Fact]
    public void Beq_taken_when_z_set()
    {
        // BEQ *+0x10 = 0x670E (cc 7 = EQ). Z set → taken.
        var (cpu, _) = Build((0x1000, 0x67), (0x1001, 0x0E));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0004);   // Z set
        cpu.Step();
        Assert.Equal(0x1010u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void Beq_not_taken_falls_through()
    {
        // BEQ *+0x10 = 0x670E. Z clear → not taken; PC = the next instruction (0x1002).
        var (cpu, _) = Build((0x1000, 0x67), (0x1001, 0x0E));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0000);   // Z clear
        cpu.Step();
        Assert.Equal(0x1002u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void Bra_w_uses_the_disp_word()
    {
        // BRA.w = 0x6000, disp word 0x0100. base = 0x1002; target = 0x1102.
        var (cpu, _) = Build((0x1000, 0x60), (0x1001, 0x00), (0x1002, 0x01), (0x1003, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.Step();
        Assert.Equal(0x1102u, (uint)cpu.GetRegister("PC"));
    }

    // ── Task 3: DBcc ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Dbcc_true_falls_through_dn_unchanged()
    {
        // DBEQ D0,disp = 0x57C8 (cc 7 = EQ), disp word 0x00FE (-2). Z set → condition true → fall through.
        var (cpu, _) = Build((0x1000, 0x57), (0x1001, 0xC8), (0x1002, 0x00), (0x1003, 0xFE));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0004);    // Z set → EQ true
        cpu.SetRegister("D0", 0x00000005);
        cpu.Step();
        Assert.Equal(0x00000005u, (uint)cpu.GetRegister("D0"));  // unchanged
        Assert.Equal(0x1004u, (uint)cpu.GetRegister("PC"));      // past the instruction (no branch)
    }

    [Fact]
    public void Dbcc_false_decrements_and_branches()
    {
        // DBF D0,disp = 0x51C8 (cc 1 = F, never true → always decrement), disp word 0xFFFC (-4). Dn.w 5 → 4, branch.
        var (cpu, _) = Build((0x1000, 0x51), (0x1001, 0xC8), (0x1002, 0xFF), (0x1003, 0xFC));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000005);
        cpu.Step();
        Assert.Equal(0x00000004u, (uint)cpu.GetRegister("D0"));  // decremented
        Assert.Equal(0x0FFEu, (uint)cpu.GetRegister("PC"));      // base 0x1002 + (-4)
    }

    [Fact]
    public void Dbcc_terminates_at_minus_one_not_zero()
    {
        // DBF D0, Dn.w = 0 → decrement to 0xFFFF (-1) → loop terminates (no branch). The classic off-by-one.
        var (cpu, _) = Build((0x1000, 0x51), (0x1001, 0xC8), (0x1002, 0xFF), (0x1003, 0xFC));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000000);
        cpu.Step();
        Assert.Equal(0x0000FFFFu, (uint)cpu.GetRegister("D0"));  // -1 (the upper word preserved = 0)
        Assert.Equal(0x1004u, (uint)cpu.GetRegister("PC"));      // terminated: no branch
    }

    [Fact]
    public void Dbcc_preserves_upper_word_on_decrement()
    {
        // DBF: Dn = 0xABCD0001 → low word 1 → 0 (not -1), branch; upper 0xABCD preserved.
        var (cpu, _) = Build((0x1000, 0x51), (0x1001, 0xC8), (0x1002, 0xFF), (0x1003, 0xFC));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0xABCD0001);
        cpu.Step();
        Assert.Equal(0xABCD0000u, (uint)cpu.GetRegister("D0"));  // low word 0, upper preserved, branched
        Assert.Equal(0x0FFEu, (uint)cpu.GetRegister("PC"));
    }

    // ── Task 4: JMP/JSR ──────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Jmp_an_indirect_sets_pc()
    {
        // JMP (A0) = 0x4ED0 (mode 010 reg 000).
        var (cpu, _) = Build((0x1000, 0x4E), (0x1001, 0xD0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.Step();
        Assert.Equal(0x2000u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void Jsr_pushes_return_pc_then_jumps()
    {
        // JSR (A0) = 0x4E90. The return PC = the post-advance PC = 0x1002.
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x90));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.SetRegister("USP", 0x8000);
        cpu.Step();
        Assert.Equal(0x2000u, (uint)cpu.GetRegister("PC"));
        Assert.Equal(0x7FFCu, (uint)cpu.GetRegister("USP"));
        Assert.Equal(0x1002u, bus.Read32(0x7FFC));
    }

    // ── Task 5: RTS/RTR/RTE ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Rts_pops_pc()
    {
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x75));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("USP", 0x8000);
        bus.Write32(0x8000, 0x00003000);   // return address
        cpu.Step();
        Assert.Equal(0x3000u, (uint)cpu.GetRegister("PC"));
        Assert.Equal(0x8004u, (uint)cpu.GetRegister("USP"));   // popped by 4
    }

    [Fact]
    public void Rtr_pops_ccr_then_pc()
    {
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x77));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("USP", 0x8000);
        cpu.SetRegister("SR", 0x0000);
        bus.Write16(0x8000, 0x001F);       // CCR word: low byte 0x1F → all CCR bits
        bus.Write32(0x8002, 0x00003000);   // return PC
        cpu.Step();
        Assert.Equal(0x3000u, (uint)cpu.GetRegister("PC"));
        Assert.Equal(0x1Fu, (uint)cpu.GetRegister("SR") & 0x1F);   // CCR restored (low byte)
        Assert.Equal(0x8006u, (uint)cpu.GetRegister("USP"));       // popped by 6
    }

    [Fact]
    public void Rte_pops_sr_then_pc_in_supervisor()
    {
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x73));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SSP", 0x9000);
        cpu.SetRegister("SR", 0x2700);     // supervisor (S=bit13), mask 7
        bus.Write16(0x9000, 0x2004);       // restored SR: supervisor, Z set
        bus.Write32(0x9002, 0x00003000);   // restored PC
        cpu.Step();
        Assert.Equal(0x3000u, (uint)cpu.GetRegister("PC"));
        Assert.Equal(0x2004u, (uint)cpu.GetRegister("SR"));
        Assert.Equal(0x9006u, (uint)cpu.GetRegister("SSP"));
    }

    [Fact]
    public void Rte_in_user_mode_raises_privilege_violation()
    {
        // RTE with S clear → vector 8 (privilege). The handler entry sits at 0x20 = 4·8.
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x73));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x0000);     // user mode
        cpu.SetRegister("SSP", 0x9000);
        bus.Write32(0x20, 0x0000A000);     // vector-8 handler
        cpu.Step();
        Assert.Equal(0xA000u, (uint)cpu.GetRegister("PC"));        // vectored to the privilege handler
        Assert.True(cpu.SupervisorMode);                            // supervisor entered
    }

    // ── Task 6: LINK/UNLK ────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Link_pushes_an_sets_frame_pointer_allocates()
    {
        // LINK A0,#-8 = 0x4E50, disp 0xFFF8 (-8).
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x50), (0x1002, 0xFF), (0x1003, 0xF8));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x12345678);
        cpu.SetRegister("USP", 0x8000);
        cpu.Step();
        Assert.Equal(0x12345678u, bus.Read32(0x7FFC));         // A0 pushed
        Assert.Equal(0x7FFCu, (uint)cpu.GetRegister("A0"));    // A0 = the new A7 (frame pointer)
        Assert.Equal(0x7FF4u, (uint)cpu.GetRegister("USP"));   // A7 -= 8 (the frame)
    }

    [Fact]
    public void Unlk_restores_a7_and_pops_an()
    {
        // UNLK A0 = 0x4E58.
        var (cpu, bus) = Build((0x1000, 0x4E), (0x1001, 0x58));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x7FFC);     // frame pointer
        cpu.SetRegister("USP", 0x7FF4);
        bus.Write32(0x7FFC, 0x11112222);   // the saved An at the frame pointer
        cpu.Step();
        Assert.Equal(0x11112222u, (uint)cpu.GetRegister("A0"));  // popped
        Assert.Equal(0x8000u, (uint)cpu.GetRegister("USP"));     // A7 = frame ptr + 4
    }

    [Fact]
    public void Link_unlk_round_trip_restores_a7_and_an()
    {
        // LINK A0,#-8 then UNLK A0 restores the original A7 + A0.
        var (cpu, _) = Build((0x1000, 0x4E), (0x1001, 0x50), (0x1002, 0xFF), (0x1003, 0xF8),
                             (0x1004, 0x4E), (0x1005, 0x58));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0xDEADBEEF);
        cpu.SetRegister("USP", 0x8000);
        cpu.Step();   // LINK
        cpu.Step();   // UNLK
        Assert.Equal(0xDEADBEEFu, (uint)cpu.GetRegister("A0"));  // A0 restored
        Assert.Equal(0x8000u, (uint)cpu.GetRegister("USP"));     // A7 restored
    }
}
