# Test-Infrastructure Speedup Arc — Overview & Sequencing

> **For agentic workers:** REQUIRED SUB-SKILL when implementing any PR in this arc: use
> `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans`,
> task-by-task. Steps use checkbox (`- [ ]`) syntax.
> **This is the index doc.** The four PR plans live beside it:
> `2026-06-18-test-speedup-pr-t1-parse-cache-sampling.md`,
> `…-pr-t2-per-worker-pooling.md`,
> `…-pr-t3-parallelize-jit-and-gating.md`,
> `…-pr-t4-jit-reuse.md`.

**Goal:** Cut the verification-suite wall-clock (currently ~most of a day to merge one PR) by an order of
magnitude, with ZERO loss of coverage on the routine path and the full exhaustive gate still reachable via env.

**Diagnosis owner:** approved the full lever set (1–7). This arc groups them into four PRs ordered by
leverage × independence, serializing the ones that touch the same files.

---

## Sequencing (binding)

```
PR-1 (M6 Z80 LD emit, IN FLIGHT)  ──merges──►  [this arc: T1 → T2 → T3 → T4]  ──►  M6 PR-2…PR-6
```

This arc lands **AFTER M6 PR-1 merges** (avoids churn on the test runners while PR-1 is changing emit
behaviour) and **BEFORE M6 PR-2…PR-6** (so the rest of M6 is developed on the fast suite). Within the arc:

| PR | Scope | Levers | Size | Depends on | Touches (ownership) |
|----|-------|--------|------|------------|---------------------|
| **T1** | Path-keyed parse cache + parse-only-sampleSize + skip carried 8088 cycle tuples + unify the 4 sample resolvers + lower default to 100 | **1, 7** | M | PR-1 merged | `tests/.../TomHarte/*TomHarteCase.cs` (loaders), `*TomHarteVectors.cs` + the 2 inline resolvers, `*Tests.cs` loop call sites |
| **T2** | Port the 68000 `[ThreadStatic]` arena pattern to 6502/Z80/8088 runners + pool the `AddressSpace` page table + pool the JIT Fastmem/BlockCache containers | **2** | L | T1 (shared loop edits land first to avoid conflict) | `tests/.../TomHarte/*Runner.cs`, a small reset seam on `AddressSpace` + `JittedCpu`/`BlockCache` |
| **T3** | Split the 8088 + 68000 JIT sweeps into per-file classes (mirror the interpreter split) + env-gate Klaus heavy JIT run + derive Klaus checkpoint from the constant + downgrade ZEXDOC to a triage pre-check + encode the gating policy | **3, 5, 6** | M | T1 (shares `ResolveSampleSize`) | `M8088JitTomHarteTests.cs`, `M68000JitTomHarteTests.cs`, `KlausJitFunctionalTests.cs`, `ZexallTests.cs`, `Z80ZexJitTests.cs`, `KlausVectors.cs` (new `KlausFact` env arm) |
| **T4** | Reuse `JittedCpu`/`BlockCompiler` per worker thread, reset cache+dirty+chains between cases | **4** | L (most invasive) | **T2 + T3** | a NEW `BlockCache.FlushAll()` + `JittedCpu.ResetForReuse()` seam in `src/CpuEmulator.Jit/`, the 4 `*JittedCpuFactory.cs` + JIT runner paths |

### Why T1 is first

Highest leverage per unit risk and the only PR that is **pure win, zero coverage cost, zero production code**:
the parse cache removes the dominant cost (every loader today decompresses + JSON-parses the ENTIRE
8k–10k-case file though only `sampleSize` run, and the SAME file is re-parsed by the interpreter sweep, the JIT
sweep, and — on the 68000 — 5–8 axis classes). Parsing only `sampleSize` cases and caching by path collapses
that. It also unblocks T2/T3 cleanly because it touches the loaders + sampling, which T2 (runners) and
T3 (JIT-sweep classes) do not.

### Why T4 is last

Lever 4 (per-worker JIT reuse) is the most invasive and the only one with a real **correctness hazard**
(below). It depends on T2's pooling (the reused JIT must point at a reusable, re-zeroed bus) and on T3's
per-file JIT-sweep split (reuse is *per worker thread*, which only exists once the sweep is parallelized).
Doing it last means the speedup is already large and T4 is a bounded, well-gated increment.

