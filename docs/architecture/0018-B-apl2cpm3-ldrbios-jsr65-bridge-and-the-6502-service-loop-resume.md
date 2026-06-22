# ADR 0018-B — Addendum: the V80-2 third-layer blocker is the missing `?jsr65` Z80→6502 service-loop bridge; CP/M-3's LDRBIOS calls *back into* the 6502 (for disk reads AND console) while the Z80 is bus master, and our bus-handoff never resumes the 6502 at its service loop

> **Status:** ACCEPTED (Architect phase, apl2cpm3 CP/M 3.1 / Videx-80-col sub-arc). **Second addendum to ADR 0018**
> (sibling of ADR 0018-A).
> **Date:** 2026-06-22
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Resolves:** the NEW third-layer blocker the V80-2 Builder hit and escalated per protocol after ADR 0018-A's
> skew fix was live-verified: CPMLDR loads + runs forward, opens the `"CPM3 SYS"` FCB, but the disk load **fails
> (`A=$FF`)** and the Z80 `DI/HALT`s at `$01A9` with **zero console output**. ADR 0018-A predicted the skew fix
> alone would reach `A>`; it does not — two layered issues it did not anticipate sit behind it.
> **Reads as ground truth:** (1) apl2cpm3's **own decoded boot/loader source** — `LDRBIOS.MAC`, `BOOTLDR.MAC`,
> `BIOSKRN.MAC`, `BOOT.MAC` (`/d/prj/ROMs/asimov-cpm/cpm31-extracted/decoded/`), plus the **`DEVICE65.MAC` body
> extracted live from the Disk 6/7 images** (the `?jsr65` / `L65A` 6502 service loop / `?fdrwts` / `?odcrt`
> bodies — `include`d at assembly time, not in the decoded dir, recovered by scanning the `.dsk` strings this
> session); (2) a **live instruction-step + bus-transition trace** of the real apl2cpm3 Disk 1 on the slot-4
> `SoftCardBoard` with ADR 0018-A's track-0 DOS33 skew applied (a throwaway probe, reverted — the tree carries
> none); (3) ADR 0018 + ADR 0018-A (this sub-arc's parents); ADR 0017 (the 2.2 boot handshake + the dual-CPU
> run-loop yield); ADR 0015 (the dual-CPU board).
> **No implementation here** — this root-causes the third layer, designs the minimal 2.2-safe fix, and states
> the blast radius. The Planner re-points V80-2 Task 2; the Builder closes it against the live disk (`A>` is the
> arbiter).

---

## 1. Why this addendum exists

