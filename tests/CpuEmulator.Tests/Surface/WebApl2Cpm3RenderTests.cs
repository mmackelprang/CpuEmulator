using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using CpuEmulator.Surface.Web;
using CpuEmulator.Tests.Apple2;
using Xunit;

namespace CpuEmulator.Tests.Surface;

/// <summary>The end-to-end proof that the PRODUCTION web surface factory reaches the genuine apl2cpm3 80-col
/// boot — not merely that selection returns the right enum (that is the asset-free WebSystemSelectionTests).
/// This boots through <see cref="SoftCardVidexSurface.CreateApl2Cpm3"/> (the exact factory the web server's
/// <c>WebSystem.Apl2Cpm3Videx</c> branch calls) on the real Disk 1 + the REAL Videx firmware, runs the same
/// 12M-cycle budget as <c>Apl2Cpm3BootTests</c>, and asserts the Videx auto-engaged (ActiveIndex==1) plus the
/// decoded CP/M-3 sign-on + the `A>` CCP prompt on the live Videx $CC00 VRAM. Gated by
/// <see cref="Apl2Cpm3VidexFactAttribute"/> — asset-free CI SKIPS it cleanly (green); the selection test is
/// the asset-free arbiter.</summary>
public class WebApl2Cpm3RenderTests
{
    [Apl2Cpm3VidexFact]
    public void Production_apl2cpm3_surface_renders_the_80col_A_prompt()
    {
        var (systemRomPath, disk1Path, videxFirmware, videxCharRom) =
            Apl2Cpm3Vectors.TryGetVidexAssets()!.Value;

        byte[] sys = Apple2Rom.Load(systemRomPath);
        byte[] bootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the apl2cpm3 render gate");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();
        IBlockDevice disk1 = Apl2Cpm3.LoadBootDisk(disk1Path);

        // Build via the PRODUCTION factory — the same call the web server's Apl2Cpm3Videx branch makes (Cpm3
        // raw-DOS33 skew + slot-4 $C400 control port baked in by CreateApl2Cpm3).
        var surface = SoftCardVidexSurface.CreateApl2Cpm3(
            sys, bootRom, charRom, videxCharRom, videxFirmware, disk1, _ => { }, _ => { });

        // Run the boot headlessly on the surface's host to the Apl2Cpm3BootTests budget (12M cycles at the
        // ~17,030-cycle Apple slice). RunHeadless steps the host slice-by-slice without a wall clock.
        surface.Host.RunHeadless(totalCycles: 12_000_000, sliceCycles: 17_030);

        // The Videx auto-engaged: the apl2cpm3 CRT80 firmware programmed the CRTC ($C0B1), flipping the
        // DisplayMultiplexer to the 80-col terminal (index 1). This is the sibling of the 40-col 2.2 gate's
        // ActiveIndex==0.
        Assert.Equal(1, surface.Display.ActiveIndex);

        // Decode the live Videx $CC00 VRAM and assert the genuine CP/M-3 sign-on + the `A>` CCP prompt.
        string console = DecodeVidexConsole(surface.Videx);
        Assert.True(
            console.Contains("CP/M", StringComparison.OrdinalIgnoreCase),
            $"expected the CP/M-3 sign-on on the production web surface's Videx VRAM; decoded console was:\n{console}");
        Assert.True(
            console.Contains("A>", StringComparison.Ordinal),
            $"expected the decoded `A>` CCP prompt on the production web surface's Videx VRAM (the un-fakeable "
          + $"arbiter that the web-selected surface reaches the real 80-col boot); decoded console was:\n{console}");
    }

    /// <summary>Decode the Videx 80x24 character VRAM to ASCII — the terminal console text. Mirrors
    /// <c>Apl2Cpm3BootTests.DecodeVidexConsole</c> (a private static there): reads every bank's live cells
    /// via the shipped <c>PeekVramForTest</c> seam and joins them as 80-col rows; the Videx stores 7-bit
    /// ASCII char codes.</summary>
    private static string DecodeVidexConsole(VidexVideoterm videx)
    {
        var sb = new StringBuilder();
        for (int bank = 0; bank < VidexVideoterm.BankCount; bank++)
        {
            for (int row = 0; row * 80 < VidexVideoterm.BankSize; row++)
            {
                var line = new StringBuilder(80);
                for (int col = 0; col < 80 && row * 80 + col < VidexVideoterm.BankSize; col++)
                {
                    int code = videx.PeekVramForTest(bank, row * 80 + col) & 0x7F;
                    line.Append(code is >= 0x20 and <= 0x7E ? (char)code : ' ');
                }
                if (line.ToString().Trim().Length > 0) sb.AppendLine(line.ToString().TrimEnd());
            }
        }
        return sb.ToString();
    }
}
