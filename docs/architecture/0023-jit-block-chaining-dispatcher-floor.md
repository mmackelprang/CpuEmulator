# ADR 0023 — JIT block chaining the dynamic-target floor (cutting the dispatcher round-trip on real boots)

> **Status:** **MEASURED → REFUTED → PARKED (owner, 2026-06-24).** D0 (the `EmitDynamicChainOrExit` helper)
> shipped (PR #164, parity-clean). D2 (Z80 RET/RET-cc chaining) was implemented + rigorously A/B-measured and
> **refuted the core thesis below**: chaining works exactly as designed (Spectrum `chain:disp` 0.284 → 0.506,
> ~125k fewer dispatcher round-trips, byte-identical parity) **but throughput stayed flat** (JIT ~23.7× vs
> interpreter ~30.3×) — so the dispatcher round-trip is **not** the dominant cost on this host. The real
> Spectrum bottleneck is the **interpreter-fallback tail** (un-emitted ops the JIT must `inner.Step` — §6).
> Per the ADR-0012 discipline the arc stopped at D2 (left unmerged on `feat/jit-dynamic-chain-d2-z80`) and was
> **parked by the owner**: the two-tier design is validated (interpreter wins fallback-heavy boots, JIT wins hot
> kernels), the machines already run 23–64× real-time, and the profiler + chain/dispatch counters now stand as a
> regression instrument. **The real lever — emit coverage for the Spectrum fallback tail — is the recorded
> follow-on candidate** if JIT-on-boots is ever pursued; do NOT re-chase the dispatcher floor without new
> evidence. Original status: PROPOSED 2026-06-23; the §1-§7 design stands as the (throughput-refuted) record.
> This is the **first measured optimization** the ADR-0022 feedback loop surfaced and the owner picked: the
> real-boot JIT is *slower than the interpreter* because roughly half (DOS 3.3) to four-fifths (Spectrum) of
> hot block transitions **round-trip through the dispatcher instead of chaining**. This ADR root-causes the
> round-trips to a single, measured edge category — **dynamic-target control flow (returns + indirect
> jumps + computed calls)** — and designs a **dynamic chain edge** that lets those edges chain too, reusing
> the existing PC-keyed `ResolveChain`/`ChainTable`/eviction machinery verbatim (so it is parity- and
> SMC-safe by construction). The win is proven by a before/after `chain:disp` + real-time-ratio profile
> diff, per the ADR-0022 honesty gate.
> **Date:** 2026-06-23
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Supersedes / relates to:**
> - **ADR 0011** (`0011-jit-hot-op-emission-optimization.md`) — the M6 emit + **chaining** design. §1.2
>   established that "block chaining is FULLY IMPLEMENTED and stack-safe" via `JittedCpu.RunChain` +
>   `EmitChainOrExit` + `ChainTable`, and that *static* targets chain. This ADR closes the one gap that
>   design left open by intent: **dynamic targets exit to the dispatcher** (the RTS/RET/JMP-(ind)/CALL-r/m
>   arms emit `EmitNormalExit`, not `EmitChainOrExit`). The emit-vs-fallback boundary, the oracle-as-safety-net
>   invariant, and the per-CPU cycle/flag models are inherited unchanged — this ADR touches the *exit edge*,
>   not any op's body.
> - **ADR 0012** (`0012-jit-dirty-page-list-invalidation.md`, **REJECTED**) — the measurement-discipline
>   lesson and the *prior naming of this exact floor*. ADR 0012's premise (the 256-bool invalidation scan was
>   the ~140× floor) was refuted by measurement; the refutation named the real floor as "the **dispatcher
>   round-trip + chaining/`ResolveChain` per-edge cost + `Evict`'s dictionary churn**." This ADR attacks the
>   *first* of those three (the round-trip) on the edges that are genuinely chainable, and — critically —
>   carries ADR 0012's discipline: **the success metric is a real profile-diff speedup, not a believed one**,
>   and §6 states plainly where the round-trips are *not* recoverable.
> - **ADR 0022** (`0022-performance-feedback-loop.md`, PROPOSED) — the loop that produced this candidate. The
>   `ChainEdgesTaken` / `DispatcherEntries` free counters this ADR moves are the ones ADR 0022 §3.1 added "so
>   the next person who wonders why SMC-heavy code is slow reads a number instead of guessing." This ADR is
>   the first turn of that loop's ACT step on the JIT axis: profile (PR #162's committed `profile.json` set) →
>   rank (this floor is the top JIT item) → act (this design) → re-measure (the same profiler).
> - **ADR 0009** (`0009-device-jit-contract-and-peripheral-design.md`) Decision 2 — the bus→JIT page-level
>   invalidation hook + the fastmem split. The dynamic chain edge MUST keep emitting through the fastmem split
>   and MUST NOT weaken the invalidation hook; §5 shows it does neither (it reuses the same `dirty.Any` gate
>   and the same PC-keyed eviction the static edge already uses).

---

## 1. Context — the measured problem (PR #162's first real-boot profile)

### 1.1 The headline: on real boots the JIT LOSES to the interpreter

