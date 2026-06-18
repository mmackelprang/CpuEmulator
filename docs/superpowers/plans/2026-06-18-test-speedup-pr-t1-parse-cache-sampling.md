# PR-T1 — Parse cache + bounded parsing + sampling unification (levers 1 & 7)

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans`, task-by-task. Steps use checkbox (`- [ ]`) syntax.
> **Read first (binding):** `2026-06-18-test-speedup-arc-overview.md` (sequencing, the two shared gates, the
> gating policy). This PR lands AFTER M6 PR-1 merges, on a branch `test/speedup-parse-cache` → PR → main.
> **NO production code** (`src/`) changes in this PR — test infrastructure only.

**Goal:** Stop every TomHarte loader from decompressing + JSON-parsing the ENTIRE 8k–10k-case file when only
`sampleSize` cases run, and stop re-parsing the same file once per sweep (interpreter / JIT / each 68000 axis
class). Add a path-keyed parse cache + bounded parsing, unify the four duplicated sample resolvers, and lower the
routine default from 200 → 100.

**Architecture:** A single new static helper `TomHarteParseCache` holds a `ConcurrentDictionary<string, …>`
keyed by file path, storing the parsed case list AND the high-water sample size it was parsed to. Each loader's
`LoadFile` gains a `maxCases` parameter and early-stops enumeration at `maxCases`. Each sweep asks the cache for
`ResolveSampleSize()` cases; the cache parses only that many (or returns a wider cached list). The four sample
resolvers collapse to one shared `TomHarteSampling.ResolveSampleSize()`.

**Tech Stack:** C#, xUnit, `System.Text.Json` `JsonDocument`, `System.Collections.Concurrent`.

---

## What the recon CONFIRMED (file:line — verified against `main` @ `896f88b`)

| # | Fact | Evidence |
|---|------|----------|
| C1 | Every loader fully enumerates the file (no early-stop) | `M8088TomHarteCase.cs:114-123`; `TomHarteCase.cs:39-53`; `Z80TomHarteCase.cs`/`M68000TomHarteCase.cs` `LoadFile` same shape |
| C2 | The 8088 loader parses carried-not-asserted cycle tuples on EVERY case | `M8088TomHarteCase.cs:210-225` (`ReadCycles`) |
| C3 | Sample cap is duplicated: centralized helpers (68000 `M68000TomHarteVectors.cs:34-38`, 8088 `M8088TomHarteVectors.cs:37-41`, Z80 JIT `Z80JitTomHarteTests.cs:19-23`) AND inline (6502 `Mos6502TomHarteTests.cs:57-60` + `Mos6502JitTomHarteTests.cs:28-31`, Z80 interp `Z80TomHarteTests.cs:117-120`) | grep, all default 200 |
| C4 | Same file is parsed once per sweep — interpreter, JIT, and 5–8 68000 axis classes each call `LoadFile(path)` | all `*Tests.cs` call `…Loader.LoadFile(path)` then loop with their own `if (run >= sampleSize) break;` |
| C5 | `CPUEMULATOR_UAT=full` ⇒ `int.MaxValue` (full per-file sweep); the exhaustive gate | `*TomHarteVectors.cs:36`, inline resolvers |

**Design consequence of C4 + C5:** the cache must be keyed by path and remember the high-water sample size it
parsed to. A `sampleSize=100` caller parses 100 and caches `(list=100 cases, water=100)`; a later `sampleSize=100`
caller on the same path hits the cache; a `CPUEMULATOR_UAT=full` caller (`int.MaxValue`) re-parses to full and
upgrades the cache. This gives both "parse only what's needed" and "reuse across sweeps" with no correctness risk.

---

## File structure

- **Create** `tests/CpuEmulator.Tests/TomHarte/TomHarteSampling.cs` — the one shared `ResolveSampleSize()`.
- **Create** `tests/CpuEmulator.Tests/TomHarte/TomHarteParseCache.cs` — the generic path-keyed parse cache.
- **Modify** the four loaders' `LoadFile` to take `int maxCases` and early-stop:
  `TomHarteCase.cs`, `Z80TomHarteCase.cs`, `M68000TomHarteCase.cs`, `M8088TomHarteCase.cs`.
- **Modify** the four resolver sites to delegate to `TomHarteSampling`:
  `M68000TomHarteVectors.cs`, `M8088TomHarteVectors.cs`, `Z80JitTomHarteTests.cs` (its private resolver),
  and the two inline resolvers in `Mos6502TomHarteTests.cs` / `Mos6502JitTomHarteTests.cs` / `Z80TomHarteTests.cs`.
- **Modify** every sweep call site to fetch cases via the cache at the resolved sample size.
- **Add (lever 1 sub-fix)** a `parseCycles` flag to the 8088 loader so the data-axis sweeps skip `ReadCycles`.

---

## Task 1: Unify the sample resolver (lever 7 — single source, default 100)

**Files:**
- Create: `tests/CpuEmulator.Tests/TomHarte/TomHarteSampling.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/TomHarte/TomHarteSamplingTests.cs`:

```csharp
using System;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class TomHarteSamplingTests
{
    [Fact]
    public void Default_is_100_when_no_env_set()
    {
        Assert.Equal(100, TomHarteSampling.ResolveSampleSize(uat: null, sample: null));
    }

    [Fact]
    public void Uat_full_is_unbounded()
    {
        Assert.Equal(int.MaxValue, TomHarteSampling.ResolveSampleSize(uat: "full", sample: null));
    }

    [Fact]
    public void Explicit_sample_overrides_default()
    {
        Assert.Equal(200, TomHarteSampling.ResolveSampleSize(uat: null, sample: "200"));
    }

    [Fact]
    public void Non_positive_or_garbage_sample_falls_back_to_default()
    {
        Assert.Equal(100, TomHarteSampling.ResolveSampleSize(uat: null, sample: "0"));
        Assert.Equal(100, TomHarteSampling.ResolveSampleSize(uat: null, sample: "xyz"));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~TomHarteSamplingTests" --no-restore`
Expected: FAIL — `TomHarteSampling` does not exist (compile error).

- [ ] **Step 3: Write the helper**

Create `tests/CpuEmulator.Tests/TomHarte/TomHarteSampling.cs`:

```csharp
using System;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>The ONE per-file case-sample resolver for every TomHarte sweep (6502/Z80/680x0/8088, interpreter and
/// JIT). Replaces the five duplicated copies (the three *TomHarteVectors.cs / Z80JitTomHarteTests.cs helpers and
/// the two inline 6502/Z80-interp resolvers). Routine/CI caps the per-file case loop at CPUEMULATOR_TOMHARTE_SAMPLE
/// (default 100 — lowered from 200 in PR-T1, lever 7); CPUEMULATOR_UAT=full removes the cap (int.MaxValue) so the
/// authoritative milestone gate runs the full per-file sweep. Caps the per-file case loop ONLY — it does NOT change
/// which files run, which cases are deferred/filtered, or what is asserted.</summary>
internal static class TomHarteSampling
{
    /// <summary>The routine-path default. Lowered 200 → 100 (lever 7): a 2x faster fast path; the exhaustive
    /// gate is still CPUEMULATOR_UAT=full.</summary>
    public const int DefaultSample = 100;

    /// <summary>Reads the two env vars and resolves the cap. Public per-arg overload (no env read) so it is unit
    /// testable without mutating process-global env (which would race the parallel vector-gated theories).</summary>
    public static int ResolveSampleSize(string? uat, string? sample)
    {
        if (uat == "full") return int.MaxValue;
        return int.TryParse(sample, out int p) && p > 0 ? p : DefaultSample;
    }

    public static int ResolveSampleSize() => ResolveSampleSize(
        Environment.GetEnvironmentVariable("CPUEMULATOR_UAT"),
        Environment.GetEnvironmentVariable("CPUEMULATOR_TOMHARTE_SAMPLE"));
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~TomHarteSamplingTests" --no-restore`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/TomHarteSampling.cs tests/CpuEmulator.Tests/TomHarte/TomHarteSamplingTests.cs
git commit -m "test(speedup): unify TomHarte sample resolver, default 100 (lever 7)"
```

---

## Task 2: Point the five resolver sites at the shared helper

**Files (Modify):**
- `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectors.cs:34-38`
- `tests/CpuEmulator.Tests/TomHarte/M8088TomHarteVectors.cs:37-41`
- `tests/CpuEmulator.Tests/TomHarte/Z80JitTomHarteTests.cs:19-23`
- `tests/CpuEmulator.Tests/TomHarte/Mos6502TomHarteTests.cs:57-60`
- `tests/CpuEmulator.Tests/TomHarte/Mos6502JitTomHarteTests.cs:28-31`
- `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs:117-120`

- [ ] **Step 1: Centralized helpers → delegate.** In `M68000TomHarteVectors.cs`, replace the body of
  `ResolveSampleSize()` (`:34-38`):

```csharp
    public static int ResolveSampleSize() => TomHarteSampling.ResolveSampleSize();
```

Apply the identical one-line delegation to `M8088TomHarteVectors.cs:37-41`'s `ResolveSampleSize()` and to the
private resolver in `Z80JitTomHarteTests.cs:19-23` (keep its method name/signature; just delegate the body).

- [ ] **Step 2: Inline resolvers → call the shared helper.** In `Mos6502TomHarteTests.cs`, replace `:57-60`:

```csharp
        int sampleSize = TomHarteSampling.ResolveSampleSize();
```

(deleting the local `uatFull` + the inline `int.TryParse` block). Apply the identical replacement to
`Mos6502JitTomHarteTests.cs:28-31` and `Z80TomHarteTests.cs:117-120`. In `Z80TomHarteTests.cs` keep the separate
`registersOnly` env read (`:121`) — it is unrelated.

- [ ] **Step 3: Build + run the affected sweeps at a fixed sample to prove behaviour is unchanged**

Run: `CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~TomHarte" --no-restore`
Expected: PASS (green; the explicit `=200` makes the default change a no-op so this proves only the delegation).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000TomHarteVectors.cs \
        tests/CpuEmulator.Tests/TomHarte/M8088TomHarteVectors.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80JitTomHarteTests.cs \
        tests/CpuEmulator.Tests/TomHarte/Mos6502TomHarteTests.cs \
        tests/CpuEmulator.Tests/TomHarte/Mos6502JitTomHarteTests.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs
git commit -m "test(speedup): route all 6 sample-cap sites through TomHarteSampling (lever 7)"
```

---

## Task 3: Bound the loader enumeration — `LoadFile(path, maxCases)`

Add early-stop to each loader so parsing stops at `maxCases`. This is the bulk of lever 1's win even before the
cache (a single sweep over one file goes from parsing 10,000 cases to parsing `sampleSize`).

**Files (Modify):** `TomHarteCase.cs`, `Z80TomHarteCase.cs`, `M68000TomHarteCase.cs`, `M8088TomHarteCase.cs`.

- [ ] **Step 1: 6502 loader.** In `TomHarteCase.cs`, replace `LoadFile` (`:39-53`):

```csharp
    public static List<TomHarteCase> LoadFile(string path, int maxCases = int.MaxValue)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var cases = new List<TomHarteCase>(Math.Min(maxCases, 10_000));
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (cases.Count >= maxCases) break;
            cases.Add(new TomHarteCase(
                element.GetProperty("name").GetString()!,
                ReadState(element.GetProperty("initial")),
                ReadState(element.GetProperty("final")),
                [.. element.GetProperty("cycles").EnumerateArray().Select(ReadCycle)]));
        }
        return cases;
    }
