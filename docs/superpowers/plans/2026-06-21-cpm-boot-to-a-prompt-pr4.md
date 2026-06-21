# Plan — CPM-4: The live `A>` deliverable (decoded-text gate + `$1010` bridge bring-up)

> **Arc:** SoftCard CP/M boot-to-`A>` (ADR 0017). **PR 4 of 4 — the headline deliverable.**
> Depends on CPM-1, CPM-2, CPM-3.
> **Grounded against:** `main` @ `1d0232c` + CPM-1/CPM-2/CPM-3 landed.
> **ADR:** Decision 4 (the `$1010` bridge bring-up) + Decision 5 (the un-fakeable `A>` gate), PR-4 in §3.
> **Queue row:** **CPM-4**.

## Why this PR

With CPM-1 (per-track skew), CPM-2 (open-bus Read), and CPM-3 (run-loop yield) landed, the live trace shows
CP/M **loading** — the Z80 executes real BIOS code at `$Axxx` stably and the disk advances to the data tracks.
The remaining open behavior is the **CP/M sign-on / `A>` actually painting the 40-col screen**, which depends
on the `$1010` 6502-BIOS↔Z80 bridge round-tripping CONOUT/CONST through the now-correct handshake.

Per ADR 0017 Decision 4, the `$1010` bridge is **not pre-designed** — it is reverse-engineered against the
**running** machine after 1-3 land. The scoped hypothesis (Decision 4): if CONOUT does not reach the screen,
the bridge's CPU-switch round-trip needs CPM-3's per-instruction yield on **both** directions (which CPM-3
already provides) plus possibly an LC pre-state the bridge expects. This is a **Builder bring-up item against
the live disk, not a new ADR**.

This PR also completes Decision 5: the gate asserts the **decoded `A>` / CP/M sign-on substring** +
`CoprocessorActive` true + the multiplexer on the Apple source (`ActiveIndex==0` — this is the 40-col
master).

**Definition of done:** the live CP/M disk boots to `A>` — the decoded-text assertion passes, with the
captured real frame hash replacing `PLACEHOLDER`. **This is the deliverable.**

---

## The disk's own ASCII (the exact expected substrings — from ADR 0017 §1)

The cached real disk's bytes pin the targets:
- `A>` — the CCP prompt (the headline target).
- `Apple ][ CP/M 44K Ver. 2.20B` — the BIOS sign-on (track 2).
- `COPYRIGHT (C) 1979, DIGITAL RESEARCH` — the CCP/BDOS sign-on (track 0).

On the 40-col Apple text screen these are written in **normal video** (high bit set): `A` = `$C1`, `>` = `$BE`.
Stripping the high bit (`b & 0x7F`) yields `A` (`0x41`) and `>` (`0x3E`) — so a substring match on `"A>"` over
the decoded text is the precise, un-fakeable oracle.

---

## Task 1 — Bring-up: drive the live boot to `A>` (Decision 4)

**This task is investigative, against the live disk — not a fixed code change.** It is the
"run-the-real-boot, don't hardcode" discipline (ADR 0015 Decision 7 / ADR 0017 Decision 4). The Builder runs
the boot with CPM-1+2+3 in place and inspects the decoded text screen + the Z80/6502 PCs to find where CONOUT
stalls, then applies the **smallest** fix that lands `A>`.

### 1a. Triage harness

Extend `tools/BootProbe/Program.cs` (or add a `tools/CpmProbe/` twin — prefer extending BootProbe to keep one
triage tool) with a SoftCard CP/M config that:
- builds the real `SoftCardBoard` over the cached CP/M `.dsk` (the same wiring as the gate),
- runs the cold boot in slices,
- dumps the decoded 24x40 text page (the `TextRowBase` walk already in BootProbe) after each large slice,
- dumps the Z80 PC + `CoprocessorActive` + the disk's current track, so the stall point is visible.

This is a **dev tool**, not a gate; it must stay warning-clean under the solution-wide
`TreatWarningsAsErrors` (BootProbe already is). Use it to answer Decision 4's OQ1: *does CONOUT reach the
40-col screen, or does the bridge need an LC pre-state?*

### 1b. The expected bring-up outcomes (Decision 4's scoped hypothesis)

