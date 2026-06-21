# ADR 0017 — The SoftCard CP/M boot-to-`A>` correction: per-track CP/M sector skew, control-port open-bus read, dual-CPU run-loop yield-on-`$CnXX`, and an honest boot gate (addendum to ADR 0015)

> **Status:** PROPOSED (Architect phase, Apple ][+ arc — SoftCard CP/M boot-fix sub-arc). **Addendum to ADR 0015.**
> Live UAT with the real Microsoft SoftCard CP/M 2.2 disk (`~/.cache/cpuemulator/vectors/cpm/softcard-cpm.dsk`,
> 143 360 B) proved the shipped CP/M deliverable (PR-K) **never boots to `A>`**. This ADR carries the
> instruction-step root cause of a **multi-defect** failure and decides the corrected dual-CPU handshake semantics.
> No implementation here — this decides the shapes + the PR sequencing the Planner executes.
> **Date:** 2026-06-21
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect, grounded in a live instruction-step trace of
> the real disk (not a paper analysis).
> **Reads as ground truth:** the **live boot trace** (this session) over the cached real disk; ADR 0015 (the dual-CPU
> board — this ADR corrects two of its decisions); ADR 0016 (the Videx/asset posture — this ADR re-frames its O gate);
> `docs/research/apple-2-plus-z80-softcard-cpm-analysis.md` §5 (sector skew — **§5's boot-track claim is corrected here**)
> and §1 (the `$CnXX` write protocol).
> **Supersedes / amends:**
> - **ADR 0015 Decision 3** — the `SoftCardControlPort` is amended: **`Read()` is open-bus with NO toggle**; only
>   `Write()` toggles. ADR 0015's text ("model as write, fire on any access") was read too literally as "Read mirrors
>   Write"; the live boot proves a read-toggle livelocks the SoftCard-detect poll.
> - **ADR 0015 Decision 1** — the dual-CPU run loop is amended: the active core is driven **one instruction at a time**
>   so a `$CnXX`/`$EN00` toggle yields control **at the writing instruction**, not after a full slice budget. ADR 0015's
>   `_sliceEndRequested` flag is checked only *after* `ICpuCore.Run(ref budget)` returns — but `Run` cannot be
>   interrupted mid-budget, so the post-toggle core "runs past" its hand-back point and corrupts the handshake.
> - **The shipped `Apple2SectorOrder` / `DskFluxImage`** (PR-G/PR-K) — the CP/M skew must be **per-track**: system
>   tracks 0–2 use the **boot interleave**; data tracks 3–34 use the existing CP/M-logical table. The data-track table
>   is correct (live-verified); a **single all-tracks table is the first, fatal defect**.
> - **Research §5** — its boot-track skew was wrong/absent; the live disk pins the correct boot table (Decision 1).
> - **ADR 0016 Decision 1/§O gate** — this CP/M master is a **40-column** console (zero `$C0Bx`); the Videx never
>   engages for this asset. The O gate is re-framed (Decision 6).

---

## 1. Context — the live trace (what actually happens on the real disk)

The CP/M gate `SoftCardBoardTests.Cpm_boots_to_the_A_prompt_on_the_interpreter` **runs** (both assets are cached) and
**fails** at the `machine.CoprocessorActive` assertion — the Z80 never becomes bus master. An instruction-step trace of
the real boot (6502 + Z80, single-stepped over the cached disk) gives the full causal chain. The disk's own ASCII
confirms the asset: `COPYRIGHT (C) 1979, DIGITAL RESEARCH` (CCP/BDOS sign-on, track 0) and
`Apple ][ CP/M 44K Ver. 2.20B  (C) 1980 Microsoft` (BIOS sign-on, track 2). **This is a 44 K, 40-column CP/M.**

The real boot path (decoded live, not from the research doc):

1. **`$C600` Disk II boot ROM** reads track 0 sector 0 → `$0800`, `JMP $0801`. **This works** (boot sector loads, the
   `$0801` loader runs). The boot sector is the standard P5A loader; its embedded read order at `$082D` is
   `00 02 04 06 08 0A 0C 0E 01 03 05 07 09 0B 0D 0F` (a 2:1 physical step).

2. **boot1 (`$0801`)** reads **11 sectors** off the system tracks into `$0800–$12FF`, then **`JMP $1000`** (boot2). It
   indirects sector reads through the slot-6 ROM (`$003E/$003F = $C65C`). **`$003E` is the slot-6 read-routine pointer
   low byte — NOT a SoftCard handshake flag.** (The prompt's `$003E` is real, but it is *also* used later, by the Z80
   detect stub — see step 5.)

3. **THE FIRST FATAL DEFECT — per-track skew.** boot1 reads physical sectors and lays them at fixed memory pages, but
   each physical sector carries the **logical** sector `physToLog[p]` the `DskFluxImage` synthesized. With the shipped
   **single all-tracks table** (`SectorOrderKind.Cpm` = the data-track skew `[0,6,12,3,9,15,14,5,11,2,8,7,13,4,10,1]`
   applied to track 0 too), boot2's bytes land at the **wrong addresses**: e.g. RAM `$0F` receives disk T0 logical
   sector 13 instead of the sector boot2's `$0F7D` routine needs. The byte at `$0F7D` ends up `$00` (`BRK`).

4. boot2 (`$1000`) runs Apple ROM init, then **`$1006: JSR $0F7D`** → `$0F7D = $00` (`BRK`) → BRK vector → the monitor
   (`$FA59 → JMP ($003E) → $C65C` garbage) → the 6502 idles in the monitor keyboard loop (`$FD1B`). **This is the boot
   the user saw "never boot": a silent crash into the monitor, BEFORE any SoftCard handshake.** The Z80 is never even
   reached. *(The prompt's "boot2 never loads CP/M" is a downstream symptom of this skew defect.)*

   **Live proof:** re-synthesizing tracks 0–2 with the boot interleave `physToLog[p] = (p×11) mod 16` =
   `[0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]` makes `$0F7D = $8A` (a valid opcode) and boot2 runs **past** the BRK.
   The data tracks keep the existing CP/M table (live-verified: with the boot fix, the disk seeks to track 3 and the
   Z80 executes real CP/M BIOS code at `$Axxx`). **So the boot-track table in research §5 was wrong/missing; the correct
   one is `[0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]`.**

5. With the boot skew fixed, boot2 reaches the **real SoftCard detect handshake** (`$03C0` region) — which the research
   doc never decoded. The mechanism (decoded live):
   - boot2 self-modifies a `STA $Cn00` (`$1182: STY $1187` patches the slot), then writes `$Cn00` → **the Z80 activates**
     at its reset PC `$0000` (translated → 6502 `$1000`).
   - A tiny **Z80 detect stub** boot2 placed at 6502 `$1000` (= Z80 `$0000`) runs:
     `XOR A; LD ($F03E),A; …` — i.e. it **clears flag `$003E`** (Z80 `$F03E` → 6502 `$003E` via translation branch 6,
     `$F000→$0000`). The prompt's "the Z80 stub clears flag `$003E`" is **exactly right**.
   - The 6502 then checks `$003E` to confirm a Z80 is present.

6. **THE SECOND DEFECT — `Read()` toggles.** `SoftCardControlPort.Read()` flips the active CPU on **every read**. boot2's
   detect poll + the BIOS bridge read the `$Cn00`/slot region; each read spuriously toggles the active CPU, so the
   handshake **livelocks** and the 6502 prints **`CAN'T FIND Z80 SOFTCARD`** (live-observed at row 20) → drops to the
   monitor. **Live proof:** making `Read()` open-bus (no toggle) removes the `CAN'T FIND` message entirely, the detect
   passes, and the Z80 stays active across the CP/M load (1399 slices active; the disk advances to track 3).

7. **THE THIRD DEFECT — the run loop runs past the toggle.** `RunDualCpu` drives the active core with
   `Cpu.Run(ref budget)` / `copro.Run(ref budget)` for the whole slice. The generated `Run` is
   `while (budget > 0) { Step(); … }` with **no external break**. When the active CPU writes `$CnXX` to hand control
   over, `SetCoprocessorActive` sets `_z80Active`/`_sliceEndRequested`, but `Run` **keeps executing the rest of the
   budget** — the just-disabled CPU runs thousands more (now-meaningless, cross-translated) instructions before the loop
   re-checks the flag. This corrupts every Z80↔6502 BIOS round-trip (disk read, CONOUT). **Live proof:** after fixes
   (1)+(2), the Z80 loads CP/M and runs to `$Axxx`, but then control collapses back to the reset stub (`$0000`) and the
   loaded system at `$Axxx` is overwritten — the run-loop yield is the missing piece for a *stable* BIOS handshake, not
   just the *first* one.

**Net root cause:** the CP/M boot is a **three-defect cascade**, each masking the next. (1) per-track skew is the
**first and fatal** one (silent monitor crash, no handshake at all); (2) the read-toggle livelocks the detect once (1)
is fixed; (3) the run-loop-runs-past-toggle corrupts the *repeated* BIOS handshakes once (1)+(2) are fixed. **They must
be fixed together to reach `A>`; fixing any one alone still fails.** The live `A>` on the real disk is the only arbiter.

---

## 2. Decisions

### Decision 1 — Per-track CP/M sector skew: system tracks 0–2 use the **boot interleave**, data tracks 3–34 use the existing CP/M-logical table; correct research §5's boot table to `[0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]`

The CP/M `.dsk` uses **two** skews (research §5 "double skew" — correct in spirit, wrong in the boot table). The
correction, live-verified against the real disk:

| Tracks | physical→logical table | Source |
|---|---|---|
| **0–2 (system/boot)** | `[0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]` = `physToLog[p] = (p×11) mod 16` | **NEW — live-verified; research §5 was wrong** |
| **3–34 (data)** | `[0,6,12,3,9,15,14,5,11,2,8,7,13,4,10,1]` (the shipped `SectorOrderKind.Cpm`) | research §5 (data-track), **live-verified correct** |

**Shape (additive, no behavior change for DOS/ProDOS):** resolve the table **per track** inside
`DskFluxImage.Synthesize`. `SectorOrderKind.Cpm` becomes the only **track-dependent** order; `Dos33`/`ProDos` are
single-skew and unaffected. Concretely:

```csharp
// Apple2SectorOrder — additive overload (single-skew orders ignore the track arg):
public static int[] PhysicalToLogical(SectorOrderKind kind, int track) => kind switch
{
    SectorOrderKind.Cpm => track < 3 ? (int[])CpmBootPhysToLog.Clone()   // NEW boot table, tracks 0-2
                                     : (int[])CpmDataPhysToLog.Clone(),  // existing table, tracks 3+
    _ => PhysicalToLogical(kind),   // Dos33/ProDos: track-independent, unchanged
};
// CpmBootPhysToLog = [0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5];  // (p*11)%16
// CpmDataPhysToLog = [0,6,12,3,9,15,14,5,11,2,8,7,13,4,10,1];  // = today's CpmPhysToLog (rename for clarity)
```

`DskFluxImage` resolves `_physToLog` **per track** at synthesis time (it already synthesizes per track and caches per
track — `Synthesize(int track)` — so this is a one-line change: call the `(kind, track)` overload inside the loop, not
the cached field). The 3-track boundary (`OFF=3`, research §4) is the disk's own system/data split.

**Rationale.** The skew is a property of *which RWTS wrote the track*: the SoftCard **boot ROM/loader** wrote tracks
0–2 with the boot interleave; the **CP/M BIOS RWTS** wrote tracks 3+ with the CP/M-logical skew. A single table is wrong
for one of the two regions by construction. The boot table is not guessable from the research sources (they had it
wrong); it is pinned by the live disk (the `$0F7D` opcode + the `COPYRIGHT`/`Apple ][ CP/M` ASCII landing correctly).

**Alternatives considered.**
- **(A) Keep one table, "fix" it.** *Rejected* — no single permutation satisfies both regions; the live data tracks
  decode correctly with the data table and the boot tracks with the boot table, and they differ.
- **(B) Encode the split as two `SectorOrderKind`s (`CpmBoot`, `CpmData`) the caller selects per track.** *Rejected* —
  the *image* is one CP/M disk; the per-track split is an internal property of the CP/M order, not a caller choice. A
  `(kind, track)` overload keeps the one `SectorOrderKind.Cpm` the board declares and hides the split where it belongs.

**Consequences.** *Good:* the real boot loads boot2 correctly (the first domino); DOS/ProDOS untouched; the split is
data, not a caller burden. *Bad/accepted:* `SectorOrderKind.Cpm` is now track-dependent (a documented asymmetry — the
overload's XML-doc + a per-track regression test guard it).

### Decision 2 — `SoftCardControlPort.Read()` is **open-bus, no toggle**; only `Write()` toggles the active CPU

Amend ADR 0015 Decision 3. The control register at `$CN00` toggles the bus master on **write only**. `Read()` returns
open-bus (`0x00`) with **no side effect**:

```csharp
public uint Read(uint offset, AccessWidth width) => 0x00;  // open-bus, NO Toggle()
public void Write(uint offset, AccessWidth width, uint value) => Toggle();   // unchanged
```

`TryPeek` stays peek-free (already correct — no toggle). The `Toggle()` body (flip `_coprocessorActive`, call
`ICoprocessorControl.SetCoprocessorActive`) is unchanged; only the `Read()` call to it is removed.

**Rationale.** The live boot proves a read-toggle is fatal: the SoftCard-detect poll and the `$1010` BIOS bridge **read**
the slot/`$CnXX` region repeatedly; a per-read toggle livelocks the handshake → on-screen `CAN'T FIND Z80 SOFTCARD`.
ADR 0015 said "model as write … the decoder likely fires on any access," and PR-K over-read that as "Read mirrors Write."
The *control* semantics are write-only: the 6502 *starts* the Z80 by **writing** `$CN00`, and the Z80 *hands back* by
**writing** the same register (it sees `$EN00`). A read is a bus read of a register-less slot → open-bus. (Removing the
read-toggle is also what made `CAN'T FIND` disappear and the Z80 stay active in the live trace.)

**Alternatives considered.**
- **(A) Toggle on read AND write (the shipped behavior).** *Rejected — live-falsified* (the detect poll livelocks).
- **(B) Toggle on write, return a meaningful status byte on read (e.g. the active-CPU flag).** *Rejected* — the real
  card has no readable status at `$CN00` (no onboard ROM/RAM, research §9); open-bus is the hardware truth, and CP/M's
  detect uses the **`$003E` RAM flag the Z80 stub clears**, not a control-port read value.

**Consequences.** *Good:* the detect handshake completes; matches the no-ROM hardware; one-line change. *Bad/accepted:*
none material — the read never carried information.

### Decision 3 — The dual-CPU run loop yields **at the toggling instruction**: drive the active core one instruction at a time (`Step()`), checking `_sliceEndRequested` after each, on the SoftCard board

Amend ADR 0015 Decision 1. `RunDualCpu` must hand control to the other CPU **at the `$CnXX`/`$EN00` write**, not after a
full slice. Because `ICpuCore.Run(ref budget)` is an un-interruptible `while (budget>0) { Step(); }`, the loop instead
**steps the active core one instruction at a time** within the slice and checks `_sliceEndRequested` after each step:

```csharp
// RunDualCpu inner slice (shape): drive the ACTIVE core by Step(), break the moment a $CnXX write flips it.
while (virtualClock < sliceEnd && !_sliceEndRequested)
{
    ICpuCore active = _z80Active ? _coprocessor! : Cpu;
    long before = active.CycleCount;
    active.Step();                                  // exactly one instruction
    long ran = active.CycleCount - before;
    if (_z80Active) _coprocessorCyclesContributed += ran;   // convert via ratio in the bound time source
    // _sliceEndRequested was set INSIDE Step() if that instruction wrote $CnXX -> loop exits immediately,
    // and _z80Active already selects the other core for the next instruction (the writing instruction
    // completed first — ADR 0015 OQ5: switch takes effect on the next dispatch).
    AdvanceSchedulerAndMaybeForceInterruptSwitch();
}
```

The interrupt-forces-switch-to-6502 rule (Decision 5 of ADR 0015) and the virtual-6502-clock conversion are preserved —
they move from "after the whole `Run`" to "after each `Step`," which is strictly finer-grained and correct. The slice
still bounds to the next scheduled event (the per-`Step` check is cheap; the dormant core is still never stepped).

**Rationale.** The handshake is a *fine-grained* dance: the Z80 writes `$EN00` to ask the 6502 to do a BIOS service
(disk read, CONOUT), expects the 6502 to run and write `$CN00` to resume it, mid-routine. If the just-disabled CPU runs
thousands more instructions after its toggle (the shipped behavior), it executes cross-translated garbage and corrupts
shared RAM — live-observed as the loaded CP/M system at `$Axxx` being overwritten and the Z80 falling back to its reset
stub. Stepping one instruction at a time makes the switch land exactly at the writing instruction, which is the hardware
truth (the DMA grant/release happens at that bus cycle). This is **interpreter-tier** behavior (ADR 0015 Decision 4
already ships the SoftCard Z80 on the interpreter), so per-instruction stepping is acceptable; the JIT-under-translation
follow-on (deferred row L) would need a block-level yield hook, but that is out of scope here.

**Alternatives considered.**
- **(A) Keep `Run(ref budget)`, check `_sliceEndRequested` after it returns (the shipped behavior).** *Rejected —
  live-falsified* (the post-toggle core runs past its hand-back; the handshake corrupts).
- **(B) Add an external "stop" flag the generated `Run` polls each instruction.** *Acceptable but heavier* — it touches
  the code-generator and every core's hot loop for one board's benefit. Stepping in the dual-CPU loop keeps the change
  in `Machine` (the one dual-CPU consumer) and leaves the cores untouched. Recommend (B) only if profiling shows the
  per-`Step` dispatch overhead matters for CP/M (it will not — CP/M is light, ADR 0015 Decision 4).
- **(C) Run the active core with `budget = 1` via `Run`.** *Equivalent to Step()* but less direct; `Step()` is the
  documented one-instruction primitive (`ICpuCore.Step`).

**Consequences.** *Good:* the BIOS handshake is stable (every Z80↔6502 round-trip yields at the right cycle); the cores
are untouched; the change is localized to `RunDualCpu`. *Bad/accepted:* per-instruction dispatch in the dual-CPU loop is
slightly slower than block `Run` — invisible for CP/M, and the single-CPU `Run` path (every other board) is **byte-for-
byte unchanged** (this only touches the `_coprocessor is not null` branch). The interrupt/clock logic moves to per-Step
granularity (finer, not different).

### Decision 4 — The boot2→CP/M completion (the BIOS bridge) is closed against the live disk, NOT designed speculatively: fixes 1–3 are the gating set; any residual is a 4th, tightly-scoped item

With Decisions 1–3 the live trace shows CP/M **loading** (the Z80 executes real BIOS code at `$Axxx`; the disk advances
to the data tracks). The remaining open behavior — the **CP/M sign-on / `A>` actually painting the 40-col screen** —
depends on the `$1010` 6502-BIOS↔Z80 bridge round-tripping CONOUT/CONST through the now-correct handshake. The decision:

- **Fixes 1–3 are the gating set and must land together** (each masks the next; the live `A>` is the arbiter).
- **Do NOT pre-design a 4th fix.** The trace localizes any residual to the `$1010` bridge dispatch (CONOUT → 6502
  `$FDED` COUT; CONST → 6502 keyboard `$C000`), which is *boot-loader/RWTS code on the disk* driven through the
  handshake — so it is reverse-engineered against the **running** machine after 1–3 land, exactly as ADR 0015 Decision 7
  says ("run the real boot, don't hardcode"). If, after 1–3, CONOUT does not reach the screen, the scoped hypothesis is:
  the `$1010` bridge's CPU-switch round-trip needs Decision 3's per-instruction yield on **both** directions
  (Z80→6502 *and* 6502→Z80) — which Decision 3 already provides — plus possibly the LC pre-state the bridge expects
  (ADR 0015 Decision 3's build-time LC item). **This is a Builder bring-up item against the live disk, not a new ADR.**

**Rationale.** The Architect's job is to root-cause the *mechanism* and decide the *seams*; the exact `$1010` bridge
behavior is disk data closed at build time (ADR 0015 Decision 7's discipline). Pinning fixes 1–3 as the gating set —
each independently live-verified to advance the boot one stage — is the honest, evidence-backed scope. Pre-specifying a
4th fix against a boot stage we have not yet reached (because 1–3 aren't landed) would be the speculative-generality
ADR 0015 warned against.

**Consequences.** *Good:* the gating set is exactly the three live-verified defects; the boot completion is closed
against the real disk (no guessing). *Bad/accepted:* the final `A>` paint may need one Builder bring-up iteration on the
`$1010` bridge after 1–3 land — scoped, gated by the live disk, escalated only if it needs a disk/asset we lack
(Decision 7).

### Decision 5 — The boot gate asserts the **decoded `A>` / CP/M sign-on text**, not a pixel heuristic or a placeholder hash

`Cpm_boots_to_the_A_prompt_on_the_interpreter` is replaced by an **un-fakeable, content** assertion. The CP/M console is
the **40-column Apple text screen** (Decision 6), so the gate decodes the text page (`$0400`, the same `TextRowBase`
walk the trace + BootProbe use) and asserts the **real CP/M sign-on string**:

- Decode the 24×40 text page to ASCII (strip the high "normal-video" bit, as BootProbe does).
- Assert the screen contains the **CP/M sign-on** — the disk's own bytes give the exact target:
  `A>` (the CCP prompt) and/or the sign-on line `Apple ][ CP/M 44K Ver. 2.20B` / `COPYRIGHT (C) 1979, DIGITAL RESEARCH`
  (both are on the disk; the BIOS prints them at cold boot). The assertion is a **substring match on decoded text**, not
  a pixel count.
- Also assert `machine.CoprocessorActive` transitioned true during the boot (the Z80 ran) — keep this; it is a real,
  cheap invariant.
- **Delete** the `onPixels > 50` heuristic and **delete** the `PLACEHOLDER` hash scaffold. Do **not** lock a frame
  hash — a text-substring assertion is the un-fakeable oracle (a `CAN'T FIND Z80 SOFTCARD` screen, a monitor crash, or a
  blank screen all fail it; only a real CP/M prompt passes). A hash may be added later as a *tightening* gate once the
  boot is green, but it must never be the *primary* assertion (it is brittle to font/palette changes and was the cover
  for the false pass).

**Rationale.** The shipped gate falsely passed on the `CAN'T FIND` error screen (weak `onPixels>50` + a never-captured
placeholder hash); the current `CoprocessorActive` assert catches *today's* failure but is not a positive `A>` proof.
The interpreter-as-oracle principle demands the gate assert the **actual decoded console text** — the same thing a human
reads as "it booted." The disk's ASCII pins the exact expected substring, so the assertion is precise and un-fakeable.

**Consequences.** *Good:* the gate proves a real `A>`, not "some ink on screen"; no placeholder hash to forget to
capture. *Bad/accepted:* the gate decodes text (a few lines of code) instead of counting pixels — strictly better.

### Decision 6 — Re-frame the Videx (PR-O) CP/M gate: this CP/M master is a **40-column** console; assert the 40-col path for this asset + add a separate Videx-console test (do NOT assert `ActiveIndex==1` for this disk)

This SoftCard CP/M 2.2 master drives the **40-column Apple screen** as its console — the live trace shows **zero `$C0Bx`
accesses** (the Videx 6845 registers are never touched), so the `DisplayMultiplexer` never switches to the Videx and
`ActiveIndex==1` **cannot** hold for this image. This is **not an emulation bug** (the Videx activation path is wired,
ADR 0016 — it is simply never engaged by this disk). Decision:

- **The CP/M-boot gate (Decision 5) asserts the 40-col Apple text path** for *this* asset (the sign-on on `$0400`), and
  asserts the multiplexer stays on the **Apple source** (`ActiveIndex==0`) for it — the hardware truth for a 40-col
  CP/M master.
- **The Videx 80-col console gets its own gate**, decoupled from this disk: either (a) drive the Videx directly
  (write the `$C0B0/$C0B1` CRTC init + VRAM, assert the 80×24 render — the synthetic-char-ROM render gate ADR 0016
  Decision 4 already contemplates, no copyrighted asset needed), or (b) source an **80-column CP/M master** that targets
  the Videx (a `PIP`/`CONFIGIO`-configured or `Videoterm`-aware CP/M disk) — an **owner-asset item** (Decision 7).
- **Default if no 80-col CP/M asset:** ship (a) — the direct Videx-render gate — as the Videx console proof, and keep
  the CP/M-boot gate on the 40-col path. The "CP/M auto-widens to 80-col at `A>`" headline (ROADMAP / ADR 0016 §O) is
  **retargeted** to "CP/M boots to `A>` on the 40-col console; the Videx 80-col path is gated by the direct render test
  (and by an 80-col CP/M master if the owner sources one)."

**Rationale.** Asserting `ActiveIndex==1` for a 40-col master is asserting a falsehood — it would force a fake. The honest
gate asserts what the asset actually does (40-col) and proves the Videx path independently (direct render), keeping both
un-fakeable. Sourcing an 80-col CP/M master is a real but **owner-gated** asset decision (it may not exist in clean form),
so it is escalated, not assumed.

**Consequences.** *Good:* every gate asserts a truth; the Videx path is proven without faking a 40-col disk into 80-col.
*Bad/accepted:* the "80-col CP/M end-to-end" headline is split into "40-col CP/M boot" + "Videx renders 80×24" until/
unless the owner sources an 80-col CP/M master (Decision 7) — an honest narrowing of an over-claimed deliverable.

### Decision 7 — Escalations to the owner

| Item | Why it's an escalation | Recommended default |
|---|---|---|
| **An 80-column CP/M master that targets the Videx** (Decision 6) | The cached master is 40-col; a Videx-console CP/M disk may not exist in clean-redistribution form (same §6/§9 licensing gray-area as the SoftCard CP/M). | Ship the **direct Videx-render** gate (no asset); retarget the 80-col-CP/M headline; fetch an 80-col master only if the owner sources/sign-offs one. |
| **The `$1010` BIOS-bridge completion** (Decision 4) | If, after fixes 1–3, the live `A>` still doesn't paint, the residual is a bring-up item; it does **not** need a new asset (the cached disk is sufficient), but it may need a Builder iteration. | Builder closes it against the live disk; escalate only if it reveals a missing disk/asset (not expected — the disk is complete). |

Neither escalation blocks the **first PR** (restore a green/honest main). Decision 6's 80-col-master question and
Decision 4's bridge bring-up are both downstream of the gating fixes.

---

## 3. PR decomposition (for the Planner)

Sequenced so the **first PR restores a green, honest main** (no false pass), then each subsequent PR advances the live
boot one verified stage. **Every PR's un-fakeable gate is the live CP/M disk; the real arbiter is `A>` on screen.**

**PR-1 — Honest main: per-track skew (the verified fix) + de-fanged gate (no false pass).**
- Land Decision 1 (per-track CP/M skew: boot table for tracks 0–2, data table for 3+; `Apple2SectorOrder.(kind,track)`
  overload + `DskFluxImage` per-track resolution). Add a **per-track skew regression test** (assert the boot table for
  track 0 and the data table for track 3 — un-fakeable, asset-free).
- Land Decision 5's gate honesty **partially**: replace the `onPixels>50` + placeholder-hash with the **decoded-text
  assertion**, but keep it **skip-with-note unless the assets are present AND the boot is green** — i.e. the gate must
  not falsely pass. Since PR-1 alone does not reach `A>`, the CP/M-boot gate is `Skip`-with-note (assets absent) on CI
  and an **expected-fail-documented** (or `[Fact(Skip="boot-fix in progress, see ADR 0017")]`) when assets are present,
  so **main is green/honest** — no false pass, the failure is named. *(Correct research §5's boot table in the doc as
  part of this PR.)*
- **Gate:** the skew regression test (asset-free) is green; the CP/M gate is honestly skipped/expected-fail (not false-
  passing). Main is green.

**PR-2 — The control-port handshake fix.**
- Land Decision 2 (`Read()` open-bus, no toggle). Update `SoftCardControlPortTests` to assert **read does not toggle**
  (a read leaves `CoprocessorActive` unchanged) and **write does toggle** — the un-fakeable port-level gate.
- **Gate:** the control-port unit tests; the live CP/M gate advances past `CAN'T FIND Z80 SOFTCARD` (observable via a
  decoded-text *negative* assertion: the screen must NOT contain `CAN'T FIND`).

**PR-3 — The dual-CPU run-loop yield-on-`$CnXX`.**
- Land Decision 3 (`RunDualCpu` drives the active core by `Step()`, yields at the toggling instruction; single-CPU path
  unchanged). Add a **dual-CPU yield regression test**: a synthetic 2-CPU machine where CPU-A writes the control port
  mid-slice and CPU-B must run the very next instruction (assert the switch lands at the write, not after the budget).
- **Gate:** the yield regression test; the live CP/M gate reaches the CP/M BIOS executing on the Z80 (the disk advances
  to data tracks; the Z80 PC reaches `$Axxx`) **stably** (no fallback to the reset stub).

**PR-4 — The live `A>` gate (Decision 5 complete) + any `$1010` bridge bring-up (Decision 4).**
- With PR-1–3 landed, bring the boot to `A>` against the live disk; close any residual `$1010` BIOS-bridge item
  (Decision 4 — Builder bring-up, gated by the live disk). Complete Decision 5: the gate asserts the **decoded `A>` /
  CP/M sign-on substring** + `CoprocessorActive` true + multiplexer on the Apple source (`ActiveIndex==0`).
- **Gate:** the live CP/M disk boots to `A>` — the decoded-text assertion passes. **This is the deliverable.**

**PR-5 — Videx gate re-frame (Decision 6).**
- Retarget the PR-O / O gate: assert the 40-col path for the cached CP/M master; add the **direct Videx 80×24 render**
  gate (synthetic char ROM, asset-free). Update ROADMAP / ADR 0016 §O wording (40-col CP/M boot + Videx render proven
  separately). Escalate the 80-col-CP/M-master question to the owner (Decision 7).
- **Gate:** the direct Videx render test (asset-free); the CP/M gate asserts `ActiveIndex==0` for the 40-col master.

Dependencies: PR-1 → PR-2 → PR-3 → PR-4 are **strictly ordered** (each fix is needed for the next live stage). PR-5 is
independent of PR-2–4 (it can land after PR-1, or in parallel) but is documented last because it closes the deliverable
narrative. PR-1 alone restores a green/honest main (the owner's standing requirement); PR-4 is the headline (`A>`).

---

## 4. Consequences (cross-cutting)

**Good.**
- The boot failure is **root-caused to three concrete, live-verified defects** (per-track skew, read-toggle, run-loop-
  runs-past-toggle), each independently shown to advance the real boot one stage — not a paper analysis.
- The fixes are **small and localized**: a per-track table resolution (Decision 1), a one-line `Read()` change
  (Decision 2), a `RunDualCpu`-only stepping change (Decision 3 — single-CPU path untouched). The Z80 core, the
  translation table, and every other board are unchanged.
- The gate becomes **un-fakeable** (decoded `A>` text, no pixel heuristic, no placeholder hash) and the Videx gate
  becomes **honest** (40-col asserted for the 40-col asset; Videx proven by a direct render).
- The PR sequence keeps **main green/honest at every step** (PR-1 first), honoring the owner's branch-per-change + no-
  false-pass discipline.

**Bad / accepted costs.**
- `SectorOrderKind.Cpm` is now track-dependent (a documented asymmetry; guarded by a per-track regression test).
- The dual-CPU loop steps the active core per instruction (interpreter-tier; invisible for CP/M; the JIT-under-
  translation follow-on (row L) would need a block-level yield hook — out of scope).
- The "80-col CP/M end-to-end" headline narrows to "40-col CP/M boots to `A>`" + "Videx renders 80×24 (direct)" until an
  80-col CP/M master is sourced (owner-gated, Decision 7).

**Reversibility.** High. Decision 1 is an additive overload (revert → the single-table behavior). Decision 2 is a one-
line change. Decision 3 is gated behind `_coprocessor is not null` (single-CPU `Run` is the unchanged path). The gate
changes are test-only.

---

## 5. Open questions

1. **The `$1010` BIOS-bridge completion (Decision 4).** After fixes 1–3, does CONOUT reach the 40-col screen, or does
   the bridge need an LC-pre-state set (ADR 0015 Decision 3's build-time LC item)? **Resolve by Builder bring-up against
   the live disk in PR-4** — do not pre-design.
2. **An 80-col CP/M master (Decision 6 / 7).** Does a clean-redistribution Videx-console CP/M disk exist? **Owner.**
   Default: ship the direct Videx-render gate; retarget the headline.
3. **Clock-ratio under per-instruction stepping (Decision 3).** Per-`Step` virtual-clock conversion is finer-grained
   than per-`Run`; confirm the rounding stays invisible to CP/M (it will — CP/M is coarse-timed). Inherited from ADR
   0015 OQ3; no new risk.
4. **REFRESH-window 6502 wakeups (ADR 0015 OQ4).** Still not modeled; the live boot does not appear to need them (the
   handshake is explicit `$CnXX` writes, not refresh-window-dependent). Escalate only if PR-4 reveals a dependency.

---

*End of ADR 0017 — the SoftCard CP/M boot-to-`A>` correction. The shipped CP/M deliverable never booted because of a
**three-defect cascade** root-caused live on the real disk: (1) the CP/M sector skew must be **per-track** — system
tracks 0–2 use the boot interleave `[0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]` (`(p×11) mod 16`; **research §5's boot
table was wrong**), data tracks 3–34 use the existing CP/M-logical table (correct); the single all-tracks table loaded
boot2's `$0F7D` routine as `$00`/BRK → a silent monitor crash before any handshake. (2) `SoftCardControlPort.Read()`
toggled the active CPU on every read, livelocking the SoftCard-detect poll → `CAN'T FIND Z80 SOFTCARD`; **`Read()` must
be open-bus, no toggle — write-only toggle** (amends ADR 0015 Decision 3). (3) `RunDualCpu` ran the active core a whole
slice past its `$CnXX` toggle (the generated `Run` can't be interrupted), corrupting every BIOS round-trip; **the loop
must `Step()` the active core and yield at the toggling instruction** (amends ADR 0015 Decision 1). All three were
live-verified to each advance the boot one stage and must land together; the **live `A>` on the real disk is the
arbiter**. The gate is re-armed to assert the **decoded `A>`/CP/M sign-on text** (no pixel heuristic, no placeholder
hash), and the Videx (O) gate is re-framed: this CP/M master is a **40-column** console (zero `$C0Bx`), so the 40-col
path is asserted for it and the Videx 80×24 path is proven by a **direct render** gate (an 80-col CP/M master is owner-
gated). PR sequence: **PR-1 restores a green/honest main** (the verified per-track skew + a de-fanged, non-false-passing
gate), then PR-2 (control-port), PR-3 (run-loop yield), PR-4 (the live `A>` gate + `$1010` bring-up), PR-5 (Videx
re-frame). Planner: execute PR-1 first; Builder: the live CP/M disk is every PR's un-fakeable gate.*
