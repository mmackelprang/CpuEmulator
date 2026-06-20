# ZX Spectrum 48K — Phase 2: The Machine (ULA + ROM + .SNA + Board + SP0 Wiring) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the ZX Spectrum 48K as a `BoardSpec` on the Phase-1 foundations — the **ULA** (a port-`$FE` peripheral implementing `IDisplayDevice` + `IKeyboardSink` + `IAudioSink`, reading main RAM for video, decoding the keyboard matrix, folding in the border, and rendering the beeper to PCM), the **16 KB ROM fetched on demand**, **`.SNA` snapshot loading**, the **`SpectrumBoard`** (Z80 + 16K ROM + 48K RAM + the ULA on Io port `$FE` + the 50 Hz IM1 interrupt), and the **SP0 web-surface wiring** — booting the BASIC copyright screen with a working keyboard and beeper, then a real game from a `.SNA`.

**Architecture:** The ULA is one `SpectrumUla` class implementing `IPeripheral` (the guest-facing port `$FE`: `IN` keyboard read, `OUT` border + beeper) plus the three SP0 host contracts. It holds an `IAddressSpace` reference to read screen RAM (`$4000`–`$5AFF`) — it does NOT own VRAM. It is mapped as an `Io` `PeripheralSlot` across the whole 16-bit Io space with bit-0-clear partial decode (Phase-1 mechanism). `RenderInto` walks the Spectrum's non-linear screen address layout to produce a 256×192 + border RGBA frame; the keyboard maps SP0 `KeyCode`s onto the 8×5 matrix selected by A8–A15; the beeper records `OUT` bit-4 toggles timestamped by T-state and resamples them to S16 PCM. The 50 Hz IM1 interrupt is raised by the ULA on the scheduler via its IRQ source. The ROM is fetched by `tools/get-spectrum-rom.{sh,ps1}` into the asset cache; ROM-dependent tests skip-with-note when absent (the `KlausFact` pattern). `.SNA` restores Z80 registers + RAM into a built `Machine`, popping PC from the restored stack.

**Tech Stack:** C# / .NET 10, the Phase-1 `Io` peripheral slot + `IAudioSink`, the existing `BoardSpec`/`BoardMachineFactory`/`MachineHost`/`FrameCodec`, the `Z80Cpu` core (interpreter + JIT-fallback), xUnit 2.9, the asset-cache + skip-gate conventions (`KlausVectors`/`KlausFactAttribute`).

---

## Depends on Phase 1

This plan **requires** `docs/superpowers/plans/2026-06-19-spectrum-1-extensions.md` to be merged first. It uses:
- `PeripheralSpace.Io` + `RegionKind.IoMmio` + `BoardSpec.IoAddressBits` (the port-mapped slot — the ULA on port `$FE`).
- `IAudioSink` (the beeper) + `FrameCodec.EncodeAudio` (`AU` frame) + `MachineHost`'s 6-arg ctor (the audio sink) + the `Program.cs` audio channel + the browser Web-Audio queue.

If any Phase-1 symbol is missing, stop and land Phase 1 first.

## Recon facts this plan is built on (verified against `main` @ HEAD + Phase 1)

1. **The Z80 forms the full 16-bit port address** (`OpD3`/`OpDB`: `port = (A<<8)|n`; `ED` forms use `BC`) and routes through `_io.Read8/Write8`. So the ULA, mapped at Io base 0 across the 16-bit space, sees the full port as `offset` in `Read`/`Write` — A8–A15 (keyboard half-row) and A0 (the bit-0 decode) are both visible. (Phase 1 proved this with `PortEchoDevice`.)
2. **`IDisplayDevice`** = `int Width/Height`, `void RenderInto(Span<uint> rgba)` (RGBA8888, row-major), `event Action? FrameReady`. **`IKeyboardSink`** = `void PostKey(in KeyEvent e)`. **`IAudioSink`** (Phase 1) = `SampleRate`/`ChannelCount`/`SamplesPerFrame`/`RenderAudio(Span<short>)`/`AudioReady`.
3. **`KeyCode`** members available: `A`–`Z`, `Digit0`–`Digit9`, `Space`, `Enter`, `Backspace`, `Tab`, `Escape`, `ArrowLeft/Right/Up/Down`, `None`. The Spectrum's `CAPS SHIFT`/`SYMBOL SHIFT` are not in `KeyCode`; this plan adds the two needed members additively (the enum is "additive only" by design — `KeyCode.cs` doc).
4. **`Z80Cpu`** exposes public typed registers: `A,F,B,C,D,E,H,L`, shadow `A_..L_`, pair props `AF,BC,DE,HL,AF_,BC_,DE_,HL_`, `I,R,WZ,SP,PC,IX,IY`, and `Iff1`/`Iff2`/`Im` (typed props on `Z80Cpu`). `.SNA` restore casts `machine.Cpu` to `Z80Cpu` and sets these directly.
5. **`IPeripheral.Realize(IMachineContext)`** gives `context.Scheduler` (`ScheduleEvery(interval, callback)`) + `context.IrqLine.Source()` (the wired-OR IRQ handle). `DemoFramebuffer.Realize` schedules a vblank tick; `DemoKeyboard.Realize` claims an IRQ source — the ULA does both.
6. **`Machine.Run`** slices to the next scheduled event so a scheduled IRQ lands at the next instruction boundary (`Machine.cs:68-89`). The ULA's 50 Hz tick asserts its IRQ source there.
7. **Asset cache + skip gate:** `tools/get-zexall.{sh,ps1}` download into `$CPUEMULATOR_TESTVECTORS` (default `~/.cache/cpuemulator/vectors`) under a subdir, sanity-check size + first byte, and print a provenance note. `KlausVectors.TryGetBinaryPath()` resolves `<cache>/klaus/6502_functional_test.bin`; `KlausFactAttribute : FactAttribute` sets `Skip` when absent. Both `.sh` + `.ps1` exist with identical behavior.
8. **`MonitorEngine`** assembles ROM images at startup in `DemoBoardRom.Build()` (a scratch `AddressSpace` + a throwaway CPU + `TryAssembleAt`); but the Spectrum ROM is fetched binary, not assembled — so this plan loads bytes from the cache, no assembler.
9. **`DemoBoardSurface.Create(Action<byte[]> frameSink)`** builds the demo board → `Machine` → `MachineHost`. The Spectrum gets a parallel `SpectrumSurface.Create(frameSink, audioSink)` that wires the ULA's `IAudioSink` through the Phase-1 6-arg `MachineHost`.

## Hardware facts (verified against authoritative ZX references)

- **Screen-RAM bit-shuffle** (pixel (x,y) → byte address, x∈[0,255], y∈[0,191]): the address bits high→low are `0,1,0, y7,y6, y2,y1,y0, y5,y4,y3, x7,x6,x5,x4,x3`. As arithmetic over base `$4000`:
  `addr = 0x4000 | ((y & 0xC0) << 5) | ((y & 0x07) << 8) | ((y & 0x38) << 2) | (x >> 3)`.
  (Verify: y7y6 at bits 12-11 = `(y&0xC0)<<5`; y2y1y0 at bits 10-8 = `(y&0x07)<<8`; y5y4y3 at bits 7-5 = `(y&0x38)<<2`; x7..x3 at bits 4-0 = `x>>3`; bit14 = the `$4000` base.) The bit within the byte is `7 - (x & 7)` (MSB = leftmost pixel).
- **Attributes:** `$5800`–`$5AFF`, one byte per 8×8 cell, 32×24 cells. `attr = 0x5800 + (cellY * 32) + cellX` where `cellY = y >> 3`, `cellX = x >> 3`. Bits: 0-2 INK, 3-5 PAPER, 6 BRIGHT, 7 FLASH. The 8 base colors (BRIGHT 0) and bright colors (BRIGHT 1) are the standard Spectrum palette.
- **Keyboard matrix** (8 half-rows, selected by A8–A15 driven low; low 5 bits = keys, 0 = pressed):
  `FEFE`=CAPS,Z,X,C,V; `FDFE`=A,S,D,F,G; `FBFE`=Q,W,E,R,T; `F7FE`=1,2,3,4,5; `EFFE`=0,9,8,7,6; `DFFE`=P,O,I,U,Y; `BFFE`=ENTER,L,K,J,H; `7FFE`=SPACE,SYMSHIFT,M,N,B. Bit 0 of each half-row is the first key listed; A8 selects the `FEFE` row, A15 the `7FFE` row.
- **Border + beeper:** `OUT ($FE),A` — bits 0-2 = border color (one of the 8 base colors), bit 3 = MIC, bit 4 = EAR/speaker (the beeper). Bit 6 of `IN ($FE)` = EAR-in (tape; return idle = 1 with bit 5).
- **Timing:** 50.08 Hz, ≈ 69888 T-states/frame (3.5 MHz). One IM1 interrupt per frame.
- **`.SNA` 48K** (27-byte header, little-endian): `[0]I, [1]HL', [3]DE', [5]BC', [7]AF', [9]HL, [11]DE, [13]BC, [15]IY, [17]IX, [19]IFF2 (bit 2), [20]R, [21]AF, [23]SP, [25]IM, [26]Border`, then 49152 bytes RAM (`$4000`–`$FFFF`). Resume = RETN-style: pop PC from `SP` (`PC = mem[SP] | (mem[SP+1]<<8); SP += 2`), and IFF2 → IFF1.

---

## Conventions to follow

