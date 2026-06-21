# Apple II Plus (Apple ][+) — Architecture Analysis

> **Status:** Research deliverable (Phase 1 of the Apple ][+ arc). Produced by the `deep-research`
> harness (5 search angles, 25 sources fetched, 121 claims extracted, 25 adversarially verified
> 3-0, 107 agent calls). This is the **base-machine** reference. The **Z80 SoftCard / CP/M**
> companion is a separate document (research running in parallel).
>
> **Confidence:** every finding below passed unanimous (3-0) adversarial verification against
> primary or emulator-author-grade sources. **Open questions** at the end are *not* resolved and
> must be closed before/within the Architect phase.

This is the hardware ground-truth the Architect ADR will map onto our declarative `BoardSpec` +
peripheral model (reusing the existing, parity-gated 6502 core).

---

## Executive summary

The Apple ][+ is a fully tractable target for a cycle-accurate, declarative-board emulator reusing
a 6502 core. Its behavior is governed by a single **14.31818 MHz NTSC master crystal** (4× the
3.579545 MHz colorburst); the CPU clock is master/14 (~1.0227 MHz base) with **every 65th cycle
stretched** by two 14M periods (the "long cycle") for an effective ~1.0205 MHz; CPU and video
accesses are **interleaved on the same DRAM** (CPU on φ2-high, video+refresh on φ2-low) at an
effective ~2 MHz. The memory map and `$C0xx` soft switches are well-documented and stable, as is
the Disk II 6-and-2 GCR data path and the Language Card bank-switching.

---

## 1. CPU & system timing

- **Master crystal:** 14.31818 MHz = exactly 4× the 3.579545 MHz NTSC colorburst.
- **CPU clock:** master ÷ 14 = ~1.0227 MHz base (Apple's documented 1,023,000 cycles/sec).
- **The "long cycle":** every 65th CPU cycle (once per scan line) is stretched by two 14M periods →
  65×14+2 = **912 pixel periods/line** = exactly 228 colorburst cycles/line. Average effective CPU
  frequency = 14.31818×65/912 = **1.02048 MHz**.
- **CPU/video interleave:** the NMOS 6502 accesses memory only during the high half of the clock;
  the Apple circuitry uses the low half for a video fetch that *also* refreshes DRAM. Each accesses
  a byte at 1 MHz, interleaved → DRAM effectively ~2 MHz, **no separate refresh cycles needed.**
  - *Emulator implication:* a board model can let the CPU and a video-scanout unit share the same
    RAM each cycle without contention. Implement the per-scan-line long-cycle stretch if cycle-exact
    timing matters; base rate ~1.0227 MHz, average ~1.0205 MHz.

*Sources:* Apple II Reference Manual (primary); Edwards/apple2fpga (Columbia, primary); Empson
cpucycles; mrob colors; Wikipedia.

## 2. Memory map

**RAM ($0000–$BFFF):**
| Range | Use |
|---|---|
| `$0000–$00FF` | Zero page |
| `$0100–$01FF` | 6502 stack |
| `$0400–$07FF` | Text/lo-res **video page 1** (with 8-byte "screen holes" per 128-byte block) |
| `$0800–$0BFF` | Text/lo-res page 2 (or Applesoft program/free RAM) |
| `$2000–$3FFF` | **Hi-res graphics page 1** |
| `$4000–$5FFF` | Hi-res graphics page 2 |

**I/O + ROM ($C000–$FFFF):**
| Range | Use |
|---|---|
| `$C000–$C0FF` | Soft switches & status locations |
| `$C100–$C7FF` | Per-slot peripheral-card ROM (256 B/slot; slot N at `$CN00`) |
| `$C800–$CFFF` | Shared/bank-switched 2K expansion ROM for the card in use (enabled on `$CnXX`, reset on `$CFFF` access) |
| `$D000–$F7FF` | Applesoft BASIC interpreter (ROM) |
| `$F800–$FFFF` | System Monitor (ROM); reset/IRQ/NMI vectors at `$FFFA–$FFFF` |

On the ][+, `$D000–$FFFF` is ROM by default and only replaced by RAM when the Language Card is
banked in. Reset vector `$FFFC–$FFFD` lives in the Monitor ROM.

*Sources:* kreativekorp/Jon Relay; sizecoding.org; 6502disassembly.com (McFadden annotated ROM);
apple2history (Reference Manual lineage).

## 3. `$C0xx` soft switches

**Video (`$C050–$C057`, toggle on *any* access — read OR write — on the ][+):**
| Addr | Name | Effect |
|---|---|---|
| `$C050` | TXTCLR | graphics on |
| `$C051` | TXTSET | text on |
| `$C052` | MIXCLR | full graphics |
| `$C053` | MIXSET | mixed text/graphics |
| `$C054` | LOWSCR | page 1 |
| `$C055` | HISCR | page 2 |
| `$C056` | LORES | lo-res |
| `$C057` | HIRES | hi-res |

**Keyboard:** data at `$C000` (reads ≥128 when a key is latched: bit 7 = strobe, bits 6–0 = code);
`$C010` clears the strobe. ][+ keyboard is **uppercase-only**.

**Speaker:** `$C030` — a 1-bit flip-flop; any reference toggles it (a click). Note a *write*
instruction's 6502 read-before-write toggles it **twice**.

**Language Card (`$C080–$C08F`):** write-enabling LC RAM requires **two consecutive reads** of an
odd `$C08x` address (a pre-write count flip-flop, 74LS175, makes one read insufficient). Presence
(48K vs 64K) is detected by a **write-test** to `$D000` LC RAM, not a read.

> *][+ nuance:* video and LC soft switches respond to **any** access (read or write), unlike the
> IIe where read/write polarity is significant. The tables above don't exhaustively encode ][+
> per-address read/write side effects — verify edge cases against Sather's *Understanding the
> Apple II* (see caveats).

*Sources:* Apple II Reference Manual (primary); cc65 `apple2.inc`; sizecoding; kreativekorp;
fritzm hardware writeup; Apple Technote misc #2 (prodos8.com).

## 4. Video subsystem

- **Hi-res:** 280×192 = **53,760 dots**. Each byte uses its low 7 bits for pixels; the high
  (undisplayed) **8th bit selects the artifact color group** per byte (clear = violet/green,
  set = blue/red/orange).
- **Dot shift rates:** 40-col text & hi-res shift at 7.15909 MHz (master/2); lo-res shifts at full
  14.31818 MHz. (80-col/double-hires are **IIe/IIc only — not on the ][+**.)
- **NTSC artifact colors:** 50%-duty square waves, phase offset **12° from the +I/+Q/−I/−Q axes**,
  giving chroma amplitude higher than any real broadcast (the garish look). A proper renderer models
  the 12° offset.
- **Lo-res:** 40×48, 16-color palette. **Text:** 40×24, character generator ROM, normal/inverse/
  flashing.

### Screen-memory address mapping *(gap-fill — verified)*

**Hi-res scanline → base address** (verified **bijective** over y=0..191 by arithmetic check):

```
addr(y) = 0x2000 + (y/64)*0x28 + (y%8)*0x400 + ((y/8)&7)*0x80      // page 1
        = 0x4000 + …                                                // page 2
```

Strides: `(y%8)`→`$400`, `((y/8)&7)`→`$80`, `(y/64)`→`$28` (=40 = bytes/displayed row). Landmarks:
y=0→`$2000`, y=1→`$2400`, y=8→`$2080`, y=64→`$2028`, y=191→`$3FD0`. The popular "64:1 interleave"
label oversimplifies — it's a two-level structure (8-line within a `$80` block; 64-line third-region
triad). 🔴 A refuted variant assigned the strides the *other* way (`$400` per-8 / `$80` within /
`$28` per-64) — wrong everywhere except y=0/64/191. Use the formula above.

**Hi-res byte/pixel layout:** low 7 bits = 7 pixels; **bit 7 = half-pixel right shift / palette
flag** (physically a one-14MHz-cycle delay of the video signal, ~90° NTSC phase), turning
green/purple → blue/orange and producing *pseudo*-560px (not true 560).

**Text/lo-res:** page 1 `$0400–$07FF` (page 2 `$0800`). Each 128-byte block packs **3 rows of 40
chars** (120 bytes) + **8 "screen-hole" bytes at offsets `$78–$7F`** (holes are a *consequence* of
the 3-row interleave, not its cause). The 3 rows in a block are `$40` apart (vertical regions
0/8/16). The Monitor's **GBASCALC at `$F847`** maps row (A) → base (`GBASL`/`GBASH` = `$26`/`$27`).
The 24 row bases: `$400,$480..$780` (region 0); `$428,$4A8..$7A8` (region 1); `$450,$4D0..$7D0`
(region 2). (Hi-res uses HPOSN/HBASCALC at `$F411`/`$F465`.)

*Sources:* Apple II Reference Manual (primary); Sather *Understanding the Apple II*; AppleWin
Video.cpp; Michaelangel007 HGR tutorial; xtof.info; MAME #6308; mrob colors; Wikipedia (Apple II
graphics); 6502disassembly.com annotated ROM.

