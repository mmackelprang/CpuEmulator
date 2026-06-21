using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class DriveTwoStatusTests
{
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x62; rom[0x2FFD] = 0xFA;   // reset vector
        return rom;
    }

    private static byte[] Dsk() => new byte[35 * 16 * 256];

    [Fact]
    public void Status_reports_two_drives_with_per_drive_labels()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });

        // Two drive entries from the start (drive 2 is real since PR-Q).
        MachineStatus s0 = surface.Status();
        Assert.Equal(2, s0.Drives.Count);
        Assert.Equal("—", s0.Drives[0].Label);
        Assert.Equal("—", s0.Drives[1].Label);

        // Insert into drive 2 with a label -> only the 2nd entry updates.
        surface.InsertDisk(drive: 2, bytes: Dsk(), format: DiskFormat.Dsk, label: "DOS33");
        MachineStatus s1 = surface.Status();
        Assert.Equal("—", s1.Drives[0].Label);
        Assert.Equal("DOS33", s1.Drives[1].Label);

        // Eject drive 2 -> back to "—".
        surface.EjectDisk(drive: 2);
        Assert.Equal("—", surface.Status().Drives[1].Label);
    }
}