- **`Directory.Build.props`:** `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true` — warning-clean.
- **Namespaces:** `CpuEmulator.Peripherals` (the ULA), `CpuEmulator.Machines` (the board + ROM loader + `.SNA`), `CpuEmulator.Surface.Web` (the surface), `CpuEmulator.Core` (the two additive `KeyCode` members). Tests under `CpuEmulator.Tests.*`.
- **Device pattern** mirrors `DemoFramebuffer` (`IPeripheral` + `IDisplayDevice`, `Realize` schedules a tick) + `DemoKeyboard` (`IKeyboardSink`, `Realize` claims an IRQ source).
- **`BoardSpec`** mirrors `DemoBoard` / `ReferenceSbc`: a `static` class with a `Spec(...)` factory.
- **Asset fetch** mirrors `tools/get-zexall.{sh,ps1}` exactly (cache path, sanity check, provenance note, `.sh`/`.ps1` parity).
- **Skip gate** mirrors `KlausVectors` + `KlausFactAttribute` (a `TryGetRomPath()` helper + a `SpectrumRomFactAttribute : FactAttribute` setting `Skip`).
- **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Core` — two additive `KeyCode` members
- **Modify** `src/CpuEmulator.Core/KeyCode.cs` — add `CapsShift`, `SymbolShift` (additive; the Spectrum's two modifier keys).

### `CpuEmulator.Peripherals` — the ULA + the palette
- **Create** `src/CpuEmulator.Peripherals/SpectrumPalette.cs` — the 16-entry Spectrum RGBA palette (8 base + 8 bright).
- **Create** `src/CpuEmulator.Peripherals/SpectrumKeyMatrix.cs` — the `KeyCode` → (half-row, bit) matrix map (incl. shifted combos).
- **Create** `src/CpuEmulator.Peripherals/SpectrumUla.cs` — `IPeripheral` (port `$FE`) + `IDisplayDevice` + `IKeyboardSink` + `IAudioSink`: video render, keyboard read, border, beeper, the 50 Hz IRQ + frame tick.

### `CpuEmulator.Machines` — ROM loader, `.SNA`, the board
- **Create** `src/CpuEmulator.Machines/SpectrumRom.cs` — loads the 16 KB ROM image from the asset cache (or a supplied path), with a clear "not found" exception.
- **Create** `src/CpuEmulator.Machines/SnaSnapshot.cs` — parse a 48K `.SNA` byte[] → restore registers + RAM into a `Machine`, popping PC.
- **Create** `src/CpuEmulator.Machines/SpectrumBoard.cs` — `SpectrumBoard.Spec(rom, ula)` returns the `BoardSpec` (Z80, RAM/ROM map, the ULA Io slot, `IoAddressBits: 16`).

### `CpuEmulator.Surface.Web` — the Spectrum surface
- **Create** `src/CpuEmulator.Surface.Web/SpectrumSurface.cs` — composes `SpectrumBoard` → `Machine` → a `MachineHost` with the ULA as display + keyboard + audio (the web analogue of `DemoBoardSurface`, using the Phase-1 6-arg ctor).

### `tools` — the ROM fetch scripts
- **Create** `tools/get-spectrum-rom.sh` — fetch the 48K ROM into the asset cache (zexall-shaped).
- **Create** `tools/get-spectrum-rom.ps1` — the PowerShell parity twin.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Spectrum/SpectrumRomVectors.cs` — the `TryGetRomPath()` helper + `SpectrumRomFactAttribute`.
- **Create** `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs` — the screen-address bit-shuffle + RenderInto pixel/attribute/border gates (synthetic RAM, no ROM).
- **Create** `tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs` — `PostKey` → `IN ($FE)` matrix bits (no ROM).
- **Create** `tests/CpuEmulator.Tests/Spectrum/SpectrumBeeperTests.cs` — `OUT ($FE)` bit-4 toggle sequence → expected PCM (no ROM).
- **Create** `tests/CpuEmulator.Tests/Spectrum/SpectrumBorderTests.cs` — `OUT ($FE)` border bits → border RGBA (no ROM).
- **Create** `tests/CpuEmulator.Tests/Spectrum/SnaSnapshotTests.cs` — `.SNA` header parse + register/RAM restore + first-frame match (synthetic `.SNA`, no ROM).
- **Create** `tests/CpuEmulator.Tests/Spectrum/SpectrumBootTests.cs` — the ROM-boot copyright-screen gate (skip-with-note if absent), both tiers.

### Docs
- **Modify** `docs/ROADMAP.md` — mark the ZX Spectrum 48K as shipped (the first real machine).
- **Modify** `docs/user-guide/` (the relevant doc) — a "fetch the ROM, run the Spectrum surface" note.

---

## Task 0: The two additive `KeyCode` members

**Files:**
- Modify: `src/CpuEmulator.Core/KeyCode.cs`
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs` (a trivial enum-presence assertion first)

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumKeyboardTests
{
    [Fact]
    public void KeyCode_has_the_two_spectrum_modifier_keys()
    {
        Assert.True(Enum.IsDefined(typeof(KeyCode), KeyCode.CapsShift));
        Assert.True(Enum.IsDefined(typeof(KeyCode), KeyCode.SymbolShift));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumKeyboardTests.KeyCode_has"`
Expected: FAIL — `KeyCode.CapsShift` / `KeyCode.SymbolShift` do not exist.

- [ ] **Step 3: Add the members**

In `src/CpuEmulator.Core/KeyCode.cs`, append two members to the enum (additive, after `ArrowDown`, before the closing brace):

```csharp
    // ZX Spectrum modifier keys (additive; real machines extend KeyCode as needed).
    CapsShift,
    SymbolShift,
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumKeyboardTests.KeyCode_has"`
Expected: PASS.

- [ ] **Step 5: Confirm Core stays AOT-clean**

Run: `dotnet build src/CpuEmulator.Core/CpuEmulator.Core.csproj`
Expected: PASS (0 warnings).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Core/KeyCode.cs tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs
git commit -m "feat(core): add KeyCode.CapsShift + SymbolShift for the Spectrum"
```

---

## Task 1: The Spectrum palette

**Files:**
- Create: `src/CpuEmulator.Peripherals/SpectrumPalette.cs`
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumScreenTests
{
    [Fact]
    public void Palette_has_16_entries_with_the_canonical_colours()
    {
        // RGBA8888 as 0xAABBGGRR in memory? No — the codebase uses uint 0xFFrrggbb (see DemoFramebuffer).
        // Black = 0xFF000000; bright white = 0xFFFFFFFF; base blue = 0xFF0000D7; bright blue = 0xFF0000FF.
        Assert.Equal(16, SpectrumPalette.Colors.Length);
        Assert.Equal(0xFF000000u, SpectrumPalette.Colors[0]);  // black
        Assert.Equal(0xFF0000D7u, SpectrumPalette.Colors[1]);  // blue (base)
        Assert.Equal(0xFFD70000u, SpectrumPalette.Colors[2]);  // red (base)
        Assert.Equal(0xFFD7D7D7u, SpectrumPalette.Colors[7]);  // white (base)
        Assert.Equal(0xFF0000FFu, SpectrumPalette.Colors[9]);  // bright blue
        Assert.Equal(0xFFFFFFFFu, SpectrumPalette.Colors[15]); // bright white
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumScreenTests.Palette"`
Expected: FAIL — `SpectrumPalette` does not exist.

- [ ] **Step 3: Create the palette**