```

(Add `using System;` at the top of the file if not already present — `Math.Min`.)

- [ ] **Step 2: 8088 loader + skip carried cycle parsing.** In `M8088TomHarteCase.cs`, replace `LoadFile`
  (`:114-123`) and thread a `parseCycles` flag into `Parse` so the data-axis sweeps never run `ReadCycles`:

```csharp
    public static List<M8088TomHarteCase> LoadFile(string path, int maxCases = int.MaxValue,
                                                   bool parseCycles = false)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);   // the gzip path (shared with 680x0)
        using var doc = JsonDocument.Parse(gz);
        var cases = new List<M8088TomHarteCase>(capacity: Math.Min(maxCases, 1024));
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (cases.Count >= maxCases) break;
            cases.Add(Parse(element, parseCycles));
        }
        return cases;
    }
```

Then change `Parse` (`:125`) to accept the flag and pass it to `ReadCycles`:

```csharp
    public static M8088TomHarteCase Parse(JsonElement element, bool parseCycles = false)
    {
        // ... bytes / hash / idx unchanged ...
        return new M8088TomHarteCase(
            element.GetProperty("name").GetString()!,
            bytes,
            ReadState(element.GetProperty("initial")),
            ReadState(element.GetProperty("final")),
            parseCycles ? ReadCycles(element) : System.Array.Empty<M8088Cycle>(),
            hash,
            idx);
    }
