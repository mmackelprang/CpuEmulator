# PR-P — the `ST` status-frame seam (host→client read-only indicators)

> **Queue row:** P (`docs/BUILDER_QUEUE.md`). **Deps:** none (all ✅). **Design:** spec
> `docs/superpowers/specs/2026-06-20-apple-2-plus-design.md` task **T-A** / decision **D14**; handoff
> `docs/design-handoffs/apple-2-plus/interactions.md` §1.1, §4.2, §4.6.
> **Grounded against `main` @ `c26faac`** (PRs #99–#117 merged). Every literal code block below was
> read against that HEAD; signatures are the real shipped ones.
> **Tier:** this is a surface/wire task — no CPU tier involved. The gate runs headless, asset-free.

---

## What this PR delivers (and what it does NOT)

**Delivers:** a real, structured `ST` (status) wire frame the host pushes whenever machine state changes
— carrying **board name**, **asset state**, **per-drive motor on/off + image label**, and the
**video-mode label** — which the browser client decodes and renders **read-only**. The host reads the
**REAL** machine state (the Disk II controller's actual `$C0E8/$C0E9` motor flag + the shipped ~1 s
556 off-delay, the live `Apple2VideoState` mode flags, the live `DisplayMultiplexer.ActiveIndex`); **no
indicator is fabricated client-side and none is faked on insert.**

**Does NOT deliver:** the control-strip DOM (drive panels, eject buttons, library dropdown, upload) —
that is **row T**, which *consumes* this frame. P ships the **seam + the wire push + the minimal client
decode that proves the indicators arrive correct** (it updates the existing single status line with the
decoded mode/drive text, and exposes the parsed status on `window` for T to bind a richer UI to). P also
does **not** touch the disk runtime-swap mechanism (row Q) — the image **label** P reports comes from a
label the surface already knows at build time (the disk filename, or `"—"` when synthetic); Q later makes
that label mutate at runtime, and because P reads it live each push, Q needs no change to P.

### Why a binary `ST` frame replacing the text one

Today (`Program.cs` line 121–123) the host sends a **one-shot UTF-8 text** message `"ST <assetState>"`
once at connect; `app.js` `handleStatusText` parses it. That is a single static string and cannot carry
live per-frame motor/mode/drive state. P **keeps the wire tag `ST`** but makes it a **structured,
repeatable frame** the host re-pushes on change. To avoid a second transport, P encodes `ST` as a
**JSON text** WebSocket message (the inbound key path is already JSON text; the client already routes
*all* text messages to `handleStatusText`). The binary `FB`/`AU` path is **untouched**. This keeps the
asset-state string (the existing banner contract) working verbatim while adding the structured fields.

> **Decision (recorded):** `ST` stays a **text** frame (JSON), not a new binary opcode. Rationale: the
> client's `ws.onmessage` already branches `typeof ev.data === "string"` → `handleStatusText` vs binary
> → `FB`/`AU`; a JSON text `ST` slots into the existing text branch with zero new dispatch, and the
> indicators are tiny (no bandwidth concern — pushed only on change, not per frame). A binary `ST`
> opcode would duplicate the FB/AU header machinery for no gain. The wire stays: **text ⇒ status, binary
> ⇒ pixels/audio.**

---

## The status model (the single source of truth for the fields)

A new immutable record carries the snapshot. It lives in `CpuEmulator.Surface.Web` beside `FrameCodec`.

| field | source (REAL machine state) | example |
|---|---|---|
| `Board` | the surface's static board name | `"Apple ][+"`, `"Apple ][+ SoftCard"` |
| `Asset` | the existing `assetState` string | `"apple"`, `"softcard-cpm-videx"`, `"demo"` |
| `Mode` | derived from the live `Apple2VideoState` flags + `DisplayMultiplexer.ActiveIndex` | `"HIRES · 280×192 · page 1"`, `"Videx 80×24 · CP/M"` |
| `Drives[n].MotorOn` | `Apple2DiskII` real motor flag (the `$C0E8/$C0E9` + 556 delay) | `true` / `false` |
| `Drives[n].Label` | the image label the surface holds (filename or `"—"`) | `"—"`, `"DOS33.dsk"` |

The host snapshots this each `Step` and pushes an `ST` frame **only when the snapshot changed** (so a
quiet machine emits nothing; a motor toggle or a mode switch emits exactly one frame).

---

## TDD task list

Each task is **write the test (red) → implement (green)**. Tests live under
`tests/CpuEmulator.Tests/Surface/`. Run the suite after each: `dotnet test`.

---

### Task 1 — `MachineStatus` record + `FrameCodec.EncodeStatus`

**The un-fakeable seam:** the encoder turns a real status snapshot into the `ST` wire bytes; the decode
test asserts the exact field values round-trip. No machine yet — pure codec.

#### 1a. Test (red) — `tests/CpuEmulator.Tests/Surface/StatusFrameCodecTests.cs` (new)

```csharp
using System.Text;
using System.Text.Json;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class StatusFrameCodecTests
{
    [Fact]
    public void EncodeStatus_is_an_ST_prefixed_json_text_frame_carrying_every_field()
    {
        var status = new MachineStatus(
            Board: "Apple ][+ SoftCard",
            Asset: "softcard-cpm-videx",
            Mode: "Videx 80×24 · CP/M",
            Drives:
            [
                new DriveStatus(MotorOn: true, Label: "CPM.dsk"),
                new DriveStatus(MotorOn: false, Label: "—"),
            ]);

        byte[] frame = FrameCodec.EncodeStatus(status);
        string text = Encoding.UTF8.GetString(frame);

        // The wire stays "ST " + a JSON body (the client routes ALL text to handleStatusText; the
        // "ST " prefix is the existing contract app.js already gates on).
        Assert.StartsWith("ST ", text);

        using JsonDocument doc = JsonDocument.Parse(text["ST ".Length..]);
        JsonElement root = doc.RootElement;
        Assert.Equal("Apple ][+ SoftCard", root.GetProperty("board").GetString());
        Assert.Equal("softcard-cpm-videx", root.GetProperty("asset").GetString());
        Assert.Equal("Videx 80×24 · CP/M", root.GetProperty("mode").GetString());

        JsonElement drives = root.GetProperty("drives");
        Assert.Equal(2, drives.GetArrayLength());
        Assert.True(drives[0].GetProperty("motor").GetBoolean());
        Assert.Equal("CPM.dsk", drives[0].GetProperty("label").GetString());
        Assert.False(drives[1].GetProperty("motor").GetBoolean());
        Assert.Equal("—", drives[1].GetProperty("label").GetString());
    }

    [Fact]
    public void EncodeStatus_equal_snapshots_produce_equal_bytes_so_change_detection_is_byte_compare()
    {
        var a = new MachineStatus("Apple ][+", "apple", "TEXT · 40×24 · page 1",
            [new DriveStatus(false, "—")]);
        var b = new MachineStatus("Apple ][+", "apple", "TEXT · 40×24 · page 1",
            [new DriveStatus(false, "—")]);

        Assert.Equal(FrameCodec.EncodeStatus(a), FrameCodec.EncodeStatus(b));
    }
}
```

#### 1b. Implement (green) — `src/CpuEmulator.Surface.Web/MachineStatus.cs` (new)

```csharp
namespace CpuEmulator.Surface.Web;

/// <summary>One read-only Disk II drive indicator for the <c>ST</c> status frame: the REAL motor flag
/// (the $C0E8/$C0E9 motor switches + the shipped ~1 s 556 off-delay, ADR 0014 Decision 6 — NOT faked on
/// insert) and the loaded-image label the surface holds ("—" when empty/synthetic). The host reads the
/// motor flag live each push; the surface stays a dumb reflector of the controller's truth.</summary>
public sealed record DriveStatus(bool MotorOn, string Label);

/// <summary>The host→client read-only machine-status snapshot (design D14 / task T-A). Carries the board
/// name, the asset-state string (the existing banner contract), the derived video-mode label, and the
/// per-drive motor + image indicators. Every field is REAL machine state read at push time — no field is
/// fabricated client-side. Pushed (as the <c>ST</c> text frame) only when the snapshot changes.</summary>
public sealed record MachineStatus(
    string Board, string Asset, string Mode, IReadOnlyList<DriveStatus> Drives);
```

#### 1c. Implement (green) — extend `src/CpuEmulator.Surface.Web/FrameCodec.cs`

Add (inside the `FrameCodec` class; `using System.Text;` + `using System.Text.Json;` — `System.Text.Json`
is already imported at the top of the file):

```csharp
    /// <summary>Encode a machine-status snapshot as the <c>ST</c> text frame: the literal prefix
    /// <c>"ST "</c> (the existing client contract — app.js routes every text frame to handleStatusText
    /// and gates on "ST ") followed by a compact JSON body. Text, not binary: the FB/AU binary path is
    /// untouched; the client's text branch already owns this. JSON keys are lower-case + stable so equal
    /// snapshots produce byte-identical frames (the host's change-detection compares the encoded bytes).
    /// </summary>
    public static byte[] EncodeStatus(MachineStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var body = new
        {
            board = status.Board,
            asset = status.Asset,
            mode = status.Mode,
            drives = status.Drives.Select(d => new { motor = d.MotorOn, label = d.Label }).ToArray(),
        };
        // No indented/whitespace options -> deterministic compact JSON (equal snapshots -> equal bytes).
        string json = JsonSerializer.Serialize(body);
        return Encoding.UTF8.GetBytes("ST " + json);
    }
```

> Note: `System.Linq` (`.Select`) is implicitly available (the test project + this project both enable
> `ImplicitUsings`; `FrameCodec` already uses collection expressions). If the SDK style here lacks the
> implicit `using System.Linq;`, add it at the top of `FrameCodec.cs`.

**Gate after Task 1:** `dotnet test` — the two new codec tests pass; every pre-existing `FrameCodecTests`
test (the `EncodeFrame` header + `TryDecodeKey` cases) is untouched and green.

---

### Task 2 — the surface exposes a **live** mode label + drive snapshot

The `ST` frame needs three live reads the surfaces don't yet surface cleanly: the **video-mode label**
(from the private `Apple2VideoState`), and the **per-drive motor + label** (from the private `Apple2DiskII`
fields). We add narrow read-only accessors — the controller/video already OWN this truth; we only expose it.

#### 2a. Test (red) — `tests/CpuEmulator.Tests/Apple2/Apple2VideoModeLabelTests.cs` (new)

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2VideoModeLabelTests
{
    private static Apple2Video Video(out Apple2VideoState state)
    {
        state = new Apple2VideoState();
        var ram = new AddressSpace(AddressSpaceKind.Program, 16);
        ram.MapMemory(0x0000, new byte[0x10000], writable: true);
        return new Apple2Video(ram, state, charRom: null);
    }

    [Fact]
    public void Mode_label_reflects_the_live_video_state_flags()
    {
        Apple2Video video = Video(out Apple2VideoState state);

        // Power-on default: text, page 1, full, lo-res.
        Assert.Equal("TEXT · 40×24 · page 1", video.ModeLabel);

        state.GraphicsOn = true; state.HiRes = true; state.Page2 = true;
        Assert.Equal("HIRES · 280×192 · page 2", video.ModeLabel);

        state.HiRes = false;                                   // lo-res graphics
        Assert.Equal("LORES · 40×48 · page 2", video.ModeLabel);

        state.GraphicsOn = true; state.HiRes = true; state.Mixed = true; state.Page2 = false;
        Assert.Equal("MIXED · text+gfx · page 1", video.ModeLabel);
    }
}
```

#### 2b. Implement (green) — add `ModeLabel` to `src/CpuEmulator.Peripherals/Apple2Video.cs`

`Apple2Video` already holds `private readonly Apple2VideoState _state;` (line 20) and reads
`_state.GraphicsOn`/`HiRes`/`Page2` in `RenderInto`. Add the read-only label property (the same flag
reads the renderer uses — no new state). Place it near `Width`/`Height` (lines 24–25):

```csharp
    /// <summary>A read-only human label of the current video mode, derived from the SAME live
    /// <see cref="Apple2VideoState"/> flags the renderer reads (design D1 / interactions §1.1). Mixed
    /// takes precedence in the label (it is the visible-on-screen split). The host reads this for the
    /// <c>ST</c> status frame; it is never a control.</summary>
    public string ModeLabel
    {
        get
        {
            string page = _state.Page2 ? "page 2" : "page 1";
            if (!_state.GraphicsOn)
                return $"TEXT · 40×24 · {page}";
            if (_state.Mixed)
                return $"MIXED · text+gfx · {page}";
            return _state.HiRes
                ? $"HIRES · 280×192 · {page}"
                : $"LORES · 40×48 · {page}";
        }
    }
```

#### 2c. Test (red) — `tests/CpuEmulator.Tests/Apple2/Apple2DiskIIStatusTests.cs` (new)

The Disk II already exposes `MotorOnForTestProperty` and `SelectedDriveForTest` (lines 48–49) as
**test-only** inspectors. The `ST` frame needs the motor flag as a **production** read. We promote a
narrow public read for the motor (per drive), keeping the existing inspectors.

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2DiskIIStatusTests
{
    private static SyntheticFluxImage OneTrack()
    {
        var img = new SyntheticFluxImage(trackCount: 35);
        img.SetTrackNibbles(0, new byte[] { 0xFF, 0xD5, 0xAA, 0x96 });
        return img;
    }

    [Fact]
    public void MotorOn_reports_the_real_motor_flag_not_a_faked_insert_state()
    {
        var disk = new Apple2DiskII(OneTrack());

        // A freshly built controller with an image inserted is NOT spinning — the motor follows the
        // $C0E9/$C0E8 switches, never the presence of a disk (the design's "not faked on insert" rule).
        Assert.False(disk.MotorOn);

        disk.Access(0x9, isRead: true);   // $C0E9: motor on now
        Assert.True(disk.MotorOn);

        // $C0E8 (motor-off request) with no scheduler stops immediately (the bare-unit path).
        disk.Access(0x8, isRead: true);
        Assert.False(disk.MotorOn);
    }
}
```

#### 2d. Implement (green) — add `MotorOn` to `src/CpuEmulator.Peripherals/Apple2DiskII.cs`

Add a public read-only property beside the test inspectors (lines 44–49). It reads the same `_motorOn`
field the `$C0E8/$C0E9` switches + the 556 off-delay drive:

```csharp
    /// <summary>The REAL motor state (the $C0E9 on / $C0E8-with-556-delay off, ADR 0014 Decision 6) —
    /// the host reads this for the drive-activity light in the <c>ST</c> status frame. It is NOT set by
    /// inserting an image (design D10 / interactions §4.2 — the light is "not faked on insert"); it
    /// follows the guest's motor switches and lingers ~1 s after the last access, exactly as the lamp
    /// on a real Disk II does.</summary>
    public bool MotorOn => _motorOn;
```

> The existing `MotorOnForTestProperty` stays (referenced by `Apple2DiskIITests`); `MotorOn` is its
> production twin. Do **not** delete the inspector — it is used by 5 shipped tests.

**Gate after Task 2:** `dotnet test` — the new mode-label + motor tests pass; the existing
`Apple2DiskIITests` (which use `MotorOnForTestProperty`) and `Apple2VideoTests` are untouched and green.

---

### Task 3 — the surface builds a `MachineStatus` snapshot from live state

Each Apple surface gains a `Status()` method that reads the live machine and returns a `MachineStatus`.
The surface holds the board name + the per-drive labels (static, known at `Create`); the motor + mode are
read live. We thread the `Apple2DiskII` and (for the Videx surface) the `DisplayMultiplexer` into the
surface record so `Status()` can read them — they are already constructed in `Create`; we just keep the
reference.

#### 3a. Test (red) — `tests/CpuEmulator.Tests/Surface/Apple2SurfaceStatusTests.cs` (new)

```csharp
using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class Apple2SurfaceStatusTests
{
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x62; rom[0x2FFD] = 0xFA;   // reset vector -> $FA62 (any valid landing)
        return rom;
    }

    [Fact]
    public void Status_reads_real_board_mode_and_drive_state()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });

        MachineStatus s = surface.Status();

        Assert.Equal("Apple ][+", s.Board);
        // No disk inserted -> the synthetic image -> the "—" label; motor off at boot (not faked).
        Assert.Single(s.Drives);
        Assert.False(s.Drives[0].MotorOn);
        Assert.Equal("—", s.Drives[0].Label);
        // Power-on video mode.
        Assert.Equal("TEXT · 40×24 · page 1", s.Mode);
    }

    [Fact]
    public void Status_motor_flips_when_the_guest_turns_the_drive_motor_on()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });

        // $C0E9 through the live bus turns the REAL motor on; Status() must reflect it (not faked).
        surface.Machine.Space(AddressSpaceKind.Program).Read8(0xC0E9);
        Assert.True(surface.Status().Drives[0].MotorOn);
    }
}
```

#### 3b. Implement (green) — extend `src/CpuEmulator.Surface.Web/Apple2Surface.cs`

Add `Apple2DiskII Disk` + a drive-1 `Label` to the record, capture them in `Create`, and add `Status()`.
The current record is `(Machine, Apple2Video, Apple2Keyboard, Apple2Speaker, MachineHost)` and `Create`
already builds `disk` (line 32). Changes:

```csharp
public sealed record Apple2Surface(
    Machine Machine, Apple2Video Video, Apple2Keyboard Keyboard, Apple2Speaker Speaker,
    MachineHost Host, CpuEmulator.Peripherals.Apple2DiskII Disk, string Drive1Label)
{
    public static Apple2Surface Create(byte[] systemRom, byte[]? diskBootRom, byte[]? charRom,
                                       Action<byte[]> frameSink, Action<byte[]> audioSink,
                                       IFluxImage? drive1Image = null,
                                       ExecutionTier tier = ExecutionTier.Interpreter,
                                       string drive1Label = "—")
    {
        // ... (lines 23–46 unchanged: state, placeholder, video, keyboard, speaker, lc, disk, iou,
        //      spec, machine, video.Realize, speaker.Realize, machine.Reset) ...

        var host = new MachineHost(machine, video, keyboard, frameSink, speaker, audioSink);
        return new Apple2Surface(machine, video, keyboard, speaker, host, disk, drive1Label);
    }

    /// <summary>Snapshot the REAL machine state for the <c>ST</c> status frame (design D14): the board
    /// name, the live video-mode label, and the live per-drive motor + image label. The plain ][+ has one
    /// modeled drive (drive 1; PR-F models drive 1) — the synthetic-image label is "—" until a real disk
    /// is inserted.</summary>
    public MachineStatus Status() => new(
        Board: "Apple ][+",
        Asset: "apple",
        Mode: Video.ModeLabel,
        Drives: [new DriveStatus(Disk.MotorOn, Drive1Label)]);
}
```

> The `disk` local already exists at line 32 (`var disk = new Apple2DiskII(...)`); the only change in the
> body is appending `disk, drive1Label` to the returned record. The `Asset` here is the static `"apple"`;
> the **session** owns the real asset string (fallback-font vs apple vs demo) and overrides it when it
> pushes — see Task 4. (Keeping a sane default on the surface lets `Status()` be tested standalone.)

> **Note on `SoftCardSurface` / `SoftCardVidexSurface`:** apply the **same** record-field + `Status()`
> addition. `SoftCardSurface` board = `"Apple ][+ SoftCard"`, drive-1 label defaults to `"CP/M"`,
> `Mode` = `Video.ModeLabel`. `SoftCardVidexSurface` board = `"Apple ][+ SoftCard"`, but `Mode` reads the
> multiplexer: when `Display.ActiveIndex == 1` (the Videx) the label is `"Videx 80×24 · CP/M"`, else
> `Video.ModeLabel`. Both already construct `disk` and (Videx) `mux` in `Create`; add them to the record
> and read them in `Status()`. Concretely for `SoftCardVidexSurface.Status()`:
>
> ```csharp
>     public MachineStatus Status() => new(
>         Board: "Apple ][+ SoftCard",
>         Asset: "softcard-cpm-videx",
>         Mode: Display.ActiveIndex == VidexIndex ? "Videx 80×24 · CP/M" : Video.ModeLabel,
>         Drives: [new DriveStatus(Disk.MotorOn, Drive1Label)]);
> ```
>
> (`Display` and `VidexIndex` already exist on that record/class — lines 15–20 of the shipped file. Add
> `Apple2DiskII Disk` + `string Drive1Label` to its record and capture `disk` in `Create`.)

**Gate after Task 3:** `dotnet test` — the surface-status tests pass; every existing
`SpectrumSurfaceTests`-style test and the `SoftCard*`/`Apple2*` board tests are green (the record-shape
change is additive — every existing `Create` call site still compiles because the new params are
defaulted, and the existing positional deconstruction in tests, if any, must be checked; see the
verification note below).

> **Verification note for Builder:** adding positional record params changes the primary-constructor
> arity. Search for any `var (m, v, k, s, h) = surface;` deconstruction or `new Apple2Surface(...)` direct
> construction in tests; the shipped tests use `SpectrumSurface` (a different type) and call
> `Apple2Surface.Create(...)` (named), so this is expected to be clean — but run the build and fix any
> arity mismatch by appending the new fields, not reordering the existing ones.

---

### Task 4 — the session pushes `ST` on change (the live, un-fakeable loop)

The session must (a) push an initial `ST` frame at connect carrying the real boot state, and (b) re-push
on change. The cleanest seam: the `MachineHost` already runs `Step` on the pump timer; the session passes
a **status provider** + a **status sink** to the pump and, after each `Step`, snapshots, compares to the
last sent bytes, and pushes only on change. We extend `SurfacePump` (in `Program.cs`) with an optional
status callback. The host stays unchanged (it owns FB/AU; status is a session concern layered over the
pump's tick).

#### 4a. Test (red) — `tests/CpuEmulator.Tests/Surface/StatusPushOnChangeTests.cs` (new)

We test the **change-detection pump helper** in isolation (no socket): a small public helper
`StatusPusher` that holds the last-sent bytes and pushes via a sink only when the snapshot changes.

```csharp
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class StatusPushOnChangeTests
{
    [Fact]
    public void Pushes_once_initially_then_only_when_the_snapshot_changes()
    {
        var sent = new List<byte[]>();
        bool motor = false;
        // The provider reads "live" state each tick (here, a mutable local standing in for the machine).
        MachineStatus Provider() => new(
            "Apple ][+", "apple", "TEXT · 40×24 · page 1",
            [new DriveStatus(motor, "—")]);

        var pusher = new StatusPusher(Provider, frame => sent.Add(frame));

        pusher.Tick();                 // first tick -> initial push
        Assert.Single(sent);

        pusher.Tick();                 // no change -> no push
        Assert.Single(sent);

        motor = true;                  // the REAL motor turned on
        pusher.Tick();                 // change -> exactly one more push
        Assert.Equal(2, sent.Count);

        // The second frame's JSON carries the new motor=true (the change is the real flag, not faked).
        string text = System.Text.Encoding.UTF8.GetString(sent[1]);
        Assert.Contains("\"motor\":true", text);

        pusher.Tick();                 // still on, unchanged -> no push
        Assert.Equal(2, sent.Count);
    }
}
```

#### 4b. Implement (green) — `src/CpuEmulator.Surface.Web/StatusPusher.cs` (new)

```csharp
namespace CpuEmulator.Surface.Web;

/// <summary>Pushes the <c>ST</c> status frame to a sink only when the machine's status snapshot changes
/// (design D14 — the surface is a dumb reflector of REAL state, pushed on change). Reads the snapshot via
/// a provider each <see cref="Tick"/>, encodes it, and compares the encoded bytes to the last sent frame
/// (equal snapshots -> equal bytes, by FrameCodec.EncodeStatus's deterministic JSON). The first Tick
/// always pushes (the initial state). Kept separate from MachineHost: the host owns FB/AU pixels/audio;
/// status is a session-level overlay on the pump's tick.</summary>
public sealed class StatusPusher
{
    private readonly Func<MachineStatus> _provider;
    private readonly Action<byte[]> _sink;
    private byte[]? _last;

    public StatusPusher(Func<MachineStatus> provider, Action<byte[]> sink)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sink);
        _provider = provider;
        _sink = sink;
    }

    /// <summary>Snapshot the live status; push it only if its encoded bytes differ from the last sent
    /// frame (or this is the first push).</summary>
    public void Tick()
    {
        byte[] frame = FrameCodec.EncodeStatus(_provider());
        if (_last is not null && _last.AsSpan().SequenceEqual(frame))
            return;
        _last = frame;
        _sink(frame);
    }
}
```

#### 4c. Wire it into `Program.cs`

Two changes, both additive:

**(i)** Replace the one-shot text push (lines 121–123) with an initial structured push **plus** a status
channel. Keep the asset string accurate by letting the session inject the real `assetState` into the
snapshot. The simplest correct wiring: after building the surface, capture a `Func<MachineStatus>` that
calls the surface's `Status()` but substitutes the session's real `assetState` (so the fallback-font/demo
distinction the session knows is honored):

In each surface branch, after building the surface, set a provider. For the Apple branch (lines 92–101):

```csharp
            Apple2Surface apple = Apple2Surface.Create(sys, bootRom, charRom,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            pump = new SurfacePump(apple.Host, AppleSliceCycles, ApplePeriod);
            assetState = charRom is null ? "apple-fallback-font" : "apple";
            string asset = assetState;                                    // capture for the provider
            statusProvider = () => apple.Status() with { Asset = asset }; // real state, real asset string
```

Do the equivalent in the SoftCard/Videx branch (`statusProvider = () => softcard.Status() with { Asset = assetState };`).
For the Spectrum + demo branches, set `statusProvider = null` (no Apple status; the existing one-shot text
`ST <assetState>` push covers them — see (iii)).

Declare near `pump`/`assetState` (line 74–75):

```csharp
        Func<MachineStatus>? statusProvider = null;
```

**(ii)** After the surface branch, replace the one-shot push block (lines 118–123) with:

```csharp
        // The status frame: for the Apple surfaces, a live ST frame pushed on change (design D14, the
        // drive light / mode label / banner consume it). For the Spectrum/demo, the legacy one-shot
        // "ST <assetState>" text frame (no Apple status to reflect) — the client handles both shapes.
        Channel<byte[]> statusFrames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });

        StatusPusher? statusPusher = statusProvider is null
            ? null
            : new StatusPusher(statusProvider, f => statusFrames.Writer.TryWrite(f));

        if (statusPusher is not null)
            statusPusher.Tick();                          // initial real-state push
        else if (socket.State == WebSocketState.Open)     // Spectrum/demo: the legacy one-shot text frame
            await socket.SendAsync(Encoding.UTF8.GetBytes($"ST {assetState}"),
                WebSocketMessageType.Text, endOfMessage: true, ct);
```

**(iii)** Make the pump tick the pusher. The `SurfacePump.RunAsync` loop (lines 187–192) becomes:

```csharp
        public async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(_period);
            while (await timer.WaitForNextTickAsync(ct))
            {
                _host.Step(_slice);
                _statusPusher?.Tick();        // push ST only when the real snapshot changed
            }
        }
```

with the constructor taking an optional pusher (default `null`, so the Spectrum/demo callers are
unchanged):

```csharp
        public SurfacePump(MachineHost host, long slice, TimeSpan period, StatusPusher? statusPusher = null)
        {
            _host = host;
            _slice = slice;
            _period = period;
            _statusPusher = statusPusher;
        }
```

and add `private readonly StatusPusher? _statusPusher;` to the `SurfacePump` fields. Pass `statusPusher`
when constructing the Apple/SoftCard pumps:

```csharp
            pump = new SurfacePump(apple.Host, AppleSliceCycles, ApplePeriod, statusPusher);
```

> Ordering subtlety: `statusPusher` is constructed **after** the surface branch (step ii), but the pump
> is constructed **inside** the branch (step i). Resolve by hoisting: in each Apple branch set only
> `statusProvider` + `assetState` (not the pump), construct the `StatusPusher` after the branch (step ii),
> then construct the pump after the pusher. Move the `pump = new SurfacePump(...)` lines out of the
> branches to a single post-branch construction that reads the surface's `MachineHost`. **Builder:** the
> mechanical refactor is — keep a `MachineHost host;` + `long slice; TimeSpan period;` set per branch,
> build the pusher, then `pump = new SurfacePump(host, slice, period, statusPusher);` once. This keeps the
> Spectrum/demo path (statusPusher == null) byte-for-byte in behavior.

**(iv)** Send the status channel to the socket. The session already starts `sendFrames`/`sendAudio` via
`SendBinaryAsync` (lines 126–127). The status frames are **text**, so add a text sender and include it in
the `Task.WhenAny`/`WhenAll` set:

```csharp
        Task drive = pump.RunAsync(ct);
        Task sendFrames = SendBinaryAsync(socket, frames.Reader, ct);
        Task sendAudio = SendBinaryAsync(socket, audio.Reader, ct);
        Task sendStatus = SendTextAsync(socket, statusFrames.Reader, ct);
        Task recv = ReceiveKeysAsync(socket, pump, ct);

        await Task.WhenAny(drive, sendFrames, sendAudio, sendStatus, recv);
        frames.Writer.TryComplete();
        audio.Writer.TryComplete();
        statusFrames.Writer.TryComplete();
        try { await Task.WhenAll(drive, sendFrames, sendAudio, sendStatus, recv); } catch { /* teardown races expected */ }
```

with a text-sender mirroring `SendBinaryAsync` (add beside it, lines 136–145):

```csharp
    private static async Task SendTextAsync(WebSocket socket, ChannelReader<byte[]> reader,
                                            CancellationToken ct)
    {
        await foreach (byte[] frame in reader.ReadAllAsync(ct))
        {
            if (socket.State != WebSocketState.Open)
                break;
            await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
    }
```

**Gate after Task 4:** `dotnet test` — `StatusPushOnChangeTests` passes; the existing
`WebServerSmokeTests` (which reads the first text frame and asserts it `StartsWith("ST ")`) still passes
— the initial structured push is still a text frame starting with `"ST "` (now `"ST {...json...}"`), and
`StartsWith("ST ")` holds.

> **Builder check:** confirm `WebServerSmokeTests` only asserts `StartsWith("ST ")` (it does, per the
> shipped file lines 42–43) and does not assert the exact legacy `"ST apple"` string. If any smoke test
> asserts the full legacy string, update it to parse the JSON body (the test environment with no Apple
> ROM falls to the **demo** branch, which still sends the legacy one-shot `"ST demo"` text — so the smoke
> test, running asset-free, sees the legacy form and stays green unchanged).

---

### Task 5 — the client decodes the structured `ST` frame (read-only render)

`app.js` `handleStatusText` (lines 60–81) currently does `s.slice(3)` and string-matches the asset name.
Extend it to: detect a JSON body (`"ST {"`) and parse it; fall back to the legacy bare-asset string (so
the Spectrum/demo one-shot still works). On a JSON frame, update the status line with board + mode +
drive summary, set the banner from the asset, and **expose the parsed status on `window.machineStatus`**
so row T can bind drive panels to it without re-parsing.

#### 5a. Implement — `src/CpuEmulator.Surface.Web/wwwroot/app.js`

Replace `handleStatusText` (lines 60–81) with:

```javascript
  // Inbound text from the host: the "ST " status frame. Two shapes: a STRUCTURED JSON body
  // (the Apple surfaces, design D14 — board/asset/mode/per-drive motor+label, pushed on change) or the
  // LEGACY bare asset string (Spectrum/demo one-shot). Both start with "ST ". Read-only: the client never
  // fabricates these — every field is real machine state the host pushed.
  function handleStatusText(s) {
    if (!s.startsWith("ST ")) return;
    const body = s.slice(3);
    const banner = document.getElementById("asset-banner");
    banner.hidden = true;

    if (body.startsWith("{")) {
      let st;
      try { st = JSON.parse(body); } catch { return; }
      window.machineStatus = st;                 // row T binds drive panels to this
      applyAssetBanner(st.asset, banner);
      // The status line: board · mode · the active drive summary (read-only reflection).
      const active = (st.drives || []).find(d => d.motor);
      const driveText = active ? " · drive ●" : "";
      status.textContent = "connected · " + st.board + " · " + st.mode + driveText;
      return;
    }

    // Legacy bare-asset one-shot (Spectrum/demo).
    applyAssetBanner(body, banner);
  }

  // The asset → banner/status mapping (shared by both ST shapes). Preserves the shipped demo banner copy.
  function applyAssetBanner(stateName, banner) {
    if (stateName === "softcard-cpm-videx") {
      status.textContent = "connected · Apple ][+ SoftCard · CP/M · Videx 80-col";
    } else if (stateName === "softcard-cpm") {
      status.textContent = "connected · Apple ][+ SoftCard · CP/M";
    } else if (stateName === "apple-fallback-font") {
      status.textContent = "connected · Apple ][+ · fallback font";
    } else if (stateName && stateName.startsWith("apple")) {
      status.textContent = "connected · Apple ][+ · documented 6502";
    } else if (stateName === "spectrum") {
      status.textContent = "connected · ZX Spectrum";
    } else if (stateName === "demo") {
      status.textContent = "connected · demo fallback · no Apple ROM";
      banner.hidden = false;
      banner.textContent = "Apple ][+ ROMs not found — showing the demo pattern. " +
                           "Fetch them once: tools/get-apple2-roms.sh (or .ps1) — then reload this page.";
    }
  }
```

> For the structured frame, `applyAssetBanner` sets the status line first, then the board/mode line
> overwrites it — that is intentional: the structured frame's board+mode line is richer and wins; the
> banner (demo guidance) still shows when `asset === "demo"`. Since the Apple surfaces never send
> `asset === "demo"` (demo is the no-ROM Spectrum/demo branch, which sends the legacy string), the demo
> banner path is only reached via the legacy string — unchanged behavior.

This is **client glue only** (no production C# behavior); it is covered by the existing
`WebServerSmokeTests` HTTP/WS smoke (the `app.js` is served and the frames decode). A pixel/DOM UAT of the
live status line is row **T**'s territory; P's gate is the wire correctness (Tasks 1–4).

**No app.js test harness exists** (the project has no JS test runner); the client change is validated by
the server-side smoke test serving `app.js` 200 + the wire-format tests proving the bytes the client
parses. Keep the change minimal and mirror the shipped structure.

---

## The un-fakeable gate (the row's acceptance proof)

> **Gate:** *a host-side state change (drive motor on, mode switch) emits an `ST` frame the client decodes
> to the right indicator values; no indicator is client-fabricated.*

Encoded as deterministic tests (no asset needed, headless):

1. **`Apple2SurfaceStatusTests.Status_motor_flips_when_the_guest_turns_the_drive_motor_on`** — driving
   the **real** `$C0E9` motor switch through the live bus flips `Status().Drives[0].MotorOn` to `true`.
   A faked-on-insert indicator would be `true` at boot (it is `false`) and would not track the switch —
   the test asserts `false` at boot and `true` only after `$C0E9`, so a fabricated value fails.
2. **`Apple2VideoModeLabelTests.Mode_label_reflects_the_live_video_state_flags`** — flipping the real
   `Apple2VideoState` flags (the same object the renderer reads) changes the mode label; a hard-coded
   label fails the HIRES/LORES/MIXED/page assertions.
3. **`StatusPushOnChangeTests`** — the `ST` frame is pushed **once** initially and **again exactly when
   the snapshot changes** (the motor flips), and the pushed JSON carries `"motor":true` — proving the
   change is the real flag propagated to the wire, not a static or per-frame fabrication.
4. **`StatusFrameCodecTests`** — the encoded frame round-trips every field via JSON the client parses
   identically; equal snapshots → equal bytes (the change-detection contract).

Together: a real state change → a real snapshot delta → exactly one `ST` push → the client decodes the
real value. Nothing is faked at any layer.

---

## Self-review

- **Spec coverage:** D14/T-A (the `ST` seam carrying board, asset, per-drive motor+label, video mode,
  pushed real-not-faked) ✅. D1 mode label ✅. D10 "not faked on insert" — explicitly gated (Task 2c/3a,
  motor `false` at boot) ✅. The control-strip DOM (T-E) is **out of scope** (row T) — P ships the seam +
  proves the wire; the client render is the minimal read-only status line + `window.machineStatus` for T.
- **Placeholders:** none — every code block is literal and grounded against `c26faac`.
- **No fabrication:** the motor flag is the controller's real `_motorOn`; the mode is the live
  `Apple2VideoState`; the active source is the live `DisplayMultiplexer.ActiveIndex`. The client only
  renders what it decodes.
- **Backward-compat:** the wire tag stays `ST`; the Spectrum/demo legacy one-shot text frame is preserved
  (the no-asset smoke test stays green); the FB/AU binary path is untouched; new record params + ctor args
  are defaulted (additive).

---

## Shipped-API-vs-design-spec drift flagged

- **D14 says "a small additive `ST` (status) wire frame."** The shipped surface already uses `ST` as a
  **one-shot UTF-8 text** asset string (`Program.cs`). This plan **reuses the `ST` tag as a structured
  JSON text frame** rather than minting a new binary opcode — a deliberate, recorded deviation from a
  naive "new binary frame" reading, justified by the existing text/binary client split. **No behavior
  the design requires is lost.**
- **The motor + mode reads were test-only.** The shipped `Apple2DiskII` exposed motor/drive only via
  `*ForTest` inspectors and `Apple2Video` exposed no mode label. P promotes the narrow production reads
  (`Apple2DiskII.MotorOn`, `Apple2Video.ModeLabel`) — additive, the test inspectors stay.
- **Drive count:** PR-F/G model **drive 1 only** (the controller has a single `_image`). P reports **one**
  drive in `Status()`. The design's two-drive panel (T) is row T + row Q (Q adds the 2nd image slot). P's
  `Drives` list is sized to what the controller actually models today — when Q adds drive 2, the surface's
  `Status()` grows a second `DriveStatus` and P's frame carries it with no codec change.
```
