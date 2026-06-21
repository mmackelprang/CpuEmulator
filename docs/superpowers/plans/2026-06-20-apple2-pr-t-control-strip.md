# PR-T — Apple ][+ control-strip UI + keyboard T-F (incl. D5 ctrl-wiring) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the visible Apple ][+ control strip — two bordered drive panels (library select + upload picker + eject + the current-image label + a REAL-motor amber light), the calm named-script asset banner (replacing the silent fallback), the read-only video-mode label — consuming the already-shipped P/R/S seams, PLUS the keyboard T-F extension **including D5** (thread `ctrlKey` through the wire + chip so `Ctrl+B`/`Ctrl+C` genuinely produce control codes), and the single new `--drive-active` (amber `#d8a657`) token.

**Architecture:** The shipped surface arc already ships every *seam* this row needs: the `ST` status frame (`window.machineStatus` — board / asset / mode / per-drive `{motor,label}`), the disk-library transport (`GET /disks` → `window.diskCatalog`, `window.insertFromLibrary`, `window.ejectDrive`), and the upload transport (`window.uploadDisk`, `window.uploadState`, `window.uploadLastError`, the `st.upload` ack route). **T builds the DOM that binds to them** — the drive panels, the banner, the mode label — entirely in `app.js` + `index.html` from the shipped Spectrum tokens plus the one new `--drive-active` amber. The surface stays a **dumb reflector**: every indicator (the motor light, the image label, the mode label) is painted from the host-pushed `ST` snapshot on its arrival — nothing is client-fabricated. **The one server/chip change is D5** (the decided T-F scope, option B/full): `KeyEvent` gains a `Ctrl` field, `FrameCodec.TryDecodeKey` reads a `ctrl` JSON field, `Apple2KeyMap.TryMap` folds a letter with `$1F` when `ctrl` is set, and `app.js` sends `ctrl` + `preventDefault`s `Ctrl+B`/`Ctrl+C`. This change carries its **own un-fakeable interpreter gate**: a `Ctrl+B` key event posted to the real keyboard chip latches `$02` (not `$42`) at `$C000` over the live bus.

**Tech Stack:** C# 12 / .NET 8 minimal-API WebSockets, `System.Text.Json`; xUnit + `WebApplicationFactory<Program>` (served-asset content assertions) + the existing public-seam dispatch pattern; vanilla browser JS (`app.js` IIFE, no build step, no framework). `KeyEvent` is `CpuEmulator.Core`; `Apple2KeyMap`/`Apple2Keyboard` are `CpuEmulator.Peripherals`.

## Global Constraints

