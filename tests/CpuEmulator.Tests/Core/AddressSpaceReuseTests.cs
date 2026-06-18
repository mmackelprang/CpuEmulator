using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Core;

public class AddressSpaceReuseTests
{
    [Fact]
    public void ClearAndReinstall_zeroes_the_backing_and_keeps_the_mapping()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var ram = new byte[0x10000];
        space.MapMemory(0x0000, ram, writable: true);

        space.Write8(0x1234, 0xAB);
        Assert.Equal(0xAB, space.Read8(0x1234));

        // Reuse for the "next case": re-zero the SAME backing, same mapping, no re-alloc.
        space.ClearMappedBacking(ram);
        Assert.Equal(0x00, space.Read8(0x1234));     // cleared
        space.Write8(0x4321, 0xCD);                  // mapping still live
        Assert.Equal(0xCD, space.Read8(0x4321));
    }
}