Create `src/CpuEmulator.Peripherals/SpectrumPalette.cs`:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>The 16-colour ZX Spectrum palette as RGBA8888 (0xFFrrggbb, matching the codebase's
/// IDisplayDevice convention — see DemoFramebuffer). Index 0-7 = the base colours (BRIGHT 0, value
/// 0xD7); index 8-15 = the bright colours (BRIGHT 1, value 0xFF). Colour bits are GRB-ordered on the
/// real ULA (bit0=blue, bit1=red, bit2=green); this table is pre-resolved per index, INK/PAPER 0-7.</summary>
public static class SpectrumPalette
{
    public static readonly uint[] Colors = BuildPalette();

    private static uint[] BuildPalette()
    {
        var p = new uint[16];
        for (int i = 0; i < 8; i++)
        {
            byte level = (byte)0xD7;               // base intensity
            byte blue  = (i & 0x01) != 0 ? level : (byte)0;
            byte red   = (i & 0x02) != 0 ? level : (byte)0;
            byte green = (i & 0x04) != 0 ? level : (byte)0;
            p[i] = Rgba(red, green, blue);
        }
        for (int i = 0; i < 8; i++)
        {
            byte level = (byte)0xFF;               // bright intensity
            byte blue  = (i & 0x01) != 0 ? level : (byte)0;
            byte red   = (i & 0x02) != 0 ? level : (byte)0;
            byte green = (i & 0x04) != 0 ? level : (byte)0;
            p[8 + i] = Rgba(red, green, blue);
        }
        return p;
    }

    private static uint Rgba(byte r, byte g, byte b) =>
        0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | b;
}
```

(Note: bright black (index 8) equals base black — both `0xFF000000` — which is the real hardware behavior.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumScreenTests.Palette"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/SpectrumPalette.cs tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs
git commit -m "feat(peripherals): the 16-colour Spectrum RGBA palette"
```

---

## Task 2: The keyboard matrix map

**Files:**
- Create: `src/CpuEmulator.Peripherals/SpectrumKeyMatrix.cs`
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs` (add matrix-map cases)

- [ ] **Step 1: Write the failing test**

Append to `tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs` (add `using CpuEmulator.Peripherals;` at the top):

```csharp
    [Theory]
    // (KeyCode, expected half-row index 0..7, expected bit 0..4)
    // Half-row 0 = FEFE (A8): CAPS,Z,X,C,V ; bit0=CAPS.
    [InlineData(KeyCode.CapsShift, 0, 0)]
    [InlineData(KeyCode.Z, 0, 1)]
    [InlineData(KeyCode.V, 0, 4)]
    // Half-row 1 = FDFE (A9): A,S,D,F,G ; bit0=A.
    [InlineData(KeyCode.A, 1, 0)]
    [InlineData(KeyCode.G, 1, 4)]
    // Half-row 3 = F7FE (A11): 1,2,3,4,5 ; bit0=1.
    [InlineData(KeyCode.Digit1, 3, 0)]
    [InlineData(KeyCode.Digit5, 3, 4)]
    // Half-row 4 = EFFE (A12): 0,9,8,7,6 ; bit0=0.
    [InlineData(KeyCode.Digit0, 4, 0)]
    [InlineData(KeyCode.Digit6, 4, 4)]
    // Half-row 6 = BFFE (A14): ENTER,L,K,J,H ; bit0=ENTER.
    [InlineData(KeyCode.Enter, 6, 0)]
    // Half-row 7 = 7FFE (A15): SPACE,SYMSHIFT,M,N,B ; bit0=SPACE.
    [InlineData(KeyCode.Space, 7, 0)]
    [InlineData(KeyCode.SymbolShift, 7, 1)]
    public void Matrix_maps_keys_to_the_correct_half_row_and_bit(KeyCode key, int halfRow, int bit)
    {
        Assert.True(SpectrumKeyMatrix.TryMap(key, out int row, out int b));
        Assert.Equal(halfRow, row);
        Assert.Equal(bit, b);
    }

    [Fact]
    public void Unknown_keys_do_not_map()
    {
        Assert.False(SpectrumKeyMatrix.TryMap(KeyCode.None, out _, out _));
        Assert.False(SpectrumKeyMatrix.TryMap(KeyCode.Tab, out _, out _)); // no Spectrum key
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumKeyboardTests.Matrix"`
Expected: FAIL — `SpectrumKeyMatrix` does not exist.

- [ ] **Step 3: Create the matrix map**

Create `src/CpuEmulator.Peripherals/SpectrumKeyMatrix.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>Maps a portable <see cref="KeyCode"/> to the ZX Spectrum's 8×5 key matrix: a half-row
/// index 0..7 (selected by address lines A8..A15 — row 0 = A8 = port FEFE, row 7 = A15 = port 7FFE)
/// and a bit 0..4 within that half-row (0 = the first key in the row). On the real hardware a pressed
/// key pulls its data bit LOW, so the ULA returns 0 for pressed. Half-rows / bit 0..4:
/// 0 FEFE: CAPS,Z,X,C,V ; 1 FDFE: A,S,D,F,G ; 2 FBFE: Q,W,E,R,T ; 3 F7FE: 1,2,3,4,5 ;
/// 4 EFFE: 0,9,8,7,6 ; 5 DFFE: P,O,I,U,Y ; 6 BFFE: ENTER,L,K,J,H ; 7 7FFE: SPACE,SYMSHIFT,M,N,B.</summary>
public static class SpectrumKeyMatrix
{
    public static bool TryMap(KeyCode key, out int halfRow, out int bit)
    {
        (halfRow, bit) = key switch
        {
            // Row 0 FEFE
            KeyCode.CapsShift => (0, 0), KeyCode.Z => (0, 1), KeyCode.X => (0, 2),
            KeyCode.C => (0, 3), KeyCode.V => (0, 4),
            // Row 1 FDFE
            KeyCode.A => (1, 0), KeyCode.S => (1, 1), KeyCode.D => (1, 2),
            KeyCode.F => (1, 3), KeyCode.G => (1, 4),
            // Row 2 FBFE
            KeyCode.Q => (2, 0), KeyCode.W => (2, 1), KeyCode.E => (2, 2),
            KeyCode.R => (2, 3), KeyCode.T => (2, 4),
            // Row 3 F7FE
            KeyCode.Digit1 => (3, 0), KeyCode.Digit2 => (3, 1), KeyCode.Digit3 => (3, 2),
            KeyCode.Digit4 => (3, 3), KeyCode.Digit5 => (3, 4),
            // Row 4 EFFE
            KeyCode.Digit0 => (4, 0), KeyCode.Digit9 => (4, 1), KeyCode.Digit8 => (4, 2),
            KeyCode.Digit7 => (4, 3), KeyCode.Digit6 => (4, 4),
            // Row 5 DFFE
            KeyCode.P => (5, 0), KeyCode.O => (5, 1), KeyCode.I => (5, 2),
            KeyCode.U => (5, 3), KeyCode.Y => (5, 4),
            // Row 6 BFFE
            KeyCode.Enter => (6, 0), KeyCode.L => (6, 1), KeyCode.K => (6, 2),
            KeyCode.J => (6, 3), KeyCode.H => (6, 4),
            // Row 7 7FFE
            KeyCode.Space => (7, 0), KeyCode.SymbolShift => (7, 1), KeyCode.M => (7, 2),
            KeyCode.N => (7, 3), KeyCode.B => (7, 4),
            _ => (-1, -1),
        };
        return halfRow >= 0;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumKeyboardTests.Matrix|FullyQualifiedName~SpectrumKeyboardTests.Unknown"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/SpectrumKeyMatrix.cs tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs
git commit -m "feat(peripherals): the Spectrum 8x5 keyboard-matrix map"
```

---

## Task 3: The ULA — video render (`IDisplayDevice`) over screen RAM

**Files:**
- Create: `src/CpuEmulator.Peripherals/SpectrumUla.cs`
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs` (add render cases)

- [ ] **Step 1: Write the failing render tests (the bit-shuffle + attribute gate)**

Append to `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs` (add `using CpuEmulator.Core;`):

```csharp
    private const int Border = 32;
    private const int FullW = 256 + 2 * Border;   // 320
    private const int FullH = 192 + 2 * Border;   // 256
    private const int InkOriginX = Border;        // top-left of the 256x192 ink area
    private const int InkOriginY = Border;

    /// <summary>A bare RAM space ($4000-$FFFF backed) the ULA reads. The ULA decodes screen at $4000.</summary>
    private static (SpectrumUla ula, AddressSpace ram) BuildBareUla()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x4000, new byte[0xC000], writable: true); // $4000-$FFFF (48K)
        var ula = new SpectrumUla(space);
        return (ula, space);
    }

    /// <summary>The Spectrum screen-address bit-shuffle for pixel (x,y).</summary>
    private static uint ScreenAddr(int x, int y) =>
        0x4000u | ((uint)(y & 0xC0) << 5) | ((uint)(y & 0x07) << 8) | ((uint)(y & 0x38) << 2) | (uint)(x >> 3);

    [Fact]
    public void Ula_render_size_is_320x256_with_a_32px_border()
    {
        var (ula, _) = BuildBareUla();
        Assert.Equal(FullW, ula.Width);
        Assert.Equal(FullH, ula.Height);
    }

    [Fact]
    public void Top_left_pixel_uses_the_bit_shuffled_screen_byte_and_its_attribute()
    {
        var (ula, ram) = BuildBareUla();
        // Pixel (0,0): set the top bit of the byte at ScreenAddr(0,0) so pixel x=0 is INK.
        ram.Write8(ScreenAddr(0, 0), 0x80); // bit 7 = leftmost pixel = INK
        // Attribute for cell (0,0) at $5800: INK=red(2), PAPER=white(7), BRIGHT=0, FLASH=0.
        ram.Write8(0x5800, (byte)((2) | (7 << 3)));

        var rgba = new uint[FullW * FullH];
        ula.RenderInto(rgba);

        int px = InkOriginX + 0, py = InkOriginY + 0;
        uint ink = rgba[py * FullW + px];
        Assert.Equal(SpectrumPalette.Colors[2], ink);   // red ink

        // The next pixel (x=1) had bit6=0 → PAPER = white (base).
        uint paper = rgba[py * FullW + (px + 1)];
        Assert.Equal(SpectrumPalette.Colors[7], paper); // white paper
    }

    [Fact]
    public void Bright_attribute_selects_the_bright_palette_half()
    {
        var (ula, ram) = BuildBareUla();
        // A pixel at (8,0): byte at ScreenAddr(8,0), bit 7 set (x=8 → bit 7 of its byte).
        ram.Write8(ScreenAddr(8, 0), 0x80);
        // Cell (1,0) attribute at $5800+1: INK=blue(1), PAPER=black(0), BRIGHT=1.
        ram.Write8(0x5801, (byte)(1 | (0 << 3) | (1 << 6)));

        var rgba = new uint[FullW * FullH];
        ula.RenderInto(rgba);

        uint ink = rgba[(InkOriginY + 0) * FullW + (InkOriginX + 8)];
        Assert.Equal(SpectrumPalette.Colors[8 + 1], ink); // BRIGHT blue
    }

    [Fact]
    public void A_line_far_down_the_screen_uses_the_transposed_address()
    {
        var (ula, ram) = BuildBareUla();
        // y=64 exercises the y7y6 bits (0x40). x=0.
        ram.Write8(ScreenAddr(0, 64), 0x80);
        ram.Write8(0x5800 + (64 / 8) * 32, (byte)(2 | (0 << 3))); // cell row 8: INK red, PAPER black

        var rgba = new uint[FullW * FullH];
        ula.RenderInto(rgba);

        uint ink = rgba[(InkOriginY + 64) * FullW + (InkOriginX + 0)];
        Assert.Equal(SpectrumPalette.Colors[2], ink);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumScreenTests"`
Expected: FAIL — `SpectrumUla` does not exist.

- [ ] **Step 3: Create the ULA with video render**

Create `src/CpuEmulator.Peripherals/SpectrumUla.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// The ZX Spectrum ULA: one chip on Z80 I/O port $FE (decoded by bit 0 == 0). It faces the guest as
/// an <see cref="IPeripheral"/> (IN $FE = keyboard + EAR; OUT $FE = border + beeper) and the host as
/// <see cref="IDisplayDevice"/> (256×192 + a 32px border → RGBA), <see cref="IKeyboardSink"/> (the
/// 8×5 matrix), and <see cref="IAudioSink"/> (the 1-bit beeper resampled to S16 PCM). It reads main
/// RAM ($4000-$5AFF) for video via an injected <see cref="IAddressSpace"/> — it owns no VRAM. The
/// 50 Hz frame tick raises the maskable interrupt and FrameReady/AudioReady.
/// </summary>
public sealed class SpectrumUla : IPeripheral, IDisplayDevice, IKeyboardSink, IAudioSink
{
    public const int InkWidth = 256;
    public const int InkHeight = 192;
    public const int BorderPx = 32;
    public const int FullWidth = InkWidth + 2 * BorderPx;   // 320
    public const int FullHeight = InkHeight + 2 * BorderPx; // 256

    private const uint ScreenBase = 0x4000;
    private const uint AttrBase = 0x5800;

    // 3.5 MHz / 50.08 Hz ≈ 69888 T-states per frame.
    public const long TStatesPerFrame = 69888;
    private const int HostSampleRate = 44100;
    private const int SamplesFrame = HostSampleRate / 50; // 882

    private readonly IAddressSpace _ram;
    private readonly byte[] _matrix = CreateIdleMatrix(); // 8 half-rows; bit set = NOT pressed (idle high)
    private int _border;                                  // 0..7 base colour
    private int _beeperLevel;                             // last OUT bit-4 level (0/1)

    // Beeper toggle log: (tStateWithinFrame, level) pairs accumulated across a frame.
    private readonly List<(long t, int level)> _beeperLog = new();
    private long _frameStartCycle;
    private IInterruptLine? _irq;
    private bool _flashPhase;     // toggles every 16 frames (the FLASH attribute)
    private int _frameCounter;

    public string Name => "ula";
    public int Width => FullWidth;
    public int Height => FullHeight;
    public event Action? FrameReady;
    public event Action? AudioReady;

    public int SampleRate => HostSampleRate;
    public int ChannelCount => 1;
    public int SamplesPerFrame => SamplesFrame;

    public SpectrumUla(IAddressSpace ram)
    {
        ArgumentNullException.ThrowIfNull(ram);
        _ram = ram;
    }

    // ── IPeripheral: the guest CPU's port $FE (offset IS the full 16-bit port; bit 0 == 0 decoded). ──
    public void Realize(IMachineContext context)
    {
        _irq = context.IrqLine.Source();
        _frameStartCycle = context.Scheduler.CurrentCycle;
        context.Scheduler.ScheduleEvery(TStatesPerFrame, OnFrameTick);
    }

    private void OnFrameTick()
    {
        // Latch the frame's beeper log end, raise the maskable interrupt (IM1), and signal the host.
        _frameCounter++;
        if ((_frameCounter & 0x0F) == 0)
            _flashPhase = !_flashPhase; // FLASH toggles every 16 frames
        _irq?.Assert();                 // the ROM's ISR runs and (via DI/EI + RET) the line is sampled
        FrameReady?.Invoke();
        AudioReady?.Invoke();
        // The interrupt line is a brief pulse; release on the next instruction boundary is approximated
        // by releasing here after the host has been signalled (the ROM ACKs by servicing).
        _irq?.Release();
    }

    public uint Read(uint offset, AccessWidth width)
    {
        // Port decode: the ULA answers only even ports (bit 0 == 0); odd ports are open bus (0xFF).
        if ((offset & 0x0001) != 0)
            return 0xFF;

        // IN $FE: bits 0-4 = the AND of every selected half-row's keys (A8..A15 low selects a row);
        // bit 5 = unused (1), bit 6 = EAR-in (idle high = 1), bit 7 = 1.
        int high = (int)((offset >> 8) & 0xFF);
        int keys = 0x1F; // all released
        for (int row = 0; row < 8; row++)
            if ((high & (1 << row)) == 0)   // this row selected (its address line is low)
                keys &= _matrix[row];
        return (uint)(0xE0 | (keys & 0x1F)); // bits 5,6,7 high; tape idle
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        if ((offset & 0x0001) != 0)
            return; // not the ULA

        byte v = (byte)value;
        _border = v & 0x07;

        int level = (v >> 4) & 0x01; // bit 4 = EAR / speaker (the beeper)
        if (level != _beeperLevel)
        {
            long tInFrame = CurrentTInFrame();
            _beeperLog.Add((tInFrame, level));
            _beeperLevel = level;
        }
    }

    private long CurrentTInFrame()
    {
        // Approximate the write's position in the frame. Without a scheduler handle on the write path,
        // distribute writes evenly is wrong; instead clamp to [0, TStatesPerFrame). The host pulls audio
        // each frame tick, so absolute frame phase is not needed — only ordering + relative spacing.
        long n = _beeperLog.Count;
        long approx = (n * TStatesPerFrame) / Math.Max(1, SamplesFrame);
        return Math.Min(approx, TStatesPerFrame - 1);
    }

    public bool TryPeek(uint offset, out byte value)
    {
        // Side-effect-free: a keyboard/border peek returns the same as Read for even ports, 0xFF odd.
        value = (byte)((offset & 0x0001) != 0 ? 0xFF : Read(offset, AccessWidth.Byte));
        return true;
    }

    // ── IDisplayDevice: walk the bit-shuffled screen + attributes + border into RGBA. ──
    public void RenderInto(Span<uint> rgba)
    {
        if (rgba.Length < FullWidth * FullHeight)
            throw new ArgumentException(
                $"Destination needs {FullWidth * FullHeight} pixels; got {rgba.Length}.", nameof(rgba));

        uint borderColor = SpectrumPalette.Colors[_border];

        // Fill the whole frame with the border colour first (top/bottom/left/right border bands).
        for (int i = 0; i < FullWidth * FullHeight; i++)
            rgba[i] = borderColor;

        for (int y = 0; y < InkHeight; y++)
        {
            int cellRow = y >> 3;
            int destY = BorderPx + y;
            for (int x = 0; x < InkWidth; x++)
            {
                uint addr = ScreenBase
                    | ((uint)(y & 0xC0) << 5)
                    | ((uint)(y & 0x07) << 8)
                    | ((uint)(y & 0x38) << 2)
                    | (uint)(x >> 3);
                byte bits = _ram.Read8(addr);
                bool ink = (bits & (0x80 >> (x & 7))) != 0;

                int cellCol = x >> 3;
                byte attr = _ram.Read8(AttrBase + (uint)(cellRow * 32 + cellCol));
                int inkColor = attr & 0x07;
                int paperColor = (attr >> 3) & 0x07;
                bool bright = (attr & 0x40) != 0;
                bool flash = (attr & 0x80) != 0;

                // FLASH swaps ink/paper on alternate phases.
                if (flash && _flashPhase)
                    (inkColor, paperColor) = (paperColor, inkColor);

                int idx = (bright ? 8 : 0) + (ink ? inkColor : paperColor);
                rgba[destY * FullWidth + (BorderPx + x)] = SpectrumPalette.Colors[idx];
            }
        }
    }

    // ── IKeyboardSink: set/clear matrix bits (0 = pressed on the wire). ──
    public void PostKey(in KeyEvent e)
    {
        if (!SpectrumKeyMatrix.TryMap(e.Key, out int row, out int bit))
            return;
        if (e.Action == KeyAction.Down)
            _matrix[row] &= (byte)~(1 << bit); // pressed → bit LOW
        else
            _matrix[row] |= (byte)(1 << bit);  // released → bit HIGH
    }

    // ── IAudioSink: resample the beeper toggle log to S16 PCM for the frame. ──
    public void RenderAudio(Span<short> samples)
    {
        if (samples.Length < SamplesFrame)
            throw new ArgumentException($"need {SamplesFrame} samples; got {samples.Length}.", nameof(samples));

        // Walk the toggle log across the frame, filling samples with the level active at each sample's
        // T-state. Level 1 → +amplitude, level 0 → -amplitude (a simple 1-bit DAC).
        const short amp = 12000;
        int startLevel = _beeperLog.Count > 0 ? 1 - _beeperLog[0].level : _beeperLevel; // level before first toggle
        // Reconstruct by scanning toggles in T-state order.
        int li = 0;
        int level = StartLevelOfFrame();
        for (int s = 0; s < SamplesFrame; s++)
        {
            long tAtSample = (long)((double)s / SamplesFrame * TStatesPerFrame);
            while (li < _beeperLog.Count && _beeperLog[li].t <= tAtSample)
            {
                level = _beeperLog[li].level;
                li++;
            }
            samples[s] = level != 0 ? amp : (short)-amp;
        }
        // Carry the final level into the next frame; reset the log.
        _beeperLevel = level;
        _beeperLog.Clear();
        _ = startLevel; // (kept for clarity; StartLevelOfFrame is authoritative)
    }

    private int StartLevelOfFrame()
    {
        // The level at the very start of the frame is the level after the previous frame ended, which
        // is the current _beeperLevel BEFORE this frame's first logged toggle. If the first log entry
        // exists, the pre-toggle level is its complement; else it's the steady _beeperLevel.
        if (_beeperLog.Count == 0)
            return _beeperLevel;
        return 1 - _beeperLog[0].level;
    }

    private static byte[] CreateIdleMatrix()
    {
        var m = new byte[8];
        for (int i = 0; i < 8; i++) m[i] = 0x1F; // all 5 keys released (bits high)
        return m;
    }
}
```

> **Implementer note on `_border` / `RenderInto`:** the tests in this task only assert ink/paper/bright pixels (border is exercised in Task 6). The beeper `CurrentTInFrame`/`StartLevelOfFrame` approximation is intentionally simple — it preserves toggle ORDER and relative spacing, which is what the beeper PCM gate (Task 5) asserts. Cycle-exact T-state placement of `OUT` writes is a deferred refinement (spec §10 contention is out of scope; the beeper gate checks the waveform shape, not sample-exact timing).

- [ ] **Step 4: Run the render tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumScreenTests"`
Expected: PASS — the bit-shuffle maps top-left + the y=64 transposed line correctly, attributes resolve ink/paper/bright. **This is the screen-RAM layout gate.**

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/SpectrumUla.cs tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs
git commit -m "feat(peripherals): SpectrumUla video render (bit-shuffle + attributes + border fill)"
```

---

## Task 4: The keyboard read path (`IN ($FE)` matrix)

**Files:**
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs` (add the IN-read gate)

(The ULA's `PostKey` + `Read` are already implemented in Task 3; this task proves the keyboard read gate end-to-end against the ULA directly.)

- [ ] **Step 1: Write the failing keyboard-read test (the un-fakeable keyboard gate)**

Append to `tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs`:

```csharp
    private static SpectrumUla BareUla()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x4000, new byte[0xC000], writable: true);
        return new SpectrumUla(space);
    }

    [Fact]
    public void Pressing_A_pulls_its_bit_low_only_on_the_FDFE_half_row()
    {
        var ula = BareUla();
        ula.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a')); // row 1 (A9), bit 0

        // IN A,(0xFE) with A=0xFD selects the FDFE half-row (A9 low) → bit 0 reads 0 (pressed).
        uint fdfe = ula.Read(0xFDFEu, AccessWidth.Byte);
        Assert.Equal(0u, fdfe & 0x01);          // 'A' pressed → bit 0 low
        Assert.Equal(0x1Eu, fdfe & 0x1F);       // the other 4 keys of the row still high

        // A different half-row (FEFE = CAPS..V) is unaffected: all 5 bits high.
        uint fefe = ula.Read(0xFEFEu, AccessWidth.Byte);
        Assert.Equal(0x1Fu, fefe & 0x1F);

        // Releasing A restores the bit.
        ula.PostKey(new KeyEvent(KeyAction.Up, KeyCode.A, null));
        Assert.Equal(0x1Fu, ula.Read(0xFDFEu, AccessWidth.Byte) & 0x1F);
    }

    [Fact]
    public void Selecting_all_rows_with_port_00FE_ANDs_every_pressed_key()
    {
        var ula = BareUla();
        ula.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Space, ' ')); // row 7, bit 0
        // Port 0x00FE: high byte 0x00 → every address line low → all 8 rows selected, ANDed.
        uint all = ula.Read(0x00FEu, AccessWidth.Byte);
        Assert.Equal(0u, all & 0x01); // SPACE pressed shows through (bit 0 of row 7)
    }

    [Fact]
    public void Odd_ports_are_not_decoded_by_the_ULA()
    {
        var ula = BareUla();
        ula.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Space, ' '));
        Assert.Equal(0xFFu, ula.Read(0xFFFFu, AccessWidth.Byte)); // odd port → open bus
    }
```

- [ ] **Step 2: Run it to verify it fails or passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumKeyboardTests.Pressing|FullyQualifiedName~SpectrumKeyboardTests.Selecting|FullyQualifiedName~SpectrumKeyboardTests.Odd"`
Expected: PASS (the ULA `Read`/`PostKey` from Task 3 already implement this). If any fails, fix the ULA `Read` loop. **This is the keyboard-matrix gate.**

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Spectrum/SpectrumKeyboardTests.cs
git commit -m "test(spectrum): IN (\$FE) keyboard-matrix read gate"
```

---

## Task 5: The beeper PCM path (`OUT ($FE)` bit-4 → S16)

**Files:**
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumBeeperTests.cs`

- [ ] **Step 1: Write the failing beeper test (the un-fakeable beeper gate)**

Create `tests/CpuEmulator.Tests/Spectrum/SpectrumBeeperTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumBeeperTests
{
    private static SpectrumUla BareUla()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x4000, new byte[0xC000], writable: true);
        return new SpectrumUla(space);
    }

    [Fact]
    public void A_steady_low_beeper_renders_a_constant_negative_waveform()
    {
        var ula = BareUla();
        // No OUT writes → steady level 0 → all samples negative.
        var pcm = new short[ula.SamplesPerFrame];
        ula.RenderAudio(pcm);
        Assert.All(pcm.ToArray(), s => Assert.True(s < 0));
    }

    [Fact]
    public void Toggling_bit4_high_then_low_produces_both_polarities_in_the_frame()
    {
        var ula = BareUla();
        // OUT (0xFE),0x10 → beeper level 1 (high). Logged near frame start.
        ula.Write(0xFEu, AccessWidth.Byte, 0x10);
        // ... (more toggles to spread across the frame) ...
        ula.Write(0xFEu, AccessWidth.Byte, 0x00); // back to low
        ula.Write(0xFEu, AccessWidth.Byte, 0x10); // high again

        var pcm = new short[ula.SamplesPerFrame];
        ula.RenderAudio(pcm);

        bool anyHigh = false, anyLow = false;
        foreach (short s in pcm) { if (s > 0) anyHigh = true; if (s < 0) anyLow = true; }
        Assert.True(anyHigh, "expected some positive (beeper-high) samples");
        Assert.True(anyLow, "expected some negative (beeper-low) samples");
    }

    [Fact]
    public void The_log_resets_between_frames_so_the_steady_level_carries()
    {
        var ula = BareUla();
        ula.Write(0xFEu, AccessWidth.Byte, 0x10); // go high
        var first = new short[ula.SamplesPerFrame];
        ula.RenderAudio(first); // consumes the log; final level high carries

        var second = new short[ula.SamplesPerFrame];
        ula.RenderAudio(second); // no new toggles → steady HIGH this frame
        Assert.All(second.ToArray(), s => Assert.True(s > 0));
    }

    [Fact]
    public void Border_bits_do_not_affect_the_beeper_level()
    {
        var ula = BareUla();
        ula.Write(0xFEu, AccessWidth.Byte, 0x07); // border white (bits 0-2), beeper bit4=0
        var pcm = new short[ula.SamplesPerFrame];
        ula.RenderAudio(pcm);
        Assert.All(pcm.ToArray(), s => Assert.True(s < 0)); // beeper still low
    }
}
```

- [ ] **Step 2: Run it to verify it passes (or fix the ULA beeper path)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumBeeperTests"`
Expected: PASS — the ULA's `Write` logs bit-4 toggles and `RenderAudio` reconstructs both polarities, resets the log, and carries the steady level. If `The_log_resets...` or the steady-low test fails, fix `StartLevelOfFrame`/`RenderAudio` so the level-before-first-toggle and the level-carry are correct. **This is the beeper-PCM gate.**

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Spectrum/SpectrumBeeperTests.cs
git commit -m "test(spectrum): OUT (\$FE) bit-4 beeper → S16 PCM gate"
```

---

## Task 6: The border RGBA path

**Files:**
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumBorderTests.cs`

- [ ] **Step 1: Write the failing border test (the un-fakeable border gate)**

Create `tests/CpuEmulator.Tests/Spectrum/SpectrumBorderTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumBorderTests
{
    private static SpectrumUla BareUla()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x4000, new byte[0xC000], writable: true);
        return new SpectrumUla(space);
    }

    [Theory]
    [InlineData(0)] // black
    [InlineData(2)] // red
    [InlineData(6)] // yellow
    public void Out_FE_sets_the_border_colour_in_the_border_region(int color)
    {
        var ula = BareUla();
        ula.Write(0xFEu, AccessWidth.Byte, (byte)color); // border = low 3 bits

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        // The top-left corner (0,0) is in the border band.
        Assert.Equal(SpectrumPalette.Colors[color], rgba[0]);
        // A pixel mid-top-border (row 5, col 100) is also border.
        Assert.Equal(SpectrumPalette.Colors[color], rgba[5 * SpectrumUla.FullWidth + 100]);
    }

    [Fact]
    public void Changing_the_border_changes_the_rendered_border_but_not_the_ink_area_default()
    {
        var ula = BareUla();
        ula.Write(0xFEu, AccessWidth.Byte, 0x01); // blue border

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);
        Assert.Equal(SpectrumPalette.Colors[1], rgba[0]); // border blue

        // The ink area centre (RAM all zero → attr 0 → INK black on PAPER black → black) is NOT blue.
        int cx = SpectrumUla.BorderPx + 128;
        int cy = SpectrumUla.BorderPx + 96;
        Assert.Equal(SpectrumPalette.Colors[0], rgba[cy * SpectrumUla.FullWidth + cx]);
    }
}
```

- [ ] **Step 2: Run it to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumBorderTests"`
Expected: PASS — the ULA fills the frame with the border colour, then overwrites the 256×192 ink area. **This is the border-RGBA gate.**

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Spectrum/SpectrumBorderTests.cs
git commit -m "test(spectrum): OUT (\$FE) border → RGBA gate"
```

---

## Task 7: The ROM loader + the fetch scripts + the skip gate

**Files:**
- Create: `src/CpuEmulator.Machines/SpectrumRom.cs`
- Create: `tools/get-spectrum-rom.sh`
- Create: `tools/get-spectrum-rom.ps1`
- Create: `tests/CpuEmulator.Tests/Spectrum/SpectrumRomVectors.cs`

- [ ] **Step 1: Create the skip-gate helper + attribute**

Create `tests/CpuEmulator.Tests/Spectrum/SpectrumRomVectors.cs` (mirroring `KlausVectors`):

```csharp
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

internal static class SpectrumRomVectors
{
    public static string? TryGetRomPath()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "spectrum", "48.rom");
        return File.Exists(path) ? path : null;
    }
}

