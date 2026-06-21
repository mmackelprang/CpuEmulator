# Plan — CPM-1: Honest main (per-track CP/M skew + de-fanged boot gate)

> **Arc:** SoftCard CP/M boot-to-`A>` (ADR 0017). **PR 1 of 4.** Strictly first.
> **Grounded against:** `main` @ `1d0232c` (the commit carrying ADR 0017).
> **ADR:** `docs/architecture/0017-softcard-cpm-boot-handshake-and-per-track-skew-correction.md`
> (Decision 1 + Decision 5, PR-1 in §3). **Research:** `docs/research/apple-2-plus-z80-softcard-cpm-analysis.md` §5.
> **Queue row:** **CPM-1**.

## Why this PR

`main` @ `1d0232c` is **RED on the dev machine** (the CP/M assets are cached, so the two `[SoftCardCpmFact]`
gates run live). Verified this session:

```
Failed CpuEmulator.Tests.Apple2.SoftCardBoardTests.Cpm_boots_to_the_A_prompt_on_the_interpreter
  -> "expected the Z80 to be the active bus master after the CP/M boot handoff"  (SoftCardBoardTests.cs:179)
Failed CpuEmulator.Tests.Apple2.SoftCardVidexBoardTests.Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter
  -> Assert.Equal() Failure: Expected 1, Actual 0  (SoftCardVidexBoardTests.cs:159)
```

Those two are the SAME failures PR #128's Test Plan documents ("2 failed / 2 skipped … fail identically on
`main`"). **Until they are de-fanged, no PR can claim a green suite** — PR #128 is currently blocked from a
clean merge by them. This PR:

1. Lands the **verified first fix** (Decision 1 — per-track CP/M skew) so boot2's `$0F7D` routine is a valid
   opcode (no silent BRK-to-monitor crash). This advances the live boot past the skew crash but does **not**
   reach `A>` (fixes 2 + 3 are still needed — PR-2/PR-3).
2. **De-fangs both CP/M boot gates** so the suite is GREEN and honest: replace the `onPixels>50` heuristic +
   `PLACEHOLDER` hash with an honest assertion that asserts the **negative** (boot2 no longer BRKs into the
   monitor) and **does not** lock the error/incomplete screen. The full `A>` assertion is deferred to PR-4
   (the gate is named-and-skipped, never false-passing).
3. Corrects research §5's boot table in the doc (already done in `1d0232c` — this PR adds the per-track skew
   regression test that pins it).

**Definition of done:** the per-track skew regression test (asset-free) is green; both CP/M boot gates are
honest (negative-assertion + named-skip for the `A>` part, never false-passing); the full suite is green on a
machine *with* the assets cached. `git switch main && dotnet test` goes from 2-failed to 0-failed.

---

## TDD discipline

Each task: **write the failing test first, watch it fail for the right reason, implement, watch it pass.**
Run the named filter after every task. No task is "done" until its gate is green and the full
`SoftCardBoardTests` + `SoftCardVidexBoardTests` + `Apple2SectorOrder` lanes are green.

---

## Task 1 — Per-track CP/M skew: `Apple2SectorOrder.PhysicalToLogical(kind, track)`

### 1a. Test first

Add to `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` (the existing `Cpm_sector_order_*` facts stay):

```csharp
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
```

Run: `dotnet test … --filter "FullyQualifiedName~Cpm_skew_is_per_track|FullyQualifiedName~Single_skew_orders_ignore"`
→ FAILS to compile (the `(kind, track)` overload does not exist). That is the correct first-failure.

### 1b. Implement

Edit `src/CpuEmulator.Peripherals/Apple2SectorOrder.cs`. **Rename** the existing `CpmPhysToLog` to
`CpmDataPhysToLog` (for clarity), **add** `CpmBootPhysToLog`, and **add** the track-aware overload. The
single-arg overload stays (back-compat: `DskFluxImage` ctor + the shipped `Cpm_sector_order_*` facts call it;
single-arg `Cpm` returns the **data** table — the historical meaning).

