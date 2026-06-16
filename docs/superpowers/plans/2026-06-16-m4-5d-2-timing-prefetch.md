# M4.5d-2 — the 68000 TIMING axis: prefetch queue, cycle-exact accounting, full IPL, address-error frame

> **Status:** DRAFT (authored by the Coordinator in the main loop — the spawn-glitch fallback; awaiting owner approval
> before any build). Implements ADR 0008 (ACCEPTED) forks **C = Tier (i) staged**, **E = full IPL here**,
> **F = precise address-error frame here**. This is the SEAM-BREAKING, foundational completion of the 68000 interpreter.
>
> **Why this plan is task-structured, not literal-code-per-task** (unlike M4.5b/c/d-1): per ADR 0008 §8.1 the refill-point
> model (which bus read is a prefetch refill vs. an operand fetch, and *when* the queue refills mid-instruction) must be
> reverse-engineered EMPIRICALLY against the `transactions` traces — pre-committing literal code would be guesswork for a
> rewrite. Each task fixes the SEAM CONTRACT, the validation axis, and the reconciliation method; the Builder develops the
> exact code against the traces, TDD-style, one instruction-class at a time.

---

## ⚠️ Decisions for Coordinator/user review

**DD1 — The staged PR split (ADR fork C = "staged").** Recommend **TWO PRs**:
- **M4.5d-2a — the prefetch-queue rewrite + `final.pc`/`final.prefetch` axis + the address-error frame (F).** The structural
  seam break, reviewed in isolation on the green M4.5d-1 base. Validates the queue END STATE (the two prefetch words + the
  trailing formal PC) — NOT yet cycle counts.
- **M4.5d-2b — per-transaction bus trace + cycle accounting (`CycleCount == length`) + full IPL acknowledge (E).** Turns on
  the `transactions` trace diff + the idle-cycle accounting; completes cycle-exactness.
- *Alternative:* one mega-PR (rejected — confounds the architecturally-novel queue rewrite with the iterative idle-cycle
  accounting; the ADR's whole point is to review the seam break in isolation). *Alternative:* three PRs splitting IPL out
  (viable if 2b gets large; the Builder may promote it).

**DD2 — Where E (full IPL) lands: 2b.** The IPL *functional* model (level-vs-mask compare, `RaiseException`-via-autovector)
could land in 2a, but a thin synthetic stub already shipped in M4.5d-1, and the acknowledge sequence's *cycle trace* is only
meaningful with 2b's timing-aware bus model (ADR §4). So 2b fleshes out the full acknowledge sequence + its cycle accounting.
*Alternative:* IPL functional-complete in 2a, cycle-trace in 2b (more churn across PRs).

**DD3 — Where F (address-error group-0 frame) lands: 2a, with a 2b finalization for trace-coupled bits.** Most of the 14-byte
group-0 frame (the PC, SR, and faulting access address) is derivable from the queue/PC model in 2a. The status word's
in-progress bus-cycle bits (R/W, function code, instruction-vs-data) may be trace-coupled (ADR §5.2) — finalize those in 2b
if 2a's model can't pin them. **Empirical:** resolve against the actual group-0 frame words in the corpus; do not pre-commit.

**DD4 — The new `M68000FetchStream` seam contract** (see §2). Recommend a stateful prefetch-queue object the CPU owns across
Steps. *Alternative:* keep the stateless stream + bolt a separate queue model alongside (rejected — two fetch paths to keep
in sync; the queue IS the fetch model on a real 68000).

**DD5 — `timingAxis` stays default-OFF until the full sweep is green** (ADR §7 reversibility). The M4.5d-1 data-axis gate
(`assertExceptions`, `timingAxis:false`) must stay byte-identical-green throughout 2a/2b, so an in-progress queue model never
regresses it. The timing files assert with `timingAxis:true` only once green. *No alternative recommended — this is the safety
rail.*

**DD6 — Reconciliation budget / honesty.** Cycle-exact timing is the most iterative work in the arc (M4.5a needed 3 CCR rounds;
M4.5c needed 3; the trace model is harder — expect MORE, per instruction-class). The plan does NOT promise a case count up
front; it promises the gate (all timing files green with the trace asserted) or an HONEST per-class deferral list. If a
specific instruction-class's refill model resists reconciliation, it is disclosed + deferred (the M4.5b immediate-forms
precedent), never faked green.

---

## 1. What M4.5d-2 completes

