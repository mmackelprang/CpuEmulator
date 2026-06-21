# PR-N — `VidexVideoterm` 80-column card Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the **Videx Videoterm** 80-column card (ADR 0016 Decision 3, research §8): `VidexVideoterm : IPeripheral, IDisplayDevice` — a slot-3 card whose **6845 CRTC** is programmed via `$C0B0` (register-select) / `$C0B1` (data), whose **2 KiB on-card VRAM** is banked as 4×512-byte pages into the **`$CC00–$CDFF`** window of the `$C800` expansion space (firmware ROM occupies `$C800–$CBFF`), and which walks that VRAM through a **2 KiB char ROM** into an **80×24 monochrome RGBA frame**. It is the **first real consumer of the shipped `DisplayMultiplexer`** (PR-M) — one of its display sources — and the **second consumer of `AddressSpace.Remap`/`RemapPeripheral`** (PR-A, after the Language Card PR-E) for the `$C800` bank window. When the guest enables the `$C800` window (the CP/M terminal "turns on" the Videx), the card raises an **active-display signal** (`ActiveChanged`) that the surface (PR-O) wires to `DisplayMultiplexer.SetActive`.

**The un-fakeable gate:** the Videx VRAM (real character codes) + a **synthetic** char ROM render to an **80×24 RGBA frame** with structural ink; the CRTC init table (R1=`$50`=80 cols, R6=`$18`=24 rows) yields the 80×24 geometry; and a `DisplayMultiplexer` over `[apple40, videx80]` switches to the Videx source when the Videx raises `ActiveChanged(true)`. **No real char ROM asset** — the gate runs on a synthetic glyph set (the Apple2Font fallback pattern), so it is asset-free / always-runs (it is NOT skip-with-note; the asset-gated end-to-end CP/M boot is PR-O).

**Architecture:** One new peripheral + one new board-spec variant + the render gate, all riding shipped seams:

1. **`VidexVideoterm` (`CpuEmulator.Peripherals`):** an `IPeripheral` mapping its slot-3 register page (`$C0B0`/`$C0B1` 6845 CRTC + the `$C0nX` VRAM-bank select) **and** participating in the `$C800` expansion band; it is also an `IDisplayDevice` producing 80×24 RGBA from its own 2 KiB VRAM through the char ROM. It owns the 6845 register file (R0–R17) and the 4×512-byte VRAM. The `$C800` mapper uses the **shipped** `IAddressSpace.Remap` (the firmware ROM window `$C800–$CBFF` → ROM) + `Remap` (the VRAM window `$CC00–$CDFF` → the active 512-byte bank, plain writable RAM) — the second `Remap` consumer.
2. **`Apple2Board.SpecWithVidex` (`CpuEmulator.Machines`):** a board-spec variant that carves the `$C700–$CFFF` Mmio region so the Videx's `$C0B0` register page and its `$C800–$CDFF` expansion window are validator-clean (a `$C0B0` slot inside an Mmio region; an `$C800–$CFFF` Mmio hole the Videx Remaps into). The Videx is a board peripheral so the factory `Realize`s it (capturing the program bus + scheduling the frame tick).
3. **The render gate (`CpuEmulator.Tests`):** the un-fakeable gate — program the CRTC for 80×24, write known character codes into the Videx VRAM, render an 80×24 RGBA frame, assert the geometry + structural ink against the synthetic char ROM; plus the multiplexer-switch gate (a `DisplayMultiplexer` over `[apple40, videx80]` follows `ActiveChanged`).

**Tech Stack:** C# / .NET 10, `CpuEmulator.Core` (`IDisplayDevice`, `DisplayMultiplexer` [PR-M], `IAddressSpace.Remap`/`RemapPeripheral` [PR-A], `IPeripheral`, `IMachineContext`, `IScheduler`), `CpuEmulator.Peripherals` (the new `VidexVideoterm`, `Apple2Palette`, `Apple2Font` pattern), `CpuEmulator.Machines` (`Apple2Board`, `BoardSpec`, `BoardMachineFactory`, `BoardSpecValidator`), xUnit (`tests/CpuEmulator.Tests`). **Depends on A, M ✅** (the `Remap` seam, the `DisplayMultiplexer`).

## Global Constraints