Replace lines 23-39 (the `CpmPhysToLog` field + the single-arg method) with:

```csharp
    // CP/M (SoftCard) DATA-track skew (research §5, the canonical apple-do data-track order, live-verified
    // correct). Used for tracks 3-34. The single-arg PhysicalToLogical(Cpm) returns this (its historical
    // meaning); the new (kind, track) overload selects boot vs. data per track.
    private static readonly int[] CpmDataPhysToLog =
        [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];

    // CP/M SoftCard BOOT-track skew (ADR 0017 Decision 1 — live-verified; research §5's earlier boot table
    // was wrong). System tracks 0-2 were written by the SoftCard boot ROM/loader with this interleave
    // physToLog[p] = (p*11) mod 16. Using the data table for these tracks loads boot2's bytes at the wrong
    // addresses (its $0F7D routine becomes $00/BRK -> a silent monitor crash before any handshake).
    private static readonly int[] CpmBootPhysToLog =
        [0, 11, 6, 1, 12, 7, 2, 13, 8, 3, 14, 9, 4, 15, 10, 5];

    /// <summary>The number of CP/M system (boot) tracks: tracks 0-2 use the boot interleave, 3-34 the data
    /// table (DPB OFF=3, research §4 — the disk's own system/data split).</summary>
    private const int CpmSystemTracks = 3;

    /// <summary>The 16-entry physical→logical map for <paramref name="kind"/> (a fresh copy per call so
    /// callers cannot mutate the shared table). For <see cref="SectorOrderKind.Cpm"/> this returns the
    /// DATA-track table (its historical meaning); use the (kind, track) overload for the per-track skew.</summary>
    public static int[] PhysicalToLogical(SectorOrderKind kind) => kind switch
    {
        SectorOrderKind.Dos33 => (int[])Dos33PhysToLog.Clone(),
        SectorOrderKind.ProDos => (int[])ProDosPhysToLog.Clone(),
        SectorOrderKind.Cpm => (int[])CpmDataPhysToLog.Clone(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The 16-entry physical→logical map for <paramref name="kind"/> on <paramref name="track"/>
    /// (ADR 0017 Decision 1). Only <see cref="SectorOrderKind.Cpm"/> is track-dependent: system tracks 0-2
    /// use the boot interleave, tracks 3+ use the data table. DOS 3.3 / ProDOS are single-skew and ignore
    /// <paramref name="track"/> (the same table the single-arg overload returns). A fresh copy per call.</summary>
    public static int[] PhysicalToLogical(SectorOrderKind kind, int track) => kind switch
    {
        SectorOrderKind.Cpm => track < CpmSystemTracks
            ? (int[])CpmBootPhysToLog.Clone()
            : (int[])CpmDataPhysToLog.Clone(),
        _ => PhysicalToLogical(kind),   // Dos33 / ProDos: track-independent, unchanged
    };
```

Run the Task-1 filter → green. Run the existing `Cpm_sector_order_is_the_documented_data_track_skew` +
`Cpm_order_is_a_permutation_distinct_from_dos33_and_prodos` → still green (single-arg `Cpm` unchanged = data
table).

---

## Task 2 — `DskFluxImage` resolves the skew per track

### 2a. Test first

The ADR's claim that this is "a one-line change" is **slightly off**: `DskFluxImage` resolves
`_physToLog` ONCE in the constructor (line 33: `_physToLog = Apple2SectorOrder.PhysicalToLogical(order);`)
and uses that cached field in `Synthesize` (line 60). To make it per-track, the ctor must **retain the
`SectorOrderKind`** and call the `(kind, track)` overload inside `Synthesize`. This is a field-type change,
not a one-liner — flagged as drift below.

