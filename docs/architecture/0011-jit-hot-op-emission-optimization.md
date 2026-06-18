# ADR 0011 — JIT hot-op emission optimization (the M6 "make tier-1 fast" phase)

> **Status:** **Accepted-with-changes** (Claude Architect, 2026-06-18). Promoted from *Proposed* after a
> validation pass against the now-shipped M5 (the 8086) on `main` @ `36769c6`. **The four decisions hold
> unchanged** — all 19 file:line citations in §1.2/§4 re-verified, no rot; the all-fallback baseline is
> still accurate. **The changes are additive, not structural** (logged in §0 below): (1) the 8086 is now
> SHIPPED (interpreter + all-fallback JIT) and its descriptor table is **populated-but-forced-fallback**,
> exactly the Z80's generator-effort class (a gate-flip), NOT the 68000's (a net-new table) — §4 step-3's
> prediction is confirmed and promoted to fact; (2) the 8086 has **no measurement apparatus yet** (no bench
> driver, no frozen W1/W2 workloads, no reference core, absent from the hot-op profiler), so the §5
> measurement loop + §6 ROI gate cannot be satisfied for it until that lands — this adds an explicit
> measurement-enablement dependency to the arc. The concrete per-PR arc Planner consumes is **§8 (new)**.
> The implementation is sequenced **after M5** (now landed — the 8086 arc has freed `CpuEmitter.cs`); see §4.
> **Date:** 2026-06-17 (proposed) · 2026-06-18 (accepted-with-changes)
> **Deciders:** Mark (owner). Drafted + validated autonomously by Claude Architect.
> **Supersedes / relates to:**
> - **ADR 0008** (`0008-68000-control-flow-exceptions-and-the-timing-axis.md`) — the tier-0
>   interpreter-oracle / tier-1 IL-JIT model and the **"run fast; drop to the slower exact tier only
>   where a device/the software requires it"** lever (§5/§6). This ADR is the design for *making
>   tier-1 actually fast*; it inherits ADR 0008's "exceptions/synchronous mid-instruction vectors are
>   an M6 emit item" deferrals and its timing-axis gating of 68000 cycles/sec.
> - **ADR 0009** (`0009-device-jit-contract-and-peripheral-design.md`) — the fastmem RAM/MMIO split
>   (Decision 1), the bus→JIT page-level invalidation hook a remap/bank-switch fires (Decision 2), and
>   the coarse/fine timing tier (Decision 3). Hot-op emit MUST keep emitting through the fastmem split
>   and MUST NOT break the invalidation hook. ADR 0009 Open Question 3 (per-bank block specialization)
>   is an M6 item this ADR scopes.
> - **The M6 benchmarking plan** (`docs/superpowers/plans/2026-06-17-m6-benchmarking-comparison.md`) —
>   Milestone C (§6 of that plan: "the optimized-JIT column") is the **payoff this ADR's work
>   produces**. The W1/W2/W3 workload constants are FROZEN there; this ADR's measurement loop (§5)
>   re-runs them byte-identically.
> - **The framework research** (`docs/research/emulation-framework-research.md`) — the **fastmem
>   split**, the **tiered strategy**, and the **"build the emitter once for all ISAs" (Pydgin)** theses
>   this ADR turns into a concrete emit plan. The "IL ceiling is real (Ryujinx)" risk bounds §2.
> - **ADR 0001 / 0003–0006** — the per-CPU flag models (6502 P, Z80 F + the Q/MEMPTR lifecycle, the
>   68000 CCR, the 8086 FLAGS) the emit arms must reproduce exactly.

---

## 0. Validation record (2026-06-18, post-M5) — what changed since *Proposed*

This ADR was drafted *Proposed* on 2026-06-17 against `main` with M5 in flight. It was re-validated on
2026-06-18 against `main` @ `36769c6` (M5 fully landed — the 8086 through the JIT, all-fallback). The
verdict: **the design holds; promote to Accepted-with-changes.** The record:

### 0.1 Citations re-verified (no rot)

Every load-bearing file:line in §1.2 and §4 was re-checked against the shipped code and **confirmed**:

- The hand-written emit arms are still in `BlockCompiler.Emit.cs` / `.Flow.cs` / `.Decimal.cs`; the
  `switch (d.Class)` dispatch is at `BlockCompiler.cs:227-243`; the fallback line is `BlockCompiler.cs:209`
  (`if (d.NeedsFallback) { EmitFallbackStep(ctx); return; }`); `FallbackEmitCount` at `:31`;
  `LoadByteFromBus` + the fastmem split at `:350`; `EmitChainOrExit` + its three gates (budget ≤ 0,
  `dirty.Any`, `InterruptPending`) at `:555`; `EmitSmcGuard` at `:259`; the `_regFields` map + the
  pair-view skip comment at `:96-107`; `JittedCpu.RunChain` at `:139`.
- The generator gate is still `CpuEmitter.cs:4131` (`fallback = true; endsBlock = true;`),
  **unconditional for every structured CPU, with NO per-family whitelist yet** — the "5-3b flips the hot
  ops one family at a time" comment is still a *future* plan, not shipped. `CpuEmitter.cs` is 5306 lines;
  `KeyedDescriptorLiteral` at `:4115`; the `IJitTarget` seam ("reflection handles + delegate wraps, no
  `Reflection.Emit`") at `:167`. The 68000's `JitDescriptorsByKey` is **still EMPTY** (`= new() { };`,
  0 rows) in the generated `M68000Cpu.g.cs`.
- The committed baseline (`bench/results/REPORT.md` + `comparison.json`) is unchanged and accurate:
  Z80 0.45–0.54× / 0.11–0.13×; 68000 0.72–0.75× / 0.11–0.15×; 6502 W1 Klaus **0.00×**. The hot-op
  profiling capture (`bench/hotop-profiler/hotop-profile-results.txt`) is unchanged.

### 0.2 Drift #1 — the 8086 is now SHIPPED, and it is in the **Z80's** effort class (not the 68000's)

M5 landed the 8086 as **interpreter + all-fallback JIT** (the M5.6 commit `c9eb44b`, merged to `main`).
Verified facts that the *Proposed* draft could only predict:

- The 8086 interpreter op bodies are hand-written partials (`M8086Cpu.Alu.cs` / `.Mov.cs` / `.Shift.cs` /
  `.String.cs` / …) dispatched by the generated `ExecuteX86`. **They contain ZERO `Reflection.Emit` /
  `ILGenerator`** (verified by grep) — they are the *interpreter oracle*, NOT emit arms. The 8086 is
  genuinely all-fallback at the JIT tier, exactly as §1.1's table foresaw.
- `M8086Cpu.Jit.cs` states it outright: *"In M5 every 8086 op falls back to the interpreter Step (the
  empty-Ops, NeedsFallback descriptors), so NO emitted 8086 op calls this yet"* — and it already supplies
  the `AdvanceCycles(long)` cycle-charge seam the M6 emit arm will call.
