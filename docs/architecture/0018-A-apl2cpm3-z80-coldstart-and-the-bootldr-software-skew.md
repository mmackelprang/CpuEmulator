# ADR 0018-A — Addendum: the apl2cpm3 Z80 cold-start is correct; the V80-2 blocker is a **double sector-skew** in the 6502 boot-read path (resolves ADR 0018 Decision 6 / OQ1)

> **Status:** ACCEPTED (Architect phase, apl2cpm3 CP/M 3.1 / Videx-80-col sub-arc). **Addendum to ADR 0018.**
> **Date:** 2026-06-22
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Resolves:** ADR 0018 **Decision 6** (the "fundamental Z80-reset-semantics gap" escalation row) and **Open
> Question 1** (the Z80-entry-handoff residual), which the V80-2 Builder hit and escalated per protocol.
> **Reads as ground truth:** (1) the apl2cpm3 **own 8080/6502 boot source** decoded from the 7-disk set
> (`BOOTLDR.MAC`, `LDRBIOS.MAC`, `CONFIG.LIB`, `PUTSYS.MAC`, `LDR.SUB` on Disks 5–6; `CPMLDR.COM` on Disk 1);
> (2) a fresh **live instruction-step + RAM-correlation trace** of the real apl2cpm3 Disk 1 on the slot-4
> `SoftCardBoard` (a throwaway probe, since reverted — the tree carries none); (3) the SoftCard research doc
> §1/§9; ADR 0017 (the 2.2 per-track skew); ADR 0018 (this sub-arc's parent).
> **No implementation here** — this re-frames the root cause, takes the Z80-core-reset escalation OFF the table,
> and scopes the corrected fix to the disk-read / sector-order seam. The Planner re-points V80-2 Task 2; the
> Builder closes it against the live disk (`A>` is the arbiter).

---

## 1. Why this addendum exists

ADR 0018 §1.2 and Open Question 1 framed the post-slot-fix residual as a **Z80-entry-handoff** problem: "the
Z80 NOP-slides from `$0000` because its entry vector to the loaded CPMLDR is absent," with hypothesis 1 being
"the real SoftCard may latch a non-`$0000` Z80 start PC" (a change to `Z80Cpu.Reset()` / the dual-CPU handoff —
the one scenario ADR 0018 Decision 6 said requires owner sign-off). The V80-2 Builder ran the prescribed live
triage, reproduced the NOP-slide, tried forcing `PC=$0200` (it fell back into low page), and — correctly per
protocol — **PAUSED and escalated**, judging it landed on the flagged Z80-reset trigger.

**That framing was wrong, and so was ADR 0018's leading hypothesis.** Mining apl2cpm3's *own* boot source plus
a RAM-correlation trace shows the Z80 cold-start is **faithfully modelled already** and needs **no core change**.
The real blocker is a **disk-read sector-ordering bug** in the 6502 boot path — a `DskFluxImage`-skew composition
problem, not a CPU-reset problem. This addendum records the proven root cause and re-scopes the fix. The
escalation is **resolved in the negative**: do NOT touch the Z80 core.

## 2. Ground truth: how apl2cpm3 actually cold-starts the Z80 (from its own source)

The real Microsoft Z-80 SoftCard carries **no onboard ROM/RAM** — it is pure CPU hardware (research §9). The Z80
always comes out of reset at the documented `PC=$0000`; **there is no latched start address** (research §1: control
transfer is a single soft-switch *write* that toggles bus mastership — it does not carry a PC). All boot software
lives on the disk. apl2cpm3's `BOOTLDR.MAC` (Disk 5; `org 0`, `.phase $800`, runs on the 6502) does exactly this,
in this order:

1. **Zero-fill Z80 page zero with NOPs.** `z80p0 equ $1000` (the 6502 address of Z80 `$0000`). The loop
   `TXA / pzilp: STA z80p0,X / DEX / BNE pzilp` (entered with `X=0`) fills **`$1000–$10FF` only** — one 256-byte
   page — with `$00` (Z80 `NOP`). *(Compiled `BOOTLDR.COM` off `$51`: `8A 9D 00 10 CA D0 FA` — verified.)*
2. **Load `CPMLDR.COM` contiguously at Z80 `$0100`** (Apple physical `$1100`). The interface ROM's
   auto-incrementing DMA page register (`@dma equ $27`) is set to `#$11` and walks `$11,$12,…,$24`. *(Compiled
   off `$0B`: `A9 11 85 27`.)* `CPMLDR.COM` is a standard CP/M `.COM` (org/run `$0100`); its **first byte is
   `31 81 02` = `LD SP,$0281`** — the entry that must land at Z80 `$0100`.
3. **Release the Z80.** The 4-byte overlay `servs: STA a$cpu / RTS` is copied to `servt ($3C6)` and `JSR`ed;
   `a$cpu equ 0C400H` (CONFIG.LIB) — i.e. `STA $C400`, the slot-4 toggle (the same `8D 00 C4` ADR 0018 Decision 1
   found and V80-1 wired). The Z80 becomes bus master.

**The cold-start mechanism is a deliberate 256-byte NOP slide:** the Z80 starts at `$0000`, slides `$0000→$00FF`
through page-zero NOPs, and at `$0100` **lands exactly on `CPMLDR.COM`'s `LD SP,$0281` entry.** This is the
*entire* handoff — no `JP` stub at `$0000`, no latched PC, no second zero-fill. It is correct, and our emulator's
`Z80Cpu.Reset()→PC=0` + the slot-4 toggle already model it faithfully. ADR 0018's worry that "no entry vector is
written at the reset address" mistook the **intentional NOP slide** for a missing stub.

## 3. The actual bug: BOOTLDR's software `xlt` skew composes with our `DskFluxImage` pre-skew → a double skew

`BOOTLDR` does **not** read raw physical sectors. It reads each sector of `CPMLDR.COM` through the **Disk II
interface ROM's read entry** (`JMP (a2l)` → `$Cn5C`), and it applies its **own software logical→physical sector
translation** first:

```
btovl2  LDX sector          ; logical sector, counts 15..0
        LDA xlt,X           ; translate logical -> PHYSICAL
        STA @sect           ; physical sector for the interface read
        JMP (a2l)           ; read that physical sector via the interface ROM into the next DMA page
...
xlt     db 13,10,7,4, 1,14,11,8, 5,2,15,12, 9,6,3,0    ; BOOTLDR's own "Skew 3" table
```

But our `DskFluxImage.Synthesize` **already pre-applies** the CP/M skew when it lays the `.dsk` onto the
synthesized track: physical position `p` is given the `.dsk`-logical sector `physToLog[p]`, and for system
tracks 0–2 `physToLog = CpmBootPhysToLog = [0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]` (ADR 0017 Decision 1). So
when BOOTLDR asks the interface for physical sector `xlt[L]`, our disk returns the `.dsk`-logical sector
`CpmBootPhysToLog[xlt[L]]` — **the two skews compose, double-skewing the load.**

### 3.1 Live proof (RAM-correlation trace, since reverted)

CPMLDR.COM lives on `.dsk` tracks 0–1 (system tracks). After the real boot on the slot-4 board, the 18 file
pages **are all loaded into the correct contiguous window `$1100–$20FF` (Z80 `$0100–$10FF`)** — so the **DMA base
is right** — but the pages are **permuted by a 16-entry sector interleave**:

| Z80 load page | `$0100` | `$0200` | `$0300` | `$0400` | `$0500` | `$0600` | … | `$0F00` | `$1000` |
|---|---|---|---|---|---|---|---|---|---|
| CPMLDR file page | **14** | 8 | 2 | 12 | 6 | **0** | … | 4 | 15 |

The composition `CpmBootPhysToLog[ xlt[L] ]`, walked in BOOTLDR's `sector=15..0` order against the disk's actual
DOS-logical sector positions, **reproduces this measured permutation page-for-page (15/15 on track 0).** That is
the un-fakeable signature: it is a sector-order bug, not a CPU bug.

The damage is precise and total:
- The page that must hold CPMLDR's entry (`31 81 02` at Z80 `$0100`) instead holds **file page 14**, whose first
  byte is **`E9` = `JP (HL)`**. The Z80 NOP-slides `$0000→$00FF`, reaches `$0100`, executes `JP (HL)` (with
  `HL≈0` from reset) and **loops back into low page forever** — exactly the "NOP-slide that wraps" the V80-2
  Builder observed, and exactly why forcing `PC=$0200` also failed (every CPMLDR page is at the wrong address).
- `CPMLDR.COM`'s real `LD SP,$0281` entry landed at Z80 `$0600` (physical `$1600`); its internal cold-start
  (`21 00 00 22 88 09…`, file off `$0800`) landed at Z80 `$0200` — the "executable Z80 code at `$1200`" the
  Builder reported. Both are real CPMLDR bytes, just at the wrong (skew-permuted) pages.

### 3.2 Why 2.2 is unaffected (the invariant holds)

The shipped CP/M **2.2** master boots to `A>` because its on-disk SoftCard boot loader reads the system tracks
with an interleave that our single `CpmBootPhysToLog` pre-skew **already inverts correctly** (CPM-1 live-verified:
the `COPYRIGHT 1979 DIGITAL RESEARCH` ASCII lands intact). 2.2 does **not** run a second `xlt` software skew over
the interface ROM the way apl2cpm3's BOOTLDR does. The two disks were authored with **different boot-read
conventions over the same Disk II controller**; one pre-skewed track presentation cannot serve both. This is the
crux the fix must respect — **and it is why the fix must be apl2cpm3-scoped, never a change to the shared 2.2
path.**

## 4. Decisions

### Decision A1 — The Z80 cold-start needs **no core change**. ADR 0018 Decision 6 / OQ1 is resolved in the negative; do NOT touch `Z80Cpu.Reset()` or the dual-CPU handoff.

The SoftCard Z80 resets to `$0000` and is released by a bare toggle; apl2cpm3's NOP-slide-to-`$0100` handoff is
faithful to that and **already works in our model** once the bytes are at the right addresses. The escalation
trigger (a latched non-`$0000` start PC) is **falsified by apl2cpm3's own source** (`xlt`-read + page-zero
NOP-fill + `.COM`-at-`$0100`; no PC latch anywhere). **`Z80Cpu.Reset()`, `Machine.SetCoprocessorActive`,
`SoftCardControlPort`, `SoftCardTranslation`, and `CoprocessorSpec` are all correct and unchanged.** The
V80-2 Builder's pause was the right call against the *information then available*; with the source mined, the
risk it guarded against does not exist.

