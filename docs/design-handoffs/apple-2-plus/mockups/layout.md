# Apple ][+ web surface — ASCII layout mockups

All layouts are the existing centered flex column (`#111` bg, `#ccc` text, `system-ui`, `gap: 12px`,
`padding: 16px`). The canvas keeps `image-rendering: pixelated` + `1px solid #333` border. Everything
below the canvas is the **NEW control strip** — built only from existing tokens (see `tokens.md`).

Legend: `[ … ]` = button; `[ … ▾]` = dropdown/select; `●`/`○` = drive light on/off; `« »` = dynamic text.

---

## 1. Full page — booted Apple ][+ (assets present, the happy path)

```
                         CpuEmulator — Apple ][+                       ← h1, 14px/600
        ┌──────────────────────────────────────────────────────┐
        │                                                        │
        │            ]  (Applesoft prompt, blinking cursor)      │      ← canvas, pixelated,
        │                                                        │        280×192 (hi-res) or
        │                                                        │        the text-mode raster,
        │                                                        │        3× upscaled, 1px #333
        │                                                        │
        └──────────────────────────────────────────────────────┘
                    « TEXT · 40×24 · page 1 »                          ← mode label, 12px #888

        ┌─ Drive 1 ─────────────┐   ┌─ Drive 2 ─────────────┐         ← control strip (NEW)
        │ ○  « empty »          │   │ ○  « empty »          │
        │ [ Library ▾] [ Insert…] │ │ [ Library ▾] [ Insert…] │
        └───────────────────────┘   └───────────────────────┘

                connected · Apple ][+ · documented 6502                ← status line, 12px #888
        [ click to enable sound ]                                      ← sound button (verbatim)
        Uppercase only. Ctrl+B = BASIC. Ctrl+Reset = RESET.            ← kbd hint line
```

Notes:
- The **mode label** under the canvas is read-only; it reflects guest video state (`TEXT`/`LORES`/
  `HIRES`/`MIXED`, page 1/2). It is *not* a control — the user never picks the mode; the running
  program does (a `$C05x` access). See `interactions.md` §1.
- Each **drive panel** is a bordered group (`1px solid #333`, matching the canvas border) holding: the
  activity light + current-image label on the top line, the two insert routes on the bottom line.
- The status line, sound button, and hint line keep their Spectrum positions and styling.

---

## 2. The control strip, in detail (the one new region)

A drive panel has four states. The light is `○` (off) when the motor is off, `●` (on, amber) when the
motor is spinning (a disk access). The label shows the inserted image's name or `empty`.

```
EMPTY (no disk):
  ┌─ Drive 1 ─────────────────────────┐
  │ ○  empty                          │
  │ [ Library ▾]      [ Insert… ]     │
  └───────────────────────────────────┘

INSERTED, idle (motor off):
  ┌─ Drive 1 ─────────────────────────┐
  │ ○  DOS 3.3 System Master   [Eject]│
  │ [ Library ▾]      [ Insert… ]     │
  └───────────────────────────────────┘

INSERTED, active (motor on — disk being read/written):
  ┌─ Drive 1 ─────────────────────────┐
  │ ●  DOS 3.3 System Master   [Eject]│   ← light amber, label unchanged
  │ [ Library ▾]      [ Insert… ]     │
  └───────────────────────────────────┘

UPLOADING (file-picker chosen a local image, bytes in flight):
  ┌─ Drive 1 ─────────────────────────┐
  │ ◐  Uploading mygame.woz… 38%      │   ← spinner glyph, progress text
  │ [ Library ▾]      [ Insert… ]     │   ← controls disabled during upload
  └───────────────────────────────────┘
```

The **Library dropdown** lists the server-side cached catalog; the first option is a placeholder:

```
  [ Insert from library…        ▾]
    ─────────────────────────────
    DOS 3.3 System Master   (.dsk)
    ProDOS 2.4.2            (.po)
    Lode Runner             (.woz)
    Karateka                (.woz)
    ─────────────────────────────
    CP/M 2.2 (SoftCard)     (.dsk)   ← grouped/last; see CP/M flow
```

