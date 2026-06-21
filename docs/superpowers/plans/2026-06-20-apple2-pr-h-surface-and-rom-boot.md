# Apple ][+ PR-H — `Apple2Surface` + `get-apple2-roms.{sh,ps1}` + the ROM-boot gate (boots to `]`)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Apple ][+ arc's **first UI-touching PR** — wire the assembled `Apple2Board` machine through `MachineHost` into the web surface (mirroring `SpectrumSurface` exactly: display + keyboard + speaker), boot from the fetched system ROM when cached else fall back to the existing SP0 demo, mount the slot-6 `$C600` Disk II boot ROM, ship the **fetch-on-demand asset script** `get-apple2-roms.{sh,ps1}` (never vendored, cached outside source control, both `.sh` + `.ps1` — the `get-spectrum-rom` pattern verbatim), and add the **ROM-boot gate** that asserts the Applesoft `]` prompt (the Autostart copyright/`]` screen) renders on **both execution tiers** when the ROM is present and **skips-with-note when absent** (the `SpectrumBootTests` discipline exactly). The **char-gen ROM** ships a built-in fallback glyph set (already in `Apple2Font.Fallback`, PR-C) so text render works without the real char ROM; the real char-gen fetch is **documented in the script** with a length sanity-check, surfaced to the user when absent. The deliverable is a clear, browser-observable "boots to `]`" state so the Coordinator's Tester (browser UAT) + Polisher (consistency-vs-handoff) milestone pass is easy.

**Architecture:** Three composable layers, all mirroring the shipped Spectrum surface:

1. **`Apple2Rom` (the asset loader, `CpuEmulator.Machines`):** the `SpectrumRom` twin — `TryGetPath()` for the 12 KiB system ROM at `<cache>/apple2/apple2plus.rom`, `Load(path)` with the exact-length sanity check, plus `TryGetDiskRomPath()` for the 256-byte slot-6 boot ROM at `<cache>/apple2/disk2.rom` and `TryGetCharRomPath()` for the optional 2 KiB char-gen ROM at `<cache>/apple2/char.rom`. A missing system ROM is the fallback trigger; a missing char ROM is **non-fatal** (the `Apple2Font.Fallback` glyph set drives render, surfaced as `fallback font` in the status line).
2. **`Apple2Surface` (the surface, `CpuEmulator.Surface.Web`):** the `SpectrumSurface` twin — constructs the shared `Apple2VideoState`, the `Apple2Video`/`Apple2Keyboard`/`Apple2Speaker` triad over it, the `Apple2LanguageCard`, the `Apple2DiskII` (over a drive-1 `IFluxImage`, or none), the `Apple2Iou` holding the LC + Disk II, builds the board via `Apple2Board.SpecWithDiskII` + the new `$C600` boot-ROM slot, resets it, and wires a `MachineHost` whose display = the `Apple2Video`, keyboard = the `Apple2Keyboard`, audio = the `Apple2Speaker`.
3. **The board `$C600` boot-ROM slot + `Program.cs` wiring:** `Apple2Board.SpecWithSystem` adds a `RegionKind.Rom` region at `$C600` (256 bytes, the fetched P5/P6 boot ROM, signature-carrying) so the Autostart slot-scan finds slot 6 and `JMP ($C600)` cold-boots the disk; `DemoSession` in `Program.cs` boots the Apple when `Apple2Rom.TryGetPath()` is non-null, else the existing demo. The client `index.html`/`app.js` get the calm asset banner + the uppercase/RESET hint (the design handoff `copy.md`).

**Tech Stack:** C# / .NET 10, the shipped `MachineHost` 6-arg ctor (display + keyboard + frame sink + audio + audio sink), `FrameCodec` (FB/AU frames, JSON keys), the ASP.NET Core WebSocket surface (`Program.cs` `DemoSession`/`SurfacePump`), `wwwroot/index.html` + `app.js`, the `get-spectrum-rom.{sh,ps1}` script pattern, xUnit + the `[*RomTheory]` skip-with-note attribute pattern. **Depends on PR-C, D, E, F ✅ and G ✅** (video/keyboard/speaker/LC/Disk II + the `.dsk` adapter all merged). Namespaces: `CpuEmulator.Machines` (`Apple2Rom`, the board overload), `CpuEmulator.Surface.Web` (`Apple2Surface`, `Program.cs`), `tools/` (the scripts), `CpuEmulator.Tests` (the gate).

---

## Recon facts this plan is built on (verified against `main` @ `c2ae005`)