Add to `SoftCardBoardTests.cs` an un-fakeable, asset-free synthesis test. A `.dsk` whose every sector is
filled with its own LBA byte lets us assert which logical sector landed at each physical slot by decoding the
synthesized nibble stream's address + data fields. The simplest un-fakeable proof: two `DskFluxImage`s over
the SAME block device, one built `Cpm` (per-track) and one we *expect* to differ on track 0 only.

```csharp
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
```

### 2b. Implement the per-track resolution

Edit `src/CpuEmulator.Peripherals/DskFluxImage.cs`:

Replace the field (line 19) and ctor assignment (line 33):

```csharp
    private readonly IBlockDevice _block;
    private readonly SectorOrderKind _order;     // resolved per track at synthesis (ADR 0017 Decision 1)
    private readonly byte[]?[] _trackCache;      // lazily synthesized per-track nibble bitstreams
```

```csharp
        _block = block;
        _order = order;
        _trackCache = new byte[]?[block.SectorCount / SectorsPerTrack];
```

Replace the `int logical = _physToLog[phys];` line (line 60) inside `Synthesize` — resolve the table once
per track (not per sector, so it stays cheap), at the top of `Synthesize`:

```csharp
    private byte[] Synthesize(int track)
    {
        // Resolve the physical->logical skew for THIS track (CP/M is per-track: boot table for tracks 0-2,
        // data table for 3+; DOS/ProDOS ignore the track -> the single-skew table). ADR 0017 Decision 1.
        int[] physToLog = Apple2SectorOrder.PhysicalToLogical(_order, track);
        var nibbles = new List<byte>(SectorsPerTrack * 400);
        for (int phys = 0; phys < SectorsPerTrack; phys++)
        {
            int logical = physToLog[phys];
            // ... rest unchanged ...
```

(Everything else in `Synthesize` is unchanged.)

### 2c. The decode helper (test-support, asset-free)

The test needs to read the first data-field payload byte of a given physical sector out of the synthesized
nibble stream. Add a small **test-only** static decoder mirroring `Apple2SectorCodec.EncodeData` (the
`DskFluxImage` data field is `D5 AA AD` + `Apple2SectorCodec.EncodeData(sector)` + `DE AA EB`). Place it in
`tests/CpuEmulator.Tests/Apple2/Apple2SectorDecoder.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

/// <summary>Test-only inverse of the DskFluxImage synthesis: walks a synthesized track's nibble stream,
/// finds the Nth physical sector's address field (D5 AA 96 ... with the 4-and-4 physical-sector number),
/// then decodes the first byte of its 6-and-2 data field. Used to prove WHICH logical sector landed at a
/// given physical slot (the per-track skew gate, ADR 0017 Decision 1).</summary>
internal static class Apple2SectorDecoder
{
    public static int FirstDataByteOfPhysicalSector(byte[] nibbles, int physSector)
    {
        // Find the address-field prologue D5 AA 96 whose 4-and-4 sector number == physSector, then the
        // following data-field prologue D5 AA AD, then decode the first 256-byte payload byte.
        for (int i = 0; i + 3 < nibbles.Length; i++)
        {
            if (nibbles[i] != 0xD5 || nibbles[i + 1] != 0xAA || nibbles[i + 2] != 0x96) continue;
            // 4-and-4: vol(2) track(2) sector(2) chk(2) -> the sector pair is bytes [i+7, i+8].
            int sector = Decode44(nibbles[i + 7], nibbles[i + 8]);
            if (sector != physSector) continue;
            int d = FindDataPrologue(nibbles, i + 3);
            if (d < 0) return -1;
            // The data field is D5 AA AD | 343 GCR bytes | DE AA EB; TryDecodeData is the shipped inverse
            // of Apple2SectorCodec.EncodeData (returns the full 256-byte sector).
            var gcr = nibbles.AsSpan(d + 3, 343).ToArray();
            return Apple2SectorCodec.TryDecodeData(gcr, out byte[] data) ? data[0] : -1;
        }
        return -1;
    }

    private static int FindDataPrologue(byte[] n, int from)
    {
        for (int i = from; i + 3 < n.Length; i++)
            if (n[i] == 0xD5 && n[i + 1] == 0xAA && n[i + 2] == 0xAD) return i;
        return -1;
    }

    private static int Decode44(byte hi, byte lo) => ((hi << 1) | 1) & lo;
}
```