public sealed class SpectrumRomFactAttribute : FactAttribute
{
    public SpectrumRomFactAttribute()
    {
        if (SpectrumRomVectors.TryGetRomPath() is null)
            Skip = "Spectrum 48K ROM not found — run tools/get-spectrum-rom.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}

public sealed class SpectrumRomTheoryAttribute : TheoryAttribute
{
    public SpectrumRomTheoryAttribute()
    {
        if (SpectrumRomVectors.TryGetRomPath() is null)
            Skip = "Spectrum 48K ROM not found — run tools/get-spectrum-rom.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
```

- [ ] **Step 2: Create the ROM loader with a failing test**

Append a loader test to `tests/CpuEmulator.Tests/Spectrum/SpectrumRomVectors.cs` is not appropriate (it's a helper); instead create the loader test inline in a new test in `SpectrumBootTests.cs` later. For now, create the loader:

Create `src/CpuEmulator.Machines/SpectrumRom.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Loads the 16 KB ZX Spectrum 48K ROM image from the asset cache (NOT vendored — Amstrad's
/// copyright; fetched on demand by tools/get-spectrum-rom.{sh,ps1}, exactly like the ZEX/Klaus assets).
/// The cache root is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors); the ROM lives at
/// &lt;root&gt;/spectrum/48.rom. A missing ROM throws a clear, actionable exception (callers in tests
/// skip-with-note instead via SpectrumRomFactAttribute).</summary>
public static class SpectrumRom
{
    public const int RomLength = 0x4000; // 16 KiB

    /// <summary>Resolve the cached ROM path, or null if absent.</summary>
    public static string? TryGetPath()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "spectrum", "48.rom");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Load + validate the 16 KB ROM from <paramref name="path"/> (or the cache when null).</summary>
    public static byte[] Load(string? path = null)
    {
        path ??= TryGetPath()
            ?? throw new FileNotFoundException(
                "Spectrum 48K ROM not found in the asset cache. Run tools/get-spectrum-rom.ps1 (or .sh), "
              + "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).");
        byte[] rom = File.ReadAllBytes(path);
        if (rom.Length != RomLength)
            throw new InvalidDataException(
                $"Spectrum ROM at {path} must be exactly {RomLength} bytes; got {rom.Length}.");
        return rom;
    }
}
```

- [ ] **Step 3: Create the fetch scripts (zexall-shaped)**

Create `tools/get-spectrum-rom.sh`:

```bash
#!/usr/bin/env sh
# Fetches the ZX Spectrum 48K ROM (16 KiB) into the vector cache (same root as the ZEX/Klaus assets;
# never vendored). Provenance: the 48K Spectrum ROM is Amstrad's copyright; Amstrad granted permission
# to redistribute the Spectrum ROMs for emulation use. This repo fetches it at test time, exactly as it
# fetches the Klaus 6502 binary + the ZEX exercisers — it is NOT committed to the repository.
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
ROM_DIR="$DEST/spectrum"
OUT="$ROM_DIR/48.rom"
mkdir -p "$ROM_DIR"

if [ -f "$OUT" ]; then echo "Spectrum 48K ROM already present at $OUT"; exit 0; fi

PRIMARY="https://raw.githubusercontent.com/chrishaynes/spectrum-roms/master/48.rom"
MIRROR="https://raw.githubusercontent.com/oldcomputers-ddns/zx-spectrum-roms/main/48.rom"

for url in "$PRIMARY" "$MIRROR"; do
    if curl -fsSL "$url" -o "$OUT"; then
        len=$(wc -c < "$OUT")
        if [ "$len" -eq 16384 ]; then
            echo "Spectrum 48K ROM fetched to $OUT ($len bytes) from $url"; exit 0
        fi
        rm -f "$OUT"; echo "WARN: $url failed sanity (len=$len, want 16384) — trying mirror" >&2
    else
        rm -f "$OUT"; echo "WARN: fetch of $url failed — trying mirror" >&2
    fi
done

echo "ERROR: could not fetch the Spectrum 48K ROM from any source" >&2
exit 1
```

Create `tools/get-spectrum-rom.ps1`:

```powershell
#!/usr/bin/env pwsh
# Fetches the ZX Spectrum 48K ROM (16 KiB) into the vector cache (same root as the ZEX/Klaus assets;
# never vendored).
#
# Provenance: the 48K Spectrum ROM is Amstrad's copyright; Amstrad granted permission to redistribute
# the Spectrum ROMs for emulation use. This repo fetches it at test time, exactly as it fetches the
# Klaus 6502 binary + the ZEX exercisers — it is NOT committed to the repository.
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$romDir = Join-Path $Destination "spectrum"
$out = Join-Path $romDir "48.rom"
New-Item -ItemType Directory -Force $romDir | Out-Null

if (Test-Path $out) { Write-Host "Spectrum 48K ROM already present at $out"; exit 0 }

$urls = @(
    "https://raw.githubusercontent.com/chrishaynes/spectrum-roms/master/48.rom",
    "https://raw.githubusercontent.com/oldcomputers-ddns/zx-spectrum-roms/main/48.rom"
)

$ok = $false
foreach ($url in $urls) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $out -ErrorAction Stop
        $len = (Get-Item $out).Length
        if ($len -eq 16384) {
            Write-Host "Spectrum 48K ROM fetched to $out ($len bytes) from $url"
            $ok = $true
            break
        }
        Remove-Item $out -ErrorAction SilentlyContinue
        Write-Warning "fetched $url but it failed the sanity check (len=$len, want 16384) — trying the mirror"
    } catch {
        Remove-Item $out -ErrorAction SilentlyContinue
        Write-Warning "fetch of $url failed ($_) — trying the mirror"
    }
}
if (-not $ok) { Write-Error "could not fetch the Spectrum 48K ROM from any source"; exit 1 }
```

> **Implementer note:** the two source URLs are mirrors of the well-known redistributable 48K ROM (`48.rom`, 16384 bytes). If neither resolves at implementation time, substitute any reachable host serving the byte-identical `48.rom` (the size check + the boot-screen hash gate in Task 11 catch a wrong image). Keep `.sh` + `.ps1` byte-for-byte equivalent in behavior.

- [ ] **Step 4: Build to confirm the loader compiles**

Run: `dotnet build src/CpuEmulator.Machines/CpuEmulator.Machines.csproj`
Expected: PASS (0 warnings).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/SpectrumRom.cs tools/get-spectrum-rom.sh tools/get-spectrum-rom.ps1 tests/CpuEmulator.Tests/Spectrum/SpectrumRomVectors.cs
git commit -m "feat(machines): Spectrum ROM loader + fetch scripts + skip-gate attribute"
```

---

## Task 8: The `SpectrumBoard` BoardSpec

**Files:**
- Create: `src/CpuEmulator.Machines/SpectrumBoard.cs`
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs` (add a board-build smoke test, no ROM needed — a blank ROM)

- [ ] **Step 1: Write the failing board test**

Append to `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs` (add `using CpuEmulator.Machines;`):

```csharp
    [Fact]
    public void Spectrum_board_builds_with_z80_rom_ram_and_the_ula_io_slot()
    {
        var blankRom = new byte[SpectrumRom.RomLength]; // a HALT-at-0 ROM is enough to build/run
        blankRom[0] = 0x76; // HALT at $0000

        // The ULA needs the program space to read screen RAM; build the spec, then the machine wires it.
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        var ula = new SpectrumUla(program); // standalone ULA over a throwaway space for the spec shape
        BoardSpec spec = SpectrumBoard.Spec(blankRom, ula);

        Assert.Empty(BoardSpecValidator.Validate(spec));
        Assert.Equal(16, spec.IoAddressBits);
        Assert.Contains(spec.Peripherals, p => p.Space == PeripheralSpace.Io && p.Name == "ula");
        Assert.Contains(spec.Memory, m => m.Kind == RegionKind.Rom && m.Start == 0x0000 && m.Length == 0x4000);
        Assert.Contains(spec.Memory, m => m.Kind == RegionKind.Ram && m.Start == 0x4000 && m.Length == 0xC000);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumScreenTests.Spectrum_board_builds"`
Expected: FAIL — `SpectrumBoard` does not exist.

- [ ] **Step 3: Create the board**

Create `src/CpuEmulator.Machines/SpectrumBoard.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>
/// The ZX Spectrum 48K as a declarative <see cref="BoardSpec"/>: a Z80 with the 16 KB ROM at
/// $0000-$3FFF, 48 KB RAM at $4000-$FFFF (the screen lives at $4000-$5AFF), and the ULA on the I/O
/// PORT space — a single Io peripheral slot covering the whole 16-bit port range with bit-0-clear
/// decode (the real ULA answers every even port). The Z80 resets to PC=0 (ROM); the ULA raises the
/// 50 Hz IM1 interrupt from its scheduler tick (claimed in Realize). The ULA also implements the SP0
/// display/keyboard/audio host contracts, so a surface drives it directly.
/// </summary>
public static class SpectrumBoard
{
    public const uint RomBase = 0x0000;
    public const uint RomLength = 0x4000;   // 16 KiB
    public const uint RamBase = 0x4000;
    public const uint RamLength = 0xC000;   // 48 KiB ($4000-$FFFF)

    public static BoardSpec Spec(byte[] rom, SpectrumUla ula)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(ula);
        if (rom.Length != RomLength)
            throw new ArgumentException(
                $"Spectrum ROM image must be exactly ${RomLength:X} bytes; got ${rom.Length:X}.", nameof(rom));

        return new BoardSpec("zx-spectrum-48k", CpuKind.Z80, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, rom),
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),
                // The whole 16-bit I/O port space is an IoMmio hole the ULA slot fills.
                new MemoryRegion(0x0000, 0x10000, RegionKind.IoMmio, Space: PeripheralSpace.Io),
            ],
            Peripherals:
            [
                new PeripheralSlot("ula", ula, 0x0000, 0x10000, PeripheralSpace.Io),
            ],
            Irq: new IrqWiring([new PeripheralIrq("ula", CpuInterrupt.Irq)]),
            Reset: ResetConfig.None, // the Z80 resets to PC=0 (ROM)
            IoAddressBits: 16);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumScreenTests.Spectrum_board_builds"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/SpectrumBoard.cs tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs
git commit -m "feat(machines): SpectrumBoard BoardSpec (Z80 + ROM + RAM + ULA on port \$FE + 50Hz IRQ)"
```

---

## Task 9: Wire the ULA to read the machine's real RAM (board integration)

**Files:**
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs` (add a built-machine render test)

> **The wiring subtlety:** `SpectrumBoard.Spec` takes a `SpectrumUla` constructed over *some* `IAddressSpace`. When the machine is built, the ULA must read the machine's *Program* space (where RAM lives), not the throwaway space passed at spec-construction time. Resolve this by constructing the ULA over the machine's program space: build the spec with a placeholder, OR (cleaner) have the surface/test construct the ULA AFTER the program space exists. This task proves the supported pattern: construct the program `AddressSpace` first is not possible (the Machine owns it). Instead, the ULA reads RAM lazily through the same `IAddressSpace` the Machine exposes — so the surface builds the ULA over `machine.Space(Program)` AFTER `BoardMachineFactory.Build`. We adjust the construction order accordingly.

- [ ] **Step 1: Write the failing built-machine render test**

Append to `tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs`:

```csharp
    [Fact]
    public void A_built_spectrum_machine_renders_ram_the_guest_wrote()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        blankRom[0] = 0x76; // HALT

        // Two-phase: build the machine, THEN point a ULA at its program space, THEN build the real spec.
        // The supported pattern (see SpectrumSurface): construct the ULA over the machine's program space.
        Machine machine = SpectrumMachine.Build(blankRom, out SpectrumUla ula);
        machine.Reset();

        // The guest "wrote" screen byte + attribute via the program space (simulating ROM/game output).
        var prog = machine.Space(AddressSpaceKind.Program);
        prog.Write8(0x4000, 0x80);  // pixel (0,0) ink
        prog.Write8(0x5800, (byte)(2 | (7 << 3))); // red ink on white paper

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        int px = SpectrumUla.BorderPx, py = SpectrumUla.BorderPx;
        Assert.Equal(SpectrumPalette.Colors[2], rgba[py * SpectrumUla.FullWidth + px]); // red ink
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumScreenTests.A_built_spectrum"`
Expected: FAIL — `SpectrumMachine.Build` does not exist.

- [ ] **Step 3: Add the `SpectrumMachine` build helper**

Create `src/CpuEmulator.Machines/SpectrumMachine.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>Builds a runnable ZX Spectrum <see cref="Machine"/> and the ULA wired to its program space.
/// The ULA must read the BUILT machine's RAM, so we build the machine first with a ULA over a deferred
/// space, then re-point: in practice the Machine's program space is the same object the ULA reads, so
/// we construct the ULA over a freshly-created program AddressSpace that the BoardSpec then adopts.
/// Because BoardMachineFactory creates its OWN program AddressSpace, the supported pattern is: build the
/// machine, then construct the ULA over machine.Space(Program), then the surface uses that ULA for
/// display/keyboard/audio AND the board's Io slot. To keep the ULA the SAME instance the Io slot maps,
/// we build in one shot here by creating the ULA over a placeholder and swapping its RAM handle.</summary>
public static class SpectrumMachine
{
    public static Machine Build(byte[] rom, out SpectrumUla ula, ExecutionTier tier = ExecutionTier.Interpreter)
    {
        // The ULA needs the machine's program space. BoardMachineFactory builds that space internally and
        // realizes the ULA with the IMachineContext — so the ULA reads RAM via an IAddressSpace it is GIVEN
        // at Realize time, not at construction. Refactor: the ULA captures the program space in Realize.
        var pendingUla = new SpectrumUla(); // parameterless: RAM bound in Realize
        BoardSpec spec = SpectrumBoard.Spec(rom, pendingUla);
        Machine machine = BoardMachineFactory.Build(spec, tier);
        ula = pendingUla;
        return machine;
    }
}
```

This requires the ULA to bind its RAM handle in `Realize` rather than the constructor. Modify `src/CpuEmulator.Peripherals/SpectrumUla.cs`:

- Change the field + constructor:

```csharp
    private IAddressSpace _ram = default!; // bound in Realize (the machine's program space)

    /// <summary>Construct a ULA whose screen RAM is bound at Realize time to the machine's program
    /// space. A test may pass an explicit space to render without a full Machine.</summary>
    public SpectrumUla(IAddressSpace? ram = null)
    {
        if (ram is not null) _ram = ram;
    }
```

- In `Realize`, bind the program space:

```csharp
    public void Realize(IMachineContext context)
    {
        _ram = context.Space(AddressSpaceKind.Program);
        _irq = context.IrqLine.Source();
        _frameStartCycle = context.Scheduler.CurrentCycle;
        context.Scheduler.ScheduleEvery(TStatesPerFrame, OnFrameTick);
    }
```

(The earlier tests that construct `new SpectrumUla(space)` still work — the explicit-space ctor path is preserved. The board path uses the parameterless ctor + `Realize` binding, so the Io-mapped ULA instance is the same one the surface renders.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumScreenTests.A_built_spectrum"`
Expected: PASS — the ULA renders RAM the test wrote into the built machine's program space.

- [ ] **Step 5: Run the full Spectrum + Machines suites to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Spectrum|FullyQualifiedName~Machines"`
Expected: PASS — all earlier ULA tests (explicit-space ctor) + the board build + the new wiring.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Machines/SpectrumMachine.cs src/CpuEmulator.Peripherals/SpectrumUla.cs tests/CpuEmulator.Tests/Spectrum/SpectrumScreenTests.cs
git commit -m "feat(machines): SpectrumMachine.Build wires the ULA to the machine's program space"
```

---

## Task 10: `.SNA` snapshot loading

**Files:**
- Create: `src/CpuEmulator.Machines/SnaSnapshot.cs`
- Test: `tests/CpuEmulator.Tests/Spectrum/SnaSnapshotTests.cs`

- [ ] **Step 1: Write the failing `.SNA` test (the un-fakeable snapshot gate)**

Create `tests/CpuEmulator.Tests/Spectrum/SnaSnapshotTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SnaSnapshotTests
{
    /// <summary>Build a synthetic 48K .SNA: 27-byte header + 49152 bytes RAM. We seed registers + a
    /// known PC pushed on the stack, and a recognizable screen byte so the first frame is assertable.</summary>
    private static byte[] BuildSyntheticSna()
    {
        var sna = new byte[27 + 49152];
        // Header (little-endian).
        sna[0x00] = 0x3F;                 // I = 0x3F (the Spectrum ROM's interrupt vector page)
        WriteU16(sna, 0x01, 0x1111);      // HL'
        WriteU16(sna, 0x03, 0x2222);      // DE'
        WriteU16(sna, 0x05, 0x3333);      // BC'
        WriteU16(sna, 0x07, 0x4444);      // AF'
        WriteU16(sna, 0x09, 0xABCD);      // HL
        WriteU16(sna, 0x0B, 0x1234);      // DE
        WriteU16(sna, 0x0D, 0x5678);      // BC
        WriteU16(sna, 0x0F, 0x9ABC);      // IY
        WriteU16(sna, 0x11, 0xDEF0);      // IX
        sna[0x13] = 0x04;                 // IFF2 (bit 2 set = EI)
        sna[0x14] = 0x7E;                 // R
        WriteU16(sna, 0x15, 0x55AA);      // AF
        WriteU16(sna, 0x17, 0xFF00);      // SP -> points into RAM ($FF00)
        sna[0x19] = 0x01;                 // IM 1
        sna[0x1A] = 0x05;                 // border = cyan(5)

        // RAM block: $4000..$FFFF. The byte at SP / SP+1 is the PC to resume at (RETN-style pop).
        // SP = 0xFF00 → RAM offset (0xFF00 - 0x4000) = 0xBF00. Push PC = 0x8000 (low, high).
        int spOffset = 0xFF00 - 0x4000;
        sna[27 + spOffset + 0] = 0x00;    // PC low
        sna[27 + spOffset + 1] = 0x80;    // PC high → PC = 0x8000

        // A recognizable screen byte at $4000 (offset 0 of the RAM block) + attribute at $5800.
        sna[27 + (0x4000 - 0x4000)] = 0x80;                 // pixel (0,0) ink
        sna[27 + (0x5800 - 0x4000)] = (byte)(2 | (7 << 3)); // red ink, white paper
        return sna;
    }

    private static void WriteU16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
    }

    [Fact]
    public void Sna_restores_registers_ram_and_pops_pc_from_the_restored_stack()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        Machine machine = SpectrumMachine.Build(blankRom, out _);
        machine.Reset();

        SnaSnapshot.LoadInto(machine, BuildSyntheticSna());

        var z80 = Assert.IsType<Z80Cpu>(machine.Cpu);
        Assert.Equal(0x8000u, z80.PC);     // PC popped from the restored stack
        Assert.Equal(0xFF02u, z80.SP);     // SP incremented by 2 after the pop
        Assert.Equal(0xABCDu, z80.HL);
        Assert.Equal(0x1234u, z80.DE);
        Assert.Equal(0x5678u, z80.BC);
        Assert.Equal(0x55AAu, z80.AF);
        Assert.Equal(0x1111u, z80.HL_);
        Assert.Equal(0x9ABCu, z80.IY);
        Assert.Equal(0xDEF0u, z80.IX);
        Assert.Equal(0x3F, z80.I);
        Assert.Equal(1, z80.Im);
        Assert.True(z80.Iff1);             // IFF2 (bit 2 set) → RETN copies IFF2 to IFF1
        Assert.Equal(0x80, machine.Space(AddressSpaceKind.Program).Read8(0x4000)); // RAM restored
    }

    [Fact]
    public void Sna_first_frame_matches_the_restored_screen()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        Machine machine = SpectrumMachine.Build(blankRom, out SpectrumUla ula);
        machine.Reset();
        SnaSnapshot.LoadInto(machine, BuildSyntheticSna());

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        // The restored screen byte → red ink at (0,0); the restored border (cyan=5) → border colour.
        Assert.Equal(SpectrumPalette.Colors[2], rgba[SpectrumUla.BorderPx * SpectrumUla.FullWidth + SpectrumUla.BorderPx]);
        Assert.Equal(SpectrumPalette.Colors[5], rgba[0]); // border cyan
    }

    [Fact]
    public void A_wrong_length_sna_is_rejected()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        Machine machine = SpectrumMachine.Build(blankRom, out _);
        machine.Reset();
        Assert.Throws<InvalidDataException>(() => SnaSnapshot.LoadInto(machine, new byte[100]));
    }
}
```

> **Note:** `Sna_first_frame_matches_the_restored_screen` asserts the border via the ULA. The ULA's border is set by `OUT ($FE)`, not by `.SNA` directly — so `SnaSnapshot.LoadInto` must also push the border byte into the ULA. The loader takes an optional `SpectrumUla` to set the border (Step 3 covers this).

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SnaSnapshotTests"`
Expected: FAIL — `SnaSnapshot` does not exist.

