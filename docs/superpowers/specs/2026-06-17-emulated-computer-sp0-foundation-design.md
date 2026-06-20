# SP0 — Emulated-computer foundation: web surface + device contracts + demo machine

**Date:** 2026-06-17
**Status:** IMPLEMENTED (2026-06-19). Built per the plan
`docs/superpowers/plans/2026-06-19-sp0-web-surface.md`.
**Reconciliation with the Machine-model arc (shipped 2026-06-19, after this spec was written):** the
spec's hand-wired "DemoMachine" is realized as a declarative **`DemoBoard` `BoardSpec`** (RAM + a
framebuffer/keyboard/disk MMIO slots) built through the existing **`BoardMachineFactory`**, NOT a
hand-wired machine — `CpuEmulator.Machines` already existed. The web surface (`MachineHost` in the
new `CpuEmulator.Surface.Web`) **coexists** with the monitor host (piece #3) as a parallel surface
over the same `Machine` (canvas vs REPL). Sound stays out of SP0; an `IAudioSink`-shaped follow-on
for the first real machine's beeper is noted in the ROADMAP, not built. The three `Core` contracts
(`IDisplayDevice`/`IKeyboardSink`/`IBlockDevice`) shipped exactly as designed in §4.
**Kind:** new subsystem (the first slice of the "emulated computer" arc) — extends the existing device model.

---

## 1. Context & motivation

The goal of the broader arc is a **composable, CPU-agnostic device/machine toolkit**, proven by two real target
machines:
- **Atari 800** (6502) — custom chips: ANTIC/GTIA (display), POKEY (keyboard scan + sound + serial), PIA, SIO/810 (disk).
- **MS-DOS PC clone** (8086) — standard chips: CGA/MDA (display), 8259 PIC, 8253 PIT, 8255 PPI + keyboard controller,
  8237 DMA, µPD765 floppy controller, BIOS ROM + MS-DOS.

These two stress *completely different* device ecosystems, so a toolkit that expresses both is genuinely general.

**What already exists (the base SP0 extends):**
- `IPeripheral` — memory-mapped device contract (`Read`/`Write`/`TryPeek`/`Realize`), CPU-agnostic.
- `IScheduler` — cycle-accurate event scheduling ("device-honest time", `ScheduleEvery`).
- `IInterruptLine` — wired-OR IRQ/NMI with per-device `Source()` handles.
- `IMachineContext` — `Scheduler` + `Space(kind)` + `IrqLine` + `NmiLine`.
- `Machine` — wires CPU + memory map + devices + scheduler; chunked `Run`.
- `Breadboard6502` — a working example machine (6502 + RAM/ROM + `SimpleUart` + `IntervalTimer`), whose only I/O
  surface today is a raw-mode serial **terminal**.

**The gap** to video/keyboard/disk is two things: (1) a richer **presentation/host surface** (the terminal can't do a
bitmap display or real-time key events), and (2) three new **device types** (display, keyboard, block/disk).

**SP0 is the foundation slice:** the web surface + the three generic device contracts + a host pump + a *trivial* demo
machine — proving the whole path end-to-end **before** taking on ANTIC/GTIA or CGA fidelity.

**Sequencing within the arc** (owner chose "foundation + M5 in parallel"): SP0 (this) → SP1 Atari 800 → (M5 8086,
planned separately) → SP3 PC clone. Each gets its own spec → plan → implement cycle.

---

## 2. Scope

**In scope (SP0):**
- A **web surface**: a local HTTP + WebSocket server serving a browser client (canvas display + keyboard capture).
- The three **generic device contracts**: `IDisplayDevice`, `IKeyboardSink` (+ `KeyEvent`/`KeyCode`), `IBlockDevice`.
- **Generic device implementations** for the demo: a palettized framebuffer, a keyboard controller, a raw-image disk.
- A **`MachineHost`** pump: wall-clock-paced `Machine.Run` slices, frame push, input routing, plus a headless/"fast"
  mode for tests.
- A **`DemoMachine`** + an **acceptance test** that exercises all three contracts.

**Out of scope (SP1+ / later):** real chips (ANTIC/GTIA, CGA/MDA), sound, sprites/player-missile, multiple video
modes, real disk-image formats (ATR/IMG), DMA, the 8086 and PC clone, audio sync. SP0 proves the *contracts + the pump
+ the surface*, nothing more.

---

## 3. Architecture & data flow

**Components:**
1. **Web surface** (`CpuEmulator.Surface.Web`): a lightweight local HTTP + **WebSocket** server (ASP.NET Core minimal —
   built into .NET, no heavy GUI dependency). Serves a small HTML/JS canvas client; pushes framebuffer frames to it;
   receives input events back. WebSocket is chosen for a low-latency *bidirectional* real-time channel.
2. **Three device contracts** (in `CpuEmulator.Core`) — see §4. Each is a *host-side* capability a chip implements in
   addition to `IPeripheral` (which faces the CPU). The split is the keystone: `IPeripheral` faces the guest; these
   face the surface.
3. **Generic device implementations** (in `CpuEmulator.Peripherals`): `DemoFramebuffer`, `DemoKeyboard`, `DemoDisk`
   (+ a `DiskImage` file adapter).
4. **`MachineHost`** (in `CpuEmulator.Surface.Web`) — see §5.
5. **`DemoMachine`** (in `CpuEmulator.Machines`, the future home of `Atari800`/`PcClone`) — see §6.

**Data flow (the real-time loop):**
- **Display:** guest writes memory-mapped VRAM → on frame-complete the display device yields an RGBA buffer →
  `MachineHost` → WebSocket → browser canvas blits.
- **Input:** browser keydown/keyup → WebSocket → `MachineHost` → keyboard device → guest reads (memory-mapped, optional
  IRQ).
- **Disk:** guest ↔ memory-mapped disk controller → block device → host image file.

The emulator core stays **GUI-free**; the web surface is just one frontend behind these contracts.

**Project structure:**
- `CpuEmulator.Core` — the three contracts + `KeyEvent`/`KeyCode` (additive interfaces; no behavior change to existing
  types).
- `CpuEmulator.Peripherals` — `DemoFramebuffer`, `DemoKeyboard`, `DemoDisk`, `DiskImage`.
- **NEW** `CpuEmulator.Surface.Web` — the HTTP+WebSocket server, the browser client assets, and `MachineHost`.
- **NEW** `CpuEmulator.Machines` — `DemoMachine` now; `Atari800`/`PcClone` later.

---

## 4. The three device contracts

A machine's chip implements the relevant capability interface *in addition to* `IPeripheral`.

### 4.1 `IDisplayDevice` — display output (host pulls RGBA)
```csharp
public interface IDisplayDevice
{
    int Width  { get; }                 // native pixels; may change with video mode
    int Height { get; }
    void RenderInto(Span<uint> rgba);   // chip writes final RGBA8888, row-major — palette/mode lookup is the chip's job
    event Action FrameReady;            // raised at the chip's vblank, scheduled via IScheduler at the real refresh rate
}
```
The chip schedules its own vblank tick on the cycle-accurate `IScheduler`, so refresh matches the real machine. The
**surface is a dumb blitter** — it never knows about modes or palettes. This is what lets one surface serve both ANTIC
and CGA.

### 4.2 `IKeyboardSink` + `KeyEvent` — input (host pushes)
```csharp
public enum KeyAction { Down, Up }
public readonly record struct KeyEvent(KeyAction Action, KeyCode Key, char? Char);  // KeyCode = portable physical-key id
public interface IKeyboardSink { void PostKey(in KeyEvent e); }
```
The browser maps DOM key events → a normalized `KeyCode` (+ optional typed `Char`); each machine's keyboard chip owns
the translation to its native scan matrix (POKEY scan / 8255 PPI) and raises IRQ as appropriate. Unknown keys → no-op.

### 4.3 `IBlockDevice` — backing storage for disk controllers
```csharp
public interface IBlockDevice
{
    int  SectorSize  { get; }
    long SectorCount { get; }
    bool IsReadOnly  { get; }
    void ReadSector (long lba, Span<byte> dst);
    void WriteSector(long lba, ReadOnlySpan<byte> src);  // throws if IsReadOnly
}
```
Backed by a host image file via a `DiskImage` adapter (LBA → file offset). Machine-specific disk controllers (SIO/810,
µPD765) sit *on top*, translating the guest's register protocol into block ops; image-format quirks (ATR headers, etc.)
are the controller/adapter's concern (SP1+). SP0's demo uses a **raw** sector image.

### Error handling
- Out-of-range LBA → `ArgumentOutOfRangeException`. Write to a read-only device → throws (the controller surfaces a
  status bit to the guest). A too-small `RenderInto` span → throws.
- Unknown `KeyCode` → the machine's mapping ignores it (no-op).

---

## 5. The `MachineHost` pump

`MachineHost` drives a `Machine` for interactive (or headless) use:
- **Wall-clock pacing:** runs `Machine.Run` in slices sized to keep the guest at real speed; a `fast`/headless mode
  disables the throttle (for tests + batch).
- **Frame push:** subscribes each `IDisplayDevice.FrameReady` → calls `RenderInto` → pushes the RGBA frame to the
  surface (WebSocket binary frame: width, height, pixels).
- **Input routing:** inbound WebSocket key events → `IKeyboardSink.PostKey`.
- **Lifecycle:** start/stop, single machine per host instance (multi-machine is YAGNI).

`MachineHost` lives in `CpuEmulator.Surface.Web` for now; if a second surface type ever appears, extract a
surface-agnostic core then (YAGNI until then).

---

## 6. The demo machine + the SP0 acceptance test

### `DemoMachine` (`CpuEmulator.Machines`)
A **6502** + a small memory map, composed like a richer `Breadboard6502`:
- **RAM** + a small **ROM** holding the demo program.
- **`DemoFramebuffer`** — memory-mapped 8bpp palettized linear framebuffer (**256×192**, 1 byte/pixel VRAM → RGBA via a
  fixed 256-entry palette). Implements `IDisplayDevice` (`RenderInto` does the palette lookup; `FrameReady` on a 60 Hz
  scheduler tick).
- **`DemoKeyboard`** — memory-mapped data+status register (UART-rx-shaped); implements `IKeyboardSink` (host `PostKey`
  enqueues; guest reads; raises IRQ on the wired-OR line).
- **`DemoDisk`** — memory-mapped sector/command/data registers driving an `IBlockDevice` over a **raw** image file.

### The demo program (small 6502 ROM, monitor-assembled like the existing `DemoRom`)
1. Paints a **test pattern** to VRAM (proves display out).
2. Polls/IRQ-reads the keyboard; on a keypress, **echoes** it into the framebuffer (moving cursor / color change) —
   proves the input round-trip.
3. Issues a **read-sector** command for sector 0 and paints a byte from it on screen — proves the block device.

### Acceptance test (definition of "done" for SP0)
- **Automated (CI gate, headless/fast mode — no browser, no throttle):** run `DemoMachine` via `MachineHost` headless;
  assert (a) `RenderInto` produces the expected test-pattern RGBA, (b) a synthetic `PostKey` is observed by the guest
  and changes VRAM as expected, (c) a `ReadSector` surfaces the image bytes to the guest. Un-fakeable; runs without a
  display.
- **Manual (the visible proof):** `dotnet run` the web surface → open the local URL → see the test pattern, type a key
  → see it echo, see the disk byte. The "it works in a browser" moment, captured for the docs.

---

## 7. Decisions (resolved during the brainstorm)
- **Surface = web** (local HTTP + WebSocket → browser canvas). Keeps the core GUI-free; every machine is instantly
  shareable/screenshottable.
- **Transport = WebSocket** (bidirectional, low-latency); framebuffer frames out, key events in.
- **Framebuffer = RGBA8888**, chip renders the *final* pixels; the surface is a dumb blitter (general across ANTIC/CGA).
- **Input = normalized `KeyEvent`** (portable `KeyCode` + optional `Char`); the machine owns the native-scan mapping.
- **Block device = raw sector image** via `DiskImage`; machine-specific controllers + image formats are SP1+.
- **Refresh** is driven by each display chip's own scheduler-based vblank event (matches the real machine's rate).

## 8. Open questions (minor — resolve at plan time, none blocking)
- Exact `KeyCode` enum form (USB-HID-usage-like vs DOM `code` strings).
- WebSocket frame encoding (raw RGBA first; add a light delta/RLE only if bandwidth demands).
- Demo framebuffer exact resolution/palette (256×192 8bpp proposed).
- Whether `MachineHost` should be surface-agnostic from day one (default: no — keep it in `Surface.Web`, extract later).

## 9. Testing strategy
- The automated headless acceptance test (§6) is the SP0 gate.
- Unit tests per contract + per generic device (framebuffer palette lookup, keyboard queue/IRQ, block read/write +
  read-only throw).
- SP0 adds new projects + tests and only *additive* interfaces to `Core` — the existing 6502/Z80/68000 suites and
  byte-identity guards are unaffected.

---

## 10. Next step (when scheduled)
After M5 + M6 ship, invoke `writing-plans` to turn this spec into a bite-sized implementation plan (the web surface +
contracts + `MachineHost` + the demo machine + the acceptance test), then build it. Until then, this spec stands as the
approved design.
