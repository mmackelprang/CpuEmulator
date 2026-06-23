using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using CpuEmulator.Surface.Web;
using CpuEmulator.Tests.Apple2;
using Xunit;

namespace CpuEmulator.Tests.Surface;

/// <summary>The end-to-end proof that the PRODUCTION web surface factory reaches the genuine Apple Pascal
/// (UCSD p-System) COMMAND line — not merely that selection returns the right enum (that is the asset-free
/// <see cref="WebSystemSelectionTests"/>). This boots through <see cref="Apple2Surface.CreatePascal"/> (the
/// exact factory the web server's <c>WebSystem.Pascal</c> branch calls) on the real APPLE1 (boot) + APPLE0
/// (program) `.dsk` images + the slot-6 disk2.rom, runs the same 90M-cycle budget as <c>PascalBootTests</c>,
/// and asserts the p-System sign-on + the outer <c>COMMAND:</c> line decoded off the live 40-col text page.
/// Gated by <see cref="PascalBootFactAttribute"/> — asset-free CI SKIPS it cleanly (green); the selection
/// test is the asset-free arbiter.</summary>
public class WebPascalRenderTests
{
    // The Pascal loader chain settles the COMMAND: line by ~75M cycles; this is the PascalBootTests budget.
    private const long BootCycles = 90_000_000L;

    [PascalBootFact]
    public void Production_pascal_surface_renders_the_command_line()
    {
        var (systemRomPath, bootDiskPath, programDiskPath) = PascalVectors.TryGetAssets()!.Value;

        byte[] sys = Apple2Rom.Load(systemRomPath);
        byte[] bootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the Pascal render gate");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();

        // Build via the PRODUCTION factory — the exact call the web server's WebSystem.Pascal branch makes
        // (APPLE1 in drive 1, APPLE0 in drive 2, re-nibblized at Pascal.Order, the LC read-ROM/write-RAM mode).
        var surface = Apple2Surface.CreatePascal(
            sys, bootRom, charRom, bootDiskPath, programDiskPath, _ => { }, _ => { });

        // Run the boot headlessly on the surface's host to the PascalBootTests budget. RunHeadless steps the
        // host slice-by-slice without a wall clock.
        surface.Host.RunHeadless(totalCycles: BootCycles, sliceCycles: 17_030);

        // Decode the live 40-col text page off the surface's machine bus and assert the genuine sign-on + the
        // outer p-System COMMAND line — the same un-fakeable arbiters PascalBootTests uses.
        IAddressSpace bus = surface.Machine.Space(AddressSpaceKind.Program);
        string console = DecodeText40(bus);

        Assert.True(
            console.Contains("APPLE II PASCAL", StringComparison.Ordinal),
            $"expected the Apple II Pascal sign-on on the production web surface's text page; decoded console was:\n{console}");
        Assert.True(
            console.Contains("COMMAND:", StringComparison.Ordinal),
            $"expected the outer p-System COMMAND: line on the production web surface's text page (the "
          + $"un-fakeable arbiter that the web-selected Pascal surface reaches the real interactive command "
          + $"loop); decoded console was:\n{console}");
        Assert.True(
            console.Contains("E(DIT", StringComparison.Ordinal),
            $"expected the COMMAND: menu's E(DIT entry on the production web surface's text page; decoded console was:\n{console}");
    }

    /// <summary>Decode the 40-col text page ($0400, page 1) to ASCII. The ][+ character ROM maps four bands
    /// ($00-$3F inverse, $40-$7F flashing, $80-$FF normal) over the SAME 6-bit uppercase glyph set; fold the
    /// low 6 bits to printable ASCII ($00-$1F -> '@'..'_', $20-$3F -> ' '..'?'). Joined as 24 rows. Copied
    /// from PascalBootTests (the headless arbiter) so the web gate decodes the screen identically.</summary>
    private static string DecodeText40(IAddressSpace bus)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 24; r++)
        {
            uint rowBase = Apple2HiResAddress.TextRowBase(r, page2: false);
            for (int c = 0; c < 40; c++)
            {
                int g = bus.Read8(rowBase + (uint)c) & 0x3F;     // band-independent 6-bit glyph index
                int ascii = g < 0x20 ? g + 0x40 : g;             // $00-$1F -> @A-Z[\]^_ ; $20-$3F -> space..?
                sb.Append(ascii is >= 0x20 and <= 0x7E ? (char)ascii : ' ');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
