# Parallelize the TomHarte single-step sweep — cut the `dotnet test -c Release` wall-clock

**Date:** 2026-06-16
**Kind:** test-infrastructure (no production / emulator-semantics change)
**Author:** Planner (dispatched, non-interactive)
**Status:** PLAN — ready for Builder
**Branch policy:** docs may land straight to main (this file). The implementation is a TEST-code change → it goes on a
branch (`feat/parallelize-tomharte-sweep` or similar) and merges via PR, per the global workflow rule.

---

## 1. Spec

### Problem
`dotnet test -c Release` ran **~33 min** today. The dominant cost is the 680x0 TomHarte single-step sweep: ~6 test
classes, each a `[Theory]` over a list of mnemonic-keyed vector files, each file ~**8,065 cases** run through the real
`Step`/diff runner. This wall-clock is now on the critical path of three upcoming workstreams that each re-run the full
68000 sweep:
- **M4.5d-2b** — the cycle-exact timing gate (re-runs the PC/prefetch + trace sweep).
- **M4.6** — the JIT tier-parity sweep (re-runs every data-axis family file *through* `JittedCpu<M68000Cpu>`, doubling
  the 68000 case volume).
- the full timing sweep (`M68000TimingAxisTomHarteTests`, the heaviest per-case axis).

A 33-min gate that every future 68000 PR pays is a standing tax. Cutting it speeds every subsequent cycle.

### Root cause (measured, not assumed)
The suite runs on **xUnit v2 (2.9.3) defaults**: there is no `xunit.runner.json` and no parallelization attributes in
`tests/CpuEmulator.Tests/`. xUnit v2's default behavior is:
- **Test collections parallelize against each other.** Each test *class* with no explicit `[Collection]` is its own
  implicit collection.
- **Theory rows WITHIN one class/collection run SERIALLY** — and **xUnit v2 has no method-level parallelism at all**
  (it is a v3 feature). `maxParallelThreads` cannot split a single class's rows across threads.

So the 68000 sweep's parallelism is capped at **the number of 68000 test classes (~6)**, not the number of rows (~240)
and certainly not the 32 cores. The single slowest class is the long pole. Concretely, the heaviest classes are:

| Class | Rows (files) | Per-row work | Notes |
|---|---|---|---|
| `M68000AluTomHarteTests` | 51 | 8,065 cases, data axis | runs **all 51 serially on one thread** |
| `M68000ExceptionCorpusTomHarteTests` | 51 | 8,065 loaded, only exception cases asserted | still loads + scans every case |
| `M68000M45cTomHarteTests` | 42 | 8,065 cases, data axis | |
| `M68000TimingAxisTomHarteTests` | 63 (22 + 41) | 8,065 cases, **PC/prefetch axis** (heaviest per-case) | the long pole candidate |
| `M68000M45d1TomHarteTests` | 22 | 8,065 cases, data axis + exceptions | |
| `M68000TomHarteTests` (MOVE) | 10 | 8,065 cases, data axis | |

(The 6502 `Mos6502TomHarteTests` / `Mos6502JitTomHarteTests` and Z80 `Z80TomHarteTests` / `Z80JitTomHarteTests` sweeps
are **one row per opcode** — ~150–256 rows each, **sampled to 200 cases/row** unless `CPUEMULATOR_UAT=full`. They are
NOT the long pole at CI scale, but they ARE many short rows that benefit from the same fix and must stay byte-identical.)

With ~6 classes on a 32-core box, ~26 cores sit idle while the slowest class grinds through its rows one at a time.

### Goal
Cut `dotnet test -c Release` wall-clock by spreading the TomHarte rows across the 32 cores **without changing a single
pass/fail outcome**. Baseline to preserve EXACTLY: today's clean gate was **Passed 5613, Failed 0, Skipped 0**. The
parallelized suite MUST reproduce byte-identical totals, zero new skips, and zero flakes.

### Non-goals
- NOT changing any emulator/production code (`src/**`). This PR touches only `tests/CpuEmulator.Tests/**` and adds one
  config file.
- NOT changing what any test asserts, the case lists, the sampling defaults, or the skip-when-absent discipline.
- NOT writing an ADR. The parallelization model is test-infra, not a cross-cutting architecture decision (it does not
  change any data model, API shape, or emulator contract). If the owner disagrees, see Open Questions Q4.
