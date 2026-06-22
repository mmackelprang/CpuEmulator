# ADR 0018-C — Addendum: the V80-2/V80-3 fifth-layer blocker is the Language-Card write-enable flip-flop clobbering write-enable on an odd-address *write*; the SoftCard CP/M-3 CCP copy into LC bank 2 is silently dropped. Classified **SAFE** (the fix makes the LC model MORE faithful to the real 74LS175 — MAME `ramcard16k`; no Z80-core / no translation change), and **live-proven to reach `A>`**.

> **Status:** ACCEPTED (Architect phase, apl2cpm3 CP/M 3.1 / Videx-80-col sub-arc). **Third addendum to ADR 0018**
> (sibling of ADR 0018-A and ADR 0018-B).
> **Date:** 2026-06-22
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Resolves:** the FIFTH-layer blocker the V80-2/V80-3 Builder hit and escalated per protocol after the
> sign-on-on-Videx milestone shipped (the `## Blocker` section of `docs/BUILDER_QUEUE.md` on
> `feat/apl2cpm3-v80-2-combined-fix`): after the CP/M-3 sign-on renders on the Videx, the CCP runs and the
> banked BDOS `RET`s into a **zeroed** Z80 `$1901` (phys `$2901`) and NOP-slides — no `A>`. Pinned
> byte-identically at instr 36583, `PC=$1929`.
> **Reads as ground truth:** (1) apl2cpm3's **own decoded loader source** —
> `/d/prj/ROMs/asimov-cpm/cpm31-extracted/decoded/{BOOT_MAC,LDRBIOS_MAC,BIOSKRN_MAC,BOOTLDR.MAC}.txt`
> (the `?ldccp` CCP-copy routine + the `boot:` LC arming read); (2) the canonical Apple Language Card
> write-enable flip-flop behavior — **MAME `src/devices/bus/a2bus/ramcard16k.cpp` `do_io()`** (the device
> literally named "Apple II 16K Language Card") + **Sather, *Understanding the Apple II/IIe*, ch. 5** (the
> 74LS175 pre-write count flip-flop); (3) a **fresh live instruction-step + LC-state-correlation trace** of
> the real apl2cpm3 Disk 1 on the slot-4 SoftCard+Videx board, instrumented in a throwaway worktree probe
> (since reverted — the tree carries none), which **reproduced the bug AND validated the fix to `A>`**; (4)
> ADR 0018 + 0018-A + 0018-B (this sub-arc's parents); ADR 0014 Decision 4 (PR-E, the Language Card);
> ADR 0017 (the 2.2 boot); ADR 0015 (the dual-CPU board); the SoftCard research doc §2/§3/§7.
> **No implementation here** — this root-causes the fifth layer, designs the minimal fix, states the blast
> radius, and **classifies it SAFE**. Because it is SAFE and live-proven, it **hands to the Builder** via the
> re-pointed V80-2 / new V80-4 plan; the live apl2cpm3 `A>` is the arbiter.

---

## 1. Why this addendum exists

ADR 0018-B root-caused the third layer (the `?jsr65` bridge) and predicted the skew + bridge fix would reach
`A>`. The V80-2/V80-3 Builder then found, live, that **the bridge was already working with no machine change**
(the natural `$03C9` resume re-enters `L65A`; ~73 hand-backs observed — ADR 0018-B's "dead bridge" framing was
falsified), and shipped the genuine, un-fakeable milestone: with the raw-DOS33 `SectorOrderKind.Cpm3` skew + the
**real Videx firmware**, apl2cpm3's CRT80 `?icrt`/`?odcrt` program the Videx CRTC and paint the **genuine CP/M 3.1
sign-on** (`CP/M Version 3.0, 56K BIOS R6/89` / `46K TPA`) into the Videx `$CC00` VRAM. The only production change
that shipped was the additive `SectorOrderKind.Cpm3` enum+table (2.2 untouched, all 2.2 gates green live).

**Then a FIFTH layer appears, and ADR 0018-B §8 OQ3 anticipated a tail but not this one.** After the sign-on, the
CCP takes control and the banked BDOS `RET`s to a **zeroed** Z80 `$1901` (phys `$2901`) and NOP-slides — no `A>`,
reproduced byte-identically. The Builder diagnostic-classified it **BUCKET A (a load/banking defect, NOT a
Z80-core / translation mis-execution)** and pinned the mechanism to the Language Card. Because the suspected fix
touches the **shared** Language-Card model (PR-E — used by DOS/Applesoft/the base boot/CP/M 2.2), and ADR 0018-A
Decision A1 + the V80-2 hard constraints put the LC-banking model off-limits for that apl2cpm3-scoped PR, the
Builder **STOPPED per protocol and escalated.** That was the right call.

This addendum **confirms the Builder's BUCKET-A diagnosis against the canonical hardware AND a fresh live trace**,
designs the minimal fix, and — crucially — shows the fix is **not a risky departure from the working machine but a
*correction toward* the documented 74LS175 hardware**, with the existing 2.2 + LC + base-machine gates as the
un-fakeable guard. It is **SAFE**, and it **reaches `A>` live**.

## 2. Ground truth: how apl2cpm3 copies the CCP into LC bank 2, and what the real LC does

### 2.1 The CP/M-3 loader's CCP copy (apl2cpm3 `BOOT.MAC`, `?ldccp` / `ld$rl1`)

CP/M 3 keeps the CCP in a banked region so the full TPA is free; the loader copies it into Language-Card RAM for
reload. The routine (`BOOT_MAC.txt:120-138`), reached from `BIOSKRN_MAC.txt:265` (`CALL ?ldccp`):

```
ld$rl1: LD   BC,0C80H    ; clone 3K
        LD   (0E08BH),A  ; select extra bank  -> Z80 $E08B = Apple $C08B (odd-address WRITE), A=0 from XOR A
        LDIR             ; move block: Z80 $0100 -> $B000  (= Apple $D000, via translation branch 2 = LC bank 2)
        LD   (0E083H),A  ; select TPA          -> Z80 $E083 = Apple $C083 (odd-address WRITE)
        RET
```

Two facts pin the bus behavior:
- **`LD (0E08BH),A` is a Z80 *write*.** Via `SoftCardTranslation` branch 3 (`$C000-$CFFF`→`$E000-$EFFF`, so the
  inverse `$E08B`→`$C08B`), it is an **odd-address WRITE to Apple `$C08B`** — a bank-2 select. The *value* (`A=0`)
  is irrelevant; the soft switch responds to the address. A Z80 `LD (nn),A` does a single bus write with **no
  6502-style phantom read** of the destination (Zilog Z80 manual — `32`, 4 M-cycles, one memory write).
- **The `LDIR` writes go through `$B000` (Z80) → `$D000` (Apple)** = the LC `$D000` bank region (translation
  branch 2). Those are RAM writes; whether they land depends entirely on the LC write-enable latch state at that
  moment.

### 2.2 The real LC write-enable flip-flop — TWO separate latches (MAME `ramcard16k` / Sather)

The canonical `do_io(offset, writing)` (MAME `ramcard16k.cpp`, the "Apple II 16K Language Card") models the
74LS175 as **two independent bits**:

```
if ((offset & 1) == 0) { m_prewrite = false; inh &= ~INH_WRITE; }  // EVEN access clears count AND write-enable
if (writing)           { m_prewrite = false; }                     // ANY write clears the COUNT only (NOT INH_WRITE)
else if ((offset & 1) == 1) {                                      // odd READ
    if (!m_prewrite) m_prewrite = true;                            //   first odd read: arm the count
    else             inh |= INH_WRITE;                             //   second consecutive odd read: enable writes
}
```

The load-bearing, non-conventional facts:
1. **Write-enable (`INH_WRITE`) is cleared ONLY by an EVEN-address access.** It is *never* cleared by an
   odd-address access — read or write.
2. **A WRITE (any address) clears the pre-write *count* (`m_prewrite`) only** — it has **no effect on `INH_WRITE`
   if write-enable was already set.** ("has no effect on write-enable if writing was enabled already" — the MAME
   comment verbatim.)
3. **The count and the write-enable are separate latches.** Two consecutive odd READS set write-enable; once set,
   it **persists** through bank-selects, RAM writes, and odd-address writes — until an **even** access clears it.

So on real hardware, the `?ldccp` sequence lands because: write-enable was armed earlier by **two odd reads** (the
`boot:` `LD A,(0E081H)` arming sequence — `$C081` is odd), stays latched, and the intervening **odd-address write**
`LD (0E08BH),A` (bank-2 select) **does NOT clear it** — it only clears the count. The `LDIR` then writes into a
write-enabled bank 2.

## 3. The actual bug: our LC conflates the two latches and clears write-enable on ANY non-qualifying access — including the odd-address bank-2-select WRITE

`src/CpuEmulator.Peripherals/Apple2LanguageCard.cs` (`Access`, the `$C08x` truth table) collapses the two latches
into one `_writeEnabled` driven by one `_armCount`:

```csharp
bool qualifies = isRead && (o & 0x01) != 0;     // an ODD-address READ
if (qualifies) { if (_armCount < 2) _armCount++; _writeEnabled = _armCount >= 2; }
else           { _armCount = 0; _writeEnabled = false; }   // <-- clears write-enable on ANY non-qualifying access
```

The `else` branch fires on **any** access that is not an odd read — **including an odd-address WRITE** like
`LD (0E08BH),A` (`STA $C08B`). Per MAME/Sather, an odd-address write must clear **only** the count (`_armCount`),
**not** `_writeEnabled`. As written, the bank-2-select write immediately **write-protects** the card, so the
following `LDIR` into the `$D000` bank-2 region is **silently dropped — LC bank 2 stays zeroed.** Every later
banked operation that dereferences the CCP region (the `$1901` pointer chain) lands on zeros → the `RET` into
zeroed `$1901` → the NOP-slide → no `A>`. This is precisely the Builder's BUCKET-A pin.

### 3.1 Live proof (instruction-step + LC-state correlation; throwaway probe, reverted)

A throwaway probe instrumented the LC's `Access` and ran the **real apl2cpm3 Disk 1 on the slot-4 SoftCard+Videx
board** (the shipped board construction; `SectorOrderKind.Cpm3`; real Videx firmware). It logged, for every
`$C08x` access, the offset / read-vs-write / the live `_writeEnabled` / the bank / `_armCount`, and tracked a
*shadow* "fix model" (MAME `do_io`) in parallel. Two runs:

**Run A — current model (the bug):**
| Signal | Value |
|---|---|
| Total `$C08x` accesses before the wedge | **999** |
| `_writeEnabled` ever true during boot | **True** (first true at access #2, a `$C081` **read** — the card DOES arm via two odd reads; the "single-read" worry is refuted) |
| LC bank-1 nonzero bytes | **1494** (the `?rlccp` bank-1 copy *worked* — write-enable survived there) |
| **LC bank-2 nonzero bytes** | **0 / 4096** (the CCP copy was dropped) |
| The decisive access | `acc#998 $C08B isRead=False odd=True \| CURRENT writeEnabled=False \| FIX writeEnabled=True` |
| phys `$2901` (Z80 `$1901`, the RET target) | `$00` (zeroed — the wedge) |

The decisive line is the un-fakeable signature: the last bank-2-selecting access is the `LD (0E08BH),A`
**odd-address write**; under the **current** model write-enable is `False` at that instant (the write cleared it),
so the `LDIR` is dropped; under the **MAME-faithful fix** write-enable is `True` (the odd write preserves the
already-set latch).

**Run B — the MAME-faithful fix applied (`even clears WE; odd write clears count only; odd read arms/enables`):**
| Signal | Value |
|---|---|
| Total `$C08x` accesses (boot ran FAR past the old wedge) | **83609** |
| LC bank-1 nonzero bytes | 2099 |
| **LC bank-2 nonzero bytes** | **3026 / 4096** (the CCP copy LANDS) |
| Decoded **Videx 80-col VRAM** | `CP/M Version 3.0, 56K BIOS R6/89 … A> … 46K TPA` |
| **Videx VRAM contains `A>`** | **True** |

**With the fix, the live apl2cpm3 Disk 1 reaches the CP/M 3.1 `A>` prompt on the Videx 80-column console.** This
is the un-fakeable arbiter (ADR 0018 Decision 4) — the genuine CCP prompt, decoded off the live VRAM, not a
heuristic. (The 40-col Apple page still shows `APPLE ][`; apl2cpm3 routes its console to the Videx, so the `A>`
appears there — which is also the V80-3 headline, see §6.)

### 3.2 Why CP/M 2.2, DOS 3.3, Applesoft, and the base boot are unaffected (the invariant — live-verified)

The fix only changes one thing: **an odd-address WRITE to `$C08x` no longer clears `_writeEnabled` (it still
clears `_armCount`); even accesses still clear both; two odd reads still enable.** For any path to regress, it
would have to (a) latch write-enable, then (b) execute an odd-address `$C08x` WRITE, and (c) *depend* on that write
turning write-protect back on — a behavior the real 74LS175 never had. Real Apple firmware was written against
real hardware, so it cannot depend on a behavior the hardware lacks. Concretely, verified **live with the fix
applied** (throwaway worktree; all real suites green):

- **The 12 LC unit tests all pass** — including the two that appear to "lock" the old behavior:
  `A_write_between_the_reads_resets_the_pre_write_flip_flop` (it asserts the *count* reset, which the fix
  preserves — one read + a write + one read still ≠ two consecutive reads) and
  `An_even_address_read_after_write_enable_disables_writes_again` (it asserts the *even-access* clear, which the
  fix preserves). **No existing LC test asserts write-enable is cleared by an odd-address write** — that behavior
  was an implementation artifact, not a contract.
- **The load-bearing 2.2 gates pass byte-for-byte:** the **CPM-4 committed-hash `A>` gate**
  (`SoftCardBoardTests.Cpm_boots_to_the_A_prompt_on_the_interpreter`) and the **CPM-5 `ActiveIndex==0` Videx gate**
  (`SoftCardVidexBoardTests.Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter`). The 2.2 boot drives
  its own real firmware; the fix does not change its frame (the hash is unchanged). 2.2 arms/disarms write-enable
  via the canonical read/even-access pattern, not via an odd write.
- **Applesoft / base ][+ boot** runs entirely from system ROM (read-ROM mode) and never arms LC write-enable;
  **DOS 3.3** appears only as a sector-skew table, never via the LC write path.
- **Full Apple2 + SoftCard + dual-CPU + Apl2Cpm3 sweep: 212/212 real tests pass** with the fix (the lone "failure"
  was the throwaway probe's deliberate `Assert.Fail` data-dump).

This is the crux the fix respects, and it is **live-proven**, not asserted.

## 4. Decisions

### Decision C1 — Root cause: the LC write-enable flip-flop is modelled as a single latch that an odd-address *write* clears; the real 74LS175 keeps write-enable across odd-address writes. The fix is a **two-latch correction to `Apple2LanguageCard.Access`** that makes the model MORE faithful to the documented hardware. This is **NOT** a Z80-core, translation, or `Machine`/handoff change.

The bug is in the LC's `$C08x` write-enable decode (`Apple2LanguageCard.Access`), the PR-E truth table. It is
**not** a Z80-core change (ADR 0018-A A1 stands), **not** a translation change (`SoftCardTranslation` is correct —
branch 2 `$B000`→`$D000` and branch 3 `$C000`→`$E000` map exactly as the CCP copy needs), **not** a
`Machine`/`ICoprocessorControl` handoff change (ADR 0018-B's bridge works as-is), and **not** a skew change
(`SectorOrderKind.Cpm3` stands). The Z80 executes correct code on correctly-translated addresses; the only defect
is that the bytes the CCP copy should have written never landed, because the LC dropped them.

### Decision C2 — The fix shape: separate the pre-write COUNT from the write-enable LATCH in `Access`, per MAME `ramcard16k` `do_io`. **Single method body, no signature/seam change, no new abstraction.**

The corrected decode (the exact body is a Builder one-liner; the SHAPE is fixed here):

```csharp
// Two latches, per the 74LS175 (MAME ramcard16k do_io / Sather ch.5):
//   EVEN access      -> clear the count AND clear write-enable
//   odd-address WRITE -> clear the count ONLY (write-enable, if already set, SURVIVES)
//   odd-address READ  -> 1st arms the count; 2nd (consecutive) sets write-enable
if ((o & 0x01) == 0)      { _armCount = 0; _writeEnabled = false; }       // even
else if (!isRead)         { _armCount = 0; }                              // odd write: count only
else { if (_armCount < 2) _armCount++; if (_armCount >= 2) _writeEnabled = true; }  // odd read
```

This is the *entire* production change. `_bank` / `_readRam` selection (lines 73-79) and `ApplyMapping` (the
Remap calls) are unchanged. The method signature, the IOU delegation (`Apple2Iou` already threads `isRead`
faithfully — a `$C08x` write routes to `Access(o, isRead:false)`), the peek-free invariant, the Remap/JIT
invalidation, and every board's LC construction are all unchanged.

**Optional hardening (Builder's call, not required for `A>`):** the `_bank` select is read-ROM/read-RAM-source
decoded on every `$C08x` access including writes, which is already correct (the bank latch is separate from
write-enable). Leave it.

### Decision C3 — Classification: **SAFE.** A contained, single-method LC correction toward documented hardware; the existing 2.2 + LC + base-machine gates are the un-fakeable guard, and the live boot reaching `A>` is the positive proof. **NOT RISKY** — it touches no Z80 core, no shared-translation semantics, no dual-CPU handoff.

The prompt's gate is: SAFE (contained LC-banking/translation/board change) vs RISKY (Z80-core / shared-translation
semantics that could affect the working 2.2 machine). This fix is **SAFE**:
- It is a contained change to **one method** of **one peripheral** (`Apple2LanguageCard.Access`).
- It changes the LC model **toward** the canonical 74LS175 (MAME `ramcard16k` / Sather) — it removes a
  hardware-incorrect behavior (odd write clears write-enable), it does not invent one.
- The shared consumers (DOS 3.3, Applesoft, base ][+ boot, **CP/M 2.2**) are **live-verified unchanged**: the
  CPM-4 committed-hash gate, the CPM-5 `ActiveIndex==0` gate, the 12 LC unit tests, and the full 212-test
  Apple2/SoftCard/dual-CPU sweep all pass with the fix applied.
- It is **live-proven to reach the goal**: the real apl2cpm3 Disk 1 boots to `A>` on the Videx with the fix.

The one nuance that *would* have been RISKY — a "single odd read arms write-enable" change to the canonical
two-read rule (one competing hypothesis from the hardware analysis) — is **refuted by the live trace**: the card
*does* arm via two odd reads (`_writeEnabled` first true at access #2, a `$C081` read), so the two-read rule is
correct and **unchanged**. The fix is strictly the odd-write-preserves-write-enable correction. **No owner sign-off
on a risky change is needed.**

### Decision C4 — The un-fakeable gate: promote the apl2cpm3 boot gate from the shipped sign-on-on-Videx milestone to the **decoded `A>` on the Videx 80-col VRAM** (ADR 0018 Decision 4 / the headline). Add a **cheap LC-bank-2 load discriminator** upstream of the `A>` paint.

Keep the decoded-console gate (the shipped `Apl2Cpm3VidexFact`) as the primary oracle, and **strengthen its
assertion from the sign-on substring to the `A>` prompt** on the live Videx VRAM (the CCP prompt — the arbiter).
Because the bug is a precise dropped-copy, **also** add a fast, asset-gated discriminator the Builder can iterate
on, upstream of the full boot:
- **LC bank-2 load discriminator:** after the boot runs the CCP copy, assert LC bank 2 is **non-zero** (the CCP
  landed) — FAILS on the dropped copy (bank 2 all zeros), PASSES only when the odd-write-preserves-write-enable fix
  lets the `LDIR` land. (A test-visible `_bankD2` nonzero count, or a read of a known CCP byte at the banked
  `$D000` region.) This is the tight red→green proof of *this* fix.
- **(Already shipped, keep)** the `$31`-at-Z80-`$0100` skew discriminator + the ≥1 `Z80→6502` hand-back bridge
  discriminator.

The decoded `A>` on the live disk remains the headline arbiter (ADR 0018 Decision 4; never a pixel heuristic, never
a placeholder hash).

## 5. Consequences

**Good.**
- The fifth-layer blocker is root-caused to **one proven, quantified mechanism** (the odd-write-clears-write-enable
  latch conflation), confirmed against the **canonical 74LS175** (MAME `ramcard16k` `do_io` + Sather) **and** a
  fresh **live LC-state-correlation trace** that reproduces the dropped copy AND validates the fix to `A>`.
- **The fix makes the LC model MORE correct, not more special-cased.** It is the documented hardware; every
  Language-Card consumer benefits from the corrected truth table.
- **Live-proven to the deliverable:** with the fix, the real apl2cpm3 Disk 1 reaches **CP/M 3.1 `A>` on the Videx
  80-column console** (decoded VRAM: `CP/M Version 3.0, 56K BIOS R6/89 … A> … 46K TPA`). The headline.
- **2.2-safe, base-machine-safe — live-verified:** the CPM-4 hash gate, the CPM-5 `ActiveIndex==0` gate, the 12 LC
  unit tests, and the full 212-test Apple2/SoftCard/dual-CPU sweep all pass with the fix applied. **No Z80-core, no
  translation, no `Machine`/handoff, no skew, no 2.2-board change.**
- The escalation trigger (a risky shared-code change) is **resolved in the negative** — the LC truth-table
  correction is SAFE, contained, and hardware-faithful.

**Bad / accepted.**
- ADR 0018-B §8 OQ3's "possible tail" is realized as this fifth layer; ADR 0018 Decisions 1/2/4/5, ADR 0018-A A1
  (no Z80 change), and ADR 0018-B's bridge-works-as-is finding all **stand**. Only the implicit assumption that
  the LC model was complete is corrected.
- Two existing LC unit tests (`A_write_between_the_reads_resets_the_pre_write_flip_flop`,
  `An_even_address_read_after_write_enable_disables_writes_again`) keep passing but their *comments* describe the
  old single-latch mental model; the Builder should refresh the comments to the two-latch (count vs write-enable)
  model and **add** a positive test: "an odd-address WRITE between two odd reads does NOT lose an
  already-set write-enable" (the new contract — the un-fakeable lock on this fix).
- The apl2cpm3 boot gate's assertion strengthens from the sign-on substring to the `A>` prompt — a stricter,
  more honest oracle (no new asset, same decode).

**Reversibility.** High. The fix is a single-method edit to `Apple2LanguageCard.Access`; revert → the
single-latch state (apl2cpm3 drops the CCP copy, 2.2 unaffected — its frame is identical either way because 2.2
never relies on the odd-write behavior). No core / translation / handoff / skew change to reverse.

## 6. What changes for the Planner / Builder

- **V80-2 is SHIPPED as the sign-on-on-Videx milestone (PR on `feat/apl2cpm3-v80-2-combined-fix`).** Do not
  reopen it. The fifth-layer fix lands as a **new row V80-4** (the LC write-enable correction → CP/M 3.1 `A>` on
  the Videx, which simultaneously closes V80-3's `ActiveIndex==1` headline — see below).
- **V80-4 = the LC write-enable two-latch correction (Decision C2) → live `A>`.** Single-method change to
  `Apple2LanguageCard.Access`. Its gate: the live apl2cpm3 Disk 1 decodes **`A>`** on the Videx VRAM (strengthen
  the shipped `Apl2Cpm3VidexFact` from the sign-on substring to `A>`), plus the cheap LC-bank-2-nonzero
  discriminator (Decision C4), plus a new positive LC unit test (odd write preserves write-enable). The
  load-bearing 2.2 no-regression gates (CPM-4 hash, CPM-5 `ActiveIndex==0`) + the 12 LC unit tests **MUST run live
  and stay green** in the landing PR — they are the SAFE-classification guard (all verified green in the Architect
  probe).
- **V80-3 (the 80-col Videx `ActiveIndex==1` headline) is unblocked by V80-4 and likely closes in the same work.**
  The live trace shows the `A>` paints on the **Videx** VRAM (the console is already routed there). The remaining
  V80-3 question is purely the **auto-switch trigger**: the Builder's blocker note observes apl2cpm3 paints VRAM
  linearly at `$CC00`/`$CD00` and may not bank-switch via `$C0B8-$C0BF` in the window the current
  `videx.ActiveChanged` watches, so `ActiveIndex` may not flip to 1 on its own. That is a V80-3-time question — a
  contained `DisplayMultiplexer`/Videx auto-engage-trigger decision (does the CRTC program itself signal
  engagement?), **not** a new class of problem, and **not** in V80-4's LC scope. Pin it once `A>` lands: if the
  CRTC-program-implies-engage trigger needs a small Videx change, it is a separate, contained V80-3 row (the Videx
  is already wired — ADR 0016/0018 §1.3). **Do not widen V80-4 to chase it.**
- **No owner sign-off needed** — the fix is SAFE (Decision C3). Hand V80-4 to the Builder.

---

## 7. Related decisions

- **ADR 0018-B** (sibling): its `?jsr65` bridge analysis is **superseded by the live finding that the bridge works
  as-is** (the Builder confirmed ~73 hand-backs; no plumbing was added). ADR 0018-B's §8 OQ3 "possible tail" is
  this fifth layer. ADR 0018-B Decision B1's "not a Z80-core change" principle **stands and is reinforced**.
- **ADR 0018-A** (sibling): Decision A1 (no Z80-core change) and A2 (the `SectorOrderKind.Cpm3` raw-DOS33 skew)
  **stand** — A2 shipped in the V80-2 PR. This addendum confirms the residual is the LC banking latch, not the CPU
  and not the skew.
- **ADR 0018** (parent): Decisions 1 (slot-4 — V80-1, shipped), 4 (the decoded-`A>` gate — strengthened here from
  sign-on to `A>`), 5 (asset) **stand**. Decision 2's "no new skew" was already superseded by 0018-A's `Cpm3`.
- **ADR 0014 Decision 4 (PR-E, the Language Card)** — this addendum corrects the PR-E `$C08x` write-enable truth
  table to the canonical 74LS175 two-latch model (MAME `ramcard16k`). The Remap/JIT-invalidation and bank/read
  selection of PR-E are unchanged.
- **ADR 0017** (the 2.2 boot) — confirmed unaffected: the 2.2 `A>` frame is byte-identical under the fix
  (CPM-4 hash gate green live). The 2.2 board never relies on the odd-write-clears-write-enable behavior.
- **ADR 0015** (dual-CPU board) — the Z80 reset/handoff/translation model is confirmed correct and unchanged.
- **SoftCard research doc** §2 (the translation table: branch 2 `$B000`→`$D000` LC bank 2, branch 3 `$C000`→`$E000`
  so `$E08B`→`$C08B`) and §3/§7 (the LC write-enable "two consecutive reads" + the ][+ "soft switches respond to
  any access" nuance) — the fix aligns the model to both.

## 8. Open questions (narrowed)

1. **V80-3 auto-engage trigger** (§6): once `A>` lands on the Videx VRAM, does `DisplayMultiplexer.ActiveIndex`
   flip to 1 on its own, or does apl2cpm3's linear `$CC00`/`$CD00` paint never fire the current `ActiveChanged`
   bank-switch trigger? If the latter, a contained V80-3 decision on whether the CRTC-program-implies-engagement
   (the `?icrt` slot-3 `$C0Bx` CRTC write) should drive `SetActive(true)`. **Not in V80-4's LC scope; pin live
   once `A>` is reached.** Not a new ADR unless it needs a Videx-model change.
2. **Data-track skew past the system tracks** (ADR 0018 OQ3 / 0018-B OQ2): now testable end-to-end with the boot
   reaching `A>` and the BIOS reading data tracks via `?fdrwts`. Re-verify `SectorOrderKind.Cpm3` decodes the data
   tracks the running BIOS reaches. Escalate only on a live divergence. Not pre-designed.
3. **Read-ROM-while-writing-RAM split** (PR-E's noted JIT-tier follow-on, `Apple2LanguageCard.cs:104` comment):
   the LC currently maps a single backing per page (read source = write source). apl2cpm3's `?ldccp` writes bank 2
   while read-source is whatever `$C08B` selected — the live trace shows the copy lands correctly under the
   single-backing model (the `LDIR` reads from `$0100` TPA and writes to the banked `$D000`, both correctly
   mapped), so this split is **not** needed for `A>`. Confirmed out of scope; left as the documented JIT-tier
   follow-on it already was.

---

*End of ADR 0018-C. The V80-2/V80-3 fifth-layer blocker is the Language-Card write-enable flip-flop: apl2cpm3's
CP/M-3 loader (`?ldccp`, `BOOT.MAC`) copies the CCP into LC bank 2 with `LD (0E08BH),A` (an odd-address WRITE to
Apple `$C08B`, bank-2 select) → `LDIR` → `LD (0E083H),A`. Our `Apple2LanguageCard.Access` clears `_writeEnabled`
on ANY non-qualifying access — including that odd-address write — so bank 2 is remapped write-protected and the
`LDIR` is silently dropped; LC bank 2 stays zeroed and the banked BDOS `RET`s into a zeroed Z80 `$1901`,
NOP-sliding with no `A>`. The real 74LS175 (MAME `ramcard16k` `do_io` + Sather ch.5) has TWO separate latches: an
odd-address WRITE clears only the pre-write COUNT, never the write-enable LATCH; write-enable is cleared ONLY by an
EVEN access. The fix is a single-method two-latch correction to `Access` (even clears both; odd write clears the
count only; odd read arms/enables) — making the model MORE faithful to documented hardware. Classified **SAFE**: a
contained single-peripheral change, no Z80-core / no translation / no handoff / no skew / no 2.2-board change.
Live-proven on the real apl2cpm3 Disk 1 (throwaway probe, reverted): with the fix, LC bank 2 receives the CCP copy
(3026/4096 bytes, was 0), the boot runs far past the old wedge (83609 `$C08x` accesses, was 999), and the Videx
80-col VRAM decodes **`CP/M Version 3.0, 56K BIOS R6/89 … A> … 46K TPA`** — CP/M 3.1 reaches `A>`. The 2.2 CPM-4
committed-hash gate, the CPM-5 `ActiveIndex==0` gate, the 12 LC unit tests, and the full 212-test
Apple2/SoftCard/dual-CPU sweep all pass under the fix — the un-fakeable 2.2-safe guard. The competing "single odd
read arms write-enable" hypothesis is REFUTED live (the card arms via two odd reads — write-enable first true at a
`$C081` read). No owner sign-off needed: SAFE, hand V80-4 to the Builder. V80-3's `ActiveIndex==1` headline is
unblocked behind it (the `A>` already paints on the Videx; the only residual is the auto-engage trigger, a
contained V80-3-time question). Further layers: the data-track skew re-verify (now testable to `A>`) is the only
flagged tail, escalated only on a live divergence — not pre-designed.*
