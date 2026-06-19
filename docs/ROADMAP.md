# Roadmap

This page tracks what has shipped (the M1–M6 milestone arc) and what is **deferred** or a **candidate**
for future work. It is the single forward-looking index; the per-milestone detail lives in the
[architecture decision records](architecture/) (ADRs) and the [user guide](user-guide/README.md).

> **A note on prioritization.** The ordering of the deferred items below is **not committed** — it is a
> menu, not a plan. Which follow-on (if any) comes next is the **owner's call**. Each item is tagged
> **[deferred]** (a scoped, named follow-on the M6 arc explicitly left out) or **[candidate]** (a looser
> idea worth recording). Nothing here is scheduled.

---

## Shipped — the M1–M6 arc

| Milestone | What it delivered |
|---|---|
| **M1 — core + 6502** | `CpuEmulator.Core` contracts, the Roslyn source generator (typed C# spec → cycle-exact interpreter + disassembler + single-instruction assembler), the **MOS 6502** (151/151 documented opcodes, cycle-exact, TomHarte 1,510,000 cases + Klaus), the device layer (scheduler, interrupt lines, `SimpleUart`, `IntervalTimer`), and the CPU-agnostic monitor + REPL + host. |
| **M2 — the IL-JIT tier** | `CpuEmulator.Jit` (`JittedCpu` + `BlockCompiler`): the dual-tier, provably-equivalent execution path — PC-keyed block cache, the RAM/ROM fastmem split (MMIO bus-callout), block chaining, per-page SMC invalidation, and emitted 6502 ops including the decimal ADC/SBC arms. Validated to full parity (TomHarte through the JIT, the differential fuzzer, Klaus cycle-exact). |
| **M3 — Zilog Z80** | The framework's 2nd ISA — the full instruction set (base + CB/ED/DD/FD/DDCB/FDCB planes), the per-spec flag-bit map, register-pair aliasing, the Q/MEMPTR + undocumented X/Y bits, validated against the Z80 TomHarte corpus and ZEXALL/ZEXDOC. |
| **M4 — Motorola 68000** | The 3rd ISA — 32-bit registers over a 16-bit big-endian bus, the field-grammar decoder, the full EA-mode set, MOVE/ALU/shift/bit/BCD/control families, the CCR (X-bit), data-axis-exact against the 680x0 corpus (coarse-cycle timing by design). |
| **M5 — Intel 8086/8088** | The 4th ISA — variable-length ModR/M decode, `(CS<<4)+IP` segmentation, the full op families, the FLAGS model (AF/PF), validated against the 8088 TomHarte corpus. |
| **M6 — cross-arch JIT emit** | The "make tier-1 fast" pass (ADR 0011). Three more CPUs now emit IL for their high-ROI families, each gated on byte-identical TomHarte-through-JIT parity; the 6502 SMC/recompile-cost lever closed the W1 Klaus thrash. Plus the **test-infra speedup arc** (T1–T4: parse cache, per-worker allocation pooling, parallelized JIT sweeps, per-worker JIT reuse, and the in-tree gating policy). |

### What M6 emitted, per CPU

Each CPU's rare/exception/microcoded tail stays interpreter-fallback **by design** — the interpreter is
always the oracle and the byte-exact fallback, so partial emit is a pure performance dial.

| CPU | Emits IL for | Stays fallback (by design) |
|---|---|---|
| **6502** | the full ISA + the decimal arms; plus the **SMC/recompile-cost lever** (recompiles collapsed ~6.8× on Klaus) | BRK/RTI, undefined |
| **Z80** | LD, ALU + flags (Q/MEMPTR, X/Y), ED 16-bit (`ADC`/`SBC HL,rr`, `INC`/`DEC rr`), branch/call/stack — the Z80 JIT now **exceeds its own interpreter** on the W2 kernel | the prefix-plane long tail (block ops, ED/DD/FD/CB rarities) |
| **68000** | MOVE (the only net-new descriptor generation; needed a word-granular `Discover` fetch-stream fix), ALU + CCR (the X-bit), shifts, branch/DBcc — data-axis-exact (coarse-cycle by design) | TRAP/TRAPV/CHK/÷0/MOVEM/MUL/DIV/RTE/LINK/UNLK, address-error, privilege |
| **8086** | MOV (+ the `(CS<<4)+IP` seam), ALU + FLAGS, **near** branch/call/return | **far flow** (CS-invariant block key), MUL/DIV, string-REP, INT/INTO/IRET/BOUND, IN/OUT |

See [The JIT Tier](user-guide/jit.md) for the emit arms and the accuracy contract, and ADR 0011 for the
design rationale (the emit-vs-fallback boundary, the rollout order, the profiling-ranked ROI).

---

## Deferred & candidate follow-ons

These were surfaced and explicitly scoped-out during the M6 arc. **Prioritization is the owner's call —
this is an unordered menu.**

### JIT emit completeness

- **[deferred] 8086 far-flow emit.** Far `JMP`/`CALL`/`RET` (and far interrupts) stay fallback because
  the block-cache key is `(IP)`, CS-invariant. Emitting them requires **widening the cache key to
  `(CS,IP)`** so a far transfer to the same offset under a different segment is a distinct block. Scoped,
  named, not built.
- **[deferred] 8086 MUL/DIV + string/REP + INT/IRET emit.** The microcoded multiply/divide, the
  `REP MOVS/STOS/CMPS/SCAS` CX-counted string loops, and the INT/IRET vectoring machinery are fallback by
  design (rare, high-emit-cost). A future profile could justify emitting any of them.
- **[candidate] Z80 / 68000 tail emit (PR-2b-style).** The prefix-plane / microcoded long tails of the
  Z80 and 68000 stay fallback. Emitting selected hot members (the PR-2b precedent — Z80 ED 16-bit ops —
  showed this is cheap when a tail op turns out to recur) is a candidate as profiles dictate.
- **[candidate] The generic `OpModel`-walked emitter.** ADR 0011 Decision 2 / OQ2 — promote the
  hand-written per-CPU emit arms to a single spec-`OpModel`-driven emitter (with per-CPU flag/cycle
  plug-ins) once ≥2 CPUs' arms have revealed what genuinely generalizes. The descriptor `Ops` bridge
  keeps this possible with no spec change.

### Cycle-exactness & timing

- **[deferred] Cycle-exact emitted 68000 timing (the prefetch-queue model).** The 68000 is data-axis-exact
  but charges **coarse cycles** today; the cycle-exact axis (ADR 0008 §6 / ADR 0011 OQ4) — the
  prefetch-queue model — would make the emitted 68000 cycles/sec trustworthy and let it report cycles
  instead of leading with guest-MIPS.
- **[candidate] A cycle-exact 8086 timing model.** M5 charges a rudimentary one-cycle-per-bus-access
  model; a real 8086 timing model is post-M5 / unscheduled.

### Block-cache / specialization

- **[deferred] Per-bank `(PC, bankState)` block specialization.** ADR 0009 OQ3 — key blocks on
  `(PC, bankState)` so a re-entered memory bank reuses compiled blocks instead of evicting on every
  bank switch. Complementary to the M6 SMC lever (which handles self-modifying code, not bank-switching).

### Benchmark / profiler harness (bench-only, not core correctness)

- **[deferred] The 68000 W3 profiler arm.** The hot-op profiler covers 68000 W1/W2 but not W3; adding the
  W3 arm completes the 68000's profiling input. *(Tracked backlog.)*
- **[deferred] 68000 W2 bench-harness cycle off-by-2.** A small cycle discrepancy in the 68000 W2
  bench harness (affects the bench number, not interpreter/JIT parity). *(Tracked backlog.)*

### Host / surfacing

- **[candidate] A Z80 / 68000 / 8086 monitor host.** The interactive host still boots only the
  Breadboard6502; no Z80/68000/8086 REPL machine ships yet. The monitor engine is CPU-agnostic, so this
  is wiring a board, not new core work.

---

## Pointers

- Per-milestone design: [`docs/architecture/`](architecture/) (ADRs 0001–0011).
- The M6 emit design + PR arc: ADR 0011 (`docs/architecture/0011-jit-hot-op-emission-optimization.md`).
- The JIT tier (emit arms, accuracy contract, chaining, the SMC lever):
  [`docs/user-guide/jit.md`](user-guide/jit.md).
- Benchmarks + the before/after speedup story: [`docs/user-guide/benchmarks.md`](user-guide/benchmarks.md).
- Test-suite speedup detail: [`bench/results/test-suite-speedup.md`](../bench/results/test-suite-speedup.md).
