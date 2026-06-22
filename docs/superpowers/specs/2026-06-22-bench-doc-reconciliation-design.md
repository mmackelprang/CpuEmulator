# Design — 68000 bench doc-reconciliation (close roadmap #3 truthfully)

**Date:** 2026-06-22
**Queue row:** B68-DOC
**Roadmap:** Deferred & candidate follow-on #3 — "68000 bench-harness cleanups (small, bench-only)"
(`docs/ROADMAP.md:234-237`)
**Status:** Spec (autonomous scoping per owner authorization)
**Scope:** **doc-only.** No source change, no test change of behavior (one assertion ADDED to lock the docs).

---

## 1. Problem

Roadmap deferred-item #3 records two "tracked backlog" 68000 bench cleanups. **Both are stale** — the work
either shipped or was resolved-as-not-a-bug:

- **(a) "the W3 profiler arm — the hot-op profiler covers 68000 W1/W2 but not W3."** This **shipped** in
  commit **`bc68ee7`** ("bench(profiler): add 68000 W3 (sieve) hot-op arm"). The profiler now calls
  `Run68000("W3 sieve-kernel", M68000Workloads.SieveKernel())` at `bench/hotop-profiler/Profiler.cs:215`,
  and the regenerated results carry the `## 68000 — W3 sieve-kernel` block (the W3 ranked table is in
  `bench/results/REPORT.md`, the 68000 W3 sieve-kernel rows). The roadmap claim "covers W1/W2 but not W3" is
  false as of `bc68ee7`.
