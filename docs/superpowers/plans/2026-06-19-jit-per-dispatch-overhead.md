# #42 — per-dispatch JIT-overhead reduction (the dirtied-page list)

> **Status:** PLANNED (2026-06-19). The follow-on the #40 root-cause surfaced (ROADMAP §"Further candidates"
> → "Per-dispatch JIT-overhead reduction"). PR-S (the SMC/recompile-cost lever) cut Klaus recompiles ~6.8×,
> but Klaus-through-JIT still runs at ~0.8M cyc/s vs the interpreter's ~110M (**~140× slower**) — so
> recompilation is NOT the floor; the **per-dispatch `Run`-loop overhead** is. This plan removes the
> dominant per-dispatch cost: the **256-page `DirtyMap` full scan + full `Array.Clear`** that
> `InvalidateIfDirty` runs on (nearly) every Klaus dispatch, even though only 1–2 pages are actually dirty.
>
> **This is a pure PERFORMANCE change with byte-identical results.** It changes *how* the dispatcher finds
> which pages to invalidate (a tracked list of dirtied pages instead of a full scan), NEVER *whether* a
> self-modified page is observed and its blocks evicted. The set of pages evicted, and therefore every
> architectural result (registers, memory, cycle counts, interrupt latency), is identical.
>
> **Dependencies:** NONE. Touches `BlockCache.cs` (`DirtyMap` + `InvalidateIfDirty`) only on the production
> side, plus tests. A *different, smaller* file set than the emit PRs and orthogonal to PR-S (PR-S is the
> dispatch-tier *policy*; this is the invalidation *data structure*). The emitted IL is **unchanged** —
> `DirtyMap.Mark(page)` keeps the same signature and call sites, so no `BlockCompiler.*` edit is needed.
> Main @ `a1b1efa` (the M6 arc + PR-S complete).

---

## 1. The investigation — where the per-dispatch cost actually goes (traced, file:line)

The dispatcher loop is `JittedCpu.Run` (`src/CpuEmulator.Jit/JittedCpu.cs:123-168`). Every iteration of
`while (cycleBudget > 0)`, in order:

```
JittedCpu.cs:127   if (_inner.InterruptPending) { ... _inner.Step(); continue; }   // check 1 (cheap)
JittedCpu.cs:134   if (_inner.Halted)           { ... _inner.Step(); continue; }   // check 2 (cheap; dead for 6502)
JittedCpu.cs:146   _cache.InvalidateIfDirty();   // ← the SMC invalidation — THE COST on Klaus
JittedCpu.cs:147   var pc = (ushort)_inner.GetRegister(_pcName);
JittedCpu.cs:154   if (_cache.ShouldInterpret(pc)) { ... _inner.Step(); ... continue; }  // PR-S lever (cheap dict probe)
JittedCpu.cs:162   CompiledBlock<TCpu> block = _cache.GetOrCompile(pc, _compiler);
JittedCpu.cs:163   RunChain(block, ref cycleBudget);
```

### 1.1 `InvalidateIfDirty` is the dominant per-dispatch cost on Klaus

`BlockCache.InvalidateIfDirty` (`BlockCache.cs:112-123`):

```csharp
public void InvalidateIfDirty()
{
    if (!Dirty.Any) return;                              // (already short-circuits when CLEAN)
    for (int page = 0; page < _pageCount; page++)        // 256 for a 16-bit board — the FULL SCAN
    {
        if (!Dirty[page]) continue;
        if (_blocksByPage.TryGetValue(page, out var list))
            foreach (CompiledBlock<TCpu> block in list.ToArray())
                Evict(block);
    }
    Dirty.Clear();                                        // Array.Clear over ALL 256 entries
}
```

and `DirtyMap` (`BlockCache.cs:5-12`):

```csharp
public sealed class DirtyMap(int pageCount)
{
    private readonly bool[] _dirty = new bool[pageCount];
    public bool Any { get; private set; }
    public void Mark(int page) { _dirty[page] = true; Any = true; }
    public bool this[int page] => _dirty[page];
    public void Clear() { System.Array.Clear(_dirty); Any = false; }
}
```

**The key correction to the naïve framing:** candidate (a) — "skip the scan when nothing is dirty" — is
**already implemented** (`if (!Dirty.Any) return;` at `BlockCache.cs:114`). On a *clean* dispatch the
method already costs one bool read and returns. So "skip when clean" is not the lever; it is already there.

**The actual Klaus pathology** (ADR 0011 §1.1 fact 2; §6 6502 W1 table): Klaus's flag-save/restore +
test-vector pattern (16.2% PHP, 15.7% CMP, 8.7% PLA, plus STA into code-adjacent pages) **dirties a page on
nearly every dispatch**. So `Dirty.Any` is **true** almost every iteration, the short-circuit does NOT fire,
and `InvalidateIfDirty` pays the **full 256-iteration scan** to find the 1–2 pages that were actually
dirtied — *plus* `Dirty.Clear()` runs `Array.Clear` over the full 256-element `bool[]` every time. That is
**~512 array touches per dispatch** to act on ~1–2 dirtied pages, on a workload that dispatches per
instruction (PR-S routes the hot SMC PC through `inner.Step`, which returns to the top of the loop — so the
`InvalidateIfDirty` scan still runs every interpreted dispatch too). The scan cost is **O(pageCount)
per dispatch, independent of how few pages are dirty** — that is the floor PR-S cannot move (PR-S removes
the `Compile()` cost; the scan remains).

### 1.2 Why the scan dominates (cost decomposition)

Per Klaus dispatch, the per-dispatch work decomposes as:

