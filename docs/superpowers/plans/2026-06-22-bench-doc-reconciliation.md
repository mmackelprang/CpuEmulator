# 68000 Bench Doc-Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Update the two stale doc locations so roadmap #3 reads as resolved — the W3 profiler arm shipped (`bc68ee7`) and the W2 off-by-2 is accepted coarse-cycle slack (DECISION T2) — gated by one self-pinning doc-assertion test.

**Architecture:** Doc-only. Edit `docs/ROADMAP.md:234-237` and `docs/user-guide/benchmarks.md:148-150`, then add a single test that asserts (1) the stale phrases are gone, (2) the resolution + citations are present, and (3) the cited evidence still exists in source (so the docs can't silently re-rot).

**Tech Stack:** Markdown + one xUnit v2.9.3 test (C# / .NET 10).

## Global Constraints

- **No behavior change:** doc-only + one assertion-only test. No CPU, JIT, bench, or harness code changes.
- **Keep resolved items in the roadmap list** (house style — cf. the `[investigated → refuted + shelved]` item at `ROADMAP.md:270`), retagged `[resolved]` (decisions B68-1/B68-2).

## Reference (verified verbatim current text)

- `docs/ROADMAP.md:234-237` — the stale item #3 (`[deferred] 68000 bench-harness cleanups`).
- `docs/user-guide/benchmarks.md:148-150` — the stale "Known benchmark-harness caveats" blockquote.
- Evidence the resolution cites: `bench/hotop-profiler/Profiler.cs:215` (`Run68000("W3 sieve-kernel", ...)`); the `## 68000 — W3 sieve-kernel` block in `bench/results/REPORT.md`; `tests/CpuEmulator.Tests/Jit/BenchHarnessSmokeTests.cs:86-128` (the `<= 16` W2 tolerance + the `DECISION T2` comment); DECISION T2 defined in `docs/superpowers/plans/2026-06-18-m6-pr5-68000-alu-ccr.md:26`; the commit `bc68ee7` ("bench(profiler): add 68000 W3 (sieve) hot-op arm").
- Repo-root resolution idiom: `FindRepoRoot()` walking up to `CpuEmulator.slnx` — `tests/CpuEmulator.Tests/Generators/FlagLayoutTests.cs:90-97`.

## File Structure

| File | Responsibility |
|---|---|
| `docs/ROADMAP.md` (modify :234-237) | Retag item #3 `[resolved]`, record W3-shipped + W2-accepted with citations. |
| `docs/user-guide/benchmarks.md` (modify :148-150) | Reframe the caveat blockquote as resolved with the same citations. |
| `tests/CpuEmulator.Tests/Docs/BenchDocReconciliationTests.cs` (create) | The self-pinning gate. |

---

### Task 1: The doc edits

**Files:**
- Modify: `docs/ROADMAP.md:234-237`
- Modify: `docs/user-guide/benchmarks.md:148-150`

- [ ] **Step 1: Edit `docs/ROADMAP.md`** — replace the item-#3 bullet (lines 234-237). Current text:

```markdown
3. **[deferred] 68000 bench-harness cleanups (small, bench-only).** (a) the **W3 profiler arm** — the
   hot-op profiler covers 68000 W1/W2 but not W3; (b) the **W2 cycle off-by-2** — a small cycle discrepancy
   in the 68000 W2 bench harness (affects the bench number, not interpreter/JIT parity). *(Both tracked
   backlog.)*
```

Replace with:

```markdown
3. **[resolved] 68000 bench-harness cleanups (small, bench-only).** Both items are closed. (a) the **W3
   profiler arm** **shipped in `bc68ee7`** — the hot-op profiler now covers 68000 W1/W2/**W3**
   (`bench/hotop-profiler/Profiler.cs` calls `Run68000("W3 sieve-kernel", …)`; the `## 68000 — W3
   sieve-kernel` block is in `bench/results/REPORT.md`). (b) the **W2 cycle off-by-2** is **not a bug** — it
   is the **accepted coarse-cycle slack** of the data-axis-exact / coarse-cycle 68000 stance (**DECISION T2**,
   ADR 0011 §4): the JIT charges each descriptor's coarse `BaseCycles + 1` opcode-fetch vs the interpreter's
   exact per-word prefetch, so the tiers round to different instruction boundaries (the observed gap is 2).
   The data axis stays byte-identical; only the cycle *count* diverges. Gated within a root-cause-justified
   `<= 16` tolerance in `tests/CpuEmulator.Tests/Jit/BenchHarnessSmokeTests.cs`
   (`The_two_68000_tiers_run_and_agree_on_the_W2_cycle_count`) — forcing exact equality would contradict
   DECISION T2.
```

- [ ] **Step 2: Edit `docs/user-guide/benchmarks.md`** — replace the blockquote (lines 148-150). Current text:

```markdown
> **Known benchmark-harness caveats (not core correctness):** a 68000 W2 bench-harness cycle off-by-2
> and the 68000 W3 workload's absence from the hot-op profiler arm are tracked backlog items (see the
> [Roadmap](../ROADMAP.md)); they affect the bench harness, not the interpreter/JIT parity.
```

Replace with:

```markdown
> **Benchmark-harness notes (resolved; not core correctness):** the 68000 W3 hot-op profiler arm **shipped**
> (`bc68ee7` — the profiler + `bench/results/REPORT.md` now carry the 68000 W3 sieve-kernel ranking), and the
> 68000 W2 cycle off-by-2 is the **expected coarse-cycle slack** (DECISION T2: the JIT charges coarse
> `BaseCycles + 1` fetch vs the interpreter's exact prefetch; gated within a `<= 16` tolerance in
> `BenchHarnessSmokeTests`), **not** a harness defect — the data axis is byte-identical.
```

- [ ] **Step 3: Verify the stale phrases are gone (manual grep)**

Run: `grep -rn "covers 68000 W1/W2 but not W3" docs/ ; grep -rn "absence from the hot-op profiler arm" docs/`
Expected: **no matches** (both stale phrases removed).

- [ ] **Step 4: Commit the doc edits**

```bash
git add docs/ROADMAP.md docs/user-guide/benchmarks.md
git commit -m "docs: reconcile 68000 bench cleanups (W3 shipped bc68ee7; W2 off-by-2 = DECISION T2 slack)"
```

---

### Task 2: The self-pinning doc-assertion gate

**Files:**
- Create: `tests/CpuEmulator.Tests/Docs/BenchDocReconciliationTests.cs`

**Interfaces:**
- Consumes: the two doc files + the three evidence sources, read from disk via `FindRepoRoot()`.

- [ ] **Step 1: Write the gate**

```csharp
using System.IO;
using Xunit;

namespace CpuEmulator.Tests.Docs;

/// <summary>Pins the 68000 bench doc-reconciliation (roadmap #3): the stale claims are gone, the resolution +
/// citations are present, and the cited evidence still exists in source — so the docs can't silently re-rot.
/// Doc-only gate (B68-DOC); no behavior under test.</summary>
public class BenchDocReconciliationTests
{
    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), relative));

    [Fact]
    public void The_stale_W3_missing_claims_are_gone()
    {
        string roadmap = Read("docs/ROADMAP.md");
        string bench = Read("docs/user-guide/benchmarks.md");
        // The exact stale phrasings (verified present pre-edit) must be absent post-edit.
        Assert.DoesNotContain("covers 68000 W1/W2 but not W3", roadmap);
        Assert.DoesNotContain("absence from the hot-op profiler arm", bench);
    }

    [Fact]
    public void The_resolution_and_its_citations_are_recorded()
    {
        string roadmap = Read("docs/ROADMAP.md");
        string bench = Read("docs/user-guide/benchmarks.md");
        // Both docs tie the resolution to its evidence: the W3-shipped commit AND the DECISION T2 reference.
        Assert.Contains("bc68ee7", roadmap);
        Assert.Contains("DECISION T2", roadmap);
        Assert.Contains("bc68ee7", bench);
        Assert.Contains("DECISION T2", bench);
    }

    [Fact]
    public void The_cited_evidence_still_exists_in_source()
    {
        // If a future change removes the W3 arm or the W2 gate, this fails — forcing the doc to re-reconcile.
        string profiler = Read("bench/hotop-profiler/Profiler.cs");
        Assert.Contains("Run68000(\"W3 sieve-kernel\"", profiler);

        string benchSmoke = Read("tests/CpuEmulator.Tests/Jit/BenchHarnessSmokeTests.cs");
        Assert.Contains("DECISION T2", benchSmoke);
        Assert.Contains("<= 16", benchSmoke);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CpuEmulator.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
```

> **Implementer note:** `The_cited_evidence_still_exists_in_source` reads `Run68000("W3 sieve-kernel"` — confirm
> the exact spelling in `Profiler.cs:215` after checkout (it is `Run68000("W3 sieve-kernel", M68000Workloads.SieveKernel());`).
> The `<= 16` substring must match `BenchHarnessSmokeTests.cs`'s assertion literal (`Math.Abs(...) <= 16` — note
> the spaces; if the source uses `<=16` without spaces, match that). Read both lines and pin the exact substring
> before finalizing.

- [ ] **Step 2: Run it, verify the FIRST assertion fails against pre-edit docs (TDD red proof)**

To prove the gate is un-fakeable: temporarily `git stash` the Task-1 doc edits, run the test —
`The_stale_W3_missing_claims_are_gone` FAILS (the stale phrase is present). Restore the edits (`git stash pop`).

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BenchDocReconciliationTests"`
Expected (with edits applied): PASS (all 3 tests).

- [ ] **Step 3: Whole-solution build warning-clean**

Run: `dotnet build -c Release`
Expected: warning-clean.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Docs/BenchDocReconciliationTests.cs
git commit -m "test(docs): self-pinning gate for the 68000 bench reconciliation"
```

---

## Self-Review

- **Spec coverage:** §3 Edit 1 (ROADMAP) → Task 1 Step 1. §3 Edit 2 (benchmarks.md) → Task 1 Step 2. §4 (the un-fakeable gate: stale-gone / resolution-present / evidence-exists) → Task 2's three `[Fact]`s. §5 (invariants: no behavior change, truthful close, self-pinning) → Global Constraints + Task 2. Decisions B68-1/B68-2 (keep retagged in-list) → Task 1's replacement text retains the items as `[resolved]`. B68-3 (gate asserts all three) → Task 2. No gaps.
- **Placeholder scan:** no TODOs; the one implementer-note gives an exact substring to confirm, not a deferral.
- **Type consistency:** `FindRepoRoot()` and `Read(string)` consistent across the three tests; the `FindRepoRoot` body is copied verbatim from the shipped `FlagLayoutTests.cs:90-97` idiom.
