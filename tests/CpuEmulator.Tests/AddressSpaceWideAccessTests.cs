using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests;

/// <summary>M4.2 (ADR 0003 Decision 2) — the wide big-endian bus proof. AddressSpace gains
/// Read16/Read32/Write16/Write32 + an Endianness property. A word is one transaction (two byte accesses
/// composed per Endianness); a long is two words, HIGH WORD FIRST under big-endian. The 6502/Z80 default
/// (LittleEndian) is the mirror, and is byte-identical to the pre-M4.2 CPU-side little-endian convention.
/// All proven against a real RAM-backed AddressSpace — the wide path composes over Read8/Write8, so it
/// inherits page resolution, wrap-masking, and (Task 3 covers) ROM/MMIO routing.</summary>
public class AddressSpaceWideAccessTests
{
    private static AddressSpace NewSpace(Endianness endianness, int addressBits = 16)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits, endianness: endianness);
        space.MapMemory(0x0000, new byte[0x1000], writable: true);
        return space;
    }

    [Fact]
    public void Endianness_property_reflects_construction()
    {
        Assert.Equal(Endianness.BigEndian, NewSpace(Endianness.BigEndian).Endianness);
        Assert.Equal(Endianness.LittleEndian, NewSpace(Endianness.LittleEndian).Endianness);
    }

    [Fact]
    public void Default_endianness_is_little_endian()
    {
        // The 2-arg ctor (no endianness) is LittleEndian — the 6502/Z80 bus order, byte-identical to before.
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        Assert.Equal(Endianness.LittleEndian, space.Endianness);
    }

    [Fact]
    public void Write16_big_endian_lays_the_high_byte_at_the_lower_address()
    {
        var space = NewSpace(Endianness.BigEndian);
        space.Write16(0x0100, 0xABCD);
        Assert.Equal(0xAB, space.Read8(0x0100));   // high byte at the lower address (BE)
        Assert.Equal(0xCD, space.Read8(0x0101));   // low byte next
    }

    [Fact]
    public void Write16_little_endian_lays_the_low_byte_at_the_lower_address()
    {
        var space = NewSpace(Endianness.LittleEndian);
        space.Write16(0x0100, 0xABCD);
        Assert.Equal(0xCD, space.Read8(0x0100));   // low byte at the lower address (LE)
        Assert.Equal(0xAB, space.Read8(0x0101));
    }

    [Fact]
    public void Read16_big_endian_round_trips_a_word()
    {
        var space = NewSpace(Endianness.BigEndian);
        space.Write16(0x0200, 0x1234);
        Assert.Equal((ushort)0x1234, space.Read16(0x0200));
    }

    [Fact]
    public void Read16_big_endian_reads_high_byte_first()
    {
        var space = NewSpace(Endianness.BigEndian);
        space.Write8(0x0300, 0xBE);                // high byte at the lower address
        space.Write8(0x0301, 0xEF);                // low byte next
        Assert.Equal((ushort)0xBEEF, space.Read16(0x0300));
    }

    [Fact]
    public void Write32_big_endian_lays_four_bytes_big_endian_high_word_first()
    {
        var space = NewSpace(Endianness.BigEndian);
        space.Write32(0x0400, 0xDEADBEEF);
        Assert.Equal(0xDE, space.Read8(0x0400));   // byte 0: most significant
        Assert.Equal(0xAD, space.Read8(0x0401));
        Assert.Equal(0xBE, space.Read8(0x0402));
        Assert.Equal(0xEF, space.Read8(0x0403));   // byte 3: least significant
        // = word 0xDEAD at 0x0400 (the HIGH word, written first), word 0xBEEF at 0x0402.
        Assert.Equal((ushort)0xDEAD, space.Read16(0x0400));
        Assert.Equal((ushort)0xBEEF, space.Read16(0x0402));
    }

    [Fact]
    public void Write32_little_endian_lays_four_bytes_little_endian()
    {
        var space = NewSpace(Endianness.LittleEndian);
        space.Write32(0x0400, 0xDEADBEEF);
        Assert.Equal(0xEF, space.Read8(0x0400));   // least significant at the lower address
        Assert.Equal(0xBE, space.Read8(0x0401));
        Assert.Equal(0xAD, space.Read8(0x0402));
        Assert.Equal(0xDE, space.Read8(0x0403));
    }

    [Fact]
    public void Read32_big_endian_round_trips_a_long()
    {
        var space = NewSpace(Endianness.BigEndian);
        space.Write32(0x0500, 0x01020304);
        Assert.Equal(0x01020304u, space.Read32(0x0500));
    }

    [Fact]
    public void Read32_little_endian_round_trips_a_long()
    {
        var space = NewSpace(Endianness.LittleEndian);
        space.Write32(0x0500, 0x01020304);
        Assert.Equal(0x01020304u, space.Read32(0x0500));
    }

    [Fact]
    public void Wide_access_straddling_a_page_boundary_composes_correctly()
    {
        // 256-byte pages: address 0x00FF is the last byte of page 0; 0x0100 is the first byte of page 1.
        // A word at 0x00FF straddles the boundary; each composed Read8/Write8 resolves through its own page.
        var space = NewSpace(Endianness.BigEndian);
        space.Write16(0x00FF, 0x9A7C);
        Assert.Equal(0x9A, space.Read8(0x00FF));   // page 0, last byte (high byte, BE)
        Assert.Equal(0x7C, space.Read8(0x0100));   // page 1, first byte
        Assert.Equal((ushort)0x9A7C, space.Read16(0x00FF));
    }

    [Fact]
    public void Wide_access_at_the_top_of_the_space_wraps_the_second_byte()
    {
        // On a 16-bit space the top address is 0xFFFF; the word's second byte wraps to 0x0000 (the bus
        // wraps — each composed Read8 masks independently). Map the full space so both ends are RAM.
        var space = new AddressSpace(AddressSpaceKind.Program, 16, endianness: Endianness.BigEndian);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write16(0xFFFF, 0x55AA);
        Assert.Equal(0x55, space.Read8(0xFFFF));   // high byte at 0xFFFF (BE)
        Assert.Equal(0xAA, space.Read8(0x0000));   // low byte wrapped to 0x0000
    }
}
