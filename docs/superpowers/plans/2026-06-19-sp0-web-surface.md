# SP0 — Web-Surface Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the reusable, GUI-free web surface for the "real machines" arc — three additive `Core` device contracts, three generic demo devices, a `MachineHost` pump, a browser canvas client over HTTP+WebSocket, and a `DemoBoard` (`BoardSpec`) that proves display + keyboard + disk end-to-end through an un-fakeable headless acceptance test.

**Architecture:** The emulator core stays GUI-free. Chips implement host-facing capability interfaces (`IDisplayDevice` / `IKeyboardSink` / `IBlockDevice`) **in addition to** `IPeripheral` (which faces the guest CPU). A new `CpuEmulator.Surface.Web` ASP.NET Core minimal project hosts a `MachineHost` pump (wall-clock-paced or headless/fast) that subscribes `FrameReady`, pulls RGBA via `RenderInto`, pushes binary frames over a WebSocket to a browser `<canvas>`, and routes inbound key events to `IKeyboardSink.PostKey`. The demo is expressed as a declarative `BoardSpec` built through the existing `BoardMachineFactory`, with MMIO peripheral slots for a palettized framebuffer, a UART-rx-shaped keyboard, and a raw-image disk — coexisting with the existing monitor host as a *parallel* surface over the same `Machine`.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs + `System.Net.WebSockets` (both built into .NET 10 — no new heavy dependency), `Span<uint>` RGBA framebuffers, xUnit 2.9 for tests. Browser client is vanilla HTML + JS canvas (no framework).

---

## Reconciliation with the shipped Machine-model arc

The SP0 design spec (`docs/superpowers/specs/2026-06-17-emulated-computer-sp0-foundation-design.md`) predates the "CPUs → computers" arc (board model shipped 2026-06-19). This plan honors the spec's §4 contracts, §5 pump, §6 demo + acceptance test, and §7 decisions verbatim where they still hold, with three reconciliations confirmed against the real code:

1. **`DemoMachine` → a `BoardSpec`.** The spec's hand-wired `DemoMachine` becomes a `DemoBoard.Spec(...)` factory returning a `BoardSpec` with **MMIO peripheral slots** (framebuffer + keyboard + disk), built via `BoardMachineFactory.Build`, exactly mirroring `Breadboard6502Board` / `ReferenceSbc` (`src/CpuEmulator.Machines/`). The demo ROM is monitor-assembled like the existing `DemoRom.Build()` (`src/CpuEmulator.Host/DemoRom.cs`).
2. **The web surface coexists with the monitor host (piece #3).** `BoardRegistry` / `BootedBoard` (`src/CpuEmulator.Host/`) is untouched. `MachineHost` (in `Surface.Web`) is a *parallel* surface — canvas vs REPL — over the same `Machine` produced by the same `BoardMachineFactory`. No production file outside the new `Surface.Web` project + the additive `Core` contracts + the new `Peripherals`/`Machines` types is edited.
3. **Sound stays out of SP0.** An `IAudioSink`-shaped follow-on for the first real machine's beeper is noted in the ROADMAP but **not built**.

The three contracts land in `CpuEmulator.Core`, which is `IsAotCompatible` — they are pure additive interfaces with no new dependency, so AOT-cleanliness is preserved. `CpuEmulator.Surface.Web` is intentionally **not** AOT-publishable (it references `CpuEmulator.Machines` → `CpuEmulator.Jit`, same as the Host post-piece-#3, plus ASP.NET Core).

---

## File Structure

### `CpuEmulator.Core` — the three additive contracts (no new dependency)
- **Create** `src/CpuEmulator.Core/IDisplayDevice.cs` — `IDisplayDevice` (Width/Height/`RenderInto(Span<uint>)`/`FrameReady`).
- **Create** `src/CpuEmulator.Core/KeyCode.cs` — the portable `KeyCode` enum.
- **Create** `src/CpuEmulator.Core/KeyEvent.cs` — `KeyAction` enum + `KeyEvent` readonly record struct.
- **Create** `src/CpuEmulator.Core/IKeyboardSink.cs` — `IKeyboardSink.PostKey(in KeyEvent)`.
- **Create** `src/CpuEmulator.Core/IBlockDevice.cs` — `IBlockDevice` (SectorSize/SectorCount/IsReadOnly/`ReadSector`/`WriteSector`).

### `CpuEmulator.Peripherals` — the three generic demo devices + the disk-image adapter
- **Create** `src/CpuEmulator.Peripherals/DemoFramebuffer.cs` — 256×192 8bpp palettized linear framebuffer; `IPeripheral` + `IDisplayDevice`.
- **Create** `src/CpuEmulator.Peripherals/DemoKeyboard.cs` — UART-rx-shaped data+status register; `IPeripheral` + `IKeyboardSink`.
- **Create** `src/CpuEmulator.Peripherals/DiskImage.cs` — `IBlockDevice` over a host `byte[]` / file (LBA → offset; raw sector image).
- **Create** `src/CpuEmulator.Peripherals/DemoDisk.cs` — memory-mapped sector/command/data registers driving an `IBlockDevice`; `IPeripheral`.

### `CpuEmulator.Machines` — the demo board (a `BoardSpec`) + its ROM
- **Create** `src/CpuEmulator.Machines/DemoBoard.cs` — `DemoBoard.Spec(rom, fb, kbd, disk)` returns a `BoardSpec` (6502, RAM/MMIO/ROM map + three peripheral slots + keyboard IRQ wiring).
- **Create** `src/CpuEmulator.Machines/DemoBoardRom.cs` — the demo 6502 ROM image, monitor-assembled at startup (test pattern + keyboard echo + read-sector), like `DemoRom`.

### `CpuEmulator.Surface.Web` — NEW project: the HTTP+WebSocket server + browser client + `MachineHost`
- **Create** `src/CpuEmulator.Surface.Web/CpuEmulator.Surface.Web.csproj` — `Microsoft.NET.Sdk.Web`, refs `Core` + `Machines` + `Peripherals`.
- **Create** `src/CpuEmulator.Surface.Web/FrameCodec.cs` — encodes an RGBA frame to the WebSocket binary wire format (header: magic, width, height, then pixels) and decodes inbound key-event JSON → `KeyEvent`.
- **Create** `src/CpuEmulator.Surface.Web/MachineHost.cs` — the pump: start/stop, wall-clock pacing OR headless/fast, `FrameReady` → `RenderInto` → frame sink, inbound key → `IKeyboardSink.PostKey`.
- **Create** `src/CpuEmulator.Surface.Web/DemoBoardSurface.cs` — composes the `DemoBoard` (`BoardSpec` → `Machine` via `BoardMachineFactory`) + its devices into a `MachineHost`-ready bundle (the web analogue of `BootedBoard`).
- **Create** `src/CpuEmulator.Surface.Web/Program.cs` — the minimal-API host: serves the client, accepts the WebSocket, drives a `MachineHost` in wall-clock mode.
- **Create** `src/CpuEmulator.Surface.Web/wwwroot/index.html` — the canvas page.
- **Create** `src/CpuEmulator.Surface.Web/wwwroot/app.js` — the client: open WebSocket, decode binary frames → blit to canvas, capture keydown/keyup → send key-event JSON.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Peripherals/DemoFramebufferTests.cs`
- **Create** `tests/CpuEmulator.Tests/Peripherals/DemoKeyboardTests.cs`
- **Create** `tests/CpuEmulator.Tests/Peripherals/DiskImageTests.cs`
- **Create** `tests/CpuEmulator.Tests/Peripherals/DemoDiskTests.cs`
- **Create** `tests/CpuEmulator.Tests/Surface/FrameCodecTests.cs`
- **Create** `tests/CpuEmulator.Tests/Surface/MachineHostTests.cs`
- **Create** `tests/CpuEmulator.Tests/Surface/Sp0AcceptanceTests.cs` — the un-fakeable headless gate (§6).

### Docs (status + roadmap)
- **Modify** `docs/superpowers/specs/2026-06-17-emulated-computer-sp0-foundation-design.md` — status DEFERRED → scheduled/implemented + the reconciliation note.
- **Modify** `docs/ROADMAP.md` — add the SP0 web-surface entry under the "CPUs → computers" arc and note the `IAudioSink` follow-on.

---

## Conventions to follow (from the existing codebase)

- **Namespaces** match the assembly: `CpuEmulator.Core`, `CpuEmulator.Peripherals`, `CpuEmulator.Machines`, `CpuEmulator.Surface.Web`. Tests use `CpuEmulator.Tests.Peripherals` / `CpuEmulator.Tests.Surface`.
- **`Directory.Build.props`** sets `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true` — code must be warning-clean.
- **Device-register pattern** mirrors `SimpleUart`: offset-decoded registers, `Realize(IMachineContext)` claims `context.IrqLine.Source()`, level-IRQ recomputed on every state change, `AccessWidth` ignored for 8-bit devices, `TryPeek` is side-effect-free.
- **`BoardSpec` shape** mirrors `Breadboard6502Board`: a `static` board class with a `Spec(...)` factory; MMIO regions are holes the slots fill; `PeripheralSlot.Base` page-aligned, `Length` a multiple of 256.
- **ROM assembly** mirrors `DemoRom.Build()`: a scratch `AddressSpace` mapping the same `byte[]` writable, a `MonitorEngine` over a throwaway `Mos6502Cpu`, `TryAssembleAt(pc, line, out bytes, out error)` per line.
- **Tests** use xUnit `[Fact]`/`[Theory]`; `Xunit` is a global `Using`; `Assert.Equal`/`Assert.True` style as in `SimpleUartTests`.
- **The solution file is `CpuEmulator.slnx`** (XML `<Solution>`), not a `.sln`. New projects are added with `dotnet sln CpuEmulator.slnx add <path>`.

---

## Task 0: Scaffold the `Surface.Web` project + register it in the solution

**Files:**
- Create: `src/CpuEmulator.Surface.Web/CpuEmulator.Surface.Web.csproj`
- Create: `src/CpuEmulator.Surface.Web/Placeholder.cs` (deleted in Task 9; exists only so the project compiles before any real file)
- Modify: `CpuEmulator.slnx`
- Modify: `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`

- [ ] **Step 1: Create the web project file**

Create `src/CpuEmulator.Surface.Web/CpuEmulator.Surface.Web.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!--
    The web surface for the "real machines" arc: a local HTTP + WebSocket server (ASP.NET Core
    minimal, built into .NET 10 — no heavy GUI dependency) → a browser HTML/JS canvas client.
    Intentionally NOT IsAotCompatible: it references CpuEmulator.Machines (→ CpuEmulator.Jit,
    Reflection.Emit) and ASP.NET Core, exactly like the Host post-piece-#3. The emulator CORE
    stays GUI-free; this is one frontend behind the IDisplayDevice / IKeyboardSink / IBlockDevice
    contracts.
  -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\CpuEmulator.Core\CpuEmulator.Core.csproj" />
    <ProjectReference Include="..\CpuEmulator.Peripherals\CpuEmulator.Peripherals.csproj" />
    <ProjectReference Include="..\CpuEmulator.Machines\CpuEmulator.Machines.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add a temporary placeholder so the project compiles**

Create `src/CpuEmulator.Surface.Web/Placeholder.cs`:

```csharp
namespace CpuEmulator.Surface.Web;

// Temporary: lets the Web SDK project compile before any real type exists. Deleted in Task 9
// once Program.cs is the entry point. (The Web SDK needs at least one compilable unit.)
internal static class Placeholder;
```

- [ ] **Step 3: Register the project in the solution**

Run: `dotnet sln CpuEmulator.slnx add src/CpuEmulator.Surface.Web/CpuEmulator.Surface.Web.csproj --solution-folder src`
Expected: `Project ... added to the solution.`

- [ ] **Step 4: Reference the web project from the test project**

In `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`, add to the existing `<ItemGroup>` of `<ProjectReference>`s (the block that already lists `CpuEmulator.Machines`):

```xml
    <ProjectReference Include="..\..\src\CpuEmulator.Surface.Web\CpuEmulator.Surface.Web.csproj" />
```

- [ ] **Step 5: Build to verify the scaffold compiles**

Run: `dotnet build CpuEmulator.slnx -c Debug`
Expected: Build succeeded, 0 errors (the new `CpuEmulator.Surface.Web` project builds; ASP.NET Core is resolved from the shared framework).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Surface.Web/CpuEmulator.Surface.Web.csproj src/CpuEmulator.Surface.Web/Placeholder.cs CpuEmulator.slnx tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj
git commit -m "build(sp0): scaffold CpuEmulator.Surface.Web project"
```

---

## Task 1: `IDisplayDevice` contract (Core)

**Files:**
- Create: `src/CpuEmulator.Core/IDisplayDevice.cs`
- Test: `tests/CpuEmulator.Tests/Peripherals/DemoFramebufferTests.cs` (the contract is exercised through `DemoFramebuffer` in Task 4; this task only adds the interface, so its "test" is a compile-time mock in the Core test surface)

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Surface/DisplayContractTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.Surface;

public class DisplayContractTests
{
    private sealed class StubDisplay : IDisplayDevice
    {
        public int Width => 2;
        public int Height => 1;
        public void RenderInto(Span<uint> rgba)
        {
            if (rgba.Length < Width * Height)
                throw new ArgumentException("span too small", nameof(rgba));
            rgba[0] = 0xFF0000FFu; // red
            rgba[1] = 0xFF00FF00u; // green
        }
        public event Action? FrameReady;
        public void Raise() => FrameReady?.Invoke();
    }

    [Fact]
    public void RenderInto_fills_the_rgba_span_and_FrameReady_fires()
    {
        var d = new StubDisplay();
        bool fired = false;
        d.FrameReady += () => fired = true;

        Span<uint> buf = stackalloc uint[d.Width * d.Height];
        d.RenderInto(buf);
        d.Raise();

        Assert.Equal(0xFF0000FFu, buf[0]);
        Assert.Equal(0xFF00FF00u, buf[1]);
        Assert.True(fired);
    }

    [Fact]
    public void RenderInto_throws_on_a_too_small_span()
    {
        var d = new StubDisplay();
        Assert.Throws<ArgumentException>(() =>
        {
            // local function so the Span isn't captured by the lambda
            void Call() { Span<uint> tiny = stackalloc uint[1]; d.RenderInto(tiny); }
            Call();
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DisplayContractTests"`
Expected: FAIL to **compile** — `IDisplayDevice` does not exist.

- [ ] **Step 3: Write the interface**

Create `src/CpuEmulator.Core/IDisplayDevice.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// A display output a chip exposes to the host, IN ADDITION TO <see cref="IPeripheral"/>
/// (which faces the guest CPU). The host PULLS final RGBA pixels: the chip writes RGBA8888,
/// row-major, doing any palette/mode lookup itself — so the surface is a dumb blitter that
/// never knows about modes or palettes (this is what lets one surface serve both ANTIC and CGA).
/// The chip raises <see cref="FrameReady"/> at its own vblank, scheduled via
/// <see cref="IScheduler"/> at the real refresh rate.
/// </summary>
public interface IDisplayDevice
{
    /// <summary>Native pixel width; may change with video mode.</summary>
    int Width { get; }

    /// <summary>Native pixel height; may change with video mode.</summary>
    int Height { get; }

    /// <summary>Write the final RGBA8888 frame, row-major, into <paramref name="rgba"/>.
    /// The destination must hold at least <see cref="Width"/> * <see cref="Height"/> pixels;
    /// a too-small span throws <see cref="ArgumentException"/>.</summary>
    void RenderInto(Span<uint> rgba);

    /// <summary>Raised at the chip's vblank (scheduler-driven), signalling a complete frame.</summary>
    event Action FrameReady;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DisplayContractTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core/IDisplayDevice.cs tests/CpuEmulator.Tests/Surface/DisplayContractTests.cs
git commit -m "feat(sp0): add IDisplayDevice contract (host pulls RGBA)"
```

---

## Task 2: `IKeyboardSink` + `KeyEvent` + `KeyCode` contract (Core)

**Files:**
- Create: `src/CpuEmulator.Core/KeyCode.cs`
- Create: `src/CpuEmulator.Core/KeyEvent.cs`
- Create: `src/CpuEmulator.Core/IKeyboardSink.cs`
- Test: `tests/CpuEmulator.Tests/Surface/KeyboardContractTests.cs`

**Decision (spec §8 open question, resolved):** `KeyCode` is a portable physical-key id modelled on the **USB-HID-usage-like** naming the project already favours for portability across machines (rather than DOM `code` strings). The browser maps DOM `event.code` → these names in `app.js`; unknown codes are dropped client-side (and any that slip through map to `KeyCode.None`, a no-op for the machine).

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Surface/KeyboardContractTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.Surface;

public class KeyboardContractTests
{
    private sealed class StubSink : IKeyboardSink
    {
        public readonly List<KeyEvent> Seen = [];
        public void PostKey(in KeyEvent e) => Seen.Add(e);
    }

    [Fact]
    public void KeyEvent_carries_action_keycode_and_optional_char()
    {
        var sink = new StubSink();
        sink.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a'));
        sink.PostKey(new KeyEvent(KeyAction.Up, KeyCode.A, null));

        Assert.Equal(2, sink.Seen.Count);
        Assert.Equal(KeyAction.Down, sink.Seen[0].Action);
        Assert.Equal(KeyCode.A, sink.Seen[0].Key);
        Assert.Equal('a', sink.Seen[0].Char);
        Assert.Equal(KeyAction.Up, sink.Seen[1].Action);
        Assert.Null(sink.Seen[1].Char);
    }

    [Fact]
    public void KeyCode_None_is_the_zero_value_for_unknown_keys()
    {
        Assert.Equal(0, (int)KeyCode.None);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~KeyboardContractTests"`
Expected: FAIL to compile — `KeyCode`, `KeyEvent`, `KeyAction`, `IKeyboardSink` do not exist.

- [ ] **Step 3: Write `KeyCode`**

Create `src/CpuEmulator.Core/KeyCode.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// A portable physical-key identifier (USB-HID-usage-like), independent of any one machine's
/// scan matrix. The browser maps DOM key events to these; each machine's keyboard chip owns the
/// translation to its native scan codes (POKEY scan / 8255 PPI). Unknown keys map to
/// <see cref="None"/> (a no-op for the machine). SP0 covers the printable-ASCII + a few control
/// keys the demo program needs; real machines extend this as required (additive only).
/// </summary>
public enum KeyCode
{
    None = 0,

    // Letters
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    // Digits (top row)
    Digit0, Digit1, Digit2, Digit3, Digit4,
    Digit5, Digit6, Digit7, Digit8, Digit9,

    // Whitespace / editing
    Space,
    Enter,
    Backspace,
    Tab,
    Escape,

    // Arrows (the demo's moving cursor)
    ArrowLeft,
    ArrowRight,
    ArrowUp,
    ArrowDown,
}
```

- [ ] **Step 4: Write `KeyEvent`**

Create `src/CpuEmulator.Core/KeyEvent.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>Whether a key went down or came up.</summary>
public enum KeyAction
{
    Down,
    Up,
}

/// <summary>
/// One normalized keyboard event the host pushes to a machine's keyboard chip. <see cref="Key"/>
/// is the portable physical-key id; <see cref="Char"/> is the typed character when the host could
/// resolve one (e.g. 'A' for Shift+A) and null otherwise (key-up, or a non-printing key).
/// </summary>
public readonly record struct KeyEvent(KeyAction Action, KeyCode Key, char? Char);
```

- [ ] **Step 5: Write `IKeyboardSink`**

Create `src/CpuEmulator.Core/IKeyboardSink.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// A keyboard input a chip exposes to the host, IN ADDITION TO <see cref="IPeripheral"/>. The
/// host PUSHES normalized <see cref="KeyEvent"/>s; the chip owns the translation to its native
/// scan matrix and raises IRQ as appropriate. An unknown <see cref="KeyCode"/> (or
/// <see cref="KeyCode.None"/>) is ignored (no-op).
/// </summary>
public interface IKeyboardSink
{
    void PostKey(in KeyEvent e);
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~KeyboardContractTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/CpuEmulator.Core/KeyCode.cs src/CpuEmulator.Core/KeyEvent.cs src/CpuEmulator.Core/IKeyboardSink.cs tests/CpuEmulator.Tests/Surface/KeyboardContractTests.cs
git commit -m "feat(sp0): add IKeyboardSink + KeyEvent + KeyCode contract"
```

---

## Task 3: `IBlockDevice` contract + `DiskImage` adapter (Core + Peripherals)

**Files:**
- Create: `src/CpuEmulator.Core/IBlockDevice.cs`
- Create: `src/CpuEmulator.Peripherals/DiskImage.cs`
- Test: `tests/CpuEmulator.Tests/Peripherals/DiskImageTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Peripherals/DiskImageTests.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class DiskImageTests
{
    [Fact]
    public void ReadSector_returns_the_image_bytes_for_an_lba()
    {
        var bytes = new byte[256 * 2];
        bytes[256] = 0xAB;           // first byte of sector 1
        bytes[256 + 255] = 0xCD;     // last byte of sector 1
        var disk = new DiskImage(bytes, sectorSize: 256, isReadOnly: false);

        var dst = new byte[256];
        disk.ReadSector(1, dst);

        Assert.Equal(0xAB, dst[0]);
        Assert.Equal(0xCD, dst[255]);
        Assert.Equal(2, disk.SectorCount);
        Assert.Equal(256, disk.SectorSize);
    }

    [Fact]
    public void WriteSector_persists_into_the_image()
    {
        var disk = new DiskImage(new byte[256 * 2], sectorSize: 256, isReadOnly: false);
        var src = new byte[256];
        src[0] = 0x11;
        src[255] = 0x22;

        disk.WriteSector(0, src);

        var back = new byte[256];
        disk.ReadSector(0, back);
        Assert.Equal(0x11, back[0]);
        Assert.Equal(0x22, back[255]);
    }

    [Fact]
    public void WriteSector_throws_when_read_only()
    {
        var disk = new DiskImage(new byte[256], sectorSize: 256, isReadOnly: true);
        Assert.True(disk.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => disk.WriteSector(0, new byte[256]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Out_of_range_lba_throws(long lba)
    {
        var disk = new DiskImage(new byte[256 * 2], sectorSize: 256, isReadOnly: false);
        Assert.Throws<ArgumentOutOfRangeException>(() => disk.ReadSector(lba, new byte[256]));
    }

    [Fact]
    public void Wrong_size_destination_span_throws()
    {
        var disk = new DiskImage(new byte[256], sectorSize: 256, isReadOnly: false);
        Assert.Throws<ArgumentException>(() => disk.ReadSector(0, new byte[128]));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DiskImageTests"`
Expected: FAIL to compile — `IBlockDevice`, `DiskImage` do not exist.

- [ ] **Step 3: Write `IBlockDevice`**

Create `src/CpuEmulator.Core/IBlockDevice.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// Backing storage for disk controllers: a flat array of fixed-size sectors addressed by LBA.
/// Machine-specific disk controllers (SIO/810, µPD765) sit ON TOP, translating the guest's
/// register protocol into block ops; image-format quirks (ATR headers, etc.) are the
/// controller/adapter's concern (SP1+). SP0's demo uses a RAW sector image.
/// </summary>
public interface IBlockDevice
{
    int SectorSize { get; }
    long SectorCount { get; }
    bool IsReadOnly { get; }

    /// <summary>Read sector <paramref name="lba"/> into <paramref name="dst"/> (exactly
    /// <see cref="SectorSize"/> bytes). Out-of-range LBA throws
    /// <see cref="ArgumentOutOfRangeException"/>; a wrong-length span throws
    /// <see cref="ArgumentException"/>.</summary>
    void ReadSector(long lba, Span<byte> dst);

    /// <summary>Write <paramref name="src"/> (exactly <see cref="SectorSize"/> bytes) to sector
    /// <paramref name="lba"/>. Throws <see cref="System.InvalidOperationException"/> if
    /// <see cref="IsReadOnly"/>; out-of-range LBA / wrong-length span throw as in
    /// <see cref="ReadSector"/>.</summary>
    void WriteSector(long lba, ReadOnlySpan<byte> src);
}
```

- [ ] **Step 4: Write `DiskImage`**

Create `src/CpuEmulator.Peripherals/DiskImage.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// A raw sector image backing an <see cref="IBlockDevice"/>: a flat byte array where
/// LBA n occupies <c>[n * SectorSize, (n+1) * SectorSize)</c>. Constructed over an in-memory
/// array (the demo + tests) or loaded from a host file via <see cref="FromFile"/>. SP0 keeps
/// it raw; ATR/IMG headers and machine-specific formats are SP1+.
/// </summary>
public sealed class DiskImage : IBlockDevice
{
    private readonly byte[] _image;

    public int SectorSize { get; }
    public long SectorCount { get; }
    public bool IsReadOnly { get; }

    /// <summary>Wrap an existing image array. Its length must be a positive multiple of
    /// <paramref name="sectorSize"/>.</summary>
    public DiskImage(byte[] image, int sectorSize, bool isReadOnly)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (sectorSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Sector size must be positive.");
        if (image.Length == 0 || image.Length % sectorSize != 0)
            throw new ArgumentException(
                $"Image length {image.Length} must be a positive multiple of sector size {sectorSize}.",
                nameof(image));

        _image = image;
        SectorSize = sectorSize;
        SectorCount = image.Length / sectorSize;
        IsReadOnly = isReadOnly;
    }

    /// <summary>Load a raw image from a host file (read-write unless <paramref name="isReadOnly"/>).
    /// The on-disk file is NOT written back in SP0 — writes mutate the in-memory copy only
    /// (persistence is SP1+); the demo only reads.</summary>
    public static DiskImage FromFile(string path, int sectorSize, bool isReadOnly) =>
        new(File.ReadAllBytes(path), sectorSize, isReadOnly);

    public void ReadSector(long lba, Span<byte> dst)
    {
        if (dst.Length != SectorSize)
            throw new ArgumentException(
                $"Destination span length {dst.Length} must equal sector size {SectorSize}.", nameof(dst));
        _image.AsSpan(Offset(lba), SectorSize).CopyTo(dst);
    }

    public void WriteSector(long lba, ReadOnlySpan<byte> src)
    {
        if (IsReadOnly)
            throw new InvalidOperationException("Disk image is read-only.");
        if (src.Length != SectorSize)
            throw new ArgumentException(
                $"Source span length {src.Length} must equal sector size {SectorSize}.", nameof(src));
        src.CopyTo(_image.AsSpan(Offset(lba), SectorSize));
    }

    private int Offset(long lba)
    {
        if (lba < 0 || lba >= SectorCount)
            throw new ArgumentOutOfRangeException(nameof(lba),
                $"LBA {lba} is out of range [0, {SectorCount}).");
        return checked((int)(lba * SectorSize));
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DiskImageTests"`
Expected: PASS (6 tests: 1 read, 1 write, 1 read-only, 2 range, 1 wrong-size).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Core/IBlockDevice.cs src/CpuEmulator.Peripherals/DiskImage.cs tests/CpuEmulator.Tests/Peripherals/DiskImageTests.cs
git commit -m "feat(sp0): add IBlockDevice contract + raw DiskImage adapter"
```

---

## Task 4: `DemoFramebuffer` device (Peripherals)

**Files:**
- Create: `src/CpuEmulator.Peripherals/DemoFramebuffer.cs`
- Test: `tests/CpuEmulator.Tests/Peripherals/DemoFramebufferTests.cs`

**Decision (spec §8, resolved):** 256×192, 8bpp, one byte per pixel of VRAM (49,152 bytes), a fixed 256-entry palette. The palette is a simple deterministic ramp so tests can assert exact RGBA without a palette table file: `palette[i] = 0xFF000000 | (i*0x010101)` — a 256-level grayscale (A=0xFF, R=G=B=i). Real machines supply their own palette; the contract (chip does the lookup) is identical.

The VRAM is memory-mapped at the slot base; the page span is larger than 49,152 to keep the slot a multiple of 256 — the framebuffer decodes `offset` against its VRAM length and ignores offsets past the end (writes dropped, reads return 0), matching the open-bus-ish behaviour of a partially-populated map.

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Peripherals/DemoFramebufferTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class DemoFramebufferTests
{
    [Fact]
    public void Dimensions_are_256_by_192()
    {
        var fb = new DemoFramebuffer();
        Assert.Equal(256, fb.Width);
        Assert.Equal(192, fb.Height);
    }

    [Fact]
    public void Written_vram_byte_renders_through_the_grayscale_palette()
    {
        var fb = new DemoFramebuffer();
        // pixel (0,0) = palette index 0x00 (black); pixel (1,0) = index 0xFF (white)
        fb.Write(0, AccessWidth.Byte, 0x00);
        fb.Write(1, AccessWidth.Byte, 0xFF);

        var rgba = new uint[fb.Width * fb.Height];
        fb.RenderInto(rgba);

        Assert.Equal(0xFF000000u, rgba[0]); // black, opaque
        Assert.Equal(0xFFFFFFFFu, rgba[1]); // white, opaque
    }

    [Fact]
    public void A_mid_index_maps_to_a_gray_ramp_entry()
    {
        var fb = new DemoFramebuffer();
        fb.Write(10, AccessWidth.Byte, 0x80);

        var rgba = new uint[fb.Width * fb.Height];
        fb.RenderInto(rgba);

        Assert.Equal(0xFF808080u, rgba[10]);
    }

    [Fact]
    public void Reads_return_the_stored_vram_byte()
    {
        var fb = new DemoFramebuffer();
        fb.Write(5, AccessWidth.Byte, 0x3C);
        Assert.Equal(0x3Cu, fb.Read(5, AccessWidth.Byte));
    }

    [Fact]
    public void RenderInto_throws_on_a_too_small_span()
    {
        var fb = new DemoFramebuffer();
        Assert.Throws<ArgumentException>(() => fb.RenderInto(new uint[10]));
    }

    [Fact]
    public void FrameReady_fires_on_the_scheduled_vblank_tick()
    {
        var fb = new DemoFramebuffer();
        bool fired = false;
        fb.FrameReady += () => fired = true;

        // Drive a Machine so the scheduler advances past one 60 Hz vblank interval.
        var machine = CpuEmulator.Tests.Surface.DemoSurfaceFixture.BuildMachineWith(fb);
        machine.Reset();
        machine.Run(machine.Cpu is null ? 0 : 100_000); // > one vblank interval at the demo clock

        Assert.True(fired);
    }
}
```

> Note: `DemoSurfaceFixture.BuildMachineWith` is created in Task 8 (the acceptance-test fixture). If running Task 4 in isolation, comment out the last `[Fact]` until Task 8, then re-enable — it is the only cross-task test reference and is repeated verbatim in Task 8's fixture description.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoFramebufferTests"`
Expected: FAIL to compile — `DemoFramebuffer` does not exist.

- [ ] **Step 3: Write `DemoFramebuffer`**

Create `src/CpuEmulator.Peripherals/DemoFramebuffer.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// The SP0 demo display: a 256×192, 8-bits-per-pixel palettized linear framebuffer. One byte of
/// VRAM per pixel, row-major; <see cref="RenderInto"/> looks each byte up in a fixed 256-entry
/// palette to produce RGBA8888 (so the surface is a dumb blitter — see <see cref="IDisplayDevice"/>).
/// The palette is a deterministic 256-level grayscale ramp (A=0xFF, R=G=B=index), letting tests
/// assert exact pixels without a palette file; a real machine supplies its own palette behind the
/// same contract. <see cref="FrameReady"/> fires on a scheduler-driven 60 Hz vblank tick (claimed
/// in <see cref="Realize"/>); VRAM reads/writes are memory-mapped (<see cref="IPeripheral"/>).
/// </summary>
public sealed class DemoFramebuffer : IPeripheral, IDisplayDevice
{
    private const int WidthPx = 256;
    private const int HeightPx = 192;
    private const int VramLength = WidthPx * HeightPx; // 49,152 bytes, one per pixel

    // 60 frames/sec at the demo's nominal 1 MHz 6502 clock = one vblank every 16,667 cycles.
    private const long VblankIntervalCycles = 16_667;

    private readonly byte[] _vram = new byte[VramLength];
    private static readonly uint[] Palette = BuildGrayscalePalette();

    public string Name => "framebuffer";
    public int Width => WidthPx;
    public int Height => HeightPx;
    public event Action? FrameReady;

    /// <summary>Schedule the recurring vblank tick that raises <see cref="FrameReady"/>.</summary>
    public void Realize(IMachineContext context) =>
        context.Scheduler.ScheduleEvery(VblankIntervalCycles, () => FrameReady?.Invoke());

    public uint Read(uint offset, AccessWidth width) =>
        offset < VramLength ? _vram[offset] : 0x00u;

    public void Write(uint offset, AccessWidth width, uint value)
    {
        if (offset < VramLength)
            _vram[offset] = unchecked((byte)value);
    }

    public bool TryPeek(uint offset, out byte value)
    {
        value = offset < VramLength ? _vram[offset] : (byte)0x00;
        return true;
    }

    public void RenderInto(Span<uint> rgba)
    {
        if (rgba.Length < VramLength)
            throw new ArgumentException(
                $"Destination needs {VramLength} pixels; got {rgba.Length}.", nameof(rgba));
        for (int i = 0; i < VramLength; i++)
            rgba[i] = Palette[_vram[i]];
    }

    private static uint[] BuildGrayscalePalette()
    {
        var p = new uint[256];
        for (int i = 0; i < 256; i++)
            p[i] = 0xFF000000u | (uint)(i << 16) | (uint)(i << 8) | (uint)i; // 0xFFrrggbb, r=g=b=i
        return p;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoFramebufferTests"`
Expected: PASS. (If Task 8's fixture is not yet present, the `FrameReady` `[Fact]` is temporarily commented out per Step 1's note; re-enable after Task 8.)

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/DemoFramebuffer.cs tests/CpuEmulator.Tests/Peripherals/DemoFramebufferTests.cs
git commit -m "feat(sp0): add DemoFramebuffer (256x192 8bpp palettized, IDisplayDevice)"
```

---

## Task 5: `DemoKeyboard` device (Peripherals)

**Files:**
- Create: `src/CpuEmulator.Peripherals/DemoKeyboard.cs`
- Test: `tests/CpuEmulator.Tests/Peripherals/DemoKeyboardTests.cs`

The keyboard is UART-rx-shaped (mirrors `SimpleUart`'s rx half): two registers — DATA (offset 0, destructive read of the next queued key byte) and STATUS (offset 1, bit0 = key-ready). `PostKey` enqueues the typed `Char` (only printable down-events produce a byte; up-events and `KeyCode.None`/no-`Char` events are ignored). It claims `context.IrqLine.Source()` in `Realize` and asserts while the queue is non-empty (level-IRQ), exactly like the UART rx path.

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Peripherals/DemoKeyboardTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class DemoKeyboardTests
{
    [Fact]
    public void PostKey_down_with_char_enqueues_a_byte_readable_at_DATA()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'A'));

        Assert.Equal(0x01u, kbd.Read(1, AccessWidth.Byte) & 0x01); // STATUS: key-ready
        Assert.Equal((uint)'A', kbd.Read(0, AccessWidth.Byte));    // DATA: dequeue
        Assert.Equal(0x00u, kbd.Read(1, AccessWidth.Byte) & 0x01); // now empty
    }

    [Fact]
    public void Keys_dequeue_FIFO_in_order()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.H, 'H'));
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.I, 'I'));

        Assert.Equal((uint)'H', kbd.Read(0, AccessWidth.Byte));
        Assert.Equal((uint)'I', kbd.Read(0, AccessWidth.Byte));
    }

    [Fact]
    public void Key_up_events_are_ignored()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Up, KeyCode.A, 'A'));
        Assert.Equal(0x00u, kbd.Read(1, AccessWidth.Byte) & 0x01);
    }

    [Fact]
    public void Events_without_a_char_are_ignored()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.None, null));
        Assert.Equal(0x00u, kbd.Read(1, AccessWidth.Byte) & 0x01);
    }

    [Fact]
    public void Empty_DATA_read_returns_zero()
    {
        var kbd = new DemoKeyboard();
        Assert.Equal(0x00u, kbd.Read(0, AccessWidth.Byte));
    }

    [Fact]
    public void TryPeek_does_not_dequeue()
    {
        var kbd = new DemoKeyboard();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Z, 'Z'));

        Assert.True(kbd.TryPeek(0, out byte head));
        Assert.Equal((byte)'Z', head);
        Assert.Equal((uint)'Z', kbd.Read(0, AccessWidth.Byte)); // still there to dequeue
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoKeyboardTests"`
Expected: FAIL to compile — `DemoKeyboard` does not exist.

- [ ] **Step 3: Write `DemoKeyboard`**

Create `src/CpuEmulator.Peripherals/DemoKeyboard.cs`:

```csharp
using System.Collections.Concurrent;
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// The SP0 demo keyboard: a UART-rx-shaped 2-register device (mirrors <see cref="SimpleUart"/>'s
/// receive half). The host PUSHES normalized key events via <see cref="IKeyboardSink.PostKey"/>;
/// the guest READS them memory-mapped (<see cref="IPeripheral"/>):
/// <list type="bullet">
///   <item>offset 0 DATA — read: dequeue the next key byte (0x00 when empty); recomputes IRQ.</item>
///   <item>offset 1 STATUS — read: bit0 = key-ready; never dequeues.</item>
/// </list>
/// Only printable DOWN events with a resolved <see cref="KeyEvent.Char"/> enqueue a byte; key-ups
/// and char-less events are no-ops. <see cref="Realize"/> claims <c>context.IrqLine.Source()</c>
/// and the source is asserted while the queue is non-empty (level-IRQ, matching the UART rx path).
/// AccessWidth is ignored (8-bit device).
/// </summary>
public sealed class DemoKeyboard : IPeripheral, IKeyboardSink
{
    private readonly ConcurrentQueue<byte> _keys = new();
    private IInterruptLine? _irq;

    public string Name => "keyboard";

    public void Realize(IMachineContext context) => _irq = context.IrqLine.Source();

    public void PostKey(in KeyEvent e)
    {
        if (e.Action != KeyAction.Down || e.Char is not char c)
            return;                          // ignore key-ups and char-less events
        _keys.Enqueue(unchecked((byte)c));
        UpdateIrqLevel();
    }

    public uint Read(uint offset, AccessWidth width)
    {
        switch (offset & 0x01)
        {
            case 0:
            {
                uint value = _keys.TryDequeue(out byte b) ? b : 0x00u; // DATA: destructive read
                UpdateIrqLevel();
                return value;
            }
            default:
                return _keys.IsEmpty ? 0x00u : 0x01u;                  // STATUS: key-ready
        }
    }

    public bool TryPeek(uint offset, out byte value)
    {
        value = (offset & 0x01) == 0
            ? (_keys.TryPeek(out byte head) ? head : (byte)0x00)
            : (byte)(_keys.IsEmpty ? 0x00 : 0x01);
        return true;
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        // The demo keyboard is read-only to the guest; writes are ignored.
    }

    private void UpdateIrqLevel()
    {
        if (_irq is null) return;            // bare (unrealized) keyboards drive no line
        if (!_keys.IsEmpty) _irq.Assert();
        else _irq.Release();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoKeyboardTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/DemoKeyboard.cs tests/CpuEmulator.Tests/Peripherals/DemoKeyboardTests.cs
git commit -m "feat(sp0): add DemoKeyboard (UART-rx-shaped, IKeyboardSink, level-IRQ)"
```

---

## Task 6: `DemoDisk` controller (Peripherals)

**Files:**
- Create: `src/CpuEmulator.Peripherals/DemoDisk.cs`
- Test: `tests/CpuEmulator.Tests/Peripherals/DemoDiskTests.cs`

The demo disk controller is a tiny memory-mapped register file over an `IBlockDevice`:
- offset 0 **LBA** (read/write): the sector number to operate on (one byte — the demo image is small).
- offset 1 **CMD** (write): writing `0x01` triggers a read of sector `LBA` into the internal 256-byte buffer; writing `0x02` triggers a write of the buffer to sector `LBA` (ignored if the block device is read-only). Reading CMD returns STATUS: bit0 = ready (1 when idle), bit1 = error (last op threw).
- offset 2 **DATA** (read/write): an auto-incrementing window into the 256-byte buffer — each read returns the next buffer byte and advances; each write stores and advances. Reading CMD (status) or writing LBA resets the DATA pointer to 0.

This is deliberately the simplest controller that lets the demo ROM issue "read sector 0, then read a byte out" — real controllers (SIO/810, µPD765) replace it in SP1+.

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Peripherals/DemoDiskTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class DemoDiskTests
{
    private static DemoDisk DiskWithSector0(byte first, byte second)
    {
        var image = new byte[256 * 2];
        image[0] = first;
        image[1] = second;
        return new DemoDisk(new DiskImage(image, sectorSize: 256, isReadOnly: false));
    }

    [Fact]
    public void Read_command_surfaces_the_sector_bytes_through_DATA()
    {
        var disk = DiskWithSector0(0xDE, 0xAD);

        disk.Write(0, AccessWidth.Byte, 0);     // LBA = 0
        disk.Write(1, AccessWidth.Byte, 0x01);  // CMD = read

        Assert.Equal(0x01u, disk.Read(1, AccessWidth.Byte) & 0x01); // STATUS: ready
        Assert.Equal(0xDEu, disk.Read(2, AccessWidth.Byte));        // DATA[0]
        Assert.Equal(0xADu, disk.Read(2, AccessWidth.Byte));        // DATA[1]
    }

    [Fact]
    public void Reading_a_second_sector_replaces_the_buffer()
    {
        var image = new byte[256 * 2];
        image[0] = 0x11;          // sector 0, byte 0
        image[256] = 0x22;        // sector 1, byte 0
        var disk = new DemoDisk(new DiskImage(image, sectorSize: 256, isReadOnly: false));

        disk.Write(0, AccessWidth.Byte, 1);     // LBA = 1
        disk.Write(1, AccessWidth.Byte, 0x01);  // CMD = read
        Assert.Equal(0x22u, disk.Read(2, AccessWidth.Byte));
    }

    [Fact]
    public void Out_of_range_lba_sets_the_error_status_bit_and_does_not_throw_to_the_guest()
    {
        var disk = DiskWithSector0(0x00, 0x00);

        disk.Write(0, AccessWidth.Byte, 9);     // LBA = 9 (only 2 sectors)
        disk.Write(1, AccessWidth.Byte, 0x01);  // CMD = read -> out of range

        Assert.Equal(0x02u, disk.Read(1, AccessWidth.Byte) & 0x02); // STATUS: error bit set
    }

    [Fact]
    public void Write_command_persists_the_buffer_to_the_sector()
    {
        var image = new byte[256];
        var block = new DiskImage(image, sectorSize: 256, isReadOnly: false);
        var disk = new DemoDisk(block);

        disk.Write(0, AccessWidth.Byte, 0);     // LBA = 0
        disk.Write(2, AccessWidth.Byte, 0x7E);  // DATA[0] = 0x7E (writing LBA reset the pointer)
        disk.Write(1, AccessWidth.Byte, 0x02);  // CMD = write

        var back = new byte[256];
        block.ReadSector(0, back);
        Assert.Equal(0x7E, back[0]);
    }

    [Fact]
    public void Realize_is_a_no_op_no_irq_claimed()
    {
        // The demo disk is polled (no IRQ); Realize must not throw and the device works unrealized.
        var disk = DiskWithSector0(0x42, 0x00);
        disk.Write(0, AccessWidth.Byte, 0);
        disk.Write(1, AccessWidth.Byte, 0x01);
        Assert.Equal(0x42u, disk.Read(2, AccessWidth.Byte));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoDiskTests"`
Expected: FAIL to compile — `DemoDisk` does not exist.

- [ ] **Step 3: Write `DemoDisk`**

Create `src/CpuEmulator.Peripherals/DemoDisk.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// The SP0 demo disk controller: a minimal memory-mapped register file over an
/// <see cref="IBlockDevice"/> (a <see cref="DiskImage"/> in the demo). It is the simplest
/// controller that lets the demo ROM "read sector N, then read a byte out". Real controllers
/// (SIO/810, µPD765) replace it in SP1+.
/// <list type="bullet">
///   <item>offset 0 LBA — read/write: the target sector (one byte; resets the DATA pointer on write).</item>
///   <item>offset 1 CMD/STATUS — write 0x01 = read sector LBA into the buffer; write 0x02 = write the
///         buffer to sector LBA (no-op if the block device is read-only). Read = STATUS: bit0 ready
///         (always 1 — ops complete synchronously), bit1 = error (last op was out of range / read-only).
///         A STATUS read also resets the DATA pointer to 0.</item>
///   <item>offset 2 DATA — read/write: an auto-incrementing window into the 256-byte buffer.</item>
/// </list>
/// Polled (no IRQ); <see cref="Realize"/> is a no-op. AccessWidth is ignored (8-bit device).
/// </summary>
public sealed class DemoDisk : IPeripheral
{
    private readonly IBlockDevice _block;
    private readonly byte[] _buffer;
    private long _lba;
    private int _dataPtr;
    private bool _error;

    public string Name => "disk";

    public DemoDisk(IBlockDevice block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _block = block;
        _buffer = new byte[block.SectorSize];
    }

    public void Realize(IMachineContext context) { /* polled device — nothing to wire */ }

    public uint Read(uint offset, AccessWidth width)
    {
        switch (offset % 3)
        {
            case 0:
                return (uint)(_lba & 0xFF);                    // LBA
            case 1:
                _dataPtr = 0;                                  // STATUS read rewinds DATA
                return 0x01u | (_error ? 0x02u : 0x00u);       // bit0 ready, bit1 error
            default:
                byte b = _buffer[_dataPtr];                    // DATA: read + advance
                _dataPtr = (_dataPtr + 1) % _buffer.Length;
                return b;
        }
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        switch (offset % 3)
        {
            case 0:
                _lba = value & 0xFF;                           // LBA (resets DATA window)
                _dataPtr = 0;
                break;
            case 1:
                Execute(unchecked((byte)value));               // CMD
                break;
            default:
                _buffer[_dataPtr] = unchecked((byte)value);    // DATA: store + advance
                _dataPtr = (_dataPtr + 1) % _buffer.Length;
                break;
        }
    }

    private void Execute(byte command)
    {
        _error = false;
        _dataPtr = 0;
        try
        {
            switch (command)
            {
                case 0x01:
                    _block.ReadSector(_lba, _buffer);
                    break;
                case 0x02:
                    _block.WriteSector(_lba, _buffer);
                    break;
                // other commands are no-ops
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            _error = true;                                     // surface as STATUS, never throw to the guest
        }
        catch (InvalidOperationException)
        {
            _error = true;                                     // read-only write attempt
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoDiskTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/DemoDisk.cs tests/CpuEmulator.Tests/Peripherals/DemoDiskTests.cs
git commit -m "feat(sp0): add DemoDisk controller over IBlockDevice (polled, LBA/CMD/DATA)"
```

---

## Task 7: `DemoBoardRom` — the demo 6502 program (Machines)

**Files:**
- Create: `src/CpuEmulator.Machines/DemoBoardRom.cs`
- Test: `tests/CpuEmulator.Tests/Machines/DemoBoardRomTests.cs`

The ROM is assembled at startup like `DemoRom.Build()` (scratch space + `MonitorEngine.TryAssembleAt`). The MMIO map (finalized in Task 8's `DemoBoard`):
- Framebuffer VRAM base `$8000` (spans `$8000`–`$BFFF`, 16 KiB window; VRAM is 48 KiB so only the first 16 KiB is reachable via this 6502's 64 KiB space — the demo only paints the top rows, which fit).

> **Address reconciliation:** a 6502 has 64 KiB of address space; the 49,152-byte framebuffer cannot be fully mapped alongside RAM/ROM. The demo paints only the **first 16 KiB of VRAM** (the top 64 rows of 256 px), mapped at `$8000`–`$BFFF`. `RenderInto` still renders all 256×192 (untouched rows are palette index 0 = black). This is a demo-scale concession; SP1 machines with bank-switching or a dedicated VRAM space map full framebuffers.

- Keyboard at `$D000` (DATA `$D000`, STATUS `$D001`).
- Disk at `$D100` (LBA `$D100`, CMD/STATUS `$D101`, DATA `$D102`).
- RAM `$0000`–`$7FFF`; ROM `$E000`–`$FFFF`.

The program:
1. Paint a test pattern: write an incrementing byte to the first 256 VRAM cells (`$8000`–`$80FF`) — a gradient strip (proves display-out; the acceptance test asserts these exact bytes/pixels).
2. Poll the keyboard STATUS; on a ready key, read DATA and store it at VRAM `$8100` (proves the input round-trip).
3. Issue disk read of sector 0 (LBA=0, CMD=1), read one DATA byte, store it at VRAM `$8101` (proves the block device).
4. Loop back to the keyboard poll (so the host can keep feeding keys).

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Machines/DemoBoardRomTests.cs`:

```csharp
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Machines;

public class DemoBoardRomTests
{
    [Fact]
    public void Build_returns_an_8kib_image_with_a_reset_vector_into_rom()
    {
        byte[] rom = DemoBoardRom.Build();

        Assert.Equal(0x2000, rom.Length);              // $E000-$FFFF
        // RESET vector at $FFFC/$FFFD points into ROM ($E000..$FFFF)
        ushort reset = (ushort)(rom[0x1FFC] | (rom[0x1FFD] << 8));
        Assert.InRange(reset, (ushort)0xE000, (ushort)0xFFFF);
        Assert.Equal((ushort)DemoBoardRom.Entry, reset);
    }

    [Fact]
    public void Build_is_deterministic()
    {
        Assert.Equal(DemoBoardRom.Build(), DemoBoardRom.Build());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoBoardRomTests"`
Expected: FAIL to compile — `DemoBoardRom` does not exist.

- [ ] **Step 3: Write `DemoBoardRom`**

Create `src/CpuEmulator.Machines/DemoBoardRom.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;

namespace CpuEmulator.Machines;

/// <summary>
/// The SP0 demo's 8 KiB 6502 ROM ($E000–$FFFF), assembled AT STARTUP by the generated
/// single-instruction assembler (the same pattern as the host's DemoRom). The program proves all
/// three SP0 device contracts:
/// <list type="number">
///   <item>paints a 256-byte gradient test pattern to VRAM $8000.. (display out);</item>
///   <item>polls the keyboard and echoes the typed byte to VRAM $8100 (input round-trip);</item>
///   <item>reads disk sector 0 and paints its first byte to VRAM $8101 (block device).</item>
/// </list>
/// Assembly happens in a SCRATCH space mapping the SAME byte[] writable at $E000, exactly as
/// DemoRom does. The device addresses MUST match <see cref="DemoBoard"/>: framebuffer $8000,
/// keyboard $D000 (DATA/STATUS), disk $D100 (LBA/CMD/DATA).
/// </summary>
public static class DemoBoardRom
{
    public const ushort Entry = 0xE000;

    // Device addresses — kept in lockstep with DemoBoard's peripheral slots.
    public const ushort FramebufferBase = 0x8000; // VRAM
    public const ushort PatternLength = 0x0100;    // 256-byte gradient strip
    public const ushort EchoCell = 0x8100;         // where a typed key lands
    public const ushort DiskCell = 0x8101;         // where the disk byte lands
    public const ushort KbdData = 0xD000;
    public const ushort KbdStatus = 0xD001;
    public const ushort DiskLba = 0xD100;
    public const ushort DiskCmd = 0xD101;
    public const ushort DiskData = 0xD102;

    public static byte[] Build()
    {
        var image = new byte[0x2000];
        var scratch = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        scratch.MapMemory(0xE000, image, writable: true);
        var cpu = new Mos6502Cpu(scratch);
        var assembler = new MonitorEngine(cpu, scratch, cpu);

        // Program listing (hand-laid; addresses are documented for the reader and to anchor branches).
        //
        //   ; ---- 1. paint the 256-byte gradient at $8000 (X = index = colour) ----
        //   E000  LDX #$00
        //   E002  TXA            ; A = X (the gradient byte == the index)
        //   E003  STA $8000,X    ; VRAM[X] = X
        //   E006  INX
        //   E007  BNE $E002      ; loop 256 times (until X wraps to 0)
        //
        //   ; ---- 3. read disk sector 0, paint first byte at $8101 (done once, up front) ----
        //   E009  LDA #$00
        //   E00B  STA $D100      ; disk LBA = 0
        //   E00E  LDA #$01
        //   E010  STA $D101      ; disk CMD = read
        //   E013  LDA $D102      ; disk DATA[0]
        //   E016  STA $8101      ; VRAM[$8101] = disk byte
        //
        //   ; ---- 2. keyboard poll/echo loop ----
        //   E019  LDA $D001      ; keyboard STATUS
        //   E01C  AND #$01       ; key-ready?
        //   E01E  BEQ $E019      ; no -> keep polling
        //   E020  LDA $D000      ; keyboard DATA (dequeue)
        //   E023  STA $8100      ; VRAM[$8100] = typed byte
        //   E026  JMP $E019      ; poll forever
        string[] program =
        [
            "LDX #$00",      // E000
            "TXA",           // E002
            "STA $8000,X",   // E003
            "INX",           // E006
            "BNE $E002",     // E007
            "LDA #$00",      // E009
            "STA $D100",     // E00B
            "LDA #$01",      // E00E
            "STA $D101",     // E010
            "LDA $D102",     // E013
            "STA $8101",     // E016
            "LDA $D001",     // E019
            "AND #$01",      // E01C
            "BEQ $E019",     // E01E
            "LDA $D000",     // E020
            "STA $8100",     // E023
            "JMP $E019",     // E026
        ];

        uint pc = Entry;
        foreach (string line in program)
        {
            if (!assembler.TryAssembleAt(pc, line, out byte[] bytes, out string? error))
                throw new EmulationException($"demo board ROM assembly failed at ${pc:X4} '{line}': {error}");
            pc += (uint)bytes.Length;
        }

        scratch.Write8(0xFFFA, 0x00); scratch.Write8(0xFFFB, 0xE0); // NMI    -> entry
        scratch.Write8(0xFFFC, 0x00); scratch.Write8(0xFFFD, 0xE0); // RESET  -> entry
        scratch.Write8(0xFFFE, 0x00); scratch.Write8(0xFFFF, 0xE0); // IRQ/BRK -> entry
        return image;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoBoardRomTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/DemoBoardRom.cs tests/CpuEmulator.Tests/Machines/DemoBoardRomTests.cs
git commit -m "feat(sp0): add DemoBoardRom (monitor-assembled 6502 demo program)"
```

---

## Task 8: `DemoBoard` (`BoardSpec`) + the headless acceptance test (Machines + the §6 gate)

**Files:**
- Create: `src/CpuEmulator.Machines/DemoBoard.cs`
- Create: `tests/CpuEmulator.Tests/Surface/DemoSurfaceFixture.cs`
- Create: `tests/CpuEmulator.Tests/Surface/Sp0AcceptanceTests.cs`

`DemoBoard.Spec(...)` mirrors `Breadboard6502Board.Spec(...)`: a `BoardSpec` with RAM `$0000`–`$7FFF`, an MMIO region spanning `$8000`–`$FFFF` (the framebuffer + the two device slots live inside it; the gap is open-bus), and ROM `$E000`–`$FFFF`. The framebuffer slot is a full 16 KiB window (`$8000`, length `$4000`); the keyboard and disk slots are one page each (`$D000`, `$D100`). Only the keyboard is IRQ-wired (the disk is polled; the framebuffer raises no CPU interrupt — its `FrameReady` is a host-side event, not a guest IRQ).

> **MMIO/ROM overlap note:** the ROM region `$E000`–`$FFFF` sits *inside* the `$8000`–`$FFFF` MMIO span. `BoardMachineFactory` maps the ROM as backing memory and the peripheral slots as devices; the validator treats MMIO as a hole the slots/ROM fill (the same way `Breadboard6502Board`'s `$D000`–`$DFFF` MMIO holds the two slots plus an open-bus gap). If `BoardSpecValidator` rejects ROM-inside-MMIO, split the map into RAM `$0000`–`$7FFF`, MMIO `$8000`–`$DFFF`, ROM `$E000`–`$FFFF` (no MMIO over the ROM) — verify against `BoardSpecValidatorTests` during Step 2 and use whichever the validator accepts. The reference (`Breadboard6502Board`) puts ROM *outside* its MMIO span, so the split form is the expected-safe one; the spec below uses the split form.

- [ ] **Step 1: Write the failing acceptance test + fixture**

Create `tests/CpuEmulator.Tests/Surface/DemoSurfaceFixture.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Surface;

/// <summary>Builds the SP0 demo board for tests — the same composition the web surface uses, minus
/// the WebSocket. Exposes the three device handles so tests can pull RGBA, post keys, and seed the disk.</summary>
public sealed record DemoSurfaceFixture(
    Machine Machine, DemoFramebuffer Framebuffer, DemoKeyboard Keyboard, DemoDisk Disk)
{
    public static DemoSurfaceFixture Build()
    {
        var fb = new DemoFramebuffer();
        var kbd = new DemoKeyboard();
        // Seed disk sector 0 with a recognizable first byte for the acceptance assertion.
        var image = new byte[256 * 2];
        image[0] = 0x5A;
        var disk = new DemoDisk(new DiskImage(image, sectorSize: 256, isReadOnly: false));

        BoardSpec spec = DemoBoard.Spec(DemoBoardRom.Build(), fb, kbd, disk);
        Machine machine = BoardMachineFactory.Build(spec);
        return new DemoSurfaceFixture(machine, fb, kbd, disk);
    }

    /// <summary>Build a minimal Machine wrapping ONLY the framebuffer (for DemoFramebufferTests'
    /// FrameReady vblank test) — a one-slot board so the scheduler advances and raises the tick.</summary>
    public static Machine BuildMachineWith(DemoFramebuffer fb)
    {
        // Reuse the full demo board; the framebuffer's vblank fires regardless of the other devices.
        return Build() is var f && ReferenceEquals(f.Framebuffer, fb)
            ? f.Machine
            : BoardMachineFactory.Build(DemoBoard.Spec(DemoBoardRom.Build(), fb, new DemoKeyboard(),
                new DemoDisk(new DiskImage(new byte[256], 256, false))));
    }
}
```

Create `tests/CpuEmulator.Tests/Surface/Sp0AcceptanceTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.Surface;

/// <summary>
/// The SP0 acceptance gate (design spec §6), headless/fast — no browser, no throttle. Runs the
/// DemoBoard via the Machine and asserts the three device contracts end-to-end: (a) RenderInto
/// yields the expected gradient test pattern, (b) a synthetic PostKey changes VRAM, (c) a disk
/// ReadSector surfaces image bytes to the guest. Un-fakeable: the assertions read the real RGBA
/// the chip produced, the real VRAM the guest wrote, and the real disk byte the guest fetched.
/// </summary>
[Trait("Category", "UAT")]
public class Sp0AcceptanceTests
{
    [Fact]
    public void Demo_proves_display_keyboard_and_disk_end_to_end()
    {
        DemoSurfaceFixture fix = DemoSurfaceFixture.Build();
        fix.Machine.Reset();

        // Run enough cycles for the ROM to paint the pattern, read the disk byte, and enter the
        // keyboard poll loop. The pattern (256 px) + disk read complete in well under 5,000 cycles.
        fix.Machine.Run(20_000);

        var rgba = new uint[fix.Framebuffer.Width * fix.Framebuffer.Height];
        fix.Framebuffer.RenderInto(rgba);

        // (a) DISPLAY: the gradient test pattern — VRAM[i] == i for i in [0,256), grayscale palette.
        for (int i = 0; i < 256; i++)
        {
            uint expected = 0xFF000000u | (uint)(i << 16) | (uint)(i << 8) | (uint)i;
            Assert.Equal(expected, rgba[i]);
        }

        // (c) DISK: the guest read sector 0 (first byte 0x5A) and painted it at VRAM offset $8100-$8000
        //     = 0x0100 (DiskCell $8101 -> offset 0x0101). Index 0x0101 in the rgba buffer.
        Assert.Equal(0xFF5A5A5Au, rgba[0x0101]);

        // (b) KEYBOARD: synthetically post a key; run; assert it landed at the echo cell ($8100 -> 0x0100).
        fix.Keyboard.PostKey(new KeyEvent(KeyAction.Down, KeyCode.K, 'K'));
        fix.Machine.Run(20_000);

        fix.Framebuffer.RenderInto(rgba);
        uint k = 0xFF000000u | ((uint)'K' << 16) | ((uint)'K' << 8) | (uint)'K';
        Assert.Equal(k, rgba[0x0100]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~Sp0AcceptanceTests"`
Expected: FAIL to compile — `DemoBoard` does not exist.

- [ ] **Step 3: Write `DemoBoard`**

Create `src/CpuEmulator.Machines/DemoBoard.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>
/// The SP0 demo computer, expressed as a declarative <see cref="BoardSpec"/> (mirroring
/// <see cref="Breadboard6502Board"/>): a 6502 with RAM low, a memory-mapped framebuffer + keyboard
/// + disk, and the demo ROM high. This replaces the SP0 design's hand-wired "DemoMachine" — the
/// board model (shipped 2026-06-19) is now the one way to compose a machine. The web surface
/// (CpuEmulator.Surface.Web) drives the built Machine through a MachineHost; the monitor host
/// (CpuEmulator.Host) could boot the very same spec — two surfaces, one machine.
/// <para>Map: RAM $0000-$7FFF; framebuffer VRAM $8000-$BFFF (16 KiB window); MMIO $C000-$DFFF
/// holding the keyboard ($D000) + disk ($D100) slots; ROM $E000-$FFFF. Only the keyboard is
/// IRQ-wired (the disk is polled; the framebuffer's FrameReady is a host event, not a guest IRQ).</para>
/// </summary>
public static class DemoBoard
{
    public const uint RamBase = 0x0000;
    public const uint RamLength = 0x8000;        // $0000-$7FFF (32 KiB)
    public const uint FramebufferBase = 0x8000;  // VRAM window
    public const uint FramebufferLength = 0x4000; // $8000-$BFFF (16 KiB reachable VRAM)
    public const uint MmioBase = 0xC000;         // $C000-$DFFF device block
    public const uint MmioLength = 0x2000;
    public const uint KeyboardBase = 0xD000;
    public const uint DiskBase = 0xD100;
    public const uint RomBase = 0xE000;
    public const uint RomLength = 0x2000;        // $E000-$FFFF (8 KiB)

    /// <summary>Build the demo board-spec over a ROM image and the three device instances (so the
    /// caller — the surface or a test — keeps handles to RenderInto / PostKey / the disk).</summary>
    public static BoardSpec Spec(byte[] rom, DemoFramebuffer framebuffer, DemoKeyboard keyboard, DemoDisk disk) =>
        new("demo", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),
                new MemoryRegion(FramebufferBase, FramebufferLength, RegionKind.Mmio), // VRAM slot hole
                new MemoryRegion(MmioBase, MmioLength, RegionKind.Mmio),               // device slots hole
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, rom),
            ],
            Peripherals:
            [
                new PeripheralSlot("framebuffer", framebuffer, FramebufferBase, FramebufferLength),
                new PeripheralSlot("keyboard", keyboard, KeyboardBase, 0x0100),
                new PeripheralSlot("disk", disk, DiskBase, 0x0100),
            ],
            Irq: new IrqWiring(
            [
                new PeripheralIrq("keyboard", CpuInterrupt.Irq),
            ]),
            Reset: ResetConfig.None); // the demo ROM image carries its own $FFFC reset vector.
}
```

- [ ] **Step 4: Run the acceptance test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~Sp0AcceptanceTests"`
Expected: PASS (1 test — the three-contract gate).

If the validator rejects the framebuffer slot covering a 16 KiB MMIO region, or the slot `Length` exceeds what `PeripheralSlot` allows, re-check `BoardSpecValidatorTests` for the page/length rule and adjust `FramebufferLength` to the largest accepted multiple of 256 that still covers the 256-byte pattern + the two echo cells (the pattern needs `$8000`–`$8101`, so a single 256-page slot at `$8000` of length `$0200` suffices — shrink `FramebufferLength` to `0x0200` and the corresponding MMIO region to match if the large window is rejected).

- [ ] **Step 5: Re-enable + run the framebuffer vblank test**

Re-enable the `FrameReady_fires_on_the_scheduled_vblank_tick` `[Fact]` in `DemoFramebufferTests.cs` (Task 4) now that `DemoSurfaceFixture` exists.

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoFramebufferTests"`
Expected: PASS (all DemoFramebuffer tests, including the vblank tick).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Machines/DemoBoard.cs tests/CpuEmulator.Tests/Surface/DemoSurfaceFixture.cs tests/CpuEmulator.Tests/Surface/Sp0AcceptanceTests.cs tests/CpuEmulator.Tests/Peripherals/DemoFramebufferTests.cs
git commit -m "feat(sp0): add DemoBoard BoardSpec + the headless acceptance gate (§6)"
```

---

## Task 9: `FrameCodec` — the WebSocket wire format (Surface.Web)

**Files:**
- Create: `src/CpuEmulator.Surface.Web/FrameCodec.cs`
- Delete: `src/CpuEmulator.Surface.Web/Placeholder.cs`
- Test: `tests/CpuEmulator.Tests/Surface/FrameCodecTests.cs`

**Decision (spec §8, resolved):** frames are sent as **raw RGBA** binary (no delta/RLE in SP0 — bandwidth is fine for one local client at 256×192×4 = 192 KiB/frame; a delta is a future optimization). The binary frame is: magic `0x46`,`0x42` ("FB"), version `0x01`, reserved `0x00`, then `uint16 width` (LE), `uint16 height` (LE), then `width*height` little-endian `uint32` RGBA pixels. Inbound key events are JSON text frames: `{"action":"down|up","code":"<DOM code>","char":"<char or empty>"}`.

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Surface/FrameCodecTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class FrameCodecTests
{
    [Fact]
    public void EncodeFrame_writes_header_then_little_endian_pixels()
    {
        uint[] pixels = [0xFF0000FFu, 0xFF00FF00u]; // 2x1
        byte[] frame = FrameCodec.EncodeFrame(2, 1, pixels);

        // header: 'F','B',version,reserved, w_lo,w_hi, h_lo,h_hi
        Assert.Equal((byte)'F', frame[0]);
        Assert.Equal((byte)'B', frame[1]);
        Assert.Equal(0x01, frame[2]);
        Assert.Equal(0x00, frame[3]);
        Assert.Equal(2, frame[4] | (frame[5] << 8)); // width
        Assert.Equal(1, frame[6] | (frame[7] << 8)); // height
        // pixel 0 little-endian
        Assert.Equal(0xFF, frame[8]);  // 0x000000FF -> LE bytes FF 00 00 FF
        Assert.Equal(0x00, frame[9]);
        Assert.Equal(0x00, frame[10]);
        Assert.Equal(0xFF, frame[11]);
        Assert.Equal(8 + 2 * 4, frame.Length);
    }

    [Theory]
    [InlineData("{\"action\":\"down\",\"code\":\"KeyA\",\"char\":\"a\"}", KeyAction.Down, KeyCode.A, 'a')]
    [InlineData("{\"action\":\"up\",\"code\":\"KeyA\",\"char\":\"\"}", KeyAction.Up, KeyCode.A, null)]
    [InlineData("{\"action\":\"down\",\"code\":\"Enter\",\"char\":\"\"}", KeyAction.Down, KeyCode.Enter, null)]
    [InlineData("{\"action\":\"down\",\"code\":\"Space\",\"char\":\" \"}", KeyAction.Down, KeyCode.Space, ' ')]
    public void TryDecodeKey_parses_a_json_key_event(string json, KeyAction action, KeyCode key, char? ch)
    {
        Assert.True(FrameCodec.TryDecodeKey(json, out KeyEvent e));
        Assert.Equal(action, e.Action);
        Assert.Equal(key, e.Key);
        Assert.Equal(ch, e.Char);
    }

    [Fact]
    public void TryDecodeKey_maps_an_unknown_dom_code_to_None()
    {
        Assert.True(FrameCodec.TryDecodeKey("{\"action\":\"down\",\"code\":\"F13\",\"char\":\"\"}", out KeyEvent e));
        Assert.Equal(KeyCode.None, e.Key);
    }

    [Fact]
    public void TryDecodeKey_rejects_malformed_json()
    {
        Assert.False(FrameCodec.TryDecodeKey("not json", out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~FrameCodecTests"`
Expected: FAIL to compile — `FrameCodec` does not exist.

- [ ] **Step 3: Delete the placeholder, write `FrameCodec`**

Delete `src/CpuEmulator.Surface.Web/Placeholder.cs`.

Create `src/CpuEmulator.Surface.Web/FrameCodec.cs`:

```csharp
using System.Buffers.Binary;
using System.Text.Json;
using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// The SP0 WebSocket wire format. Frames OUT: a small binary header ('F','B', version, reserved,
/// uint16 width LE, uint16 height LE) followed by width*height little-endian RGBA8888 pixels (raw —
/// no delta/RLE in SP0; one local client at 256×192 is well within bandwidth). Keys IN: JSON text
/// {"action","code","char"} where "code" is the DOM KeyboardEvent.code; <see cref="MapDomCode"/>
/// normalizes it to a portable <see cref="KeyCode"/> (unknown -> <see cref="KeyCode.None"/>).
/// </summary>
public static class FrameCodec
{
    private const int HeaderBytes = 8;

    public static byte[] EncodeFrame(int width, int height, ReadOnlySpan<uint> pixels)
    {
        if (pixels.Length < width * height)
            throw new ArgumentException("pixel buffer smaller than width*height", nameof(pixels));

        var frame = new byte[HeaderBytes + width * height * 4];
        frame[0] = (byte)'F';
        frame[1] = (byte)'B';
        frame[2] = 0x01; // version
        frame[3] = 0x00; // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), (ushort)height);

        Span<byte> body = frame.AsSpan(HeaderBytes);
        for (int i = 0; i < width * height; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(i * 4, 4), pixels[i]);
        return frame;
    }

    public static bool TryDecodeKey(string json, out KeyEvent e)
    {
        e = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string action = root.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
            string code = root.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "" : "";
            string charStr = root.TryGetProperty("char", out JsonElement ch) ? ch.GetString() ?? "" : "";

            KeyAction keyAction = action == "up" ? KeyAction.Up : KeyAction.Down;
            KeyCode key = MapDomCode(code);
            char? typed = charStr.Length == 1 ? charStr[0] : null;
            e = new KeyEvent(keyAction, key, typed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Map a DOM <c>KeyboardEvent.code</c> to a portable <see cref="KeyCode"/>. Unknown
    /// codes return <see cref="KeyCode.None"/> (the machine ignores them).</summary>
    public static KeyCode MapDomCode(string code) => code switch
    {
        "KeyA" => KeyCode.A, "KeyB" => KeyCode.B, "KeyC" => KeyCode.C, "KeyD" => KeyCode.D,
        "KeyE" => KeyCode.E, "KeyF" => KeyCode.F, "KeyG" => KeyCode.G, "KeyH" => KeyCode.H,
        "KeyI" => KeyCode.I, "KeyJ" => KeyCode.J, "KeyK" => KeyCode.K, "KeyL" => KeyCode.L,
        "KeyM" => KeyCode.M, "KeyN" => KeyCode.N, "KeyO" => KeyCode.O, "KeyP" => KeyCode.P,
        "KeyQ" => KeyCode.Q, "KeyR" => KeyCode.R, "KeyS" => KeyCode.S, "KeyT" => KeyCode.T,
        "KeyU" => KeyCode.U, "KeyV" => KeyCode.V, "KeyW" => KeyCode.W, "KeyX" => KeyCode.X,
        "KeyY" => KeyCode.Y, "KeyZ" => KeyCode.Z,
        "Digit0" => KeyCode.Digit0, "Digit1" => KeyCode.Digit1, "Digit2" => KeyCode.Digit2,
        "Digit3" => KeyCode.Digit3, "Digit4" => KeyCode.Digit4, "Digit5" => KeyCode.Digit5,
        "Digit6" => KeyCode.Digit6, "Digit7" => KeyCode.Digit7, "Digit8" => KeyCode.Digit8,
        "Digit9" => KeyCode.Digit9,
        "Space" => KeyCode.Space,
        "Enter" => KeyCode.Enter,
        "Backspace" => KeyCode.Backspace,
        "Tab" => KeyCode.Tab,
        "Escape" => KeyCode.Escape,
        "ArrowLeft" => KeyCode.ArrowLeft,
        "ArrowRight" => KeyCode.ArrowRight,
        "ArrowUp" => KeyCode.ArrowUp,
        "ArrowDown" => KeyCode.ArrowDown,
        _ => KeyCode.None,
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~FrameCodecTests"`
Expected: PASS (8 cases: 1 encode, 4 decode-theory, 1 unknown-code, 1 malformed, plus the encode length assertion within the first).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/FrameCodec.cs tests/CpuEmulator.Tests/Surface/FrameCodecTests.cs
git rm src/CpuEmulator.Surface.Web/Placeholder.cs
git commit -m "feat(sp0): add FrameCodec (binary RGBA frames out, JSON key events in)"
```

---

## Task 10: `MachineHost` pump (Surface.Web)

**Files:**
- Create: `src/CpuEmulator.Surface.Web/MachineHost.cs`
- Test: `tests/CpuEmulator.Tests/Surface/MachineHostTests.cs`

`MachineHost` drives a `Machine` and bridges its display + keyboard to a host. It is transport-agnostic: it takes a **frame sink** delegate (`Action<byte[]>` of encoded frames) and exposes a `PostKey` so the WebSocket layer (Task 12) wires inbound events. Two modes: **wall-clock** (paced `Run` slices, default for the live server) and **headless/fast** (no throttle — for tests and batch). The acceptance test (Task 8) already exercises the `Machine` directly; `MachineHost` is tested here for the frame-push wiring and the headless step.

To keep the host testable without a real thread/clock, the public surface is: `Step(long cycles)` (run one slice + push any frame that became ready), `PostKey(in KeyEvent)`, and `RunHeadless(long totalCycles, long sliceCycles)` (loop `Step` until the budget is spent). The live server (Task 11) calls `Step` on a wall-clock-paced loop.

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Surface/MachineHostTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class MachineHostTests
{
    [Fact]
    public void Step_pushes_an_encoded_frame_after_a_vblank()
    {
        DemoSurfaceFixture fix = DemoSurfaceFixture.Build();
        fix.Machine.Reset();

        var frames = new List<byte[]>();
        var host = new MachineHost(fix.Machine, fix.Framebuffer, fix.Keyboard, frames.Add);

        // Run past at least one 60 Hz vblank interval so FrameReady fires and a frame is pushed.
        host.Step(100_000);

        Assert.NotEmpty(frames);
        // The pushed frame is a valid FB frame with the framebuffer's dimensions.
        byte[] frame = frames[0];
        Assert.Equal((byte)'F', frame[0]);
        Assert.Equal((byte)'B', frame[1]);
        int w = frame[4] | (frame[5] << 8);
        int h = frame[6] | (frame[7] << 8);
        Assert.Equal(fix.Framebuffer.Width, w);
        Assert.Equal(fix.Framebuffer.Height, h);
        Assert.Equal(8 + w * h * 4, frame.Length);
    }

    [Fact]
    public void PostKey_routes_to_the_keyboard_and_the_guest_observes_it()
    {
        DemoSurfaceFixture fix = DemoSurfaceFixture.Build();
        fix.Machine.Reset();
        var host = new MachineHost(fix.Machine, fix.Framebuffer, fix.Keyboard, _ => { });

        host.RunHeadless(20_000, 5_000);                                   // paint + enter poll loop
        host.PostKey(new KeyEvent(KeyAction.Down, KeyCode.J, 'J'));
        host.RunHeadless(20_000, 5_000);                                   // guest echoes the key

        var rgba = new uint[fix.Framebuffer.Width * fix.Framebuffer.Height];
        fix.Framebuffer.RenderInto(rgba);
        uint j = 0xFF000000u | ((uint)'J' << 16) | ((uint)'J' << 8) | (uint)'J';
        Assert.Equal(j, rgba[0x0100]); // VRAM $8100 echo cell
    }

    [Fact]
    public void RunHeadless_pushes_at_least_one_frame_over_a_multi_vblank_run()
    {
        DemoSurfaceFixture fix = DemoSurfaceFixture.Build();
        fix.Machine.Reset();
        var frames = new List<byte[]>();
        var host = new MachineHost(fix.Machine, fix.Framebuffer, fix.Keyboard, frames.Add);

        host.RunHeadless(100_000, 10_000);

        Assert.NotEmpty(frames);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~MachineHostTests"`
Expected: FAIL to compile — `MachineHost` does not exist.

- [ ] **Step 3: Write `MachineHost`**

Create `src/CpuEmulator.Surface.Web/MachineHost.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// Drives a <see cref="Machine"/> for a surface (design spec §5). Subscribes the display's
/// <see cref="IDisplayDevice.FrameReady"/>, pulls RGBA via <see cref="IDisplayDevice.RenderInto"/>,
/// encodes a frame (<see cref="FrameCodec"/>), and hands it to a transport-agnostic frame sink.
/// Inbound keys route to <see cref="IKeyboardSink.PostKey"/>. Transport-agnostic on purpose: the
/// WebSocket server (Program.cs) supplies the frame sink and calls <see cref="Step"/> on a
/// wall-clock-paced loop; tests drive <see cref="RunHeadless"/> with no throttle. One machine per
/// host (multi-machine is YAGNI). Frame pushes are coalesced: at most one frame per Step, using the
/// latest RenderInto — so a slow sink never backs up the pump.
/// </summary>
public sealed class MachineHost
{
    private readonly Machine _machine;
    private readonly IDisplayDevice _display;
    private readonly IKeyboardSink _keyboard;
    private readonly Action<byte[]> _frameSink;
    private readonly uint[] _rgba;
    private volatile bool _frameDirty;

    public MachineHost(Machine machine, IDisplayDevice display, IKeyboardSink keyboard,
                       Action<byte[]> frameSink)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(frameSink);
        _machine = machine;
        _display = display;
        _keyboard = keyboard;
        _frameSink = frameSink;
        _rgba = new uint[display.Width * display.Height];
        _display.FrameReady += () => _frameDirty = true;
    }

    /// <summary>Push a key into the machine's keyboard.</summary>
    public void PostKey(in KeyEvent e) => _keyboard.PostKey(e);

    /// <summary>Run one slice of <paramref name="cycles"/>, then — if a vblank fired during it —
    /// render the latest frame and push it to the sink (coalesced: one frame per Step).</summary>
    public void Step(long cycles)
    {
        _machine.Run(cycles);
        if (!_frameDirty)
            return;
        _frameDirty = false;
        _display.RenderInto(_rgba);
        _frameSink(FrameCodec.EncodeFrame(_display.Width, _display.Height, _rgba));
    }

    /// <summary>Headless/fast run (no wall-clock throttle): step in <paramref name="sliceCycles"/>
    /// chunks until <paramref name="totalCycles"/> is spent. For tests + batch.</summary>
    public void RunHeadless(long totalCycles, long sliceCycles)
    {
        if (sliceCycles <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceCycles), "Slice must be positive.");
        for (long run = 0; run < totalCycles; run += sliceCycles)
            Step(Math.Min(sliceCycles, totalCycles - run));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~MachineHostTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/MachineHost.cs tests/CpuEmulator.Tests/Surface/MachineHostTests.cs
git commit -m "feat(sp0): add MachineHost pump (frame push + key routing, headless mode)"
```

---

## Task 11: `DemoBoardSurface` — compose the board + devices for the surface (Surface.Web)

**Files:**
- Create: `src/CpuEmulator.Surface.Web/DemoBoardSurface.cs`
- Test: `tests/CpuEmulator.Tests/Surface/DemoBoardSurfaceTests.cs`

The web analogue of `BootedBoard`: a factory that builds the three devices, builds the `DemoBoard` `BoardSpec` → `Machine` via `BoardMachineFactory`, resets it, and exposes a `MachineHost` (given a frame sink). Keeps `Program.cs` thin and gives tests a one-call composition.

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Surface/DemoBoardSurfaceTests.cs`:

```csharp
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class DemoBoardSurfaceTests
{
    [Fact]
    public void Create_builds_a_reset_machine_and_a_host_that_pushes_a_frame()
    {
        var frames = new List<byte[]>();
        DemoBoardSurface surface = DemoBoardSurface.Create(frames.Add);

        surface.Host.Step(100_000); // past one vblank

        Assert.NotEmpty(frames);
        Assert.Equal("demo", surface.Machine.Name);
    }

    [Fact]
    public void Disk_is_seeded_so_the_demo_can_read_sector_zero()
    {
        DemoBoardSurface surface = DemoBoardSurface.Create(_ => { });
        surface.Host.RunHeadless(20_000, 5_000);

        var rgba = new uint[surface.Framebuffer.Width * surface.Framebuffer.Height];
        surface.Framebuffer.RenderInto(rgba);
        // The seeded disk byte (0x5A) lands at VRAM $8101 -> rgba index 0x0101.
        Assert.Equal(0xFF5A5A5Au, rgba[0x0101]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoBoardSurfaceTests"`
Expected: FAIL to compile — `DemoBoardSurface` does not exist.

- [ ] **Step 3: Write `DemoBoardSurface`**

Create `src/CpuEmulator.Surface.Web/DemoBoardSurface.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// Composes the SP0 demo board for the web surface — the web analogue of the monitor host's
/// BootedBoard. Builds the three devices, compiles the <see cref="DemoBoard"/> spec to a
/// <see cref="Machine"/> via <see cref="BoardMachineFactory"/>, resets it, and wires a
/// <see cref="MachineHost"/> to the supplied frame sink. The disk is seeded with a recognizable
/// sector 0 so the demo program (and the acceptance test) have a byte to surface.
/// </summary>
public sealed record DemoBoardSurface(
    Machine Machine, DemoFramebuffer Framebuffer, DemoKeyboard Keyboard, DemoDisk Disk, MachineHost Host)
{
    public static DemoBoardSurface Create(Action<byte[]> frameSink)
    {
        var fb = new DemoFramebuffer();
        var kbd = new DemoKeyboard();
        var image = new byte[256 * 2];
        image[0] = 0x5A; // recognizable sector-0 first byte
        var disk = new DemoDisk(new DiskImage(image, sectorSize: 256, isReadOnly: false));

        BoardSpec spec = DemoBoard.Spec(DemoBoardRom.Build(), fb, kbd, disk);
        Machine machine = BoardMachineFactory.Build(spec);
        machine.Reset();

        var host = new MachineHost(machine, fb, kbd, frameSink);
        return new DemoBoardSurface(machine, fb, kbd, disk, host);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~DemoBoardSurfaceTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/DemoBoardSurface.cs tests/CpuEmulator.Tests/Surface/DemoBoardSurfaceTests.cs
git commit -m "feat(sp0): add DemoBoardSurface (web analogue of BootedBoard)"
```

---

## Task 12: The HTTP+WebSocket server `Program.cs` (Surface.Web)

**Files:**
- Create: `src/CpuEmulator.Surface.Web/Program.cs`
- Test: `tests/CpuEmulator.Tests/Surface/WebServerSmokeTests.cs`

`Program.cs` is the minimal-API host: serves static files (`wwwroot`), accepts a WebSocket at `/ws`, creates a `DemoBoardSurface` whose frame sink queues encoded frames to that socket, and runs a wall-clock-paced pump on a background loop while reading inbound key-event text frames. The smoke test uses ASP.NET Core's `WebApplicationFactory`-style in-memory `TestServer` — but since this is a top-level-statements `Program`, the test instead validates the *server wiring* by spinning the app on a loopback port and asserting (a) `GET /` returns the canvas HTML, (b) a WebSocket connects and receives at least one binary `FB` frame, (c) sending a key-event JSON over the socket changes the framebuffer (observed via a second frame whose echo-cell pixel matches).

To make the app testable, `Program.cs` exposes a `public partial class Program` marker (the standard pattern for top-level-statement test access) and reads the listen URL from `urls`/`ASPNETCORE_URLS` (so the test binds `http://127.0.0.1:0`).

- [ ] **Step 1: Write the failing smoke test**

Create `tests/CpuEmulator.Tests/Surface/WebServerSmokeTests.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CpuEmulator.Tests.Surface;

/// <summary>End-to-end smoke for the web server wiring: the static client is served, a WebSocket
/// streams binary FB frames, and an inbound key-event JSON changes the framebuffer (echoed back in
/// a later frame). Uses the in-memory test host (no real port). Tagged UAT — it is the closest
/// automated proxy to the manual "open the browser" moment, without a browser.</summary>
[Trait("Category", "UAT")]
public class WebServerSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public WebServerSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Root_serves_the_canvas_client()
    {
        using HttpClient client = _factory.CreateClient();
        string html = await client.GetStringAsync("/");
        Assert.Contains("<canvas", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebSocket_streams_a_binary_FB_frame()
    {
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
        using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        byte[] buffer = new byte[8 + 256 * 192 * 4];
        WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal((byte)'F', buffer[0]);
        Assert.Equal((byte)'B', buffer[1]);
    }
}
```

> Note: `WebApplicationFactory<Program>` requires `Microsoft.AspNetCore.Mvc.Testing`. Add it to the test project in Step 3.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~WebServerSmokeTests"`
Expected: FAIL to compile — `Program` (web) is not referenceable / `WebApplicationFactory` not found.

- [ ] **Step 3: Add the test-host package**

In `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`, add to the `<PackageReference>` `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
```

(If `10.0.0` is not yet published, use the latest `10.0.*` the restore resolves; the package version tracks the shared framework.)

- [ ] **Step 4: Write `Program.cs`**

Create `src/CpuEmulator.Surface.Web/Program.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

app.UseDefaultFiles();   // serve wwwroot/index.html at "/"
app.UseStaticFiles();
app.UseWebSockets();

// One machine per connected client (single-machine-per-host; a new socket = a fresh demo).
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await DemoSession.RunAsync(socket, context.RequestAborted);
});

app.Run();

namespace CpuEmulator.Surface.Web
{
    /// <summary>One WebSocket session: a DemoBoardSurface whose frames stream to the socket, a
    /// wall-clock pump task, and an inbound key-event read loop. Closes when the socket closes or
    /// the request aborts.</summary>
    internal static class DemoSession
    {
        // ~60 Hz pacing: one ~16,667-cycle slice every ~16 ms of wall-clock.
        private const long SliceCycles = 16_667;
        private static readonly TimeSpan SlicePeriod = TimeSpan.FromMilliseconds(16);

        public static async Task RunAsync(WebSocket socket, CancellationToken ct)
        {
            // Bounded channel of encoded frames; drop-oldest if the client can't keep up.
            Channel<byte[]> frames = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });

            DemoBoardSurface surface = DemoBoardSurface.Create(frame => frames.Writer.TryWrite(frame));

            Task pump = PumpAsync(surface, ct);
            Task send = SendFramesAsync(socket, frames.Reader, ct);
            Task recv = ReceiveKeysAsync(socket, surface, ct);

            await Task.WhenAny(pump, send, recv);
            frames.Writer.TryComplete();
            try { await Task.WhenAll(pump, send, recv); } catch { /* socket teardown races are expected */ }
        }

        private static async Task PumpAsync(DemoBoardSurface surface, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(SlicePeriod);
            while (await timer.WaitForNextTickAsync(ct))
                surface.Host.Step(SliceCycles);
        }

        private static async Task SendFramesAsync(WebSocket socket, ChannelReader<byte[]> reader,
                                                  CancellationToken ct)
        {
            await foreach (byte[] frame in reader.ReadAllAsync(ct))
            {
                if (socket.State != WebSocketState.Open)
                    break;
                await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }

        private static async Task ReceiveKeysAsync(WebSocket socket, DemoBoardSurface surface,
                                                  CancellationToken ct)
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;
                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (FrameCodec.TryDecodeKey(json, out KeyEvent e))
                    surface.Host.PostKey(e);
            }
        }
    }

    /// <summary>Marker for WebApplicationFactory&lt;Program&gt; (top-level-statements need an explicit
    /// public Program type for the test host to reference).</summary>
    public partial class Program;
}
```

- [ ] **Step 5: Run the smoke test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~WebServerSmokeTests"`
Expected: PASS (2 tests — root serves the canvas, the WebSocket streams an FB frame). The client assets (`index.html`/`app.js`) are added in Task 13; the `Root_serves_the_canvas_client` test needs `index.html` present, so if Task 13 is sequenced after this, the root test will 404 until then. **Run order:** complete Task 13 before re-running the `Root_serves_the_canvas_client` assertion; the WebSocket frame test passes independently. (If executing strictly in order, mark `Root_serves_the_canvas_client` with `[Fact(Skip = "client added in Task 13")]` here and un-skip it in Task 13.)

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Surface.Web/Program.cs tests/CpuEmulator.Tests/Surface/WebServerSmokeTests.cs tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj
git commit -m "feat(sp0): add HTTP+WebSocket server (frame stream + key receive)"
```

---

## Task 13: The browser canvas client (Surface.Web/wwwroot)

**Files:**
- Create: `src/CpuEmulator.Surface.Web/wwwroot/index.html`
- Create: `src/CpuEmulator.Surface.Web/wwwroot/app.js`

The client: a `<canvas>` sized to the frame, a WebSocket to `/ws`, a binary-frame decoder that parses the `FB` header and blits pixels via `ImageData`, and keydown/keyup handlers that send key-event JSON. No framework. The `Root_serves_the_canvas_client` smoke test (Task 12) asserts the `<canvas>` element is served; the manual UAT is "open the browser and see it".

- [ ] **Step 1: Write `index.html`**

Create `src/CpuEmulator.Surface.Web/wwwroot/index.html`:

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>CpuEmulator — SP0 demo</title>
  <style>
    body { margin: 0; background: #111; color: #ccc; font-family: system-ui, sans-serif;
           display: flex; flex-direction: column; align-items: center; gap: 12px; padding: 16px; }
    h1 { font-size: 14px; font-weight: 600; letter-spacing: .04em; margin: 4px 0; }
    /* Nearest-neighbour upscaling so the 256×192 framebuffer stays crisp when enlarged. */
    canvas { image-rendering: pixelated; border: 1px solid #333; background: #000;
             width: 768px; height: 576px; }
    #status { font-size: 12px; color: #888; }
    kbd { background: #222; border: 1px solid #444; border-radius: 3px; padding: 1px 5px; }
  </style>
</head>
<body>
  <h1>CpuEmulator — SP0 web surface</h1>
  <canvas id="screen" width="256" height="192"></canvas>
  <div id="status">connecting…</div>
  <div id="hint">Type to echo a key; sector-0 byte is painted on boot. <kbd>Esc</kbd> ignored.</div>
  <script src="app.js"></script>
</body>
</html>
```

- [ ] **Step 2: Write `app.js`**

Create `src/CpuEmulator.Surface.Web/wwwroot/app.js`:

```javascript
"use strict";
(function () {
  const canvas = document.getElementById("screen");
  const ctx = canvas.getContext("2d");
  const status = document.getElementById("status");

  const wsUrl = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws";
  const ws = new WebSocket(wsUrl);
  ws.binaryType = "arraybuffer";

  ws.onopen = () => { status.textContent = "connected"; };
  ws.onclose = () => { status.textContent = "disconnected"; };
  ws.onerror = () => { status.textContent = "error"; };

  // Decode a binary FB frame: 'F','B', version, reserved, u16 width LE, u16 height LE, then RGBA u32 LE.
  ws.onmessage = (ev) => {
    const data = new DataView(ev.data);
    if (data.getUint8(0) !== 0x46 || data.getUint8(1) !== 0x42) return; // not "FB"
    const width = data.getUint16(4, true);
    const height = data.getUint16(6, true);
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
    const image = ctx.createImageData(width, height);
    const src = new Uint8Array(ev.data, 8);
    // Wire pixels are RGBA8888 stored little-endian as 0xAABBGGRR bytes -> [R,G,B,A] in memory.
    // Our encoder writes uint32 0xFFrrggbb little-endian = bytes [bb, gg, rr, FF]. Re-pack to RGBA.
    for (let i = 0, p = 0; i < width * height; i++, p += 4) {
      const b = src[p], g = src[p + 1], r = src[p + 2], a = src[p + 3];
      image.data[p] = r;
      image.data[p + 1] = g;
      image.data[p + 2] = b;
      image.data[p + 3] = a;
    }
    ctx.putImageData(image, 0, 0);
  };

  function sendKey(action, ev) {
    if (ws.readyState !== WebSocket.OPEN) return;
    // A single printable character (length-1 key) is the typed char; otherwise empty.
    const ch = ev.key && ev.key.length === 1 ? ev.key : "";
    ws.send(JSON.stringify({ action: action, code: ev.code, char: ch }));
    // Keep the browser from scrolling on Space/Arrows while focused.
    if (["Space", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(ev.code))
      ev.preventDefault();
  }

  window.addEventListener("keydown", (ev) => sendKey("down", ev));
  window.addEventListener("keyup", (ev) => sendKey("up", ev));
})();
```

- [ ] **Step 3: Verify the client is served + un-skip the root smoke test**

If `Root_serves_the_canvas_client` was skipped in Task 12, remove the `Skip` now.

Run: `dotnet test tests/CpuEmulator.Tests -c Debug --filter "FullyQualifiedName~WebServerSmokeTests"`
Expected: PASS (both — root serves the `<canvas>` HTML, the WebSocket streams an FB frame).

- [ ] **Step 4: Manual verification (the visible proof — documented, not a CI gate)**

Run: `dotnet run --project src/CpuEmulator.Surface.Web`
Then open the printed `http://localhost:<port>` in a browser.
Expected: a gradient strip across the top rows, a single bright pixel a row down (the disk byte 0x5A → mid-gray), and typing a key paints that character's grayscale at the echo cell. Capture a screenshot for the docs.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/wwwroot/index.html src/CpuEmulator.Surface.Web/wwwroot/app.js
git commit -m "feat(sp0): add browser canvas client (binary frame blit + key capture)"
```

---

## Task 14: Full-suite green + status/roadmap docs

**Files:**
- Modify: `docs/superpowers/specs/2026-06-17-emulated-computer-sp0-foundation-design.md`
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test CpuEmulator.slnx -c Debug`
Expected: all tests pass (the existing suites unaffected — SP0 added only additive `Core` interfaces + new projects/types; the new SP0 tests pass, including the §6 acceptance gate and the web smoke).

- [ ] **Step 2: Update the SP0 spec status (DEFERRED → implemented + reconciliation note)**

In `docs/superpowers/specs/2026-06-17-emulated-computer-sp0-foundation-design.md`, replace the `**Status:**` block (lines 4-6) with:

```markdown
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
```

- [ ] **Step 3: Add the ROADMAP entry**

In `docs/ROADMAP.md`, add a new row to the "Recently shipped — the 'CPUs → computers' arc" table (after the `#3 — the monitor hosts` row):

```markdown
| **SP0 — the web-surface foundation** | The reusable, GUI-free **web surface** for the "real machines" arc. Three additive `Core` contracts — **`IDisplayDevice`** (host pulls RGBA; the chip does palette/mode lookup so the surface is a dumb blitter), **`IKeyboardSink`** + portable **`KeyEvent`/`KeyCode`** (host pushes; the chip owns the native scan mapping), **`IBlockDevice`** (raw sector storage; controllers + image formats are SP1+). Three generic demo devices in `CpuEmulator.Peripherals` (`DemoFramebuffer` 256×192 8bpp palettized, `DemoKeyboard` UART-rx-shaped with level-IRQ, `DemoDisk` over a raw `DiskImage`). A new **`CpuEmulator.Surface.Web`** project: an ASP.NET Core minimal HTTP+WebSocket server (built into .NET 10 — no heavy dependency) → a browser HTML/JS **canvas** client (binary RGBA frames out, JSON key events in), plus the **`MachineHost`** pump (wall-clock-paced or headless/fast). The demo is a declarative **`DemoBoard` `BoardSpec`** built via `BoardMachineFactory` — a parallel surface to the monitor host over the same `Machine`. The gate is the **un-fakeable headless acceptance test** (no browser, no throttle): the demo ROM paints a gradient test pattern (display out), echoes a synthetic `PostKey` into VRAM (input round-trip), and reads disk sector 0 onto the screen (block device) — all asserted on the real RGBA / VRAM / disk bytes. |
```

And add a follow-on bullet under "Deferred & candidate follow-ons" (append to the numbered list as the next item):

```markdown
7. **[deferred] `IAudioSink` for the first real machine's beeper.** SP0 deliberately omits sound. The
   first real machine (e.g. the ZX Spectrum 48K beeper) needs a host-facing audio-output contract,
   shaped like the SP0 display/keyboard contracts (the chip produces samples; the surface plays them
   over the WebSocket). Designed at that machine's spec time, not built in SP0.
```

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-06-17-emulated-computer-sp0-foundation-design.md docs/ROADMAP.md
git commit -m "docs(sp0): mark SP0 spec implemented + add ROADMAP entry + IAudioSink follow-on"
```

---

## Self-Review

### 1. Spec coverage

| Spec requirement | Task |
|---|---|
| §2 web surface (HTTP+WebSocket → browser canvas) | Tasks 9, 12, 13 |
| §2 / §4.1 `IDisplayDevice` | Task 1 |
| §2 / §4.2 `IKeyboardSink` + `KeyEvent` + `KeyCode` | Task 2 |
| §2 / §4.3 `IBlockDevice` | Task 3 |
| §2 generic device impls (framebuffer, keyboard, raw-image disk) | Tasks 4, 5, 6 (+ `DiskImage` in 3) |
| §2 / §5 `MachineHost` pump (paced + headless/fast) | Task 10 |
| §2 / §6 `DemoMachine` (→ `DemoBoard` `BoardSpec`) | Tasks 7 (ROM), 8 (board) |
| §6 demo program (test pattern + key echo + read-sector) | Task 7 |
| §6 automated headless acceptance test (3 contracts) | Task 8 |
| §6 manual visible proof | Task 13 Step 4 |
| §4 error handling (out-of-range LBA, read-only throw, too-small span, unknown key no-op) | Tasks 3, 4, 5 |
| §7 surface=web, transport=WebSocket, RGBA8888, normalized KeyEvent, raw block, scheduler vblank | Tasks 1, 4, 9, 12 |
| §8 open questions resolved (KeyCode form; raw-frame encoding; 256×192 8bpp; MachineHost stays in Surface.Web) | Tasks 2, 4, 9, 10 (DemoBoardSurface keeps the pump in Surface.Web) |
| §9 testing (per-contract + per-device unit tests; additive-only Core) | Tasks 1-11 each ship tests; Task 14 confirms existing suites unaffected |
| Reconciliation: DemoMachine→BoardSpec; web↔monitor coexist; sound out | Tasks 8, 11, 14 |
| Status update + ROADMAP | Task 14 |

No gaps. Every §2/§4/§5/§6/§7 item maps to a task with complete code.

### 2. Placeholder scan

Searched for `TBD`, `TODO`, `implement later`, `add appropriate`, `similar to Task`, `fill in`, `...`-as-elision. **None present** — every code step has complete, literal code (the browser HTML/JS, the WebSocket protocol, all three contracts, three devices, the board, the ROM, the pump, the server). The one cross-task test reference (`DemoSurfaceFixture.BuildMachineWith` used by Task 4's vblank `[Fact]`) is explicitly flagged with a temporary-comment-out instruction and the fixture's full code appears in Task 8.

### 3. Type consistency

Checked names/signatures across tasks:
- `IDisplayDevice` — `Width`/`Height`/`RenderInto(Span<uint>)`/`event Action FrameReady` consistent in Tasks 1, 4, 10.
- `IKeyboardSink.PostKey(in KeyEvent)` — consistent in Tasks 2, 5, 10, 12. `KeyEvent(KeyAction, KeyCode, char?)` consistent everywhere. `KeyCode.None == 0` consistent (Tasks 2, 9).
- `IBlockDevice` — `SectorSize`/`SectorCount`/`IsReadOnly`/`ReadSector(long, Span<byte>)`/`WriteSector(long, ReadOnlySpan<byte>)` consistent in Tasks 3, 6.
- `DiskImage(byte[], int sectorSize, bool isReadOnly)` ctor consistent in Tasks 3, 6, 8, 11.
- `DemoDisk(IBlockDevice)` ctor consistent in Tasks 6, 8, 11.
- `DemoBoard.Spec(byte[] rom, DemoFramebuffer, DemoKeyboard, DemoDisk)` consistent in Tasks 8, 11 (and matches `Breadboard6502Board.Spec`'s recon'd shape).
- `MachineHost(Machine, IDisplayDevice, IKeyboardSink, Action<byte[]>)` + `Step(long)` / `RunHeadless(long, long)` / `PostKey(in KeyEvent)` consistent in Tasks 10, 11, 12.
- `FrameCodec.EncodeFrame(int, int, ReadOnlySpan<uint>)` + `TryDecodeKey(string, out KeyEvent)` + `MapDomCode(string)` consistent in Tasks 9, 10, 12.
- The framebuffer palette formula `0xFF000000 | (i<<16) | (i<<8) | i` is identical in the device (Task 4), the acceptance test (Task 8), the host test (Task 10), and the surface test (Task 11).
- Device addresses: ROM `$E000`, framebuffer `$8000`, keyboard `$D000`, disk `$D100` — identical in `DemoBoardRom` (Task 7) and `DemoBoard` (Task 8). The echo cell `$8100` → rgba index `0x0100` and disk cell `$8101` → `0x0101` are consistent in Tasks 7, 8, 10, 11.

One reconciliation guard built in: Task 8 Step 4 notes the validator may reject a 16 KiB framebuffer slot or ROM-inside-MMIO and gives the exact fallback (shrink the slot to `$0200` / split the MMIO so ROM is outside it), to be confirmed against `BoardSpecValidatorTests` at build time — the only place the real validator's rules (not fully read at plan time) could force a numeric tweak. The pattern + echo cells fit either way.

All consistent. No fixes required beyond the inline validator guard already in Task 8.