The ADR-0022 standing profiler (`bench/profiler/`, shipped in PR #162 / commit `e29a196`) ran the real
machine boots on both tiers for the first time. The committed `profile.json` set
(`bench/results/profiles/<system>/<workload>.json`) shows the JIT is *slower than the interpreter* on the
two flat-CPU real boots — the opposite of the synthetic-kernel result:

| boot | interp real-time ratio | JIT real-time ratio | JIT vs interp | `chain:disp` (ChainEdgesTaken : DispatcherEntries) |
|---|---:|---:|---:|---:|
| **apple2-dos33 / boot-to-basic** | 50.4× | 19.7× | **0.39× (JIT 2.6× SLOWER)** | 339,658 : 329,217 ≈ **1.0** |
| **spectrum-48k / boot-to-copyright** | 22.9× | 19.3× | **0.84× (JIT 1.2× SLOWER)** | 241,261 : 850,022 ≈ **0.28** |

> The task framing cited slightly different absolute ratios (DOS 23.2× vs 60.8×; Spectrum 22.6× vs 29.5×) from
> an earlier tree; the *shape* is identical and is what matters: **JIT < interpreter on real boots, and a low
> `chain:disp`.** Compare the synthetic kernels, where chaining carries the hot path almost completely:
> bench-z80 W2 `chain:disp` = **114,692**; bench-68000 W2 = **129,129**; bench-8086 W2 = **1,000,973**. On the
> kernels the JIT is 1.2–11× *faster* than the interpreter; on the real boots it loses. **The entire delta is
> the round-trip rate.**

### 1.2 The arithmetic isolates the cost to the round-trip, not SMC or recompiles

From the committed counters (zero believed numbers — every figure is read off the profile):

- **DOS 3.3:** `dispatcherEntries / (dispatcherEntries + chainEdgesTaken)` = **49.2%** of block entries are
  dispatcher round-trips. `compileCount` = **107**, `totalRecompiles` = **0**, `totalEvictions` = **0**,
  `smcHotPcCount` = **0**. So the 329,217 round-trips are **not** SMC, **not** recompilation, **not** cache
  eviction — they are re-dispatches of the **107 already-compiled, never-evicted blocks** (each re-entered
  ~3,077 times). The cost is purely the per-dispatch `Run`-loop preamble paid on a chainable edge that didn't
  chain.
- **Spectrum:** **77.9%** of block entries round-trip. `compileCount` = **265**, recompiles/evictions = **0**.
  Same story, worse ratio.

This is exactly ADR 0012's *real* floor (the one it found after its own premise was refuted): "the dispatcher
round-trip." ADR 0012 shelved it ("run SMC-heavy/integration code on the interpreter tier"); ADR 0022 re-opened
it by making it a standing measurement; this ADR acts on it **only where the measured round-trips are on
chainable edges** (§2 proves they are).

### 1.3 What a round-trip costs that a chain edge does not

The dispatcher preamble (`JittedCpu.Run`, `src/CpuEmulator.Jit/JittedCpu.cs:185-237`) runs **per round-trip**:
`_inner.InterruptPending` (callvirt) → `_inner.Halted` (callvirt) → `_cache.InvalidateIfDirty()` →
`_target.ProjectBlockKey(_inner)` (a CPU-state read) → `_cache.ShouldInterpret(key)` (dict lookup) →
`_dispatcherEntries++` → `_cache.GetOrCompile(key, _compiler)` (dict `TryGetValue`) → re-enter `RunChain`.

A chain edge (`EmitChainOrExit`, `BlockCompiler.cs:1502`; `ChainEdge`, `JittedCpu.cs:274`) runs **per edge**:
three inline IL gates (budget ≤ 0, `dirty.Any`, `InterruptPending`) → `_cache.ResolveChain(targetPc, pred,
compiler)` (one dict `TryGetValue` + a `Chains.Link`) → continue in the same `RunChain` frame. The chain edge
**skips** the `Halted` check, the `ProjectBlockKey` state read, the `ShouldInterpret` lookup, the loop re-top,
and (because it stays in `RunChain`) avoids the dispatcher's outer `while` bookkeeping. That difference,
multiplied by ~330K–850K times per boot window, is the floor.

---

## 2. Root cause — the round-trips are dynamic-target control flow (the measured edge-category breakdown)

A block ends (and either chains or round-trips) at one of three things (`BlockCompiler.cs:482/523`,
`ClassifyForJit` at `CpuEmitter.cs:5292`):

| Block-ending category | Chains today? | Why |
|---|---|---|
| **Conditional branch** (BNE/BPL/JR-cc/Bcc/Jcc, both arms) | **YES** — both edges | both the taken target and the fall-through are compile-time constants; `EmitBranch`/`EmitZ80JpCc`/etc. emit `EmitChainOrExit` on *both* arms (`Flow.cs:449/454`, `Z80.cs:1031/1033`) |
| **Static jump / call / RST** (JMP-abs, JSR/CALL-rel, RST n, LOOP) | **YES** | the entry/target is an immediate in the code stream — `EmitChainOrExit` with a baked constant (`Flow.cs:482/578`, `Z80.cs:898/917/939/952`, `M8086.cs:1152-1216`) |
| **`BlockLengthCap` fall-through** (64-instr straight run) | **YES** | the continuation PC is a constant (`BlockCompiler.cs:534`) |
| **Dynamic-target control flow** — **RTS / RET / RET-cc-taken / JMP-(indirect) / JMP r/m / CALL r/m** | **NO — round-trips** | the successor PC is computed at run time (popped from the stack, or read from memory). The arms emit **`EmitNormalExit`** (a hard exit to the dispatcher), explicitly: 6502 `EmitRts` (`Flow.cs:619`), 6502 `EmitJump` Indirect (`Flow.cs:530`), Z80 `EmitZ80Ret` (`Z80.cs:962`), Z80 `EmitZ80RetCc` taken (`Z80.cs:1131`), 8086 `RET`/`RET imm16` (`M8086.cs:1204`), 8086 `FF /2 CALL r/m`, `FF /4 JMP r/m` (`M8086.cs:1248/1255`) |
| **Fallback op** (any not-yet-emitted opcode) | **NO — round-trips** | after `inner.Step` the PC is dynamic; `EmitFallbackStep` → `EmitNormalExit` (`BlockCompiler.cs:1585`) |

**The dominant round-trip category, by the measured hot-op histograms:**

- **Spectrum (Z80):** `RET` 9.0% + `CALL` 9.0% of all executed instructions, and the Spectrum 48K ROM is
  deeply subroutine-structured (the print/RST-`$10` path, the calculator, the keyboard scan — all `CALL`/`RET`
  pairs). **Every `CALL` chains in (static entry), but every matching `RET` round-trips out.** The 77.9%
  round-trip rate is dominated by returns: the chained `CALL` and the round-tripping `RET` are roughly equal
  in count, so returns alone account for ~half of all block entries being a round-trip. The remaining
  round-trips are the small fallback tail (the ROM's `RLD`/`RRD`/`LDIR`-class and the `IM`/`EI`/`DI` it uses
  during init) plus the occasional `JP (HL)`/`RET cc`.
- **DOS 3.3 (6502):** the hot ops are BNE 25.5% / INC 24.1% / BPL 24.0% / BIT 24.0% — **all of which emit and
  chain.** The 49.2% round-trip rate therefore is *not* in the hot quartet; it is the **`RTS`/`JMP (indirect)`
  in the loop bodies those branches sit inside.** DOS RWTS and the Applesoft inner loops dispatch through
  `JMP (vector)` and call subroutines that `RTS` — DOS 3.3's `$03D0` "warm-start" vector, the RWTS read-loop's
  indirect dispatch, and the monitor's `JSR`/`RTS` per-character output. With only 107 blocks each re-entered
  ~3,077×, the loops are tight: a handful of subroutines, called and returned-from hundreds of thousands of
  times — and the *return* is the un-chained edge on every iteration. (The exact RTS-vs-JMP-(ind) split is the
  one number §7 PR-D1 captures before the fix, to confirm the ROI ordering; the *category* — dynamic-target
  control flow — is already proven by the histograms + the zero-SMC counters.)

**Conclusion (measured, not assumed):** the round-trips are **overwhelmingly dynamic-target control-flow
edges that are chainable in principle** — the successor PC *is* a real PC the cache can key on; it just isn't a
compile-time constant, so the current arms exit instead of chaining. This is the high-ROI, recoverable
category. It is **not** dominated by genuinely-unchainable edges (a fallback op's post-`Step` PC, or a
self-modified block) — those exist (the fallback tail) but are the minority and stay as-is.

---

## 3. Decision 1 — the **dynamic chain edge**: chain returns and indirect jumps through a runtime-computed target

The chaining machinery is **already target-value-agnostic.** The `ChainDispatch` callback
(`CompiledBlock.cs:63`) takes the target PC as a *value*: `void ChainDispatch(uint targetPc, ref long budget,
out BlockExit exit)`. `EmitChainOrExit` (`BlockCompiler.cs:1523-1530`) merely happens to push a *compile-time
constant* (`Ldc_I4 staticTargetKey`) as that value. `ChainEdge` → `ResolveChain` (`JittedCpu.cs:285`,
`BlockCache.cs:112`) then resolves the successor **by PC through the live cache** (`GetOrCompile(targetPc)`),
compiling on first reach and linking the predecessor in the `ChainTable`. **Nothing in that path requires the
target to be a constant** — it is keyed on the runtime `uint` value.

**The decision: add `EmitDynamicChainOrExit(ctx)` — identical to `EmitChainOrExit` except it reads the target
PC from `cpu.PC` (the value the dynamic arm just computed and stored) instead of baking a constant.** The
dynamic-target arms call it in place of `EmitNormalExit`:

```
  EmitDynamicChainOrExit(ctx):
    # the arm has ALREADY stored the runtime target into cpu.PC (RTS popped it; JMP-(ind) read it)
    if (budget <= 0)        goto toDispatcher     # gate (2) — identical to EmitChainOrExit
    if (dirty.Any)          goto toDispatcher     # gate (3) — the SMC backstop, UNCHANGED
    if (cpu.InterruptPending) goto toDispatcher   # gate (4) — irq sampled at the edge
    chain.Invoke( (uint)cpu.PC, ref budget, out exit )   # ← the ONLY difference: load cpu.PC, not a constant
    ret
  toDispatcher:
    EmitNormalExit(ctx)                            # exactly today's behavior when a gate blocks
```

The single IL difference from `EmitChainOrExit` is at the chain-call site: instead of
`il.Emit(Ldc_I4, staticTargetKey)`, emit `il.Emit(Ldarg_0); il.Emit(Ldfld, _fpc); il.Emit(Conv_U4)` (read the
PC field the arm already set, widen to the `uint` chain key). For the flat CPUs (6502/Z80/68000) the block key
*is* `(uint)PC` (identity — see `JittedCpu.cs:212` `ProjectBlockKey`), so `(uint)cpu.PC` is exactly the key the
dispatcher would have computed. The arms change one line each: their terminal `EmitNormalExit(ctx)` becomes
`EmitDynamicChainOrExit(ctx)`.

**Which arms convert (the measured-ROI order):**

1. **Returns — the top ROI** (Spectrum's dominant edge, DOS's co-dominant): 6502 `EmitRts`, Z80 `EmitZ80Ret` +
   `EmitZ80RetCc` (taken arm), 8086 `RET`/`RET imm16`. These are the highest-frequency dynamic edges and the
   ones the histograms prove dominate.
2. **Indirect / computed jumps:** 6502 `JMP (indirect)`, 8086 `FF /4 JMP r/m`. (DOS's `JMP (vector)` dispatch.)
3. **Computed calls:** 8086 `FF /2 CALL r/m`. (Lower frequency; converted for completeness once 1+2 prove out.)

The 68000's `RTS`/`JMP (An)`/`JSR (An)` are the same shape and convert with the same one-line change, but the
68000 has no flat-CPU *real boot* in the profiler today (it is kernel-only), so it is lower-priority and gated
on a profile that shows it (per the ADR-0022 ROI rule). The 8086 is segmented — see §3.1.

### 3.1 The 8086 segmented wrinkle (already solved by FF-1)

The 8086's block key is the linear `(CS<<4)+IP` projection (ADR 0019, `ProjectBlockKey` folds it). A near `RET`
pops only IP (CS unchanged), so the dynamic chain key is `(_m8086CodePhysBase + (uint)IP) & 0xFFFFF` — exactly
the `M8086NearChainKey(ip)` helper the *static* near arms already use (`M8086.cs:1152`). So the 8086 dynamic
return edge reuses `M8086NearChainKey` over the runtime IP. A **far** `RET`/`RETF` changes CS:IP and is
**out of scope** for this ADR (it stays `EmitNormalExit`): far returns are rarer, and the `PagesSpanned`
non-zero-CS SMC caveat (ROADMAP, the FF-1 follow-on) is unresolved for far flow — chaining a far edge would
widen the blast radius into that open item. Near returns (the hot 8086 case) are in scope; far returns wait.

### 3.2 Why this is the right mechanism (alternatives considered)

- **(A) An inline cache / return-stack buffer (RSB) — predict the return target, verify, fall back on miss.**
  Rejected for the first cut: it is the classic dynarec technique and *would* shave the `ResolveChain` dict
  lookup itself, but it is strictly more machinery (a per-edge cached `(predictedPc → block)` slot + a runtime
  compare + a misprediction path) for a *second-order* win. The measured floor is the **dispatcher round-trip**
  (the `Run`-loop preamble), which the dynamic chain edge eliminates entirely; the `ResolveChain` dict lookup
  is the *same* cost the static chain edge already pays and is not the floor (the kernels chain through it at
  `chain:disp` > 100,000 and are fast). Start with the dynamic chain edge (removes the round-trip); measure;
  add an inline cache **only if** the re-profile shows `ResolveChain` itself is now the hot residual (§7 OQ1).
  This is the ADR-0012 discipline: don't build the bigger machine until the measurement says the smaller one
  left something on the table.
- **(B) Speculatively chain returns by baking the static return address at the matching CALL.** Rejected — a
  return address is not statically known at the callee's `RET` (the callee can be reached from many call sites;
  that is the whole point of a subroutine). Baking it would be wrong for any subroutine called from ≥2 sites
  (the common case). The dynamic edge resolves the *actual* popped PC, so it is correct for all call sites.
- **(C) Make the dispatcher cheaper instead of chaining more (trim the `Run`-loop preamble).** Partially
  complementary, not a substitute: even a trimmed preamble pays the loop re-top + the `ProjectBlockKey` state
  read + the `ShouldInterpret` lookup that the chain edge skips. The chain edge is the bigger lever; a preamble
  trim is a possible follow-on (§7 OQ2). We do the lever first.
- **(D) Do nothing — keep the ADR-0012 "run integration code on the interpreter" posture.** This is the honest
  default and the §8 fallback. But the measurement now shows the round-trips are on *chainable* edges (§2), not
  genuinely-unchainable ones — so there is a real, recoverable win the shelving assumed away. The loop's job
  (ADR 0022) is to act on exactly this kind of measured, recoverable gap. Proceeding is justified *because the
  edge category is chainable*; if §2 had shown the round-trips were dominated by fallback-op edges, (D) would
  stand. It doesn't.

---

## 4. Decision 2 — parity safety: the dynamic chain edge is byte-identical to the round-trip it replaces

The chain edge and the dispatcher round-trip **resolve the same successor PC and run the same block** — the
only difference is *whether control returns to the outer `Run` loop in between*. This is provably
result-preserving:

1. **Same target PC.** The dynamic arm sets `cpu.PC` to the computed successor (it does this *today*, before
   `EmitNormalExit` — RTS pops into PC, JMP-(ind) reads into PC). The round-trip path then does
   `key = ProjectBlockKey(_inner)` = `(uint)cpu.PC` (flat CPUs) and dispatches that block. The dynamic chain
   edge passes that *same* `(uint)cpu.PC` to `chain.Invoke`, which `ResolveChain`s the *same* block. **Identical
   successor, identical block, identical CPU state at entry.**
2. **Same gates, same order.** `EmitDynamicChainOrExit` re-uses the exact three gates of `EmitChainOrExit`
   (budget ≤ 0 → dispatcher; `dirty.Any` → dispatcher; `InterruptPending` → dispatcher). When any gate fires it
   calls `EmitNormalExit` — i.e. it degrades to *exactly today's behavior*. So the dynamic edge can only ever
   chain when all three gates that the static edge already honors are clear; in every other case it is
   byte-for-byte the current round-trip.
3. **The oracle backstop is untouched.** Chaining changes *scheduling* (where control flows), never *semantics*
   (what each instruction computes). The interpreter remains the oracle; the differential fuzzer (which already
   runs chaining ON and OFF and asserts both match the interpreter — `JitOptions.DisableChaining`,
   `JitOptions.cs:19`) gains the dynamic-edge case for free: a divergent dynamic-chain result would surface as a
   fuzzer seed that diverges from the interpreter, exactly as a divergent static chain would.
4. **The TomHarte-through-JIT parity gate is the merge precondition (inherited, binding).** Every converted arm
   re-runs its TomHarte/ZEX/SingleStep slice through the JIT and must be **byte-identical**. Because the change
   is "chain instead of round-trip to the same block," and both paths were already individually parity-proven,
   the gate is expected green by construction — but it is *run*, not assumed (the ADR-0012 honesty: prove it).
   `JitOptions.DisableChaining = true` must still produce a byte-identical run (it forces every dynamic edge
   back through the dispatcher — the differential cross-check).

---

## 5. Decision 3 — SMC safety: a dynamic chain edge into a self-modified / invalidated block re-resolves correctly

This is the hard invariant and the reason the design reuses the existing machinery rather than inventing a
faster-but-riskier path. **A chained edge into a self-modified or evicted block MUST re-resolve to the
recompiled block, never run stale code.** The dynamic chain edge inherits *every* SMC guard the static chain
edge already has, because it goes through the identical resolution path:

1. **The `dirty.Any` gate fires before any chain.** `EmitDynamicChainOrExit` gate (3) is `if (dirty.Any) goto
   toDispatcher` — the same coarse SMC backstop as the static edge (`BlockCompiler.cs:1513-1516`). If *any* code
   page was written since the last dispatch, the dynamic edge does **not** chain; it round-trips, and the
   dispatcher's `InvalidateIfDirty()` (`JittedCpu.cs:208`) evicts the dirtied pages' blocks *before*
   re-dispatch. So a self-modified successor is never reached via a chain — it is reached via the dispatcher,
   after eviction, recompiled from the post-write bytes. **Identical to the static edge's SMC handling.**
2. **`ResolveChain` resolves BY PC through the live cache — no baked delegate, no IL patching.**
   `ResolveChain(targetPc, …)` → `GetOrCompile(targetPc)` (`BlockCache.cs:112-116`). If the successor block was
   evicted (SMC on its page, or a bus remap via `OnRemap`), the cache *misses* and recompiles it from current
   bytes *here*, on the edge. The dynamic edge cannot run a stale block: the cache is the single source of
   truth, keyed on the runtime PC. (This is the "resolve-by-PC, not bake-the-delegate" discipline ADR 0011 §1.2
   and `ChainTable.cs` document — the dynamic edge gets it for free by passing through `ResolveChain`.)
3. **Eviction severs inbound links, including dynamic ones.** When a block is evicted, `Evict`
   (`BlockCache.cs:182`) calls `Chains.Sever(block.EntryPc)` (drop inbound links INTO it) + `Chains.Forget(block)`
   (drop it FROM any inbound set). A dynamic chain edge registers its predecessor in the *same* `ChainTable`
   (via `ResolveChain` → `Chains.Link`), so its links are severed by the same eviction path. There is no
   second link table and no dynamic-edge-specific bookkeeping — the dynamic edge is indistinguishable from a
   static edge to the `ChainTable`. **The blast radius of eviction is unchanged.**
4. **The intra-block SMC guard is unaffected.** A block that writes its *own* page exits mid-block via
   `BlockExit.Recompile` (the `EmitSmcGuard`), which `RunChain` already treats as a forced round-trip
   (`JittedCpu.cs:262`). A dynamic chain edge is only at a *block-ending* opcode (a `RET`/`JMP-(ind)` is the
   last instruction), so it is never reached by a block that self-modified earlier — the guard already exited.
5. **The SMC cooldown lever (PR-S) interaction is already handled.** `ChainEdge` (`JittedCpu.cs:284`) checks
   `if (_cache.ShouldInterpret(targetPc)) return;` *before* `ResolveChain` — so a dynamic edge into an SMC-hot
   cooling PC breaks the chain (round-trips) exactly as a static edge does, routing the cooling PC through the
   interpreter. **No new interaction; the dynamic edge calls the same `ChainEdge`.**

**The net SMC argument:** the dynamic chain edge adds **zero** new state, **zero** new link table, and **zero**
new resolution path. It feeds a runtime PC into the *same* `chain.Invoke` → `ChainEdge` → `ResolveChain` →
`GetOrCompile`/`Chains.Link` pipeline that the static edge uses and that is already SMC-proven (the M2-ii
eviction model, the differential fuzzer, Klaus cycle-exact). The only new thing is *where the target value comes
from* (a field read instead of a constant), and the value is the same one the dispatcher round-trip would have
used. **SMC correctness is preserved by construction, not by a new argument.**

---

## 6. Honest scope — what this does NOT recover (the ADR-0012 discipline)

Measurement-disciplined, the round-trips this design does **not** eliminate, and why that is correct:

- **Fallback-op edges stay round-trips.** An un-emitted opcode ends the block with a dynamic post-`Step` PC
  (`EmitFallbackStep` → `EmitNormalExit`). These are genuinely unchainable at compile time (the interpreter
  computed the next PC at run time inside `inner.Step`, and the block has no idea what it is). They are the
  *minority* on the flat real boots (the hot quartets emit), but they are real — the Spectrum init's
  `LDIR`/`IM`/`EI` tail, DOS's occasional `BRK`-vector path. **This ADR does not touch them**; the lever for
  them is *more emit coverage* (ADR 0011 / ADR 0022 item D's fallback-by-opcode histogram), not chaining.
- **Far 8086 control flow stays a round-trip** (§3.1) — out of scope pending the `PagesSpanned` non-zero-CS
  follow-on.
- **The `ResolveChain` dict lookup itself is not removed** — only the dispatcher *round-trip around it* is. If
  the re-profile shows `ResolveChain` is now the hot residual, an inline cache is the named follow-on (OQ1), not
  part of this ADR.
- **SMC-thrash boots are not the target.** This is a real-boot *integration-code* win (returns + indirect
  jumps), orthogonal to the W1-Klaus SMC story (PR-S / ADR 0013 own that). The DOS/Spectrum boots have
  **zero** recompiles — this lever is for them, not for SMC thrash.

**If the pre-fix per-edge measurement (§7 PR-D1) showed the round-trips were dominated by fallback-op edges
rather than dynamic-control-flow edges, this ADR would not proceed** (the win would be small). The committed
counters + hot-op histograms already make that outcome unlikely (zero SMC; RET+CALL 18% on Spectrum; the DOS
hot quartet all emits), but PR-D1 *confirms the category split with a number* before any arm is converted —
that is the gate, not a belief.

---

## 7. Blast radius, SAFE/RISKY, and the Builder-ready plan

### 7.1 Blast radius + classification

The change touches the **shared block-cache chaining path** — specifically it adds one emit helper
(`EmitDynamicChainOrExit`) and converts the terminal exit of a handful of dynamic-target arms from
`EmitNormalExit` to that helper. It does **not** touch: any op body, the flag/cycle models, `BlockCache`'s
eviction/resolution logic, `ChainTable`, the fastmem split, the SMC guard, or the dispatcher preamble. The new
helper is a near-clone of the proven `EmitChainOrExit` with one line changed.

- **Classification: SAFE-leaning-RISKY.** It is **SAFE** in mechanism (reuses the parity- and SMC-proven
  chaining pipeline verbatim; degrades to today's behavior on every gate; one-line-per-arm change), but it
  touches the **shared chaining infra that every CPU's blocks flow through**, so a bug would be cross-CPU and
  in correctness-critical territory. Hence: **per-arm, per-CPU, parity-gated rollout** (one arm family at a
  time, each behind its own TomHarte/ZEX byte-identity gate + a before/after profile diff), never a big-bang
  conversion. With that discipline it is a **contained, parity-safe chaining improvement with a clear measured
  target** — which is the PROCEED bar.

### 7.2 The plan (bite-sized, each step parity-gated + profile-diffed)

Two global gates bind every PR (inherited from ADR 0011 §8 / ADR 0022 §6.3):
- **Parity gate (merge precondition):** the arm's TomHarte/ZEX/SingleStep-through-JIT slice is byte-identical,
  AND `JitOptions.DisableChaining = true` still produces a byte-identical run (the dynamic edge correctly
  degrades to a round-trip). The differential fuzzer runs chaining ON and OFF.
- **Honesty gate (the win is real):** a before/after `profile.json` diff on the candidate's hot boot, against
  the **frozen** budgets (a `git diff` of the profiler budgets shows no change), with `chain:disp` and the
  real-time ratio committed. The predicted win must materialize or the PR is reverted.

| PR | Scope | Gate | Size | Deps |
|---|---|---|---|---|
| **D0 — `EmitDynamicChainOrExit` helper** | Add the helper in `BlockCompiler.cs` (clone of `EmitChainOrExit`, target from `cpu.PC` via `_fpc`). **No arm converted yet** — a JIT unit test compiles a one-shot block ending in a dynamic edge and asserts it chains to a runtime PC (and round-trips when a gate fires). | the unit test; no throughput claim | **S** | none |
| **D1 — 6502 returns + indirect jumps** | Convert `EmitRts` + `EmitJump` Indirect to `EmitDynamicChainOrExit`. **Pre-step (PR-D1 measurement):** before converting, add a temporary OFFLINE per-exit-category counter to the profiler to confirm RTS/JMP-(ind) dominate the DOS round-trips (the §6 confirm-the-category gate). | 6502 TomHarte-through-JIT byte-identical; DOS 3.3 `chain:disp` rises + real-time ratio improves (committed diff) | **M** | D0 |
| **D2 — Z80 returns** | Convert `EmitZ80Ret` + `EmitZ80RetCc` (taken arm). | ZEX(ALL/DOC)-through-JIT byte-identical; **Spectrum `chain:disp` rises from 0.28 toward parity + real-time ratio crosses the interpreter** (the headline win) | **M** | D0 |
| **D3 — 8086 near returns + indirect** | Convert near `RET`/`RET imm16`, `FF /4 JMP r/m`, `FF /2 CALL r/m` via `M8086NearChainKey` over the runtime IP. Far stays fallback (§3.1). | 8086 TomHarte-through-JIT byte-identical; bench-8086 W1-mixed `chain:disp` rises (no 8086 real boot, so the kernel is the witness) | **M** | D0 |
| **D4 — 68000 returns (conditional)** | Convert `RTS`/`JMP (An)`/`JSR (An)` **only if** a 68000 real boot or a profile shows the round-trip rate matters (ADR-0022 ROI gate). | SingleStep-through-JIT byte-identical; measured delta | **S** | D0; gated on a profile |

**Recommended dispatch order:** D0 → **D2 (Spectrum, the clearest headline)** → D1 (DOS) → D3 (8086 kernel) →
checkpoint with the owner on whether the re-profile shows `ResolveChain` is the new residual (OQ1 — build the
inline cache or not). D4 only if a profile justifies it.

### 7.3 Expected before/after

| boot | metric | before (committed) | expected after | mechanism |
|---|---|---:|---:|---|
| **spectrum-48k** | `chain:disp` | 0.28 | **rises substantially** (returns stop round-tripping; the RET≈CALL pairs both chain) | D2 |
| | JIT real-time ratio | 19.3× (0.84× of interp) | **crosses the interpreter** (≥ 22.9×) — the goal: JIT beats interp on a real boot | D2 |
| **apple2-dos33** | `chain:disp` | 1.0 | **rises** (RTS/JMP-(ind) chain) | D1 |
| | JIT real-time ratio | 19.7× (0.39× of interp) | **improves materially** toward the interpreter; full parity also needs the fallback tail emitted (out of scope) | D1 |
| **bench-z80 / bench-8086 W2** | `chain:disp` | >100,000 (already chain) | **unchanged** (no returns in the tight kernels) | regression check — the kernels must NOT regress |

> **Honest caveat on DOS:** DOS's interpreter ratio (50.4×) is very high, so even with returns chained, the JIT
> may not *beat* the interpreter on DOS until more of its fallback tail emits — the dynamic chain edge is
> necessary but, for DOS specifically, may not be sufficient for a full win. The *measurable* claim is
> `chain:disp` rises and the JIT ratio improves; the *beat-the-interpreter* claim is firmest for Spectrum (the
> closest race, 0.84×, and the most return-dominated). The re-profile says exactly where each lands — no
> "after" number is asserted here, per ADR 0012.

---

## 8. Recommendation — **PROCEED (hand to Builder)**, headline on Spectrum

This is a **contained, parity-safe chaining improvement with a clear measured target**, which is the PROCEED
bar:

- **The root cause is measured, not believed** — the round-trips are dynamic-target control flow (returns +
  indirect jumps), proven by the committed `chain:disp` ratios + hot-op histograms + the zero-SMC counters.
- **The fix reuses the parity- and SMC-proven chaining pipeline verbatim** — one new helper (a near-clone of
  `EmitChainOrExit`), one line changed per arm, no new state, no new link table. SMC correctness is preserved by
  construction (§5); parity is byte-identical by construction and gated by TomHarte-through-JIT (§4).
- **The win is provable by the existing before/after profile diff** — `chain:disp` and the real-time ratio on
  the two real boots, against frozen budgets (the ADR-0022 honesty gate).
- **The blast radius is bounded and the rollout is per-arm, per-CPU, parity-gated** — never a big-bang; each PR
  is independently revertable and the kernels are a no-regression witness.

**Why not CHECKPOINT:** the CHECKPOINT triggers (a large/risky core-cache rework, or a measured root cause that
suggests low ROI) do **not** fire. The change is *not* a core-cache rework (it adds an emit helper and reuses
the cache unchanged), and the ROI is *not* low (the round-trips are on chainable edges and are 50–78% of block
entries). The one honest hedge — DOS may need more emit coverage for a full beat-the-interpreter win — is a
*scope note*, not a reason to checkpoint: the Spectrum headline (the closest race, return-dominated) is a clean,
high-confidence win, and D1 still improves DOS's `chain:disp` measurably.

**One owner-confirmable item before Builder starts** (a one-line decision, not a blocker): D1's pre-step adds a
*temporary* offline per-exit-category counter to the profiler to confirm the RTS-vs-JMP-(ind) split before
converting — this is the §6 "confirm the category with a number" gate. If the owner would rather skip the
temporary instrumentation and convert returns directly (the histograms already make the category clear),
D1/D2 can proceed without it. Recommend keeping it (it is the ADR-0012 discipline made concrete and costs one
throwaway counter).

---

## 9. Open questions

1. **Inline cache for `ResolveChain` (the second-order lever).** After D2/D1 chain the dynamic edges, is the
   remaining hot residual the `ResolveChain` dict `TryGetValue` itself? If the re-profile shows it is, a
   per-edge inline cache (`(lastTargetPc, lastBlock)` slot + a runtime compare, fall back to `ResolveChain` on
   miss) is the named follow-on. **Resolve empirically after D2 — do not build it pre-emptively** (the kernels
   chain through `ResolveChain` at `chain:disp` > 100,000 and are fast, so it is likely *not* the floor — but
   measure, don't assume). This is the ADR-0012 guard applied to this ADR's own follow-on.
2. **A cheaper dispatcher preamble (complementary).** The fallback-op edges and the gate-blocked dynamic edges
   still round-trip; the `Run`-loop preamble (`Halted` check, `ProjectBlockKey`, `ShouldInterpret`) is paid on
   each. Is trimming it (e.g. hoisting the `Halted`/`ShouldInterpret` checks, or caching `ProjectBlockKey` for
   flat CPUs where it is identity) worth a follow-on? Lower priority than chaining; revisit if the re-profile
   shows the residual round-trips are still material after D1/D2.
3. **68000 dynamic-edge priority.** D4 is gated on a profile showing it matters; the 68000 has no real-boot
   profile today (kernel-only). Does the owner want a 68000 real-boot workload added to the profiler (so D4 has
   a witness), or does D4 stay parked until a cycle-sensitive/real 68000 consumer appears? Recommend: park D4;
   add a 68000 real boot to the profiler only when one exists as a product surface.
4. **Far 8086 control flow.** Near returns are in scope (§3.1); far returns wait on the `PagesSpanned`
   non-zero-CS SMC follow-on (ROADMAP). Confirm the owner is content to leave far-flow round-tripping for now
   (it is rare in the hot path and entangled with an open SMC caveat). Recommend: yes, defer far.

---

*End of ADR 0023. Root cause (measured): on the real boots the JIT loses to the interpreter because 49% (DOS
3.3) to 78% (Spectrum) of block entries round-trip through the dispatcher instead of chaining, and the
round-trips are dominated by **dynamic-target control flow — returns + indirect jumps** — which are chainable
in principle (the successor is a real PC) but exit today because the arms bake only compile-time-constant chain
targets. The fix: a **dynamic chain edge** (`EmitDynamicChainOrExit`) that feeds the runtime `cpu.PC` into the
existing PC-keyed `ResolveChain`/`ChainTable`/eviction pipeline — parity-identical (degrades to today's
round-trip on every gate; byte-identical to the same-block dispatch it replaces) and SMC-safe by construction
(zero new state; the same `dirty.Any` gate, the same resolve-by-PC, the same eviction-severs-links machinery).
SAFE-leaning-RISKY (it touches the shared chaining path) → per-arm, per-CPU, parity-gated + profile-diffed
rollout (PRs D0–D4). Expected: Spectrum `chain:disp` rises from 0.28 and its JIT crosses the interpreter (the
headline); DOS `chain:disp` rises from 1.0 and its JIT ratio improves (full DOS parity also needs more emit
coverage — out of scope). Recommendation: **PROCEED — hand to Builder**, headline on Spectrum (D0 → D2 → D1 →
D3), checkpoint with the owner after the re-profile on whether `ResolveChain` is the new residual (OQ1). The
success metric is the real before/after profile diff, not a believed one (ADR 0012's discipline). Designer: no
UX surface (a faster JIT is invisible except as throughput). Planner: §7.2 is the PR arc; D0 is the unblocker;
D2 is the headline checkpoint; D4 is parked pending a 68000 real-boot profile.*
