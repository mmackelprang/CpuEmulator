# ADR 0018 — Booting apl2cpm3 (CP/M 3.1) on the SoftCard + Videx: a configurable per-board SoftCard slot, the CP/M-3 boot chain, and the first Videx-80-column CP/M console

> **Status:** PROPOSED (Architect phase, Apple ][+ arc — apl2cpm3 CP/M 3.1 / Videx-80-col sub-arc).
> **Builds on ADR 0015** (the dual-CPU board), **ADR 0016** (the Videx display seam + assets), and **ADR 0017**
> (the SoftCard CP/M 2.2 boot-to-`A>` correction — the per-track skew, write-only control toggle, and the
> per-instruction run-loop yield). This ADR does **not** relitigate those; it reuses every one of their decisions
> unchanged and adds exactly what apl2cpm3 needs on top.
> **Date:** 2026-06-22
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect, grounded in a **live instruction-step boot
> trace of the real apl2cpm3 Disk 1** on the shipped `SoftCardBoard` (a throwaway probe, since reverted).
> **Reads as ground truth:** the live boot trace (this session) over the real apl2cpm3 `CPM3.1_Disk_1.dsk`; the
> package README (`CPM3.1_Z80_Softcard.txt`); ADR 0015 (dual-CPU model), ADR 0016 (Videx seam + asset posture),
> ADR 0017 (the 2.2 three-defect cascade + the per-track skew + the honest-gate discipline).
> **No implementation here** — this decides the shapes + seams + the PR sequencing the Planner executes. Every PR's
> un-fakeable gate is the **live apl2cpm3 disk**; the arbiter is **CP/M 3.1 `A>` on screen** (40-col first, then
> 80-col on the Videx).

---

## 1. Context — what apl2cpm3 is, and the live boot trace (what actually happens on the real disk today)

### 1.1 The asset

The **apl2cpm3 / CPM3.1_Z80_Softcard** package (Bobbi, 2019; a clean-up of Werner K.G. Münchheimer's 1989 Apple II
CP/M 3 port) is **CP/M 3.1 ("CP/M Plus")** for the Microsoft Z-80 SoftCard — a *different and later* OS than the
CP/M **2.2** that ADR 0017 brought to `A>`. Seven 143,360-byte `.dsk` images (`CPM3.1_Disk_1`…`_7`); **only Disk 1 is
bootable** (the other six share no boot sector — confirmed byte-for-byte; they are tool/help/data disks per the README).
The package README pins the rigid hardware config:

- **Z80 SoftCard in slot 4** (`$C400`), Disk II in slot 6, an **80-column card in slot 3**, 64K (Language Card in
  slot 0 or a //e).
- **46K TPA; does NOT use banked memory** (so the dual-CPU model + translation from ADR 0015 are sufficient — no new
  banking abstraction needed).
- The BIOS `icrt` routine inits the 80-column card; warm-boot re-init is `CALL DC21` at Z80 `$D9AB` (the README's patch
  NOPs it to stop the screen clearing on every transient — `CD 21 DC` appears 4× in Disk 1, confirming the Videx
  driver lives in the Z80-side BIOS).

The disk's own ASCII confirms the OS family: `CP/M V3.0 Loader / Copyright (C) 1982, Digital Research`, `CPM3.SYS`,
`BOOTLDR COM`, `CPMLDR COM`. The boot chain is the standard CP/M-3 multi-stage loader, materially different from 2.2's
single boot2:

```
$C600 Disk II boot ROM → boot1 ($0800) → boot2 → CPMLDR ("CP/M V3.0 Loader") → loads CPM3.SYS → Z80 handoff → BIOS → A>
```

### 1.2 The live trace (real apl2cpm3 Disk 1, instruction-stepped on the shipped SoftCardBoard)

Booting the real Disk 1 on the current board (control port at **slot 5 / `$C500`**, the `SectorOrderKind.Cpm` per-track
skew from ADR 0017) produces, depending on the control-port slot:

| Config | Result (live) |
|---|---|
| **Slot 5 (`$C500`, our shipped wiring), CP/M skew** | The 6502 boot prints **`NO Z80 FOUND`** (apl2cpm3's equivalent of 2.2's `CAN'T FIND Z80 SOFTCARD`) and drops to the monitor (`PC=$FD1D`). The control port logged **0 writes / 0 toggles** — the Z80 **never activates**. |
| **Slot 4 (`$C400`, apl2cpm3's wanted slot), CP/M skew** | The 6502 boot1 runs, reads the system tracks, writes `$C400` **once** (toggle=1) at ~2.4 M cycles → **the Z80 activates** (`CoprocessorActive=True`). The CPMLDR loads into RAM (the `CP/M V3.0` sign-on string lands at `$1C3E`; RAM `$1100–$23FF` is populated). The screen shows a partial `APPLE ][`. **No crash, no `NO Z80 FOUND`.** |
| **Slot 4, DOS-3.3 skew** | Crashes with an undefined-opcode trap (wrong skew — confirms the skew family matters and CP/M is correct). |

**The slot is the gating blocker, and it is unambiguous.** apl2cpm3 hardcodes `STA $C400` (slot 4) to start the Z80
— I found the literal `8D 00 C4` (`STA $C400`) **twice** in track 0 (boot1's start-Z80 routine, followed by `RTS`, at
boot-sector offsets `$C7` and `$DBA`). Our board decodes the control port at `$C500`; the `$C400` writes hit the empty
`$C400` MMIO hole (no peripheral), the Z80 is never released, and the boot ROM's detect routine reports `NO Z80 FOUND`.
With the control port at `$C400`, the handshake fires.

**The residual (after the slot is fixed): the Z80 starts but NOP-slides.** With slot 4, the Z80 activates at reset
`PC=$0000` (= 6502 `$1000` via translation branch 1, `+$1000`) — but 6502 `$1000` is **all zeros**. The Z80 crawls a
NOP-slide through its entire 64K (`$005B → $0068 → … → $00F7 → $0003 → …`, ~+13 bytes per 2 M cycles, wrapping) and
**never reaches the CPMLDR** that is sitting loaded at `$1100+`. It never toggles control back to the 6502 (no BIOS
round-trip), so nothing further paints. This is **not slow — it is stuck** (confirmed to 100 M cycles).

**Interpretation.** The CP/M-3 boot's Z80 entry handoff is one stage short: boot2/CPMLDR must place a **Z80 entry
stub/jump at `$1000`** (the Z80's `$0000`) that vectors to the loaded loader/CPMLDR, OR the 6502 must hand the Z80 a
non-`$0000` start. The loader bytes *are* in RAM (so the disk read + skew are fundamentally working through the system
tracks), but the **entry vector at the Z80 reset address is absent**. This is the **same class** of residual ADR 0017
scoped to its `$1010` bridge (Decision 4) — a build-time bring-up item closed against the running disk, **not** a new
abstraction. The difference: for 2.2 the residual evaporated (fixes 1–3 were complete); for CP/M-3 the loader chain is
longer, so this residual is real and must be closed against the live disk. **It is the only behavioural unknown** — and
it is downstream of, and gated by, the slot fix.

### 1.3 What already works (do NOT redesign)

- **The dual-CPU board** (ADR 0015): `CoprocessorSpec`, `SoftCardTranslation` (the 6-branch table), the
  `TranslatingAddressSpace` wrapper, the interpreter-tier Z80 — **all reused unchanged**. apl2cpm3 needs no banking
  (46K TPA, no banked memory), so the existing translation is sufficient.
- **The control-port semantics** (ADR 0017 Decisions 2 + 3): write-only toggle, open-bus read, per-instruction
  run-loop yield. The live slot-4 trace shows the Z80 activating on the single `$C400` write exactly as these decisions
  intend — **reused unchanged** (only the *address* the port decodes changes).
- **The 64K Language Card** (ADR 0014 Decision 4, ridden by the IOU): the SoftCard board already wires the LC via the
  IOU (`new Apple2Iou(state, lc, …)`), and the translation routes the Z80's `$B000`/`$D000` onto the LC bank-2 / ROM
  region. The live trace **confirms this works**: the CPMLDR loaded into low RAM and the loader bytes are addressable
  through the translation. **No new 64K work is needed** — the LC is structurally present and the CP/M-3 46K TPA fits
  the existing RAM map. *(This answers the prompt's "does the SoftCard board wire the LC?" — yes, confirmed live.)*
- **The Videx** (ADR 0016, PR-N): the IOU delegates `$C0B0–$C0BF` (slot 3's CRTC) to the `VidexVideoterm`; the `$C800`
  firmware window + `$CC00` VRAM are wired on `SoftCardVidexBoard`; the `DisplayMultiplexer` auto-switch
  (`videx.ActiveChanged → SetActive`) is live. apl2cpm3's BIOS `icrt` drives the Videx via slot-3 `$C0Bx` — exactly
  where our board listens. **The Videx path is wired and waiting; it is gated only on the boot reaching the BIOS.**

### 1.4 The strategic difference from the 2.2 master (ADR 0017 Decision 6 / CPM-5)

ADR 0017 / CPM-5 established that the cached **2.2** master and all five 2.2 candidates are **40-column** consoles —
none auto-engages the Videx (zero `$C0Bx`), so the "CP/M auto-widens to 80-col" headline was honestly narrowed to
"40-col CP/M boot + a direct Videx render." **apl2cpm3 is the missing piece of that headline.** It *requires* an
80-column card (README), and its BIOS `icrt` drives the Videx CRTC. So apl2cpm3 is the **first realistic candidate to
make `DisplayMultiplexer.ActiveIndex==1` true from a real CP/M boot** — the genuine 80-col CP/M console the 2.2 arc
could not produce. This is the headline of this sub-arc.

---

## 2. Decisions

### Decision 1 — The SoftCard control-port slot becomes a **per-board parameter**; apl2cpm3 sets it to slot 4 (`$C400`). The shipped 2.2 board stays slot 5 (`$C500`).

The control-port decode address is currently a hard-coded constant (`SoftCardBoard.ControlPortBase = 0xC500`). The
live trace proves apl2cpm3 hard-codes `STA $C400` (slot 4) — a different physical slot. Rather than move the existing
2.2 board (which boots to `A>` at slot 5 and must not regress), make the slot **configurable per board**.

**Shape (additive; the existing 2.2 board's behaviour is byte-for-byte unchanged):**

```csharp
// SoftCardBoard — the slot becomes a parameter, defaulted to the shipped slot-5 value (no caller breaks):
public const uint ControlPortBaseSlot5 = 0xC500;   // the shipped 2.2 default (unchanged)
public const uint ControlPortBaseSlot4 = 0xC400;   // apl2cpm3 (README: SoftCard in slot 4)

// The slot is validated to lie inside the board's $C000-$C7FF I/O band and to be page-aligned ($Cn00).
public static BoardSpec Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom,
                             uint controlPortBase = ControlPortBaseSlot5)   // NEW optional arg, defaulted
{ … new PeripheralSlot(ControlPortName, controlPort, controlPortBase, 0x100) … }
```

`SoftCardVidexBoard.Spec` gains the same optional `controlPortBase` argument (defaulted to slot 5 for back-compat;
apl2cpm3's surface/gate passes `ControlPortBaseSlot4`). **No change to `SoftCardControlPort`, `CoprocessorSpec`, or
`SoftCardTranslation`** — the translation's branch-5 (`$E000→$C000`) already maps the Z80's `$E400` write back to the
6502 `$C400` (the Z80 hands control back through whatever slot the 6502 started it on; the slot is symmetric under the
translation, since both `$E400`→`$C400` and `$E500`→`$C500` are branch-5).

**Rationale.** The slot is a *board-config* property (which physical slot the card sits in), not an architectural
invariant — the real hardware is jumper/slot-selectable, and the disk's firmware hard-codes its expected slot. A
per-board parameter is the minimal, honest model: the 2.2 board keeps slot 5 (where it boots), apl2cpm3 gets slot 4
(where it boots). The live trace is the un-fakeable proof that this single change flips `NO Z80 FOUND` → Z80-activates.

**Alternatives considered.**
- **(A) Move the shared board to slot 4 for both disks.** *Rejected* — it would regress the shipped 2.2 `A>` gate
  (the 2.2 master writes `$C500`; live-verified ADR 0017). Two disks, two slots; the slot must be per-board.
- **(B) Decode the control port at *both* `$C400` and `$C500` (a wide slot or two slots).** *Rejected* — it is a
  fiction (no real card answers two slots), it muddies the validator's MMIO-overlap checks, and it would let a disk
  that writes the "wrong" slot silently appear to work. One slot per board is the hardware truth.
- **(C) Auto-detect the slot by watching which `$Cn00` the boot writes.** *Rejected as speculative* — the slot is
  known from the asset (README + the disk's `STA $C400`); runtime sniffing is complexity with no payoff and would be
  un-gateable.

**Consequences.** *Good:* one optional, defaulted parameter unblocks apl2cpm3's handshake; the 2.2 board is untouched;
the validator's existing MMIO-containment check guards a bad slot. *Bad/accepted:* `SoftCardBoard`/`SoftCardVidexBoard`
gain one parameter (documented, defaulted); the per-board slot is a small new degree of freedom (gated by the live disk
and a slot-placement unit test).

### Decision 2 — The apl2cpm3 disk skew is the **same `SectorOrderKind.Cpm` per-track tables as 2.2** (boot table for tracks 0–2, data table for 3+) — live-verified; do NOT introduce a new skew kind unless the live boot forces one.

The live boot with `SectorOrderKind.Cpm` (the ADR 0017 per-track tables: boot `[0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]`
for tracks 0–2, data `[0,6,12,3,9,15,14,5,11,2,8,7,13,4,10,1]` for 3+) reads the apl2cpm3 system tracks **correctly**:
the CPMLDR loads into RAM intact (the `CP/M V3.0` sign-on lands at `$1C3E`, RAM `$1100–$23FF` populated), and the
6502 boot1 runs without the silent-BRK/monitor crash the wrong skew caused on the 2.2 candidates (CPM-5) and on
apl2cpm3 itself under the DOS-3.3 skew (live: undefined-opcode trap). **So the existing CP/M skew is correct for
apl2cpm3** — no new table, no new `SectorOrderKind`.

**Rationale.** apl2cpm3 is a SoftCard CP/M disk in the same 16-sector Apple CP/M format (research §4/§5); it shares the
SoftCard boot ROM/loader's boot-track interleave and the CP/M BIOS RWTS's data-track skew. The live trace confirms the
loader lands intact — the residual (the Z80 entry stub) is downstream of disk I/O, not a skew artifact (a skew error
would corrupt the loader bytes; they are intact). *(The boot1 read-order table embedded in apl2cpm3's boot sector,
`[13,10,7,4,1,…,0]`, is the loader's sector-read sequence, not the disk's physical→logical map — distinct concerns; the
`DskFluxImage` physical→logical map is what `SectorOrderKind.Cpm` provides, and the live load proves it correct.)*

**Alternatives considered.**
- **(A) Assume CP/M 3 needs a different skew and add `SectorOrderKind.Cpm3`.** *Rejected — live-falsified* (the loader
  loads intact under the existing CP/M skew; adding a kind would be unused complexity).
- **(B) Defer the skew question to Builder bring-up.** *Partially adopted* — the *decision* is "the existing tables are
  correct" (live-verified for the system tracks). If, after the slot fix + entry-stub bring-up, a **data-track** read
  past track 3 proves to need a different skew (not yet observed — the boot crawls before seeking data tracks), that is
  a tightly-scoped Builder finding, not a new ADR (mirrors ADR 0017 Decision 4's discipline).

**Consequences.** *Good:* zero new skew surface; the per-track CPM tables are reused exactly. *Bad/accepted:* the
data-track skew past track 3 is verified only as far as the live boot reaches (the system tracks) — a documented
build-time tail, closed against the disk, escalated only if it diverges.

### Decision 3 — The CP/M-3 Z80-entry-handoff residual is a **Builder bring-up item against the live disk**, scoped exactly like ADR 0017 Decision 4's `$1010` bridge — NOT a new abstraction, and NOT pre-designed here.

With Decision 1 (slot 4) the Z80 activates and the CPMLDR loads; the residual is that the Z80 NOP-slides from `$0000`
because no entry stub/jump was placed at the Z80 reset address (6502 `$1000`). The decision:

- **The slot fix (Decision 1) is the gating change and lands first.** It is independently live-verified to flip the
  boot from `NO Z80 FOUND` to "Z80 activates + loader loads."
- **The entry-handoff residual is reverse-engineered against the running disk**, exactly as ADR 0017 Decision 4 did for
  2.2's `$1010` bridge: run the real boot, instruction-step the 6502's start-Z80 routine + boot2/CPMLDR, find where the
  Z80 entry vector should be written, and confirm whether it is (a) a faithful-emulation gap our seams must service
  (e.g. the 6502 must place a jump at `$1000`, or start the Z80 at a non-`$0000` PC the SoftCard latches), or (b) a
  per-instruction-yield interaction (the CP/M-3 loader's Z80↔6502 round-trips are more numerous than 2.2's — the same
  per-instruction yield from ADR 0017 Decision 3 should cover them, but the *first* handoff at `$0000` is the one to
  trace). **The scoped hypotheses, in order of likelihood (to be confirmed/falsified live):**
  1. **The Z80 reset PC.** The real SoftCard Z80 may not reset to `$0000`; if apl2cpm3's loader expects the Z80 to
     begin at the loaded loader (e.g. a SoftCard convention that the start address is latched, or the loader writes a
     `JP` at `$0000`), our Z80 core's `Reset()` → `PC=$0000` plus an empty `$1000` is the gap. *Likely the crux.*
  2. **boot2 places the entry stub late / on a track we stop reading.** If the `JP <loader>` at `$0000` is written by a
     boot2 stage that runs *after* the first `$C400` toggle (i.e. the 6502 and Z80 interleave more than once during
     load), the per-instruction yield must round-trip correctly through slot 4 — verify the Z80→6502 handback
     (`$E400`→`$C400`) actually fires (the live trace showed only **one** toggle, suggesting the handback never
     happens — which points back to hypothesis 1).
- **Do NOT pre-design a fix.** The Architect's job is to localize the *mechanism* (done: the entry vector at the Z80
  reset address is absent) and decide the *seam* (the existing dual-CPU handshake + the slot fix). The exact stub/PC
  behaviour is disk/loader data closed at build time against the oracle (ADR 0015 Decision 7's "run the real boot,
  don't hardcode" discipline).

**Rationale.** Pre-specifying a fix against a boot stage gated behind the not-yet-landed slot change would be the
speculative generality ADR 0015/0017 warned against. The honest scope: land the live-verified slot fix, then close the
entry handoff against the running disk — the same evidence-backed staging that brought 2.2 to `A>`.

**Consequences.** *Good:* the gating change (slot) is pinned by live evidence; the residual is localized to one concrete
mechanism (the Z80 entry vector) and bounded to a Builder iteration against the live disk. *Bad/accepted:* CP/M-3 `A>`
may need one or two Builder bring-up iterations after the slot fix (scoped; gated by the live disk; escalated only if it
reveals a missing disk/asset or a fundamental Z80-reset-semantics gap — see Decision 6).

### Decision 4 — The CP/M-3 boot gate is the **decoded `A>` / CP/M-3 sign-on text** (the ADR 0017 Decision 5 oracle), staged 40-col first, then 80-col — un-fakeable, asset-gated, never false-passing.

Reuse ADR 0017 Decision 5's gate philosophy exactly: decode the live console to ASCII and assert the **real CP/M-3
sign-on substring** (the disk's own bytes pin the target: `CP/M V3.0` / `Copyright (C) 1982, Digital Research`, and the
CCP `A>` prompt) + `CoprocessorActive` true. Two staged gates:

- **40-col CP/M-3 `A>`** (milestone 1): assert `A>` + a CP/M-3 sign-on line on the **Apple 40-col text page** (`$0400`),
  same decode as the 2.2 gate. This is reached as soon as the boot completes (Decisions 1 + 3), *before* the BIOS
  switches the console to the Videx — apl2cpm3's BIOS may paint its early sign-on to the 40-col screen before `icrt`
  engages the 80-col card, or it may go straight to 80-col; the live boot decides which, and the gate asserts whichever
  the disk actually does (no fake).
- **80-col CP/M-3 `A>` on the Videx** (the headline): assert the console text on the **Videx 80×24 render** (decode the
  `$CC00` VRAM through the synthetic char ROM, ADR 0016 Decision 4) **and** assert `DisplayMultiplexer.ActiveIndex==1`
  (the auto-switch engaged — `videx.ActiveChanged` fired). This is the **first time `ActiveIndex==1` is asserted
  against a real CP/M boot** (ADR 0017 Decision 6 / OQ2's open sibling-gate, now closeable with apl2cpm3).

Both gates are `[SoftCardCpmFact]`-style **skip-with-note when the apl2cpm3 asset is absent** (the fetch-on-demand
posture, Decision 5), and **never false-pass** (no pixel heuristic, no placeholder hash — a substring on decoded text).
Until the boot is green, the gates are honest skips/expected-fails (ADR 0017 PR-1's discipline: main stays green/honest).

**Rationale.** The interpreter-as-oracle principle demands the gate assert the actual decoded console — the same thing a
human reads as "CP/M 3.1 booted in 80 columns." The disk's ASCII pins the exact substrings, so the assertion is precise
and un-fakeable. Staging 40-col → 80-col lets each PR reach a verifiable milestone (ADR 0017's proven cadence).

**Consequences.** *Good:* every gate asserts a truth; the 80-col gate finally closes the `ActiveIndex==1`-from-real-CP/M
question. *Bad/accepted:* the gate decodes text + (for 80-col) the Videx VRAM — a few lines, strictly better than a
hash.

### Decision 5 — Asset posture: apl2cpm3 is **fetch-on-demand-and-cache, never vendored** (the ADR 0016 / Spectrum-ROM pattern), under a distinct cache name; the 7-disk set is supported but only **Disk 1 is required** for the boot gate.

apl2cpm3 carries CP/M 3 (DR 1982 grant — non-exclusive, ADR 0016 §6/research §6) **plus** Münchheimer's Apple II
system-specific BIOS. Treat it exactly like the SoftCard 2.2 `.dsk`: **fetch-on-demand-and-cache, never committed**,
owner sign-off required at PR time (ADR 0016 Decision 5).

**Shape (additive; mirrors `SoftCardCpm`):**

```csharp
// A new Apl2Cpm3 asset loader (sibling to SoftCardCpm), distinct cache path so it never clobbers the
// working 2.2 softcard-cpm.dsk:
//   <cache>/cpm/apl2cpm3/CPM3.1_Disk_1.dsk … _7.dsk   (each 143,360 bytes)
public static class Apl2Cpm3
{
    public const int DiskLength = 143360;
    public static string? TryGetBootDiskPath(string? root = null);   // Disk 1 — REQUIRED for the boot gate
    public static IBlockDevice LoadBootDisk(string? path = null);
    public static string? TryGetDiskPath(int n, string? root = null);// Disks 2-7 — OPTIONAL (data/tools/help)
}
// tools/get-apl2cpm3.{sh,ps1}: same fetch_one + length-sanity (143360) pattern as get-softcard-cpm,
// owner-configured source URL; all 7 disks optional except Disk 1.
```

**Disks 2–3 are NOT needed for the boot gate** — Disk 1 boots to `A>` standalone (README: "the day-to-day boot disk,
has everything needed to run the system"). Disks 2–7 (dev tools, utilities, help) are optional follow-ons (e.g. a
multi-drive UAT or `EMULA.COM` advanced-terminal test) — supported by the loader but not gated.

**Rationale.** Same licensing gray-area + same preservation-mirror provenance as the 2.2 disk → the same proven
fetch-cache posture. A distinct cache subdirectory (`cpm/apl2cpm3/`) guarantees the working 2.2
`cpm/softcard-cpm.dsk` is never clobbered (the prompt's explicit constraint).

**Consequences.** *Good:* reuses the shipped asset pattern; the 2.2 disk is isolated; only Disk 1 is required.
*Bad/accepted:* the boot gate skips-with-note until the owner configures the apl2cpm3 fetch URL + sign-off (same as
2.2 — non-blocking for the slot-fix PR, which has asset-free unit gates).

### Decision 6 — Escalations to the owner

| Item | Why it's an escalation | Recommended default |
|---|---|---|
| **apl2cpm3 asset fetch URL + sign-off** (Decision 5) | Same licensing/provenance gray-area as the 2.2 SoftCard `.dsk`; the package (cpm.z80.de / a preservation mirror) needs an owner-confirmed source. | Configure `tools/get-apl2cpm3.*` like `get-softcard-cpm.*`; the boot gate skips-with-note until then. The disk is already staged locally for this investigation. |
| **A fundamental Z80-reset-semantics gap** (Decision 3, hypothesis 1) | *If* the live bring-up shows apl2cpm3 depends on the SoftCard latching a non-`$0000` Z80 start PC (not just a stub the loader writes), that touches the dual-CPU handoff model (how the 6502 starts the Z80), not just a board param. | **Likely not needed** — the more probable cause is the loader writing a `JP` stub at `$0000` that our boot completes once the round-trip works; but flagged so it is not a silent surprise. Builder closes it against the live disk; escalate only if it needs a Z80-core reset change. |
| **Disks 2–7** (Decision 5) | Optional follow-on UAT (multi-drive, dev tools, `EMULA.COM`). | Not needed for `A>`; fetch only if a follow-on gate wants them. |

**No deep blocker is expected.** The banked-memory worry is explicitly ruled out (README: apl2cpm3 does NOT use banked
memory, 46K TPA), so the existing dual-CPU + translation + 64K-LC model is sufficient — **no secret banking need**. The
slot fix is live-verified to unblock the handshake. The only genuine unknown is the Z80-entry-handoff residual
(Decision 3), which is a bounded Builder bring-up, the same shape that closed 2.2.

---

## 3. PR decomposition (for the Planner)

Sequenced so the **first PR lands a live-verified, gating change** (the slot fix → Z80 activates), then each subsequent
PR advances the live boot one verified stage to **CP/M 3.1 `A>` in 40-col**, then the **80-col Videx headline**. Every
PR's un-fakeable gate is the **live apl2cpm3 Disk 1**; the arbiter is `A>` on screen (40-col, then 80-col).

**PR-1 — Configurable SoftCard slot (the live-verified gating fix) + asset loader + honest skipped gate.**
- Land Decision 1 (the per-board `controlPortBase` parameter on `SoftCardBoard`/`SoftCardVidexBoard`, defaulted to slot
  5 so the 2.2 board is byte-for-byte unchanged; apl2cpm3 passes slot 4). Add a **slot-placement unit test** (assert the
  control port decodes at `$C400` for the apl2cpm3 board and `$C500` for the 2.2 board — asset-free, un-fakeable).
- Land Decision 5's `Apl2Cpm3` asset loader + `tools/get-apl2cpm3.{sh,ps1}` (distinct `cpm/apl2cpm3/` cache path).
- Add the CP/M-3 boot gate **skip-with-note** (Decision 4, 40-col variant) — present but honestly skipped (asset absent
  on CI / expected-fail-documented when present, since PR-1 alone does not reach `A>`). **Main stays green/honest.**
- **Gate:** the slot-placement unit test (asset-free) is green; the 2.2 `A>` gate still passes unchanged (no
  regression); the apl2cpm3 boot gate is honestly skipped/expected-fail.

**PR-2 — CP/M-3 boot to `A>` on the 40-col console (close the Z80-entry-handoff residual against the live disk).**
- Bring the apl2cpm3 boot to `A>` on the live disk: close Decision 3's entry-handoff residual (Builder bring-up,
  instruction-stepped against the running disk — reuse the ADR 0017 per-instruction yield + the slot-4 handshake).
  Verify Decision 2's skew holds for the data tracks the completing boot now reaches.
- Complete Decision 4's 40-col gate: assert the decoded `A>` + CP/M-3 sign-on (`CP/M V3.0` / `1982, Digital Research`)
  on the `$0400` page + `CoprocessorActive`. Capture a human-visible `A>` PNG via `tools/BootProbe --cpm-screenshot`
  (extend it to take the apl2cpm3 board + slot).
- **Gate:** the live apl2cpm3 Disk 1 boots to **CP/M 3.1 `A>`** (40-col) — the decoded-text assertion passes. **This is
  milestone 1.**

**PR-3 — The 80-column Videx CP/M console (the headline): `ActiveIndex==1` from a real CP/M boot.**
- With the boot reaching the BIOS (PR-2), confirm `icrt` drives the Videx slot-3 CRTC (`$C0B0/$C0B1`) → the
  `DisplayMultiplexer` auto-switch fires (`videx.ActiveChanged → ActiveIndex==1`). Close any Videx-init bring-up against
  the live disk (the CRTC programming + VRAM writes flow through the already-wired `$C0Bx`/`$CC00` seams — no new
  abstraction).
- Complete Decision 4's 80-col gate: assert the decoded console text on the **Videx 80×24 render** + `ActiveIndex==1`
  (the first real-CP/M Videx engagement — closes ADR 0017 OQ2's sibling-gate). Add the sibling to
  `SoftCardVidexBoardTests` asserting `ActiveIndex==1` for apl2cpm3 (vs the 2.2 master's `ActiveIndex==0`).
- Update ROADMAP + ADR 0016 §O wording: the "80-col CP/M end-to-end" headline is **achieved** by apl2cpm3 (the 2.2
  master remains the 40-col reference; apl2cpm3 is the 80-col Videx console).
- **Gate:** the live apl2cpm3 Disk 1 renders **CP/M 3.1 `A>` in 80 columns on the Videx** with `ActiveIndex==1`. **This
  is the deliverable.**

Dependencies: **PR-1 → PR-2 → PR-3 are strictly ordered** (each is the gate for the next live stage). PR-1 alone is
safe (additive param + asset loader + honest skip; the 2.2 board untouched). PR-2 is the 40-col milestone; PR-3 is the
80-col headline. Optional follow-on (un-numbered, not gating): a multi-drive UAT with Disks 2–7 (`EMULA.COM`, dev tools)
— fetch those disks only if that follow-on is scheduled.

---

## 4. Consequences (cross-cutting)

**Good.**
- The boot blocker is **root-caused live to one concrete, gating change** — the SoftCard slot (slot 4, not 5) — proven
  by the live disk flipping `NO Z80 FOUND` → Z80-activates. The fix is one additive, defaulted board parameter; the
  shipped 2.2 board is byte-for-byte unchanged.
- **Every reused decision (ADR 0015 dual-CPU, ADR 0017 control-port + yield + per-track skew, ADR 0016 Videx + 64K LC)
  holds unchanged** — apl2cpm3 needs no new abstraction (confirmed: 46K TPA, no banked memory, the LC + translation +
  Videx are all already wired and live-verified working as far as the boot reaches).
- apl2cpm3 is the **first real CP/M that engages the Videx 80-col path** — it closes the open `ActiveIndex==1`-from-real-CP/M
  question ADR 0017 Decision 6 / OQ2 left to an owner-sourced 80-col master. **It is that master.**
- The PR sequence keeps **main green/honest at every step** (PR-1's slot fix + honest skip first), and every gate is the
  **un-fakeable live disk** with a decoded-text oracle (40-col, then 80-col) — no pixel heuristic, no placeholder hash.

**Bad / accepted costs.**
- `SoftCardBoard`/`SoftCardVidexBoard` gain a per-board `controlPortBase` parameter (defaulted; documented; unit-gated).
- The CP/M-3 Z80-entry-handoff residual (Decision 3) is a Builder bring-up against the live disk (bounded; the same
  shape that closed 2.2's `$1010` bridge), not pre-designed — so PR-2 carries an irreducible "close it against the
  running disk" step.
- The data-track skew past track 3 is verified only as far as the live boot reaches (the system tracks load intact);
  a divergence there is a scoped Builder finding, not a new ADR.
- The boot gates skip-with-note until the owner configures the apl2cpm3 fetch URL + sign-off (same posture as 2.2).

**Reversibility.** High. Decision 1 is an additive defaulted parameter (revert → the slot-5-only behaviour). Decision 5
is a new sibling asset loader (additive). Decisions 2–4 reuse shipped seams. No core/translation/Z80 changes are
proposed (Decision 6 flags the one scenario — a Z80-reset-semantics gap — that *would* touch the core, judged unlikely
and escalated, not assumed).

---

## 5. Open questions

1. **The Z80-entry-handoff residual (Decision 3).** After the slot fix, why does the Z80 NOP-slide from `$0000` instead
   of entering the loaded CPMLDR at `$1100+`? Most likely the loader's `JP <loader>` stub at the Z80 `$0000` is written
   by a boot2/CPMLDR stage that runs after the first `$C400` toggle and our handback isn't round-tripping (live: only
   **one** toggle observed). **Resolve by instruction-stepping the live boot in PR-2.** Escalate (Decision 6) only if it
   reveals a Z80-reset-PC-latch dependency (a core change), which is judged unlikely.
2. **Does apl2cpm3's BIOS paint its sign-on to 40-col before `icrt` switches to 80-col, or go straight to 80-col?**
   (Decision 4 staging.) The live boot (PR-2/PR-3) decides; the gate asserts whichever the disk actually does. No
   pre-assumption.
3. **Data-track skew past track 3 (Decision 2).** Verified intact for the system tracks (the loader loads); confirm the
   data tracks the completing boot reaches also decode under `SectorOrderKind.Cpm` (expected — same format), escalate
   only on a live divergence.
4. **Clock ratio (`2.0`) under the longer CP/M-3 loader chain.** Inherited from ADR 0015 OQ3 / ADR 0017 OQ3; CP/M-3 is
   still a coarse-timed workload — no new risk expected; confirm the boot completes within the gate's cycle budget.

---

*End of ADR 0018 — booting apl2cpm3 (CP/M 3.1) on the SoftCard + Videx. The boot blocker is root-caused **live on the
real apl2cpm3 Disk 1** to one gating change: the SoftCard control port must decode at **slot 4 (`$C400`)**, not the
shipped 2.2 slot 5 (`$C500`) — apl2cpm3 hard-codes `STA $C400` to start the Z80 (live: `8D 00 C4` twice on track 0; at
slot 5 the boot prints `NO Z80 FOUND` and the Z80 never activates; at slot 4 the Z80 activates, the CPMLDR loads to
`$1100+`, no crash). The slot becomes a **per-board parameter** (Decision 1; the 2.2 board stays slot 5, byte-for-byte
unchanged). Everything else is **reused unchanged**: the dual-CPU model (ADR 0015), the write-only control toggle +
per-instruction yield (ADR 0017), the per-track `SectorOrderKind.Cpm` skew (live-verified correct for apl2cpm3's system
tracks — no new skew), the 64K Language Card (live-confirmed wired — the loader loads through it), and the Videx slot-3
CRTC + `DisplayMultiplexer` auto-switch (already wired, waiting on the boot). apl2cpm3 needs **no banked memory** (46K
TPA — no secret banking blocker) and **no new abstraction**. The one residual — the Z80 NOP-slides from `$0000` because
its entry vector to the loaded CPMLDR is absent — is a **bounded Builder bring-up against the live disk** (Decision 3),
the same shape that closed 2.2's `$1010` bridge, not a new ADR. apl2cpm3 is the **first real CP/M to engage the Videx
80-col path** — it is the owner-sourced 80-col master ADR 0017 Decision 6 / OQ2 left open. PR sequence: **PR-1** lands
the configurable slot (live-verified gating fix) + the asset loader + an honest skipped gate (the 2.2 board untouched);
**PR-2** reaches **CP/M 3.1 `A>` in 40-col** (closes the entry-handoff residual against the live disk); **PR-3** reaches
the headline — **CP/M 3.1 `A>` in 80 columns on the Videx with `ActiveIndex==1`** from a real boot. Planner: execute
PR-1 first; Builder: the live apl2cpm3 disk is every PR's un-fakeable gate, and on-screen 80-col `A>` is the arbiter.*
