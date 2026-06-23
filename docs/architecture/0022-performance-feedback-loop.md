# ADR 0022 — The performance feedback loop (the aggregate/improvement engine for the emulated computers)

> **Status:** PROPOSED (2026-06-23). Owner review + approval required before any Planner/Builder work.
> This is the **strategic** ADR for the owner's standing ask — *"enable a feedback loop to improve overall
> performance of the various emulated computers"* — across **both** execution tiers (JIT emit + the
> interpreter/translation tier) and the **whole-system** layer (real-time ratio, per-peripheral cost,
> allocations). It is a measurement-and-prioritization design, not a single optimization: it makes the
> M6 profiling discipline (ADR 0011 §6) a **standing loop** that runs over **real workloads** (the live
> DOS/CP/M/Pascal/Spectrum boots, not just the bench kernels), persists profiles, ranks candidates by a
> concrete **hotness × cost** ROI model, and gates every act on byte-identical parity + a before/after
> benchmark. It is the forcing function the ROADMAP's parked items (#2 cycle-exact 68000, #5 per-bank
> specialization + the generic emitter, L JIT-under-translation) were explicitly waiting for.
> **Date:** 2026-06-23
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Supersedes / relates to:**
> - **ADR 0011** (`0011-jit-hot-op-emission-optimization.md`) — the M6 emit design. §6 (the throwaway
>   hot-op profiler) and §5 (the frozen-workload re-measure loop) are the **seed** of this ADR. ADR 0011's
>   profiler is interpreter-only, kernel-only, and throwaway; ADR 0022 generalizes it into a standing,
>   real-workload, JIT-aware engine **without forking it** (Decision 2). The emit-vs-fallback boundary,
>   the profiling-ranked ROI, and the oracle-as-safety-net invariants are **inherited unchanged**.
> - **ADR 0012** (`0012-jit-dirty-page-list-invalidation.md`, REJECTED) — the load-bearing **measurement
>   discipline**. ADR 0012's premise (the 256-bool invalidation scan was the ~140× Klaus floor) was
>   *refuted by measurement* (the scan is ~1.3% of runtime; the real floor is the dispatcher round-trip +
>   `ResolveChain` per-edge + `Evict` dict churn). This ADR's loop is built so that **the same refutation
>   would happen automatically and cheaply** — the ROI model ranks on *measured* cost, never a believed
>   cost model (§4, the anti-0012 guard).
> - **ADR 0013** (`0013-per-bank-block-specialization.md`, PROPOSED, parked as ROADMAP #5a) and the
>   ROADMAP parked items #2 / #5 / L — these are *candidates the loop ranks*, not commitments. This ADR
>   does not relitigate them; it gives them the **measured forcing function** their parked status names as
>   missing ("no current measured bottleneck", "no cycle-sensitive consumer", "revisit on a concrete
>   consumer"). The first backlog (§7) shows where each lands today.
> - **The perf-overlay HUD design handoff** (`docs/design-handoffs/2026-06-23-perf-overlay.md`, being built
>   on branch `feat/perf-overlay-hud`) — the **LIVE** view. It is a subset of this ADR's metric set,
>   surfaced per-session at ~3 Hz, ephemeral, display-only. ADR 0022 is the **AGGREGATE/improvement** view
>   over the same underlying counters. Decision 3 makes them share one metrics source so they never drift.
> - **ADR 0009** (`0009-device-jit-contract-and-peripheral-design.md`) — the fastmem split + the coarse/fine
>   device-timing tier. Per-peripheral cost (a §3 metric) is the input that would justify promoting a hot
>   peripheral's tick path; the loop measures it before anyone optimizes it.
> - **The bench harness** (`bench/`, `bench/results/comparison.json` + `REPORT.md`, the `ITierDriver` seam)
>   — the **before/after speedup gate**. ADR 0022's loop *acts through* this harness (the honesty gate);
>   it does not build a parallel benchmark.

---

## 1. Context

### 1.1 The ask, stated as an engineering problem

The owner wants the emulated computers to *get measurably faster over time*, on both tiers, via a
**loop** — not a one-off optimization and not a dashboard. The M6 arc (ADR 0011) already proved the
*shape* of one turn of that loop for the JIT emit axis: profile (the §6 hot-op histogram) → rank (the
86–100%-in-the-top-8 finding) → act (emit the hot families, parity-gated) → re-measure (the frozen
W1/W2/W3 comparison columns). It worked: the Z80 JIT went from 0.45× of its own interpreter to *exceeding*
it on W2; the 6502 W1 Klaus thrash collapsed ~6.8×.

But that loop was **hand-cranked, one-shot, and narrow**:

1. **It only saw the bench kernels.** The hot-op profiler (`bench/hotop-profiler/Profiler.cs`) runs the
   tier-0 interpreter over the *synthetic* W1/W2/W3 workloads. It never profiled a **real boot** — DOS 3.3,
   CP/M, Apple Pascal, the Spectrum ROM. The real machines (the thing the owner actually cares about) have
   never been profiled. Their hot ops, their hot peripherals, their real-time ratio, and their SMC/bank
   pressure are *unmeasured*.
2. **It was JIT-emit-only.** The interpreter/translation tier (the dual-CPU SoftCard's 6-branch
   `SoftCardTranslation` table, the per-instruction interpreter dispatch cost) and the **system** layer
   (per-peripheral frame cost, allocations) were never instrumented. ADR 0015 made the SoftCard coprocessor
   interpreter-only *by design*; whether that path is hot enough to matter has never been measured.
3. **It was throwaway.** The profiler "is NOT part of any runtime/test graph"; its output
   (`hotop-profile-results.txt`) is a single committed text dump with no schema, no per-system×workload
   structure, no time series, no diffability across runs. There is no *persisted* profile to compare a
   "before" against an "after" except the bench `comparison.json`.
4. **It had no explicit ROI model.** The 86–100% cumulative finding *was* a hotness ranking, but "cost"
   (how expensive a candidate is to act on, and how much it would buy) was a human judgement per PR, not a
   formula that auto-produces a ranked backlog. ADR 0012 is the cautionary tale: a *believed* cost model
   ("the scan is the floor") drove work that measurement then refuted. A loop must rank on *measured* cost.

The M6 arc finished (ROADMAP: the queue is empty; #2/#5/L are *parked for lack of a forcing function*).
**This ADR is that forcing function** — the standing loop that decides what to optimize next, on evidence,
and proves it moved the needle.

### 1.2 What already exists (verified, file:line) — the foundation the loop extends

The loop is mostly *plumbing existing counters into a standing, persisted, real-workload harness*. The
hard parts (the counters, the bench gate, the oracle discipline, the live-overlay seam) already exist.

**JIT counters (run-lifetime, already computed):**
- `BlockCache.TotalRecompiles` / `TotalEvictions` (`src/CpuEmulator.Jit/BlockCache.cs:40-41`) — the
  SMC/recompile churn. `SmcHotPcCount` (`:42`, `=> _everHotPcs.Count`). `CompileCount` (on the compiler).
  These are the PR-S "quantify first" artifacts (ADR 0011 §3.4).
- `BlockCompiler.FallbackEmitCount` (`src/CpuEmulator.Jit/BlockCompiler.cs:31`, **per-Compile**, resets) —
  the "this block emitted N fallback callouts" test seam. **This is the top-ROI JIT signal (ADR 0011 §2)
  but it is per-block and per-compile, not a run-lifetime per-opcode histogram** (§3 names the gap).
- The per-family `*EmitSelections` counters (`BlockCompiler.cs:40-105`: `M68kMoveEmitSelections`,
  `M68kAluEmitSelections`, …, `M8086InterruptEmitSelections`) — accumulate across Compiles, prove "this
  family's emit arm actually ran." **Internal/test-only, per-family (not per-opcode), counted at compile
  time (not weighted by execution frequency).**
- `ICpuCore.CycleCount` (`src/CpuEmulator.Core/ICpuCore.cs:13`) — the monotonic guest cycle counter, the
  basis for cycles/sec and the real-time ratio.

**The live-overlay seam (in-flight on `feat/perf-overlay-hud`):**
- `IJitMetrics` (`src/CpuEmulator.Core/IJitMetrics.cs`) — a **non-generic public** view exposing the four
  JIT counters; `Cpu is IJitMetrics` is the tier test. `Machine.IsJitted` / `Machine.JitMetrics` /
  `Machine.NominalClockHz` / `Machine.AddressSpaceBytes` / `Machine.CoprocessorActive` /
  `Machine.Coprocessor` are the additive read-only Machine seams. `PerfPusher` computes the cycles/sec rate
  from `CycleCount` deltas. **This is the live half of the loop's collection layer — already being built.**

**The bench before/after gate:**
- `ITierInstance` (`bench/CpuEmulator.Benchmarks/ITierDriver.cs`) **already has a guest-instruction-retired
  counter** — `InstructionCount` (each `Step()` / budget-1 `JittedCpu.Run` is one instruction). **This is
  the very counter the live overlay deferred** (`PerfStats.cs`: "ips … deferred to a follow-on … Architect
  call"). It exists at the *bench* layer but not on the *runtime* `ICpuCore` — a precise, small unification
  (§3.1, Backlog item B).
- `bench/results/comparison.json` (schemaVersion 1) — the persisted per-CPU × per-workload × per-tier
  cycles/sec + guest-MIPS, the "optimized JIT column" (ADR 0011 §5). The frozen W1/W2/W3 constants are law.

**The throwaway profiler:** `bench/hotop-profiler/Profiler.cs` — interpreter-only, kernel-only, top-15 hot
mnemonic histogram per CPU × workload, recovered via the generated decode (`IJitTarget.Decode` →
`DescriptorFor` → `Mnemonic`; the 68000's empty descriptor table recovered via the field-grammar scan).

### 1.3 The two views, and why they must not duplicate

| | **Live overlay** (`feat/perf-overlay-hud`) | **Aggregate engine** (this ADR) |
|---|---|---|
| Audience | the user watching a boot, right now | the developer/agent deciding what to optimize next |
| Cadence | ~3 Hz, in-session | offline runs, persisted, diffed across commits |
| Granularity | 8 summary rows (board/fps/guest/ips/mem/tier/jit/cpu2) | full histograms (hot-PC, fallback-by-opcode, per-peripheral) |
| Lifetime | ephemeral (frozen on disconnect) | committed profiles + a ranked backlog |
| Mutates? | no (display-only) | no (measurement-only; acting is a separate gated PR) |

They share **one underlying source of truth** (Decision 3): the same counters, the same `IJitMetrics`-style
seams, the same rate math. The overlay is a *projection* (the cheap, always-on subset); the engine is the
*full capture + persistence + ranking*. Building them on one source is the whole reason this ADR coordinates
with the in-flight overlay rather than ignoring it.

---

## 2. The decisions (overview)

1. **Decision 1 — the metric set + a tiered collection-cost contract.** A concrete catalogue across JIT /
   interpreter-translation / system, each tagged *exists | extend | new*, each with a collection-cost class
   and a toggle. The hard rule: **zero cost when off; sampling, not exact counting, for any hot-path
   metric** (§3).
2. **Decision 2 — collection + persistence: extend the hot-op profiler into a standing `bench/profiler/`
   that profiles REAL workloads, emits a versioned `profile.json`, and persists per system×workload.** Do
   NOT fork it; promote it from throwaway to a real (still non-shipping) tool (§4 mislabeled — see below).
3. **Decision 3 — one metrics source feeds both the live overlay and the aggregate engine.** A small
   `CpuEmulator.Core`-side metrics surface (extending the in-flight `IJitMetrics`) that both consumers read;
   the engine adds capture + persistence, the overlay adds projection (§5).
4. **Decision 4 — the ROI model: `score = hotness × unit_cost × headroom`, ranked into an auto-produced
   backlog, with the decision rules per lever (emit-this-opcode / add-SMC-or-bank-lever / optimize-this-
   peripheral / is-L-worth-it).** Measured cost only — the anti-ADR-0012 guard (§6).
5. **Decision 5 — the loop: profile → rank → act (parity-gated + before/after-benchmarked) → re-measure →
   repeat, tied to the bench harness and the live overlay, with the measurement traps named** (§6.4).
6. **Decision 6 — invariants (carried, non-negotiable): the interpreter is always the oracle/fallback;
   emit stays a pure perf dial (byte-identical); Core stays AOT-clean; all instrumentation is
   removable/zero-cost in shipping paths** (§8).

> Numbering note: §3 = Decision 1, §4 = Decision 2, §5 = Decision 3, §6 = Decisions 4+5, §8 = Decision 6.

---

## 3. Decision 1 — the metric set + the collection-cost contract

The catalogue. Each metric: **what it answers**, **source**, **status** (*exists* / *extend* / *new*), and
**collection cost** with its toggle. The governing rule (the anti-regression guarantee, §8 invariant 4):

> **A metric is one of: (a) FREE — a counter already incremented on a path that runs regardless (e.g.
> `CompileCount`); (b) SAMPLED — read at a coarse cadence (per-frame, per-N-dispatches, or via a periodic
> tick), never per-instruction; or (c) OFFLINE — computed only in the non-shipping profiler tool over a
> replay, never in any normal run. NO metric may add per-instruction work to a shipping hot path. Any
> metric that would is OFFLINE-only.**

This is the lesson of the whole codebase's emit discipline applied to instrumentation: the hot path stays
pristine; measurement lives in counters that are free, samples that are coarse, or an offline replay tool.

### 3.1 JIT-tier metrics

| Metric | Answers | Source | Status | Cost / toggle |
|---|---|---|---|---|
| **Hot-PC / hot-block histogram** | which blocks dominate execution → where emit + chaining pay off | run-lifetime; the offline profiler counts block entries by `(CPU, PC)`; live, a sampled block-entry counter | **new (offline) + extend (live sampled)** | OFFLINE exact (replay); live = a sampled counter behind `JitOptions.ProfileBlockEntries` (default off) |
| **Fallback-emit BY OPCODE** (the top-ROI signal, ADR 0011 §2) | the single highest-value "what to emit next" list, **weighted by execution frequency** not just compile count | today only `FallbackEmitCount` (per-block, `BlockCompiler.cs:31`) + per-family `*EmitSelections` (`:40-105`); neither is per-opcode × execution-weighted | **extend** | OFFLINE exact (the profiler attributes each *executed* fallback to its opcode via the descriptor); FREE coarse summary at compile (`FallbackEmitCount` already there) |
| **Recompile / eviction churn** | SMC + bank thrash (the W1 Klaus axis, ADR 0011 §3.4) | `TotalRecompiles` / `TotalEvictions` / `SmcHotPcCount` (`BlockCache.cs:40-42`) | **exists** | FREE (already counted) |
| **Chain-resolution vs dispatcher round-trips** (the ~140× Klaus floor, ADR 0012) | THE refuted-and-rediscovered floor: how often a hot path round-trips to the dispatcher instead of chaining | none today — `ResolveChain` (`BlockCache.cs:104`) and the chain-break gates exist but are **uncounted** | **new** | FREE (two counters: chain-edges-taken vs dispatcher-entries) incremented on paths that already run |
| **Emit coverage per CPU** | what fraction of *executed* instructions ran emitted IL vs fell back | derivable from the fallback-by-opcode histogram + the hot-op histogram | **new (offline, derived)** | OFFLINE (a ratio over the profiler's two histograms) |

The **chain-vs-dispatcher counter is the most important new JIT metric** — it directly measures ADR 0012's
*real* floor (which was found by accident, after a wrong cost model wasted a PR). Two free counters
(`_chainEdgesTaken`, `_dispatcherEntries` on `JittedCpu`) make that floor a *standing* number, so the next
person who wonders "why is SMC-heavy code slow on the JIT" reads it instead of guessing.

The **fallback-by-opcode histogram is the top-ROI feed** (Decision 4): it is the execution-frequency-weighted
version of the per-family `*EmitSelections` counters — "opcode `0x62` (BOUND) fell back 4.1M times this boot"
is a directly actionable emit candidate; "the MOV family selected emit 900 times" (the current counter) is not.

### 3.2 Interpreter / translation-tier metrics

| Metric | Answers | Source | Status | Cost / toggle |
|---|---|---|---|---|
| **Hot-op histogram (interpreter)** | the interpreter's own hot ops on REAL workloads (not just kernels) | the existing profiler's mnemonic histogram, **re-pointed at real boots** | **extend** (profiler exists; real workloads are new) | OFFLINE exact |
| **SoftCard 6-branch translation-table hit counts** | is the interpreter-only coprocessor translation (ADR 0015) hot enough to matter? (the L "JIT-under-translation" forcing-function) | none — `SoftCardTranslation`'s 6 branches are **uncounted** | **new** | OFFLINE exact (counter per branch, profiler-build only) or SAMPLED behind a flag |
| **Per-instruction dispatch cost** | the interpreter's dispatch overhead per op family (the "0.13× of Musashi is dispatch" datum, ADR 0011 §1.1) | bench micro-measurement (BenchmarkDotNet `--bdn`) | **exists (bench) / extend** | OFFLINE (bench), never in-run |

The **SoftCard translation-table hit counts are the forcing function for ROADMAP item L** (JIT-under-
translation, parked: "CP/M is not perf-critical … revisit only on a perf-critical coprocessor workload").
The loop *measures whether it is perf-critical* — if a real CP/M boot spends, say, <2% of instructions in the
translation branches, L stays parked with **a number behind the decision** instead of an assumption.

### 3.3 System-level metrics

| Metric | Answers | Source | Status | Cost / toggle |
|---|---|---|---|---|
| **Real-time ratio per system** | is the machine keeping authentic speed? (the headline "how's perf" signal) | `cycles/sec ÷ NominalClockHz` — the overlay's row 3 math (`PerfPusher` + `Machine.NominalClockHz`) | **exists (live, in-flight)** | FREE / SAMPLED (overlay already does it at 3 Hz) |
| **Per-peripheral frame cost** | which device's tick/render dominates a frame (ULA render? Videx CRTC? Disk II LSS?) | none — peripherals tick inside the scheduler with no per-device timing | **new** | SAMPLED (a per-peripheral stopwatch behind `MachineHost` profiling flag, per-frame not per-tick) or OFFLINE (replay a frame with per-device timing) |
| **Allocations / GC** | is a hot path allocating per-instruction/per-frame? (a silent throughput sink) | none in-run; BenchmarkDotNet `--bdn` reports allocations per op offline | **new (offline) / extend (bench)** | OFFLINE (BDN `[MemoryDiagnoser]`) + a SAMPLED `GC.GetTotalAllocatedBytes()` delta per profiler window |

**Per-peripheral frame cost** is the system-tier analogue of fallback-by-opcode: it tells the loop *which
peripheral to optimize* (a §7 candidate), the same way fallback-by-opcode tells it *which opcode to emit*.
It is SAMPLED (per-frame, ~50–60 Hz, behind a flag), never per-tick — a per-tick stopwatch would itself
dominate the very cost it measures.

### 3.4 The toggle architecture (one switch family, default-off)

All non-FREE collection lives behind a single options surface so "is the machine instrumented?" is one
answer, and the shipping default is *no*:

- **`JitOptions`** already exists (it carries `DisableSmcLever`, `SmcCooldownDispatches`,
  `UseLegacyFullScanInvalidation`). Add `ProfileBlockEntries` (the sampled hot-block counter) and
  `CountChainVsDispatch` (default **on** for the two chain/dispatch counters — they are FREE, so they cost
  nothing and the ADR-0012 floor is always visible; flag exists only to A/B their negligibility).
- **A new `ProfilingOptions`** (system-tier) for the SAMPLED per-peripheral + allocation-delta capture,
  threaded through `MachineHost`. Default off; the non-shipping profiler tool turns it on.
- **The OFFLINE metrics never need a runtime toggle** — they only exist inside the profiler tool, which is
  not in any shipping/test graph (the ADR 0011 §6 posture, preserved).

---

## 4. Decision 2 — collection + persistence: promote the hot-op profiler into a standing real-workload profiler with a versioned profile format

**Extend the existing `bench/hotop-profiler/` into `bench/profiler/` — do NOT fork it.** The current tool
already has the load-bearing machinery (per-CPU board construction mirroring the tier drivers, the
descriptor-decode mnemonic recovery, the 68000 field-grammar scan, the top-N histogram). What it lacks is
(a) real workloads, (b) JIT-side capture, (c) a structured persisted format. Add those; keep it
non-shipping.

### 4.1 Real workloads — profile the actual machines, not just kernels

The profiler gains a second input class beyond the frozen W-kernels: **the real boots**, driven through the
*same* `BoardMachineFactory` / surface factories the live machines use (so the profile is of the real thing,
not a re-creation):

- **DOS 3.3** (Apple ][+ boot to BASIC), **CP/M** (SoftCard 2.2 boot to `A>`, and apl2cpm3 CP/M 3.1 to the
  Videx 80-col `A>`), **Apple Pascal** (UCSD p-System to `COMMAND:`), **Spectrum** (48K ROM to the copyright
  screen / a `.SNA` resume). Each runs **headless, fast** (the `MachineHost` headless mode), for a fixed,
  frozen instruction budget (the analogue of the W-kernel caps — a "boot-window" constant per system,
  committed and frozen exactly as W1/W2/W3 are).
- These are **asset-gated, skip-with-note** when the ROM/disk assets are absent (the existing convention —
  the bench W1 streams already do this). So the profiler runs in CI-equivalent absence and produces the
  kernel profiles always, the real-boot profiles when assets are cached (owner-local).
- The profile captures **both tiers**: the interpreter run (hot-op histogram, SoftCard translation hits) AND
  the JIT run (fallback-by-opcode, recompile/eviction churn, chain-vs-dispatch, emit coverage). The current
  profiler is interpreter-only; the JIT pass is the net-new capture.

### 4.2 The profile format — a versioned `profile.json` (the diffable artifact)

A schema mirroring `comparison.json`'s posture (versioned, host-stamped, real-numbers-only), persisted
**per system × workload × commit**:

```jsonc
{
  "schemaVersion": 1,
  "generatedUtc": "2026-06-23T...",
  "commit": "6d805fc",                  // the tree the profile was taken on — diffability anchor
  "host": { "cpu": "...", "os": "...", "dotnet": "..." },
  "system": "apple2-dos33",             // the real machine (or "bench-6502" for a kernel)
  "workload": "boot-to-basic",          // the frozen window
  "frozenBudget": 50000000,             // FROZEN per system×workload (the re-measure contract, §6.4)
  "budgetUnit": "cycles",               // "cycles" (real boots) or "instructions" (kernels) — labels frozenBudget
  "tiers": {
    "interpreter": {
      "instructionsRetired": 50000000,
      "cyclesPerSecond": 1.74e8,
      "realtimeRatio": 1.0,
      "hotOps": [ { "mnemonic": "LDA", "count": 12750000, "pct": 25.5, "cumPct": 25.5 }, ... ],
      "softcardTranslationHits": null,  // present only for SoftCard boots
      "allocBytesPerWindow": 0
    },
    "jit": {
      "instructionsRetired": 50000000,
      "cyclesPerSecond": 8.8e7,
      "realtimeRatio": 1.0,
      "emitCoverage": 0.94,             // fraction of executed instrs that ran emitted IL
      "fallbackByOpcode": [ { "opcode": "0x62", "mnemonic": "BOUND", "count": 4100000, "pct": 8.2 }, ... ],
      "compileCount": 312, "totalRecompiles": 4, "totalEvictions": 1, "smcHotPcCount": 2,
      "chainEdgesTaken": 48000000, "dispatcherEntries": 2000000,   // the ADR-0012 floor signal
      "allocBytesPerWindow": 0
    }
  },
  "perPeripheralFrameCostNs": [ { "device": "Apple2Video", "ns": 41000 }, ... ]   // SAMPLED, optional
}
```

**Persistence layout:** `bench/results/profiles/<system>/<workload>.json` (latest) + the loop commits these
the same way `comparison.json` is committed, so a `git diff` of the profile *is* the before/after of a
profiling turn. A small `profiles/INDEX.md` (generated) tabulates the current top candidates across all
systems — the human-readable surface of the ranked backlog (Decision 4).

### 4.3 What this composes with (no duplication)

- It **reuses** the bench `BenchWorkload`/`*Workloads` for the kernels and the `BoardMachineFactory`/surface
  factories for the real boots — one source of board construction.
- It **reuses** the runtime counters (`IJitMetrics` + the new chain/dispatch + fallback-by-opcode counters)
  — the profiler reads them after a run; it does not re-derive them.
- It **feeds** `comparison.json`: the profiler's per-tier cycles/sec for a system×workload is the same
  number the bench would report; the profiler is the *richer* capture (histograms) over the *same* runs.
- It is **not** in the shipping or unit-test graph (ADR 0011 §6 posture). A thin smoke test may assert the
  profiler produces a well-formed `profile.json` for a kernel (so the format doesn't rot), but the heavy
  real-boot profiling is a developer/agent-invoked offline run.

---

## 5. Decision 3 — one metrics source feeds both views

The live overlay (in-flight) and the aggregate engine (this ADR) read the **same** counters through the
**same** Core-side seam. Concretely:

- The in-flight `IJitMetrics` (`src/CpuEmulator.Core/IJitMetrics.cs`) is the JIT-counter seam. **Extend it
  (additively) with the two new free counters** the loop needs and the overlay can ignore:
  ```csharp
  public interface IJitMetrics
  {
      int CompileCount { get; }
      long TotalRecompiles { get; }
      long TotalEvictions { get; }
      int SmcHotPcCount { get; }
      // ADR 0022 additions (free counters — the ADR-0012 floor signal):
      long ChainEdgesTaken { get; }        // chain edges followed without a dispatcher round-trip
      long DispatcherEntries { get; }      // dispatcher round-trips (the cost the chain avoids)
  }
  ```
  The overlay's 8-row HUD does not show these (it stays lean); the profiler reads them. One seam, two
  consumers — they cannot drift because there is one source.
- **Unify the instruction-retired counter (the deferred-ips resolution).** The bench `ITierInstance` already
  has `InstructionCount`; the live overlay deferred ips because `ICpuCore` (runtime) does not expose it. The
  Architect call the overlay flagged: **add a monotonic `InstructionCount` to the runtime metrics seam**
  (either on `ICpuCore` alongside `CycleCount`, or on a sibling `IExecutionMetrics` to keep `ICpuCore`
  minimal — §9 OQ1). This unblocks the overlay's ips row *and* gives the profiler the retired-instruction
  denominator for emit-coverage and ips. It is FREE on the interpreter (one increment per `Step`) and on the
  JIT (one per retired instruction — the budget-1 path already counts it; the bulk path needs the running
  total, a single add per emitted instruction's existing PC-increment site, which is the cheapest place).
- **The system/peripheral + allocation metrics** are SAMPLED through `MachineHost` (the pump both surfaces
  and the profiler drive), behind `ProfilingOptions`. The overlay's `PerfPusher` already samples cycles/sec
  there at 3 Hz; the profiler samples the richer set at its own cadence. Same host, same sampling discipline.

**Net:** the overlay is the *thin live projection*; the engine is the *full capture + persistence + ranking*;
both read one Core-side metrics surface. No metric is computed two ways.

---

## 6. Decisions 4 + 5 — the ROI model and the loop

### 6.1 Decision 4 — the ROI model: `score = hotness × unit_cost × headroom`

Every optimization candidate the loop surfaces is scored by three measured factors, ranked descending, and
emitted as the backlog:

- **hotness** = the candidate's share of executed work, from the profile. For an opcode: its
  fraction of executed instructions (the fallback-by-opcode pct). For a peripheral: its fraction of frame
  time. For a translation branch: its hit-count share. **Measured, never assumed.**
- **unit_cost** = how expensive each occurrence is *today*. For a fallback opcode: the
  fallback dispatch cost (a dispatcher round-trip + an `inner.Step` vs an emitted inline op) — measurable as
  the cycles/sec delta between an all-fallback and an emitted block of that op, OR approximated by the
  chain-vs-dispatch ratio for blocks containing it. For a peripheral: its measured per-frame ns. For SMC: the
  recompile count × the per-compile cost.
- **headroom** = how much is recoverable — the gap to the realistic ceiling. An opcode already
  emitting has ~0 headroom (don't re-emit it). A hot fallback opcode has high headroom (the difference
  between fallback and emitted throughput). A peripheral already at the irreducible cost of its real work has
  low headroom. **This is the ADR-0012 guard: a candidate with a believed-high cost but measured-low headroom
  scores low** — exactly the trap the invalidation-scan PR fell into (high believed cost, ~1.3% real
  headroom).

`score = hotness × unit_cost × headroom`. The backlog is `ORDER BY score DESC`. The profiler's
`INDEX.md` renders it.

### 6.2 The decision rules (concrete, per lever)

The model resolves to a yes/no per lever, so it "auto-produces a ranked list" the owner can act on:

- **"Emit this opcode next?"** YES if the opcode is in the fallback-by-opcode histogram with hotness above a
  threshold (e.g. ≥1% of executed instructions on any real workload) AND it is not exception/microcoded/rare
  (the ADR 0011 §2 fallback-by-design set — those stay fallback regardless of hotness, because emitting the
  exception frame is high-risk for the headroom). The threshold is the ADR 0011 §6 "top-8 covers 86–100%"
  finding made into a rule.
- **"Add the SMC or `(PC,bankState)` lever?"** SMC lever (already shipped, PR-S): tune/extend if
  `TotalRecompiles` is a material fraction of `CompileCount` on a real workload (a thrash signature). Bank
  lever (ADR 0013, parked #5a): build it if a real banked boot (Apple ][+ language-card / SoftCard) shows
  recompile churn *attributable to remaps* (not SMC) — the loop distinguishes them via the
  recompile-on-remap vs recompile-on-write split. **This is the measured forcing function ADR 0013's parked
  status named as missing.**
- **"Optimize this peripheral?"** YES if its per-frame ns is a material fraction of the frame budget AND the
  real-time ratio is below target on that system (a peripheral that's 30% of frame time on a machine already
  at 3× real-time is not worth touching — headroom gates it).
- **"Is L (JIT-under-translation) worth it for this workload?"** YES only if the SoftCard translation-branch
  hit-count is a material fraction of executed instructions on a real CP/M boot AND that boot's real-time
  ratio is below target. **Today the loop's first job is to produce that number** — the parked decision
  ("CP/M is not perf-critical") becomes evidence-backed.

### 6.3 The loop (Decision 5)

```
  ┌────────────┐   ┌────────┐   ┌──────────────────────────┐   ┌──────────────┐
  │  PROFILE   │──▶│  RANK  │──▶│  ACT (one candidate)     │──▶│  RE-MEASURE  │──┐
  │ real+kernel│   │ ROI    │   │  gated: parity + before/  │   │ profile +    │  │
  │ both tiers │   │ score  │   │  after benchmark          │   │ comparison   │  │
  └────────────┘   └────────┘   └──────────────────────────┘   └──────────────┘  │
        ▲                                                                          │
        └──────────────────────────────────────────────────────────────────────┘
```

1. **PROFILE** — run `bench/profiler/` over the kernels + the cached real boots, both tiers; commit the
   `profile.json` set. (The "before".)
2. **RANK** — the ROI model produces the ordered backlog (`INDEX.md`). The top item is the next move.
3. **ACT** — a single Builder PR implements one candidate, gated by **two** binding gates, both inherited
   from ADR 0011 §5/§8:
   - **Parity gate (correctness):** byte-identical TomHarte/ZEX/SingleStep through the JIT for any emit
     change; the differential fuzzer for any cache/lever change. A change that isn't byte-identical to the
     interpreter oracle **does not ship** (§8 invariant 1+2).
   - **Honesty gate (the win is real):** a *measured* before/after on the candidate's hot workload against
     the **frozen** constants (`git diff` of the constants shows no change), committed to `comparison.json`
     AND the re-run `profile.json`. The score's predicted win must materialize as a real number, or the PR
     is reverted/reconsidered.
4. **RE-MEASURE** — re-profile; the candidate's hotness/headroom drops (it's now emitted/optimized), the next
   candidate rises. Commit. Loop.

### 6.4 The measurement traps (the ADR-0012 lesson, made structural)

ADR 0012 spent a PR on a *believed* floor that measurement refuted. The loop is built so that cannot recur:

- **Rank on measured cost, never a cost model.** `unit_cost` and `headroom` are read from the profile, not
  argued from first principles. A candidate cannot enter the backlog on a hypothesis.
- **The before/after honesty gate is the un-fakeable check.** If the predicted win doesn't show in the
  re-measure against frozen constants, the candidate is wrong — exactly how ADR 0012's net-negative would
  surface as a red gate, not a shipped regression. (ADR 0012's dirtied-page-list *did* have an A/B toggle;
  the loop makes that toggle-and-measure the standard, not the exception.)
- **The chain-vs-dispatch counter makes the real floor standing.** ADR 0012's true floor was found by
  accident; the new free counters keep it permanently visible, so the next SMC-perf question reads a number.
- **Real workloads, not just kernels.** The kernels are clean compute; the real boots are where the
  surprising costs live (the integration mix ADR 0011 §1.1 calls out). Profiling only kernels is how a loop
  optimizes the wrong thing. The loop's primary input is the real machines.

---

## 7. The first ROI-ranked optimization backlog (what the loop surfaces NOW)

This is the backlog the loop would produce on the current tree, from what is *already known* (the committed
baseline + the M6 findings + the parked items). Each carries the **metric that justifies it** and a
**cost/ROI** read. The scores are *qualitative pending the first real-boot profile run* — which is itself
the recommended first move (item A): the loop's premise is that this list gets *replaced by measured
numbers*, not asserted.

| # | Candidate | Justifying metric (today) | Cost | ROI read | Type |
|---|---|---|---|---|---|
| **A** | **Run the first real-workload profile pass** (DOS/CP/M/Pascal/Spectrum, both tiers) | *none yet — the real machines have never been profiled* | **S–M** (extend the profiler, wire the real boots, emit `profile.json`) | **Highest** — every other item below is currently a guess; this turns the whole backlog from assumed to measured. The unblocker. | quick win (the engine's bootstrap) |
| **B** | **Unify the runtime `InstructionCount` counter** (resolve the overlay's deferred ips) | the overlay explicitly deferred ips → "Architect call"; bench already has the counter | **S** (one monotonic counter on the runtime seam; the bench/`ITierInstance` shape is the reference) | **High** — unblocks the live ips row AND gives the profiler the emit-coverage/ips denominator. Tiny, enabling. | quick win |
| **C** | **Add the chain-vs-dispatch free counters** (the ADR-0012 floor, made standing) | ADR 0012's refutation found the floor by accident; it is currently uncounted | **S** (two free counters on `JittedCpu`, surfaced via `IJitMetrics`) | **High** — makes the SMC-heavy floor a permanent number; cheap; prevents the next ADR-0012-style guess. | quick win |
| **D** | **Fallback-by-opcode histogram on real boots** | `FallbackEmitCount` exists but is per-block, not execution-weighted per-opcode | **M** (offline attribution in the profiler) | **High** — the canonical "what to emit next" feed; likely surfaces real-mode 8086 / 68000 tail opcodes the kernels never exercise | quick win (feeds the real arcs) |
| **E** | **Per-bank `(PC,bankState)` specialization** (ROADMAP #5a / ADR 0013) | the Apple ][+ language-card + SoftCard boots remap banks; recompile-on-remap churn is **unmeasured** | **L** (ADR 0013 is fully designed; ~3–5 PR) | **Conditional** — build IFF item A's profile shows remap-attributable recompile churn on a real banked boot. The parked item's missing forcing function. | real arc (gated on A) |
| **F** | **A hot peripheral's tick/render path** (e.g. `Apple2Video` hi-res, Videx CRTC, ULA, Disk II LSS) | per-peripheral frame cost is **unmeasured** | **M** per peripheral | **Conditional** — build IFF item A shows the peripheral is a material frame fraction AND the system's real-time ratio is below target | real arc (gated on A) |
| **G** | **L — JIT-under-translation for the SoftCard** (ROADMAP L, parked) | SoftCard translation-branch hits are **unmeasured**; parked as "not perf-critical" | **L** (reverses ADR 0015's interpreter-only coprocessor) | **Conditional — likely stays parked** — build IFF a real CP/M boot shows the translation path is a material fraction AND below real-time target. Item A produces the number that confirms or kills it. | real arc (gated on A; probably no-op) |
| **H** | **Generic `OpModel`-walked emitter** (ROADMAP #5b / ADR 0011 Decision 2) | the 4 CPUs' arms now exist (the "≥2 CPUs reveal what generalizes" precondition is met) | **L** | **Low-urgency** — a maintainability/leverage refactor, not a throughput win; the loop deprioritizes it (low headroom on throughput) unless a 5th CPU is queued | real arc (low priority) |
| **I** | **Cycle-exact 68000 timing** (ROADMAP #2 / ADR 0020, parked) | no cycle-sensitive 68000 consumer; a reporting-unit change | **L** (~3–10 PR) | **Lowest** — the loop confirms ADR 0020's "park it": zero throughput headroom (it changes the *reporting unit*, not speed). Stays parked unless a cycle-sensitive consumer appears. | parked (loop agrees) |

### 7.1 The recommended FIRST MOVE

**Item A — run the first real-workload profile pass — bundled with the two enabling quick-wins B and C.**

Rationale, measurement-disciplined:

- **Everything below A is currently a guess.** The owner's machines (DOS, CP/M, Pascal, Spectrum) have
  *never been profiled*. The entire parked backlog (#2/#5/L) is parked precisely because no one has the
  numbers. A is the smallest change that converts the whole list from assumed to measured — the literal
  definition of "enable the feedback loop."
- **It is low-cost and low-risk.** The profiler already exists and already constructs the boards; A extends
  it (real workloads + JIT-side capture + the JSON format), touches **no shipping path**, and is gated only
  by "produces a well-formed `profile.json`." No `src/` hot-path risk.
- **B and C are tiny, enabling, and dovetail.** B (the runtime `InstructionCount`) unblocks the live
  overlay's deferred ips row *and* gives the profiler its coverage denominator — one counter, two payoffs. C
  (the chain-vs-dispatch counters) makes the ADR-0012 floor a standing number for ~free. Both are S-sized and
  land alongside A.
- **The payoff is the backlog itself.** After A+B+C, the loop produces a *measured* ranked list, and the
  conditional items (E/F/G) resolve to build-or-park on evidence. That is the loop running its first turn.

**Quick wins vs real arcs:** A, B, C, D are quick wins (small, enabling, no hot-path risk — the engine's
bootstrap). E, F, G are real arcs *gated on A's evidence* (don't build them until the profile justifies
them — the whole point). H is a low-priority leverage refactor. I the loop confirms stays parked.

---

## 8. Decision 6 — invariants (carried, non-negotiable)

These are inherited from ADR 0008/0011 and are **load-bearing for the loop's safety** — every "act" step is
bounded by them:

1. **The interpreter is always the oracle and the byte-exact fallback.** No optimization the loop surfaces
   may change this. A faster path is only valid if it is byte-identical to the interpreter (the parity gate).
2. **Emit stays a pure performance dial.** Acting on an emit candidate means an op goes from interpreted to
   emitted-and-parity-proven; it never changes an architectural result. The fallback valve makes partial
   coverage correctness-free (ADR 0011 §2).
3. **`Core` stays AOT-clean.** The metrics seams are interfaces (`IJitMetrics`, the instruction-counter
   seam) implemented by the JIT/interpreter cores; `Core` names no `Reflection.Emit`, no concrete JIT type.
   The in-flight `IJitMetrics` already honors this (a non-generic interface, interpreter cores simply don't
   implement it); the ADR's additions follow the same rule.
4. **Instrumentation is removable / zero-cost in shipping paths.** The collection-cost contract (§3) is the
   teeth: FREE counters only on paths that already run; SAMPLED metrics at coarse cadence behind a default-off
   flag; OFFLINE metrics only in the non-shipping profiler tool. **No metric adds per-instruction work to a
   shipping hot path.** A shipping run with all profiling flags off is byte-for-byte and cycle-for-cycle the
   un-instrumented run.

---

## 9. Open questions

1. **Where does the runtime `InstructionCount` live (Backlog B)?** On `ICpuCore` alongside `CycleCount`
   (minimal, but widens the most-implemented interface), or on a sibling `IExecutionMetrics` the cores opt
   into (keeps `ICpuCore` lean, parallels `IJitMetrics`)? Recommend the **sibling seam** — it mirrors the
   `IJitMetrics` precedent the overlay just established and keeps `ICpuCore` minimal. **Owner/Planner call at
   implementation; this is the one B-sized decision the overlay flagged as Architect-worthy.**
2. **The frozen real-boot window constants.** Each real system needs a frozen instruction budget (the W-kernel
   analogue) so the re-measure is byte-identical. What budget per system makes the profile stable but the run
   fast enough to iterate? Recommend: the boot-to-prompt instruction count + a fixed steady-state window,
   pinned per system in the profiler, committed and frozen. Resolve empirically on item A's first run.
3. **Per-peripheral cost: SAMPLED-in-run vs OFFLINE-replay.** A per-frame per-device stopwatch (SAMPLED,
   behind a flag) is cheap but coarse; an offline frame-replay with per-device timing is exact but needs a
   replay harness. Recommend SAMPLED first (it's enough to rank peripherals); promote to replay only if a
   peripheral's cost needs sub-frame attribution. Resolve when item F is first triggered.
4. **Does the profiler's real-boot pass belong in CI at all (even asset-gated-skip)?** The kernels can run in
   CI (dependency-free); the real boots are owner-asset-local. Recommend: a CI smoke that profiles the kernels
   + asserts the `profile.json` schema (so the format can't rot), and the real-boot pass as a developer/agent
   offline run (the ADR 0011 §6 posture). Confirm with owner.
5. **The score thresholds (Decision 4).** The "≥1% of executed instructions → emit candidate" and the
   "material fraction of frame budget → peripheral candidate" thresholds are starting guesses from the ADR
   0011 §6 top-8 finding. They should be *tuned against the first real profiles*, not fixed now — the loop
   calibrates its own thresholds on the second turn.

---

*End of ADR 0022. The decision: stand up a performance feedback loop — a metric set (§3) across JIT
(hot-PC/hot-block, fallback-by-opcode = the top-ROI feed, recompile/eviction churn, the new chain-vs-dispatch
floor counter, emit coverage), interpreter/translation (real-workload hot-op histogram, SoftCard
translation-branch hits, dispatch cost), and system (real-time ratio, per-peripheral frame cost, allocations),
each tagged exists/extend/new with a strict collection-cost contract (FREE counters, SAMPLED at coarse cadence,
or OFFLINE-only — never per-instruction in a shipping path); collection + persistence by promoting the
throwaway hot-op profiler into a standing `bench/profiler/` that profiles the REAL boots (DOS/CP/M/Pascal/
Spectrum) on both tiers and emits a versioned, diffable `profile.json` per system×workload (§4); one metrics
source feeding both the in-flight live overlay and this aggregate engine (extend `IJitMetrics`, unify the
runtime instruction-retired counter the overlay deferred) (§5); a measured ROI model `score = hotness ×
unit_cost × headroom` that auto-produces a ranked backlog with concrete per-lever decision rules (§6.1-6.2);
and the loop profile → rank → act (parity + before/after-benchmark gated) → re-measure → repeat, with the
ADR-0012 measurement traps made structural — rank on measured cost, never a believed model (§6.3-6.4).
Invariants: interpreter is the oracle, emit is a pure dial, Core stays AOT-clean, instrumentation is zero-cost
off (§8). The first ranked backlog (§7) and the recommended first move — **run the first real-workload profile
pass (item A) bundled with the two enabling quick-wins (the runtime instruction counter B + the chain-vs-
dispatch counters C)** — turn the parked items #2/#5/L from assumptions into evidence-gated build-or-park
decisions. Designer: no UX surface beyond the already-designed live overlay (this is the offline aggregate
engine). Planner: §7 is the backlog; item A+B+C is the first PR bundle; the conditional arcs E/F/G are gated on
A's measured output — do not schedule them until the profile justifies them.*
