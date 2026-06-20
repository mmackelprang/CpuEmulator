# Apple ][+ web surface — interactions

State transitions, edge cases, keyboard/touch/screen-reader behavior. Grounded in the shipped client
(`wwwroot/app.js`, `FrameCodec.cs`, `Program.cs`) and ADRs 0014–0016. Where a behavior is the
Spectrum's, it is marked **[follows]**; new behavior is **[extends]**.

---

## 1. Display modes + the 40 / 80 switch

### 1.1 Apple video modes (the mode label)
The Apple video is one `IDisplayDevice` whose `Width`/`Height` and pixel content depend on guest video
state (the `$C050–$C057` soft switches, ADR 0014 Decision 3). The user **never selects the mode** — the
running program does, by accessing a soft switch. The surface only *reflects* it:

- A small read-only **mode label** under the canvas shows the current mode and page, derived from the
  IOU's video-state flags. Strings: `TEXT · 40×24`, `LORES · 40×48`, `HIRES · 280×192`,
  `MIXED · text+gfx`, each optionally `· page 1` / `· page 2`. See `copy.md` §2.
- The label updates on each frame (cheap — it reads the same shared mode-state object the renderer
  reads). It is **not** focusable and **not** a control. No `aria-live` (it changes too often to
  announce usefully; it is decorative status, mirrored in the status line for screen readers only at
  board granularity).

### 1.2 The canvas resize is automatic [follows]
The Apple's modes can change the framebuffer dimensions (e.g. hi-res 280×192 vs. the Videx 720×216). The
**client already handles this** — `app.js` does `if (canvas.width !== width) { canvas.width = width; … }`
on every `FB` frame, and the codec carries width/height per frame. So a mode change or a 40↔80 switch
needs **zero new client code** for the canvas. The CSS `width`/`height` (the upscale) should be
expressed so the displayed size tracks the native aspect (see `tokens.md` — replace the Spectrum's
hardcoded `768×576` with an aspect-preserving rule so both 280-wide and 720-wide frames upscale sanely).

### 1.3 The 40 / 80 (Apple ↔ Videx) switch [extends, ADR 0016]
- The switch is **guest-driven** via `DisplayMultiplexer.SetActive` — the Videx peripheral calls it when
  the guest enables the Videx as the live terminal. The surface pulls from the multiplexer as one
  `IDisplayDevice` (`MachineHost` unchanged). **No UI toggle exists** (Decision 1: the user sees what the
  guest drives — the hardware truth).
- On a source switch, the multiplexer fires `FrameReady`, the host re-renders at the new dimensions, the
  client re-sizes the canvas, and the **mode label flips** to `Videx 80×24 · CP/M`.
- **Edge case — mid-frame switch:** the switch takes effect on the next `FrameReady`; there is no torn
  frame because the host renders whole frames. Acceptable.
- **Edge case — switch back:** if CP/M exits / the disk is ejected and the machine resets, the guest
  re-selects Apple video; the label returns to the Apple mode. Same machinery, reversed.
- **Auto vs. manual:** the brief asked us to decide. **Decision: auto only** (guest-driven), per ADR
  0016 Decision 1/2. There is no manual override button. Rationale: a manual toggle would let the user
  show a display the guest isn't driving (a blank or stale Videx) — confusing and non-hardware. If a
  future debugging need arises, a manual override is an additive follow-on, explicitly out of scope here.

---

## 2. Keyboard