1. **`SpectrumSurface.Create(rom, frameSink, audioSink)`** (`src/CpuEmulator.Surface.Web/SpectrumSurface.cs`) is the exact template: build the machine, `machine.Reset()`, `new MachineHost(machine, ula, ula, frameSink, ula, audioSink)`. `Apple2Surface` does the same with the Apple triad (display = `Apple2Video`, keyboard = `Apple2Keyboard`, audio = `Apple2Speaker` — three *different* objects over one shared `Apple2VideoState`, not one object like the ULA).
2. **`MachineHost`'s 6-arg ctor** (`src/CpuEmulator.Surface.Web/MachineHost.cs`) is `(Machine, IDisplayDevice display, IKeyboardSink keyboard, Action<byte[]> frameSink, IAudioSink? audio, Action<byte[]>? audioSink)`. It sizes `_rgba` from `display.Width * display.Height` (280×192 for the Apple) and subscribes `FrameReady`/`AudioReady`. **No `MachineHost` change is needed** — it is display-device-agnostic (the `DisplayMultiplexer` re-size is PR-M, not H).
3. **`SpectrumRom`** (`src/CpuEmulator.Machines/SpectrumRom.cs`) is the asset-loader template: `TryGetPath()` reads `CPUEMULATOR_TESTVECTORS` (default `~/.cache/cpuemulator/vectors`) + a fixed subpath, returns null if absent; `Load(path)` validates the exact length (throws a clear, actionable `FileNotFoundException`/`InvalidDataException`). `Apple2Rom` mirrors it for three ROMs (system / disk2 boot / char-gen).
4. **`tools/get-spectrum-rom.{sh,ps1}`** are the script templates: `set -eu` / `$ErrorActionPreference = "Stop"`, cache root `${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}`, `mkdir -p`/`New-Item`, skip-if-present, fetch with a fallback mirror, **byte-length sanity check**, fail loud. `get-apple2-roms.{sh,ps1}` fetches the three Apple ROMs (system 12 KiB, disk2 256 B, char-gen 2 KiB) the same way. **Apple ROMs are Apple copyright** — the script documents that they are user-supplied / fetched on demand and **never vendored** (ADR 0014 Decision 7), and the char-gen fetch is documented but **non-fatal** (the fallback font covers it).
5. **`Program.cs` `DemoSession.RunAsync`** (`src/CpuEmulator.Surface.Web/Program.cs`) already does the boot-if-cached-else-fallback: `string? romPath = SpectrumRom.TryGetPath(); if (romPath is not null) { ...SpectrumSurface... } else { ...DemoBoardSurface... }`. H inserts an **Apple-first** branch: try `Apple2Rom.TryGetPath()` → `Apple2Surface`; else the existing Spectrum/demo branch (or, per the owner's "one machine per surface" design D15, the Apple replaces the Spectrum as the primary — see the implementer note in Task 4). The `SurfacePump` (slice + period) is reused; the Apple runs ~1.0205 MHz → a ~17,030-cycle slice every ~16 ms (60 Hz), matching `Apple2Video.CyclesPerFrame`.
6. **`Apple2Board`** (`src/CpuEmulator.Machines/Apple2Board.cs`) ships `Spec`, `SpecWithLanguageCard`, `SpecWithDiskII`. None map the `$C600` boot ROM (PR-F's plan explicitly deferred it to PR-H: "The $C600 boot ROM slot is added in PR-H"). H adds a **`RegionKind.Rom` region at `$C600` (256 bytes)** inside the existing `$C000-$CFFF` Mmio band — but the validator requires slots in Mmio and ROM regions elsewhere; the `$C600` boot ROM is a **read-only ROM window inside the I/O band**, so it is mapped as a `PeripheralSlot` over a tiny read-only ROM peripheral (a `RomPeripheral`), OR — simpler — the board carves `$C600-$C6FF` as its own `RegionKind.Rom` region and shrinks the Mmio hole. **See Task 3's decision** (carve the ROM region; the validator allows adjacent Ram/Rom/Mmio regions as long as they tile the space without overlap).
7. **The `Apple2Video` chip is 280×192** (`Width280`/`Height192`) and renders text from live RAM via `Apple2HiResAddress.TextRowBase` + the char ROM (real or `Apple2Font.Fallback`). The Applesoft boot paints the **`]` prompt** (Applesoft uses `]`, ASCII `$5D`; the ][+ stores it as `$DD` — normal video, high bit set) at the bottom-left of the 40×24 text screen (row 23, `TextRowBase(23) = $7D0`), preceded by the Autostart `]`/copyright screen. The ROM-boot gate asserts the text screen renders **non-blank ink** (the structural `SpectrumBootTests` discipline — a mostly-`MonoOff` screen with a meaningful count of `MonoOn` ink pixels where the prompt + heading sit), on **both tiers**.
8. **`Apple2Video(ram, state, charRom)`** accepts an optional `byte[]? charRom` (256×8 = 2048 bytes; null → `Apple2Font.Fallback`). H injects the **real char ROM** when `Apple2Rom.TryGetCharRomPath()` is non-null, else the fallback (and the status line says `fallback font`). The gate runs with whichever is present (the fallback is always available, so the gate is **ROM-system-gated, not char-ROM-gated**).
9. **The `SpectrumRomTheoryAttribute`** (`tests/CpuEmulator.Tests/Spectrum/SpectrumRomVectors.cs`) is the skip-with-note template: a `TheoryAttribute` subclass that sets `Skip` when the ROM path is null. `Apple2RomFactAttribute`/`Apple2RomTheoryAttribute` mirror it for the system ROM.
10. **The `SpectrumBootTests` gate** (`tests/CpuEmulator.Tests/Spectrum/SpectrumBootTests.cs`) is the exact discipline: `[SpectrumRomTheory] [InlineData(Interpreter)] [InlineData(Jit)]`, run ~2 frames, `RenderInto`, a **structural** assertion (mostly-paper + some-ink) plus a **committed-hash placeholder** that stays inert until captured on first green run (`if (ExpectedBootHash != "PLACEHOLDER_...") Assert.Equal(...)`). `Apple2BootTests` mirrors it. **`[Trait("Category", "UAT")]`** is kept.
11. **`MachineHost.RunHeadless(total, slice)`** drives the surface headless for tests (no wall clock). The Apple gate can build the surface and `RunHeadless` enough cycles for the ROM to paint the prompt, then `RenderInto` — but the simplest, most direct gate (mirroring `SpectrumBootTests`) builds the machine via `Apple2Board.SpecWithSystem`, `machine.Reset()`, `machine.Run(bootCycles)`, then `Apple2Video.RenderInto` — no surface needed for the **render** gate (the surface wiring is gated separately in Task 4 by a `WebApplicationFactory`-free construction smoke test).

---

## Conventions to follow

- **Mirror `SpectrumSurface` / `SpectrumRom` / `get-spectrum-rom` / `SpectrumBootTests` exactly** — this PR is the Apple analogue of the shipped Spectrum surface set. Do not invent new patterns.
- **Assets fetch-on-demand, never vendored** (ADR 0014 Decision 7) — the script caches outside source control; tests skip-with-note when absent; the surface falls back calmly with a named-script banner (handoff `copy.md` §5).
- **Both `.sh` and `.ps1`** ship (ADR 0016 Decision 4 / the owner decision).
- **The char-gen ROM is non-fatal** — the `Apple2Font.Fallback` set drives render; the real fetch is documented + length-sanity-checked but optional.
- **The ROM-boot gate runs on BOTH tiers and skips-with-note when the system ROM is absent** — the Spectrum's exact discipline. The structural assertion + the committed-hash placeholder.
- **Design the gate as a clear "boots to `]`" browser-observable state** (the Coordinator runs a Tester + Polisher pass after H ships).
- **TDD per task**, literal code, commit per task. Warning-clean. **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test ...`.

---

## File Structure

### `CpuEmulator.Machines`
- **Create** `src/CpuEmulator.Machines/Apple2Rom.cs` — the asset loader (system / disk2-boot / char-gen ROM cache lookup + length-validated load; the `SpectrumRom` twin).
- **Modify** `src/CpuEmulator.Machines/Apple2Board.cs` — add `SpecWithSystem(systemRom, iou, disk2, diskBootRom)` mapping the `$C600` slot-6 boot ROM, and a `RomPeripheral` (or a carved ROM region) for it.

### `CpuEmulator.Surface.Web`
- **Create** `src/CpuEmulator.Surface.Web/Apple2Surface.cs` — the `SpectrumSurface` twin: build the Apple triad + LC + Disk II + IOU + board, reset, wire `MachineHost`.
- **Modify** `src/CpuEmulator.Surface.Web/Program.cs` — boot the Apple when its system ROM is cached, else the existing fallback; report the asset state for the banner.
- **Modify** `src/CpuEmulator.Surface.Web/wwwroot/index.html` — Apple title + the asset banner element + the uppercase/RESET hint.
- **Modify** `src/CpuEmulator.Surface.Web/wwwroot/app.js` — render the asset banner from a status message; the `Ctrl+Backspace` RESET binding `preventDefault` (the keyboard extension is the minimum needed for the boot-to-`]` flow).

### `tools/`
- **Create** `tools/get-apple2-roms.sh` — fetch the three Apple ROMs (system / disk2-boot / char-gen) with length sanity checks; never vendored.
- **Create** `tools/get-apple2-roms.ps1` — the PowerShell sibling.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2RomVectors.cs` — `TryGetRomPath()` + `Apple2RomFactAttribute`/`Apple2RomTheoryAttribute` (the skip-with-note pattern).
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2BootTests.cs` — the ROM-boot gate (both tiers, structural `]`-screen assertion + committed-hash placeholder, skip-with-note absent) + a surface-construction smoke test.

### Docs
- **Modify** `docs/BUILDER_QUEUE.md` — set row **H** to ✅; update the banner; add the Recently-shipped entry.

---

## Task 1: `Apple2Rom` — the three-ROM asset loader (the `SpectrumRom` twin)

**Files:**
- Create: `src/CpuEmulator.Machines/Apple2Rom.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2RomVectors.cs`

- [ ] **Step 1: Write the failing loader test (length validation + the cache-path resolution)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2RomVectors.cs`:

```csharp
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

internal static class Apple2RomVectors
{
    public static string? TryGetRomPath()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "apple2", "apple2plus.rom");
        return File.Exists(path) ? path : null;
    }
}

/// <summary>Skip-with-note when the Apple ][+ system ROM is absent (the SpectrumRomFact pattern) so
/// ROM-free CI stays green.</summary>
public sealed class Apple2RomFactAttribute : FactAttribute
{
    public Apple2RomFactAttribute()
    {
        if (Apple2RomVectors.TryGetRomPath() is null)
            Skip = "Apple ][+ system ROM not found — run tools/get-apple2-roms.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}

public sealed class Apple2RomTheoryAttribute : TheoryAttribute
{
    public Apple2RomTheoryAttribute()
    {
        if (Apple2RomVectors.TryGetRomPath() is null)
            Skip = "Apple ][+ system ROM not found — run tools/get-apple2-roms.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}

public class Apple2RomLoaderTests
{
    [Fact]
    public void Load_rejects_a_wrong_length_system_rom()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"apple2-bad-{Guid.NewGuid():N}.rom");
        File.WriteAllBytes(tmp, new byte[0x100]);   // not 12 KiB
        try
        {
            Assert.Throws<InvalidDataException>(() => Apple2Rom.Load(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Load_accepts_an_exact_12KiB_system_rom()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"apple2-ok-{Guid.NewGuid():N}.rom");
        File.WriteAllBytes(tmp, new byte[Apple2Rom.SystemRomLength]);
        try
        {
            byte[] rom = Apple2Rom.Load(tmp);
            Assert.Equal(Apple2Rom.SystemRomLength, rom.Length);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void A_missing_char_rom_is_non_fatal_TryGetCharRomPath_is_null_when_absent()
    {
        // With no char.rom in a throwaway empty cache root, the optional char-ROM path is simply null
        // (the surface uses Apple2Font.Fallback) — NOT an exception.
        string emptyRoot = Path.Combine(Path.GetTempPath(), $"empty-cache-{Guid.NewGuid():N}");
        string? prev = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS");
        Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", emptyRoot);
        try
        {
            Assert.Null(Apple2Rom.TryGetCharRomPath());
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", prev); }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2RomLoaderTests"`
Expected: FAIL — `Apple2Rom` does not exist.

- [ ] **Step 3: Create `Apple2Rom`**

Create `src/CpuEmulator.Machines/Apple2Rom.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Loads the Apple ][+ ROM images from the asset cache (NOT vendored — Apple's copyright;
/// fetched on demand by tools/get-apple2-roms.{sh,ps1}, exactly like the Spectrum/ZEX/Klaus assets, ADR
/// 0014 Decision 7). The cache root is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors);
/// the ROMs live under &lt;root&gt;/apple2/. Three images: the 12 KiB SYSTEM ROM (Applesoft + Monitor,
/// $D000-$FFFF) is REQUIRED to boot a real Apple; the 256 B slot-6 DISK II BOOT ROM ($C600) is needed to
/// boot a disk; the 2 KiB CHAR-GEN ROM is OPTIONAL (Apple2Font.Fallback covers it). A missing system ROM
/// triggers the demo fallback; a missing char ROM is non-fatal.</summary>
public static class Apple2Rom
{
    public const int SystemRomLength = 0x3000;   // 12 KiB $D000-$FFFF (Applesoft + Monitor)
    public const int DiskRomLength = 0x100;      // 256 B slot-6 P5/P6 boot ROM ($C600)
    public const int CharRomLength = 0x800;      // 2 KiB char-gen (256 glyphs x 8 rows)

    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    private static string? PathIfExists(string fileName)
    {
        string path = Path.Combine(CacheRoot, "apple2", fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The 12 KiB system ROM path, or null if absent (the demo-fallback trigger).</summary>
    public static string? TryGetPath() => PathIfExists("apple2plus.rom");

    /// <summary>The 256 B slot-6 Disk II boot ROM path, or null if absent.</summary>
    public static string? TryGetDiskRomPath() => PathIfExists("disk2.rom");

    /// <summary>The optional 2 KiB char-gen ROM path, or null (non-fatal — Apple2Font.Fallback is used).</summary>
    public static string? TryGetCharRomPath() => PathIfExists("char.rom");

    /// <summary>Load + validate the 12 KiB system ROM (from <paramref name="path"/>, or the cache).</summary>
    public static byte[] Load(string? path = null) =>
        LoadExact(path ?? TryGetPath(), SystemRomLength, "Apple ][+ system");

    /// <summary>Load + validate the 256 B Disk II boot ROM, or null if absent.</summary>
    public static byte[]? TryLoadDiskRom() =>
        TryGetDiskRomPath() is { } p ? LoadExact(p, DiskRomLength, "Apple ][+ Disk II boot") : null;

    /// <summary>Load + validate the optional 2 KiB char-gen ROM, or null if absent (non-fatal).</summary>
    public static byte[]? TryLoadCharRom() =>
        TryGetCharRomPath() is { } p ? LoadExact(p, CharRomLength, "Apple ][+ char-gen") : null;

    private static byte[] LoadExact(string? path, int length, string which)
    {
        if (path is null)
            throw new FileNotFoundException(
                $"{which} ROM not found in the asset cache. Run tools/get-apple2-roms.ps1 (or .sh), "
              + "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).");
        byte[] rom = File.ReadAllBytes(path);
        if (rom.Length != length)
            throw new InvalidDataException(
                $"{which} ROM at {path} must be exactly {length} bytes; got {rom.Length}.");
        return rom;
    }
}
```