### Decision A2 — The fix is in the **apl2cpm3 disk-read sector ordering**, scoped to that board/asset; the shipped 2.2 path is byte-for-byte untouched.

apl2cpm3's BOOTLDR applies its own `xlt` skew over the interface ROM, so our `DskFluxImage` must present the
apl2cpm3 system tracks such that `(our presentation) ∘ (BOOTLDR's xlt) ∘ (interface read)` yields the disk's true
sectors — i.e. the **net** skew on apl2cpm3's tracks must be identity-correct for an `xlt`-translating reader, not
the `CpmBootPhysToLog` pre-skew that a non-`xlt` reader (2.2) needs.

The seam to change is the **physical→logical skew table `DskFluxImage` uses for the apl2cpm3 disk** (and possibly
*which tracks* it treats as "boot" vs "data"). The concrete shape — to be pinned by the Builder against the live
disk, the exact composition arithmetic is in §3.1 — is **one of** (in order of likely-minimal):

- **(A2-i) A new `SectorOrderKind.Cpm3` (or an `apl2cpm3` flag on the existing kind)** whose physical→logical
  table is the composition that *cancels* BOOTLDR's `xlt` — i.e. the apl2cpm3 board constructs
  `new DskFluxImage(disk1, SectorOrderKind.Cpm3)` while the 2.2 board keeps `SectorOrderKind.Cpm`. This is the
  cleanest: additive enum value + one table + the apl2cpm3 wiring point; the 2.2 `SectorOrderKind.Cpm` table and
  every 2.2 caller are untouched. **Recommended default.**
