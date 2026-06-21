# Plan — CPM-2: `SoftCardControlPort.Read()` open-bus (no toggle on read)

> **Arc:** SoftCard CP/M boot-to-`A>` (ADR 0017). **PR 2 of 4.** Depends on CPM-1.
> **Grounded against:** `main` @ `1d0232c` + CPM-1 landed.
> **ADR:** Decision 2 (amends ADR 0015 Decision 3), PR-2 in §3. **Queue row:** **CPM-2**.

## Why this PR

The second defect of the three-defect cascade (ADR 0017 §1.6, live-verified): `SoftCardControlPort.Read()`
flips the active CPU on **every read**. The SoftCard-detect poll and the `$1010` BIOS bridge **read** the
`$Cn00`/slot region repeatedly; a per-read toggle livelocks the handshake, the 6502 prints
**`CAN'T FIND Z80 SOFTCARD`** and drops to the monitor. The control register is **write-only**: the 6502
*starts* the Z80 by writing `$CN00`; the Z80 *hands back* by writing the same register (it sees `$EN00`). A
read is a bus read of a register-less slot → open-bus.

The fix is one line: `Read()` returns open-bus `0x00` with **no** `Toggle()`. `Write()` still toggles;
`TryPeek` is already peek-free.

**Definition of done:** the control-port unit tests assert a read does NOT flip `_z80Active` and a write
DOES; with CPM-1 + CPM-2 the live CP/M screen no longer contains `CAN'T FIND Z80 SOFTCARD` (a decoded-text
*negative* assertion). This PR alone still does not reach `A>` (PR-3's run-loop yield is needed for a stable
BIOS handshake).

---

## TDD discipline

Write the failing test first. The port-level test is the primary, fast, un-fakeable gate; the live
decoded-text negative assertion is the integration confirmation (asset-gated, runs on the dev machine).

---

## Task 1 — Port-level gate: a read does NOT toggle; a write DOES

### 1a. Test first

Edit `tests/CpuEmulator.Tests/Apple2/SoftCardControlPortTests.cs`. The `ControlSpy` already records
`LastActive` + `Calls`. Add:

```csharp
[Fact]
public void A_read_is_open_bus_and_does_NOT_toggle_the_active_cpu()
{
    // ADR 0017 Decision 2: the control register toggles on WRITE ONLY. A read is a bus read of a
    // register-less slot -> open-bus, no side effect. (A read-toggle livelocks the SoftCard-detect poll
    // -> CAN'T FIND Z80 SOFTCARD. This is the second defect of the cascade.)
    var spy = new ControlSpy();
    var port = new SoftCardControlPort();
    port.Realize(spy);

    uint v = port.Read(0x00, AccessWidth.Byte);
    Assert.Equal(0x00u, v);          // open-bus
    Assert.Equal(0, spy.Calls);      // the read did NOT toggle the active CPU
    Assert.Null(spy.LastActive);     // SetCoprocessorActive was never called by a read

    // Many reads (the detect poll + the $1010 bridge read the region repeatedly) still never toggle.
    for (int i = 0; i < 1000; i++) port.Read(0x00, AccessWidth.Byte);
    Assert.Equal(0, spy.Calls);
}

[Fact]
public void Reads_interleaved_with_writes_only_count_the_writes()
{
    // The handshake reads the region between writes; only the writes flip the bus master.
    var spy = new ControlSpy();
    var port = new SoftCardControlPort();
    port.Realize(spy);

    port.Read(0x00, AccessWidth.Byte);                 // no toggle
    port.Write(0x00, AccessWidth.Byte, 0x00);          // toggle -> active true (call 1)
    port.Read(0x00, AccessWidth.Byte);                 // no toggle
    port.Read(0x00, AccessWidth.Byte);                 // no toggle
    port.Write(0x00, AccessWidth.Byte, 0x00);          // toggle -> active false (call 2)

    Assert.Equal(2, spy.Calls);                        // exactly the two writes
    Assert.Equal(false, spy.LastActive);
}
```

Run: `dotnet test … --filter "FullyQualifiedName~A_read_is_open_bus|FullyQualifiedName~Reads_interleaved"`
→ FAILS (today `Read()` calls `Toggle()`, so `spy.Calls` is 1001 / non-zero). Correct first-failure.

### 1b. Implement

Edit `src/CpuEmulator.Peripherals/SoftCardControlPort.cs`. Replace the `Read` method (lines 31-35):