- [ ] **Step 4: Run the loader gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2RomLoaderTests"`
Expected: PASS — wrong length rejected; exact 12 KiB accepted; a missing char ROM is null (non-fatal). **This is the asset-loader gate.**

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/Apple2Rom.cs tests/CpuEmulator.Tests/Apple2/Apple2RomVectors.cs
git commit -m "feat(machines): Apple2Rom — system/disk2-boot/char-gen asset loader (the SpectrumRom twin)"
```

---

## Task 2: The fetch-on-demand asset scripts (`get-apple2-roms.{sh,ps1}`)

**Files:**
- Create: `tools/get-apple2-roms.sh`
- Create: `tools/get-apple2-roms.ps1`

> **No automated test** — the scripts are operational (they fetch live URLs). The gate is: they exist, are length-sanity-checked, never vendor, and the loader (Task 1) consumes their cache layout. The owner runs them once to fetch real ROMs, then the ROM-boot gate (Task 5) goes from skipped to green.

- [ ] **Step 1: Create `tools/get-apple2-roms.sh`**

```sh
#!/usr/bin/env sh
# Fetches the Apple ][+ ROMs into the vector cache (same root as the Spectrum/ZEX/Klaus assets; NEVER
# vendored). The Apple ][+ system ROM (Applesoft + Monitor), the slot-6 Disk II P5/P6 boot ROM, and the
# character-generator ROM are Apple's copyright; this repo fetches them on demand at test time — they are
# NOT committed to the repository (ADR 0014 Decision 7). Provide your own URLs/mirror if the defaults move.
#
# Layout written (consumed by CpuEmulator.Machines.Apple2Rom):
#   <cache>/apple2/apple2plus.rom   12288 bytes  (REQUIRED to boot a real Apple)
#   <cache>/apple2/disk2.rom          256 bytes  (needed to boot a disk; slot 6 $C600)
#   <cache>/apple2/char.rom          2048 bytes  (OPTIONAL — a built-in fallback font covers it)
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
ROM_DIR="$DEST/apple2"
mkdir -p "$ROM_DIR"

# Each row: filename | expected byte length | required(1)/optional(0) | space-separated candidate URLs.
# NOTE: the URLs below are placeholders for the owner to point at their preferred source/mirror; the Apple
# ROMs are user-supplied. The length sanity-check is what guarantees a correct image regardless of source.
fetch_one() {
    name="$1"; want_len="$2"; required="$3"; shift 3
    out="$ROM_DIR/$name"
    if [ -f "$out" ]; then echo "$name already present at $out"; return 0; fi
    for url in "$@"; do
        if curl -fsSL "$url" -o "$out" 2>/dev/null; then
            len=$(wc -c < "$out")
            if [ "$len" -eq "$want_len" ]; then
                echo "$name fetched to $out ($len bytes) from $url"; return 0
            fi
            rm -f "$out"; echo "WARN: $url failed sanity (len=$len, want $want_len) — trying next" >&2
        else
            rm -f "$out"; echo "WARN: fetch of $url failed — trying next" >&2
        fi
    done
    if [ "$required" -eq 1 ]; then
        echo "ERROR: could not fetch the required $name from any source" >&2; return 1
    fi
    echo "NOTE: optional $name not fetched — the built-in fallback font will be used" >&2; return 0
}

fetch_one "apple2plus.rom" 12288 1 \
    "https://mirror.example/apple2/apple2plus.rom"
fetch_one "disk2.rom" 256 1 \
    "https://mirror.example/apple2/disk2-p5p6.rom"
fetch_one "char.rom" 2048 0 \
    "https://mirror.example/apple2/apple2-character.rom"

echo "Apple ][+ ROM fetch complete (cache: $ROM_DIR)."
```

- [ ] **Step 2: Create `tools/get-apple2-roms.ps1`**

```pwsh
#!/usr/bin/env pwsh
# Fetches the Apple ][+ ROMs into the vector cache (same root as the Spectrum/ZEX/Klaus assets; NEVER
# vendored). The system ROM (Applesoft + Monitor), the slot-6 Disk II P5/P6 boot ROM, and the
# character-generator ROM are Apple's copyright; fetched on demand at test time, NOT committed (ADR 0014
# Decision 7). Layout written (consumed by CpuEmulator.Machines.Apple2Rom):
#   <cache>/apple2/apple2plus.rom   12288 bytes  (REQUIRED)
#   <cache>/apple2/disk2.rom          256 bytes  (needed to boot a disk)
#   <cache>/apple2/char.rom          2048 bytes  (OPTIONAL — fallback font covers it)
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$romDir = Join-Path $Destination "apple2"
New-Item -ItemType Directory -Force $romDir | Out-Null

function Fetch-One($name, $wantLen, $required, $urls) {
    $out = Join-Path $romDir $name
    if (Test-Path $out) { Write-Host "$name already present at $out"; return }
    foreach ($url in $urls) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $out -ErrorAction Stop
            $len = (Get-Item $out).Length
            if ($len -eq $wantLen) { Write-Host "$name fetched to $out ($len bytes) from $url"; return }
            Remove-Item $out -ErrorAction SilentlyContinue
            Write-Warning "$url failed the sanity check (len=$len, want $wantLen) — trying next"
        } catch {
            Remove-Item $out -ErrorAction SilentlyContinue
            Write-Warning "fetch of $url failed ($_) — trying next"
        }
    }
    if ($required) { Write-Error "could not fetch the required $name from any source" }
    else { Write-Warning "optional $name not fetched — the built-in fallback font will be used" }
}

# NOTE: placeholder URLs for the owner to point at a preferred source/mirror; the length sanity-check
# guarantees a correct image regardless of source.
Fetch-One "apple2plus.rom" 12288 $true  @("https://mirror.example/apple2/apple2plus.rom")
Fetch-One "disk2.rom"        256  $true  @("https://mirror.example/apple2/disk2-p5p6.rom")
Fetch-One "char.rom"        2048  $false @("https://mirror.example/apple2/apple2-character.rom")

Write-Host "Apple ][+ ROM fetch complete (cache: $romDir)."
```

> **Implementer note — the placeholder URLs.** The Apple ROMs are user-supplied (Apple copyright). The `mirror.example` URLs are placeholders the **owner** confirms at PR time (the char-gen ROM source is the residual item ADR 0014 Decision 8 flags). The **length sanity-check** (12288 / 256 / 2048) is the real guarantee — any source that returns the right length is correct. Mark the scripts executable: `git update-index --chmod=+x tools/get-apple2-roms.sh` (mirror the Spectrum script's mode).

- [ ] **Step 3: Commit**

```bash
chmod +x tools/get-apple2-roms.sh
git add tools/get-apple2-roms.sh tools/get-apple2-roms.ps1
git update-index --chmod=+x tools/get-apple2-roms.sh
git commit -m "feat(tools): get-apple2-roms.{sh,ps1} — fetch-on-demand system/disk2/char ROMs (never vendored)"
```

---

## Task 3: The `$C600` slot-6 boot-ROM board overload (`Apple2Board.SpecWithSystem`)

**Files:**
- Modify: `src/CpuEmulator.Machines/Apple2Board.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2BootTests.cs` (the board-mapping asserts; the file grows in Task 5)

The base board's `$C000-$CFFF` is one `RegionKind.Mmio` hole. The slot-6 boot ROM at `$C600-$C6FF` is a 256-byte **read-only ROM window inside that band**. The cleanest validator-clean shape: **carve the Mmio hole into three regions** — `$C000-$C5FF` Mmio, `$C600-$C6FF` Rom (the boot ROM), `$C700-$CFFF` Mmio — so the `$C600` page is a real ROM region the CPU reads while the IOU still owns the `$C000` page (the soft switches). The validator tiles the program space with non-overlapping Ram/Rom/Mmio regions (the existing `Apple2Board.Spec` already places adjacent Ram + Mmio + Rom regions), so three adjacent regions in the I/O band is allowed.

- [ ] **Step 1: Write the failing board-mapping test (the `$C600` boot ROM is readable; the IOU still owns `$C000`)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2BootTests.cs` (the board-mapping test first):