| Cost site | Per-dispatch cost | Notes |
|---|---|---|
| `InterruptPending` check (`:127`) | 1 virtual call, ~O(1) | cheap; a getter on the interpreter |
| `Halted` check (`:134`) | 1 virtual call, ~O(1) | cheap; always false (dead) for the 6502 |
| **`InvalidateIfDirty` scan (`:115`)** | **O(256) array reads** | **the dominant cost — runs whenever any page is dirty (≈ every Klaus dispatch)** |
| **`Dirty.Clear()` (`:122`)** | **O(256) `Array.Clear`** | **a SECOND full-256 touch every dirty dispatch** |
| `Evict` per dirtied page (`:146`) | O(blocks-on-page) | small — 1–2 pages, few blocks each; NOT the scan cost |
| `ShouldInterpret` (`:154`) | 1 dict probe | cheap (PR-S) |
| `GetOrCompile` hit / `Step` (`:162`/`:157`) | the actual work | the floor we WANT to be paying |

The two O(256) terms (`:115` scan + `:122` clear) are ~99% of `InvalidateIfDirty`'s cost and the largest
single per-dispatch term on Klaus. **The checks (interrupt/halt) are cheap** — candidate (c) (hoisting the
interrupt/halt checks) would shave a virtual call or two but leaves the O(256)×2 untouched; it is the wrong
target. The cooldown bookkeeping (PR-S `NoteInterpretedDispatch`) is a single dict decrement — negligible.