The 68000 becomes **cycle-exact** for the single-step model: every instruction models the 2-word prefetch queue + its refills,
produces the exact `final.prefetch` + `final.pc`, emits the exact `transactions` bus trace, and charges `CycleCount == length`.
This is the last 68000-interpreter milestone — after it, M4.6 (JIT) and the 68000 cycle-benchmarking (bench Milestone B) open
up. (Per ADR §6, M4.6 actually only needs M4.5d-1, so it can proceed in parallel — see §6.)

## 2. The seam contract change (the heart of the PR — DD4)

**Today** (`src/CpuEmulator.Core/Jit/M68000FetchStream.cs`): a stateless, per-instruction `Read16`-walk from a PC origin
(`NextUnit` reads `bus[origin + offset*2]`, `offset` resets each instruction). The generated `Step` arm charges a flat
`__stream.UnitsConsumed * 4` cycles (`CpuEmitter.cs:231`); the bus helpers charge a flat `WordAccessCycles = 4`.

**After 2a:** a **stateful prefetch-queue object** the CPU owns across `Step`s:
- Holds the 2-word queue (`irc`/`ir`-style: the word being executed + the next prefetched word), seeded from `initial.prefetch`.
- `NextUnit()` returns `queue[0]`, advances (`queue[1]→[0]`), and issues a **refill read** of the next word at the formal-PC
  frontier — at the point in the instruction the real 68000 refills (reverse-engineered per class against `transactions`).
- Exposes `FinalPrefetch` (the end-state two words) + the **formal PC** (trails the physical fetch frontier by the prefetch
  depth) so the runner can diff `final.pc`/`final.prefetch`.
- 6502/Z80 are **unaffected**: this type is 68000-specific (it's `M68000FetchStream`, used only by the 68000 FieldGrammar Step
  path). The Mos6502/Z80 fetch streams + their generated arms are untouched — verified by `RegeneratedSpecTests` staying
  byte-identical (the regression guard, every stage).

**After 2b:** the bus helpers + the generated Step cycle-charge are reworked from flat `*4` to the **exact transaction
sequence** (`["r"/"w", cycles, addr, val]` accesses interleaved with `["n", N]` idle runs), so `CycleCount == length` and the
emitted trace matches `transactions`. This touches the generator's FieldGrammar Step emit (`CpuEmitter.cs:231`) — gated to
`model.FieldGrammar is not null`, so 6502/Z80 codegen is untouched.

## 3. M4.5d-2a — the queue + PC/prefetch axis (the seam break, in isolation)

> Validates the queue END STATE: `final.prefetch` (both words) + `final.pc` (the formal PC). Cycle counts NOT yet asserted.
> Merge gate uses `timingAxis:true` but a PC/prefetch-only assertion mode (the trace/cycle diff stays deferred to 2b).

- **T0 — Recon + the runner seam.** Read the current `DiffBusTrace`/`timingAxis` path in `M68000TomHarteRunner.cs:146` and the
  `final.pc`/`final.prefetch`/`transactions`/`length` fields in `M68000TomHarteLoader`/`M68000TomHarteVectors`. Add a runner
  assertion mode that diffs `final.pc` + `final.prefetch` (queue state) WITHOUT the full trace/cycle diff (the 2a ceiling). Keep
  `timingAxis` default-off (DD5).
- **T1 — The stateful prefetch-queue object.** Rewrite `M68000FetchStream` to the queue contract (§2): seed from
  `initial.prefetch`, execute-from-`[0]` + advance + refill, expose `FinalPrefetch` + the formal PC. Wire the CPU to own it
  across `Step`s (replacing the per-instruction throwaway stream). The generated `Decode(IFetchStream)` walk consumes through it
  unchanged (same `IFetchStream` interface) — the change is statefulness + the refill, not the walk API.
- **T2 — The refill-point model, reverse-engineered per instruction-class.** EMPIRICAL (ADR §8.1): for each class (the simple
  reg ops, the EA modes, the multi-word imm/MOVEM forms, the control-flow ops), determine where the queue refills relative to
  operand reads by diffing `final.prefetch`/`final.pc` against the corpus. TDD one class at a time; do NOT pre-commit a global
  refill rule. Reconcile until `final.pc`/`final.prefetch` match.
- **T3 — The address-error group-0 frame (F, DD3).** With the formal-PC + queue model, pin the group-0 frame words (PC, SR,
  faulting address, the instruction-register word) and flip the M4.5d-1 "trap-taken only" address-error deferral to assert the
  frame contents. Any status-word bit that proves trace-coupled (R/W, function code) is deferred to 2b with a disclosed note.
- **T4 — The 2a sweep + gate.** Run the M4.5d-1 + M4.5a–c files with the PC/prefetch assertion mode on. Show the executed
  green count. The cycle/trace diff stays off (2b). Reconcile per-class.