```csharp
using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

[Trait("Category", "UAT")]
public class Apple2BootTests
{
    private static byte[] DiskBootRom()
    {
        var rom = new byte[Apple2Rom.DiskRomLength];   // 256 B
        // The slot-6 boot signature ($Cn01=$20,$Cn03=$00,$Cn05=$03,$Cn07=$3C) so the Autostart scan
        // recognizes a Disk II in slot 6 (research §9). Offsets are slot-relative within $C600.
        rom[0x01] = 0x20; rom[0x03] = 0x00; rom[0x05] = 0x03; rom[0x07] = 0x3C;
        rom[0x00] = 0xA9;   // a recognizable first opcode (LDA #) so a read of $C600 is non-zero
        return rom;
    }

    private static (Machine machine, IAddressSpace bus) BuildBootBoard(byte[] systemRom)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var image = new SyntheticFluxImage(trackCount: 35);
        var disk = new Apple2DiskII(image);
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = Apple2Board.SpecWithSystem(systemRom, iou, disk, DiskBootRom());
        Machine machine = BoardMachineFactory.Build(spec);
        return (machine, machine.Space(AddressSpaceKind.Program));
    }

    [Fact]
    public void The_C600_boot_rom_is_readable_and_carries_the_slot6_signature()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000 (unused here)
        var (_, bus) = BuildBootBoard(rom);

        Assert.Equal(0xA9, bus.Read8(0xC600));    // the boot ROM's first byte
        Assert.Equal(0x20, bus.Read8(0xC601));    // the slot-6 signature bytes
        Assert.Equal(0x00, bus.Read8(0xC603));
        Assert.Equal(0x03, bus.Read8(0xC605));
        Assert.Equal(0x3C, bus.Read8(0xC607));
    }

    [Fact]
    public void The_IOU_still_owns_the_C000_page_after_adding_the_C600_rom()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        var (_, bus) = BuildBootBoard(rom);
        // A $C057 HIRES access still toggles the shared video state (the IOU owns $C000-$C0FF unchanged).
        _ = bus.Read8(0xC057);
        // $C600 is ROM (a different page); reading it has no soft-switch side effect.
        Assert.Equal(0xA9, bus.Read8(0xC600));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2BootTests.The_C600|FullyQualifiedName~Apple2BootTests.The_IOU_still"`
Expected: FAIL — `Apple2Board.SpecWithSystem` does not exist.

- [ ] **Step 3: Add `SpecWithSystem` to `Apple2Board`**

In `src/CpuEmulator.Machines/Apple2Board.cs`, add the `$C600` constants + the overload (carving the I/O band):

```csharp
    public const uint DiskBootRomBase = 0xC600;
    public const uint DiskBootRomLength = 0x0100;   // slot 6: $C600-$C6FF (the P5/P6 boot ROM)

    /// <summary>The fully-wired ][+ board for the surface (PR-H): the system ROM, the IOU (holding the LC
    /// + Disk II), AND the slot-6 Disk II boot ROM at $C600. The $C000-$CFFF I/O band is carved into three
    /// regions so $C600-$C6FF is a real ROM window (the boot ROM the Autostart slot-scan JMP ($C600)s into)
    /// while the IOU still owns the $C000 page (the soft switches). The LC + Disk II ride the IOU (the
    /// SpecWithLanguageCard / SpecWithDiskII contract: the IOU holds + Realizes them; no extra slot).
    /// <para>CALLER CONTRACT: <paramref name="iou"/> MUST have been constructed with this same
    /// <paramref name="disk2"/> (and the LC, if any) — <c>new Apple2Iou(state, lc, disk2)</c> — exactly as
    /// SpecWithDiskII requires.</para></summary>
    public static BoardSpec SpecWithSystem(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2,
                                           byte[] diskBootRom)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        ArgumentNullException.ThrowIfNull(disk2);
        ArgumentNullException.ThrowIfNull(diskBootRom);
        if (systemRom.Length != RomLength)
            throw new ArgumentException(
                $"Apple ][+ system ROM must be exactly ${RomLength:X} bytes; got ${systemRom.Length:X}.",
                nameof(systemRom));
        if (diskBootRom.Length != DiskBootRomLength)
            throw new ArgumentException(
                $"Disk II boot ROM must be exactly ${DiskBootRomLength:X} bytes; got ${diskBootRom.Length:X}.",
                nameof(diskBootRom));

        return new BoardSpec("apple2plus", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),                      // $0000-$BFFF RAM
                new MemoryRegion(IoBase, DiskBootRomBase - IoBase, RegionKind.Mmio),       // $C000-$C5FF I/O
                new MemoryRegion(DiskBootRomBase, DiskBootRomLength, RegionKind.Rom, diskBootRom), // $C600-$C6FF
                new MemoryRegion(DiskBootRomBase + DiskBootRomLength,                      // $C700-$CFFF I/O
                    IoBase + IoLength - (DiskBootRomBase + DiskBootRomLength), RegionKind.Mmio),
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, systemRom),           // $D000-$FFFF ROM
            ],
            Peripherals:
            [
                new PeripheralSlot("iou", iou, IouBase, IouLength),   // the $C000 page decoder (unchanged)
            ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None);
    }
```

> **Implementer note — validator tiling (verified clean).** `BoardSpecValidator` (`src/CpuEmulator.Machines/BoardSpecValidator.cs`) checks **region overlap** + **256-byte page-alignment** + **slot-in-Mmio** + **rom-image-size** — it does **not** require regions to tile contiguously, so adjacent non-overlapping Mmio/Rom/Mmio regions are accepted. The three I/O-band regions (`$C000-$C5FF` Mmio, `$C600-$C6FF` Rom, `$C700-$CFFF` Mmio) are page-aligned, non-overlapping, and tile `$C000-$CFFF`; the IOU slot (`$C000`, length `$0100`) sits inside the first Mmio region (`slot-not-in-mmio` passes — the slot is fully contained); the `$C600` Rom region's 256-byte image matches its length (`rom-image-mismatch` passes). The carve is the grounded, validator-clean form (and it is plain ROM the JIT fastmems). `Apple2BootTests.The_C600...` confirms it builds + reads.

