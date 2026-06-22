# ADR 0019 — 8086 far-flow emit and the `(CS,IP)` block-cache key

> **Status:** **Proposed** (Claude Architect, 2026-06-22). Drafted against fresh `main` @ `7cb3265`.
> Designs the **#1 owner-prioritized deferred item** (ROADMAP.md "Deferred & candidate follow-ons" item 1,
> 2026-06-19): *8086 far-flow emit — far `JMP`/`CALL`/`RET` (and far interrupts) stay fallback because the
> block-cache key is `(IP)`, CS-invariant. Emitting them requires widening the cache key to `(CS,IP)` so a
> far transfer to the same offset under a different segment is a distinct block.* The most-named M6 gap; it
> unblocks real-mode 8086 programs.
>
> **Date:** 2026-06-22
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Verdict (stated up front, per the task's stop-or-hand-off gate):**
> - **Cache key:** widen to the **linear physical entry `(CS<<4)+IP` (20-bit `uint`)**, NOT a composite
>   `(CS,IP)` struct — §3 Decision 1. It is the address the 8086 already computes everywhere (the same
>   `M8086CodePhys` / `AddressSpaceFetchStream` origin the emit arm and `Discover` already bake), it
>   disambiguates overlapping segments correctly (two `(CS,IP)` pairs that alias the same physical byte ARE
>   the same block — the hardware truth), and it folds the segmented and flat CPUs onto **one `uint`-keyed
>   cache** with no struct, no boxing, no per-CPU key type.
> - **Generic vs 8086-only:** a **generic `uint` linear-key seam**, NOT an 8086 special-case — §3 Decision 1.
>   Every CPU already keys on *its own program counter*; we widen the cache's key type from `ushort` to
>   `uint` and add **one per-CPU `BlockKey` projection** (`_pcName`-read → `uint`). The non-segmented CPUs'
>   projection is the identity (`(uint)PC`), so they collapse to today's behavior **byte-for-byte**.
> - **Blast-radius classification: SAFE** — §3 Decision 2. The widening is `ushort → uint` on the cache key
>   type plus one projection function; the 6502/Z80/68000 projection is `(uint)PC` (their PC already IS the
>   whole key), so their emitted blocks, chains, SMC marks, and `FallbackEmitCount` are unchanged. The
>   classification rests on an un-fakeable regression: a **key-projection identity gate** that proves the
>   three non-segmented CPUs produce byte-identical block/chain/eviction behavior before vs after the widening.
> - **One PR or a short arc:** a **short arc of 2 PRs** (FF-1 the key widening + identity gate; FF-2 the far
>   emit arms + aliasing gate), with FF-2 optionally split if the far-INT/IRET tail is deferred — §5.
> - **Because the verdict is SAFE, this ADR hands to the Planner** (it does not stop at design). The one place
>   that would flip to RISKY — a non-`uint`-wide composite key that changes the non-segmented CPUs' keying — is
>   the alternative this ADR **rejects** (§3 Decision 1, Alternative A).
>
> **Supersedes / relates to:**
> - **ADR 0011** (`0011-jit-hot-op-emission-optimization.md`) — the M6 emit design. §0 names "8086 far-flow
>   emit (needs `(CS,IP)` cache-key widening)" as the first **named deferred follow-on**; ROADMAP item 1 is
>   the same gap. ADR 0011 Decision 1 (per-CPU hand-written arms, descriptor-gated, fastmem-direct,
>   flag-exact, mirroring the oracle) and the emit-vs-fallback boundary (§2: emit the hot 86–100%; fallback
>   the rare/complex/exception tail) govern §4's far-arm scope. The 8086 MOV/ALU/near-flow arms this ADR
>   extends are PR-B/C/D of that arc (`BlockCompiler.M8086.cs`).
> - **ADR 0005 / 0006** (`0005-8086-state-segmentation-bus-and-flags.md`,
>   `0006-8086-decode-modrm-instruction-set-and-m5-arc.md`) — the 8086's `(CS<<4)+IP` segmentation, the FLAGS
>   model, and the far-transfer semantics (CS+IP load, far push/pop) the far arms must reproduce exactly.
> - **ADR 0009 Decision 2 + OQ3** + **ADR 0013** (per-bank `(PC, BankConfigId)` specialization). The
>   bank-switch keying question is structurally adjacent ("the same offset means different code under a
>   different *configuration*") but **orthogonal**: ADR 0013 keys on the *bank-config id*; this ADR keys on
>   the *segment-folded physical address*. §6 Open Question 3 reconciles them (a future board that both
>   segments AND bank-switches composes the two key axes).
> - **ADR 0012** (`0012-jit-dirty-page-list-invalidation.md`, Rejected) — confirms the per-page SMC scan is
>   ~1.3% of runtime, not the floor; the widened key does not touch the dirty-page mechanism (§3 Decision 2.4),
>   so that finding stands.

---

## 1. Context

### 1.1 The bug, stated precisely (verified against `main` @ `7cb3265`)

The 8086 JIT (M5 interpreter + M6 PR-B/C/D emit arms) emits IL for MOV, ALU+FLAGS, and **near**
branch/call/return. **Far** `JMP`/`CALL`/`RET` and the far-vectoring `INT`/`INTO`/`IRET` stay
interpreter-fallback (ROADMAP "What M6 emitted" table: *"far flow (CS-invariant block key)"*). The blocker is
not the emit difficulty of the far arms themselves — it is that **emitting a CS-changing transfer is unsound
under the current cache key**, and the arms were deliberately scoped out until the key is fixed.

**The block cache is keyed on the 16-bit IP alone, CS-invariant.** The chain is, end to end:

- **The dispatcher reads only IP.** `JittedCpu.Run` (`JittedCpu.cs:153`):
  `var pc = (ushort)_inner.GetRegister(_pcName);` — where `_pcName` is `ProgramCounterName`, which for the
  8086 is **`"IP"`** (`M8086Spec.cs:44`: `new("IP", 16, RegisterRole.ProgramCounter)`). **CS is never read on
  the dispatch hot path.** On a cache hit the dispatcher hands back a block by IP alone.
- **The cache, recompile lever, and chain table are all `ushort`-keyed.** `BlockCache<TCpu>`
  (`BlockCache.cs:26-35`): `Dictionary<ushort, CompiledBlock<TCpu>> _blocks`, plus `_recompiles`,
  `_cooldown`, `_blocksByPage` (page index). `ChainTable<TCpu>._inbound` is
  `Dictionary<ushort, HashSet<…>>` (`ChainTable.cs:10`). `CompiledBlock<TCpu>.EntryPc` is `ushort`
  (`CompiledBlock.cs:70`). Every key is the 16-bit IP.
- **The decode is segment-correct, so a *freshly compiled* block is right — but the cache key is not.**
  `Discover` bakes `_m8086CodePhysBase` from the **live CS at Compile time**
  (`BlockCompiler.cs:380-382`): `_m8086CodePhysBase = ((uint)(ushort)_m8086CS.GetValue(_cpu) << 4) & 0xFFFFF`,
  and `M8086CodePhys(ip) = (_m8086CodePhysBase + ip) & 0xFFFFF` (`:807`) feeds every emit-time const read and
  the `AddressSpaceFetchStream(_bus, ip, CS)` decode walk (`:399-400`). The arm's own comment names the hazard
  outright (`:377`): *"Bake the block's code-segment base once (the live CS at Compile time — **the CS-aliasing
  invariant**)."* The block's *bytes* are decoded from `(CS<<4)+IP`; its *key* is `IP`.

**The aliasing failure.** Compile a block when `CS=0x1000, IP=0x0100`: it decodes physical `0x10100…`, and is
cached under key `0x0100`. Now execution reaches `CS=0x2000, IP=0x0100` (physical `0x20100…` — entirely
different code). The dispatcher reads IP `0x0100`, hits `_blocks[0x0100]`, and **runs the `0x1000` segment's
compiled code against the `0x2000` segment's state** — a silent wrong-execution bug. This is the single worst
class of JIT bug (ADR 0009's framing): it passes every existing test (the 8086 corpus is single-step, one
instruction per case — no far transfer that re-enters the same IP under a new CS) and fails only on real
real-mode software, where far calls into a shared library at a fixed offset under rotating segments are
routine (DOS INT vectors, overlay loaders, `.COM`→`.EXE` far calls, BIOS far entry points).

**Why far flow is fallback today, and why fallback is currently safe.** A fallback op ends the block
(`EndsBlock=true`) and round-trips to the dispatcher, which re-reads IP and (critically) **re-compiles against
the now-live CS** because the only way a fallback block's successor is reached is a fresh `GetOrCompile`. The
near-flow arm chains (`EmitChainOrExit`, `BlockCompiler.M8086.cs:1128/1141/1152/1164`), but it only ever
chains to targets **in the same segment** (a near branch cannot change CS), so within a single dispatch the
baked `_m8086CodePhysBase` stays valid. The unsoundness is latent: it is armed the instant an *emitted* op
changes CS mid-chain (a far transfer) OR the instant two segments' code share an IP offset across dispatches.
The first is why far emit is blocked; the second is a pre-existing latent hazard that the current all-near
8086 corpus never exercises but real software does. **Widening the key fixes both at once.**

### 1.2 What the shipped code already proves (the seam is half-built)

The hard part — computing the segment-folded physical address — **already exists and is already used**:

- `M8086CodePhys(ip) = (_m8086CodePhysBase + ip) & 0xFFFFF` (`BlockCompiler.cs:807`) is exactly the linear key
  this ADR proposes, already computed at compile time for every code-byte read.
- `AddressSpaceFetchStream(bus, offset, segment)` (`AddressSpaceFetchStream.cs:41`) is the interpreter's own
  segmented fetch origin — the oracle the emit arm mirrors. `FetchAddress` is `((segment<<4)+offset)&0xFFFFF`.
- The interpreter already loads CS+IP atomically on a far transfer (M5, ADR 0005); the far emit arm transcribes
  that the same one-for-one way the near arm transcribed near flow.

So the missing pieces are narrow: **(a)** the cache key type and the dispatcher's projection of `(CS,IP)` →
`uint`, and **(b)** the far emit arms, which must set the key's CS half (write the `CS` field) before they
`EmitChainOrExit`/exit so the next block is keyed and decoded under the new segment.

---

## 2. Forces

1. **Correctness is non-negotiable and un-fakeable.** The interpreter is the oracle (ADR 0011); a far-emitted
   block must be byte-identical to the interpreter through the TomHarte 8088 corpus, AND the aliasing case
   (two segments, same offset) must produce distinct blocks. A wrong key is a silent data-corruption bug, so
   the gate must *fail on the old key* and *pass on the new* (the un-fakeable bar).
2. **The cache is SHARED infrastructure.** `BlockCache<TCpu>`, `ChainTable<TCpu>`, `CompiledBlock<TCpu>`,
   `JittedCpu<TCpu>` are generic over every CPU. The 6502/Z80/68000 must be **byte-for-byte unchanged** —
   same blocks, same chains, same eviction counts, same `FallbackEmitCount`. Any key change that alters their
   keying is RISKY and out of scope.
3. **AOT-clean Core.** The key projection must not introduce a per-CPU concrete dependency into `Core` or a
   reflection-heavy hot path. The dispatcher already reads PC via `_inner.GetRegister(_pcName)` (interface
   only); the CS read must use the same interface-only discipline or a once-resolved field handle (the JIT's
   existing `_m8086CS` pattern, `BlockCompiler.cs:261`, is JIT-internal and AOT-irrelevant — `CpuEmulator.Jit`
   is already the dynamic-code tier).
4. **Hot-path cost.** The dispatcher runs the projection **once per block dispatch** (not per instruction —
   chaining stays within the emitted blocks). A `(CS<<4)+IP` fold is two field reads + a shift + an add + a
   mask: negligible, and the non-segmented CPUs pay only `(uint)PC` (a widening conv).
5. **Future segmented + banked CPUs.** A 32-bit-key seam (`uint`) leaves headroom for the 80286/386 protected
   mode (where the "segment base" is a descriptor-table lookup, not `CS<<4`) and composes with ADR 0013's
   bank-config axis (§6 OQ3). A 16-bit composite would not.

---

## 3. Decisions

### Decision 1 — the cache key widens to a generic 32-bit linear `BlockKey`, projected per-CPU; the 8086's projection is `(CS<<4)+IP`, the non-segmented CPUs' is the identity `(uint)PC`

**The key type becomes `uint` (call it `BlockKey`), and each CPU supplies a projection
`ICpuCore/IMonitorSupport state → uint` that the dispatcher calls once per block dispatch.** Concretely:

- **Non-segmented CPUs (6502, Z80, 68000):** the projection is the identity over the PC register —
  `(uint)GetRegister(_pcName)`. Their PC *is* the entire address the block decodes from, so the `uint` key is
  numerically the same value the `ushort`/`uint` PC already was. **Their keying is unchanged.** (The 68000's
  PC is already 32-bit; the 6502/Z80's is 16-bit zero-extended to `uint` — same key set, just a wider box.)
- **The 8086:** the projection is `((CS<<4) + IP) & 0xFFFFF` — the linear physical entry, **the exact value
  `M8086CodePhys` and `AddressSpaceFetchStream` already compute** for the decode. Two `(CS,IP)` pairs that
  fold to the same physical byte are the same block (the hardware truth — overlapping segments execute the
  same code); two pairs that fold to different bytes are distinct blocks (the aliasing fix).

**Why linear `(CS<<4)+IP`, not a composite `(CS,IP)` struct.**

- **It is the address the machine already computes.** The decode, the emit-time const reads, and the
  interpreter's fetch all origin at `(CS<<4)+IP`. Keying on the same value means the key and the decoded
  bytes are derived from one source — there is no way for them to disagree. A composite `(CS,IP)` re-derives
  the physical at decode time and risks the key and the bytes drifting (the very class of bug being fixed).
- **It models overlapping segments correctly — for free.** `CS=0x1000,IP=0x0100` and `CS=0x1010,IP=0x0000`
  are the *same* physical byte `0x10100` and *must* be the same block. The linear key makes them collide
  (correctly, one cached block); a `(CS,IP)` composite makes them two distinct keys for identical code —
  wasteful and, worse, two blocks that can fall out of sync under SMC (one evicted, one stale).
- **It unifies the cache.** One `Dictionary<uint, …>` serves every CPU. No per-CPU key struct, no
  `IEqualityComparer`, no boxing, no generic-over-TKey explosion. The `uint` is a value type the JIT already
  manipulates everywhere.
- **It is the SMC/page story already.** The dirty-page index keys on `physical >> 8` (`BlockCompiler.cs:1086`,
  `dirty.Mark(addr >> 8)`); a block's `SpannedPages` are physical pages. Keying the block itself on the
  physical entry makes the block key and the page index live in the same address space — the eviction logic
  (`InvalidateIfDirty` → `EvictBlocksOnPage` → `Evict(block)` by `EntryPc`) needs no translation layer.

**Alternatives considered.**

- **(A) A composite `(CS,IP)` struct key (the literal ROADMAP phrasing).** *Rejected.* It fails the
  overlapping-segment case (same code, two keys), it needs a per-CPU key struct + comparer (the non-segmented
  CPUs would need a degenerate `(0, PC)`), and it re-derives the physical separately from the decode — exactly
  the key/bytes-drift hazard. The ROADMAP says "`(CS,IP)` *or, more generally, the linear `(CS<<4)+IP`*"; this
  ADR takes the more-general option the ROADMAP itself flags as preferable. **This is the alternative whose
  adoption would make the change RISKY** (it changes the non-segmented CPUs' key shape); rejecting it is what
  keeps the change SAFE.
- **(B) Keep `ushort` and store CS in the block, comparing CS on hit.** *Rejected.* A `Dictionary<ushort,…>`
  can hold only one block per IP, so two segments at the same IP evict each other on every cross-segment
  transfer — cache thrash precisely on the hot far-call pattern. The dictionary must be keyed on the full
  discriminant, not key-on-IP-then-validate.
- **(C) An 8086-only second cache, leaving the shared cache `ushort`.** *Rejected.* It forks the
  dispatcher/chain/SMC machinery per CPU, duplicating the most-tested code in the JIT — the opposite of the
  ADR 0011 "share what's proven shared" discipline. The widening is small enough that one generic `uint`
  cache is strictly simpler than two.
- **(D) Make the key the full 32-bit linear address only for ≥16-bit-segmented CPUs, `ushort` otherwise (a
  per-CPU key width).** *Rejected.* It reintroduces a generic-over-TKey split for no benefit; `uint` costs the
  16-bit CPUs nothing measurable (the dictionary's hash of a `uint` vs a `ushort` is identical work).

**Consequences.**
- *Good:* one `uint`-keyed cache serves every CPU; the 8086 aliasing bug is fixed at the root; overlapping
  segments are modeled correctly; the key and the decoded bytes share one source of truth.
- *Good:* the seam is genuinely generic — the 80286/386 (descriptor-base segments) and any future segmented
  CPU supply their own projection without touching the cache.
- *Bad / accepted:* the key widens `ushort → uint` across `BlockCache`, `ChainTable`, `CompiledBlock`, and
  the dispatcher's local — a mechanical type change touching ~5 type signatures. Bounded and SAFE (Decision 2),
  but it is a real diff in shared files (the source of the "is this RISKY?" question — answered in Decision 2).
- *Bad / accepted:* the 8086 dispatcher now reads **two** fields (CS + IP) per block dispatch instead of one.
  Negligible (once per block, not per instruction), and the CS read uses the once-resolved field handle the
  compiler already holds (`_m8086CS`). The projection lives behind the per-CPU seam so the 6502/Z80/68000 read
  only their PC.

#### The per-CPU projection seam (the concrete shape)

The dispatcher needs `state → uint`. Two AOT-clean options; the ADR recommends the first:

1. **A `BlockKey` projection on the JIT-side per-CPU target (`IJitTarget`), resolved once at construction.**
   `IJitTarget` already carries the per-CPU reflection seam (`BlockCompiler.cs:167`, ADR 0011). Add:

   ```csharp
   // CpuEmulator.Core.Jit.IJitTarget (the existing per-CPU JIT seam — JIT tier, AOT-irrelevant)
   /// <summary>Project the CPU's current execution point to the 32-bit block-cache key. For a flat-PC
   /// CPU this is (uint)PC (the identity — byte-identical to the old ushort key). For the 8086 it folds
   /// the segmented origin: ((CS&lt;&lt;4)+IP)&amp;0xFFFFF — the same physical the decode/fetch already use.
   /// Read once per block dispatch (NOT per instruction; chaining stays inside the emitted block).</summary>
   uint ProjectBlockKey(TCpu cpu);
   ```

   The 6502/Z80/68000 implementations are `(uint)cpu.<PC>`; the 8086's is
   `(uint)(((cpu.CS << 4) + cpu.IP) & 0xFFFFF)`. The dispatcher replaces
   `var pc = (ushort)_inner.GetRegister(_pcName);` with `uint key = _target.ProjectBlockKey(_inner);`.
   This keeps the projection on the existing per-CPU seam, resolved once, no per-dispatch reflection.

2. **A dispatcher-local fold using the already-held field handles.** `BlockCompiler`/`JittedCpu` already
   resolve `_fpc` (the IP field) and `_m8086CS` (the CS field). The dispatcher could read both directly when
   `TargetIsM8086`. *Rejected as the primary* — it scatters the 8086 special-case into the generic dispatcher
   (`JittedCpu.Run`), exactly the per-CPU branch the `IJitTarget` seam exists to avoid. Option 1 keeps
   `JittedCpu.Run` CPU-agnostic.

**The emit arm's compile-time base must equal the dispatch-time key.** `Discover` bakes
`_m8086CodePhysBase` from the live CS (`:380`). After the widening, the block is *keyed* on
`(CS<<4)+IP` and *decoded* from `(CS<<4)+IP` with the **same CS** — they are now provably the same value (both
fold the live CS at compile time). The far arm's obligation (Decision 3 / §4) is to **write the CS field
before exiting** so the *next* dispatch's `ProjectBlockKey` reads the new CS and keys/decodes the successor
under the new segment. This is the direct analogue of the near arm writing IP before `EmitChainOrExit`.

### Decision 2 — blast radius: the widening is SAFE; the non-segmented CPUs collapse to today's behavior byte-for-byte, gated by a key-projection identity regression

**Classification: SAFE.** The change is `ushort → uint` on the cache key type plus one per-CPU projection
whose non-segmented implementation is the identity. The 6502/Z80/68000 keep:

1. **The same key set.** `(uint)PC` is numerically the old `ushort PC` (zero-extended). A `Dictionary<uint,…>`
   keyed on `(uint)PC` partitions blocks identically to a `Dictionary<ushort,…>` keyed on `PC` — same
   collisions (none beyond identity), same lookups, same hits/misses.
2. **The same chains.** `ChainTable<TCpu>` widens its key to `uint`; `EmitChainOrExit`'s static target
   (`BlockCompiler.cs:1391`, a compile-time-constant PC) is projected through the same identity, so every
   chain edge links the same predecessor→successor pairs.
3. **The same SMC/eviction.** `SpannedPages` and the dirty-page index are **already physical-page based** and
   are NOT the block key — the widening does not touch them. `Evict(block)` removes by `block.EntryPc` (now
   `uint`, but the identity value); the per-page index (`_blocksByPage`) is unchanged. ADR 0012's finding (the
   page scan is ~1.3%, not the floor) stands untouched.
4. **The same `FallbackEmitCount` and emit arms.** Not one emit arm changes for the non-segmented CPUs; the
   key type is upstream of emission. `FallbackEmitCount` (`BlockCompiler.cs:31`) is unaffected.

**Why this is not RISKY (the discriminator).** RISKY would mean the widening could change a non-segmented
CPU's block boundaries, chain edges, eviction order, or emitted IL. It cannot, because:
- the key change is a **type widening with an identity projection** for those CPUs — the *values* are
  unchanged, only the *box* is wider;
- the SMC/page machinery (the one place a key change could ripple into invalidation) is keyed on physical
  pages, **independently of the block key**, so it is literally not touched;
- the emit arms are downstream of the key and read the PC field directly, not the cache key.

The one structure that meaningfully changes is the `Dictionary<ushort,…> → Dictionary<uint,…>` type and the
dispatcher's `ushort pc → uint key` local. Both are mechanical and covered by the identity gate below.

**The un-fakeable SAFE gate (the merge precondition for the widening PR).** A **key-projection identity
regression**: run the existing 6502/Z80/68000 JIT sweeps (TomHarte-through-JIT, Klaus cycle-exact, ZEXALL/ZEXDOC,
the 68000 SingleStep slice, the chaining + SMC + `FallbackEmitCount` pins) **before and after the widening** and
assert **byte-identical** results — same block count, same `ChainStepCount`, same `TotalEvictions`/
`TotalRecompiles`, same `FallbackEmitCount`, same emitted bytes. A unit test asserts
`ProjectBlockKey` for each non-segmented CPU equals `(uint)PC` over a sweep of PC values. This is the
"non-segmented CPUs unchanged" claim made un-fakeable: the gate fails if the widening perturbs them at all.

**Blast-radius surface (the files the widening touches, all in `CpuEmulator.Jit` + the `IJitTarget` seam):**

| File | Change | Risk |
|---|---|---|
| `BlockCache.cs` | `_blocks`/`_recompiles`/`_cooldown` key `ushort→uint`; `GetOrCompile`/`ResolveChain`/`Evict`/`ShouldInterpret`/`NoteInterpretedDispatch` param `ushort→uint` | mechanical type widening; identity-gated |
| `ChainTable.cs` | `_inbound` key + `Link`/`InboundTo`/`Sever`/`ResolveChain` param `ushort→uint` | mechanical; identity-gated |
| `CompiledBlock.cs` | `EntryPc` `ushort→uint`; `BlockDelegate`/`ChainDispatch` target `ushort→uint` (the emitted chain-edge call site) | the chain-edge IL passes the target; widened constant push — covered by emit re-gen |
| `JittedCpu.cs` | dispatcher local `ushort pc → uint key = _target.ProjectBlockKey(_inner)`; `ShouldInterpret`/`NoteInterpretedDispatch`/`ResolveChain`/`OnRemap` call sites | the one hot-path read changes; identity-gated |
| `BlockCompiler.cs` | `Compile(uint entryPc)`/`Discover`/`EmitChainOrExit` static-target type `ushort→uint`; the emitted chain-target constant widens; for the 8086 the compile-time key now equals `M8086CodePhys` | the 8086 key/decode unify; non-8086 unchanged |
| `IJitTarget` (Core.Jit) | add `uint ProjectBlockKey(TCpu)` | additive; per-CPU impls trivial |

**Note on the emitted chain-edge constant.** Today `EmitChainOrExit(ctx, ushort staticTargetPc)` pushes a
16-bit constant the chain callback resolves by `ushort`. After the widening it must push the **projected
`uint` key**. For the non-segmented CPUs that is `(uint)staticTargetPc` — same value. **For the 8086 near arm,
the static target is an IP within the same CS**, so its projected key is `(_m8086CodePhysBase + target) &
0xFFFFF` — the compile-time-constant physical (the base is baked, the target is constant), so the near arm's
chain edge is still a compile-time constant `uint`, just folded with the baked CS. **The 8086 near arm
therefore also needs this one-line change** (fold the static target through the baked base) — which is why the
widening PR (FF-1) must re-gate the *existing* 8086 near-flow parity too, not only the non-segmented CPUs.

### Decision 3 — the far emit arms: emit far `JMP`/`CALL`/`RET` (the CS-loading + far stack effects); keep `INT`/`INTO`/`IRET` and `BOUND` fallback by the M6 partial-emit philosophy

Per ADR 0011 §2's emit-vs-fallback boundary (emit the hot/simple/regular; fallback the rare/microcoded/
exception-vectoring), the far family splits:

**EMIT (the hot, regular far transfers):**
- **`EA` far `JMP ptr16:16`** (direct far jump — `CS:IP` immediate) and **`FF /5` far `JMP m16:16`** (indirect
  far jump through memory). Sets CS+IP from the immediate/memory operand; dynamic-or-static per form. The
  direct `EA` form has a **compile-time-constant** target `(newCS, newIP)` → a constant projected key, so it
  can **chain** (the successor block is keyed/decoded under the new segment — the whole point of the widening).
  The `FF /5` indirect form is a dynamic exit (the `(CS,IP)` comes from runtime memory).
- **`9A` far `CALL ptr16:16`** and **`FF /3` far `CALL m16:16`**. Far call pushes **CS then IP** (the far
  return frame) onto SS:SP, then loads the new CS:IP. The direct `9A` form's target is constant → chainable;
  `FF /3` is dynamic. The far push reuses the proven `EmitM8086PushWord` (`BlockCompiler.M8086.cs:982`) twice
  (push CS, push IP) — the same stack machinery the near `CALL` already emits.
- **`CB` far `RET`** and **`CA` far `RET imm16`**. Far return pops **IP then CS** off SS:SP (and `CA` adds
  imm16 to SP). Dynamic target (the `(CS,IP)` comes off the stack) → a dynamic exit (`EmitNormalExit`), exactly
  as the near `C3/C2 RET` arm already does (`BlockCompiler.M8086.cs:1169-1182`) but popping the CS half too.

These are the regular, non-vectoring far transfers — the far analogue of the near arms PR-D already shipped.
The only new machinery is **writing the CS field** (in addition to IP) before the exit, so the next dispatch's
`ProjectBlockKey` keys/decodes under the new segment. The far push/pop is the near push/pop applied twice.

**FALLBACK (kept on the interpreter oracle, by design — ADR 0011 §2):**
- **`CD` `INT imm8`, `CC` `INT3`, `CE` `INTO`, `CF` `IRET`** and the **divide-error `INT 0`**. These are the
  8086's **exception/vectoring** ops: they read the interrupt vector table at `0000:vector*4`, push FLAGS + CS
  + IP, clear IF/TF, and load CS:IP from the IVT (and `IRET` pops FLAGS+CS+IP and restores them). ADR 0011 §2
  explicitly keeps the exception/vectoring tail fallback (rare, high emit-cost, near-zero hot-path gain), and
  ADR 0011 OQ5 resolved the synchronous-vector question **conservatively (fallback, not emit)** for the 8086
  `INT`/`INTO`/`IRET`. **This ADR honors that** — the IVT-walking vector machinery stays the interpreter's. The
  key widening makes it *possible* to emit them later if a profile ever shows them hot (the successor block is
  now correctly keyed under the new CS), but emitting them is out of scope here.
- **`BOUND`** (range-check, can vector `INT 5`) and **`62`/`63`** — fallback (rare, vectoring).

**Rationale.** The far `JMP`/`CALL`/`RET` are the transfers real-mode code executes constantly (far calls into
DOS/BIOS, overlay returns); emitting them is the unblock the ROADMAP names. The vectoring `INT` family is
rare on the hot path and intricate (IVT walk + flag push + IF/TF clear); ADR 0011's boundary keeps it on the
oracle. The fallback valve means this split is **correctness-free**: a far `INT` that falls back is exactly the
interpreter, and (now) re-enters a correctly-keyed successor block.

**Consequences.**
- *Good:* real-mode 8086 programs run through the JIT (the headline). Far call/return — the overlay/library
  idiom — chains where the target is static (`9A`/`EA`).
- *Good:* the far arms are mechanically the near arms + a CS write + a doubled push/pop — low novelty, high
  oracle coverage.
- *Bad / accepted:* far INT/IRET stay fallback, so an interrupt-heavy 8086 workload fragments into short blocks
  around the vectoring ops. Bounded (interrupts are rare relative to instruction count) and consistent with the
  68000 TRAP / 6502 BRK/RTI fallback. A future profile can lift them (the key now supports it).

### Decision 4 — the parity + aliasing gates (the un-fakeable bar)

Two merge-precondition gates, both un-fakeable:

1. **Far-flow TomHarte parity (the oracle bar).** The 8088 TomHarte cases for the emitted far opcodes
   (`9A`/`EA`/`CB`/`CA`/`FF /3`/`FF /5`) run **through the JIT** and are **byte-identical** to the interpreter
   (registers + FLAGS + CS:IP + the far stack frame + memory + cycles), AND `FallbackEmitCount` drops by exactly
   the emitted far opcodes. A far op that is not byte-identical to the oracle does not ship (ADR 0011 §5).
2. **The far-transfer aliasing regression (the key-widening bar — fails on the old key, passes on the new).**
   A constructed program: place distinct code at `CS=0x1000,IP=0x0100` (physical `0x10100`) and at
   `CS=0x2000,IP=0x0100` (physical `0x20100`); far-`JMP`/`CALL` between them; assert each executes **its own**
   segment's code (observable via a segment-distinguishing side effect — e.g. each writes a segment-unique byte).
   **This test FAILS on the current `ushort`-IP key** (both alias to `_blocks[0x0100]`, so the second segment
   runs the first's compiled block) and **PASSES on the `(CS<<4)+IP` linear key** (distinct physical entries →
   distinct blocks). This is the un-fakeable proof that the widening is real and load-bearing — exactly the
   ADR 0011 "a gate that fails pre-fix and passes post-fix" discipline (the ADR 0017 CPM regression pattern).
3. **The overlapping-segment coherence check (linear-key-specific).** A second constructed case:
   `CS=0x1000,IP=0x0100` and `CS=0x1010,IP=0x0000` fold to the **same** physical `0x10100` and must be the
   **same** block (one compile, one cache entry). Asserts the linear key collapses aliases correctly (the
   composite-key alternative would fail this — two keys for one byte). This is the positive case that justifies
   *linear* over *composite* (Decision 1).

Plus the SAFE gate (Decision 2): the non-segmented identity regression. Together: the non-segmented CPUs are
proven unchanged, the far ops are proven oracle-identical, and the key widening is proven load-bearing (fails
on the old key) and correct on overlap.

---

## 4. The emit shape (concrete, for the Planner — signatures, not implementations)

The far arm extends `BlockCompiler.M8086.cs`, dispatched from `EmitInstruction` via a new
`IsM8086FarFlowOpcode(d)` gate (mirroring `IsM8086FlowOpcode`, `BlockCompiler.cs:650`). The arm reuses the
shipped helpers:

- **CS write:** a new `EmitM8086SetCs(ctx, value)` mirroring `EmitM8086SetIp` (`BlockCompiler.M8086.cs:1245`),
  storing the `CS` field (resolved once, the `_m8086CS` handle the compiler already holds, `:261`).
- **Far push:** `EmitM8086PushWord(ctx, pushCS)` then `EmitM8086PushWord(ctx, pushIP)` — the existing push
  helper, called twice in the far-call CS-then-IP order (ADR 0005's far frame).
- **Far pop:** `EmitM8086PopWord` (`:1002`) twice (IP then CS), setting IP then CS from the popped words.
- **Chain vs exit:** the direct `9A`/`EA` forms have a constant `(newCS,newIP)` → set CS+IP, then
  `EmitChainOrExit(ctx, projectedKey)` where `projectedKey = ((newCS<<4)+newIP)&0xFFFFF` (a compile-time
  constant — chainable across the segment change, the widening's payoff). The indirect `FF /3`,`FF /5` and the
  `CB`/`CA` RET forms are dynamic (the `(CS,IP)` is runtime) → set CS+IP from the runtime value, then
  `EmitNormalExit` (the near `RET`/`FF /4` dynamic-exit shape, `:1180`/`:1224`).

```csharp
// BlockCompiler.M8086.cs (new) — the far-flow arm, dispatched when IsM8086FarFlowOpcode(d).
private void EmitM8086FarFlow(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg);
private void EmitM8086SetCs(EmitContext ctx, ushort value);     // mirror of EmitM8086SetIp
private void EmitM8086SetCsFromStack(EmitContext ctx);          // mirror of EmitM8086SetIpFromStack
// far CALL: PushWord(CS); PushWord(IP); set CS,IP from operand. far RET: pop IP; pop CS (CA: SP+=imm16).
```

The arm sets NO flags (far JMP/CALL/RET, like near, touch no FLAGS — `INT`/`IRET`, which do, stay fallback).

---

## 5. The PR decomposition (Planner-ready handoff)

A **short arc of 2 PRs** (FF-1 → FF-2), with FF-2 splittable. Both honor the ADR 0011 global rules (parity
gate = merge precondition; honesty gate = measured deltas against frozen workloads). The 8086 measurement
apparatus (ADR 0011 PR-A) shipped in M6, so the honesty gate is satisfiable.

### FF-1 — the `(CS<<4)+IP` linear block key + the per-CPU `ProjectBlockKey` seam · size **M** · **the SAFE-gated widening, no far emit**
> **Scope:** widen the cache key `ushort → uint` across `BlockCache`/`ChainTable`/`CompiledBlock`/`JittedCpu`/
> `BlockCompiler` (the §3 Decision 2 table); add `IJitTarget.ProjectBlockKey` with the identity impl for
> 6502/Z80/68000 and `((CS<<4)+IP)&0xFFFFF` for the 8086; fold the **existing 8086 near-flow** static chain
> target through the baked code-phys base (Decision 2 note); replace the dispatcher's `ushort pc` with
> `uint key = _target.ProjectBlockKey(_inner)`.
> **Gate (un-fakeable, merge precondition):** the **key-projection identity regression** (Decision 2) — the
> 6502/Z80/68000 JIT sweeps (TomHarte/Klaus/ZEXALL/ZEXDOC/SingleStep + the chaining/SMC/`FallbackEmitCount`
> pins) are **byte-identical** before vs after; the `ProjectBlockKey == (uint)PC` unit test for each
> non-segmented CPU; the existing **8086 near-flow** parity stays green (the static-target fold is correct);
> AND the **overlapping-segment coherence** check (Decision 4 gate 3) passes. No far emit yet, so
> `FallbackEmitCount` for the far opcodes is unchanged (they are still fallback).
> **Deps:** none (the 8086 emit arms already exist; this is the cache-key infra they need). **Why first:**
> the far arms are *unsound* until the key is widened; shipping the widening alone — proven SAFE by the
> identity gate — de-risks the far emit into a pure additive PR. **Classified SAFE** (Decision 2): this PR's
> entire risk is the type widening, and the identity gate is the proof it changed nothing for the other CPUs.

### FF-2 — the far `JMP`/`CALL`/`RET` emit arms + the aliasing regression · size **M** · **the unblock**
> **Scope:** the §4 far arm — emit `9A`/`EA` (far CALL/JMP direct, chainable), `FF /3`/`FF /5` (far CALL/JMP
> indirect, dynamic), `CB`/`CA` (far RET, dynamic). The CS-write + doubled far push/pop. Un-force the
> `CpuEmitter` descriptor gate for the far-flow family (the same gate-un-force as the near family — drift #1
> of ADR 0011: the 8086 table is populated-but-forced-fallback). `INT`/`INTO`/`IRET`/`BOUND` **stay
> fallback** (Decision 3).
> **Gate (un-fakeable, merge precondition):** (1) **far-flow TomHarte-through-JIT byte-identical** parity for
> `9A`/`EA`/`CB`/`CA`/`FF /3`/`FF /5`, `FallbackEmitCount` drops by exactly those opcodes; (2) **the
> far-transfer aliasing regression** (Decision 4 gate 2) — two segments, same offset, distinct blocks: FAILS
> on the FF-1-absent `ushort` key, PASSES with the linear key; (3) a measured 8086 throughput delta on a
> far-call-bearing workload against the frozen constants (honesty gate).
> **Deps:** FF-1 (the linear key — the far arms are unsound without it). **Splittable:** if the far-indirect
> `FF /3`/`FF /5` EA-resolution proves heavy, ship FF-2a (direct `9A`/`EA`/`CB`/`CA` — chainable + the
> aliasing gate) then FF-2b (indirect `FF /3`/`FF /5`). The fallback valve keeps a partial far family correct.

**Ordering / parallelism.** Strictly serial: `FF-1 → FF-2`. FF-1 touches shared JIT infra (the `uint`
widening) and MUST land + pass its identity gate before any far arm is sound. FF-1's `CpuEmitter` edit is
zero (the key is pure JIT-runtime + the `IJitTarget` seam); FF-2's `CpuEmitter` edit is the far-family
gate-un-force (post-M5, `CpuEmitter.cs` is free — ADR 0011 §4). No collision with any other deferred item.

---

## 6. Open questions

1. **`ProjectBlockKey` on `IJitTarget` vs a `RegisterRole.CodeSegment` in the spec.** This ADR puts the
   projection on the JIT-side `IJitTarget` (Decision 1, option 1). An alternative is to mark `CS` with a new
   `RegisterRole.CodeSegment` in `M8086Spec` and have the generic projection fold any role-tagged segment.
   The `IJitTarget` route is narrower (no spec/generator change, no new role the 80286+ might redefine); the
   role route is more declarative. **Recommend `IJitTarget`** (smaller surface, JIT-local); revisit if a
   second segmented CPU (80286) lands and wants the projection generated. Owner/Planner's call at FF-1 plan
   time.

2. **The `uint` key vs a future >20-bit linear space.** `(CS<<4)+IP` is 20-bit (masked `0xFFFFF`); `uint`
   has ample headroom. The 80286 protected mode's segment base is 24-bit (a descriptor lookup) → linear
   addresses up to 24-bit; the 386's is 32-bit. `uint` covers all of these. No `ulong` needed now; flagged so
   a 386 arc revisits the projection (the *fold* changes — descriptor base, not `CS<<4` — but the key *type*
   stays `uint`).

3. **Composition with ADR 0013's `(PC, BankConfigId)` bank-config axis.** A hypothetical future board that
   both segments (8086) AND bank-switches (EMS paging) would need a key on *both* axes — the physical entry
   AND the bank-config id. ADR 0013 keys on `(PC, BankConfigId)`; this ADR keys on the segment-folded
   physical. They are orthogonal projections of "what code is at this entry"; a combined board composes them
   into `(linearPhysical, BankConfigId)`. **Not decided here** (no such board exists; 8086 EMS is a candidate,
   ROADMAP item 5a). Flagged so the bank-specialization arc, if it reaches the 8086, composes rather than
   conflicts. The `uint` linear key + ADR 0013's bank id are independent dictionary axes — composable without
   re-litigating either.

4. **Far emit + the SMC `EmitSmcGuard`/dirty-page interaction.** The intra-block SMC guard
   (`BlockCompiler.cs:702`) and the dirty-page index are physical-page-based and unchanged by the key widening
   (Decision 2.4). A far transfer that lands in a self-modified page is handled exactly as today (the guard
   trips, the dispatcher re-compiles under the new key). **Expected no interaction**; confirm with an
   SMC-into-far-target spot test at FF-2 (low risk — the mechanisms are independent, but the combination is
   new).

5. **Far INT/IRET emit (deferred, ADR 0011 OQ5).** This ADR keeps them fallback (Decision 3). The key
   widening makes emitting them *possible* (the post-vector block is correctly keyed under the new CS). If a
   profile of a real interrupt-heavy 8086 workload ever shows them hot, a follow-on can emit the IVT walk +
   flag push. **Recommend staying fallback** (ADR 0011 OQ5's resolution); owner confirm whether the deferred
   "8086 MUL/DIV + string/REP + INT/IRET emit" item (ROADMAP item 4) wants INT/IRET pulled forward now that
   the key supports it, or kept deferred.

---

*End of ADR 0019. **Decision 1:** widen the block-cache key to the **generic 32-bit linear `(CS<<4)+IP`** —
the address the decode/fetch already compute — projected per-CPU via `IJitTarget.ProjectBlockKey`; the
non-segmented CPUs' projection is the identity `(uint)PC`, so they are unchanged. **Decision 2:** the widening
is **SAFE** — `ushort→uint` + an identity projection leaves the 6502/Z80/68000 byte-for-byte unchanged
(same keys, chains, SMC, `FallbackEmitCount`), proven by the key-projection identity regression; the SMC/page
machinery is physical-page-based and untouched. **Decision 3:** emit far `JMP`/`CALL`/`RET` (the CS-load +
doubled far push/pop); keep `INT`/`INTO`/`IRET`/`BOUND` fallback per ADR 0011 §2 / OQ5. **Decision 4:** the
un-fakeable bar — far-flow TomHarte byte-identical through the JIT + the far-transfer aliasing regression
(fails on the old `ushort` key, passes on the linear key) + the overlapping-segment coherence check.
**Verdict: SAFE → hand to Planner.** A short arc — **FF-1** (the linear key + the SAFE identity gate, no far
emit) **→ FF-2** (the far arms + the aliasing gate, splittable). Designer: no UX surface (a correctly-keyed,
faster JIT is invisible except as the 8086 running real software). Planner can write the TDD plan + the two
queue rows now.*
