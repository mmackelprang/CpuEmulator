# PR-M — `DisplayMultiplexer` + `MachineHost` per-frame re-size Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the **active/overriding-display-source seam** (ADR 0016 Decision 1): a host-side `DisplayMultiplexer : IDisplayDevice` that wraps N underlying `IDisplayDevice` sources and delegates `Width`/`Height`/`RenderInto`/`FrameReady` to whichever source is currently **active**; plus the one required `MachineHost` change — re-size its `_rgba` buffer when the active source's dimensions change (so switching from a 40-col 280×192 Apple source to an 80-col Videx source re-pulls at the new geometry). The single-source (non-multiplexed) path stays byte-for-byte unchanged. This is the seam PR-N's Videx plugs into; **this PR ships only the multiplexer + the host re-size + their gates — no Videx.**

**Architecture:** Per ADR 0016 Decision 1. Two additive changes:

1. **`DisplayMultiplexer` (new, `CpuEmulator.Core`, alongside `IDisplayDevice`):** an `IDisplayDevice` that holds an ordered list of source `IDisplayDevice`s and an active index. `Width`/`Height`/`RenderInto` delegate to the active source; `FrameReady` forwards the active source's `FrameReady` **and** fires on `SetActive` (so the surface re-pulls at the new size when the source switches). The active source is chosen by a guest-driven signal — `SetActive(int)` — which PR-N's Videx will call from its `$C800`-enable state (ADR 0016 Decision 2). This PR ships only the multiplexer and `SetActive`; the Videx caller is PR-N.
2. **`MachineHost` re-size (modify, `CpuEmulator.Surface.Web`):** `MachineHost` sizes `_rgba` once at construction from `display.Width * display.Height` (`MachineHost.cs:43`). When the active source changes size, that fixed buffer is wrong. The change: on each frame render, if `display.Width * display.Height` differs from the current `_rgba` length, **reallocate** `_rgba` to the new size before `RenderInto`. This is a strict superset of the current behavior — identical when dimensions never change (every shipped single-display board), a one-time realloc on the rare source switch. The `FrameCodec.EncodeFrame(width, height, rgba)` call already carries per-frame width/height (`MachineHost.cs:68`), so the client already handles changing dimensions; only the host buffer follows.

**Tech Stack:** C# / .NET 10, `CpuEmulator.Core` (`IDisplayDevice`, the new `DisplayMultiplexer`), `CpuEmulator.Surface.Web` (`MachineHost`, `FrameCodec`), xUnit (`tests/CpuEmulator.Tests`).

## Global Constraints

