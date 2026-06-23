# Perf-overlay HUD — design handoff

> **Status:** Design handoff (Designer phase). Implementation-ready, sized for **one Builder PR**.
> **Date:** 2026-06-23
> **Author:** Claude Designer.
> **Scope:** A toggleable, read-only performance/telemetry HUD overlaid on the emulator canvas, on the
> SP0 web surface (`src/CpuEmulator.Surface.Web/`). Toggled with backtick `` ` ``.
> **Builder note — file contention:** a favicon Builder and a Pascal-docs Builder are concurrently
> editing `wwwroot/index.html` / the Pascal docs. **Do not start the `index.html`/`app.js` edits until
> that work has landed.** This doc is the spec; nothing here touches those files yet.

---

## `follows` / `extends` / `deviates`

- **FOLLOWS** the canonical visual language established in `docs/design-handoffs/apple-2-plus/`
  (which itself promotes the Spectrum in-code language to a written handoff). Specifically it reuses,
  verbatim and by name, the tokens recorded in `apple-2-plus/tokens.md`:
  - `--bg #111`, `--fg #ccc`, `--muted #888`, `--muted-size 12px`, the monospace-leaning calm register,
    and the single new accent the Apple surface introduced: `--drive-active #d8a657` (amber).
  - the `kbd`-panel idiom (`--kbd-bg #222`, `--kbd-border #444`) for the HUD's chrome.
  - the calm, lowercase-leaning copy register (`apple-2-plus/copy.md` §3): no red, no `Error:`, no
    `alert()`, no emoji.
- **EXTENDS** the surface with one genuinely new region — a **floating overlay panel** on top of the
  canvas (every prior region sat *below* the canvas in the flex column). It is built **only from existing
  tokens** plus one new push-frame type (`PF`) and a handful of additive host accessors. No framework,
  no build step, no web font, no icon set, no animation library — exactly the Apple-surface posture.
- **EXTENDS** the wire protocol with a **new `PF` (perf) text frame** alongside the existing `ST` frame.
  Rationale for *not* extending `ST` is in §6.1 (the on-change dedupe in `StatusPusher` is defeated by
  always-changing perf data; the two channels have opposite cadence semantics).
- **DEVIATES:** none. There is no Claude Design handoff for this surface; where the visual language was
  silent (an overlay vs. an in-column panel) this handoff composes the overlay from the existing tokens,
  per the Apple-surface precedent (`apple-2-plus/overview.md` "missing handoff" note).

---

## 1. What this is / who uses it

A single local user running `dotnet run` on `CpuEmulator.Surface.Web` wants to see, at a glance, **how
fast the emulator is running and what it's doing** — without leaving the browser or reading server logs.
The HUD is a **read-only instrument panel**: it shows FPS, guest throughput, the real-time ratio,
memory, the execution tier + JIT stats, the active board, and (for the SoftCard) coprocessor state. It
**changes nothing** about the running machine — every value is observed, never set.

**Success criteria**

1. Pressing backtick `` ` `` toggles the HUD on/off instantly; it never reaches the guest.
2. With the HUD on, the user can read every metric clearly **over any canvas content** (a black boot
   screen, a bright hi-res image, the 80-col Videx terminal) without the canvas becoming unreadable.
3. The tier row shows `JIT` (amber) or `interpreter` (muted) truthfully — and offers **no control** to
   change it (the owner's locked decision: display-only).
4. Every degenerate state — before the first `PF` frame, zeroed counters at boot, a dropped socket —
   has a defined, calm appearance (no `NaN`, no flicker, no blank rows).

---

## 2. The locked decision: JIT is DISPLAY-ONLY

The HUD **shows** the current execution tier and JIT statistics. It has **no toggle, no reboot button,
no control of any kind** to change the tier. The tier is chosen at boot.

- **Tier row** (always shown): the active tier is rendered as the word `JIT` in amber `--drive-active`
  `#d8a657` when the JIT is live, or `interpreter` in `--muted #888` when the interpreter is live. The
  inactive word is **not** shown (we don't render `interpreter` greyed-next-to-`JIT`; we render exactly
  the one that is running). This keeps the row a single honest fact, not a fake switch.
