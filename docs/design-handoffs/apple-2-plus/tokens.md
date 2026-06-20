# Apple ][+ web surface — tokens

The Apple surface introduces **no new visual language** — it reuses the Spectrum client's inline-style
values verbatim. This file (1) records the reused values as named tokens so the Planner/Builder and
Polisher have one citable reference, and (2) justifies the **two** new semantic values the drive light
requires. No CSS framework, no build step — these are plain values, exactly as the Spectrum client
embeds them in `wwwroot/index.html`.

## Provenance

Source of truth: `src/CpuEmulator.Surface.Web/wwwroot/index.html` (the `<style>` block) and `app.js`.
Every "reused" value below is copied from there — do not change them for the Apple surface (that would be
drift the Polisher flags across both machines).

## Reused tokens (FOLLOWS — verbatim from the Spectrum client)

| Token (proposed name) | Value | Source | Used for |
|---|---|---|---|
| `--bg` | `#111` | `body { background }` | page background |
| `--fg` | `#ccc` | `body { color }` | primary text |
| `--font` | `system-ui, sans-serif` | `body { font-family }` | all text |
| `--gap` | `12px` | `body { gap }` | column spacing |
| `--pad` | `16px` | `body { padding }` | page padding |
| `--h1-size` | `14px` | `h1 { font-size }` | the title |
| `--h1-weight` | `600` | `h1 { font-weight }` | the title |
| `--h1-tracking` | `.04em` | `h1 { letter-spacing }` | the title |
| `--canvas-border` | `1px solid #333` | `canvas { border }` | canvas + drive-panel borders |
| `--canvas-bg` | `#000` | `canvas { background }` | canvas backing |
| `--canvas-render` | `pixelated` | `canvas { image-rendering }` | crisp upscale |
| `--muted` | `#888` | `#status { color }` | status line, mode label, drive-off light |
| `--muted-size` | `12px` | `#status { font-size }` | status + mode label |
| `--kbd-bg` | `#222` | `kbd { background }` | `<kbd>` chips, banner panel |
| `--kbd-border` | `1px solid #444` | `kbd { border }` | `<kbd>` chips, banner left rule |
| `--kbd-radius` | `3px` | `kbd { border-radius }` | `<kbd>` chips |
| `--kbd-pad` | `1px 5px` | `kbd { padding }` | `<kbd>` chips |

The drive panels, the asset banner, and the control strip are composed entirely from the above. The
panel border = `--canvas-border` (`#333`); the banner panel = `--kbd-bg` (`#222`) with a `--kbd-border`
(`#444`) left rule; all secondary text = `--muted` (`#888`).

## New tokens (EXTENDS — justified)

Only the **drive-activity light** needs values not already in the Spectrum palette. The light must read
as "active" at a glance and remain calm (no alarm-red). One amber + the reuse of `--muted` for off:

| Token | Value | Justification |
|---|---|---|
| `--drive-active` | `#d8a657` | Amber "motor on" lamp — evokes the Disk II's in-use light. Chosen as a warm, non-alarming hue that's clearly distinct from the `#888`/`#ccc` greys and reads on `#111`. **Not red** (red = error in this surface; the drive light is normal activity). One new hue is the minimum to signal activity by color. |
| `--drive-idle` | `#888` (reuse `--muted`) | The off/idle light reuses the existing muted grey — **no new token**; an outline `○` in `--muted`. |
| `--drive-upload` | `#d8a657` (reuse `--drive-active`) | The upload spinner `◐` reuses the active amber — no third hue. |

That is the **entire** new-token footprint: **one** new color value (`--drive-active`). Everything else
is reuse. This keeps the two-machine surface visually unified and gives the Polisher a single new value
to track.

## Canvas sizing change (FOLLOWS the per-frame model, refines the CSS)

The Spectrum client hardcodes `canvas { width: 768px; height: 576px; }` (3× of 256×192). The Apple is
multi-geometry (280×192 hi-res, the text raster, **and** the Videx 720×216). A fixed CSS size would
distort one of them. **Refinement (not a token, a sizing rule):** size the canvas in CSS by a scale
factor on its *intrinsic* (per-frame) dimensions rather than a fixed px box — e.g. a `max-width` + `image-rendering: pixelated` with `height: auto`, or a JS-set CSS size = `intrinsic × 3` updated when the
frame dimensions change (the client already detects that change for the backing buffer). The **design
requirement**: both a 280-wide Apple frame and a 720-wide Videx frame upscale crisply and keep their
aspect — the user never sees a stretched display across the 40↔80 switch. The exact CSS/JS mechanism is
the Planner's call; this is the one place the Spectrum's fixed `768×576` does not transfer.

## What this surface does NOT introduce

- No new font, no web font (system-ui only).
- No spacing scale beyond `--gap` / `--pad`.
- No dark-mode variants (the surface is already dark; there is no light mode — consistent with the
  Spectrum client, which has no `dark:`-style variants).
- No icon set (the drive light is a text glyph `○`/`●`/`◐`; buttons are text labels).
- No animation library (the only motion is the amber on/off and the `◐` spinner, CSS-only, and gated by
  `prefers-reduced-motion` per `interactions.md` §8).
