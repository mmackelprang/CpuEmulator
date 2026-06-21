# Apple ][+ web surface — design spec

> **Status:** Approved-pending-owner-review design (Designer phase, Apple ][+ arc). For the Planner to
> turn into UX/implementation tasks once the owner has reviewed the handoff.
> **Date:** 2026-06-20
> **Author:** Claude Designer.
> **Topic:** The browser-facing UX for emulating the **Apple ][+** (and its Z80 SoftCard + CP/M + Videx
> 80-col deliverable) on the SP0 web surface — display (multi-mode + the 40/80 switch), keyboard
> (uppercase-only ][+ + RESET), audio (1-bit speaker), disk (two Disk II drives, cached-library
> dropdown **and** file upload, drive light), the board/CP/M entry flow, and every asset-absent /
> empty / loading / uploading / error state.
>
> **Design handoff package (the visual detail this spec summarizes):**
> `docs/design-handoffs/apple-2-plus/` — `overview.md`, `mockups/layout.md`, `interactions.md`,
> `copy.md`, `tokens.md`. **Read the handoff for the verbatim copy, the ASCII layouts, and the state
> machines; this spec is the decision summary + the Planner hand-off.**

---

## 0. follows / extends / deviates (the audit trail)

This surface is governed by the **Spectrum web client** (`src/CpuEmulator.Surface.Web/wwwroot/`) as the
canonical visual language; there was no prior `docs/design-handoffs/` package (this arc establishes the
directory). It is also governed by **ADR 0014** (base ][+), **ADR 0015** (dual-CPU SoftCard), and
**ADR 0016** (CP/M deliverable + the Videx second-display seam).

- **FOLLOWS:** the entire page chrome, canvas, status line, sound button, `kbd` hint, wire format
  (`FB`/`AU` out, JSON keys in), `MapDomCode`, and the boot-if-cached-else-fallback pattern
  (`Program.cs`). Token-level provenance in `tokens.md`.
- **EXTENDS (additive, no new visual language):** a control strip below the canvas (two disk drives +
  indicators); a NEW inbound-binary path (disk upload); a per-drive cached-library dropdown; an
  asset-status banner that replaces the Spectrum's *silent* fallback with named-script guidance; a
  read-only display-mode / active-source label; one new color token (`--drive-active` amber).
- **DEVIATES:** none. Every decision follows or extends the Spectrum client; no decided pattern is
  contradicted. No Claude Design handoff exists for this surface (none exist at all yet) — where the
  visual language was silent (control-strip layout, upload states), the handoff composes patterns from
  **existing tokens only** (overview.md "missing handoff" note).

## 1. The locked owner decisions this spec honors (do not reopen)

- Full machine incl. Disk II; Videx 80-col bundled into the CP/M deliverable; Disk II = full `.woz`
  fidelity upfront; assets fetch-on-demand / never-vendored; SoftCard CP/M sign-off given (ADRs 0014–0016).
- **Disk loading = BOTH** a per-drive cached-library dropdown **and** a per-drive browser file-upload
  picker, both per drive (2 drives), with eject. The upload path is a NEW inbound-binary path — designed
  cleanly with size/type validation and empty/uploading/error states (this spec §4; handoff
  `interactions.md` §4).

## 2. Key UX decisions (the summary; rationale in `interactions.md`)

### Display
- **D1. The video mode is reflected, never chosen.** A read-only mode label under the canvas shows
  `TEXT/LORES/HIRES/MIXED · geometry · page N`. The user never picks the mode; the guest does (a `$C05x`
  access). (ADR 0014 D3.)
- **D2. The 40↔80 (Apple↔Videx) switch is guest-driven, auto-only, no UI toggle.** The Videx peripheral
  calls `DisplayMultiplexer.SetActive`; the canvas re-sizes automatically (the client already follows
  per-frame `FB` width/height); the mode label flips to `Videx 80×24 · CP/M`. **No manual override.**
  (ADR 0016 D1/D2.)
- **D3. Canvas CSS must size by intrinsic dimensions, not the Spectrum's fixed `768×576`** — the Apple
  is multi-geometry (280-wide vs 720-wide Videx). This is the one place the Spectrum CSS doesn't transfer
  (handoff `tokens.md` "Canvas sizing change").

### Keyboard
- **D4. RESET = `Ctrl+Backspace`** (the real `Ctrl+Reset` can't be sent by a browser); the hint line
  names the actual binding. `preventDefault()` it. (interactions §2.3.)
- **D5. Pass `ctrlKey` through the wire** (additive `ctrl` boolean on the key JSON) so `Ctrl+B` (BASIC),
  `Ctrl+C` (break), etc. reach the chip (AND letter with `$1F`). `Ctrl+B`/`Ctrl+C` `preventDefault()`.
- **D6. Uppercase-only is surfaced, not hidden** — the hint says `Uppercase only.`; lowercase folds to
  the ][+ uppercase code; keys the ][+ lacks are a silent no-op (matches the Spectrum unknown-key no-op).
- **D7. No new `KeyCode` enum values needed** for the base Apple keyboard (Shift/Ctrl ride the
  `char`/`ctrl` fields). An unmapped Apple key would be an additive `MapDomCode` arm, not a design change.

### Audio
- **D8. Reuse the Spectrum beeper UX verbatim** — same single sound button, same gesture gate, same `AU`
  frame path. No new audio UX. (ADR 0014 D3 reuses the Spectrum resampler.)

### Disk (both paths, two drives)
- **D9. Each drive is a bordered panel** (`#333` border) with a drive light + image label on top, two
  insert routes + eject below. Four states: EMPTY / INSERTED-idle / INSERTED-active / UPLOADING
  (`interactions.md` §4.1).
- **D10. The drive light follows real motor state** (the `$C0E8/$C0E9` motor + the ~1 s 556 off-delay,
  ADR 0014 D6) — amber `●` when spinning, outline `○` when off. **Not faked on insert.** It lingers ~1 s
  after the last access (authentic). Color + a text alternative (a11y).
- **D11. Path A — cached-library dropdown.** A per-drive `[ Library ▾]` populated from a new
  `GET /disks` catalog (the `disks/` cache the `get-*` scripts fill). Insert = a text WS message
  (`disk-insert`, drive, id). Empty catalog → disabled select with `No cached disks — see tools/get-*`.
- **D12. Path B — file upload (the NEW inbound-binary path).** A per-drive `[ Insert… ]` → hidden
  `<input type="file" accept=".woz,.dsk,.po">`. **Client-side validation** (extension, size cap **2 MB**,
  non-empty) before send; **binary WS upload** frame (`DK`: drive, format, bytes) on the open socket;
  **server-side re-validation** (size + `.woz` magic / `.dsk`/`.po` exact length). UPLOADING state
  (`◐`, indeterminate or `NN%`), controls disabled; success → INSERTED, failure → revert + inline error.
- **D13. Eject** removes the running image (text WS `disk-eject`, drive). Allowed even mid-access, **no
  confirm** (re-insert re-reads; nothing is destroyed). Uploaded disks are **session-scoped** — v1 does
  not persist uploads into the catalog (additive follow-on).
- **D14. A small additive `ST` (status) wire frame** carries all the new read-only indicators (board
  name, asset state, per-drive motor + image, video mode) host→client. Keeps the surface a dumb reflector
  of real machine state. (Planner owns the encoding; the design requires the indicators reflect real
  state, pushed.)

### Board / CP/M entry
- **D15. No board picker.** The Apple ][+ is *the* machine this surface serves (one machine per session).
- **D16. CP/M is entered by inserting + booting the CP/M disk** (RESET-with-disk-in-Drive-1 — the
  hardware idiom), **not** a "Boot CP/M" button. The dual-CPU hand-off is invisible (ADR 0015). The
  **moment** is the display widening to the 80-col Videx terminal — that *is* the "CP/M is running" cue.
- **D17. No "which CPU" indicator** (ADR 0015 says optional → decision: omit). The `· CP/M` mode-label
  suffix is the only needed signal.
- **D18. One optional, dismissible explainer note** on the first Videx activation
  (`Now running CP/M on the Z-80 SoftCard (80-column Videx display).`) — the only explanatory copy.

### Asset-absent / empty / loading / error (the anti-rot section)
- **D19. Replace silent fallback with a calm, named-script banner.** No Apple ROMs → boots the SP0 demo
  (shipped) **and** shows `Apple ][+ ROMs not found — showing the demo pattern.` + `tools/get-apple2-roms.sh`.
  Never red, never "Error", never a trace. (handoff `copy.md` §5; `interactions.md` §7.)
- **D20. A per-condition asset matrix** (ROMs / disks / CP/M-Videx / char-gen) each has a defined state —
  `interactions.md` §7.1. CP/M insert with Videx assets absent → **refuse the insert** with the named
  script, cleaner than a half-broken boot.
- **D21. Loading + error states** reuse the Spectrum status-line lifecycle (`connecting…`, `connected`,
  `disconnected`, `connection error`) extended with board + asset state; the status line gets
  `aria-live="polite"`. (`copy.md` §3.)

## 3. The user flows (end to end)

1. **First run, no assets:** open → demo fallback + calm banner naming `get-apple2-roms.sh` → run it →
   reload → Applesoft `]`.
2. **Plain ][+:** boot to `]`, type (uppercase), `Ctrl+B` to BASIC, hear the speaker, `Ctrl+Backspace`
   to RESET. Mode label tracks what programs draw.
3. **Insert a disk (library):** pick from `[ Library ▾]` → disk loads → drive light flickers on access.
4. **Insert a disk (upload):** `[ Insert… ]` → pick `mygame.woz` → validate → upload (`◐`) → INSERTED →
   the disk runs. Wrong type/size/corrupt → inline calm error, revert.
5. **Eject:** `[ Eject ]` → EMPTY.
6. **CP/M:** insert the CP/M disk (Drive 1) → `Ctrl+Backspace` (boot) → brief 40-col Apple boot → the
   display widens to **80-col Videx** → `A>` → (optional one-time note). Exit = RESET / eject + RESET.

## 4. Hand-off to the Planner — task-shaping flags (NOT a plan; the Planner owns decomposition)

These are the seams where the design touches implementation; the Planner decides PR structure.

- **T-A. The `ST` status-frame seam (D14).** A new lightweight host→client frame for board name, asset
  state, per-drive motor + image, and video mode. The drive light, image labels, mode label, and banner
  all consume it. **Suggested as an early task** — most indicators depend on it.
- **T-B. The disk-upload inbound-binary path (D12) — flag as a DISTINCT task.** The brief explicitly
  allows splitting the upload *plumbing* out: it's the surface's first inbound binary path (client
  `<input type=file>` → validation → binary WS `DK` frame → server validation → load into the running
  Disk II for drive N). It's cleanly separable from the library-dropdown path (which is text-only) and
  from the rest of the UI. **Recommend the Planner make this its own task** (transport + validation +
  the UPLOADING state), depending only on the Disk II controller being able to accept an in-session
  image swap for a drive.
- **T-C. The `GET /disks` catalog endpoint (D11)** — server lists the cached `disks/` images (name,
  format, drive-compat, a CP/M grouping flag). Feeds both library dropdowns. Independent of T-B.
- **T-D. The in-session disk insert/eject mechanism** — the Disk II controller (ADR 0014 D6) must accept
  "load these bytes as the image for drive N" and "eject drive N" at runtime, for both the library and
  upload paths. This is the shared dependency of T-B and T-C. (The `IFluxImage` `.woz` seam + the
  `.dsk`/`.po` adapter from ADR 0014 D6 are where the bytes land.)
- **T-E. The control-strip UI** (drive panels, library selects, upload buttons, eject, drive lights,
  mode label, banner) — pure client + the `ST`/catalog wiring. Composed from existing tokens + the one
  new `--drive-active` amber.
- **T-F. The keyboard extensions** (D4/D5): the `ctrl` field on the key JSON + the `Ctrl+Backspace`
  RESET binding + the `preventDefault` additions in `app.js`, and the chip-side fold (ADR 0014 D3).
- **T-G. The asset-status surfacing** (D19/D20): the server reports which assets are cached
  (`Apple2Rom.TryGetPath()`-style checks for system ROM / char-gen / Videx / CP/M) → the `ST` frame →
  the banner + the per-condition states.
- **T-H. Canvas sizing refinement** (D3): replace the fixed `768×576` CSS with an intrinsic-dimension
  upscale so 280-wide and 720-wide frames both render correctly across the 40↔80 switch.

The display multiplexer (D2), the Videx peripheral, the Language Card, the dual-CPU machine, and the
`.woz` controller are **Architect/Builder territory** (ADRs 0014–0016) — this spec consumes them and only
designs what the *user sees*. The Designer does not specify the data model, the wire encoding details, or
the peripheral internals.

## 5. Open items for the owner (before the Planner runs)

1. **Upload transport — WS-binary vs POST.** This spec recommends a **binary WS `DK` frame** (session-
   scoped, one channel, no session-id handshake). The brief allowed either. If the owner prefers a POST
   endpoint (e.g. for resumable/large uploads — not needed at ≤250 KB), say so; the UX states are
   identical either way. **Default: WS-binary.**
2. **Persist uploaded disks into the catalog?** v1 treats uploads as session-scoped (D13). Persisting an
   uploaded image into `disks/` (so it appears in the library next time) is a tasteful follow-on. **Default: session-scoped, no persistence.** Confirm or defer.
3. **A per-drive `[ Boot ]` convenience button?** D16 uses RESET-with-disk (hardware idiom, no button). A
   `[ Boot ]` button per drive (which would just do RESET) is optional sugar. **Default: no button.**
   Confirm.
4. **OS-aware fetch-command copy.** The copy names `tools/get-*.sh`; the `.ps1` siblings ship too (ADR
   0016 D4). Should the server show `.ps1` when it detects Windows? **Default: name the `.sh` form**
   (matches the existing `get-spectrum-rom` docs). Confirm if OS-aware copy is wanted.
5. **Control-strip pixel polish.** No Claude Design handoff exists; the control strip is specified in
   ASCII from existing tokens. If the owner wants rendered/pixel-level polish on that one region, a Claude
   Design pass could refine it — **otherwise the ASCII layout + tokens are sufficient to build.**

None of these block the Planner from starting; they're the small forks where an owner preference changes
a default. The defaults above are all shippable.

---

*End of the Apple ][+ design spec. The surface FOLLOWS the Spectrum visual language verbatim and EXTENDS
it additively for the Apple's disk drives (two, library + upload + eject + a real-motor activity light),
its multi-mode + 40/80-Videx display (guest-driven, auto, no toggle), its uppercase-only keyboard + RESET
(`Ctrl+Backspace`), and a calm named-script asset-absent banner replacing the Spectrum's silent fallback.
One new token (`--drive-active` amber). The NEW inbound-binary disk-upload path is designed with full
validation + empty/uploading/error states and flagged as a distinct Planner task. No pattern DEVIATES
from a decided one. Handoff package: `docs/design-handoffs/apple-2-plus/`.*