- NOT touching the standing "heavy gates run SEQUENTIAL under `-c Release`" rule — that forbids running multiple
  `dotnet test` *invocations* at once; it does NOT forbid intra-invocation xUnit parallelism, which is exactly what we
  add here.
- NOT chasing the 6502/Z80 sweeps' sample size or the JIT fuzzer's `N=64` — those are unchanged. (The 6502/Z80
  sweeps ARE split into per-file classes per the resolved scope in §7.2 — but their case lists/sampling are untouched.)

---

## 2. Chosen approach + rationale

### The mechanism: split the dominant classes into per-file collections + a tuned `xunit.runner.json`

Because xUnit v2 **cannot** parallelize theory rows within a class, raising `maxParallelThreads` alone does nothing for
the long pole. The unit of parallelism must become finer than "one class." Two ways to do that:

- **(A) Class-split — partition each dominant `[Theory]` into many `[Collection]`-distinct classes** so xUnit's
  existing collection-parallelism distributes them. Pure config + test-structure change, no new dependency, fully
  within xUnit v2 semantics. **CHOSEN.**
- **(B) Add `Meziantou.Xunit.ParallelTestFramework`** — a drop-in custom test framework that adds method-/theory-level
  parallelism via one assembly attribute. One line, parallelizes *everything* including theory rows. **Runner-up** (see
  Open Questions Q1) — rejected as the default only because it adds a third-party test-framework dependency to a project
  that currently has none, and the owner has not blessed that. It is the better answer if the owner is fine with the dep.

**DEFAULT = (A) class-split + `xunit.runner.json`,** for these reasons:
- Zero new dependencies; stays on the xUnit version already pinned.
- Deterministic and transparent — the parallelism boundary is visible in the test source.
- It is the standard idiomatic xUnit-v2 remedy for exactly this "one giant theory class" shape.

#### What "class-split" means concretely

The single biggest lever is the heaviest classes. We split each dominant `[Theory]` so that **each vector file becomes
its own test class in its own implicit collection**, but WITHOUT hand-writing 240 classes. The clean idiom is a small
**generic base class + one tiny derived class per file-group**, or — simpler and lower-risk — split each monolith into
**N partition classes** (e.g. 8 partitions per dominant sweep), each carrying a disjoint slice of the file list. With
~6 sweeps × up to 8 partitions = ~48 collections, xUnit saturates 32 cores. Partitioning (not one-class-per-file) keeps
the diff small, keeps each class's `[MemberData]` shape identical, and avoids 240 near-duplicate files.

The recommended granularity is **per-file collections via a shared base** — it gives the finest parallelism (240
collections, each ~8,065 cases ≈ the same size, so the load balances cleanly) with the least bespoke partition logic.
Both are spelled out in the tasks; **DEFAULT = per-file collection via a shared abstract base** (Task 2), with the
N-partition variant documented as the fallback if the base-class refactor proves noisy (Open Questions Q2).

#### `xunit.runner.json`

```json
{
  "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json",
  "parallelizeTestCollections": true,
  "maxParallelThreads": 24,
  "preEnumerateTheories": true,
  "diagnosticMessages": false
}
```

Rationale for each key:
- `parallelizeTestCollections: true` — explicitly on (it is the v2 default, but pinning it documents intent and guards
  against a future default change). This is what lets the now-many collections run concurrently.
- `maxParallelThreads: 24` — **NOT 32.** The per-row work is gzip-decompress (`GZipStream`) + JSON parse of an ~8 MB
  decompressed document into ~8,065 records + 8,065 `Step`/diff runs. That is allocation-heavy → GC pressure, plus
  transient memory: the 68000 runner allocates a fresh **16 MiB** `byte[0x1000000]` address space PER CASE
  (`M68000TomHarteRunner.RunCase`). At 32 concurrent rows each holding multi-MB live JSON case lists + a 16 MiB arena,
  peak working set and GC churn can *erase* the parallel win (the documented "I/O/GC thrash" failure mode). A cap of
  **~75% of cores (24)** leaves headroom for the GC's background threads and the JIT/runtime, which empirically gives
  the best throughput on allocation-bound suites. This is a tuning knob — see the validation contract (Task 4) which
  has Builder sweep {16, 24, 32} and keep the fastest that stays green. **DEFAULT to commit: 24** unless the sweep shows
  a clear winner.
