# ADR 0013 — Per-bank `(PC, bankState)` JIT block specialization

> **Status:** PROPOSED (2026-06-19). Owner review + approval required before any Planner/Builder work. This is a
> JIT-optimization design (a ROADMAP candidate, §"Per-bank specialization + the generic emitter" item 5a), NOT a
> committed lever. It resolves **ADR 0009 Open Question 3** and the **ADR 0011 §3.4 / OQ3 candidate (b)**
> ("key blocks on `(PC, bankState)` so a re-entered bank reuses compiled blocks").
> **Date:** 2026-06-19
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Supersedes / relates to:**
> - **ADR 0009** (`0009-device-jit-contract-and-peripheral-design.md`) — **Decision 2** (a bus remap fires a
>   page-level JIT invalidation; the `IMapInvalidationListener.OnRemap` → `BlockCache.InvalidatePages` +
>   `Fastmem` re-patch path) is the foundation this ADR builds on, and **Open Question 3** is exactly this
>   decision. ADR 0009 Decision 2 makes bank switching *correct* under the JIT (evict-on-remap); ADR 0013 makes
>   a *re-entered* bank *fast* (reuse instead of recompile). This ADR does **not** relitigate Decision 2 — it is
>   a strict, opt-in refinement layered on top of it.
> - **ADR 0011** (`0011-jit-hot-op-emission-optimization.md`) — §3.4 the SMC/recompile-cost axis. Per-bank
>   specialization is §3.4 candidate (b). It is **complementary to PR-S** (the SMC/recompile-cost lever, which
>   handles *self-modifying code* — the same bytes mutating in place) and **orthogonal to it**: SMC = a write
>   changes the bytes at a PC; banking = a remap changes *which memory is visible* at a PC. They compose; this
>   ADR §3 is explicit about not conflating them.
> - **The #42 dirtied-page-list plan** (`docs/superpowers/plans/2026-06-19-jit-per-dispatch-overhead.md` +
>   proposed **ADR 0012**) — the per-dispatch `Run`-loop cost analysis. This ADR's "how does the dispatcher read
>   `bankState` per lookup without per-dispatch cost" reasoning (§1, §4) is shaped by the same discipline: a
>   per-dispatch O(1) read, never a per-access or per-page-scan cost. ADR 0013's key change must NOT reintroduce
>   the per-dispatch floor #42 removes.
> - **ADR 0010** (`0010-machine-definition-format.md`) — the declarative manifest. A bank-switch mapper is the
>   code-behavior side of the hybrid (ADR 0010 §1.2): the manifest declares the windowed region and the mapper
>   device id; the mapper *is* an `IPeripheral` whose bank-select `Write` fires the remap. ADR 0013 designs the
>   JIT side to accept a `bankState` signal from the bus/machine **without hard-coupling to any specific board or
>   mapper** — the reusable Machine/board model is being designed in parallel.

---

## 1. Context

### 1.1 The PC-only key and the evict-on-remap cost

The JIT block cache keys compiled blocks on **PC alone**: `BlockCache._blocks` is a
`Dictionary<ushort, CompiledBlock<TCpu>>` (`src/CpuEmulator.Jit/BlockCache.cs:26`), and `GetOrCompile(ushort pc, …)`
(`:66`) hits or compiles by that `ushort` key. The dispatcher reads the live PC
(`JittedCpu.Run` → `(ushort)_inner.GetRegister(_pcName)`, `JittedCpu.cs:147`) and looks the block up by it.

