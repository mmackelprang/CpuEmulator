# ADR 0009 — The device↔JIT contract and JIT-derived peripheral-design principles

> **Status:** ACCEPTED (owner-approved 2026-06-17). No implementation now — this is a
> cross-cutting design that constrains SP0's device contracts, SP1's peripherals/mappers, and the M6 JIT-optimization
> work. It is deliberately authored **ahead** of SP0 (which is itself deferred until after M5 + M6 ship) so the device
> contracts SP0 introduces are shaped by the JIT's existing fast/slow-path reality from the start, not retrofitted.
> **Date:** 2026-06-17
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Supersedes / relates to:**
> - **The SP0 foundation design** (`docs/superpowers/specs/2026-06-17-emulated-computer-sp0-foundation-design.md`) — the
>   `IDisplayDevice`/`IKeyboardSink`/`IBlockDevice` host-side contracts + the `MachineHost` pump. This ADR generalizes
>   SP0's `IDisplayDevice.RenderInto`-on-`FrameReady` snapshot pattern into a stated **fastmem-aware device-memory**
>   principle, and adds the bus→JIT invalidation hook + the tiered-timing declaration that SP0/SP1 will lean on. SP0 §4's
>   three contracts are confirmed; this ADR adds the *fast-RAM-region declaration* and *timing-tier* facets they imply.
> - **ADR 0008** (`0008-68000-control-flow-exceptions-and-the-timing-axis.md`) — §5 the prefetch-queue/cycle-exact timing
>   axis and the JIT's tier model (tier-0 interpreter oracle / tier-1 IL-JIT; "drop to the slower, exact tier only where a
>   device/software requires it"). Decision 3 below is the device-facing mirror of that same perf/accuracy lever.
> - **ADR 0002** (`0002-address-space-scaling.md`) — the flat 256-byte-page table + `TryGetDirectAccess` fastmem seam this
>   ADR's region-declaration and page-invalidation hooks build on; the 32-bit two-level-table deferral bounds Decision 1's
>   framebuffer-size reasoning.
> - **The framework research** (`docs/research/emulation-framework-research.md` §"fastmem split", §"tiered strategy") —
>   the original "emit a direct array read/write … a guest *can't* hit a device" fastmem rationale these principles
>   formalize for the device side.

---

## 1. Context

The emulated-computer arc (SP0 → SP1 Atari 800 → SP3 PC clone) introduces the first **real peripherals with hot data
paths**: framebuffers the guest writes thousands of times per frame, bank-switching cartridge mappers, and raster-timed
display chips. Until now every peripheral the framework has shipped (`SimpleUart`, `IntervalTimer`) is a handful of MMIO
registers touched rarely — so the existing device model (`IPeripheral.Read`/`Write` trap-per-access + `IScheduler`
event-driven timing + `IInterruptLine` wired-OR) has never been stressed against the JIT's performance model.

The JIT (`CpuEmulator.Jit`) is now a mature tier-1 IL-JIT (M2/M3 shipped for 6502 + Z80; M4.6 brings the 68000 through it
all-fallback). Its entire speed premise is the **fastmem split** (`Fastmem.cs`, `BlockCompiler.LoadByteFromBus`/
`EmitStoreByte`): every memory access the compiler emits branches at run time on `Fastmem.PageBacking[page]` —

- **non-null (RAM/ROM):** a direct backing-array load/store, `backing[PageOffset[page] + (addr & 0xFF)]`, at full JIT
  speed, no call;
- **null (MMIO/unmapped):** a `bus.Read8`/`bus.Write8` callout — an indirect virtual call into `AddressSpace`, which
  dispatches to `IPeripheral.Read`/`Write`.

A device's choice of *how it exposes its memory to the guest* therefore directly determines whether the guest runs at
tier-1 array-store speed or pays a per-access virtual-call MMIO tax. This is not a micro-optimization: an Atari 800
framebuffer is ~7,680 bytes (40×192 in ANTIC mode 0xF, more in others) repainted every frame at 60 Hz; a CGA text buffer
sees the guest's whole screen-clear loop. **If a framebuffer is modeled as a trapping MMIO region, every pixel write is a
virtual call into `IPeripheral.Write` — the JIT speedup is erased over exactly the code that matters most.** The fastmem
split was built precisely so the guest "can't hit a device" on the hot path; the device contracts must let a device opt
*into* that fast path for its bulk memory while keeping its control registers on the slow MMIO path.

Three further realities of the JIT shape the device contract:

1. **Blocks are keyed by PC and cached** (`BlockCache._blocks` keyed on `ushort` PC). The `Fastmem` page classification is
   computed **once at `JittedCpu` construction** (the comment at `Fastmem.cs:6` is explicit: "For a fixed-map 8-bit board
   the map is static"). A device that changes *what backing array is mapped at an address* at run time (a bank switch)
   therefore violates two baked-in assumptions at once: the fastmem page table is stale, **and** any cached block whose
   bytes came from that region is now decoding the wrong memory.
2. **Self-modifying-code invalidation already exists** (`BlockCache.InvalidateIfDirty` + `DirtyMap` + the per-page
   `_blocksByPage` index + the `ChainTable` sever). It is a *page-precise* mechanism: a write to a code page marks it
   dirty, and the dispatcher evicts exactly the blocks on dirtied pages before the next dispatch. **A bank switch is
   structurally the same event as SMC — "the bytes at this PC are no longer the bytes the cached block was compiled
   from" — but today nothing connects a peripheral's remap to that invalidation path.**
3. **Cycle accuracy is a tier, not a default.** ADR 0008 established that the framework runs fast (block-granular) and
   drops to cycle-exact only where the prefetch-queue/timing axis demands it. Devices have the identical spectrum: a UART
   or a disk controller is happy serviced at a block boundary (a few-microsecond jitter is invisible); an ANTIC display
   list interrupt or a C64 raster split is *defined by* its cycle-exact firing point and is wrong if serviced a block
   late. The device contract should let a device **declare** which it needs, so the machine pays cycle-exact overhead only
   where a device earns it — the device-side mirror of the JIT's tier-0/tier-1 lever.

This ADR formalizes four principles that fall out of those realities. They are stated now, ahead of SP0's implementation,
so SP0/SP1 build the device contracts already JIT-aware rather than discovering the MMIO tax (Principle 1) and the stale-
block hazard (Principle 2) the hard way when the first framebuffer or mapper lands.

### 1.1 What the shipped code already proves (verified, not assumed)

- **The fastmem fast/slow split is exactly as described and is RAM/ROM vs everything-else.** `Fastmem` (the whole file)
  classifies each 256-byte page from `AddressSpace.TryGetDirectAccess`: a page with `Backing != null` becomes a fastmem
  page; a peripheral or unmapped page leaves `PageBacking[p] == null` and takes the bus arm. `LoadByteFromBus`
  (`BlockCompiler.cs:350`) and `EmitStoreByte` (`:398`) emit exactly that runtime branch. So a device's bulk memory is
  fastmem-fast **if and only if it is registered as `MapMemory`** (a backing array), and is per-access-trapping **if it is
  registered as `MapPeripheral`**. The contract lever already exists at the `AddressSpace` API level; what is missing is a
  device-facing way to *declare* that split and a place for the device to *read back* its fast-RAM region.
- **The page table is static after construction.** `Fastmem`'s constructor walks every page once; nothing re-runs it.
  `JittedCpu` holds a single `_fastmem`. There is no remap path today. (Confirmed: no `MapMemory`/`MapPeripheral` caller
  exists after `Machine`'s phase-1/phase-3 construction in `Machine.cs:36-52`.)
- **The SMC invalidation path is page-precise and already severs chains.** `BlockCache.InvalidateIfDirty` (`:66`) →
  per-page `Evict` (`:83`) → `Chains.Sever`/`Chains.Forget`. The dispatcher consults it before every block dispatch
  (`JittedCpu.Run:124`). This is the exact machinery a bank switch needs; it is reached today only via the emitted RAM
  store's `dirty.Mark(page)`.
- **`IScheduler` is genuinely cycle-accurate and device-honest.** `CycleScheduler.CurrentCycle` (`:15`) returns the CPU's
  live cycle count mid-slice (via the bound time source) or the firing event's exact cycle during dispatch. `Machine.Run`
  (`:68`) chunks each slice to the next scheduled event so a callback fires at its exact cycle and its IRQ lands at the
  next instruction boundary. So a *coarse* device (schedule a 60 Hz vblank tick) and the substrate for a *fine* device
  (exact-cycle callbacks) both already exist — what is missing is the device's declaration of which granularity it needs
  and the machine's honoring of it (today the granularity is implicitly whatever `Machine.Run`'s slicing yields).
- **Devices already see "now" correctly for snapshots.** A display device's `FrameReady` is raised from a scheduled
  callback (SP0 §4.1), at which point `CurrentCycle` is the exact vblank cycle — so a snapshot-at-`FrameReady` reads a
  coherent guest-written framebuffer. SP0's `RenderInto`-on-`FrameReady` is already the right shape; Principle 1
  generalizes it.

---

## 2. Decisions

### Decision 1 — Fastmem-aware device memory: hot device memory is a fastmem-backed RAM region the device snapshots asynchronously, NOT a per-write MMIO trap

A device with a hot data path (a framebuffer, a text buffer, a tile/sprite RAM, a sound-sample buffer) **declares that
data path as one or more fastmem-backed RAM region(s)** that the guest writes at full tier-1 array-store speed. The device
**reads/snapshots that memory asynchronously** — at its own scheduled event (`FrameReady` for a display, an end-of-buffer
tick for audio) — rather than trapping each write through `IPeripheral.Write`. Its **control/status registers** stay an
MMIO region (a small `MapPeripheral` range) on the slow path, where per-access traps are correct and cheap because they
are rare.

A device thus has, in general, **two kinds of mapped region**:

- **Fast-RAM region(s):** bulk memory the guest reads/writes hot. Registered via `MapMemory` (a backing `byte[]` the
  device owns and the guest shares). `Fastmem` classifies it non-null → the JIT emits direct array access. The device
  reads its own backing array when it needs to present (snapshot at `FrameReady`).
- **MMIO register region(s):** the device's control/status/command ports. Registered via `MapPeripheral` → `Read`/`Write`
  traps. These are the writes with observable side effects (latch a palette, kick a DMA, raise/clear an IRQ).

The device declares this split through a small additive capability (`IFastMemoryProvider`, §3.1) the `Machine` consults at
build time to perform the right `MapMemory`/`MapPeripheral` calls — so `Fastmem` carves the regions out correctly without
the device author hand-wiring two `AddressSpace` calls and risking a mismatch.

**Rationale.**
- This is the *only* way the guest hits the JIT fast path over its hot memory. A per-write trap turns every framebuffer
  store into a virtual call into `AddressSpace.Write8` → `IPeripheral.Write` (an interface dispatch + a base-relative
  offset compute + the device's own decode). For a screen-clear loop or a full-frame repaint that is the difference
  between tier-1 array-store throughput and interpreter-class throughput over the hottest code in a game. The fastmem
  split exists precisely so "the guest can't hit a device" on the hot path (research §fastmem); a trapping framebuffer
  defeats it.
- **Snapshot-at-present is also more *correct* for the common case, not just faster.** Real display hardware does not
  observe the CPU's individual writes; it reads VRAM during its own scan/refresh. Snapshotting the backing array at
  `FrameReady` reproduces exactly that "the chip reads memory at its own time" semantics, and is coherent because
  `FrameReady` fires from a scheduled callback at a known cycle (§1.1). The guest's writes between frames are simply the
  VRAM contents the chip will read next refresh — which is the hardware truth.
- SP0 already chose this shape for display (`RenderInto` pulls the final RGBA at `FrameReady`); Principle 1 names it as a
  *general* device-memory principle (it applies equally to audio sample buffers and any "guest fills a buffer, device
  consumes it periodically" device) and ties it explicitly to the fastmem classification so the region actually lands on
  the fast path.

**Alternatives considered.**
- **(A) Trap every write through `IPeripheral.Write` (the naive model).** *Rejected* for the hot path: it is the MMIO tax
  the fastmem split was built to avoid. It remains correct and is the right choice for *register* regions (rare, side-
  effecting writes) — which is why the contract keeps both kinds of region rather than banning traps outright.
- **(B) A write-through fast region that also notifies the device per write (a "dirty span" callback).** *Rejected as the
  default* — it reintroduces a per-write cost (even a cheap dirty-range mark is a branch + a store the emitted IL would
  have to add to the fast path, and it couples the JIT's store emit to a device concept). Kept as a *flagged* future
  option for the rare device that genuinely needs per-write visibility (a memory-mapped device with side effects on
  *every* byte, e.g. a hardware blitter's source FIFO) — see Open Question 1.
- **(C) Let the device own the backing array but have the JIT call a device-supplied snapshot before each frame push
  regardless of region kind.** *Rejected* — that is just (A)/(B) relocated; the win is specifically that the guest writes
  go *nowhere near* the device on the hot path.

**Consequences.**
- *Good:* hot device memory runs at tier-1 speed; the snapshot model matches hardware refresh semantics; the device's
  side-effecting writes still trap correctly on its register region; no change to the JIT's emit (a fast-RAM region is
  just another `MapMemory` page to `Fastmem`).
- *Good:* the framework's existing fastmem invalidation and SMC machinery apply unchanged to a fast-RAM region (it *is*
  RAM as far as the JIT is concerned) — including the (rare but real) case of a guest executing code out of video RAM.
- *Bad / accepted:* a device's fast-RAM region is **not** read-trappable, so a device cannot react to an individual guest
  read/write of its bulk memory. This is the point (it is what makes it fast) but it constrains the device model: anything
  that *must* see every access (a device with a side effect on read, like some auto-increment data ports) must keep that
  access on its MMIO register region, not in the fast-RAM region. The contract must make the fast-vs-register boundary the
  device author's explicit, documented choice.
- *Bad / accepted:* the guest and the device share a mutable `byte[]`. A device that snapshots mid-write (if it ever reads
  off-schedule) could observe a torn buffer. Mitigated by the rule that devices read their fast-RAM region only at their
  scheduled present point (`FrameReady`), where the guest is between instructions at a known cycle. Documented as a device-
  author invariant, not enforced.

### Decision 2 — Memory-map-change signal: a bus remap fires a page-level JIT invalidation (a remap is "page-level SMC")

When a device changes what is mapped at an address — bank-switching cartridge mappers, the Apple II language card, the C64
PLA, PC EMS/UMB paging, the 68000 boot-time ROM-overlay-then-RAM trick — the framework treats the remap as a **page-level
invalidation event**, structurally identical to self-modifying code: the JIT must (a) refresh its `Fastmem` page
classification for the affected pages, and (b) evict every cached block that spans those pages (severing their inbound
chain links), so the next dispatch recompiles against the newly-mapped bytes. This is exposed as a single bus→JIT hook the
remapping device calls.

The mechanism reuses the existing SMC path. `BlockCache` already has page-precise eviction + chain-sever
(`InvalidateIfDirty` → `Evict` → `Chains.Sever`/`Forget`). The remap hook marks the affected pages dirty (or calls a new
`InvalidatePages(range)` that does the eviction directly) **and** flips the `Fastmem` page entries for those pages to
point at the newly-mapped backing array (or to null if the new mapping is MMIO). The dispatcher's existing
`InvalidateIfDirty` at the top of `Run` then evicts the stale blocks on the next round-trip.

The hook fires from the device's `Write` handler (a write to the mapper's bank-select register), which already runs on the
MMIO slow path (it is a control-register write — Decision 1). So the remap is observed at exactly the cycle the guest
writes the bank-select register, and the very next block dispatch sees the new map. The chain-break gates already in the
emitted chain edge (`EmitChainOrExit` checks `dirty.Any` at every static chain edge, `BlockCompiler.cs:567`) guarantee a
chained run cannot leap *past* the remap without a dispatcher round-trip if the remap marked anything dirty — so the
coarse backstop is already in place; the remap hook just needs to set the dirty marks / evict and update `Fastmem`.

**Rationale.**
- A bank switch breaks both JIT invariants at once (the static `Fastmem` map and the PC-keyed block cache, §1 fact 1). It
  *must* be signaled or the JIT silently executes stale code — the single worst class of correctness bug (it would pass
  every existing test, which never remaps, and fail only on real cartridge software). Reusing the SMC path is the minimal,
  proven mechanism: the eviction + chain-sever is exactly what SMC already does; only the `Fastmem` refresh is new.
- A remap is genuinely "page-level SMC": the semantic is identical ("the bytes at these PCs changed out from under the
  cache"). Treating them as one concept keeps one invalidation discipline rather than two parallel ones.
- Firing from the device's MMIO `Write` keeps the signal at the exact cycle and exact place the remap happens — no polling,
  no per-block map re-check. The cost is paid only when a bank actually switches (rare relative to instruction count), and
  the granularity is the remapped page range, not the whole cache (the M2-ii "don't thrash every chain on every store"
  lesson, `BlockCache.cs:14`, applies).

**Alternatives considered.**
- **(A) Re-check the page map on every block dispatch.** *Rejected* — a per-dispatch map-version compare adds cost to the
  hot dispatch path (`Run`'s loop) for an event that is rare; the dirty/evict-on-remap model pays only when a remap occurs.
- **(B) Make `Fastmem` dynamic — resolve the backing array per access at run time from a live page table.** *Rejected* —
  this is exactly what the fastmem split removed (the whole point is to bake the backing-array reference and skip the
  per-access table lookup). A dynamic map would slow *every* access to speed up the rare remap. The right shape is a
  static map that is *patched* on remap, not a per-access dynamic lookup.
- **(C) Flush the entire block cache + rebuild `Fastmem` from scratch on any remap.** *Acceptable as a first-cut
  implementation* (correct, simple, and a bank switch is rare enough that a full flush is not catastrophic the way per-RAM-
  store full-flush was in M2-i). *Not recommended as the final shape* because a mapper that switches banks frequently
  (some MMC-class mappers switch per-scanline) would thrash the whole cache + every chain on every switch — the exact
  pathology M2-ii fixed for SMC. Page-precise eviction (the recommended shape) avoids it. **Recommendation: implement the
  page-precise hook from the start, since the machinery already exists; do not ship the full-flush stopgap.**
- **(D) Forbid run-time remapping; require all banks pre-mapped into a flat 24-bit space.** *Rejected* — it does not model
  the hardware (a 6502 sees 64 KB; a mapper multiplexes more ROM through a 16 KB window — the windows genuinely overlap in
  the guest's address space) and it breaks down entirely for the 8086's segment-overlapping EMS.

**Consequences.**
- *Good:* bank-switching machines (the whole SP1 Atari cartridge story, the SP3 PC EMS story, the C64/Apple II future) are
  *possible at all* under the JIT; the remap is correct and page-precise; the mechanism reuses proven SMC code.
- *Good:* a remapped-to-RAM page becomes fastmem-fast immediately after the switch (the `Fastmem` patch points it at the
  new backing), so a banked-in RAM page runs at full speed.
- *Bad / accepted:* the remap hook is a new public seam on the bus/`IMachineContext` (§3.2) that a device can call, and a
  buggy device that remaps without firing it, or fires it for the wrong range, produces stale-execution bugs. Mitigated by
  routing *all* remapping through a single `AddressSpace.Remap`-style method that fires the hook internally, so a device
  cannot remap without signaling (the remap and the signal are one call). The device must not poke the page table behind
  that method.
- *Bad / accepted:* the JIT now holds a reference back to a remap notifier (or `AddressSpace` holds a JIT-invalidation
  callback). This is a new coupling between `Core` (the bus) and the JIT. Resolved by the bus exposing an *abstract*
  invalidation-listener seam (`Core` defines the interface; the JIT registers as a listener) so `Core` does not depend on
  `CpuEmulator.Jit` — the same dependency direction the existing `AddressSpace.TryGetDirectAccess` fastmem seam already
  uses (`Core` exposes; the JIT consumes).
- *Open:* the M6 interaction — whether a frequently-remapping mapper wants the JIT to *specialize* blocks per bank
  (compile a separate block keyed on `(PC, bank)`), which is a real M6 optimization and is flagged, not decided here
  (Open Question 3).

### Decision 3 — Tiered timing accuracy: a device declares coarse (block-boundary) vs fine (cycle-exact) servicing; the machine runs fast by default and drops to cycle-exact only where a device or its software requires it

Each device **declares its timing-accuracy tier**, mirroring the JIT's tier-0/tier-1 model:

- **Coarse (the default):** the device is correctly serviced at **block/slice boundaries**. Its scheduled events
  (`IScheduler.ScheduleEvery` for a periodic tick, `ScheduleAt` for a one-shot) fire at their exact cycle in *scheduler*
  time, and any IRQ they raise lands at the next instruction boundary — a sub-instruction-to-sub-slice jitter that is
  invisible to the overwhelming majority of software (UARTs, timers, disk controllers, keyboard, sound at buffer
  granularity). The machine keeps running tier-1 JIT blocks; nothing forces a slowdown. **This is what `Machine.Run`
  already does** (it chunks each slice to the next event and lets the JIT run the block).
- **Fine (cycle-exact, opt-in per device):** the device's behavior is defined by *where in a scanline/instruction* an
  event fires — raster splits, mid-frame palette/scroll changes, ANTIC display-list interrupts, CIA/VIC timing tricks,
  demos. A fine device's presence forces the machine to **bound block execution to that device's next event cycle**, so
  the event is serviced at exactly the right cycle even if it falls mid-block. Concretely: the slice/block budget is
  clamped to the next fine-timed event, so the JIT block exits (the existing `Budget` exit, `CompiledBlock.cs:11`) at or
  before that cycle and the dispatcher round-trips, fires the event, and resumes — at the cost of shorter blocks (fewer
  chained edges, more dispatcher round-trips) around that device's events. In the limit (a device needing per-instruction
  or per-cycle accuracy over a region) it forces interpreter (tier-0) fallback over that region, exactly as ADR 0008's
  timing axis forces the cycle-exact prefetch model only where the vectors demand it.

The machine runs fast by default and pays cycle-exact overhead only over the windows a fine device's events actually fall
in — the device-side mirror of the JIT's "tier-1 unless this op needs tier-0 fallback" lever.

This is declared, not inferred: a device states its tier (and, for a fine device, the granularity it needs) so the machine
sizes its run slices accordingly. The substrate already exists — `Machine.Run` already clamps a slice to the next
scheduled event (`Machine.cs:77`); the new part is (a) the device *declaring* it needs that clamping to be honored as a
hard block boundary (vs. coarse, where landing the IRQ a block late is fine), and (b) the machine distinguishing a coarse
periodic tick (don't shorten blocks for it) from a fine event (do).

**Rationale.**
- Cycle accuracy is expensive (shorter blocks, fewer chains, more dispatcher round-trips, ultimately interpreter
  fallback) and **most software does not need it.** Forcing every machine to cycle-exact servicing because *some* device on
  *some* machine needs a raster trick would tax the common case (a DOS text-mode program, a BASIC game) to serve the rare
  one. A per-device declaration confines the cost to where it is earned — the same principle ADR 0008 applied to the
  68000's prefetch-queue timing axis (fast functional core; cycle-exact only where it is asserted).
- The two tiers map cleanly onto mechanisms that **already exist**: coarse = the current `ScheduleEvery`/`Run`-slicing
  behavior (proven); fine = clamping the JIT budget to the next event (the `Budget` exit already does block-boundary exit;
  the only new behavior is treating a fine event as a hard clamp). No new timing engine is needed — the lever is which
  events the machine treats as block-bounding.
- It keeps the JIT and the device model on **one coherent accuracy story**: "run fast; be exact only where required." A
  developer reasoning about the framework sees the same tier lever at the CPU level (ADR 0008) and the device level (here).

**Alternatives considered.**
- **(A) Always cycle-exact (every device serviced per cycle).** *Rejected* — defeats the JIT entirely (no block could
  cross any device event); it is the WinUAE-style "always exact" model the framework deliberately did not choose
  (research §tiered strategy: "drop to the slower tier only where required").
- **(B) Always coarse (block-boundary only, no cycle-exact path).** *Rejected* — makes raster tricks / display-list
  interrupts / demos impossible; SP1's Atari 800 (ANTIC display-list interrupts are *normal*, not exotic) needs fine
  timing for ordinary software, not just demos.
- **(C) Infer the tier from the device type (display = fine, UART = coarse).** *Rejected* — too coarse and wrong at the
  edges: a simple linear framebuffer (SP0's `DemoFramebuffer`) is perfectly happy *coarse* (snapshot at a 60 Hz tick,
  Decision 1) and should not force cycle-exact servicing; only a *raster-interactive* display (ANTIC mid-frame) needs
  fine. The tier is a property of the device *implementation's* needs, not its category, so the device declares it.
- **(D) A global machine "accuracy mode" toggle.** *Rejected* — it is the wrong granularity (per-machine, not per-device)
  and forces the user to choose; the device knows its own needs, and a machine with one fine device and ten coarse ones
  should pay fine cost only around the one.

**Consequences.**
- *Good:* the common case stays tier-1 fast; cycle-exact cost is confined to the windows around fine-timed events; SP1's
  ANTIC and SP3's CGA-snow/raster cases are expressible without taxing every other machine.
- *Good:* it composes with Decision 1 — a coarse display (snapshot-at-`FrameReady`) and a fine display (mid-frame register
  changes that must land cycle-exact) are the same device type at two declared tiers, not two separate contracts.
- *Bad / accepted:* a fine device shortens blocks around its events (lost chaining + extra dispatcher round-trips), a real
  throughput cost — but bounded to that device's event windows, and the whole point is that it is opt-in. A machine that
  over-declares fine timing (every device fine) gets the always-cycle-exact cost; this is the device authors' to get right,
  and the default (coarse) is the safe one.
- *Bad / accepted:* "fine" servicing of an event that falls mid-block requires the JIT budget to be clamped to the event
  cycle, which `Machine.Run` does at the slice level but the JIT's chaining can currently run a chain *past* a slice's
  intended end as long as budget remains (chains only break on budget ≤ 0 / dirty / interrupt, `EmitChainOrExit`). For a
  fine event the machine must size the budget so the block/chain cannot overshoot the event cycle — which it already does
  by clamping the slice to the event (`Machine.cs:77`), *provided* the event was enqueued. The new requirement is only that
  fine events are guaranteed enqueued before the slice that should stop at them — a scheduling-discipline detail for the
  fine path, flagged (Open Question 2).

### Decision 4 — (Flagged, not mandated) spec-driven device register maps + device ground-truth tests, applied selectively

The framework's CPU side is built on a **spec → generator → interpreter+JIT** pipeline (the ISA spec drives code
generation) and a **ground-truth test discipline** (the TomHarte single-step vectors: seed state, step, diff state — the
un-fakeable correctness oracle, also used to prove JIT-vs-interpreter parity). This ADR **flags, but does not mandate**,
applying the analogous pattern to devices:

- **Spec-driven register maps:** for a *complex* device with a large, regular register set — a VDP-class chip (ANTIC/GTIA,
  the 9918, a VIC-II), a sound chip (POKEY/SID) with dozens of addressed registers, an 8259/8253/8237 with documented
  bit-field semantics — a small declarative register-map spec (offset → name → width → read/write/clear-on-read semantics →
  bit fields) could generate the decode/dispatch boilerplate, exactly as the ISA spec generates instruction dispatch.
- **Device ground-truth tests:** record real-hardware or reference-emulator **traces** (a sequence of register
  writes/reads + the resulting interrupt/timing/output) or **state-in-state-out** snapshots, and diff the device against
  them — the TomHarte analogue for a peripheral.

**Where it pays off (recommend adopting when the device arrives):** the VDP-class and PIC/PIT/DMA-class chips, where the
register set is large, regular, and the bit-field semantics are the bulk of the bug surface — the same conditions that made
the ISA generator worth it. A recorded trace is also the only honest way to validate a timing-sensitive chip (you cannot
eyeball a raster-IRQ schedule).

**Where it is YAGNI (do NOT build it preemptively):** the SP0 demo devices (`DemoFramebuffer`, `DemoKeyboard`, `DemoDisk`)
and simple devices like `SimpleUart`/`IntervalTimer` have a handful of registers; a generator + a trace harness for them is
pure overhead. SP0's hand-written contracts + the headless acceptance test (SP0 §6) are the right tool there.

**Rationale for flagging rather than deciding.** The payoff is entirely a function of a device's register-set complexity,
and no complex device exists yet (SP0 is demo-simple; the first VDP is SP1+). Committing to a device-spec format now would
be designing against an imaginary device — the spec format should be reverse-engineered from the *first real complex chip's*
register map, not guessed. The discipline is recorded here so that when ANTIC/GTIA (SP1) or the 8259/8253 (SP3) land, the
team reaches for spec+ground-truth *deliberately* (the CPU side's proven win) rather than re-litigating it — but the
trigger is "a complex device arrived," not "now."

**Consequences.**
- *Good:* the framework's hardest-won discipline (generate the regular boilerplate; prove against an un-fakeable oracle) is
  on the table for the device side, with a clear adopt/skip heuristic so it is applied where it pays.
- *Bad / accepted:* nothing is built now, so the first complex device pays the cost of *defining* the device-spec format
  and sourcing ground-truth traces. This is the correct cost (it is paid against a real register map), and it is flagged so
  it is budgeted, not a surprise.

---

## 3. Concrete contract additions (for `Core`, the bus, and the device interfaces)

These are the **shapes** the four decisions imply — signatures and seams, not implementations. They are additive to the
existing `Core` contracts (no behavior change to `IPeripheral`/`IScheduler`/`IInterruptLine`/`Machine` for existing
devices; a device opts into each capability).

### 3.1 Decision 1 — declaring fast-RAM vs MMIO-register regions

A new **optional capability interface** in `CpuEmulator.Core`, implemented by a device *in addition to* `IPeripheral`,
that declares its fast-RAM region(s); the `MachineBuilder`/`Machine` consults it at build time to `MapMemory` the fast
region(s) (sharing the device's backing array) and `MapPeripheral` the register region(s):

```csharp
namespace CpuEmulator.Core;

/// <summary>A device that exposes bulk memory the guest writes on the JIT fast path (a framebuffer,
/// a text/tile buffer, an audio sample buffer). The Machine maps each region with MapMemory (so the
/// JIT's Fastmem classifies it non-null → direct array access), sharing the device's backing array;
/// the device reads/snapshots that array at its own scheduled present point (e.g. FrameReady), NOT
/// per guest write. Side-effecting control registers stay on the device's IPeripheral MMIO region.</summary>
public interface IFastMemoryProvider
{
    /// <summary>The fast-RAM region(s) this device exposes: the guest-visible base address, the
    /// shared backing array, and whether the guest may write it (a ROM-like font region is read-only).
    /// Page-aligned + page-multiple length (the AddressSpace mapping rule). The device retains the
    /// array reference and reads it at snapshot time.</summary>
    IReadOnlyList<FastMemoryRegion> FastRegions { get; }
}

public readonly record struct FastMemoryRegion(uint Base, byte[] Backing, bool GuestWritable);
```

The device's *register* region remains its ordinary `IPeripheral` mapping (the small `MapPeripheral` range the
`MachineBuilder` already wires). SP0's `IDisplayDevice` (and a future `IAudioDevice`) compose `IFastMemoryProvider` for
their buffer + `IPeripheral` for their control registers. **No JIT change is required** — a fast-RAM region is just
`MapMemory` pages, which `Fastmem` already classifies and `BlockCompiler` already emits direct access for.

SP0 impact: `DemoFramebuffer` becomes "`IPeripheral` (mode/palette/control registers) + `IFastMemoryProvider` (the 256×192
VRAM byte array) + `IDisplayDevice` (`RenderInto`/`FrameReady`)". Its VRAM writes hit the JIT fast path; the surface still
pulls RGBA at `FrameReady` (SP0 §4.1 unchanged).

### 3.2 Decision 2 — the bus→JIT remap/invalidation seam

A remap goes through a single `AddressSpace` method that performs the page-table change **and** fires the JIT-invalidation
signal atomically, so a device cannot remap without signaling. `Core` defines an abstract listener; the JIT registers as
one (preserving the `Core` → JIT dependency direction the existing `TryGetDirectAccess` fastmem seam already uses):

```csharp
namespace CpuEmulator.Core;

/// <summary>A consumer (the JIT block cache + its Fastmem) that must be told when the mapping over a
/// page range changes at run time (a bank switch / language-card / EMS remap), so it can refresh its
/// page classification and evict cached blocks over those pages. Core defines this; the JIT implements
/// it — Core does not depend on CpuEmulator.Jit.</summary>
public interface IMapInvalidationListener
{
    /// <summary>The mapping over [firstPage, firstPage+pageCount) changed. The listener refreshes its
    /// fastmem classification for those pages and evicts every cached block spanning them (severing
    /// inbound chain links) — page-level SMC.</summary>
    void OnRemap(int firstPage, int pageCount);
}
```

On `AddressSpace` (additive):

```csharp
// Register the JIT (or any consumer) to be notified of run-time remaps. Called once at JittedCpu
// construction (the JIT registers; the interpreter registers nothing — it re-reads the live page table).
internal void AddMapInvalidationListener(IMapInvalidationListener listener);

// Remap a previously-mapped page range to a new backing (RAM/ROM) — the bank-switch primitive. Updates
// the page table AND fires OnRemap to every listener. The mapper device calls this from its bank-select
// register Write (the MMIO slow path), so the remap is observed at the exact guest write cycle.
public void Remap(uint start, byte[] backing, bool writable);
public void RemapPeripheral(uint start, uint length, IPeripheral peripheral);  // remap a range to MMIO
```

The JIT's listener implementation (in `CpuEmulator.Jit`) refreshes `Fastmem.PageBacking/PageOffset/PageWritable` for the
affected pages (re-running the `TryGetDirectAccess` classification for that range) and calls a new
`BlockCache.InvalidatePages(firstPage, pageCount)` that runs the existing per-page `Evict` loop (the same body
`InvalidateIfDirty` uses, factored out). Because `Fastmem`'s arrays are baked into emitted blocks *by reference*
(`Fastmem.cs:8`), patching the array *contents* (not the array object) is visible to already-emitted blocks immediately —
the next access through any live block reads the patched `PageBacking[page]`. The eviction then drops the blocks whose
*decoded bytes* came from the remapped region.

`IMachineContext` may grow a convenience accessor so a mapper device gets at `Remap` without re-fetching the space, but the
`Space(kind)` it already exposes (`IMachineContext.cs:7`) returning an `IAddressSpace` is sufficient if `Remap` is promoted
onto `IAddressSpace`. **Decision needed:** whether `Remap` lives on `IAddressSpace` (every consumer sees it) or on the
concrete `AddressSpace` reached via a mapper-specific seam (Open Question 4).

### 3.3 Decision 3 — the device timing-tier declaration

A small additive declaration on the device (or a property on a capability interface), read by the `Machine`/`MachineHost`
when sizing run slices:

```csharp
namespace CpuEmulator.Core;

public enum TimingTier
{
    /// <summary>Block/slice-boundary servicing is correct (the default). The device's scheduled events
    /// fire at their exact scheduler cycle; an IRQ they raise lands at the next instruction boundary —
    /// invisible jitter for almost all software. The JIT keeps running chained blocks.</summary>
    Coarse,

    /// <summary>The device's events define behavior by their exact cycle (raster splits, display-list
    /// interrupts, mid-frame register changes). The machine clamps block/chain execution so these events
    /// are serviced at their exact cycle even mid-block — shorter blocks + more round-trips around the
    /// event, ultimately interpreter (tier-0) fallback if per-instruction/per-cycle accuracy is needed.</summary>
    Fine,
}

/// <summary>A device declaring it needs cycle-exact servicing of its scheduled events. Absence ==
/// Coarse (the default). The Machine treats a Fine device's next scheduled event as a hard block
/// boundary (clamps the run budget to it); a Coarse device's periodic tick does not shorten blocks.</summary>
public interface ITimingSensitive
{
    TimingTier TimingTier { get; }
}
```

`Machine.Run`'s slice-sizing (`Machine.cs:77`, which already clamps a slice to the next event cycle) is extended to
distinguish a *fine* event (a hard clamp — the block must not overshoot it) from a *coarse* periodic tick (fire at the
slice boundary; do not shorten the block for it). The substrate is present; the new logic is the coarse/fine distinction
when choosing `sliceEnd`. A device that needs full per-cycle accuracy over a region declares `Fine` and (M6) the machine
forces tier-0 over that region — the JIT already has the interpreter fallback valve (`JittedCpu` wraps the interpreter as
the oracle/fallback).

### 3.4 Summary of the additive surface

| New `Core` surface | Decision | Consumed by |
|---|---|---|
| `IFastMemoryProvider` + `FastMemoryRegion` | 1 | `MachineBuilder`/`Machine` (maps regions); `Fastmem` (classifies); the device (snapshots) |
| `IMapInvalidationListener` | 2 | JIT registers as listener; `AddressSpace.Remap` fires it |
| `AddressSpace.Remap`/`RemapPeripheral` + `AddMapInvalidationListener` | 2 | mapper devices (call `Remap`); `JittedCpu` (registers, refreshes `Fastmem`, evicts) |
| `BlockCache.InvalidatePages(firstPage, pageCount)` (JIT-internal; factored from `InvalidateIfDirty`) | 2 | the JIT's `IMapInvalidationListener` impl |
| `TimingTier` + `ITimingSensitive` | 3 | `Machine.Run` slice-sizing; `MachineHost` |

None of these alter the existing `IPeripheral`/`IScheduler`/`IInterruptLine` contracts; a device that implements none of
them behaves exactly as today (coarse, no fast region, no remap). They are pure opt-in capability interfaces — the same
"a chip implements the relevant capability in addition to `IPeripheral`" pattern SP0 §4 already established for
`IDisplayDevice`/`IKeyboardSink`/`IBlockDevice`.

---

## 4. Consequences (cross-cutting)

**Good.**
- SP0's three device contracts land already JIT-aware: the framebuffer is a fast-RAM region (Decision 1, hot path fast),
  and the contract pattern (capability interface + `IPeripheral`) is uniform.
- SP1's cartridge mappers and SP3's EMS/UMB paging are *expressible at all* under the JIT (Decision 2) via proven SMC
  machinery, not a new invalidation engine.
- The framework keeps one coherent accuracy story across CPU and devices ("fast by default, exact where required" —
  Decision 3 is the device mirror of ADR 0008's CPU timing axis).
- The device-spec/ground-truth discipline is on record with a clear adopt/skip heuristic (Decision 4), so the first
  complex chip reaches for the CPU side's proven win deliberately.

**Bad / accepted costs.**
- New `Core` ↔ JIT coupling for the remap seam (Decision 2) — mitigated by `Core` defining the listener interface and the
  JIT consuming it (the existing fastmem-seam dependency direction).
- A fast-RAM region is not access-trappable (Decision 1) — a deliberate constraint; side-effect-on-access memory must stay
  on the MMIO register region.
- A fine-timed device costs throughput around its events (Decision 3) — bounded and opt-in, but a real cost a machine
  author can mis-budget by over-declaring `Fine`.
- Nothing is built for Decision 4 now; the first complex device pays the device-spec-format definition cost.

**Reversibility.** All four are additive opt-in capabilities; a machine using none behaves as today. Decision 2's remap
seam is the one with teeth (it touches the JIT's invalidation path and `Fastmem`), but it reuses the existing per-page
eviction and is gated behind a method no current device calls. Decision 4 is pure documentation until a complex device
triggers it.

---

## 5. Open questions

1. **Per-write-visible fast memory (Decision 1, alternative B).** Is there a real SP1/SP3 device that needs the guest's
   *individual* writes to its bulk memory observed (a hardware blitter source FIFO, a memory-mapped device with a side
   effect on every byte) and yet cannot afford the MMIO trap? If so, a "fast region + a cheap dirty-span mark the JIT emits
   on store" hybrid is the fallback — but it couples the JIT's store emit to a device concept and should not be built until
   a device demands it. Resolve when the first such device appears (likely SP3 DMA, possibly never).
2. **Fine-event scheduling discipline (Decision 3).** A `Fine` event must be enqueued *before* the slice that should stop at
   it, or the JIT chain can overshoot its cycle (chains break only on budget/dirty/interrupt, not on "an event was just
   scheduled mid-block"). Does the framework need a mid-block fine-event injection path (force a chain break when a fine
   event is scheduled during a running block), or is "fine events are always scheduled at known future cycles before the
   slice" a sufficient discipline? Resolve against the first real fine device (ANTIC display-list interrupts, SP1).
3. **M6: per-bank block specialization (Decision 2 × M6).** A mapper that switches banks frequently evicts + recompiles the
   windowed region's blocks on every switch. An M6 optimization is to key blocks on `(PC, bankState)` so a re-entered bank
   reuses its already-compiled blocks instead of recompiling. This is a real JIT-optimization item (it changes the block
   cache key) and is flagged for M6, not decided here. The simple page-precise evict-on-remap (Decision 2) is the M-now
   shape; per-bank specialization is the M6 refinement if profiling shows remap-thrash.
   **Update (2026-06-19):** designed in **ADR 0013** (`0013-per-bank-block-specialization.md`, Status: Proposed) —
   keys blocks on `(PC, BankConfigId)`, re-patches the shared `Fastmem` per active bank on remap WITHOUT eviction
   (eviction stays the SMC-write path), and rides the proven chain-break gate. Pending owner approval.
4. **`Remap` placement (Decision 2, §3.2).** Does `Remap`/`RemapPeripheral` live on `IAddressSpace` (every consumer of the
   bus sees the remap primitive — broad surface) or on the concrete `AddressSpace` reached via a narrower mapper-specific
   seam (tighter, but a mapper device must get the concrete bus)? Leaning toward `IAddressSpace` for uniformity with the
   existing `Read8`/`Write8`/`MapMemory` surface, but it widens the interface every device sees. Owner's call.
5. **`Fastmem` refresh granularity on remap (Decision 2).** Patching `PageBacking`/`PageOffset`/`PageWritable` array
   *contents* for the remapped range is visible to live blocks immediately (the arrays are baked by reference). Confirm
   there is no emitted-IL assumption that the *contents* of those arrays are constant for a block's lifetime (a quick audit
   of `BlockCompiler` says no — every access re-indexes `PageBacking[page]` at run time; nothing caches a backing pointer
   across instructions within a block). Verify when Decision 2 is implemented.
6. **Coarse-display vs fine-display as one device or two (Decision 1 × 3).** SP0's `DemoFramebuffer` is coarse
   (snapshot-at-`FrameReady`); SP1's ANTIC is fine (mid-frame register changes must land cycle-exact). Confirm that one
   `IDisplayDevice` contract spans both tiers (the device declares `ITimingSensitive`/`TimingTier`) rather than needing a
   separate "raster-interactive display" contract. Expected yes; confirm against the ANTIC register map at SP1 plan time.

---

*End of ADR 0009. Decision 1 (fastmem-aware device memory: hot memory is a fast-RAM region snapshotted at present, not a
per-write MMIO trap) generalizes SP0's `RenderInto`-on-`FrameReady` and ties it to the JIT fastmem classification.
Decision 2 (a bus remap fires page-level SMC invalidation) makes bank-switching mappers expressible under the JIT via
proven eviction machinery. Decision 3 (declared coarse/fine timing tier) mirrors ADR 0008's CPU timing axis at the device
level. Decision 4 (spec-driven register maps + ground-truth traces) is flagged for the first complex device, not mandated.
Designer: the only UX-adjacent implication is that a fine-timed display is what makes mid-frame visual effects faithful —
no surface change. Planner can expand §3's contract shapes into SP0/SP1 tasks once the owner signs off.*