- `preEnumerateTheories: true` — keeps the per-row test-case identity stable (each row is a discrete test case), which
  preserves the exact 5613 reported-test count and gives the scheduler the finest granularity to balance.
- `diagnosticMessages: false` — keep the log clean; flip to true only when debugging a flake.

### Expected speedup + Amdahl ceiling

- **Amdahl bound:** after the split, the longest single *non-splittable* unit is **one vector file = 8,065 cases on one
  thread** (a single theory row still runs serially — we cannot split below a row in v2). With ~240 roughly equal-sized
  68000 rows distributed over 24 threads, the 68000 portion drops to roughly `ceil(240 / 24) ≈ 10` sequential
  row-times plus scheduling tail, versus ~6-classes-deep today. The serial floor is **one row-time** (~the cost of
  8,065 cases of the heaviest axis).
- **Realistic estimate:** the 68000 sweep is the bulk of the 33 min. Going from ~6-way to ~24-way effective parallelism
  on the dominant work, discounted for GC/memory contention and the non-68000 remainder, should land the full
  `dotnet test -c Release` in the **~6–11 min** range — a **3–5× wall-clock reduction**. This is an estimate; Task 4's
  before/after measurement is the source of truth, and the number above is explicitly NOT a gate (the gate is
  "byte-identical green + measurably faster").
- **Diminishing returns past ~24 threads** are expected precisely because of the per-case 16 MiB arena allocation; this
  is why the cap is a tuned knob, not "all cores."

---

## 3. Isolation-hazard analysis (the correctness gate)

Parallelizing is only safe if no two now-concurrent tests share mutable state or ordering assumptions. Each hazard below
is enumerated with a verdict: **SAFE** (no change needed) or **FIX** (the plan addresses it).

| # | Hazard | Finding | Verdict |
|---|---|---|---|
| H1 | `CPUEMULATOR_TESTVECTORS` env-read race | The resolver reads the env var but **no shipped test mutates it.** `M68000TomHarteVectorsTests` deliberately uses the *pure* `ResolveVectorDirectory(root)` overload with temp roots and never touches the process-global — its own XML doc comment says so. The only `SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", …)` occurrences in the repo are inside a **plan document** (`2026-06-15-m4-4b-…md`), not in source. A pure env *read* from many threads is safe. | **SAFE** (verified by grep across the repo; see §6). The plan adds a guard test (Task 3) that asserts no test mutates it, to keep it safe. |
| H2 | Static mutable state in loaders/runners/caches | `M68000TomHarteLoader.LoadFile` opens a fresh `FileStream` + `GZipStream` + `JsonDocument` per call and returns a new list — **no static cache.** The runners (`M68000TomHarteRunner`, `TomHarteRunner`, `Z80TomHarteRunner`) are static *methods* with **no static fields**; every call allocates its own `AddressSpace`, `TracingAddressSpace`, and CPU. A targeted grep for static mutable collections/arrays/caches in `src/**` and the TomHarte test dir returned **nothing**. | **SAFE** (verified). |
| H3 | Per-case CPU / bus instance sharing | Each `RunCase` builds a brand-new `AddressSpace` (16 MiB arena), a fresh `TracingAddressSpace`, and a fresh `M68000Cpu`/`Mos6502Cpu`/`Z80Cpu`. No instance crosses a case boundary, let alone a thread boundary. | **SAFE.** |
| H4 | gzip loader thread-safety | `GZipStream`/`JsonDocument` instances are local to each `LoadFile` call; the only shared resource is the **file on disk, opened read-only** (`File.OpenRead`). Concurrent read-only opens of the same file across threads are safe on Windows. Different rows read different files anyway. | **SAFE.** |
| H5 | `Console`-capturing tests (Importer) | `Importer/*` tests share `[Collection("ConsoleIsolation")]` because they capture/redirect the process-global `Console`. That collection **must stay intact** so those tests remain serialized relative to each other. Our change does not touch them; they keep their collection and simply run as one of the many parallel collections. | **SAFE** — do NOT split or remove `ConsoleIsolation`. |
| H6 | Klaus / `Category=UAT` functional tests | The Klaus 6502 functional suite and any `Category=UAT` tests run a full program to a trap. They build their own `AddressSpace`/CPU per test and assert a final park state — no shared state, no inter-test ordering. They were already parallel-eligible as separate classes; nothing about the split changes them. | **SAFE.** (Confirm no `[Collection]`-shared fixture among them in Task 1 Step 0.) |
| H7 | JIT differential fuzzer (`DifferentialFuzzTests`, N=64) | Each seed builds a fresh interpreter + fresh `JittedCpu` over a **cloned** RAM image (`NewSpace` clones `p.Ram`), so SMC cannot leak between runs or threads. The JIT compiles into a per-`JittedCpu` code cache (instance-scoped), not a process-global. Already per-seed isolated. | **SAFE.** |
| H8 | JIT code-cache / fastmem global state | The 6502/Z80/68000 JIT sweeps each construct their own `JittedCpu`/`BlockCompiler` per case (`JittedCpuFactory`, `Z80JittedCpuFactory`). Verify in Task 1 Step 0 that `JitTarget`/`Fastmem`/the generated dispatch hold no process-global mutable cache that two `JittedCpu` instances would share. The generated dispatch tables are immutable static *readonly* data (safe to share for reads). | **SAFE pending Task 1 Step 0 confirmation** — a 5-minute grep; if a mutable global is found, that is a genuine fork → Open Questions Q3. |
| H9 | Shared output files / temp dirs | `M68000TomHarteVectorsTests` and a few Importer tests create temp dirs — each uses `Guid.NewGuid()` in the path, so no collision across threads. | **SAFE.** |