This is exactly correct for a **fixed-map** board — the premise `Fastmem` is built on: "For a fixed-map 8-bit board
the map is static" (`Fastmem.cs:6`). The whole address space is classified once at `JittedCpu` construction
(`Fastmem`'s constructor walks every page via `AddressSpace.TryGetDirectAccess`, `Fastmem.cs:30-46`), and the same PC
always decodes the same bytes.

A **bank switch breaks that premise.** When a mapper remaps which ROM/RAM page is visible at an address range (an Atari
cartridge mapper, the Apple II language card, the C64 PLA, PC EMS/UMB paging, the 68000 boot-time ROM-overlay), the same
PC now maps to *different code* depending on the active bank. ADR 0009 Decision 2 makes this **correct** by treating a
remap as page-level SMC: the remapping device calls `AddressSpace.Remap` → fires `IMapInvalidationListener.OnRemap` →
the JIT re-patches `Fastmem.PageBacking/PageOffset/PageWritable` for the affected pages **and** calls a
`BlockCache.InvalidatePages(firstPage, pageCount)` that evicts every cached block spanning those pages (the same
per-page `Evict` loop `InvalidateIfDirty` uses, factored out; `BlockCache.cs:112-160`).

Eviction severs the blocks' inbound chain links (`Chains.Sever`/`Chains.Forget`, `BlockCache.cs:158-159`). So after a
bank switch the windowed region's blocks are **gone**. The next time the program re-enters that bank, every block in the
window **recompiles from scratch** — `Discover` re-walks, `Compile` re-emits a fresh `DynamicMethod`
(`BlockCompiler.Compile`, `:443-476`), chains re-link by PC. A program that repeatedly re-enters a bank (a
per-scanline mapper switch; a banked-overlay routine called in a loop; a PC EMS page mapped in/out per request) pays a
**full recompile of the window on every switch** — the bank-switch analogue of the W1 Klaus SMC-thrash hole ADR 0011
§3.4 names, except the cause is remapping, not self-modification.

### 1.2 The opportunity: a re-entered bank is identical code

The key observation: **the bytes at a given PC under bank B are the same every time bank B is active.** Bank B's
window-$8000 block is byte-for-byte identical on the program's first entry into B and its hundredth. Evicting it on the
switch *away* from B and recompiling it on the switch *back* is pure waste — the compiled artifact was correct and is
correct again. The PC-only key conflates "PC $8000 under bank A" with "PC $8000 under bank B" into one cache slot, so the
cache can hold only one of them at a time and must throw it away to make room for the other.

**Per-bank specialization keys blocks on `(PC, bankState)`** so the cache holds bank A's $8000 block *and* bank B's
$8000 block simultaneously, and re-entering a bank **reuses** its already-compiled blocks. The recompile-per-switch cost
collapses to a one-time compile-per-(bank, hot-PC).

### 1.3 What the shipped code already proves (verified, not assumed) — the facts the design rests on

These are the load-bearing facts I confirmed in the source; the correctness invariant (§4) and the approach choice (§7)
depend on each one.

- **The cache key is literally `ushort` PC and nothing else.** `BlockCache._blocks`
  (`Dictionary<ushort, CompiledBlock<TCpu>>`, `BlockCache.cs:26`), `_blocksByPage` (`int page → List<block>`, `:27`),
  `_recompiles`/`_cooldown` (PR-S, keyed `ushort` pc, `:34-35`), and the chain table's inbound map
  (`Dictionary<ushort, HashSet<block>>`, `ChainTable.cs:10`) are **all** keyed or sub-keyed on the bare PC. The key
  surface to widen is well-contained: it is one dictionary's key type plus the PR-S/chain sub-keys, not a sprawl.
- **The emitted IL reads `Fastmem` arrays LIVE on every access — it never bakes a backing pointer across instructions.**
  `LoadByteFromBus` (`BlockCompiler.cs:824-864`) emits `Ldarg_2` (the `Fastmem` arg) → `Callvirt PageBacking` →
  `Ldelem_Ref` at `ea >> 8` *per load*; `EmitStoreByte` (`:873`) does the same per store. The page index is a **runtime**
  value (`ea >> 8`), and `PageBacking`/`PageOffset`/`PageWritable` are re-indexed every single access. **Nothing caches a
  backing-array reference across instructions within a block.** This resolves ADR 0009 Open Question 5 affirmatively and
  is the reason a *single shared* `Fastmem` whose array *contents* are patched on remap is visible to all live blocks
  immediately (ADR 0009 §3.2). It is also why §4's invariant is provable: a block's correctness depends only on (a) the
  bytes it was compiled from and (b) the `Fastmem` contents at run time matching the bank it was compiled under.
- **`Fastmem` is a single per-`JittedCpu` instance**, constructed once (`JittedCpu.cs:76`) and passed by reference into
  every emitted block (`BlockDelegate`'s `Fastmem fastmem` param, `CompiledBlock.cs:48-49`). There is exactly one page
  table. ADR 0009 Decision 2 patches its *contents* on remap; ADR 0013 must reckon with the fact that a block compiled
  under bank A and one compiled under bank B both read the *same* `Fastmem` object — so the page table must reflect the
  *currently active* bank when *any* block runs (§4, the central correctness constraint).
- **The remap path already exists in design and reuses proven machinery** (ADR 0009 Decision 2, not yet implemented):
  `IMapInvalidationListener.OnRemap(int firstPage, int pageCount)` (Core-defined), `AddressSpace.Remap(...)` (fires the
  listener atomically), `BlockCache.InvalidatePages(firstPage, pageCount)` (the factored per-page `Evict` loop). ADR 0013
  threads `bankState` through this same path rather than inventing a parallel one.
- **The dispatcher reads PC once per dispatch via an interface call** (`_inner.GetRegister(_pcName)`,
  `JittedCpu.cs:147`) — a cheap, already-paid per-dispatch cost. Reading `bankState` per dispatch must be the same order:
  an O(1) field/getter read, NOT a per-access or per-page-scan cost (the #42 discipline).
- **Chaining resolves successors BY PC through the live cache on every edge** (`ResolveChain` → `GetOrCompile`,
  `BlockCache.cs:99-104`; the emitted edge calls back into `ChainEdge`, `JittedCpu.cs:204-216`), and the chain-break
  gates (`EmitChainOrExit`, `BlockCompiler.cs:1391-1423`) round-trip to the dispatcher on `budget <= 0`, `dirty.Any`, or
  `InterruptPending`. **A remap does not currently break a running chain** unless it marks something dirty — §4 examines
  whether a chain edge can cross a bank boundary and what bounds it.

---

## 2. Decision

**Adopt per-bank `(PC, bankState)` block specialization as a JIT capability that is OFF unless a board declares it
banking-capable, behind a `JitOptions` flag, realized as the "compose the cache key from `(PC, bankState)`" approach
(§7 Approach 1) using a single shared `Fastmem` snapshotted per active bank. Scope it to the simple fixed-window bank
switch; defer arbitrary MMU-style mapping (§5).**

Concretely, the decision is the sum of the five design points in §2.1–§2.5, expanded in §3–§6. The recommended approach
and the alternatives it beat are in §7.

### 2.1 `bankState` is a small integer "bank generation/configuration id" the bus owns

`bankState` is a `uint` (call it `BankConfigId`) that identifies the **current mapping configuration** of the banked
window(s). It is owned and computed by the bus/`AddressSpace` (the single place a remap happens), exposed to the JIT
through the same `Core`-defined seam family ADR 0009 Decision 2 introduced. The JIT reads it once per dispatch (the cost
of `GetRegister(_pcName)` order, §1.3) and composes the cache key from `(pc, BankConfigId)`.

Two representations, chosen by the board's complexity (§3.1 picks):

- **(Recommended for the in-scope simple case) A small interned configuration id.** The bus maintains a monotonic-ish
  *interned* id over the distinct bank configurations it has seen. A "configuration" is the tuple of which backing each
  banked window currently points at. When a `Remap` produces a configuration the bus has seen before, it returns that
  configuration's existing id (so re-entering bank B yields the **same** `BankConfigId` it had last time — that is what
  makes the cache hit). A never-seen configuration gets a fresh id. For the simple fixed-window case (one window, N
  banks) this is just "the bank number," and the interning is a small dictionary keyed on the window→backing tuple.
- **(For richer cases) A composed bitfield.** When there are a few independent windows (e.g. a low ROM bank + a high RAM
  bank), `BankConfigId` is a packed bitfield: `bank_low | (bank_high << k) | …`, computed by the bus from each window's
  current selection. Still a single `uint`, still O(1) to read.

The critical property either way: **`BankConfigId` is stable across leaving and re-entering the same configuration** (a
re-entered bank reproduces its id) and **distinct for distinct configurations** (so two banks never share a cache slot).
A pure monotonic "bump on every remap" counter is WRONG here — it would give bank B a *new* id every time it is
re-entered, defeating reuse entirely (it is the right shape for a *dirty epoch*, the wrong shape for a *configuration
id*; §7 Alternative notes this trap). Interning is what turns "which configuration" into a reusable key.

### 2.2 The cache key becomes `(PC, BankConfigId)`

`BlockCache._blocks` is re-keyed from `ushort` to a `(ushort Pc, uint Bank)` value-tuple (or a small `readonly record
struct BlockKey(ushort Pc, uint Bank)`). The per-page index `_blocksByPage`, the chain table, and the PR-S
`_recompiles`/`_cooldown` maps key on the same `BlockKey` (or carry the bank alongside the PC). Memory/footprint and
eviction policy are §3.2. For a **non-banking board** the bank is always a single constant id (`0`), so the cache behaves
exactly as today (one slot per PC) — the change is inert when banking is not declared.

### 2.3 The bank-switch event path distinguishes a REMAP from a WRITE — a remap does NOT evict the other bank's blocks

This is the heart of the ADR and the explicit non-conflation the prompt demands:

- **A WRITE to a code page (SMC)** changes the bytes *in place* under the *current* bank. It must **evict** the affected
  blocks (they are now stale) — the existing `dirty.Mark(page)` → `InvalidateIfDirty` → `Evict` path
  (`BlockCache.cs:112-160`), unchanged. SMC eviction is keyed by `(page, current bank)`: a write under bank B
  invalidates bank B's blocks on that page; bank A's blocks for the same page are a *different* configuration's code and
  are untouched (they are not stale — bank A's bytes did not change).
- **A REMAP (bank switch)** changes *which memory is visible*, not the bytes of any bank. It must **NOT evict** the
  outgoing bank's blocks (the whole point of this ADR — they are reusable). Instead it: (1) computes the new
  `BankConfigId` (interned, §2.1); (2) re-patches the single shared `Fastmem` page table's *contents* for the windowed
  pages to the newly-active bank's backing (the ADR 0009 §3.2 `Fastmem` patch — still required, because the live page
  table must describe the *active* bank for whichever block runs next); (3) publishes the new `BankConfigId` so the next
  dispatch keys lookups on it. The next `GetOrCompile((pc, newBank), …)` then **hits** the cached block for that bank if
  one exists, or compiles it the first time.

The distinction is encoded in the bus's two operations: `Write8` to a banked-window RAM page → SMC dirty-mark (evict);
`Remap` (the mapper's bank-select register `Write`, on the MMIO slow path) → `BankConfigId` recompute + `Fastmem` patch +
publish (no evict). ADR 0009 Decision 2's `OnRemap` no longer *evicts*; under ADR 0013 it *re-patches Fastmem and
republishes the bank id* (eviction becomes the SMC-only path). §6 validates exactly this: switching banks in a loop
produces **no growth in recompiles per switch**.

### 2.4 The correctness invariant (stated here, proved in §4)

> **A compiled block `B` keyed `(PC, bankX)` is dispatched (entered, and continued via any chain edge) ONLY while
> `BankConfigId == bankX`, AND the shared `Fastmem` page table's contents over `B`'s windowed pages describe `bankX`'s
> backing. Equivalently: the active `BankConfigId` and the live `Fastmem` contents are always mutually consistent, and a
> block is reachable only under the bank it was compiled from.**

The two clauses are the two ways a stale block could run: (a) the *dispatcher* picks the wrong block (handled by the key
— a `(PC, bankX)` block is unreachable when `BankConfigId != bankX`), and (b) a *correctly-picked* block reads the wrong
*data* because `Fastmem` describes a different bank than the block was compiled under (handled by the remap re-patching
`Fastmem` atomically with publishing the new id — §4 argument 2). The single fact that makes both provable is §1.3's
"emitted IL re-indexes `PageBacking[page]` live, never bakes a pointer": the block's data reads always go through the
*current* `Fastmem`, so as long as the current `Fastmem` matches the current bank and the current bank matches the
block's key, the block reads exactly its own bank's memory.

### 2.5 Scope: the simple fixed-window bank switch is IN; arbitrary MMU-style mapping is deferred (§5)

In scope: one or a few **fixed windows**, each remappable to one of a small set of backings, switched by a mapper's
bank-select register. This covers the SP1 Atari cartridge story, the C64/Apple II bank cases, and the simple PC EMS
window. Out of scope (YAGNI, §5): arbitrary per-page MMU/MMU-style mapping where every page can independently map
anywhere (the configuration space explodes and interning stops being cheap); per-cycle bank changes; and self-modifying
*mappers* (a mapper whose bank-select logic is itself in banked RAM). These are flagged as open questions, not built.

---

## 3. Design detail

### 3.1 `bankState` representation and update path

**Ownership.** The bus (`AddressSpace`) owns `BankConfigId` because the bus is the single place a remap is applied (ADR
0009 Decision 2 routes *all* remapping through `AddressSpace.Remap` so a device cannot remap without signaling). Putting
`BankConfigId` anywhere else (the CPU, the mapper device) risks two sources of truth.

**Computation.** On each `Remap`, the bus updates the affected window's current-backing record, then computes the
configuration id:

- *Simple single-window:* `BankConfigId` = the bank index the mapper selected (the mapper passes it, or the bus derives
  it by interning the window→backing identity). O(1).
- *Few independent windows:* the bus interns the tuple `(window0Backing, window1Backing, …)` into a small id via a
  `Dictionary<config-tuple, uint>` it maintains. First time a configuration appears → assign the next id; thereafter →
  return the stored id. The interning dictionary is bounded by the number of *distinct* configurations the program
  actually uses (small — a 4-bank cartridge has ≤ 4; two 8-bank windows have ≤ 64, and a real program touches far fewer).

**The Core seam (additive, consistent with ADR 0009's direction — Core exposes, the JIT consumes).** The bus exposes the
live id and notifies listeners when it changes:

```csharp
namespace CpuEmulator.Core;

public partial interface IAddressSpace   // or on the concrete AddressSpace, reached via IMachineContext
{
    /// <summary>The current bank-configuration id (ADR 0013). 0 for a fixed-map (non-banking) board —
    /// where it never changes, so the JIT's (PC, 0) key behaves exactly as the legacy PC-only key.
    /// Stable across leaving + re-entering the same configuration (interned), distinct per configuration.
    /// Read O(1) by the JIT dispatcher once per dispatch (the GetRegister(PC) cost order).</summary>
    uint BankConfigId { get; }
}
```

The remap notification *extends* ADR 0009's `IMapInvalidationListener` rather than adding a second listener:

```csharp
namespace CpuEmulator.Core;

public interface IMapInvalidationListener
{
    /// <summary>ADR 0009 Decision 2: the mapping over [firstPage, firstPage+pageCount) changed at run time.
    /// ADR 0013: newBankConfigId is the bus's interned id for the NOW-ACTIVE configuration. The JIT
    /// re-patches its Fastmem contents for those pages to the active bank's backing and adopts
    /// newBankConfigId as the active key component. It does NOT evict the OTHER banks' blocks (the ADR 0013
    /// refinement — eviction is the SMC-write path, not the remap path).</summary>
    void OnRemap(int firstPage, int pageCount, uint newBankConfigId);
}
```

**How the JIT reads it per dispatch without per-dispatch cost.** Two equally cheap options; the design picks (a):

- **(a) Push (recommended):** the JIT caches the active id in a `JittedCpu` field `_activeBank`, updated *only* in
  `OnRemap` (a rare event). The dispatcher reads the field — a plain field load, cheaper than the existing
  `GetRegister(_pcName)` interface call. This is the #42-aligned choice: the per-dispatch cost is one field read; the
  work happens on the rare remap, not on every dispatch.
- **(b) Pull:** the dispatcher reads `_bus.BankConfigId` each dispatch (an interface getter, same order as
  `GetRegister`). Simpler (no field to keep in sync) but one extra interface call per dispatch vs (a)'s field read. (a)
  wins on the #42 floor.

Either way, **no per-access and no per-page-scan cost** — the cost is O(1) per dispatch, paid where the PC read is
already paid (`JittedCpu.cs:147`).

### 3.2 Cache structure, footprint, and eviction policy

**Structure.** `BlockCache<TCpu>` changes its key type from `ushort` to `BlockKey`:

```csharp
internal readonly record struct BlockKey(ushort Pc, uint Bank);
// _blocks:        Dictionary<BlockKey, CompiledBlock<TCpu>>
// _blocksByPage:  Dictionary<int, List<CompiledBlock<TCpu>>>   // page → blocks, UNCHANGED key (page is bank-agnostic;
//                                                              //   a page's blocks may belong to several banks)
// PR-S maps:      keyed BlockKey (a bank's hot PC cools independently of another bank's same PC)
// ChainTable:     inbound keyed BlockKey (a chain edge targets a (PC, bank), §4.3)
```

`CompiledBlock` carries its `Bank` alongside `EntryPc` (so `Evict` and the page index can recover the key). `_blocksByPage`
stays keyed by **page** (bank-agnostic) because SMC dirty-marking is by physical page; the per-page list now holds blocks
from possibly several banks, and SMC eviction filters to the current bank (§2.3, §4.4).

**Footprint.** The cache grows from "one block per hot PC" to "one block per (hot PC, bank actually entered)." Bounded by
`hot_PCs_in_window × banks_actually_run`. For a 4-bank, 16 KiB-window cartridge with ~hundreds of hot block PCs, that is
a few thousand `DynamicMethod`s — each a delegate + its IL, on the order of low hundreds of bytes to a few KiB. Worst
realistic case (low MiB of JIT'd code) is acceptable and is bounded by the eviction policy below. **Non-banked pages are
unaffected** — a block whose `SpannedPages` are all outside any banked window has the same bytes under every bank, so it
should key on a *single* canonical bank (the "bank-invariant" optimization, §7 note / Open Question 2) rather than be
duplicated per bank. The simple first cut keys *every* block on the active bank (correct, slightly wasteful for
bank-invariant blocks); the bank-invariant refinement is deferred.

**Eviction policy.** Today the cache is unbounded and relies on SMC/remap eviction + `FlushAll` (reuse) to bound it. With
per-bank specialization the cache can grow with the number of banks, so a bound is more important:

- **First cut: keep unbounded, add instrumentation.** Track `_blocks.Count` and a per-bank block count
  (`TotalBlocksByBank`) as committed instrumentation (the same "quantify first" artifact PR-S added —
  `TotalRecompiles`/`TotalEvictions`, `BlockCache.cs:40-43`). Prove on real banked programs that the footprint is bounded
  in practice before adding a reclamation policy.
- **If profiling shows unbounded growth: bound per bank with LRU.** A capacity `JitOptions.PerBankBlockCap` and an LRU
  list per bank; evict the least-recently-entered bank's coldest blocks when over cap. This is a clean reclamation (an
  evicted block simply recompiles on next entry — the exact pre-ADR-0013 behavior), so it is safe to add later. **Do NOT
  build it pre-emptively** (YAGNI — the bound matters only if a real program's bank × hot-PC product is large; measure
  first).
- **A whole bank can be dropped cheaply** when a configuration is provably gone (e.g. a cartridge unmapped): drop all
  `BlockKey` with that `Bank`. Not needed for the in-scope case; noted for completeness.

### 3.3 The remap-vs-write distinction in the bus, concretely

| Event | Bus operation | `BankConfigId` | `Fastmem` contents | Block cache action | Why |
|---|---|---|---|---|---|
| Guest writes a banked-window RAM byte (SMC) | `Write8` (fastmem RAM store → `dirty.Mark(page)`) | unchanged | unchanged (same backing) | evict `(page, CURRENT bank)` blocks on next `InvalidateIfDirty` | the bytes changed in place under the current bank; that bank's blocks are stale |
| Mapper bank-select register write (REMAP) | `Remap(...)` → `OnRemap(firstPage, pageCount, newId)` | recomputed (interned) | re-patched to the new bank's backing for the windowed pages | **none** — reuse the new bank's blocks (compile only if absent) | which memory is visible changed; no bank's bytes changed, so no bank's blocks are stale |

The two are never conflated because they enter through two different bus operations. A mapper that (unusually) has RAM in
its banked window *and* self-modifies it hits both paths independently: the SMC write evicts the current bank's blocks for
that page (correct), and a later remap switches banks without eviction (correct). They compose.

---

## 4. Correctness (the load-bearing section)

The invariant (§2.4) decomposes into four arguments. The whole proof rests on §1.3's verified fact: **emitted blocks
re-index the shared `Fastmem.PageBacking[page]` live on every access (`BlockCompiler.cs:834-855`, `:885-917`); no
backing-array pointer is baked across instructions.**

**Argument 1 — the dispatcher never enters a wrong-bank block.** `GetOrCompile` is keyed `(pc, _activeBank)`
(`JittedCpu.cs:162` becomes `_cache.GetOrCompile(new BlockKey(pc, _activeBank), …)`). A `(PC, bankX)` block lives in the
cache under key `(PC, bankX)` and is returned **only** when the lookup key's bank is `bankX`. When `_activeBank != bankX`
the lookup is a miss for `bankX`'s slot and a hit/compile for the active bank's slot. So the dispatcher entry is always
the active bank's block. `_activeBank` is updated only in `OnRemap`, atomically with the `Fastmem` re-patch (same call),
so between two dispatches the bank and the page table are mutually consistent.

**Argument 2 — a correctly-entered block reads its own bank's memory.** A `(PC, bankX)` block is entered only while
`_activeBank == bankX` (argument 1). By the `OnRemap` atomicity, `_activeBank == bankX` implies the shared `Fastmem`
contents over the windowed pages describe `bankX`'s backing (the remap that set the active id to `bankX` also re-patched
`Fastmem` to `bankX`'s backing, in the same `OnRemap` call; no other code path changes either). The block's data reads go
through that live `Fastmem` (§1.3). Therefore the block reads exactly `bankX`'s memory. **The `Fastmem` `PageBacking`
snapshot stays valid for the block's bank because the page table is re-patched to the active bank on every remap, and the
block runs only under its own active bank.**

This is the subtle part the prompt flags ("fastmem `PageBacking` snapshots must stay valid — a block reads the right
backing for its bank"): the snapshot is *shared and mutable*, not per-block. It is valid because the snapshot always
reflects the *active* bank and a block runs only under its active bank — not because each block carries a frozen copy. A
per-block frozen `Fastmem` copy is the rejected Approach 3 (§7); it is more memory and more emit complexity for no
correctness gain, because the live-re-index design already guarantees the block reads the active page table.

**Argument 3 — chaining cannot leap across a bank boundary into a wrong-bank block.** A chain edge resolves its successor
BY (PC, bank) through the live cache (`ResolveChain` → `GetOrCompile`, `BlockCache.cs:99-104`), and the resolution must
use the **active** bank at the moment the edge is taken, not the predecessor's compile-time bank. Two sub-cases:

- *Within one bank (no remap between predecessor and successor):* the active bank is unchanged, so the successor resolves
  under the same bank — identical to today, just bank-tagged.
- *A remap happens "between" blocks:* a remap is a mapper bank-select **register write**, which runs on the MMIO slow
  path inside *some* instruction's emitted store (or a fallback `Step`). The store's `Write8` → `Remap` → `OnRemap`
  updates `_activeBank` and the `Fastmem`. The crucial gate: **a chain edge must round-trip to the dispatcher (not chain
  on) when the active bank changed during the block**, so the successor is resolved under the new bank. This is the
  bank-switch analogue of the existing `dirty.Any` chain-break gate (`EmitChainOrExit` gate (3),
  `BlockCompiler.cs:1402-1405`). DECISION 13-CHAIN (§4.3 below) makes a remap **mark the windowed pages dirty as well as
  re-patch** — so the existing `dirty.Any` gate already breaks the chain and forces a dispatcher round-trip, where the
  new `_activeBank` keys the lookup. No new emit gate is required; the remap rides the proven SMC backstop. (See §4.3 for
  why this is correct and not over-eager.)

**Argument 4 — SMC eviction stays bank-precise.** A self-modifying write marks `dirty.Mark(page)`. On the next dispatch,
`InvalidateIfDirty` evicts the page's blocks. Under per-bank keying, it must evict only the **current bank's** blocks for
that page — bank A's $8000 block is not stale when the guest writes $8000 *under bank B* (different memory). So
`InvalidateIfDirty`'s per-page eviction filters `_blocksByPage[page]` to blocks whose `Bank == _activeBank`. (The page
index is bank-agnostic; the eviction is bank-filtered.) This preserves the exact SMC semantics per bank and is the place
the SMC axis (ADR 0011 §3.4 / PR-S) and the banking axis stay cleanly separated: SMC = evict the current bank's stale
blocks; banking = switch which bank's blocks are live, evicting none.

### 4.3 DECISION 13-CHAIN — a remap marks the windowed pages dirty (rides the existing chain-break + invalidation gate), so no new emit gate is needed

A remap, under ADR 0013, must do three things atomically in `OnRemap`: (1) recompute `_activeBank`; (2) re-patch
`Fastmem` for the windowed pages; (3) **mark the windowed pages dirty** (`Dirty.Mark(page)` for each windowed page). Step
(3) is what makes the design ride the proven machinery:

- The emitted **chain-edge `dirty.Any` gate** (`EmitChainOrExit` gate (3)) already breaks any running chain and
  round-trips to the dispatcher whenever `dirty.Any` — so a remap that fires mid-block (the mapper register write) forces
  the chain to break and the next lookup to use the new `_activeBank`. **Argument 3 is satisfied for free.**
- `InvalidateIfDirty` then runs over the dirtied windowed pages. **But under ADR 0013 it must NOT evict the other banks'
  blocks** — only SMC writes evict. So the remap-marked dirty pages need a *different* consumer than the SMC-marked ones.

The clean resolution (DECISION 13-CHAIN): a remap marks the windowed pages with a **bank-transition marker** distinct
from the SMC dirty mark — a separate `Dirty.MarkBankTransition(page)` (or a single `BankSwitchPending` flag, since a
remap is window-global, not per-page). The chain-break gate reads `dirty.Any || bankSwitchPending` (one extra OR of a
bool — negligible, and it can be folded into `Any`). `InvalidateIfDirty` on a bank-transition does **nothing but clear
the marker** (the `Fastmem` re-patch + the key change already did the work; no eviction). The SMC dirty marks continue to
evict (bank-filtered, argument 4). This keeps the remap on the proven chain-break path **without** abusing the SMC
eviction to throw away the very blocks ADR 0013 exists to keep.

> **Implementation note for Planner:** the cheapest faithful spelling is a single `bool _bankSwitchPending` on the
> dispatcher (a remap sets it; the chain-break gate ORs it; the dispatcher clears it after re-keying the lookup), because
> a remap is window-global — there is no need for per-page bank-transition marks. The per-page framing above is the
> conservative general form; the single-flag form is the recommended first cut. Either is byte-identical in result.

### 4.4 The oracle backstop

The differential fuzzer (JIT-vs-interpreter) and the TomHarte parity sweeps are the un-fakeable backstop, exactly as for
PR-S and #42: if a per-bank block ever ran under the wrong bank (a stale `Fastmem`, a missed chain break, a mis-filtered
SMC eviction), a banked-program differential seed would diverge from the interpreter. §6's synthetic bank-switch test +
the fuzzer extended with a banking peripheral are the gates.

---

## 5. Scope / non-goals (YAGNI)

**In scope (build this):**

- One or a few **fixed windows**, each remappable to a small set of backings, switched by a mapper register. The simple
  cartridge/EMS/language-card case.
- `BankConfigId` as an interned small `uint` (single-window: the bank index; few windows: a packed/interned tuple).
- The `(PC, BankConfigId)` key, the remap-vs-write distinction, the chain-break-on-remap (DECISION 13-CHAIN), the
  bank-filtered SMC eviction, committed footprint instrumentation.
- Gated behind a `JitOptions` flag + a board "banking-capable" declaration; **inert for non-banking boards** (bank id is
  always `0`, one slot per PC, byte-identical to today).

**Deferred / explicitly NOT in this ADR:**

- **Arbitrary MMU-style mapping** (every page independently maps anywhere — a paging MMU, a full 68030 PMMU, large PC
  EMS/XMS maps). The configuration space explodes; interning a `uint` id stops being cheap; the cache footprint becomes
  unbounded in a way LRU alone may not tame. This needs its own ADR if/when a board demands it — likely a *different*
  mechanism (a per-access translation the fastmem split was built to avoid, or coarse whole-cache flush on map change).
- **The bank-invariant-block optimization** (key a block that touches no banked page on a single canonical bank, so it is
  not duplicated per bank). A real footprint win, but additive and measurement-gated — Open Question 2.
- **A bounded LRU per-bank eviction policy.** Designed (§3.2) but not built until instrumentation shows it is needed.
- **Per-cycle / per-scanline-exact bank changes** beyond what the coarse remap-at-register-write model gives. A mapper
  that must switch mid-instruction at an exact cycle is the ADR 0009 Decision 3 (fine timing tier) territory, composed
  with this — flagged, not designed here.
- **Self-modifying mappers** (a mapper whose bank-select logic executes from banked RAM that it itself remaps). A
  correctness corner; out of scope until a real board needs it (Open Question 3).
- **Cross-CPU specifics.** The design is CPU-agnostic (it touches `BlockCache`/`JittedCpu`/`Fastmem`/the bus seam, none
  CPU-specific). The 8086's `(CS<<4)+IP` block keying (`BlockCompiler.cs:147-152`, the CS-aliasing invariant) is a
  *separate* axis from `BankConfigId` (segment vs. bank) and composes orthogonally — a far-CS change widens the PC key
  (PR-D's concern), a bank switch changes the bank key; flagged as Open Question 4, not designed here.

---

## 6. Validation approach

A **synthetic bank-switch test** — no full board needed, per the prompt. A minimal machine with a tiny banking
peripheral, exercised through the JIT, asserting BOTH correct execution AND block reuse (recompiles do not grow per
switch).

**The synthetic machine.** A `BankSwitchTestPeripheral` (an `IPeripheral`) mapped at a control register, plus a windowed
region (say one 256-byte page at $8000) backed by N distinct banks (N small byte[]s with distinguishable contents — e.g.
bank k's window runs `LDA #k / STA $00 / RTS`, or a value that lets the test assert which bank executed). A write to the
control register calls `AddressSpace.Remap` to swap the window to the selected bank (and, per §2.1, the bus interns the
configuration id).

**Test 1 — correct execution across switches.** A driver loop: select bank 0, JSR $8000, assert the side effect is bank
0's; select bank 1, JSR $8000, assert bank 1's; … round-robin many times. Run through the `JittedCpu` and assert the
observed side effects match the *interpreter* run of the identical program (the differential oracle). This proves the
remap-vs-write distinction and argument 1/2 (the right bank's code ran and read the right memory).

**Test 2 — block reuse (the payoff gate; the no-recompile-growth assertion).** Using the committed instrumentation
(`TotalRecompiles` / a new `TotalBlocksByBank` / `CompileCount`), run the round-robin switch loop for K full cycles
through all N banks and assert: **the number of compiles is bounded by `N × (hot PCs in the window)` and does NOT grow
with K.** I.e. after the first pass compiles each bank's window block once, subsequent passes are cache HITS — recompiles
per switch trend to zero. Contrast with the pre-ADR-0013 (PC-only) behavior, which recompiles the window every switch:
the test runs both (the `JitOptions` flag OFF = legacy evict-on-remap, ON = per-bank reuse) and asserts the ON path's
compile count is materially lower and flat in K, while the OFF path's grows linearly in K. This is the directional gate,
mirroring the PR-S `KlausSmcLeverDirectionalTests` and #42 `KlausDispatchOverheadDirectionalTests` idioms (bounded,
deterministic, an A/B on a `JitOptions` flag).

**Test 3 — SMC × banking composition.** A banked window backed by *RAM* (writable): under bank B, self-modify a window
byte, assert bank B's block re-decodes (SMC eviction fires, bank-filtered); switch to bank A, assert bank A's block is
UNAFFECTED (its bytes never changed) and reused. This pins argument 4 (bank-precise SMC eviction) and the non-conflation.

**Test 4 — chain-crossing-a-remap.** A block that chains into the window, then a remap fires (a mapper register write
mid-chain), then the chain would continue into the window: assert the chain BREAKS at the remap and the post-remap entry
resolves under the new bank (DECISION 13-CHAIN / argument 3). Use the `ChainStepCount` seam (`JittedCpu.cs:93`) to assert
the chain did not leap across the boundary.

**The differential-fuzzer / parity stance.** Extend `DifferentialFuzzTests` with a banking-peripheral variant: seed a
program that bank-switches and runs banked code, run it JIT (per-bank ON) vs interpreter, assert byte-identical final
state — across many seeds. Because per-bank specialization is a pure *scheduling/caching* change (the compiled code per
bank is the same code the legacy path would compile; only *when* it is compiled vs reused differs), it is
**parity-transparent**: the fuzzer must show JIT == interpreter with the flag ON *and* OFF, exactly as it does for
chaining-on/off, the SMC lever on/off, and the #42 list/legacy paths. No new correctness surface escapes the oracle. Full
TomHarte sweeps are unaffected (they never bank-switch), so they stay byte-identical — the change is inert for them.

---

## 7. Decision + alternatives (the approaches considered)

Three approaches, scored on correctness-safety × footprint × emit-complexity × reuse-payoff.

### Approach 1 (CHOSEN) — compose the cache key from `(PC, BankConfigId)`, one shared `Fastmem` re-patched per active bank

Re-key `BlockCache` on `(PC, bank)`; the bus interns a `BankConfigId`; a remap re-patches the single shared `Fastmem` and
republishes the id without eviction; SMC eviction is bank-filtered; the chain breaks on remap via the proven dirty gate
(DECISION 13-CHAIN).

- *Correctness-safety:* **maximal.** It reuses the entire proven machinery — the live-re-indexed `Fastmem` (so no per-block
  page-table copy can drift), the SMC eviction path (bank-filtered), the chain-break gate (remap rides it). The invariant
  (§2.4) is provable in four short arguments (§4), each anchored to a verified code fact (§1.3). The single shared
  `Fastmem` means there is exactly one page table to keep consistent with one active-bank id — no fan-out of mutable
  snapshots.
- *Footprint:* one block per (hot PC, bank entered). Bounded, instrumented, LRU-able later (§3.2).
- *Emit-complexity:* **zero emitted-IL change.** The block bodies are byte-identical to today — they already read the
  shared `Fastmem` live. The change is entirely in the *cache key* (`BlockCache`/`JittedCpu`/the bus seam), never in
  `BlockCompiler`'s emit arms. This is the decisive advantage: the hottest, most-tested code (the emit arms) is untouched.
- *Reuse-payoff:* **full.** A re-entered bank's blocks are cache hits.

### Approach 2 (REJECTED as the primary) — a per-bank cache PARTITION (a separate `BlockCache` per bank)

Hold N independent `BlockCache` instances, one per bank; the dispatcher selects the partition by `_activeBank`.

- *Correctness-safety:* good — partitions are naturally isolated. But the **shared cross-cutting concerns fragment**: SMC
  dirty-marking, the chain table, and `Fastmem` are global (a page is one physical page regardless of partition), so
  either each partition re-implements them (duplication, drift risk) or they stay global and the partition boundary is
  artificial. The chain table especially wants to span partitions (a non-banked block chaining into a banked one), which a
  hard partition fights.
- *Footprint:* same as Approach 1 (the blocks are the same set).
- *Emit-complexity:* same zero IL change, but more dispatcher plumbing (partition selection, cross-partition chaining).
- *Reuse-payoff:* full.
- *Verdict:* it is Approach 1 with the bank pushed from the key into a container split — more moving parts (N caches, N
  per-page indexes, a cross-partition chain story) for no correctness or footprint gain. **Rejected:** the `(PC, bank)`
  key is the same idea expressed once, in one cache, reusing the existing global SMC/chain/`Fastmem` machinery
  bank-filtered where needed. Worth keeping in mind if a future MMU-scale case wants whole-partition drop (§5 deferral),
  but not for the in-scope simple case.

### Approach 3 (REJECTED) — tag each block with a bank-validity mask + a per-block frozen `Fastmem` snapshot

Keep the PC-only key but tag each block with the bank(s) it is valid under (a bitmask), and freeze each block's
`Fastmem` view at compile time so it reads its own bank's backing regardless of the active page table.

- *Correctness-safety:* **worse, and more complex.** A per-block frozen page table means N copies of the `Fastmem` arrays
  (or N copies of the windowed slots), each a mutable snapshot that must be kept consistent with SMC writes *to that
  bank* — reintroducing exactly the multi-snapshot drift risk Approach 1 avoids by having one shared table. The
  validity-mask-on-a-PC-key still can't hold two banks' *different* blocks for the same PC in one slot (the core problem),
  so it does not even solve reuse — it only annotates a single block. To hold both banks' blocks it must *also* key on
  bank, at which point the mask is redundant.
- *Emit-complexity:* **highest** — the emit arms would have to bind a per-block backing array (breaking the "re-index the
  shared `Fastmem` live" property that makes everything else simple, §1.3), or carry a per-block `Fastmem` arg. This
  touches the hottest code and forfeits Approach 1's zero-IL-change advantage.
- *Verdict:* **rejected.** It is more memory, more emit risk, and does not actually deliver simultaneous per-bank blocks
  without *also* adopting a bank key. The per-block frozen snapshot is a solution to a problem Approach 1 does not have
  (a shared mutable page table is *fine* precisely because a block runs only under its matching active bank — §4
  argument 2).

### Recommendation

**Approach 1.** It delivers full reuse with zero emitted-IL change, the strongest correctness story (one shared
`Fastmem`, one cache, the proven SMC + chain-break machinery bank-filtered), the smallest blast radius (the cache key and
one bus seam, not the emit arms), and a clean inert-when-not-banking property. Approaches 2 and 3 add structure (a
partition container; per-block frozen snapshots) that buys no correctness or reuse and costs more complexity — Approach 2
fragments the global concerns, Approach 3 forfeits the live-re-index simplicity and still needs a bank key anyway.

---

## 8. Consequences

**Good.**
- A re-entered bank reuses its compiled blocks: the bank-switch recompile-thrash collapses to one compile per (bank, hot
  PC). The banking analogue of what PR-S did for SMC thrash, achieved by a *caching* change rather than a *fallback*
  policy.
- **Zero emitted-IL change** (Approach 1): the emit arms — the most-tested, hottest code — are untouched. The change is
  confined to `BlockCache` (key), `JittedCpu` (read the active bank), `Fastmem` (already re-patched per ADR 0009
  Decision 2), and one additive `Core` bus seam.
- The remap-vs-write distinction is made *structural* (two different bus operations), so SMC and banking can never be
  conflated, and they compose (a self-modifying banked RAM works correctly).
- **Inert for non-banking boards** — bank id `0`, one slot per PC, byte-identical to today. No cost imposed on the fixed-map
  common case (every current board + every TomHarte sweep).
- Composes cleanly with the in-flight #42 dirtied-page-list (the per-dispatch bank read is an O(1) field load, not a
  scan) and with PR-S (the SMC lever's recompile cap now keys per `(PC, bank)`, so a bank's hot PC cools independently).

**Bad / accepted.**
- **Cache footprint grows** with the number of banks actually entered (one block set per bank). Bounded and instrumented;
  an LRU bound is designed (§3.2) but deferred until measurement justifies it. The bank-invariant-block optimization
  (don't duplicate non-banked blocks per bank) is deferred (Open Question 2), so the first cut over-duplicates slightly.
- **A new `Core` bus seam** (`BankConfigId` + the `OnRemap` bank-id parameter) that extends ADR 0009 Decision 2's already-new
  remap seam. Mitigated by extending the *existing* `IMapInvalidationListener` rather than adding a parallel listener, and
  by the `Core`-defines-JIT-consumes dependency direction ADR 0009 established.
- **The interning logic is a new bus responsibility** — the bus must correctly reproduce a configuration's id on
  re-entry (the stability property §2.1). A bug here (a fresh id per re-entry) silently *defeats* reuse (the cache never
  hits) without being *incorrect* (it just recompiles, as today). So a reuse bug degrades to the status quo, not to a
  correctness failure — a benign failure mode, caught by Test 2's no-growth assertion.
- **Mapper-author discipline:** a mapper must route *all* bank changes through `AddressSpace.Remap` (so the id + `Fastmem`
  + dirty-mark are updated atomically), exactly as ADR 0009 Decision 2 already requires. A mapper that pokes the page
  table behind that method breaks the invariant — the same teeth ADR 0009 Decision 2 already accepted.

**Reversibility.** The capability is behind a `JitOptions` flag + a board declaration; OFF restores the exact
pre-ADR-0013 evict-on-remap behavior (ADR 0009 Decision 2 as-is). The key-type change is internal to `CpuEmulator.Jit`;
the `Core` seam is additive (non-banking consumers ignore `BankConfigId`/never call `Remap`). It can be shipped dark
(flag default OFF) and enabled per banked board.

---

## 9. Open questions

1. **`BankConfigId` interning location + lifetime (§2.1, §3.1).** Does the interning dictionary live on `AddressSpace`
   (one per bus) or on a dedicated bank-manager the mapper and bus share? And is the id space ever reclaimed (a
   configuration that will never recur)? For the in-scope simple case the bus-owned dictionary is sufficient and unbounded
   is fine (few distinct configs). Resolve against the first real mapper (SP1 Atari cartridge).
2. **The bank-invariant-block optimization (§3.2, §5).** Should a block whose `SpannedPages` are entirely outside any
   banked window key on a single canonical bank (so it is shared across banks, not duplicated)? A real footprint win and
   a natural fit (the bus knows which pages are banked), but additive. Measurement-gated: build it only if the
   footprint instrumentation shows non-banked-block duplication is material. Likely worth it; deferred to keep the first
   cut simple.
3. **Self-modifying mappers (§5).** A mapper whose bank-select logic executes from banked RAM it itself remaps is a
   correctness corner (the code that does the remap may be banked out by the remap). Does any in-scope board do this?
   Expected no for SP1; resolve if a board needs it.
4. **Banking × segmentation (8086) and × the CS-key (§5).** The 8086 already keys blocks on `(CS<<4)+IP` physical with a
   CS-aliasing invariant (`BlockCompiler.cs:147-152`); a banked PC EMS window adds a `BankConfigId` axis on top. Confirm
   the two keys compose (the block key becomes `(physicalPC-or-IP, bank)` and CS changes already widen the PC key via
   PR-D). Expected orthogonal; confirm at the SP3 PC-clone plan time.
5. **The chain-break marker spelling (DECISION 13-CHAIN, §4.3).** A single `bool _bankSwitchPending` (window-global) vs a
   per-page bank-transition mark. The single flag is the recommended first cut (a remap is window-global); confirm there
   is no case where two windows remap independently within one block such that a per-page mark is needed. Expected the
   single flag suffices; verify at implementation.
6. **Eviction policy trigger (§3.2).** At what measured footprint does the unbounded-first-cut warrant the LRU bound, and
   what is the right `PerBankBlockCap` default? Needs the committed `TotalBlocksByBank` instrumentation run against a real
   banked program. Resolve from measurement, not a guess.

---

*End of ADR 0013. The decision: per-bank `(PC, BankConfigId)` block specialization via Approach 1 — compose the cache key
from `(PC, bank)`, intern a stable `BankConfigId` on the bus, re-patch the single shared `Fastmem` per active bank on
remap WITHOUT eviction (eviction stays the SMC-write path, bank-filtered), and ride the proven chain-break gate on remap
(DECISION 13-CHAIN). The remap-vs-write distinction is structural (two bus operations) so SMC and banking never conflate
and compose. The correctness invariant — a `(PC, bankX)` block runs only while `BankConfigId == bankX` AND the live
`Fastmem` describes `bankX` — is provable because emitted blocks re-index the shared `Fastmem` live (verified,
`BlockCompiler.cs:834-855`/`:885-917`), so a block always reads the active page table and runs only under its own active
bank. Validation is a synthetic banking peripheral (no full board): correct execution across switches, block reuse with
no recompile growth per switch (the directional A/B gate), SMC × banking composition, chain-crossing-a-remap, and the
differential fuzzer extended with a banking variant. Scope is the simple fixed-window switch; arbitrary MMU-style mapping,
the bank-invariant-block optimization, and an LRU bound are deferred (YAGNI). Designer: the only UX-adjacent implication
is that banked machines (cartridges, EMS) become *performant*, not just correct — no surface change. Planner can expand
§3's seam shapes + §6's tests into tasks once the owner signs off; this is a JIT-optimization item gated on measured
bank-switch recompile-thrash (ADR 0011 §3.4), to be implemented after the #42 dirtied-page-list lands (it composes with,
and assumes, the per-dispatch O(1) discipline).*
