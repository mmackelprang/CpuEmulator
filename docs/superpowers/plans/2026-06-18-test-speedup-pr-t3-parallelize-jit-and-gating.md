# PR-T3 — Parallelize the JIT sweeps + encode the gating policy (levers 3, 5, 6)

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans`, task-by-task. Steps use checkbox (`- [ ]`) syntax.
> **Read first (binding):** `2026-06-18-test-speedup-arc-overview.md` — especially the **Gating policy** table (T3
> is what encodes it) and the per-PR shared gates. Lands AFTER PR-T1 (shares `ResolveSampleSize`) and ideally
> after PR-T2; independent of their files (T3 touches the JIT-sweep classes + Klaus/ZEX, not the runners). Branch
> `test/speedup-parallel-jit-gating` → PR → main. **NO production code.**

**Goal:** Three independent levers that together unblock the JIT path and the heavy exercisers:
- **Lever 3:** split the 8088 + 68000 JIT sweeps — today each is ONE `[Theory]`+`[MemberData]` = one xUnit
  collection on ONE thread — into per-partition classes that mirror the interpreter split, so the JIT sweep
  parallelizes across the configured 16 threads.
- **Lever 5:** env-gate the heavy Klaus-through-JIT functional run (it runs every invocation today wherever the
  binary is cached) and derive its checkpoint from the hard-coded anchor constant instead of a redundant ~96M-cycle
  interpreter re-run.
- **Lever 6:** downgrade the full ZEXDOC pass to a bounded fail-fast triage pre-check (ZEXALL ⊃ ZEXDOC is the real
  gate), trimming the `CPUEMULATOR_ZEX=full` runtime.

**Architecture:** Mirror the established interpreter-split idiom (`Mos6502TomHarteSweepBase` + N tiny `sealed`
derived classes, each its own collection — `Mos6502TomHarteTests.cs:48,84-106`) for the two JIT sweeps. Add a
`KlausFact`-style env gate for the heavy Klaus-JIT run; replace the interp re-run with the constant. Cap the
ZEXDOC full pass at a triage budget.

**Tech Stack:** xUnit `[Theory]`/`[MemberData]`/collection parallelism, the existing `xunit.runner.json`
(`maxParallelThreads: 16`), env-gating via early-return (the project's established `CPUEMULATOR_ZEX=full` idiom).

---

## What the recon CONFIRMED (file:line — verified against `main` @ `896f88b`)

| # | Fact | Evidence |
|---|------|----------|
| J1 | The 8088 JIT sweep is ONE `[Theory]` class over the file list → one collection, one thread | `M8088JitTomHarteTests.cs:26,34-35` (`[M8088TomHarteTheory][MemberData(nameof(AllDataAxisFiles))]`) |
| J2 | The 68000 JIT sweep is likewise ONE `[Theory]` class | `M68000JitTomHarteTests.cs:23,31-32` |
| J3 | xUnit parallelizes across COLLECTIONS (classes), not `[Theory]` rows; the interpreter side splits into per-partition classes to get parallelism | `Mos6502TomHarteTests.cs:48` (base), `:84-106` (`Mos6502Tom_P0..P3`, each `PartitionOpcodes(i,4)`); `xunit.runner.json` (`parallelizeTestCollections: true, maxParallelThreads: 16`) |
| K1 | The Klaus-JIT functional run is gated on BINARY PRESENCE only (`[KlausFact]`), NOT env → runs every invocation where the binary is cached | `KlausVectors.cs` (`KlausFactAttribute` sets `Skip` only when the binary is absent) |
| K2 | It re-runs a full ~96M-cycle interpreter pass purely to derive the checkpoint, then asserts that result == the hard-coded constant | `KlausJitFunctionalTests.cs:44` (`RunInterpreterToTrap`), `:45` (`Assert.Equal(InterpreterAnchorCycles, anchorCycles)`), `:46` (`checkpoint = anchorCycles - TailWindow`), `:26` (`InterpreterAnchorCycles = 96_241_367`) |
| K3 | A SEPARATE interpreter Klaus pin exists (`KlausFunctionalTests`) and the differential fuzzer covers JIT every run | `tests/.../Klaus/KlausFunctionalTests.cs`, `tests/.../Jit/DifferentialFuzzTests.cs` |
| Z1 | ZEXALL is a strict superset of ZEXDOC; ZEXDOC is the faster pre-check | `ZexallTests.cs:8-11` |
| Z2 | The full ZEX runs (interp + JIT) are ALREADY env-gated to `CPUEMULATOR_ZEX=full`; only the ~1.3 s smoke runs per CI | `ZexallTests.cs:27-28,62-66,73-77`; `Z80ZexJitTests.cs` same |

**Refinement (vs. the original diagnosis):** lever 6 is NOT a per-PR saving (Z2 — ZEX full is already gated). Its
value is trimming the *within-full-gate* runtime: under `CPUEMULATOR_ZEX=full`, four ~130 s passes run (ZEXDOC
interp, ZEXALL interp, ZEXDOC-JIT, ZEXALL-JIT) though ZEXDOC ⊂ ZEXALL. T3 downgrades the two ZEXDOC FULL passes to
bounded triage pre-checks. Lever 5's per-PR saving is real (K1).

---

## File structure

- **Modify:** `M8088JitTomHarteTests.cs` — extract a `M8088JitSweepBase` + N per-partition `sealed` classes.
- **Modify:** `M68000JitTomHarteTests.cs` — same split.
- **Modify:** `KlausVectors.cs` — add a `KlausJitFact` attribute (binary-presence + `CPUEMULATOR_KLAUS=full` gate).
- **Modify:** `KlausJitFunctionalTests.cs` — use `[KlausJitFact]`; derive checkpoint from the constant; drop the
  redundant interpreter re-run.
- **Modify:** `ZexallTests.cs` + `Z80ZexJitTests.cs` — ZEXDOC full → bounded triage pre-check; ZEXALL stays the gate.
- **Modify (doc):** add the gating-policy table as a doc comment in a new `tests/.../GatingPolicy.cs` marker file
  (a documented, discoverable home for the policy).

---

## Task 1: Split the 8088 JIT sweep into parallel per-partition classes (lever 3)

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/M8088JitTomHarteTests.cs`