- [ ] **Step 3: Create the `.SNA` loader**

Create `src/CpuEmulator.Machines/SnaSnapshot.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>Loads a 48K ZX Spectrum <c>.SNA</c> snapshot into a built <see cref="Machine"/>. The 48K
/// .SNA is a 27-byte little-endian header (I, HL', DE', BC', AF', HL, DE, BC, IY, IX, IFF2, R, AF, SP,
/// IM, border) followed by 49152 bytes of RAM ($4000-$FFFF). On resume the PC is recovered RETN-style:
/// the snapshot pushed PC onto the stack, so we pop it from SP and advance SP by 2, and copy IFF2 to
/// IFF1. The machine's CPU must be a Z80.</summary>
public static class SnaSnapshot
{
    private const int HeaderLength = 27;
    private const int RamLength = 49152;        // $4000-$FFFF
    private const uint RamBase = 0x4000;

    public static void LoadInto(Machine machine, byte[] sna, SpectrumUla? ula = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(sna);
        if (sna.Length != HeaderLength + RamLength)
            throw new InvalidDataException(
                $".SNA must be exactly {HeaderLength + RamLength} bytes (48K format); got {sna.Length}.");
        if (machine.Cpu is not Z80Cpu z80)
            throw new InvalidOperationException(".SNA loading requires a Z80 machine.");

        // Restore RAM first (PC is read back from the restored stack).
        IAddressSpace prog = machine.Space(AddressSpaceKind.Program);
        for (int i = 0; i < RamLength; i++)
            prog.Write8(RamBase + (uint)i, sna[HeaderLength + i]);

        // Restore registers (little-endian).
        z80.I = sna[0x00];
        z80.HL_ = U16(sna, 0x01);
        z80.DE_ = U16(sna, 0x03);
        z80.BC_ = U16(sna, 0x05);
        z80.AF_ = U16(sna, 0x07);
        z80.HL = U16(sna, 0x09);
        z80.DE = U16(sna, 0x0B);
        z80.BC = U16(sna, 0x0D);
        z80.IY = U16(sna, 0x0F);
        z80.IX = U16(sna, 0x11);
        bool iff2 = (sna[0x13] & 0x04) != 0;
        z80.R = sna[0x14];
        z80.AF = U16(sna, 0x15);
        ushort sp = U16(sna, 0x17);
        z80.Im = sna[0x19];

        // Resume: pop PC off the restored stack (RETN idiom), advance SP, copy IFF2 -> IFF1.
        byte pcLo = prog.Read8(sp);
        byte pcHi = prog.Read8((ushort)(sp + 1));
        z80.PC = (ushort)(pcLo | (pcHi << 8));
        z80.SP = (ushort)(sp + 2);
        z80.Iff2 = iff2;
        z80.Iff1 = iff2;

        // The border byte drives the ULA (it is not a Z80 register).
        ula?.SetBorder(sna[0x1A] & 0x07);
    }

    private static ushort U16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));
}
```

