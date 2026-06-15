using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000AluCcrTests
{
    [Theory]
    [InlineData(0x10u, 0x20u, 0u, 0x30u)]                    // .b 0x10+0x20 = 0x30
    [InlineData(0x00FFu, 0x0001u, 1u, 0x0100u)]              // .w 0xFF+1 = 0x100
    [InlineData(0x7FFFFFFFu, 0x00000001u, 2u, 0x80000000u)] // .l signed-overflow value (CCR is checked below)
    public void Alu_Add_sums_within_the_size(uint a, uint b, uint size, uint expected)
        => Assert.Equal(expected & M68000Cpu.SizeMaskProbe(size), M68000Cpu.Alu.Add(a, b, false, size) & M68000Cpu.SizeMaskProbe(size));

    [Theory]
    [InlineData(0x30u, 0x10u, 0u, 0x20u)]                    // .b 0x30-0x10 = 0x20
    [InlineData(0x0000u, 0x0001u, 1u, 0xFFFFu)]              // .w 0-1 = 0xFFFF (borrow)
    public void Alu_Sub_subtracts_within_the_size(uint a, uint b, uint size, uint expected)
        => Assert.Equal(expected & M68000Cpu.SizeMaskProbe(size), M68000Cpu.Alu.Sub(a, b, false, size) & M68000Cpu.SizeMaskProbe(size));

    [Fact] public void Alu_And() => Assert.Equal(0x0F00u, M68000Cpu.Alu.And(0xFF00, 0x0FF0, false, 2u) & 0xFFFFu);
    [Fact] public void Alu_Or()  => Assert.Equal(0xFFF0u, M68000Cpu.Alu.Or (0xFF00, 0x0FF0, false, 2u) & 0xFFFFu);
    [Fact] public void Alu_Eor() => Assert.Equal(0xF0F0u, M68000Cpu.Alu.Eor(0xFF00, 0x0FF0, false, 2u) & 0xFFFFu);

    // ── CCR rules. CCR bit positions: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01 ──────────────────────────────────────
    [Fact]
    public void Ccr_Arith_add_carry_out_sets_C_and_X()
    {
        // .b 0xFF + 0x01 = 0x100 -> result byte 0x00: Z set, carry-out -> C+X set, N clear, V clear.
        byte c = M68000Cpu.AluCcr.ArithProbe(a: 0xFFu, b: 0x01u, result: 0x100u & 0xFFu, size: 0u, xIn: false, oldCcr: 0x00, isSub: false);
        Assert.Equal(0x04 | 0x01 | 0x10, c);   // Z + C + X
    }

    [Fact]
    public void Ccr_Arith_add_signed_overflow_sets_V()
    {
        // .b 0x7F + 0x01 = 0x80: N set (0x80), V set (pos+pos->neg), C clear, X clear, Z clear.
        byte c = M68000Cpu.AluCcr.ArithProbe(0x7Fu, 0x01u, 0x80u, 0u, false, 0x00, isSub: false);
        Assert.Equal(0x08 | 0x02, c);          // N + V
    }

    [Fact]
    public void Ccr_Arith_sub_borrow_sets_C_and_X()
    {
        // .b 0x00 - 0x01 = 0xFF: borrow -> C+X set, N set, Z clear, V clear.
        byte c = M68000Cpu.AluCcr.ArithProbe(0x00u, 0x01u, 0xFFu, 0u, false, 0x00, isSub: true);
        Assert.Equal(0x08 | 0x01 | 0x10, c);   // N + C + X
    }

    [Fact]
    public void Ccr_Logic_sets_NZ_clears_VC_keeps_X()
    {
        // .w result 0x8000: N set, Z clear, V=C=0, X preserved (oldCcr X set -> stays set).
        byte c = M68000Cpu.AluCcr.LogicProbe(0x8000u, 1u, oldCcr: 0x10);
        Assert.Equal(0x08 | 0x10, c);          // N + (preserved X)
    }

    [Fact]
    public void Ccr_Cmp_is_arith_without_X()
    {
        // CMP .b 0x00 - 0x01 = 0xFF: N+C set, but X is NOT touched (oldCcr X clear -> stays clear).
        byte c = M68000Cpu.AluCcr.CmpProbe(0x00u, 0x01u, 0xFFu, 0u, oldCcr: 0x00);
        Assert.Equal(0x08 | 0x01, c);          // N + C, NO X
    }

    [Fact]
    public void Ccr_ArithX_Z_is_sticky_cleared_on_nonzero_preserved_on_zero()
    {
        // Result non-zero -> Z cleared. Result zero with oldCcr Z set -> Z STAYS set (sticky).
        byte nonZero = M68000Cpu.AluCcr.ArithXProbe(0x10u, 0x01u, 0x11u, 0u, xIn: false, oldCcr: 0x04, isSub: false);
        Assert.Equal(0x00, nonZero & 0x04);    // Z cleared (non-zero result)
        byte zeroKeepsZ = M68000Cpu.AluCcr.ArithXProbe(0x01u, 0x01u, 0x00u, 0u, xIn: false, oldCcr: 0x04, isSub: true);
        Assert.Equal(0x04, zeroKeepsZ & 0x04); // Z preserved (zero result + oldCcr Z set)
        byte zeroOldClear = M68000Cpu.AluCcr.ArithXProbe(0x01u, 0x01u, 0x00u, 0u, xIn: false, oldCcr: 0x00, isSub: true);
        Assert.Equal(0x00, zeroOldClear & 0x04); // Z stays clear (never SET by ArithX)
    }
}