### 2.1 The portable-KeyCode path [follows]
Inbound keys are JSON `{action, code, char}` (DOM `KeyboardEvent.code` + the typed char). `MapDomCode`
normalizes `code` → portable `KeyCode`; the Apple keyboard chip (`Apple2Keyboard : IKeyboardSink`, ADR
0014 Decision 3) owns the translation to the ][+ code set. Unknown keys → `KeyCode.None` → no-op. This
is the exact Spectrum pipeline; the Apple extends the `KeyCode` enum **additively** if it needs codes the
Spectrum didn't (see §2.4).

### 2.2 Uppercase-only reality [extends]
The ][+ keyboard is **uppercase-only** with a non-standard set (research §5, ADR 0014 Decision 3).
Design consequences for the user:
- Lowercase typed in the browser maps to the ][+ **uppercase** code (the chip folds case). The user sees
  uppercase on screen regardless of Shift — this is correct ][+ behavior, not a bug. The hint line says
  so (`Uppercase only.`) so the user isn't surprised.
- Characters the ][+ keyboard physically lacks (e.g. `[`, `]`, `_`, backtick, lowercase as distinct
  glyphs) either map to their ][+ equivalent where one exists or are dropped (no-op). The mapping table
  is the chip's concern (ADR 0014); the UX contract is: **typing a key the ][+ doesn't have does
  nothing visible** (no error, no beep) — consistent with the Spectrum's unknown-key no-op.

### 2.3 RESET (Ctrl+Reset) [extends]
The Apple's RESET is **Ctrl + Reset** on real hardware. There is no browser "Reset" key, so:
- **Decision: map `Ctrl + Backspace`** (a chord the browser surfaces cleanly and rarely steals) to the
  Apple RESET line, **and** offer it in the hint line as the discoverable path. Rationale: `Backspace`
  is the closest physical analogue to the ][+'s top-right Reset position is not literal, but the chord is
  memorable and non-destructive in-browser. The hint line reads `Ctrl+Reset = RESET` but the *binding*
  is `Ctrl+Backspace` — **so the hint must name the actual chord**: `Ctrl+Backspace = RESET`. (Naming
  the real binding beats naming the hardware key the browser can't send.)
- RESET is **edge-triggered**: the chord asserts the 6502 RESET (the core's `Reset()` reads `$FFFC/$FFFD`
  → `$FA62`, ADR 0014 Decision 7). The Autostart Monitor's cold/warm decision is guest-ROM behavior.
- **Guard:** `Ctrl+Backspace` must `preventDefault()` so the browser doesn't navigate back / delete a
  word. Extend the existing `preventDefault` list in `app.js` (which already guards Space/Arrows).
- **Edge case — no ROM:** in the demo fallback, RESET is a no-op-with-note (there's no Apple to reset);
  the hint line in fallback mode omits the RESET line entirely (see `copy.md`).

### 2.4 Ctrl key combinations (Ctrl+B, Ctrl+C, etc.) [extends]
Applesoft and DOS use Ctrl combinations (`Ctrl+B` = enter BASIC, `Ctrl+C` = break, `Ctrl+Reset` =
RESET). The Apple keyboard sends control codes for `Ctrl + letter`. The browser surfaces these as
`code: "KeyB"` + `ctrlKey: true`. **Decision: pass the `ctrlKey` modifier through the wire** — extend the
JSON key event with a `ctrl` boolean (additive; the Spectrum ignored modifiers beyond its own
CapsShift/SymbolShift). The chip ANDs the letter code with `$1F` when `ctrl` is set, producing the
control code. `Ctrl+B` and `Ctrl+C` must `preventDefault()` (the browser may bind them). The hint line
surfaces the two most useful (`Ctrl+B = BASIC`, and RESET) without listing all.

### 2.5 Modifier KeyCodes
The `KeyCode` enum already has `CapsShift`/`SymbolShift` (Spectrum). The Apple needs no new *physical*
modifier codes — its Shift and Ctrl are handled via the `char`/`ctrl` fields, not distinct `KeyCode`s.
**No `KeyCode` additions required for the base Apple keyboard.** (If the Planner finds an Apple key with
no DOM-`code` mapping, that's an additive `MapDomCode` arm, not a design change.)

---

## 3. Audio [follows]

The 1-bit speaker (`Apple2Speaker : IAudioSink`, ADR 0014 Decision 3) reuses the Spectrum beeper path
verbatim:
- The **single sound button** ("click to enable sound" → "sound on") is unchanged — same element, same
  copy, same autoplay-gesture requirement (`ensureAudio()` on click, `audioCtx.resume()`).
- The host pushes `AU` frames; the client schedules them back-to-back (the shipped `handleAudioFrame`).
- **No new audio UX.** The speaker's double-toggle-on-write is a chip-level detail (ADR 0014), invisible
  here.

---

## 4. Disk — insert / eject (both paths) + the drive light

Two drives (Disk II), each with two insert routes and an eject. The drive panel is the new control-strip
region (`mockups/layout.md` §2).

### 4.1 Drive panel state machine [extends]

```
        ┌─────────┐  insert (library or upload succeeds)   ┌──────────┐
        │  EMPTY  │ ───────────────────────────────────────▶│ INSERTED │
        │  ○      │                                          │  ○ idle  │
        └─────────┘ ◀─────────────────────────────────────── └──────────┘
             ▲            eject                                   │   ▲
             │                                          motor on  │   │ motor off (after ~1s 556 delay)
             │                                                    ▼   │
             │ upload fails                                  ┌──────────┐
        ┌─────────┐                                          │ INSERTED │
        │ UPLOAD  │ ── bytes in flight ──▶ (validate) ──▶    │  ● active│
        │  ◐      │                                          └──────────┘
        └─────────┘
```

- **EMPTY**: light `○`, label `empty`, no Eject. Both insert routes enabled (library enabled only if the
  catalog is non-empty; upload always enabled).
- **INSERTED idle**: light `○`, label = image name, **Eject** shown. Motor off.
- **INSERTED active**: light `●` (amber), label unchanged, Eject shown. Motor on (a disk access). The
  light follows the **motor**, which includes the **~1-second 556 motor-off delay** (ADR 0014 Decision
  6) — so the light lingers ~1s after the last access, exactly as a real Disk II's lamp does. This is a
  feature, not lag: it's the authentic "drive still spinning" cue.
- **UPLOADING**: light `◐` (spinner), label `Uploading <name>… NN%`, controls disabled. On success →
  INSERTED idle; on failure → back to the prior state (EMPTY or the previously-inserted disk) + an inline
  error (§4.4).

### 4.2 The drive-activity light [extends]
- Driven by the Disk II controller's **motor-on state** (the `$C0E8`/`$C0E9` motor switches + the 556
  one-shot delay, ADR 0014 Decision 6). The controller already tracks this for timing; the surface needs
  a host-side read of "is the motor on for drive N".
- **Mechanism (flag for Planner):** the cleanest seam is a tiny host-pull signal analogous to
  `IDisplayDevice` — e.g. the Disk II exposes `bool MotorOn(int drive)` (or raises an event the host
  coalesces), and the host pushes a lightweight **status frame** (a new wire tag, see §4.5) carrying the
  per-drive motor + current-image state. This keeps the surface a dumb reflector (the Disk II owns the
  truth). The Planner decides whether to piggyback drive status on an existing frame or add a `ST`
  status frame; the **design requirement** is only that the light reflects real motor state at ~frame
  cadence, not faked on insert.
- Color: amber `#d8a657` (the one new semantic token — `tokens.md`). Off state: the `#888` muted text
  color (an outline `○`). The light must have a **text alternative** (`aria-label="drive 1 active"` /
  `"drive 1 idle"`) for screen readers, since color alone isn't accessible.

### 4.3 Path A — the cached-library dropdown [extends]
- Each drive has a `[ Library ▾]` select. On page load the client requests the **catalog** (a new
  `GET /disks` JSON endpoint listing the cached `disks/` images: name, format, drive-compat). The select
  is populated from it.
- Choosing an item sends an **insert-from-library** message (JSON over the existing WebSocket — text, so
  it reuses the inbound text path: `{action:"disk-insert", drive:N, id:"<catalog-id>"}`). The server
  loads the cached bytes into the running Disk II for that drive. No upload — the bytes are already
  server-side.
- **Empty catalog:** the select is **disabled** with the single option `No cached disks — see tools/get-*`.
  Upload (Path B) remains available. (See `copy.md`.)
- **CP/M disk in the catalog:** the CP/M image is listed (grouped last). Inserting it into a drive and
  booting is the CP/M entry flow (§6). The catalog marks it so the UI can group it; no special control.

### 4.4 Path B — the file-upload picker (the NEW inbound-binary path) [extends]
This is the surface's first inbound *binary* path. Design it cleanly:
- The `[ Insert… ]` button opens a hidden `<input type="file" accept=".woz,.dsk,.po">`. (Touch: the OS
  file picker; no drag-drop required for v1 but drag-onto-the-drive-panel is a tasteful additive
  follow-on, noted, not required.)
- **Client-side validation before upload** (fail fast, no round-trip):
  - **Extension/type:** must be `.woz`, `.dsk`, or `.po`. Else → inline error `Unsupported file — use
    .woz, .dsk, or .po` and abort.
  - **Size cap:** a `.woz` is ~200–250 KB; `.dsk`/`.po` are exactly 143,360 bytes (140 KB). **Decision:
    reject > 2 MB outright** (no real Disk II image approaches it — a generous ceiling that blocks
    accidental huge files). Inline error `File too large — Disk II images are under ~250 KB`.
  - **Empty file:** reject 0 bytes → `That file is empty`.
- **Upload transport:** the bytes go to the server for **this session's** drive N. **Decision: a binary
  WebSocket frame** with a new outbound-from-client tag (e.g. `DK`: tag, drive, format byte, then bytes),
  reusing the open socket — *not* a separate POST — so it's session-scoped and needs no extra endpoint
  wiring. (The brief allowed POST or WS; WS keeps it on the one channel the session already owns and
  avoids a session-id handshake. The Planner may choose POST-with-session-cookie if WS binary-up proves
  awkward; the **design contract** is just: per-drive, validated, with the states below.)
- **Server-side re-validation** (never trust the client): size + format sniff (`.woz` magic `WOZ1`/`WOZ2`
  + the FF-checksum; `.dsk`/`.po` exact length). On reject → an error status frame → inline error
  (§4.4 errors mirror client-side ones plus `That image looks corrupt` for a failed magic/length check).
- **States during upload:** the drive panel enters UPLOADING (`◐`, `Uploading <name>… NN%`), its controls
  disabled. Progress is the WS send progress (or, for small files, just an indeterminate `◐ Uploading…`
  with no percent — Disk II images are small enough that a percent may flash by; **indeterminate is
  acceptable**). On success → INSERTED idle, label = the uploaded filename. On any failure → revert + the
  inline error, error auto-clears on the next successful action or after ~6 s.
- **Eject during upload:** disabled (controls are disabled while UPLOADING). Eject is only available in
  INSERTED states.

### 4.5 Eject [extends]
- `[ Eject ]` in either INSERTED state removes the image from that drive (`{action:"disk-eject",
  drive:N}` over the text path). The panel returns to EMPTY (`○`, `empty`).
- **Eject while the motor is on (active):** allowed, but it's the user's call — ejecting a spinning disk
  mid-access is a real (if ill-advised) thing. **Decision: allow it without a confirm** (this is an
  emulator, not data-loss territory — a re-insert re-reads from the cached/uploaded bytes; nothing is
  destroyed). The motor light goes off with the eject.
- **No persistence:** ejecting and the session ending both discard the *running* image (the cached
  library bytes and any uploaded bytes the server chooses to keep are separate; v1 does **not** persist
  uploads to the catalog — an uploaded disk is session-scoped. Persisting uploads into `disks/` is an
  additive follow-on, noted, not required).

### 4.6 The status-frame seam (Planner flag)
The drive light, the current-image label, and the mode label all need **host→client status** that isn't
a framebuffer or audio frame. **Recommendation:** one small additive `ST` (status) wire frame the host
pushes when state changes (board name, asset state, per-drive motor + image, video mode). This is the
clean home for all the new read-only indicators and avoids stuffing them into the `FB`/`AU` frames. The
Planner owns the exact encoding; the design only requires that these indicators reflect **real machine
state**, pushed, not inferred client-side.

---

## 5. The board / plain-][+ entry flow

- **Default boot:** opening the surface boots the plain Apple ][+ if the system ROM is cached (the
  `Program.cs` pattern: `Apple2Rom.TryGetPath()` → boot real machine, else fallback). No board picker —
  one machine per session, the Apple ][+ is *the* machine this surface serves.
- The user lands at the Applesoft `]` prompt (or the Monitor `*`, per the ROM's autostart). Typing,
  sound, and disk all work as above. RESET re-runs the autostart.
- **There is no UI to "choose the plain ][+ vs the SoftCard"** — the SoftCard/CP/M is entered by
  *inserting and booting the CP/M disk* (§6), exactly as on real hardware (you don't flip a switch; you
  boot a CP/M floppy). This keeps the entry model honest and the UI free of a mode selector.

---

## 6. The CP/M / SoftCard entry flow (the UX moment)

The dual-CPU hand-off is invisible at runtime (ADR 0015: the Z80 runs under translation; the user never
sees "which CPU"). The **entering** of CP/M is the moment to design:

1. The user **inserts the CP/M disk** into Drive 1 — via the library (it's listed, grouped last) or via
   upload. (Requires the `get-softcard-cpm` + `get-videx-roms` assets to be present; if absent, see the
   asset-absent flow §7.)
2. The user triggers a boot of that disk. **Decision: a boot is `Ctrl+Reset` (RESET) with the CP/M disk
   in Drive 1** — the autostart scans slots and boots slot 6 (the real ][+ boot path, ADR 0014 Decision
   7 / 0015 Decision 7). *No special "Boot CP/M" button* — booting a disk is RESET-with-disk, the
   hardware idiom. (A convenience `[ Boot ]` button per drive is a tasteful additive follow-on if the
   owner wants it; not required, and it would just do RESET.)
3. The 6502 boot loader reads tracks `$00–$02`, sets up the Language Card, and writes `$CN00` to start
   the Z80 (ADR 0015). **All invisible.** The user sees the 40-col Apple boot text briefly.
4. CP/M's terminal driver enables the **Videx**; `DisplayMultiplexer.SetActive` switches the active
   display; the canvas re-sizes to 80×24; the mode label flips to `Videx 80×24 · CP/M`; the user sees
   `A>`. **This is the moment** — the display visibly widening to 80 columns is the "CP/M is running"
   signal. No textual "now in CP/M mode" announcement is needed; the 80-col terminal *is* the
   announcement. (The mode label provides the screen-reader-accessible text.)
5. **Optional, recommended:** a one-time, dismissible note the first time the Videx activates —
   `Now running CP/M on the Z-80 SoftCard (80-column Videx display).` — so a user who doesn't know the
   history understands what just happened. Dismiss persists for the session. (See `copy.md`; this is the
   only "explainer" copy in the surface and it's optional/dismissible.)
6. **Exit:** there is no CP/M-exit UI; the user RESETs (returns to Apple video) or ejects + RESETs. The
   display switches back to Apple video when the guest re-selects it.

### 6.1 The "which CPU is running" indicator — decision
ADR 0015 says this indicator is **optional** ("CP/M just looks like a terminal"). **Decision: do NOT add
a CPU indicator.** The Videx 80-col display *is* the CP/M cue; a `6502 / Z80` light would be noise
(the hand-off is invisible and rapid, and the user's mental model is "I'm in CP/M now", not "the Z80 is
bus-master this instant"). The mode label's `· CP/M` suffix carries the only needed signal. If the owner
later wants a debug-grade CPU indicator, it's an additive follow-on.

---

## 7. Asset-absent / empty / loading / error states (the anti-rot section)

The surface fetches **nothing** by default; the user must run the `get-*` scripts. Every absence has a
defined, **non-alarming** state. The driving principle: *a missing asset is a normal first-run condition,
not an error* — calm tone, name the exact script, never red, never a stack trace.

### 7.1 The asset matrix

| Condition | What boots | What the user sees |
|---|---|---|
| **No Apple ROMs** | SP0 demo fallback (shipped) | Banner: `Apple ][+ ROMs not found — showing the demo pattern.` + `tools/get-apple2-roms.sh`. Drive panels disabled with a note. Mode label `demo · no ROM`. |
| **Apple ROMs present, no disks cached** | Plain ][+ to `]` | Boots fine. Library dropdowns disabled (`No cached disks — see tools/get-*`); upload available. No banner. |
| **CP/M disk inserted, Videx/SoftCard assets absent** | n/a — can't boot CP/M | Inline error on the drive: `CP/M needs the Videx ROMs — run tools/get-videx-roms.sh`. The insert is refused (or boots to a non-Videx state — **decision: refuse the CP/M insert** with the note, cleaner than a half-broken boot). |
| **Char-gen ROM absent (text glyphs)** | ][+ boots, text uses a built-in fallback glyph set | Subtle note in the status line: `· fallback font` (ADR 0014 Decision 8 ships a built-in fallback glyph set). Not a banner — text is still readable. |

### 7.2 Loading states
- **Connecting:** status line `connecting…` (the shipped Spectrum string) until the socket opens.
- **First frame:** the canvas is `#000` (its CSS background) until the first `FB` frame arrives — a brief
  black, then the boot screen. No spinner needed (the first frame is ~instant on localhost). If no frame
  arrives within ~3 s of `connected`, show status `connected · waiting for first frame…` (a soft
  diagnostic, not an error).
- **Catalog loading:** the library dropdowns show `Loading…` (disabled) until `GET /disks` returns;
  then populate or show the empty-catalog text.

### 7.3 Error states (the genuinely-wrong cases — still calm)
- **WebSocket error / server down:** status line `connection error — is the server running?` (mirrors the
  shipped `ws.onerror`). No modal.
- **WebSocket closed:** `disconnected — reload to reconnect` (mirrors `ws.onclose`).
- **Upload rejected (size/type/corrupt):** inline error in the drive panel (§4.4), auto-clearing.
- **Disk insert failed (library item missing server-side):** inline `Couldn't load that disk — it may
  have been removed from the cache.` + revert to the prior state.
- **Never:** a red banner, an alert(), an uncaught-exception trace, or a blank canvas with no
  explanation. Every dead-end has a sentence and (where relevant) a command.

---

## 8. Accessibility & input notes

- **Focus:** the canvas does not take text focus (keys are captured at `window` level, as in the shipped
  client). The new controls (selects, buttons) are standard focusable elements with visible focus rings —
  do not suppress the browser default `:focus-visible` outline; if the design wants a custom ring, use
  the `#888`/`#ccc` palette, never remove it (a Polisher check).
- **Drive light:** color + a text alternative (`aria-label`, §4.2). Never color-only.
- **Mode label / status line:** the status line is the screen-reader-relevant state at board granularity;
  give it `aria-live="polite"` so board/asset/connection changes are announced. The per-frame mode label
  is decorative (no live region — it'd spam).
- **Keyboard traps:** the file-`<input>` is hidden but reachable via the `Insert…` button (a real
  `<button>` that `.click()`s the input) — no keyboard trap.
- **Touch:** the file picker uses the OS picker; selects and buttons are native (touch-friendly). The
  canvas is display-only on touch (no on-screen keyboard in v1 — noted as a follow-on; the Spectrum had
  none either).
- **Reduced motion:** the only animation is the drive-light spinner (`◐`) during upload and the amber
  on/off. Respect `prefers-reduced-motion` by making the spinner a static `◐` (no spin) — the percent
  text still conveys progress.