If the catalog is empty (no `disks/` cache), the dropdown is **disabled** and shows
`No cached disks — see tools/get-*`. The **Insert…** (upload) button stays enabled regardless — upload
is the always-available route. See `copy.md`.

---

## 3. Asset-absent banner (the anti-rot state)

When the Apple system ROM is **not** cached, the surface still loads but boots the SP0 demo fallback (the
shipped `Program.cs` behavior). Instead of the Spectrum's *silent* fallback, the Apple shows a banner
between the `h1` and the canvas — `#222` panel, `#444` left rule, `#ccc` text, no red:

```
                         CpuEmulator — Apple ][+
        ┌────────────────────────────────────────────────────────┐
        │  Apple ][+ ROMs not found — showing the demo pattern.   │   ← banner, calm
        │  Fetch them once:   tools/get-apple2-roms.sh            │   ← kbd-styled command
        │  then reload this page.                                 │
        └────────────────────────────────────────────────────────┘
        ┌──────────────────────────────────────────────────────┐
        │           (SP0 demo gradient test pattern)            │      ← the fallback canvas
        └──────────────────────────────────────────────────────┘
                    « demo · no ROM »
        ┌─ Drive 1 ──── (disabled) ──┐  ┌─ Drive 2 ──── (disabled) ──┐
        │  Insert a disk after        │  │  fetching the Apple ROMs.   │
        └─────────────────────────────┘  └─────────────────────────────┘
                disconnected? · demo fallback · no Apple ROM
        [ click to enable sound ]
        Fetch the ROMs to boot a real Apple ][+.
```

The banner copy is keyed per-asset (see `copy.md`): ROMs absent → `get-apple2-roms`; CP/M chosen but
its assets absent → `get-softcard-cpm` + `get-videx-roms`. The command itself is wrapped in a `kbd`
element so it reads as "type this".

---

## 4. The CP/M / Videx moment (the display switch)

CP/M entry is **disk-driven**, not a mode toggle: the user inserts the CP/M disk and the SoftCard boot
runs. The display switches itself from the 40-col Apple video to the 80-col Videx terminal when the
guest enables the Videx (`DisplayMultiplexer.SetActive`, ADR 0016). The canvas re-sizes automatically
(the client already follows the per-frame `FB` width/height). The mode label follows:

```
BEFORE (Apple video, booting CP/M from the disk):
        ┌──────────────────────────────────────────────────────┐
        │   APPLE ][   (40-col text while the 6502 boot runs)   │
        └──────────────────────────────────────────────────────┘
                    « TEXT · 40×24 »

AFTER  (the guest enabled the Videx — display switched itself):
        ┌────────────────────────────────────────────────────────────────┐
        │ A>                                                              │   ← 80 cols, wider canvas
        │                                                                │
        └────────────────────────────────────────────────────────────────┘
                    « Videx 80×24 · CP/M »
```

There is **no UI control** for the switch — it is the hardware truth (the guest drives the display). The
only UI change is the canvas geometry (handled) and the mode label (`Videx 80×24 · CP/M`). If the user
ejects the CP/M disk and resets, the next boot returns to Apple video and the label follows back.

---

## 5. Connection lifecycle (reused from Spectrum, status-line copy adapted)

The status line mirrors the Spectrum client's WebSocket lifecycle (`ws.onopen`/`onclose`/`onerror`),
extended with the board name + asset state:

```
connecting…                                    ← before the socket opens
connected · Apple ][+ · documented 6502        ← socket open, real machine booted
connected · demo fallback · no Apple ROM       ← socket open, fallback (banner also shown)
disconnected — reload to reconnect             ← socket closed
connection error — is the server running?      ← socket error
```

These are the only status-line strings; see `copy.md` for the full set.
