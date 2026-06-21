# ADR 0016 — The CP/M deliverable: the Videx 80-column second-display seam (active/overriding display source) and the CP/M/Videx/SoftCard asset + licensing posture

> **Status:** PROPOSED (Architect phase, Apple ][+ arc). No implementation now. This ADR covers the two cross-cutting
> decisions the **CP/M deliverable** introduces beyond the dual-CPU board (ADR 0015): (1) the **second-display-source
> seam** — the Videx Videoterm produces its own 80×24 output that *overrides* the main Apple video, but the SP0 surface
> assumes a single `IDisplayDevice`; and (2) the **asset-fetch + licensing posture** for the CP/M disk, the Videx ROMs,
> and the SoftCard data — fetch-on-demand, never vendored, with a licensing caveat that needs owner sign-off. The base
> ][+ board is ADR 0014; the dual-CPU SoftCard board is ADR 0015.
> **Date:** 2026-06-20
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Reads as ground truth:** `docs/research/apple-2-plus-z80-softcard-cpm-analysis.md` §8 (Videx Videoterm hardware
> model), §9 (assets + legal), §6 (CP/M licensing); `docs/research/apple-2-plus-architecture-analysis.md` §10 (ROM
> legal), §2 (the `$C800` expansion band). Section references are to the SoftCard doc unless noted.
> **Owner scope decision (final, do not re-litigate):** the Videx 80-column card is **bundled into the CP/M deliverable**
> — SoftCard + CP/M + Videx 80-col ship together; CP/M is never shipped in a half-usable 40-col state (research §7).
> **Supersedes / relates to:**
> - **ADR 0015** (dual-CPU SoftCard board) — CP/M runs on the Z80 via ADR 0015; this ADR adds its *display* (Videx) and
>   its *assets* (the CP/M disk). The Videx inherits the `$C800` expansion-bank mapper ADR 0014 Decision 5 deferred here.
> - **ADR 0014** (base ][+ board) — Decision 5 deferred the `$C800–$CFFF` expansion-bank machinery to the card that needs
>   it (the Videx); this ADR builds it. Decision 7's asset-fetch posture (the `get-spectrum-rom` pattern) is extended.
> - **SP0** (`IDisplayDevice`, `MachineHost`) — the surface pulls RGBA from one `IDisplayDevice` (`MachineHost.cs:43`,
>   single `_display`). The second-display seam is the additive change SP0's single-display assumption requires.
> - **ADR 0009** (device↔JIT contract) — the Videx VRAM is a fast-RAM region (Decision 1); the `$C800` VRAM banking is a
>   `Remap` consumer (Decision 2, the seam ADR 0014 builds).
> - **ADR 0010** Decision 8 (artifact-ingestion tooling) — the deferred SP1+ vision; the fetch posture here is the
>   *shipped* `get-spectrum-rom`-style scripts, the down payment that ADR 0010 Decision 8 will later generalize.

---

## 1. Context

### 1.1 The Videx Videoterm — a second display that overrides the main video

The Videx Videoterm is the historically dominant 80-column card and the CP/M terminal target (Pascal/CP/M drivers expect
a terminal). Hardware model (§8, from MAME `a2videoterm.c` + the Videx ROM 2.4 disasm + the Videx manual):

- **Slot 3** by default; addressing parameterizable to other slots (the slot-3 "must" is firmware-2.4-specific, not a HW
  constraint).
- **6845 CRTC** at slot base `$C0B0`: offset 0 (`$C0B0`) = CRTC register-pointer select; offset 1 (`$C0B1`) = data
  register. (Write reg# to `$C0B0`, value to `$C0B1`.) Init table at `$C8A1` → R1=`$50` (80 cols), R6=`$18` (24 rows),
  R9=`$08` (9 lines/char) → **80×24 text**.
- **Screen RAM:** 2 KiB on-card as **4 × 512-byte pages**, banked into the **`$CC00–$CDFF`** window of the `$C800`
  expansion space; active bank = `((offset>>2)&3)*512` of a `$C0nX` access.
- **`$C800` window:** firmware ROM at **`$C800–$CBFF`** (1 KiB); banked VRAM at **`$CC00–$CDFF`**.
- **Char ROM:** 2 KiB (256 chars × 8 lines); swappable variants.

The architectural problem: the Videx produces an **80×24 monochrome character display that overrides** the Apple's main
40-column video. When CP/M runs, the user sees the Videx, not the Apple text/hi-res screen. But the SP0 surface pulls RGBA
from **one** `IDisplayDevice` (`MachineHost` holds a single `_display`, sizes one `_rgba` buffer at `display.Width *
display.Height`, subscribes one `FrameReady`). There are now **two** display sources — the Apple video (ADR 0014
`Apple2Video`) and the Videx — and which one is "live" depends on guest state (whether the Videx is the active terminal).

### 1.2 What the shipped code gives us

- **`IDisplayDevice` is already a clean pull contract** (`IDisplayDevice.cs`): `Width`/`Height` (may change with mode),
  `RenderInto(Span<uint>)`, `FrameReady`. Two display devices can both implement it; the question is how the surface
  picks which to pull.
- **The Videx VRAM-reading-into-RGBA is the ULA pattern again** — the Videx is one more `IDisplayDevice` that reads its
  own 2 KiB VRAM + char ROM and produces 80×24 RGBA. It owns its VRAM (unlike the Apple video, which reads main RAM),
  because the Videx VRAM is on-card (banked into `$CC00–$CDFF`).
- **The `$C800` expansion-bank machinery does not exist yet** (ADR 0014 Decision 5 deferred it here). It is a `Remap`
  consumer (the same ADR 0009 Decision 2 seam ADR 0014 Decision 4 builds): a `$C0nX` access selects the active 512-byte
  VRAM bank into `$CC00–$CDFF`, and a `$CnXX`/`$CFFF` access enables/resets the `$C800` firmware window.

---

## 2. Decisions

### Decision 1 — The second-display seam: a host-side `IDisplaySource` selector (a `DisplayMultiplexer`) the surface pulls from; the *active* source is chosen by a guest-driven signal, NOT by the surface

Introduce a small **host-side display multiplexer** that implements `IDisplayDevice` by delegating to whichever of N
underlying display sources is currently **active**. The surface (`MachineHost`) is unchanged — it still pulls from a
single `IDisplayDevice`; that device is now the multiplexer. The multiplexer's *active source* is set by a guest-driven
signal (the Videx being switched on as the terminal), so the **display selection is driven by emulated state, not a UI
toggle** — the user sees what the guest is actually driving, which is the hardware truth.

```csharp
namespace CpuEmulator.Core;   // additive; alongside IDisplayDevice

/// <summary>A display device that delegates to whichever underlying IDisplayDevice is currently ACTIVE.
/// The surface pulls from this as an ordinary IDisplayDevice (unchanged MachineHost); the active source
/// is selected by guest state (e.g. the Videx being the live terminal), via SetActive — so the user
/// sees what the guest drives. Width/Height/RenderInto/FrameReady delegate to the active source; a
/// source switch raises FrameReady so the surface re-pulls (and re-sizes — the dimensions change, e.g.
/// 280x192 Apple hi-res vs 720x216 Videx 80x24).</summary>
public sealed class DisplayMultiplexer : IDisplayDevice
{
    public DisplayMultiplexer(IReadOnlyList<IDisplayDevice> sources, int initialActive = 0);
    public void SetActive(int index);          // called by the active-display signal (Decision 2)
    public int Width  => _active.Width;         // delegates
    public int Height => _active.Height;
    public void RenderInto(Span<uint> rgba) => _active.RenderInto(rgba);
    public event Action? FrameReady;            // forwards the active source's FrameReady; also fires on SetActive
}
```

**The dimension-change problem (load-bearing):** `MachineHost` sizes its `_rgba` buffer **once** at construction from
`display.Width * display.Height` (`MachineHost.cs:43`). The Apple video (e.g. 280×192 or 560×192 artifact) and the Videx
(80×24 chars × a glyph cell, e.g. 720×216) have **different dimensions**, so a single fixed buffer is wrong when the
active source changes. **Decision: `MachineHost` re-checks `Width`/`Height` and re-sizes its `_rgba` buffer on each
`FrameReady` if the dimensions changed** (a tiny additive change: compare against the last size, reallocate if
different). This is the one `MachineHost` change the seam requires, and it is small + safe (a per-frame size check, a
reallocation only on the rare source switch). The frame codec already carries width/height per frame
(`FrameCodec.EncodeFrame(width, height, rgba)`, `MachineHost.cs:68`), so the client already handles changing dimensions
— only the host's buffer sizing needs to follow.

**Rationale.** A multiplexer that *is* an `IDisplayDevice` keeps the surface and `MachineHost` almost entirely unchanged
(the single-display assumption holds — the surface pulls one device), while making the multi-source reality an
implementation detail behind the contract. Driving the active source from **guest state** (not a UI control) is correct:
on real hardware you don't pick the display, the software does (CP/M drives the Videx; Applesoft drives the 40-col). The
dimension-change re-size is the minimal honest accommodation of two differently-sized sources.

**Alternatives considered.**
- **(A) Send both displays to the surface; let the client pick/overlay.** *Rejected* — it pushes emulation state (which
  display is live) to the UI, doubles the frame bandwidth, and the client would need to know Apple-vs-Videx semantics
  (the surface is meant to be a dumb blitter, the `IDisplayDevice` contract's whole point). The multiplexer keeps the
  selection in the emulation where it belongs.
- **(B) A "composite" display that renders both and overlays/switches internally.** *Rejected* — the Videx fully
  *replaces* the Apple video (it is not an overlay); a multiplexer (one active at a time) is the right model, not a
  compositor. (If a future card genuinely overlaid — e.g. a genlock — a compositor source could be one of the
  multiplexer's entries; the seam composes.)
- **(C) Make `MachineHost` itself multi-display-aware (a list of displays + an active index).** *Rejected as the default*
  — it spreads the multi-source logic into the host pump; isolating it in a `DisplayMultiplexer` (which `MachineHost`
  treats as one device) keeps the pump simple and the seam reusable (any future multi-display machine — a IIe with an
  RGB card — reuses it).

**Consequences.** *Good:* the surface/`MachineHost` stay single-display; the multi-source reality is one additive
`DisplayMultiplexer` + a one-line host re-size; the selection is guest-driven (correct). *Bad/accepted:* `MachineHost`
gains a per-frame dimension check + occasional reallocation (negligible); the multiplexer must forward `FrameReady` from
the active source and fire it on switch (so the surface re-pulls at the new size).

### Decision 2 — The active-display signal: the Videx peripheral drives `DisplayMultiplexer.SetActive` from its guest-facing state — the same writer/reader split as ADR 0014's IOU↔video

The signal that switches the active display is **guest-driven**: when the guest writes the Videx's
control/`$C800`-enable registers to make it the live terminal (and, conversely, when the Apple video is re-selected), the
**Videx peripheral calls `DisplayMultiplexer.SetActive`**. This reuses ADR 0014 Decision 3's writer/reader pattern: the
guest-facing `IPeripheral` (the Videx's `$C0Bx`/`$C800` registers) is the *writer* of the active-source state, and the
host-facing `DisplayMultiplexer` is the *reader* — they share the multiplexer reference (the Videx holds it, calls
`SetActive`).

Concretely, the **active source follows the `$C800` expansion-window enable**: the Videx becomes the active display when
its `$C800` firmware window + VRAM bank are enabled (the guest selecting the Videx as terminal); the Apple video is
active otherwise. (The exact register condition that CP/M's terminal driver uses to "turn on" the Videx is a build-time
detail — §8 gives the `$C0B0`/`$C0B1` CRTC programming + the `$C8A1` init table; default: treat the `$C800`-window
enable as the active-display signal, refine against the Videx firmware 2.4 behavior if needed.)

**Rationale.** Guest-driven selection (Decision 1) needs a concrete signal; the Videx's own enable state *is* that signal
(the hardware shows the Videx exactly when it is enabled). Reusing the IOU↔video writer/reader split keeps one pattern
across both Apple-internal video-mode state and the cross-card display selection.

**Consequences.** *Good:* the selection is the Videx's own state — no separate mechanism; one pattern with ADR 0014.
*Bad/accepted:* the exact enable condition is a build-time detail (defaulted to the `$C800`-window enable).

### Decision 3 — The Videx as a peripheral: `VidexVideoterm : IPeripheral, IDisplayDevice, IFastMemoryProvider`, owning its 2 KiB VRAM, the 6845 CRTC, and the `$C800` expansion-bank mapper (built here, deferred from ADR 0014 Decision 5)

The Videx ships as `VidexVideoterm` mapping its slot-3 register window (`$C0B0`/`$C0B1` CRTC) + participating in the
`$C800` expansion band. It:

- **Faces the guest as `IPeripheral`** for the 6845 CRTC registers (`$C0B0` reg-select, `$C0B1` data) and the
  `$C0nX`-driven VRAM-bank select; it owns the 6845 register file (R0–R17, the init table at `$C8A1` writes them).
- **Faces the host as `IDisplayDevice`** producing 80×24 RGBA: walks its own VRAM (the character codes) through the
  **char ROM** (2 KiB, 256×8) into a monochrome glyph raster, at the CRTC-programmed geometry. It is one of the
  `DisplayMultiplexer`'s sources (Decision 1).
- **Owns its 2 KiB VRAM as a fast-RAM region (`IFastMemoryProvider`, ADR 0009 Decision 1)** banked into `$CC00–$CDFF` —
  the guest writes characters there hot (a screen-clear / scroll), so it should be on the JIT fast path, snapshotted at
  the Videx's frame tick (the ADR 0009 Decision 1 model). The 4 × 512-byte bank selection is the `$C800` mapper:
- **Builds the `$C800–$CFFF` expansion-bank mapper** (deferred from ADR 0014 Decision 5) using the **same
  `AddressSpace.Remap` seam ADR 0014 Decision 4 builds**: a `$C0nX` access remaps `$CC00–$CDFF` to the selected 512-byte
  VRAM bank; a `$CnXX` access enables the `$C800–$CBFF` firmware ROM window; a `$CFFF` access resets it (§8 / base §2).
  The firmware ROM window (`$C800–$CBFF`) is a `Remap`-to-ROM; the VRAM window (`$CC00–$CDFF`) is a `Remap`-to-the-
  active-bank-backing. This is the second consumer of the `Remap` seam (the Language Card is the first, ADR 0014).

**Timing tier:** `Coarse` — the Videx is a character display refreshed at its own rate; an 80×24 text snapshot per frame
is correct (no per-scanline tricks in the CP/M terminal use case). The CRTC's R-register reprogramming is rare (init-time
+ cursor), not per-scanline.

**Rationale.** The Videx is the ULA pattern with on-card VRAM: an `IPeripheral` (CRTC + bank registers) + `IDisplayDevice`
(VRAM+charROM → RGBA) + `IFastMemoryProvider` (the hot VRAM). The `$C800` mapper is exactly the `Remap` machinery already
being built for the Language Card — so the Videx reuses it rather than inventing expansion-bank handling, and it is the
right home for the `$C800` machinery (the card that actually uses the expansion band) per ADR 0014 Decision 5.

**Alternatives considered.**
- **(A) Model Videx VRAM as MMIO (trap every char write).** *Rejected* — a CP/M screen clear/scroll writes the whole
  buffer; trapping it is the ADR 0009 Decision 1 MMIO tax. Fast-RAM region + snapshot is correct and fast.
- **(B) Put the `$C800` mapper in the base board (ADR 0014).** *Rejected* (and ADR 0014 Decision 5 already deferred it) —
  no base-board peripheral uses the `$C800` expansion band; building it with the Videx keeps the base board free of
  unused machinery (YAGNI).

**Consequences.** *Good:* the Videx reuses the ULA pattern + the `Remap` seam + the fast-RAM model; the `$C800` mapper
lands with its only consumer. *Bad/accepted:* the Videx is the second `Remap` consumer (more pressure on that seam being
correct — but it is the *same* seam, so it is exercised twice, which is good test coverage).

### Decision 4 — Assets: fetch-on-demand, never vendored — the CP/M disk, the Videx firmware + char ROMs, and (no) SoftCard ROM; skip-with-note when absent

Per the explicit owner directive and the established `tools/get-spectrum-rom.{sh,ps1}` pattern, **all CP/M-deliverable
assets are fetched on demand and cached outside source control, never vendored.** The complete asset inventory (§9 +
base §10):

| Asset | Size / format | Canonical source (research) | Used by |
|---|---|---|---|
| Apple ][+ system ROM (Applesoft+Monitor) | 12 KiB | Apple copyright, user-supplied (base §10) | ADR 0014 base board |
| Disk II P5/P6 boot ROM | 256 B | Apple copyright, user-supplied | ADR 0014 Disk II |
| Character-generator ROM | 2 KiB (base, exact TBD) | Apple copyright, user-supplied (base §-res 2) | ADR 0014 video (text) |
| **Videx firmware ROM** | **1 KiB** | `asimov.net/emulators/rom_images/videx/` (§9) | Videx `$C800` firmware |
| **Videx char ROM** | **2 KiB** (256×8) | `asimov.net/emulators/rom_images/videx/` (§9) | Videx glyph render |
| **SoftCard CP/M 2.2 `.dsk`** | **140 KiB, 16-sector** | Asimov archive (`apple2.org.za` mirror `/images/cpm/os/`) (§9) | ADR 0015 CP/M boot |
| SoftCard ROM | **none** — the card has no ROM (§9) | — | (nothing to fetch) |

The script contract (mirroring `get-spectrum-rom.sh` exactly): cache root
`${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}` under per-asset subdirs (`apple2/`, `videx/`, `cpm/`),
idempotent (skip if present), sanity-check the byte length / sector count, fail loud with a mirror fallback, and **provide
both a `.sh` and a `.ps1`** (the project hit a PowerShell deny-wall — ship the `.sh` sibling, ADR 0010 Decision 8.6).
Asset-dependent tests **skip-with-note when the asset is absent** (the Spectrum ROM-boot gate's exact discipline) — so
the test suite is green without any copyrighted bytes, and the asset-gated gates (CP/M boots to the `A>` prompt on the
Videx 80-col display; the Videx render gate) run only when the user has fetched the assets. Proposed scripts:
`tools/get-apple2-roms.{sh,ps1}`, `tools/get-videx-roms.{sh,ps1}`, `tools/get-softcard-cpm.{sh,ps1}`.

**Rationale.** This is the owner-blessed, case-law-clean posture already shipped for the Spectrum/Klaus/ZEX assets,
extended to the Apple's three ROMs + the Videx two ROMs + the CP/M disk. The SoftCard having no ROM (§9) simplifies the
gate (one fewer asset). Skip-with-note keeps the suite green without bytes (the un-fakeable gates that *don't* need the
assets — the Videx VRAM→RGBA render with a synthetic char ROM, the translation-table boundary test, the soft-switch
decode — still run, exactly as the Spectrum's render/keyboard/beeper gates run without the ROM).

**Consequences.** *Good:* clean licensing posture; suite green without assets; the CP/M end-to-end gate runs when the
user opts in. *Bad/accepted:* the user must supply the assets out-of-band (intended — it is why they are not in the repo);
the CP/M boot gate is asset-gated (skip-with-note when absent).

### Decision 5 — Licensing caveat (ESCALATE TO OWNER before any CP/M asset loader ships): generic DR CP/M 2.2 is covered by the 2022 DRDOS grant, but Microsoft's SoftCard-specific CP/M is NOT — prefer clean-redistribution assets and get owner sign-off

> **✅ OWNER DECISION (Coordinator session, 2026-06-20): RESOLVED — option (a), fetch-on-demand.**
> The `get-softcard-cpm.{sh,ps1}` script fetches the SoftCard CP/M `.dsk` from the Asimov preservation
> mirror on demand, caches it outside source control, and never vendors it. The owner acknowledges the
> SoftCard-specific CP/M is grant-silent (gray-area) and accepts it as standard, non-redistributive
> emulator practice — **sign-off is GIVEN for the fetch-on-demand loader.** Still prefer clean-status
> assets where equivalent, and the suite stays green without any assets (skip-with-note). The
> escalation below is retained as the rationale of record.

This is a **flag for owner sign-off**, not an Architect decision to make unilaterally (it is exactly the "auth/secrets/
data/legal" category the workflow says to pause on). The research is explicit (§6, §9):

- **Generic DR CP/M 2.2** is covered by the **2022 DRDOS / Bryan Sparks non-exclusive grant** ("a right to use,
  distribute, modify, enhance and otherwise make available in a nonexclusive manner the CP/M technology"; §6, refreshed
  2001 / 2022). 🔴 It is **NOT** an open-source license — the "Caldera open-sourced CP/M 1997–98" framing was **refuted
  0-3** (§6). Even generic CP/M is a fetch-on-demand-and-cache asset, not a vendored one.
- **The SoftCard-specific CP/M is the riskier asset (§9):** the 2022 grant is **SILENT on Microsoft's SoftCard-specific
  CP/M** (the MS-authored BIOS). 🟡 Treat the SoftCard `.dsk` images as legally **distinct from — and riskier than —
  generic CP/M.** Fetch-on-demand-cache, never vendor, **and get owner sign-off before any asset *loader* ships.**

**Recommended posture (for owner decision):**

1. **Prefer assets whose redistribution status is clean.** Where CP/M can boot the SoftCard from a *generic* DR CP/M 2.2
   (covered by the grant) plus a separately-sourced or *user-supplied* SoftCard BIOS, prefer that decomposition over a
   single MS-authored `.dsk` of uncertain status.
2. **Gate the SoftCard CP/M `.dsk` fetch behind explicit owner sign-off.** The fetch *script* can exist (it fetches
   nothing copyrighted by default and documents the source), but **shipping a loader that fetches the MS SoftCard CP/M
   image is the step to pause on** — surface the source, the §6/§9 caveat, and let the owner decide whether to (a) ship
   the fetch pointing at the Asimov mirror, (b) require the user to supply their own SoftCard CP/M disk, or (c) target
   generic CP/M + user-supplied BIOS.
3. **Default if unsigned:** the CP/M boot gate **skip-with-note** (no asset fetched) — the SoftCard board, the
   translation, the Videx, and all non-CP/M gates ship and pass; the CP/M-boots-to-`A>` gate is dark until the owner
   signs off the asset path. This keeps the entire arc shippable and gated *except* the one legally-sensitive gate.

**Rationale.** Architect records and surfaces legal risk; the owner decides. The research gives a clear, asymmetric risk
(generic CP/M = grant-covered; SoftCard CP/M = silent/riskier), so the honest move is to (a) prefer the clean asset, (b)
escalate the riskier one, and (c) ensure nothing in the arc *depends* on the riskier asset to ship — only the final
CP/M-boot gate does, and it skips cleanly when the asset is absent.

**Consequences.** *Good:* the arc ships and gates fully on clean/synthetic assets; the one legally-sensitive step is
isolated and escalated, not silently shipped. *Bad/accepted:* the headline "CP/M boots on the Videx 80-col display" gate
is owner-sign-off-gated (correct — it is the one with legal risk).

---

## 3. Consequences (cross-cutting)

**Good.**
- The two-display reality is one additive `DisplayMultiplexer` (an `IDisplayDevice` the surface pulls from, unchanged) +
  a one-line `MachineHost` re-size; the selection is guest-driven (hardware-correct), not a UI toggle.
- The Videx is the ULA pattern + the `Remap` seam (its second consumer, after the Language Card) + the ADR 0009
  fast-RAM model; the `$C800` expansion-bank machinery lands with its only user.
- Assets are fetch-on-demand/never-vendored (the shipped Spectrum posture, extended); the suite is green without bytes;
  the SoftCard has no ROM to source.

**Bad / accepted costs.**
- `MachineHost` gains a per-frame dimension re-check + occasional reallocation (negligible) — the one host change.
- The Videx is the second `Remap` consumer (more reliance on that seam — mitigated: same seam, more coverage).
- The CP/M-boot gate is owner-sign-off-gated for licensing (Decision 5) and asset-gated (Decision 4) — both skip cleanly.

**Reversibility.** High. `DisplayMultiplexer` is additive (a single-display board passes one source and the multiplexer
is transparent); the Videx is an opt-in peripheral; the assets are external. The only host change (`MachineHost`
re-size) is a strict superset of the current behavior (same result when dimensions never change).

---

## 4. Open questions

1. **The exact active-display enable condition (Decision 2).** Default: the Videx `$C800`-window enable is the
   active-display signal. Confirm against the Videx firmware 2.4 / the CP/M terminal driver behavior at build time.
2. **Videx dimensions for RGBA (Decision 1/3).** 80×24 chars × a glyph cell (e.g. 9×9 → 720×216) — pin the exact glyph
   cell + whether the surface scales. Defaulted to the CRTC R9 lines/char; confirm against the char ROM.
3. **Licensing path (Decision 5 — OWNER).** Which of (a) fetch SoftCard CP/M from the Asimov mirror, (b) user-supplied
   SoftCard disk, (c) generic CP/M + user-supplied BIOS? **Owner decides before the CP/M asset loader ships.**
4. **`MachineHost` re-size mechanism (Decision 1).** Confirm a per-`FrameReady` size compare + realloc is acceptable vs.
   sizing the buffer to the max of all sources up front (simpler, slightly wasteful). Leaning the compare-and-realloc
   (sources can have very different sizes); confirm at Planner time.

---

*End of ADR 0016 — the CP/M deliverable's two cross-cutting decisions. The **second-display seam** is a host-side
`DisplayMultiplexer` (an `IDisplayDevice` the surface pulls from, so `MachineHost` stays single-display) whose **active
source is guest-driven** (the Videx's own enable state calls `SetActive` — the ADR 0014 writer/reader split), with the
one required host change being a per-frame dimension re-check + re-size (the codec already carries per-frame
width/height). The **Videx** is `IPeripheral` (6845 CRTC + bank registers) + `IDisplayDevice` (VRAM+charROM → 80×24 RGBA)
+ `IFastMemoryProvider` (its hot 2 KiB VRAM), and it **builds the `$C800` expansion-bank mapper** (deferred from ADR 0014
Decision 5) on the **same `AddressSpace.Remap` seam the Language Card builds** — making it the seam's second consumer.
**Assets** (Apple system/boot/char ROMs, Videx firmware+char ROMs, the SoftCard CP/M `.dsk`; the SoftCard itself has no
ROM) are **fetch-on-demand, never vendored** via `get-*.{sh,ps1}` scripts mirroring `get-spectrum-rom`, with
skip-with-note tests. The **licensing caveat is escalated to the owner** (Decision 5): generic DR CP/M 2.2 is covered by
the 2022 DRDOS grant, but Microsoft's SoftCard-specific CP/M is NOT — prefer clean-redistribution assets, gate the
SoftCard CP/M fetch behind owner sign-off, and keep the entire arc shippable/gated on clean+synthetic assets with only
the final CP/M-boots-to-`A>`-on-the-Videx gate owner-sign-off-gated. Designer: the CP/M surface is the **Videx 80×24
monochrome terminal** (the real CP/M display), switched in by guest state; the Apple 40-col video is what shows
otherwise — the user never picks, the guest does. Planner: see the sibling report's PR decomposition.*