```

(`ReadCycles` at `:210-225` is unchanged; it simply is not called on the data-axis path. The data-axis runner
already ignores `Cycles` — verified in the loader doc comment `:108-109` and `M8088TomHarteRunner.cs:11-13`. The
TIMING-axis sweep, if/when it asserts cycles, calls `LoadFile(path, maxCases, parseCycles: true)`.)

- [ ] **Step 3: Z80 + 68000 loaders.** Apply the identical `int maxCases = int.MaxValue` parameter +
  `if (cases.Count >= maxCases) break;` guard to `Z80TomHarteCase.cs`'s `LoadFile` and
  `M68000TomHarteCase.cs`'s `LoadFile`. (Both have the same `foreach (… EnumerateArray()) cases.Add(Parse(…))`
  shape as the 8088 loader; do NOT add `parseCycles` to these — only the 8088 carries the heavy tuple.)

- [ ] **Step 4: Build (call sites still pass no args → default `int.MaxValue` → behaviour unchanged)**

Run: `dotnet build tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --no-restore`
Expected: build succeeds, 0 warnings (existing call sites use the defaults).

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/TomHarteCase.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80TomHarteCase.cs \
        tests/CpuEmulator.Tests/TomHarte/M68000TomHarteCase.cs \
        tests/CpuEmulator.Tests/TomHarte/M8088TomHarteCase.cs
git commit -m "test(speedup): bound TomHarte LoadFile to maxCases + skip carried 8088 cycles (lever 1)"
```