---

## The lever-4 block-cache-isolation hazard (READ BEFORE T4)

Verified against `main` @ `896f88b`:

- `JittedCpu<TCpu>` builds `new Fastmem`, `new BlockCache`, `new BlockCompiler` per construction
  (`src/CpuEmulator.Jit/JittedCpu.cs:76-78`).
- `BlockCache<TCpu>` holds `_blocks` keyed by **`ushort pc`**, plus `_blocksByPage`, a `DirtyMap`, and a
  `ChainTable` (`src/CpuEmulator.Jit/BlockCache.cs:25-31`).
- The TomHarte dispatch cache key is `(ushort)IP` (the 8088 runner doc even calls this out,
  `M8088TomHarteRunner.cs:115-119`).
- **The only existing reset is `JittedCpu.Reset() => _inner.Reset()` (`JittedCpu.cs:91`) — it resets the inner
  CPU ONLY, not the block cache.** There is NO public seam to flush `_blocks`/`_blocksByPage`/`Chains` today.

**The hazard:** across cases the same `(ushort)IP` is reused with DIFFERENT instruction bytes. If a reused
`JittedCpu` keeps a cached block compiled from case A's bytes, case B at the same IP would execute case A's
code → silent wrong-answer (or a green-but-vacuous gate). T4 MUST add an explicit `BlockCache.FlushAll()` (clear
`_blocks`, `_blocksByPage`, `Chains`, and the `DirtyMap`) AND rebuild Fastmem against the re-zeroed/re-installed
bus, called between every case. T4's coverage gate is therefore a **byte-for-byte equivalence proof**: the
reused-JIT sweep must produce the identical executed/deferred/excluded counts and identical pass/fail as the
fresh-JIT sweep on the same corpus (run both, diff). If T4 cannot prove equivalence, it is dropped and the arc
still banks T1–T3.

---

## Shared gates (every PR in this arc applies BOTH)

### Measurement gate (prove the speedup)

Each PR plan ends with a measurement task that captures a **suite-subset wall-clock + allocation/GC** baseline
BEFORE the change and again AFTER, on the same machine, same `-c Release`, same sample size. The canonical probe
commands (Windows / Git Bash) — record the numbers in the PR body:

```bash
# Wall-clock of a representative subset (the subset each PR names in its own measurement task).
# Example for the 8088 JIT sweep filter:
time dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~M8088JitTomHarteTests" --no-restore

# Allocation/GC: run the same subset under the GC stats the runner already prints, or wrap with
# DOTNET_gcServer=1 and capture the test host's peak working set + Gen0/1/2 counts. The simplest
# portable signal this repo already supports is the per-test output line each sweep writes
# (e.g. "ran N, executed M …") plus the `dotnet test` elapsed total — capture both.
DOTNET_gcServer=1 time dotnet test … --filter "<subset>" --no-restore
```

Record in the PR body, as a table: **subset | before wall-clock | after wall-clock | speedup | before-after
notes on allocation/GC**. The gate is **after < before** on the named subset (each PR sets a target, e.g.
T1 ≥ 5× on a single-file sweep, T2 measurable Gen0 reduction, T3 ≥ Nx on the JIT sweep where N≈thread count).

### Coverage-preservation gate (prove no coverage lost)

1. **The same tests still pass.** `dotnet test -c Release` green at the routine sample on the changed subset.
2. **Executed-count parity.** Every sweep prints a `ran R, executed E, deferred D…` line. For levers that do
   NOT change coverage (1, 2, 3, 4), the `executed`/`deferred`/`excluded` counts at a FIXED sample
   (`CPUEMULATOR_TOMHARTE_SAMPLE=200`) MUST be **byte-identical before and after** — capture both, diff them.
3. **Full gate still reachable.** For the sampling/gating levers (7, 5, 6), the PR documents the exact coverage
   delta on the fast path AND proves the exhaustive gate still runs:
   `CPUEMULATOR_UAT=full` (full per-file sweep) and `CPUEMULATOR_ZEX=full` (full ZEX) still execute and pass.

---

## Gating policy (encode explicitly — PR-T3 writes this into a doc comment + the new env arms)