- [ ] **Step 4: Run the board-mapping gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2BootTests.The_C600|FullyQualifiedName~Apple2BootTests.The_IOU_still"`
Expected: PASS — `$C600` reads the boot ROM (signature intact); the IOU still owns `$C000`. **This is the boot-ROM-mapping gate.**

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/Apple2Board.cs tests/CpuEmulator.Tests/Apple2/Apple2BootTests.cs
git commit -m "feat(machines): Apple2Board.SpecWithSystem — map the slot-6 \$C600 Disk II boot ROM"
```

---

## Task 4: `Apple2Surface` + the `Program.cs` boot-if-cached wiring + the client banner

**Files:**
- Create: `src/CpuEmulator.Surface.Web/Apple2Surface.cs`
- Modify: `src/CpuEmulator.Surface.Web/Program.cs`
- Modify: `src/CpuEmulator.Surface.Web/wwwroot/index.html`
- Modify: `src/CpuEmulator.Surface.Web/wwwroot/app.js`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2BootTests.cs` (the surface-construction smoke test)

- [ ] **Step 1: Write the failing surface-construction smoke test**

Append to `Apple2BootTests`:

```csharp
    [Fact]
    public void Apple2Surface_constructs_and_renders_a_280x192_frame()
    {
        // The surface wires the Apple triad through MachineHost (the SpectrumSurface pattern). With a
        // bare (all-zero) system ROM there is no boot, but the surface must construct, reset, and produce
        // a 280x192 FB frame when stepped (the host renders on the video chip's frame tick). No real ROM
        // is needed for THIS smoke test (the boot-to-] assertion is the separate ROM-gated test).
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000 (a NOP region; no crash)

        byte[]? lastFrame = null;
        CpuEmulator.Surface.Web.Apple2Surface surface =
            CpuEmulator.Surface.Web.Apple2Surface.Create(rom, diskBootRom: null, charRom: null,
                f => lastFrame = f, _ => { });

        surface.Host.RunHeadless(totalCycles: 40_000, sliceCycles: 17_030);   // > one frame tick

        Assert.NotNull(lastFrame);
        // FB header: 'F','B', ver, reserved, u16 width LE, u16 height LE.
        Assert.Equal((byte)'F', lastFrame![0]);
        Assert.Equal((byte)'B', lastFrame[1]);
        int width = lastFrame[4] | (lastFrame[5] << 8);
        int height = lastFrame[6] | (lastFrame[7] << 8);
        Assert.Equal(280, width);
        Assert.Equal(192, height);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2BootTests.Apple2Surface_constructs"`
Expected: FAIL — `Apple2Surface` does not exist.

- [ ] **Step 3: Create `Apple2Surface`**

Create `src/CpuEmulator.Surface.Web/Apple2Surface.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Composes the Apple ][+ for the web surface — the analogue of <see cref="SpectrumSurface"/>.
/// Builds the shared <see cref="Apple2VideoState"/>, the video / keyboard / speaker triad over it, the
/// Language Card, and (optionally) the Disk II controller; assembles the board via
/// <see cref="Apple2Board.SpecWithSystem"/> (with the slot-6 $C600 boot ROM when present), resets it, and
/// wires a <see cref="MachineHost"/> whose DISPLAY = the video chip, KEYBOARD = the keyboard chip, AUDIO =
/// the speaker chip (three objects over one shared state — unlike the Spectrum's single ULA). When the
/// boot ROM is absent the board uses <see cref="Apple2Board.SpecWithDiskII"/> (no $C600 window — no disk
/// boot, but the ROM-monitor `]` still appears). The char ROM is optional (Apple2Font.Fallback covers it).</summary>
public sealed record Apple2Surface(
    Machine Machine, Apple2Video Video, Apple2Keyboard Keyboard, Apple2Speaker Speaker, MachineHost Host)
{
    public static Apple2Surface Create(byte[] systemRom, byte[]? diskBootRom, byte[]? charRom,
                                       Action<byte[]> frameSink, Action<byte[]> audioSink,
                                       IFluxImage? drive1Image = null,
                                       ExecutionTier tier = ExecutionTier.Interpreter)
    {
        var state = new Apple2VideoState();
        // The video chip is constructed over a placeholder space; Realize re-binds it to the built
        // machine's program bus (the SpectrumUla/Apple2Video Realize contract).
        var placeholder = new AddressSpace(AddressSpaceKind.Program, 16);
        placeholder.MapMemory(0x0000, new byte[0x10000], writable: true);
        var video = new Apple2Video(placeholder, state, charRom);
        var keyboard = new Apple2Keyboard(state);
        var speaker = new Apple2Speaker(state);
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(drive1Image ?? new SyntheticFluxImage(trackCount: 35));
        var iou = new Apple2Iou(state, lc, disk);

        BoardSpec spec = diskBootRom is not null
            ? Apple2Board.SpecWithSystem(systemRom, iou, disk, diskBootRom)
            : Apple2Board.SpecWithDiskII(systemRom, iou, disk);

        Machine machine = BoardMachineFactory.Build(spec, tier);
        // The video/speaker chips are not board peripherals (the IOU owns $C000); Realize them over the
        // built machine so the video binds the live program bus + both schedule their 60 Hz ticks.
        // `Machine : IMachineContext` (verified: src/CpuEmulator.Core/Machine.cs `public sealed class
        // Machine : IMachineContext`), so the built machine IS the context — pass it directly.
        video.Realize(machine);
        speaker.Realize(machine);
        machine.Reset();

        var host = new MachineHost(machine, video, keyboard, frameSink, speaker, audioSink);
        return new Apple2Surface(machine, video, keyboard, speaker, host);
    }
}
```

> **Implementer note — Realizing the non-board video/speaker chips.** The IOU owns `$C000`, so `Apple2Video`/`Apple2Speaker` are **not** `PeripheralSlot`s — they need their `Realize` called to bind the live program bus (video) and schedule the 60 Hz ticks (both). **Verified grounding:** `Machine` is declared `public sealed class Machine : IMachineContext` (`src/CpuEmulator.Core/Machine.cs:8`), so the built `Machine` **is** the `IMachineContext` — pass it directly: `video.Realize(machine); speaker.Realize(machine);` (the form shown in `Apple2Surface` above). This is the grounded, smallest-change choice and needs no IOU/board change. (The Spectrum surface gets the same effect "for free" only because its ULA *is* a board peripheral the factory Realizes; the Apple's video/speaker are deliberately separate from the IOU, so the surface Realizes them itself.) If a future need arises to Realize them inside the board build, the IOU already forwards `_lc?.Realize` + `_disk2?.Realize` and could hold the video + speaker too — but that is unnecessary here.

- [ ] **Step 4: Wire `Program.cs` (Apple-first boot, then the existing fallback) + the client banner**

In `Program.cs` `DemoSession.RunAsync`, add an Apple branch BEFORE the Spectrum/demo branch:

```csharp
        // Boot the Apple ][+ when its system ROM is cached; else fall back to the Spectrum, else the demo.
        string? appleRom = CpuEmulator.Machines.Apple2Rom.TryGetPath();
        ISurfacePump pump;
        string assetState;   // surfaced to the client banner / status line
        if (appleRom is not null)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom);
            byte[]? bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom();
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            Apple2Surface apple = Apple2Surface.Create(sys, bootRom, charRom,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            pump = new SurfacePump(apple.Host, AppleSliceCycles, ApplePeriod);
            assetState = charRom is null ? "apple-fallback-font" : "apple";
        }
        else { /* ...the existing Spectrum-if-cached-else-demo branch, with assetState set accordingly... */ }
