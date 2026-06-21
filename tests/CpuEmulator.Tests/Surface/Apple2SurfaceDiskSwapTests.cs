using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class Apple2SurfaceDiskSwapTests
{
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x62; rom[0x2FFD] = 0xFA;   // reset vector
        return rom;
    }

    // A minimal valid DOS 3.3 .dsk: 35 tracks * 16 sectors * 256 bytes, distinctive per-LBA bytes.
    private static byte[] BuildDsk()
    {
        var img = new byte[35 * 16 * 256];
        for (int i = 0; i < img.Length; i++) img[i] = (byte)((i + 1) & 0xFF);
        return img;
    }

    [Fact]
    public void Surface_inserts_a_dsk_from_bytes_then_a_running_read_pulls_a_nibble()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });

        // Insert a .dsk from raw bytes at runtime (the path R/S will drive).
        surface.InsertDisk(drive: 1, bytes: BuildDsk(), format: DiskFormat.Dsk);

        // Run the machine's motor + a poll via the live bus: $C0E9 (motor), then $C0EC reads advance the
        // head over the runtime-inserted image and eventually latch a GCR byte (bit 7 set).
        var bus = surface.Machine.Space(AddressSpaceKind.Program);
        bus.Read8(0xC0E9);                         // motor on
        bool sawNibble = false;
        for (int i = 0; i < 50_000 && !sawNibble; i++)
            if ((bus.Read8(0xC0EC) & 0x80) != 0) sawNibble = true;
        Assert.True(sawNibble, "a running machine must read a nibble off the runtime-inserted .dsk");
    }

    [Fact]
    public void Eject_then_a_running_read_pulls_nothing()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });
        surface.InsertDisk(drive: 1, bytes: BuildDsk(), format: DiskFormat.Dsk);
        surface.EjectDisk(drive: 1);

        var bus = surface.Machine.Space(AddressSpaceKind.Program);
        bus.Read8(0xC0E9);                         // motor on
        bool sawNibble = false;
        for (int i = 0; i < 50_000 && !sawNibble; i++)
            if ((bus.Read8(0xC0EC) & 0x80) != 0) sawNibble = true;
        Assert.False(sawNibble, "an ejected drive reads nothing — no byte ever has bit 7 set");
    }
}