**Conclusion (confirms the #40 finding): the `DirtyMap` full-256 scan + full-256 clear is the dominant
per-dispatch cost on SMC-heavy Klaus.** It is O(pageCount) per dispatch but does O(pages-actually-dirtied)
worth of useful work — a structural mismatch that the dirtied-page list fixes.

---

## 2. Design — rank the candidates, pick one

The prompt's four candidates, scored on **correctness-safety × payoff** against the §1 finding:

### Candidate (a) — "skip the scan when nothing is dirty" — ALREADY DONE (not the lever)

`InvalidateIfDirty` already returns early on `!Dirty.Any` (`BlockCache.cs:114`). On Klaus the map is dirty
almost every dispatch, so the early-out does not fire and the scan runs anyway. **Rejected: it exists and
does not help the SMC-heavy case** (it only helps the clean-dispatch case, which Klaus is not).

### Candidate (d) — a dirty epoch / generation counter — same limitation as (a)

A monotonic "generation" bumped on any `Mark`, compared per dispatch, is just a cheaper spelling of
`Dirty.Any` — it still tells you only **whether** something is dirty, not **which** pages, so when something
*is* dirty you still need a scan to find the pages. On Klaus (dirty ≈ every dispatch) it collapses to the
same full scan. **Rejected: it optimizes the already-optimized clean path, not the hot dirty path.**

### Candidate (c) — reduce per-dispatch check frequency (hoist/batch interrupt/halt) — wrong target + correctness-risky

The interrupt/halt checks are **cheap** (§1.2) — they are not the cost. Worse, batching them ("check every N
dispatches / only at chain edges") **changes interrupt latency**: the dispatcher's block-entry
`InterruptPending` check (`:127`) and the chain-edge gate (`EmitChainOrExit` gate (4), `BlockCompiler.cs:1407-1410`)
are exactly what bound IRQ/NMI servicing latency to an instruction boundary. Deferring them would let an
interrupt sit unserviced for N instructions — an observable behavior change (and a correctness regression on
the interrupt-latency gates). **Rejected: low payoff (the checks are cheap) and it touches the single
most correctness-sensitive timing surface.** Out of scope by the prompt's own correctness mandate.

### Candidate (b) — finer-grained invalidation: a DIRTIED-PAGE LIST (CHOSEN)

Track, in the `DirtyMap`, the **list of pages dirtied since the last check** alongside the existing
`bool[]`. Then `InvalidateIfDirty` iterates **only the dirtied pages** (1–2 on Klaus), and `Clear` resets
**only those pages** (not a full `Array.Clear`). Both the scan and the clear become O(pages-actually-dirtied)
instead of O(pageCount). The `bool[]` stays as the **membership guard** (so a page dirtied twice between
checks is listed once — no duplicate evictions, no unbounded list growth).

This is the prompt's candidate (b) ("track which page(s) were dirtied and act only on those") realized at the
**page granularity that exactly matches the existing invalidation contract** — `InvalidateIfDirty` already
evicts per-page via `_blocksByPage` (`BlockCache.cs:118`); we are only changing *how it enumerates the
dirtied pages*, from "scan all 256, test each" to "walk the 1–2 that were marked." **The eviction logic, the
per-page `_blocksByPage` lookup, the `Chains.Sever`/`Forget`, the `Dirty.Clear` semantics — all unchanged.**

**Why (b) and not finer-than-page (per-block byte-range / checksum):** the prompt and ADR 0011 §3.4 OQ3
considered per-block checksums; PR-S DECISION S-1 rejected going finer-than-page because Klaus genuinely
writes *into the executing block's own page*, so a finer grain still evicts the hot block AND adds
re-validation cost to the correctness-critical hot path. **The dirtied-page list does NOT go finer than the
page** — it keeps page granularity (identical eviction set) and only makes *enumerating the dirty pages*
cheap. That is the safe, high-ROI slice of candidate (b): same correctness surface, O(256)→O(dirty) cost.

**Scorecard:**

| Candidate | Correctness-safety | Payoff on Klaus | Verdict |
|---|---|---|---|
| (a) skip-when-clean | n/a (already shipped) | none (Klaus is dirty every dispatch) | already done |
| (b) **dirtied-page list** | **maximal — byte-identical eviction set, page granularity unchanged** | **removes the O(256)×2 per-dispatch floor** | **CHOSEN** |
| (c) batch interrupt/halt | **LOW — changes interrupt latency** | low (checks are cheap) | rejected |
| (d) dirty generation | high | none (Klaus dirty every dispatch → same scan) | rejected |

**Chosen: (b), the dirtied-page list.** Correctness-safety: maximal (the evicted set is provably identical —
§4 argument). Payoff: removes the dominant per-dispatch term, directly attacking the ~140× floor.

### DECISION 42-1: keep the `bool[]` as the membership guard; add a `List<int>` of dirtied pages

The `DirtyMap` keeps its `bool[] _dirty` (so `this[page]` and the dedup test stay O(1)) and adds a
`List<int> _dirtyPages`. `Mark(page)` appends to the list **only on the 0→1 transition** (guarded by the
`bool[]`), so the list holds each dirtied page exactly once and is bounded by the number of distinct pages
dirtied between checks (1–2 on Klaus, never more than `pageCount`). `InvalidateIfDirty` walks `_dirtyPages`;
`Clear` resets `_dirty[p]=false` for each listed page then clears the list. `Any` becomes
`_dirtyPages.Count != 0` (or the retained bool — see Task 1). **No emitted-IL change:** `Mark(int)` keeps its
signature, so the `EmitStoreByte` / wide-store call sites (`BlockCompiler.cs:923`, `:960`, the wide helpers)
are untouched.

### DECISION 42-2: this changes the invalidation *implementation*, not the *contract* → a short ADR is warranted (proposed)

The page-precise invalidation contract (ADR 0009 Decision 2 / ADR 0011 §1.2: "the fastmem invalidation hook
and the SMC machinery are page-precise and proven") is **unchanged in observable behavior** — the same pages
are evicted on the same dispatch. But the *data structure backing the contract* changes (a `bool[]` scan →
a tracked dirty-page list), and the M2-i/M2-ii carry-forward invariants (a page's mark is cleared by the
same step that evicts that page's blocks; a not-yet-cached page's mark is consumed harmlessly) now rest on
the list+guard rather than the scan+`Array.Clear`. That is a load-bearing implementation change to a
correctness-critical structure, so **Task 7 proposes a short ADR-0012** documenting the dirtied-page-list
representation and re-stating the invariants against it. (The ADR is *additive*; the behavior it documents is
identical to ADR 0009 Decision 2 — it records the representation change, not a contract change.)

---

## 3. The measurement gate (bounded, directional — NOT the full benchmark)

Mirroring PR-S's bounded directional Klaus check (`KlausSmcLeverDirectionalTests`), this PR carries a
**bounded** directional throughput check: over a ~5M-cycle Klaus window, the dirtied-page-list build's
wall-clock must be **materially faster** than the full-scan build's, and the non-SMC W2/W3 kernels must
**not regress** (they dirty data pages rarely, so the list is short there too — never slower). The headline
full-W1/W2/W3 magnitude stays for the arc-end benchmark; this is the "it moved, and nothing regressed" gate.

The directional comparison needs a way to run the **old full-scan path** for the A/B. We expose it behind a
`JitOptions` flag `UseLegacyFullScanInvalidation` (default **false** — the new list path is on), exactly as
`DisableSmcLever` / `DisableChaining` give the differential harness an A/B toggle. The flag is test-only
plumbing; production always runs the list path.

---

## 4. Correctness-preservation argument (the load-bearing section)

The change is byte-identical because **the set of pages evicted on any given `InvalidateIfDirty` call is
identical** between the full-scan and the dirtied-page-list implementations, and eviction is set-like (order
within one call does not matter — each `Evict` independently drops one block's PC-map/page-index/chain
entries; no `Evict` depends on another's having-run-or-not within the same call).

1. **Same pages visited.** Full-scan visits exactly `{ p : _dirty[p] }`. The list path visits exactly the
   pages appended to `_dirtyPages`. By DECISION 42-1, a page is appended **iff** its `_dirty[p]` went 0→1
   since the last `Clear` — i.e. `_dirtyPages` (as a set) **equals** `{ p : _dirty[p] }` at every
   `InvalidateIfDirty` call. So both paths visit the identical page set.

2. **No duplicate or missing evictions.** The `bool[]` guard makes `Mark` append a page at most once per
   window (the second `Mark` of an already-dirty page is a no-op append), so the list has no duplicates →
   no double-evict. Every dirtied page is appended on its 0→1 transition → no missed page.

3. **`Clear` semantics preserved.** Full-scan `Clear` resets all 256 bools + `Any=false`. The list path
   resets `_dirty[p]=false` for each `p in _dirtyPages` (exactly the pages that were true — no other bool was
   ever set true), then empties the list. Post-`Clear` state is identical: all bools false, no dirty pages,
   `Any` false.

4. **The M2-i / M2-ii carry-forward invariants hold unchanged.** (i) "A page's mark is cleared by the SAME
   step that evicts that page's blocks" — `InvalidateIfDirty` still evicts then `Clear`s in one call, over
   the same page set. (ii) "A not-yet-cached page's mark is consumed harmlessly" — a dirtied page with no
   entry in `_blocksByPage` evicts nothing and is cleared, identical to today
   (`InvalidationTests.Mark_on_a_not_yet_cached_page_does_not_strand_a_later_block` pins this). (iii) The
   intra-block SMC guard (`EmitSmcGuard` → `BlockExit.Recompile`) and the chain-edge `Dirty.Any` gate
   (`EmitChainOrExit` gate (3)) read `Dirty.Any`, whose value is unchanged (DECISION 42-1).

5. **SMC observation is never weakened.** `Mark(page)` is still called from the identical emitted-store sites
   with the identical page argument (no IL change). Every self-modifying store still marks its page; every
   marked page is still evicted on the next dispatch. The change is **purely how the dispatcher enumerates
   the marked pages** — it cannot cause a marked page to go un-evicted (that is exactly what argument 1
   forbids).

6. **Interrupt latency is untouched.** This plan does NOT alter the interrupt/halt checks or the chain-edge
   interrupt gate (candidate (c) rejected). The block-entry `InterruptPending` check, the `Halted` fast
   path, and `EmitChainOrExit` gate (4) are byte-for-byte unchanged. Interrupt-latency behavior is identical.

**The oracle discipline is the backstop:** the differential fuzzer diffs JIT-vs-interpreter over seeded SMC
programs; if the dirtied-page list ever evicted a different set than the scan, a fuzzer seed would diverge.
The Test Plan (§6) runs the fuzzer with the list path AND the legacy-scan path, proving they are mutually
parity-transparent against the interpreter.

---

## 5. Tasks (literal code)

### Task 1 — `DirtyMap`: add the dirtied-page list + the membership guard

**File:** `src/CpuEmulator.Jit/BlockCache.cs`

Replace the `DirtyMap` class (`BlockCache.cs:5-12`) with the list-backed version. `Mark` appends on the
0→1 transition only; `Clear` resets only the listed pages; a new `DirtyPages` accessor lets
`InvalidateIfDirty` enumerate them. `Any` is retained (the emitted chain gate + `InvalidateIfDirty`
early-out read it) and now derives from the list.

```csharp
/// <summary>Per-page dirty marks for SMC. An emitted RAM store calls <see cref="Mark"/>(page); the
/// dispatcher consults the marks before each block dispatch (the cc-cheap SMC check). #42: the marks are
/// backed by BOTH a per-page bool[] (O(1) membership — read by the emitted chain gate and the SMC guard)
/// AND a list of the pages dirtied since the last <see cref="Clear"/> (so invalidation walks only the 1–2
/// pages actually dirtied per dispatch, not the full 256-page table — the per-dispatch-overhead fix). The
/// bool[] is the membership GUARD that keeps each dirtied page in the list exactly once: Mark appends only
/// on the 0→1 transition, so the list has no duplicates and is bounded by the count of distinct dirtied
/// pages (never more than pageCount). The page SET the two structures describe is identical at every
/// point — the list is the cheap enumeration of { p : _dirty[p] } (correctness argument: plan §4).</summary>
public sealed class DirtyMap(int pageCount)
{
    private readonly bool[] _dirty = new bool[pageCount];
    private readonly System.Collections.Generic.List<int> _dirtyPages = new();

    /// <summary>True iff at least one page is dirty since the last <see cref="Clear"/>. Read by the
    /// emitted chain-edge SMC gate (EmitChainOrExit) and the InvalidateIfDirty early-out. Derived from the
    /// dirtied-page list, so it is exact and O(1).</summary>
    public bool Any => _dirtyPages.Count != 0;

    /// <summary>The pages dirtied since the last <see cref="Clear"/> (each at most once — the bool[] guard
    /// dedups). InvalidateIfDirty walks THIS, not the full page table. Exposed as the backing list (read
    /// only by the cache's own InvalidateIfDirty); never mutated by callers.</summary>
    public System.Collections.Generic.IReadOnlyList<int> DirtyPages => _dirtyPages;

    /// <summary>Mark a page dirty. Appends to the dirtied-page list ONLY on the 0→1 transition (the bool[]
    /// guard), so a page dirtied repeatedly between checks is listed once. Same signature as before — the
    /// emitted store IL (BlockCompiler.EmitStoreByte + the wide-store helpers) is UNCHANGED.</summary>
    public void Mark(int page)
    {
        if (!_dirty[page])
        {
            _dirty[page] = true;
            _dirtyPages.Add(page);
        }
    }

    public bool this[int page] => _dirty[page];

    /// <summary>Reset all marks. #42: resets ONLY the pages in the dirtied-page list (exactly the pages
    /// whose bool is true — no other bool was ever set), then empties the list. O(pages-actually-dirtied),
    /// NOT O(pageCount) — replaces the former full Array.Clear over all 256 entries.</summary>
    public void Clear()
    {
        foreach (int page in _dirtyPages)
            _dirty[page] = false;
        _dirtyPages.Clear();
    }
}
```

> **Implementer note (the `pageCount` field):** the former class used `pageCount` only to size `_dirty`.
> The list-backed version still sizes `_dirty[pageCount]` from the primary-constructor param; `pageCount` is
> otherwise unused (as before). No external caller passes anything new — `new DirtyMap(pageCount)` is
> unchanged at every site (`BlockCache.cs:28`, `InvalidationTests.cs:204`/`:233`).

### Task 2 — `InvalidateIfDirty`: walk the dirtied-page list, not the full table

**File:** `src/CpuEmulator.Jit/BlockCache.cs`

Replace `InvalidateIfDirty` (`BlockCache.cs:112-123`). The early-out, the per-page `_blocksByPage` eviction,
the `Evict` call, and the final `Clear` are all preserved — only the enumeration changes from a 256-iteration
scan to a walk of `Dirty.DirtyPages`. A `UseLegacyFullScanInvalidation` opts-flag keeps the old scan for the
directional A/B (Task 6) and for a belt-and-suspenders differential cross-check (Task 5).

```csharp
    /// <summary>The SMC check, run by the dispatcher before each block dispatch. PRECISE (M2-ii): evict
    /// only the blocks on dirtied pages (and sever their inbound chain links), not the whole cache.
    /// #42: enumerate ONLY the pages actually dirtied since the last call (Dirty.DirtyPages — 1–2 on Klaus)
    /// instead of scanning all <see cref="_pageCount"/> pages every dispatch. The evicted page set is
    /// IDENTICAL to the former full scan (plan §4): DirtyPages is exactly { p : Dirty[p] }. Preserves the
    /// M2-i carry-forward #1 invariant: a page's mark is cleared by the SAME step that evicts that page's
    /// blocks; a not-yet-cached page's later block reads post-write bytes (it compiles after the eviction).
    /// A dirtied page that owns no block evicts nothing and its mark is cleared as harmless.</summary>
    public void InvalidateIfDirty()
    {
        if (!Dirty.Any) return;
        if (_opts.UseLegacyFullScanInvalidation)          // #42: the A/B + cross-check path (test-only)
        {
            for (int page = 0; page < _pageCount; page++)
            {
                if (!Dirty[page]) continue;
                if (_blocksByPage.TryGetValue(page, out var legacyList))
                    foreach (CompiledBlock<TCpu> block in legacyList.ToArray())
                        Evict(block);
            }
            Dirty.Clear();
            return;
        }
        // The #42 default: walk only the dirtied pages. Snapshot to an array first because Evict mutates
        // _blocksByPage (and Evict does not touch Dirty, but the snapshot also future-proofs against any
        // re-entrancy). The page set is identical to the full scan; order is irrelevant (each Evict is
        // independent — plan §4 argument 1).
        foreach (int page in System.Linq.Enumerable.ToArray(Dirty.DirtyPages))
        {
            if (_blocksByPage.TryGetValue(page, out var list))
                foreach (CompiledBlock<TCpu> block in list.ToArray())
                    Evict(block);
        }
        Dirty.Clear();
    }
```

> **Implementer note (why snapshot `DirtyPages`):** `Evict` mutates `_blocksByPage` (removes the block from
> the page list) but does NOT call `Dirty.Mark`/`Dirty.Clear`, so `_dirtyPages` is not modified during the
> loop. The `ToArray()` snapshot is defensive (matching the existing `list.ToArray()` discipline) and costs
> O(dirty-pages) — still far below O(256). If a reviewer prefers, a plain `foreach (int page in
> Dirty.DirtyPages)` is also correct (no mutation of the list during the loop); the snapshot is the
> conservative choice and is what this plan specifies.

### Task 3 — `JitOptions`: the legacy-scan A/B flag

**File:** `src/CpuEmulator.Jit/JitOptions.cs`

Add the flag after `DisableSmcLever`:

```csharp
    /// <summary>#42: run the LEGACY full-256-page DirtyMap scan in InvalidateIfDirty instead of the
    /// dirtied-page-list walk. Default false (the fast list path is ON). Exists for the directional
    /// throughput A/B (KlausDispatchOverheadDirectionalTests) and the differential cross-check (the fuzzer
    /// runs the list path AND this legacy path, proving both are byte-identical to the interpreter). A pure
    /// PERFORMANCE/representation toggle — both paths evict the identical page set (plan §4), so this NEVER
    /// changes the result, exactly like DisableChaining / DisableSmcLever.</summary>
    public bool UseLegacyFullScanInvalidation { get; init; }
```

**Test (committed):** add to `tests/CpuEmulator.Tests/Jit/JitOptionsTests.cs`:

```csharp
    // ── #42: the dispatch-overhead invalidation toggle defaults to the fast (list) path ──────────
    [Fact]
    public void DirtiedPageList_is_the_default_invalidation_path()
    {
        var o = new JitOptions();
        Assert.False(o.UseLegacyFullScanInvalidation);   // the fast dirtied-page-list path is ON
    }
```

### Task 4 — `DirtyMap` unit pins: the list-vs-bool equivalence + dedup + bounded growth

**New file:** `tests/CpuEmulator.Tests/Jit/DirtyMapTests.cs`

These prove DECISION 42-1's guard directly (the list is exactly the dirty set, deduped, and `Clear` resets
both structures), so the §4 correctness argument has a unit-level anchor independent of the end-to-end
fuzzer.

```csharp
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>#42 — the dirtied-page-list DirtyMap. The list backing makes InvalidateIfDirty O(dirty-pages)
/// instead of O(256) per dispatch; these pins prove the list is exactly the set { p : map[p] } (deduped,
/// bounded, reset by Clear) — the membership-guard invariant the byte-identical-eviction argument (plan §4)
/// rests on.</summary>
public class DirtyMapTests
{
    [Fact]
    public void DirtyPages_equals_the_marked_set_and_dedups()
    {
        var map = new DirtyMap(256);
        Assert.False(map.Any);
        Assert.Empty(map.DirtyPages);

        map.Mark(0x02);
        map.Mark(0x40);
        map.Mark(0x02);   // re-mark: must NOT duplicate (the bool[] guard)
        map.Mark(0x40);

        Assert.True(map.Any);
        Assert.True(map[0x02]);
        Assert.True(map[0x40]);
        Assert.False(map[0x03]);
        // The list is exactly the marked set, each page once.
        var pages = new System.Collections.Generic.HashSet<int>(map.DirtyPages);
        Assert.Equal(2, map.DirtyPages.Count);           // deduped
        Assert.Equal(new System.Collections.Generic.HashSet<int> { 0x02, 0x40 }, pages);
    }

    [Fact]
    public void Clear_resets_both_the_bools_and_the_list()
    {
        var map = new DirtyMap(256);
        map.Mark(0x10);
        map.Mark(0x11);
        map.Clear();

        Assert.False(map.Any);
        Assert.Empty(map.DirtyPages);
        Assert.False(map[0x10]);
        Assert.False(map[0x11]);

        // Re-marking after Clear works (the 0→1 transition fires again).
        map.Mark(0x10);
        Assert.True(map.Any);
        Assert.Single(map.DirtyPages);
        Assert.Equal(0x10, map.DirtyPages[0]);
    }

    [Fact]
    public void Mark_growth_is_bounded_by_distinct_pages_not_mark_count()
    {
        var map = new DirtyMap(256);
        for (int i = 0; i < 10_000; i++)
            map.Mark(0x80);                              // 10k marks of ONE page
        Assert.Single(map.DirtyPages);                   // listed once — bounded by DISTINCT pages
        Assert.Equal(0x80, map.DirtyPages[0]);
    }
}
```

### Task 5 — the list-vs-legacy eviction-equivalence pin (the cache-level byte-identity proof)

**File:** `tests/CpuEmulator.Tests/Jit/InvalidationTests.cs`

Add a pin that drives the cache primitives both ways (list path and `UseLegacyFullScanInvalidation`) over
the same dirty marks and asserts the **same blocks are evicted** (same recompile counts). This pins §4
argument 1 at the cache level, independent of the end-to-end programs.

```csharp
    // ── #42: the dirtied-page-list InvalidateIfDirty evicts the SAME blocks as the legacy full scan ──
    [Theory]
    [InlineData(false)]   // the #42 dirtied-page-list path (default)
    [InlineData(true)]    // the legacy full-256-scan path
    public void InvalidateIfDirty_evicts_the_same_blocks_either_path(bool legacy)
    {
        // A block is cached on page $03 and another on page $05. Marking ONLY $03 dirty must evict the
        // $03 block and leave the $05 block intact — identically on both invalidation paths.
        var space = NewRamSpace();
        Poke(space, 0x0300, 0xE6, 0x10, 0x60);   // INC $10 / RTS on page $03
        Poke(space, 0x0500, 0xE6, 0x11, 0x60);   // INC $11 / RTS on page $05
        var inner = new Mos6502Cpu(space);
        var opts = new JitOptions { UseLegacyFullScanInvalidation = legacy };
        var fastmem = new Fastmem(space, opts);
        var compiler = new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, fastmem, opts);
        var cache = new BlockCache<Mos6502Cpu>(space.PageCount, opts);

        cache.GetOrCompile(0x0300, compiler);
        cache.GetOrCompile(0x0500, compiler);
        Assert.Equal(2, compiler.CompileCount);

        cache.Dirty.Mark(0x03);                  // only page $03 dirtied
        cache.InvalidateIfDirty();               // must evict ONLY the $03 block

        cache.GetOrCompile(0x0500, compiler);    // $05 still cached → NO recompile
        Assert.Equal(2, compiler.CompileCount);
        cache.GetOrCompile(0x0300, compiler);    // $03 evicted → recompiles
        Assert.Equal(3, compiler.CompileCount);
    }
```

### Task 6 — the bounded, directional Klaus dispatch-overhead check (the lever-works gate)

**New file:** `tests/CpuEmulator.Tests/Klaus/KlausDispatchOverheadDirectionalTests.cs`

Mirrors `KlausSmcLeverDirectionalTests` exactly (same `[KlausJitFact]` skip+env gate, same bounded 5M-cycle
window, same `KlausVectors.TryGetBinaryPath()`). Runs Klaus through the JIT with the **list path** vs the
**legacy full-scan path** and asserts the list path's wall-clock is materially better (and never worse),
proving the per-dispatch scan was the cost. Both paths run the lever ON (PR-S default) so the A/B isolates
the invalidation representation.

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.Klaus;

/// <summary>#42 — the BOUNDED, DIRECTIONAL Klaus dispatch-overhead check. PR-S cut Klaus RECOMPILES ~6.8×
/// but the JIT stayed ~140× slower than the interpreter because the per-dispatch DirtyMap FULL-256-PAGE
/// scan (run on ≈ every Klaus dispatch, since Klaus dirties a page almost every instruction) dominates.
/// This pin proves the dirtied-page-list invalidation removes that floor: over a bounded ~5M-cycle Klaus
/// window, the list path's wall-clock is materially below the legacy full-scan path's (same evicted set,
/// cheaper enumeration — plan §4). Bounded + foreground; skips when the Klaus binary is absent OR
/// CPUEMULATOR_KLAUS != full (the same periodic-gate idiom as the PR-S directional pin).</summary>
public class KlausDispatchOverheadDirectionalTests(ITestOutputHelper output)
{
    private const ushort StartAddress = 0x0400;
    private const long Window = 5_000_000;   // bounded: ~5M cycles — NOT the full 96M run

    private static (Mos6502Cpu Cpu, JittedCpu<Mos6502Cpu> Jit) NewKlausJit(byte[] image, JitOptions opts)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, (byte[])image.Clone(), writable: true);   // Klaus self-modifies RAM
        var cpu = new Mos6502Cpu(space) { PC = StartAddress, S = 0xFD, P = 0x34 };
        return (cpu, new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space, options: opts));
    }

    private static double RunWindowSeconds(byte[] image, JitOptions opts)
    {
        var (cpu, jit) = NewKlausJit(image, opts);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (cpu.CycleCount < Window)
        {
            long budget = System.Math.Min(2_000_000, Window - cpu.CycleCount);
            jit.Run(ref budget);
        }
        sw.Stop();
        return sw.Elapsed.TotalSeconds;
    }

    [KlausJitFact]
    public void DirtiedPageList_beats_the_full_scan_over_a_bounded_window()
    {
        byte[] image = File.ReadAllBytes(KlausVectors.TryGetBinaryPath()!);
        Assert.Equal(0x10000, image.Length);

        // Warm both paths once (JIT-of-the-host warmup) before the timed comparison.
        _ = RunWindowSeconds(image, new JitOptions { UseLegacyFullScanInvalidation = true });
        _ = RunWindowSeconds(image, new JitOptions());

        double legacy = RunWindowSeconds(image, new JitOptions { UseLegacyFullScanInvalidation = true });
        double list   = RunWindowSeconds(image, new JitOptions());

        output.WriteLine($"Klaus[{Window} cyc]  full-scan: {legacy:F3}s   dirtied-page-list: {list:F3}s   " +
                         $"speedup x{legacy / list:F2}");

        // DIRECTIONAL gate: the list path is MATERIALLY faster than the full scan (the per-dispatch O(256)
        // scan was the cost). A conservative bar — the list path must be at least 20% faster — absorbs
        // machine noise on a bounded window; the headline magnitude lives in the arc-end full-W1 capture.
        Assert.True(list < legacy * 0.80,
            $"dirtied-page-list {list:F3}s should be materially faster than full-scan {legacy:F3}s");
    }
}
```

> **Tuning note:** if the 20%-faster bar is flaky on a noisy host for the bounded window, raise the warmup
> count or widen the window (NOT loosen the bar past the point it proves a real win). The win is structural
> (O(256)→O(1–2) per dispatch on a per-instruction-dispatching workload), so the directional signal is
> robust; the bar exists to catch a regression (the list path accidentally costing MORE), not to chase a
> specific multiple. Do NOT weaken the assertion to pass a genuinely-not-faster result — investigate.

### Task 7 — the W2/W3-not-regressed smoke + the ADR

**File (smoke):** add to `tests/CpuEmulator.Tests/Jit/DirtyMapTests.cs` (or a new
`tests/CpuEmulator.Tests/Jit/DispatchOverheadTests.cs` — same project). W2/W3 are SMC-free (their stores hit
DATA pages, dirtying few distinct pages), so the dirtied-page list is short there and the path is never
slower. This asserts the cheap, deterministic fact — the kernels run identically (same final state + cycles)
under the list path and the legacy path — rather than a wall-clock claim (which the arc-end bench owns):

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Benchmarks;

namespace CpuEmulator.Tests.Jit;

/// <summary>#42 — the SMC-free compute kernels (W2 arith, W3 sieve) run byte-identically under the
/// dirtied-page-list invalidation and the legacy full scan (they dirty only DATA pages, so the page set —
/// and thus every result — is the same on both paths). The byte-identical-W2/W3 guard for the dispatch
/// overhead change.</summary>
public class DispatchOverheadTests
{
    [Fact]
    public void W2_W3_kernels_are_byte_identical_list_vs_legacy()
    {
        // The top-level Workloads.ArithmeticKernel()/SieveKernel() are the 6502 W2/W3 (same accessors the
        // shipped PR-S W2_W3_shaped_kernels_never_trip_the_lever pin uses); both return a BenchWorkload.
        foreach (var w in new[] { Workloads.ArithmeticKernel(), Workloads.SieveKernel() })
        {
            var (la, lx, ly, lp, lpc, lcyc) = RunKernel(w, new JitOptions());                               // list path
            var (ga, gx, gy, gp, gpc, gcyc) = RunKernel(w, new JitOptions { UseLegacyFullScanInvalidation = true }); // legacy
            Assert.Equal((ga, gx, gy, gp, gpc, gcyc), (la, lx, ly, lp, lpc, lcyc));
        }
    }

    private static (byte A, byte X, byte Y, byte P, ushort PC, long Cyc) RunKernel(
        BenchWorkload w, JitOptions opts)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
        var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space, options: opts);
        long budget = 5_000_000;
        jit.Run(ref budget);
        return (cpu.A, cpu.X, cpu.Y, cpu.P, cpu.PC, cpu.CycleCount);
    }
}
```

