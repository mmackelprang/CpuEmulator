using CpuEmulator.Core;

namespace CpuEmulator.Tests.Core;

public class AddressSpaceRemapTests
{
    private static AddressSpace Space16()
    {
        var s = new AddressSpace(AddressSpaceKind.Program, 16);
        // $D000-$DFFF mapped to "ROM" bank (read-only), value 0xAA throughout.
        var rom = new byte[0x1000];
        Array.Fill(rom, (byte)0xAA);
        s.MapMemory(0xD000, rom, writable: false);
        return s;
    }

    [Fact]
    public void Remap_re_points_a_mapped_range_to_a_new_writable_backing()
    {
        var s = Space16();
        Assert.Equal(0xAA, s.Read8(0xD000));   // the "ROM" bank
        s.Write8(0xD000, 0x55);                // ROM write ignored
        Assert.Equal(0xAA, s.Read8(0xD000));

        var ram = new byte[0x1000];
        Array.Fill(ram, (byte)0xBB);
        s.Remap(0xD000, ram, writable: true);  // bank in the LC RAM

        Assert.Equal(0xBB, s.Read8(0xD000));   // now reads the RAM bank
        s.Write8(0xD000, 0x55);                // and it is writable
        Assert.Equal(0x55, s.Read8(0xD000));
    }
}