- **`IFastMemoryProvider` is NOT in `src/`.** ⚠️ **Shipped-API drift from ADR 0016 Decision 3 + the queue row N title** (both name `VidexVideoterm : IPeripheral, IDisplayDevice, IFastMemoryProvider`). The interface is **designed in ADR 0009 Decision 1 but never shipped** (verified by `grep -r IFastMemoryProvider src/` → no hits) — exactly like `TimingTier`/`ITimingSensitive` (ADR-only; the PR-M and PR-K plans correctly avoid it). **Resolution:** the ADR-0009 fast-RAM *intent* (the guest writes the Videx VRAM hot; the card reads/snapshots it at its frame tick, NOT per-write) is achieved through the **shipped `Remap` seam**: the active 512-byte VRAM bank is mapped into `$CC00–$CDFF` as **plain writable RAM** (a `byte[]` the card owns), so the guest's character writes hit the fastmem fast path (memory-backed pages, `MapMemory`/`Remap` → direct array access in the JIT) with no MMIO tax, and the card reads that same `byte[]` at its frame tick. So **`VidexVideoterm : IPeripheral, IDisplayDevice`** (drop the unshipped third interface). This is the honest, shippable realization of Decision 3; the `IFastMemoryProvider` formalization is a future framework PR, not a blocker. **Flag in the queue + the PR body.**
- **Interpreter-first.** The render gate runs without the JIT (the interpreter is the oracle). The VRAM-as-RAM-behind-Remap means the JIT path is exercised by PR-A's already-shipped `OnRemap` evict (the Language Card PR-E proved the JIT remap-evict end-to-end); N adds no new JIT seam.
- **The Videx VRAM window is RAM behind `Remap`, the firmware ROM window is ROM behind `Remap`.** The `$C800–$CBFF` firmware window is `Remap`-to-ROM (read-only `byte[]`); the `$CC00–$CDFF` VRAM window is `Remap`-to-the-active-bank-RAM (writable `byte[]`). Both ride the shipped `AddressSpace.Remap` (PR-A) — N is its **second consumer** (the LC is the first).
- **The CRTC registers + the `$C0nX` bank select are MMIO** (side-effecting control registers stay on the peripheral's `Read`/`Write` — the ADR 0009 Decision 1 split: control = MMIO, bulk VRAM = fast RAM). The Videx maps its `$C0B0`-page slot (a `$C0B0`-aligned 256-byte page) as a peripheral.
- **No real char ROM asset.** The render gate uses a synthetic char ROM built the same way as `Apple2Font.Fallback` (a small built-in glyph set sufficient to assert structural ink). The real Videx char ROM is the PR-O asset (`get-videx-roms`), injected the same way `Apple2Video` injects the real Apple char ROM. N ships a `VidexFont.Fallback` (the synthetic set) and accepts an optional real char ROM.
- **The active-display signal is guest-driven** (ADR 0016 Decision 2): the Videx raises `ActiveChanged(bool)` from its `$C800`-window-enable state. N ships the **signal + the gate that a `DisplayMultiplexer` follows it**; PR-O wires the real surface multiplexer. (The Videx is the *writer*; the multiplexer is the *reader* — ADR 0016 Decision 2's writer/reader split, the same shape as the IOU↔video state.)
- **No `TimingTier` / `ITimingSensitive`** (ADR-only, not in `src/`). The Videx schedules a frame tick the same way `Apple2Video`/`Apple2Speaker` do (`context.Scheduler.ScheduleEvery`).
- **HEAD grounding:** all literal code is grounded against `main` @ `59c1c05` (PRs #99–#114 merged). Verify with `git rev-parse HEAD` before starting.

---

## Recon facts this plan is built on (verified against `main` @ `59c1c05`)

1. **`IDisplayDevice`** (`src/CpuEmulator.Core/IDisplayDevice.cs`) is `{ int Width; int Height; void RenderInto(Span<uint> rgba); event Action? FrameReady; }`. `Width`/`Height` are documented "may change with video mode" — the Videx returns 80×24-derived dimensions (the multiplexer + the PR-M host re-size make that real across sources).
2. **`DisplayMultiplexer`** (`src/CpuEmulator.Core/DisplayMultiplexer.cs`, PR-M) is `sealed class DisplayMultiplexer : IDisplayDevice` with ctor `(IReadOnlyList<IDisplayDevice> sources, int initialActive = 0)`, `void SetActive(int index)` (fires `FrameReady` on an actual change), `int ActiveIndex`, and the delegating `Width`/`Height`/`RenderInto`/`FrameReady`. The Videx is one source; the surface (PR-O) calls `SetActive` from the Videx's `ActiveChanged`.
3. **`IAddressSpace.Remap(uint start, byte[] backing, bool writable)`** + **`RemapPeripheral(uint start, uint length, IPeripheral peripheral)`** are shipped on the interface (`src/CpuEmulator.Core/IAddressSpace.cs:33,39`) and concretely on `AddressSpace` (`src/CpuEmulator.Core/AddressSpace.cs:100,119`). `Remap` re-points page-aligned pages to a RAM/ROM `byte[]` (clears the MMIO handler); `RemapPeripheral` re-points to an MMIO device. The interface doc on `RemapPeripheral` already says *"Used by the Videx $C800 expansion-bank window (ADR 0016 Decision 3)."* **N is that consumer.** The Language Card (`src/CpuEmulator.Peripherals/Apple2LanguageCard.cs:109-115`) is the precedent: it captures `_bus = context.Space(AddressSpaceKind.Program)` in `Realize`, then `_bus.Remap(addr, backing, writable)` on each switch.
4. **`Remap` requires the range be ALREADY mapped + page-aligned** (`ValidateRange`; `AddressSpace.cs:100-115`) — the `$C800–$CDFF` window must be a real region (an Mmio hole) at build time so the page table has entries to re-point. The Videx's `Realize` performs the initial `Remap` of `$C800–$CBFF` (firmware ROM) + `$CC00–$CDFF` (VRAM bank 0), and re-`Remap`s `$CC00–$CDFF` when the bank changes. (PageSize = 256, so `$CC00`/`$CD00` are page boundaries — the 512-byte VRAM bank is exactly 2 pages; ✅ page-aligned.)
5. **`Apple2Board.SpecWithSystem`** (`src/CpuEmulator.Machines/Apple2Board.cs:92`) carves `$C000–$CFFF` into `$C000–$C5FF` Mmio / `$C600–$C6FF` Rom / `$C700–$CFFF` Mmio + the `$D000-$FFFF` ROM + the `"iou"` slot at `$C000`. The `$C700–$CFFF` Mmio region already contains `$C800–$CDFF` — but `Remap` needs those pages mapped, and an Mmio region maps **no backing** (`BoardMachineFactory.cs:39-42`: Mmio is "a hole that peripheral slots fill; no backing"). So the Videx board variant must (a) keep `$C800–$CDFF` mappable. **The clean grounded approach:** the Videx `Realize` maps the `$C800` window itself via `Remap` after the board builds — but `Remap` requires the range be already mapped. **Resolution (verified against the validator + factory):** the board variant maps the Videx as a peripheral slot covering `$C0B0`-page AND adds the `$C800–$CDFF` window as **RAM regions in the board spec** (so the factory `MapMemory`s them → the page table has entries) which the Videx then `Remap`s in `Realize` to its own backing. The firmware-window region is declared `Rom` (carries the firmware image) and the VRAM-window region is declared `Ram`; the Videx re-points both to its own arrays in `Realize`. See Task 3 for the exact carve. (A simpler alternative — map the whole `$C800` window as a Videx peripheral and serve it from `Read`/`Write` — would put the hot VRAM on the MMIO tax, violating the ADR 0009 fast-RAM intent; the RAM-region + `Remap` path keeps VRAM on the fast path.)
6. **`BoardSpecValidator`** (`src/CpuEmulator.Machines/BoardSpecValidator.cs`) rules that matter: `slot-misaligned` (slot Base + Length must be 256-multiples), `slot-not-in-mmio` (a peripheral slot must be **fully contained in an Mmio region**, line 49-56), `region-overlap` (Program-space regions must not overlap, line 185-188), `region-misaligned`, `rom-image-mismatch` (a Rom region's `Image.Length` must equal its `Length`). So: the Videx `$C0B0` register slot must sit inside an Mmio region; the `$C800–$CDFF` windows are **Ram/Rom regions** (not slots), so they are NOT subject to `slot-not-in-mmio` — they just must not overlap and must be page-aligned + correctly sized.
7. **`BoardMachineFactory.Build`** (`src/CpuEmulator.Machines/BoardMachineFactory.cs:29-52`) maps each `MemoryRegion` (Ram → `WithRam`, Rom → `WithRom(image)`, Mmio → hole) then each `PeripheralSlot` (`WithPeripheral`), then `builder.Build()` — and **`Machine` Realizes every peripheral after all mappings exist** (`src/CpuEmulator.Core/Machine.cs:85-86`: `foreach (... peripheral) peripheral.Realize(this)`). So the Videx (a board peripheral) gets `Realize(machine)` with the live program bus + scheduler.
8. **`IMachineContext`** (`src/CpuEmulator.Core/IMachineContext.cs`) = `{ IScheduler Scheduler; IAddressSpace Space(AddressSpaceKind kind); IInterruptLine IrqLine; IInterruptLine NmiLine; }`. **`IScheduler.ScheduleEvery(long interval, Action callback)`** returns a `ScheduledEvent` (a cancellation handle). `Apple2Video.Realize` (`src/CpuEmulator.Peripherals/Apple2Video.cs:44-52`): `_ram = context.Space(AddressSpaceKind.Program); context.Scheduler.ScheduleEvery(CyclesPerFrame, () => FrameReady?.Invoke());`. The Videx mirrors this (bind the bus for `Remap`, schedule the frame tick).
9. **`IPeripheral`** (`src/CpuEmulator.Core/IPeripheral.cs`) = `{ string Name; void Realize(IMachineContext); uint Read(uint offset, AccessWidth width); void Write(uint offset, AccessWidth width, uint value); bool TryPeek(uint offset, out byte value) [default: false]; }`. The offset is relative to the mapping base. **`AccessWidth`** (`src/CpuEmulator.Core/AccessWidth.cs`) = `{ Byte = 1, Word = 2, Long = 4 }`.
10. **`Apple2Palette`** (`src/CpuEmulator.Peripherals/Apple2Palette.cs`): `public const uint MonoOff = 0xFF000000u;` (black) and `public const uint MonoOn = 0xFFFFFFFFu;` (white) — ARGB8888. The Videx is a monochrome terminal; reuse these.
11. **`Apple2Font`** (`src/CpuEmulator.Peripherals/Apple2Font.cs`): `public static readonly byte[] Fallback = Build();` — 256 glyphs × 8 rows = 2048 bytes, glyph N at bytes `[N*8 .. N*8+7]`, each byte one row, **bit 6 = leftmost pixel** (bits 6..0 = the 7 horizontal pixels). N's `VidexFont.Fallback` mirrors this shape but at the Videx cell geometry (9 rows/char from CRTC R9=`$08`; the gate uses 8 active glyph rows + 1 blank, see Task 1).
12. **`Apple2VideoState`** (`src/CpuEmulator.Peripherals/Apple2VideoState.cs`) is the IOU↔video shared-state precedent (`GraphicsOn`/`HiRes`/`Page2` + `ToggleSpeaker`/`LatchKey`). The Videx does NOT use it — the Videx owns its own VRAM + CRTC state (an on-card display, unlike the Apple video which reads main RAM). The `ActiveChanged` event is the cross-card signal (the writer/reader split applied to the multiplexer, ADR 0016 Decision 2).
13. **The 6845 CRTC init table (research §8 / ADR 0016):** R0=`$7A`, **R1=`$50`** (80 chars/row), **R6=`$18`** (24 displayed rows), **R9=`$08`** (9 scan lines/char minus 1 → 9 lines). Register-select write to `$C0B0` selects R0–R17; data write to `$C0B1` sets the selected register. The Videx geometry: `Width = R1 * cellWidth`, `Height = R6 * (R9+1)`. With the Videx's standard 7-px-wide cell × 9-line cell → 80×7 = 560 wide, 24×9 = 216 tall → **560×216** RGBA (ADR 0016 OQ2 left the exact cell open; 7×9 is the documented Videx cell — see Task 1 design note).
14. **`AddressSpace` PageSize** is 256 (`PageShift`; the validator's `PageSize` constant). `$C0B0` is NOT 256-aligned, so the Videx **register slot is the whole `$C0B0`-containing page** — i.e. base `$C000`? No: the IOU already owns `$C000`. **Resolution:** the Videx register window is the slot-3 page `$C300`? No — the 6845 is at `$C0B0` (slot scratch-area, the `$C0n0` I/O strobe region, n=3 → `$C0B0`). `$C0B0` falls in the `$C000–$C5FF` Mmio region (`$C0B0` is in page `$C000`, which the IOU owns). **So the Videx CRTC at `$C0B0`/`$C0B1` is delegated by the IOU** (the same `$C08x`/`$C0Ex` delegate pattern), OR the Videx maps a sub-page the IOU forwards. See Task 2's design note for the grounded decision (the IOU delegates `$C0Bx` to the optional Videx, mirroring its `$C08x`→LC and `$C0Ex`→Disk II delegates).

---

## Conventions to follow

- **Mirror the shipped `IDisplayDevice` + `IPeripheral` contracts exactly** — the Videx IS both (the multiplexer treats it as one display source; the board treats it as one peripheral).
- **Reuse the shipped `Remap` seam** (PR-A) for the `$C800` window — do NOT invent expansion-bank handling; the Language Card (`Apple2LanguageCard.cs`) is the line-for-line precedent (`Realize` captures the bus; switches call `_bus.Remap`).
- **Reuse the IOU delegate pattern** for the `$C0Bx` CRTC registers — mirror `Apple2Iou`'s `$C08x`→LC / `$C0Ex`→Disk II delegate (a read's side effect rides `BusValue`, a write's rides `ApplyAnyAccessSideEffect`, `TryPeek` short-circuits — peek-free).
- **Reuse `Apple2Palette.MonoOn`/`MonoOff`** for the monochrome glyph render; mirror `Apple2Font.Fallback`'s synthetic-glyph build for `VidexFont.Fallback`.
- **TDD per task**, literal code, commit per task. Warning-clean. **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter ...`.

---

## File Structure

### `CpuEmulator.Peripherals`
- **Create** `src/CpuEmulator.Peripherals/VidexFont.cs` — the synthetic Videx char ROM (256 glyphs × 8 rows fallback, the `Apple2Font.Fallback` shape), used by the render gate (no real asset).
- **Create** `src/CpuEmulator.Peripherals/VidexVideoterm.cs` — `IPeripheral` + `IDisplayDevice`: the 6845 CRTC (`$C0B0`/`$C0B1`), the 4×512-byte VRAM, the `$C800` `Remap` mapper, the 80×24 RGBA render, the `ActiveChanged` signal.
- **Modify** `src/CpuEmulator.Peripherals/Apple2Iou.cs` — delegate `$C0B0`/`$C0B1` to an optional `VidexVideoterm` (the `$C08x`→LC / `$C0Ex`→Disk II pattern), peek-free.

### `CpuEmulator.Machines`
- **Modify** `src/CpuEmulator.Machines/Apple2Board.cs` — add `SpecWithVidex` carving the `$C800–$CDFF` expansion window as Rom (`$C800–$CBFF`) + Ram (`$CC00–$CDFF`) regions the Videx `Remap`s, with the IOU still owning `$C000`.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs` — the CRTC programming → 80×24 geometry, the VRAM+synthetic-charROM → RGBA render (structural ink), the `$C800` bank `Remap`, the `ActiveChanged` signal, and the `DisplayMultiplexer`-follows-the-signal switch gate.

---

## Task 1: `VidexFont` — the synthetic char ROM (no asset)

**Files:**
- Create: `src/CpuEmulator.Peripherals/VidexFont.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs` (the font shape assert)

**Interfaces:**
- Consumes: nothing.
- Produces: `VidexFont.Fallback` (a 256×8 = 2048-byte `byte[]`, the `Apple2Font.Fallback` shape) + `VidexFont.GlyphRows` (8) + `VidexFont.CellWidth` (7).

**Design notes (grounded against `Apple2Font.cs`):** The real Videx char ROM is 2 KiB (256 chars × 8 lines), the CRTC R9=`$08` gives a 9-line cell (8 active glyph rows + 1 blank descender line). For the render gate we need a **synthetic** glyph set with at least: a blank glyph (code `$20` space → all-zero) and at least one non-blank glyph (e.g. code `$41` `'A'` → a recognizable pattern) so the render produces structural ink that a dead render lacks. Mirror `Apple2Font.Fallback`: byte `[code*8 + row]`, **bit 6 = leftmost** of the 7-px cell. Build a simple deterministic set: every printable code `$20–$7E` gets a glyph whose ink count is a deterministic function of the code (so distinct codes paint distinct ink), with `$20` (space) explicitly all-zero (blank). This is sufficient for "VRAM of known codes → structurally non-blank, count-correct RGBA."

- [ ] **Step 1: Write the failing font test**

Create `tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class VidexVideotermTests
{
    [Fact]
    public void VidexFont_fallback_is_256x8_with_a_blank_space_and_inked_letters()
    {
        Assert.Equal(256 * 8, VidexFont.Fallback.Length);

        // The space glyph ($20) is blank — all 8 rows zero (no ink).
        for (int row = 0; row < 8; row++)
            Assert.Equal(0, VidexFont.Fallback[0x20 * 8 + row]);

        // A printable letter ('A' = $41) has ink in at least one row (a non-blank glyph).
        int aInk = 0;
        for (int row = 0; row < 8; row++)
            aInk += System.Numerics.BitOperations.PopCount((uint)VidexFont.Fallback[0x41 * 8 + row]);
        Assert.True(aInk > 0, "the 'A' glyph must carry ink");

        Assert.Equal(7, VidexFont.CellWidth);   // 7-px Videx cell
        Assert.Equal(8, VidexFont.GlyphRows);   // 8 active glyph rows (the char ROM is 256x8)
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~VidexVideotermTests.VidexFont"`
Expected: FAIL — `VidexFont` does not exist (compile error).

- [ ] **Step 3: Create `VidexFont`**

Create `src/CpuEmulator.Peripherals/VidexFont.cs`:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>A SYNTHETIC Videx Videoterm character ROM (256 glyphs x 8 rows = 2048 bytes), the
/// <see cref="Apple2Font.Fallback"/> shape, used when no real Videx char ROM asset is fetched (PR-N's
/// render gate is asset-free; the real 2 KiB char ROM is the PR-O asset, get-videx-roms, injected the
/// same way Apple2Video injects the real Apple char ROM). Each glyph is byte [code*8 + row]; bit 6 is the
/// LEFTMOST of the 7-px cell (the Apple2Font order). The space ($20) is blank; every other printable code
/// ($21-$7E) gets a deterministic non-blank pattern so distinct character codes paint distinct, countable
/// ink — enough for the "VRAM of known codes -> structurally correct 80x24 RGBA" gate. Non-printables are
/// blank.</summary>
public static class VidexFont
{
    /// <summary>The 7-pixel-wide Videx character cell (bit 6..0 of each glyph row).</summary>
    public const int CellWidth = 7;

    /// <summary>The 8 active glyph rows the 2 KiB char ROM stores (the CRTC's 9-line cell, R9=$08, adds
    /// one blank descender line at render time — see VidexVideoterm.RenderInto).</summary>
    public const int GlyphRows = 8;

    /// <summary>256 glyphs x 8 rows = 2048 bytes; built once at type load.</summary>
    public static readonly byte[] Fallback = Build();

    private static byte[] Build()
    {
        var rom = new byte[256 * GlyphRows];
        // Printable ASCII $20-$7E. $20 (space) stays all-zero (blank). Every other printable code gets a
        // deterministic glyph: a centered box outline whose middle rows encode the low bits of the code,
        // so distinct codes carry distinct, countable ink (the render gate counts ink, not exact shapes).
        for (int code = 0x21; code <= 0x7E; code++)
        {
            // Top + bottom rows: a full 7-px bar (bits 6..0 set). Middle rows: a pattern from the code.
            rom[code * GlyphRows + 0] = 0x7F;                 // bits 6..0
            rom[code * GlyphRows + GlyphRows - 1] = 0x7F;
            for (int row = 1; row < GlyphRows - 1; row++)
            {
                // A code-dependent middle pattern (kept within bits 6..0); guarantees ink + per-code variety.
                int pattern = ((code >> (row & 3)) ^ (code << 1)) & 0x7F;
                if (pattern == 0) pattern = 0x08;             // never leave a fully-blank middle row
                rom[code * GlyphRows + row] = (byte)pattern;
            }
        }
        return rom;   // $20 and all non-printables remain blank (all-zero) — the inverse of "inked".
    }
}
```

- [ ] **Step 4: Run the font test**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~VidexVideotermTests.VidexFont"`
Expected: PASS — the font is 256×8, the space is blank, 'A' is inked, the cell metrics are 7×8.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/VidexFont.cs tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs
git commit -m "feat(peripherals): VidexFont — synthetic Videx char ROM (render-gate font, no asset)"
```

---

## Task 2: `VidexVideoterm` — the 6845 CRTC + VRAM + 80×24 render + `$C800` Remap + ActiveChanged

**Files:**
- Create: `src/CpuEmulator.Peripherals/VidexVideoterm.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs` (the CRTC + render + bank asserts)

**Interfaces:**
- Consumes: `IPeripheral`, `IDisplayDevice`, `IMachineContext`, `IAddressSpace.Remap`, `IScheduler.ScheduleEvery`, `Apple2Palette`, `VidexFont`.
- Produces: `sealed class VidexVideoterm : IPeripheral, IDisplayDevice`. Ctor `VidexVideoterm(byte[]? charRom = null, byte[]? firmwareRom = null)`. The `IPeripheral` `Read`/`Write` decode the 6845 register-select (`$C0B0`, offset 0) / data (`$C0B1`, offset 1) + the `$C0nX` bank select; the `IDisplayDevice` `Width`/`Height`/`RenderInto`/`FrameReady` produce the 80×24 RGBA. `void Access(byte offset, bool isRead)` is the IOU delegate entry (mirroring the LC). `event Action<bool>? ActiveChanged` is the guest-driven active-display signal.

**Design notes (grounded against `Apple2Video.cs` + `Apple2LanguageCard.cs` + the 6845 model in research §8):**
- **The 6845 register file** is 18 registers (R0–R17); the Videx programs R1 (chars/row), R6 (displayed rows), R9 (lines/char-1), and the cursor/start regs (R12/R13/R14/R15) — the render uses R1, R6, R9, and the start-address regs R12/R13 (the VRAM scanout base). Power-on the registers are zero; the guest's CRTC init writes them (the gate writes R1=`$50`, R6=`$18`, R9=`$08`).
- **The register-select/data split:** a write to offset 0 (`$C0B0`) sets `_crtcAddr` (which register the next data write hits, masked to 0–17); a write to offset 1 (`$C0B1`) writes `_crtc[_crtcAddr]`. A read of offset 1 returns `_crtc[_crtcAddr]` for the readable regs (R14–R17 cursor/lightpen; the gate doesn't need reads). Offsets 0/1 are within the `$C0B0` page.
- **The `$C0nX` VRAM bank select** (research §8): "active bank = `((offset>>2)&3)*512` of the `$C0nX` access" — a `$C0n8`–`$C0nF`-style access selects the active 512-byte VRAM page. Model: a Videx-page access in the `$C0B8`–`$C0BF` sub-range (offsets `$B8`–`$BF`) sets `_bank = (offset >> 2) & 3` (0–3) and `Remap`s `$CC00–$CDFF` to that 512-byte VRAM slice. (The exact bank-select offset decode is a build-time detail; the gate programs the bank explicitly and asserts the window follows — see Task 2 Step-1 test.)
- **The `$C800` window enable** is the active-display signal (ADR 0016 Decision 2): a `$C0nX` access that turns the Videx on (the CP/M terminal driver) raises `ActiveChanged(true)`; a reset (`$CFFF` access / the Apple re-selecting its video) raises `ActiveChanged(false)`. Model: the **first** `$C0Bx` access that enables the window raises `ActiveChanged(true)` (idempotent — only on an actual transition, mirroring `DisplayMultiplexer.SetActive`'s no-op guard). The gate drives the enable and asserts the signal.
- **The VRAM** is 2 KiB = 4×512-byte banks (`_vram = new byte[2048]`). The active 512-byte slice is `$CC00–$CDFF`; the Videx `Remap`s a **window view** `byte[512]` that aliases `_vram[bank*512 .. bank*512+512]`. Because `Remap` takes a `byte[]` and the page table holds that array reference (with `BackingOffset` index-0-based, per `AddressSpace.Remap` setting `BackingOffset = i << PageShift` from the passed array), the Videx maps the **active 512-byte sub-array**. The cleanest grounded model: keep `_vram` as 2048 bytes and, on a bank switch, `Remap($CC00, sliceOf(_vram, bank), writable: true)` where `sliceOf` returns a fresh `byte[512]` copy — BUT a copy breaks the guest-writes-VRAM live link. **Resolution (grounded):** store the VRAM as **four separate 512-byte bank arrays** (`_vramBanks[0..3]`), and `Remap($CC00, _vramBanks[bank], writable: true)`; the render reads `_vramBanks[startBank]` for scanout. The guest writes the live mapped bank array; the Videx reads the same array — the live link holds, and `Remap` re-points `$CC00–$CDFF` to whichever bank is active (the second `Remap` consumer). (The on-card VRAM is logically contiguous 2 KiB; four 512-byte arrays is the implementation that fits the `Remap`-a-`byte[]` seam. The render walks the active scanout bank; CP/M's 80×24 screen fits in one 2000-char buffer spread across banks — for the gate, R12/R13 select the scanout base bank.)
- **The render** (`RenderInto`): `Width = R1 * VidexFont.CellWidth` (80×7 = 560), `Height = R6 * cellLines` (24×9 = 216, where `cellLines = (R9 & 0x1F) + 1` = 9). Walk the displayed grid: for each row r (0..R6-1), col c (0..R1-1), the character code is `vramScanout[startAddr + r*R1 + c]` (the CRTC start address R12/R13 + linear offset); render its glyph from the char ROM (8 active rows + 1 blank descender line = 9 cell lines) at `MonoOn`/`MonoOff`. **Default geometry** (before any CRTC programming): R1/R6/R9 are zero → guard with a minimum (return a 1×1 or the default 80×24 until programmed) so `Width`/`Height` are never zero (the multiplexer + host divide by them). Use `Math.Max(1, ...)` and treat an all-zero CRTC as the default 80×24 (so the surface has a valid size before CP/M programs it).

- [ ] **Step 1: Write the failing CRTC + render + bank tests**

Append to `VidexVideotermTests`:

```csharp
    // Program the standard Videx 80x24 init (research §8 / ADR 0016): R1=$50 (80 cols), R6=$18 (24 rows),
    // R9=$08 (9 lines/char). Writes go reg#->$C0B0 (offset 0), value->$C0B1 (offset 1).
    private static void Program80x24(VidexVideoterm videx)
    {
        void SetReg(byte reg, byte val)
        {
            videx.Write(0x00, AccessWidth.Byte, reg);   // register-select ($C0B0)
            videx.Write(0x01, AccessWidth.Byte, val);   // data ($C0B1)
        }
        SetReg(1, 0x50);   // R1 = 80 chars/row
        SetReg(6, 0x18);   // R6 = 24 displayed rows
        SetReg(9, 0x08);   // R9 = 9 scan lines/char minus 1 -> 9 lines
        SetReg(12, 0x00);  // R12 = start address high
        SetReg(13, 0x00);  // R13 = start address low
    }

    [Fact]
    public void Crtc_programming_yields_80x24_geometry()
    {
        var videx = new VidexVideoterm();
        Program80x24(videx);
        Assert.Equal(80 * VidexFont.CellWidth, videx.Width);   // 80 cols x 7-px cell = 560
        Assert.Equal(24 * 9, videx.Height);                    // 24 rows x 9-line cell = 216
    }

    [Fact]
    public void Vram_of_known_codes_renders_structural_ink_through_the_synthetic_char_rom()
    {
        var videx = new VidexVideoterm();           // null char ROM -> VidexFont.Fallback
        Program80x24(videx);

        // Write a row of 'A' ($41, inked) into the scanout bank's first 80 cells; the rest stay $00.
        // (Bank 0 is the scanout base when R12/R13 = 0.)
        for (int c = 0; c < 80; c++)
            videx.PokeVramForTest(0, c, (byte)'A');

        var rgba = new uint[videx.Width * videx.Height];
        videx.RenderInto(rgba);

        int on = 0, off = 0;
        foreach (uint p in rgba)
        {
            if (p == Apple2Palette.MonoOn) on++;
            else if (p == Apple2Palette.MonoOff) off++;
        }
        Assert.Equal(rgba.Length, on + off);        // monochrome — every pixel is on or off
        Assert.True(off > rgba.Length / 2, "a mostly-blank terminal screen");
        Assert.True(on > 80, "the row of 'A's must paint ink (a dead render is all-off)");
    }

    [Fact]
    public void An_unprogrammed_videx_reports_a_valid_default_size_never_zero()
    {
        var videx = new VidexVideoterm();           // no CRTC programming yet
        Assert.True(videx.Width > 0 && videx.Height > 0,
            "Width/Height must never be zero (the multiplexer/host divide by them)");
    }
```

- [ ] **Step 2: Write the bank-Remap + ActiveChanged tests**

Append to `VidexVideotermTests`:

```csharp
    [Fact]
    public void Selecting_a_vram_bank_remaps_the_CC00_window_to_that_bank_via_the_shipped_Remap()
    {
        // Build a real Apple+Videx machine so $CC00-$CDFF is a mappable window the Videx Remaps.
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000
        (Machine machine, VidexVideoterm videx) = BuildAppleWithVidex(rom);
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // Select bank 1, then write a byte to $CC00 — it must land in the Videx's bank-1 array.
        videx.SelectBankForTest(1);
        bus.Write8(0xCC00, 0x5A);
        Assert.Equal(0x5A, videx.PeekVramForTest(1, 0));   // the guest write reached the live bank-1 array

        // Select bank 2 and write again — bank 1's byte is untouched (the window re-pointed).
        videx.SelectBankForTest(2);
        bus.Write8(0xCC00, 0x3C);
        Assert.Equal(0x3C, videx.PeekVramForTest(2, 0));
        Assert.Equal(0x5A, videx.PeekVramForTest(1, 0));   // bank 1 still holds its earlier byte
    }

    [Fact]
    public void Enabling_the_videx_raises_ActiveChanged_true_exactly_once_on_the_transition()
    {
        var videx = new VidexVideoterm();
        var events = new List<bool>();
        videx.ActiveChanged += active => events.Add(active);

        videx.SetActiveForTest(true);    // the guest turns the Videx on (the $C800-window enable)
        videx.SetActiveForTest(true);    // idempotent — no second event on no transition
        videx.SetActiveForTest(false);   // the Apple re-selects its video

        Assert.Equal(new[] { true, false }, events);   // one transition each way, no duplicates
    }
```

- [ ] **Step 3: Add the test harness helper** (append to `VidexVideotermTests`; `BuildAppleWithVidex` builds a real Apple+Videx machine — it depends on Task 3's `SpecWithVidex`, so this compiles only after Task 3; the bank/ActiveChanged tests run after Task 3):

```csharp
    private static (Machine, VidexVideoterm) BuildAppleWithVidex(byte[] systemRom)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);   // the Videx delegate (Task 2 IOU change)
        BoardSpec spec = Apple2Board.SpecWithVidex(systemRom, iou, disk, videx);  // Task 3
        Machine machine = BoardMachineFactory.Build(spec);
        return (machine, videx);
    }
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~VidexVideotermTests"`
Expected: FAIL — `VidexVideoterm` (and `SpecWithVidex`, the IOU 4-arg ctor) do not exist (compile errors).

- [ ] **Step 5: Create `VidexVideoterm`**

Create `src/CpuEmulator.Peripherals/VidexVideoterm.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Videx Videoterm 80-column card (ADR 0016 Decision 3, research §8): a slot-3 card whose
/// 6845 CRTC is programmed via $C0B0 (register-select) / $C0B1 (data), whose 2 KiB on-card VRAM is banked
/// as 4 x 512-byte pages into the $CC00-$CDFF window of the $C800 expansion space (firmware ROM at
/// $C800-$CBFF), and which walks that VRAM through a 2 KiB char ROM into an 80x24 monochrome RGBA frame.
/// It is BOTH an IPeripheral (the CRTC + bank registers, delegated from the IOU at $C0Bx) AND an
/// IDisplayDevice (one source of the host DisplayMultiplexer, PR-M). The $C800 window is mapped with the
/// SHIPPED IAddressSpace.Remap (PR-A) — the SECOND Remap consumer after the Language Card: the firmware
/// window $C800-$CBFF is Remapped to ROM, the VRAM window $CC00-$CDFF to the active 512-byte bank (plain
/// writable RAM, so the guest's hot character writes ride the fastmem fast path — the ADR 0009 Decision 1
/// fast-RAM intent realized through Remap, since IFastMemoryProvider is ADR-designed but not shipped). The
/// guest-driven active-display signal (ADR 0016 Decision 2) is ActiveChanged(bool): the Videx is the
/// WRITER (its $C800-enable state), the host DisplayMultiplexer the READER (PR-O wires ActiveChanged ->
/// SetActive). Timing: the present tick is scheduled in Realize (the Apple2Video precedent); no IRQ.</summary>
public sealed class VidexVideoterm : IPeripheral, IDisplayDevice
{
    // --- $C800 expansion-window geometry (research §8) ---
    public const uint FirmwareWindowBase = 0xC800;   // $C800-$CBFF firmware ROM (1 KiB)
    public const uint FirmwareWindowLength = 0x0400;
    public const uint VramWindowBase = 0xCC00;       // $CC00-$CDFF banked VRAM (512 B)
    public const uint VramWindowLength = 0x0200;     // 512 bytes = one bank
    public const int BankSize = 512;
    public const int BankCount = 4;                  // 4 x 512 B = 2 KiB on-card VRAM

    private const long CyclesPerFrame = 17030;       // ~60 Hz present cadence (the Apple2Video value)

    // --- 6845 CRTC register file (R0-R17) ---
    private readonly byte[] _crtc = new byte[18];
    private int _crtcAddr;                            // the register the next $C0B1 access targets

    // --- 2 KiB VRAM as 4 x 512 B bank arrays (the Remap-a-byte[] model; the guest writes the live bank) ---
    private readonly byte[][] _vramBanks;
    private int _bank;                               // the active $CC00-$CDFF bank (0-3)

    private readonly byte[] _charRom;                // 256 x 8; the synthetic VidexFont unless a real ROM is injected
    private readonly byte[] _firmwareRom;            // 1 KiB $C800-$CBFF firmware (synthetic unless injected)

    private IAddressSpace _bus = default!;           // the live program bus, captured in Realize (for Remap)
    private bool _active;                            // the $C800-window enable (the active-display state)

    public string Name => "videx";
    public event Action? FrameReady;
    /// <summary>The guest-driven active-display signal (ADR 0016 Decision 2): true when the Videx becomes
    /// the live terminal (its $C800 window enabled), false when the Apple video is re-selected. The host
    /// DisplayMultiplexer subscribes this and calls SetActive (PR-O).</summary>
    public event Action<bool>? ActiveChanged;

    /// <param name="charRom">Optional 256x8 char-gen ROM; null uses the synthetic VidexFont.Fallback (the
    /// PR-N render gate is asset-free; the real char ROM is the PR-O asset).</param>
    /// <param name="firmwareRom">Optional 1 KiB $C800 firmware ROM; null uses an all-zero synthetic image
    /// (the PR-N gate does not execute the firmware; the real firmware is the PR-O asset).</param>
    public VidexVideoterm(byte[]? charRom = null, byte[]? firmwareRom = null)
    {
        _charRom = charRom ?? VidexFont.Fallback;
        if (_charRom.Length != 256 * VidexFont.GlyphRows)
            throw new ArgumentException("Videx char ROM must be 256x8 = 2048 bytes.", nameof(charRom));
        _firmwareRom = firmwareRom ?? new byte[(int)FirmwareWindowLength];
        if (_firmwareRom.Length != (int)FirmwareWindowLength)
            throw new ArgumentException("Videx firmware ROM must be 1 KiB ($C800-$CBFF).", nameof(firmwareRom));

        _vramBanks = new byte[BankCount][];
        for (int i = 0; i < BankCount; i++)
            _vramBanks[i] = new byte[BankSize];
    }

    public void Realize(IMachineContext context)
    {
        _bus = context.Space(AddressSpaceKind.Program);   // the live bus we Remap (the LC/Apple2Video precedent)
        // Map the $C800 expansion window: firmware ROM ($C800-$CBFF, read-only) + VRAM bank 0 ($CC00-$CDFF,
        // writable). The board carved these as mappable regions (SpecWithVidex), so the page table has
        // entries to re-point. This is the second Remap consumer (the Language Card is the first).
        _bus.Remap(FirmwareWindowBase, _firmwareRom, writable: false);
        _bus.Remap(VramWindowBase, _vramBanks[_bank], writable: true);
        context.Scheduler.ScheduleEvery(CyclesPerFrame, () => FrameReady?.Invoke());
    }

    // --- IPeripheral: the $C0B0/$C0B1 register window (delegated by the IOU; offsets relative to $C0B0) ---
    public uint Read(uint offset, AccessWidth width) => ReadReg((byte)offset);
    public void Write(uint offset, AccessWidth width, uint value) => WriteReg((byte)offset, (byte)value);

    /// <summary>The IOU delegate entry for $C0B0-$C0BF (mirrors the LC's $C08x Access): a read's side
    /// effect rides the returned value; a write's rides the same path. offset is the low byte ($B0-$BF).</summary>
    public byte Access(byte offset, bool isRead)
    {
        byte o = (byte)(offset & 0x0F);   // $C0B0-$C0BF low nibble
        return isRead ? ReadReg(o) : WriteRegReturn0(o, lastWritten: 0x00);
    }

    private byte WriteRegReturn0(byte o, byte lastWritten) { WriteReg(o, lastWritten); return 0x00; }

    private byte ReadReg(byte o)
    {
        // offset 1 ($C0B1) reads the selected CRTC register (only R14-R17 are truly readable on a 6845;
        // returning the stored value is adequate for the cursor/status the firmware polls).
        return o == 1 ? _crtc[_crtcAddr & 0x1F % 18] : (byte)0x00;
    }

    private void WriteReg(byte o, byte value)
    {
        switch (o)
        {
            case 0x00:                        // $C0B0: register-select
                _crtcAddr = value & 0x1F;     // 6845 has 18 regs; mask to 0-31, index guarded on use
                break;
            case 0x01:                        // $C0B1: data
                if (_crtcAddr < _crtc.Length)
                    _crtc[_crtcAddr] = value;
                break;
            default:
                // $C0B8-$C0BF region: VRAM bank select (research §8: bank = ((offset>>2)&3)). A bank-select
                // access also enables the Videx (the active-display signal): the first enable raises
                // ActiveChanged(true).
                if (o is >= 0x08 and <= 0x0F)
                {
                    SelectBank((o >> 2) & 0x03);
                    SetActive(true);
                }
                break;
        }
    }

    private void SelectBank(int bank)
    {
        if ((uint)bank >= BankCount) return;
        if (bank == _bank) return;
        _bank = bank;
        // Re-point $CC00-$CDFF to the newly selected 512-byte bank (the second Remap consumer). The guest
        // then writes the live bank array; the render reads the same array.
        _bus?.Remap(VramWindowBase, _vramBanks[_bank], writable: true);
    }

    private void SetActive(bool active)
    {
        if (active == _active) return;        // only on an actual transition (the SetActive no-op-guard shape)
        _active = active;
        ActiveChanged?.Invoke(active);
    }

    // --- IDisplayDevice: 80x24 RGBA from the VRAM through the char ROM ---
    private int Cols => Math.Max(1, _crtc[1] == 0 ? 80 : _crtc[1]);          // R1 (chars/row), default 80
    private int Rows => Math.Max(1, _crtc[6] == 0 ? 24 : _crtc[6]);          // R6 (displayed rows), default 24
    private int CellLines => Math.Max(1, ((_crtc[9] & 0x1F) == 0 ? 8 : (_crtc[9] & 0x1F)) + 1); // R9+1, default 9

    public int Width => Cols * VidexFont.CellWidth;
    public int Height => Rows * CellLines;

    public void RenderInto(Span<uint> rgba)
    {
        int width = Width, height = Height;
        if (rgba.Length < width * height)
            throw new ArgumentException($"Destination needs {width * height} pixels; got {rgba.Length}.",
                nameof(rgba));

        int cols = Cols, rows = Rows, cellLines = CellLines;
        // The scanout base address (R12 high / R13 low) selects which VRAM the screen starts at; for the
        // gate (R12/R13 = 0) the scanout is the active bank from offset 0. We read characters linearly from
        // the active bank (the 80x24 = 1920-char screen the CP/M terminal drives; a full multi-bank scanout
        // is a build-time refinement — the active bank holds the gate's row of characters).
        byte[] scanout = _vramBanks[_bank];
        int startAddr = (_crtc[12] << 8) | _crtc[13];

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int cell = startAddr + r * cols + c;
                byte code = cell < scanout.Length ? scanout[cell] : (byte)0x00;
                int glyphBase = (code & 0xFF) * VidexFont.GlyphRows;
                for (int gy = 0; gy < cellLines; gy++)
                {
                    // 8 active glyph rows + (cellLines-8) blank descender lines.
                    byte rowBits = gy < VidexFont.GlyphRows ? _charRom[glyphBase + gy] : (byte)0x00;
                    for (int gx = 0; gx < VidexFont.CellWidth; gx++)
                    {
                        bool on = (rowBits & (0x40 >> gx)) != 0;   // bit 6 = leftmost (the Apple2Font order)
                        int px = c * VidexFont.CellWidth + gx;
                        int py = r * cellLines + gy;
                        rgba[py * width + px] = on ? Apple2Palette.MonoOn : Apple2Palette.MonoOff;
                    }
                }
            }
        }
    }

    // --- Test seams (mirror Apple2Video.RaiseFrameForTest; no production caller) ---
    internal void PokeVramForTest(int bank, int offset, byte value) => _vramBanks[bank][offset] = value;
    internal byte PeekVramForTest(int bank, int offset) => _vramBanks[bank][offset];
    internal void SelectBankForTest(int bank) => SelectBank(bank);
    internal void SetActiveForTest(bool active) => SetActive(active);
}
```

> **Implementer note — the `internal` test seams.** `PokeVramForTest`/`PeekVramForTest`/`SelectBankForTest`/`SetActiveForTest` are `internal` and reached from the test project via the existing `InternalsVisibleTo` (the same mechanism `Apple2Video.RaiseFrameForTest` uses — confirm `CpuEmulator.Peripherals` has `[assembly: InternalsVisibleTo("CpuEmulator.Tests")]`; if not, make the seams `public` like `Apple2LanguageCard.AccessCount`). The production VRAM path is the guest writing `$CC00-$CDFF` (the `Remap`ped bank); the poke seam is only the gate's deterministic shortcut to avoid driving the full CRTC+RWTS.

- [ ] **Step 6: Delegate `$C0Bx` from the IOU to the optional Videx**

In `src/CpuEmulator.Peripherals/Apple2Iou.cs`, add the optional Videx delegate (mirror the LC `$C08x` / Disk II `$C0Ex` pattern):

Add the field + the 4-arg ctor:

```csharp
    private readonly VidexVideoterm? _videx;   // PR-N: $C0B0-$C0BF delegate (null on the bare board)
```

Add a 4-arg ctor (and forward the existing 3-arg ones with `videx: null`):

```csharp
    public Apple2Iou(Apple2VideoState state, Apple2LanguageCard? lc, Apple2DiskII? disk2)
        : this(state, lc, disk2, null) { }

    public Apple2Iou(Apple2VideoState state, Apple2LanguageCard? lc, Apple2DiskII? disk2,
                     VidexVideoterm? videx)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _lc = lc;
        _disk2 = disk2;
        _videx = videx;
    }
```

> **Implementer note — keep the existing 3-arg ctor body but route it through the 4-arg one.** The shipped 3-arg `Apple2Iou(state, lc, disk2)` (`Apple2Iou.cs:30-36`) currently has the field-assigning body. Replace its body with `: this(state, lc, disk2, null)` (a ctor chain) and put the field assignment in the new 4-arg ctor (above). The other two convenience ctors (`(state)`, `(state, lc)`, `(state, disk2)`) already chain to the 3-arg one — they are unaffected.

In `Realize`, forward the Videx (it is ALSO a board peripheral with its own slot, so it is Realized by the factory too — but the Videx's `Realize` is idempotent-safe; to avoid a double-Realize, **do NOT forward the Videx from the IOU** — it is a real board peripheral, unlike the LC/Disk II which own no slot). Leave `Realize` unchanged:

```csharp
    public void Realize(IMachineContext context)
    {
        _lc?.Realize(context);      // the LC owns no page, so the IOU Realizes it
        _disk2?.Realize(context);   // same — the Disk II captures the scheduler
        // The Videx is NOT realized here: it owns its own board slot ($C0B0 page) + the $C800 window, so
        // the factory Realizes it directly (Machine.cs:85-86). Forwarding it here would double-Realize.
    }
```

In `ApplyAnyAccessSideEffect`, add the `$C0Bx` write delegate (mirror `$C08x`/`$C0Ex`):

```csharp
            // --- Videx CRTC $C0B0-$C0BF (delegated; WRITES only here — a read's Access is owned by BusValue
            // so the Videx's Access fires exactly once per bus access). ---
            case >= 0xB0 and <= 0xBF:
                if (!isRead) _videx?.Access(o, isRead: false);
                break;
```

In `BusValue`, add the `$C0Bx` read delegate (before the final `switch`):

```csharp
        if (o is >= 0xB0 and <= 0xBF)
            return _videx?.Access(o, isRead: true) ?? 0x00;
```

In `TryPeek`, short-circuit `$C0Bx` (peek-free — a debugger peek must not program the CRTC or switch banks):

```csharp
        // PEEK-FREE for $C0Bx: a Videx CRTC/bank access has side effects (register-select, bank Remap,
        // ActiveChanged). Short-circuit a peek to open-bus 0 BEFORE BusValue, like $C08x/$C0Ex.
        if (o is >= 0xB0 and <= 0xBF)
        {
            value = 0x00;
            return true;
        }
```

> **Implementer note — the IOU `$C0Bx` decode collides with nothing shipped.** `$C0B0-$C0BF` is currently in the `default: break;` arm of `ApplyAnyAccessSideEffect` and the open-bus `_ => 0x00` of `BusValue` (no shipped soft switch lives there). Adding the Videx delegate is purely additive — the bare board (`_videx == null`) is byte-for-byte unchanged (`?.` is a no-op). Confirm no existing `$C0Bx` case exists before adding (it does not — the shipped cases are `$C050-$C057`, `$C010`, `$C030`, `$C000`, `$C08x`, `$C0Ex`).

- [ ] **Step 7: Run the CRTC + render tests** (the bank/ActiveChanged tests need Task 3's `SpecWithVidex` to compile)

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~VidexVideotermTests.Crtc_programming OR FullyQualifiedName~VidexVideotermTests.Vram_of_known OR FullyQualifiedName~VidexVideotermTests.An_unprogrammed OR FullyQualifiedName~VidexVideotermTests.Enabling_the_videx"`
Expected: PASS — the CRTC yields 80×24 (560×216), known codes render structural ink, an unprogrammed Videx reports a valid default, the enable raises `ActiveChanged` once each way. (The `Selecting_a_vram_bank...` test compiles + passes after Task 3.)

- [ ] **Step 8: Commit**

```bash
git add src/CpuEmulator.Peripherals/VidexVideoterm.cs src/CpuEmulator.Peripherals/Apple2Iou.cs tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs
git commit -m "feat(peripherals): VidexVideoterm — 6845 CRTC + VRAM + 80x24 render + \$C800 Remap + ActiveChanged"
```

---

## Task 3: `Apple2Board.SpecWithVidex` — the board variant carving the `$C800` window + the Videx slot

**Files:**
- Modify: `src/CpuEmulator.Machines/Apple2Board.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs` (the board-build + the bank-Remap gate now compile + run)

**Interfaces:**
- Consumes: `Apple2Board.SpecWithSystem`'s carve, `VidexVideoterm`, `BoardSpec`/`MemoryRegion`/`PeripheralSlot`, the validator rules.
- Produces: `Apple2Board.SpecWithVidex(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, VidexVideoterm videx)` → a `BoardSpec` with the `$C800-$CBFF` firmware window as a Rom region, the `$CC00-$CDFF` VRAM window as a Ram region (both `Remap`able by the Videx), the Videx CRTC at the `$C0B0`-containing page, and the IOU still owning `$C000`. (No `diskBootRom` — the PR-N gate is render-only; the CP/M-boot-on-Videx board is PR-O, which composes `SoftCardBoard` + the Videx.)

**Design notes (grounded against `SpecWithSystem`'s carve + the validator):**
- The Videx CRTC at `$C0B0`/`$C0B1` lives in **page `$C000`** (the IOU's page). So the Videx is NOT a separate `$C0B0` slot (it would overlap the IOU slot → `region-overlap`/double-map). Instead the **IOU delegates `$C0Bx`** (Task 2's IOU change), exactly as it delegates `$C08x`→LC and `$C0Ex`→Disk II. So `SpecWithVidex` adds **no** `$C0B0` peripheral slot — the Videx rides the IOU's `$C000` page like the LC/Disk II. **But the Videx must still be a peripheral the factory Realizes** (it owns the `$C800` window + the frame tick). **Resolution:** add the Videx as a peripheral slot over the **`$C800-$CBFF` firmware window** (a `$C800`-page-aligned, Mmio-contained slot) — so the factory maps + Realizes it, and the Videx's `Realize` immediately `Remap`s that window to its firmware ROM array (memory wins). The VRAM window `$CC00-$CDFF` is a separate Ram region the Videx `Remap`s to bank 0.
- **The carve** (extending `SpecWithSystem`'s three-region I/O band): `$C000-$C5FF` Mmio (IOU + the Videx CRTC delegate live here) / `$C600-$C6FF` Rom (disk boot, if present — but PR-N's render board has no disk-boot ROM, so this is just Mmio) / `$C700-$C7FF` Mmio / **`$C800-$CBFF` Mmio** (the Videx firmware-window slot sits here; the Videx Remaps it to ROM in Realize) / **`$CC00-$CDFF` Ram** (the VRAM window; the Videx Remaps it to bank 0 in Realize — declared Ram so the factory `MapMemory`s it, giving `Remap` a mapped range) / `$CE00-$CFFF` Mmio (the rest of the band). Every region is 256-aligned + 256-multiple (the validator's `region-misaligned` / `slot-misaligned` rules); the firmware slot is fully inside the `$C800-$CBFF` Mmio region (`slot-not-in-mmio` ✅); no region overlaps (`region-overlap` ✅).
- **Why the firmware window is the Videx's slot (not `$C0B0`):** the slot must be (a) Mmio-contained and (b) not overlap the IOU. The `$C800-$CBFF` window is a clean, IOU-free, Mmio region — the natural home for the Videx's board slot. The CRTC at `$C0B0` is reached via the IOU delegate (Task 2). After `Realize`, the Videx `Remap`s `$C800-$CBFF` from the (empty) slot-MMIO to its firmware `byte[]` (memory wins over the handler) — so the slot is just the factory's "Realize me + give me a mapped page" hook.

- [ ] **Step 1: Write the failing board-build test**

Append to `VidexVideotermTests`:

```csharp
    [Fact]
    public void SpecWithVidex_validates_and_builds_with_the_C800_window_mapped()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        (Machine machine, VidexVideoterm videx) = BuildAppleWithVidex(rom);
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // The $CC00 VRAM window is writable RAM (the Videx Remapped it to bank 0 in Realize): a guest write
        // round-trips through the live bank-0 array.
        bus.Write8(0xCC00, 0x77);
        Assert.Equal(0x77, videx.PeekVramForTest(0, 0));

        // The $C800 firmware window is read-only ROM (the Videx Remapped it read-only): a write is ignored.
        byte before = bus.Read8(0xC800);
        bus.Write8(0xC800, 0xAB);
        Assert.Equal(before, bus.Read8(0xC800));   // ROM — the write did not take
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~VidexVideotermTests.SpecWithVidex"`
Expected: FAIL — `Apple2Board.SpecWithVidex` does not exist.

- [ ] **Step 3: Add `SpecWithVidex`**

In `src/CpuEmulator.Machines/Apple2Board.cs`, add the constants + the method (after `SpecWithSystem`):

```csharp
    public const uint VidexFirmwareBase = 0xC800;
    public const uint VidexFirmwareLength = 0x0400;   // $C800-$CBFF (1 KiB, the Videx firmware window slot)
    public const uint VidexVramBase = 0xCC00;
    public const uint VidexVramLength = 0x0200;        // $CC00-$CDFF (512 B, the banked VRAM window)

    /// <summary>The ][+ board with the Videx Videoterm 80-column card wired (ADR 0016 Decision 3, PR-N).
    /// The Videx CRTC ($C0B0/$C0B1) is delegated by the IOU (like the LC's $C08x / Disk II's $C0Ex — the
    /// IOU must have been constructed with this same <paramref name="videx"/>), so no $C0B0 slot is added.
    /// The Videx owns the $C800 expansion window: $C800-$CBFF (firmware, Remapped to ROM in Realize) is the
    /// Videx's board peripheral SLOT (so the factory Realizes the card), and $CC00-$CDFF (banked VRAM,
    /// Remapped to bank 0) is a Ram region the Videx re-points. The $C000-$CFFF band is re-carved so each
    /// window is a validator-clean region. This is the render board (no disk-boot ROM); the CP/M-on-Videx
    /// board is PR-O (SoftCardBoard + the Videx).
    /// <para>CALLER CONTRACT: <paramref name="iou"/> MUST have been constructed with this same
    /// <paramref name="videx"/> (and the LC/Disk II) — <c>new Apple2Iou(state, lc, disk2, videx)</c>.</para></summary>
    public static BoardSpec SpecWithVidex(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2,
                                          VidexVideoterm videx)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        ArgumentNullException.ThrowIfNull(disk2);
        ArgumentNullException.ThrowIfNull(videx);
        if (systemRom.Length != RomLength)
            throw new ArgumentException(
                $"Apple ][+ system ROM must be exactly ${RomLength:X} bytes; got ${systemRom.Length:X}.",
                nameof(systemRom));

        return new BoardSpec("apple2plus-videx", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),                       // $0000-$BFFF RAM
                new MemoryRegion(IoBase, VidexFirmwareBase - IoBase, RegionKind.Mmio),      // $C000-$C7FF I/O
                new MemoryRegion(VidexFirmwareBase, VidexFirmwareLength, RegionKind.Mmio),  // $C800-$CBFF (Videx slot)
                new MemoryRegion(VidexVramBase, VidexVramLength, RegionKind.Ram),           // $CC00-$CDFF VRAM window
                new MemoryRegion(VidexVramBase + VidexVramLength,                           // $CE00-$CFFF I/O
                    IoBase + IoLength - (VidexVramBase + VidexVramLength), RegionKind.Mmio),
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, systemRom),            // $D000-$FFFF ROM
            ],
            Peripherals:
            [
                new PeripheralSlot("iou", iou, IouBase, IouLength),                         // the $C000 page decoder
                new PeripheralSlot("videx", videx, VidexFirmwareBase, VidexFirmwareLength), // the $C800 firmware slot
            ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None);
    }
```

> **Implementer note — the Videx slot is the firmware window; the Videx Remaps it in Realize.** The `"videx"` slot at `$C800-$CBFF` makes the factory map the Videx as the MMIO handler for that window AND Realize it (`Machine.cs:85-86`). In `Realize`, the Videx immediately `Remap`s `$C800-$CBFF` to its firmware `byte[]` (read-only) — `Remap` clears the handler (memory wins, `AddressSpace.cs:112`), so subsequent reads hit the ROM bytes, not the Videx's `Read`. The `$CC00-$CDFF` Ram region is mapped by the factory (`MapMemory`), giving the Videx a mapped range to `Remap` to bank 0. **Verify the carve leaves `$C600-$C6FF` as Mmio** (no disk-boot ROM on the render board — the whole `$C000-$C7FF` is one Mmio region here, simpler than `SpecWithSystem`'s three-way carve, because PR-N's gate does not disk-boot). If a later integration needs both the disk boot ROM and the Videx, PR-O's board composes them.

- [ ] **Step 4: Run the board + bank tests**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~VidexVideotermTests.SpecWithVidex OR FullyQualifiedName~VidexVideotermTests.Selecting_a_vram_bank"`
Expected: PASS — the board validates + builds; the `$CC00` window is writable RAM round-tripping to the live bank; the `$C800` window is ROM; selecting a bank re-points `$CC00` to that bank via the shipped `Remap`.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/Apple2Board.cs tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs
git commit -m "feat(machines): Apple2Board.SpecWithVidex — the \$C800 Videx window carve (2nd Remap consumer)"
```

---

## Task 4: The un-fakeable gate — the `DisplayMultiplexer` switches to the Videx on `ActiveChanged`

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs`

**Interfaces:**
- Consumes: `DisplayMultiplexer` (PR-M), `VidexVideoterm` (Task 2), `Apple2Video` (PR-C) as the 40-col source.

**Design notes — this is the row-N capstone gate:** *the Videx (synthetic char ROM) renders an 80×24 RGBA frame, AND a `DisplayMultiplexer` over `[apple40, videx80]` switches to the Videx when it raises `ActiveChanged(true)`.* This wires the two shipped seams (the Videx as an `IDisplayDevice` source + the multiplexer's `SetActive`) exactly as PR-O's surface will — but with deterministic synthetic sources, so it is asset-free + always runs (NOT skip-with-note). A dead Videx renders all-off (no 80×24 ink); a multiplexer that ignores `ActiveChanged` stays at the 40-col geometry — both unfakeable.

- [ ] **Step 1: Write the gate**

Append to `VidexVideotermTests`:

```csharp
    [Fact]
    public void DisplayMultiplexer_switches_to_the_Videx_80col_when_it_signals_active()
    {
        // A 40-col Apple video source (PR-C) + the 80-col Videx (this PR) behind the host multiplexer (PR-M).
        var apple = new Apple2Video(
            ApplePlaceholderBus(), new Apple2VideoState());     // 280x192 (the 40-col render)
        var videx = new VidexVideoterm();
        Program80x24(videx);

        var mux = new DisplayMultiplexer([apple, videx], initialActive: 0);

        // Initially the Apple 40-col source is active.
        Assert.Equal(Apple2Video.Width280, mux.Width);
        Assert.Equal(Apple2Video.Height192, mux.Height);

        // Wire the guest-driven active-display signal exactly as PR-O's surface will: ActiveChanged ->
        // SetActive (index 1 = the Videx; index 0 = the Apple video).
        videx.ActiveChanged += active => mux.SetActive(active ? 1 : 0);

        int frames = 0;
        mux.FrameReady += () => frames++;

        // The guest enables the Videx (its $C800 window): the multiplexer switches to the 80-col geometry.
        videx.SetActiveForTest(true);
        Assert.Equal(1, frames);                                // the switch fired FrameReady (host re-pulls)
        Assert.Equal(videx.Width, mux.Width);                   // now the Videx 80x24 geometry (560)
        Assert.Equal(videx.Height, mux.Height);                 // (216)
        Assert.Equal(80 * VidexFont.CellWidth, mux.Width);

        // And the multiplexer now renders the Videx frame (structural ink against the synthetic char ROM).
        var rgba = new uint[mux.Width * mux.Height];
        for (int c = 0; c < 80; c++) videx.PokeVramForTest(0, c, (byte)'A');
        mux.RenderInto(rgba);
        int on = 0;
        foreach (uint p in rgba) if (p == Apple2Palette.MonoOn) on++;
        Assert.True(on > 80, "the multiplexer renders the Videx's inked 80-col frame");

        // The guest hands back to the Apple video: the multiplexer switches back to 40-col.
        videx.SetActiveForTest(false);
        Assert.Equal(Apple2Video.Width280, mux.Width);
        Assert.Equal(2, frames);                                // the switch-back also fired FrameReady
    }

    private static IAddressSpace ApplePlaceholderBus()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }
```

- [ ] **Step 2: Run the gate**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~VidexVideotermTests.DisplayMultiplexer_switches"`
Expected: PASS — the multiplexer starts at the Apple 40-col geometry, switches to the Videx 80×24 (560×216) on `ActiveChanged(true)` (firing `FrameReady`), renders the Videx's inked frame, and switches back on `ActiveChanged(false)`.

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/VidexVideotermTests.cs
git commit -m "test(videx): the DisplayMultiplexer switches Apple-40 -> Videx-80 on the guest active signal"
```

---

## Task 5: Final gate — full suite + warning-clean build (the bare-board regression)

**Files:** none (verification only).

**Design note — the load-bearing regression:** the IOU's new `$C0Bx` delegate must leave the bare board (no Videx) byte-for-byte unchanged. The full suite is the real gate — every `Apple2*`/`SoftCard*`/`Spectrum*` test must stay green, because the `_videx?.Access(...)` is a no-op when `_videx == null` and `$C0Bx` was previously an open-bus `default`/`_ => 0x00` arm (no shipped soft switch lived there).

- [ ] **Step 1: Full build, warning-clean**

Run: `dotnet build CpuEmulator.slnx`
Expected: Build succeeded, **0 warnings**.

- [ ] **Step 2: Full test suite**

Run: `dotnet test CpuEmulator.slnx`
Expected: the post-PR-M baseline (7211) green plus the new PR-N tests (`VidexVideotermTests`), 0 failed. **No pre-existing test regresses** — the IOU's `$C0Bx` delegate is inert on the bare board (`_videx == null`), the `SpecWithVidex` variant is additive (`SpecWithSystem`/`SpecWithDiskII` unchanged), and `VidexVideoterm`/`VidexFont` are new types no shipped surface references (PR-O is the first surface caller).

- [ ] **Step 3: Confirm the un-fakeable gates ran**

Confirm these are in the passing set:
- `Crtc_programming_yields_80x24_geometry` — the CRTC init table → 80×24 (560×216).
- `Vram_of_known_codes_renders_structural_ink_through_the_synthetic_char_rom` — VRAM + synthetic char ROM → inked 80×24 RGBA.
- `Selecting_a_vram_bank_remaps_the_CC00_window...` — the `$C800` bank `Remap` (the 2nd `Remap` consumer).
- `SpecWithVidex_validates_and_builds_with_the_C800_window_mapped` — the board carve is validator-clean.
- `DisplayMultiplexer_switches_to_the_Videx_80col_when_it_signals_active` — the capstone: the multiplexer follows the guest active-display signal.

---

## Self-Review

**1. Spec coverage (ADR 0016 Decision 3 + the row-N gate):**
- `VidexVideoterm : IPeripheral, IDisplayDevice` (the 6845 CRTC at `$C0B0`/`$C0B1`, the 4×512-byte VRAM, the 80×24 render) → Task 2. ✓ (`IFastMemoryProvider` dropped — unshipped; the fast-RAM intent realized via `Remap`-to-RAM, see drift note.)
- The CRTC init table (R1=`$50`=80 cols, R6=`$18`=24 rows, R9=`$08`) → 80×24 → Task 2 (`Crtc_programming_yields_80x24_geometry`). ✓
- 2 KiB VRAM as 4×512-byte banks into `$CC00–$CDFF`; firmware ROM `$C800–$CBFF` → Task 2 + Task 3 (the `$C800` carve). ✓
- The `$C800` mapper as the **second `Remap` consumer** (PR-A) → Task 2 (`Realize` Remaps the firmware + VRAM windows; `SelectBank` re-points) + Task 3 (`Selecting_a_vram_bank...` gate). ✓
- First consumer of the shipped `DisplayMultiplexer` (PR-M) → Task 4 (the multiplexer over `[apple40, videx80]`). ✓
- Raises the active-display signal (`SetActive`) → Task 2 (`ActiveChanged`) + Task 4 (wired to `mux.SetActive`). ✓ (ADR's `SetActive` is the multiplexer's; the Videx's writer-side signal is `ActiveChanged` → the surface calls `SetActive` — ADR 0016 Decision 2's writer/reader split.)
- Synthetic char ROM → 80×24 RGBA, no real char ROM asset → Task 1 (`VidexFont`) + Task 2 (the render gate). ✓ (NOT skip-with-note; asset-free — the asset-gated path is PR-O.)
- Deps A (the `Remap` seam ✅), M (the `DisplayMultiplexer` ✅). ✓

**2. Placeholder scan:** No TBD/TODO/"implement later"/"similar to Task N". Every code step shows literal code (the font, the Videx peripheral+display, the IOU delegate, the board carve, all tests). The "build-time refinement" notes (the exact bank-select offset decode; a full multi-bank scanout) are explicitly scoped *beyond the gate* (the gate programs the bank + scanout base explicitly), not missing code — the literal `WriteReg`/`SelectBank`/`RenderInto` are complete and gated.

**3. Type consistency:** `VidexVideoterm(byte[]? charRom = null, byte[]? firmwareRom = null)`, `Read`/`Write(uint, AccessWidth, uint)`, `Access(byte, bool)`, `ActiveChanged` (`event Action<bool>?`), `Width`/`Height`/`RenderInto(Span<uint>)`/`FrameReady` (the `IDisplayDevice` contract), the `internal` `*ForTest` seams — used identically across tasks. `Apple2Iou` gains a 4-arg ctor `(state, lc, disk2, videx)` (the existing 3-arg chains to it). `Apple2Board.SpecWithVidex(byte[], Apple2Iou, Apple2DiskII, VidexVideoterm)` mirrors `SpecWithSystem`'s positional shape. `DisplayMultiplexer([IDisplayDevice...], int)` / `SetActive(int)` / `Width`/`Height` (PR-M, verified shipped). `Apple2Palette.MonoOn`/`MonoOff`, `IAddressSpace.Remap(uint, byte[], bool)`, `IScheduler.ScheduleEvery(long, Action)`, `IMachineContext.Space(AddressSpaceKind)`, `BoardMachineFactory.Build(BoardSpec)`, `MemoryRegion(uint, uint, RegionKind, byte[]?)`, `PeripheralSlot(string, IPeripheral, uint, uint)` are the shipped signatures verified during planning.

**Builder-readiness note:** the cross-file touches are `Apple2Iou.cs` (the additive `$C0Bx` delegate + the 4-arg ctor — the bare board is byte-for-byte unchanged) and `Apple2Board.cs` (the additive `SpecWithVidex` — the shipped overloads untouched). The render gate uses the synthetic `VidexFont` + a synthetic system ROM, so it needs **no asset** and always runs (the asset-gated CP/M-on-Videx end-to-end is PR-O). **One flagged shipped-API drift (carried to the queue + the PR body):** ADR 0016 Decision 3 + the queue row N name `IFastMemoryProvider`, which is **NOT in `src/`** (ADR-0009-designed, never shipped — like `TimingTier`); N realizes the fast-RAM intent through the shipped `Remap`-to-RAM seam and ships `VidexVideoterm : IPeripheral, IDisplayDevice`. **Two flagged build-time confirmations for the Builder:** (1) confirm `[assembly: InternalsVisibleTo("CpuEmulator.Tests")]` on `CpuEmulator.Peripherals` for the `*ForTest` seams (else make them `public`); (2) the exact `$C0nX` bank-select offset decode (the gate uses `SelectBankForTest`; the production `$C0B8-$C0BF` decode is the documented research §8 `((offset>>2)&3)` — refine against the Videx firmware 2.4 at PR-O if the real CP/M driver's bank cadence differs).
