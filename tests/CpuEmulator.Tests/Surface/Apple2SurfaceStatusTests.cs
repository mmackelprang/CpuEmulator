using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class Apple2SurfaceStatusTests
{
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x62; rom[0x2FFD] = 0xFA;   // reset vector -> $FA62 (any valid landing)
        return rom;
    }

    [Fact]
    public void Status_reads_real_board_mode_and_drive_state()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });

        MachineStatus s = surface.Status();

        Assert.Equal("Apple ][+", s.Board);
        // No disk inserted -> the synthetic image -> the "—" label; motor off at boot (not faked).
        // Two modeled drives (PR-Q made drive 2 real; PR-R reports both); both empty at boot.
        Assert.Equal(2, s.Drives.Count);
        Assert.False(s.Drives[0].MotorOn);
        Assert.Equal("—", s.Drives[0].Label);
        Assert.Equal("—", s.Drives[1].Label);
        // Power-on video mode.
        Assert.Equal("TEXT · 40×24 · page 1", s.Mode);
    }

    [Fact]
    public void Status_motor_flips_when_the_guest_turns_the_drive_motor_on()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });

        // $C0E9 through the live bus turns the REAL motor on; Status() must reflect it (not faked).
        surface.Machine.Space(AddressSpaceKind.Program).Read8(0xC0E9);
        Assert.True(surface.Status().Drives[0].MotorOn);
    }
}
