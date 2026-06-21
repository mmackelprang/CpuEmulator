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

    /// <summary>A trivial MMIO device that returns a fixed byte and records writes — to prove a
    /// memory→MMIO remap actually routes through the handler.</summary>
    private sealed class StubDevice(byte readValue) : IPeripheral
    {
        public byte LastWrite { get; private set; }
        public string Name => "stub";
        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) => readValue;
        public void Write(uint offset, AccessWidth width, uint value) => LastWrite = (byte)value;
    }

    private sealed class RecordingListener : IMapInvalidationListener
    {
        public int FirstPage { get; private set; } = -1;
        public int PageCount { get; private set; }
        public int Calls { get; private set; }
        public void OnRemap(int firstPage, int pageCount)
        {
            FirstPage = firstPage; PageCount = pageCount; Calls++;
        }
    }

    [Fact]
    public void RemapPeripheral_re_points_memory_to_mmio()
    {
        var s = Space16();
        var dev = new StubDevice(0x42);
        s.RemapPeripheral(0xD000, 0x0100, dev);  // one page now MMIO

        Assert.Equal(0x42, s.Read8(0xD000));     // routes through the device
        s.Write8(0xD000, 0x99);
        Assert.Equal(0x99, dev.LastWrite);       // write reached the device
    }

    [Fact]
    public void Remap_back_to_memory_drops_a_prior_handler()
    {
        var s = Space16();
        s.RemapPeripheral(0xD000, 0x0100, new StubDevice(0x42));
        Assert.Equal(0x42, s.Read8(0xD000));

        var ram = new byte[0x0100];
        Array.Fill(ram, (byte)0x7E);
        s.Remap(0xD000, ram, writable: true);    // memory wins again
        Assert.Equal(0x7E, s.Read8(0xD000));     // NOT the device's 0x42
    }

    [Fact]
    public void Remap_validates_alignment_and_length()
    {
        var s = Space16();
        Assert.Throws<MachineConfigurationException>(() => s.Remap(0xD080, new byte[0x0100], true)); // unaligned start
        Assert.Throws<MachineConfigurationException>(() => s.Remap(0xD000, new byte[0x0080], true)); // sub-page length
    }

    [Fact]
    public void Remap_fires_the_listener_with_the_exact_page_span()
    {
        var s = Space16();
        var listener = new RecordingListener();
        s.AddMapInvalidationListener(listener);   // internal — visible to the test assembly via InternalsVisibleTo

        s.Remap(0xD000, new byte[0x1000], writable: true); // $D000-$DFFF = pages 0xD0..0xDF (16 pages)

        Assert.Equal(1, listener.Calls);
        Assert.Equal(0xD0, listener.FirstPage);
        Assert.Equal(16, listener.PageCount);
    }
}
