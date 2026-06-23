# Running the Machines

This guide is the single reference for **running every emulated system CpuEmulator ships today** — the
console boards (no assets, built-in ROMs) and the browser web-surface systems (asset-gated). For the deep,
command-by-command first 6502 session, see [Getting Started](getting-started.md); this page is the breadth
map across all the machines.

There are two ways to run a machine:

- **Part A — Console boards** (`CpuEmulator.Host`): four CPU reference boards that boot from built-in
  ROMs and talk over a UART on the terminal. No downloads.
- **Part B — Web-surface systems** (`CpuEmulator.Surface.Web`): a browser canvas (video out, keyboard in,
  beeper audio) over WebSocket. Which machine boots is decided **automatically** by what asset images are in
  your local cache — see the [selection logic](#how-the-web-server-picks-a-system) below.

All web-surface ROM/disk images are **copyrighted or owner-supplied and are never committed to the
repository**. They are fetched on demand (or supplied by you) into the asset cache:

```
$CPUEMULATOR_TESTVECTORS        (if set)
~/.cache/cpuemulator/vectors    (the default)
```

The same cache root holds the optional test vectors (TomHarte, Klaus, ZEX). The per-asset `tools/get-*`
scripts write into it; the combined `tools/setup-*` scripts (Part B) chain the per-asset ones for the
multi-asset rigs.

---

## Part A — Console boards

```
dotnet run --project src/CpuEmulator.Host -- --board <name>
```

Four boards are registered (`src/CpuEmulator.Host/BoardRegistry.cs`). The default board when `--board` is
omitted is **`6502`**.

| `--board` name | CPU | Boots to |
|---|---|---|
| `6502` (default) | MOS 6502 | The breadboard demo ROM, then the machine-language **monitor REPL** (`*` prompt) |
| `breadboard6502` | MOS 6502 | Identical to `6502` (the same breadboard board — an alias) |
| `z80` | Zilog Z80 | A boot program poked into RAM that prints `OK\r` out the UART, then `HALT`s |
| `68000` | Motorola 68000 | A boot ROM that prints `OK\r` out the UART, then self-loops |
| `8086` | Intel 8086/8088 | A boot ROM that prints `OK\r` out the UART, then self-loops |

List the catalog at runtime:

```
dotnet run --project src/CpuEmulator.Host -- --board list
```

### The 6502 (default board)

```
dotnet run --project src/CpuEmulator.Host
# identical to:
dotnet run --project src/CpuEmulator.Host -- --board 6502
```

Boots the pre-wired breadboard 6502 (52 KiB RAM, a UART, an interval timer, an 8 KiB demo ROM at `$E000`)
and drops into the monitor REPL. The CPU sits at the reset entry (`$E000`); type `g 1000` to run the demo's
hello-print, `i TEXT` to feed UART input, `a` to assemble. The full walkthrough — every command, the captured
transcript, `--demo`/`--load`/`--terminal` examples — is in [Getting Started](getting-started.md). It is not
duplicated here.

### The z80 / 68000 / 8086 reference boards

```
dotnet run --project src/CpuEmulator.Host -- --board z80
dotnet run --project src/CpuEmulator.Host -- --board 68000
dotnet run --project src/CpuEmulator.Host -- --board 8086
```

Each of these boots a tiny **"print `OK\r` then stop"** boot program (the same byte-for-byte programs the
reference-SBC smoke tests prove round-trip; `src/CpuEmulator.Host/BoardRoms.cs`) and then drops into the
**same CPU-agnostic monitor REPL** as the 6502. They are wired to a UART exactly like the 6502 board, so the
`OK` appears when the boot program runs. The Z80 runs its program from RAM at `$0000`; the 68000 and 8086
boot from ROM directly.

To see the `OK` boot output without entering the REPL, use `--demo` (below), or in the REPL type `g` to run.

### Shared modes (every console board)

These flags apply to whichever `--board` you select (`src/CpuEmulator.Host/HostOptions.cs`,
`Program.cs`):

| Mode | What it does |
|---|---|
| *(default — no mode flag)* | Boot, print the banner, drop into the line-oriented monitor REPL (`*` prompt). |
| `--demo` | Reset, run the boot program for 10,000 cycles, print any UART output, exit 0. (Mutually exclusive with `--load` and `--terminal`.) |
| `--load <bin> [--at $addr] [--pc $addr]` | Preload a raw binary into memory before the REPL. `--at` defaults to `$0200`; `--pc` sets the initial program counter. `--at`/`--pc` require `--load`. |
| `--terminal` | Open a raw per-keystroke terminal onto the guest UART (every key is a byte immediately, no Enter). **Ctrl-]** exits to the monitor. Needs an interactive console. May combine with `--load`. |
| `--board list` | Print the catalog of board names and exit. |