- [ ] **Step 1: Extract the body into an abstract base.** Rename the class `M8088JitTomHarteTests` to an abstract
  `M8088JitSweepBase`, change the `[Theory]` method into a `protected void RunFile(string file)` (move the entire
  current `Family_is_tier_parity_green_through_the_JIT` body — `:36-105` — into it verbatim, dropping the
  `[M8088TomHarteTheory][MemberData]` attributes from the method). Keep the static `s_metadata` field and add a
  partition helper:

```csharp
/// <summary>Shared 8088 JIT tier-parity sweep body (lever 3 split). One derived class per partition → one xUnit
/// COLLECTION per partition → the heaviest JIT tier parallelizes across the configured threads, mirroring the
/// interpreter split (Mos6502TomHarteSweepBase). The sampling + classify logic is IDENTICAL to the pre-split body.</summary>
public abstract class M8088JitSweepBase(ITestOutputHelper output)
{
    protected static readonly M8088Metadata s_metadata =
        M8088Metadata.Load(M8088TomHarteVectors.TryGetVectorDirectory());

    /// <summary>Partition the data-axis file list into <paramref name="parts"/> stripes; return stripe
    /// <paramref name="index"/>. Stripe assignment is by position (i % parts) so each stripe is a balanced mix.</summary>
    public static TheoryData<string> Partition(int index, int parts)
    {
        var data = new TheoryData<string>();
        int i = 0;
        foreach (var f in M8088DataAxisCorpus.Files)
        {
            if (i % parts == index) data.Add(f);
            i++;
        }
        return data;
    }

    protected void RunFile(string file)
    {
        // ... the ENTIRE current body of Family_is_tier_parity_green_through_the_JIT (M8088JitTomHarteTests.cs:38-104),
        //     verbatim, including the PR-T1 cache call site if T1 has merged ...
    }
}
```

- [ ] **Step 2: Add the per-partition derived classes** (8 stripes — the 8088 data-axis corpus is large; 8 keeps
  each stripe well under the thread budget). After the base class:

```csharp
public sealed class M8088JitTom_P0(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(0, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M8088JitTom_P1(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(1, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

// ... P2..P7 identical, Partition(2,8) … Partition(7,8) ...
```

