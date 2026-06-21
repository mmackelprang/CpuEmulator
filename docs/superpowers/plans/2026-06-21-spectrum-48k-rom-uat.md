# Plan — ZX Spectrum 48K ROM UAT (multi-variant boot + interactive BASIC)

**Date:** 2026-06-21
**Author:** Claude Planner
**Grounded against:** `main` @ `fbd3a61` (the Apple ][+ arc A–T + the post-arc boot-gate fix).
**Spec/decision basis:** ZX Spectrum 48K row in `docs/ROADMAP.md` (Recently shipped) + the live diagnostic
calibration captured in the queue brief (2026-06-21). 48K-only by owner decision; the 128/+2/+3 ROMs are a
separate future machine arc and are explicitly **out of scope** here.

---

## Why

The shipped Spectrum boot gate
(`tests/CpuEmulator.Tests/Spectrum/SpectrumBootTests.cs`) has two real defects that a live diagnostic surfaced:

1. **`BootCycles = 200_000` is ~30× too small.** The real 48K power-on RAM test isn't even complete at 200k
   T-states. Full boot to the copyright screen is **≈ 5.9M cycles**; the frame is fully stable by **~13M**. The
   gate "passes" today only because the partial screen happens to be mostly white with some ink — it is **not**
   asserting the real `© 1982 Sinclair Research Ltd` screen. The header comment ("Two frames at 69888
   T-states/frame ≈ 140k cycles") is wrong and misleads the next reader.
2. **It tests exactly one ROM.** The owner has six 48K ROM variants (canonical UK, three Arabic, a Swedish
   "Beckman", a prototype). They differ in character set, copyright wording, and (Beckman) even the reset
   sequence, but they all **boot to a mostly-white paper screen with black text** — the structural invariant we
   can gate on every present variant, on both tiers.

This plan recalibrates + parameterizes the boot gate across **(variant × tier)** and adds an **interactive BASIC
behavioral UAT** that drives the keyboard matrix to enter a BASIC line and asserts the on-screen result —
proving boot → keyboard → BASIC interpreter → screen end-to-end.

### Exact calibration numbers (from the live diagnostic — use these, do not re-guess)

| Quantity | Value | Note |
|---|---|---|
| Full boot to copyright screen | **≈ 5,900,000 cycles** | RAM test + screen clear + (C) line painted |
| Fully stable | **~13,000,000 cycles** | flash phase settled, no further repaint |
| **`BootCycles`** (new) | **`7_000_000`** | ≈ 100 frames; safely past 5.9M, before the unnecessary 13M |
| White paper | `SpectrumPalette.Colors[7]` = **`0xFFD7D7D7`** | base white, **NOT** bright `Colors[15]` `0xFFFFFFFF` |
| Black ink | `SpectrumPalette.Colors[0]` = **`0xFF000000`** | |
| Canonical (C) line ink | **≈ 307 black px** | gate floor `> 50` is safe for all variants; the canonical ROM can tighten to `> 200` |
| Both tiers byte-identical | yes | a committed per-variant frame hash is viable |

---

## Approach (high level)

- **Task 1** — Extend the asset-cache layout + a discovery/fetch convention to hold the six variant ROMs at
  `<cache>/spectrum/variants/<name>.rom` (the canonical `<cache>/spectrum/48.rom` stays exactly where it is).
  A new `SpectrumRomVariants` helper enumerates the present 16384-byte variant ROMs (skip-with-note when none),
  and a `tools/get-spectrum-rom-variants.{sh,ps1}` documents the owner-copy step. **Production-side helper lives
  in `CpuEmulator.Machines` beside `SpectrumRom`** so the surface/tools can reuse it; the **test-side theory data
  source** lives in the test project beside `SpectrumRomVectors`.
- **Task 2** — Recalibrate `BootCycles` to `7_000_000`, fix the comment, and turn
  `Rom_boots_to_the_basic_copyright_screen` into a `[Theory]` over **(variant × tier)** with a variant-safe
  structural assertion (mostly-white `Colors[7]` paper + a black-`Colors[0]` ink floor) and a per-variant
  committed hash captured on first green run.
- **Task 3** — Add `SpectrumInteractiveTests`: boot the canonical ROM fully, drive the keyboard matrix to enter
  `PRINT 2+2` + ENTER, run enough post-keystroke frames, and assert the printed `4` / report line appears in the
  top print region (structural ink-delta + a committed hash) — on both tiers.
- **Task 4** — Notes only (do NOT build here): a `--board` surface override and the Tester's scratch cleanup.

Every task is **TDD**: write/adjust the test, watch it fail (or skip-with-note when the asset is absent), then
make it pass. ROM-dependent tests **skip-with-note** when the asset is absent so ROM-free CI stays green — the
exact pattern the shipped `SpectrumRomFactAttribute`/`SpectrumRomTheoryAttribute` already use.

---

## Grounded shipped-API facts (verified against `main` @ `fbd3a61`)

These are the load-bearing signatures the literal code below depends on. All verified by reading the source:

- `SpectrumRom.TryGetPath()` → `string?` and `SpectrumRom.Load(string? path = null)` → `byte[]`
  (validates `rom.Length == RomLength` = `0x4000`). Cache root =
  `Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS") ?? <UserProfile>/.cache/cpuemulator/vectors`;
  the canonical ROM is `<root>/spectrum/48.rom`. (`src/CpuEmulator.Machines/SpectrumRom.cs`)
- `SpectrumMachine.Build(byte[] rom, out SpectrumUla ula, ExecutionTier tier = ExecutionTier.Interpreter)`
  → `Machine`. (`src/CpuEmulator.Machines/SpectrumMachine.cs`)
- `Machine.Reset()` and `Machine.Run(long cycles)` → `long`. (`src/CpuEmulator.Core/Machine.cs:112`)
- `SpectrumUla.RenderInto(Span<uint> rgba)`; constants `InkWidth=256`, `InkHeight=192`, `BorderPx=32`,
  `FullWidth=320`, `FullHeight=256`. `PostKey(in KeyEvent e)` maps via `SpectrumKeyMatrix.TryMap`; a pressed key
  pulls its matrix bit LOW. (`src/CpuEmulator.Peripherals/SpectrumUla.cs`)
- `SpectrumPalette.Colors[7] == 0xFFD7D7D7u` (base white), `Colors[0] == 0xFF000000u` (black), `Colors[15] ==
  0xFFFFFFFFu` (bright white). (`src/CpuEmulator.Peripherals/SpectrumPalette.cs`,
  `tests/.../SpectrumScreenTests.cs:14-21`)
- `KeyEvent` = `readonly record struct KeyEvent(KeyAction Action, KeyCode Key, char? Char, bool Ctrl = false)`.
  `KeyAction { Down, Up }`. `KeyCode` includes all letters, `Digit0..9`, `Space`, `Enter`, `CapsShift`,
  `SymbolShift`. (`src/CpuEmulator.Core/KeyEvent.cs`, `KeyCode.cs`)
- `SpectrumKeyMatrix.TryMap(KeyCode, out int halfRow, out int bit)`: `P → (5,0)`, `Digit2 → (3,1)`,
  `K → (6,2)`, `SymbolShift → (7,1)`, `Enter → (6,0)`. A chord (two keys down simultaneously) is two `PostKey`
  Down events with no intervening Up. (`src/CpuEmulator.Peripherals/SpectrumKeyMatrix.cs`)
- `SpectrumRomVectors.TryGetRomPath()` + `SpectrumRomTheoryAttribute` (skip-with-note) already exist in the test
  project. (`tests/.../SpectrumRomVectors.cs`)
- `ExecutionTier { Interpreter, Jit }` is the enum the existing `[InlineData(ExecutionTier.Interpreter)]` /
  `[InlineData(ExecutionTier.Jit)]` rows use.
- xUnit `[MemberData(nameof(Source), MemberType = typeof(T))]` over `IEnumerable<object[]>` is the in-tree
  theory-data convention (`tests/.../TomHarte/Z80TomHarteTests.cs`).

### Shipped-API drift / facts the Builder must know

1. **No variant cache layout exists yet.** `SpectrumRom` only knows `<root>/spectrum/48.rom`. Task 1 *adds* the
   `variants/` subdir convention — it does **not** move or rename `48.rom` (the canonical path stays for the
   shipped surface + existing gate).
2. **The web surface probes Apple before Spectrum** (`Program.cs`: `appleRom is not null` branch wins, then
   SoftCard, then Spectrum, then demo). A live Spectrum UAT through the server would need a `--board` override if
   an Apple ROM is also cached. **Flagged as an optional follow-on in Task 4 — not built here.** (This plan's
   gates are headless and do not go through the server, so they are unaffected.)
3. **The Beckman ROM has "a markedly different reset sequence with coloured blocks"** (owner `info.txt`). It
   still settles to a white-paper BASIC screen, but the structural floor (`> 50` ink, `> half` white) must not
   assume the canonical 307-px copyright line. The variant-safe thresholds below hold for it; its committed hash
   is captured per-variant on first green run.
4. **The frame buffer is `uint[FullWidth*FullHeight]` = `320*256` = 81920 pixels.** The ink region is the inner
   `256×192` offset by `BorderPx` (the existing gate's loop, reused verbatim).

---

## Task 1 — Multi-variant ROM cache convention + discovery helper

**Files:**
- `src/CpuEmulator.Machines/SpectrumRomVariants.cs` (new — production helper)
- `tools/get-spectrum-rom-variants.sh` + `tools/get-spectrum-rom-variants.ps1` (new — owner-copy convention)
- `tests/CpuEmulator.Tests/Spectrum/SpectrumRomVariants.cs` (new — test theory-data source + skip attr)
- `tests/CpuEmulator.Tests/Spectrum/SpectrumRomVariantsTests.cs` (new — discovery unit gate)

### 1a. Production discovery helper

The variants live at `<cache>/spectrum/variants/<name>.rom`. The canonical UK ROM is *also* surfaced as a
variant named `spec48` so the (variant × tier) theory can include it even when only `48.rom` (not
`variants/spec48.rom`) is cached — we fall back to the canonical path for that one name.

```csharp
// src/CpuEmulator.Machines/SpectrumRomVariants.cs
namespace CpuEmulator.Machines;

/// <summary>Discovers the owner's ZX Spectrum 48K ROM *variants* in the asset cache (NOT vendored — Amstrad's
/// copyright; the owner copies their six 16 KiB ROMs in, exactly as the canonical 48.rom is fetched on demand).
/// Variants live at &lt;cache&gt;/spectrum/variants/&lt;name&gt;.rom; the canonical UK ROM (&lt;cache&gt;/spectrum/48.rom,
/// fetched by tools/get-spectrum-rom) is also surfaced under the variant name "spec48" so a single (variant ×
/// tier) gate can cover it without a duplicate copy. Every returned path is a present, exactly-16384-byte file;
/// callers skip-with-note when the enumeration is empty.</summary>
public static class SpectrumRomVariants
{
    /// <summary>One discovered variant ROM: a stable short <paramref name="Name"/> (the file stem, e.g.
    /// "spec48", "spec48-arabic-v1") and the absolute <paramref name="Path"/> to a 16384-byte image.</summary>
    public readonly record struct Variant(string Name, string Path);

    /// <summary>The cache subdir holding the variant ROMs (&lt;root&gt;/spectrum/variants).</summary>
    public static string VariantsDir(string? root = null)
    {
        root ??= Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "cpuemulator", "vectors");
        return System.IO.Path.Combine(root, "spectrum", "variants");
    }

    /// <summary>Enumerate the present, valid (exactly 16384-byte) variant ROMs, deterministically ordered by
    /// name. Always includes "spec48" when the canonical 48.rom is cached (even if variants/spec48.rom is not),
    /// so the canonical ROM is part of the (variant × tier) sweep. Returns an empty list when nothing is present
    /// (callers skip-with-note).</summary>
    public static IReadOnlyList<Variant> Discover(string? root = null)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);

        string dir = VariantsDir(root);
        if (System.IO.Directory.Exists(dir))
        {
            foreach (string path in System.IO.Directory.EnumerateFiles(dir, "*.rom"))
            {
                if (new System.IO.FileInfo(path).Length != SpectrumRom.RomLength) continue; // 0x4000 only
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                found[name] = path;
            }
        }

        // Fold in the canonical ROM under "spec48" if a variants/spec48.rom was not already found.
        if (!found.ContainsKey("spec48"))
        {
            string? canonical = SpectrumRom.TryGetPath(root);
            if (canonical is not null) found["spec48"] = canonical;
        }

        var list = new List<Variant>(found.Count);
        foreach (var kv in found) list.Add(new Variant(kv.Key, kv.Value));
        return list;
    }
}
```

This calls `SpectrumRom.TryGetPath(root)` with an explicit root. The shipped `TryGetPath()` takes **no
argument** — so Task 1 also adds the optional-`root` overload to `SpectrumRom` (additive; the no-arg call sites
are unchanged):

```csharp
// src/CpuEmulator.Machines/SpectrumRom.cs — REPLACE the existing TryGetPath() with this overloaded pair.
    /// <summary>Resolve the cached canonical ROM path, or null if absent. The optional <paramref name="root"/>
    /// overrides the cache root (defaults to $CPUEMULATOR_TESTVECTORS or ~/.cache/cpuemulator/vectors).</summary>
    public static string? TryGetPath(string? root = null)
    {
        root ??= Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "spectrum", "48.rom");
        return File.Exists(path) ? path : null;
    }
```

> The body is byte-identical to the shipped one except the root is now a parameter with the same default. Every
> existing `SpectrumRom.TryGetPath()` call (no arg) compiles + behaves unchanged.

### 1b. Owner-copy convention scripts (never vendored)

Mirror the shipped `get-spectrum-rom.{sh,ps1}` provenance + sanity pattern, but **copy from the owner's local
ROM directory** (the variants are not on a public mirror in this set). Builder runs this to populate the cache
before the live UAT.

```sh
#!/usr/bin/env sh
# tools/get-spectrum-rom-variants.sh
# Copies the owner's six ZX Spectrum 48K ROM *variants* (each exactly 16384 bytes) into the vector cache
# (<root>/spectrum/variants). NOT vendored — Amstrad's copyright; used with permission per the owner's
# zx-roms/spectrum16-48/info.txt. Source defaults to the owner's local mirror; override with $1.
set -eu
SRC="${1:-D:/prj/zx-roms/spectrum16-48}"
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
OUT="$DEST/spectrum/variants"
mkdir -p "$OUT"

count=0
for f in "$SRC"/spec48.rom "$SRC"/spec48-arabic-v1.rom "$SRC"/spec48-arabic-v2.rom \
         "$SRC"/spec48-arabic-v31.rom "$SRC"/spec48-beckman.rom "$SRC"/spec48-prototype.rom; do
    if [ ! -f "$f" ]; then echo "WARN: missing $f — skipping" >&2; continue; fi
    len=$(wc -c < "$f")
    if [ "$len" -ne 16384 ]; then echo "WARN: $f is $len bytes (want 16384) — skipping" >&2; continue; fi
    cp "$f" "$OUT/$(basename "$f")"
    count=$((count + 1))
done
echo "Copied $count Spectrum 48K variant ROM(s) into $OUT"
[ "$count" -gt 0 ] || { echo "ERROR: copied 0 variants from $SRC" >&2; exit 1; }
```

```powershell
#!/usr/bin/env pwsh
# tools/get-spectrum-rom-variants.ps1
# Copies the owner's six ZX Spectrum 48K ROM variants (each exactly 16384 bytes) into the vector cache
# (<root>/spectrum/variants). NOT vendored — Amstrad's copyright; used with permission.
param(
    [string]$Source = "D:/prj/zx-roms/spectrum16-48",
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$out = Join-Path $Destination "spectrum/variants"
New-Item -ItemType Directory -Force $out | Out-Null

$names = @("spec48.rom","spec48-arabic-v1.rom","spec48-arabic-v2.rom",
           "spec48-arabic-v31.rom","spec48-beckman.rom","spec48-prototype.rom")
$count = 0
foreach ($n in $names) {
    $src = Join-Path $Source $n
    if (-not (Test-Path $src)) { Write-Warning "missing $src — skipping"; continue }
    $len = (Get-Item $src).Length
    if ($len -ne 16384) { Write-Warning "$src is $len bytes (want 16384) — skipping"; continue }
    Copy-Item $src (Join-Path $out $n) -Force
    $count++
}
Write-Host "Copied $count Spectrum 48K variant ROM(s) into $out"
if ($count -eq 0) { Write-Error "copied 0 variants from $Source" }
```

### 1c. Test-side theory data source + skip attribute

```csharp
// tests/CpuEmulator.Tests/Spectrum/SpectrumRomVariants.cs
using CpuEmulator.Core;
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

/// <summary>xUnit theory-data source for the (variant × tier) boot sweep. Enumerates the present 16384-byte
/// variant ROMs via the production SpectrumRomVariants.Discover, crossed with both execution tiers. Empty when
/// no variant (and no canonical 48.rom) is cached — the [SpectrumRomVariantTheory] attribute then skips-with-note
/// so ROM-free CI stays green (mirrors SpectrumRomTheoryAttribute).</summary>
internal static class SpectrumRomVariantData
{
    public static IReadOnlyList<SpectrumRomVariants.Variant> Present() => SpectrumRomVariants.Discover();

    /// <summary>(name, romPath, tier) rows for [MemberData]. name is the stable variant id used to key the
    /// committed per-variant hash.</summary>
    public static IEnumerable<object[]> VariantTierRows()
    {
        foreach (var v in Present())
        {
            yield return new object[] { v.Name, v.Path, ExecutionTier.Interpreter };
            yield return new object[] { v.Name, v.Path, ExecutionTier.Jit };
        }
    }
}

/// <summary>Skip-with-note when NO Spectrum 48K ROM (canonical or variant) is cached.</summary>
public sealed class SpectrumRomVariantTheoryAttribute : TheoryAttribute
{
    public SpectrumRomVariantTheoryAttribute()
    {
        if (SpectrumRomVariantData.Present().Count == 0)
            Skip = "No Spectrum 48K ROM cached — run tools/get-spectrum-rom.ps1 (canonical) and/or " +
                   "tools/get-spectrum-rom-variants.ps1 (the six variants), or set CPUEMULATOR_TESTVECTORS.";
    }
}
```

> **`[MemberData]` + skip interaction (load-bearing):** xUnit evaluates `[MemberData]` at discovery time. When
> no ROM is cached, `VariantTierRows()` yields **zero rows**, which xUnit reports as a hard failure
> ("No data found for ... theory"). To keep ROM-free CI green, `VariantTierRows()` must yield **at least one
> sentinel row** when empty, and the test body skips it. Use this guarded form instead:
>
> ```csharp
>     public static IEnumerable<object[]> VariantTierRows()
>     {
>         var present = Present();
>         if (present.Count == 0)
>         {
>             yield return new object[] { "(none)", "", ExecutionTier.Interpreter }; // sentinel; body skips
>             yield break;
>         }
>         foreach (var v in present)
>         {
>             yield return new object[] { v.Name, v.Path, ExecutionTier.Interpreter };
>             yield return new object[] { v.Name, v.Path, ExecutionTier.Jit };
>         }
>     }
> ```
>
> **This project is xUnit v2.9.3** (`tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` —
> `xunit` `2.9.3`). xUnit v2 has **no `Assert.SkipWhen`** (that is v3). So the test body handles the all-absent
> sentinel row with an **early return** — `if (romPath.Length == 0) return;` — while the
> `[SpectrumRomVariantTheory]` attribute carries the human-facing skip reason for the all-absent case (the whole
> theory is `Skip`ped at the attribute level when `Present().Count == 0`, so the sentinel-row early-return is
> only a belt-and-suspenders guard that never actually executes when the attribute already skipped). **Use the
> early-return form throughout this plan — do NOT use `Assert.SkipWhen`.**

### 1d. Discovery unit gate (asset-free — always runs)

```csharp
// tests/CpuEmulator.Tests/Spectrum/SpectrumRomVariantsTests.cs
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

public class SpectrumRomVariantsTests
{
    [Fact]
    public void Discover_only_returns_present_16384_byte_roms_deterministically_ordered()
    {
        string root = Path.Combine(Path.GetTempPath(), "spec-variants-" + Guid.NewGuid().ToString("N"));
        try
        {
            string vdir = Path.Combine(root, "spectrum", "variants");
            Directory.CreateDirectory(vdir);
            File.WriteAllBytes(Path.Combine(vdir, "spec48-arabic-v1.rom"), new byte[SpectrumRom.RomLength]);
            File.WriteAllBytes(Path.Combine(vdir, "spec48.rom"),           new byte[SpectrumRom.RomLength]);
            File.WriteAllBytes(Path.Combine(vdir, "too-short.rom"),        new byte[100]);  // rejected: wrong len
            File.WriteAllBytes(Path.Combine(vdir, "notarom.bin"),          new byte[SpectrumRom.RomLength]); // not *.rom

            var found = SpectrumRomVariants.Discover(root);

            Assert.Equal(new[] { "spec48", "spec48-arabic-v1" }, found.Select(v => v.Name).ToArray()); // ordinal sort
            Assert.All(found, v => Assert.Equal(SpectrumRom.RomLength, new FileInfo(v.Path).Length));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_folds_in_the_canonical_48rom_under_the_spec48_name()
    {
        string root = Path.Combine(Path.GetTempPath(), "spec-canon-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sdir = Path.Combine(root, "spectrum");
            Directory.CreateDirectory(sdir);
            File.WriteAllBytes(Path.Combine(sdir, "48.rom"), new byte[SpectrumRom.RomLength]); // canonical only

            var found = SpectrumRomVariants.Discover(root);

            var spec48 = Assert.Single(found, v => v.Name == "spec48");
            Assert.EndsWith("48.rom", spec48.Path.Replace('\\', '/'));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_is_empty_when_nothing_is_cached()
    {
        string root = Path.Combine(Path.GetTempPath(), "spec-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Assert.Empty(SpectrumRomVariants.Discover(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

**Un-fakeable gate (Task 1):** the discovery tests run with a temp cache root and seeded files — they prove
the wrong-length file is rejected, the `*.rom`-only filter holds, the ordinal ordering is stable, the canonical
fold-in works, and the empty case is empty. These run **without any real ROM** (always green in CI). Add the new
production file + tools to the `.slnx`/csproj as the existing tools are (verify with a clean Release build).

---

## Task 2 — Recalibrate + parameterize the boot gate (variant × tier)

**File:** `tests/CpuEmulator.Tests/Spectrum/SpectrumBootTests.cs` (rewrite the single theory).

**TDD:** First raise `BootCycles` and run the *existing* canonical gate to confirm the real copyright screen
asserts green at 7M (this also captures the canonical hash). Then widen to the variant theory.

Replace the entire body of `SpectrumBootTests.cs` with:

```csharp
using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

/// <summary>The ROM-boot acceptance gate (ROADMAP: ZX Spectrum 48K). Boots a real 16 KB 48K ROM, runs it well
/// past the full power-on RAM test + screen clear (the copyright screen is painted by ≈5.9M T-states and is
/// stable by ~13M — 200k was ~30× too small and never reached it), renders the first stable frame, and asserts
/// it is the BASIC copyright screen — parameterized across every present ROM variant AND both execution tiers.
/// Skips-with-note when no ROM is cached (mirroring Klaus/ZEX gating) so ROM-free CI stays green. The structural
/// assertion (mostly-white Colors[7] paper + a black-Colors[0] ink floor) holds for every variant — including
/// the Beckman ROM's different reset sequence and the Arabic/prototype character sets — and a per-variant
/// committed RGBA hash (captured on first green run) is the tight, both-tiers-identical gate.</summary>
[Trait("Category", "UAT")]
public class SpectrumBootTests
{
    // Full boot to the copyright screen ≈ 5.9M T-states; stable by ~13M. 7M (~100 frames) is safely past the
    // (C) screen and before the unnecessary 13M. (Was 200_000 — ~30× too small; the RAM test wasn't even done.)
    private const long BootCycles = 7_000_000;

    [SpectrumRomVariantTheory]
    [MemberData(nameof(SpectrumRomVariantData.VariantTierRows), MemberType = typeof(SpectrumRomVariantData))]
    public void Rom_boots_to_the_basic_copyright_screen(string variant, string romPath, ExecutionTier tier)
    {
        // The all-absent sentinel row (see SpectrumRomVariantData): the [SpectrumRomVariantTheory] attribute
        // already Skip'd the whole theory with a note when nothing is cached; this early-return is the xUnit-v2
        // belt-and-suspenders guard (v2 has no Assert.SkipWhen) so the sentinel never asserts.
        if (romPath.Length == 0) return;

        byte[] rom = SpectrumRom.Load(romPath);
        Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
        machine.Reset();
        machine.Run(BootCycles);

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        // Un-fakeable structural invariant: every 48K ROM clears to a WHITE base paper (Colors[7], NOT bright
        // Colors[15]) and prints its copyright line in black (Colors[0]) near the bottom. Count over the inner
        // 256×192 ink area (offset by the 32px border). An empty/garbage/partial-boot screen lacks both.
        int whitePaper = 0, blackInk = 0;
        for (int y = 0; y < SpectrumUla.InkHeight; y++)
        for (int x = 0; x < SpectrumUla.InkWidth; x++)
        {
            uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
            if (p == SpectrumPalette.Colors[7]) whitePaper++;
            else if (p == SpectrumPalette.Colors[0]) blackInk++;
        }

        Assert.True(whitePaper > SpectrumUla.InkWidth * SpectrumUla.InkHeight / 2,
            $"[{variant}/{tier}] expected a mostly-white paper screen; got {whitePaper} white pixels");
        // Variant-safe floor: the canonical (C) line is ≈307 px; the Arabic/prototype/Beckman lines differ but
        // all carry well over 50 black ink pixels. (The canonical spec48 could tighten to >200 — see the
        // per-variant note below — but the shared floor stays variant-safe.)
        int inkFloor = variant == "spec48" ? 200 : 50;
        Assert.True(blackInk > inkFloor,
            $"[{variant}/{tier}] expected the black copyright text; got {blackInk} black pixels");

        // Tight gate: a per-variant committed RGBA hash of the full frame. Both tiers MUST produce the identical
        // frame for a given variant, so the hash is keyed by variant name only (not tier). On the FIRST green
        // run, capture each variant's hash (uncomment the WriteLine), paste it into ExpectedHashes, re-run.
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[boot frame hash] {variant} = {hash}");  // <-- uncomment once to capture
        if (ExpectedHashes.TryGetValue(variant, out string? expected) &&
            expected != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
        {
            Assert.Equal(expected, hash);
        }
    }

    // Per-variant committed boot-frame hashes (captured on first green run, both tiers identical). A variant with
    // a PLACEHOLDER (or absent here) skips the hash check and relies on the structural floor — so a not-yet-
    // captured variant never fails spuriously. Capture at least spec48 (canonical) definitely; the others as
    // their ROMs are present and green.
    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spec48"]            = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-arabic-v1"]  = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-arabic-v2"]  = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-arabic-v31"] = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-beckman"]    = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
            ["spec48-prototype"]  = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN",
        };

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
```

**Notes for the Builder on Task 2:**
- The old single-ROM `[SpectrumRomTheory] + [InlineData(...)]` form is replaced by the `[MemberData]` sweep. The
  old `SpectrumRomVectors.TryGetRomPath()` + `SpectrumRomTheoryAttribute` stay in the project (other tests may
  reference them; this gate no longer does).
- **Capturing hashes:** run with the variant ROMs cached, uncomment the `WriteLine`, run once with console
  output visible (`dotnet test --logger "console;verbosity=detailed"` and `[Trait("Category","UAT")]` selected),
  paste each `variant = HASH` into `ExpectedHashes`, re-comment, re-run. Capture **spec48 definitely**; the
  others as feasible (a still-PLACEHOLDER variant simply relies on the structural floor — never a spurious fail).
- **Why keyed by variant, not (variant,tier):** the diagnostic confirmed both tiers are byte-identical at
  completion, so one hash per variant covers both `[InlineData]` tiers. If a future tier divergence appears, the
  structural floor still holds and only the hash equality would flag it (correctly).
- **Runtime:** 7M cycles × (6 variants × 2 tiers) = 12 runs of ~7M Z80 T-states. This is a `[Trait("Category",
  "UAT")]` gate (the existing trait) — acceptable for a UAT sweep; it is not in the fast unit lane. If wall-time
  is a concern, Builder may gate the hash-capture sweep behind the same UAT trait it already carries (no change
  needed — the trait is already there).

---

## Task 3 — Interactive BASIC behavioral UAT (canonical ROM, both tiers)

**File:** `tests/CpuEmulator.Tests/Spectrum/SpectrumInteractiveTests.cs` (new).

**Goal:** prove boot → keyboard → BASIC interpreter → screen end-to-end. Boot the canonical `spec48` ROM fully
(past the `K` cursor), drive the keyboard matrix to enter `PRINT 2+2` and ENTER, run enough post-keystroke
frames for the ROM to evaluate + print, then assert the printed result `4` (and the `0 OK` report) appears in
the **top print region** of the screen — where before there was only blank white paper.

### The exact keystroke sequence (grounded against the shipped matrix + the 48K keyword-entry model)

The 48K BASIC editor starts each line in **`K` (keyword) command mode**: the FIRST letter key types a whole
BASIC keyword, then the cursor flips to **`L` (letter) mode** for the rest of the line. So:

| Step | Intent | KeyCode(s) (matrix half-row, bit) | Produces |
|---|---|---|---|
| 1 | keyword `PRINT ` | `P` (5,0) | `PRINT ` (+ trailing space, cursor → `L`) |
| 2 | first operand | `Digit2` (3,1) | `2` |
| 3 | the `+` operator | `SymbolShift` (7,1) **+** `K` (6,2) **chord** | `+` (SYMBOL SHIFT + K) |
| 4 | second operand | `Digit2` (3,1) | `2` |
| 5 | submit | `Enter` (6,0) | evaluate → print `4`, report `0 OK` |

The only chord is **SYMBOL SHIFT + K = `+`** (both keys held down at once, then both released). Everything else
is a single key. This is fully decode-checkable: the program is deterministic and the printed result is the
single glyph `4` on line 0, with the `0 OK, 0:1` report line near the bottom.

Each "key press" is a Down event, several frames held (so the 50 Hz ISR samples the matrix while the key is
down — the ROM's keyboard scan runs in the IM1 handler), then an Up event, then a gap before the next key (the
ROM debounces / requires the key to be released before repeat). The helper below holds each key for a generous
number of frames to be robust to the ROM's scan timing.

### The test

```csharp
using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

/// <summary>Interactive BASIC behavioral UAT on the canonical 48K ROM, both tiers: boot to the K cursor, drive
/// the real key matrix to enter `PRINT 2+2` + ENTER, then assert the printed `4` / report line appears in the
/// top print region of the screen. Proves boot → keyboard → BASIC interpreter → screen end-to-end. Skips-with-
/// note when the canonical ROM is absent.</summary>
[Trait("Category", "UAT")]
public class SpectrumInteractiveTests
{
    private const long BootCycles = 7_000_000;            // reach the K cursor (≥5.9M)
    private const long CyclesPerFrame = SpectrumUla.TStatesPerFrame; // 69888

    [SpectrumRomTheory]   // skips-with-note when 48.rom (canonical) is absent
    [InlineData(ExecutionTier.Interpreter)]
    [InlineData(ExecutionTier.Jit)]
    public void Typing_PRINT_2_plus_2_then_ENTER_prints_4(ExecutionTier tier)
    {
        byte[] rom = SpectrumRom.Load(SpectrumRomVectors.TryGetRomPath());
        Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
        machine.Reset();
        machine.Run(BootCycles); // boot to the `K` cursor

        // Baseline: capture the ink in the TOP print region (rows 0..7 = the first text line) BEFORE typing.
        // The freshly-booted screen is blank white paper there (the (C) line is near the BOTTOM), so the top
        // print rows have ~0 black ink. After PRINT 2+2 the result `4` is printed at the top → ink appears.
        int inkTopBefore = CountBlackInkInRows(ula, 0, 8);

        // PRINT (keyword P) 2 + (SymbolShift+K) 2 ENTER.
        TypeKey(machine, ula, KeyCode.P);
        TypeKey(machine, ula, KeyCode.Digit2);
        TypeChord(machine, ula, KeyCode.SymbolShift, KeyCode.K);  // '+'
        TypeKey(machine, ula, KeyCode.Digit2);
        TypeKey(machine, ula, KeyCode.Enter);

        // Let the ROM evaluate + print + emit the report line.
        RunFrames(machine, 60);

        ula.RenderInto(new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight]); // settle a final frame
        int inkTopAfter = CountBlackInkInRows(ula, 0, 8);

        // Un-fakeable: typing actually drove the interpreter to PRINT a result on the top line. A machine that
        // ignored the keystrokes (or never evaluated) leaves the top print rows blank → inkTopAfter ≈ inkTopBefore.
        Assert.True(inkTopBefore < 10,
            $"[{tier}] precondition: the top print row should start blank; got {inkTopBefore} ink px");
        Assert.True(inkTopAfter > inkTopBefore + 20,
            $"[{tier}] expected the printed result on the top line; before={inkTopBefore} after={inkTopAfter}");

        // Tight gate: a committed RGBA hash of the full post-RUN frame (both tiers identical). Captured on first
        // green run; PLACEHOLDER until then so the structural delta is the live gate.
        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[interactive frame hash] {hash}");  // <-- uncomment once to capture
        const string ExpectedHash = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN";
        if (ExpectedHash != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
            Assert.Equal(ExpectedHash, hash);
    }

    /// <summary>Count black-ink (Colors[0]) pixels in the ink-area pixel rows [yStart, yEnd) (8 rows = one text
    /// line). Renders a fresh frame first.</summary>
    private static int CountBlackInkInRows(SpectrumUla ula, int yStart, int yEnd)
    {
        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);
        int ink = 0;
        for (int y = yStart; y < yEnd; y++)
        for (int x = 0; x < SpectrumUla.InkWidth; x++)
        {
            uint p = rgba[(SpectrumUla.BorderPx + y) * SpectrumUla.FullWidth + (SpectrumUla.BorderPx + x)];
            if (p == SpectrumPalette.Colors[0]) ink++;
        }
        return ink;
    }

    /// <summary>Press one key: Down, hold several frames (so the 50 Hz ISR scans the matrix), Up, then a gap
    /// frame so the ROM sees the release before the next key.</summary>
    private static void TypeKey(Machine machine, SpectrumUla ula, KeyCode key)
    {
        ula.PostKey(new KeyEvent(KeyAction.Down, key, null));
        RunFrames(machine, 4);
        ula.PostKey(new KeyEvent(KeyAction.Up, key, null));
        RunFrames(machine, 3);
    }

    /// <summary>Press two keys as a chord (both down → hold → both up → gap). Used for SYMBOL SHIFT + K = '+'.</summary>
    private static void TypeChord(Machine machine, SpectrumUla ula, KeyCode a, KeyCode b)
    {
        ula.PostKey(new KeyEvent(KeyAction.Down, a, null));
        ula.PostKey(new KeyEvent(KeyAction.Down, b, null));
        RunFrames(machine, 4);
        ula.PostKey(new KeyEvent(KeyAction.Up, b, null));
        ula.PostKey(new KeyEvent(KeyAction.Up, a, null));
        RunFrames(machine, 3);
    }

    private static void RunFrames(Machine machine, int frames) => machine.Run(CyclesPerFrame * frames);

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
```

### How the assertion is un-fakeable

- **Precondition** `inkTopBefore < 10`: the freshly-booted screen's top text line is blank white paper (the
  copyright/(C) line is near the *bottom*), so before typing there is essentially no black ink in rows 0..7.
- **Postcondition** `inkTopAfter > inkTopBefore + 20`: after `PRINT 2+2`+ENTER, the 48K ROM prints the result
  `4` at the top of the screen (BASIC `PRINT` output starts at the top print position) — a single glyph is ≈8–15
  black ink pixels, and the cursor/echo of the entered line adds more. A machine that ignored the matrix, or
  never ran the interpreter, would leave the top rows blank and **fail** the postcondition. This cannot be faked
  without genuinely: scanning the matrix in the ISR, decoding `K`-mode `P`→`PRINT`, decoding the SYMBOL-SHIFT
  chord, evaluating `2+2`, and rasterizing `4` through the real screen bit-shuffle.
- **Committed hash** (captured on first green run): pins the exact frame, both tiers identical — the tight gate.

### Robustness fallbacks (Builder guidance if the chord proves fiddly on live silicon-accurate timing)

The SYMBOL-SHIFT + K chord is the one timing-sensitive step. If the live ROM scan does not register the chord
reliably with the frame counts above, apply these in order before changing the program:
1. **Increase hold frames** in `TypeKey`/`TypeChord` (e.g. 4→8 held, 3→5 gap). The ROM's key-repeat/debounce
   wants the key clearly down across at least one full ISR scan and clearly up before the next.
2. **If still flaky, drop the operator** and use the keyword-only program **`PRINT` `Digit4`** → prints `4`
   with no chord at all (the assertion is identical — a `4` glyph on the top line). This still exercises
   keyword-mode entry (`P`→`PRINT`), digit entry in `L` mode, ENTER, evaluation, and print. Documented here so
   the Builder has a no-chord deterministic fallback that satisfies the same un-fakeable gate.
3. Only if both fail, fall back to the simplest possible deterministic line **`PRINT 9`** (keyword `P` + `Digit9`
   + ENTER) — still proves the full pipeline; `9` is an unambiguous single glyph.

The primary program is `PRINT 2+2`→`4` (it additionally proves the arithmetic evaluator + the symbol-shift
path); the fallbacks keep the gate green if chord timing is uncooperative without weakening the end-to-end claim.

---

## Task 4 — Notes only (do NOT build here)

These are **flagged, not implemented** in this plan. Builder: do not author production code for them; surface
them as follow-on rows / cleanup when this UAT lands.

1. **`--board` surface override (optional follow-on).** `Program.cs` probes Apple → SoftCard → Spectrum → demo
   (`appleRom is not null` wins first). A live in-browser Spectrum UAT on a machine that *also* has an Apple ROM
   cached would never select the Spectrum. A `--board spectrum` (or env) override on the web surface would let a
   live UAT pin the board regardless of which ROMs are cached. **This plan's gates are headless and bypass the
   server, so they are unaffected** — the override is only needed for a future *browser* Spectrum UAT. Flag as a
   small optional follow-on row; do not build here.
2. **Tester scratch to clean up.** The Tester left scratch the Builder should remove when this lands (they are
   not part of the build and clutter `tools/`): `tools/SpectrumProbe/`, `tools/WsProbe/`, and the
   `.uat-artifacts/` directory at the repo root. (Confirmed present on `main` @ `fbd3a61`.) Remove them in this
   PR's cleanup commit or note them for the queue — owner's call; do NOT leave them indefinitely.

---

## Self-review

- **Spec coverage:** Task 1 = multi-variant cache convention + discovery helper + theory data source + fetch
  scripts (queue brief §1). Task 2 = recalibrate `BootCycles` to 7M + fix the comment + (variant × tier) theory
  with variant-safe thresholds + per-variant committed hash (§2). Task 3 = interactive BASIC UAT, canonical ROM,
  both tiers, exact keystrokes + un-fakeable on-screen assertion (§3). Task 4 = `--board` override + scratch
  cleanup, flagged not built (§4). All four queue-brief items covered.
- **Placeholder scan:** the only literal `PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN` strings are the **intended**
  capture-on-first-green hash sentinels (exactly as the shipped boot gate does it) — they are reachable-but-inert
  branches, not unfinished code. No `TBD`/`implement later`/`similar to Task N` placeholders. Every code block is
  literal and complete.
- **Type consistency:** `SpectrumRomVariants.Variant(string, string)`; `Discover` → `IReadOnlyList<Variant>`;
  `VariantTierRows()` → `IEnumerable<object[]>` of `{ string, string, ExecutionTier }`; `TryGetPath(string?
  root)` additive overload preserves the no-arg call sites; `KeyEvent(KeyAction, KeyCode, char?, bool=false)`
  used 3-arg (Ctrl defaulted, correct for the Spectrum). All signatures verified against `main`.
- **Calibration numbers:** `BootCycles = 7_000_000` (≥5.9M, <13M); white = `Colors[7]`/`0xFFD7D7D7`; black =
  `Colors[0]`/`0xFF000000`; ink floor 50 (variant-safe) / 200 (canonical tighten). All from the diagnostic.
- **xUnit MemberData-empty hazard:** explicitly handled (sentinel row + attribute-level `Skip` + an xUnit-v2
  early-return guard — the project is xUnit v2.9.3, no `Assert.SkipWhen`) so no-ROM CI does not hard-fail on
  "no data found."
- **Scope:** 48K only; no 128/+2/+3. No production behavior change beyond the additive `TryGetPath` overload +
  the new `SpectrumRomVariants` helper + the two fetch scripts; the rest is tests. The web surface is untouched.

---

## Builder readiness

All literal code is grounded against `main` @ `fbd3a61`. The Builder should:
1. Run `tools/get-spectrum-rom-variants.{sh,ps1}` (after creating them) to copy the owner's six ROMs from
   `D:/prj/zx-roms/spectrum16-48/` into `<cache>/spectrum/variants/`, and ensure the canonical `48.rom` is
   cached (`tools/get-spectrum-rom`).
2. Implement Task 1 → 2 → 3 (TDD each), capture the per-variant + interactive hashes on first green run, paste
   them in, re-run.
3. Confirm the full suite is green + warning-clean (Release, whole solution), with the new UAT gates green when
   the ROMs are cached and skip-with-note when absent.
4. Do the Task 4 scratch cleanup (or queue it). Do **not** build the `--board` override.
5. This project is **xUnit v2.9.3** — use the early-return form (`if (romPath.Length == 0) return;`) for the
   sentinel row; there is no `Assert.SkipWhen` in v2.