- **JIT stats rows** (shown only when the tier is JIT): compile count, recompiles, evictions, SMC hot
  PCs (see §3). When the tier is interpreter, these rows are **omitted entirely** (not shown as `—`),
  because they don't exist for the interpreter — showing empty JIT rows would imply a disabled feature.
- **Tiny additive, NOT a HUD control:** the tier may be selected at boot via a server flag / query param
  (e.g. `?tier=jit`). This is a launch-time choice owned by `Program.cs`, documented for the Builder as
  an *optional* convenience, and is **explicitly not surfaced as a HUD affordance**. If the Builder
  defers the flag, the HUD still works (it reflects whatever tier booted). The HUD's only job is to
  *report* the tier, never to pick it.

This is the cleanest possible PR: no host-model change, no reboot machinery, no confirm dialogs.

---

## 3. The metric set (final)

A deliberately **non-crowded** selection — eight rows max in the common case, fewer when the tier is
interpreter or the board is single-CPU. Each row is marked **[client]** (computed in the browser) or
**[server]** (pushed in the `PF` frame). The amber accent `#d8a657` is reserved for the **active
tier word** and the **headline throughput value**; everything else is `--fg #ccc` (labels in
`--muted #888`).

| Row | Label (HUD) | Value example | Source | Notes |
|---|---|---|---|---|
| 1 | `board` | `Apple ][+` | **[server]** | reuse `MachineStatus.Board`; identifies what's running. |
| 2 | `fps` | `60` | **[client]** | measured from `FB`-frame arrivals in `ws.onmessage` (§4). The display refresh the user actually sees — not the emulation step rate. |
| 3 | `guest` | `1.02 MHz · 1.0×` | **[server]** | **cycles/sec** expressed as a guest-clock rate **and** the real-time ratio on one line (see below). The headline number; rendered in amber. |
| 4 | `ips` | `0.71 M/s` | **[server]** | **instructions/sec** (guest-MIPS). Distinct from cycles/sec because one instruction is many cycles — both are interesting (cycles → "is it keeping real-time?", ips → "raw work rate"). |
| 5 | `mem` | `64 KB map · 41 MB host` | **[server]** | **emulated RAM-map size** (the address-space size the board exposes) **and** **host working-set** (process memory). Two numbers, one row. |
| 6 | `tier` | `JIT` / `interpreter` | **[server]** | the locked display-only tier (§2). Amber when JIT. |
| 7 | `jit` | `compiled 312 · recompiled 4 · evicted 1 · smc 2` | **[server]** | shown **only when tier=JIT**. `CompileCount` / `TotalRecompiles` / `TotalEvictions` / `SmcHotPcCount`. |
| 8 | `cpu2` | `Z80 active` / `Z80 idle` | **[server]** | shown **only when** `Machine.CoprocessorActive` is meaningful for the board (SoftCard). Omitted on single-CPU boards. |

### 3.1 The real-time ratio (row 3, the "is it keeping up" signal)

The **real-time ratio** = emulated-clock-advance ÷ wall-clock-elapsed over the sample window. `1.0×`
means the guest is running at authentic speed; `2.0×` means twice real-time (faster than the real
machine); `0.6×` means it's falling behind. This is the single most useful "how's perf" number, so it
shares the headline row with cycles/sec:

```
guest   1.02 MHz · 1.0×
```

- **cycles/sec** is derived server-side from `Machine.Cpu.CycleCount` deltas over the `PF` sample window
  (a rate the host must compute — none exists today; see §7).
- The **ratio** is `cycles_per_sec ÷ nominal_guest_hz`, where `nominal_guest_hz` is the board's documented
  clock (e.g. the Apple ][+ ≈ 1.0205 MHz). If the host doesn't know a nominal clock for a board, the
  ratio is **omitted** from the row (show only `1.02 MHz`), never faked.
- Color: the ratio is amber when ≥ `0.95×` (keeping real-time) and `--fg #ccc` (not red — calm register)
  when below; we do **not** alarm-color a slow ratio. A slow emulator is information, not an error.

### 3.2 cycles/sec vs ips vs FPS — why all three

These answer different questions and the Builder must not collapse them:
- **fps [client]** — display frames the browser painted this second. Tells the user the *display* is
  smooth. Measured purely client-side from `FB` arrivals; the server never sends FPS.
