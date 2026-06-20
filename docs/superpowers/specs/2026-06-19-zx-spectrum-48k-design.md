# ZX Spectrum 48K — the first real machine (design)

> **Status:** Approved design (brainstormed with the owner, 2026-06-19). Ready for an implementation plan.
> **Date:** 2026-06-19
> **Topic:** Emulate the **ZX Spectrum 48K** on the `BoardSpec` machine model + the SP0 web surface — its ULA
> (video / keyboard / border / beeper), the 50 Hz interrupt, the 16 KB ROM (fetched on demand), and `.SNA`
> snapshot loading — running real software in the browser, with sound.

## 1. Context

The first **"real" historical microcomputer**. Z80-based → reuses our Z80 core + JIT emit. Builds on:
- The `BoardSpec` machine model (pieces #1–3: declarative boards + `BoardMachineFactory` + the monitor host).
- **SP0** (the web surface + `IDisplayDevice`/`IKeyboardSink`/`IBlockDevice` contracts + `MachineHost` + the
  browser canvas, on `main`).

The Spectrum is the first real consumer of SP0's display/keyboard contracts, and it **motivates two
extensions**: **port-mapped I/O** (the ULA lives on the Z80 I/O port space, not memory) and **audio output**
(the beeper — an `IAudioSink` contract + a Web-Audio path in the web surface).

## 2. Goal & the first bar

- Boot the 16 KB ROM → the **BASIC copyright screen, visible in the browser**, with a working **keyboard**.
- **Beeper sound** (owner asked for audio in the first cut, not deferred).
- Load a **`.SNA` snapshot → a real game** running with bitmap video + sound + keyboard.

## 3. New capabilities this introduces (two small extensions)

### 3.1 Port-mapped I/O — the ULA is on Z80 `IN`/`OUT` port `$FE`
The board model is memory-mapped-only so far (port I/O was deferred). The Spectrum's ULA responds to the Z80
**I/O port space** (`IN A,($FE)` keyboard read; `OUT ($FE),A` border + beeper). The Z80 core already has a
separate **I/O bus** (`IoBus`); this extension routes it to a **port-mapped peripheral slot** in the
`BoardSpec` (a `PeripheralSlot` attachment kind = `Port`, matched by port address + a mask — the Spectrum
ULA decodes only bit 0 = 0, so it answers every even port; key the design on the real ULA decode). The ULA
is the first port-I/O peripheral; memory-mapped peripherals are unchanged.

### 3.2 Audio output — `IAudioSink` + the web-surface audio path
SP0 deferred sound; the beeper needs it now. Add (all additive):
- **`IAudioSink`** in `Core` — analogous to `IDisplayDevice`: the chip produces a **PCM sample buffer per
  frame** (e.g. `RenderAudio(Span<short> samples)` at a fixed host sample rate; raised on a scheduler audio
  tick). The host is a dumb player.
- **The web-surface audio path** (extend SP0's `CpuEmulator.Surface.Web`): `MachineHost` pushes the PCM
  buffer over the WebSocket; the **browser client renders it via the Web Audio API** (parallel to the
  framebuffer path; its own frame tag in the wire format).
- **Headless mode:** the PCM buffer is asserted in tests; no actual playback.

## 4. The ULA (one peripheral, on port `$FE`, reading main RAM)

- **`IDisplayDevice`** — `RenderInto` reads the machine's **screen RAM** (`$4000`–`$57FF` bitmap in the
  Spectrum's **non-linear line order** — the Y-address bit-shuffle; `$5800`–`$5AFF` attributes: 8 ink/paper
  colors + BRIGHT + FLASH) + the current **border** color → a **256×192 (+ border) RGBA** frame. `FrameReady`
  at **50 Hz** via the scheduler (≈ 69888 T-states/frame). The ULA holds an `IAddressSpace` reference to read
  main RAM (it does not own VRAM — unlike SP0's demo framebuffer).
- **`IKeyboardSink`** — the **8×5 key matrix**. The guest reads `IN ($FE)` with `A8–A15` selecting half-rows;
  the low 5 bits return pressed keys for the addressed half-rows (0 = pressed). The ULA maps SP0's normalized
  `KeyCode`s onto the matrix (host `PostKey` sets/clears matrix bits). Bit 6 of `IN ($FE)` = EAR-in (tape —
  deferred; return idle).
- **Border** — `OUT ($FE)` bits 0–2 → the border color (folded into the RGBA frame's border region).
- **Beeper** — `OUT ($FE)` bit 4 (EAR/speaker; bit 3 = MIC). The ULA records the 1-bit toggle stream over the
  frame (timestamped at the write's T-state) and renders it to PCM for `IAudioSink`.

## 5. The 50 Hz IM1 interrupt
The ULA raises the Z80 **maskable interrupt once per frame (50 Hz)** on the scheduler, via the existing
interrupt line. The ROM's main loop depends on it (keyboard scan, the FLASH counter, the frames clock). The
Spectrum runs **IM1** (the ROM sets it).

## 6. The ROM — fetched on demand (NOT vendored)
The 48K Spectrum ROM (16 KB) is Amstrad's (redistributable for emulation, but **not committed to the repo**).
A **`tools/get-spectrum-rom.sh` + `tools/get-spectrum-rom.ps1`** script fetches it into the asset/vector cache
(the same convention as `tools/get-zexall` / the TomHarte vectors), with the licensing note. The
`SpectrumBoard` loads it from the cache; ROM-dependent tests are **skip-with-note when the ROM is absent**
(mirroring the ZEX/Klaus gating) so CI without the ROM stays green.

## 7. `.SNA` snapshot loading (the instant path to real games)
The 48K `.SNA` format: a **27-byte header** (`I`, `HL'/DE'/BC'/AF'`, `HL/DE/BC/IY/IX`, `IFF2`, `R`, `AF`,
`SP`, `IM`, border) + **49152 bytes** of RAM (`$4000`–`$FFFF`). A `SnaSnapshot` loader restores the Z80
registers + RAM into a `Machine`; the **PC is taken from the top of the restored stack** (the `.SNA` resume
idiom — `RETN`-style: pop PC from `SP`, `SP += 2`), then execution continues. (`.Z80`/`.TAP` are follow-ons.)

## 8. The Spectrum board + SP0 integration
- **`SpectrumBoard`** (`CpuEmulator.Machines`) — a `BoardSpec`: **Z80** + **16 KB ROM** (`$0000`–`$3FFF`) +
  **48 KB RAM** (`$4000`–`$FFFF`) + the **ULA** as a **port-`$FE` peripheral** implementing
  `IDisplayDevice` + `IKeyboardSink` + `IAudioSink` (+ border/beeper) + the **50 Hz interrupt** wiring.
- **Hosted by SP0's `MachineHost`** (the web surface): the ULA's `IDisplayDevice` → the browser canvas; the
  browser keyboard → `IKeyboardSink`; the ULA's `IAudioSink` → the browser Web Audio. Also bootable in the
  monitor host (it's a `BoardSpec`).

## 9. Validation (un-fakeable gates)
- **ROM boot (skip-with-note if the ROM is absent):** boot → the first 50 Hz frame's framebuffer matches the
  **BASIC copyright screen** (a committed reference RGBA hash) on **both tiers** (interpreter + JIT).
- **Keyboard:** a synthetic `PostKey` → the guest's `IN ($FE)` reads the correct matrix bits.
- **Beeper:** an `OUT ($FE)` bit-4 toggle sequence → the expected PCM waveform from `RenderAudio`.
- **Border:** an `OUT ($FE)` → the border RGBA changes.
- **`.SNA`:** loading a small known snapshot → the first rendered frame matches a committed reference.
- **No regression:** the SP0 acceptance path + the CPU/board/monitor suites stay green; `Core` stays AOT-clean
  (the new `IAudioSink` + port-I/O additions are additive).

## 10. Non-goals (follow-ons)
- **Memory contention** (the ULA stalls the CPU on lower-16K access during display — a cycle-accuracy
  refinement; most software runs without it).
- **`.TAP`/`.TZX` tape loading** (snapshots first; tape via the ROM LOAD trap later).
- **The 128K models** (memory **banking** — which would exercise per-bank specialization, ADR 0013 — + the
  **AY-3-8912** sound chip).
- Kempston/joystick + other peripherals.

## 11. Open questions for the Planner
- **Port-I/O routing:** how the Z80 core's `IoBus` `IN`/`OUT` reaches a `BoardSpec` port peripheral today
  (is there an existing port-peripheral seam, or does `BoardMachineFactory`/`Machine` need a `Port` slot
  kind?). The ULA's partial decode (answers all even ports) is the real behavior to reproduce.
- **`IAudioSink` shape + the WebSocket audio frame encoding** (sample rate, buffer size = one 50 Hz frame,
  S16 mono; a distinct wire tag from the `FB` frames).
- **The screen-RAM non-linear line order** (`$4000` Y-address bit-shuffle) — the exact mapping.
- **Plan phasing:** this spec spans two small extensions (port-I/O, audio) + the machine. The plan may phase
  them (extensions → the ULA/ROM/snapshot/board) or ship as one — the Planner's call per writing-plans.