```

Add the Apple pacing constants to `DemoSession`:

```csharp
    // The Apple ][+ runs at ~1.0205 MHz: a ~17,030-cycle slice every ~16 ms (60 Hz, matches Apple2Video).
    private const long AppleSliceCycles = 17_030;
    private static readonly TimeSpan ApplePeriod = TimeSpan.FromMilliseconds(16);
```

> **Implementer note — surfacing `assetState` to the client.** The minimal H wiring sends the asset/board state to the client once on connect as a small text WS message the client renders into the banner + status line (the design `copy.md` strings). The full `ST` status-frame seam is **PR-P** (a later, richer frame); H needs only the one-shot board/asset string to drive the boot-time banner. Send it right after `socket` is accepted (a `socket.SendAsync(Encoding.UTF8.GetBytes($"ST {assetState}"), Text, ...)` before the pump starts), and have `app.js` map `assetState` → the `copy.md` banner/status strings. Keep it text (the client already reads text for nothing else inbound — outbound text is new but trivial; it does not disturb the FB/AU binary path).

In `wwwroot/index.html`, set the Apple title + add the banner + the hint (the `copy.md` strings):

```html
  <title>CpuEmulator — Apple ][+</title>
  ...
  <h1>CpuEmulator — Apple ][+</h1>
  <canvas id="screen" width="280" height="192"></canvas>
  <div id="status">connecting…</div>
  <div id="asset-banner" hidden></div>
  <button id="enable-sound" type="button">click to enable sound</button>
  <div id="hint">Uppercase only. <kbd>Ctrl+B</kbd> = BASIC. <kbd>Ctrl+Backspace</kbd> = RESET.</div>
```

> **Implementer note — canvas sizing (handoff D3 / `tokens.md`).** Replace the Spectrum's fixed `width: 768px; height: 576px;` canvas CSS with an aspect-preserving rule so the 280-wide Apple frame upscales sanely (the full 40↔80 Videx multi-geometry is PR-M; H needs the 280×192 frame to display crisply). A simple `width: min(90vw, 840px); height: auto; aspect-ratio: 280 / 192;` with `image-rendering: pixelated` is sufficient and forward-compatible. This is the one Spectrum CSS that does not transfer (the handoff's "Canvas sizing change").

In `wwwroot/app.js`, handle the inbound `ST` text message (banner + status) and add the `Ctrl+Backspace` RESET `preventDefault`:

```javascript
  // Inbound text from the host: a one-shot "ST <assetState>" board/asset string drives the banner.
  ws.addEventListener("message", (ev) => {
    if (typeof ev.data === "string" && ev.data.startsWith("ST ")) {
      const stateName = ev.data.slice(3);
      const banner = document.getElementById("asset-banner");
      if (stateName === "apple-fallback-font") {
        status.textContent = "connected · Apple ][+ · fallback font";
      } else if (stateName.startsWith("apple")) {
        status.textContent = "connected · Apple ][+ · documented 6502";
      } else if (stateName === "demo") {
        status.textContent = "connected · demo fallback · no Apple ROM";
        banner.hidden = false;
        banner.textContent = "Apple ][+ ROMs not found — showing the demo pattern. " +
                             "Fetch them once: tools/get-apple2-roms.sh — then reload this page.";
      }
    }
  });
  // RESET is Ctrl+Backspace (the browser cannot send the hardware Ctrl+Reset).
  window.addEventListener("keydown", (ev) => {
    if (ev.ctrlKey && ev.code === "Backspace") ev.preventDefault();
  });
```

> **Implementer note — keep the existing binary `ws.onmessage`.** `app.js` sets `ws.binaryType = "arraybuffer"` and its `ws.onmessage` decodes FB/AU binary frames. The new text-`ST` handler is added as a SECOND `addEventListener("message", …)` (text frames arrive as strings, binary as `ArrayBuffer`) so the binary path is untouched. The full keyboard extension set (the `ctrl` field on the key JSON, `Ctrl+B`/`Ctrl+C` folds — handoff T-F) is **PR-T**; H adds only the `Ctrl+Backspace` RESET `preventDefault` needed for the boot-to-`]` flow to be exercisable.

- [ ] **Step 5: Run the surface smoke gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2BootTests.Apple2Surface_constructs"`
Expected: PASS — `Apple2Surface.Create` builds, resets, and emits a 280×192 FB frame through `MachineHost`. **This is the surface-construction gate** (the wiring is correct even with a bare ROM).

- [ ] **Step 6: Build the web project + commit**

```bash
dotnet build src/CpuEmulator.Surface.Web/CpuEmulator.Surface.Web.csproj
git add src/CpuEmulator.Surface.Web/Apple2Surface.cs src/CpuEmulator.Surface.Web/Program.cs \
        src/CpuEmulator.Surface.Web/wwwroot/index.html src/CpuEmulator.Surface.Web/wwwroot/app.js \
        tests/CpuEmulator.Tests/Apple2/Apple2BootTests.cs
git commit -m "feat(surface): Apple2Surface wires the ][+ triad through MachineHost; Apple-first boot + calm asset banner"
```

---

