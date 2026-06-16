using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M4.5c synthetic execute tests for bit ops (Tasks 9-10), BCD (Task 11), and Scc (Task 13). No vectors; the
/// TomHarte sweep (Task 22) is the oracle. These pin the wiring + the CCR rules in isolation.
/// </summary>
public class M68000BitBcdSccExecuteTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    // ── Task 10 Step 0: confirm the static bit-number leading word is captured by the decode walk ───────────
    [Fact]
    public void Btst_static_decode_captures_bit_word()
    {
        // BTST #5,D0 (static) = 0x0800, then bit-number word 0x0005. Dn target -> .l.
        var buf = new byte[] { 0x08, 0x00, 0x00, 0x05, 0, 0 };
        var stream = new BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        DecodeResult r = M68000Cpu.Decode(stream);
        Assert.NotEqual(0xFFFFFFFFu, r.OperationKey);     // BTST_STATIC matched
        Assert.True(r.ExtensionWords.Count >= 1, "the leading bit-number word must be captured");
        Assert.Equal((ushort)0x0005, r.ExtensionWords[0]);
        Assert.Equal(4, r.Length);                        // operword + bit-number word = 2 words = 4 bytes
    }

    // ── Task 9: BitCcr.BitTest ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void BitTest_sets_Z_when_tested_bit_is_zero()
        => Assert.Equal(0x04, M68000Cpu.BitCcr.BitTestProbe(0b0000_0001u, bit: 4, oldCcr: 0x00) & 0x04);

    [Fact]
    public void BitTest_clears_Z_when_tested_bit_is_one_and_preserves_NVCX()
    {
        // bit 0 is 1 -> Z clear; N,V,C,X preserved from oldCcr 0x1B (X N V C set, Z was set).
        byte ccr = M68000Cpu.BitCcr.BitTestProbe(0b0000_0001u, bit: 0, oldCcr: 0x1B);
        Assert.Equal(0x00, ccr & 0x04);          // Z cleared (bit was 1)
        Assert.Equal(0x1B & ~0x04, ccr);         // N V C X preserved
    }

    // ── Task 9: dynamic execute (Dn .l mod 32, memory .b mod 8) ────────────────────────────────────────────
    [Fact]
    public void Btst_dynamic_on_dn_tests_bit_mod_32_no_write()
    {
        // BTST D1,D0 = 0x0300. 0000 ddd=001(D1) 100(btst dynamic) eaMode=000 eaReg=000(D0).
        var (cpu, _) = Build((0x1000, 0x03), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000000);   // bit 7 of D0 is 0 -> Z set
        cpu.SetRegister("D1", 7);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00000000u, (uint)cpu.GetRegister("D0"));   // BTST does not write
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x04);  // Z set (tested bit 0)
    }

    [Fact]
    public void Bset_dynamic_on_memory_sets_bit_mod_8_and_writes()
    {
        // BSET D1,(A0) = 0x03D0. 0000 ddd=001(D1) 111(bset dynamic) eaMode=010 eaReg=000((A0)).
        var (cpu, bus) = Build((0x1000, 0x03), (0x1001, 0xD0), (0x2000, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.SetRegister("D1", 11);           // mod 8 = bit 3
        cpu.Step();
        Assert.Equal((byte)0x08, bus.Read8(0x2000));   // bit 3 set
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x04);   // Z = old bit 3 was 0
    }

    [Fact]
    public void Bclr_static_on_dn_clears_bit()
    {
        // BCLR #4,D0 (static) = 0x0880, then bit-number word 0x0004. 0000 1000 10 eaMode=000 eaReg=000(D0).
        var (cpu, _) = Build((0x1000, 0x08), (0x1001, 0x80), (0x1002, 0x00), (0x1003, 0x04));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000010);   // bit 4 set
        cpu.Step();
        Assert.Equal(0x00000000u, (uint)cpu.GetRegister("D0"));   // bit 4 cleared
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x04);  // Z clear (bit 4 was 1)
    }

    // ── Task 11: BcdCcr + the decimal-adjust truth ─────────────────────────────────────────────────────────
    [Fact]
    public void Abcd_decimal_adjust_no_carry()
    {
        // ABCD D1,D0 = 0xC101. 1100 ddd=000(D0) 1 0000 0 reg=001(D1). Dn-Dn form (bit 3 = 0).
        var (cpu, _) = Build((0x1000, 0xC1), (0x1001, 0x01));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000009);   // 09 + 01 = 10 (BCD)
        cpu.SetRegister("D1", 0x00000001);
        cpu.SetRegister("SR", 0x0000);       // X = 0
        cpu.Step();
        Assert.Equal(0x00000010u, (uint)cpu.GetRegister("D0"));   // BCD 10
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x11);  // no C, no X
    }

    [Fact]
    public void Abcd_decimal_adjust_with_carry_out()
    {
        var (cpu, _) = Build((0x1000, 0xC1), (0x1001, 0x01));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000099);   // 99 + 01 = 00 carry 1
        cpu.SetRegister("D1", 0x00000001);
        cpu.SetRegister("SR", 0x0004);       // Z set going in (sticky)
        cpu.Step();
        Assert.Equal(0x00000000u, (uint)cpu.GetRegister("D0"));   // BCD 00
        uint sr = (uint)cpu.GetRegister("SR") & 0x1F;
        Assert.Equal(0x10u, sr & 0x10);   // X = carry
        Assert.Equal(0x01u, sr & 0x01);   // C = carry
        Assert.Equal(0x04u, sr & 0x04);   // sticky Z preserved (result 0 AND oldZ set)
    }

    [Fact]
    public void Sbcd_decimal_subtract()
    {
        // SBCD D1,D0 = 0x8101. 1000 ddd=000(D0) 1 0000 0 reg=001(D1).
        var (cpu, _) = Build((0x1000, 0x81), (0x1001, 0x01));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000042);   // 42 - 01 = 41 (BCD)
        cpu.SetRegister("D1", 0x00000001);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00000041u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x11);  // no borrow
    }

    [Fact]
    public void Nbcd_negates_decimal()
    {
        // NBCD D0 = 0x4800. 0100 1000 00 eaMode=000 eaReg=000(D0).
        var (cpu, _) = Build((0x1000, 0x48), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000001);   // 0 - 1 - 0 = 99 (BCD), borrow
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00000099u, (uint)cpu.GetRegister("D0"));
        uint sr = (uint)cpu.GetRegister("SR") & 0x1F;
        Assert.Equal(0x10u, sr & 0x10);   // X = borrow
        Assert.Equal(0x01u, sr & 0x01);   // C = borrow
    }

    // ── Task 13: EvaluateCondition truth table + Scc execute ───────────────────────────────────────────────
    [Theory]
    [InlineData(0x0u, 0x00, true)]    // T
    [InlineData(0x1u, 0x00, false)]   // F
    [InlineData(0x2u, 0x00, true)]    // HI: !C && !Z
    [InlineData(0x3u, 0x01, true)]    // LS: C || Z (C set)
    [InlineData(0x4u, 0x00, true)]    // CC: !C
    [InlineData(0x5u, 0x01, true)]    // CS: C
    [InlineData(0x6u, 0x00, true)]    // NE: !Z
    [InlineData(0x7u, 0x04, true)]    // EQ: Z
    [InlineData(0x8u, 0x00, true)]    // VC: !V
    [InlineData(0x9u, 0x02, true)]    // VS: V
    [InlineData(0xAu, 0x00, true)]    // PL: !N
    [InlineData(0xBu, 0x08, true)]    // MI: N
    [InlineData(0xCu, 0x00, true)]    // GE: N==V (both 0)
    [InlineData(0xDu, 0x08, true)]    // LT: N!=V (N set, V clear)
    [InlineData(0xEu, 0x00, true)]    // GT: !Z && N==V
    [InlineData(0xFu, 0x04, true)]    // LE: Z || N!=V (Z set)
    public void EvaluateCondition_truth_table(uint cc, byte ccr, bool expected)
        => Assert.Equal(expected, M68000Cpu.EvaluateConditionProbe(cc, ccr));

    [Fact]
    public void Scc_sets_byte_FF_when_true_no_ccr_change()
    {
        // ST D0 (Scc with cc=T) = 0x50C0. 0101 cond=0000(T) 11 eaMode=000 eaReg=000(D0).
        var (cpu, _) = Build((0x1000, 0x50), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x12345678);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x123456FFu, (uint)cpu.GetRegister("D0"));   // low byte = 0xFF, upper preserved
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x1F);  // no CCR change
    }

    [Fact]
    public void Scc_sets_byte_00_when_false()
    {
        // SF D0 (Scc with cc=F) = 0x51C0. cond=0001(F).
        var (cpu, _) = Build((0x1000, 0x51), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x123456FF);
        cpu.Step();
        Assert.Equal(0x12345600u, (uint)cpu.GetRegister("D0"));   // low byte = 0x00
    }
}
