# Apple ][+ web surface — copy

Every user-visible string, verbatim. Tone: calm, terse, lowercase-leaning to match the Spectrum client's
`connecting…` / `connected` register. Commands are wrapped in `<kbd>`. No exclamation marks, no "Error!",
no red. American spelling. Where a string is the Spectrum's, it's marked **[verbatim from Spectrum]**.

---

## 1. Page chrome

| Element | Copy |
|---|---|
| `<title>` | `CpuEmulator — Apple ][+` |
| `h1` | `CpuEmulator — Apple ][+` |
| Sound button (initial) | `click to enable sound` **[verbatim from Spectrum]** |
| Sound button (after click) | `sound on` **[verbatim from Spectrum]** |

## 2. Mode label (under the canvas, read-only, `#888`)

Format: `<MODE> · <geometry>[ · page N][ · CP/M]`. Exact strings:

| State | Label |
|---|---|
| Text mode | `TEXT · 40×24 · page 1` (or `page 2`) |
| Lo-res | `LORES · 40×48 · page 1` |
| Hi-res | `HIRES · 280×192 · page 1` |
| Mixed | `MIXED · text+gfx · page 1` |
| Videx (CP/M) | `Videx 80×24 · CP/M` |
| Demo fallback | `demo · no ROM` |
| Char-gen fallback active | append ` · fallback font` is shown in the **status line**, not here |

## 3. Status line (`#status`, 12px `#888`, `aria-live="polite"`)

| State | Copy |
|---|---|
| Before socket opens | `connecting…` **[verbatim from Spectrum]** |
| Connected, real Apple booted | `connected · Apple ][+ · documented 6502` |
| Connected, fallback font | `connected · Apple ][+ · fallback font` |
| Connected, demo fallback | `connected · demo fallback · no Apple ROM` |
| Connected but no frame yet (>3 s) | `connected · waiting for first frame…` |
| Socket closed | `disconnected — reload to reconnect` |
| Socket error | `connection error — is the server running?` |

## 4. Hint line (the `kbd`-styled line under the sound button)

| Context | Copy (with `<kbd>` on the chords) |
|---|---|
| Real Apple booted | `Uppercase only. ` `Ctrl+B` ` = BASIC. ` `Ctrl+Backspace` ` = RESET.` |
| Demo fallback (no ROM) | `Fetch the ROMs to boot a real Apple ][+:` ` tools/get-apple2-roms.sh` |

Notes:
- The hint names the **actual browser binding** `Ctrl+Backspace`, not the hardware `Ctrl+Reset` the
  browser can't send (interactions §2.3).
- In fallback mode the RESET/BASIC hints are omitted (there's no Apple to drive).

## 5. The asset-absent banner (`#222` panel, `#444` left rule, `#ccc` text — never red)

**No Apple ROMs:**
```
Apple ][+ ROMs not found — showing the demo pattern.
Fetch them once:  tools/get-apple2-roms.sh
then reload this page.
```
(`tools/get-apple2-roms.sh` is wrapped in `<kbd>`.)

**CP/M chosen but its assets are missing** (shown as an inline drive error, not the top banner — §7):
see §7.

## 6. Drive panels

### 6.1 Drive group titles
- `Drive 1`, `Drive 2` (the `<fieldset>`-style legend on each bordered panel).

### 6.2 Image label (top line of each panel)
| State | Label |
|---|---|
| Empty | `empty` |
| Inserted | the image name, e.g. `DOS 3.3 System Master` (library) or `mygame.woz` (upload) |
| Uploading | `Uploading <filename>… <NN>%` or, indeterminate, `Uploading <filename>…` |

### 6.3 Buttons
| Button | Copy | Notes |
|---|---|---|
| Library select (placeholder option) | `Insert from library…` | the disabled first option |
| Library select (empty catalog) | `No cached disks — see tools/get-*` | the only option; select disabled |
| Library select (loading) | `Loading…` | select disabled until `GET /disks` returns |
| Upload button | `Insert…` | opens the OS file picker |
| Eject button | `Eject` | shown only when a disk is inserted |

### 6.4 Drive-light accessible labels (`aria-label`, not visible)
- Motor on: `drive 1 active` / `drive 2 active`
- Motor off, disk in: `drive 1 idle` / `drive 2 idle`
- Empty: `drive 1 empty` / `drive 2 empty`
- Uploading: `drive 1 uploading` / `drive 2 uploading`

### 6.5 Drive panels in demo fallback (disabled)
The two panels show a single calm sentence split across them (or one note if simpler):
```
Insert a disk after fetching the Apple ROMs.
```

## 7. Disk error / validation messages (inline in the drive panel, auto-clear ~6 s)

| Condition | Copy |
|---|---|
| Wrong file type | `Unsupported file — use .woz, .dsk, or .po` |
| Too large (>2 MB) | `File too large — Disk II images are under ~250 KB` |
| Empty file (0 bytes) | `That file is empty` |
| Server says corrupt (bad magic/length) | `That image looks corrupt` |
| Library item missing server-side | `Couldn't load that disk — it may have been removed from the cache.` |
| CP/M disk, Videx ROMs missing | `CP/M needs the Videx ROMs — run tools/get-videx-roms.sh` |
| CP/M disk, SoftCard CP/M missing | `CP/M disk not cached — run tools/get-softcard-cpm.sh` |

(All `tools/get-*.sh` wrapped in `<kbd>`. On Windows the `.ps1` sibling exists; the copy names the `.sh`
form for brevity — both scripts ship per ADR 0016 Decision 4. If the Planner wants OS-aware copy, swap to
`.ps1` when the server detects Windows; not required — naming one form is fine and matches the existing
`get-spectrum-rom` docs.)

## 8. The optional CP/M explainer note (first Videx activation, dismissible, session-scoped)

```
Now running CP/M on the Z-80 SoftCard (80-column Videx display).
```
With a small `[ × ]` / `dismiss` affordance. Shown once per session, the first time the Videx becomes the
active display. This is the **only** explanatory copy in the surface; everything else is a label or a
status. It is optional — the 80-col display already signals CP/M; the note just names it for newcomers.

## 9. Strings explicitly NOT used (anti-patterns to avoid)

- ❌ `Error:` / `ERROR` / red text / `alert()` / a modal dialog.
- ❌ `Failed to load ROM` (alarming; say what to do instead).
- ❌ `Please contact support` (there is no support; it's a local tool).
- ❌ `Loading…` as a permanent canvas overlay (the first frame is instant on localhost).
- ❌ Emoji in any UI string (the project convention).
- ❌ "Click here" (name the action: `Insert…`, `Eject`, the script).