Examples (using the 8086 board; the modes are board-independent):

```
dotnet run --project src/CpuEmulator.Host -- --board 8086 --demo
dotnet run --project src/CpuEmulator.Host -- --board z80 --terminal
dotnet run --project src/CpuEmulator.Host -- --board 68000 --load prog.bin --pc $0008
```

See [Getting Started](getting-started.md) and the [Monitor Reference](monitor-reference.md) for the REPL
command set.

---

## Part B — Web-surface systems

```
dotnet run --project src/CpuEmulator.Surface.Web
```

Then open the URL the server prints (Kestrel's default is typically `http://localhost:5000`). The page is a
canvas that streams video frames out and key events in over a WebSocket, with an optional **enable sound**
button (Web Audio) for the systems that have audio.

> **One machine per connection.** Each new browser/WebSocket connection boots a fresh machine. There is **no
> `--board` switch for the web surface** — the server probes the asset cache and picks a system automatically.

### How the web server picks a system

The server decides which machine to boot by **probing the asset cache in a fixed priority order** — the
*first* set of assets it finds wins. This is the actual logic in
`src/CpuEmulator.Surface.Web/Program.cs` (`DemoSession.RunAsync`), and it is checked once per connection:

1. **CP/M 3.1 + Videx (80-column)** — boots if the Apple ][+ system ROM (`apple2/apple2plus.rom`), the
   apl2cpm3 disk (`cpm/apl2cpm3/CPM3.1_Disk_1.dsk`) **and** the real Videx firmware (`videx/videx-firmware.rom`)
   are all cached. This is the **80-column** headline: CP/M 3.1 boots to `A>` on the Videx 80×24 console (the
   display auto-switches to the Videx). It runs on `SoftCardVidexSurface` configured for apl2cpm3 (the slot-4
   SoftCard + the `Cpm3` raw-DOS33 disk skew). The real Videx firmware is required — the apl2cpm3 CRT80 console
   JMPs into the `$C800` firmware window, so without it the 80-col screen would be blank and the server falls
   through to the 2.2 disk instead.
2. **SoftCard CP/M 2.2 (40-column)** — else, boots if **both** the Apple ][+ system ROM (`apple2/apple2plus.rom`)
   **and** the 2.2 CP/M disk (`cpm/softcard-cpm.dsk`) are cached. (The CP/M discs + Videx firmware are only
   stat-checked when the Apple ROM is present.) This runs on the same Videx-capable `SoftCardVidexSurface`, but
   the 2.2 master is a 40-column console that never engages the Videx, so you get 40 columns.
3. **Apple ][+** — else, boots if the Apple ][+ system ROM (`apple2/apple2plus.rom`) is cached.
4. **ZX Spectrum 48K** — else, boots if the Spectrum ROM (`spectrum/48.rom`) is cached.
5. **SP0 demo board** — else, the built-in fallback (no assets required).

Each later probe only runs when the earlier branch was *not* taken, so the common boot path does no extra
file-stat work. **The priority is asset-driven, not a preference you set** — e.g. if you have the Apple ROM
cached, you get the Apple ][+ (or one of the SoftCard CP/M rigs, if a CP/M disk is also there) and *not* the
Spectrum, even if the Spectrum ROM is also cached. When **both** CP/M disks are cached, the 80-column CP/M 3.1
rig wins over the 40-column 2.2 disk (the 2.2 disk is the fallback). To switch the web surface back to the
Spectrum or the demo, the Apple/CP/M assets must not be present in the cache.

> **Accuracy note vs. Getting Started.** The "Running the web surface" section of
> [Getting Started](getting-started.md) describes only the Spectrum-or-demo behavior — it predates the
> Apple/SoftCard branches. The list above is the current, complete selection order; the Apple and SoftCard
> branches are probed *before* the Spectrum.

The sections below are ordered simplest-asset-first (Spectrum's single ROM → the Apple ][+ ROMs → the
multi-asset CP/M rigs → the no-asset demo), **not** by the probe priority above. Remember the probe order when
reasoning about *which* system actually boots: if you have several asset sets cached, the SoftCard CP/M →
Apple ][+ → Spectrum → demo priority decides the winner.

---

### ZX Spectrum 48K

A complete 48K Spectrum: the Z80 + the ULA driving the display, keyboard, and beeper
(`src/CpuEmulator.Surface.Web/SpectrumSurface.cs`).

**Assets:** the 16 KiB Spectrum 48K ROM. **Single asset → no combined setup script.** The ROM is **Amstrad's
copyright** (Amstrad granted redistribution permission for emulation); it is **fetched on demand, never
vendored**, into `spectrum/48.rom`.

Fetch it:

```bash
sh tools/get-spectrum-rom.sh
```
```powershell
tools/get-spectrum-rom.ps1
```

Run it (with no Apple/CP/M assets cached, so the Spectrum branch wins):

```
dotnet run --project src/CpuEmulator.Surface.Web
```

**What you see:** the Spectrum BASIC copyright screen with a working keyboard. Click **enable sound** for the
beeper. Without the ROM cached (and no Apple/CP/M assets either), the server runs the SP0 demo board instead.

---

### Apple ][+

The Apple ][+: a 6502 + the Apple video/keyboard/speaker triad, the Language Card, and a Disk II controller
(`src/CpuEmulator.Surface.Web/Apple2Surface.cs`).

**Assets** (`tools/get-apple2-roms`, written under `apple2/`):

| File | Size | Role |
|---|---|---|
| `apple2plus.rom` | 12 KiB | **Required** — the system ROM (Applesoft + Monitor). Its presence is what triggers the Apple ][+ boot. |
| `disk2.rom` | 256 B | The slot-6 Disk II boot ROM — needed to **boot a disk**. Without it the board still comes up to the Applesoft `]` prompt, but cannot boot a disk. |
| `char.rom` | 2 KiB | **Optional** — the character generator. A built-in fallback font covers it. |

All three are **Apple's copyright and owner-supplied** — the fetch script ships with **placeholder URLs**;
point them at your own source/mirror (the length sanity-check guarantees a correct image regardless of
source). Nothing is vendored.

This rig has **two ROM files that matter** (system + Disk II boot), so a combined script is provided:

```bash
sh tools/setup-apple2.sh        # get-apple2-roms (+ get-woz-disks if WOZ_DISK_URL is set)
```
```powershell
tools/setup-apple2.ps1
```

Or fetch the ROMs directly:

```bash
sh tools/get-apple2-roms.sh
```
```powershell
tools/get-apple2-roms.ps1
```

Run it (Apple system ROM cached, no CP/M disk):

```
dotnet run --project src/CpuEmulator.Surface.Web
```

**What you see:** the Apple ][+ power-on screen down to the Applesoft `]` prompt (or the ROM monitor). The
status line reflects the live video mode and the disk-drive state. If you cached `disk2.rom` you can boot a
disk image via the in-page disk library / upload (a `.dsk`, `.po`, or `.woz`); see
[Sample disks (WOZ)](#sample-disks-woz) below.

#### Apple Pascal (UCSD p-System) — ✅ verified (PR #153)

Apple Pascal (the UCSD p-System) runs on the **native Apple ][+ 6502 plus the Language Card — there is no
SoftCard involved.** (The Microsoft SoftCard is a Z80 coprocessor card for CP/M only; UCSD Pascal is a 6502
p-code interpreter and does not use it.) **Verified end-to-end:** the real Apple II Pascal 1.1 (UCSD p-System
II.1) distribution boots to the p-System `COMMAND:` line on the emulator — the `Apple2/PascalBootTests` gate
decodes the live screen and asserts the sign-on + the `COMMAND:` menu; `tools/BootProbe --apple-pascal`
captures the screenshot.

Two findings from the bring-up (**ADR 0021**):

- **No new disk sector order was needed.** Apple Pascal `.dsk` images are in **DOS 3.3 on-disk sector order**
  (carrying a UCSD filesystem), so the existing `SectorOrderKind.Dos33` read path is correct — the
  ProDOS/Pascal physical interleave, composed through the Disk II BIOS, resolves to the DOS 3.3 table.
- **The real fix was a Language-Card mode.** `SYSTEM.APPLE` write-enables LC RAM while *running from the
  Monitor/Applesoft ROM*, copies the p-code interpreter into the banked `$D000–$FFFF`, then jumps into it —
  a "read ROM, write RAM" mode our single-backing page table couldn't express. `Apple2LanguageCard` now
  routes that window as an MMIO write-through device in that mode (on the existing LC seam — no core change,
  JIT-coherent; the apl2cpm3 CP/M-3 boot and all LC tests stayed green). This is the second real Language-Card
  fidelity gain from running real software (after ADR 0018-C's two-latch fix).

**Boot topology** (authentic, two-drive): **`APPLE1` in drive 1** (it carries `SYSTEM.APPLE` + `SYSTEM.PASCAL`)
and **`APPLE0` in drive 2** (the program/compiler volume). Booting `APPLE0` alone reaches the genuine
`NO FILE SYSTEM.APPLE` halt — the boot loader works; the interpreter just isn't on that volume.

**To run it:** stage your owner-supplied Pascal disks with `tools/get-apple-pascal.ps1` (or `sh
tools/get-apple-pascal.sh`) — never vendored — then boot headless via `tools/BootProbe --apple-pascal`.
80-column Pascal via the Videx (given the right p-System 80-column driver) is plausible but **not yet
verified**; the 40-column boot above is confirmed.

---

### SoftCard CP/M 2.2 (40-column)

The Microsoft Z-80 SoftCard running **CP/M 2.2** on the Apple ][+. CP/M boots on the Z80 coprocessor
(interpreter tier) against shared RAM; the console is the Apple **40-column** text display
(`src/CpuEmulator.Surface.Web/SoftCardVidexSurface.cs` — see the note below on why the Videx surface is the
one that runs).

**Assets:**

- The Apple ][+ ROMs (`apple2/apple2plus.rom` **and** `apple2/disk2.rom` — the SoftCard needs the slot-6
  Disk II boot ROM to load CP/M; `char.rom` optional), via `tools/get-apple2-roms`.
- The CP/M 2.2 disk image `cpm/softcard-cpm.dsk` (143,360 bytes), via `tools/get-softcard-cpm`.

The CP/M `.dsk` is **owner-supplied / fetch-on-demand** — the script ships with a **placeholder URL guarded
to fail clearly** until you point it at the real Asimov-mirror source (owner sign-off given per ADR 0016).
The Apple ROMs are Apple's copyright. Nothing is vendored.

Fetch both halves:

```bash
sh tools/get-apple2-roms.sh         # the Apple ROMs (system + disk2 boot ROM)
sh tools/get-softcard-cpm.sh        # the CP/M 2.2 .dsk
```
```powershell
tools/get-apple2-roms.ps1
tools/get-softcard-cpm.ps1
```

> **Why no combined `setup-cpm` script for 2.2?** The CP/M 2.2 rig is two existing one-liners (Apple ROMs +
> the single CP/M disk). The combined script is reserved for the 80-column CP/M 3.1 rig, which has a genuinely
> larger asset set (see below). Run the two lines above, or reuse `tools/setup-apple2` for the ROM half.

Run it (Apple ROMs **and** `cpm/softcard-cpm.dsk` cached, and the apl2cpm3 80-column rig *not* cached → the
2.2 SoftCard branch wins):

```
dotnet run --project src/CpuEmulator.Surface.Web
```

**What you see:** CP/M boots to the `A>` prompt on the Apple **40-column** console. The CP/M 2.2 master is a
40-column console (it never programs the Videx CRTC), so the display **stays 40 columns** — `ActiveIndex`
stays on the Apple video source.

> **Note — the web surface always uses the Videx-capable surface.** The server's CP/M branch always builds
> `SoftCardVidexSurface` (the Videx 80-column card is wired and waiting), and the display auto-switches from
> Apple-40 to Videx-80 *only when the guest CP/M enables the Videx*. CP/M 2.2 does not, so you get 40 columns.
> The 40-column-only `SoftCardSurface` class exists in the tree but is **not** wired into the web server. The
> 80-column experience comes from CP/M 3.1 (next section).

---

### CP/M 3.1 + Videx (80-column) — via the apl2cpm3 rig

CP/M **3.1 "Plus"** for the SoftCard, whose BIOS drives the **Videx Videoterm 80-column card**. This is the
genuine 80-column CP/M console: CP/M 3.1's `icrt` routine programs the Videx CRTC, the display multiplexer
auto-switches to the Videx 80×24 source, and `A>` renders in 80 columns.

**The 80-column CP/M 3.1 boot is now reachable in the browser.** When the apl2cpm3 assets are cached, the web
server boots CP/M 3.1 on the Videx 80-column console automatically — it is the **first** branch in the
selection order above (ahead of the 40-column 2.2 disk). Cache the rig with `tools/setup-cpm-videx` (below),
run the server, and your browser shows CP/M 3.1 booting to `A>` in 80 columns:

```
sh tools/setup-cpm-videx.sh         # apl2cpm3 Disk 1 + the real Videx firmware (supply Apple ROMs separately)
dotnet run --project src/CpuEmulator.Surface.Web
```

Concretely:

- The web server's CP/M branch probes the apl2cpm3 CP/M 3.1 images first, via
  `CpuEmulator.Machines.Apl2Cpm3` (the `cpm/apl2cpm3/CPM3.1_Disk_1.dsk` subdirectory — distinct from the 2.2
  `cpm/softcard-cpm.dsk`), and builds `SoftCardVidexSurface` for apl2cpm3 (slot-4 SoftCard + the `Cpm3` disk
  skew). The 2.2 disk is the **fallback** — selected only when the apl2cpm3 rig is not cached.
- The same 80-column `A>`-on-the-Videx render can also be produced headlessly and captured to a PNG by
  `tools/BootProbe` (the owner-UAT artifact, no browser required):

  ```
  dotnet run --project tools/BootProbe -- --apl2cpm3-videx out.png
  ```

  This boots the real apl2cpm3 Disk 1 on the SoftCard+Videx board (slot 4) and renders the Videx 80×24
  console — the human-visible proof of the 80-column headline.

**Assets** for the 80-column CP/M 3.1 rig:

| Asset | Cache path | Notes |
|---|---|---|
| Apple ][+ system ROM | `apple2/apple2plus.rom` | Required (`tools/get-apple2-roms`). Apple's copyright, owner-supplied. |
| Disk II boot ROM | `apple2/disk2.rom` | Required to boot a disk. |
| CP/M 3.1 Disk 1 | `cpm/apl2cpm3/CPM3.1_Disk_1.dsk` | The bootable disk (`tools/get-apl2cpm3`). Owner-supplied / fetch-on-demand (placeholder URL, guarded). Disks 2–7 are optional data/tool/help disks. |
| Videx firmware ROM | `videx/videx-firmware.rom` (1 KiB) | **Optional** for the boot gate (a synthetic all-zero image covers it), **but the real firmware is required for the browser path and the `BootProbe --apl2cpm3-videx` screenshot** — the CRT80 console JMPs into the `$C800` firmware. Without it the web server falls through to the 2.2 40-column disk. `tools/get-videx-roms`. |
| Videx char ROM | `videx/videx-char.rom` (2 KiB) | **Optional** — a synthetic fallback font covers it; the real ROM sharpens glyphs. |

The CP/M 3.1 disk is **owner-supplied / fetch-on-demand**; the Videx ROMs are **owner-supplied and optional**.
None are vendored.

This rig has **two assets to orchestrate** (the CP/M 3.1 disk + the Videx ROMs), so a combined script is
provided:

```bash
sh tools/setup-cpm-videx.sh         # get-apl2cpm3 + get-videx-roms (you supply the Apple ROMs separately)
```
```powershell
tools/setup-cpm-videx.ps1
```

It chains `get-apl2cpm3` (the CP/M 3.1 Disk 1) and `get-videx-roms` (the Videx firmware + char ROMs). You
still need the Apple ][+ ROMs — run `tools/setup-apple2` or `tools/get-apple2-roms` for those.

**What you see (in the browser, or via `BootProbe`):** CP/M 3.1's sign-on and the `A>` CCP prompt rendered on
the Videx 80×24 console (an 80-column text screen, not the Apple 40-column page). With the rig cached, the web
server picks this path automatically and the display auto-switches from the Apple 40-column page to the Videx
80×24 source as CP/M 3.1's console driver brings the Videx online. (Note: the real Videx firmware is required
for the **browser** path too — the apl2cpm3 CRT80 console JMPs into the `$C800` firmware; without it the server
falls through to the 2.2 40-column disk.)

---

### SP0 demo board (fallback)

The built-in demo board — **no assets required** (`src/CpuEmulator.Surface.Web/DemoBoardSurface.cs`). This is
what the web server boots when none of the asset-gated systems above match.

```
dotnet run --project src/CpuEmulator.Surface.Web
```

With an empty asset cache (no Apple ROM, no Spectrum ROM), the page shows the SP0 demo board's framebuffer.
The demo board has no audio device.

---

## Asset cache reference

| System | Required asset(s) | Cache path(s) | Provenance |
|---|---|---|---|
| Console boards (6502/z80/68000/8086) | *none* | — | Built-in boot ROMs |
| ZX Spectrum 48K | `48.rom` (16 KiB) | `spectrum/48.rom` | Amstrad copyright; fetch-on-demand, never vendored |
| Apple ][+ | `apple2plus.rom` (12 KiB); `disk2.rom` (256 B) to boot a disk; `char.rom` (2 KiB) optional | `apple2/` | Apple copyright; owner-supplied (placeholder URLs) |
| SoftCard CP/M 2.2 (40-col) | Apple ROMs + `softcard-cpm.dsk` (143,360 B) | `apple2/`, `cpm/softcard-cpm.dsk` | Apple ROMs (copyright); CP/M `.dsk` owner-supplied / fetch-on-demand |
| CP/M 3.1 + Videx (80-col) | Apple ROMs + `CPM3.1_Disk_1.dsk` + real Videx firmware (`videx-firmware.rom`) | `apple2/`, `cpm/apl2cpm3/`, `videx/` | Apple ROMs (copyright); CP/M 3.1 disk + Videx ROMs owner-supplied. The web server boots this 80-col path automatically when all three are cached (the real firmware is load-bearing — without it the server falls through to the 2.2 disk). |
| Apple Pascal (UCSD p-System) | Apple ROMs + **your** p-System boot disks | `apple2/`, plus your disk images | Apple ROMs (copyright); p-System disks owner-supplied. **✅ Verified (PR #153) — boots to `COMMAND:`.** |
| SP0 demo board | *none* | — | Built-in |

**Setup scripts:**

| Script | Chains | For |
|---|---|---|
| `tools/get-spectrum-rom.{sh,ps1}` | — | ZX Spectrum ROM (single asset) |
| `tools/get-apple2-roms.{sh,ps1}` | — | Apple ][+ ROMs |
| `tools/get-softcard-cpm.{sh,ps1}` | — | CP/M 2.2 `.dsk` |
| `tools/get-apl2cpm3.{sh,ps1}` | — | CP/M 3.1 Disk 1 |
| `tools/get-videx-roms.{sh,ps1}` | — | Videx firmware + char ROMs |
| `tools/get-woz-disks.{sh,ps1}` | — | A public-domain `.woz` (owner-supplied via `WOZ_DISK_URL`) |
| **`tools/setup-apple2.{sh,ps1}`** | `get-apple2-roms` (+ `get-woz-disks` if `WOZ_DISK_URL` set) | The Apple ][+ rig |
| **`tools/setup-cpm-videx.{sh,ps1}`** | `get-apl2cpm3` + `get-videx-roms` | The 80-column CP/M 3.1 rig (supply Apple ROMs separately) |

### Sample disks (WOZ)

`tools/get-woz-disks.{sh,ps1}` fetches a single `.woz` image into `woz/demo.woz`. There is **no default URL**
— the WOZ format spec is public domain, but most circulating `.woz` images are copyrighted, so the script
**requires you to set `WOZ_DISK_URL`** to a confirmed public-domain image (or to drop a local file at the
destination). `setup-apple2` invokes it only when `WOZ_DISK_URL` is set.

Note: the in-page **disk library** (`GET /disks`) lists images from `disks/` in the cache (`.dsk`/`.po`/`.woz`)
plus the CP/M 2.2 disk; that is a separate directory from `woz/`. To make a disk appear in the library, place
it under `<cache>/disks/`.