One of:
1. **CONOUT already reaches the screen** once CPM-1+2+3 are in — `A>` paints with no further change. (The
   ADR's best case: fixes 1-3 are the complete gating set.) → No production change in CPM-4; the PR is the
   gate + the captured hash.
2. **The `$1010` bridge needs the LC pre-state** the bridge expects (ADR 0015 Decision 3's build-time LC
   item) — a small, localized set-up in the SoftCard boot wiring. → A tightly-scoped change, grounded by the
   triage harness, with its own un-fakeable assertion (the decoded `A>`).
3. **Something else the live disk reveals** — escalate to the owner ONLY if it needs an asset/disk we lack
   (Decision 7; not expected — the cached disk is complete).

**Do not pre-write the production fix.** Write it against what the harness shows. Whatever it is, it must be
gated by the decoded-`A>` assertion below (Task 2), which is the arbiter.

> **Grounding note:** the `$1010` bridge dispatch is *disk data* (boot-loader/RWTS code on the CP/M disk)
> driven through the handshake — it is reverse-engineered against the running machine, never hardcoded
> (Decision 4). If Task 1 lands in outcome (1), record that explicitly in the PR ("fixes 1-3 were the
> complete gating set; no bridge change needed") — that is a valid, ADR-anticipated result.

---

## Task 2 — The un-fakeable `A>` gate (Decision 5 complete)

### 2a. Replace the CPM-1 named-skip with the real assertion

In CPM-1, `Cpm_boots_to_the_A_prompt_on_the_interpreter` was a `[Fact(Skip="…CPM-4…")]`. CPM-4 replaces it
with the live, content-decoding gate. Edit `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` — replace
the skipped placeholder with:

```csharp
[SoftCardCpmFact]
public void Cpm_boots_to_the_A_prompt_on_the_interpreter()
{
    // ADR 0017 Decision 5: the un-fakeable oracle is the DECODED CONSOLE TEXT, not a pixel count or a
    // placeholder hash. With CPM-1 (per-track skew) + CPM-2 (open-bus Read) + CPM-3 (run-loop yield) + any
    // CPM-4 bridge bring-up, the real disk boots to A> on the 40-col Apple text screen.
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
    // own ASCII pins these -- ADR 0017 Decision 5 / §1).
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
    // System.Console.WriteLine($"[cpm boot frame hash] {hash}");  // <-- uncomment ONCE to capture, then paste
    const string ExpectedBootHash = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN";
    if (ExpectedBootHash != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
        Assert.Equal(ExpectedBootHash, hash);
}
```

> **The PLACEHOLDER hash here is OK** because the **text substring `"A>"` is the primary, un-fakeable
> assertion** — the hash is an optional tightening gate that the Builder captures on the first green run and
> commits (replacing the placeholder string in the SAME PR). This is the inverse of the old gate, where the
> placeholder hash was the *cover* for a weak pixel heuristic. Here the gate cannot pass without a real `A>`
> on screen. **The PR must not merge with the placeholder still in place** — capture + commit the real hash.

### 2b. The text-decode helper (reuse / promote the CPM-1 helper)

CPM-1 added `DecodeBootScreen()` (no machine arg — it builds its own). CPM-4 needs to decode a screen from an
**already-built** machine (so it can also render the frame for the hash). Promote a small shared helper. Add
to `SoftCardBoardTests.cs` (or keep CPM-1's `DecodeBootScreen` delegating to it):

```csharp
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
            int g = bus.Read8(rowBase + (uint)c) & 0x7F;
            sb.Append(g is >= 0x20 and <= 0x7E ? (char)g : ' ');
        }
        rows[r] = sb.ToString();
    }
    return rows;
}
```

Re-add the `AsBytes` helper if CPM-1 removed it (it is needed for the hash). And re-add
`using System.Security.Cryptography;` if removed — or use the fully-qualified
`System.Security.Cryptography.SHA256` as above to avoid touching usings.

> **Decode caveat (Builder, verify on the live screen):** if `A>` paints split across the cell grid or with a
> trailing cursor cell between `A` and `>`, the naive `Contains("A>")` could miss. The triage harness (Task
> 1a) shows the exact cells. If the prompt is `A` then a cursor/space then `>`, assert on the row containing
> `A` followed (allowing one cursor cell) by `>`, or assert the row `.TrimEnd().EndsWith("A>")` form the live
> screen actually shows. Ground the exact assertion on what the harness prints — the disk is the arbiter.

### 2c. Remove the CPM-1 negative gate's now-redundant skip-companions (optional cleanup)

CPM-1's `Cpm_boot_clears_the_per_track_skew_crash_*` and CPM-2's `Cpm_boot_passes_the_softcard_detect_*` are
subsumed by the full `A>` gate, but they are cheap, fast, and pin the intermediate stages — **keep them** as
defense-in-depth (they catch a regression that breaks the skew or the detect without breaking `A>` framing).
The CPM-3 `Cpm_boot_runs_the_z80_bios_at_Axxx_stably_*` likewise stays. Do NOT delete them.

---

## Task 3 — Capture + commit the real frame hash

1. Run `Cpm_boots_to_the_A_prompt_on_the_interpreter` with the assets cached.
2. With the text assertions green, uncomment the `Console.WriteLine` hash print once, run, copy the printed
   SHA-256.
3. Paste it into `ExpectedBootHash`, re-comment the print, re-run → green with the committed hash active.
4. **Confirm the placeholder is gone** (`grep PLACEHOLDER` over the test file returns nothing in this gate).

> **Determinism:** the interpreter tier is deterministic (no JIT for the coprocessor — ADR 0015 Decision 4),
> so the frame hash is reproducible run-to-run. If it is NOT stable (e.g. the boot lands at slightly
> different CONOUT progress per run because the cycle budget cuts mid-print), bump `CpmBootCycles` until the
> prompt is fully settled and the hash is stable across 3 consecutive runs before committing it.

---

## Task 4 — Verify

1. `dotnet test … --filter "FullyQualifiedName~Cpm_boots_to_the_A_prompt_on_the_interpreter"` → green, with
   the committed hash (not placeholder).
2. The intermediate-stage gates (CPM-1/2/3 negatives + stability) still green.
3. **Full solution Release** → 0 failed, warning-clean.
4. **Owner UAT (out of band):** the headline is a *visible* `A>` on the 40-col render in the browser surface
   (`SoftCardSurface`). The headless gate proves it; the browser confirmation is owner UAT (no browser MCP
   here). Note it in the PR as the owner's confirmation step.

---

## Self-review checklist

- [ ] **`A>` is the primary assertion** — `Assert.Contains("A>", decoded)`, not a pixel count.
- [ ] **No PLACEHOLDER survives** — the real frame hash is captured + committed in this PR (Task 3); the
      `grep PLACEHOLDER` of the gate file is empty.
- [ ] **`CoprocessorActive` true** + the 40-col path is asserted (this is the 40-col master; the multiplexer
      `ActiveIndex==0` for the *Videx* board is PR-5's concern, not this SoftCard-only gate — see drift).
- [ ] **Intermediate gates kept** (CPM-1/2/3) — defense-in-depth, not deleted.
- [ ] **Bridge bring-up grounded:** any production change in Task 1 is the smallest fix the live triage
      harness justified, gated by the decoded `A>`. If outcome (1) (no change needed), that is recorded.
- [ ] **Hash is deterministic** across 3 runs before committing.
- [ ] Full solution 0-failed in Release; warning-clean.

---

## Drift from ADR 0017 (flag in the PR body)

1. **The `A>` gate is on the SoftCard (40-col) board, not the Videx board.** ADR 0017 Decision 5's gate
   "asserts the multiplexer stays on the Apple source (`ActiveIndex==0`)" — but that `ActiveIndex` concept
   lives on the *Videx* surface (`SoftCardVidexSurface.Display`), not the plain `SoftCardBoard` the
   `Cpm_boots_to_the_A_prompt_on_the_interpreter` gate uses (which has a single `Apple2Video`, no
   multiplexer). So this gate asserts `A>` + `CoprocessorActive` on the 40-col SoftCard board; the
   `ActiveIndex==0`-for-the-40-col-master assertion belongs to the **Videx** gate, which is PR-5's re-frame
   (Decision 6, out of this batch). This keeps CPM-4 scoped to the 40-col `A>` deliverable without pulling in
   the owner-gated Videx work.
2. **The `$1010` bridge bring-up may be a no-op** (outcome (1)) — if fixes 1-3 are the complete gating set,
   CPM-4 ships only the gate + the captured hash, with no production change. The ADR anticipates this
   ("fixes 1-3 are the gating set"); record the actual outcome in the PR.
3. **Videx `A>` gate stays skipped.** `SoftCardVidexBoardTests.Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter`
   was named-skipped in CPM-1 and remains skipped until PR-5 (Decision 6 / owner-gated 80-col master). CPM-4
   does not touch it.