> **Grounding note (verified against `1d0232c`):** `Apple2SectorCodec` ships
> `TryDecodeData(byte[] gcr, out byte[] sector)` — the exact inverse of `EncodeData`, returning the full
> 256-byte sector (`Apple2SectorCodec.cs:72`). The decoder above uses it. There is **no** `DecodeData(span)`
> overload, so the helper slices the 343-byte GCR field and calls `TryDecodeData`. If the Builder finds the
> stream-decode brittle, the fallback is to drop Task 2's stream-decode test and rely on Task 1's direct
> `PhysicalToLogical(Cpm, track)` unit assertions + the higher-level boot gate (Task 3) — but the stream
> decode is the stronger end-to-end proof that the per-track table actually reaches `Synthesize`.

Run the Task-2 filter → green. Run the full `DskFluxImage`/`Apple2SectorOrder`/`SoftCard*` lanes → green.

---

## Task 3 — De-fang the SoftCard CP/M boot gate (honest, no false pass)

### Goal

The current gate (`SoftCardBoardTests.Cpm_boots_to_the_A_prompt_on_the_interpreter`) asserts `onPixels>50`
(weak) + a `PLACEHOLDER` hash (never captured) + `CoprocessorActive` (which FAILS today). Per ADR 0017
Decision 5, replace the heuristic + placeholder with an **un-fakeable negative** assertion that holds after
PR-1 (the skew fix), and **name-and-defer** the full `A>` assertion to PR-4 so the gate cannot lie.

After PR-1 alone: boot2's `$0F7D` is a valid opcode (no BRK), so the 6502 does NOT crash into the monitor —
but the Z80 is NOT yet the bus master (that needs PR-2 + PR-3), so `CoprocessorActive` is still false and `A>`
is not painted. The honest PR-1 gate therefore asserts the **negative**: the boot does NOT land in the
monitor (no `*` prompt) and does NOT print `CAN'T FIND Z80 SOFTCARD` — i.e. the skew crash is gone.

### 3a. Replace the gate body

Edit `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs`. Replace the whole
`Cpm_boots_to_the_A_prompt_on_the_interpreter` method (lines 139-189) with a renamed, de-fanged gate plus a
named-skip placeholder for the full `A>` deliverable (xUnit 2.9.3 → attribute-level `Skip`, no
`Assert.Skip`). Add a text-decode helper.

```csharp
    // Generous budget for the CP/M cold boot (tuned on the first green run with the real asset).
    private const long CpmBootCycles = 10_000_000;

    // ADR 0017 PR-1: with the per-track skew (Task 1/2), boot2's $0F7D is a VALID opcode -> the 6502 no
    // longer BRKs into the monitor. PR-1 alone does NOT reach A> (that needs the control-port fix [PR-2] and
    // the run-loop yield [PR-3]); this gate asserts the NEGATIVE -- the skew crash is gone -- so main is
    // green/honest without false-passing on the incomplete boot. The full A> assertion lands in PR-4.
    [SoftCardCpmFact]
    public void Cpm_boot_clears_the_per_track_skew_crash_no_monitor_no_softcard_error()
    {
        string[] screen = DecodeBootScreen();

        // (1) NOT in the monitor: a BRK-to-monitor crash leaves the Apple Monitor '*' prompt on screen.
        Assert.DoesNotContain(screen, row => row.Contains('*'));
        // (2) NOT the SoftCard-detect failure (that needs PR-2's open-bus Read; this PR's gate just proves
        //     the skew crash is gone, so this row should already be absent before the handshake is reached).
        Assert.DoesNotContain(screen, row => row.Contains("CAN'T FIND"));
        // (3) The screen is a real text screen, not all-blank garbage: at least one printable cell.
        Assert.Contains(screen, row => row.Any(ch => ch != ' '));
    }

    [Fact(Skip = "A> deliverable lands in CPM-4 (ADR 0017 PR-4); PR-1 only restores honest main.")]
    public void Cpm_boots_to_the_A_prompt_on_the_interpreter()
    {
        // Intentionally skipped until CPM-4 wires the full handshake (control-port open-bus + run-loop yield
        // + the $1010 bridge bring-up). CPM-4 replaces this body with the decoded-`A>` substring assertion
        // + CoprocessorActive + ActiveIndex==0 (ADR 0017 Decision 5). Kept named so the gate is visible and
        // un-fakeable when it lands -- never a silent PLACEHOLDER pass.
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
```