> **Reference note (the implementer confirms):** this references `CpuEmulator.Benchmarks` (for `Workloads`
> + the `BenchWorkload` record). The test project already references it (the PR-S
> `W2_W3_shaped_kernels_never_trip_the_lever` pin and `BenchHarnessSmokeTests` use `Workloads`), so no
> `.csproj` change. The `BenchWorkload` members are `Image` / `LoadAddress` / `StartPc`
> (`bench/CpuEmulator.Benchmarks/IEmulatorAdapter.cs:59-68`), and `Workloads.ArithmeticKernel()` /
> `Workloads.SieveKernel()` are the top-level 6502 W2/W3 accessors (verified against the shipped PR-S test).

**File (ADR):** **New** `docs/architecture/0012-jit-dirty-page-list-invalidation.md` (DECISION 42-2). A short
ADR: Context (the per-dispatch O(256) scan dominates SMC-heavy dispatch — the #40/#42 finding); Decision (the
dirtied-page-list representation, DECISION 42-1); the correctness argument (a condensed §4 — the evicted page
set is identical, the carry-forward invariants hold against the list+guard); Consequences (O(256)→O(dirty)
per dispatch; the `bool[]` is retained as the membership guard; the contract from ADR 0009 Decision 2 is
*unchanged in behavior* — this records the representation, not a new contract); and a "Relates to" pointer to
ADR 0009 (Decision 2, the invalidation hook) and ADR 0011 §3.4 (the SMC/recompile axis, of which this is the
dispatch-floor sibling to PR-S). Keep it to ~1 page — it is a representation record, not a new decision arc.

