using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class Apl2Cpm3BootTests
{
    // ADR 0018 PR-2/PR-3 (V80-2 + V80-3 combined) -- the apl2cpm3 CP/M 3.1 boot on the SoftCard+Videx board.
    //
    // WHAT THIS GATE PROVES (un-fakeable, live on the real Disk 1 + the REAL Videx firmware):
    //   (1) The Cpm3 skew is correct: CPMLDR.COM's entry `LD SP,$0281` ($31) lands at Z80 $0100 (phys $1100).
    //       Under the 2.2 `Cpm` per-track skew this byte is $E9/$C3 (double-skewed) and the boot never starts;
    //       only raw DOS33-on-every-track (SectorOrderKind.Cpm3, ADR 0018-A) places it correctly.
    //   (2) The ?jsr65 Z80<->6502 service-loop bridge round-trips: the boot produces MANY Z80->6502 hand-backs
    //       (the LDRBIOS/BIOSKRN disk-read + console primitives call back into the 6502 L65A loop and return).
    //       ADR 0018-B's predicted "dead bridge" (0 hand-backs) is FALSIFIED live -- the existing handoff works.
    //   (3) The REAL Videx firmware drives the 80-col console: apl2cpm3 is a CRT80 build whose ?icrt/?odcrt
    //       primitives JMP into the $C800 Videx firmware. With the real firmware loaded, the boot programs the
    //       Videx CRTC for 80x24 and paints the GENUINE CP/M 3.1 sign-on ("CP/M Version 3.0, 56K BIOS R6/89" /
    //       "46K TPA") into the Videx $CC00 VRAM -- decoded here off the live VRAM through PeekVramForTest.
    //       (With the SYNTHETIC all-zero firmware the prior pass saw NOTHING here -- the real firmware is the
    //       load-bearing unblock, which is why this gate is [Apl2Cpm3VidexFact] -- it skips without it.)
    //
    // THE WALL (honest -- NOT asserted here): the boot renders the CP/M-3 sign-on on the Videx but does NOT
    // reach the `A>` CCP prompt. After the sign-on, the CCP takes control (JP Z80 $0100, CALL 5 = BDOS) and the
    // BDOS path hits a DETERMINISTIC execution divergence -- a conditional RET returns to Z80 $1901 (a zeroed
    // region) and the Z80 NOP-slides (reproduced byte-identically: instr 36583, PC=$1929, across runs). This is
    // a FIFTH layer BELOW the V80-2/V80-3 scope (skew OK + bridge OK + firmware OK => the sign-on), in the
    // banked CP/M-3 BDOS/CCP execution -- i.e. the Z80 core / SoftCard translation / LC-banking model, which
    // ADR 0018-A A1 + the V80-2 hard constraints put OFF-LIMITS for this PR (no Z80-core / no translation
    // change). So this gate asserts the genuine, un-fakeable milestone the in-scope work achieves (the real
    // CP/M-3 console text on the Videx 80-col VRAM) and the `A>` headline is escalated for the owner to scope
    // the fifth layer. Asserting `A>` or ActiveIndex==1 here would be a false pass (neither happens live).
    //
    // CONTRAST SIBLING (V80-3 Task 4): the shipped CPM-5 2.2 gate
    // (SoftCardVidexBoardTests.Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter) asserts
    // videxEngagedCount==0 + ActiveIndex==0 -- the 2.2 master is a 40-col console that issues ZERO $C0Bx and
    // never touches the Videx VRAM. apl2cpm3 is the opposite: it programs the Videx CRTC and paints the
    // console into the $CC00 VRAM (the sign-on decoded below). Same board wiring + auto-switch; the only
    // difference is the disk. (Do NOT modify the CPM-5 gate -- it asserts the 40-col hardware truth.)
    [Apl2Cpm3VidexFact]
    public void Cpm3_renders_the_cpm3_signon_on_the_Videx_80col_interpreter()
    {
        var (systemRomPath, disk1Path, videxFirmware, videxCharRom) =
            Apl2Cpm3Vectors.TryGetVidexAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the apl2cpm3 boot gate");
        IBlockDevice disk1 = Apl2Cpm3.LoadBootDisk(disk1Path);

        // Build the SoftCard+Videx board at SLOT 4 (apl2cpm3's slot -- V80-1) with the REAL Videx firmware +
        // char ROM, tracking the Videx auto-switch the production SoftCardVidexSurface wires.
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(disk1, SectorOrderKind.Cpm3);   // ADR 0018-A: raw DOS33 on every track
        var disk = new Apple2DiskII(drive1);
        var videx = new VidexVideoterm(videxCharRom, videxFirmware);  // the REAL firmware drives the $C800 console
        var iou = new Apple2Iou(state, lc, disk, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx,
            controlPortBase: SoftCardBoard.ControlPortBaseSlot4);     // THE SLOT FIX (V80-1)
        Machine machine = BoardMachineFactory.Build(spec);            // interpreter tier (the coprocessor is interpreter)
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state);

        var mux = new DisplayMultiplexer([video, videx], initialActive: 0);
        int videxEngagedCount = 0;
        videx.ActiveChanged += active =>
        {
            if (active) videxEngagedCount++;
            mux.SetActive(active ? 1 : 0);
        };

        machine.Reset();
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // Run the boot in fine chunks so we can sample the skew discriminator + count Z80<->6502 hand-backs
        // (a coarse single Run() would miss handback+handoff pairs that collapse within one slice).
        const long total = CpmBootCycles;
        const long slice = 2_000L;
        bool sawZ31AtZ80Entry = false;     // $31 (LD SP) at Z80 $0100 == phys $1100 -- the Cpm3-skew proof
        int handBacks = 0;                 // Z80->6502 transitions (CoprocessorActive true->false) -- the bridge
        bool prevActive = machine.CoprocessorActive;
        for (long run = 0; run < total; run += slice)
        {
            machine.Run(slice);
            if (!sawZ31AtZ80Entry && bus.Read8(0x1100) == 0x31) sawZ31AtZ80Entry = true;
            bool active = machine.CoprocessorActive;
            if (active != prevActive)
            {
                if (!active) handBacks++;
                prevActive = active;
            }
        }

        // --- (1) The Cpm3 skew is correct: CPMLDR's `LD SP` ($31) landed at Z80 $0100 (phys $1100). This FAILS
        //         under the 2.2 `Cpm` double-skew (which never even starts the Z80) -- the un-fakeable proof of
        //         ADR 0018-A's raw-DOS33-on-every-track decision (V80-2 / ADR 0018-A Decision A3).
        Assert.True(sawZ31AtZ80Entry,
            "expected CPMLDR.COM's `LD SP,$0281` ($31) at Z80 $0100 (phys $1100) -- the Cpm3 raw-DOS33 skew. "
          + "It is mis-placed under the 2.2 `Cpm` per-track skew (the double-skew ADR 0018-A root-caused).");

        // --- (2) The ?jsr65 Z80<->6502 bridge round-trips: the boot produced many hand-backs (the LDRBIOS /
        //         BIOSKRN disk-read + console primitives call the 6502 L65A loop and return). ADR 0018-B's
        //         predicted dead bridge (0 hand-backs) is falsified live (ADR 0018-B Decision B3 discriminator).
        Assert.True(handBacks > 0,
            $"expected >=1 Z80->6502 hand-back (the ?jsr65 service-loop bridge); observed {handBacks}.");

        // --- (3) The REAL Videx firmware drove the 80-col console: the GENUINE CP/M 3.1 sign-on is decoded off
        //         the live Videx $CC00 VRAM (the firmware programmed the CRTC for 80x24 and ran ?odcrt). This is
        //         the headline in-scope achievement -- the first real CP/M-3 console text on the Videx 80-col
        //         render. With the synthetic all-zero firmware this VRAM is EMPTY (the firmware is load-bearing).
        string videxConsole = DecodeVidexConsole(videx);
        Assert.True(
            videxConsole.Contains("CP/M", StringComparison.OrdinalIgnoreCase),
            $"expected the CP/M-3 sign-on on the Videx 80-col VRAM; decoded console was:\n{videxConsole}");
        Assert.Contains("BIOS", videxConsole, StringComparison.OrdinalIgnoreCase);   // "...56K BIOS R6/89"
        Assert.Contains("TPA", videxConsole, StringComparison.OrdinalIgnoreCase);    // "46K TPA"
    }

    /// <summary>Decode the Videx 80x24 character VRAM to ASCII -- the terminal console text. The Videx maps its
    /// 2 KiB VRAM as 4 x 512-byte banks behind the $CC00 window; this firmware addresses bank 0 linearly (80
    /// chars/row), so the sign-on lands in bank 0. We read every bank's live cells (PeekVramForTest(bank,
    /// offset) -- the shipped seam) and join them as 80-col rows. The Videx stores 7-bit ASCII char codes.</summary>
    private static string DecodeVidexConsole(VidexVideoterm videx)
    {
        var sb = new System.Text.StringBuilder();
        for (int bank = 0; bank < VidexVideoterm.BankCount; bank++)
        {
            for (int row = 0; row * 80 < VidexVideoterm.BankSize; row++)
            {
                var line = new System.Text.StringBuilder(80);
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

    // The apl2cpm3 boot budget. The CP/M-3 loader chain (CPMLDR -> CPM3.SYS -> BIOSKRN) settles the sign-on on
    // the Videx well before this; tuned on the first green run.
    private const long CpmBootCycles = 12_000_000L;
}