---

## Task 4: The path-keyed parse cache

**Files:**
- Create: `tests/CpuEmulator.Tests/TomHarte/TomHarteParseCache.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/TomHarte/TomHarteParseCacheTests.cs`:

```csharp
using System.Collections.Generic;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class TomHarteParseCacheTests
{
    [Fact]
    public void Second_request_at_same_or_smaller_size_does_not_reparse()
    {
        int parses = 0;
        var cache = new TomHarteParseCache<int>();
        List<int> Parse(int max) { parses++; var l = new List<int>(); for (int i = 0; i < max; i++) l.Add(i); return l; }

        var a = cache.Get("k", 100, Parse);
        var b = cache.Get("k", 100, Parse);
        var c = cache.Get("k", 50, Parse);   // smaller — served from the cached 100

        Assert.Equal(1, parses);             // parsed ONCE
        Assert.Equal(100, a.Count);
        Assert.Same(a, b);                   // same backing list returned
        Assert.Equal(100, c.Count);          // smaller request still returns the wider cached list (caller caps)
    }

    [Fact]
    public void Larger_request_reparses_and_upgrades_the_high_water_mark()
    {
        int parses = 0;
        var cache = new TomHarteParseCache<int>();
        List<int> Parse(int max) { parses++; var l = new List<int>(); for (int i = 0; i < max; i++) l.Add(i); return l; }

        cache.Get("k", 100, Parse);
        var big = cache.Get("k", 500, Parse); // larger — re-parses to 500
        cache.Get("k", 200, Parse);           // now served from the cached 500

        Assert.Equal(2, parses);              // 100 then 500; the 200 is a hit
        Assert.Equal(500, big.Count);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~TomHarteParseCacheTests" --no-restore`
Expected: FAIL — `TomHarteParseCache` does not exist.

- [ ] **Step 3: Write the cache**

Create `tests/CpuEmulator.Tests/TomHarte/TomHarteParseCache.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>A path-keyed parse cache for the TomHarte loaders (lever 1). The SAME vector file is parsed once per
/// sweep today — the interpreter sweep, the JIT sweep, and (on the 680x0) up to ~5-8 axis classes each call
/// LoadFile(path). This cache parses a path ONCE (to the requested sample size) and reuses the parsed list across
/// all sweeps in the run. Keyed by path; stores the high-water sample size it was parsed to, so a later request
/// for MORE cases (e.g. CPUEMULATOR_UAT=full → int.MaxValue) re-parses to the larger size and upgrades the entry,
/// while a request for the same-or-fewer cases is a pure hit (the caller's own `if (run >= sampleSize) break;`
/// loop caps a wider cached list down to its sample — verified: every sweep already has that loop).
///
/// Thread-safe (the sweeps run in parallel collections): a ConcurrentDictionary holds a per-path lock object so
/// two threads racing the SAME path parse once, not twice; different paths never contend.</summary>
internal sealed class TomHarteParseCache<TCase>
{
    private sealed class Entry { public List<TCase>? Cases; public int Water; }

    private readonly ConcurrentDictionary<string, Entry> _byPath = new();

    /// <summary>Return at least <paramref name="maxCases"/> parsed cases for <paramref name="path"/> (or the whole
    /// file if it has fewer), parsing via <paramref name="parse"/> only when the cache cannot satisfy the request.
    /// The returned list may be WIDER than maxCases (a cached larger parse) — the caller caps with its own loop.</summary>
    public List<TCase> Get(string path, int maxCases, Func<int, List<TCase>> parse)
    {
        var entry = _byPath.GetOrAdd(path, static _ => new Entry());
        lock (entry)
        {
            if (entry.Cases is not null && entry.Water >= maxCases)
                return entry.Cases;
            // Cache miss or the cached parse is too small → parse to the requested size and upgrade.
            entry.Cases = parse(maxCases);
            entry.Water = entry.Cases.Count < maxCases ? maxCases : maxCases; // water = requested cap (file may be shorter)
            return entry.Cases;
        }
    }
}

/// <summary>The four concrete singletons (one per case type), so a sweep just calls the cache for its CPU.</summary>
internal static class TomHarteCaches
{
    public static readonly TomHarteParseCache<TomHarteCase> Mos6502 = new();
    public static readonly TomHarteParseCache<Z80TomHarteCase> Z80 = new();
    public static readonly TomHarteParseCache<M68000TomHarteCase> M68000 = new();
    public static readonly TomHarteParseCache<M8088TomHarteCase> M8088 = new();
}
```