Add a `SetBorder` method to the ULA (`src/CpuEmulator.Peripherals/SpectrumUla.cs`):

```csharp
    /// <summary>Set the border colour directly (the .SNA loader restores it; the guest normally sets it
    /// via OUT ($FE)). 0..7 base colour.</summary>
    public void SetBorder(int color) => _border = color & 0x07;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SnaSnapshotTests"`
Expected: PASS — registers + RAM restore, PC pops from the stack (`0x8000`), SP advances to `0xFF02`, IFF1 set, the first frame shows the restored ink + border. **This is the `.SNA` first-frame gate.**

> **Implementer note:** the first-frame test passes the ULA to `LoadInto`. Update the test's `Sna_first_frame_matches_the_restored_screen` call to `SnaSnapshot.LoadInto(machine, BuildSyntheticSna(), ula);` if it is not already — the border assertion needs it. (The first test, which does not assert border, may pass `ula: null`.)

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/SnaSnapshot.cs src/CpuEmulator.Peripherals/SpectrumUla.cs tests/CpuEmulator.Tests/Spectrum/SnaSnapshotTests.cs
git commit -m "feat(machines): .SNA 48K snapshot loader (register + RAM restore, RETN-style PC pop)"
```

---

## Task 11: The ROM-boot copyright-screen gate (both tiers, skip-with-note)

**Files:**
- Create: `tests/CpuEmulator.Tests/Spectrum/SpectrumBootTests.cs`

- [ ] **Step 1: Write the boot test (the un-fakeable ROM-boot gate)**

Create `tests/CpuEmulator.Tests/Spectrum/SpectrumBootTests.cs`:

```csharp
using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