ADR 0018-A root-caused the second layer (a double sector-skew) and resolved it: present apl2cpm3's **boot track 0
in raw DOS 3.3 order** (`Apple2SectorOrder.Dos33PhysToLog`) so BOOTLDR's own software `xlt` deskews `CPMLDR`. That
fix is **proven and live-verified** (the Builder confirmed it): under it the Z80 activates, the byte at Z80 `$0100`
is `$31` (`LD SP,$0281` — CPMLDR's real entry, not the mis-skewed `$E9`/`JP (HL)`), CPMLDR loads contiguously
page-for-page, and the Z80 **executes CPMLDR forward** — `$0100` entry → `CALL` BIOS init → BDOS `open` of the
`"CPM3    SYS"` FCB.

**Then a NEW, distinct failure appears that ADR 0018-A did not anticipate:** the open returns `A=$FF`, CPMLDR
`DI/HALT`s at Z80 `$01A9`, and there is **zero console output** anywhere (not on the 40-col Apple page, not on the
Videx — `videxEngaged=0`, VRAM empty; OQ2 40-vs-80-col staging is ruled out — the Builder built on the
SoftCard+Videx board and the Videx never engaged). The Builder swept **all 18 data-track skew candidates** (DOS33 /
`CpmData` / all 16 `xlt`-cancel offsets on tracks 3+) → **identical halt at `$01A9`**, proving this is **not** a
data-track sector-order problem. Two layered issues sit behind the skew fix:

- **(a) the LDRBIOS disk-read of `CPM3.SYS` fails regardless of data-track skew**, and
- **(b) the LDRBIOS console stub never renders.**

This addendum proves **(a) and (b) are the SAME bug** — one missing mechanism — mined from apl2cpm3's own
`DEVICE65.MAC`, and confirmed by a live bus-transition trace.

## 2. Ground truth: CP/M-3's LDRBIOS calls *back into* the 6502 via `?jsr65` — a synchronous Z80→6502 service call that returns

This is the structural fact ADR 0017/0018-A did not surface, because **CP/M 2.2 never does it.** apl2cpm3's
CP/M-3 LDRBIOS does **all** of its hardware I/O — floppy reads AND console output — by calling a 6502 service
routine **while the Z80 is bus master**, and **waiting for the 6502 to return.** The mechanism (`LDRBIOS.MAC`):

```
?jsr65: LD   (a$vec),DE    ; a$vec = $F3D0 (Z80) -> Apple $03D0 (branch 6): the 6502 sub address to call
        LD   (z$cpu),A     ; z$cpu = the Z80-side slot-4 toggle: HAND THE BUS TO THE 6502
dummy:  RET                ; ...the Z80 only continues AFTER the 6502 ran the sub and toggled the bus BACK
```

Every LDRBIOS I/O primitive funnels through `?jsr65`:

| LDRBIOS entry | 6502 sub it dispatches (`DE`) | purpose |
|---|---|---|
| `conout:` (`LD (@pscl+3),A` then `?jsr65 ?odcrt`) | `?odcrt` | **console output** — the char goes in `@pscl+3` = `$F67B`→`$067B` |
| `fdrd:`   (`LD (@cmd),A` then `?jsr65 ?fdrwts`) | `?fdrwts` | **floppy read** — then `LDIR $FF00→@dma`, `LD A,(@stat)`, `AND 3` |
| `boot:`/`init:` (`?icrt`, `?ilst`, `?iv24`) | `?icrt` etc. | device init |
| `?time:` (`?clock`) | `?clock` | clock |

The 6502 side that `?jsr65` hands to is the **`L65A` service loop**, installed by LDRBIOS `boot:` (it `LDIR`s
`PHL65…` onto page 3 and points the 6502 reset/BRK/NMI/`&`/`^Y` vectors at it). Recovered verbatim from
`DEVICE65.MAC` (extracted from the Disk 6 image; `.phase $3C0` — it lives at Apple **`$03C0`**):

```
        .phase $3C0
L65A    LDA  $C083        ; r/w enable LC
        ... dispatch: JSR through a$vec ($03D0) -> run ?odcrt / ?fdrwts / ?icrt ...
        STA  a$cpu        ; = STA $C400 : turn the Z80 back on (hand the bus BACK to the Z80)
        LDA  $C081        ; w/o enable LC
        JSR  RESTORE      ; monitor save/restore regs ($FF3F)
        JSR  OLDRST
        STA  $C081
        JSR  SAVE         ; ($FF4A)
        JMP  L65A         ; loop: wait for the NEXT ?jsr65
```

And the two leaf subs, also recovered from `DEVICE65.MAC`:
- **`?odcrt`** (Disk 7): `odcrt LDA PSCL+3` (reads the char at `@pscl+3` = `$067B`) … `JMP COUT1` (`$FDF0`) — it
  **paints the Apple 40-col text screen via the monitor `COUT1`.** So `$067B` is a *parameter-passing byte*, not
  the render target; the screen paint is `?odcrt`'s `JMP COUT1`. **The Builder's flag on the `$F67B`→`$067B`
  mapping is correct — but the mapping is fine; the problem is `?odcrt` never runs to consume it.**
- **`?fdrwts`** (`?fdrwts JMP fdrwts`, `jmp65+42`): the floppy RWTS that drives the Disk II controller
  (`$C08C…$C08F`) and leaves the status in `@stat`, which `fdrd:` then reads.

**So the LDRBIOS contract is:** Z80 writes `a$vec` + toggles `z$cpu` → 6502 **resumes at `$03C0` (`L65A`)** →
runs the requested sub → `STA $C400` toggles back → Z80 resumes after its `?jsr65 RET`. **A synchronous,
re-entrant, bidirectional bus round-trip.**

## 3. The actual bug: our bus handoff never resumes the 6502 at `L65A` — it resumes at BOOTLDR's `STA $C400 / RTS` overlay, so the 6502 never runs `?odcrt` or `?fdrwts`

Our dual-CPU model toggles bus mastership correctly (ADR 0017 Decision 3 made it instruction-granular), but the
toggle is a **bare boolean flag flip with no PC redirection**: the dormant core resumes **wherever it was last
suspended**. Cited:

- `Machine.SetCoprocessorActive` (`src/CpuEmulator.Core/Machine.cs:97-101`) — the entire switch is
  `_z80Active = active; _sliceEndRequested = true;`. No PC manipulation.
- `Machine.RunDualCpu` (`src/CpuEmulator.Core/Machine.cs:150-199`) — when `_z80Active` flips, the inner loop
  (`:164-184`) steps whichever core is now active **from its existing register state** (`Cpu.Step()` /
  `copro.Step()`). No entry-vector / service-loop redirection of the resumed core anywhere.
- `SoftCardControlPort.Toggle` (`src/CpuEmulator.Peripherals/SoftCardControlPort.cs:43-47`) and
  `ICoprocessorControl.SetCoprocessorActive` (`src/CpuEmulator.Core/ICoprocessorControl.cs:10-14`) — a flag-only
  contract ("set which CPU drives the bus on the NEXT slice").

**What the 6502 PC actually is when the Z80 holds the bus.** BOOTLDR releases the Z80 with the 4-byte overlay
`servs: STA a$cpu / RTS` copied to `servt = $03C6` and `JSR`ed (`BOOTLDR.MAC:81-85,157-158`). So at the moment the
Z80 first becomes bus master, the 6502 is suspended **inside that `JSR servt`** — its return path is the `RTS` at
**`$03C9`** (`$03C6: STA $C400` / `$03C9: RTS`). **That overlay is NOT `L65A`.** LDRBIOS's `boot:` is *supposed*
to `LDIR` the real `L65A` loop onto page 3 — but `L65A`'s entry is `$03C0`, and `boot:` runs **as Z80 code, after
the Z80 already holds the bus**; the 6502's *suspended* PC is still BOOTLDR's `$03C9` `RTS`. When CPMLDR's LDRBIOS
does its first `?jsr65` (the `?icrt` console init) and toggles `z$cpu`, our model resumes the 6502 **at `$03C9`** —
it executes the `RTS`, returns up BOOTLDR's stale stack, and **never enters `L65A`, never `JSR`s through `a$vec`.**

### 3.1 Live proof (bus-transition trace, since reverted)

On the real apl2cpm3 Disk 1, slot-4 board, ADR 0018-A track-0 DOS33 skew applied:
- **Exactly ONE bus transition in the entire boot**: `6502→Z80` at ~874 K cycles, with the **6502 suspended at
  `$03C9`**. **No `Z80→6502` hand-back was ever observed** — `CoprocessorActive` stays `True`; the Z80 churns and
  eventually wedges (the `$01A9` `DI/HALT` the Builder reported is the downstream wedge of a dead bridge).
- Page-3 dump at the switch: `$03C6..$03D3 = 8D 00 C4 60 35 36 37 …` → `$03C6: STA $C400`, **`$03C9: 60 = RTS`**
  (the suspended PC), and `$03CA+` is uninitialised junk — the LDRBIOS `L65A` loop is **not** present/entered at
  the 6502's resume point.

The single signature explains **both** symptoms with one mechanism:
- `?odcrt` never runs on the 6502 → its `JMP COUT1` never fires → **zero console output** (the
  `"CP/M V3.0 Loader…"` sign-on is loaded in RAM at Z80 `$023E` but never painted — exactly the Builder's
  observation). Symptom **(b)**.
- `?fdrwts` never runs on the 6502 → `@stat` is never populated, `fdrd:`'s `LDIR $FF00→@dma` copies garbage, and
  the BDOS `open`/read of `CPM3.SYS` returns **`A=$FF`** → CPMLDR prints its (un-rendered) error and
  `DI/HALT`s at `$01A9`. Symptom **(a)**.

**That is the un-fakeable signature: both blockers are the missing `?jsr65` resume-at-`L65A` semantics, not two
independent bugs and not a data-track skew.**

### 3.2 Why CP/M 2.2 is unaffected — the invariant this fix MUST respect

The shipped CP/M **2.2** boots to `A>` (CPM-4, live-verified, committed-hash gate) **without** this mechanism,
because 2.2's boot architecture is fundamentally different:

- **2.2 disk reads happen during 6502 boot2, BEFORE the Z80 BIOS is live.** boot2 (6502) loads the whole CP/M
  image off the system/data tracks, *then* hands to a **self-contained Z80 BIOS** that runs in its translated
  space. 2.2's steady-state BIOS does **not** call back into the 6502 RWTS via a `?jsr65`-style synchronous
  bridge (ADR 0017 §1 traced the entire 2.2 path: a one-shot detect handshake — a Z80 *detect stub* clears flag
  `$003E` and writes `$EN00` — then the 6502 finishes the load; there is no re-entrant `L65A` service loop).
- **2.2 CONOUT reaches the `$0400` screen through the TRANSLATED BUS, not through a 6502 callback.** Per CPM-4
  (ADR 0017 Decision 4 / the committed `A>` gate): the Z80 BIOS CONOUT writes the text page directly via
  `SoftCardTranslation` branch 6 (`$F000-$FFFF`→`$0000-$0FFF`, covering the `$0400` screen). The reviewer
  confirmed the `A>` can only come from a real CONOUT through the translated `$04xx` write — no 6502 round-trip.

So **2.2 exercises neither the disk-read bridge nor the console bridge that CP/M-3 needs.** The two disks were
authored to **different BIOS↔hardware conventions over the same SoftCard**: 2.2 = "6502 loads, Z80 runs
self-contained + pokes the screen via translation"; CP/M-3 = "Z80 BIOS synchronously calls the 6502 `L65A`
service loop for every I/O." **This is the crux the fix must respect — the resume-at-service-loop semantics must
be apl2cpm3-scoped, or at minimum a no-op for the 2.2 boot path, which never resumes the 6502 anywhere but its
own suspended PC after a one-shot handoff.**

## 4. Decisions

### Decision B1 — Root cause: the missing `?jsr65` bridge. The fix is to model the SoftCard's bus-handoff faithfully enough that handing the bus to the 6502 resumes it at the **installed service-loop entry**, not its stale suspended PC. This is a **dual-CPU-handoff** fix, NOT a Z80-core / Z80-reset change.

The bug is in the **bus-mastership resume semantics** (`Machine` / `SoftCardControlPort` / `ICoprocessorControl`),
the same seam ADR 0017 Decision 3 amended. It is emphatically **not** a Z80-core change (ADR 0018-A Decision A1
stands — `Z80Cpu.Reset()` and the Z80 instruction core are correct and untouched) and **not** a disk-skew change
(ADR 0018-A's track-0 DOS33 skew stands and is part of the landing PR; the data-track sweep already proved skew
is not the residual).

**The mechanism to model (from `DEVICE65.MAC` `L65A`):** on the real SoftCard, when the Z80 writes `z$cpu`
(`$EN00`→`$C400`) to release the bus, the 6502 does **not** simply continue at its dormant PC — it **takes a
control transfer to the installed 6502 entry** (the SoftCard routes the 6502 to the LDRBIOS-installed service
vector; `L65A` is reached because LDRBIOS pointed the 6502 RESET/BRK/NMI/`&`/`^Y` vectors at it and the card's
hand-back asserts one of those, or the loader's own dispatch enters it). The faithful model: **when the
SoftCard hands the bus to the 6502, resume the 6502 at the address the loader installed**, run until the 6502
hands the bus back (`STA $C400`), then resume the Z80 after its `?jsr65 RET`.

### Decision B2 — The fix shape: a **per-board "coprocessor-release entry"** carried on the SoftCard handoff seam, written by the apl2cpm3 board (and inert/identity for the 2.2 board). The exact entry + trigger are a live Builder bring-up against the disk (the ADR-0017-Decision-4 discipline).

The seam is already per-board and additive; this rides it. Concretely, in likely-minimal order:

- **(B2-i) A SoftCard "service-call resume vector" on the handoff.** Extend the bus-handoff so that the toggle
  that activates the **6502** (the Z80's `$EN00`/`$C400` write) optionally **sets the 6502 PC to a configured
  service-loop entry** before the 6502 runs, and the toggle that activates the **Z80** leaves the Z80 PC alone
  (it resumes after `?jsr65`). The configured entry is a **per-board parameter** on `SoftCardBoard.Spec` /
  `SoftCardVidexBoard.Spec` (sibling of V80-1's `controlPortBase`), **defaulted to "none / resume-at-suspended-PC"
  so the 2.2 board is byte-for-byte unchanged**; apl2cpm3 passes the LDRBIOS service entry (`$03C0` = `L65A`, or
  the `servt` vector `$03C6`/`$03D0` the loader actually installs — pinned live). The plumbing: a new optional
  field on the handoff contract (e.g. `ICoprocessorControl.SetCoprocessorActive(bool active, uint? primaryResumePc)`
  or a sibling `SetPrimaryResumeEntry(uint?)` the control port reads), consumed in `RunDualCpu`'s resume of the
  primary. **This is the cleanest: additive optional parameter + one apl2cpm3 wiring point; the 2.2 default
  (`null`) is the exact current behavior — the dormant 6502 resumes at its suspended PC, which 2.2 relies on.**
  **Recommended default.**
- **(B2-ii) Model the SoftCard hand-back as a 6502 interrupt-style vector fetch.** If the live trace shows the
  real card asserts the 6502 NMI/IRQ/RESET on hand-back (LDRBIOS points `RESET`/`NMI`/`BRKV` at `L65A` — see
  `boot:` vectoring), model that vector fetch on the `6502←Z80` toggle. More faithful, larger blast radius
  (touches the 6502 interrupt entry); choose only if (B2-i)'s direct-PC-set is shown insufficient by the live
  disk. **Fallback.**

**The decision is the SEAM** (a per-board "6502 resume entry on bus hand-to-6502", parameterised exactly like
V80-1's slot) **and the INVARIANT** (the 2.2 board passes no entry → `null` → today's resume-at-suspended-PC,
byte-for-byte; the CPM-4 committed-hash `A>` gate + the CPM-3 `$Axxx`-stable gate must stay green and run LIVE in
the landing PR). **The exact entry address + the exact trigger condition (which toggle, whether a vector fetch is
needed) are a bounded Builder bring-up closed against the live disk** — `L65A`'s `$03C0` and `servt`'s `$03C6`/
`$03D0` are the candidates; the live trace (the single `6502→Z80` at `$03C9` with no hand-back) is the red→green
discriminator.

**Rationale.** The bug is a faithful-emulation gap in *one BIOS's* I/O convention, isolated to the bus-handoff
seam that already exists, is already per-board-constructable, and was already amended once (ADR 0017 D3). No new
abstraction beyond an optional parameter; no shared-path change (2.2's default is the current behavior); no CPU
change. The mechanism is recovered from apl2cpm3's own `DEVICE65.MAC` (`L65A` verbatim) and the single-transition
live trace, so the fix is verifiable, not speculative.

### Decision B3 — The un-fakeable gate is unchanged (decoded `A>` + CP/M-3 sign-on on the live disk). Add **two cheap red→green discriminators** upstream of the `A>` paint, one per symptom, so the Builder iterates without a full boot.

Keep the decoded-`A>` gate (V80-2 plan Task 3 / ADR 0018-A Decision A3) as the primary oracle. Because that gate
is expensive and this bug is a precise bridge gap, **also** add two asset-gated micro-checks that FAIL on the
dead bridge and PASS only when `?jsr65` round-trips:

1. **Bus round-trip count (disk-read symptom).** Assert the boot produces **≥1 `Z80→6502` hand-back**
   (`CoprocessorActive` returns to `false` at least once after the Z80 first activates). Today: **0** hand-backs
   (the live trace's single `6502→Z80`, never back). This is the tight un-fakeable proof of the bridge fix,
   upstream of `CPM3.SYS` loading. *(The Builder can also assert the 6502 PC reaches `L65A`/`$03C0` during the
   boot.)*
2. **Console-paint discriminator (console symptom).** After CPMLDR runs, assert the decoded 40-col text page
   contains a byte of the CPMLDR sign-on (`?odcrt` ran), e.g. the `"CP/M"` substring — FAILS on the dead bridge
   (VRAM/text-page empty), PASSES only when `?odcrt`'s `JMP COUT1` fired.

*(Both optional but recommended; the decoded `A>` substring on the live disk remains the headline arbiter — ADR
0018 Decision 4.)*

## 5. Consequences

**Good.**
- The third-layer blocker is root-caused to **one proven, quantified mechanism** (the missing `?jsr65`
  resume-at-`L65A` bridge; §3.1's single `6502→Z80`-at-`$03C9`-with-no-hand-back trace is the un-fakeable
  signature), mined from apl2cpm3's **own `DEVICE65.MAC`** — not a guess.
- **(a) and (b) collapse into ONE fix.** The disk-read failure and the missing console are the same dead bridge;
  fixing the resume semantics lights up `?fdrwts` AND `?odcrt` together.
- **The Z80 core, the Z80 reset, and the disk-skew layer are all correct.** ADR 0018-A Decisions A1 (no
  Z80-core change) and A2 (the track-0 DOS33 skew) stand; this is the bus-handoff seam (ADR 0017 D3's territory),
  not the CPU.
- The fix is **additive and per-board** (an optional "6502 resume entry" on the handoff, defaulted to today's
  behavior), riding the existing per-board `SoftCardBoard.Spec` / `ICoprocessorControl` seam — the same pattern
  V80-1 used for the slot. **2.2's default is byte-for-byte the current code.**
- Two cheap red→green discriminators (bus round-trip count + console-paint) give the Builder a fast loop
  independent of the full `A>` boot.

**Bad / accepted.**
- ADR 0018-A's prediction that "the skew fix alone reaches `A>`" is **superseded** — a third layer (this bridge)
  sits behind it. ADR 0018-A Decisions A1 (no Z80 change), A2 (track-0 DOS33 skew), A3 (the gate) **all stand**;
  only its "skew fix is the last domino" framing is corrected. ADR 0018 Decisions 1/2/4/5 stand.
- The handoff contract (`ICoprocessorControl` / `SetCoprocessorActive`) gains an **optional** parameter (a
  per-board 6502 resume entry). This is the one cross-cutting touch — but it is additive, defaulted to the
  current behavior, and the 2.2 board never passes it. The `RunDualCpu` resume of the primary reads it only when
  set. Blast radius is precisely: `ICoprocessorControl.cs`, `Machine.cs` (`SetCoprocessorActive` +
  `RunDualCpu`'s primary-resume), `SoftCardControlPort.cs` (carry the entry from the board to the toggle),
  `SoftCardBoard.cs` / `SoftCardVidexBoard.cs` (the optional `Spec` parameter, defaulted), and the apl2cpm3
  wiring point. **No Z80 core, no `SoftCardTranslation`, no `Apple2SectorOrder`/`DskFluxImage` change beyond ADR
  0018-A's already-scoped track-0 skew, no 2.2-board change.**
- The exact resume entry (`$03C0` `L65A` vs `servt $03C6`/`$03D0`) and the exact trigger (direct PC-set on the
  `6502←Z80` toggle vs a modelled vector fetch — B2-i vs B2-ii) are a bounded live Builder bring-up. The
  candidates and the discriminator are pinned (§3, §4), so it is a verification step, not open-ended triage.
- **Possible fourth layer (flagged, not yet hit — see §8).** Once the bridge round-trips, the running CP/M-3
  BIOS (`BIOSKRN.MAC`) uses the **same** `?jsr65` mechanism for *its* CONOUT/RWTS (it is the same author, same
  `DEVICE65`), so the bridge fix should carry through to `A>`. But the BIOS also reads **data tracks** via
  `?fdrwts` and may exercise the `?icrt` **Videx** init (V80-3). If a residual appears after the bridge lands, it
  is most likely (i) a data-track skew the running BIOS RWTS needs (ADR 0018 OQ3 — same per-track discipline) or
  (ii) the Videx `icrt` path (V80-3, already wired). Neither is a new class of problem; both are closed by the
  live disk. The bridge is the gating layer; design it now, let the live `A>` reveal any tail.

**Reversibility.** High. The fix is an additive optional handoff parameter set only by the apl2cpm3 board; revert
→ the skew-fix-only state (apl2cpm3's bridge is dead, 2.2 unaffected). No Z80-core / translation / skew change to
reverse (the track-0 skew is ADR 0018-A's, landed in the same PR but independently revertible).

## 6. What changes for the Planner / Builder

- **V80-2 stays a single PR but gains the bridge fix.** Its landing change is now the COMBINED:
  **(1)** ADR 0018-A's track-0 DOS33 skew (a new `SectorOrderKind.Cpm3` whose track-0 returns `Dos33PhysToLog`,
  tracks 1+ keep the CP/M tables — or the equivalent per-board presentation; ADR 0018-A Decision A2-ii, the
  track-0-only constraint + `table[0]==0` the Builder pinned)
  **+ (2)** the `?jsr65` bridge fix (Decision B2 — the per-board "6502 resume entry on bus-hand-to-6502",
  defaulted off for 2.2) → CP/M 3.1 `A>`.
- **V80-2 Task 1 (triage) is DONE by this addendum** — the mechanism (`?jsr65`/`L65A`), the single-transition
  signature, and the candidate entries (`$03C0`/`$03C6`/`$03D0`) are pinned. The Builder's remaining job is
  **Task 2 = land the combined skew + bridge fix** (Decisions A2 + B2), then Task 3's decoded-`A>` gate + the §B3
  discriminators.
- **No Z80-core change, no owner sign-off on a risky change is needed** — the escalation trigger (a Z80-reset
  change) stays voided (ADR 0018-A A1). This is the bus-handoff seam (ADR 0017 D3 territory), already amended
  once, additive and 2.2-defaulted-safe.
- **The 2.2 no-regression gates are load-bearing and MUST run LIVE in the landing PR**: the CPM-4 committed-hash
  `A>` gate + the CPM-3 `$Axxx`-stable gate. They pass byte-for-byte because the 2.2 board passes no resume entry
  (`null` → today's resume-at-suspended-PC).
- **V80-3 (the 80-col Videx headline) stays behind V80-2.** Once the bridge round-trips, the running BIOS's
  `?icrt` drives the already-wired Videx path (ADR 0018 §1.3 / Decision 4) — but it now also routes through the
  same `?jsr65` bridge this PR fixes, so V80-3 is unblocked by V80-2, unchanged in scope.

---

## 7. Related decisions

- **ADR 0018-A** (sibling): Decision A1 (no Z80-core change) and A2 (the track-0 DOS33 skew) **stand and are
  reinforced** — this addendum confirms the residual is the bus-handoff bridge, not the CPU and not the skew. A2
  lands in the same PR as B2. A3 (the gate) stands; B3 adds two discriminators.
- **ADR 0018** (parent): Decisions 1 (slot-4 — V80-1, shipped), 2 (skew family), 4 (gate), 5 (asset) stand.
- **ADR 0017** Decision 3 (the dual-CPU run-loop yield-on-`$CnXX`) — **this addendum extends the SAME seam**: ADR
  0017 D3 made the toggle instruction-granular; B2 makes the 6502 *resume at its service loop* on the toggle.
  Both are bus-handoff-fidelity fixes on `Machine.RunDualCpu` / `ICoprocessorControl`, additive and 2.2-safe.
  ADR 0017 Decision 4 (live-triage bring-up discipline) governs the exact-entry pinning.
- **ADR 0015** (dual-CPU board) — the Z80 reset/handoff model is confirmed correct; B2 adds an optional resume
  entry to the handoff, not a change to the core handoff.

## 8. Open questions (narrowed)

1. **The exact 6502 resume entry + trigger** (Decision B2-i vs B2-ii): `$03C0` (`L65A`) vs `servt` `$03C6`/`$03D0`;
   direct PC-set on the `6502←Z80` toggle vs a modelled 6502 vector fetch (LDRBIOS points RESET/NMI/BRK at
   `L65A`). Pinned to §3's single-transition trace; closed by the Builder against the live disk (the §B3
   round-trip discriminator + `A>`). *Not a new ADR.*
2. **Does the running CP/M-3 BIOS (`BIOSKRN`) need a data-track skew** distinct from the boot tracks once the
   bridge round-trips (it reads `CPM3.SYS` and data via `?fdrwts`)? ADR 0018 OQ3 — same per-track discipline;
   the Builder's 18-candidate sweep covered tracks 3+, but that sweep ran with the bridge DEAD, so it only
   proved skew-alone doesn't help. Re-verify once the bridge lights up. Escalate only on a live divergence.
3. **The possible fourth layer (§5 "Bad").** If a residual survives the bridge fix, it is most likely a
   data-track skew (OQ2 above) or the Videx `icrt` path (V80-3, already wired) — neither a new problem class.
   The live `A>` reveals it. *Not pre-designed; the disk decides.*
4. **40-col vs 80-col staging** (ADR 0018 OQ2) — unchanged; the live boot decides, the gate asserts what the
   disk paints. (The Builder confirmed the Videx is not engaged at the halt — so V80-2's gate is 40-col; V80-3
   is the 80-col engage.)

---

*End of ADR 0018-B. The V80-2 third-layer blocker is NOT a new skew and NOT a Z80-core gap — apl2cpm3's CP/M-3
**LDRBIOS does all its I/O (floppy reads AND console output) by calling back into the 6502 via `?jsr65`**: it
writes the 6502 sub address to `a$vec` ($03D0), toggles `z$cpu` to hand the bus to the 6502, and waits for the
6502's `L65A` service loop to run the sub (`?fdrwts` / `?odcrt`) and `STA $C400` the bus back. Our bus handoff
flips a boolean with **no PC redirection**, so the 6502 resumes at BOOTLDR's stale `STA $C400 / RTS` overlay
(`$03C9`), never enters `L65A`, and never runs `?fdrwts` or `?odcrt` — so the `CPM3.SYS` read fails (`A=$FF`) AND
the console never paints, from ONE missing mechanism (live trace: a single `6502→Z80` hand-off, zero hand-backs).
The fix is an **additive, per-board, 2.2-defaulted-safe "6502 resume entry on bus-hand-to-6502"** on the existing
`ICoprocessorControl` / `SoftCardBoard.Spec` handoff seam (the seam ADR 0017 D3 already amended) — resume the
6502 at the LDRBIOS service loop ($03C0/$03C6) on the toggle, so `?jsr65` round-trips. CP/M 2.2 is untouched (it
never calls the 6502 back — it loads via 6502 boot2 and paints the screen via the translated bus). ADR 0018-A
A1/A2 stand: no Z80-core change, the track-0 DOS33 skew lands in the same PR. V80-2 is unblocked (combined skew +
bridge fix → `A>`); V80-3 stays behind it.*
