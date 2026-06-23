using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

/// <summary>The Apple II Pascal (UCSD p-System II.1) boot gate -- the headline of the Pascal bring-up.
///
/// WHAT THIS GATE PROVES (un-fakeable, live on the real owner-supplied APPLE1 + APPLE0 `.dsk` images):
///   (1) The sector order is correct: the Apple Pascal `.dsk` is in DOS-3.3 on-disk order containing a UCSD
///       Pascal filesystem, so <see cref="SectorOrderKind.Dos33"/> (NOT ProDOS) re-nibblizes it correctly.
///       Under ProDOS order the boot executes garbage and faults; under DOS&#160;3.3 the p-System loads.
///       (Cross-checked against dmolony/AppleFileSystem's Pascal interleave table -- see <see cref="Pascal"/>.)
///   (2) The Language Card "read ROM, write RAM" mode works: the p-machine interpreter (SYSTEM.APPLE) loader
///       write-enables the LC RAM while executing from the Monitor/Applesoft ROM ($C081/$C089) and copies the
///       interpreter into the banked $D000-$FFFF, then runs it ($C080 -> `JMP ($FFF8)`). The single-backing
///       page table could not express read-source != write-target, so the writes were dropped and the boot
///       `JMP ($0000)`-faulted; the LC write-through fix (Apple2LanguageCard.ApplyMapping) lets the interpreter
///       land and run. This gate is the live red->green proof of that fix.
///   (3) The boot reaches the genuine p-System sign-on AND the outer COMMAND line, decoded off the live 40-col
///       text page -- the un-fakeable arbiter (the disk is the oracle; we assert the verbatim decoded text).
///
/// TOPOLOGY: APPLE1 (boot) in drive 1, APPLE0 (program/compiler) in drive 2 -- the authentic two-drive order
/// (APPLE1 carries SYSTEM.APPLE + SYSTEM.PASCAL; APPLE0 carries the compiler/editor set). See <see cref="Pascal"/>.</summary>
[Trait("Category", "UAT")]
public class PascalBootTests
{
    // The Pascal loader chain (Disk II boot -> SYSTEM.APPLE interpreter -> SYSTEM.PASCAL OS -> the outer
    // command line) settles the sign-on well before this; tuned on the first green run (the live boot reaches
    // COMMAND: by ~75M cycles -- this budget has comfortable headroom).
    private const long BootCycles = 90_000_000L;

    [PascalBootFact]
    public void Pascal_boots_to_the_p_system_command_line_on_the_apple2plus()
    {
        var (systemRomPath, bootDiskPath, programDiskPath) = PascalVectors.TryGetAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the Pascal boot gate");

        // The plain Apple ][+ board: system ROM + the Language Card (rides the IOU) + the REAL slot-6 Disk II
        // boot ROM at $C600. Drive 1 = APPLE1 (boot), drive 2 = APPLE0 (program) -- both re-nibblized with the
        // DOS-3.3 on-disk order (Pascal.Order). The cold Autostart scan finds the slot-6 signature in the real
        // disk2.rom, JMP ($C600)s into it, and the P5/P6 boot reads track 0 sector 0 into $0800 and runs the
        // Pascal boot block, which loads the interpreter into the Language Card RAM. The canonical board is built
        // ONCE in Pascal.CreateBoard (the single source of truth — BootProbe + the web surface reuse it).
        Machine machine = Pascal.CreateBoard(systemRom, diskBootRom, bootDiskPath, programDiskPath).Machine;

        machine.Reset();
        machine.Run(BootCycles);

        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
        string console = DecodeText40(bus);

        // (3) The genuine Apple II Pascal 1.1 sign-on (UCSD p-System II.1) -- decoded verbatim off the live
        //     40-col text page. These strings are the disk's own boot banner (the oracle), not a heuristic.
        Assert.Contains("APPLE II PASCAL", console, StringComparison.Ordinal);   // "WELCOME ..., TO APPLE II PASCAL 1.1"
        Assert.Contains("UCSD PASCAL", console, StringComparison.Ordinal);       // "BASED ON UCSD PASCAL II.1"

        // (3, the headline) The outer p-System COMMAND line -- "COMMAND: E(DIT, R(UN, F(ILE, C(OMP, L(IN..."
        //     -- the canonical UCSD outer command menu. Reaching this is the proof the p-System is interactive
        //     (the interpreter + OS loaded and handed control to the outer command loop).
        Assert.Contains("COMMAND:", console, StringComparison.Ordinal);
        Assert.Contains("E(DIT", console, StringComparison.Ordinal);
        Assert.Contains("R(UN", console, StringComparison.Ordinal);
        Assert.Contains("F(ILE", console, StringComparison.Ordinal);
        Assert.Contains("C(OMP", console, StringComparison.Ordinal);
    }

    /// <summary>Decode the 40-col text page ($0400, page 1) to ASCII. The ][+ character ROM maps four bands
    /// ($00-$3F inverse, $40-$7F flashing, $80-$FF normal) over the SAME 6-bit uppercase glyph set; fold the
    /// low 6 bits to printable ASCII ($00-$1F -> '@'..'_', $20-$3F -> ' '..'?'). Joined as 24 rows.</summary>
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