(Write out all eight `P0`–`P7` literally — do not abbreviate in the implementation; the engineer may read tasks
out of order. Each is three lines, differing only in the partition index `0..7`.)

- [ ] **Step 3: Build + run the 8088 JIT sweep**

Run: `CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~M8088JitTom" --no-restore`
Expected: PASS — same green, now reported across 8 classes. (xUnit runs the 8 collections in parallel.)

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M8088JitTomHarteTests.cs
git commit -m "test(speedup): split the 8088 JIT sweep into 8 parallel per-partition classes (lever 3)"
```

---

## Task 2: Split the 68000 JIT sweep the same way (lever 3)

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/M68000JitTomHarteTests.cs`

- [ ] **Step 1: Extract `M68000JitSweepBase`** with the partition helper over `M68000DataAxisCorpus.Files` and a
  `protected void RunFile(string file)` holding the current body (`:33-61` verbatim, incl. the
  `IsExcludedCase`/`assertExceptions:true`/`DeferredException` logic and — if T1 merged — the cache call site):

```csharp
public abstract class M68000JitSweepBase(ITestOutputHelper output)
{
    public static TheoryData<string> Partition(int index, int parts)
    {
        var data = new TheoryData<string>();
        int i = 0;
        foreach (var f in M68000DataAxisCorpus.Files) { if (i % parts == index) data.Add(f); i++; }
        return data;
    }

    protected void RunFile(string file)
    {
        // ... the ENTIRE current body of Family_is_tier_parity_green_through_the_JIT, verbatim ...
    }
}
```

- [ ] **Step 2: Add `M68000JitTom_P0..P7`** (8 stripes, written out literally, same three-line shape as Task 1
  Step 2 but using `M68000TomHarteTheory` if that attribute exists, else `[Theory]` — match the attribute the
  current `M68000JitTomHarteTests` uses at `:31`).

- [ ] **Step 3: Build + run**

Run: `CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~M68000JitTom" --no-restore`
Expected: PASS, same green across 8 classes.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000JitTomHarteTests.cs
git commit -m "test(speedup): split the 68000 JIT sweep into 8 parallel per-partition classes (lever 3)"
```

---

## Task 3: Env-gate the Klaus-JIT run + drop the redundant interp re-run (lever 5)

**Files:**
- Modify: `tests/CpuEmulator.Tests/Klaus/KlausVectors.cs` (new `KlausJitFact` attribute)
- Modify: `tests/CpuEmulator.Tests/Klaus/KlausJitFunctionalTests.cs`

- [ ] **Step 1: Add the `KlausJitFact` env gate.** In `KlausVectors.cs`, after the existing `KlausFactAttribute`:

```csharp
/// <summary>Like <see cref="KlausFactAttribute"/> (skips when the Klaus binary is absent) but ALSO env-gates the
/// HEAVY through-JIT functional run behind CPUEMULATOR_KLAUS=full — it is a periodic / pre-arc / pre-merge gate,
/// NOT a per-PR cost (lever 5, mirroring the CPUEMULATOR_ZEX=full precedent). The per-run JIT coverage is carried
/// by the differential fuzzer (DifferentialFuzzTests) + the sampled JIT TomHarte sweeps + the interpreter Klaus
/// pin (KlausFunctionalTests), all of which still run every invocation.</summary>
public sealed class KlausJitFactAttribute : FactAttribute
{
    public KlausJitFactAttribute()
    {
        if (KlausVectors.TryGetBinaryPath() is null)
            Skip = "Klaus functional-test binary not found — run tools/get-klaus.ps1 (or .sh), " +
                   "or set CPUEMULATOR_TESTVECTORS.";
        else if (Environment.GetEnvironmentVariable("CPUEMULATOR_KLAUS") != "full")
            Skip = "Klaus-through-JIT is a periodic gate — set CPUEMULATOR_KLAUS=full to run it.";
    }
}
```

- [ ] **Step 2: Gate the heavy method + derive the checkpoint from the constant.** In
  `KlausJitFunctionalTests.cs`, change `Functional_test_runs_to_the_success_trap_under_the_JIT` from `[KlausFact]`
  to `[KlausJitFact]`, and replace the interpreter re-run (`:44-46`) with the constant-derived checkpoint:

```csharp
    [KlausJitFact]
    public void Functional_test_runs_to_the_success_trap_under_the_JIT()
    {
        byte[] image = File.ReadAllBytes(KlausVectors.TryGetBinaryPath()!);
        Assert.Equal(0x10000, image.Length);

        // The checkpoint is derived from the PINNED interpreter anchor (no redundant ~96M-cycle interp re-run —
        // lever 5). InterpreterAnchorCycles is the committed oracle; the interpreter Klaus PIN (KlausFunctionalTests)
        // still re-verifies it every run, so this constant cannot silently drift.
        long checkpoint = InterpreterAnchorCycles - TailWindow;

        // ... the rest of the method is UNCHANGED from :48 onward (the JIT run, the budget-1 tail, the final
        //     Assert.Equal(InterpreterAnchorCycles, inner.CycleCount)) ...
    }