- **(A2-ii) Present the apl2cpm3 system tracks in raw/identity DOS-physical order** (no pre-skew) on the tracks
  BOOTLDR reads via `xlt`, leaving the data tracks (read later by the running CP/M BIOS RWTS) on the existing
  skew. This is correct *if* the only `xlt`-translated reads are the boot/CPMLDR load; the live boot confirms
  which tracks each reader touches.

The decision is the **seam** (the apl2cpm3 `DskFluxImage` skew, parameterised per board/asset) and the
**invariant** (the 2.2 `SectorOrderKind.Cpm` tables + the 2.2 board's `DskFluxImage(... Cpm)` construction are
unchanged — the load-bearing V80-1/CPM regression gates must stay green). The **exact table/kind** is a
Builder bring-up closed against the live disk (the ADR-0017-Decision-4 discipline), now with the composition
math (§3.1) and the file→page correlation as the un-fakeable check: CPMLDR's `31 81 02` must land at Z80 `$0100`,
and the contiguous 18-page load must match `CPMLDR.COM` page-for-page.

**Rationale.** The bug is a faithful-emulation gap in *one disk's* boot-read convention, isolated to the
sector-presentation layer that already exists and is already per-board-constructable (each board builds its own
`DskFluxImage(disk, kind)`). No new abstraction, no shared-path change, no CPU change. The composition is
arithmetic we have proven, so the fix is verifiable, not speculative.

