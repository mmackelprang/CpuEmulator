# Apple ][+ web surface — design handoff (overview)

> **Status:** Design handoff (Designer phase, Apple ][+ arc). For the Planner to turn into UX tasks.
> **Date:** 2026-06-20
> **Author:** Claude Designer.
> **Establishes** `docs/design-handoffs/` — this is the **first** handoff package in the repo. The
> Spectrum surface set the visual language *in code* (`src/CpuEmulator.Surface.Web/wwwroot/`); this
> package promotes that language to a written, citable handoff and extends it for the Apple ][+.
> **The design spec the Planner consumes** is `docs/superpowers/specs/2026-06-20-apple-2-plus-design.md`.

---

## What this is

The browser-facing UX for emulating the **Apple ][+** (the next "real machine" after the ZX Spectrum
48K) on the SP0 web surface. It covers everything the user sees and does:

- the **display** (multi-mode Apple video — 40-col text, lo-res, hi-res, mixed — plus the **40-col
  Apple ↔ 80-col Videx** switch for CP/M),
- the **keyboard** (uppercase-only ][+ keymap, RESET, the missing-lowercase reality),
- the **audio** (1-bit speaker — reuses the Spectrum beeper button),
- the **disk** (Disk II, **two drives**, insert/eject via **both** a cached-library dropdown **and** a
  browser file-upload picker, plus a drive-activity light),
- the **board / CP/M entry** flow (boot the plain ][+ vs. bring up the Z80 SoftCard + CP/M experience),
- and — called out explicitly because emulator UX rots here — every **asset-absent / empty / loading /
  uploading / error** state, since ROMs and the CP/M disk are fetch-on-demand and **nothing is
  vendored**.

## Who uses it

A single local user running `dotnet run` on `CpuEmulator.Surface.Web` and opening the local URL in a
browser. One machine per WebSocket connection (the shipped model — `Program.cs` `DemoSession`). No
auth, no multi-user, no persistence beyond the on-disk asset cache. The user is technical enough to run
a fetch script when told to, but the UX must *tell* them — clearly and without alarm — when an asset is
missing and which script fetches it.

## Relationship to existing design (`follows` / `extends` / `deviates`)

This surface is governed by the **Spectrum web client** as the canonical visual language. There was no
prior `docs/design-handoffs/` package; the source of truth is the shipped code
(`wwwroot/index.html`, `wwwroot/app.js`) and the Spectrum design spec
(`docs/superpowers/specs/2026-06-19-zx-spectrum-48k-design.md`).

- **FOLLOWS** (reused verbatim — see `tokens.md` for the exact values):
  - The page chrome: `#111` background, `#ccc` text, `system-ui` font, centered flex column,
    `gap: 12px`, `padding: 16px`; the 14px/600/`letter-spacing:.04em` `h1`.
  - The canvas: `image-rendering: pixelated`, `1px solid #333` border, `#000` backing; **client-side
    per-frame resize** from the `FB` header (the Spectrum client already does
    `if (canvas.width !== width) canvas.width = width` — this is what makes the 40↔80 switch free).
  - The status line: `#status`, 12px, `#888`.
  - The **single sound button** UX ("click to enable sound" → "sound on"), kept verbatim.
  - The `kbd`-styled hint line: `#222` / `1px solid #444` / `border-radius: 3px`.
  - The wire format: binary `FB` (display) + `AU` (audio) frames out, JSON `{action,code,char}` keys in;
    `MapDomCode` for DOM-`code` → portable `KeyCode`.
  - The asset-fetch precedent: `SpectrumRom.TryGetPath()` cache lookup, boot-real-machine-if-cached.

- **EXTENDS** (new surface area the Spectrum never had — each justified in `interactions.md` / `copy.md`):
  1. **A control strip below the canvas** — the Spectrum had only a sound button + a static hint. The
     Apple needs **disk controls (two drives)**, a **board/mode indicator**, and **per-drive activity
     lights**. This is the one genuinely new layout region. It reuses the existing tokens; it does not
     introduce a framework or a new visual idiom.
  2. **A NEW inbound-binary path: disk image upload.** The surface has only ever received text (JSON
     keys). Uploading `.woz`/`.dsk`/`.po` bytes into the running session is a new wire direction. Its
     empty / uploading / error states are designed in `interactions.md` and `copy.md`. (The Planner may
     split the upload *plumbing* into its own task — flagged in the spec.)
  3. **A cached-library dropdown per drive** — populated from a server-listed `disks/` catalog
     (the `get-*` scripts / cache dir). New, but it reuses the asset-fetch-and-cache convention.
  4. **An asset-status banner** — the Spectrum *silently* fell back to the SP0 demo when its ROM was
     absent. The Apple replaces silent fallback with **clear, non-alarming guidance** (a banner naming
     the exact fetch script). This is a deliberate improvement over the Spectrum's silent fallback.
  5. **A display-mode / active-source label** — the Apple is multi-mode and dual-display (Videx). A tiny
     read-only label tells the user what they're looking at (e.g. `HIRES · page 1` or `Videx 80×24`).

- **DEVIATES** (departs from a decided pattern): **none.** Every decision either follows the Spectrum
  client or extends it additively. No Claude Design handoff exists for this surface (none exist at all
  yet); where the visual language was silent (the control strip, the upload states), this handoff
  proposes patterns built **only from the existing tokens** — see the "missing handoff" note below.

## A note on the missing Claude Design handoff

Per the Designer playbook, when no Claude Design handoff exists for a surface the default is to *not*
invent a visual language alone. Here the constraint is softer than usual because a **strong in-code
visual language already exists** (the Spectrum client) and the owner's brief explicitly says to *match
its style and minimal/no-framework posture*. So this handoff does not invent an aesthetic — it composes
the Apple's new affordances out of the Spectrum's existing tokens and idioms. The one place a designer
would normally want a rendered mockup is the **control strip layout** (disk drives + indicators);
`mockups/` provides ASCII layouts for it, and the spec flags it as the single screen a future Claude
Design pass could refine if the owner wants pixel-level polish. Nothing here blocks the Planner.

## Success criteria

1. A user with **no assets** opens the surface and immediately understands (a) that they're seeing a
   fallback, (b) why, and (c) the exact command to get the real machine — with **zero alarm** (no red,
   no "error", no stack-trace energy).
2. A user with the **Apple ROMs** fetched boots to the Applesoft `]` prompt, can type (uppercase), hears
   the speaker, and can tell at a glance which video mode is live.
3. A user can **insert a disk** into either drive by two routes — pick from the cached library, or upload
   a local image — **eject** it, and see the **drive light** flicker as the disk is accessed.
4. A user with the **CP/M + Videx + SoftCard** assets can boot CP/M and watch the display switch to the
   **80-column Videx terminal** — without ever choosing the display themselves (the guest drives it).
5. Every absent / loading / uploading / error condition for display, disk, and CP/M has a defined,
   non-alarming state — no blank canvas with no explanation, ever.

## The files in this package

- `overview.md` (this file) — what/who/why, the follows-extends-deviates declaration, success criteria.
- `mockups/layout.md` — ASCII layouts: the full page, the control strip, each disk drive's states, the
  asset-absent banner, the CP/M / Videx moment.
- `interactions.md` — state machines and transitions: display modes + the 40/80 switch, disk
  insert/eject (both paths) + drive light, the upload flow (size/type validation, uploading/error),
  keyboard (RESET, modifiers), the board/CP/M entry flow, and the connection lifecycle.
- `copy.md` — every label, placeholder, hint, banner, and error string, verbatim.
- `tokens.md` — the reused Spectrum tokens (no new ones introduced) + the two semantic colors the drive
  light needs, with justification.
