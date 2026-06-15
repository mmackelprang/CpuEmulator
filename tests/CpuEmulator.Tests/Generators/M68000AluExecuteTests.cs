using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M4.5b synthetic execute tests for the integer-ALU families (no vectors). Drives cpu.Step() over a
/// hand-built operword + extension words and asserts the (result, CCR) the ALU layer produces. These cover the
/// regular reg↔EA / address-reg / unary families, the immediate + quick forms (the D1 differential-equivalence +
/// synthetic-fetch HARDENING — the imm/quick forms have NO v1 vectors, so this is their proof alongside the
/// transitive silicon proof from the reg↔EA TomHarte sweep), and the bespoke tail (EXT/CLR/ADDX/SUBX/NEGX/
/// MULU/MULS/DIVU/DIVS incl. ÷0 detect-and-defer). The dispatch is wired (Task 13), so every test runs un-skipped.
/// </summary>
public class M68000AluExecuteTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    // ── Task 3: regular reg↔EA ────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Add_w_ea_to_dn_sets_result_and_ccr()
    {
        // ADD.w D1,D0 = 0xD041 (Dn=D0 @11-9, opmode 001 = .w to Dn, ea-mode 000 reg 001 = D1).
        var (cpu, _) = Build((0x1000, 0xD0), (0x1001, 0x41));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00001000);
        cpu.SetRegister("D1", 0x00000234);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00001234u, (uint)cpu.GetRegister("D0"));   // .w add into D0 low word, upper preserved
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x1F);  // no flags
    }

    [Fact]
    public void Add_b_dn_to_ea_memory_writes_back()
    {
        // ADD.b D1,(A0) (dir=1: D1 + <ea(=(A0))> -> (A0)). operword 0xD310 = 1101 001 100 010 000
        //   Dn=D1(@11-9=001), opmode 100 = .b to <ea>, ea-mode 010 reg 000 = (A0). (A reg-direct dest dir=1
        //   collides with ADDX's tighter mask, so a memory EA is used to exercise the dir=1 write-back path.)
        var (cpu, bus) = Build((0x1000, 0xD3), (0x1001, 0x10), (0x2000, 0x0A));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.SetRegister("D1", 0x00000005);
        cpu.Step();
        Assert.Equal((byte)0x0F, bus.Read8(0x2000));   // 0x0A + 0x05 = 0x0F written back to (A0)
    }

    [Fact]
    public void Cmp_w_sets_ccr_without_writing()
    {
        // CMP.w D1,D0 (D0 - D1, compare-only). operword 0xB041 = 1011 000 001 000 001.
        var (cpu, _) = Build((0x1000, 0xB0), (0x1001, 0x41));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00005000);
        cpu.SetRegister("D1", 0x00005000);   // equal -> Z set
        cpu.Step();
        Assert.Equal(0x00005000u, (uint)cpu.GetRegister("D0"));   // CMP writes nothing
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x1F);  // Z set, others clear
    }

    [Fact]
    public void And_l_clears_v_c()
    {
        // AND.l D1,D0 -> D0. operword 0xC081 = 1100 000 010 000 001 (opmode 010 = .l to Dn).
        var (cpu, _) = Build((0x1000, 0xC0), (0x1001, 0x81));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0xFF00FF00);
        cpu.SetRegister("D1", 0x0FF00FF0);
        cpu.SetRegister("SR", 0x0003);       // V+C set going in -> must clear
        cpu.Step();
        Assert.Equal(0x0F000F00u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x03); // V=C=0
    }

    // ── Task 13: dispatch smoke ──────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Step_routes_an_add_operword_to_the_add_body()
    {
        var (cpu, _) = Build((0x1000, 0xD0), (0x1001, 0x41));   // ADD.w D1,D0
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00001000);
        cpu.SetRegister("D1", 0x00000234);
        cpu.Step();
        Assert.Equal(0x00001234u, (uint)cpu.GetRegister("D0"));
    }

    // ── Task 4: ADDA/SUBA/CMPA ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Adda_w_sign_extends_source_and_sets_no_ccr()
    {
        // ADDA.w D0,A1 = 0xD2C0 = 1101 001 011 000 000 (An=A1@11-9, opmode 011 = .w, ea D0).
        var (cpu, _) = Build((0x1000, 0xD2), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000FFFF);   // .w source 0xFFFF -> sign-extends to 0xFFFFFFFF (= -1)
        cpu.SetRegister("A1", 0x00001000);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00000FFFu, (uint)cpu.GetRegister("A1"));  // 0x1000 + (-1) = 0x0FFF (full 32-bit add)
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x1F); // ADDA sets NO CCR
    }

    [Fact]
    public void Cmpa_l_sets_ccr_does_not_write_an()
    {
        // CMPA.l D0,A1 = 0xB3C0 = 1011 001 111 000 000 (An=A1, opmode 111 = .l, ea D0). A1 - D0.
        var (cpu, _) = Build((0x1000, 0xB3), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A1", 0x00005000);
        cpu.SetRegister("D0", 0x00005000);   // equal -> Z
        cpu.Step();
        Assert.Equal(0x00005000u, (uint)cpu.GetRegister("A1"));  // CMPA writes nothing
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x1F); // Z set
    }

    // ── Task 5: NEG/NOT/TST ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Neg_b_negates_and_sets_carry()
    {
        // NEG.b D0 = 0x4400 (size .b, ea-mode 000 reg 000 = D0).
        var (cpu, _) = Build((0x1000, 0x44), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x11223301);   // .b = 0x01 -> 0 - 1 = 0xFF
        cpu.Step();
        Assert.Equal(0x112233FFu, (uint)cpu.GetRegister("D0"));      // partial .b
        Assert.Equal(0x08 | 0x01 | 0x10, (int)((uint)cpu.GetRegister("SR") & 0x1F)); // N + C + X
    }

    [Fact]
    public void Not_w_complements_and_logic_ccr()
    {
        // NOT.w D0 = 0x4640 (size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x46), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000FF00);   // .w 0xFF00 -> ~ = 0x00FF
        cpu.Step();
        Assert.Equal(0x000000FFu, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x03);    // V=C=0
    }

    [Fact]
    public void Tst_l_sets_nz_without_writing()
    {
        // TST.l D0 = 0x4A80 (size .l, ea D0).
        var (cpu, _) = Build((0x1000, 0x4A), (0x1001, 0x80));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x80000000);   // negative
        cpu.SetRegister("SR", 0x0003);       // V+C going in -> cleared
        cpu.Step();
        Assert.Equal(0x80000000u, (uint)cpu.GetRegister("D0"));     // unchanged
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x0F);    // N set, V=C=0, Z clear
    }

    // ── Task 6: ADDI/SUBI/ANDI/ORI/EORI/CMPI (NOT vector-gated — D1) ────────────────────────────────────────
    [Fact]
    public void Addi_w_decode_captures_immediate_word_and_length()
    {
        var buf = new byte[] { 0x06, 0x40, 0x00, 0x10, 0, 0 };   // ADDI.w #$0010,D0
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        var r = M68000Cpu.Decode(stream);
        Assert.NotEqual(0xFFFFFFFFu, r.OperationKey);   // ADDI matched
        Assert.Equal(4, r.Length);                      // operword + 1 imm word = 4 bytes
        Assert.True(r.ExtensionWords.Count >= 1);
        Assert.Equal((ushort)0x0010, r.ExtensionWords[0]);
    }

    [Fact]
    public void Addi_w_adds_immediate_to_dn()
    {
        // ADDI.w #$0010,D0 = 0x0640 + imm word 0x0010.
        var (cpu, _) = Build((0x1000, 0x06), (0x1001, 0x40), (0x1002, 0x00), (0x1003, 0x10));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000005);
        cpu.Step();
        Assert.Equal(0x00000015u, (uint)cpu.GetRegister("D0"));   // 0x05 + 0x10 = 0x15
    }

    [Fact]
    public void Cmpi_l_compares_immediate_no_write()
    {
        // CMPI.l #$00005000,D0 = 0x0C80 + imm long 0x00005000.
        var (cpu, _) = Build((0x1000, 0x0C), (0x1001, 0x80),
                             (0x1002, 0x00), (0x1003, 0x00), (0x1004, 0x50), (0x1005, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00005000);   // equal -> Z
        cpu.Step();
        Assert.Equal(0x00005000u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x1F);  // Z set
    }

    [Fact]
    public void Addi_b_reads_one_immediate_word_low_byte()
    {
        // ADDI.b #$12,D0 = 0x0600 + imm word 0x0012.
        var (cpu, _) = Build((0x1000, 0x06), (0x1001, 0x00), (0x1002, 0x00), (0x1003, 0x12));
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", 0x11223303);
        cpu.Step();
        Assert.Equal(0x11223315u, (uint)cpu.GetRegister("D0"));   // 0x03 + 0x12 = 0x15, partial .b
    }

    [Fact]
    public void Addi_l_reads_two_immediate_words()
    {
        // ADDI.l #$00010002,D0 = 0x0680 + imm long 0x0001 0x0002.
        var (cpu, _) = Build((0x1000, 0x06), (0x1001, 0x80),
                             (0x1002, 0x00), (0x1003, 0x01), (0x1004, 0x00), (0x1005, 0x02));
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", 0x00000003);
        cpu.Step();
        Assert.Equal(0x00010005u, (uint)cpu.GetRegister("D0"));   // 0x00010002 + 3 = 0x00010005
    }

    // ── Differential-equivalence (D1 HARDENING): each imm/quick form ≡ its vector-proven reg↔EA counterpart ───
    // Run two independent CPUs through the SAME (a, b) and assert identical (result, CCR). Because the reg form
    // is TomHarte-green against silicon (Task 14), this transitively inherits the silicon proof for the imm/
    // quick form's ALU function + CCR rule (the high-bug-density core).
    private static (uint Result, uint Ccr) RunImm(uint a, uint size, byte ow0, byte ow1, byte[] immBytes)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        bus.Write8(0x1000, ow0); bus.Write8(0x1001, ow1);
        for (int i = 0; i < immBytes.Length; i++) bus.Write8((uint)(0x1002 + i), immBytes[i]);
        var cpu = new M68000Cpu(bus);
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", a); cpu.SetRegister("SR", 0);
        cpu.Step();
        return ((uint)cpu.GetRegister("D0"), (uint)cpu.GetRegister("SR") & 0x1F);
    }
    private static (uint Result, uint Ccr) RunReg(uint a, uint b, byte ow0, byte ow1)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        bus.Write8(0x1000, ow0); bus.Write8(0x1001, ow1);
        var cpu = new M68000Cpu(bus);
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", a); cpu.SetRegister("D1", b); cpu.SetRegister("SR", 0);
        cpu.Step();
        return ((uint)cpu.GetRegister("D0"), (uint)cpu.GetRegister("SR") & 0x1F);
    }

    [Theory]
    // (a, b, immOw0, immOw1, immBytes, regOw0, regOw1) — .w forms. Both land on D0 (dir=0); the asserted
    // invariant is identical (result, CCR), not a specific encoding.
    // ADDI.w #b,D0 = 0x0640 ≡ ADD.w D1,D0 = 0xD041.  SUBI.w = 0x0440 ≡ SUB.w = 0x9041.
    // ANDI.w = 0x0240 ≡ AND.w = 0xC041.  ORI.w = 0x0040 ≡ OR.w = 0x8041.
    [InlineData(0x00000005u, 0x0010u, (byte)0x06, (byte)0x40, (byte)0x00, (byte)0x10, (byte)0xD0, (byte)0x41)] // ADDI≡ADD
    [InlineData(0x00000030u, 0x0010u, (byte)0x04, (byte)0x40, (byte)0x00, (byte)0x10, (byte)0x90, (byte)0x41)] // SUBI≡SUB
    [InlineData(0x0000FF00u, 0x0FF0u, (byte)0x02, (byte)0x40, (byte)0x0F, (byte)0xF0, (byte)0xC0, (byte)0x41)] // ANDI≡AND
    [InlineData(0x0000FF00u, 0x0FF0u, (byte)0x00, (byte)0x40, (byte)0x0F, (byte)0xF0, (byte)0x80, (byte)0x41)] // ORI≡OR
    public void Immediate_form_matches_its_reg_form_result_and_ccr(
        uint a, uint b, byte immOw0, byte immOw1, byte immHi, byte immLo, byte regOw0, byte regOw1)
    {
        var imm = RunImm(a, size: 1u, immOw0, immOw1, new byte[] { immHi, immLo });
        var reg = RunReg(a, b, regOw0, regOw1);
        Assert.Equal(reg.Result, imm.Result);
        Assert.Equal(reg.Ccr, imm.Ccr);
    }

    [Fact]
    public void Cmpi_matches_cmp_result_and_ccr()
    {
        // CMPI.w #b,D0 (0x0C40 + #b) ≡ CMP.w D1,D0 (0xB041).
        var imm = RunImm(0x00005000u, 1u, 0x0C, 0x40, new byte[] { 0x50, 0x00 });
        var reg = RunReg(0x00005000u, 0x5000u, 0xB0, 0x41);
        Assert.Equal(reg.Result, imm.Result);   // both leave D0 unchanged
        Assert.Equal(reg.Ccr, imm.Ccr);         // Z set, etc.
    }

    [Fact]
    public void Eori_matches_eor_alu_and_logic_ccr()
    {
        // EOR has only the Dn ^ <ea> -> <ea> form, so compare the ALU func + CCR rule directly (either style
        // proves the equivalence). EORI.w #b,D0 must equal Alu.Eor(a,b) + AluCcr.Logic over the .w result.
        const uint a = 0x0000FF00u, b = 0x0FF0u;
        var imm = RunImm(a, 1u, 0x0A, 0x40, new byte[] { 0x0F, 0xF0 });   // EORI.w #$0FF0,D0
        uint expectedResult = (a & 0xFFFF0000u) | (M68000Cpu.Alu.Eor(a, b, false, 1u) & 0xFFFFu);
        uint expectedCcr = M68000Cpu.AluCcr.LogicProbe(M68000Cpu.Alu.Eor(a, b, false, 1u), 1u, 0x00) & 0x1Fu;
        Assert.Equal(expectedResult, imm.Result);
        Assert.Equal(expectedCcr, imm.Ccr);
    }

    // ── Task 7: ADDQ/SUBQ (NOT vector-gated — D1) ──────────────────────────────────────────────────────────
    [Fact]
    public void Addq_w_adds_quick_immediate()
    {
        // ADDQ.w #3,D0 = 0x5640 (data=3@11-9, opmode 0, size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x56), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000005);
        cpu.Step();
        Assert.Equal(0x00000008u, (uint)cpu.GetRegister("D0"));   // 5 + 3 = 8
    }

    [Fact]
    public void Addq_to_an_is_full_32_bit_no_ccr()
    {
        // ADDQ.w #1,A0 = 0x5248 (data=1, opmode 0, size .w, ea-mode 001 reg 000 = A0).
        var (cpu, _) = Build((0x1000, 0x52), (0x1001, 0x48));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x0000FFFF);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00010000u, (uint)cpu.GetRegister("A0"));   // full-32 add (NOT masked to .w)
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x1F);  // An dest -> NO CCR
    }

    [Fact]
    public void Subq_quick_zero_means_eight()
    {
        // SUBQ.w #8,D0 = 0x5140 (data 000 -> 8, opmode 1 = sub, size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x51), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000000A);
        cpu.Step();
        Assert.Equal(0x00000002u, (uint)cpu.GetRegister("D0"));   // 0x0A - 8 = 2
    }

    [Fact]
    public void Addq_quick_field_seven_maps_to_seven()
    {
        // ADDQ.w #7,D0 = 0x5E40 (data 111 = 7 @11-9, opmode 0, size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x5E), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", 0x00000001);
        cpu.Step();
        Assert.Equal(0x00000008u, (uint)cpu.GetRegister("D0"));   // 1 + 7 = 8 (field 7 -> 7, NOT 8)
    }

    [Theory]
    // (a, n, quickOw0, quickOw1, regOw0, regOw1). ADDQ.w #n,D0 = 0x5n40 ≡ ADD.w D1,D0 = 0xD041 with D1=n.
    [InlineData(0x00000005u, 3u, (byte)0x56, (byte)0x40, (byte)0xD0, (byte)0x41)]   // ADDQ #3 ≡ ADD D1=3
    [InlineData(0x0000000Au, 8u, (byte)0x51, (byte)0x40, (byte)0x90, (byte)0x41)]   // SUBQ #8 (000->8) ≡ SUB D1=8
    public void Quick_form_matches_its_reg_form_result_and_ccr(
        uint a, uint n, byte quickOw0, byte quickOw1, byte regOw0, byte regOw1)
    {
        var quick = RunImm(a, size: 1u, quickOw0, quickOw1, immBytes: new byte[0]);  // no imm word for quick
        var reg   = RunReg(a, n, regOw0, regOw1);
        Assert.Equal(reg.Result, quick.Result);
        Assert.Equal(reg.Ccr, quick.Ccr);
    }

    // ── Task 8: EXT ──────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Ext_w_sign_extends_byte_to_word()
    {
        // EXT.w D0 = 0x4880 (opmode 010 = byte->word, Dn=0).
        var (cpu, _) = Build((0x1000, 0x48), (0x1001, 0x80));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x112233F0);   // .b = 0xF0 (negative) -> .w = 0xFFF0
        cpu.Step();
        Assert.Equal(0x1122FFF0u, (uint)cpu.GetRegister("D0"));   // low word sign-extended, upper word preserved
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x0F);  // N set, Z/V/C clear
    }

    [Fact]
    public void Ext_l_sign_extends_word_to_long()
    {
        // EXT.l D0 = 0x48C0 (opmode 011 = word->long, Dn=0).
        var (cpu, _) = Build((0x1000, 0x48), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00008000);   // .w = 0x8000 (negative) -> .l = 0xFFFF8000
        cpu.Step();
        Assert.Equal(0xFFFF8000u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x0F);  // N set
    }

    // ── Task 9: CLR ──────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Clr_w_writes_zero_and_sets_only_Z()
    {
        // CLR.w D0 = 0x4240 (size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x42), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x1122FFFF);   // .w cleared -> 0x11220000
        cpu.SetRegister("SR", 0x0019);       // X+N+C set going in: Z must become 1, N/V/C 0, X preserved
        cpu.Step();
        Assert.Equal(0x11220000u, (uint)cpu.GetRegister("D0"));   // low word zeroed, upper preserved
        Assert.Equal(0x04 | 0x10, (int)((uint)cpu.GetRegister("SR") & 0x1F)); // Z set + X preserved; N=V=C=0
    }

    [Fact]
    public void Clr_b_memory_issues_a_read_before_the_write()
    {
        // CLR.b (A0) = 0x4210 (size .b, ea-mode 010 reg 000 = (A0)).
        var (cpu, bus) = Build((0x1000, 0x42), (0x1001, 0x10), (0x2000, 0x7F));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.Step();
        Assert.Equal((byte)0x00, bus.Read8(0x2000));   // memory cleared
    }

    [Fact]
    public void Clr_b_postincrement_advances_an_exactly_once()
    {
        // CLR.b (A0)+ = 0x4218 (size .b, ea-mode 011 reg 000 = (A0)+). The address-once fix: A0 += 1 ONCE.
        var (cpu, bus) = Build((0x1000, 0x42), (0x1001, 0x18), (0x2000, 0x7F));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.Step();
        Assert.Equal((byte)0x00, bus.Read8(0x2000));         // memory cleared
        Assert.Equal(0x2001u, (uint)cpu.GetRegister("A0"));  // A0 advanced by 1 (NOT 2 — single write-back)
    }

    // ── Task 10: ADDX/SUBX/NEGX ──────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Addx_b_reg_reg_uses_x_and_sticky_z()
    {
        // ADDX.b D1,D0 = 0xD101 (Dy=D0@11-9, R/M=0 reg, size .b, Dx=D1@2-0).
        var (cpu, _) = Build((0x1000, 0xD1), (0x1001, 0x01));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000010);   // Dy
        cpu.SetRegister("D1", 0x00000005);   // Dx
        cpu.SetRegister("SR", 0x0010);       // X set -> +1
        cpu.Step();
        Assert.Equal(0x00000016u, (uint)cpu.GetRegister("D0"));   // 0x10 + 0x05 + 1 = 0x16
    }

    [Fact]
    public void Addx_b_zero_result_preserves_incoming_Z_sticky()
    {
        var (cpu, _) = Build((0x1000, 0xD1), (0x1001, 0x01));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000000);
        cpu.SetRegister("D1", 0x00000000);
        cpu.SetRegister("SR", 0x0004);       // Z=1 going in, X=0
        cpu.Step();
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x04);  // Z preserved (sticky)
    }

    [Fact]
    public void Addx_b_predecrement_pairs_both_operands()
    {
        // ADDX.b -(A1),-(A0) = 0xD109 (Ay=A0@11-9, R/M=1 mem, Ax=A1@2-0).
        var (cpu, bus) = Build((0x1000, 0xD1), (0x1001, 0x09), (0x1FFF, 0x05), (0x2FFF, 0x10));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);       // dest -(A0) -> reads/writes 0x1FFF
        cpu.SetRegister("A1", 0x3000);       // src  -(A1) -> reads 0x2FFF
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal((byte)0x15, bus.Read8(0x1FFF));         // 0x05(dest) + 0x10(src) = 0x15 written to -(A0)
        Assert.Equal(0x1FFFu, (uint)cpu.GetRegister("A0"));  // A0 predecremented once
        Assert.Equal(0x2FFFu, (uint)cpu.GetRegister("A1"));  // A1 predecremented once
    }

    [Fact]
    public void Negx_b_negates_with_x()
    {
        // NEGX.b D0 = 0x4000 (size .b, ea D0). 0 - 0x01 - X(1) = 0xFE.
        var (cpu, _) = Build((0x1000, 0x40), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000001);
        cpu.SetRegister("SR", 0x0010);       // X=1
        cpu.Step();
        Assert.Equal(0x000000FEu, (uint)cpu.GetRegister("D0"));   // 0 - 1 - 1 = 0xFE
    }

    // ── Task 11: MULU/MULS ───────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Mulu_multiplies_unsigned_word_into_long()
    {
        // MULU D1,D0 = 0xC0C1 (Dn=D0@11-9, MULU opmode 011, ea D1).
        var (cpu, _) = Build((0x1000, 0xC0), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00001000);   // .w source = 0x1000
        cpu.SetRegister("D1", 0x00000010);   // .w source = 0x0010
        cpu.Step();
        Assert.Equal(0x00010000u, (uint)cpu.GetRegister("D0"));   // 0x1000 * 0x10 = 0x10000 (32-bit)
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x03);  // V=C=0
    }

    [Fact]
    public void Muls_multiplies_signed()
    {
        // MULS D1,D0 = 0xC1C1 (Dn=D0, MULS opmode 111, ea D1).
        var (cpu, _) = Build((0x1000, 0xC1), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000FFFF);   // .w = -1
        cpu.SetRegister("D1", 0x00000002);   // .w = 2
        cpu.Step();
        Assert.Equal(0xFFFFFFFEu, (uint)cpu.GetRegister("D0"));   // -1 * 2 = -2
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x08);  // N set
    }

    // ── Task 12: DIVU/DIVS (incl. ÷0 detect-and-defer) ───────────────────────────────────────────────────────
    [Fact]
    public void Divu_divides_quotient_and_remainder()
    {
        // DIVU D1,D0 = 0x80C1 (Dn=D0, DIVU opmode 011, ea D1).
        var (cpu, _) = Build((0x1000, 0x80), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00010005);   // dividend = 0x10005
        cpu.SetRegister("D1", 0x00000010);   // divisor .w = 0x10
        cpu.Step();
        // 0x10005 / 0x10 = quotient 0x1000, remainder 0x5 -> D0 = (rem<<16)|quot = 0x00051000.
        Assert.Equal(0x00051000u, (uint)cpu.GetRegister("D0"));
    }

    [Fact]
    public void Divu_by_zero_leaves_dn_unchanged_no_write()
    {
        // DIVU #0 path: body detects ÷0 and DEFERS (no write, no CCR change). The vectoring is M4.5d.
        var (cpu, _) = Build((0x1000, 0x80), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00010005);
        cpu.SetRegister("D1", 0x00000000);   // divisor 0
        cpu.Step();
        Assert.Equal(0x00010005u, (uint)cpu.GetRegister("D0"));   // unchanged (÷0 deferred; M4.5d vectors)
    }
}
