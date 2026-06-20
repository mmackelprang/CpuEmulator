using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumSurfaceTests
{
    [Fact]
    public void Surface_composes_a_machine_host_with_the_ula_as_display_keyboard_and_audio()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        blankRom[0] = 0x76; // HALT

        byte[]? lastFrame = null;
        byte[]? lastAudio = null;
        SpectrumSurface surface = SpectrumSurface.Create(blankRom,
            frame => lastFrame = frame, audio => lastAudio = audio);

        surface.Machine.Reset();
        // Write a recognizable screen byte through the guest space, then step past a frame tick.
        surface.Machine.Space(AddressSpaceKind.Program).Write8(0x4000, 0x80);
        surface.Machine.Space(AddressSpaceKind.Program).Write8(0x5800, (byte)(2 | (7 << 3)));
        surface.Host.RunHeadless(SpectrumUla.TStatesPerFrame * 2, 5_000);

        Assert.NotNull(lastFrame);
        Assert.Equal((byte)'F', lastFrame![0]);   // an FB frame was pushed
        Assert.NotNull(lastAudio);
        Assert.Equal((byte)'A', lastAudio![0]);    // an AU frame was pushed
    }

    [Fact]
    public void Surface_routes_a_key_to_the_ula_matrix()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        blankRom[0] = 0x76;
        SpectrumSurface surface = SpectrumSurface.Create(blankRom, _ => { }, _ => { });
        surface.Machine.Reset();

        surface.Host.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a'));
        Assert.Equal(0u, surface.Ula.Read(0xFDFEu, AccessWidth.Byte) & 0x01); // 'A' pressed
    }
}