**2a merge gate:** full suite green + 6502/Z80 byte-identical (the queue rewrite is 68000-only); the PC/prefetch axis green with
vectors present under `-c Release` (executed count shown), the M4.5d-1 data-axis gate still byte-identical-green (DD5); pre-merge
review pointed at the queue/refill model (the highest-risk new code).

## 4. M4.5d-2b — cycle accounting + per-transaction trace + full IPL

> Turns on the full `transactions` trace diff + `CycleCount == length`. Completes cycle-exactness.

- **T5 — The bus-transaction model.** Rework the bus helpers + the generated Step cycle-charge (CpuEmitter.cs:231) from flat
  `*4` to the exact `["r"/"w", cycles, …]` + `["n", N]` idle-run sequence. Emit the transaction list the runner diffs against
  `transactions`. Gated to the FieldGrammar Step path (6502/Z80 untouched).
- **T6 — Per-class cycle reconciliation.** EMPIRICAL: reconcile `CycleCount == length` + the trace order (refill reads
  interleaved with operand accesses) per instruction-class against the corpus. This is the iterative core (DD6) — expect
  multiple rounds per class. Disclose + defer any class that resists (honest, never faked).
- **T7 — Full IPL acknowledge (E, DD2).** Flesh out the M4.5d-1 stub into the complete acknowledge sequence: level-vs-mask
  compare (SR bits 10-8; level 7 NMI), enter supervisor, push the (PC, SR) frame via `RaiseException`, set mask to the serviced
  level, read the autovector (25–31) — with its cycle trace now meaningful under the 2b bus model. Synthetic-tested (no vector
  exercises async interrupts — ADR §4, honest disclosure) PLUS the acknowledge-cycle trace asserted where the model allows.
- **T8 — Finalize the address-error status word (F tail, DD3).** Pin the trace-coupled group-0 status-word bits deferred from
  T3.
- **T9 — The full timing sweep + gate.** Flip `timingAxis:true` for all ~120 families with the full trace + cycle diff on. Show
  the executed green count + any disclosed per-class deferrals.

**2b merge gate:** full suite green + 6502/Z80 byte-identical; the FULL timing axis (`final.pc`/`final.prefetch`/trace/`cycle ==
length`) green with vectors present under `-c Release` (executed count shown; honest deferral list if any); pre-merge review on
the cycle/trace model.

## 5. Seam invariant (this BREAKS it — by design, per ADR 0008 §5.2/§7)
M4.5d-2 deliberately rewrites `M68000FetchStream.cs` (stateless→stateful queue) and the bus-helper/Step cycle model. This is
unavoidable — the timing axis *is* the prefetch-queue model. Containment: (a) the new fetch type stays 68000-specific; (b) all
generator changes gate to `model.FieldGrammar is not null`; (c) **6502/Z80 byte-identity (`RegeneratedSpecTests`) is the
every-stage regression guard**; (d) `timingAxis` stays default-off until green (DD5), so the M4.5d-1 data-axis gate never
regresses mid-rewrite.

## 6. Cross-cutting (ADR §6)
- **M4.6 (68000 through the JIT) depends only on M4.5d-1, NOT on this.** M4.6 can proceed in PARALLEL with M4.5d-2 — the JIT
  parity gate is the data axis, and exception-capable ops are `NeedsFallback`. **Scheduling note for the Coordinator:** M4.6
  need not wait for M4.5d-2.
- **The benchmarking 68000 cycle numbers (bench Milestone B / the M6 re-measure) CONSUME M4.5d-2.** Until 2b lands, 68000
  benchmarks may quote instructions/sec (M4.5d-1 functional core) but NOT cycles/sec. Flag so the bench plan doesn't quote
  68000 cycle figures before the prefetch model exists.

## 7. Honesty
- IPL is synthetic-tested (no async-interrupt vector) — disclosed, like M4.5b's immediate forms.
- Any instruction-class whose refill/cycle model resists reconciliation is DISCLOSED + deferred per-class, never faked green.
- `final.pc`/prefetch (2a) ships before full cycle accuracy (2b) — stated plainly; the 68000 is "PC/prefetch-exact" after 2a,
  "cycle-exact" after 2b.

## 8. Risk
**HIGH / foundational.** The fetch-path rewrite is shared by every instruction, so a bug regresses the whole sweep at once
(mitigated by DD5's default-off flag + the byte-identity guard). The cycle/trace reconciliation is the most iterative work in
the 68000 arc. This is the PR the owner reviews most closely; recommend landing 2a and 2b as separate reviewable PRs.
