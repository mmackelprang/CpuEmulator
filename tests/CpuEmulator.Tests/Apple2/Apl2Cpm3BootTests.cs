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
    //   (4) The boot reaches the decoded `A>` CCP prompt on the Videx 80-col VRAM (the headline arbiter,
    //       ADR 0018 Decision 4 / ADR 0018-C Decision C4). The fifth-layer blocker is now FIXED by V80-4
    //       (ADR 0018-C): the CP/M-3 loader's ?ldccp copies the CCP into LC bank 2 with `LD (0E08BH),A`
    //       (an odd-address WRITE to Apple $C08B, a bank-2 select) -> `LDIR` -> `LD (0E083H),A`. The old
    //       single-latch Language-Card model cleared write-enable on that odd-address WRITE, so the `LDIR`
    //       was silently dropped and LC bank 2 stayed zeroed (the banked BDOS then RET'd into a zeroed
    //       Z80 $1901 and NOP-slid -- no `A>`). The two-latch 74LS175 correction (MAME ramcard16k do_io /
    //       Sather ch.5) keeps write-enable across the odd-address write (only an even access clears it),
    //       so the CCP copy LANDS in LC bank 2 and the boot runs far past the old wedge to the genuine
    //       `A>` prompt -- decoded here off the live Videx $CC00 VRAM (the un-fakeable CCP prompt, never a
    //       heuristic). This gate now asserts BOTH the CP/M-3 sign-on (still on screen above the prompt)
    //       AND the decoded `A>`, plus the LC-bank-2-nonzero discriminator (the tight red->green proof
    //       that the CCP copy landed -- bank 2 is all zeros under the old model, ADR 0018-C).
    //
    //   (5) THE V80-3 80-col AUTO-ENGAGE (ADR 0018-C OQ1): the boot also proves the DisplayMultiplexer
    //       switched to the Videx (ActiveIndex==1). The apl2cpm3 CRT80 firmware programs the Videx CRTC via
    //       $C0B1 data writes but paints VRAM LINEARLY at $CC00 and never does a $C0B8-$C0BF bank-select, so
    //       the old bank-select-only engagement trigger never fired (ActiveIndex stayed 0). V80-3 makes a
    //       CRTC-data write ($C0B1 -- the firmware bringing the 80-col display online) ALSO engage the Videx,
    //       so the mux flips to index 1 from a real CP/M-3 boot. This is the headline + the contrast sibling
    //       to the CPM-5 gate's ActiveIndex==0.
    //
    // CONTRAST SIBLING (V80-3): the shipped CPM-5 2.2 gate
    // (SoftCardVidexBoardTests.Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter) asserts
    // videxEngagedCount==0 + ActiveIndex==0 -- the 2.2 master is a 40-col console that issues ZERO $C0Bx and
    // never touches the Videx VRAM, so the CRTC-program trigger never fires. apl2cpm3 is the opposite: it
    // programs the Videx CRTC ($C0B1) and paints the console into the $CC00 VRAM (the sign-on + `A>` decoded
    // below), which engages the 80-col display -> ActiveIndex==1. Same board wiring + auto-switch; the only
    // difference is the disk. (Do NOT modify the CPM-5 gate -- it asserts the 40-col hardware truth.)
    [Apl2Cpm3VidexFact]
    public void Cpm3_boots_to_the_A_prompt_in_80col_on_the_Videx_interpreter()
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
        bool sawCoprocessorActive = false; // the Z80 was the bus master at some point -- the boot is genuinely live
        bool prevActive = machine.CoprocessorActive;
        for (long run = 0; run < total; run += slice)
        {
            machine.Run(slice);
            if (!sawZ31AtZ80Entry && bus.Read8(0x1100) == 0x31) sawZ31AtZ80Entry = true;
            bool active = machine.CoprocessorActive;
            if (active) sawCoprocessorActive = true;
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

        // --- (2b) The Z80 was the active bus master during the boot -- the boot is genuinely live (the
        //          coprocessor took the bus and ran CP/M-3). Sampled across the loop, not at the final instant:
        //          the CCP idles in the 6502 `?jsr65` service loop, so at loop-exit the 6502 is the master
        //          (CoprocessorActive false). The sibling of the CPM-5 gate's CoprocessorActive truth -- the Z80 ran.
        Assert.True(sawCoprocessorActive,
            "expected the Z80 to have been the active bus master during the apl2cpm3 CP/M-3 boot (the live coprocessor)");

        // --- (2c) THE V80-3 HEADLINE (ADR 0018-C OQ1): the Videx auto-engaged and the DisplayMultiplexer
        //          switched to the 80-col terminal (ActiveIndex==1). The apl2cpm3 CRT80 firmware programmed the
        //          Videx CRTC ($C0B1 data writes), which engaged the 80-col display (the CRTC-program trigger
        //          fired). This is the contrast sibling to the CPM-5 2.2 gate's ActiveIndex==0: a 40-col master
        //          issues ZERO $C0Bx and never engages, while apl2cpm3 programs the CRTC and flips the mux to
        //          the live 80-col terminal (index 1).
        Assert.True(videxEngagedCount > 0,
            $"expected the Videx to auto-engage: the apl2cpm3 CRT80 firmware programmed the Videx CRTC ($C0B1), "
          + $"which engages the 80-col display (ADR 0018-C OQ1 / V80-3); observed {videxEngagedCount} engagements. "
          + "A 40-col master issues zero $C0Bx and never engages -- ActiveIndex would stay 0 (the CPM-5 gate).");
        Assert.Equal(1, mux.ActiveIndex);   // the DisplayMultiplexer switched to the Videx (index 1 = the live 80-col terminal)

        // --- (3a) The LC-bank-2-nonzero discriminator (ADR 0018-C Decision C4 -- the tight red->green proof of
        //          the V80-4 fix). apl2cpm3's ?ldccp `LDIR` copies the CP/M-3 CCP into LC bank 2 via the
        //          odd-address bank-2-select write `LD (0E08BH),A`. Under the OLD single-latch LC model that
        //          odd-address write cleared write-enable, so the `LDIR` was dropped and bank 2 stayed ALL ZEROS.
        //          With the two-latch fix write-enable survives the odd write and the copy LANDS (the live trace
        //          saw 3026/4096 nonzero). Assert it is well above noise -- this is 0 under the old model.
        int bank2NonZero = lc.Bank2NonZeroCountForTest();
        Assert.True(bank2NonZero > 100,
            $"expected LC bank 2 to be populated by the ?ldccp CCP `LDIR` copy (the live trace saw 3026/4096); "
          + $"observed {bank2NonZero} nonzero bytes. It is 0 under the old single-latch LC model that cleared "
          + "write-enable on the odd-address bank-2-select write -- the V80-4 two-latch fix (ADR 0018-C) lets it land.");

        // --- (3b) The REAL Videx firmware drove the 80-col console and the boot reached the decoded `A>` CCP
        //          prompt (the headline arbiter, ADR 0018 Decision 4 / ADR 0018-C Decision C4). The firmware
        //          programmed the CRTC for 80x24 and the CP/M-3 sign-on + the `A>` prompt are decoded off the
        //          live Videx $CC00 VRAM. The sign-on remains on screen above the prompt, so the gate proves
        //          BOTH the sign-on AND the `A>` CCP prompt. With the synthetic all-zero firmware this VRAM is
        //          EMPTY (the firmware is load-bearing); under the old single-latch LC model `A>` never appears.
        string videxConsole = DecodeVidexConsole(videx);
        Assert.True(
            videxConsole.Contains("CP/M", StringComparison.OrdinalIgnoreCase),
            $"expected the CP/M-3 sign-on on the Videx 80-col VRAM; decoded console was:\n{videxConsole}");
        Assert.Contains("BIOS", videxConsole, StringComparison.OrdinalIgnoreCase);   // "...56K BIOS R6/89"
        Assert.Contains("TPA", videxConsole, StringComparison.OrdinalIgnoreCase);    // "46K TPA"
        Assert.True(
            videxConsole.Contains("A>", StringComparison.Ordinal),
            $"expected the decoded `A>` CCP prompt on the Videx 80-col VRAM (the un-fakeable arbiter -- "
          + $"ADR 0018 Decision 4 / ADR 0018-C / V80-4); decoded console was:\n{videxConsole}");
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