- **guest cycles/sec [server]** — guest CPU clock advance per wall second → the real-time ratio. Tells
  the user the *emulation* is keeping authentic speed.
- **ips / guest-MIPS [server]** — instructions retired per wall second. Tells the user the raw *work*
  rate (and, with JIT on, how much the JIT is buying — ips climbs while the ratio stays pinned at the
  board's real speed if the host is frame-pacing, or climbs freely if it's running flat-out).

### 3.3 Memory — what we report and why

- **Emulated RAM-map size [server]:** the address-space size the board exposes (e.g. `64 KB` for the
  ][+). A small, stable, honest number — the guest's memory, not the host's. Derive from the board's
  address-space extent (additive accessor, §7).
- **Host working-set [server]:** `System.Diagnostics.Process.GetCurrentProcess().WorkingSet64`, rounded
  to MB. The "is this leaking / how heavy is the emulator process" number. No host-memory accounting
  exists today (§7) — this is the minimal addition: read the working set once per `PF` tick.
- We intentionally do **not** add GC-gen breakdowns, heap stats, or per-region maps — that's
  debug-grade noise for a glance HUD. Two numbers on one row is the ceiling.

---

## 4. Client-measured FPS — the exact seam

FPS is computed entirely in the browser; the server sends nothing for it. The hook is the existing
`FB`-frame branch in `ws.onmessage` (`wwwroot/app.js` — after the `0x46 0x42` `'F','B'` check, around
the `firstFrameSeen` line / `ctx.putImageData`):

- Maintain a small ring (or a 1-second bucket): on each accepted `FB` frame, push `performance.now()`.
- FPS = count of frame timestamps within the last 1000 ms. Update the HUD's `fps` row on a light timer
  (the same ~250 ms HUD repaint as the `PF` cadence, §6.3) — **not** on every frame (avoid layout
  thrash).
- Round to an integer. Before any frame has arrived, show `fps —` (em dash), never `0` (0 reads as
  "broken"; `—` reads as "not measured yet").
- This is the **only** client-computed metric. Everything else is server-pushed.

---

## 5. The HUD visual

A small, semi-transparent, corner-anchored panel floating over the canvas. Monospace. Dim background +
blur so it's legible over any canvas content (bright hi-res, black boot, white Videx terminal).

### 5.1 Anchor & placement

- **Anchored top-right**, with a `--pad`-equivalent (`16px`) inset from the canvas's top-right corner,
  `position: absolute` within a canvas-relative wrapper (the canvas gets a positioned parent; the HUD is
  a sibling overlay). **Top-right** is chosen over top-left because the Apple/Spectrum boot text and the
  CP/M `A>` prompt all live at the **top-left** of the display — anchoring the HUD top-right keeps it
  clear of the prompt the user is reading/typing at.
- The panel does **not** capture pointer events on the canvas behind it beyond its own box, and it never
  steals keyboard focus (it has no focusable controls — it is pure output).
- It scrolls/anchors with the canvas, not the page (so it stays in the corner of the *display*, not the
  viewport).

### 5.2 Style (all from existing tokens)

| Property | Value | Source |
|---|---|---|
| background | `rgba(0, 0, 0, 0.7)` | a 70%-opaque form of `--canvas-bg #000` — same black, dimmed |
| backdrop | `backdrop-filter: blur(4px)` (+ `-webkit-` prefix) | new CSS property, no new token |
| border | `1px solid #444` | reuse `--kbd-border` |
| border-radius | `3px` | reuse `--kbd-radius` |
| padding | `8px 10px` | within the existing spacing register (`--pad` is 16; the HUD is tighter) |
| font-family | `ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace` | the surface's first *monospace* need; system mono stack, **no web font** |
| font-size | `12px` | reuse `--muted-size` |
| line-height | `1.5` | for scannable rows |
| label color | `#888` | reuse `--muted` |
| value color | `#ccc` | reuse `--fg` |
| accent (tier=JIT, headline value) | `#d8a657` | reuse `--drive-active` |

Labels are lowercase, fixed-width-aligned in a left column; values in a right column. No icons, no
sparklines, no charts — text rows only.

### 5.3 ASCII mock — HUD on, JIT tier, SoftCard board (the fullest case)