**Net:** every hazard is already SAFE except H8, which is a quick confirmation in Task 1. No production-code fix is
required for correctness; the parallelization is a structural/config change only. This is the single most important
conclusion of the plan: **the suite was already written parallel-clean** (fresh-instance-per-case discipline
throughout), so the only thing missing is telling xUnit to actually distribute the rows.

---

## 4. Task breakdown

Each task is independently reviewable. Tasks 1–3 are the change; Task 4 is the validation contract Builder MUST satisfy
before merge.

### Task 1 — Confirm the hazard model + capture the baseline (no code change yet)

**Step 0 (hazard confirmation, ~10 min):**
- [ ] Grep `src/**` and `tests/**` for static mutable state shared by the JIT path (H8):
  ```
  rg -n "static\s+(readonly\s+)?\w" src/CpuEmulator.Jit src/CpuEmulator.Cpus.M68000 \
     | rg -v "const|static (partial )?class|static (readonly )?[A-Za-z0-9_<>,\. ]+\b\w+\s*=\s*(new\s+)?[A-Za-z0-9_]+\[\]|=>"
  ```
  Confirm any static field reachable from `JittedCpu`/`BlockCompiler`/`Fastmem`/generated dispatch is either `const`,
  `static readonly` immutable data, or `[ThreadStatic]`. If a *mutable* process-global cache is found → STOP, record it
  in Open Questions Q3, and do not proceed without owner input.
- [ ] Confirm no `[Collection]`-shared fixture among the Klaus/UAT classes (H6).

**Step 1 (baseline capture):** on the 32-core machine, with the 680x0 vectors present, run the CURRENT suite and record
both the totals and the wall-clock:
```
dotnet build -c Release
# time the run; capture stdout to a file for the totals line
/usr/bin/time -v dotnet test -c Release --no-build 2>&1 | tee baseline.log    # (PowerShell: Measure-Command { dotnet test -c Release --no-build })
```
- [ ] Record: `Passed 5613, Failed 0, Skipped 0` (MUST match — if it does not, the environment differs from the
  reference and the whole comparison is invalid; stop and reconcile).
- [ ] Record baseline wall-clock (expected ~33 min).

### Task 2 — Split the dominant 68000 sweeps into per-file collections (the parallelism lever)

The goal: turn each dominant `[Theory]`-over-many-files class into many collections so xUnit distributes them. DEFAULT
mechanism = a shared abstract base that carries the runner logic, with one tiny derived class per file (each derived
class is its own implicit collection). This keeps the assertion body in ONE place (no behavior drift) while multiplying
the collection count.

**Pattern (illustrated for the ALU sweep — the same shape applies to `M68000M45cTomHarteTests`,
`M68000M45d1TomHarteTests`, `M68000ExceptionCorpusTomHarteTests`, `M68000TimingAxisTomHarteTests`, `M68000TomHarteTests`):**

Extract the existing per-file body into a base method, then make each file its own class. The literal shape:

```csharp
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>Shared body for the M4.5b integer-ALU data-axis sweep. Each in-scope file gets its OWN derived
/// class (hence its own xUnit collection) so the 51 files distribute across cores instead of running serially
/// in one class. The assertion logic is IDENTICAL to the pre-split single-theory body — only the collection
/// boundary changed (test-infra parallelism; zero semantics change).</summary>
public abstract class M68000AluTomHarteSweepBase
{
    /// <summary>The vector file this sweep class asserts (one file == one collection == one parallel unit).</summary>
    protected abstract string File { get; }

    [M68000TomHarteFact]   // see Task 2b: a Fact-shaped skip-when-absent attribute (one file per class now)
    public void Alu_family_is_TomHarte_green_on_the_data_axis()
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, File);
        Assert.True(System.IO.File.Exists(path), $"in-scope ALU-family vector file missing: {path}");

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int executed = 0, deferred = 0;
        foreach (var c in cases)
        {
            string? r = M68000TomHarteRunner.RunCase(c);            // data axis (timingAxis: false) — UNCHANGED
            if (ReferenceEquals(r, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (r is not null) { failures.Add(r); if (failures.Count >= 10) break; }
        }
        Assert.True(executed > 0, $"{File}: 0 executed (non-exception) cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{File}: {failures.Count}+ data-axis failures over {executed} executed cases " +
            $"({deferred} deferred to M4.5d):\n" + string.Join("\n", failures));
    }
}

// One derived class per file — each is its own collection → each runs on its own thread.
public sealed class M68000Alu_ADD_b   : M68000AluTomHarteSweepBase { protected override string File => "ADD.b.json.gz"; }
public sealed class M68000Alu_ADD_w   : M68000AluTomHarteSweepBase { protected override string File => "ADD.w.json.gz"; }
public sealed class M68000Alu_ADD_l   : M68000AluTomHarteSweepBase { protected override string File => "ADD.l.json.gz"; }
// … one per entry in the existing AluFiles list (51 total) …
public sealed class M68000Alu_DIVS    : M68000AluTomHarteSweepBase { protected override string File => "DIVS.json.gz"; }
```

- [ ] **Step 1:** For each of the 6 dominant 68000 sweep classes, extract its per-file loop into an abstract base,
  preserving the assertion body **verbatim** (including the `IsInconsistentRegisterShiftVector` / `IsChkInRangeCase`
  filters and the `assertExceptions`/`pcPrefetchAxis` flags — copy them unchanged into the base). Generate one
  `sealed` derived class per file in the existing `*Files` list.
- [ ] **Step 2:** `M68000TimingAxisTomHarteTests` has TWO theories (`M45d1Files` + `M45acFiles`). Split BOTH into the
  same per-file derived-class shape, each calling the shared `RunPcPrefetchSweep(file)` body (move it to the base).
- [ ] **Step 3:** Delete the now-empty original `[Theory]`/`[MemberData]` methods (their rows are now the derived
  classes). Keep the original `*Files` list **as a guard test** (Task 3) so a dropped file is caught.

> **Counting note:** the reported *test count* changes shape — was N theory rows in 6 classes; becomes N facts in N
> classes. The total **number of executed test cases stays the same** (still one assertion per file), so the
> `Passed 5613` total is preserved IFF every file maps 1:1 to one derived class. Task 3's guard test enforces that 1:1.

### Task 2b — A `Fact`-shaped skip-when-absent attribute

The existing `M68000TomHarteTheoryAttribute : TheoryAttribute` gates theories. The split classes use `[Fact]` (one file
each), so add the `Fact` analogue with identical skip logic:

```csharp
/// <summary>FactAttribute that skips at discovery when the 680x0 vectors are absent — the Fact-shaped twin of
/// <see cref="M68000TomHarteTheoryAttribute"/>, for the per-file split sweep classes (one file == one Fact).</summary>
public sealed class M68000TomHarteFactAttribute : FactAttribute
{
    public M68000TomHarteFactAttribute()
    {
        if (M68000TomHarteVectors.TryGetVectorDirectory() is null)
            Skip = "680x0 TomHarte vectors not found — run tools/get-test-vectors-68000.ps1, " +
                   "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).";
    }
}
```
- [ ] Add to `M68000TomHarteVectors.cs` (next to the existing Theory attribute).

