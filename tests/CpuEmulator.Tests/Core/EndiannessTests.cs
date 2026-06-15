using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Core;

/// <summary>M4.2 (ADR 0003 Decision 2) — the bus byte-order property. Endianness is a BUS property (not a
/// CPU-side convention baked into emitted IL): the 6502/Z80 are LittleEndian (the default), the 68000 is
/// BigEndian. Pinned so the wide-bus path (AddressSpace) can branch on it.</summary>
public class EndiannessTests
{
    [Fact]
    public void Has_exactly_two_members()
    {
        Assert.Equal(2, System.Enum.GetValues<Endianness>().Length);
    }

    [Fact]
    public void LittleEndian_is_the_default_zero_value()
    {
        // default(Endianness) is LittleEndian — the 6502/Z80 bus order, so a default-constructed bus is LE.
        Assert.Equal(Endianness.LittleEndian, default(Endianness));
    }

    [Fact]
    public void Members_are_named_LittleEndian_and_BigEndian()
    {
        Assert.True(System.Enum.IsDefined(Endianness.LittleEndian));
        Assert.True(System.Enum.IsDefined(Endianness.BigEndian));
    }
}