```csharp
    public uint Read(uint offset, AccessWidth width) => 0x00;   // open-bus, NO Toggle (ADR 0017 Decision 2)
```

Update the class XML-doc to match the corrected semantics. Replace the doc block (lines 5-15) — the key
change is "write-only toggle; Read is open-bus":

```csharp
/// <summary>The Z-80 SoftCard control register at the slot's $CN00 page (ADR 0015 Decision 3 as amended by
/// ADR 0017 Decision 2; research §1). A WRITE toggles which CPU is the bus master: from 6502 mode a $CN00
/// write hands control to the Z80 (and DMA-suspends the 6502); the Z80's matching write (which it sees as
/// $EN00, translated back to $CN00 by SoftCardTranslation branch 5) hands control back to the 6502. A READ
/// is a bus read of a register-less slot -> OPEN-BUS (0x00) with NO toggle: the control semantics are
/// write-only. (ADR 0015 said "fire on any access"; ADR 0017's live boot proved a read-toggle livelocks the
/// SoftCard-detect poll -> CAN'T FIND Z80 SOFTCARD; the real card has no readable status -- research §9 has
/// no onboard ROM/RAM.) The toggle is performed through ICoprocessorControl, captured from the Realize
/// context (the dual-CPU Machine IS the IMachineContext and implements ICoprocessorControl). On a single-CPU
/// board the cast fails and the port is inert (never an exception).
/// <para>PEEK-FREE (the ][+ invariant, ADR 0014 Decision 2): a debugger LOOKING at the control register
/// must NOT switch CPUs -- TryPeek returns open-bus 0 with no side effect.</para></summary>
```

The `Write`, `TryPeek`, `Realize`, and `Toggle` members are unchanged.

Run the Task-1 filter → green. Run the existing `SoftCardControlPortTests` lane (the
`A_write_flips_*`, `TryPeek_is_side_effect_free_*`, `On_a_non_coprocessor_context_*`,
`Real_Z80_runs_translated_*` facts) → all still green (none depended on a read-toggle).

> **Regression check on `Real_Z80_runs_translated_against_shared_6502_RAM_after_the_control_port_handoff`:**
> that test hands off via `STA $C200` (a WRITE) and the Z80 hands back via `LD ($F000),A` (also a write that
> the translation maps to physical `$0000`, not the control port). It never relied on a read-toggle, so it
> stays green. Confirm by running it explicitly.

---

## Task 2 — Live integration: the `CAN'T FIND Z80 SOFTCARD` message is gone

### 2a. Extend the CPM-1 de-fanged gate (decoded-text negative)