> **6502/Z80 note:** those sweeps are **one row per opcode** already — many collections per class is NOT the issue there
> (each class is still one collection, but its rows are short/sampled). They are not the long pole and do **not** need
> splitting for the CI win. **DEFAULT: leave the 6502/Z80 sweeps structurally unchanged.** They still benefit from the
> `xunit.runner.json` collection-parallelism (their classes run concurrently with the 68000 collections). If profiling
> in Task 4 shows a 6502/Z80 class is now a secondary long pole, splitting them is a trivial follow-up (Open Q2).

### Task 3 — Add `xunit.runner.json` + the guard tests

- [ ] **Step 1:** Add `tests/CpuEmulator.Tests/xunit.runner.json` with the content from §2. Wire it into the csproj so
  it is copied to the output dir (xUnit reads it from next to the test assembly):
  ```xml
  <ItemGroup>
    <None Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  ```
- [ ] **Step 2:** Add a guard test asserting the split classes cover EXACTLY the original file lists (no file dropped,
  none duplicated) — this is what protects the `5613` total against a copy-paste slip:
  ```csharp
  [Fact]
  public void Split_alu_classes_cover_exactly_the_canonical_file_list()
  {
      // The canonical list (kept from the pre-split M68000AluTomHarteTests.AluFiles).
      var expected = M68000AluTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
      var covered = typeof(M68000AluTomHarteSweepBase).Assembly.GetTypes()
          .Where(t => t.IsSealed && typeof(M68000AluTomHarteSweepBase).IsAssignableFrom(t))
          .Select(t => ((M68000AluTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
          .OrderBy(x => x).ToArray();
      Assert.Equal(expected, covered);
  }
  ```
  (Expose the file list as a `static` `CanonicalFiles` array on each base + a `FileForGuard => File` accessor. One guard
  per split family.)
- [ ] **Step 3:** Add a guard test asserting no test mutates `CPUEMULATOR_TESTVECTORS` (H1 belt-and-suspenders) — a
  simple test that records the var, runs nothing, and confirms it is unchanged is too weak; instead document the
  invariant in the runner's XML comment and rely on the existing `ResolveVectorDirectory` pure-overload discipline.
  (DEFAULT: skip a runtime guard here — the static discipline + grep in §6 is sufficient. See Open Q4.)

### Task 4 — The validation contract (Builder MUST satisfy ALL before merge)

This is the merge gate. Builder proves success with evidence, not assertion (per the project's verification discipline).

1. **Byte-identical pass/fail vs baseline.** Run `dotnet test -c Release` with the 680x0 vectors present. REQUIRE:
   `Passed 5613, Failed 0, Skipped 0` — the **exact** totals from Task 1's baseline. Any delta (even +1 passed from a
   miscount, or any new skip) is a FAIL → reconcile before merge. Capture the totals line in the PR body.
   - [ ] Also confirm the 6502 + Z80 byte-identity sweeps (`Mos6502TomHarteTests`, `Mos6502JitTomHarteTests`,
     `Z80TomHarteTests`, `Z80JitTomHarteTests`) are among the passing set (they are part of the 5613; call them out
     explicitly so a structural mistake there is caught).
2. **Before/after wall-clock on the SAME machine.** Record both numbers (Task 1 baseline vs the parallelized run) and
   the speedup ratio in the PR body. REQUIRE the parallelized run is **measurably faster** (the whole point). If it is
   NOT faster, the cap is likely too high (GC thrash) → re-run the thread sweep below.
3. **Thread-cap sweep (tuning, pick the winner).** Run the parallelized suite at `maxParallelThreads` ∈ **{16, 24, 32}**
   (edit `xunit.runner.json` or pass `-- xUnit.MaxParallelThreads=N`). Record wall-clock for each; **commit the fastest
   value that stays fully green.** DEFAULT to commit if the sweep is inconclusive: **24**.
4. **Flakiness check — run the parallelized suite N=3 times, REQUIRE identical green every time.** Races that the first
   green run hides (a missed shared-state hazard) surface as an intermittent failure across repeats. REQUIRE
   `5613/0/0` on **all three** runs. Any non-green run, even once, blocks merge and triggers a hazard re-analysis (start
   with H8/JIT globals). Capture all three totals lines.
5. **No production-code change.** REQUIRE `git diff --stat origin/main -- src/` is **empty** — the PR touches only
   `tests/**` + the new `xunit.runner.json`. (A test-infra PR that edits `src/` has overstepped its scope.)