```

(Delete the now-unused `private static long RunInterpreterToTrap(byte[] image)` helper — `:116-133` — IF no other
test references it. Grep first: `grep -rn RunInterpreterToTrap tests/`. If the other Klaus method or a sibling
uses it, leave it.)

- [ ] **Step 3: Verify the gated path skips by default, runs under env.**

```bash
# Default: the heavy Klaus-JIT run is now SKIPPED.
dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~KlausJitFunctionalTests" --no-restore 2>&1 | tail -10
# Periodic gate: it RUNS and is green (and proves the constant-derived checkpoint reaches the success trap).
CPUEMULATOR_KLAUS=full dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~KlausJitFunctionalTests" --no-restore 2>&1 | tail -10
```

Expected: default run reports the method skipped; the `=full` run is PASS with the trap reached at exactly
`96,241,367` cycles (proving dropping the interp re-run changed nothing).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Klaus/KlausVectors.cs tests/CpuEmulator.Tests/Klaus/KlausJitFunctionalTests.cs
git commit -m "test(speedup): env-gate Klaus-JIT + derive checkpoint from the anchor constant (lever 5)"
```

---

## Task 4: Downgrade ZEXDOC FULL to a bounded triage pre-check (lever 6)

ZEXALL ⊃ ZEXDOC, so a FULL ZEXDOC pass under `CPUEMULATOR_ZEX=full` is redundant with the ZEXALL pass. Keep ZEXDOC
as a FAST fail-fast triage signal (a bounded budget that catches gross breakage cheaply before the long ZEXALL
pass), and keep ZEXALL as the real gate.

**Files:**
- Modify: `tests/CpuEmulator.Tests/Zex/ZexallTests.cs` (`Zexdoc_all_subtests_pass`)
- Modify: `tests/CpuEmulator.Tests/Zex/Z80ZexJitTests.cs` (`Zexdoc_passes_through_the_JIT`)

- [ ] **Step 1: ZEXDOC interp → bounded triage.** In `ZexallTests.cs`, change `Zexdoc_all_subtests_pass` so that
  under `CPUEMULATOR_ZEX=full` it runs ZEXDOC only to a BOUNDED triage budget (enough to clear init + the first
  sub-tests and surface any `ERROR`), not the full multi-billion-T-state pass:

```csharp
    // Triage budget: enough to clear ZEX init and run the first several sub-tests to an OK/ERROR verdict (a few
    // billion T-states), NOT the full ~46.7e9-T-state pass. ZEXALL (the strict superset) is the authoritative
    // full gate (Zexall_all_subtests_pass); ZEXDOC-full is redundant with it, so ZEXDOC is the fast triage signal.
    private const long ZexdocTriageBudget = 5_000_000_000;

    [ZexFact("zexdoc.com")]
    public void Zexdoc_triage_precheck()
    {
        if (!FullEnabled)
        {
            output.WriteLine("skipped — set CPUEMULATOR_ZEX=full to enable the ZEXDOC triage pre-check.");
            return;
        }
        string path = ZexVectors.TryGetBinaryPath("zexdoc.com")!;
        var host = new CpmBdosHost(File.ReadAllBytes(path));
        string transcript = host.Run(ZexdocTriageBudget);
        output.WriteLine(transcript);
        // Triage gate: any ERROR in the cleared sub-tests fails fast (cheaper than waiting for the full ZEXALL).
        AssertNoError(transcript);
    }
```