> **Note on `Water`:** when the file is shorter than `maxCases` the parse returns fewer than `maxCases`, but we
> still set `Water = maxCases` so a later equal request is a hit (we already proved the file can't yield more).
> The ternary is written explicitly for that reading; both arms equal `maxCases` by design.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~TomHarteParseCacheTests" --no-restore`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/TomHarteParseCache.cs tests/CpuEmulator.Tests/TomHarte/TomHarteParseCacheTests.cs
git commit -m "test(speedup): add path-keyed TomHarte parse cache (lever 1)"
```

---

## Task 5: Route the sweep call sites through the cache

Each sweep currently does `var cases = …Loader.LoadFile(path);` then loops with `if (run >= sampleSize) break;`.
Resolve the sample FIRST, then ask the cache.

**Files (Modify):** every `*Tests.cs` sweep — the representative edits below; apply the same shape to each.

- [ ] **Step 1: 6502 interpreter sweep.** In `Mos6502TomHarteTests.cs`, the body becomes (replacing the
  `LoadFile` + sample lines so the sample is resolved before the cache call):

```csharp
        int sampleSize = TomHarteSampling.ResolveSampleSize();
        var cases = TomHarteCaches.Mos6502.Get(path, sampleSize,
            max => TomHarteLoader.LoadFile(path, max));
```

(The existing `foreach (var testCase in cases) { if (run >= sampleSize) break; … }` loop is unchanged — it caps
a possibly-wider cached list to `sampleSize`.)

- [ ] **Step 2: 8088 data-axis sweeps.** Each 8088 data-axis sweep (`M8088MovTomHarteTests.cs`,
  `M8088AluTomHarteTests.cs`, `M8088ShiftStackMiscTomHarteTests.cs`, `M8088ControlStringsIntTomHarteTests.cs`,
  `M8088JitTomHarteTests.cs`) replaces its `var cases = M8088TomHarteLoader.LoadFile(path);` with:

```csharp
        int sampleSize = M8088TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M8088.Get(path, sampleSize,
            max => M8088TomHarteLoader.LoadFile(path, max, parseCycles: false));
```

(`parseCycles: false` is the data axis — lever 1's carried-tuple skip. If a timing-axis sweep is added later it
passes `parseCycles: true`, which makes it a DIFFERENT cache need; if both axes ever run in one process, key the
8088 cache on `(path, parseCycles)` — out of scope here, all current 8088 sweeps are data-axis.)

- [ ] **Step 3: 68000 sweeps (the biggest win — 5–8 classes share each file).** Each 68000 sweep
  (`M68000TomHarteTests.cs`, `M68000AluTomHarteTests.cs`, `M68000M45cTomHarteTests.cs`,
  `M68000M45d1TomHarteTests.cs`, `M68000ExceptionCorpusTomHarteTests.cs`, `M68000TimingAxisTomHarteTests.cs`,
  `M68000TimingReconTests.cs`, `M68000JitTomHarteTests.cs`) replaces `var cases = M68000TomHarteLoader.LoadFile(path);`:

```csharp
        int sampleSize = M68000TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M68000.Get(path, sampleSize,
            max => M68000TomHarteLoader.LoadFile(path, max));
```

(Where the local was named `sample` rather than `sampleSize`, keep the local name; only the right-hand side changes.)

- [ ] **Step 4: Z80 sweeps.** `Z80TomHarteTests.cs` and `Z80JitTomHarteTests.cs` replace their `LoadFile`:

```csharp
        var cases = TomHarteCaches.Z80.Get(path, sampleSize,
            max => Z80TomHarteLoader.LoadFile(path, max));
```

(Resolve `sampleSize` via `TomHarteSampling.ResolveSampleSize()` / the Z80-JIT resolver BEFORE this line — both
already compute it; just move the computation above the cache call.)

- [ ] **Step 5: Full coverage-parity build + run at a FIXED sample**

Run: `CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~TomHarte" --no-restore`
Expected: PASS, identical green to baseline. Capture the per-sweep `ran R, executed E…` output lines.

- [ ] **Step 6: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/*.cs
git commit -m "test(speedup): route all TomHarte sweeps through the parse cache (lever 1)"
```

---

## Task 6: MEASUREMENT GATE (prove the speedup)

- [ ] **Step 1: Baseline (BEFORE — run on `main`, captured at PR start).** On `main` @ the PR base, record:

```bash
git stash || true
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~M68000" --no-restore 2>&1 | tee /tmp/t1-before.txt
git stash pop || true
```

Record the `dotnet test` total elapsed line. (Pick the 68000 family for the subset — it is the worst case: many
axis classes re-parse the same files, so it shows the cache win most.)

- [ ] **Step 2: After.** On the PR branch with all of T1 applied:

```bash
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~M68000" --no-restore 2>&1 | tee /tmp/t1-after.txt
```

- [ ] **Step 3: Record in the PR body.** Table: `subset | before | after | speedup`. **Gate: after < before**,
  target **≥ 5×** on the 68000 subset at `=200` (the multi-class re-parse elimination + bounded parse). Note the
  reduced peak working set if captured.

---

## Task 7: COVERAGE-PRESERVATION GATE

- [ ] **Step 1: Executed-count parity (levers 1 — zero coverage cost).** At a FIXED sample, the `executed` /
  `deferred` / `excluded` counts MUST be byte-identical before↔after. Diff the captured output lines:

```bash
grep -hoE "ran [0-9]+, executed [0-9]+.*" /tmp/t1-before.txt | sort > /tmp/before-counts.txt
grep -hoE "ran [0-9]+, executed [0-9]+.*" /tmp/t1-after.txt  | sort > /tmp/after-counts.txt
diff /tmp/before-counts.txt /tmp/after-counts.txt && echo "COUNTS IDENTICAL"
```

Expected: `COUNTS IDENTICAL` (the cache + bounded parse change WHEN bytes are parsed, never WHICH cases run).

- [ ] **Step 2: Document the lever-7 coverage delta (default 200 → 100).** In the PR body, state explicitly: the
  routine fast path now asserts 100 cases/file instead of 200 — a deliberate halving of the *sampled* fast-path
  coverage, NOT a change to total coverage. The exhaustive gate is unchanged.

- [ ] **Step 3: Prove the full gate is still reachable.** Run ONE file at full to confirm `int.MaxValue` still
  parses the whole file through the cache:

```bash
CPUEMULATOR_UAT=full dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~Mos6502TomHarteSweepBase" --no-restore 2>&1 | tail -20
```

Expected: PASS, and the `ran R` value reflects the full per-file count (10,000), proving the cache upgrades to
the full parse under `UAT=full`.

- [ ] **Step 4: Final full-suite green at the new default**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --no-restore`
Expected: PASS.

- [ ] **Step 5: Commit any gate fixes, then open the PR**

```bash
git commit -am "test(speedup): T1 measurement + coverage gates green" || true
```

PR body MUST include: the measurement table, the `COUNTS IDENTICAL` proof, the explicit 200→100 coverage-delta
note, and the `UAT=full` reachability proof. **Docs Impact:** none (test infra only).

---

## Self-review (run before opening the PR)

- **Spec coverage:** lever 1 = Tasks 3–5 (bounded parse + carried-tuple skip + cache); lever 7 = Tasks 1–2 (unified
  resolver + default 100). ✔
- **Placeholder scan:** no TBD/TODO; every code step shows literal code. ✔
- **Type consistency:** `LoadFile(path, maxCases)` / `LoadFile(path, maxCases, parseCycles)` signatures match
  between Task 3 (definition) and Task 5 (call sites); `TomHarteParseCache<TCase>.Get(path, maxCases, parse)` matches
  between Task 4 and Task 5; `TomHarteSampling.ResolveSampleSize()` matches Task 1 ↔ Task 2. ✔
