using CpuEmulator.Surface.Web;
using Xunit;

namespace CpuEmulator.Tests.Surface;

/// <summary>The asset-free precedence proof for the web server's system selection (ADR 0018 / V80-1): the
/// pure <see cref="DemoSession.SelectSystem"/> decision is the single source of truth for which system a
/// browser boots, given which assets are cached. These assertions are un-fakeable — they would FAIL if the
/// apl2cpm3 (80-col CP/M 3.1) branch were missing or ordered AFTER the 2.2 (40-col) disk, or if the Videx
/// firmware gate (no blank 80-col boot) were dropped. <see cref="DemoSession"/> + the enum are internal to
/// the Web assembly, reached here via the project's InternalsVisibleTo("CpuEmulator.Tests").</summary>
public class WebSystemSelectionTests
{
    // The expected system is passed by NAME (a string) rather than the enum value: a public xUnit test method
    // cannot take the internal DemoSession.WebSystem in its signature (CS0051), so the theory data stays
    // public-typed and the name is mapped to the enum inside.
    // appleRom, apl2cpm3Disk, videxFirmware, cpm22Disk, spectrumRom -> the expected WebSystem name.
    [Theory]
    // apple + apl2cpm3 + videx + cpm22 ALL present -> apl2cpm3 wins over 2.2 (THE precedence proof).
    [InlineData(true, true, true, true, true, nameof(DemoSession.WebSystem.Apl2Cpm3Videx))]
    [InlineData(true, true, true, false, false, nameof(DemoSession.WebSystem.Apl2Cpm3Videx))]
    // apple + cpm22 present, apl2cpm3 absent -> the 2.2 fallback is unchanged.
    [InlineData(true, false, true, true, false, nameof(DemoSession.WebSystem.SoftCardCpm22))]
    [InlineData(true, false, false, true, false, nameof(DemoSession.WebSystem.SoftCardCpm22))]
    // apple + apl2cpm3 present but the REAL Videx firmware is ABSENT -> NOT apl2cpm3 (the firmware gate, no
    // blank 80-col boot): 2.2 if its disk is present, else the bare Apple ][+.
    [InlineData(true, true, false, true, false, nameof(DemoSession.WebSystem.SoftCardCpm22))]
    [InlineData(true, true, false, false, false, nameof(DemoSession.WebSystem.Apple2))]
    // apple only -> the bare Apple ][+.
    [InlineData(true, false, false, false, false, nameof(DemoSession.WebSystem.Apple2))]
    [InlineData(true, false, false, false, true, nameof(DemoSession.WebSystem.Apple2))]   // a present Spectrum ROM never outranks the Apple ROM
    // no apple, spectrum present -> the Spectrum.
    [InlineData(false, false, false, false, true, nameof(DemoSession.WebSystem.Spectrum))]
    [InlineData(false, true, true, true, true, nameof(DemoSession.WebSystem.Spectrum))]   // apl2cpm3/2.2 assets are inert without the Apple ROM
    // nothing -> the demo board.
    [InlineData(false, false, false, false, false, nameof(DemoSession.WebSystem.Demo))]
    public void SelectSystem_picks_the_expected_system(bool appleRom, bool apl2cpm3Disk, bool videxFirmware,
                                                       bool cpm22Disk, bool spectrumRom,
                                                       string expected)
    {
        DemoSession.WebSystem actual =
            DemoSession.SelectSystem(appleRom, apl2cpm3Disk, videxFirmware, cpm22Disk, spectrumRom);
        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public void Apl2cpm3_outranks_the_2_2_disk_when_both_are_cached()
    {
        // The headline precedence proof, called out explicitly: with apple + apl2cpm3 + videx + the 2.2 disk
        // ALL present, the 80-col apl2cpm3 boot is selected over the 40-col 2.2 disk.
        Assert.Equal(DemoSession.WebSystem.Apl2Cpm3Videx,
            DemoSession.SelectSystem(appleRom: true, apl2cpm3Disk: true, videxFirmware: true,
                                     cpm22Disk: true, spectrumRom: true));
    }

    [Fact]
    public void Missing_videx_firmware_falls_through_to_the_2_2_disk_not_a_blank_80col_boot()
    {
        // The firmware gate: the apl2cpm3 CRT80 console JMPs into the $C800 Videx firmware, so without the
        // REAL firmware the 80-col VRAM paints nothing. Selection must NOT pick apl2cpm3 — it falls through
        // to the 2.2 (40-col) disk (which renders), never a blank-screen 80-col boot.
        Assert.Equal(DemoSession.WebSystem.SoftCardCpm22,
            DemoSession.SelectSystem(appleRom: true, apl2cpm3Disk: true, videxFirmware: false,
                                     cpm22Disk: true, spectrumRom: false));
    }
}
