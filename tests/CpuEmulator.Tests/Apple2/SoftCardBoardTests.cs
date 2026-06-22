using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardBoardTests
{
    [Fact]
    public void Cpm_sector_order_is_the_documented_data_track_skew()
    {
        // research §5: the canonical CP/M data-track skew (apple-do order).
        int[] expected = [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];
        int[] actual = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Cpm_order_is_a_permutation_distinct_from_dos33_and_prodos()
    {
        int[] cpm = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm);
        // A valid interleave is a permutation of 0..15.
        Assert.Equal(Enumerable.Range(0, 16), cpm.OrderBy(x => x));
        // And it is genuinely a third ordering (distinct from the two shipped tables).
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33), cpm);
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.ProDos), cpm);
    }

    [Fact]
    public void Cpm_skew_is_per_track_boot_table_for_system_tracks_data_table_for_the_rest()
    {
        // ADR 0017 Decision 1 (live-verified): system tracks 0-2 use the BOOT interleave (p*11)%16;
        // data tracks 3-34 use the existing CP/M-logical (apple-do) table. A single all-tracks table
        // was the first, fatal defect (boot2's $0F7D loaded as $00/BRK).
        int[] boot = [0, 11, 6, 1, 12, 7, 2, 13, 8, 3, 14, 9, 4, 15, 10, 5];   // (p*11) mod 16
        int[] data = [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];

        // tracks 0, 1, 2 -> boot table
        Assert.Equal(boot, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 0));
        Assert.Equal(boot, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 1));
        Assert.Equal(boot, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 2));
        // track 3+ -> data table
        Assert.Equal(data, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 3));
        Assert.Equal(data, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 34));

        // The boot table is a genuine 0..15 permutation distinct from the data table.
        Assert.Equal(Enumerable.Range(0, 16), boot.OrderBy(x => x));
        Assert.NotEqual(data, boot);
    }

    [Fact]
    public void Cpm3_order_is_raw_dos33_on_every_track_a_clean_permutation()
    {
        // ADR 0018-A: apl2cpm3's CP/M 3.1 disk must be presented in RAW DOS 3.3 order on EVERY track. Its
        // BOOTLDR `xlt` + the running LDRBIOS `fdxlt` both re-translate logical->physical against the disk's
        // address-field IDs, so a raw (un-pre-skewed) DOS 3.3 presentation composes to identity (live-verified:
        // CPMLDR's `LD SP` lands at Z80 $0100, CPM3.SYS reads byte-exact, the CP/M-3 sign-on renders on the Videx
        // VRAM (the V80-2 milestone); `A>` is blocked by a fifth layer in the banked BDOS/CCP -- see
        // `Apl2Cpm3BootTests` / ADR 0018-B). This
        // is the OPPOSITE of the 2.2 `Cpm` path (which needs the CpmBoot/CpmData pre-skew); Cpm3 is single-skew
        // = the documented DOS 3.3 table for all 35 tracks.
        int[] dos33 = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33);

        // The single-arg overload returns raw DOS 3.3.
        Assert.Equal(dos33, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm3));

        // The (kind, track) overload returns raw DOS 3.3 for the boot tracks (0/1/2), the system/data split
        // boundary (3), and the last track (34) alike -- there is NO per-track split for Cpm3.
        foreach (int track in new[] { 0, 1, 2, 3, 34 })
            Assert.Equal(dos33, Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm3, track));

        // It is a genuine 0..15 permutation (the DOS 3.3 interleave).
        int[] cpm3 = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm3);
        Assert.Equal(Enumerable.Range(0, 16), cpm3.OrderBy(x => x));

        // And it is DISTINCT from the 2.2 CP/M data/boot tables (the un-fakeable proof Cpm3 is its own kind,
        // not aliased onto the 2.2 skew that double-skews apl2cpm3).
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm), cpm3);
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 0), cpm3);   // boot table

        // The 2.2 Cpm tables are byte-for-byte unchanged by the additive Cpm3 kind (no shared-path drift).
        Assert.Equal(new[] { 0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1 },
            Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm));
        Assert.Equal(new[] { 0, 11, 6, 1, 12, 7, 2, 13, 8, 3, 14, 9, 4, 15, 10, 5 },
            Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm, 0));
    }

    [Fact]
    public void Single_skew_orders_ignore_the_track_argument_dos33_and_prodos_unchanged()
    {
        // DOS 3.3 / ProDOS are single-skew: the (kind, track) overload returns the same table for every track,
        // byte-for-byte equal to the legacy single-arg call (the regression guard for the additive overload).
        foreach (SectorOrderKind kind in new[] { SectorOrderKind.Dos33, SectorOrderKind.ProDos })
        {
            int[] legacy = Apple2SectorOrder.PhysicalToLogical(kind);
            Assert.Equal(legacy, Apple2SectorOrder.PhysicalToLogical(kind, 0));
            Assert.Equal(legacy, Apple2SectorOrder.PhysicalToLogical(kind, 3));
            Assert.Equal(legacy, Apple2SectorOrder.PhysicalToLogical(kind, 34));
        }
    }

    [Fact]
    public void DskFluxImage_cpm_uses_the_boot_skew_on_track_0_and_the_data_skew_on_track_3()
    {
        // A 35-track .dsk where each 256-byte sector is filled with a byte == its absolute LBA (mod 256).
        // The synthesized track's 6-and-2 data fields therefore encode WHICH logical sector landed at each
        // physical slot; decoding the first data byte of each physical sector recovers physToLog[phys].
        const int tracks = 35, spt = 16;
        var bytes = new byte[tracks * spt * 256];
        for (int lba = 0; lba < tracks * spt; lba++)
            Array.Fill(bytes, (byte)(lba % 256), lba * 256, 256);
        IBlockDevice block = new DiskImage(bytes, 256, isReadOnly: true);

        var flux = new DskFluxImage(block, SectorOrderKind.Cpm);

        // The boot table for track 0 maps physical 1 -> logical 11; the data table maps physical 1 -> logical 6.
        // Decode physical sector 1's first payload byte on track 0 (boot) and track 3 (data) and assert the
        // logical sector each carries.
        Assert.Equal(11, FirstPayloadLogical(flux, track: 0, phys: 1));   // boot table: (1*11)%16 = 11
        Assert.Equal(6,  FirstPayloadLogical(flux, track: 3, phys: 1));   // data table: 6
    }

    // Decode the LBA byte the synthesized data field carries for (track, phys); the test image fills each
    // sector with (track*16 + logical) % 256, so payload % 16 (for track < 16) recovers `logical`.
    private static int FirstPayloadLogical(DskFluxImage flux, int track, int phys)
    {
        byte[] nibbles = flux.TrackBits(track).ToArray();
        int payload = Apple2SectorDecoder.FirstDataByteOfPhysicalSector(nibbles, phys);  // see Task 2c
        return payload % 16;   // track < 16 in this test, so the low nibble is the logical sector
    }

    private static byte[] DiskBootRom()
    {
        var rom = new byte[Apple2Rom.DiskRomLength];   // 256 B
        rom[0x01] = 0x20; rom[0x03] = 0x00; rom[0x05] = 0x03; rom[0x07] = 0x3C;  // slot-6 signature
        rom[0x00] = 0xA9;
        return rom;
    }

    private static Machine BuildSoftCard(byte[] systemRom, IFluxImage? drive1 = null)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(drive1 ?? new SyntheticFluxImage(trackCount: 35));
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, DiskBootRom());
        return BoardMachineFactory.Build(spec);   // interpreter tier; the coprocessor is always interpreter
    }

    [Fact]
    public void The_softcard_board_builds_a_6502_primary_and_a_dormant_Z80_coprocessor()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000
        Machine machine = BuildSoftCard(rom);

        Assert.NotNull(machine.Coprocessor);          // the Z80 coprocessor is wired (PR-I)
        Assert.False(machine.CoprocessorActive);      // the 6502 is the bus master at reset (Z80 dormant)
    }

    [Fact]
    public void The_softcard_board_carries_a_control_port_named_to_match_the_coprocessor_spec()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        BoardSpec spec = SoftCardBoard.Spec(
            rom, new Apple2Iou(new Apple2VideoState(), new Apple2LanguageCard(rom),
                               new Apple2DiskII(new SyntheticFluxImage(trackCount: 35))),
            new Apple2DiskII(new SyntheticFluxImage(trackCount: 35)), DiskBootRom());

        Assert.NotNull(spec.Coprocessor);
        // The control-port slot's Name must equal the CoprocessorSpec.ControlPortPeripheral (PR-I's
        // copro-control-port-unwired validator contract) — the wiring is self-consistent.
        Assert.Equal(spec.Coprocessor!.ControlPortPeripheral,
            spec.Peripherals.Single(p => p.Name == "softcard").Name);
        Assert.Equal(CpuKind.Z80, spec.Coprocessor.Cpu);
    }

    [Fact]
    public void Slot5_is_the_default_control_port_address_byte_for_byte_unchanged()
    {
        // The shipped 2.2 board MUST be byte-for-byte unchanged: the default slot is 5 ($C500).
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000
        BoardSpec spec = SoftCardBoard.Spec(
            rom, new Apple2Iou(new Apple2VideoState(), new Apple2LanguageCard(rom),
                               new Apple2DiskII(new SyntheticFluxImage(trackCount: 35))),
            new Apple2DiskII(new SyntheticFluxImage(trackCount: 35)), DiskBootRom());

        PeripheralSlot port = spec.Peripherals.Single(p => p.Name == SoftCardBoard.ControlPortName);
        Assert.Equal(SoftCardBoard.ControlPortBaseSlot5, port.Base);   // $C500 -- the shipped default
        Assert.Equal(0xC500u, port.Base);
    }

    [Fact]
    public void Apl2cpm3_board_decodes_the_control_port_at_slot_4_C400()
    {
        // ADR 0018 Decision 1: apl2cpm3 hard-codes STA $C400 (slot 4). The board built with the slot-4 base
        // decodes the control port at $C400, NOT $C500.
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        BoardSpec spec = SoftCardBoard.Spec(
            rom, new Apple2Iou(new Apple2VideoState(), new Apple2LanguageCard(rom),
                               new Apple2DiskII(new SyntheticFluxImage(trackCount: 35))),
            new Apple2DiskII(new SyntheticFluxImage(trackCount: 35)), DiskBootRom(),
            controlPortBase: SoftCardBoard.ControlPortBaseSlot4);

        PeripheralSlot port = spec.Peripherals.Single(p => p.Name == SoftCardBoard.ControlPortName);
        Assert.Equal(SoftCardBoard.ControlPortBaseSlot4, port.Base);   // $C400 -- apl2cpm3's slot
        Assert.Equal(0xC400u, port.Base);

        // The board still validates clean: $C400 (len $100) is fully contained in SpecWithSystem's
        // $C000-$C5FF Mmio region (BoardSpecValidator's slot-not-in-mmio check). Building the machine proves it.
        Machine machine = BoardMachineFactory.Build(spec);
        Assert.NotNull(machine.Coprocessor);
    }

    // ADR 0018 PR-1's un-fakeable slot-placement gate: the slot-4 board's control port toggles ONLY on a
    // $C400 write; the default slot-5 board toggles ONLY on a $C500 write. One slot per board, exclusive
    // (Decision 1 rejects a both-slots decode). A 6502 ROM writes a slot then spins; we assert who toggled.
    [Theory]
    [InlineData(0xC400u, 0xC4, true)]    // slot-4 board, write $C400 -> the Z80 activates
    [InlineData(0xC400u, 0xC5, false)]   // slot-4 board, write $C500 -> NO toggle (empty MMIO hole)
    [InlineData(0xC500u, 0xC5, true)]    // slot-5 (default) board, write $C500 -> the Z80 activates
    [InlineData(0xC500u, 0xC4, false)]   // slot-5 board, write $C400 -> NO toggle
    public void The_control_port_toggles_only_on_a_write_to_its_own_slot(
        uint boardSlot, byte writeHiByte, bool expectActive)
    {
        // A 6502 system ROM: STA $<writeHiByte>00 ; JMP self. Reset vector -> $D000.
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x0000] = 0x8D; rom[0x0001] = 0x00; rom[0x0002] = writeHiByte;   // STA $hh00
        rom[0x0003] = 0x4C; rom[0x0004] = 0x03; rom[0x0005] = 0xD0;          // JMP $D003 (spin)
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;                              // reset -> $D000

        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(rom);
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(rom, iou, disk, DiskBootRom(), controlPortBase: boardSlot);
        Machine machine = BoardMachineFactory.Build(spec);

        machine.Reset();
        Assert.False(machine.CoprocessorActive);   // the 6502 is the bus master at reset
        machine.Run(100);                          // run past the STA + into the spin

        Assert.Equal(expectActive, machine.CoprocessorActive);
    }

    [Fact]
    public void SoftCardCpm_load_rejects_a_wrong_length_image()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"cpm-bad-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(tmp, new byte[1024]);   // not 143,360
        try { Assert.Throws<InvalidDataException>(() => SoftCardCpm.LoadBlockDevice(tmp)); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SoftCardCpm_load_accepts_an_exact_140KiB_image_as_a_256_byte_sector_block_device()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"cpm-ok-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(tmp, new byte[SoftCardCpm.DiskLength]);   // 143,360 = 35*16*256
        try
        {
            IBlockDevice block = SoftCardCpm.LoadBlockDevice(tmp);
            Assert.Equal(256, block.SectorSize);
            Assert.Equal(560, block.SectorCount);   // 35 tracks * 16 sectors
            Assert.True(block.IsReadOnly);
            // And it re-nibblizes onto the shipped DskFluxImage with the CP/M order (the adapter is unchanged).
            var flux = new DskFluxImage(block, SectorOrderKind.Cpm);
            Assert.Equal(35, flux.TrackCount);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SoftCardSurface_constructs_and_renders_a_280x192_frame()
    {
        // A synthetic (all-zero) system ROM + a synthetic CP/M block device: the surface must construct,
        // reset, and produce a 280x192 FB frame (the Apple video tick). No real asset is needed for THIS
        // smoke test — the boot-to-A> assertion is the separate asset-gated test.
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        IBlockDevice cpm = new DiskImage(new byte[SoftCardCpm.DiskLength], 256, isReadOnly: true);
        var bootRom = new byte[Apple2Rom.DiskRomLength];
        bootRom[0x01] = 0x20; bootRom[0x03] = 0x00; bootRom[0x05] = 0x03; bootRom[0x07] = 0x3C;
        bootRom[0x00] = 0xA9;

        byte[]? lastFrame = null;
        CpuEmulator.Surface.Web.SoftCardSurface surface =
            CpuEmulator.Surface.Web.SoftCardSurface.Create(rom, bootRom, charRom: null,
                cpmDisk: cpm, f => lastFrame = f, _ => { });

        surface.Host.RunHeadless(totalCycles: 40_000, sliceCycles: 17_030);

        Assert.NotNull(lastFrame);
        Assert.Equal((byte)'F', lastFrame![0]);
        Assert.Equal((byte)'B', lastFrame[1]);
        int width = lastFrame[4] | (lastFrame[5] << 8);
        int height = lastFrame[6] | (lastFrame[7] << 8);
        Assert.Equal(280, width);
        Assert.Equal(192, height);
        Assert.NotNull(surface.Machine.Coprocessor);   // the Z80 is wired even on the synthetic board
    }

    // Generous budget for the CP/M cold boot (tuned on the first green run with the real asset).
    private const long CpmBootCycles = 10_000_000;

    // ADR 0017 PR-1 (CPM-1): with the per-track skew (Task 1/2), boot2's $0F7D is a VALID opcode -> the 6502
    // no longer BRKs into the monitor. PR-1 alone does NOT reach A> (that needs the control-port open-bus fix
    // [CPM-2] + the run-loop yield [CPM-3]); this gate asserts the NEGATIVE -- the SKEW CRASH is gone -- so
    // main is green/honest without false-passing on the incomplete boot. The full A> assertion lands in CPM-4.
    //
    // DRIFT FROM THE PLAN (live-verified, grounded in ADR 0017 §root-cause step 6): the plan's literal
    // predicates (DoesNotContain('*') / DoesNotContain("CAN'T FIND")) are WRONG against ground truth. Both the
    // PRE-fix and POST-fix screens end at a monitor '*' prompt, and `CAN'T FIND Z80 SOFTCARD` is precisely the
    // POST-fix signal that the skew crash cleared and the boot ADVANCED to the SoftCard-detect handshake --
    // which then livelocks on the CPM-2 read-toggle defect (NOT fixed in this PR). So '*' can't discriminate,
    // and asserting the absence of "CAN'T FIND" would be backwards (it would fail forever in CPM-1, red main).
    // The honest un-fakeable discriminator is the SKEW-CRASH SIGNATURE itself: the Apple Monitor BRK register
    // dump at the $0F7D region (live PRE-fix: "0F7F-    A=00 X=60 Y=0B P=37 S=F7"). It is present ONLY when
    // boot2 BRKed at $0F7D (the skew bug); it is absent once $0F7D is a valid opcode. This row FAILS pre-fix
    // (register dump present) and PASSES post-fix (register dump gone) -- the load-bearing Task 3b proof.
    [SoftCardCpmFact]
    public void Cpm_boot_clears_the_per_track_skew_crash_no_brk_to_monitor_register_dump()
    {
        string[] screen = DecodeBootScreen();

        // (1) NOT the $0F7D skew-crash BRK: the Apple Monitor's BRK handler prints a register dump line
        //     "<addr>-    A=hh X=hh Y=hh P=hh S=hh". Pre-fix this is "0F7F-..." right after boot2's
        //     "JSR $0F7D" hits the $00/BRK byte. We assert NO such register-dump row exists (the skew crash
        //     is gone). A row is the monitor register dump iff it carries the A=/X=/P=/S= register fields.
        Assert.DoesNotContain(screen, IsMonitorRegisterDump);
        // (2) The boot ran a real text screen, not all-blank garbage: at least one printable cell. (Post-fix
        //     the screen carries the ADR-documented CPM-2 "CAN'T FIND Z80 SOFTCARD" detect-failure line, which
        //     is EXPECTED here -- it proves the boot advanced past the skew crash to the detect handshake.)
        Assert.Contains(screen, row => row.Any(ch => ch != ' '));
    }

    /// <summary>True iff <paramref name="row"/> is the Apple Monitor's BRK register-dump line
    /// (".... A=hh X=hh Y=hh P=hh S=hh") -- the on-screen signature of a crash into the monitor. boot2's
    /// $0F7D skew-crash BRK lands here; a clean (skew-fixed) boot never shows it. Matches on the four
    /// register-field tags together so an ordinary "A=" in text can't false-trip it.</summary>
    private static bool IsMonitorRegisterDump(string row) =>
        row.Contains("A=") && row.Contains("X=") && row.Contains("P=") && row.Contains("S=");

    // ADR 0017 PR-2 (CPM-2): the control-port Read() is now open-bus with NO toggle (SoftCardControlPort.cs).
    // The un-fakeable proof of that change is the PORT-LEVEL gate in SoftCardControlPortTests
    // (A_read_is_open_bus_and_does_NOT_toggle_the_active_cpu + Reads_interleaved_with_writes_only_count_the_writes):
    // a read -- even 1000 reads -- fires 0 toggles; a write fires exactly 1. That test FAILS with the old
    // read-toggle and PASSES with the open-bus Read, so it fully discriminates CPM-2.
    //
    // The LIVE decoded-text gate the plan envisioned (assert "CAN'T FIND Z80 SOFTCARD" disappears after the
    // open-bus Read) is DEFERRED to CPM-3, because on THIS build it is NOT discriminating. Live-verified on the
    // cached softcard-cpm.dsk at the CpmBootCycles budget (the arbiter the plan §Drift 1 / Task 2c names):
    //
    //   single Run(CpmBootCycles):  read-toggle -> finalActive=True, screen row 19 "CAN'T FIND Z80 SOFTCARD",
    //                                              row 23 monitor "*" prompt
    //                               open-bus    -> finalActive=True, screen row 19 "CAN'T FIND Z80 SOFTCARD",
    //                                              row 23 monitor "*" prompt   (BYTE-IDENTICAL)
    //
    // i.e. with CPM-2 ALONE the boot still reaches the detect, still prints CAN'T FIND, and the Z80 is still the
    // bus master at the end -- the read-toggle vs open-bus difference is NOT observable on the decoded screen at
    // this stage. (This is exactly what the merged CPM-1 gate already documents: CAN'T FIND is the post-CPM-1
    // state that "then livelocks on the CPM-2 read-toggle defect" and clears only once CPM-3's run-loop yield
    // stabilizes the handshake.) A decoded-text gate here would be a FALSE PASS -- it cannot fail with the
    // read-toggle restored -- which the plan forbids. So the decoded "CAN'T FIND"-gone assertion moves to CPM-3,
    // kept visible and un-fakeable as this named-skip until the yield lands.
    [Fact(Skip = "CPM-2's decoded-text effect is not live-observable until CPM-3's run-loop yield clears the " +
        "detect livelock; the un-fakeable CPM-2 proof is the port-level read/write-toggle asymmetry test. " +
        "(ADR 0017 PR-2; live-verified -- CAN'T FIND is byte-identical with read-toggle vs open-bus here.)")]
    public void Cpm_boot_passes_the_softcard_detect_no_cant_find_message()
    {
        // CPM-3 replaces this body with the decoded "CAN'T FIND"-gone negative + the z80-handshake-stable
        // assertion, once the run-loop yield makes the detect non-fatal. Kept named so the gate is visible and
        // un-fakeable when it lands -- never a silent placeholder pass.
    }

    [SoftCardCpmFact]
    public void Cpm_boot_runs_the_z80_bios_at_Axxx_stably_after_the_run_loop_yield()
    {
        // ADR 0017 PR-3: with the per-track skew (CPM-1) + open-bus Read (CPM-2) + the run-loop yield (CPM-3),
        // the Z80 executes real CP/M BIOS code in the $Axxx region and -- crucially -- does NOT collapse back to
        // its $0000 reset stub once it gets there (the instability the whole-slice Run caused). We sample the
        // Z80 PC over the boot and assert it reached $A000-$AFFF and that, in the LATER boot window, it is no
        // longer stuck at the reset stub ($0000-$00FF).
        var (systemRomPath, cpmDiskPath) = SoftCardCpmVectors.TryGetAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the CP/M boot gate");
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(cpmDiskPath);

        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
        Machine machine = BoardMachineFactory.Build(spec);

        machine.Reset();
        bool reachedAxxx = false;
        bool lateInResetStub = false;
        const long slice = 50_000;
        long lateThreshold = CpmBootCycles * 3 / 4;   // the last quarter of the boot is the "stable" window
        for (long run = 0; run < CpmBootCycles; run += slice)
        {
            machine.Run(slice);
            if (machine.Coprocessor is { } z80 && machine.CoprocessorActive)
            {
                ulong pc = z80.GetRegister("PC");            // Z80Spec.cs:47 — the program-counter register
                if (pc is >= 0xA000 and <= 0xAFFF) reachedAxxx = true;
                if (run >= lateThreshold && pc <= 0x00FF) lateInResetStub = true;
            }
        }

        Assert.True(reachedAxxx, "expected the Z80 to execute CP/M BIOS code in the $Axxx region during the boot");
        Assert.False(lateInResetStub,
            "the Z80 fell back to its $0000 reset stub late in the boot -- the run-loop yield did not stabilise " +
            "the BIOS handshake");
    }

    // ADR 0017 PR-4 (CPM-4) -- THE HEADLINE DELIVERABLE. With CPM-1 (per-track skew) + CPM-2 (open-bus Read) +
    // CPM-3 (run-loop yield) all landed, the live triage shows outcome (1) of the plan's Decision-4 hypothesis:
    // CONOUT already reaches the 40-col screen and the real disk boots to A> with NO further production change
    // (fixes 1-3 were the complete gating set; no $1010 bridge change needed). The decoded text page is stable
    // and byte-identical from ~5M cycles through 40M cycles -- well settled before the CpmBootCycles budget --
    // so the frame is deterministic. This gate is the un-fakeable A> oracle (Decision 5): the decoded console
    // text MUST carry "A>" (the CCP prompt) + a CP/M sign-on line, the Z80 MUST be the active bus master, and
    // the rendered 40-col frame MUST match the committed SHA-256.
    [SoftCardCpmFact]
    public void Cpm_boots_to_the_A_prompt_on_the_interpreter()
    {
        var (systemRomPath, cpmDiskPath) = SoftCardCpmVectors.TryGetAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the CP/M boot gate");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();   // null -> Apple2Font.Fallback (still renders A>)
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(cpmDiskPath);

        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
        Machine machine = BoardMachineFactory.Build(spec);   // interpreter tier (coprocessor is interpreter)
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state, charRom);

        machine.Reset();
        machine.Run(CpmBootCycles);                          // the real $C600 -> tracks -> $CnXX -> CP/M boot

        // --- (1) The un-fakeable content oracle: decode the 40-col text page and assert the CP/M prompt/sign-on.
        string[] screen = DecodeTextScreen(machine);
        string joined = string.Join("\n", screen);
        Assert.Contains("A>", joined);   // the CCP prompt -- the headline target

        // At least one of the disk's known sign-on lines also paints at cold boot (belt-and-braces; the disk's
        // own ASCII pins these -- ADR 0017 Decision 5 / §1). The cached disk signs on as Microsoft's
        // "APPLE ][ CP/M 44K VER. 2.20B / (C) 1980 MICROSOFT", so the CP/M arm is what passes here.
        Assert.True(
            joined.Contains("CP/M") || joined.Contains("DIGITAL RESEARCH"),
            $"expected a CP/M sign-on line on the console; decoded screen was:\n{joined}");

        // --- (2) The Z80 ran: it became the bus master during the boot (the $CnXX handoff fired).
        Assert.True(machine.CoprocessorActive,
            "expected the Z80 to be the active bus master after the CP/M boot handoff");

        // --- (3) The committed frame hash (a TIGHTENING gate, captured on the first green run -- NOT the primary
        //         assertion; the text substring above is the oracle). Render + hash the 40-col frame.
        var rgba = new uint[Apple2Video.Width280 * Apple2Video.Height192];
        video.RenderInto(rgba);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AsBytes(rgba)));
        // Captured 2026-06-21 on the first green run, byte-identical across 3 consecutive runs (the interpreter
        // tier is deterministic and the screen is fully settled well before the CpmBootCycles budget).
        const string ExpectedBootHash = "89D10D1532F3A1DEA21C8497B774DA40F83B8CBF0C8842AB763B8A22CC3E0952";
        Assert.Equal(ExpectedBootHash, hash);
    }

    /// <summary>Build the real SoftCard machine over the cached CP/M .dsk, run the cold boot, and decode the
    /// 24x40 Apple text page ($0400) to ASCII (high "normal-video" bit stripped) -- the same TextRowBase walk
    /// BootProbe uses. Returns 24 rows of 40 chars.</summary>
    private static string[] DecodeBootScreen()
    {
        var (systemRomPath, cpmDiskPath) = SoftCardCpmVectors.TryGetAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the CP/M boot gate");
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(cpmDiskPath);

        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
        Machine machine = BoardMachineFactory.Build(spec);   // interpreter tier (coprocessor is interpreter)

        machine.Reset();
        machine.Run(CpmBootCycles);                          // the real $C600 -> tracks -> $CnXX boot

        return DecodeTextScreen(machine);
    }

    /// <summary>Decode the live 24x40 Apple text page ($0400, page 1) of <paramref name="machine"/> to ASCII --
    /// the same TextRowBase walk BootProbe uses, stripping the normal-video high bit. Non-printable cells become
    /// spaces. Returns 24 rows of 40 chars.</summary>
    private static string[] DecodeTextScreen(Machine machine)
    {
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
        var rows = new string[24];
        for (int r = 0; r < 24; r++)
        {
            uint rowBase = Apple2HiResAddress.TextRowBase(r, page2: false);
            var sb = new System.Text.StringBuilder(40);
            for (int c = 0; c < 40; c++)
            {
                int g = bus.Read8(rowBase + (uint)c) & 0x7F;   // strip the normal-video high bit
                sb.Append(g is >= 0x20 and <= 0x7E ? (char)g : ' ');
            }
            rows[r] = sb.ToString();
        }
        return rows;
    }

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