- **Critically:** the generated 8086 `JitDescriptorsByKey` is **POPULATED** (283 rows in `M8086Cpu.g.cs`),
  every row forced `NeedsFallback=true, EndsBlock=true, Ops=[]` by the same `CpuEmitter.cs:4131` gate that
  forces the Z80's 1604 rows. So the 8086's emit-enablement is the **same gate-un-force as the Z80**, NOT
  the 68000's net-new table generation. **§4 step-3's prediction ("the same gate-un-force as the Z80, family
  by family") is confirmed and promoted to fact.** The 68000 remains the *only* empty-table CPU and the only
  one needing net-new descriptor generation.
- The 8086 also carries a wrinkle §2's fallback boundary already covers: IN/OUT (E4-E7/EC-EF) are handled
  *inside* the interpreter body as open-bus/no-op (no peripheral on the 8088 corpus), so there is **no
  `IoBus` accessor** — the 8086 emit arm has no separate I/O-space `Port` family to emit (unlike the Z80).

### 0.3 Drift #2 — the 8086 has NO measurement apparatus (the binding gate the arc must front-load)

The §5 measurement loop and the §6 profiling-ranked ROI are **binding gates** on every emit PR. For the
8086, neither can be satisfied today:

- **No bench driver:** `bench/CpuEmulator.Benchmarks/Drivers/` holds exactly `Mos6502TierDriver`,
  `Z80TierDriver`, `M68000TierDriver` — no `M8086TierDriver`. There is no `"m8086"` architecture registered.
- **No frozen 8086 workloads + no reference core:** the M6 benchmarking plan's §4 workload table and §3
  reference-feasibility table both list the 8086 row as **FUTURE — gated on M5** (plan §8 Q3). The frozen
  W1/W2 constants the §5 re-measure contract requires *do not exist yet* for the 8086.
- **Absent from the hot-op profiler:** `bench/hotop-profiler/Profiler.cs` profiles only `6502` (`:125`),
  `Z80` (`:138`), `68000` (`:152`). There is no ranked 8086 hot-op list — so §6's emit-prioritization input,
  which orders the emit work, **does not exist for the 8086.**

**Consequence for the arc (§8):** an 8086 emit PR cannot pass its honesty gate (a measured before/after
delta against a frozen workload + a reference, per §5) until the 8086 measurement apparatus lands. This is
a *dependency*, not a redesign — it adds one front-loaded "8086 bench + profile enablement" PR ahead of any
8086 emit work, and it is exactly the work the M6 benchmarking plan already scoped as gated-future. It does
not touch `src/` and so is parallel-safe with the Z80/68000 emit PRs.

### 0.4 Net verdict

**Accepted-with-changes.** The four decisions are unchanged. The two drifts are both *confirmations that
sharpen the plan*, not contradictions: drift #1 makes the 8086 cheaper than the *Proposed* framing implied
(Z80-class gate-flip, not 68000-class table-gen), and drift #2 surfaces a real ordering dependency the arc
(§8) now front-loads. The rollout order (§4: Z80 → 68000 → 8086, 6502 SMC in parallel) survives intact —
the 8086 stays last, now for a *measurement* reason on top of the *shared-helper-maturity* reason §4 already
gave.

---

## 1. Context

### 1.1 The honest "before" (the committed baseline this ADR optimizes)

The M6 comparison framework (Milestone A/B + the M6 table) has shipped and committed the baseline in
`bench/results/REPORT.md` + `bench/results/comparison.json`. It is the genuine "before":

| CPU | Tier-1 emit state | Tier-1 vs **best existing** (head-to-head, same host) | Tier-1 vs **our own interpreter** |
|---|---|---|---|
| **6502** | **partially emits IL** (Load/Store/Register/ALU/RMW/Branch/JMP/JSR/RTS/Port + ADC/SBC decimal); BRK/RTI/undefined fall back | W1 Klaus **0.00×**, W2 **0.32×**, W3 **0.23×** of fake6502 (C) | W1 **0.00×**, W2 **0.49×**, W3 **0.62×** |
| **Z80** | **all-fallback** (every op → `inner.Step`, forced by the generator's `NeedsFallback` gate) | W1 **0.11×**, W2 **0.12×**, W3 **0.13×** of superzazu/z80 (C) | **0.45–0.54×** |
| **68000** | **all-fallback** (the descriptor table is *empty* — every key is the Undefined sentinel) | W1 **0.15×**, W2 **0.14×**, W3 **0.11×** of Musashi (C), in guest-MIPS (~9.7 vs 63.5) | **0.72–0.75×** |
| **8086** | (gated on M5; interpreter lands first, then all-fallback JIT) | — | — |

Two facts jump out of that table and shape the whole design:

1. **An all-fallback tier-1 is a net LOSS over its own interpreter** (Z80 0.45×, 68000 0.72× of tier-0).
   Wrapping each interpreter `Step()` in a compiled block — fetch the block, charge the budget, run a
   one-instruction `DynamicMethod` that just calls `inner.Step()`, check the chain gates, round-trip —
   costs *more* than the interpreter's own dispatch loop. This is the "0.13× of Musashi is largely
   dispatch overhead" the task names, quantified: the 68000's 0.13×-of-best is `0.72 (tier ratio) ×
   ~0.19 (our interpreter vs Musashi)`. **Hot-op emit is what turns the block from pure overhead into
   a win** — it is not a nice-to-have; without it the JIT tier is pointless for Z80/68000/8086.

2. **The 6502 W1 Klaus is 0.00× (466 K cycles/sec vs 178 M interpreter)** — a *catastrophic*
   regression, far worse than any all-fallback case, on a CPU that *does* emit IL. Klaus is the
   self-modifying-code stress: it writes test-vector bytes into code-adjacent pages constantly, so the
   block cache thrashes — `InvalidateIfDirty` evicts, `GetOrCompile` recompiles, every few
   instructions. **This is the load-bearing M6 finding the profiling pass below confirms: emit speed
   is irrelevant if the block cache is being rebuilt faster than it runs.** SMC/recompile cost is a
   first-class M6 axis, not an afterthought (§3.4).

The owner's target — **our optimized JIT ≈ the best available existing emulator** — means closing the
gap from 0.11–0.32× to something near 1.0× (or honestly reporting how close IL-on-RyuJIT can get; the
research's "IL ceiling" caveat means the realistic target for 8/16-bit guests is "competitive with the
best C interpreters/simple dynarecs," not "beat a hand-tuned native backend").

### 1.2 What the shipped code already proves (verified, file:line — load-bearing)

Read against `main` (M5.5b merged; M4.6 + the M6 framework merged). The emit machinery is mature; what
is missing is *coverage*, not *capability*.

- **The IL-emission logic is HAND-WRITTEN in the JIT assembly, NOT generated by `CpuEmitter`.** This is
  the single most important architectural fact for this ADR, and it contradicts the naive "the emit
  lands in `CpuEmitter`" framing. The per-op emit arms live in
  `src/CpuEmulator.Jit/BlockCompiler.Emit.cs` / `.Flow.cs` / `.Decimal.cs`; the dispatch is a `switch
  (d.Class)` in `BlockCompiler.cs:227-243` (`EmitInstruction`). `CpuEmitter` generates the **descriptor
  table** (the `OpcodeDescriptor` rows with `Class`/`Mode`/`BaseCycles`/`NeedsFallback`/`Ops`), the
  **per-CPU `IJitTarget` seam** (reflection handles + decode delegate; `CpuEmitter.cs:167`), the
  **decode walk**, and the **interpreter `Step` oracle** — but it emits **zero IL** (explicitly, the
  seam is "reflection handles + delegate wraps, no `Reflection.Emit`"). So "where hot-op emit lands"
  has TWO parts, and the bigger part is the JIT runtime, not the generator (§4 corrects the framing).
- **The emit-vs-fallback decision is one line:** `BlockCompiler.cs:209` —
  `if (d.NeedsFallback) { EmitFallbackStep(ctx); return; }`. An emittable op runs its class arm; a
  fallback op emits a callout to `inner.Step()`. `FallbackEmitCount` (`BlockCompiler.cs:31`) is the
  test seam that asserts "an ADC block emits 0 fallbacks; a BRK block emits 1."
- **An emitted op's IL shape is already the right shape.** `EmitInstruction` charges the opcode-fetch
  cycle up front (`EmitChargeOneCycle`, GT-F(a) ordering), increments PC, resolves operands via the
  **fastmem split** (`LoadByteFromBus` at `BlockCompiler.cs:350` branches on `Fastmem.PageBacking[page]`
  → direct `backing[PageOffset[page] + (addr & 0xFF)]` for RAM/ROM, else `bus.Read8` for MMIO), runs
  the op (e.g. `EmitAdc` in `BlockCompiler.Decimal.cs` is a one-for-one IL transcription of the
  interpreter's NMOS ADC), and exits through the chain gates. **The emitted 6502 op is already
  fastmem-direct + flag-exact.** The work is replicating this for the Z80/68000/8086 op families, not
  inventing a new emit model.
- **The descriptor carries a per-op micro-op list (`Ops: ImmutableArray<JitOp>`).** Each `JitOp(Kind,
  RegA, RegB, FlagBit, BoolArg)` (`OpcodeDescriptor.cs:46`) is the **descriptorized form of the spec's
  `OpModel`** — the SAME closed vocabulary (`"Adc"`, `"ShiftLeft"`, `"Compare"`, `"BranchIf"`,
  `"SetFlag"`, …) the `CpuEmitter` switches on to generate the interpreter body. **This is the Pydgin
  "one spec → interpreter AND emitter" bridge, already half-built:** the interpreter body is generated
  from `OpModel`; the descriptor serializes the same `OpModel` as `JitOp[]` into the table; today the
  6502 emit arms *ignore* `d.Ops` and hard-code the 6502 convention, but the data to drive a generic,
  spec-walked emitter is **already in the descriptor** (§2 is the decision about how far to lean on it).
- **The Z80 all-fallback is a GENERATOR GATE, flippable per family.** `CpuEmitter.cs:4131` forces
  `fallback = true; endsBlock = true;` for every structured CPU (any CPU with a `DecodeStructure` — Z80,
  8086, the synthetic fixtures). The comment is explicit: "5-3b flips the hot Z80 ops back to emitted IL
  one family at a time, each with its own correct cycle + flag model." So enabling Z80 emit is
  *removing a guard for whitelisted families*, not building decode.
- **The 68000 all-fallback is STRUCTURAL: `JitDescriptorsByKey` is EMPTY** (`= new() { };` in the
  generated `M68000Cpu.g.cs`). Every decoded key misses the table → `DescriptorFor` returns
  `OpcodeDescriptor.Undefined` → `NeedsFallback=true`. So the 68000 has *no per-op descriptor at all*:
  the field-grammar decode walk computes a `(1<<24)|(opIndex<<8)|size` key, but nothing populates the
  table with it. Enabling 68000 emit needs the generator to **emit field-grammar descriptor rows** (a
  bigger generator change than the Z80's gate-flip) AND the JIT to grow 68000 emit arms.
- **Block chaining is FULLY IMPLEMENTED and stack-safe.** `JittedCpu.RunChain` (`:139`) loops
  block→block through `ChainDispatch` (the 5th block-delegate arg) without a dispatcher round-trip;
  `EmitChainOrExit` (`BlockCompiler.cs:555`) emits a chain edge gated on three conditions — budget ≤ 0,
  `dirty.Any` (the SMC backstop), `InterruptPending`. `ChainTable` severs links on eviction. **So the
  cross-block-dispatch overhead the task asks to "avoid" is ALREADY avoided for emitted blocks** — but
  an *all-fallback* block ends after one instruction (`endsBlock=true`), so it never chains: it pays the
  full dispatch cost per instruction. Chaining only pays off once ops emit and blocks span multiple
  instructions (§3.1).
- **The fastmem invalidation hook (ADR 0009 Decision 2) and the SMC machinery are page-precise and
  proven.** `BlockCache.InvalidateIfDirty` (`:66`) → per-page `Evict` → `Chains.Sever`. The intra-block
  SMC guard (`EmitSmcGuard`, `BlockCompiler.cs:259`) ends a block mid-stream if it writes its own page.
  Hot-op emit must keep emitting the store's `dirty.Mark` + this guard unchanged (§3.4).

---

## 2. Decision 1 — the hot-op emit strategy: a per-(CPU,family) hand-written emit arm, gated by the descriptor, fastmem-direct, flag-exact, mirroring the interpreter oracle one-for-one

A hot guest op goes from fallback to emitted IL by:

1. **Generator side — make the op emittable:** the `CpuEmitter` produces a descriptor for that op with
   `NeedsFallback = false`, carrying its `Class`, `Mode`, `BaseCycles`, and `Ops` (the micro-op list).
   For the Z80 this is *un-forcing* the `CpuEmitter.cs:4131` gate for the whitelisted family; for the
   68000 it is *populating* `JitDescriptorsByKey` for the family's field-grammar keys (net-new
   generation); for the 8086 it is the same gate-un-forcing as the Z80 once M5's interpreter is green.
2. **JIT side — emit the IL:** `BlockCompiler.EmitInstruction` dispatches on `d.Class` to a per-CPU
   emit arm that emits the operand read (via the fastmem split), the operation (flag-exact, mirroring
   the generated interpreter `Step` body for that op), and the cycle charge (the **per-CPU cycle
   model**, NOT the 6502's). The op no longer ends the block, so it chains.

**What an emitted op looks like** (the shape is fixed by the existing 6502 arms; the per-CPU work is the
flag + cycle model):

- **Operands** come through the fastmem split (ADR 0009 Decision 1): RAM/ROM → direct backing-array
  index; MMIO → `bus.Read8/Write8` callout. The emit arm NEVER bakes a backing-array pointer across
  instructions (it re-indexes `PageBacking[page]` each access — the property ADR 0009 OQ5 relies on for
  remap-safety). The Z80's separate I/O space uses the `ioBus` callout (the `Port` arm, never fastmem).
- **Flags** are computed inline, byte-identical to the interpreter oracle. This is the per-CPU long
  pole: the 6502's `P` (NV-BDIZC) is simple; the Z80's `F` carries the documented **Q/MEMPTR (WZ)
  lifecycle** + the undocumented F3/F5 bits (the generated `Step` already computes these — the emit arm
  transcribes that logic to IL); the 68000's CCR (XNZVC, with X distinct from C) and the 8086's FLAGS
  (with AF/PF) each have their own arm. **The oracle discipline is the safety net:** an emitted op that
  doesn't match the interpreter's flag result fails the TomHarte/ZEX parity gate (§5), so emit can never
  silently diverge.
- **The cycle charge** is the descriptor's `BaseCycles` + any mode/page-cross penalty, charged via the
  per-CPU `AdvanceCycles` seam (`M68000Cpu.AdvanceCycles` exists explicitly for this; the Z80/6502 have
  theirs). For the Z80, the T-state model (4T NOP vs the 6502's 2-cycle) is why the generator gate
  exists — an op emitted through the 6502 arm would charge the wrong cycles and break tier parity. Each
  family's arm charges *its* cycles.

### The emit-vs-fallback boundary (which ops emit, which stay fallback)

This is the load-bearing scoping decision. **Emit the hot, simple, side-effect-free-on-control ops;
keep fallback for the rare, complex, or exception-capable ops** — the profiling pass (§6) orders the
"hot" half. Concretely:

- **EMIT (the hot path):** register/immediate/direct-memory ALU (ADD/SUB/AND/OR/XOR/CP/INC/DEC),
  loads/stores/moves, register transfers, shifts/rotates, the conditional + unconditional branches and
  their taken/not-taken edges, JSR/BSR/RTS/CALL/RET (stack push/pop is proven — the 6502 JSR/RTS arms
  emit it; the 68000's `-(A7)`/`(A7)+` is the same `WriteLongBus`/`ReadLongBus` the interpreter uses).
  These are 85–99% of executed instructions (§6) and have no exception surface.
- **FALLBACK (keep the interpreter):**
  - **Exception-capable / vectoring ops** — the 68000's `TRAP`/`TRAPV`/`CHK`/`DIVU`-÷0/`ILLEGAL`/`RTE`,
    address-error, privilege violation (ADR 0008 deferred the *synchronous mid-instruction vector* to
    M6 emit explicitly, but the SIMPLEST correct M6 is to keep these as interpreter callouts — the
    exception machinery is intricate and rare); the 8086's `INT`/`INTO`/`IRET`/`BOUND`, the
    divide-error INT0; the 6502's `BRK`/`RTI` (already fallback).
  - **Microcoded / loop / string ops** — the 68000's `MOVEM`/`MUL`/`DIV` (multi-cycle, table-driven
    timing), the Z80's `LDIR`/`CPIR`/block ops + the `ED`/`DD`/`FD`/`CB` prefix planes' rarer members,
    the 8086's `REP MOVS/STOS/CMPS/SCAS` string ops (a CX-counted loop — fallback is far simpler than
    emitting the loop, and they are rare in the hot path).
  - **Rare / irregular ops** — anything in the long tail of §6's histogram (sub-1% cumulative). Emitting
    them costs generator+JIT surface for ~no throughput gain. The fallback valve makes "emit the 90%,
    interpret the 10%" a *correctness-free* choice: a fallback op is always exactly the oracle.
  - **Self-modifying / bank-switch-interacting stores** stay emitted (they ARE the hot path) but keep
    the `dirty.Mark` + `EmitSmcGuard` (§3.4) — fallback is not the lever there; invalidation is.

**Rationale.** The fallback valve (ADR 0008's tier-0 oracle) makes coverage a *pure performance* dial
with no correctness risk: every op is either emitted-and-parity-proven or interpreted-and-exact. So the
boundary is drawn by **profiling-ordered ROI**, not by correctness. Exception ops are kept fallback both
because they are rare AND because emitting the exception frame/vector machinery is high-risk for
near-zero hot-path gain — exactly ADR 0008's "the synchronous vector is an M6 item" but resolved
conservatively (fallback, not emit) unless §6 ever shows an exception op hot (it won't for normal
software).

**Alternatives considered.**
- **(A) Emit EVERYTHING (no fallback).** Rejected — it forces emitting the 68000 exception model, the
  8086 string loops, and every prefix-plane rarity in IL, a huge surface for sub-1% of executed
  instructions, and it removes the safety valve that makes partial emit correctness-free. The IL ceiling
  (Ryujinx) bites hardest exactly on the complex ops; keep them in the (already-correct) interpreter.
- **(B) Emit only the single hottest op per CPU, iterate.** Rejected as the *plan* (too slow to a
  visible win) but adopted as the *rollout order* (§4) — emit by family in profiling order, re-measuring
  each step (§5).
- **(C) A generic, spec-`OpModel`-walked emitter (full Pydgin) instead of hand-written per-CPU arms.**
  This is Decision 2's subject — deferred there, not here. (Hand-written arms are the M6 default; the
  generic emitter is the cross-ISA leverage question.)

**Consequences.**
- *Good:* each emitted family is parity-proven against TomHarte/ZEX before it counts; the fallback valve
  means a half-finished family is still correct (the un-emitted members interpret). Coverage is a
  monotonic throughput dial.
- *Good:* the emit shape (fastmem operands, inline flags, per-CPU cycles, chain edge) is already proven
  by the 6502 arms — the per-CPU work is bounded and mechanical (transcribe the generated `Step` body to
  IL), not novel.
- *Bad / accepted:* the flag models are per-CPU and intricate (Z80 Q/MEMPTR, 68000 X-bit, 8086 AF/PF).
  Each arm is hand-written IL that must match the oracle exactly — the highest-bug-density code in the
  JIT. Mitigated entirely by the parity gate: a mismatch is a red test, not a shipped bug.
- *Bad / accepted:* a fallback op still ends the block (`endsBlock=true`), so a hot path containing one
  un-emitted op fragments into short blocks. The §6 ordering minimizes this (emit the ops that actually
  recur first); the residual is a known cost that shrinks as coverage grows.

---

## 3. Decision 2 — cross-ISA strategy: hand-written per-CPU emit arms NOW; a spec-`OpModel`-driven generic emitter is the deferred leverage, adopted only if the per-CPU arms prove to converge

The research's "build the emitter once for all ISAs" (Pydgin) thesis is real, and the **bridge already
exists half-built**: the descriptor's `Ops: JitOp[]` is the same `OpModel` micro-op vocabulary that
generates the interpreter. In principle one generic emitter could `foreach (var op in d.Ops)` and emit
IL per micro-op kind (`"Adc"` → the ADC IL, `"SetNZ"` → the flag IL, `"BranchIf"` → the branch IL),
giving every CPU emit "for free" from its spec.

**The decision: do NOT build the generic emitter first. Ship hand-written per-CPU arms (Decision 1) for
M6, structured so the SHARED parts are factored out; promote to a generic `OpModel`-walked emitter ONLY
after ≥2 CPUs' arms exist and reveal what actually generalizes.** The leverage is real but the *shape* of
the leverage is unknown until the second CPU's flag/cycle model is in hand — designing the generic
emitter against only the 6502 would bake in 6502 assumptions (exactly the trap the existing 6502 arms
fell into: they hard-code the accumulator convention and ignore `d.Ops`).

**Where the leverage genuinely is (factor these out as the per-CPU arms are written):**
- **The fastmem operand-access helpers** (`LoadByteFromBus`, `EmitStoreByte`, the EA resolution) are
  ALREADY CPU-agnostic (`BlockCompiler.cs:350/398`, static helpers resolved through `IAddressSpace`).
  Every CPU's loads/stores reuse them unchanged. This is the biggest shared win and it is already shared.
- **The block scaffold** — opcode-fetch cycle, PC increment, the chain edge + its three gates, the SMC
  guard, the budget check — is fully CPU-agnostic (`BlockCompiler.cs`, `EmitInstruction`/
  `EmitChainOrExit`). Every CPU reuses it. Also already shared.
- **The register-file access** is data-driven (`_regFields`, the J2 name→`FieldInfo` map built from
  `IJitTarget.RegisterNames`) — so "load register named X" is already CPU-agnostic; the 6502 vs Z80 vs
  68000 differ only in *which* names and widths. The Z80's pair-views (AF/BC/HL as composed properties
  over half-fields) are the one gap (`BlockCompiler.cs:104-107` skips them today) — a 16-bit register
  helper (the J2 finding's "5-3b owns them") is shared infrastructure the Z80 arm needs and the 68000
  (32-bit D/A regs) reuses.
- **What does NOT generalize (keep per-CPU):** the flag computation (each CPU's flag word is different
  bits with different semantics) and the cycle model (machine cycles vs T-states vs 68000 cycles vs 8086
  clocks). These are the per-CPU arms' actual content. The micro-op `Kind` vocabulary *names* the
  operation uniformly (`"Adc"`), but the *emitted IL* for `"Adc"` differs per CPU (the 6502 sets NV-BDIZC
  one way, the Z80 sets SZ5H3PNC another) — so a generic emitter would still need a per-CPU flag-emit
  table, i.e. the generic emitter is "shared operand+scaffold + a per-CPU flag/cycle plug-in," which is
  exactly what hand-written arms with factored helpers give you, with less up-front abstraction risk.

**Rationale.** The Ryujinx lesson (research §a) is that over-abstracting the codegen backend before the
targets are understood is how IL frameworks die. The proven-cheap path is: write the second CPU's arms
by hand, ruthlessly factor anything that turns out identical to the 6502's into shared helpers, and let
the generic emitter *emerge* from what's left if it's worth it. The descriptor `Ops` bridge means the
generic emitter is *possible later* with no spec change — so deferring costs nothing and de-risks a lot.

**Alternatives considered.**
- **(A) Build the generic `OpModel`-walked emitter first, derive all four CPUs from it.** Rejected as
  premature — the flag/cycle models (the actual per-CPU content) aren't captured in `OpModel` today, so
  the "generic" emitter would still need per-CPU flag plug-ins; designing those against one CPU bakes in
  its assumptions. Revisit after Z80 + 68000 arms exist (§7 OQ2).
- **(B) Keep emit fully per-CPU forever, no shared abstraction.** Rejected — the operand/scaffold/
  register-file sharing is *already real and proven*; throwing it away would duplicate the fastmem split
  four times. The decision is "share what's proven shared, defer the speculative generic layer," not
  "no sharing."

**Consequences.**
- *Good:* M6 ships a fast tier-1 per CPU without a speculative abstraction; the shared helpers (operand,
  scaffold, register file) are reused from day one; the generic emitter stays a *possible* future with
  the spec bridge intact.
- *Bad / accepted:* the flag/cycle arms are written N times (once per CPU). This is real duplication, but
  it is the *irreducible* part (the models genuinely differ) and the oracle gate makes each one
  independently verifiable. If the generic emitter later subsumes them, the hand-written arms are the
  reference the generic output is diffed against (the same "interpreter is the oracle" discipline).

---

## 4. Decision 3 — where it lands + the M5 sequencing

**The framing "the emit lands in `CpuEmitter`" is only half right, and the inaccurate half is the part
that creates the M5 conflict.** Hot-op emit lands in TWO places:

| Part | Lands in | M5 conflict? |
|---|---|---|
| **The descriptor change** (un-force the `NeedsFallback` gate for a Z80/8086 family; **populate `JitDescriptorsByKey` for a 68000 family**) | `src/CpuEmulator.Generators/CpuEmitter.cs` (the `KeyedDescriptorLiteral` path, `:4115`; the `:4131` gate) | **YES — this is the M5-owned file** |
| **The IL-emission arms** (the actual emit logic per family) | `src/CpuEmulator.Jit/BlockCompiler.Emit.cs` / `.Flow.cs` + new per-CPU partials | **NO — the JIT assembly is not M5-owned** |

So the part that conflicts with M5 is the *descriptor-generation* edit to `CpuEmitter.cs` (the largest,
highest-risk file in the repo at 5306 lines, which M5's `EmitX86DecodeWalk` arm is actively rewriting).
**Binding sequencing rule (mirrors the M5 plan's own §1 constraint and the M6 plan's §6 gate): the
hot-op emit implementation MUST NOT start its `CpuEmitter.cs` edits until M5 has landed and freed the
file.** The JIT-side emit arms (`BlockCompiler.*`) have no M5 collision and *could* be prototyped against
the already-emitting 6502 earlier, but the descriptor change that activates them is post-M5.

**The per-CPU rollout order** (each step: generator descriptor change + JIT emit arm + a §5 re-measure):

1. **Z80 first.** Lowest-friction: the descriptor change is *un-forcing* an existing gate
   (`CpuEmitter.cs:4131`) per family, not net-new generation; the decode + descriptor table already
   exist (just forced-fallback); the Z80 is the most-tested non-6502 core (ZEXALL green). The Z80's
   flag model (Q/MEMPTR) is the hardest flag arm, but doing it first means the register-pair helper +
   the structured-CPU emit path get built against the best-validated core. **This also proves the
   cross-ISA path** (the 6502 emits; proving a *second* CPU emits is the M6 thesis's real test).
2. **68000 second.** Higher-friction generator work (populate `JitDescriptorsByKey` from the
   field-grammar — net-new descriptor generation, not a gate flip), but the biggest absolute win (it is
   the most-fallback CPU, 0.72× of its own interpreter, so the dispatch overhead it sheds is largest)
   and the data-axis ALU/MOVE families are huge and regular. Cycles/sec stays gated on the M4.5d-2
   timing axis (ADR 0008 §6); guest-MIPS is the headline (it already is in the baseline).
3. **8086 last.** Gated on M5's interpreter being green first; then the descriptor change is the same
   gate-un-force as the Z80, family by family. The 8086's variable-length decode + segmentation make its
   EA-resolution arm the most complex, so it benefits from the Z80/68000 arms' shared helpers existing.
4. **6502 cleanup (parallel, low-priority):** close the W1 Klaus 0.00× SMC-thrash hole (§3.4) and emit
   the few remaining 6502 fallbacks (BRK/RTI stay fallback — rare + exception-capable). The 6502 already
   emits; its M6 work is the SMC/recompile axis, not coverage.

### 3.4 The SMC / recompile-cost axis (the 6502 W1 finding — a co-equal M6 lever)

The 6502 W1 Klaus 0.00× regression proves that **emit coverage is necessary but not sufficient**: if the
block cache is invalidated and recompiled faster than blocks execute, emit speed is irrelevant. M6 must
also address recompile cost where SMC/bank-switching is hot:

- **Quantify first** (a §6-adjacent profiling item): instrument `BlockCache.CompileCount` /
  `InvalidateIfDirty` eviction count per workload. Klaus is the SMC stress; the kernels (W2/W3) are
  SMC-free (their stores hit data pages, not code pages). The fix's value is workload-dependent.
- **Candidate levers (design, not yet decided — §7 OQ3):** (a) a recompile-cost cap / "don't re-JIT a
  page that's invalidated N times per M instructions — fall back to interpreting that page" (the
  device-tier lever of ADR 0009 Decision 3, applied to SMC pages); (b) per-bank block specialization
  (ADR 0009 OQ3 — key blocks on `(PC, bankState)` so a re-entered bank reuses compiled blocks); (c) a
  cheaper validity check than full eviction (a per-block checksum vs the coarse dirty-page evict — the
  research's "per-block checksum" option, costlier per-block but avoids re-decode). **This ADR flags the
  axis and the candidates; the lever is chosen against measured recompile counts in the implementation
  phase.**

**Consequence.** The "our JIT ≈ best available" headline is reachable on the *compute* workloads (W2/W3)
via emit coverage; the *SMC-heavy* W1 needs the recompile-cost lever too. The comparison table (§5) will
show this honestly per workload — emit coverage lifts W2/W3 first; W1 lifts only when both coverage AND
the SMC lever land.

---

## 5. Decision 4 — the measurement loop: every emit step re-runs the frozen workloads against the same reference cores, filling the comparison table's "optimized JIT" column honestly

The M6 framework (`bench/`, the committed REPORT.md + comparison.json) is the measurement apparatus;
this ADR's work is its Milestone C ("the optimized-JIT column"). The loop is **binding**:

1. **Freeze is law.** The W1/W2/W3 window constants (`KlausExpectedCycles`, `ArithKernelCycleCap`,
   `SieveCycleCap`, `Z80W1WindowTStates`, `Z80W2/W3CycleCap`, `M68000W2CycleCap` +
   `M68000W1/W2InstructionCap`) and the kernel bytes are FROZEN (the M6 plan §6 / `bench/README.md`
   "Baseline → re-measure"). Each emit step re-runs the IDENTICAL bytes; a `git diff` of the constants
   must show no change, or the before/after comparison is void.
2. **Re-measure per family, not just per CPU.** Each emitted family is a measurable delta on its hot
   workload (the §6 ordering tells you which workload each family dominates — e.g. emitting Z80 `LD`
   moves Z80-W3 most; emitting 68000 `SUBQ`/`Bcc` moves m68k-W2 most). Commit the delta; the comparison
   table's "our Tier-1" column climbs visibly.
3. **The table already has the columns.** `ComparisonTableWriter` renders per-CPU × per-workload:
   **best existing (head-to-head, measured here) · our Tier-0 · our Tier-1**, with the **Tier-1-vs-best
   ratio** as the headline and the head-to-head/cited distinction (‡ vs [cited]). The "optimized JIT"
   re-measure just re-runs and the ratio column updates. Guest-MIPS is the cross-CPU-comparable unit;
   cycles/sec is the per-CPU sanity check (68000 cycles/sec stays timing-axis-caveated).
4. **Honesty gates (inherited, binding):** commit ONLY measured data (no fabricated "after" numbers);
   a CPU/family not yet emitted shows ≈ its all-fallback baseline (0.11–0.75×) honestly; the report
   links the baseline commit so a reader reproduces the subtraction; the parity gate (TomHarte/ZEX
   green) is a *merge precondition* for every emitted family (a family that isn't byte-identical to the
   oracle does not ship, regardless of its speed).
5. **The target, stated as a measurable:** drive the **Tier-1-vs-best-existing ratio** from today's
   0.11–0.32× toward 1.0×. Realistic checkpoints (the IL-ceiling caveat): the compute kernels (W2/W3)
   should reach a meaningful fraction of the best C interpreter/dynarec (the honest "≈ best available"
   is "same throughput class," not necessarily ≥ a hand-tuned C core); W1 Klaus needs the SMC lever
   (§3.4) to move at all. Each re-measure says exactly where we are.

---

## 6. The profiling pass — ranked hot-op lists per CPU (the emit-prioritization input)

**Method.** A throwaway harness (`bench/hotop-profiler/`, NOT in any runtime/test graph) runs the
**tier-0 interpreter** over each frozen benchmark workload, identifies each instruction at its live PC
via the per-CPU generated decode (`IJitTarget.Decode` → `DescriptorFor` → `Mnemonic` for 6502/Z80; the
field-grammar mask/match scan over the operword for the 68000, whose descriptor table is empty), and
counts mnemonic frequency over 20,000,000 instructions per workload. The ranked list orders the emit
work: **emit the ops at the top of the cumulative-% curve first.** (The harness is reproducible:
`dotnet run -c Release --project bench/hotop-profiler`; W1 streams skip-with-note when their fetched
binary is absent.)

### 6502 (the partially-emitting CPU — top 8 per workload)

| Rank | W1 Klaus | W2 arith-kernel | W3 sieve-kernel |
|---|---|---|---|
| 1 | **BNE** 16.2% | **ADC** 22.2% | **LDA** 25.5% |
| 2 | **PHP** 16.2% | **BNE** 11.2% | **STA** 19.6% |
| 3 | **CMP** 15.7% | **STA** 11.1% | **ADC** 14.2% |
| 4 | **LDA** 10.0% | **CLC** 11.1% | **CLC** 7.1% |
| 5 | **PLA** 8.7% | **SEC** 11.1% | **CMP** 6.0% |
| 6 | **AND** 8.6% | **SBC** 11.1% | **BCC** 5.8% |
| 7 | **PLP** 8.1% | **EOR** 11.1% | **LDY** 5.4% |
| 8 | **ADC** 3.9% | **DEY** 11.1% | **JMP** 5.4% |
| cum top-8 | 87% | 100% | 89% |

> 6502 note: the top ops (LDA/STA/ADC/CMP/AND/EOR/BNE/BCC/JMP/INC) ALREADY emit IL — so the 6502's M6
> work is NOT coverage; it is the **W1 SMC-thrash** hole (§3.4) and the small fallback tail (BRK/RTI stay
> fallback). PHP/PLP/PLA are unusually hot in W1 (Klaus's flag-save/restore test pattern) and DO emit
> (Register/stack class). W2/W3 are SMC-free, which is why their JIT ratios (0.49×/0.62×) already beat
> their all-fallback peers — the emit is working there; the cache thrash is W1-specific.

### Z80 (all-fallback — every op below is currently `inner.Step`; top 8 per workload)

| Rank | W1 ZEXDOC-prefix | W2 arith-kernel | W3 sieve-kernel |
|---|---|---|---|
| 1 | **LD** 32.5% | **ADD** 19.9% | **LD** 34.8% |
| 2 | **PUSH** 10.7% | **SUB** 19.9% | **OR** 12.6% |
| 3 | **POP** 10.7% | **INC** 19.9% | **JR** 12.2% |
| 4 | **JP** 8.4% | **DEC** 19.9% | **ADD** 9.4% |
| 5 | **DEC** 5.1% | **DJNZ** 19.9% | **SBC** 7.6% |
| 6 | **INC** 5.1% | LD 0.3% | **INC** 5.6% |
| 7 | **RRCA** 5.0% | JP 0.2% | **PUSH** 5.2% |
| 8 | **XOR** 4.6% | — | **POP** 5.2% |
| cum top-8 | 86% | 100% | 92% |

> Z80 emit order (the §4 step-1 families, by ROI): **LD** (one third of all Z80 instructions — the single
> highest-value family, covers loads/stores/moves/16-bit-pair loads), then **ADD/SUB/INC/DEC/OR/XOR/AND/CP**
> (the ALU + flag core), then the **branch/jump/call family** (JP/JR/CALL/RET/DJNZ — DJNZ is 20% of W2
> alone, the hot counted-loop edge), then **PUSH/POP** (21% of W1 combined). RRCA/RLCA (the accumulator
> rotates, ~7% of W1) are cheap to emit. The block-op/prefix-plane long tail (LDIR/CPIR/ED-CB rarities)
> stays fallback (§2).

### 68000 (all-fallback — the descriptor table is EMPTY; top per workload)

| Rank | W1 mixed-kernel | W2 arith-kernel |
|---|---|---|
| 1 | **MOVE** 11.1% | **SUBQ** 39.9% |
| 2 | **Bcc/BSR** 11.1% | **Bcc** 20.0% |
| 3 | **ADDI** 11.1% | **ADDQ** 20.0% |
| 4 | **LSL** 11.1% | **EORI** 20.0% |
| 5 | **ADD** 11.1% | MOVEQ / MOVE <0.1% |
| 6 | **ADDQ** 11.1% | — |
| 7 | **RTS** 11.1% | — |
| 8 | **EORI** 11.1% | — |
| 9 | **DBcc** 11.1% | — |
| cum | ~100% | ~100% |

> 68000 emit order (the §4 step-2 families): **MOVE/MOVEQ** (the single largest 68000 family by ISA and
> the W1 hot op), then the **integer ALU** (ADD/SUB/ADDQ/SUBQ/ADDI/SUBI/AND/OR/EOR/EORI/CMP — SUBQ alone
> is 40% of W2, ADDQ/SUBQ the counted-loop edges), then **shifts** (LSL/LSR/ASL/ASR/ROL/ROR), then the
> **branch/call/return family** (Bcc/BSR/DBcc/JMP/JSR/RTS — DBcc is the hot counted-loop branch). The
> exception/microcoded tail (TRAP/CHK/MOVEM/MUL/DIV/RTE) stays fallback (§2). **The generator gap is the
> long pole here:** unlike the Z80 (un-force a gate), the 68000 needs `JitDescriptorsByKey` *populated*
> from the field-grammar — the field-op → `(opIndex,size)` key the decode walk already computes must get
> a matching descriptor row generated for each emittable family.

**The cross-cutting profiling finding:** across all three CPUs and every workload, the **top ~8
mnemonics cover 86–100% of executed instructions** (the long tail is 14% at most, and on the kernels
the top 5–6 are ~100%). This is the whole justification for the emit-vs-fallback boundary (§2): emitting
a handful of families per CPU captures essentially all execution; the rare/complex/exception ops in the
tail can stay interpreter-fallback with negligible throughput cost. The emit effort is small and
high-leverage — exactly because real code is dominated by a few op families.

---

## 7. Open questions

1. **The "≈ best available" target is asymmetric per workload (the W1 SMC finding).** Emit coverage
   alone reaches "best class" on the compute kernels (W2/W3) but NOT on the SMC-heavy W1 (Klaus 0.00×,
   and any future bank-switching machine). Is the M6 success bar "compute-kernel parity" (achievable
   with coverage), or does it require the SMC/recompile-cost lever (§3.4) too? The owner's "≈ best"
   headline should state which workloads it claims — recommend: coverage first (lift W2/W3 visibly),
   then the SMC lever as a named follow-on with its own re-measure. **Owner's call on the bar.**

2. **When (if ever) to promote the hand-written arms to a generic `OpModel`-walked emitter (Decision
   2).** The spec bridge (`d.Ops`) makes it possible with no spec change, but the flag/cycle models don't
   live in `OpModel` today. After the Z80 + 68000 arms exist, is the shared surface large enough to
   justify a generic emitter (with per-CPU flag/cycle plug-ins), or do the arms stay hand-written with
   factored helpers? Resolve empirically after §4 step-2, not now.

3. **The SMC/recompile-cost lever choice (§3.4).** Three candidates — (a) a per-page recompile-cost cap
   that falls back to interpreting a thrashing page; (b) per-bank block specialization (ADR 0009 OQ3,
   keys blocks on `(PC, bankState)`); (c) per-block checksum validity vs coarse dirty-evict. The choice
   needs measured recompile/eviction counts per workload (a profiling item adjacent to §6). Likely (a)
   for SMC + (b) for bank-switching are complementary; confirm against numbers in the implementation
   phase.

4. **68000 cycles/sec gating (inherited from ADR 0008 §6).** The 68000's emitted ops can charge cycles
   via `AdvanceCycles`, but the full cycle-exact axis (M4.5d-2 prefetch/timing) is partial on `main`. So
   an emitted 68000 op is cycle-trustworthy only for the cycle-exact families. The re-measure leads with
   guest-MIPS (as the baseline already does); the cycles/sec column stays caveated until M4.5d-2
   completes. No new decision — just a flagged dependency the re-measure honors.

5. **Exception-op emit (ADR 0008's deferred "synchronous mid-instruction vector is an M6 item").** This
   ADR resolves it conservatively: keep exception-capable ops (68000 TRAP/CHK/÷0, 8086 INT, 6502
   BRK/RTI) as interpreter fallback (rare + high-risk to emit, §2). If a future profile ever shows an
   exception op hot (unlikely for normal software), revisit. Is "fallback, don't emit" the accepted
   resolution of ADR 0008's deferral, or does the owner want the synchronous vector emitted? Recommend
   fallback; **owner confirm.**

6. **Z80 register-pair emit helper.** The Z80's AF/BC/DE/HL/IX/IY pair-views are *properties* over
   half-field bytes (not fields), so the J2 `_regFields` map skips them (`BlockCompiler.cs:104-107`). The
   Z80 ALU/LD arms need a 16-bit pair read/write helper (the J2 finding's "5-3b owns them"). This is
   shared infra the 68000 (32-bit regs) and 8086 (16-bit regs + the AX/AL/AH overlap) partly reuse —
   confirm the helper's shape generalizes across the three when the Z80 arm is written (it informs
   Decision 2's "what generalizes").

---

## 8. The M6 PR arc (the per-PR breakdown Planner consumes)

The concrete, ordered PR list that turns the four decisions + the §4 rollout + the §6 ROI ranking into
shippable units. **Each emit PR is one (CPU, op-family-bundle)** and carries: scope, its honesty gate
(measured-data-only, §5), its dependencies, and a rough size. Sizing is **S** (≈1 focused session,
one family, factored helpers reused), **M** (a family bundle + a new shared helper or a generator edit),
**L** (a new generator code-path or a new measurement subsystem).

**Two global rules bind every PR:**
- **Parity gate (merge precondition, §5):** the family's TomHarte/ZEX(ALL/DOC)/SingleStep slice is green AND
  `FallbackEmitCount` (`BlockCompiler.cs:31`) drops by exactly the emitted opcodes — a byte-for-byte oracle
  match, or it does not ship. A half-finished family is still correct (un-emitted members interpret), so
  PRs may be split finer than the bundles below without correctness risk.
- **Honesty gate (§5):** every PR commits a *measured* before/after delta on the family's hot workload
  against the unchanged frozen constants (`git diff` of the constants shows no change), and the
  `comparison.json`/REPORT.md "our Tier-1" column moves on real numbers only.

### PR-0 — shared register/operand infrastructure (the un-blocker)  ·  size **M**  ·  no CPU emit
> **Scope:** the cross-CPU helpers Decision 2 names as "already-proven-shared, one gap": the **16-bit/wide
> register read-write helper** for field-less pair-views (OQ6 — `BlockCompiler.cs:96-107` skips Z80 AF/BC/DE/
> HL/IX/IY today; the 68000 D/A 32-bit regs and the 8086 AX/AL/AH overlap reuse the same shape). No flag or
> cycle logic — pure register-file plumbing on the JIT side only (`BlockCompiler.*`, no `CpuEmitter.cs`).
> **Gate:** a JIT unit test reads/writes each Z80 pair and round-trips; no measured-throughput claim (it
> emits no op yet). **Deps:** none — `BlockCompiler.*` has no M5 collision, so PR-0 can start immediately.
> **Why first:** PR-1 (Z80 LD) is blocked on it; building it standalone keeps PR-1 a pure emit PR and gives
> Decision 2 its first "what generalizes" data point. *(Optional: fold into PR-1 if the helper proves trivial;
> kept separate here so Planner can parallelize it against PR-A.)*

### PR-1 — **Z80 `LD` family** (loads/stores/moves/16-bit-pair loads)  ·  size **M**  ·  **THIS IS PR-1**
> **Scope:** un-force the `CpuEmitter.cs:4131` gate for the Z80 `LD` family (the generator's first
> whitelist entry) + the JIT emit arm for `LD` r/r, r/n, r/(HL), (HL)/r, (nn) loads/stores, and the 16-bit
> pair loads. Charges the Z80 **T-state** model (not the 6502's), operands through the fastmem split,
> flags: `LD` touches no flags (the *easiest* flag arm — deliberately first), so this PR proves the
> structured-CPU emit path end-to-end *without* paying the Q/MEMPTR tax yet.
> **Gate:** Z80 ZEXALL/ZEXDOC `LD` cases green; `FallbackEmitCount` drops for every emitted `LD` opcode;
> measured delta on **Z80-W3** (LD = 34.8% of W3, the single highest-value family — §6) committed.
> **Deps:** PR-0 (pair helper). **Size note:** M not S — it's the first structured-CPU arm, so it eats the
> one-time cost of wiring the gate-whitelist mechanism + the structured decode path.
> **Why PR-1:** highest ROI (one third of all Z80 instructions), lowest risk (no-flag family), on the
> best-validated non-6502 core (ZEXALL green), and it **proves the cross-ISA thesis** — a *second* CPU
> emitting IL is the real M6 test (the 6502 already emits; the design stands or falls on the second core).

### PR-2 — **Z80 ALU + flag core** (ADD/ADC/SUB/SBC/AND/OR/XOR/CP/INC/DEC)  ·  size **L**
> **Scope:** the Z80 8-bit ALU family + **the Z80 flag model** — SZ5H3PNC including the documented **Q/MEMPTR
> (WZ) lifecycle** + the undocumented F3/F5 bits (the generated `Step` already computes these; the arm
> transcribes that logic to IL). This is the highest-bug-density code in the JIT (§2 consequence); the
> oracle gate is the entire safety net.
> **Gate:** ZEXALL ALU + the flag-exact subset green (Q/MEMPTR is exactly what ZEXALL stresses);
> measured delta on **Z80-W2** (ADD/SUB/INC/DEC = ~80% of W2 — §6). **Deps:** PR-1 (shares the structured
> emit path; reuses the operand helpers). **Size L:** the flag arm is the per-CPU long pole.

### PR-3 — **Z80 branch/jump/call/counted-loop** (JP/JR/CALL/RET/DJNZ + RST + PUSH/POP)  ·  size **M**
> **Scope:** the control-flow family + the stack family. `DJNZ` is the hot counted-loop edge (20% of W2);
> JP/JR/CALL/RET are 20%+ of W1; PUSH/POP are 21% of W1 combined (§6). Reuses the proven JSR/RTS stack-emit
> shape from the 6502 arms (`BlockCompiler.Flow.cs`). This is where Z80 blocks finally **span multiple
> instructions and chain** (the chaining payoff of §3.1 — a fallback op ends the block, so until the hot
> branches emit, blocks stay short).
> **Gate:** ZEX control-flow cases green; measured delta on **Z80-W1** (PUSH/POP/JP/CALL/RET dominate W1).
> **Deps:** PR-1, PR-2. **Closes the Z80:** the block-op/prefix-plane long tail (LDIR/CPIR/ED-CB rarities)
> stays fallback by design (§2) — the Z80 is "done" for M6 at ~the §6 cumulative-86–100% line.

### PR-4 — **68000 descriptor generation + MOVE/MOVEQ emit**  ·  size **L**  ·  generator-heavy
> **Scope:** the 68000's structural blocker first — **populate `JitDescriptorsByKey` from the field-grammar**
> (net-new generation in `CpuEmitter.cs`: the field-op → `(opIndex,size)` key the decode walk already
> computes must get a matching descriptor row emitted, for the emittable families) — then the JIT emit arm
> for **MOVE/MOVEQ** (the single largest 68000 family + the W1 hot op, §6). Charges 68000 cycles via
> `M68000Cpu.AdvanceCycles`; the cycles/sec headline stays guest-MIPS-led (OQ4 — the timing axis is partial).
> **Gate:** 68000 SingleStep MOVE cases green; measured delta on **m68k-W1** (MOVE = 11% of W1) reported in
> **guest-MIPS** (the cycle-axis-independent metric the baseline already leads with). **Deps:** none on the
> Z80 PRs for correctness, but **strongly prefer after PR-1/PR-2** so the shared wide-register helper (PR-0)
> + the structured emit path are proven first. **Size L:** the descriptor-table generation is the biggest
> *generator* change in M6 (vs the Z80/8086 gate-flips) — this is the 68000's one-time cost.

### PR-5 — **68000 integer ALU** (ADD/SUB/ADDQ/SUBQ/ADDI/SUBI/AND/OR/EOR/EORI/CMP + CCR)  ·  size **L**
> **Scope:** the 68000 ALU families + **the CCR flag model** (XNZVC, with **X distinct from C** — the per-CPU
> wrinkle). `SUBQ` alone is 40% of W2; ADDQ/SUBQ are the counted-loop edges (§6). **Gate:** SingleStep ALU
> cases green (X-bit exactness is the focus); measured delta on **m68k-W2** (SUBQ/ADDQ/EORI ≈ 80% of W2).
> **Deps:** PR-4 (the descriptor-generation path + the MOVE arm establish the 68000 emit shape). **Size L:**
> the CCR X-bit arm is the 68000's flag long pole.

### PR-6 — **68000 shifts + branch/call/counted-loop** (LSL/LSR/ASL/ASR/ROL/ROR + Bcc/BSR/DBcc/JMP/JSR/RTS)  ·  size **M**
> **Scope:** the shift family + the control-flow family. `DBcc` is the hot counted-loop branch; Bcc/BSR/RTS
> are ~33% of W1 combined (§6). `-(A7)`/`(A7)+` stack push/pop is the same `WriteLongBus`/`ReadLongBus` the
> interpreter uses (§2). **Gate:** SingleStep shift+control cases green; measured delta on m68k-W1 (Bcc/BSR/
> DBcc/RTS dominate) + m68k-W2 (Bcc). **Deps:** PR-4, PR-5. **Closes the 68000:** TRAP/CHK/÷0/MOVEM/MUL/DIV/
> RTE stay fallback by design (§2 + OQ5).

### PR-A — **8086 measurement enablement** (the §0.3 dependency)  ·  size **L**  ·  bench-only, no `src/`
> **Scope:** the measurement apparatus the §5 loop + §6 ROI require for *any* 8086 emit PR, none of which
> exists post-M5 (§0.3): (1) an `M8086TierDriver` registered as `"m8086"` (mirrors `M68000TierDriver`); (2)
> frozen 8086 **W1/W2 workloads** + their pinned constants (the M6 benchmarking plan §4 8086 row, currently
> FUTURE); (3) the 8086 hot-op **profiler arm** in `bench/hotop-profiler/Profiler.cs` (the ranked 8086 list
> that orders PR-B/C/D — it does not exist yet); (4) the 8086 reference-core decision (head-to-head vs cited
> — plan §3 / §8 Q3). **Touches only `bench/` + `bench/hotop-profiler/` — NO `src/`,** so it is parallel-safe
> with every Z80/68000 emit PR and can run anytime after M5 (i.e. now).
> **Gate:** a committed 8086 all-fallback baseline row in REPORT.md/`comparison.json` (the honest "before"
> the 8086 emit PRs subtract from) + a committed ranked 8086 hot-op list. **Deps:** none. **Why a PR, not a
> footnote:** without it, PR-B's honesty gate (a measured before/after on a frozen 8086 workload) is
> unsatisfiable. This is the binding precondition drift #2 surfaced.

### PR-B / PR-C / PR-D — **8086 emit** (MOV → ALU+FLAGS → branch/call)  ·  size **M / L / M**
> **Scope:** the 8086 emit arms, **family-ordered by PR-A's ranked list** (not yet captured — the order
> below is the *expected* shape, to be confirmed against PR-A's output). The 8086's enablement is the **same
> gate-un-force as the Z80** (drift #1: its 283-row descriptor table is populated-but-forced-fallback, not
> empty), so the generator side is a whitelist entry per family, NOT net-new table generation.
> - **PR-B — 8086 `MOV` family** (size **M**): the EA-resolution arm is the 8086's complexity pocket
>   (variable-length decode + segmentation: `M8086Cpu.Ea.cs` / `.Mov.cs` are the oracle). No flags on MOV —
>   the same "prove the path on a no-flag family first" pattern as Z80 PR-1. No `Port` family (drift #1: IN/
>   OUT are interpreter-internal open-bus, no `IoBus`). **Deps:** PR-A; benefits from PR-0's wide-register
>   helper (AX/AL/AH overlap) + the Z80/68000 EA experience.
> - **PR-C — 8086 ALU + FLAGS** (size **L**): the integer ALU + the **FLAGS model** (CF/PF/AF/ZF/SF/OF —
>   the AF/PF pair is the 8086's per-CPU wrinkle; parity over the low 8 bits; the TomHarte flags-mask
>   excludes the undefined-flag fallout). `M8086Cpu.Alu.cs` is the oracle. **Deps:** PR-B.
> - **PR-D — 8086 branch/call/return** (Jcc/JMP/CALL/RET/LOOP + PUSH/POP) (size **M**). **Deps:** PR-B, PR-C.
> - **Stays fallback by design (§2):** `INT`/`INTO`/`IRET`/`BOUND`, the divide-error INT0, and the
>   `REP MOVS/STOS/CMPS/SCAS` string loops (a CX-counted loop — far simpler to interpret, and rare).

### PR-S — **the 6502 SMC/recompile-cost lever** (the W1 Klaus 0.00× hole)  ·  size **L**  ·  parallel, the co-equal lever
> **Scope:** §3.4 — the 6502 already emits, so its M6 work is NOT coverage but the **recompile-cost axis**.
> (1) Instrument `BlockCache.CompileCount` / `InvalidateIfDirty` eviction counts per workload (the §3.4
> quantify-first step). (2) Implement the chosen lever (OQ3 — likely (a) a per-page recompile-cost cap that
> falls back to interpreting a thrashing page, the ADR 0009 Decision 3 device-tier lever applied to SMC
> pages). **Gate:** measured W1 Klaus delta — this is the *only* PR expected to move W1 (coverage moves
> W2/W3; W1 needs this — §3.4 / OQ1). **Deps:** none (orthogonal to all emit PRs — different file surface).
> **Why parallel-tracked:** it is a co-equal M6 lever, not a follow-on; it can land any time and is the
> answer to "why is the JIT slower than the interpreter on SMC-heavy code."

### Ordering, dependencies, and parallelism (the graph Planner schedules against)

```
PR-0 (shared regs) ─┬─> PR-1 (Z80 LD) ─> PR-2 (Z80 ALU+flags) ─> PR-3 (Z80 branch/stack)   [Z80 done]
                    └─> PR-4 (68000 descr-gen + MOVE) ─> PR-5 (68000 ALU+CCR) ─> PR-6 (68000 shift+branch)  [68000 done]
PR-A (8086 bench+profile, bench-only) ─> PR-B (8086 MOV) ─> PR-C (8086 ALU+FLAGS) ─> PR-D (8086 branch)   [8086 done]
PR-S (6502 SMC lever) ───────────────────────────────────────────  [orthogonal, any time]
```

- **The critical path is the Z80 chain** (PR-0→1→2→3): it proves the cross-ISA thesis and unblocks the
  shared-helper maturity the 68000 + 8086 arms reuse. **PR-1 is the first emit PR and the headline checkpoint.**
- **PR-4 (68000) can start in parallel with PR-2/PR-3** once PR-0 + PR-1 prove the structured path — its
  descriptor-generation work is independent generator surface. But the **`CpuEmitter.cs` serialization rule
  (§4) still binds:** the generator-side edits of the Z80 gate-flips and the 68000 descriptor-gen both touch
  `CpuEmitter.cs`, so they must be sequenced (or carefully partitioned) to avoid the same-file collision the
  ADR warns about for M5 — Planner serializes the `CpuEmitter.cs`-touching steps even when their JIT arms
  are independent.
- **PR-A + PR-S touch NO `src/` generator code** (bench-only / `BlockCache` + JIT-runtime), so they are the
  safe parallel work — dispatch them alongside the Z80 chain to keep the team busy without `CpuEmitter.cs`
  contention.
- **Recommended dispatch order for the first checkpoint:** PR-0 + PR-A in parallel → PR-1 (the thesis proof)
  → checkpoint with the owner on OQ1 (the W1 success bar) + OQ2 (generic-emitter timing) before committing
  to the full Z80→68000→8086 sweep.

---

*End of ADR 0011. Decision 1 (per-CPU hand-written emit arms, descriptor-gated, fastmem-direct,
flag-exact, mirroring the oracle; emit the hot 86–100%, fallback the rare/complex/exception tail).
Decision 2 (hand-written arms now with factored shared helpers; the generic OpModel-driven emitter is
deferred leverage, promoted only after ≥2 CPUs' arms converge). Decision 3 (emit lands in BOTH
CpuEmitter — descriptor change, M5-conflicting, post-M5 — AND BlockCompiler — the IL arms, no M5
conflict; rollout order Z80 → 68000 → 8086, with the 6502 SMC-thrash hole as a co-equal lever).
Decision 4 (every emit step re-runs the frozen W1/W2/W3 against the same reference cores, filling the
comparison table's optimized-JIT column honestly, parity-gated). The profiling pass (§6) ranks the hot
ops per CPU — the emit-prioritization input. **Status: Accepted-with-changes (§0) — validated against the
shipped M5; the 8086 is now a Z80-class gate-flip (drift #1) but needs measurement enablement first (drift
#2 → PR-A).** The **M6 PR arc is §8** — the ordered, dependency-graphed, parity-+honesty-gated, size-estimated
PR list Planner consumes; **PR-1 = Z80 `LD`** (highest ROI, no-flag, proves the cross-ISA thesis). Designer:
no UX surface (a faster JIT is invisible except as throughput). M5 has freed CpuEmitter.cs — Planner can
schedule §8 now.*
