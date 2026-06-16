using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000ShiftCcrTests
{
    // CCR bits: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01
    [Fact]
    public void Shift_sets_C_and_X_from_last_bit_out_NZ_from_result()
        => Assert.Equal(0x04 | 0x01 | 0x10,
            M68000Cpu.ShiftCcr.ShiftProbe(result: 0x00u, size: 0u, lastBitOut: true, msbChanged: false, oldCcr: 0x00));

    [Fact]
    public void Shift_count_zero_clears_C_keeps_X_unchanged()
        => Assert.Equal(0x08 | 0x10,
            M68000Cpu.ShiftCcr.ShiftProbe(result: 0x80u, size: 0u, lastBitOut: false, msbChanged: false,
                                          oldCcr: 0x10, countZero: true));   // N + X preserved; C cleared

    [Fact]
    public void Asl_V_set_when_msb_changed_during_shift()
        => Assert.Equal(0x02, M68000Cpu.ShiftCcr.ShiftProbe(0x00u, 0u, true, true, 0x00) & 0x02);

    [Fact]
    public void Rotate_sets_C_from_last_bit_does_NOT_touch_X()
        => Assert.Equal(0x01 | 0x10,
            M68000Cpu.ShiftCcr.RotateProbe(result: 0x01u, size: 0u, lastBitOut: true, oldCcr: 0x10));

    [Fact]
    public void RotateX_through_X_sets_C_equals_X_from_last_bit()
        => Assert.Equal(0x04 | 0x01 | 0x10,
            M68000Cpu.ShiftCcr.RotateXProbe(result: 0x00u, size: 0u, lastBitOut: true, oldCcr: 0x00));
}