## 5. Keyboard

7-bit ASCII-like code + high-bit strobe via `$C000`/`$C010` (see §3). ][+ is uppercase-only with a
non-standard set. RESET handling tied to the Autostart Monitor (see Open Questions).

## 6. Speaker

1-bit `$C030` toggle (see §3). *PCM resampling approach:* record the cycle timestamp of every
`$C030` access, reconstruct the 1-bit waveform over time, low-pass + resample to host PCM rate, and
account for the double-toggle on write opcodes. (Mirrors how our Spectrum beeper sink works.)

## 7. Language Card (48K → 64K)

16K RAM card bank-switching: two 4K banks at `$D000` + `$E000–$FFFF`. Write-enable requires two
consecutive odd-address reads (pre-write count flip-flop). Model both read-ROM/read-RAM and
bank-1/bank-2 selection across `$C080–$C08F`. (Note synergy with the roadmap's bank-switching
candidate work.)

## 8. Disk II (slot 6)

- **6-and-2 GCR:** maps 6-bit values to exactly **64 valid on-disk bytes** (first `0x96`, last
  `0xFF`); every valid byte has **MSB set** and **≤2 consecutive zero bits** (AGC noise-floor
  constraint).
- **Sector encoding:** a 256-byte sector → **342 6-and-2 bytes + 1 checksum = 343 total**. First 86
  bytes hold the low 2 bits (bit-reversed) of source groups; next 256 hold the high 6 bits; checksum
  is a running XOR (first value unaltered).
- **Sequencer soft switches:** reading `$C08D,X` (slot-relative; **`$C0ED` for slot 6**) resets the
  sequencer and clears the data latch. The motor-off switch (`$C088,X` / **`$C0E8`**) must impose a
  **~1 second delay** (556 one-shot) before stopping. Stepper phases at `$C0E0–$C0E7`.
- **DOS 3.3** is 16-sector (vs. earlier 13-sector 5-and-3). Address/data fields framed by self-sync
  bytes.
- *Emulator implication:* model the **LSS sequencer** + the nibble stream (not logical sectors) for
  copy-protection fidelity.

**Disk image formats:** `.dsk`/`.po` are pure logical sector dumps; `.nib` stores nibbles but lacks
track-sync/timing/exact length; **`.woz` stores a normalized exact-length bitstream per track** that
loops correctly and preserves protection timing/sync. **WOZ is the higher-fidelity choice**;
`.dsk`/`.po` are adequate only for non-protected disks (re-nibblize on the fly).

*Sources:* Tom Harte CLK wiki (Apple GCR encoding); Applesauce WOZ reference (primary spec author);
CiderPress2 (Beneath Apple DOS lineage); Big Mess o' Wires; Nerdly Pleasures.

---

## Caveats (from the research harness)

- **Two correct CPU rates:** base ~1.0227 MHz vs. average ~1.0205 MHz (long-cycle). Both appear in
  sources; both correct in their framing.
- **][+ read/write soft-switch behavior** differs from the IIe (any-access toggling). The supplied
  tables don't exhaustively encode ][+ per-address side effects — verify against Sather for edge
  cases.
- Some cited 80-column/double-hires details describe **later models (IIe/IIc)** and do **not** apply
  to the ][+.
- The exact non-linear text/lo-res and hi-res **address-interleave formulas** are only partially
  covered (screen-holes confirmed; per-line offset formula **not** verified here — see Open Qs).
- `$C08D,X` / `$C088,X` notation is **slot-relative** (X = slot×16) → `$C0ED` / `$C0E8` for slot 6.

## 9. Boot & RESET  *(gap-fill — verified against the annotated F8 ROM)*

- **RESET vector** `$FFFC–$FFFD` → **`$FA62`**: `CLD` then `JSR SETNORM ($FE84)` / `INIT ($FB2F)` /
  `SETVID ($FE93)` / `SETKBD ($FE89)`.
- **Cold-vs-warm decision** at `$FA85`: `LDA $03F3 / EOR #$A5 / CMP $03F4` → match = **warm** (ends
  `JMP (SOFTEV)` at `$FAA3`); mismatch = **cold** (full init, set page-3 vectors, scan slots).
- **Page-3 vectors:** `BRKV $03F0`, `SOFTEV $03F2–3` (warm entry), `PWREDUP $03F4` (= `(SOFTEV+1) EOR
  #$A5`), `AMPERV $03F5`. (Heavily used by copy-protection.)
- **Cold disk-boot:** scan slots 7→1 for the Disk II signature (`$Cn01=$20, $Cn03=$00, $Cn05=$03,
  $Cn07=$3C` via DISKID at `$FB02`); on match, `JMP ($Cn00)` — e.g. **`$C600`** for slot 6 (= PR#6).

## 10. CPU compatibility & ROM sourcing  *(gap-fill)*

- **🟡 Undocumented-opcode boundary (real, accept it):** real ][+ software *does* rely on illegal
  NMOS opcodes (e.g. **Ultima I** breaks on the 65C02-based IIc) — a strictly-151-opcode core **will
  fail some third-party titles and copy-protection**. Apple's own system ROMs/DOS 3.3 were *not*
  affirmatively audited but are widely believed clean. The **SKW/NOP-class** family
  (`$0C,1C,3C,5C,7C,DC,FC`) does a **real read, discards the value** (`$0C` = abs/4cyc; rest =
  abs,X/4+1cyc) — cheapest illegal ops to add if we want headroom. *Decision for the Architect:
  ship documented-only first, treat illegal-opcode support as a later compatibility dial.*
- **ROM legal:** the ][+ system ROM (Applesoft+Monitor) + Disk II boot ROM are **copyright Apple →
  user-supplied, never vendored** (established practice + case law). Use the same **fetch-and-cache**
  pattern as our Spectrum/Klaus/ZEX assets; ship a loader + instructions, not the images.

## Residual open items (resolve at build time — non-blocking for the ADR)

1. Whether the **four system components** (Monitor, Applesoft, DOS 3.3, P5/P6 boot ROM) themselves
   use any illegal opcodes (third-party does; system likely clean but unaudited).
2. **Exact ROM image inventory + sizes**, esp. the **character-generator ROM** (size/contents/legal —
   not independently confirmed; the "12K system + 256B Disk II" enumeration was *refuted as too
   narrow*).
3. The **minimal illegal-opcode set** a "good-compatibility" core needs beyond SKW/NOP (LAX/SAX/etc.).
4. The **P5/P6 boot-ROM sequencer internals** for slot-6 `$C600` disk boot (the Autostart hand-off to
   `$Cn00` is covered; the boot ROM's own nibble/sequencer logic is not).

## Key sources

- Apple II Reference Manual (primary) — archive.org
- Edwards / apple2fpga, Columbia (primary, timing/interleave)
- Applesauce WOZ format reference (primary, disk fidelity)
- Tom Harte CLK wiki — Apple GCR encoding
- Apple Technote misc #2 (prodos8.com) — Language Card detection
- mrob xapple2 colors; sizecoding.org; kreativekorp memory map; 6502disassembly.com annotated ROM
