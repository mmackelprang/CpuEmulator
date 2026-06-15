using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Core;

/// <summary>M4.2 (ADR 0003 Decision 2 / Decision D3) — the 68000 odd-address DETECTION predicate. A
/// word/long access at an ODD address is misaligned; a byte access never is. M4.2 ships DETECTION only —
/// the address-error EXCEPTION (vector 3, the supervisor stack frame) is the M4.5 exception model. The
/// M4.5 interpreter calls IsMisaligned BEFORE a wide access and vectors instead.</summary>
public class BusAlignmentTests
{
    [Theory]
    [InlineData(0x1000u, AccessWidth.Byte, false)]   // byte: never misaligned
    [InlineData(0x1001u, AccessWidth.Byte, false)]   // byte at an odd address: still fine
    [InlineData(0x1000u, AccessWidth.Word, false)]   // word at an even address: aligned
    [InlineData(0x1001u, AccessWidth.Word, true)]    // word at an odd address: MISALIGNED
    [InlineData(0x1000u, AccessWidth.Long, false)]   // long at an even address: aligned
    [InlineData(0x1001u, AccessWidth.Long, true)]    // long at an odd address: MISALIGNED
    [InlineData(0x1002u, AccessWidth.Long, false)]   // long at a word-even address: aligned (only bit 0 matters)
    [InlineData(0x0000u, AccessWidth.Word, false)]
    [InlineData(0xFFFFu, AccessWidth.Word, true)]
    public void IsMisaligned_is_true_only_for_wider_than_byte_at_an_odd_address(
        uint address, AccessWidth width, bool expected)
    {
        Assert.Equal(expected, BusAlignment.IsMisaligned(address, width));
    }

    [Fact]
    public void IsMisaligned_does_not_throw_and_has_no_side_effects()
    {
        // M4.2 is detection-only: the predicate is pure (no raise). The raise/vector is M4.5.
        _ = BusAlignment.IsMisaligned(0x1001u, AccessWidth.Word);
        _ = BusAlignment.IsMisaligned(0x1001u, AccessWidth.Long);
        // No exception type exists for address error in M4.2 — calling the predicate never throws.
    }
}
