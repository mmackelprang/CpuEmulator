# ADR 0020 — Cycle-exact emitted 68000 timing: finishing the prefetch-queue model and the report-cycles question

> **Status:** **PARKED — Option A (close as-built), owner decision 2026-06-23.** The cycle-exact oracle *exists*
> (the TomHarte 680x0 corpus is cycle-exact — per-bus-cycle traces; ~13/68 families reconciled green, the 2-word
> prefetch queue shipped), but finishing #2 is a ~3–10 PR grind for a *reporting-unit change* (interpreter
> cycles/sec) with no cycle-sensitive 68000 consumer on the roadmap — revisit only if such a target appears.
> Originally drafted **Proposed** by Claude Architect 2026-06-22 against fresh `main` @ `db9d64a`
> (HEAD == origin/main). Designs **roadmap "Deferred & candidate follow-ons" item #2** (ROADMAP.md:241-244,
> owner-set priority 2026-06-19): *"Cycle-exact emitted 68000 timing (the prefetch-queue model). The 68000 is
> data-axis-exact but charges **coarse cycles** today; the cycle-exact axis (ADR 0008 §6 / ADR 0011 OQ4) — the
> prefetch-queue model — would make the emitted 68000 cycles/sec trustworthy and let it **report cycles instead
> of leading with guest-MIPS**."*
>
> **Date:** 2026-06-22
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Supersedes / relates to:**
> - **ADR 0008** (`0008-68000-control-flow-exceptions-and-the-timing-axis.md`) — §2/§5/§6: the M4.5d-1
>   (data-axis) / M4.5d-2 (timing-axis) PR split, the prefetch-queue model, and the "68000 cycles/sec gates on
>   M4.5d-2" dependency. **This ADR reports the** ***as-built state*** **of that timing axis** (it shipped further
>   than ADR 0008's "hold for the owner" framing implied) and decides what the named roadmap item #2 actually is
>   now.
> - **ADR 0011** (`0011-jit-hot-op-emission-optimization.md`) — **OQ4** (the make-or-break dependency this ADR
>   resolves): *"the 68000's emitted ops can charge cycles via `AdvanceCycles`, but the full cycle-exact axis
>   (M4.5d-2 prefetch/timing) is partial on `main`. So an emitted 68000 op is cycle-trustworthy only for the
>   cycle-exact families. The re-measure leads with guest-MIPS … the cycles/sec column stays caveated until
>   M4.5d-2 completes."* — and **DECISION T2** (ADR 0011 §4): the emitted tier charges coarse `BaseCycles + 1`
>   **by design**, gated only on the data axis. This ADR decides whether to keep or change that.
> - **The bench doc-reconciliation** (`docs/superpowers/specs/2026-06-22-bench-doc-reconciliation-design.md`,
>   queue row **B68-DOC**) — the W2 "off-by-2" is the **accepted coarse-cycle slack** between the interpreter's
>   exact prefetch cycles and the JIT's coarse `BaseCycles`, gated within a `<= 16` tolerance in
>   `tests/CpuEmulator.Tests/Jit/BenchHarnessSmokeTests.cs`. This ADR explains *why that slack exists* and what
>   removing it would cost.
>
> **Verdict (stated up front, per the task's stop-or-hand-off gate):**
> - **The gating oracle EXISTS and is un-fakeable — the task's premise is factually wrong.** The TomHarte 680x0
>   v1 corpus is **NOT** "data-axis-exact but coarse-cycle." Every case carries `length` (the total cycle count)
>   AND `transactions` (the full per-bus-cycle trace: direction, **per-slot cycle count**, function code,
>   address, size, value, plus `["n", N]` idle runs). The interpreter already asserts `CycleCount == length` +
>   the per-transaction trace against it, and **13 instruction families are reconciled to full cycle-exactness
>   today** (`M68000TimingReconBase`, `tests/.../M68000TimingReconTests.cs:77-89`). The un-fakeable cycle oracle
>   is not just obtainable — **it is already wired and green for a third of the ISA.** §3 is the proof.
> - **What roadmap #2 actually is, now:** **two genuinely-separable pieces** — **(2-INTERP)** finish the
>   *interpreter* cycle reconciliation for the remaining ~55 families (the un-fakeable corpus gates every one of
>   them except async-IPL, which has no vector), and **(2-EMIT)** decide whether the *emitted/JIT* tier should
>   stop charging coarse `BaseCycles` and instead reproduce the interpreter's exact per-instruction cycle count
>   (which is what "trustworthy emitted cycles/sec" requires). These are **NOT** one arc; they have different
>   risk and different value.
> - **Blast-radius: SAFE on the data axis, RISKY on the shared fetch/cycle path.** The data-axis-exact contract
>   is **structurally protected** — cycle work only touches `_cycles` accumulation + the trace, never the data
>   result (proven: the runner diffs data and cycles on independent axes, `M68000TomHarteRunner.RunCase`
>   `:201-238`). The risk is that the prefetch-queue/cycle path is **shared by every instruction**, so a
>   reconciliation bug regresses the whole timing sweep — but **not** the data sweep. The other three CPUs are
>   untouched (the model is `M68000`-specific — `M68000FetchStream.cs:40-42`).
> - **Recommendation: CHECKPOINT-WITH-OWNER — do NOT auto-proceed to Planner.** Not because the oracle is
>   missing (it isn't), but because **(a)** the highest-value half (2-EMIT — "trustworthy emitted cycles/sec")
>   has a **poor cost/value ratio and partially fights an accepted decision** (DECISION T2 deliberately made the
>   emitted tier coarse-cycle), and **(b)** the lower-risk half (2-INTERP) is a **long iterative grind** (~55
>   families, "the most iterative work in the 68000 arc" per the M4.5d-2 plan) whose payoff is a benchmark
>   headline-unit change, not a capability. The owner should pick the **scope** (interpreter-only vs both tiers)
>   and the **bar** (which families; whether to keep DECISION T2) before a multi-PR grind is queued. §7 frames
>   the three concrete options. This ADR is design + recommendation; it does **not** hand to Planner.

---

## 1. Context — what "coarse cycles" means here, and why the framing matters

The roadmap one-liner ("the 68000 charges coarse cycles") is true **of one tier and a minority of the other**,
and the precision matters because it changes what the work *is* and whether it's gateable. Three facts, all
verified against `main`:

1. **The interpreter is NOT uniformly coarse — it is cycle-exact for a reconciled subset and being extended
   family-by-family.** The shipped interpreter owns a **stateful 2-word prefetch queue** (`M68000FetchStream`,
   `src/CpuEmulator.Core/Jit/M68000FetchStream.cs`) with a **deferred-refill cycle/trace model**: the decode
   walk advances the queue via an *untraced peek* and pushes each refill's frontier address onto a
   per-instruction backlog; the op body (or the generated `Step`) issues each refill as a **traced 4-clock word
   bus cycle** at the point the real 68000 does, then flushes the remainder (`FetchStream.NextUnit` `:170-186`,
   `TryPopRefill` `:205-211`; the generator emits `FlushRefills`/`IdleCycles`/`Refill` —
   `CpuEmitter.cs:63-93`). The result is asserted `CycleCount == length` + per-transaction-trace-exact against
   the corpus for **13 families today** (NOP/SWAP/MOVEQ/LEA + the read-only-EA and single-EA RMW `.b`/`.w` ALU:
   TST.w/CLR.b/NEG.w/CMP.w/CMP.b/AND.b/OR.w/ADD.b/SUB.b — `M68000TimingReconTests.cs:77-89`). The queue
   **end-state** (`final.pc` + both `final.prefetch` words) is asserted for **all 68 canonical families** (the
   "2a" sweep, `M68000TimingAxisTomHarteTests.cs`). So the interpreter is *PC/prefetch-exact everywhere,
   cycle-exact for ~13/68 families, and coarse (a `PendingRefills * 4` fetch charge with no per-transaction
   trace) for the rest.*

2. **The emitted/JIT tier IS uniformly coarse — and that is a deliberate, gated decision (DECISION T2).** The
   JIT charges each descriptor's coarse `BaseCycles + 1` opcode-fetch cycle (`BlockCompiler.cs`
   `EmitChargeOneCycle`/`EmitChargeCycles` `:739-765`; the 68000 MOVE arm charges whole-op `BaseCycles` once,
   `:976-977`), **explicitly NOT** the interpreter's exact per-word prefetch model. The data-axis parity gate
   (`RunCaseThroughJit`, `M68000TomHarteRunner.cs:250-314`) **ignores `CycleCount`** — it asserts regs+SR+RAM
   byte-identity only. This is why the W2 cross-tier bench shows the accepted "off-by-2" (DECISION T2, ADR 0011
   §4; the bench smoke test tolerates `<= 16`, "one instruction's worst-case charge").

3. **The bench leads with guest-MIPS *because* of #2, not because the oracle is missing.** The Musashi adapter
   says it outright (`bench/CpuEmulator.Benchmarks/Adapters/MusashiAdapter.cs:10`): *"this row carries
   guest-MIPS — the cross-CPU-comparable headline (the 68000 cycle axis is partial …)."* The roadmap's
   aspiration — "report cycles instead of leading with guest-MIPS" — is therefore a statement about making the
   **emitted** tier's cycles trustworthy (the bench measures the JIT tier), which is squarely the coarse
   DECISION T2 tier.

**The reframe this ADR delivers:** roadmap #2 is not "build a cycle model we have no oracle for." It is
"**finish** the interpreter cycle reconciliation (oracle present, green for 13, ~55 to go) **and/or** decide
whether to un-coarsen the emitted tier (which means partially reversing DECISION T2)." The make-or-break
oracle question the task flags as the crux is **already answered in the affirmative by shipped, green tests.**

---

## 2. The two separable pieces (the load-bearing decomposition)

| | **2-INTERP — finish the interpreter cycle reconciliation** | **2-EMIT — make the emitted tier cycle-exact (trustworthy cycles/sec)** |
|---|---|---|
| **Goal** | `CycleCount == length` + per-transaction trace green for the remaining ~55 families (the T9 sweep flip: `timingAxis:true` for all families) | The JIT's emitted 68000 ops charge the **same** per-instruction cycle count as the interpreter, so the bench can report trustworthy cycles/sec and the W2 off-by-2 vanishes |
| **Oracle** | **The TomHarte 680x0 `length` + `transactions` corpus** (un-fakeable; already wired) — gates every family **except async-IPL** (no async-interrupt vector exists; synthetic-tested, honestly disclosed) | The **interpreter is the oracle** (the project's standard tier-parity discipline): emitted `CycleCount` must equal interpreter `CycleCount` per instruction. *Transitively* corpus-gated (the interpreter is corpus-gated) |
| **Touches** | `M68000FetchStream` refill-point model per class + the generated `Step` cycle-charge (`CpuEmitter.cs`) — the **shared fetch/cycle path** | The 68000 JIT emit arms (`BlockCompiler.*` 68000 families) — must emit the deferred-refill/idle cycle logic in IL, OR call a per-instruction "exact cycle" seam; **reverses DECISION T2's coarse charge** |
| **Data axis** | **UNTOUCHED** — cycle work never changes the data result; the runner diffs data + cycles on independent axes | **UNTOUCHED** — `RunCaseThroughJit` data parity is unaffected; only `CycleCount` sharpens |
| **Risk** | **RISKY (medium)** — shared path, regresses the *timing* sweep on a bug (not the data sweep). Iterative: "the most iterative work in the 68000 arc" (M4.5d-2 plan §8) | **RISKY (medium-high) + fights an accepted decision** — re-introduces the per-CPU cycle-model IL complexity DECISION T2 *deliberately avoided*; the IL-ceiling cost (ADR 0011 §2) lands on the cycle path |
| **Value** | The interpreter (the oracle/reference tier) becomes fully cycle-exact — completes the M4 "cycle-exact 68000" milestone framing | The **bench headline** can quote 68000 cycles/sec; the roadmap's literal ask. But the bench measures *throughput*, and exact cycles don't change throughput — only the *reported unit* |
| **Scope** | **~3–6 PRs** (family batches: the EA-mode classes, the multi-word/MOVEM forms, the control-transfer reseeds, MUL/DIV data-dependent idle, address-error frame timing, full IPL) | **~2–4 PRs** (the exact-cycle emit seam + per-family emit-cycle reconciliation + the bench re-measure + the DECISION T2 reversal/caveat) |

**Key dependency:** **2-EMIT depends on 2-INTERP.** The emitted tier can only be made cycle-exact for families
the *interpreter* is cycle-exact for (the interpreter is 2-EMIT's oracle). So "trustworthy emitted cycles/sec
for the whole ISA" requires finishing 2-INTERP first. A *partial* 2-EMIT (exact cycles for the 13 reconciled
families, coarse for the rest) is possible but produces a tier whose cycle count is exact for some instructions
and coarse for others — arguably worse than uniformly-coarse for a benchmark headline (it would be
*inconsistently* trustworthy).

---

## 3. The gating oracle — the crux, answered (un-fakeable, present, green)

The task names this "THE make-or-break question." Here is the un-fakeable reference, in the corpus and in the
shipped runner:

**3.1 The corpus carries exact cycles.** `M68000TomHarteCase` (`tests/.../M68000TomHarteCase.cs:39-42`) parses
a top-level `length` (the total cycle count) and a `transactions` array. Each transaction is either an **idle
slot** `["n", cycles]` or a **bus access** `[dir, cycles, fc, addr, sizeTag, value]` (`:14-28`). The loader doc
pins the semantics (verified at Task 1 against the live SingleStepTests/680x0 v1 data): *"`Cycles` (field 2) is
the per-slot CYCLE COUNT — CONFIRMED, not the ADR-flagged unknown: the case's top-level `length` equals the sum
of `Cycles` over its transactions"* (`:16-19`). This is the canonical n/S/r/w microcycle accounting the task
asks about — present, per-slot, summing to the total. **It cannot be faked:** to match it the emulator must
issue the right bus accesses, in the right order, at the right cycle cost, with the right idle runs.

**3.2 The runner already asserts against it, two ways.** `M68000TomHarteRunner.RunCase` (`:146-241`):
- `timingAxis:true` asserts `cpu.CycleCount == c.Length` **and** `DiffBusTrace(problems, bus.Trace,
  c.Transactions)` — a per-access diff of address+direction+size+value, in order, against the non-idle
  transactions (`:232-238`, `:320-345`). This is the full cycle-exact gate.
- The data axis (regs+SR+RAM, `:201-213`) is asserted **independently and always**, on a separate
  non-tracing space. **This independence is the data-axis-exactness guarantee** (§5): cycle work cannot
  corrupt the data result because they are diffed on disjoint axes against the same corpus case.

**3.3 It is green for 13 families today.** `M68000TimingReconBase` (`M68000TimingReconTests.cs:22-89`) runs
whole corpus files with `timingAxis:true` over the non-deferred cases; the 13 sealed subclasses
(NOP/SWAP/MOVEQ/LEA/TST.w/CLR.b/NEG.w/CMP.w/CMP.b/AND.b/OR.w/ADD.b/SUB.b) are the families reconciled to full
cycle-exactness. The merge gate runs `CPUEMULATOR_UAT=full` (~8065 cases/file). **This is the un-fakeable proof
the oracle works:** a wrong refill point, a missing idle cycle, or a mis-ordered trace fails these tests.

**3.4 The oracle's ONE genuine gap (honestly disclosed): async IPL.** The single-step corpus exercises only
*instructions*; no case fires an asynchronous interrupt mid-stream. So the **IPL interrupt-acknowledge cycle
trace cannot be corpus-gated** — it is synthetic-tested, exactly the M4.5b immediate-forms precedent (M4.5d-2
plan §7: *"IPL is synthetic-tested (no async-interrupt vector) — disclosed"*). This is the only piece of 2-INTERP
without an un-fakeable cycle oracle, and it is small. **Everything else is corpus-gated.**

**3.5 Verdict on the crux:** the un-fakeable cycle-exact reference is **the TomHarte 680x0 `length` +
`transactions` corpus** — it is obtainable (already cached + loaded), un-fakeable (per-slot microcycle
accounting + the full bus trace), and **already gating 13 families green**. A cycle model we *can* un-fakeably
verify is exactly what we have. **The make-or-break question is answered YES.** This removes the task's primary
reason to stop. (The reasons to checkpoint are about value/cost, §7 — not verifiability.)

---

## 4. 2-INTERP — the remaining family reconciliation (shape, not full bodies)

The shipped deferred-refill machinery (`M68000FetchStream` + the generated `FlushRefills`/`IdleCycles`/`Refill`
seam) is **general**; what remains per family is the **refill-point model** — *which* bus read is a prefetch
refill vs. an operand fetch, and *where* in the operand sequence the refill lands — which the M4.5d-2 plan
mandates be **reverse-engineered empirically against `transactions`, never pre-committed as a global rule** (ADR
0008 §8.1; M4.5d-2 plan T2). The reconciled-vs-remaining split, by difficulty:

| Family class | State | What makes it hard (the refill-point / idle model) |
|---|---|---|
| Register-only + read-only/RMW single-EA `.b`/`.w` ALU | **GREEN (13)** | Refills lead; one operand access; the simplest interleave |
| Two-EA MOVE (`(An)→(An)` etc.) | remaining | Two operand accesses with a refill *between* them; the refill order per src/dst EA-mode pair must be derived |
| `.l` (long) register ALU with data-dependent idle | remaining | The internal idle-cycle count depends on **operand data** (e.g. shift counts, the `.l` ADD path) — the idle run length varies per case; the model must compute it, not table it |
| Shifts/rotates (`ASL`/`LSR`/`ROL`/`ROXR` by count) | remaining | Cycle count = base + 2×shiftcount — data-dependent idle; gateable (the corpus pins each count) |
| MUL/DIV | remaining | Microcoded, data-dependent cycle count (the classic 68000 variable-latency multiply/divide); the corpus pins it per operand, but the model is the most intricate |
| MOVEM | remaining | Cycle count scales with the register-list population count; refills interleave with the list transfers |
| Control transfers (Bcc/BSR/DBcc/JMP/JSR/RTS/RTE) | remaining | The **reseed** discards the backlogged refill and emits *target* prefetch reads; the taken/not-taken cycle split + the reseed trace must match (the `_reseededInBody`/`ReseedPeek` path, `CpuEmitter.cs:362`) |
| TRAP/CHK/exception frames | remaining | The frame-push + vector-fetch bus trace + cycle cost; small-frame is tractable, address-error group-0 is trace-coupled (ADR 0008 §2.1) |
| Address-error group-0 frame timing | remaining | The status word encodes **in-progress bus-cycle state** — the single most timing-coupled case (ADR 0008 F) |
| Async IPL acknowledge | remaining | **No corpus oracle** (§3.4) — synthetic-tested only |

**Sequencing shape (if the owner greenlights 2-INTERP):** batch by the table above, easiest→hardest, each batch
a PR that flips its families into `M68000TimingReconBase` then folds them into the T9 full-sweep gate. Each PR's
merge precondition is the un-fakeable `CPUEMULATOR_UAT=full` cycle sweep for its families. Per the M4.5d-2 plan's
**DD6 honesty rule**, any class whose refill model resists reconciliation is *disclosed + deferred per-class,
never faked green*. **No new generator shape** is needed (the seam is shipped); this is body-level reconciliation
work, ~3–6 PRs.

---

## 5. The data-axis-exact invariant — why cycle work cannot disturb it (the SAFE half)

The task's hard constraint: changing cycle accounting must **NOT** change the data-axis-exact contract (data
results stay byte-identical; only the cycle counts sharpen). This is **structurally guaranteed**, not merely
intended:

- **The runner diffs data and cycles on disjoint axes.** `RunCase` asserts regs/SR/RAM (`:201-213`) on a
  **non-tracing** inner space, always; the cycle/trace assertions (`:232-238`) are gated behind `timingAxis`
  and read a *separate* tracing wrapper's `Trace`. A cycle bug shows up as a trace/`CycleCount` mismatch, never
  as a data mismatch — the existing 13 green families and the full data sweep prove the two are independent.
- **`_cycles` is write-only side state.** Cycle charging is `_cycles += n` (`CpuEmitter.cs:83,93,360,385`); no
  data path reads `_cycles`. Refills read via the **untraced peek** (`SeedPeek`/`Peek16`,
  `M68000FetchStream.cs:115-134`) precisely so they cannot perturb operand reads or the data trace.
- **The other three CPUs are untouched.** `M68000FetchStream` is 68000-specific (`:40-42`: *"6502/Z80 are
  UNAFFECTED … verified by the byte-identity regression guard"*). The cycle model is confined to the 68000's
  FieldGrammar Step path.

So on the **data axis**, this whole arc is **SAFE** — the existing data-axis green sweep is a hard regression
gate that any cycle PR must keep green, and the architecture makes a data regression from a cycle change
implausible (disjoint axes, write-only `_cycles`). The **RISKY** classification is confined to the *timing*
sweep: a refill-model bug regresses the (shared) timing sweep, and the iterative nature means many small
reconciliation rounds. RISKY-on-timing, SAFE-on-data.

---

## 6. 2-EMIT — the emitted-tier cycle question, and DECISION T2

This is the half that **fights an accepted decision** and where the cost/value is poor — the heart of the
checkpoint recommendation.

**DECISION T2 (ADR 0011 §4) deliberately made the emitted tier coarse-cycle.** The JIT charges
`BaseCycles + 1` per descriptor; the data-axis parity gate ignores `CycleCount`. The rationale was sound: the
emitted tier exists for **throughput**, the interpreter is the **cycle oracle**, and emitting the full
deferred-refill/idle/data-dependent cycle model in IL re-introduces exactly the per-CPU cycle-model complexity
the JIT was trying to avoid (the IL-ceiling cost, ADR 0011 §2 — "the highest-bug-density code in the JIT" is the
flag/cycle arms). Making the emitted tier cycle-exact means **transcribing the interpreter's per-instruction
cycle computation (including data-dependent idle for shifts/MUL/DIV/`.l`) into emitted IL** for every family —
a large surface for a benchmark-unit change.

**The value is narrower than it looks.** "Report cycles instead of leading with guest-MIPS" is a *reporting*
change. The bench measures throughput (cycles or instructions per wall-second); making the emitted cycle count
*exact* does not make the JIT faster — it makes the cycles/sec number *trustworthy*. But:
- Guest-MIPS is the **cross-CPU-comparable** headline (`ComparisonTableWriter.cs:50-56,101-103`); cycles/sec is
  per-CPU-only. Leading with guest-MIPS is arguably *correct* for a cross-CPU comparison regardless.
- A cheaper alternative to full emit-cycle-exactness exists: **drive the emitted-tier cycle charge from the
  interpreter's per-instruction cycle count via the existing `AdvanceCycles` seam** (`M68000Cpu.Jit.cs:12`) — i.e.
  for each emitted op, charge the *interpreter-computed* exact cycle count rather than re-deriving it in IL. This
  is only possible for families where the interpreter is cycle-exact (depends on 2-INTERP) and where the cycle
  count is not data-dependent in a way the emitted op must compute inline. It narrows the IL surface but does not
  eliminate the dependency on 2-INTERP, and it partially defeats the JIT's purpose (recomputing exact cycles is
  overhead the coarse charge avoided).

**Options for 2-EMIT (if the owner wants trustworthy emitted cycles/sec):**
- **(E-a) Keep DECISION T2; relabel the bench.** Cheapest: keep the emitted tier coarse, and have the bench
  report the **interpreter** cycles/sec for the 68000 (the interpreter *is* cycle-exact for the reconciled
  families) while the JIT column stays a throughput/guest-MIPS figure with the honest coarse-cycle caveat. **No
  IL change.** This satisfies "report cycles" without un-coarsening the JIT. Recommended sub-option.
- **(E-b) Make the emitted tier exact via the `AdvanceCycles` interpreter-cycle seam** for the reconciled
  families. Medium surface; partially reverses T2; gated by emitted-`CycleCount` == interpreter-`CycleCount`.
- **(E-c) Full emit-cycle-exactness in IL** (transcribe the deferred-refill/idle model into emitted arms).
  Largest surface, highest IL-ceiling cost, fully reverses T2. **Not recommended.**

---

## 7. The three concrete options for the owner (the checkpoint)

| Option | Scope | Risk | Value | Recommend |
|---|---|---|---|---|
| **A. Do nothing — close #2 as "as-built is the right ceiling."** Document that the interpreter is cycle-exact for the high-value families, the emitted tier is coarse-by-design (T2), and the bench leads with guest-MIPS correctly. | 0 PRs (doc-only, like B68-DOC) | None | Honest closure; frees the slot | **Viable** — if the owner agrees the bench headline (guest-MIPS) is fine and partial interpreter cycle-exactness suffices |
| **B. Finish 2-INTERP only — the interpreter becomes fully cycle-exact; keep the emitted tier coarse (T2); bench reports interpreter cycles/sec (E-a).** | ~3–6 PRs | RISKY-on-timing, SAFE-on-data | The reference tier is fully cycle-exact; "report cycles" satisfied via the interpreter; T2 untouched | **Recommended IF the owner wants #2 actually built.** Highest value-per-risk; the oracle is present; no fight with T2 |
| **C. Finish 2-INTERP + un-coarsen the emitted tier (2-EMIT, E-b/E-c).** | ~5–10 PRs | RISKY + reverses an accepted decision | Trustworthy *emitted* cycles/sec, ISA-wide | **Not recommended now** — poor cost/value; re-introduces the IL cycle complexity T2 avoided, for a reporting-unit change |

**My recommendation: CHECKPOINT-WITH-OWNER, leaning toward Option B if anything is built.** The owner should
decide between A (close it — the as-built ceiling is defensible) and B (finish the interpreter cycle axis,
report interpreter cycles/sec, leave the JIT coarse). **C is the one to avoid** unless a concrete need for
trustworthy *emitted* cycle counts emerges (e.g. a future cycle-sensitive 68000 machine — none is on the
roadmap; the Amiga/Mac-class targets that would need it are not queued).

---

## 8. Consequences

**Good.**
- The make-or-break oracle question is settled: the un-fakeable cycle reference exists, is wired, and is green
  for 13 families. No unverifiable model is at risk of being built — every reconciliation PR is corpus-gated.
- The data-axis-exact 68000 invariant is structurally protected (disjoint diff axes, write-only `_cycles`,
  68000-confined model). The other three CPUs are untouched.
- The decomposition (2-INTERP vs 2-EMIT, with the dependency edge) lets the owner buy exactly the value they
  want at exactly the risk they'll accept, and exposes that the headline ask ("report cycles") has a cheap
  satisfier (E-a / Option B) that doesn't fight DECISION T2.

**Bad / accepted costs.**
- 2-INTERP is a long iterative grind ("the most iterative work in the 68000 arc"); the value is a fully
  cycle-exact *reference* tier and a reporting-unit change, not a new capability.
- 2-EMIT (Option C) would reverse a deliberate, gated decision (T2) and re-incur the IL-ceiling cycle-model
  cost — high cost for a reporting change. Flagged as not-recommended.
- The async-IPL acknowledge cycle trace has **no corpus oracle** (§3.4) — it stays synthetic-tested, honestly
  disclosed. The only un-gateable sliver of the arc.

**Reversibility.** 2-INTERP is additive per-family (each family flips into the timing sweep behind the existing
`timingAxis` gate; default-off until green) — fully reversible and incremental. Option A is doc-only. 2-EMIT
(C) is the foundational one (it changes the emitted cycle charge every block shares) and would need its own
seam-break review.

---

## 9. Open questions

1. **Which option (A / B / C)?** The owner's call — this is the checkpoint. Recommend A or B; avoid C absent a
   concrete cycle-sensitive 68000 target.
2. **If B: does "report cycles" mean the bench leads with interpreter cycles/sec, or keeps guest-MIPS as the
   cross-CPU headline and adds interpreter cycles/sec as the per-CPU figure?** The cross-CPU comparison arguably
   *should* stay guest-MIPS (`ComparisonTableWriter` is built around it). Recommend: add cycles/sec as the
   per-CPU sanity figure; keep guest-MIPS as the cross-CPU headline. Owner confirm.
3. **The data-dependent-idle families (shifts/MUL/DIV/`.l` ALU):** the corpus gates each case, but the model must
   *compute* the idle count from operand data. Confirm empirically per the M4.5d-2 DD6 rule that each reconciles;
   disclose+defer any that resists (never fake green).
4. **Address-error group-0 frame timing** (ADR 0008 F): the status word encodes in-progress bus-cycle state — the
   most timing-coupled case. Resolve against the actual corpus frame words; do not pre-commit; defer if it
   resists.

---

*End of ADR 0020. The gating oracle is present and un-fakeable (the TomHarte 680x0 `length` + `transactions`
corpus, already green for 13 families) — the make-or-break question is answered YES. The data-axis-exact 68000
invariant is structurally protected (SAFE-on-data); the risk is confined to the shared timing path
(RISKY-on-timing) and to 2-EMIT reversing the accepted DECISION T2. Because the highest-value half (trustworthy
emitted cycles/sec) has a poor cost/value ratio and fights an accepted decision, and the lower-risk half is a
long iterative grind whose payoff is a reporting-unit change, the recommendation is **CHECKPOINT-WITH-OWNER**
(pick Option A/B/C and the bar) rather than auto-proceed to Planner. Designer: no UX surface (headless
framework). Planner can pick up Option B's §4 family-batch shape once the owner chooses scope.*