- **Branch + PR:** all work on `feat/apple2-control-strip`; open a PR to `main`; do not commit to `main` directly.
- **DEPENDS ON P, R, S (all ✅ shipped):** T consumes `window.machineStatus` (P), `window.diskCatalog`/`insertFromLibrary`/`ejectDrive` (R), `window.uploadDisk`/`uploadState`/`uploadLastError` + the `st.upload` ack route (S). All exist in the shipped `app.js` (lines grounded below). T adds DOM + the D5 chip/wire change; it does **not** re-author the transport helpers.
- **Interpreter-first invariant:** the D5 gate runs on the interpreter tier (the keyboard chip is tier-agnostic — it latches into `Apple2VideoState`; the bus read of `$C000` is the same on both tiers, but the gate is written interpreter-tier per the queue invariant).
- **The surface is a dumb reflector.** No indicator is fabricated client-side. The motor light reads `st.drives[i].motor` (the REAL `$C0E8/$C0E9` line + the ~1 s 556 off-delay the controller owns — `Apple2DiskII.MotorOn`, false at boot). The image label reads `st.drives[i].label`. The mode label reads `st.mode`. If a field is absent, the panel shows its empty/idle default — never a guess.
- **One new visual value only:** `--drive-active` = amber `#d8a657` (from `tokens.md`). The off/idle light reuses `--muted` (`#888`); the upload spinner reuses the amber. Everything else reuses the shipped Spectrum inline-style values (`#111`/`#ccc`/`#333`/`#222`/`#444`/`#888`/`12px`/`system-ui`). Do **not** introduce a CSS framework, a build step, a web font, an icon set, or any spacing scale beyond the existing `gap`/`padding`.
- **Copy is verbatim from `docs/design-handoffs/apple-2-plus/copy.md`.** No emoji in any UI string (project convention + `copy.md` §9). Never red, never `alert()`, never a stack trace; a missing asset is a calm first-run condition.
- **Honesty bar:** the hint line advertises `Ctrl+B = BASIC`. D5 makes that real — `Ctrl+B`/`Ctrl+C` must produce a control code end-to-end, not a lying affordance. `.woz` library items render disabled-with-note; `.woz` uploads honestly reject (the shipped server behavior — `WozFluxImage` is the separate backlog row W).
- **Comment policy / structured style:** match the existing `app.js`/`FrameCodec.cs`/`Apple2KeyMap.cs` doc-comment density; no emojis.
- **No new NuGet dependencies. No new JS toolchain** (the gate strategy is hybrid C#-served-asset assertions + the existing public-seam dispatch, never a headless browser).
- **Ground truth HEAD:** `main` @ `f4755e5` (PRs #99–#123 merged). All literal code below calls the shipped signatures verified in the files cited per task.

---

## File Structure

**New files:**
- `tests/CpuEmulator.Tests/Apple2/Apple2CtrlKeyTests.cs` — the D5 un-fakeable interpreter gate: a `Ctrl+B` `KeyEvent` posted to a real `Apple2Keyboard` latches `$02` at `$C000`; `Ctrl+C` latches `$03`; a non-ctrl `B` latches `$42`; `Apple2KeyMap.TryMap` folds `ctrl` with `$1F`.
- `tests/CpuEmulator.Tests/Surface/KeyEventCtrlDecodeTests.cs` — `FrameCodec.TryDecodeKey` reads the new `ctrl` JSON field (true / false / absent → default false), and round-trips a `Ctrl+B` JSON to a `KeyEvent` whose `Ctrl` is true.
- `tests/CpuEmulator.Tests/Surface/ControlStripAssetTests.cs` — the served-asset content gate (`WebApplicationFactory`): `/app.js` carries the drive-panel render + the control wiring + the `ctrl` send; `/index.html` carries the two drive-panel containers, the mode label, the `--drive-active` token, and the asset banner.

**Modified files:**
- `src/CpuEmulator.Core/KeyEvent.cs` — add the trailing positional `bool Ctrl = false` (non-breaking: all 3-arg call sites compile unchanged).
- `src/CpuEmulator.Peripherals/Apple2KeyMap.cs` — `TryMap` gains a `bool ctrl` parameter (defaulted) that folds a letter/printable with `$1F`.
- `src/CpuEmulator.Peripherals/Apple2Keyboard.cs` — `PostKey` passes `e.Ctrl` into `TryMap`.
- `src/CpuEmulator.Surface.Web/FrameCodec.cs` — `TryDecodeKey` reads the `ctrl` JSON field into `KeyEvent.Ctrl`.
- `src/CpuEmulator.Surface.Web/wwwroot/index.html` — the `<style>` gains the `--drive-active` token + the control-strip rules; the `<body>` gains the mode-label element, the two drive-panel containers, and the (already-present) `asset-banner` is kept; the hint line stays (it already names `Ctrl+B` / `Ctrl+Backspace`).
- `src/CpuEmulator.Surface.Web/wwwroot/app.js` — the control-strip renderer (`renderControlStrip`) driven by `window.machineStatus`/`window.diskCatalog`/`window.uploadState`; the panel event wiring (library select onchange → `insertFromLibrary`, `Insert…` → hidden file input → `uploadDisk`, `Eject` → `ejectDrive`); `sendKey` adds the `ctrl` field + `Ctrl+B`/`Ctrl+C` `preventDefault`; `applyAssetBanner` already exists — keep it (the demo banner copy is shipped). The mode-label paint from `st.mode`.

**Note — no new C# production file:** D5 is additive edits to four shipped files; the control strip is HTML/JS only. The only new C# files are tests.

---

## Task 1: `KeyEvent.Ctrl` — thread the ctrl modifier into the Core key event (D5, part 1)

**Files:**
- Modify: `src/CpuEmulator.Core/KeyEvent.cs`
- (No new test here — the field is exercised by Task 2's chip gate and Task 4's decode gate.)

**Interfaces:**
- The shipped `KeyEvent` (verified `src/CpuEmulator.Core/KeyEvent.cs` line 15): `public readonly record struct KeyEvent(KeyAction Action, KeyCode Key, char? Char);`
- Add a trailing positional `bool Ctrl = false`. A positional record-struct parameter **may** carry a default; all 8 shipped 3-arg `new KeyEvent(...)` call sites (FrameCodec + the Apple2/Spectrum/Demo/Sp0/MachineHost tests) compile unchanged because the 4th positional defaults. This is the minimal non-breaking migration.

- [ ] **Step 1: Edit `KeyEvent.cs`** (replace the record declaration; keep the `KeyAction` enum + the doc comment, extending it)

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
/// <see cref="Ctrl"/> is the Control-modifier state at the time of the event (the browser's
/// <c>KeyboardEvent.ctrlKey</c>) — defaulted false so every pre-existing 3-arg call site is
/// unchanged; the Apple ][+ keyboard chip ANDs a letter code with $1F when it is set (ADR 0014
/// Decision 3 / interactions §2.4), so Ctrl+B/Ctrl+C produce real control codes. Machines that
/// ignore Ctrl (e.g. the Spectrum, which uses its own CapsShift/SymbolShift) simply never read it.
/// </summary>
public readonly record struct KeyEvent(KeyAction Action, KeyCode Key, char? Char, bool Ctrl = false);
```

- [ ] **Step 2: Build the solution** — confirm zero call-site breakage. `dotnet build` (the 8 shipped 3-arg `new KeyEvent(...)` sites must still compile). No test change in this task.

**Verification:** `dotnet build` warning-clean; no existing test references break.

---

## Task 2: `Apple2KeyMap` + `Apple2Keyboard` fold the letter with $1F on ctrl (D5, part 2) — the interpreter gate

**Files:**
- Modify: `src/CpuEmulator.Peripherals/Apple2KeyMap.cs`
- Modify: `src/CpuEmulator.Peripherals/Apple2Keyboard.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2CtrlKeyTests.cs`

**Interfaces:**
- Shipped `Apple2KeyMap.TryMap` (verified `Apple2KeyMap.cs` line 18): `public static bool TryMap(KeyCode key, char? ch, out byte code)`. Add a trailing `bool ctrl = false` parameter; when `ctrl` is true AND the mapped code is a letter/printable, return `code & 0x1F` (the ASCII control code: `B`=$42 → $02, `C`=$43 → $03). The control fold applies to the printable range; the dedicated control keys (Enter/BS/Esc/Space) are left as-is (a `Ctrl+Enter` is still CR — the real ][+ has no distinct ctrl form for those, and `$0D & $1F = $0D` anyway, so the masking is harmless even if applied; we scope the fold to letters/printables to be explicit).
- Shipped `Apple2Keyboard.PostKey` (verified `Apple2Keyboard.cs` lines 31–38): on key-down it calls `Apple2KeyMap.TryMap(e.Key, e.Char, out byte code)` then `_state.LatchKey(code)`. Change the call to pass `e.Ctrl`.
- The latch: `Apple2VideoState.LatchKey(byte code)` masks to 7 bits (`_keyCode = code & 0x7F`) and raises bit 7; `$C000` returns `(strobe?0x80:0)|(_keyCode&0x7F)` (verified `Apple2VideoState.cs` lines 24–27). So a latched `$02` reads back as `$82` at `$C000`; the 7-bit code (`& 0x7F`) is `$02`. The gate asserts the 7-bit code.

- [ ] **Step 1: Write the failing test** `tests/CpuEmulator.Tests/Apple2/Apple2CtrlKeyTests.cs`

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

/// <summary>D5 (interactions §2.4): the Apple ][+ keyboard produces a control code for Ctrl+letter —
/// the chip ANDs the uppercase letter code with $1F. The un-fakeable proof is at the $C000 latch the
/// IOU reads: a Ctrl+B event latches $02 (not $42); a Ctrl+C latches $03; a plain B latches $42. The
/// keyboard chip is tier-agnostic (it latches into the shared Apple2VideoState), so this is the
/// interpreter-tier gate the queue's T-F row requires.</summary>
public class Apple2CtrlKeyTests
{
    // The pure map fold: a letter with ctrl set returns its ASCII control code.
    [Theory]
    [InlineData(KeyCode.B, 'b', 0x02)]   // Ctrl+B -> STX (enter BASIC)
    [InlineData(KeyCode.C, 'c', 0x03)]   // Ctrl+C -> ETX (break)
    [InlineData(KeyCode.M, 'm', 0x0D)]   // Ctrl+M -> CR (the real ][+ equivalence)
    public void TryMap_folds_a_letter_with_1F_when_ctrl(KeyCode key, char ch, byte expected)
    {
        Assert.True(Apple2KeyMap.TryMap(key, ch, out byte code, ctrl: true));
        Assert.Equal(expected, code);
    }

    [Fact]
    public void TryMap_without_ctrl_is_the_plain_uppercase_letter()
    {
        Assert.True(Apple2KeyMap.TryMap(KeyCode.B, 'b', out byte code, ctrl: false));
        Assert.Equal(0x42, code);   // 'B'
    }

    // The end-to-end latch: a Ctrl+B KeyEvent posted to the real chip latches $02 at $C000 (7-bit code).
    [Fact]
    public void Ctrl_B_latches_02_at_the_keyboard_byte()
    {
        var state = new Apple2VideoState();
        var kbd = new Apple2Keyboard(state);

        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.B, 'b', Ctrl: true));

        Assert.Equal(0x82, state.KeyboardByte);            // strobe (bit7) + $02
        Assert.Equal(0x02, state.KeyboardByte & 0x7F);     // the 7-bit control code (not $42)
    }

    [Fact]
    public void Ctrl_C_latches_03_at_the_keyboard_byte()
    {
        var state = new Apple2VideoState();
        var kbd = new Apple2Keyboard(state);

        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.C, 'c', Ctrl: true));

        Assert.Equal(0x03, state.KeyboardByte & 0x7F);
    }

    [Fact]
    public void Plain_B_without_ctrl_still_latches_42()
    {
        var state = new Apple2VideoState();
        var kbd = new Apple2Keyboard(state);

        kbd.PostKey(new KeyEvent(KeyAction.Down, KeyCode.B, 'b'));   // Ctrl defaults false

        Assert.Equal(0x42, state.KeyboardByte & 0x7F);
    }
}
```

- [ ] **Step 2: Edit `Apple2KeyMap.cs`** — add the `ctrl` parameter + the fold. Replace the `TryMap` method body:

```csharp
    /// <summary>Translate one host key to a ][+ 7-bit code. Returns false (code 0) for a key the ][+
    /// keyboard does not produce. <paramref name="ch"/> is the host-resolved typed character (null for
    /// non-printing keys); when a key has no dedicated arm but carried a printable Char, that Char's
    /// uppercase ASCII is used (so host-localised symbols still reach the guest). When <paramref
    /// name="ctrl"/> is set, a letter/printable code is ANDed with $1F to yield its control code (ADR
    /// 0014 Decision 3 / interactions §2.4) — Ctrl+B -> $02, Ctrl+C -> $03 — so Applesoft/DOS control
    /// chords (enter BASIC, break) reach the guest. The dedicated control keys (Enter/BS/Esc/Space) are
    /// not re-folded (they are already control codes).</summary>
    public static bool TryMap(KeyCode key, char? ch, out byte code, bool ctrl = false)
    {
        switch (key)
        {
            // Dedicated control keys (Char is typically null for these). Not ctrl-folded — already control.
            case KeyCode.Enter: code = 0x0D; return true;        // CR
            case KeyCode.Backspace: code = 0x08; return true;    // BS / left-arrow
            case KeyCode.Escape: code = 0x1B; return true;
            case KeyCode.Space: code = 0x20; return true;
        }

        // Letters: fold to uppercase ASCII regardless of the typed case; AND with $1F when ctrl is set.
        if (key is >= KeyCode.A and <= KeyCode.Z)
        {
            code = (byte)('A' + (key - KeyCode.A));   // $41..$5A
            if (ctrl) code = (byte)(code & 0x1F);     // Ctrl+letter -> $01..$1A
            return true;
        }

        // Digits: the top-row 0..9 -> ASCII '0'..'9' (ctrl on a digit is a no-op fold on real ][+).
        if (key is >= KeyCode.Digit0 and <= KeyCode.Digit9)
        {
            code = (byte)('0' + (key - KeyCode.Digit0));   // $30..$39
            return true;
        }

        // No dedicated key arm: if the host resolved a printable Char, latch its UPPERCASE ASCII (ctrl-folded).
        if (ch is char c && c is >= ' ' and <= '~')
        {
            code = (byte)char.ToUpperInvariant(c);
            if (ctrl) code = (byte)(code & 0x1F);
            return true;
        }

        code = 0;
        return false;   // unmapped -> no-op (the host's unknown-key contract)
    }
```

- [ ] **Step 3: Edit `Apple2Keyboard.cs`** — pass `e.Ctrl` into `TryMap`. Replace the `PostKey` body's `TryMap` line:

```csharp
    // ── IKeyboardSink: translate + latch on key-down; key-up is a no-op (the ][+ latch holds). ──
    public void PostKey(in KeyEvent e)
    {
        if (e.Action != KeyAction.Down)
            return; // the ][+ keyboard latch has no release event
        if (Apple2KeyMap.TryMap(e.Key, e.Char, out byte code, e.Ctrl))
            _state.LatchKey(code);  // sets the 7-bit code + raises the strobe (bit 7)
        // an unmapped key is silently ignored (the IKeyboardSink unknown-key contract)
    }
```

**Verification:** the new `Apple2CtrlKeyTests` pass; the shipped `Apple2KeyboardTests` (which call the 3-arg `TryMap`/`PostKey`) still pass (the new params default).

---

## Task 3: `FrameCodec.TryDecodeKey` reads the `ctrl` JSON field (D5, part 3)

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/FrameCodec.cs`
- Test: `tests/CpuEmulator.Tests/Surface/KeyEventCtrlDecodeTests.cs`

**Interfaces:**
- Shipped `TryDecodeKey` (verified `FrameCodec.cs` lines 105–129): parses `{action, code, char}` and builds `new KeyEvent(keyAction, key, typed)`. Add: read a boolean `ctrl` property (absent → false) and pass it as the 4th positional. The disk-command path (`TryDecodeDisk`, tried first in `Program.cs`) is unaffected — a key JSON with `ctrl` is still not a disk command.

- [ ] **Step 1: Write the failing test** `tests/CpuEmulator.Tests/Surface/KeyEventCtrlDecodeTests.cs`

```csharp
using CpuEmulator.Core;
using CpuEmulator.Surface.Web;
using Xunit;

namespace CpuEmulator.Tests.Surface;

/// <summary>D5 wire half: the inbound key JSON gains an optional <c>ctrl</c> boolean (the browser's
/// KeyboardEvent.ctrlKey). TryDecodeKey reads it into KeyEvent.Ctrl; absent -> false (every shipped
/// non-ctrl key event decodes unchanged).</summary>
public class KeyEventCtrlDecodeTests
{
    [Fact]
    public void Ctrl_true_decodes_to_KeyEvent_Ctrl()
    {
        Assert.True(FrameCodec.TryDecodeKey(
            "{\"action\":\"down\",\"code\":\"KeyB\",\"char\":\"b\",\"ctrl\":true}", out KeyEvent e));
        Assert.True(e.Ctrl);
        Assert.Equal(KeyCode.B, e.Key);
    }

    [Fact]
    public void Ctrl_false_decodes_to_Ctrl_false()
    {
        Assert.True(FrameCodec.TryDecodeKey(
            "{\"action\":\"down\",\"code\":\"KeyB\",\"char\":\"b\",\"ctrl\":false}", out KeyEvent e));
        Assert.False(e.Ctrl);
    }

    [Fact]
    public void Absent_ctrl_defaults_to_false()
    {
        Assert.True(FrameCodec.TryDecodeKey(
            "{\"action\":\"down\",\"code\":\"KeyA\",\"char\":\"a\"}", out KeyEvent e));
        Assert.False(e.Ctrl);   // the shipped non-ctrl shape is unchanged
    }
}
```

- [ ] **Step 2: Edit `FrameCodec.cs`** — read the `ctrl` field in `TryDecodeKey`. Replace the body's `charStr`/`KeyEvent` construction block:

```csharp
            string action = root.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
            string code = root.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "" : "";
            string charStr = root.TryGetProperty("char", out JsonElement ch) ? ch.GetString() ?? "" : "";
            // D5: the optional ctrl modifier (browser KeyboardEvent.ctrlKey). Absent -> false (the shipped
            // non-ctrl key shape is unchanged); a true value lets the Apple keyboard chip fold the letter
            // with $1F so Ctrl+B/Ctrl+C reach the guest as control codes.
            bool ctrl = root.TryGetProperty("ctrl", out JsonElement ck)
                        && ck.ValueKind == JsonValueKind.True;

            KeyAction keyAction = action == "up" ? KeyAction.Up : KeyAction.Down;
            KeyCode key = MapDomCode(code);
            char? typed = charStr.Length == 1 ? charStr[0] : null;
            e = new KeyEvent(keyAction, key, typed, ctrl);
            return true;
```

**Verification:** the new `KeyEventCtrlDecodeTests` pass; the shipped `FrameCodecTests` (which decode non-ctrl key JSON) still pass.

---

## Task 4: `index.html` — the control-strip markup + the `--drive-active` token + the mode label

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/wwwroot/index.html`

**Interfaces / grounding:**
- Shipped `index.html` (verified): a centered flex column; `#screen` canvas; `#status`; `#asset-banner` (hidden, already present, `#cc8`); `#enable-sound`; `#hint` (already names `<kbd>Ctrl+B</kbd> = BASIC. <kbd>Ctrl+Backspace</kbd> = RESET.`). The `<style>` block holds `--bg`-equivalent inline values; there are **no** CSS custom properties yet — `tokens.md` proposes naming them but the surface uses literal values. To introduce `--drive-active` as a *named* token (the one new value, citable by the Polisher) while keeping the rest as literals, declare the custom properties the design references on `:root` and use `--drive-active` in the control-strip rules; the existing canvas/status/kbd rules stay literal (no churn).
- The mode label (`copy.md` §2) sits **under the canvas** (`mockups/layout.md` §1). Insert a `#mode-label` between `#screen` and `#status`.
- The two drive panels (`mockups/layout.md` §2) sit **below** the status/sound/hint cluster per the layout's full-page mock (the control strip is its own region). Insert a `#control-strip` container with two `#drive-1`/`#drive-2` panels after `#hint`. The panels' inner DOM is built by `app.js` (`renderControlStrip`) so the markup here is the container scaffold + the hidden file inputs.
- The asset banner stays `#asset-banner` (the shipped `applyAssetBanner` writes it). Keep it; do not rename.

- [ ] **Step 1: Replace `index.html`** with the control-strip scaffold (keeps every shipped element + copy verbatim; adds the mode label, the `--drive-active` token, and the two drive-panel containers):

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>CpuEmulator — Apple ][+</title>
  <style>
    /* The one NEW visual value (tokens.md): the drive-activity amber. Everything else reuses the
       shipped Spectrum literals (#111/#ccc/#333/#222/#444/#888). --drive-active is the only addition. */
    :root { --drive-active: #d8a657; --drive-idle: #888; }
    body { margin: 0; background: #111; color: #ccc; font-family: system-ui, sans-serif;
           display: flex; flex-direction: column; align-items: center; gap: 12px; padding: 16px; }
    h1 { font-size: 14px; font-weight: 600; letter-spacing: .04em; margin: 4px 0; }
    /* Nearest-neighbour upscaling so the framebuffer stays crisp; aspect tracks the per-frame size. */
    canvas { image-rendering: pixelated; border: 1px solid #333; background: #000;
             width: min(90vw, 840px); height: auto; aspect-ratio: 280 / 192; }
    #status { font-size: 12px; color: #888; }
    #mode-label { font-size: 12px; color: #888; }
    #asset-banner { font-size: 12px; color: #cc8; max-width: 840px; text-align: center; }
    kbd { background: #222; border: 1px solid #444; border-radius: 3px; padding: 1px 5px; }
    /* --- Control strip (NEW; composed only from the shipped tokens + --drive-active) --- */
    #control-strip { display: flex; gap: 12px; flex-wrap: wrap; justify-content: center; }
    .drive-panel { border: 1px solid #333; border-radius: 3px; padding: 8px 10px;
                   min-width: 240px; font-size: 12px; color: #ccc; }
    .drive-panel legend, .drive-panel .legend { color: #888; font-size: 12px; padding: 0 4px; }
    .drive-top { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
    .drive-light { font-family: system-ui; color: var(--drive-idle); }
    .drive-light.active { color: var(--drive-active); }
    .drive-label { flex: 1; color: #ccc; }
    .drive-eject { margin-left: auto; }
    .drive-controls { display: flex; gap: 8px; align-items: center; }
    .drive-controls select, .drive-controls button { font-size: 12px; }
    .drive-error { color: #cc8; margin-top: 4px; min-height: 1em; }
    /* Reduced motion: the upload spinner is a static glyph (interactions §8). */
    @media (prefers-reduced-motion: reduce) { .drive-light.uploading { animation: none; } }
  </style>
</head>
<body>
  <h1>CpuEmulator — Apple ][+</h1>
  <canvas id="screen" width="280" height="192"></canvas>
  <div id="mode-label"></div>
  <div id="status">connecting…</div>
  <div id="asset-banner" hidden></div>
  <button id="enable-sound" type="button">click to enable sound</button>
  <div id="hint">Uppercase only. <kbd>Ctrl+B</kbd> = BASIC. <kbd>Ctrl+Backspace</kbd> = RESET.</div>

  <!-- The control strip (PR-T). app.js (renderControlStrip) fills each panel's top line (light +
       label + eject) and controls line (library select + Insert… + the hidden file input) from the
       host-pushed ST snapshot + the disk catalog. The containers are scaffold only — no fabricated
       indicator lives in the markup. -->
  <div id="control-strip">
    <fieldset class="drive-panel" id="drive-1">
      <legend>Drive 1</legend>
      <div class="drive-top">
        <span class="drive-light" id="drive-1-light" aria-label="drive 1 empty">○</span>
        <span class="drive-label" id="drive-1-label">empty</span>
        <button class="drive-eject" id="drive-1-eject" type="button" hidden>Eject</button>
      </div>
      <div class="drive-controls">
        <select id="drive-1-library" aria-label="Drive 1 library"></select>
        <button id="drive-1-insert" type="button">Insert…</button>
        <input id="drive-1-file" type="file" accept=".woz,.dsk,.po" hidden />
      </div>
      <div class="drive-error" id="drive-1-error" role="status" aria-live="polite"></div>
    </fieldset>
    <fieldset class="drive-panel" id="drive-2">
      <legend>Drive 2</legend>
      <div class="drive-top">
        <span class="drive-light" id="drive-2-light" aria-label="drive 2 empty">○</span>
        <span class="drive-label" id="drive-2-label">empty</span>
        <button class="drive-eject" id="drive-2-eject" type="button" hidden>Eject</button>
      </div>
      <div class="drive-controls">
        <select id="drive-2-library" aria-label="Drive 2 library"></select>
        <button id="drive-2-insert" type="button">Insert…</button>
        <input id="drive-2-file" type="file" accept=".woz,.dsk,.po" hidden />
      </div>
      <div class="drive-error" id="drive-2-error" role="status" aria-live="polite"></div>
    </fieldset>
  </div>

  <script src="app.js"></script>
</body>
</html>
```

**Verification:** the page parses; the control-strip containers, the `#mode-label`, the `--drive-active` token, and the `#asset-banner` are present (Task 6 asserts them via `WebApplicationFactory`).

---

## Task 5: `app.js` — the control-strip renderer + the panel wiring + the D5 client send

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/wwwroot/app.js`

**Interfaces / grounding (verified in the shipped `app.js`):**
- `handleStatusText` sets `window.machineStatus = st` (line 79), routes `st.upload` acks to `window.uploadState`/`window.uploadLastError` + calls `window.onUploadResult(drive, ok, message)` if present (lines 72–77), and `applyAssetBanner(st.asset, banner)` paints the banner/status (line 80). **Keep all of it.** T adds a `renderControlStrip()` call after `window.machineStatus = st` so the panels repaint on each ST snapshot, and defines `window.onUploadResult` to repaint + surface the inline error.
- `window.diskCatalog` is loaded by `loadCatalog()` on startup (lines 161–168); each entry is `{id, name, format, cpm, supported}` (from `Program.cs` `/disks`). T populates each `<select>` from it; `.woz` (`supported:false`) options render disabled-with-note.
- `window.insertFromLibrary(drive, id)` (lines 172–175), `window.ejectDrive(drive)` (lines 178–181), `window.uploadDisk(drive, file)` (lines 196–234), `window.uploadState`/`window.uploadLastError` (lines 185–186). **T calls these — it does not redefine them.**
- `sendKey(action, ev)` (lines 139–147) sends `{action, code, char}` + `preventDefault`s Space/Arrows. T adds `ctrl: ev.ctrlKey` to the payload and extends the `preventDefault` set with `Ctrl+B`/`Ctrl+C`. The shipped `Ctrl+Backspace` RESET preventDefault (lines 154–156) stays.
- The mode label: paint `#mode-label` from `st.mode` inside `handleStatusText` (after `window.machineStatus = st`).

- [ ] **Step 1: Edit `sendKey` (the D5 client send)** — add `ctrl` to the payload and guard `Ctrl+B`/`Ctrl+C`. Replace the shipped `sendKey` (lines 139–147):

```javascript
  function sendKey(action, ev) {
    if (ws.readyState !== WebSocket.OPEN) return;
    // A single printable character (length-1 key) is the typed char; otherwise empty.
    const ch = ev.key && ev.key.length === 1 ? ev.key : "";
    // D5: forward the Ctrl modifier so the Apple keyboard chip can fold Ctrl+letter into a control code
    // (Ctrl+B = enter BASIC, Ctrl+C = break). The server reads the `ctrl` field; absent on older shapes.
    ws.send(JSON.stringify({ action: action, code: ev.code, char: ch, ctrl: ev.ctrlKey }));
    // Keep the browser from scrolling on Space/Arrows, and from stealing Ctrl+B / Ctrl+C, while focused.
    if (["Space", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(ev.code))
      ev.preventDefault();
    if (ev.ctrlKey && (ev.code === "KeyB" || ev.code === "KeyC"))
      ev.preventDefault();
  }
```

- [ ] **Step 2: Paint the mode label + repaint the strip on each ST snapshot** — inside `handleStatusText`, just after the shipped `window.machineStatus = st;` line (line 79), add:

```javascript
      window.machineStatus = st;                 // row T binds drive panels to this
      const modeLabel = document.getElementById("mode-label");
      if (modeLabel) modeLabel.textContent = st.mode || "";
      renderControlStrip();                       // repaint lights/labels/eject from the real snapshot
```

- [ ] **Step 3: Define `window.onUploadResult`** (the S ack hook) — repaint the panel + surface the inline error. Add near the upload-state block (after line 186, `window.uploadLastError = ...`):

```javascript
  // The S ack hook: handleStatusText calls this when an upload result arrives. Repaint the panel; on a
  // server-side error, show the calm inline message (it auto-clears on the next ST snapshot or after ~6 s).
  window.onUploadResult = function (drive, ok, message) {
    renderControlStrip();
    if (!ok) showDriveError(drive, message || "That image looks corrupt");
  };

  // A per-drive inline error (copy.md §7). Auto-clears after ~6 s; the next successful action also clears it.
  const driveErrorTimers = { 1: null, 2: null };
  function showDriveError(drive, msg) {
    const el = document.getElementById("drive-" + drive + "-error");
    if (!el) return;
    el.textContent = msg;
    if (driveErrorTimers[drive]) clearTimeout(driveErrorTimers[drive]);
    driveErrorTimers[drive] = setTimeout(() => { el.textContent = ""; }, 6000);
  }
```

- [ ] **Step 4: The control-strip renderer** — append the renderer + the one-time wiring at the end of the IIFE (before the closing `})();`). This is the bulk of T. It paints each panel from `window.machineStatus` (the dumb-reflector source), `window.uploadState`, and `window.diskCatalog`; it wires the library select, the `Insert…` picker, and the eject button **once**:

```javascript
  // --- Control strip (PR-T, design T-E/T-G/T-H) ---
  // Repaint both drive panels from the REAL host-pushed snapshot (window.machineStatus.drives[i] =
  // {motor,label}) + the per-drive upload state. Nothing here is fabricated: an absent snapshot leaves
  // the boot defaults (○ / empty). Called on each ST frame and after an upload result.
  const GLYPH = { idle: "○", active: "●", uploading: "◐" };

  function renderControlStrip() {
    for (let drive = 1; drive <= 2; drive++) renderDrivePanel(drive);
  }

  function renderDrivePanel(drive) {
    const st = window.machineStatus;
    const d = st && st.drives && st.drives[drive - 1];   // {motor, label} or undefined
    const uploading = window.uploadState[drive] === "uploading";
    const label = d ? d.label : "—";
    const hasDisk = !!d && label && label !== "—" && label !== "empty";

    const lightEl = document.getElementById("drive-" + drive + "-light");
    const labelEl = document.getElementById("drive-" + drive + "-label");
    const ejectEl = document.getElementById("drive-" + drive + "-eject");

    // The light: amber only when the REAL motor is on; the spinner during upload; else the idle outline.
    let glyph, cls, aria;
    if (uploading) { glyph = GLYPH.uploading; cls = "drive-light uploading"; aria = "drive " + drive + " uploading"; }
    else if (d && d.motor) { glyph = GLYPH.active; cls = "drive-light active"; aria = "drive " + drive + " active"; }
    else if (hasDisk) { glyph = GLYPH.idle; cls = "drive-light"; aria = "drive " + drive + " idle"; }
    else { glyph = GLYPH.idle; cls = "drive-light"; aria = "drive " + drive + " empty"; }
    if (lightEl) { lightEl.textContent = glyph; lightEl.className = cls; lightEl.setAttribute("aria-label", aria); }

    // The label: the uploading text, the image name, or "empty".
    if (labelEl) {
      if (uploading) labelEl.textContent = "Uploading…";
      else if (hasDisk) labelEl.textContent = label;
      else labelEl.textContent = "empty";
    }

    // Eject is shown only when a disk is inserted and not uploading.
    if (ejectEl) ejectEl.hidden = !(hasDisk && !uploading);

    // The library select + the Insert… button are disabled during an upload (controls locked, interactions §4.1).
    const selEl = document.getElementById("drive-" + drive + "-library");
    const insEl = document.getElementById("drive-" + drive + "-insert");
    if (selEl) selEl.disabled = uploading || selEl.dataset.empty === "1";
    if (insEl) insEl.disabled = uploading;
  }

  // Populate a drive's [ Library ▾] from window.diskCatalog (read-only — the server lists the real cache).
  // The placeholder option is first; an empty catalog disables the select with the named-script hint;
  // .woz items (supported:false) render disabled-with-note (no WozFluxImage yet — backlog row W).
  function populateLibrary(drive) {
    const sel = document.getElementById("drive-" + drive + "-library");
    if (!sel) return;
    const cat = window.diskCatalog || [];
    sel.innerHTML = "";
    if (cat.length === 0) {
      const opt = document.createElement("option");
      opt.textContent = "No cached disks — see tools/get-*";
      opt.value = "";
      sel.appendChild(opt);
      sel.disabled = true;
      sel.dataset.empty = "1";
      return;
    }
    sel.dataset.empty = "0";
    const placeholder = document.createElement("option");
    placeholder.textContent = "Insert from library…";
    placeholder.value = "";
    placeholder.disabled = true;
    placeholder.selected = true;
    sel.appendChild(placeholder);
    cat.forEach((e) => {
      const opt = document.createElement("option");
      const fmt = e.format ? " (." + String(e.format).toLowerCase() + ")" : "";
      if (e.supported === false) {
        opt.textContent = e.name + fmt + " — not yet supported";
        opt.disabled = true;
      } else {
        opt.textContent = e.name + fmt;
      }
      opt.value = e.id;
      sel.appendChild(opt);
    });
  }

  // Wire each panel's controls ONCE (the renderer only repaints; the listeners are attached here).
  function wireDrivePanels() {
    for (let drive = 1; drive <= 2; drive++) {
      const sel = document.getElementById("drive-" + drive + "-library");
      const ins = document.getElementById("drive-" + drive + "-insert");
      const file = document.getElementById("drive-" + drive + "-file");
      const eject = document.getElementById("drive-" + drive + "-eject");

      // Library select: an explicit choice inserts that catalog id into this drive (text WS); reset to
      // the placeholder so the same item can be re-selected.
      if (sel) sel.addEventListener("change", function () {
        const id = sel.value;
        if (id) { window.insertFromLibrary(drive, id); sel.selectedIndex = 0; }
      });

      // Insert…: open the OS file picker (a real button .click()s the hidden input — no keyboard trap).
      if (ins && file) ins.addEventListener("click", function () { file.value = ""; file.click(); });
      if (file) file.addEventListener("change", function () {
        const f = file.files && file.files[0];
        if (!f) return;
        const err = window.uploadDisk(drive, f);   // "" on send; a client-side error string otherwise
        renderDrivePanel(drive);
        if (err) showDriveError(drive, err);
      });

      // Eject: remove this drive's image (text WS); the next ST snapshot repaints to empty.
      if (eject) eject.addEventListener("click", function () { window.ejectDrive(drive); });
    }
  }

  // Initial render + wiring. The catalog arrives async (loadCatalog's fetch); re-populate when it lands by
  // polling window.diskCatalog once it differs from the initial empty array (cheap, one-shot).
  wireDrivePanels();
  populateLibrary(1); populateLibrary(2);
  renderControlStrip();
  // loadCatalog() resolves asynchronously; re-populate the selects once the catalog is in.
  (function awaitCatalog() {
    let tries = 0;
    const t = setInterval(function () {
      if ((window.diskCatalog && window.diskCatalog.length) || ++tries > 40) {
        clearInterval(t);
        populateLibrary(1); populateLibrary(2); renderControlStrip();
      }
    }, 100);
  })();
```

**Note on the catalog-await:** the shipped `loadCatalog()` sets `window.diskCatalog` after a `fetch`; there is no callback hook, so the one-shot poll (≤4 s, then it stops) re-populates the selects when the catalog arrives. An empty catalog (the common no-asset case) leaves the disabled "No cached disks" option — correct.

**Verification:** the served `app.js` carries `renderControlStrip`, `populateLibrary`, `wireDrivePanels`, the `ctrl:` send, and the `Ctrl+B`/`Ctrl+C` preventDefault (Task 6 asserts them via `WebApplicationFactory`). The in-browser visual confirmation (panels render, light animates) is **owner UAT** (see Gate Strategy).

---

## Task 6: The served-asset content gate (the hybrid gate, part a)

**Files:**
- Test: `tests/CpuEmulator.Tests/Surface/ControlStripAssetTests.cs`

**Interfaces:**
- `WebApplicationFactory<WebProgram>` (the shipped pattern in `WebServerSmokeTests`/`DiskUploadEndpointTests`): `client.GetStringAsync("/app.js")` + `GetStringAsync("/index.html")` (or `/`) return the served static assets. Assert the control-strip DOM + the `--drive-active` token + the control wiring + the D5 `ctrl` send are present in the served bytes. This proves the client carries the row's behavior without a browser (the visual rendering is owner UAT).

- [ ] **Step 1: Write the gate** `tests/CpuEmulator.Tests/Surface/ControlStripAssetTests.cs`

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>PR-T served-asset gate (hybrid strategy part a): the control-strip DOM, the one new
/// --drive-active token, the control wiring, and the D5 ctrl send are present in the SERVED /app.js +
/// /index.html. This is the automated half — the in-browser visual confirmation (panel rendering, the
/// amber light) is owner UAT. No headless browser, no new JS toolchain.</summary>
[Trait("Category", "UAT")]
public class ControlStripAssetTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public ControlStripAssetTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    [Fact]
    public async Task Index_carries_the_drive_panels_mode_label_token_and_banner()
    {
        using var client = _factory.CreateClient();
        string html = await client.GetStringAsync("/");

        Assert.Contains("id=\"control-strip\"", html);
        Assert.Contains("id=\"drive-1\"", html);
        Assert.Contains("id=\"drive-2\"", html);
        Assert.Contains("id=\"mode-label\"", html);
        Assert.Contains("id=\"asset-banner\"", html);
        Assert.Contains("--drive-active: #d8a657", html);   // the ONE new token, exact value
        Assert.Contains("accept=\".woz,.dsk,.po\"", html);  // the upload picker allow-list
        Assert.Contains("Drive 1", html);
        Assert.Contains("Drive 2", html);
        // The hint line still names the real chords (copy.md §4).
        Assert.Contains("<kbd>Ctrl+B</kbd>", html);
        Assert.Contains("<kbd>Ctrl+Backspace</kbd>", html);
    }

    [Fact]
    public async Task AppJs_carries_the_control_wiring_and_the_ctrl_send()
    {
        using var client = _factory.CreateClient();
        string js = await client.GetStringAsync("/app.js");

        // The renderer + populate + wiring (the panel behavior binds to the shipped seams).
        Assert.Contains("function renderControlStrip", js);
        Assert.Contains("function populateLibrary", js);
        Assert.Contains("function wireDrivePanels", js);
        // The bindings to the shipped P/R/S seams.
        Assert.Contains("window.insertFromLibrary", js);
        Assert.Contains("window.ejectDrive", js);
        Assert.Contains("window.uploadDisk", js);
        Assert.Contains("window.machineStatus", js);
        // D5 client half: the ctrl field is sent and Ctrl+B/Ctrl+C are guarded.
        Assert.Contains("ctrl: ev.ctrlKey", js);
        Assert.Contains("ev.code === \"KeyB\"", js);
        Assert.Contains("ev.code === \"KeyC\"", js);
    }
}
```

**Verification:** both facts pass against the served assets (the in-memory host serves `wwwroot/` unchanged).

---

## Gate strategy (hybrid — no new JS toolchain)

The row's gate is three legs, with an explicit automated-vs-owner-UAT split:

**(a) C# served-asset content assertions** (`ControlStripAssetTests`, Task 6) — via `WebApplicationFactory`, the panel DOM / the `--drive-active` token / the control wiring / the D5 `ctrl` send are present in the served `/app.js` + `/index.html`. **Automated.**

**(b) The wire/seam gates** — already shipped and green (R's `DiskLibraryEndpointTests`, S's `DiskUploadEndpointTests`, P's `StatusPushOnChangeTests`/`Apple2SurfaceStatusTests`): the controls emit the right `disk-insert`/`DK`/eject messages; the banner shows iff the asset is absent; the `ST` round-trip drives the indicators. T reuses these — the panels call the *already-gated* `window.insertFromLibrary`/`uploadDisk`/`ejectDrive`, so the transport is proven; T's new surface is DOM, covered by (a). **Automated (pre-existing + (a)).**

**(c) The D5 interpreter gate** (`Apple2CtrlKeyTests`, Task 2 + `KeyEventCtrlDecodeTests`, Task 3) — a `Ctrl+B` key event latches `$02` (not `$42`) at `$C000` over the live keyboard latch; the wire decode reads the `ctrl` field. **Automated, un-fakeable, interpreter-tier.**

**Owner UAT (explicitly NOT automated):** the in-browser *visual* confirmation — the panels actually render, the amber light turns on during a real disk access and lingers ~1 s (the 556 off-delay), the upload spinner shows, the library dropdown populates from a seeded cache, a real `Ctrl+B` in the browser drops into BASIC. These need a running browser + cached assets and are the owner's manual pass (the project has no browser-MCP UAT). The plan's automated gates prove the *plumbing*; the owner confirms the *pixels*.

---

## Spec coverage self-review

- **Two bordered drive panels** (Task 4 markup + Task 5 render): library select + `Insert…` + eject + label + light, per `mockups/layout.md` §2 and `interactions.md` §4.1. ✅
- **Library select from `GET /disks`** (Task 5 `populateLibrary`): placeholder first; empty catalog disabled with `No cached disks — see tools/get-*`; `.woz` disabled-with-note. ✅ (`copy.md` §6.3, `interactions.md` §4.3)
- **Upload picker → `DK`** (Task 5 file-input wiring → `window.uploadDisk`): UPLOADING → INSERTED/error via the S ack (`window.onUploadResult`). ✅ (`interactions.md` §4.4)
- **Eject** (Task 5): shown only when inserted; calls `window.ejectDrive`. ✅ (`interactions.md` §4.5)
- **Current-image label, both drives** (Task 5): from `st.drives[i].label`. ✅
- **Real-motor amber light** (Task 5): `st.drives[i].motor` only (the REAL `$C0E8/$C0E9` line + 556 off-delay; single shared motor line, per-drive labels). Never faked on insert. ✅ (`interactions.md` §4.2)
- **Calm named-script asset banner** (shipped `applyAssetBanner` kept; the demo-fallback copy is verbatim, never red, omits RESET/BASIC implicitly via the banner copy). ✅ (`copy.md` §5, `mockups/layout.md` §3)
- **Read-only video-mode label** (Task 5 `#mode-label` ← `st.mode`). ✅ (`copy.md` §2)
- **Keyboard T-F incl. D5** (Tasks 1–3, 5): chip+wire `ctrl` fold + interpreter gate + client `preventDefault` guards + the hint-line confirmation (kept verbatim). ✅
- **One new token `--drive-active`** (Task 4): amber `#d8a657`, the only new value. ✅ (`tokens.md`)
- **Accessibility** (Task 4/5): `aria-label` on the light (active/idle/empty/uploading); the file `<input>` reachable via a real button; `prefers-reduced-motion` on the spinner; the inline error is `aria-live="polite"`. ✅ (`interactions.md` §8)

## Placeholder / consistency self-review

- No `TBD`, no "implement later", no "similar to Task N" — every step has literal code.
- All shipped signatures verified at `f4755e5`: `KeyEvent` (3-arg → 4-arg defaulted), `Apple2KeyMap.TryMap`, `Apple2Keyboard.PostKey`, `Apple2VideoState.KeyboardByte`/`LatchKey`, `FrameCodec.TryDecodeKey`, the `app.js` seams (`machineStatus`/`diskCatalog`/`insertFromLibrary`/`ejectDrive`/`uploadDisk`/`uploadState`/`onUploadResult` hook), the `index.html` element set, `WebApplicationFactory<WebProgram>`.
- The D5 gate's `$02`-not-`$42` assertion reads the 7-bit code via `KeyboardByte & 0x7F` (the latch masks to 7 bits + raises bit 7; `$C000` returns `$82`).
- No `KeyCode` enum addition (the design §2.5 confirms Ctrl rides the `Ctrl` field, not a new physical key code).

---

## Out of scope (carried forward, not this row)

- **`WozFluxImage` (backlog row W):** `.woz` library items render disabled-with-note; `.woz` uploads honestly reject (the shipped server returns the not-yet-supported reject). T does not parse `.woz`.
- **The CP/M explainer note** (`copy.md` §8, the optional first-Videx-activation dismissible note) — optional/dismissible; not required for T's deliverable (the 80-col display + the `· CP/M` mode label already signal it). Noted as a tasteful additive follow-on, not gated here.
- **Drag-drop onto the drive panel** (`interactions.md` §4.4) — the `Insert…` button + OS picker is v1; drag-drop is a noted follow-on.
- **A per-drive `[ Boot ]` button** — RESET-with-disk is the idiom (locked decision); no boot button.
- **The CP/M Videx-assets-absent inline refusal** (`copy.md` §7 CP/M rows) — depends on the SoftCard/Videx asset-probe at insert; the shipped server inserts library items format-agnostically. If the owner wants the CP/M-specific refusal copy, it is an additive arm on the library insert (a small follow-on), not part of T's drive-panel deliverable.
