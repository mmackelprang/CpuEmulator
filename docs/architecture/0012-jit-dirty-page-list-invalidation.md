# ADR 0012 — JIT dirtied-page-list invalidation (the per-dispatch overhead floor)

> **Status:** Proposed (Claude Planner, 2026-06-19). The representation record for issue #42 (per-dispatch
> JIT-overhead reduction). Consumed by the plan `docs/superpowers/plans/2026-06-19-jit-per-dispatch-overhead.md`.
> **Date:** 2026-06-19
> **Deciders:** Mark (owner). Drafted by Claude Planner.
> **Relates to:**
> - **ADR 0009** (`0009-device-jit-contract-and-peripheral-design.md`) Decision 2 — the bus→JIT page-level
>   invalidation hook. This ADR records a **representation change to the structure backing that hook**; the
>   observable contract (which pages are invalidated, and when) is **unchanged**.
> - **ADR 0011** (`0011-jit-hot-op-emission-optimization.md`) §3.4 — the SMC/recompile-cost axis. PR-S (the
>   recompile-cost lever) and this change are the two halves of the SMC-heavy dispatch cost: PR-S removed the
>   per-dispatch `Compile()`; this removes the per-dispatch invalidation **scan**. Sibling levers, orthogonal
>   files.

## Context

On SMC-heavy workloads (the canonical case: the 6502 Klaus functional test) the JIT `Run`-loop's
**per-dispatch overhead dominates**, not recompilation. After PR-S cut Klaus recompiles ~6.8×, the
Klaus-through-JIT throughput still sat at ~0.8M cyc/s vs the interpreter's ~110M (**~140× slower**). The
#40 investigation root-caused the residual floor to **`BlockCache.InvalidateIfDirty`** (`BlockCache.cs`):
the dispatcher calls it before every block dispatch, and on Klaus — which dirties a code-adjacent page on
nearly every instruction — it pays a **full 256-page `bool[]` scan** to find the 1–2 pages actually dirtied,
**plus** a full 256-element `Array.Clear` in `DirtyMap.Clear`. That is ~512 array touches per dispatch to act
on ~1–2 pages, on a workload that dispatches per instruction.

The early-out for the *clean* case already exists (`if (!Dirty.Any) return;`), so the cost is specifically
the **dirty-but-sparse** case: dirty is true (so the early-out does not fire), but only a handful of pages
are dirty (so the full scan is almost all wasted work). The cost is O(pageCount) per dispatch while the
useful work is O(pages-actually-dirtied).

## Decision

Back the `DirtyMap` with **both** a per-page `bool[]` (the O(1) membership test the emitted chain-edge SMC
gate and the SMC guard read) **and** a `List<int>` of the pages dirtied since the last `Clear`. `Mark(page)`
appends to the list **only on the 0→1 transition** (guarded by the `bool[]`), so each dirtied page appears
exactly once and the list is bounded by the count of *distinct* pages dirtied between checks (1–2 on Klaus,
never more than `pageCount`). `InvalidateIfDirty` enumerates the **list**, not the full page table;
`DirtyMap.Clear` resets only the listed pages then empties the list. Both `InvalidateIfDirty` and `Clear`
become **O(pages-actually-dirtied)** instead of **O(pageCount)**.

The emitted IL is **unchanged** — `Mark(int)` keeps its signature, so every emitted-store call site is
untouched. This is a `BlockCache.cs` representation change, not an emit-arm change.

## Correctness (the page set is identical)

The change is byte-identical because the **set of pages evicted on any `InvalidateIfDirty` call is identical**
to the former full scan, and eviction is set-like (each `Evict` is independent within a call):

- The list, as a set, **equals** `{ p : _dirty[p] }` at every call — a page is listed iff its bool went 0→1
  since the last `Clear` (the membership-guard invariant). So both paths visit the identical page set.
- The `bool[]` guard dedups (a re-mark of an already-dirty page is a no-op append), so no double-evict and no
  missed page.
- `Clear` leaves identical post-state (all bools false, list empty, `Any` false).
- The M2-i/M2-ii carry-forward invariants hold against the list+guard exactly as against the scan+`Array.Clear`:
  a page's mark is cleared by the same step that evicts its blocks; a not-yet-cached dirtied page evicts
  nothing and is cleared harmlessly.
- SMC observation and interrupt latency are untouched — `Mark` is still called from the identical sites with
  the identical page; the interrupt/halt checks and the chain-edge interrupt gate are not modified.

The differential fuzzer (run with the list path AND a legacy-full-scan toggle) is the backstop: a divergent
eviction set would surface as a fuzzer seed that diverges from the interpreter oracle.

## Consequences

- **Good:** the dominant per-dispatch term on SMC-heavy dispatch (the O(256) scan + O(256) clear) collapses
  to O(pages-actually-dirtied) — directly attacking the ~140× Klaus floor PR-S could not move.
- **Good:** page granularity is unchanged (no finer-than-page validation added to the correctness-critical
  hot path — the risk PR-S DECISION S-1 explicitly avoided), so the eviction set and every architectural
  result are byte-identical.
- **Accepted:** the `bool[]` is retained alongside the list (a small constant memory overhead — one `List<int>`
  per cache, bounded by `pageCount`). It is the membership guard that keeps the list deduped and is read O(1)
  by the emitted gate; dropping it would cost a list scan per `Mark` or per gate read.
- **Accepted:** a test-only `JitOptions.UseLegacyFullScanInvalidation` toggle is added to A/B the two paths
  (the directional throughput gate + the differential cross-check). It is never set in production.

## Status note

This ADR records a **representation**, not a new contract — the behavior it documents is identical to ADR
0009 Decision 2. It is promoted from *Proposed* to *Accepted* when the #42 plan lands and its byte-identity
gates (the differential fuzzer both ways, the cache-level eviction-equivalence pin, Klaus cycle-exact) are
green.