- **The single-source path MUST stay byte-for-byte unchanged.** Every shipped surface (`SpectrumSurface`, `Apple2Surface`, `DemoBoardSurface`) constructs `MachineHost` directly over one `IDisplayDevice` whose dimensions never change. After this PR they must behave identically — the re-size check is a no-op when the size never differs. This is the load-bearing regression (Task 4).
- **`DisplayMultiplexer` lives in `CpuEmulator.Core`** (ADR 0016 Decision 1: "additive; alongside IDisplayDevice"). It is pure host-side glue — no guest/CPU coupling, no scheduler.
- **The multiplexer is transparent for a single source.** A `DisplayMultiplexer` built with one source behaves exactly like that source (Task 1's transparency test) — so a board that wants the seam without multiple displays pays nothing.
- **No Videx in this PR.** `SetActive` is the seam PR-N's Videx will drive; this PR ships the mechanism + gates with **test-double display sources** (a tiny `FakeDisplay` of a given size). N depends on M shipping.
- **The host re-size is on `Width`/`Height` change only** (ADR 0016 Decision 1 / OQ4 — the compare-and-realloc, not a max-of-all-sources pre-size). A per-render size compare + realloc-only-on-change.
- **No `TimingTier` / `ITimingSensitive`** (ADR-only, not in `src/`).
- **HEAD grounding:** all literal code is grounded against `main` @ `10f5737` (PRs #99–#111 merged). Verify with `git rev-parse HEAD` before starting.

---

## Recon facts this plan is built on (verified against `main` @ `10f5737`)

1. **`IDisplayDevice`** (`src/CpuEmulator.Core/IDisplayDevice.cs`) is `{ int Width; int Height; void RenderInto(Span<uint> rgba); event Action? FrameReady; }`. `Width`/`Height` are documented as "may change with video mode" — the multiplexer + the host re-size make that real across sources.
2. **`MachineHost`** (`src/CpuEmulator.Surface.Web/MachineHost.cs`) holds `private readonly uint[] _rgba;` (line 20), sizes it `new uint[display.Width * display.Height]` at ctor (line 43), subscribes `_display.FrameReady += () => _frameDirty = true;` (line 44), and in `Step` (lines 60–77) does `_display.RenderInto(_rgba); _frameSink(FrameCodec.EncodeFrame(_display.Width, _display.Height, _rgba));` when `_frameDirty`. The `_rgba` field is `readonly` — **the re-size requires dropping `readonly`** (it is reassigned on a size change). This is the only field-mutability change.
3. **`FrameCodec.EncodeFrame(int width, int height, uint[] rgba)`** is called with the live `_display.Width`/`_display.Height` per frame, so the wire frame already carries the current geometry — the client re-sizes its canvas from the frame header (no client change needed for M).
4. **Both shipped display devices change nothing:** `Apple2Video : IPeripheral, IDisplayDevice` is fixed 280×192 (`Width280`/`Height192`, `src/CpuEmulator.Peripherals/Apple2Video.cs:24-25`); `SpectrumUla : IPeripheral, IDisplayDevice, IKeyboardSink, IAudioSink` is fixed 256×192. Neither resizes; the host re-size is inert for both (the regression gate).
5. **`MachineHost.RunHeadless(totalCycles, sliceCycles)`** (lines 81–87) drives the host headless for tests. But M's host re-size gate does not need a real machine — it needs a frame to render, which `Step` does when `_frameDirty`. The gate drives a **fake machine + fake display** so the size-change is deterministic (Task 3).
6. **The multiplexer's `FrameReady` must fire on `SetActive`** (ADR 0016 Decision 1) so the host's `_frameDirty` flips and the next `Step` re-pulls at the new size. The multiplexer also forwards the **active** source's `FrameReady` (each source raises its own vblank; only the active one's frames reach the host).

---

## Conventions to follow

- **Mirror the shipped `IDisplayDevice` contract exactly** — the multiplexer IS an `IDisplayDevice` (the surface treats it as one device, ADR 0016 Decision 1).
- **Additive only** — `DisplayMultiplexer` is a new file; the `MachineHost` change is the minimal re-size (drop `readonly`, add a size-compare + realloc before `RenderInto`). No surface (`Apple2Surface`/`SpectrumSurface`) changes — they keep passing one device; PR-N is the first caller that passes a multiplexer.
- **TDD per task**, literal code, commit per task. Warning-clean. **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter ...`.

---

## File Structure

### `CpuEmulator.Core`
- **Create** `src/CpuEmulator.Core/DisplayMultiplexer.cs` — the `IDisplayDevice` that delegates to the active of N sources; `SetActive(int)` switches + fires `FrameReady`.

### `CpuEmulator.Surface.Web`
- **Modify** `src/CpuEmulator.Surface.Web/MachineHost.cs` — drop `readonly` on `_rgba`; re-size it before `RenderInto` when the active display's `Width*Height` changed.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/DisplayMultiplexerTests.cs` — delegation, switch-fires-FrameReady, source-FrameReady forwarding, single-source transparency, bounds.
- **Create** `tests/CpuEmulator.Tests/MachineHostResizeTests.cs` — the host re-pulls at the new size when the active source changes size (the un-fakeable gate) + the single-source-unchanged regression.

---

## Task 1: `DisplayMultiplexer` — the active-source delegating `IDisplayDevice`

**Files:**
- Create: `src/CpuEmulator.Core/DisplayMultiplexer.cs`
- Test: `tests/CpuEmulator.Tests/DisplayMultiplexerTests.cs`

**Interfaces:**
- Consumes: `IDisplayDevice` (shipped).
- Produces: `sealed class DisplayMultiplexer : IDisplayDevice`, ctor `DisplayMultiplexer(IReadOnlyList<IDisplayDevice> sources, int initialActive = 0)`. `SetActive(int index)`. `Width`/`Height`/`RenderInto` delegate to the active source. `FrameReady` forwards the active source's event and fires on `SetActive`.

**Design notes (grounded against `IDisplayDevice.cs` + ADR 0016 Decision 1's sketch):**
- The multiplexer subscribes **every** source's `FrameReady` at construction (a source can raise its vblank whenever), but only **re-raises** its own `FrameReady` when the source that fired is the **active** one — a dormant source's frames are dropped (the host only ever pulls the active source, and pulling an inactive source's frame would render the wrong geometry). This matches "Width/Height/RenderInto/FrameReady delegate to the active source."
- `SetActive` validates the index, swaps the active source, and **always** raises `FrameReady` (even if the index is unchanged is acceptable, but guard the no-op for cleanliness: only raise when the index actually changes — the host re-pull is only needed on a real switch). The plan raises on an **actual change**.
- Single-source transparency: with one source, the active source is index 0, every `FrameReady` from it forwards, and `SetActive(0)` is a no-op — identical to using the source directly.

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/DisplayMultiplexerTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class DisplayMultiplexerTests
{
    // A minimal IDisplayDevice test double of a fixed size that records RenderInto + can raise FrameReady.
    private sealed class FakeDisplay(int width, int height, uint fill) : IDisplayDevice
    {
        public int Width => width;
        public int Height => height;
        public int RenderCalls { get; private set; }
        public void RenderInto(Span<uint> rgba)
        {
            if (rgba.Length < Width * Height)
                throw new ArgumentException($"need {Width * Height}; got {rgba.Length}", nameof(rgba));
            RenderCalls++;
            rgba[..(Width * Height)].Fill(fill);
        }
        public event Action? FrameReady;
        public void RaiseFrame() => FrameReady?.Invoke();
    }

    [Fact]
    public void Delegates_dimensions_and_render_to_the_active_source()
    {
        var a = new FakeDisplay(280, 192, 0xFF111111);
        var b = new FakeDisplay(720, 216, 0xFF222222);
        var mux = new DisplayMultiplexer([a, b], initialActive: 0);

        Assert.Equal(280, mux.Width);
        Assert.Equal(192, mux.Height);

        var buf = new uint[720 * 216];
        mux.RenderInto(buf);
        Assert.Equal(1, a.RenderCalls);     // the active source rendered
        Assert.Equal(0, b.RenderCalls);     // the inactive source did not
        Assert.Equal(0xFF111111u, buf[0]);  // a's fill
    }

    [Fact]
    public void SetActive_switches_dimensions_render_target_and_fires_FrameReady()
    {
        var a = new FakeDisplay(280, 192, 0xFF111111);
        var b = new FakeDisplay(720, 216, 0xFF222222);
        var mux = new DisplayMultiplexer([a, b]);

        int frames = 0;
        mux.FrameReady += () => frames++;

        mux.SetActive(1);                   // switch to the 720x216 source
        Assert.Equal(1, frames);            // the switch fires FrameReady (so the host re-pulls at the new size)
        Assert.Equal(720, mux.Width);
        Assert.Equal(216, mux.Height);

        var buf = new uint[720 * 216];
        mux.RenderInto(buf);
        Assert.Equal(1, b.RenderCalls);     // now the second source renders
        Assert.Equal(0xFF222222u, buf[0]);
    }

    [Fact]
    public void Only_the_active_sources_FrameReady_is_forwarded()
    {
        var a = new FakeDisplay(280, 192, 0xFF111111);
        var b = new FakeDisplay(720, 216, 0xFF222222);
        var mux = new DisplayMultiplexer([a, b], initialActive: 0);

        int frames = 0;
        mux.FrameReady += () => frames++;

        a.RaiseFrame();          // the active source's vblank -> forwarded
        Assert.Equal(1, frames);

        b.RaiseFrame();          // a dormant source's vblank -> dropped (the host only pulls the active one)
        Assert.Equal(1, frames);
    }

    [Fact]
    public void A_single_source_multiplexer_is_transparent()
    {
        var only = new FakeDisplay(256, 192, 0xFF333333);
        var mux = new DisplayMultiplexer([only]);

        Assert.Equal(256, mux.Width);
        Assert.Equal(192, mux.Height);

        int frames = 0;
        mux.FrameReady += () => frames++;
        only.RaiseFrame();
        Assert.Equal(1, frames);             // the one source's frames forward

        mux.SetActive(0);                    // switching to the already-active source is a no-op
        Assert.Equal(1, frames);             // no extra FrameReady (index unchanged)
    }

    [Fact]
    public void SetActive_rejects_an_out_of_range_index()
    {
        var mux = new DisplayMultiplexer([new FakeDisplay(8, 8, 0)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => mux.SetActive(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mux.SetActive(-1));
    }

    [Fact]
    public void The_ctor_rejects_an_empty_source_list_and_a_bad_initial_index()
    {
        Assert.Throws<ArgumentException>(() => new DisplayMultiplexer([]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DisplayMultiplexer([new FakeDisplay(8, 8, 0)], initialActive: 2));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DisplayMultiplexerTests"`
Expected: FAIL — `DisplayMultiplexer` does not exist (compile error).

- [ ] **Step 3: Write the multiplexer**

Create `src/CpuEmulator.Core/DisplayMultiplexer.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>A display device that delegates to whichever underlying <see cref="IDisplayDevice"/> is
/// currently ACTIVE (ADR 0016 Decision 1). The surface pulls from this as an ordinary IDisplayDevice
/// (unchanged <c>MachineHost</c> apart from its per-frame buffer re-size); the active source is selected
/// by guest state — e.g. the Videx being the live terminal — via <see cref="SetActive"/>, so the user
/// sees what the guest drives, not a UI toggle. <see cref="Width"/>/<see cref="Height"/>/<see
/// cref="RenderInto"/> delegate to the active source; <see cref="FrameReady"/> forwards the ACTIVE
/// source's FrameReady AND fires on a <see cref="SetActive"/> switch (so the surface re-pulls — and
/// re-sizes — at the new geometry, e.g. 280x192 Apple hi-res vs a wider Videx 80x24 raster). A dormant
/// source's FrameReady is dropped (the host only ever pulls the active source; rendering a dormant
/// source's frame would write the wrong geometry). With one source the multiplexer is transparent.</summary>
public sealed class DisplayMultiplexer : IDisplayDevice
{
    private readonly IReadOnlyList<IDisplayDevice> _sources;
    private int _active;

    public DisplayMultiplexer(IReadOnlyList<IDisplayDevice> sources, int initialActive = 0)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            throw new ArgumentException("At least one display source is required.", nameof(sources));
        if (initialActive < 0 || initialActive >= sources.Count)
            throw new ArgumentOutOfRangeException(nameof(initialActive));
        _sources = sources;
        _active = initialActive;

        // Subscribe every source: a source raises its own vblank, but only the ACTIVE source's frames
        // are forwarded (the host only pulls the active source). Capturing the index keeps the check O(1).
        for (int i = 0; i < sources.Count; i++)
        {
            int index = i;
            sources[i].FrameReady += () => { if (index == _active) FrameReady?.Invoke(); };
        }
    }

    /// <summary>Select which source is live (called by the guest-driven active-display signal — PR-N's
    /// Videx drives it from its $C800-enable state). On an ACTUAL change, raises <see cref="FrameReady"/>
    /// so the surface re-pulls at the new source's geometry (the MachineHost re-size). A no-op (and no
    /// FrameReady) when the index is unchanged.</summary>
    public void SetActive(int index)
    {
        if (index < 0 || index >= _sources.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _active)
            return;
        _active = index;
        FrameReady?.Invoke();   // the source changed: the host re-pulls + re-sizes at the new geometry
    }

    /// <summary>The current active source index.</summary>
    public int ActiveIndex => _active;

    public int Width => _sources[_active].Width;
    public int Height => _sources[_active].Height;
    public void RenderInto(Span<uint> rgba) => _sources[_active].RenderInto(rgba);
    public event Action? FrameReady;
}
```

- [ ] **Step 4: Run the multiplexer tests**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DisplayMultiplexerTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core/DisplayMultiplexer.cs tests/CpuEmulator.Tests/DisplayMultiplexerTests.cs
git commit -m "feat(core): DisplayMultiplexer — active-source IDisplayDevice seam (ADR 0016 Decision 1)"
```

---

## Task 2: `MachineHost` per-frame re-size — follow the active source's geometry

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/MachineHost.cs`
- Test: `tests/CpuEmulator.Tests/MachineHostResizeTests.cs` (the re-size gate is Task 3; this task makes the change)

**Interfaces:**
- Consumes: `IDisplayDevice` (the display can now change `Width`/`Height` between frames).
- Produces: `MachineHost` re-sizes `_rgba` to `display.Width * display.Height` before `RenderInto` whenever that product changed. The ctor + `Step` + `RunHeadless` signatures are unchanged.

**Design notes (grounded against `MachineHost.cs` @ 10f5737):**
- `_rgba` is `private readonly uint[] _rgba;` (line 20) — **drop `readonly`** (it is reassigned on a size change).
- The ctor still sizes `_rgba = new uint[display.Width * display.Height];` (line 43) — the initial size.
- In `Step`, the frame branch (lines 64–69) currently does:
  ```csharp
  if (_frameDirty)
  {
      _frameDirty = false;
      _display.RenderInto(_rgba);
      _frameSink(FrameCodec.EncodeFrame(_display.Width, _display.Height, _rgba));
  }
  ```
  Insert a size-compare-and-realloc **before** `RenderInto` so the buffer matches the active source's current geometry. Extract a tiny `EnsureFrameBuffer()` helper for clarity (and so the gate can reason about it). The `EncodeFrame` call already reads the live `_display.Width`/`_display.Height`, so the wire frame carries the new geometry automatically.

- [ ] **Step 1: Drop `readonly` on `_rgba`**

In `src/CpuEmulator.Surface.Web/MachineHost.cs`, change line 20:

```csharp
    private uint[] _rgba;   // re-sized when the active display source's dimensions change (ADR 0016 Decision 1)
```

- [ ] **Step 2: Add the re-size before `RenderInto` in `Step`**

Replace the frame branch in `Step` (lines 64–69) with:

```csharp
        if (_frameDirty)
        {
            _frameDirty = false;
            EnsureFrameBuffer();                       // follow the active source's geometry (re-size on change)
            _display.RenderInto(_rgba);
            _frameSink(FrameCodec.EncodeFrame(_display.Width, _display.Height, _rgba));
        }
```

Add the helper after `Step` (or after `RunHeadless`):

```csharp
    /// <summary>Re-size the RGBA frame buffer to the active display's current geometry if it changed
    /// (ADR 0016 Decision 1). A no-op for every single-display board (the dimensions never change), so
    /// the single-source path is byte-for-byte unchanged; a one-time reallocation on the rare active-
    /// source switch (e.g. 40-col Apple -> 80-col Videx behind a DisplayMultiplexer). The wire frame's
    /// width/height come from _display.Width/_display.Height per frame (FrameCodec.EncodeFrame), so the
    /// client re-sizes its canvas automatically — only this host-side buffer needs to follow.</summary>
    private void EnsureFrameBuffer()
    {
        int needed = _display.Width * _display.Height;
        if (_rgba.Length != needed)
            _rgba = new uint[needed];
    }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/CpuEmulator.Surface.Web/CpuEmulator.Surface.Web.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/CpuEmulator.Surface.Web/MachineHost.cs
git commit -m "feat(surface): MachineHost re-sizes its frame buffer to the active display's geometry"
```

---

## Task 3: The un-fakeable gate — the host re-pulls at the new size when the active source changes size

**Files:**
- Test: `tests/CpuEmulator.Tests/MachineHostResizeTests.cs`

**Interfaces:**
- Consumes: `MachineHost`, `DisplayMultiplexer` (Task 1), `FrameCodec`; a fake `IDisplayDevice` + a fake `Machine`-free drive (use a real but trivial `Machine`, or drive `Step` directly — see the design note).

**Design notes — this is the row-M un-fakeable gate (the re-size half):** *switching the active source makes the surface re-pull at the new size.* The cleanest deterministic gate uses a **`DisplayMultiplexer` over two differently-sized fake sources** wired into a real `MachineHost`, with a minimal machine that does nothing on `Run` (so the only frames are the ones the test triggers). The test:
1. Builds a `MachineHost` over a `DisplayMultiplexer([small, large])` (e.g. 280×192 and 720×216).
2. Raises the small source's `FrameReady`, calls `Step` → captures the FB frame; asserts it is **280×192** and the right byte length.
3. Calls `mux.SetActive(1)` (fires `FrameReady`), `Step` → captures the FB frame; asserts it is **720×216** — proving the host re-pulled at the new size (the `_rgba` reallocated; the frame did not truncate or overflow).

The decoded width/height come from the FB header (`'F','B',ver,res,u16 w LE,u16 h LE`), and the payload length is `width*height*4 + headerLen` — both are asserted, so a host that did NOT re-size would either throw (small buffer, large render) or emit a wrong-length/wrong-geometry frame — unfakeable.

**The machine:** `MachineHost`'s ctor needs a `Machine`. Build a trivial real single-CPU machine that does nothing meaningful on `Run` (a tiny RAM-only board), OR — simpler and grounded — reuse the **two-source fake display** and a tiny machine via `Machine.Create(...).WithAddressSpace(...).WithRam(...).WithCpu(_ => new FakeCpu()).Build()` (the `FakeCpu` test double the dual-CPU tests use). `Step(cycles)` runs the machine (a no-op-ish FakeCpu) then renders if `_frameDirty` — and `_frameDirty` is set by the source `FrameReady` the test raises. The keyboard arg is a throwaway `IKeyboardSink` stub.

- [ ] **Step 1: Write the gate**

Create `tests/CpuEmulator.Tests/MachineHostResizeTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Surface.Web;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class MachineHostResizeTests
{
    // A fixed-size display source that raises FrameReady on demand and fills a known color.
    private sealed class FakeDisplay(int width, int height, uint fill) : IDisplayDevice
    {
        public int Width => width;
        public int Height => height;
        public void RenderInto(Span<uint> rgba) => rgba[..(Width * Height)].Fill(fill);
        public event Action? FrameReady;
        public void RaiseFrame() => FrameReady?.Invoke();
    }

    private sealed class NoKeyboard : IKeyboardSink
    {
        public void PostKey(in KeyEvent e) { }
    }

    // A trivial real machine the host can Run (a FakeCpu does nothing meaningful; frames come from the
    // display FrameReady the test raises, not from the CPU).
    private static Machine TrivialMachine() =>
        Machine.Create("host-resize")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => new FakeCpu())
            .Build();

    private static (int width, int height, int payloadLen) DecodeFb(byte[] frame)
    {
        // FB header: 'F','B', ver, reserved, u16 width LE, u16 height LE, then width*height*4 RGBA bytes.
        Assert.Equal((byte)'F', frame[0]);
        Assert.Equal((byte)'B', frame[1]);
        int w = frame[4] | (frame[5] << 8);
        int h = frame[6] | (frame[7] << 8);
        return (w, h, frame.Length);
    }

    [Fact]
    public void Switching_the_active_source_makes_the_host_re_pull_at_the_new_size()
    {
        var small = new FakeDisplay(280, 192, 0xFF111111);
        var large = new FakeDisplay(720, 216, 0xFF222222);
        var mux = new DisplayMultiplexer([small, large], initialActive: 0);

        byte[]? frame = null;
        var host = new MachineHost(TrivialMachine(), mux, new NoKeyboard(), f => frame = f);

        // 1) The small source's vblank -> Step renders a 280x192 frame.
        small.RaiseFrame();
        host.Step(1);
        Assert.NotNull(frame);
        var (w1, h1, len1) = DecodeFb(frame!);
        Assert.Equal(280, w1);
        Assert.Equal(192, h1);
        // The header is 8 bytes; the payload is width*height*4 RGBA bytes (grounded against FrameCodec).
        Assert.Equal(8 + 280 * 192 * 4, len1);

        // 2) Switch the active source (fires FrameReady) -> Step re-pulls at 720x216 (the host re-sized).
        mux.SetActive(1);
        host.Step(1);
        var (w2, h2, len2) = DecodeFb(frame!);
        Assert.Equal(720, w2);          // the host followed the new geometry...
        Assert.Equal(216, h2);
        Assert.Equal(8 + 720 * 216 * 4, len2);   // ...and the buffer re-sized (no truncation/overflow)
        Assert.True(len2 > len1);       // the larger source yields a larger frame (the re-size happened)
    }

    [Fact]
    public void A_single_source_host_is_unchanged_the_buffer_never_re_sizes()
    {
        // The single-display path (every shipped surface): one fixed-size source, frames always the same
        // geometry, no reallocation. This is the byte-for-byte-unchanged regression for the host re-size.
        var only = new FakeDisplay(256, 192, 0xFF333333);

        var frames = new List<byte[]>();
        var host = new MachineHost(TrivialMachine(), only, new NoKeyboard(), frames.Add);

        for (int i = 0; i < 5; i++) { only.RaiseFrame(); host.Step(1); }

        Assert.Equal(5, frames.Count);
        foreach (byte[] f in frames)
        {
            var (w, h, len) = DecodeFb(f);
            Assert.Equal(256, w);
            Assert.Equal(192, h);
            Assert.Equal(8 + 256 * 192 * 4, len);   // every frame identical geometry — no re-size ever
        }
    }
}
```

> **Implementer note — the FB header length.** The payload assertions use `8 + width*height*4` (an 8-byte FB header: `'F','B',ver,reserved,u16 w,u16 h`). **Verify against the shipped `FrameCodec.EncodeFrame`** (`src/CpuEmulator.Surface.Web/FrameCodec.cs`) before running — if the header is a different size, adjust the `8 +` constant to the real header length (the geometry assertions `Assert.Equal(280, w1)` etc. are the load-bearing part and do not depend on the header size; the length assertion is the corroborating check). The `DecodeFb` width/height offsets (bytes 4–7) mirror the shipped `Apple2BootTests.Apple2Surface_constructs_and_renders_a_280x192_frame` decode (`lastFrame[4] | (lastFrame[5] << 8)` for width).

- [ ] **Step 2: Run the gate**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~MachineHostResizeTests"`
Expected: PASS (2 tests). If the FB header length differs, fix the `8 +` constant per the implementer note and re-run.

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/MachineHostResizeTests.cs
git commit -m "test(surface): host re-pulls at the new size on active-source switch; single-source unchanged"
```

---

## Task 4: Final gate — full suite + warning-clean build (the single-source regression)

**Files:** none (verification only).

**Design note — the load-bearing regression:** the host re-size must leave every shipped single-display surface byte-for-byte unchanged. The full suite is the real gate — every `SpectrumSurface`/`Apple2Surface`/`DemoBoardSurface` test (including `Apple2BootTests.Apple2Surface_constructs_and_renders_a_280x192_frame`, which asserts a 280×192 frame through a real `MachineHost`) must stay green with zero changes, because their displays never change size so `EnsureFrameBuffer` is always a no-op.

- [ ] **Step 1: Full build, warning-clean**

Run: `dotnet build CpuEmulator.slnx`
Expected: Build succeeded, **0 warnings**.

- [ ] **Step 2: Full test suite**

Run: `dotnet test CpuEmulator.slnx`
Expected: the post-PR-K/J baseline green plus the new PR-M tests (`DisplayMultiplexerTests` + `MachineHostResizeTests`), 0 failed. **No pre-existing test regresses** — the host re-size is inert for every shipped fixed-size display, and `DisplayMultiplexer` is a new, un-referenced-by-shipped-surfaces type (PR-N is its first surface caller).

- [ ] **Step 3: Confirm the un-fakeable gates ran**

Confirm these are in the passing set:
- `DisplayMultiplexerTests.SetActive_switches_dimensions_render_target_and_fires_FrameReady` — the switch delegates + fires FrameReady.
- `MachineHostResizeTests.Switching_the_active_source_makes_the_host_re_pull_at_the_new_size` — the host re-pulls at the new geometry on a source switch (the re-size gate).
- `MachineHostResizeTests.A_single_source_host_is_unchanged_the_buffer_never_re_sizes` — the single-source path is unchanged.

---

## Self-Review

**1. Spec coverage (ADR 0016 Decision 1 + the row-M gate):**
- `DisplayMultiplexer : IDisplayDevice` delegating `Width`/`Height`/`RenderInto`/`FrameReady` to the active source → Task 1. ✓
- `SetActive` fires `FrameReady` (so the surface re-pulls) → Task 1. ✓
- `MachineHost` re-sizes its `_rgba` buffer when dimensions change → Task 2. ✓
- The un-fakeable gate: switching the active source makes the surface re-pull at the new size → Task 3 (`Switching_the_active_source_makes_the_host_re_pull_at_the_new_size`). ✓
- The single-source (non-multiplexed) path is unchanged → Task 3 (`A_single_source_host_is_unchanged...`) + Task 4 (the full suite). ✓
- No deps (row M `Deps: —`); no Videx (that is PR-N, dep A + M). ✓

**2. Placeholder scan:** No TBD/TODO/"implement later"/"similar to Task N". Every code step shows literal code (the multiplexer, the host re-size, all tests). The one "verify against the shipped FrameCodec header length" note is a corroborating-assertion tuning detail, not missing code — the geometry assertions are complete and load-bearing regardless.

**3. Type consistency:** `DisplayMultiplexer(IReadOnlyList<IDisplayDevice>, int initialActive = 0)`, `SetActive(int)`, `ActiveIndex`, `Width`/`Height`/`RenderInto(Span<uint>)`/`FrameReady` (the `IDisplayDevice` contract) used identically across tasks. `MachineHost`'s ctor/`Step`/`RunHeadless` signatures are untouched; only `_rgba`'s `readonly` is dropped + the `EnsureFrameBuffer` helper added. `FakeCpu` + `IKeyboardSink`/`KeyEvent` are the shipped test-double / contract names (verify `FakeCpu` is under `CpuEmulator.Tests.TestDoubles`, used by `DualCpuMachineTests`).

**Builder-readiness note:** the only cross-file touch is `MachineHost.cs` (drop `readonly`, add the re-size). No surface (`Apple2Surface`/`SpectrumSurface`/`DemoBoardSurface`) changes — they keep passing one device; PR-N is the first caller that passes a `DisplayMultiplexer`. The gate uses fake displays + the shipped `FakeCpu`, so it needs no asset and no Videx. The one open verification (the FB header length constant in the corroborating length-assertion) is flagged inline with the grounded fallback.