### Decision A3 — The un-fakeable gate is unchanged from ADR 0018 Decision 4 / the V80-2 plan: decoded `A>` + CP/M-3 sign-on on the live disk. Add a **load-correctness micro-check** as a fast red→green discriminator.

Keep the decoded-`A>` gate (V80-2 plan Task 3) as the primary oracle. Because that gate is expensive (a full
boot) and the bug is a precise sector permutation, **also** add a cheap, asset-gated discriminator the Builder
can iterate on: after the boot loads CPMLDR (before `A>`), assert the byte at Z80 `$0100` (physical `$1100`) is
`$31` (the `LD SP` opcode of `CPMLDR.COM`'s real entry), **not** `$E9` (the mis-skewed `JP (HL)`). This FAILS on
the double-skew and PASSES only when the load is correctly ordered — a tight, un-fakeable proof of *this* fix,
upstream of the `A>` paint. *(Optional but recommended; the `A>` substring remains the headline arbiter.)*

## 5. Consequences

**Good.**
- The blocker is root-caused to **one proven, quantified mechanism** (a double sector-skew; §3.1 reproduces the
  measured permutation exactly), mined from apl2cpm3's **own source** — not a guess.
- **The Z80 core, the dual-CPU run loop, the control port, the translation, and the 2.2 board are all correct
  and untouched.** The cross-cutting risk ADR 0018 Decision 6 flagged is eliminated, not merely deferred.
- The fix is **additive and apl2cpm3-scoped** (a per-board/asset skew table or kind), riding the existing
  `DskFluxImage(disk, kind)` per-board construction — no new abstraction, no shared-path edit.
- A cheap load-correctness micro-check gives the Builder a fast red→green loop independent of the full boot.

**Bad / accepted.**
- ADR 0018 §1.2 and OQ1's "missing entry vector / latched-PC" framing is **superseded** by this addendum (the
  NOP slide is intentional; the real bug is the skew). ADR 0018 Decisions 1 (slot), 2 (skew *family* is CP/M),
  4 (gate), 5 (asset) **stand**; only Decision 3's *characterisation* of the residual and Decision 6/OQ1 change.
- The exact skew table/kind is still a bounded Builder bring-up (Decision A2) — but now with proven composition
  math and a deterministic correlation check, so it is a verification step, not open-ended triage.
- A new `SectorOrderKind.Cpm3` (if A2-i is chosen) is one more enum value + table; documented, regression-gated,
  and the DOS/ProDOS/2.2-Cpm tables are untouched.

**Reversibility.** High. The fix is an additive skew table/kind selected only by the apl2cpm3 board; revert →
the slot-fix-only state (apl2cpm3 mis-skews, 2.2 unaffected). No core/translation/handoff change to reverse.

## 6. What changes for the Planner / Builder

- **V80-2 is UNBLOCKED. No owner sign-off on a Z80-core change is needed** — there is no Z80-core change. Update
  the queue row from ⛔ to 📋.
