using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2DiskIIStatusTests
{
    private static SyntheticFluxImage OneTrack()
    {
        var img = new SyntheticFluxImage(trackCount: 35);
        img.SetTrackNibbles(0, new byte[] { 0xFF, 0xD5, 0xAA, 0x96 });
        return img;
    }

    [Fact]
    public void MotorOn_reports_the_real_motor_flag_not_a_faked_insert_state()
    {
        var disk = new Apple2DiskII(OneTrack());

        // A freshly built controller with an image inserted is NOT spinning — the motor follows the
        // $C0E9/$C0E8 switches, never the presence of a disk (the design's "not faked on insert" rule).
        Assert.False(disk.MotorOn);

        disk.Access(0x9, isRead: true);   // $C0E9: motor on now
        Assert.True(disk.MotorOn);

        // $C0E8 (motor-off request) with no scheduler stops immediately (the bare-unit path).
        disk.Access(0x8, isRead: true);
        Assert.False(disk.MotorOn);
    }
}