- **(b) "the W2 cycle off-by-2 — a small cycle discrepancy in the 68000 W2 bench harness."** This is **not a
  bug** — it is **accepted coarse-cycle slack** per **DECISION T2** (the data-axis-exact / coarse-cycle
  68000 stance). The JIT emits real IL charging each descriptor's coarse `BaseCycles + 1` opcode-fetch
  cycle; the interpreter charges its exact per-word prefetch model. The two per-instruction cycle models
  legitimately differ, so the tiers cross the fixed cap on different instruction boundaries (the observed gap
  is exactly 2). The data axis stays byte-identical; only the cycle *count* diverges. This is documented and
  **gated** in `tests/CpuEmulator.Tests/Jit/BenchHarnessSmokeTests.cs:86-128` (the
  `The_two_68000_tiers_run_and_agree_on_the_W2_cycle_count` test): a `<= 16` root-cause-justified tolerance
  (one instruction's worst-case charge), with a comment block citing "ADR 0011 §4 / DECISION T2" and noting
  "forcing exact equality would contradict DECISION T2." So the "off-by-2" is the *expected* outcome, not a
  cleanup to do.

A second copy of the stale claims lives in the user guide:
`docs/user-guide/benchmarks.md:148-150` ("Known benchmark-harness caveats … a 68000 W2 bench-harness cycle
off-by-2 and the 68000 W3 workload's absence from the hot-op profiler arm are tracked backlog items").

The carried-forward Planner finding (this session's grounding) confirms: **no code work** — only a stale-doc
truth to fix.

## 2. Goal & non-goals

**Goal:** update the two stale doc locations so the roadmap and user guide tell the truth — W3 shipped (cite
`bc68ee7`), and the W2 off-by-2 is accepted coarse-cycle slack (cite DECISION T2 / `BenchHarnessSmokeTests`).
Add one un-fakeable assertion that the stale phrasing is gone and the resolution is recorded.

**Non-goals:**

- No source change to `Profiler.cs`, the bench harness, or any CPU. The W3 arm already ships; the off-by-2 is
  already correctly gated.
- No change to the `BenchHarnessSmokeTests` W2 tolerance or comment (it is already correct and is the citation
  target, not the edit target).
- No re-running of benchmarks / regenerating `REPORT.md` (it already has the W3 block).

## 3. The edits (surgical, verbatim-anchored)

**Edit 1 — `docs/ROADMAP.md:234-237`.** Replace the deferred-item-#3 bullet. The current text:

> 3. **[deferred] 68000 bench-harness cleanups (small, bench-only).** (a) the **W3 profiler arm** — the
>    hot-op profiler covers 68000 W1/W2 but not W3; (b) the **W2 cycle off-by-2** — a small cycle discrepancy
>    in the 68000 W2 bench harness (affects the bench number, not interpreter/JIT parity). *(Both tracked
>    backlog.)*

becomes a **[resolved]** item (kept in the list as a closed record, the way the roadmap keeps other
resolved/refuted items like the JIT-overhead investigation): item #3 retitled `[resolved]`, recording that
(a) the W3 arm shipped in `bc68ee7` (profiler arm + the `## 68000 — W3 sieve-kernel` results block) and
(b) the W2 off-by-2 is accepted coarse-cycle slack per DECISION T2, gated by `BenchHarnessSmokeTests`'
`<= 16` tolerance — not a bug, nothing to do. *Decision B68-1: keep the item in the deferred list, retagged
`[resolved]`, rather than deleting it — the roadmap's house style preserves resolved/refuted items for the
record (cf. the `[investigated → refuted + shelved]` JIT-overhead item at `ROADMAP.md:270`).*

**Edit 2 — `docs/user-guide/benchmarks.md:148-150`.** Replace the "Known benchmark-harness caveats" blockquote.
The current text:

> **Known benchmark-harness caveats (not core correctness):** a 68000 W2 bench-harness cycle off-by-2
> and the 68000 W3 workload's absence from the hot-op profiler arm are tracked backlog items (see the
> [Roadmap](../ROADMAP.md)); they affect the bench harness, not the interpreter/JIT parity.

becomes a **resolved** note: the W3 arm shipped (`bc68ee7`; the profiler + `REPORT.md` now carry the 68000 W3
sieve-kernel ranking) and the W2 cycle off-by-2 is the **expected coarse-cycle slack** (DECISION T2 — the JIT
charges coarse `BaseCycles + 1` fetch vs the interpreter's exact prefetch; gated within a `<= 16` tolerance in
`BenchHarnessSmokeTests`), not a harness defect — the data axis is byte-identical. *Decision B68-2: keep a
short note (now framed as resolved) rather than deleting it, so a reader who recalls the old caveat sees the
resolution.*

## 4. The un-fakeable gate

A doc-only change still gets an un-fakeable gate (the project's standard). Add a small test —
`tests/CpuEmulator.Tests/Docs/BenchDocReconciliationTests.cs` (or fold into an existing docs-assertion test if
one exists; Builder checks) — that reads the two doc files and asserts:

1. **The stale phrases are gone:** `docs/ROADMAP.md` no longer contains the substring
   "the hot-op profiler covers 68000 W1/W2 but not W3", and `docs/user-guide/benchmarks.md` no longer
   contains "the 68000 W3 workload's absence from the hot-op profiler arm". *(These are the exact stale
   claims; their disappearance is the un-fakeable "the lie is gone" assertion.)*
2. **The resolution is recorded:** both files now contain a marker tying the resolution to its evidence — the
   commit `bc68ee7` (or the W3-shipped statement) AND a `DECISION T2` / coarse-cycle-slack reference. *(The
   presence of the citation is the un-fakeable "the truth is documented" assertion.)*
3. **The evidence still exists** (so the doc can't cite vanished facts): `bench/hotop-profiler/Profiler.cs`
   contains `Run68000("W3 sieve-kernel"` and `tests/CpuEmulator.Tests/Jit/BenchHarnessSmokeTests.cs` contains
   the `<= 16` W2 tolerance assertion with a `DECISION T2` reference. *(This pins the docs to live source —
   if a future change removes the W3 arm or the gate, this test fails, forcing the doc to re-reconcile.)*

This test is the gate: it fails on the pre-edit docs (stale phrase present → assertion 1 fails) and passes on
the post-edit docs, and it cross-checks that the cited evidence is real. *Decision B68-3.*

## 5. Invariants honored

- **No behavior change:** doc-only + one assertion-only test. No CPU, JIT, bench, or harness code changes.
- **Truthful close:** the roadmap item moves from a false "TODO" to an accurate "resolved" record with
  citations a reader can verify (`bc68ee7`, `Profiler.cs:215`, `BenchHarnessSmokeTests.cs:86-128`, DECISION
  T2 in `docs/superpowers/plans/2026-06-18-m6-pr5-68000-alu-ccr.md:26`).
- **Self-pinning:** the gate ties the doc claims to live source, so the docs can't silently re-rot.

## 6. Dependencies & priority

- **Deps:** none. Independent of W and D68. Can land first or last.
- **Priority:** third of the three (tiny, doc-only) — but trivially parallelizable; it has no code-merge
  conflict surface with W or D68.

## 7. Scoping decisions (recorded)

- **B68-1:** keep roadmap item #3 in the list, retagged `[resolved]` (house style preserves resolved items).
- **B68-2:** keep the benchmarks.md note, reframed as resolved (so the old caveat's resolution is visible).
- **B68-3:** the gate asserts (1) stale phrases gone, (2) resolution + citations present, (3) the cited
  evidence still exists in source.

No cross-cutting architecture; no Architect escalation.
