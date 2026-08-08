# RetroCore — brand guide

**Proposed display name:** RetroCore
**Tagline:** *Old silicon, new JIT.*

## Why this name

Pluggable retro CPU cores, executed on a thoroughly modern trick — guest machine code translated
to .NET IL and JIT-compiled by RyuJIT. **RetroCore** carries both halves; the tagline supplies
the punchline.

**Alternates considered:** *Dynarec* (the technique, insider-only), *SiliconLoom* (weaving IL,
poetic but opaque), *Breadboard* (the 6502 machine's name; too narrow for a multi-arch framework).

## The mark

A DIP chip with a phosphor-green play button where the die would be: press play on a 6502. The
play triangle doubles as the "run" of the monitor's `g` command.

## Palette

| Color | Hex | Role |
|---|---|---|
| Void Purple | `#1E1B2E` | Background / primary brand color |
| Lilac | `#B39DFF` | Chip, structure, secondary text |
| Phosphor Green | `#39FF88` | Play, prompts, success states |

## Voice

Monitor voice: terse, lowercase commands, hex addresses. Docs can lean into the `* g $0200`
aesthetic — the monitor transcript in the README is already the best branding the project has.

## Files in this directory

| File | Use |
|---|---|
| `logo.svg` | Full lockup (mark + wordmark + tagline) for README headers and docs |
| `favicon.svg` | Square app mark, scales from 16px to full size |
| `favicon.ico` | Legacy multi-size favicon (16/32/48) for browsers that want `.ico` |
| `favicon-32.png` | 32px PNG favicon |
| `apple-touch-icon.png` | 180px iOS home-screen icon |
| `icon-512.png` | Large raster for app manifests, social cards, stores |

### Wiring the favicon into a web page

```html
<link rel="icon" href="/branding/favicon.svg" type="image/svg+xml">
<link rel="icon" href="/branding/favicon.ico" sizes="16x16 32x32 48x48">
<link rel="apple-touch-icon" href="/branding/apple-touch-icon.png">
```

### README header

```markdown
<p align="center"><img src="branding/logo.svg" alt="RetroCore" width="520"></p>
```

## Typography

Wordmark: **Montserrat Bold** (falls back to Segoe UI / system sans). Body text: the platform
default sans. For code-adjacent surfaces, any monospace at hand — the brand doesn't pin one.

The logo's wordmark is live SVG text, so it renders with whatever sans is installed; if you want
it pixel-identical everywhere, convert the text to outlines in any SVG editor and re-save.

## Dark and light backgrounds

The tile carries its own background, so both `logo.svg` and `favicon.svg` work unchanged on
light or dark pages. The wordmark in `logo.svg` is dark ink — on a dark page, either rely on the
tile alone (use `favicon.svg`) or restyle the two `<text>` fills to `#F0F2F5`.

---
*Generated as a proposal — names, colors, and marks are suggestions to accept, tweak, or reject.*
