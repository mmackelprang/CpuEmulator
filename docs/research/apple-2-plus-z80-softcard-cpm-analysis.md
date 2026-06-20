# Z80 SoftCard + CP/M on the Apple ][+ — Architecture Analysis

> **Status:** Research deliverable (Phase 1 of the Apple ][+ arc, SoftCard companion). Produced by
> the `deep-research` harness (5 angles, 18 sources, 82 claims extracted, 25 verified, **21
> confirmed / 4 refuted**, 100 agent calls). Companion to
> `apple-2-plus-architecture-analysis.md` (base machine).
>
> **Confidence:** findings below passed adversarial verification (vote shown). **4 claims were
> actively refuted** — see the Refuted section; do not reintroduce them.

This is the ground-truth for the **dual-CPU shared-RAM board** the Architect ADR must design — the
single biggest new abstraction in the arc. We already own a full, parity-gated **Z80 core** (M3),
so this is integration + a new board model, *not* new-CPU work.

---

## Executive summary

The Microsoft Z-80 SoftCard makes the ][+ a dual-CPU machine: a **~2.04 MHz Z80** (bus-synchronized,
runs in one Apple clock phase, uses Apple main RAM for everything) and the host **1.023 MHz 6502**
share the same DRAM, **only one CPU bus-master at a time**. Control passes by a single slot-dependent
soft-switch **write**. On-card address-translation remaps the Z80 view so CP/M's zero page/TPA land
on usable RAM while the Apple's immovable regions ($0000 zero page/stack, $0400 screen, $C0xx I/O)
shuffle to the top of the Z80 map. CP/M 2.2 uses the 16-sector Apple format with a documented DPB
and a **double sector skew** done in 6502 RWTS (the Z80 BIOS does no translation, XLT=0).

---

## 1. CPU control transfer & bus arbitration  *(vote 3-0 core)*

- **Switch:** a single slot-dependent soft-switch **WRITE**. From 6502 mode, write `$CN00`
  (N = slot) → Z80 mode. The Z80 returns by writing the same register, which it sees as `$EN00` in
  Z80 space → Z80 sleeps, 6502 resumes where it stopped.
- **Arbitration:** the card asserts the Apple bus **DMA′ line** to suspend the 6502 — true single-
  bus-master. The suspended 6502 sits in a spin loop.
- **Interrupts:** because the 6502 is suspended via DMA, **ALL interrupt processing must be handled
  by the 6502** (a real constraint for our scheduling model).
- **Refresh detail:** the card uses the Z80 **REFRESH line** to grant the dormant 6502 brief
  execution windows so its dynamic NMOS state/DRAM doesn't decay. *Logical correctness needs only
  single-bus-master semantics; cycle-accurate models may need this.*
- *Caveat:* one credible source recalls the trigger as a READ; the documented protocol + existing
  emulators use a **WRITE**. The physical decoder likely fires on any access — model it as write.

## 2. Z80 → Apple address translation  *(vote 3-0 — IMPLEMENT FROM THE TABLE)*

> 🔴 **The "add $1000 mod 64K" shortcut is WRONG and was explicitly refuted (1-2).** It's correct
> only for the low region. Implement from this enumerated table:

**Complete MAME-verified table** (`a2softcard.cpp` `dma_r`/`dma_w` — supersedes the coarse version;
the gap-fill nailed the middle "shuffle" branches):

| Z80 logical | → Apple physical | Mapping |
|---|---|---|
| `$0000–$AFFF` | `+$1000` (→ `$1000–$BFFF`) | **true additive** offset; CP/M zero page/TPA on usable RAM |
| `$B000–$BFFF` | `(off&$FFF)+$D000` | Language Card **bank 2** |
| `$C000–$CFFF` | `(off&$FFF)+$E000` | |
| `$D000–$DFFF` | `(off&$FFF)+$F000` | ROM / LC `$F000–$FFFF` |
| `$E000–$EFFF` | `(off&$FFF)+$C000` | 6502 **I/O space** — incl. Disk II controller for the BIOS |
| `$F000–$FFFF` | `off&$FFF` (→ `$0000–$0FFF`) | 6502 zero page, stack, Apple screen, CP/M RWTS |

Branches 2–6 mask `&$FFF` then add a 4K-window base (page-wrap); only branch 1 is a true additive
offset. Translation active **only when the Z80 is enabled**.

- Granularity is **4K pages**. Rationale: CP/M wants contiguous RAM from $0; the 6502 hardwires zero
  page/stack to pages 0–1 and the Apple fixes the text screen at $0400 — these can't leave the
  lowest 4K, so reserved/non-RAM regions are shuffled to the top of the Z80 map.
- Translation is **disable-able via DIP switch S1-1 ON** (a config bit our board model should expose).

## 3. Z80 clock & bus timing  *(vote 3-0 / 2-1)*

- **~2.04 MHz** (≈2× the 6502), derived/synchronized from the 14.31818 MHz master — **not**
  free-running 2.000 MHz.
- Z80 executes during **one Apple clock phase**; a **74LS573** latches Apple data during the other
  phase so the Z80 can read it. No Z80 wait states in standard mode.
- Effective throughput is **below 2 MHz** due to the syncopated/bus-synchronized clock.
- *Caveat:* the PH0-vs-PH1 phase **label** is ambiguous across sources — rely on the substance
  (single-phase, bus-synchronized, shared RAM, single-active-CPU), not the literal token.

## 4. CP/M 2.2 disk format & DPB  *(vote 3-0, multi-source: SoftCard ref + CiderPress2 + cpmtools)*

16-sector Apple CP/M format (introduced by the SoftCard): 256-byte physical sectors, 35 tracks,
16 sectors/track, ~140 KB total; first **3 tracks ($00–$02) reserved** (boot + CCP + BDOS + BIOS) →
**128 KB usable**.

**Disk Parameter Block:**
| Field | Value | Meaning |
|---|---|---|
| SPT | 32 | 128-byte logical records/track (= 16 × 256-byte sectors × 2) |
| BSH / BLM | 3 / 7 | **1024-byte allocation blocks** |
| EXM | 0 | |
| DSM | 127 | 128 blocks × 1 KB = 128 KB |
| DRM | 63 | **64 directory entries** (a 128-entry variant was **refuted**) |
| AL0 / AL1 | `$C0` / `$00` | 2 directory blocks |
| CKS | 16 | |
| OFF | 3 | 3 system tracks |
| XLT | `0000H` | **BIOS does NO sector translation — skew is in the 6502 RWTS** |

Directory = 32-byte extent records, each extent spanning 16 KB; with ≤256 blocks each block number
is a single byte. Two CP/M 128-byte logical sectors pack into one 256-byte physical sector.

## 5. Sector skew  *(vote 3-0)*

**Double skew:** system (boot) tracks use the CP/M-physical skew; data tracks use the CP/M-logical
skew. Canonical data-track skew table (DOS-3.3-ordered `.do`/`.dsk`, "apple-do"):

```
0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1
```

Distinct from the ProDOS ("apple-po") table `0,9,3,12,6,15,1,10,4,13,7,8,2,11,5,14`. Same low-level
16-sector Disk II encoding as DOS/ProDOS — **differs only in skew**. The logical-skew table lives in
the SoftCard v2.20B BIOS at `DE92H–DEA1H`.

## 6. Legal status of CP/M  *(vote 3-0)*

- The CP/M redistribution license was **refreshed & expanded by Lineo on 19 Oct 2001** (Bryan Sparks
  letter): "a right to use, distribute, modify, enhance and otherwise make available in a
  nonexclusive manner the CP/M technology."
- 🔴 **NOT** a clean open-source license — the "Caldera open-sourced CP/M in 1997–98" framing was
  **refuted (0-3)**. Treat as a **fetch-on-demand-and-cache** asset; confirm current rights-holder
  terms before vendoring. (Same pattern as our Spectrum ROM fetch.)

---

## Refuted claims (do NOT reintroduce)

| Refuted claim | Vote | Correct version |
|---|---|---|
| Address translation is a uniform "+$1000 mod 64K" | 1-2 | Enumerated table with a mid-range shuffle (§2) |
| CP/M disk has **128** directory entries | 0-3 | **64** entries (DRM=63) (§4) |
| Caldera open-sourced CP/M 2.2 in 1997–98 | 0-3 | Non-exclusive Lineo grant, 2001 (§6) |

## 7. Dual-CPU scheduling model  *(gap-fill — MAME-grounded, RECOMMENDED)*

> **✅ ARC SCOPE DECISION (owner):** the Videx 80-column card is **bundled into the CP/M deliverable**
> — SoftCard + CP/M + Videx 80-col ship together; CP/M is never shipped in a half-usable 40-col state.

**Do NOT cycle-interleave the 6502 and Z80.** The model matching both MAME's SoftCard device and the
real hardware is **run-one-then-the-other bus arbitration** — only one CPU drives shared RAM at a time:

- Z80 starts held in **WAIT** (disabled) at reset.
- A **write to the slot `$CnXX` I/O space toggles control**: enable → release the Z80 (WAIT→clear)
  and **DMA-suspend the 6502** (assert its HALT line); next write → re-assert Z80 WAIT, resume 6502.
- Run the **active** CPU for a timeslice (or until the disabling I/O write), then switch. *Cleaner than
  MAME:* simply **don't schedule the disabled CPU at all** (MAME's Z80 core spins in WAIT — wasteful).