CPM-1 added `Cpm_boot_clears_the_per_track_skew_crash_no_monitor_no_softcard_error` with a `DecodeBootScreen()`
helper that decodes the 24x40 text page. That gate already asserts `DoesNotContain("CAN'T FIND")`. Before
CPM-2, that assertion may pass only because the boot crashes earlier (the skew is fixed but the handshake
isn't reached cleanly) OR it may be the live failure point. To make CPM-2's effect **un-fakeable and
specific**, add a CPM-2-specific gate that drives the boot far enough to reach the detect handshake and
asserts the SoftCard error is absent AND the boot advanced past the detect (the Z80 became active at least
once during the run — even if it later collapses, which PR-3 fixes).

Add to `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs`:

```csharp
[SoftCardCpmFact]
public void Cpm_boot_passes_the_softcard_detect_no_cant_find_message()
{
    // ADR 0017 PR-2: with the per-track skew (CPM-1) + the open-bus Read (CPM-2), boot2 reaches the real
    // SoftCard-detect handshake and the detect POLL no longer livelocks -> the "CAN'T FIND Z80 SOFTCARD"
    // message is gone, and the Z80 activates at least once. (A STABLE handshake to A> still needs the
    // run-loop yield -- CPM-3; this gate only asserts the detect is no longer fatal.)
    var (screen, z80EverActive) = DecodeBootScreenTrackingCoprocessor();

    Assert.DoesNotContain(screen, row => row.Contains("CAN'T FIND"));
    Assert.True(z80EverActive,
        "expected the Z80 to become bus master at least once during the detect handshake (the $CnXX write " +
        "fired and was not spuriously cancelled by a read-toggle)");
}
```

### 2b. The tracking decode helper

The existing `DecodeBootScreen()` runs the boot once and decodes the screen. To observe whether the Z80 ever
became active, run the boot in slices and OR `machine.CoprocessorActive` across slices. Add to
`SoftCardBoardTests.cs`:

```csharp
/// <summary>Like DecodeBootScreen but runs the cold boot in slices, recording whether the Z80 ever became
/// the bus master at a slice boundary (the $CnXX handoff fired during the detect). Returns the final decoded
/// 24x40 text screen + that flag.</summary>
private static (string[] screen, bool z80EverActive) DecodeBootScreenTrackingCoprocessor()
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
    Machine machine = BoardMachineFactory.Build(spec);

    machine.Reset();
    bool z80EverActive = false;
    const long slice = 100_000;
    for (long run = 0; run < CpmBootCycles; run += slice)
    {
        machine.Run(slice);
        if (machine.CoprocessorActive) z80EverActive = true;
    }

    IAddressSpace bus = machine.Space(AddressSpaceKind.Program);
    var rows = new string[24];
    for (int r = 0; r < 24; r++)
    {
        uint rowBase = Apple2HiResAddress.TextRowBase(r, page2: false);
        var sb = new System.Text.StringBuilder(40);
        for (int c = 0; c < 40; c++)
        {
            int g = bus.Read8(rowBase + (uint)c) & 0x7F;
            sb.Append(g is >= 0x20 and <= 0x7E ? (char)g : ' ');
        }
        rows[r] = sb.ToString();
    }
    return (rows, z80EverActive);
}
```

> **Why slice-and-OR, not a single `CoprocessorActive` check at the end:** before PR-3 the handshake is
> unstable — the Z80 activates then collapses back to the reset stub (ADR 0017 §1.7). A single end-of-run
> check could miss the activation. Slicing and OR-ing proves the `$CnXX` handoff fired at all (CPM-2's
> contribution) without asserting the *stable* end-state PR-3 delivers. This keeps each PR's gate scoped to
> exactly what it fixes.

### 2c. Verify the gate FAILS without CPM-2

Temporarily revert `Read()` to call `Toggle()` (or test before applying Task 1b) and run
`Cpm_boot_passes_the_softcard_detect_*` → it should FAIL (either `CAN'T FIND` appears, or the read-toggle
cancels the handoff so `z80EverActive` stays false). With CPM-2 applied → it PASSES. This is the un-fakeable
gate ADR 0017 PR-2 demands.

> **Grounding caveat (flag in the PR if it bites):** the ADR's live trace says the open-bus Read makes the
> Z80 "stay active across the CP/M load (1399 slices active)". If, on this exact build, the detect requires
> PR-3's yield to even *fire once* cleanly (i.e. `z80EverActive` is still false after CPM-2 alone), relax
> Task 2's gate to assert ONLY the `DoesNotContain("CAN'T FIND")` negative (the message-gone is the
> ADR's primary live-observed CPM-2 effect) and move the `z80EverActive` positive assertion to CPM-3, noting
> the move. Decide on the first live run — the live disk is the arbiter.

---

## Task 3 — Verify

1. `dotnet test … --filter "FullyQualifiedName~SoftCardControlPort"` → all green (the read/write toggle
   asymmetry + the existing facts).
2. `dotnet test … --filter "FullyQualifiedName~Cpm_boot_passes_the_softcard_detect"` → green (assets present).
3. Full solution Release → 0 failed, warning-clean.

---

## Self-review checklist

- [ ] **One-line production change:** only `SoftCardControlPort.Read` body + its XML-doc changed; `Write`,
      `TryPeek`, `Toggle`, `Realize` untouched.
- [ ] **Read/write asymmetry proven:** a read (even 1000 reads) → 0 toggles; a write → 1 toggle.
- [ ] **No regression:** `Real_Z80_runs_translated_*` + `A_write_flips_*` + `TryPeek_*` still green.
- [ ] **Live negative gate is un-fakeable:** confirmed it FAILS with the read-toggle restored (Task 2c).
- [ ] **Scope honesty:** CPM-2's gate asserts the detect passes (no `CAN'T FIND`), NOT `A>` (that is CPM-4).
- [ ] Full solution 0-failed in Release.

---

## Drift from ADR 0017 (flag in the PR body)

1. **`z80EverActive` may need to move to CPM-3.** ADR 0017 says the open-bus Read keeps the Z80 active across
   the load, but the live trace also says the activation is *unstable* until PR-3. If CPM-2 alone can't show
   even one clean activation, the positive half of Task 2's gate moves to CPM-3 and CPM-2 keeps only the
   `CAN'T FIND`-gone negative. Decided on the first live run (Task 2c). Both are honest, un-fakeable; the
   difference is only which PR claims the positive.