## Task 5: The ROM-boot gate — boots to the Applesoft `]` prompt on both tiers (skip-with-note absent)

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2BootTests.cs`

The row-H deliverable: with the real 12 KiB system ROM fetched, the ][+ boots and the Autostart Monitor paints the **`]` prompt** (the Applesoft ready prompt) on the text screen — asserted as **structural ink on a mostly-blank screen** on **both execution tiers** (the `SpectrumBootTests` discipline), **skip-with-note when the ROM is absent** so ROM-free CI stays green.

- [ ] **Step 1: Write the ROM-boot gate (both tiers, structural assertion + committed-hash placeholder)**

Append to `Apple2BootTests`:

```csharp
    // Two ~17,030-cycle frames is ample for the ROM cold-start to clear the screen + paint the prompt.
    private const long BootCycles = 500_000;

    [Apple2RomTheory]
    [InlineData(ExecutionTier.Interpreter)]
    [InlineData(ExecutionTier.Jit)]
    public void Rom_boots_to_the_applesoft_prompt_on_both_tiers(ExecutionTier tier)
    {
        byte[] systemRom = Apple2Rom.Load(Apple2RomVectors.TryGetRomPath());
        byte[]? charRom = Apple2Rom.TryLoadCharRom();   // may be null -> Apple2Font.Fallback (still renders)

        // Build the fully-wired board (LC + Disk II + the $C600 boot ROM signature) and the video chip.
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var image = new SyntheticFluxImage(trackCount: 35);
        var disk = new Apple2DiskII(image);
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = Apple2Board.SpecWithSystem(systemRom, iou, disk, DiskBootRom());
        Machine machine = BoardMachineFactory.Build(spec, tier);
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state, charRom);
        machine.Reset();
        machine.Run(BootCycles);

        var rgba = new uint[Apple2Video.Width280 * Apple2Video.Height192];
        video.RenderInto(rgba);

        // Un-fakeable structural assertion: the Autostart Monitor clears the text screen (mostly MonoOff)
        // and paints the heading + the `]` prompt (MonoOn ink pixels). A dead/garbage boot lacks both
        // properties: it is either all-off (no prompt) or noisy (no clear mostly-off background).
        int offPixels = 0, onPixels = 0;
        foreach (uint p in rgba)
        {
            if (p == Apple2Palette.MonoOff) offPixels++;
            else if (p == Apple2Palette.MonoOn) onPixels++;
        }
        int total = Apple2Video.Width280 * Apple2Video.Height192;
        Assert.True(offPixels > total / 2,
            $"expected a mostly-blank text screen; got {offPixels}/{total} off pixels");
        Assert.True(onPixels > 50,
            $"expected the `]` prompt + heading ink; got {onPixels} on pixels");

        // Tighter gate: a committed RGBA hash. On the FIRST green run, capture the hash (uncomment the
        // print), paste it below, then re-run. Both tiers MUST produce the identical frame.
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[apple boot frame hash] {hash}");  // <-- uncomment once to capture
        string ExpectedBootHash = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN";
        if (ExpectedBootHash != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
            Assert.Equal(ExpectedBootHash, hash);
    }

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
```

> **Implementer note — the committed-hash placeholder + the char-ROM choice.** The structural assertion (mostly-off + meaningful ink) is the un-fakeable gate; the committed-hash branch stays inert until the owner runs `get-apple2-roms` and captures the hash on first green run (the exact `SpectrumBootTests` mechanic). The hash depends on the char ROM — if both the real char ROM and the fallback are in play across machines, **capture the hash with the fallback font** (always available) so the gate is reproducible without the optional char asset; note that in the captured-hash comment. The `]` prompt is structurally present regardless of font (the glyph cell at row 23 col 0 is non-blank in both the real and fallback sets), so the **structural** assertion holds either way — that is the load-bearing gate; the hash is the tightening.

- [ ] **Step 2: Run it (skipped without the ROM; green with it)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2BootTests.Rom_boots"`
Expected (no ROM in cache): **SKIPPED** with the note "Apple ][+ system ROM not found — run tools/get-apple2-roms…". Expected (after the owner runs `get-apple2-roms` and the 12 KiB ROM is cached): **PASS on both tiers** — a mostly-blank text screen with the `]` prompt + heading ink. **This is the row-H ROM-boot gate (both tiers, skip-with-note absent).**

- [ ] **Step 3: Run the full Apple2 suite + the full suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2"`
Then: `dotnet build CpuEmulator.slnx && dotnet test CpuEmulator.slnx`
Expected: PASS — PR-A..G gates + PR-H's loader/script-layout/boot-ROM-mapping/surface/boot gates all green (the boot gate skips without the asset); the full suite green; the web project builds. The base `Apple2Board.Spec`/`SpecWithDiskII` overloads are unchanged (additive overload only).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2BootTests.cs
git commit -m "test(apple2): ROM-boot gate — boots to the Applesoft ] prompt on both tiers (skip-with-note absent)"
```

---

## Task 6: Queue update

**Files:**
- Modify: `docs/BUILDER_QUEUE.md`

- [ ] **Step 1: Flip the queue row + add the shipped entry**

In `docs/BUILDER_QUEUE.md`, set row **H** status to ✅, update the **Last updated** banner (date + "PR-H merged — base-machine boot milestone complete"), and add a **Recently shipped** entry for PR-H (the surface + scripts + ROM-boot gate; the base ][+ now boots to `]`).

- [ ] **Step 2: Commit**

```bash
git add docs/BUILDER_QUEUE.md
git commit -m "docs(queue): Apple2 PR-H (surface + ROM-boot gate) done — base-machine boot milestone complete"
```

---

## Done-when

- `Apple2Rom` ships the three-ROM cache loader (system 12 KiB required / disk2-boot 256 B / char-gen 2 KiB optional) with exact-length validation and a missing-char-ROM-is-non-fatal contract (the `SpectrumRom` twin).
- `tools/get-apple2-roms.{sh,ps1}` fetch all three ROMs into `<cache>/apple2/` with byte-length sanity checks, **never vendoring**, both `.sh` + `.ps1`, char-gen documented + optional.
- `Apple2Board.SpecWithSystem` maps the slot-6 `$C600` boot ROM (signature-carrying) as a real ROM window while the IOU keeps the `$C000` page; the base overloads are unchanged.
- `Apple2Surface` wires the `Apple2Video`/`Apple2Keyboard`/`Apple2Speaker` triad (over one shared `Apple2VideoState`) through `MachineHost` exactly as `SpectrumSurface` does; `Program.cs` boots the Apple when its system ROM is cached, else the existing fallback, surfacing the asset state to a calm named-script banner (never red).
- The **ROM-boot gate** asserts the Applesoft `]` prompt (structural ink on a mostly-blank text screen) on **both** execution tiers when the system ROM is present, and **skips-with-note when absent** — the `SpectrumBootTests` discipline exactly, plus the committed-hash placeholder.
- The char-gen ROM uses `Apple2Font.Fallback` when absent (text renders; status says `fallback font`); the real char ROM injects when fetched.
- The deliverable is a clear, browser-observable "boots to `]`" state (the Coordinator's Tester + Polisher milestone pass is enabled). Queue row **H** is ✅ — the **base-machine boot milestone is complete**.

---

## API-drift note for the owner

- **`MachineHost` is display-device-agnostic, no change needed** — it sizes `_rgba` from `display.Width * display.Height`, so the 280×192 Apple frame works through the shipped 6-arg ctor. (The per-frame re-size for the 40↔80 Videx switch is PR-M, not H.)
- **Realizing the non-board video/speaker chips is resolved, not open:** `Machine` is `public sealed class Machine : IMachineContext` (`src/CpuEmulator.Core/Machine.cs:8`), so the built machine **is** the context — `video.Realize(machine); speaker.Realize(machine);` is the grounded form (shown in `Apple2Surface`), needing no IOU/board change. **This is a wiring choice the ADR anticipated** — ADR 0014 Decision 3's composition note: "construct the collaborators, build the BoardSpec referencing them, build the Machine (which Realizes them), then hand the same instances to the surface."
- **No drift on the asset posture** — the `get-spectrum-rom`/`SpectrumRom`/`SpectrumBootTests` patterns transfer verbatim (ADR 0014 Decision 7). The **placeholder fetch URLs** in the scripts are the one owner-input item: the Apple ROMs are user-supplied (Apple copyright), and the char-gen ROM's canonical source is the residual item ADR 0014 Decision 8 flagged — the **length sanity-check** (12288 / 256 / 2048) is the real correctness guarantee, so any source returning the right length works. Flag to the owner at PR time: confirm the fetch URLs (or supply the ROMs into the cache manually) before the ROM-boot gate goes from skipped to green.
- **`TimingTier`/`ITimingSensitive` remain unshipped and unreferenced** (inherited from PR-C/D/F's polled/Coarse models).