```
        ┌──────────────────────────────────────────────────────┐
        │  ]                                          ╔════════════════════════════╗
        │     (canvas — Apple boot / hi-res / Videx)  ║ board    Apple ][+         ║
        │                                             ║ fps      60                ║
        │                                             ║ guest    1.02 MHz · 1.0×   ║  ← amber value
        │                                             ║ ips      0.71 M/s          ║
        │                                             ║ mem      64 KB · 41 MB     ║
        │                                             ║ tier     JIT               ║  ← amber word
        │                                             ║ jit      c312 r4 e1 smc2   ║
        │                                             ║ cpu2     Z80 idle          ║
        └──────────────────────────────────────────────────────╚════════════════════════════╝
                    « TEXT · 40×24 · page 1 »                          ← existing mode label, unaffected
        ┌─ Drive 1 ─────────────┐   ┌─ Drive 2 ─────────────┐         ← existing control strip, unaffected
```

(The `╔═╗` box is the dim/blurred overlay; in reality it overlaps the canvas's top-right interior. The
mock pulls it aside for legibility. The `jit` row uses compact prefixes `c`/`r`/`e`/`smc` to stay on one
line at 12px monospace; the full words appear as the row's `aria-label`/title — see §8.)

### 5.4 ASCII mock — HUD on, interpreter tier, single-CPU board (the lean case)

```
                                             ╔════════════════════════════╗
                                             ║ board    ZX Spectrum 48K   ║
                                             ║ fps      50                ║
                                             ║ guest    3.50 MHz · 1.0×   ║
                                             ║ ips      0.98 M/s          ║
                                             ║ mem      64 KB · 39 MB     ║
                                             ║ tier     interpreter       ║  ← muted word, no amber
                                             ╚════════════════════════════╝
```

Note: **no `jit` row**, **no `cpu2` row** — both omitted (not blanked) because they don't apply. `tier`
reads `interpreter` in `--muted #888` (no amber anywhere except, if the ratio ≥ 0.95×, the headline
`3.50 MHz · 1.0×` value).

### 5.5 ASCII mock — HUD off (default)

The HUD is **off by default** on page load (it's an instrument you reach for, not always-on clutter).
Off = the panel is not in the DOM / `display:none`; the canvas is exactly the existing surface. A single
discoverability hint lives in the existing `kbd` hint line (§ copy below):

```
        (canvas, unobstructed — identical to today's surface)
                    « TEXT · 40×24 · page 1 »
        ┌─ Drive 1 ─────────────┐   ┌─ Drive 2 ─────────────┐
                connected · Apple ][+ · documented 6502
        [ click to enable sound ]
        Uppercase only. Ctrl+B = BASIC. Ctrl+Backspace = RESET. ` = perf HUD
```

---

## 6. The data contract

### 6.1 Frame type — recommend a NEW `PF` frame (not extending `ST`)

**Decision: add a new `"PF " + JSON` text frame**, sibling to `ST`. Two independent reasons:

1. **Opposite cadence semantics.** `ST` is an **on-change** snapshot — `StatusPusher.Tick()` compares the
   encoded bytes and only pushes when the snapshot *differs* (`FrameCodec.cs` notes: "equal snapshots →
   equal bytes"). Perf metrics **always differ** every tick (rates, counters, working-set all move), so
   routing them through `ST` would make `StatusPusher`'s change-detection fire on every tick — defeating
   the dedupe and entangling two unrelated update streams. `PF` is an unconditional **periodic** push.
2. **Clean separation of concern.** `ST` carries machine *state* (board, asset, video mode, drives) that
   the drive panels and mode label bind to (`window.machineStatus`). `PF` carries *telemetry* that only
   the HUD binds to (`window.perfStats`). Keeping them separate means the perf PR never risks the
   shipped drive/mode UI.

**Client routing:** `app.js` `ws.onmessage` already branches on string vs binary, and routes all strings
to `handleStatusText`. Add a prefix check at the top of `handleStatusText` (or a sibling): a frame
starting with `"PF "` → `handlePerfText` → `window.perfStats = parsed; repaintHud()`. A frame starting
with `"ST "` keeps its existing path untouched. (Both are text frames on the same socket; the prefix is
the discriminator, exactly as `ST` is today.)

### 6.2 The `PF` JSON shape

Compact, lower-case keys, stable order (matches the `ST`/`FrameCodec` convention). Encoder lives next to
`EncodeStatus` in `FrameCodec.cs` as `EncodePerf(PerfStats stats)` returning `"PF " + JSON`:

```jsonc
{
  "fps":  null,            // always null/absent from the server — FPS is client-only (§4). Listed here
                           //   only to document that the server does NOT own it. Omit the key entirely.
  "board": "Apple ][+",    // string; mirrors MachineStatus.Board (may dedupe to the ST value client-side)
  "cps":  1020500,         // guest cycles/sec over the sample window (number, Hz)
  "hz":   1020500,         // board nominal guest clock in Hz, or null if unknown (drives the ratio)
  "ips":  712000,          // guest instructions/sec over the sample window (number)
  "ramBytes":  65536,      // emulated address-space size in bytes
  "hostBytes": 42991616,   // host process working-set in bytes
  "tier": "jit",           // "jit" | "interpreter"  — the locked display-only tier (§2)
  "jit": {                 // present ONLY when tier == "jit"; omit the key entirely for interpreter
    "compiled":  312,
    "recompiled": 4,
    "evicted":   1,
    "smcHot":    2
  },
  "cpu2": "idle"           // "active" | "idle" for a coprocessor board; omit the key for single-CPU boards
}
```

Client formats the display strings from these raw numbers (so the wire stays locale-neutral and the
formatting lives in one place):
- `cps`/`hz` → `1.02 MHz` (and `· 1.0×` ratio = `cps/hz`, omitted if `hz` is null/absent).
- `ips` → `0.71 M/s` (or `712 k/s` under 1 M; one decimal).
- `ramBytes` → `64 KB`; `hostBytes` → `41 MB` (integer MB).
- `tier` → the word, amber iff `"jit"`.
- `jit.*` → `c312 r4 e1 smc2`.
- `cpu2` → `Z80 active` / `Z80 idle` (the `Z80` label is the board's coprocessor name from §7's
  `Machine.Coprocessor`; if the name is unavailable, fall back to `coproc active`/`coproc idle`).

### 6.3 Cadence

- Server pushes `PF` at **~3 Hz** (every ~333 ms). The brief's ~2–4 Hz band; 3 Hz is smooth enough that
  rates don't visibly jitter, slow enough to be negligible load and **NOT per-frame**. The push is
  gated on **HUD-open** if cheap to signal, but defaulting to always-push at 3 Hz is acceptable (a tiny
  JSON 3×/sec is nothing). **Decision: always push at 3 Hz** for PR simplicity; an "only when HUD open"
  optimization is an additive follow-on, not required.
- The sample window for `cps`/`ips` is the wall-time between consecutive `PF` ticks (so rates are
  averaged over ~333 ms — steady, not spiky). The host keeps the previous `CycleCount` / instruction
  count + a timestamp and computes deltas each tick (§7 — no rate computation exists today).
- The client repaints the HUD on each `PF` arrival (~3 Hz) and folds in the latest client FPS at the
  same beat. No `requestAnimationFrame` loop is needed for the HUD; it's event-driven off `PF`.

### 6.4 The backtick toggle interaction

- **Key:** backtick `` ` `` — confirmed safe end-to-end:
  - `MapDomCode` has **no `Backquote` arm** → the guest receives `KeyCode.None` → no-op (the key never
    affects the emulated machine).
  - `app.js` `sendKey` does **not** `preventDefault` `Backquote` and the browser has no default action to
    suppress for it, so intercepting it costs nothing.
- **Handler placement:** add a `keydown` listener (a small dedicated one, sibling to the existing
  `Ctrl+Backspace` listener in `app.js`) that, on `ev.code === "Backquote"`, **toggles HUD visibility and
  returns early — before `sendKey` forwards anything**. Concretely: the HUD toggle must run *ahead of*
  the `sendKey` path (either intercept in `sendKey` itself with an early `return` for `Backquote`, or
  ensure the toggle listener runs and the key is simply never machine-meaningful — it's `None` anyway).
  Recommended: intercept at the top of `sendKey` — `if (ev.code === "Backquote") { if (action === "down")
  toggleHud(); return; }` — so the key never reaches the wire and only the keydown toggles (keyup is
  swallowed too).
- **Behavior:** first press → HUD appears (panel added / `display:block`), `window.hudOn = true`. Second
  press → HUD hides. State is session-only (no persistence in v1; a `localStorage` remember-my-choice is
  an additive follow-on). Default = **off** on load.
- **No modifier, no chord** — single backtick. It's a tilde-key tap, the classic "console/HUD" key, and
  it's free on this surface.
- **Discoverability:** append ` `` ` ` ` = perf HUD` to the existing `kbd` hint line (copy below). One
  short addition; the hint line already lists `Ctrl+B`, `Ctrl+Backspace`.

---

## 7. Host accessors the Builder must add FIRST (prerequisites)

These are **additive host accessors** — read-only seams that expose state the host can already see
internally but the surface can't yet read. They are the gating work; the HUD can't be wired until they
exist. **Assessment: none of these require an Architect ADR** — they are public-forwarding shims and
one process-memory read, not data-model or cross-cutting-abstraction decisions. (If, while implementing,
the JIT-internals forwarding turns out to need a new public type that ripples across the JIT package,
**that** single question — and only that — is worth a quick Architect check. The default expectation is
no Architect needed.)

| # | Need | Today | Add (additive, public, read-only) |
|---|---|---|---|
| 1 | **Execution-tier query** | only `Cpu is JittedCpu<T>` (a type test) | a public property the host/surface can read, e.g. `Machine.ExecutionTier` → `enum { Interpreter, Jit }` (or `bool Machine.IsJitted`). Drives the `tier` field + the conditional `jit`/SMC rows. |
| 2 | **JIT stats forwarding** | `CompileCount`, `TotalRecompiles`, `TotalEvictions`, `SmcHotPcCount` are **`internal`** on `JittedCpu<T>` / `BlockCache` | a **public forwarding seam** exposing these four as read-only values (e.g. a `JitStats` struct via `Machine.TryGetJitStats(out JitStats)` returning false on the interpreter tier). This is the one place to watch for a rippling public type (see Architect note above). |
| 3 | **cycles/sec rate** | `Machine.Cpu.CycleCount` exists (a monotonic counter); **no rate** | host-side rate computation in the `PF` producer: keep `(prevCycleCount, prevTimestamp)`, compute `cps = ΔCycleCount / Δseconds` each tick. No new public API on the CPU — the *producer* (next to `StatusPusher`) owns the delta. |
| 4 | **instructions/sec rate** | no per-instruction retired counter exposed (confirm) | if an instruction-retired counter exists internally, forward it like #3 and rate it; **if none exists**, add a public monotonic `Machine.Cpu.InstructionCount` (or equivalent) and rate it in the producer. Flag for Builder: verify whether a retired-instruction counter already exists before adding one. |
| 5 | **real-time ratio** | none | derived in the producer/client from `cps ÷ nominal_hz`. Needs a **board nominal clock** value — add `Machine.NominalClockHz` (or `BoardSpec.ClockHz`) → `double?` (null if the board doesn't declare one). No real-time accounting type needed; it's one number per board. |
| 6 | **emulated RAM-map size** | the address space exists; no exposed extent | a read-only `Machine.AddressSpaceBytes` (or read the board's declared map extent). Static per board — cheap. |
| 7 | **host working-set** | none (no host-process memory accounting) | `Process.GetCurrentProcess().WorkingSet64` read in the `PF` producer (server-side only; never a guest concern). One line; no new abstraction. |
| 8 | **coprocessor state** | `Machine.CoprocessorActive` + `Machine.Coprocessor` **already exist** | reuse as-is for the `cpu2` field. Read `CoprocessorActive` → `"active"`/`"idle"`; read `Machine.Coprocessor`'s name → the `Z80` label. **No new accessor** — this one's already there. |

Plus the surface-side plumbing (not host accessors, but Builder work):
- a **`PerfStats` DTO** + `FrameCodec.EncodePerf` (mirrors `MachineStatus`/`EncodeStatus`).
- a **`PerfPusher`** (mirrors `StatusPusher`) on a ~3 Hz timer in `Program.cs`'s pump, *without* the
  on-change gate (it pushes every tick).
- `app.js`: `handlePerfText` + `window.perfStats`, the FPS ring in the `FB` branch, the `Backquote`
  toggle, `repaintHud()`, and the HUD DOM/CSS.
- the one-line hint-line copy addition.

---

## 8. States (the calm-degenerate matrix)

| State | When | HUD appearance |
|---|---|---|
| **off (default)** | page load, or after a second backtick | not rendered; canvas unobstructed; hint line shows `` ` = perf HUD``. |
| **initializing** | HUD toggled on, **before the first `PF` frame** | panel visible with `board` from `ST` if known else `board —`; all server rows show `—` (em dash, `--muted`); `fps` shows the client value if frames are arriving, else `—`. **Never `0`, never `NaN`.** |
| **zero / boot** | counters genuinely zero at cold boot (`compiled 0`, `cps` near 0 before the guest runs) | show the real zeros (`c0 r0 e0 smc0`, `guest 0.00 MHz`). Zero here is **truthful** (the machine just started) — distinct from the em-dash "not measured yet". |
| **interpreter tier** | `tier == "interpreter"` | omit the `jit` row entirely; `tier` word in `--muted` (no amber); see §5.4. |
| **single-CPU board** | no coprocessor | omit the `cpu2` row entirely. |
| **unknown nominal clock** | `hz` null | show `guest 1.02 MHz` with **no** `· N×` ratio suffix; never show `· NaN×` or `· ∞×`. |
| **disconnected** | `ws.onclose`/`onerror` | the HUD **freezes on its last values** and appends a single muted line `· disconnected` at the bottom of the panel (it does not blank — frozen-but-labeled beats blank). The status line below still shows the canonical `disconnected — reload to reconnect` (`copy.md` §3). No red. |
| **stale (no `PF` for >2 s while connected)** | producer hiccup | dim the server-sourced values to `--muted` and keep showing them (don't blank); FPS keeps updating client-side. Self-heals on the next `PF`. |

---

## 9. Copy (every HUD string)

Tone matches `apple-2-plus/copy.md` §9 anti-patterns: lowercase labels, no red, no `Error:`, no emoji,
no exclamation marks.

| Element | Copy |
|---|---|
| Row labels | `board` `fps` `guest` `ips` `mem` `tier` `jit` `cpu2` (lowercase, fixed) |
| Tier values | `JIT` (amber) / `interpreter` (muted) |
| jit row | `c<N> r<N> e<N> smc<N>` (compact); full words in the row `title`/`aria-label`: `compiled N, recompiled N, evicted N, smc hot PCs N` |
| cpu2 values | `<name> active` / `<name> idle` (e.g. `Z80 active`); fallback `coproc active` / `coproc idle` |
| not-yet-measured | `—` (em dash, U+2014), `--muted` |
| disconnected line | `· disconnected` (muted, appended) |
| hint-line addition | `` ` = perf HUD`` (appended to the existing `kbd` hint line) |
| panel `aria-label` | `performance overlay` |

Strings explicitly NOT used: `Error`, `FAIL`, `0` as a placeholder for "unknown", `NaN`, `Infinity`,
any red, any emoji, `Click to…`, a modal, an `alert()`.

---

## 10. Accessibility & input notes

- **No focus capture.** The HUD has no focusable controls (it's pure output); it never traps the
  keyboard. The canvas keeps capturing keys at `window` level exactly as today.
- **Backtick never reaches the guest** (`MapDomCode` no-op) and is intercepted before `sendKey` forwards
  (§6.4). No `preventDefault` needed beyond the early return (the browser has no default for `Backquote`
  in this context).
- **Screen reader:** the panel carries `aria-label="performance overlay"`. It is **not** an `aria-live`
  region — it updates 3×/sec and would spam. Perf telemetry is a glance-instrument; the board/connection
  state that *matters* for SR users is already announced via the existing status line's
  `aria-live="polite"` (`apple-2-plus/interactions.md` §8). The HUD adds no SR-relevant state beyond it.
- **Color is never the only signal.** The amber tier word is *also* the literal word `JIT` vs
  `interpreter`; the amber ratio is *also* the number. No metric is conveyed by color alone.
- **Reduced motion:** the HUD has **no animation** (no spinner, no transition) — values just update in
  place. `prefers-reduced-motion` needs no special handling here; if the Builder adds a fade-in on
  toggle, gate it on `prefers-reduced-motion` (make it instant) per the Apple-surface §8 convention.
- **Contrast:** `#ccc`/`#888`/`#d8a657` on `rgba(0,0,0,0.7)` + blur clears WCAG AA for the value text;
  the blur guarantees legibility over bright hi-res content. (Polisher check: confirm the amber on the
  dimmed-black panel still clears AA — `#d8a657` on `#000` does; the 0.7 alpha + blur only darkens what's
  behind, so the effective contrast is at least as good.)

---

## 11. Out of scope (additive follow-ons, explicitly deferred)

- Any control to **change** the tier (the locked decision — display-only).
- Persisting the HUD on/off choice across reloads (`localStorage`).
- "Only push `PF` when the HUD is open" optimization (always-3 Hz is fine for v1).
- GC/heap breakdowns, per-region memory maps, sparklines/graphs, history.
- A draggable/resizable/repositionable panel (fixed top-right is v1).
- A CPU-bus-master indicator finer than `cpu2 active/idle` (ADR 0015 already deemed this noise).
- The boot-time `?tier=jit` flag is *optional* for this PR (the HUD reflects whatever booted regardless).

---

## 12. Builder summary (the one-PR checklist)

1. **Host accessors first** (§7, items 1–7; item 8 already exists): tier query, JIT-stats forwarding,
   cycles/sec + ips rate computation, nominal-clock, RAM-map size, host working-set. Confirm whether an
   instruction-retired counter exists before adding one.
2. **`PerfStats` DTO + `FrameCodec.EncodePerf`** → `"PF " + JSON` (§6.2), next to `EncodeStatus`.
3. **`PerfPusher`** at ~3 Hz, no on-change gate, in the pump (§6.3).
4. **`app.js`:** `handlePerfText` + `window.perfStats`; FPS ring in the `FB` branch (§4); `Backquote`
   toggle ahead of `sendKey` (§6.4); `repaintHud()`; HUD DOM + CSS from existing tokens (§5).
5. **Hint-line copy** addition (§9).
6. **States** (§8) and **a11y** (§10) wired: em-dash placeholders, conditional `jit`/`cpu2` rows, frozen
   `· disconnected`, `aria-label`, no live region.
7. **Wait for `index.html`/`app.js` to be free** (favicon + Pascal Builders) before touching them.

---

### Appendix — grounded symbols (for the Builder's audit trail)

- `ST` frame: `FrameCodec.EncodeStatus` (`src/CpuEmulator.Surface.Web/FrameCodec.cs:78`), pushed by
  `StatusPusher.Tick()` on-change (`StatusPusher.cs`), routed client-side by `handleStatusText`
  (`wwwroot/app.js:90`, gated in `ws.onmessage` `app.js:185`). `PF` mirrors this path with its own
  prefix and its own (un-gated) pusher.
- `FB` frame draw (the FPS hook): `ws.onmessage` `'F','B'` branch, `app.js:189`–`209`
  (`firstFrameSeen` at `:191`, `ctx.putImageData` at `:209`).
- Backtick safety: `sendKey` (`app.js:212`) does not `preventDefault` `Backquote`; the existing
  `preventDefault` list (`app.js:220`–`223`, `:232`) covers only Space/Arrows/`Ctrl+B`/`Ctrl+C`/
  `Ctrl+Backspace`; `MapDomCode` has no `Backquote` arm.
- Coprocessor (already present): `Machine.CoprocessorActive` + `Machine.Coprocessor`.
- JIT internals (need forwarding): `JittedCpu<T>` / `BlockCache` `CompileCount`, `TotalRecompiles`,
  `TotalEvictions`, `SmcHotPcCount` (currently `internal`).
- Tokens reused (named in `apple-2-plus/tokens.md`): `--bg #111`, `--fg #ccc`, `--muted #888`,
  `--muted-size 12px`, `--canvas-bg #000`, `--kbd-bg #222`, `--kbd-border #444`, `--kbd-radius 3px`,
  `--drive-active #d8a657`.