(Rename the method `Zexdoc_all_subtests_pass` → `Zexdoc_triage_precheck`; the FULL correctness proof is ZEXALL,
which is unchanged.)

- [ ] **Step 2: ZEXDOC-JIT → bounded triage.** Apply the identical downgrade to `Z80ZexJitTests.cs`'s
  `Zexdoc_passes_through_the_JIT` (rename → `Zexdoc_triage_precheck_through_the_JIT`, bounded budget, `AssertNoError`
  on the transcript via the file's existing inline ERROR check). Leave `Zexall_passes_through_the_JIT` unchanged —
  it is the real JIT composition gate.

- [ ] **Step 3: Verify under the full gate.**

```bash
CPUEMULATOR_ZEX=full dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~Zex" --no-restore 2>&1 | tail -20
```

Expected: PASS — ZEXALL (interp + JIT) full, ZEXDOC triage (interp + JIT) bounded. Record the wall-clock vs. the
pre-change full-gate run in the PR body (the saving is the two skipped full ZEXDOC passes).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Zex/ZexallTests.cs tests/CpuEmulator.Tests/Zex/Z80ZexJitTests.cs
git commit -m "test(speedup): ZEXDOC full → bounded triage pre-check; ZEXALL stays the gate (lever 6)"
```

---

## Task 5: Encode the gating policy explicitly (documented, discoverable)

**Files:**
- Create: `tests/CpuEmulator.Tests/GatingPolicy.cs`

- [ ] **Step 1: Write the policy doc as a discoverable marker.** Create the file with the policy table from the
  overview as a class-level doc comment, so the policy lives in-tree next to the tests it governs:

```csharp
namespace CpuEmulator.Tests;

/// <summary>
/// THE VERIFICATION GATING POLICY (encoded by PR-T3). What runs per-PR vs periodically:
///
/// | Workload                       | Per-PR (routine)                  | Periodic / pre-arc / pre-merge      |
/// |--------------------------------|-----------------------------------|-------------------------------------|
/// | TomHarte interpreter sweeps    | sampled (default 100)             | CPUEMULATOR_UAT=full (full per-file)|
/// | TomHarte JIT sweeps            | sampled, parallel per-partition   | CPUEMULATOR_UAT=full                |
/// | Klaus interpreter pin          | every run (the oracle)            | —                                   |
/// | Klaus through-JIT functional   | gated CPUEMULATOR_KLAUS=full      | run pre-arc / pre-merge             |
/// | ZEX smoke (wiring)             | every run (~1.3 s)                | —                                   |
/// | ZEXDOC                         | triage pre-check (bounded)        | within CPUEMULATOR_ZEX=full         |
/// | ZEXALL (interp + JIT)          | gated CPUEMULATOR_ZEX=full        | the real composition gate           |
/// | Differential fuzzer            | every run (covers JIT each run)   | —                                   |
///
/// Rationale (PR-1 precedent): full ZEXDOC-through-JIT is a periodic / pre-arc gate, not per-PR. The heavy JIT
/// exercisers (Klaus-JIT, ZEXDOC/ZEXALL-JIT) sit behind env gates; per-run JIT coverage is the differential fuzzer
/// + the sampled JIT TomHarte sweeps + the interpreter Klaus pin — all of which run every invocation.
/// </summary>
internal static class GatingPolicy { }
```

- [ ] **Step 2: Build (a doc-only marker — must compile).**

Run: `dotnet build tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --no-restore`
Expected: build succeeds, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/GatingPolicy.cs
git commit -m "test(speedup): encode the verification gating policy in-tree (levers 5/6 policy)"
```

---

## Task 6: MEASUREMENT GATE

- [ ] **Step 1: Lever-3 parallelism win (the headline).** Time the JIT sweeps before↔after the split at a fixed
  sample:

```bash
# BEFORE (on main@base — single-collection JIT sweep):
git stash || true
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~M8088JitTomHarteTests|FullyQualifiedName~M68000JitTomHarteTests" --no-restore 2>&1 | tee /tmp/t3-before.txt
git stash pop || true
# AFTER (PR branch — per-partition classes):
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~M8088JitTom|FullyQualifiedName~M68000JitTom" --no-restore 2>&1 | tee /tmp/t3-after.txt
```

- [ ] **Step 2: Lever-5 win.** Confirm the default-path Klaus-JIT cost is gone:

```bash
# The heavy Klaus-JIT method no longer runs by default (it was ~330M cycles where the binary is cached).
dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~Klaus" --no-restore 2>&1 | tee /tmp/t3-klaus.txt
```

- [ ] **Step 3: Record in the PR body.** Table: `subset | before | after | speedup`. **Gates:** (3) JIT-sweep
  wall-clock after < before, target ≈ the parallel-stripe factor (bounded by 16 threads / other concurrent
  collections); (5) the default-path Klaus-JIT method is reported skipped; (6) the `CPUEMULATOR_ZEX=full` wall-clock
  drops by ~two full-ZEXDOC passes.

---

## Task 7: COVERAGE-PRESERVATION GATE

- [ ] **Step 1: Lever 3 — zero coverage cost (executed-count parity).** The split changes only HOW the JIT cases
  are distributed across collections, not WHICH run. Sum the `ran/executed/deferred` across the 8 partition classes
  and confirm it equals the pre-split single-class totals:

```bash
grep -hoE "ran [0-9]+, executed [0-9]+.*" /tmp/t3-before.txt > /tmp/jit-before.txt
grep -hoE "ran [0-9]+, executed [0-9]+.*" /tmp/t3-after.txt  > /tmp/jit-after.txt
# Sum executed across both files; they must be equal (same files, same sample, same classifier).
awk '{for(i=1;i<=NF;i++) if($i=="executed") s+=$(i+1)} END{print "executed total:", s}' /tmp/jit-before.txt
awk '{for(i=1;i<=NF;i++) if($i=="executed") s+=$(i+1)} END{print "executed total:", s}' /tmp/jit-after.txt
```

Expected: identical `executed total` before↔after. State it in the PR body.

- [ ] **Step 2: Lever 5 — document the coverage delta + prove the gate reachable.** PR body states: the
  per-run JIT coverage is unchanged (the differential fuzzer + sampled JIT TomHarte sweeps + interp Klaus pin all
  still run); ONLY the heavy Klaus-JIT functional run moved to `CPUEMULATOR_KLAUS=full`. Prove it still runs:
  `CPUEMULATOR_KLAUS=full dotnet test … --filter "FullyQualifiedName~KlausJitFunctionalTests"` → PASS (already run
  in Task 3 Step 3 — cite it).

- [ ] **Step 3: Lever 6 — document the coverage delta + prove ZEXALL still gates.** PR body states: ZEXALL (the
  strict superset, interp + JIT) is unchanged and remains the authoritative composition gate; ZEXDOC dropped from a
  full pass to a bounded triage pre-check — NO net coverage loss (every ZEXDOC sub-test is a ZEXALL sub-test).
  Prove: the `CPUEMULATOR_ZEX=full` run in Task 4 Step 3 is green (cite it).

- [ ] **Step 4: Full-suite green at the new defaults**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --no-restore`
Expected: PASS (Klaus-JIT + ZEXDOC-full now skipped/bounded by default).

- [ ] **Step 5: Open the PR.** Body MUST include: the measurement table, the JIT `executed total` parity, the
  lever-5 + lever-6 coverage-delta statements with the `=full` reachability proofs, and a pointer to
  `GatingPolicy.cs`. **Docs Impact:** new `GatingPolicy.cs` (the in-tree policy doc).

---

## Self-review (run before opening the PR)

- **Spec coverage:** lever 3 = Tasks 1–2 (both JIT sweeps split); lever 5 = Task 3 (env gate + constant checkpoint);
  lever 6 = Task 4 (ZEXDOC triage); policy = Task 5. ✔
- **Placeholder scan:** the only `// ...` markers are explicit "move the current body verbatim" / "P2..P7 identical"
  instructions WITH the exact shape shown — acceptable per the writing-plans note (the three-line per-partition
  class is fully specified and the engineer copies it with the index changed); no behavioural TBDs. The reviewer
  should confirm all 8 partition classes are written out in the implementation. ✔
- **Type consistency:** `Partition(int index, int parts)` → `TheoryData<string>` matches base ↔ derived in Tasks 1-2;
  `KlausJitFactAttribute` matches Task 3 Step 1 ↔ Step 2; `ZexdocTriageBudget` / renamed methods consistent. ✔