- **V80-2 Task 1 (triage) is largely DONE by this addendum** — the mechanism, the composition math, and the
  target (CPMLDR's `31 81 02` at Z80 `$0100`) are pinned. The Builder's remaining job is **Task 2 = land the
  apl2cpm3 skew correction** (Decision A2; most likely a new `SectorOrderKind.Cpm3` table or the identity/raw
  presentation on the apl2cpm3 boot tracks), then Task 3's decoded-`A>` gate + the §A3 load-correctness check.
- **V80-2's outcome is NOT "outcome (3) / escalate"** (the plan's ESCALATE branch is void) and almost certainly
  **NOT "outcome (1) / no production change"** (the slot fix alone double-skews) — it is a **new, fourth outcome:
  a bounded, apl2cpm3-scoped disk-skew change** the V80-2 plan must add. The Planner re-points V80-2 Task 2 to
  Decision A2 and removes the Z80-reset escalation path.
- **V80-3 (the 80-col Videx headline) is unchanged behind V80-2** — once CPMLDR runs, the BIOS `icrt` drives the
  already-wired Videx path (ADR 0018 §1.3 / Decision 4); no change here.

---

## 7. Related decisions

- **ADR 0018** (parent): Decision 1 (slot-4 control port — V80-1, shipped) and Decision 2 (CP/M skew *family*)
  stand. Decision 3's residual characterisation and **Decision 6 / Open Question 1 are superseded by this
  addendum** (resolved: no Z80-core change).
- **ADR 0017** Decision 1 (per-track CP/M skew) and Decision 4 (live-triage bring-up discipline) — this addendum
  applies the same per-track-skew machinery and the same evidence-against-the-live-disk discipline; the new
  finding is that apl2cpm3's BOOTLDR adds a *second* software skew the 2.2 path lacks.
- **ADR 0015** (dual-CPU board) — confirmed correct and unchanged; the Z80 reset/handoff model is faithful.

## 8. Open questions (narrowed)

1. **Which exact table/kind cancels BOOTLDR's `xlt`** (Decision A2-i vs A2-ii), and **which tracks** apl2cpm3's
   `xlt`-translated reader touches vs the running CP/M BIOS RWTS. Pinned to the composition in §3.1; closed by the
   Builder against the live disk (the load-correlation check + `A>`). *Not a new ADR.*
2. **Do the CP/M data tracks (3+) read by the running BIOS need the existing `Cpm` data skew or the new one?**
   The boot/CPMLDR load is on tracks 0–1; the BIOS RWTS reads data tracks later. Verify both decode correctly
   once `A>` is reached (ADR 0018 OQ3 — same per-track discipline). Escalate only on a live divergence.
3. **40-col vs 80-col staging** (ADR 0018 OQ2) — unchanged; the live boot decides, the gate asserts what the disk
   paints.

---

*End of ADR 0018-A. The V80-2 blocker is NOT a Z80 cold-start / reset-PC gap — apl2cpm3's own `BOOTLDR.MAC`
zero-fills Z80 page zero with NOPs, loads `CPMLDR.COM` at Z80 `$0100`, and releases the Z80 to NOP-slide
`$0000→$0100` straight onto CPMLDR's `LD SP,$0281` entry; our `Z80Cpu.Reset()→PC=0` + the slot-4 toggle model
this faithfully. The real bug: BOOTLDR reads CPMLDR through the Disk II interface ROM with its **own** software
`xlt` skew, which **composes** with our `DskFluxImage` `CpmBootPhysToLog` pre-skew into a **double skew** that
permutes CPMLDR's load — placing a `JP (HL)` (`E9`) at Z80 `$0100` instead of `LD SP` (`31`), so the NOP slide
loops forever. Proven: the composition reproduces the measured page permutation 15/15 on track 0. The fix is an
**additive, apl2cpm3-scoped disk-skew correction** (a new `SectorOrderKind.Cpm3` table, or raw/identity
presentation on the apl2cpm3 boot tracks) on the existing per-board `DskFluxImage(disk, kind)` seam — the shared
2.2 path and the Z80 core are byte-for-byte untouched. ADR 0018 Decision 6 / OQ1 resolved in the negative: **do
not change the Z80 core.** V80-2 is unblocked; Planner re-points its Task 2 to Decision A2; V80-3 unchanged.*