---

## 6. Test plan (the gates — all must pass before merge)

**Correctness-preservation gates (the change is byte-identical — §4):**

1. **The differential fuzzer** (`DifferentialFuzzTests`) — the seeded SMC-biased JIT-vs-interpreter diff.
   **Extend it to also run `UseLegacyFullScanInvalidation = true`** so a list-path bug is distinguishable
   from a base bug (mirrors the existing chaining-on/off + lever-on/off discipline). Edit
   `Jit_matches_the_interpreter_for_a_seeded_program`:

```csharp
        // #42: the dirtied-page-list path (default) AND the legacy full-scan path — both must match the
        // interpreter, proving the list path is byte-identical (it evicts the same page set — plan §4).
        AssertMatchesInterpreter(seed, program, new JitOptions { UseLegacyFullScanInvalidation = true });
```

   CI N=64; the pre-merge gate is `CPUEMULATOR_FUZZ=full` (N=4096).
2. **`ChainingSmcSafetyTests`** — all SMC pins green (the list path is the default; the chain-edge
   `Dirty.Any` gate is unchanged — DECISION 42-1).
3. **`InvalidationTests`** — all pins green, incl. the new Task-5 list-vs-legacy eviction-equivalence
   `[Theory]` and the existing not-yet-cached/MMIO/intra-block pins (unchanged behavior).
