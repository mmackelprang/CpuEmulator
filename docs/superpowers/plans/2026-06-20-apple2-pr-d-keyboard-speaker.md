# Apple ][+ PR-D — `Apple2Keyboard` (`IKeyboardSink`) + `Apple2Speaker` (`IAudioSink`)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the ][+'s host-facing **keyboard** and **speaker** chips (ADR 0014 Decision 3). `Apple2Keyboard : IKeyboardSink` translates portable `KeyCode`/`Char` host events into the ][+'s **uppercase-only** 7-bit code set and latches them into the shared `Apple2VideoState` (the `$C000` latch the **already-shipped** IOU returns; `$C010` clears the strobe). `Apple2Speaker : IAudioSink` resamples the 1-bit `$C030` toggle log into S16 PCM, **reusing the `SpectrumUla` beeper sink pattern verbatim** (toggle log → both polarities → level-carry across frames). The un-fakeable gate: a `PostKey('a')` makes `$C000` return `0xC1` (bit-7 strobe + uppercase `$41`); `$C010` clears bit 7; and a logged sequence of `$C030` toggles renders a PCM frame with both polarities — all on synthetic state, **no ROM**.

**Architecture:** The IOU shipped in PR-B already (a) returns `state.KeyboardByte` for a `$C000` read and (b) clears the strobe on a `$C010` access, and (c) increments `state.SpeakerToggles` on every `$C030` access. So the **guest-facing** half is done. PR-D adds the two **host-facing** collaborators that drive / consume that shared `Apple2VideoState`:

- **`Apple2Keyboard : IKeyboardSink`** — `PostKey(in KeyEvent)` maps a `KeyEvent` to a ][+ 7-bit code via a new `Apple2KeyMap` (the analogue of `SpectrumKeyMatrix`) and calls `state.LatchKey(code)` on key-down (key-up is a no-op — the ][+ latch has no "release"; it holds the last key until `$C010`). Uppercase-only: a lowercase `Char` folds to its uppercase code; unmapped keys are a silent no-op.
- **`Apple2Speaker : IAudioSink`** — reads `state.SpeakerToggles` (the running count the IOU increments on each `$C030` bus access) and reconstructs the 1-bit waveform across the frame, emitting S16 PCM at the host rate. It mirrors the `SpectrumUla` beeper resampler: a per-frame toggle accounting, both polarities, and a level that carries into the next frame. Because the IOU only exposes a **count** (not per-toggle timestamps), the speaker derives toggle positions by spreading the frame's toggles evenly across the frame — the same pragmatic approximation `SpectrumUla.CurrentTInFrame` uses (the host pulls one frame at a time, so only relative spacing + the carried level matter for an audible square wave).

Both chips hold a reference to the **same** `Apple2VideoState` the IOU writes (ADR 0014 Decision 3's one-shared-mutable-object rule) — no new plumbing. Neither maps a bus page (the IOU owns `$C000`); they are `IPeripheral` only to receive `Realize` (the speaker schedules its ~60 Hz `AudioReady` tick there, the `SpectrumUla` precedent; the keyboard needs no tick). PR-H wires them into `Apple2Surface` as the `IKeyboardSink` / `IAudioSink` (the way `SpectrumSurface` hands the ULA to the host).

**Tech Stack:** C# / .NET 10, `IKeyboardSink.PostKey(in KeyEvent)`, `IAudioSink` (`SampleRate`/`ChannelCount`/`SamplesPerFrame`/`RenderAudio(Span<short>)`/`AudioReady`), the shared `Apple2VideoState` from PR-B, the `SpectrumUla` beeper resampler shape, xUnit. **Depends on PR-B** (the `Apple2VideoState` latch + speaker-toggle API, and the IOU that drives them). Namespace: `CpuEmulator.Peripherals`.

---

## Recon facts this plan is built on (verified against `main` @ `97a44d5`)

1. **`Apple2VideoState` (PR-B, shipped)** already exposes the keyboard latch + speaker toggle API PR-D consumes — confirmed in `src/CpuEmulator.Peripherals/Apple2VideoState.cs`:
   - `public byte KeyboardByte => (byte)((_strobe ? 0x80 : 0x00) | (_keyCode & 0x7F));`
   - `public void LatchKey(byte code) { _keyCode = (byte)(code & 0x7F); _strobe = true; }`
   - `public void ClearStrobe() => _strobe = false;`
   - `public long SpeakerToggles { get; private set; }` + `public void ToggleSpeaker() => SpeakerToggles++;`
   **PR-D adds no new `Apple2VideoState` API** — it drives `LatchKey` (keyboard) and reads `SpeakerToggles` (speaker). (If the speaker needs to *reset* the running count per frame, see Task 3 Step 3 — it tracks a private "last consumed" baseline rather than mutating the shared state, so the IOU's monotonic counter is untouched.)
2. **The IOU (PR-B, shipped)** already does the guest-facing half (`src/CpuEmulator.Peripherals/Apple2Iou.cs`): `$C000` read → `state.KeyboardByte`; `$C010` access → `state.ClearStrobe()`; `$C030` access → `state.ToggleSpeaker()`. The `Apple2IouTests.C000_read_returns_the_latched_key_and_C010_clears_the_strobe` test already passes against the latch. **PR-D does not modify the IOU.**
3. **`IKeyboardSink`** (`src/CpuEmulator.Core/IKeyboardSink.cs`): `void PostKey(in KeyEvent e)`. The host pushes; the chip owns translation; an unknown `KeyCode`/`KeyCode.None` is ignored (no-op).
4. **`KeyEvent`** (`src/CpuEmulator.Core/KeyEvent.cs`): `readonly record struct KeyEvent(KeyAction Action, KeyCode Key, char? Char)`. `Char` is the typed character when the host resolved one (e.g. `'A'` for Shift+A; `'a'` for an unshifted letter), else null. `KeyAction` is `Down`/`Up`.
5. **`KeyCode`** (`src/CpuEmulator.Core/KeyCode.cs`) covers letters A–Z, Digit0–9, `Space`, `Enter`, `Backspace`, `Tab`, `Escape`, the four arrows, plus the Spectrum modifiers. **No new enum value is needed for the base ][+ keyboard** (ADR 0014 + the design spec D7: Ctrl/Shift ride the `Char`/(future) `ctrl` fields; an unmapped Apple key would be an additive `KeyCode` arm, out of scope here). The keymap maps the printable subset to ][+ codes and treats the rest as no-ops.
6. **`IAudioSink`** (`src/CpuEmulator.Core/IAudioSink.cs`): `int SampleRate`, `int ChannelCount`, `int SamplesPerFrame`, `void RenderAudio(Span<short>)` (a too-small span throws `ArgumentException`), `event Action? AudioReady`. S16 samples, interleaved by channel.
7. **The `SpectrumUla` beeper resampler** (`src/CpuEmulator.Peripherals/SpectrumUla.cs:189-225`) is the pattern to mirror: `RenderAudio` walks a per-frame toggle accounting filling each sample with the level active at that sample's position; level 1 → `+amp`, level 0 → `-amp`; the final level **carries** into the next frame; the log resets. `SampleRate = 44100`, `SamplesPerFrame = SampleRate / frameRate`. (The ][+ frame rate is 60 Hz → `44100 / 60 = 735` samples/frame; the Spectrum used 50 Hz → 882.)
8. **The ][+ keyboard is uppercase-only with a 7-bit ASCII-ish code set** (research §6 / ADR 0014 Decision 3). Letters latch as their **uppercase** ASCII code (`'A'`..`'Z'` = `$41`..`$5A`); digits + common symbols latch as ASCII; `Enter` = `$0D` (CR); `Space` = `$20`; `Backspace`/left-arrow = `$08`; `Escape` = `$1B`. Bit 7 (the strobe) is added by the latch, not the keymap. (The ][+ has no lowercase; a lowercase `Char` folds up.)
9. **Neither chip maps a page.** The IOU owns the `$C000` page. The keyboard/speaker are wired as board peripherals (PR-H) purely to receive `Realize`. Their `Read`/`Write` are unreachable (no slot maps to them); they return a harmless default — the `Apple2Video` precedent (PR-C). PR-D constructs + drives them directly in tests over a shared `Apple2VideoState` (the bare-chip pattern of the Spectrum beeper/keyboard tests). **PR-D does not modify `Apple2Board`'s peripheral list** — wiring into the surface is PR-H.

---

## Conventions to follow

- **Device pattern** mirrors `SpectrumUla` (keyboard = `IKeyboardSink` translate-and-latch; speaker = `IAudioSink` resample-the-toggle-log; bind/schedule in `Realize`).
- **Shared state:** read/drive the one `Apple2VideoState`; never duplicate the latch or the toggle count.
- **No new `Apple2VideoState` API**; no IOU change; no board change (PR-H wires).
- **TDD per task**, literal code, commit per task. Warning-clean.
- **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Peripherals`
- **Create** `src/CpuEmulator.Peripherals/Apple2KeyMap.cs` — the portable `KeyCode`/`Char` → ][+ 7-bit code map (uppercase-only), pure + separately gated (the `SpectrumKeyMatrix` analogue).
- **Create** `src/CpuEmulator.Peripherals/Apple2Keyboard.cs` — `IPeripheral` + `IKeyboardSink`: `PostKey` translates + `state.LatchKey`.
- **Create** `src/CpuEmulator.Peripherals/Apple2Speaker.cs` — `IPeripheral` + `IAudioSink`: resample the `$C030` toggle log into S16 PCM (the beeper sink shape).

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2KeyMapTests.cs` — the keymap landmarks (uppercase fold, digits, Enter/Space/Backspace, unmapped no-op).
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2KeyboardTests.cs` — `PostKey` → `$C000` latch byte (via the IOU + the shared state); `$C010` clears the strobe; key-up no-op.
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2SpeakerTests.cs` — the un-fakeable PCM gate (steady level, both polarities, level-carry); plus the interpreter-tier gate (a real `STA $C030` loop produces toggles the speaker renders).

### Docs
- **Modify** `docs/BUILDER_QUEUE.md` — set row **D** to ✅; update the banner. (Planner pre-fills the plan link; Builder flips the status on merge.)

---

## Task 1: `Apple2KeyMap` — the uppercase-only ][+ key-code map

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2KeyMap.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2KeyMapTests.cs`

- [ ] **Step 1: Write the failing test (the code-set landmarks + uppercase fold + no-op)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2KeyMapTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2KeyMapTests
{
    [Theory]
    // Letters latch as UPPERCASE ASCII regardless of the typed Char's case.
    [InlineData(KeyCode.A, 'a', 0x41)]   // lowercase 'a' folds up -> $41 'A'
    [InlineData(KeyCode.A, 'A', 0x41)]   // shifted 'A' -> $41 'A'
    [InlineData(KeyCode.Z, 'z', 0x5A)]
    // Digits + common symbols latch as ASCII.
    [InlineData(KeyCode.Digit0, '0', 0x30)]
    [InlineData(KeyCode.Digit9, '9', 0x39)]
    // Whitespace / editing.
    [InlineData(KeyCode.Space, ' ', 0x20)]
    [InlineData(KeyCode.Enter, null, 0x0D)]       // CR
    [InlineData(KeyCode.Backspace, null, 0x08)]   // left-arrow / BS
    [InlineData(KeyCode.Escape, null, 0x1B)]
    public void Maps_a_key_to_the_uppercase_2plus_code(KeyCode key, char? ch, int expected)
    {
        Assert.True(Apple2KeyMap.TryMap(key, ch, out byte code));
        Assert.Equal((byte)expected, code);
    }

    [Fact]
    public void A_printable_char_with_no_dedicated_keycode_uses_the_uppercased_char()
    {
        // A symbol the host resolved to a Char (e.g. '/') maps to its ASCII even without a KeyCode arm.
        Assert.True(Apple2KeyMap.TryMap(KeyCode.None, '/', out byte code));
        Assert.Equal((byte)'/', code);
    }

    [Theory]
    [InlineData(KeyCode.None, null)]      // nothing typed
    [InlineData(KeyCode.Tab, null)]       // the ][+ keyboard has no Tab key code we model
    [InlineData(KeyCode.ArrowUp, null)]   // up-arrow: no base-][+ code (it is a later additive arm)
    public void Unmapped_keys_are_a_no_op(KeyCode key, char? ch)
    {
        Assert.False(Apple2KeyMap.TryMap(key, ch, out byte code));
        Assert.Equal(0, code);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2KeyMapTests"`
Expected: FAIL — `Apple2KeyMap` does not exist.

- [ ] **Step 3: Create the keymap**

Create `src/CpuEmulator.Peripherals/Apple2KeyMap.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>Maps a portable <see cref="KeyCode"/> + typed <see cref="char"/> to the Apple ][+'s
/// uppercase-only 7-bit key code (research §6, ADR 0014 Decision 3) — the analogue of
/// <see cref="SpectrumKeyMatrix"/>. The ][+ has no lowercase: letters fold to their UPPERCASE ASCII
/// ($41..$5A); digits + symbols latch as their ASCII; Enter=$0D (CR), Space=$20, Backspace/left-arrow
/// =$08, Escape=$1B. The strobe (bit 7) is added by the latch, not here. An unmapped key (no dedicated
/// arm AND no printable Char) is a no-op — the host's unknown-key behaviour (the SpectrumKeyMatrix
/// contract). Pure + separately gated.</summary>
public static class Apple2KeyMap
{
    /// <summary>Translate one host key to a ][+ 7-bit code. Returns false (code 0) for a key the ][+
    /// keyboard does not produce. <paramref name="ch"/> is the host-resolved typed character (null for
    /// non-printing keys); when a key has no dedicated arm but carried a printable Char, that Char's
    /// uppercase ASCII is used (so host-localised symbols still reach the guest).</summary>
    public static bool TryMap(KeyCode key, char? ch, out byte code)
    {
        switch (key)
        {
            // Dedicated control keys (Char is typically null for these).
            case KeyCode.Enter: code = 0x0D; return true;        // CR
            case KeyCode.Backspace: code = 0x08; return true;    // BS / left-arrow
            case KeyCode.Escape: code = 0x1B; return true;
            case KeyCode.Space: code = 0x20; return true;
        }

        // Letters: fold to uppercase ASCII regardless of the typed case.
        if (key is >= KeyCode.A and <= KeyCode.Z)
        {
            code = (byte)('A' + (key - KeyCode.A));   // $41..$5A
            return true;
        }

        // Digits: the top-row 0..9 -> ASCII '0'..'9'.
        if (key is >= KeyCode.Digit0 and <= KeyCode.Digit9)
        {
            code = (byte)('0' + (key - KeyCode.Digit0));   // $30..$39
            return true;
        }

        // No dedicated key arm: if the host resolved a printable Char, latch its UPPERCASE ASCII.
        if (ch is char c && c is >= ' ' and <= '~')
        {
            code = (byte)char.ToUpperInvariant(c);
            return true;
        }

        code = 0;
        return false;   // unmapped -> no-op (the host's unknown-key contract)
    }
}
```

- [ ] **Step 4: Run the keymap gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2KeyMapTests"`
Expected: PASS. **This is the keymap gate** — uppercase fold, digits, control keys, printable-Char fallback, and the unmapped no-op.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2KeyMap.cs tests/CpuEmulator.Tests/Apple2/Apple2KeyMapTests.cs
git commit -m "feat(peripherals): Apple2KeyMap — uppercase-only ][+ key-code map"
```

---

## Task 2: `Apple2Keyboard` — `IKeyboardSink` translate-and-latch

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2Keyboard.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2KeyboardTests.cs`

- [ ] **Step 1: Write the failing test (PostKey → the `$C000` latch via the IOU; `$C010` clears)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2KeyboardTests.cs`. The gate drives the **real** IOU read path (the shipped guest-facing half) so it proves the whole keyboard pipe end to end on synthetic state:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2KeyboardTests
{
    // The keyboard chip + the IOU share ONE Apple2VideoState (ADR 0014 Decision 3). PostKey drives the
    // latch the IOU reads at $C000 — so we assert through the IOU, exactly as the guest would.
    private static (Apple2Keyboard kbd, Apple2Iou iou, Apple2VideoState state) Build()
    {
        var state = new Apple2VideoState();
        return (new Apple2Keyboard(state), new Apple2Iou(state), state);
    }

    [Fact]
    public void PostKey_lowercase_a_latches_uppercase_with_the_strobe_at_C000()
    {
        var (kbd, iou, _) = Build();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a'));   // host typed lowercase 'a'
        // $C000 read: bit7 strobe set + uppercase $41 => $C1.
        Assert.Equal(0xC1u, iou.Read(0x00, AccessWidth.Byte));
    }

    [Fact]
    public void C010_clears_the_strobe_but_keeps_the_code()
    {
        var (kbd, iou, _) = Build();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Z, 'z'));
        Assert.Equal(0xDAu, iou.Read(0x00, AccessWidth.Byte));       // strobe + $5A
        iou.Read(0x10, AccessWidth.Byte);                            // $C010: clear strobe
        Assert.Equal(0x5Au, iou.Read(0x00, AccessWidth.Byte) & 0xFF); // strobe gone, $5A retained
    }

    [Fact]
    public void Key_up_is_a_no_op_the_latch_holds_the_last_key()
    {
        var (kbd, iou, _) = Build();
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a'));
        kbd.PostKey(new KeyEvent(KeyAction.Up, KeyCode.A, null));    // release: the ][+ latch is unchanged
        Assert.Equal(0xC1u, iou.Read(0x00, AccessWidth.Byte));       // still $C1 (strobe + 'A')
    }

    [Fact]
    public void An_unmapped_key_does_not_disturb_the_latch()
    {
        var (kbd, iou, state) = Build();
        state.LatchKey(0x42);                                        // 'B' already waiting
        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.Tab, null)); // no ][+ Tab code -> no-op
        Assert.Equal(0xC2u, iou.Read(0x00, AccessWidth.Byte));       // strobe + $42 unchanged
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2KeyboardTests"`
Expected: FAIL — `Apple2Keyboard` does not exist.

- [ ] **Step 3: Create `Apple2Keyboard`**

Create `src/CpuEmulator.Peripherals/Apple2Keyboard.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ keyboard (ADR 0014 Decision 3): a host-facing IKeyboardSink that translates
/// portable KeyEvents into the ][+'s uppercase-only 7-bit code set (Apple2KeyMap) and latches them into
/// the shared Apple2VideoState the IOU reads at $C000 (bit 7 = strobe). It owns no bus page — the IOU
/// owns $C000; this chip is an IPeripheral only to receive Realize (it needs no tick). The ][+ latch
/// has no "release": a key-up is a no-op (the latch holds the last key until the guest reads $C010,
/// which the IOU handles). One shared Apple2VideoState, no duplication.</summary>
public sealed class Apple2Keyboard : IPeripheral, IKeyboardSink
{
    private readonly Apple2VideoState _state;

    /// <param name="state">The shared latch/mode state the IOU also holds (ADR 0014 Decision 3).</param>
    public Apple2Keyboard(Apple2VideoState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public string Name => "apple2keyboard";

    public void Realize(IMachineContext context) { /* no bus page, no tick, no IRQ */ }

    // The chip maps no page; these are unreachable but must satisfy IPeripheral.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    // ── IKeyboardSink: translate + latch on key-down; key-up is a no-op (the ][+ latch holds). ──
    public void PostKey(in KeyEvent e)
    {
        if (e.Action != KeyAction.Down)
            return; // the ][+ keyboard latch has no release event
        if (Apple2KeyMap.TryMap(e.Key, e.Char, out byte code))
            _state.LatchKey(code);  // sets the 7-bit code + raises the strobe (bit 7)
        // an unmapped key is silently ignored (the IKeyboardSink unknown-key contract)
    }
}
```

- [ ] **Step 4: Run the keyboard gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2KeyboardTests"`
Expected: PASS. **This is the keyboard gate** — `PostKey('a')` → `$C000` reads `0xC1` (strobe + uppercase `$41`); `$C010` clears the strobe; key-up + unmapped keys leave the latch alone — asserted **through the real IOU read path**, no ROM.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2Keyboard.cs tests/CpuEmulator.Tests/Apple2/Apple2KeyboardTests.cs
git commit -m "feat(peripherals): Apple2Keyboard (IKeyboardSink) — translate + latch into the shared state"
```

---

## Task 3: `Apple2Speaker` — `IAudioSink` 1-bit toggle resampler (the beeper sink shape)

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2Speaker.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2SpeakerTests.cs`

- [ ] **Step 1: Write the failing PCM gate (steady level + both polarities + level-carry)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2SpeakerTests.cs`. The speaker reads the IOU's `$C030` toggle count, so the gate drives toggles **through the IOU** (each `$C030` access = one toggle) and asserts the rendered PCM — the `SpectrumBeeperTests` shape:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2SpeakerTests
{
    private static (Apple2Speaker spk, Apple2Iou iou, Apple2VideoState state) Build()
    {
        var state = new Apple2VideoState();
        return (new Apple2Speaker(state), new Apple2Iou(state), state);
    }

    [Fact]
    public void No_toggles_renders_a_constant_waveform()
    {
        var (spk, _, _) = Build();
        var pcm = new short[spk.SamplesPerFrame];
        spk.RenderAudio(pcm);
        // Steady level (no toggles) => every sample is the same value (a flat line, no square wave).
        short first = pcm[0];
        Assert.All(pcm.ToArray(), s => Assert.Equal(first, s));
    }

    [Fact]
    public void Toggling_C030_within_a_frame_produces_both_polarities()
    {
        var (spk, iou, _) = Build();
        // Three $C030 accesses across the frame => the flip-flop visits both 0 and 1.
        iou.Read(0x30, AccessWidth.Byte);
        iou.Read(0x30, AccessWidth.Byte);
        iou.Read(0x30, AccessWidth.Byte);

        var pcm = new short[spk.SamplesPerFrame];
        spk.RenderAudio(pcm);

        bool anyHigh = false, anyLow = false;
        foreach (short s in pcm) { if (s > 0) anyHigh = true; if (s < 0) anyLow = true; }
        Assert.True(anyHigh, "expected some positive (speaker-high) samples");
        Assert.True(anyLow, "expected some negative (speaker-low) samples");
    }

    [Fact]
    public void The_level_carries_into_the_next_frame()
    {
        var (spk, iou, _) = Build();
        iou.Read(0x30, AccessWidth.Byte);          // one toggle: level flips to high and STAYS
        var first = new short[spk.SamplesPerFrame];
        spk.RenderAudio(first);                     // consumes the toggle; ends high

        var second = new short[spk.SamplesPerFrame];
        spk.RenderAudio(second);                    // no new toggles => steady HIGH this frame
        Assert.All(second.ToArray(), s => Assert.True(s > 0));
    }

    [Fact]
    public void RenderAudio_rejects_a_too_small_span()
    {
        var (spk, _, _) = Build();
        Assert.Throws<ArgumentException>(() => spk.RenderAudio(new short[4]));
    }

    [Fact]
    public void Sink_reports_the_host_audio_shape()
    {
        var (spk, _, _) = Build();
        Assert.Equal(44100, spk.SampleRate);
        Assert.Equal(1, spk.ChannelCount);
        Assert.Equal(44100 / 60, spk.SamplesPerFrame);   // 735
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2SpeakerTests"`
Expected: FAIL — `Apple2Speaker` does not exist.

- [ ] **Step 3: Create `Apple2Speaker`**

Create `src/CpuEmulator.Peripherals/Apple2Speaker.cs`. It mirrors the `SpectrumUla` beeper resampler, but the IOU exposes a monotonic **count** (not per-toggle timestamps), so the speaker tracks how many toggles it has already consumed (`_lastConsumed`), spreads this frame's new toggles evenly across the frame (the `SpectrumUla.CurrentTInFrame` approximation), and carries the ending level:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ 1-bit speaker (ADR 0014 Decision 3): a host-facing IAudioSink that resamples
/// the $C030 toggle stream into S16 PCM, reusing the SpectrumUla beeper sink approach (1-bit DAC: each
/// toggle flips the level; level 1 -> +amp, level 0 -> -amp; the ending level carries into the next
/// frame). The IOU increments Apple2VideoState.SpeakerToggles on every $C030 bus access (so an
/// STA $C030 double-toggles — the RMW dummy read + the store — naturally). This chip reads that
/// monotonic count, derives how many NEW toggles happened this frame, spreads them evenly across the
/// frame (the IOU exposes a count, not timestamps — the same pragmatic approximation the Spectrum
/// beeper uses; only relative spacing + the carried level matter for an audible square wave), and emits
/// the frame. It owns no bus page (the IOU owns $C030); it is IPeripheral only to schedule the ~60 Hz
/// AudioReady tick in Realize (the SpectrumUla precedent). One shared Apple2VideoState, no duplication.</summary>
public sealed class Apple2Speaker : IPeripheral, IAudioSink
{
    private const int HostSampleRate = 44100;
    private const int FrameRate = 60;                          // the ][+ present cadence (PR-C)
    private const int SamplesFrame = HostSampleRate / FrameRate; // 735
    private const long CyclesPerFrame = 17030;                 // ~1.0205 MHz / 60 Hz (matches Apple2Video)
    private const short Amp = 12000;

    private readonly Apple2VideoState _state;
    private long _lastConsumed;   // SpeakerToggles value at the end of the previous frame
    private int _level;           // the current flip-flop level (0/1), carried across frames

    public Apple2Speaker(Apple2VideoState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _lastConsumed = state.SpeakerToggles;
    }

    public string Name => "apple2speaker";
    public int SampleRate => HostSampleRate;
    public int ChannelCount => 1;
    public int SamplesPerFrame => SamplesFrame;
    public event Action? AudioReady;

    public void Realize(IMachineContext context)
    {
        // Schedule the per-frame audio pull tick; no IRQ on the bare ][+ (the SpectrumUla precedent).
        context.Scheduler.ScheduleEvery(CyclesPerFrame, () => AudioReady?.Invoke());
    }

    // The chip maps no page; these are unreachable but must satisfy IPeripheral.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    /// <summary>Test-only: stand in for the scheduler tick so a unit test can assert AudioReady without
    /// building a full Machine.</summary>
    internal void RaiseAudioForTest() => AudioReady?.Invoke();

    // ── IAudioSink: reconstruct the 1-bit waveform from this frame's toggles into S16 PCM. ──
    public void RenderAudio(Span<short> samples)
    {
        if (samples.Length < SamplesFrame)
            throw new ArgumentException($"need {SamplesFrame} samples; got {samples.Length}.", nameof(samples));

        long now = _state.SpeakerToggles;
        long newToggles = now - _lastConsumed;   // toggles since the previous frame
        _lastConsumed = now;

        if (newToggles <= 0)
        {
            // Steady level all frame (a flat line — no square wave).
            short flat = _level != 0 ? Amp : (short)-Amp;
            for (int s = 0; s < SamplesFrame; s++)
                samples[s] = flat;
            return;
        }

        // Spread the toggles evenly across the frame: toggle k (0-based) lands at sample
        // floor((k + 1) * SamplesFrame / (newToggles + 1)). Walk samples, flipping the level as each
        // toggle boundary is crossed. (Relative spacing + the carried level are what matter audibly.)
        int nextToggle = 0;
        for (int s = 0; s < SamplesFrame; s++)
        {
            while (nextToggle < newToggles &&
                   s >= (int)((nextToggle + 1L) * SamplesFrame / (newToggles + 1)))
            {
                _level ^= 1;   // flip the 1-bit flip-flop
                nextToggle++;
            }
            samples[s] = _level != 0 ? Amp : (short)-Amp;
        }
    }
}
```

- [ ] **Step 4: Run the PCM gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2SpeakerTests"`
Expected: PASS. **This is the speaker resampler gate** — no toggles → flat; toggles within a frame → both polarities; the ending level carries; a too-small span throws; the sink reports `44100 / 1 / 735`.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2Speaker.cs tests/CpuEmulator.Tests/Apple2/Apple2SpeakerTests.cs
git commit -m "feat(peripherals): Apple2Speaker (IAudioSink) — 1-bit $C030 toggle resampler (beeper sink shape)"
```

---

## Task 4: The un-fakeable interpreter-tier gate — a real `STA $C030` loop drives audible toggles

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2SpeakerTests.cs` (add the on-board interpreter gate)

This is the row-D interpreter-as-oracle gate: a **real 6502 program** running on a built `Machine` (interpreter tier) toggles `$C030`, and the speaker — sharing the board's `Apple2VideoState` — renders a non-flat frame. It proves the whole pipe (6502 bus access → IOU `$C030` decode → `state.ToggleSpeaker()` → `Apple2Speaker.RenderAudio`) with no faking.

- [ ] **Step 1: Write the failing interpreter gate**

Append to `Apple2SpeakerTests`:

```csharp
    // A 12 KiB system ROM whose reset vector points into a NOP loop (the Apple2BoardTests shape).
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x0000] = 0xEA;                                              // NOP at $D000
        rom[0x0001] = 0x4C; rom[0x0002] = 0x00; rom[0x0003] = 0xD0;      // JMP $D000
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;                          // reset -> $D000
        return rom;
    }

    [Fact]
    public void A_real_STA_C030_loop_makes_the_speaker_render_a_square_wave()
    {
        // Build a real ][+ board; the speaker shares the board's Apple2VideoState (the IOU writes it).
        var state = new Apple2VideoState();
        var iou = new CpuEmulator.Peripherals.Apple2Iou(state);
        var speaker = new Apple2Speaker(state);
        var spec = CpuEmulator.Machines.Apple2Board.Spec(SystemRom(), iou);
        var machine = CpuEmulator.Machines.BoardMachineFactory.Build(spec);   // interpreter tier (the oracle)
        var bus = machine.Space(AddressSpaceKind.Program);

        // $0300: LDA $C030 ; JMP $0300  (LDA = one bus access = one toggle per loop; tight + cheap)
        bus.Write8(0x0300, 0xAD); bus.Write8(0x0301, 0x30); bus.Write8(0x0302, 0xC0); // LDA $C030
        bus.Write8(0x0303, 0x4C); bus.Write8(0x0304, 0x00); bus.Write8(0x0305, 0x03); // JMP $0300
        machine.Cpu.SetRegister("PC", 0x0300);

        long before = state.SpeakerToggles;
        machine.Run(2000);                 // many LDA/JMP iterations -> many $C030 accesses
        Assert.True(state.SpeakerToggles > before + 10,
            $"expected the loop to toggle the speaker many times; got {state.SpeakerToggles - before}");

        var pcm = new short[speaker.SamplesPerFrame];
        speaker.RenderAudio(pcm);
        bool anyHigh = false, anyLow = false;
        foreach (short s in pcm) { if (s > 0) anyHigh = true; if (s < 0) anyLow = true; }
        Assert.True(anyHigh && anyLow,
            "a real STA/LDA $C030 loop on the interpreter must render a non-flat (both-polarity) frame");
    }
```

- [ ] **Step 2: Run it to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2SpeakerTests.A_real_STA"`
Expected: PASS. **This is the row-D interpreter-tier gate (interpreter-as-oracle):** a real 6502 `$C030` loop, with no faked toggles, makes the speaker render an audible square wave — the whole keyboard/speaker arc validated on the oracle tier.

- [ ] **Step 3: Run the full Apple2 suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2"`
Expected: PASS — PR-B/C gates + PR-D's keymap/keyboard/speaker gates all green.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2SpeakerTests.cs
git commit -m "test(apple2): interpreter-tier gate — a real $C030 loop drives the speaker resampler"
```

---

## Task 5: Queue update

**Files:**
- Modify: `docs/BUILDER_QUEUE.md`

- [ ] **Step 1: Flip the queue row**

In `docs/BUILDER_QUEUE.md`, set row **D** status to ✅ and update the **Last updated** banner with the date + "PR-D merged". (The Plan column already links here; Planner pre-filled it.)

- [ ] **Step 2: Commit**

```bash
git add docs/BUILDER_QUEUE.md
git commit -m "docs(queue): Apple2 PR-D (keyboard + speaker) done"
```

---

## Done-when

- `Apple2KeyMap` maps the portable `KeyCode`/`Char` set to the ][+'s uppercase-only 7-bit codes (letters fold up; digits + symbols are ASCII; Enter/Space/Backspace/Escape are the control codes); unmapped keys are a no-op.
- `Apple2Keyboard : IKeyboardSink` translates + latches on key-down into the shared `Apple2VideoState`, so a `$C000` read (through the **real shipped IOU**) returns bit-7 strobe + the uppercase code, and `$C010` clears the strobe; key-up + unmapped keys leave the latch untouched.
- `Apple2Speaker : IAudioSink` resamples the `$C030` toggle count into S16 PCM (no toggles → flat; toggles → both polarities; the ending level carries; the beeper sink shape), at `44100 / 1 / 735`.
- The **interpreter-tier gate** runs a real 6502 `$C030` loop on a built `Machine` and renders a non-flat frame — no faked toggles (interpreter-as-oracle).
- All gates run on synthetic state / synthetic ROM, **no asset** — the un-fakeable Spectrum-style posture.
- No `Apple2VideoState`, IOU, or board change (PR-H wires the chips into `Apple2Surface`). Queue row **D** is ✅.

---

## Notes for the PR-H planner (deferred — when PR-H reaches the front)

- `Apple2Keyboard` / `Apple2Speaker` are constructed + driven directly in PR-D's tests over a shared `Apple2VideoState`; **PR-H wires them into the surface** the way `SpectrumSurface` hands the ULA to `MachineHost` as the `IKeyboardSink`/`IAudioSink`. They are added to the board's peripheral list (or constructed beside it) so their `Realize` runs (the speaker's `AudioReady` tick).
- The design spec's `Ctrl` passthrough (D5: `Ctrl+B`/`Ctrl+C` AND the letter with `$1F`) and the `Ctrl+Backspace` RESET binding (D4) are **surface/wire concerns (Designer task T-F)** — they ride a future additive `ctrl` field on the inbound key JSON, decoded in `Apple2KeyMap` as an additive arm. PR-D ships the base printable map; the `Ctrl` fold is a small follow-on in the surface PR (it does not change the chips' shape).