| Workload | Per-PR (routine CI/local) | Periodic / pre-arc / pre-merge |
|----------|---------------------------|--------------------------------|
| TomHarte interpreter sweeps | sampled (default **100** after T1) | `CPUEMULATOR_UAT=full` (full per-file) |
| TomHarte JIT sweeps | sampled, **parallel per-file** (after T3) | `CPUEMULATOR_UAT=full` |
| Klaus interpreter pin | every run (fast, the oracle) | — |
| Klaus **JIT** functional run | **gated** `CPUEMULATOR_KLAUS=full` (after T3) | run pre-arc / pre-merge |
| ZEX smoke (wiring) | every run (~1.3 s) | — |
| ZEXDOC full | **triage pre-check** (bounded, fail-fast) | within `CPUEMULATOR_ZEX=full` |
| ZEXALL full (interp + JIT) | gated `CPUEMULATOR_ZEX=full` | the real composition gate, pre-arc / pre-merge |
| Differential fuzzer | every run (covers JIT each run) | — |

**Policy rationale (PR-1 precedent):** PR-1 established that *full ZEXDOC-through-JIT is a periodic / pre-arc
gate, not per-PR*. T3 encodes that: the heavy JIT exercisers (Klaus-JIT, ZEXDOC/ZEXALL-JIT) move behind env
gates; the per-run JIT coverage is carried by the differential fuzzer + the sampled JIT TomHarte sweeps + the
interpreter Klaus pin, all of which still run every invocation.

---

## Diagnosis verification result (against `main` @ `896f88b`)

All seven levers' file:line facts were spot-checked and **held up**, with three refinements the PR plans
incorporate:

1. **Lever 5 (Klaus) gating:** `[KlausFact]` (`KlausVectors.cs`) gates on **binary presence only**, NOT env. So
   on any dev/CI machine that has the Klaus binary cached, the heavy JIT Klaus run fires every invocation — the
   "~330M cycles every local run" cost is real for those machines. T3 adds the env gate.
2. **Lever 6 (ZEX):** both `ZexallTests` and `Z80ZexJitTests` **already** env-gate their full runs to
   `CPUEMULATOR_ZEX=full` (`ZexallTests.cs:27-28,62-66,73-77`). So per-PR, ZEX is already cheap. The remaining
   waste is *within* the full gate (ZEXDOC interp+JIT both run a full ~130s pass though ZEXDOC ⊂ ZEXALL). T3's
   lever-6 work is therefore an intra-full-gate optimization (downgrade ZEXDOC to a bounded triage pre-check),
   not a per-PR saving — scoped accordingly.
3. **Lever 4 (JIT reuse):** there is **NO existing block-cache flush seam** (`JittedCpu.Reset()` resets only the
   inner CPU). T4 must add one; this is the central hazard and is why T4 is last and gated on an equivalence proof.

Everything else verified exactly: parse-all (`M8088TomHarteCase.cs:114-123`), carried cycle tuples
(`:210-225`), the 4 per-case 8088 allocations (`M8088TomHarteRunner.cs:37-38,124-125,208-209,292-293`), the
64 KB 6502/Z80 allocations (`TomHarteRunner.cs:22-23,88-89`; `Z80TomHarteRunner.cs:27-28,107-108`), the
`[ThreadStatic] _ramArena` reference (`M68000TomHarteRunner.cs:107,134-135`), `AddressSpace.cs:45` page table,
`Fastmem.cs:26-28`, `JittedCpu.cs:76-78`, `BlockCache.cs:25-27`, the single-`[Theory]` JIT sweeps
(`M8088JitTomHarteTests.cs:34-35`, `M68000JitTomHarteTests.cs:31-32`), `xunit.runner.json` (16 threads,
parallel on), the duplicated sample caps (`Mos6502TomHarteTests.cs:57-60`, `Z80TomHarteTests.cs:117-120`,
centralized `M68000TomHarteVectors.cs:34-38`, `M8088TomHarteVectors.cs:37-41`), Klaus
(`KlausJitFunctionalTests.cs:26,44-46`), ZEXALL⊃ZEXDOC (`ZexallTests.cs:8-11`), and the 200 default everywhere.