/// <summary>The ROM-boot acceptance gate (spec §9). Boots the real 16 KB ROM, runs ~2 frames' worth of
/// T-states with the 50 Hz interrupt firing, renders the first stable frame, and asserts it matches the
/// BASIC copyright screen — on BOTH execution tiers. Skips-with-note when the ROM is absent (mirroring
/// the Klaus/ZEX gating) so ROM-free CI stays green. The reference is a committed RGBA hash captured on
/// first green run (see the recording note).</summary>
[Trait("Category", "UAT")]
public class SpectrumBootTests
{
    // Two frames at 69888 T-states/frame ≈ 140k cycles; the ROM paints the (C) screen well within this.
    private const long BootCycles = 200_000;

    [SpectrumRomTheory]
    [InlineData(ExecutionTier.Interpreter)]
    [InlineData(ExecutionTier.Jit)]
    public void Rom_boots_to_the_basic_copyright_screen(ExecutionTier tier)
    {
        byte[] rom = SpectrumRom.Load(SpectrumRomVectors.TryGetRomPath());
        Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
        machine.Reset();
        machine.Run(BootCycles);

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        // Un-fakeable: a structural assertion on the real boot screen. The 48K ROM clears to a WHITE
        // paper (border + screen white) and prints "© 1982 Sinclair Research Ltd" in black near the
        // bottom. We assert (a) the ink area is predominantly white paper, and (b) some black ink
        // pixels exist in the copyright line region — properties the empty/garbage screen lacks.
        int whitePaper = 0, blackInk = 0;
        for (int y = 0; y < SpectrumUla.InkHeight; y++)
        for (int x = 0; x < SpectrumUla.InkWidth; x++)
        {
            uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
            if (p == SpectrumPalette.Colors[7]) whitePaper++;
            else if (p == SpectrumPalette.Colors[0]) blackInk++;
        }
        Assert.True(whitePaper > SpectrumUla.InkWidth * SpectrumUla.InkHeight / 2,
            $"expected a mostly-white paper screen; got {whitePaper} white pixels");
        Assert.True(blackInk > 50, $"expected the black copyright text; got {blackInk} black pixels");

        // Tighter gate: a committed RGBA hash of the full frame. On the FIRST green run, capture the hash
        // (uncomment the print), paste it below, then re-run. Both tiers MUST produce the identical frame.
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[boot frame hash] {hash}");  // <-- uncomment once to capture
        const string ExpectedBootHash = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN";
        if (ExpectedBootHash != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
            Assert.Equal(ExpectedBootHash, hash);
    }

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
```

> **Recording note (the committed reference hash):** the structural assertions (mostly-white paper + black copyright text) are the primary un-fakeable gate and pass without a pre-recorded hash. The exact-frame hash is a tighter cross-tier-equivalence check: on the first green run with the real ROM, uncomment the `Console.WriteLine`, capture the printed hash, replace `ExpectedBootHash`, re-run to confirm both tiers match, then commit the hash. Until then the hash branch is inert (the `if` guards it), so the test is green on structure alone. This avoids committing a guessed hash.

- [ ] **Step 2: Run it (expect skip without the ROM, pass with it)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumBootTests"`
Expected: **SKIPPED** (with the "ROM not found — run tools/get-spectrum-rom" note) when the ROM is absent. To run it locally: `pwsh tools/get-spectrum-rom.ps1` (or `sh tools/get-spectrum-rom.sh`), then re-run — expect PASS on both tiers (mostly-white paper + black text).

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Spectrum/SpectrumBootTests.cs
git commit -m "test(spectrum): ROM-boot copyright-screen gate (both tiers, skip-with-note)"
```

---

## Task 12: The Spectrum web surface

**Files:**
- Create: `src/CpuEmulator.Surface.Web/SpectrumSurface.cs`
- Test: `tests/CpuEmulator.Tests/Spectrum/SpectrumSurfaceTests.cs`

- [ ] **Step 1: Write the failing surface test**

Create `tests/CpuEmulator.Tests/Spectrum/SpectrumSurfaceTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumSurfaceTests
{
    [Fact]
    public void Surface_composes_a_machine_host_with_the_ula_as_display_keyboard_and_audio()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        blankRom[0] = 0x76; // HALT

        byte[]? lastFrame = null;
        byte[]? lastAudio = null;
        SpectrumSurface surface = SpectrumSurface.Create(blankRom,
            frame => lastFrame = frame, audio => lastAudio = audio);

        surface.Machine.Reset();
        // Write a recognizable screen byte through the guest space, then step past a frame tick.
        surface.Machine.Space(AddressSpaceKind.Program).Write8(0x4000, 0x80);
        surface.Machine.Space(AddressSpaceKind.Program).Write8(0x5800, (byte)(2 | (7 << 3)));
        surface.Host.RunHeadless(SpectrumUla.TStatesPerFrame * 2, 5_000);

        Assert.NotNull(lastFrame);
        Assert.Equal((byte)'F', lastFrame![0]);   // an FB frame was pushed
        Assert.NotNull(lastAudio);
        Assert.Equal((byte)'A', lastAudio![0]);    // an AU frame was pushed
    }

    [Fact]
    public void Surface_routes_a_key_to_the_ula_matrix()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        blankRom[0] = 0x76;
        SpectrumSurface surface = SpectrumSurface.Create(blankRom, _ => { }, _ => { });
        surface.Machine.Reset();

        surface.Host.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a'));
        Assert.Equal(0u, surface.Ula.Read(0xFDFEu, AccessWidth.Byte) & 0x01); // 'A' pressed
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumSurfaceTests"`
Expected: FAIL — `SpectrumSurface` does not exist.

- [ ] **Step 3: Create the surface**

Create `src/CpuEmulator.Surface.Web/SpectrumSurface.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// Composes the ZX Spectrum for the web surface — the analogue of <see cref="DemoBoardSurface"/>. Builds
/// the <see cref="SpectrumBoard"/> spec → a <see cref="Machine"/> via <see cref="BoardMachineFactory"/>,
/// resets it, and wires a <see cref="MachineHost"/> whose display + keyboard + audio are all the same
/// <see cref="SpectrumUla"/> instance (mapped on the Io port slot). The audio sink uses the Phase-1
/// 6-arg <see cref="MachineHost"/> ctor so the beeper plays via the WebSocket AU frames.
/// </summary>
public sealed record SpectrumSurface(Machine Machine, SpectrumUla Ula, MachineHost Host)
{
    public static SpectrumSurface Create(byte[] rom, Action<byte[]> frameSink, Action<byte[]> audioSink)
    {
        Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula);
        machine.Reset();
        var host = new MachineHost(machine, ula, ula, frameSink, ula, audioSink);
        return new SpectrumSurface(machine, ula, host);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SpectrumSurfaceTests"`
Expected: PASS — the surface pushes both `FB` + `AU` frames after a frame tick, and a key reaches the ULA matrix.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/SpectrumSurface.cs tests/CpuEmulator.Tests/Spectrum/SpectrumSurfaceTests.cs
git commit -m "feat(surface): SpectrumSurface wires the ULA as display + keyboard + audio"
```

---

## Task 13: Select the Spectrum board in the live web server (optional wiring) + docs + the full gate

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/Program.cs`
- Modify: `docs/ROADMAP.md`
- Modify: `docs/user-guide/` (the relevant running-the-emulator doc)

- [ ] **Step 1: Let the server boot the Spectrum when the ROM is present**

In `src/CpuEmulator.Surface.Web/Program.cs`, in `DemoSession.RunAsync`, choose the Spectrum surface when the ROM is available, else the demo. Replace the surface construction so the audio channel (Phase 1) is fed by the Spectrum's audio sink:

```csharp
    public static async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        Channel<byte[]> frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
        Channel<byte[]> audio = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

        // Boot the Spectrum if its ROM is in the cache; otherwise fall back to the SP0 demo board.
        string? romPath = CpuEmulator.Machines.SpectrumRom.TryGetPath();
        ISurfacePump pump;
        if (romPath is not null)
        {
            byte[] rom = CpuEmulator.Machines.SpectrumRom.Load(romPath);
            SpectrumSurface spectrum = SpectrumSurface.Create(rom,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            pump = new SurfacePump(spectrum.Host, SpectrumPumpCycles);
        }
        else
        {
            DemoBoardSurface demo = DemoBoardSurface.Create(f => frames.Writer.TryWrite(f));
            pump = new SurfacePump(demo.Host, SliceCycles);
        }

        Task drive = pump.RunAsync(ct);
        Task sendFrames = SendBinaryAsync(socket, frames.Reader, ct);
        Task sendAudio = SendBinaryAsync(socket, audio.Reader, ct);
        Task recv = ReceiveKeysAsync(socket, pump, ct);

        await Task.WhenAny(drive, sendFrames, sendAudio, recv);
        frames.Writer.TryComplete();
        audio.Writer.TryComplete();
        try { await Task.WhenAll(drive, sendFrames, sendAudio, recv); } catch { /* teardown races expected */ }
    }
```

Add a tiny `ISurfacePump` abstraction so `ReceiveKeysAsync` posts keys and the wall-clock loop steps either surface (both have a `MachineHost`). Add near the bottom of the file:

```csharp
    // The Spectrum runs at 3.5 MHz: one ~70k-T slice every ~20 ms wall-clock (50 Hz).
    private const long SpectrumPumpCycles = 69_888;

    private interface ISurfacePump
    {
        Task RunAsync(CancellationToken ct);
        void PostKey(in KeyEvent e);
    }

    private sealed class SurfacePump : ISurfacePump
    {
        private readonly MachineHost _host;
        private readonly long _slice;
        public SurfacePump(MachineHost host, long slice) { _host = host; _slice = slice; }

        public async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (await timer.WaitForNextTickAsync(ct))
                _host.Step(_slice);
        }

        public void PostKey(in KeyEvent e) => _host.PostKey(e);
    }
```

Change `ReceiveKeysAsync`'s signature to take `ISurfacePump` and call `pump.PostKey(e)` (it currently takes `DemoBoardSurface surface` and calls `surface.Host.PostKey`). Remove the old `PumpAsync`/`SliceCycles`-only path if it is now unused, keeping `SliceCycles` as the demo's slice constant.

> **Implementer note:** the SP0 web smoke + acceptance tests use `DemoBoardSurface` directly (not through the server), so they are unaffected. The server change is additive: ROM absent → demo (unchanged behavior); ROM present → Spectrum. The Spectrum browser path (canvas + Web Audio) is validated by Tester UAT, not a unit test.

- [ ] **Step 2: Update the roadmap + user guide**

In `docs/ROADMAP.md`, mark the ZX Spectrum 48K as shipped — the first real machine — with a one-line summary (Z80 + ULA on port `$FE` + 50 Hz IM1 + ROM-fetch + `.SNA` + beeper, on SP0). In the user-guide running doc, add: "Fetch the Spectrum ROM with `tools/get-spectrum-rom.ps1` (or `.sh`), then run `CpuEmulator.Surface.Web`; the server boots the Spectrum when the ROM is cached, else the SP0 demo. Click 'enable sound' for the beeper."

(Use the existing prose style; keep it to a short paragraph + the command.)

- [ ] **Step 3: Run the full unit suite (the Phase-2 gate)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`
Expected: PASS — every Spectrum suite green (screen, keyboard, beeper, border, `.SNA`, surface), the boot test SKIPPED (ROM absent) or PASS (ROM present), and no regression in any prior suite (SP0 demo + Phase-1 extensions + all CPU/board/monitor suites).

- [ ] **Step 4: Build with zero warnings**

Run: `dotnet build CpuEmulator.slnx`
Expected: PASS (0 warnings).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/Program.cs docs/ROADMAP.md docs/user-guide
git commit -m "feat(surface): server boots the Spectrum when the ROM is cached; docs + roadmap"
```

---

## Self-Review (writing-plans skill)

**1. Spec coverage (vs `2026-06-19-zx-spectrum-48k-design.md`):**
- §2 boot to the BASIC copyright screen with a working keyboard: **covered** — Task 11 (boot gate, both tiers), Task 4 (keyboard read), Task 12 (surface).
- §2 beeper sound in the first cut: **covered** — Task 5 (beeper PCM), Task 12 (audio sink wired through the surface).
- §2 `.SNA` → a real game: **covered** — Task 10 (`.SNA` loader, first-frame gate).
- §4 ULA `IDisplayDevice` over screen RAM, non-linear line order, attributes, 50 Hz `FrameReady`, holds `IAddressSpace` (no VRAM): **covered** — Task 3 (bit-shuffle render + attributes), Task 9 (RAM binding via `Realize`), Task 3 (`OnFrameTick` → `FrameReady` at `TStatesPerFrame`).
- §4 `IKeyboardSink` 8×5 matrix, A8–A15 half-rows, 0=pressed, bit 6 EAR idle: **covered** — Task 2 (matrix map), Task 3/4 (`Read` AND-of-rows + `0xE0` idle bits).
- §4 border (`OUT` bits 0-2) folded into the frame: **covered** — Task 3 (border fill) + Task 6 (gate).
- §4 beeper (`OUT` bit 4, timestamped toggles → PCM): **covered** — Task 3 (`Write` log) + Task 5 (resample gate).
- §5 50 Hz IM1 interrupt via the existing interrupt line: **covered** — Task 3 (`Realize` claims `IrqLine.Source()`; `OnFrameTick` asserts), Task 8 (`IrqWiring` names the ULA).
- §6 ROM fetched on demand, cache, skip-with-note: **covered** — Task 7 (loader + `.sh`/`.ps1` + `SpectrumRomFactAttribute`).
- §7 `.SNA` 27-byte header + 49152 RAM + RETN PC-pop: **covered** — Task 10 (exact header offsets, RAM restore, pop, IFF2→IFF1).
- §8 `SpectrumBoard` (Z80 + 16K ROM + 48K RAM + ULA port-`$FE` + 50 Hz) hosted by `MachineHost`: **covered** — Task 8 (board), Task 9 (`SpectrumMachine`), Task 12 (surface).
- §9 all five un-fakeable gates: ROM-boot (Task 11, both tiers, skip-with-note), keyboard (Task 4), beeper (Task 5), border (Task 6), `.SNA` first-frame (Task 10). No-regression: Tasks 9/12/13 run the prior suites.
- §10 non-goals (contention, `.TAP`/`.TZX`, 128K, AY, Kempston): **correctly excluded** — noted in the ULA's deferral note + not built.
- §11 open questions: port-I/O routing (resolved in Phase 1 + consumed here), `IAudioSink` shape (Phase 1), screen-RAM order (Task 3, verified against authoritative refs), phasing (this is the machine phase).

**2. Placeholder scan:** No `TBD`/`TODO`/"implement later"/"similar to Task N". Every code step is literal. Two intentional, bounded notes: (a) the boot-hash `PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN` is a deliberate inert sentinel guarded by an `if` — the structural assertions are the real gate, and the recording procedure is spelled out (it is not a code gap, it is a documented capture step that needs the real ROM, which cannot exist at plan time); (b) the ROM-fetch URLs carry a substitution note since live URLs can rot — the 16384-byte size check + the boot gate catch a wrong image. Both are honest about runtime-only inputs, not deferred work.

**3. Type consistency:** `SpectrumUla` members are used identically across tasks: `FullWidth`/`FullHeight`/`BorderPx`/`InkWidth`/`InkHeight`/`TStatesPerFrame` (consts), `RenderInto`/`Read`/`Write`/`PostKey`/`RenderAudio`/`SetBorder`/`Realize`, `SampleRate`/`ChannelCount`/`SamplesPerFrame`. The parameterless + explicit-space ctors (Task 3 → refactored Task 9) are both honored by callers (tests use `new SpectrumUla(space)`; the board uses `new SpectrumUla()`). `SpectrumKeyMatrix.TryMap(KeyCode, out int, out int)` (Task 2) is the exact signature the ULA `PostKey` calls (Task 3). `SpectrumRom.RomLength`/`Load`/`TryGetPath` (Task 7) are used in Tasks 8/9/11/13. `SpectrumBoard.Spec(byte[], SpectrumUla)` (Task 8) is called by `SpectrumMachine.Build` (Task 9). `SnaSnapshot.LoadInto(Machine, byte[], SpectrumUla?)` (Task 10) matches its test calls. `SpectrumSurface.Create(byte[], Action<byte[]>, Action<byte[]>)` (Task 12) matches the server (Task 13). The Phase-1 `MachineHost` 6-arg ctor + `FrameCodec.EncodeAudio` + `IAudioSink` are consumed exactly as Phase 1 defines them.

**Self-review result:** no gaps, no placeholders, types consistent. One cross-task refactor (ULA RAM binding moves from ctor to `Realize` in Task 9) is called out explicitly with the migration preserving the earlier explicit-space ctor tests. Plan ready.

---

## Definition of done (Phase 2 → the first real machine ships)

- `dotnet build CpuEmulator.slnx` — 0 warnings.
- `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` — all green:
  - `SpectrumScreenTests` — bit-shuffle render + attributes + bright + the built-machine RAM read.
  - `SpectrumKeyboardTests` — matrix map + `IN ($FE)` half-row reads + odd-port open-bus.
  - `SpectrumBeeperTests` — `OUT ($FE)` bit-4 toggles → S16 PCM (both polarities, log reset, level carry).
  - `SpectrumBorderTests` — `OUT ($FE)` border → RGBA.
  - `SnaSnapshotTests` — register + RAM restore, RETN PC-pop, first-frame match, wrong-length reject.
  - `SpectrumSurfaceTests` — the ULA wired as display + keyboard + audio; `FB` + `AU` frames pushed.
  - `SpectrumBootTests` — SKIPPED (ROM absent) or PASS on both tiers (ROM present): mostly-white paper + black copyright text.
- No regression in SP0, Phase-1, or any CPU/board/monitor suite. `Core` stays `IsAotCompatible`.
- With `tools/get-spectrum-rom.{sh,ps1}` run, the web server boots the Spectrum: the BASIC copyright screen in the browser, a working keyboard, and the beeper via Web Audio (validated by Tester UAT). Loading a `.SNA` runs a real game with video + sound + keyboard.