- **All interrupts go to the 6502** (it's the one with the interrupt wiring; see §1).
- Z80 is a real core at **~2.043 MHz** (2× the 6502's 1.0218 MHz); its entire 64K routes through the
  translation table (§2) onto shared 6502 RAM.
- Corroborating precedent: MAME's PC Transporter (V30/x86) uses the same "independent scheduler device
  + soft-switch enable/halt" pattern (though there DMA halts the *coprocessor*, not the host).

*This is the blueprint for the new dual-CPU `BoardSpec` abstraction — hand it straight to the Architect.*

## 8. Videx Videoterm 80-column card  *(gap-fill — full hardware model)*

The historically dominant 80-col solution; **GO** for the CP/M deliverable. Model (MAME
`a2videoterm.c` + Videx ROM 2.4 disasm + Videx manual):

- **Slot 3** by default (where Pascal/CP/M terminal drivers expect a terminal); addressing is
  parameterizable to other slots (the slot-3 "must" is firmware-2.4-specific, not a HW constraint).
- **6845 CRTC** at slot base `$C0B0`: **offset 0 (`$C0B0`)** = CRTC register-pointer select;
  **offset 1 (`$C0B1`)** = data register. (Write reg# to `$C0B0`, value to `$C0B1`.)
- **Screen RAM:** 2KB on-card, as **4 × 512-byte pages**, banked into the **`$CC00–$CDFF`** window of
  the `$C800` expansion space; active bank = `((offset>>2)&3)*512` of the `$C0nX` access.
- **`$C800` window:** firmware ROM at **`$C800–$CBFF`** (1KB), banked VRAM at **`$CC00–$CDFF`**.
- **Char ROM:** 2KB (256 chars × 8 lines); many swappable variants.
- **CRTC init table** at `$C8A1` (R0=`$7A`, **R1=`$50`=80 cols**, **R6=`$18`=24 rows**, R9=`$08`=9
  lines/char) → 80×24 text.
- Introduces a **second display source** that overrides the main ULA-style output — the ADR's
  "active-display" seam.

## 9. SoftCard firmware & assets  *(gap-fill)*

- **SoftCard carries NO onboard ROM/RAM** — pure CPU hardware; all software is on the CP/M disk. So
  there's **no SoftCard ROM to source** (simplifies the asset gate vs. the Videoterm).
- **Assets to fetch-and-cache:** SoftCard CP/M 2.2 `.dsk` (140 KB, 16-sector) from the Asimov archive
  (`apple2.org.za` mirror `/images/cpm/os/`); Videoterm **1KB firmware** + **2KB char ROMs**
  (`asimov.net/emulators/rom_images/videx/`). Plus the Apple system ROMs (base doc §10).
- **Legal:** generic DR CP/M 2.2 is covered by the **2022 DRDOS/Bryan Sparks** non-exclusive grant —
  **but that grant is SILENT on Microsoft's SoftCard-specific CP/M** (MS-authored BIOS). 🟡 Treat the
  SoftCard `.dsk` images as legally **distinct/riskier than generic CP/M** → **fetch-on-demand-cache,
  never vendor** (our Spectrum-ROM pattern). Flag for owner sign-off before shipping any asset loader.

## Residual open items (resolve at build time — non-blocking for the ADR)

1. **Exact CP/M load map:** CCP/BDOS/BIOS load addresses for 48K vs 64K + the Z80 entry point, and
   the step-by-step 6502 `$C600` boot-loader path that reads tracks `$00–$02` and issues the `$CnXX`
   start-the-Z80 write. *Mechanism is confirmed (§1); exact addresses need the SoftCard CP/M Reference
   + BIOS listing — Planner/Builder can pull these when wiring the boot.*
2. **AppleWin SoftCard status** (the "no support" claim was refuted; current state unconfirmed) — a
   secondary cross-reference, MAME is the primary model.
3. **REFRESH-window 6502 wakeups:** MAME doesn't model them; unknown whether any CP/M software cares.
   Sets the accuracy target — default to the simpler single-bus-master model unless a title needs it.

## Source-quality note

Two source families dominate and aren't fully independent: (1) the "Apple II SoftCard CP/M Reference"
(Schlyter, via several mirrors + Guidero's transcription), tracing upstream to the Microsoft Z-80
SoftCard manual (cited indirectly); (2) disk-format/DPB/skew details independently cross-confirmed by
CiderPress2 (fadden) + canonical cpmtools diskdefs (genuinely multi-source). Raymond Chen's "Old New
Thing" is a Microsoft-engineer blog corroborated by hardware docs.

## Key sources

- Apple II SoftCard CP/M Reference (Schlyter) — apple2.org.za / stjarnhimlen mirrors
- apple2.guidero.us — SoftCard CP/M ref transcription
- Raymond Chen, "The Old New Thing" (Microsoft) — address translation + DMA mechanism
- gglabs GZ/80 — reverse-engineered SoftCard-compatible hardware (clock/phase/latch)
- CiderPress2 (fadden) + cpmtools diskdefs — DPB + skew (independent confirmation)
- Wikipedia: Z-80 SoftCard; Tim Olmstead (CP/M) — licensing