4. **`DirtyMapTests`** (Task 4) — the list = marked-set, dedup, bounded-growth, `Clear`-resets-both pins.
5. **`PerPageInvalidationTests` / `BlockCacheFlushTests`** — the cache-primitive + FlushAll pins green
   (FlushAll calls `Dirty.Clear()`, which now empties the list too — the reset stays byte-equivalent to a
   fresh cache).
6. **The full TomHarte-through-JIT sweeps (all CPUs)** — byte-identical (the change is 6502-board-agnostic;
   every CPU's `DirtyMap` is the same class). The sampled JIT sweeps run every invocation; the full sweep is
   the periodic gate.
7. **Klaus cycle-exact** (`KlausJitFunctionalTests`, `CPUEMULATOR_KLAUS=full`) — the full 96M-cycle run to
   `$3469`, cycle count exactly `96,241,367` (the invalidation representation does not touch cycle charging
   — the same blocks run, the same `inner.Step` cools the same PCs). The strongest correctness gate.
8. **Interrupt-latency behavior** — unchanged (candidate (c) rejected; the interrupt/halt checks + the
   chain-edge interrupt gate are byte-for-byte untouched — §4 argument 6). The existing interrupt-servicing
   JIT pins stay green with no body edits.

**The dispatch-overhead-works gate (the payoff — bounded + directional):**

9. **`KlausDispatchOverheadDirectionalTests`** (Task 6) — over a bounded ~5M-cycle Klaus window, the
   dirtied-page-list path is materially faster (≥ 20%) than the legacy full scan. `[KlausJitFact]` (skips if
   the Klaus binary is absent / `CPUEMULATOR_KLAUS != full`).
10. **`DispatchOverheadTests.W2_W3_kernels_are_byte_identical_list_vs_legacy`** (Task 7) — the SMC-free
    compute kernels run byte-identically on both paths (the not-regressed guard).

**JitOptions default:** `DirtiedPageList_is_the_default_invalidation_path` (Task 3) pins the list path ON.

**Verification commands (the implementer runs — evidence before claims):**

```
dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj \
  --filter "FullyQualifiedName~Jit|FullyQualifiedName~Klaus" -c Release

CPUEMULATOR_FUZZ=full dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj \
  --filter "FullyQualifiedName~DifferentialFuzz" -c Release

# the directional + cycle-exact gates (periodic; needs the Klaus binary):
CPUEMULATOR_KLAUS=full dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj \
  --filter "FullyQualifiedName~Klaus" -c Release
```

---

## 7. What this PR does NOT do (scope guard)

- **No finer-than-page invalidation** (no per-block byte-range / checksum) — page granularity is unchanged
  (DECISION 42-1); only the *enumeration* of dirtied pages gets cheap. The eviction set is identical.
- **No interrupt/halt check batching** (candidate (c) rejected) — interrupt latency is untouched.
- **No emit-arm changes** — `Mark(int)` keeps its signature; `BlockCompiler.*` and `CpuEmitter.cs` are
  untouched. This is `BlockCache.cs` + `JitOptions.cs` + tests (+ the ADR) only.
- **No PR-S change** — the SMC/recompile-cost lever (the dispatch *policy*) is orthogonal and stays as-is;
  this is the invalidation *data structure*. Both engaged together is the intended end state.
- **No full W1/W2/W3 throughput re-capture** — that is the arc-end benchmark. This carries only the bounded
  directional Klaus check + the W2/W3 byte-identity smoke.

---

## 8. Files touched

| File | Change |
|---|---|
| `src/CpuEmulator.Jit/BlockCache.cs` | `DirtyMap` gains the dirtied-page list + membership guard (Task 1); `InvalidateIfDirty` walks the list (+ a legacy A/B branch) (Task 2) |
| `src/CpuEmulator.Jit/JitOptions.cs` | + `UseLegacyFullScanInvalidation` (default false) (Task 3) |
| `tests/.../Jit/JitOptionsTests.cs` | + `DirtiedPageList_is_the_default_invalidation_path` (Task 3) |
| `tests/.../Jit/DirtyMapTests.cs` | NEW — list=marked-set, dedup, bounded-growth, Clear-resets-both (Task 4) |
| `tests/.../Jit/InvalidationTests.cs` | + the list-vs-legacy eviction-equivalence `[Theory]` (Task 5) |
| `tests/.../Jit/DispatchOverheadTests.cs` | NEW — W2/W3 byte-identical list-vs-legacy (Task 7) |
| `tests/.../Jit/DifferentialFuzzTests.cs` | + the legacy-scan parity run (Task 6 / §6 gate 1) |
| `tests/.../Klaus/KlausDispatchOverheadDirectionalTests.cs` | NEW — the bounded directional dispatch-overhead gate (Task 6) |
| `docs/architecture/0012-jit-dirty-page-list-invalidation.md` | NEW — the representation ADR (Task 7 / DECISION 42-2) |

---

*End of #42 plan. The lever is **(b) finer-grained invalidation, realized as a dirtied-page list** (DECISION
42-1): the `DirtyMap` tracks the 1–2 pages actually dirtied since the last check, so `InvalidateIfDirty` (and
`Clear`) become O(pages-actually-dirtied) instead of O(256) per dispatch — removing the full-page scan that,
on Klaus (which dirties a page almost every instruction), runs on ≈ every dispatch and is the per-dispatch
floor PR-S cannot move. It is a PERFORMANCE/representation change, NEVER a correctness change: the evicted
page set is provably identical (§4), page granularity is unchanged, and SMC observation + interrupt latency
are byte-for-byte preserved. The cost site is `JittedCpu.Run:146` → `BlockCache.InvalidateIfDirty:112-123`
(the 256-page scan + full Array.Clear); the fix replaces the scan with a tracked dirtied-page list. Correctness
is held by the membership-guard invariant (Task 4) + the cache-level eviction-equivalence pin (Task 5) + the
differential fuzzer run both ways (§6 gate 1); the payoff is gated by the bounded directional Klaus check
(Task 6); W2/W3 are byte-identical (Task 7). A short ADR-0012 records the representation (DECISION 42-2).*