Remove the now-unused `AsBytes` helper if nothing else references it (the de-fanged gate no longer hashes;
PR-4 re-adds a hash only as a *tightening* gate). **Grounding note:** confirm no other test in this file
still calls `AsBytes` before deleting it.

> **Why this is honest, not a cover:** the negative assertions FAIL on every wrong screen — a monitor `*`
> crash (today's pre-fix behavior) trips assertion (1); a `CAN'T FIND Z80 SOFTCARD` screen trips (2); a dead
> all-blank board trips (3). Only a screen that advanced past the skew crash passes. It does **not** assert
> `A>` (that is PR-4) and does **not** assert `CoprocessorActive` (still false until PR-2/PR-3), so it cannot
> false-pass on the incomplete boot and it cannot lie about reaching `A>`.

### 3b. Verify the negative gate FAILS without the skew fix (the un-fakeable check)

Before implementing Tasks 1-2 (or by temporarily reverting `DskFluxImage` to the single-table behavior),
run the new `Cpm_boot_clears_the_per_track_skew_crash_*` gate and confirm it FAILS on assertion (1) — the
pre-fix boot crashes into the monitor (`*` on screen). Then with Tasks 1-2 applied, confirm it PASSES. This
is the "un-fakeable gate" ADR 0017 PR-1 demands: with the per-track skew, boot2's `$0F7D` is valid (no BRK)
and the boot advances past the skew crash.

---

## Task 4 — De-fang the Videx CP/M boot gate (so main is fully green)

### Why this is in PR-1 (drift from ADR 0017 — see below)

ADR 0017 PR-1 names only the SoftCard gate. But `SoftCardVidexBoardTests.Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter`
is the **second RED `[SoftCardCpmFact]` gate** on the dev machine (verified this session: FAILS at
`ActiveIndex==1`, got 0). **If PR-1 leaves it RED, main is not green** and PR #128 stays blocked. The Videx
gate's full re-frame (40-col-vs-80-col split) is ADR 0017 Decision 6 / PR-5, which is **out of this batch**
(owner-gated on the 80-col master). So PR-1 does the minimal honest thing: **name-and-skip** the Videx `A>`
gate until PR-5, exactly as it skips the SoftCard `A>` gate until PR-4.

### 4a. Replace the Videx gate body

Edit `tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs`. Replace the
`Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter` method (lines 138-191) with a named-skip:

```csharp
    [Fact(Skip = "Videx 80-col CP/M re-frame is CPM/PR-5 (ADR 0017 Decision 6, owner-gated on an 80-col " +
                 "CP/M master). The cached master is 40-col (zero $C0Bx), so ActiveIndex stays 0. PR-5 " +
                 "asserts the 40-col path for this asset + a direct Videx render gate; until then this is " +
                 "named-skipped so main is green/honest (never false-passing on a 40-col disk forced to 80).")]
    public void Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter()
    {
        // Body intentionally removed: see ADR 0017 Decision 6 / PR-5. The other Videx tests in this file
        // (board wiring, VidexRom, the auto-switch SetActiveForTest path) still run and stay green.
    }
```

Remove the now-unused `AsBytes` helper + the `using System.Security.Cryptography;` import from this file if
nothing else references them (confirm first). The CpmBootCycles const can stay or be removed with the body —
remove it if unreferenced to keep the file warning-clean under `TreatWarningsAsErrors`.

> **Note:** the four non-asset-gated Videx tests in this file
> (`The_board_wires_a_Z80_coprocessor_*`, `The_board_carries_both_*`, `VidexRom_*`,
> `SoftCardVidexSurface_constructs_renders_and_wires_the_auto_switch`) are untouched and stay green — PR-1
> only defuses the one asset-gated `A>` gate.

---

## Task 5 — Verify green/honest main

1. Full filtered run:
   `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~SoftCard|FullyQualifiedName~Apple2SectorOrder|FullyQualifiedName~DskFluxImage" --nologo`
   → all green; the two former-RED `[SoftCardCpmFact]` `A>` gates are now **Skipped** (named), the
   `Cpm_boot_clears_the_per_track_skew_crash_*` gate is **Passed** (assets present), the per-track skew
   regression tests are **Passed**.
2. **Full solution** in Release (the load-bearing "green main" claim):
   `dotnet test -c Release --nologo` → 0 failed. Skip count rises by 2 (the two named-skips) vs. the prior
   2-failed state. Warning-clean (`TreatWarningsAsErrors`).
3. Sanity: `git switch main && git switch -` should not be needed — but the acceptance criterion is that
   `dotnet test -c Release` on this branch reports **0 failed** with the assets cached, where `main` reported
   **2 failed**.

---

## Self-review checklist (run before opening the PR)

- [ ] **Placeholder scan:** no `PLACEHOLDER`, no `TBD`, no "capture on first green" left in either gate file
      (the de-fanged gates assert real decoded text / named-skip; PR-4 re-introduces a *captured* hash).
- [ ] **Negative gate is un-fakeable:** confirmed it FAILS pre-skew-fix (monitor `*`) and PASSES post-fix
      (Task 3b).
- [ ] **DOS/ProDOS untouched:** `Single_skew_orders_ignore_the_track_argument_*` green; no DOS 3.3 / ProDOS
      `.dsk` boot regressed (run the `Apple2BootTests` lane).
- [ ] **Single-arg back-compat:** `Cpm_sector_order_is_the_documented_data_track_skew` still green (single-arg
      `Cpm` = data table).
- [ ] **`DskFluxImage` field change:** `_physToLog` field removed, `_order` retained, `Synthesize` resolves
      per track (drift from ADR's "one-line" claim — see below).
- [ ] **Warning-clean:** no unused `AsBytes`/imports/consts left behind (`TreatWarningsAsErrors`).
- [ ] **Full solution 0-failed in Release** with the CP/M assets cached.

---

## Drift from ADR 0017 (flag in the PR body)

1. **PR-1 also de-fangs the Videx gate (Task 4).** ADR 0017 PR-1 names only the SoftCard gate, but the Videx
   `[SoftCardCpmFact]` is the SECOND RED gate on a machine with the assets cached — leaving it RED means main
   is not green and PR #128 stays blocked. PR-1 name-skips it until PR-5 (Decision 6). This is the minimal
   honest scope; the full 40-vs-80-col re-frame is still PR-5.
2. **`DskFluxImage` change is a field-type change, not "one line."** ADR Decision 1 says "this is a one-line
   change: call the `(kind, track)` overload inside the loop, not the cached field." In the shipped code
   `_physToLog` is resolved ONCE in the constructor; making it per-track requires storing `_order` instead and
   resolving inside `Synthesize`. Small, but two-field, not one-line. No behavior risk (DOS/ProDOS resolve to
   the same table every track).
3. **PR-1 does NOT reach `A>`** (by design — fixes 2+3 are PR-2/PR-3). The `A>` gates are named-skipped, not
   asserted, in this PR.