PR body MUST include: the baseline totals + wall-clock, the parallelized totals + wall-clock for the committed cap, the
3× flakiness totals, the chosen `maxParallelThreads`, and the `git diff --stat -- src/` empty confirmation.

---

## 5. Validation contract summary (the one-screen version for the PR template)

```
[ ] Baseline (this machine, pre-change):  Passed 5613, Failed 0, Skipped 0   |  wall-clock: ____ (≈33 min)
[ ] Parallelized (cap=__):                Passed 5613, Failed 0, Skipped 0   |  wall-clock: ____  → speedup ____×
[ ] 6502 + Z80 byte-identity sweeps in the passing set: yes
[ ] Thread sweep {16,24,32} wall-clocks:  16=____  24=____  32=____   → committed cap: ____
[ ] Flakiness 3×:  run1 5613/0/0   run2 5613/0/0   run3 5613/0/0
[ ] git diff --stat origin/main -- src/   ⇒ EMPTY
```

---

## 6. Evidence gathered (so Builder need not re-derive)

- **No `xunit.runner.json`, no parallelization attributes** in `tests/CpuEmulator.Tests/` (grep for
  `CollectionBehavior|maxParallelThreads|parallelizeTest` hit only the xUnit binaries under `bin/`, never source).
- **Each 68000 vector file = 8,065 cases** (counted by decompressing MOVEM.l/MOVEM.w/MOVE.l/ADD.w/DIVU/RTE — all 8,065).
  125 files in `~/.cache/cpuemulator/vectors/680x0/v1`.
- **xUnit v2 cannot parallelize theory rows within a class** — confirmed; method-level parallelism is a v3 feature.
  This is THE reason `maxParallelThreads` alone is insufficient and a class-split is required. (Sources below.)
- **No static mutable state** shared by the runners/loaders (grep of `src/**` + the TomHarte test dir for static
  mutable collections/caches returned nothing). Loader is fresh-stream-per-call; CPUs/buses are fresh-per-case.
- **`CPUEMULATOR_TESTVECTORS` is read-only in all shipped source** — the only `SetEnvironmentVariable` for it lives in a
  plan doc, not in code. `M68000TomHarteVectorsTests` uses the pure resolver overload by design.
- **`ConsoleIsolation`** is the only existing shared collection (Importer tests) and must stay intact.
- **Per-case 16 MiB arena** (`new byte[0x1000000]`) in `M68000TomHarteRunner.RunCase` — the reason the thread cap is
  ~75% of cores, not all 32.

Sources for the xUnit-v2 parallelism limits:
- [Running Tests in Parallel | xUnit.net](https://xunit.net/docs/running-tests-in-parallel)
- [Looking for suggestions on improving parallelism · xunit/xunit Discussion #3164](https://github.com/xunit/xunit/discussions/3164)
- [Meziantou.Xunit.ParallelTestFramework](https://github.com/meziantou/Meziantou.Xunit.ParallelTestFramework)

---

## 7. Resolved decisions (Coordinator, with the owner — 2026-06-16)

The §7 open questions were resolved before dispatch. Builder implements per these:

1. **Mechanism → class-split (no new dependency).** The shared abstract base + per-file `[Fact]` classes. NOT the
   `Meziantou.Xunit.ParallelTestFramework` dependency — keep the project free of a third-party test-framework dep.
2. **Scope → split the 6 dominant 68000 sweeps AND the 6502 (`Mos6502TomHarteTests`) + Z80 (`Z80TomHarteTests`)
   sweeps**, all via the same base class. (Owner widened this from the Planner default of 68000-only — once the base
   exists, extend it to 6502/Z80 so the whole suite parallelizes uniformly.) Granularity: one class per file.
3. **JIT global state (H8) → Builder confirms in Task 1 Step 0** (the grep). Planner's verdict is SAFE; if the grep
   finds a mutable process-global on the JIT path, Builder STOPS and reports it to the Coordinator as a fork (candidate
   fixes: `[ThreadStatic]`, or keep the JIT sweeps in a single serial collection) — do not silently work around it.
4. **Thread cap → Builder tunes empirically.** Start at 24; Task 4 sweeps {16, 24, 32} and commits the fastest
   green value to `xunit.runner.json`. The validation contract measures before/after regardless.
5. **ADR → none.** Test-infra, no emulator-contract change. Confirmed.
