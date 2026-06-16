# M4.5d-1: The 68000 control-flow + exception-model interpreter — the CLEAN ADDITIVE data-axis PR (ADR 0008 §3) — SINGLE PR

> **STATUS: DRAFT — awaiting Coordinator/user ratification of the C/E/F sign-off items (the `## Decisions`
> block below). Otherwise APPROVED in content (build-per-ADR-recommendation, single PR, data-axis only).
> Do NOT branch, queue, or implement until ratified.**
> This plan implements the ADDITIVE data-axis subset of ADR 0008 (the M4.5d arc split along the data-axis /
> timing-axis fault line): the control-flow + stack + privileged + vectoring TAIL plus the exception model, at
> the SAME rigor as the merged M4.5c plan (`docs/superpowers/plans/2026-06-15-m4-5c-shift-rotate-bit-bcd.md`).
> It is the LOW-RISK first half; the timing axis + prefetch-queue rewrite (the SEAM-BREAKING half) is M4.5d-2,
> held for the owner per ADR 0008 §5.
>
> **For agentic workers (once ratified):** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or
> superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. This is the FOURTH of the M4.5 interpreter
> sub-PRs (a = MOVE ✅; b = integer ALU ✅; c = shift/rotate/bit/BCD/Scc/data-movement ✅ merged; **d-1 = control
> flow + exceptions (this plan, ONE PR)**; d-2 = timing + prefetch queue [HELD]). **M4.5c MUST be on `main`**
> (it is: `main` @ `5661857`, M4.5a/b/c merged, ADR 0008 landed). This plan REUSES the shipped substrate
> verbatim: `EvaluateCondition` (the shared cc evaluator built in M4.5c, `M68000Cpu.Scc.cs:13`), `PeaExecute`'s
> `-(A7)` push (`M68000Cpu.SystemMisc.cs:51`), the `A7` re-banking view + `SupervisorMode`/`SetSupervisorMode`/
> `SrSupervisorBit` (`M68000Cpu.cs:22-56`), `ComputeEa(pureEa)`, `ReadSized`/`WriteSized`/`ReadLongBus`/
> `WriteLongBus`/`ReadWordBus`/`WriteWordBus`, `SetDataRegPartial`/`DataReg`/`Areg`/`SetAreg`/`SizeMask`, the
> `M68000FetchStream`, and the `M68000TomHarteRunner` Step+diff.

---

## ⚠️ Decisions for Coordinator/user review

> M4.5d-1 is written assuming the ADR 0008 recommended (build-per-ADR) position of each fork. ADR 0008 marks
> **A** and **B** as Coordinator-proceedable autonomously (additive/low-risk) and **C / D-as-extended / E / F**
> as owner sign-off. **C is OUT of scope for M4.5d-1** (it governs the M4.5d-2 timing axis — restated here only
> so the morning checkpoint sees it). **D, E, F are IN scope** and are the load-bearing forks this PR commits
> to. Each is stated with the assumption made (build per ADR rec), the alternative, and that the user ratifies
> in the morning.

### DD1 — Ship M4.5d-1 (control flow + exceptions) as a clean ADDITIVE data-axis PR, separate from the timing axis (ADR 0008 sign-off A) → **YES — built per ADR rec. Confidence: HIGH.**

The most M4.5c-like work in the arc: control flow reuses `EvaluateCondition` (Scc/M4.5c), pushes/pops `A7`
exactly like the proven `PEA`, and its data-axis result (the landed PC, the pushed/popped stack, the
decremented `Dn` for DBcc) is fully diffed by the existing runner. The exception machinery is new but localized
to ONE `RaiseException` routine + new partials. **Touches none of the fetch/bus seam.** The TIMING axis
(`final.pc`/`final.prefetch`/trace/cycle) stays `timingAxis:false` throughout, exactly as M4.5a–c.

- **Assumption (built):** ship M4.5d-1 now as the additive PR.
- **Alternative:** fold control flow + exceptions + timing into one M4.5d PR. *Rejected by ADR 0008 §2* —
  confounds a low-risk additive change with the highest-risk seam break and blocks M4.6 (the JIT) on the hard
  timing axis. The user need not re-ratify A (ADR pre-blessed).

### DD2 — Funnel EVERY exception source through ONE `RaiseException(vector, frameKind)` routine (ADR 0008 sign-off B, decision B) → **YES — built per ADR rec. Confidence: HIGH.**

TRAP/TRAPV/CHK/ILLEGAL/÷0/privilege/address-error all call ONE sequence: capture SR-at-fault → enter supervisor
+ clear trace → push the frame on `-(A7)`(=`-(SSP)`) → vector `PC = Read32(4·vector)`. The privilege gate + the
÷0 detection ALREADY exist (M4.5a's MOVE-to-SR mode check; M4.5b/c's `Div` ÷0 detect-and-return); they just call
into `RaiseException` instead of executing/returning. This is the "integrate WITHOUT scattering" requirement.

- **Assumption (built):** one `RaiseException` + a per-source vector mapping + the small/large frame split.
- **Alternative:** per-op inline exception sequences. *Rejected by ADR 0008 §B* — scatters the
  frame/vector/mode logic (mirrors the M4.5b/c CCR-centralization rationale). User need not re-ratify B.

### DD3 — The ONE seam-listed-file touch: an opt-in `assertExceptions` flag on `M68000TomHarteRunner.RunCase` (ADR 0008 sign-off D) → **built per ADR rec (default FALSE). FLAGGED — the SOLE seam touch in this PR. Confidence: HIGH.**

> **THIS IS THE ONLY SEAM-LISTED FILE M4.5d-1 EDITS.** `M68000TomHarteRunner.cs` is named by the ADR 0007 §5.4
> seam invariant. The edit is **additive and default-off**: it adds a third parameter
> `bool assertExceptions = false` to `RunCase` (current signature `RunCase(M68000TomHarteCase c, bool
> timingAxis = false)`). With the default, `IsExceptionCase(c)` STILL short-circuits to `DeferredException` — so
> the M4.5a–c sweeps (which never pass the flag) stay **byte-for-byte identical**. The M4.5d-1 exception sweep
> (Task 14) passes `assertExceptions:true`, which lets those cases RUN and be diffed on the data axis. The flag
> only STRENGTHENS the gate (deferred→asserted); it never weakens the default-off behavior.

It does NOT touch `DiffBusTrace`, the `timingAxis` path, the data-axis diff logic, the fetch stream
(`M68000FetchStream.cs`), or the `M68000Cpu.cs` bus helpers — so it is **within the spirit** of the seam
invariant (the ADR 0008 classification). But because the seam invariant NAMES this file, it is flagged
explicitly.

- **Assumption (built):** add the opt-in default-FALSE `assertExceptions` flag; the M4.5d-1 exception sweep
  passes `true`; M4.5a–c keep the default.
- **Alternative:** a SEPARATE runner that asserts exceptions. *Rejected by ADR 0008 §D* — duplicates the
  seed/diff logic; the opt-in flag is the minimal change.
- **The user RATIFIES the seam-listed-file edit in the morning** (ADR sign-off D). The Coordinator may treat it
  as part of A if the owner pre-approves the spirit-test.

### DD4 — Address-error (group-0) FRAME CONTENTS: assert "trap taken" only; defer the precise group-0 frame words to M4.5d-2 (ADR 0008 sign-off F) → **DEFERRED per ADR rec. FLAGGED. Confidence: MEDIUM (data-axis verdict pending).**

The 68000 bus/address-error frame (vector 3) is the LARGE frame (group 0): it includes the access address, the
instruction register, and a status word encoding the in-progress bus-cycle state — a TIMING-coupled detail
(*which* part of the bus cycle faulted). M4.5d-1 asserts the COMMON PATH for address error — **the trap is
taken, supervisor entered, the handler PC landed** — but does NOT assert the precise pushed group-0 frame words.
The runner's `IsExceptionCase` heuristic (which keys on a vector read-pair composing to `final.pc`) already
detects "the CPU vectored," so the data-axis assertion for the address-error subset is: SR (supervisor entered),
the landed handler PC, and that a frame WAS pushed (SSP moved) — NOT the frame-word contents.

> **The precise refinement (Task 13 Step 0).** When `assertExceptions:true` un-defers the address-error cases
> embedded in the M4.5a–c files, Builder OBSERVES whether the pushed group-0 frame words match the data-axis diff
> as a side effect. If they match (the status word turns out NOT to be timing-sensitive in the single-step
> corpus), assert them. If the diff on the pushed frame WORDS fails for timing reasons, keep `IsExceptionCase`
> deferring **just the address-error subset** by an extra predicate (the ADR 0008 §3.4 one-line refinement:
> distinguish vector-3 from vector-4/5/6/7/8) — assert the small-frame exceptions, defer the address-error
> frame-word precision to M4.5d-2. **This is resolved EMPIRICALLY in Task 13; the plan assumes the deferral as
> the safe default.**

- **Assumption (built):** assert trap-taken + small-frame fully; defer the address-error large-frame WORD
  contents to M4.5d-2 if the data-axis diff proves timing-sensitive.
- **Alternative:** attempt the full group-0 frame in M4.5d-1. *Risk MEDIUM* — the status word may be
  timing-coupled and fail for M4.5d-2 reasons.
- **The user RATIFIES F in the morning** (the assert-trap-taken-only floor for address error).

### DD5 — The IPL interrupt model: a THIN synthetic-tested stub in M4.5d-1 (ADR 0008 sign-off E) → **built as a thin stub per ADR rec. FLAGGED. Confidence: MEDIUM (no vector to assert it).**

The 680x0 v1 single-step dataset has NO async-interrupt vector (every file is an instruction case), so there is
NOTHING to assert the IPL policy against on the data axis. ADR 0008 §4 leans to a **thin M4.5d-1 stub** so the
functional model is complete: a 3-bit IPL-level input (0–7) + a `TryServiceInterrupt` policy that compares it
against the SR interrupt mask (bits 10-8; level 7 non-maskable), and on `IPL > mask` (or `==7`) runs the
acknowledge sequence by REUSING `RaiseException` (push the (PC, SR) frame, set the mask to the serviced level,
read the **autovector** 24+level, jump). It is validated by SYNTHETIC unit tests only (clearly labeled, the
M4.5b immediate-forms honesty precedent), NOT by a TomHarte vector. The device-supplied-vector path and the
acknowledge-cycle cycle-accuracy are M4.5d-2.

- **Assumption (built):** a thin synthetic-tested IPL stub (the IPL-level input + the `TryServiceInterrupt`
  policy calling `RaiseException`, autovector default), Task 12.
- **Alternative:** defer the WHOLE IPL model to M4.5d-2 (where the timing-aware bus makes the acknowledge trace
  meaningful). Acceptable; keeps M4.5d-1 purely vector-gated. *Risk LOW either way (no vector pressure).*
- **The user RATIFIES E in the morning** — whether "functional-complete with a synthetic-tested IPL" or
  "vector-gated only" is the M4.5d-1 bar. If the user picks the alternative, DROP Task 12 (it is self-contained:
  the stub + its synthetic tests, no body depends on it).

### DD6 (restated, OUT of scope) — The timing-axis tier + staging (ADR 0008 sign-off C).

C governs M4.5d-2 (full per-transaction cycle accuracy tier i vs `final.pc`+`final.prefetch` tier ii, staged).
**M4.5d-1 does NOT touch the timing axis.** Restated here only so the morning checkpoint sees all four ADR forks
in one place. No M4.5d-1 task depends on C.

### DD7 — Which TomHarte vector files cover M4.5d-1 (VERIFIED present per ADR 0008 §2 table) + the exception-case axis.

Two coverage axes (the merge gate, Task 14, runs BOTH):

**(a) The 20 dedicated control-flow/exception vector files** (ADR 0008 §2 "verified present"):

| Family | Files | Count |
|---|---|---|
| Branches | `Bcc` `BSR` `DBcc` | 3 |
| Jumps/returns | `JMP` `JSR` `RTS` `RTR` `RTE` | 5 |
| Stack frame | `LINK` `UNLINK` | 2 |
| Vector/check | `TRAP` `TRAPV` `CHK` | 3 |
| No-op | `NOP` | 1 |
| To-CCR/SR | `ANDItoCCR` `ANDItoSR` `ORItoCCR` `ORItoSR` `EORItoCCR` `EORItoSR` | 6 |

= **20 dedicated files.** (`ILLEGAL` has no dedicated file — its vector-4 path asserts via the un-deferred
illegal cases embedded across the M4.5a–c files, axis (c). `BRA`/`BSR` ride the `Bcc`/`BSR` files. `UNLK`'s
file is named `UNLINK.json.gz`.)

**(b) The ÷0 vector-5 promotion re-run:** the existing `DIVU`/`DIVS` files (2 files) — the M4.5b/c ÷0
detect-and-defer cases now ASSERT (`RaiseException(5, …)`) under `assertExceptions:true`.

**(c) The exception cases EMBEDDED across the M4.5a–c files (un-deferred):** every M4.5a–c vector file (MOVE/
ALU/shift/bit/BCD/Scc/data-movement) contains cases whose real 68000 took a privilege violation (vector 8),
an address error (vector 3), or an illegal instruction (vector 4) — TODAY classified `DeferredException` by
`IsExceptionCase`. Under `assertExceptions:true` these flip deferred→asserted (small-frame fully; address-error
trap-taken per DD4). **This is the "exception-case axis" the d-1 sweep covers** — it is the un-fakeable proof
the exception model is right, run across the WHOLE existing corpus, not just the 20 new files.

**HONESTY notes:**
- Every M4.5d-1 control-flow op (Bcc/BSR/DBcc/JMP/JSR/RTS/RTR/RTE/LINK/UNLK/TRAP/TRAPV/CHK/NOP/the six
  to-CCR/SR) has a dedicated v1 vector file (verified) and IS asserted on the data axis.
- `ILLEGAL` (vector 4) has NO dedicated file; it asserts via the embedded illegal cases (axis c) — disclosed.
- The **IPL model has NO vector** (DD5) — synthetic-tested only, disclosed.
- The **address-error large-frame WORD contents** may defer to M4.5d-2 (DD4) — disclosed.
- The TIMING axis (`final.pc`/`final.prefetch`/trace/cycle) is M4.5d-2 — `timingAxis:false` throughout.

---

## Recon (verified read-only against `main` @ `5661857`)

> All facts confirmed against the merged tree. Builder re-confirms at Task 0. The dispatch is name-driven, so
> opIndices track the dataset automatically. M4.5d-1 adds NO dataset rows (every control-flow row already
> exists, R4), so there is no opIndex shift this PR.

### R1 — The governing decision (ADR 0008 — IMPLEMENT §3 the additive data-axis subset; HOLD §5 the timing axis)
`docs/architecture/0008-68000-control-flow-exceptions-and-the-timing-axis.md`: §2 = the data-axis/timing-axis
PR split (M4.5d-1 = the additive half); §3 = the M4.5d-1 scope this plan tasks out (§3.1 control flow, §3.2 the
exception model + `RaiseException`, §3.3 the to-CCR/SR forms, §3.4 the one runner change); §4 = the IPL thin stub
(DD5); the §2.1 caveat = the address-error frame deferral (DD4); the sign-off block = A/B/C/D/E/F. Confirms
against ADR 0007 §5.4 (the seam invariant — M4.5d-1's SOLE seam touch is the default-off `assertExceptions`
flag, DD3) and ADR 0004 §2 Decision 3 (the vector/privilege/IPL model + the vector assignments).

### R2 — The M4.5a–c seam this plan EXTENDS (do not re-plumb — ADR 0007 §5.4)
- **The generated FieldGrammar `Step` arm** (`CpuEmitter.cs:218-252`): fetches the operword + extension words via
  `M68000FetchStream`, charges fetch cycles, sets `_eaPcBase = PC + 2u`, **advances `PC += __r.Length` BEFORE
  dispatch**, then dispatches by `__opIndex` via `EmitMoveDispatchArms`, passing
  `(__operword, __r, __size, __srcMode, __srcReg)`. **LOAD-BEARING for M4.5d-1:** because PC is already advanced
  past the whole instruction when the body runs, the BSR/JSR **return PC** and the exception **pcAtFault** are
  simply the current `PC` (no manual length math in the bodies). M4.5d-1 adds arms; the Step shape is unchanged.
- **`EmitMoveDispatchArms`** (`CpuEmitter.cs:4262-4337`): the name-driven `op switch` (carries MOVE + the 30 ALU
  names + the 27 M4.5c names). M4.5d-1 adds the control-flow/exception operation names → their `*Execute` hooks
  here (Task 11).
- **The partial-hook declaration emit** (`CpuEmitter.cs:306-339`, inside `if (model.FieldGrammar is not null)`):
  the MOVE/ALU/M4.5c `foreach` blocks (`:322-338`). M4.5d-1 adds a sibling `foreach` block (Task 11).
- **The fetch stream** (`M68000FetchStream.cs`), **the wide-bus helpers** (`M68000Cpu.cs:79-104`),
  `M68000Cpu.Move.cs`, `M68000Cpu.Alu.cs` — UNTOUCHED (seam invariant). **`M68000TomHarteRunner.cs`** — the SOLE
  seam touch: the default-FALSE `assertExceptions` flag (DD3, Task 13). M4.5d-1 is otherwise a new CALLER only.

### R3 — The merged substrate M4.5d-1 REUSES (verbatim)
- **`EvaluateCondition(uint cc, byte ccr) -> bool`** (`M68000Cpu.Scc.cs:13`, `private static`): the 16 cc codes.
  Reused VERBATIM by Bcc (the 14 conditionals + T/F for BRA) and DBcc. Already on the class → reachable from a
  sibling partial. `EvaluateConditionProbe` (`:36`) is the test seam.
- **The `-(A7)` push** (`PeaExecute`, `M68000Cpu.SystemMisc.cs:51-57`): `uint sp = A7 - 4u; A7 = sp;
  WriteLongBus(sp, ea);` — the EXACT mechanism BSR/JSR return-push, LINK frame-push, and the exception frame-push
  reuse. `A7` (`M68000Cpu.cs:52`) re-banks USP/SSP by `SupervisorMode` automatically.
- **`SupervisorMode`** (`M68000Cpu.cs:32`) / **`SetSupervisorMode`** (`:36`) / **`SrSupervisorBit`** (`:22`,
  `const ushort 1<<13`): the privilege test + the S-bit toggle. Writing `SR` re-banks `A7` (the USP/SSP swap is
  free). The `SR` field + the `Ccr` property (`:41`) + `USP`/`SSP` (generated).
- **The wide bus:** `ReadLongBus`/`WriteLongBus`/`ReadWordBus`/`WriteWordBus` (`M68000Cpu.cs:79-104`),
  `ReadSized`/`WriteSized` (`M68000Cpu.Alu.cs:462-464`), `ReadByteAt`/`WriteByteAt` (`M68000Cpu.Move.cs:37-38`).
  The vector read is `ReadLongBus(4u * vector)`.
- **`ComputeEa(uint eaMode, uint eaReg, uint size, ExtensionWords ext, bool pureEa)`** (generated,
  `CpuEmitter.cs:4446`): `pureEa:true` for JMP/JSR (a Control EA, never dereferenced — the LEA/PEA precedent).
  `ExtensionWords.None` (`DecodeResult.cs:27`). `_eaPcBase`/`PcForEa` (`:4493-4494`) drive PC-relative EAs.
- **`DataReg(uint)`/`SetDataRegPartial(uint,uint,uint)`/`Areg(uint)`/`SetAreg(uint,uint)`/`SizeMask(uint)`**
  (generated + `M68000Cpu.Move.cs:19`): the register file + the partial-write helper (DBcc's `.w` decrement).
- **The ÷0 detect-and-defer point** (`Div`, `M68000Cpu.Alu.cs:495-500`): `if (divisorW == 0u) return;` — M4.5d-1
  replaces the bare `return` with `RaiseException(5, …)` (the detection stays; only the vectoring is added,
  Task 9).
- **The privilege detection points** (`MoveToSrExecute`, `M68000Cpu.Move.cs:122`; `MoveUspExecute`, `:151`):
  M4.5a's `// PRIVILEGED` comments mark where the gate goes. M4.5d-1 makes them call `RaiseException(8, …)` when
  `!SupervisorMode`, and adds the SAME gate to RTE/`*toSR`/STOP/RESET (Task 8).

### R4 — The FieldGrammar dataset M4.5d-1 rows (verified PRESENT, `data/m68000-fieldgrammar.json` — NO new rows)
Every control-flow/exception row already exists (M4.5d-1 adds bodies + arms only, NO dataset edit, NO opIndex
shift):

| Operation string | line | mask / match | notes |
|---|---|---|---|
| `Bcc` | 73 | 0xF000 / 0x6000 | cond 11-8 (0000=BRA, 0001=BSR, 0010-1111=conditional); disp 7-0 (+disp word when 0x00) |
| `DBcc` | 68 | 0xF0F8 / 0x50C8 | cond 11-8, Dn 2-0, +1 disp word |
| `JMP` | 53 | 0xFFC0 / 0x4EC0 | legalEa Control, pureEa |
| `JSR` | 54 | 0xFFC0 / 0x4E80 | legalEa Control, pureEa |
| `RTS` | 33 | 0xFFFF / 0x4E75 | pop PC |
| `RTR` | 34 | 0xFFFF / 0x4E77 | pop CCR-word then PC |
| `RTE` | 32 | 0xFFFF / 0x4E73 | PRIVILEGED: pop SR then PC |
| `LINK` | 41 | 0xFFF8 / 0x4E50 | +1 disp word |
| `UNLK` | 42 | 0xFFF8 / 0x4E58 | |
| `TRAP` | 44 | 0xFFF0 / 0x4E40 | TRAP #v (v = bits 3-0 → vector 32+v) |
| `TRAPV` | 31 | 0xFFFF / 0x4E76 | vector 7 when V set |
| `CHK` | 59 | 0xF1C0 / 0x4180 | vector 6 when Dn out of [0, bound] |
| `ILLEGAL` | 38 | 0xFFFF / 0x4AFC | vector 4 |
| `NOP` | 36 | 0xFFFF / 0x4E71 | no state change |
| `RESET` | 35 | 0xFFFF / 0x4E70 | PRIVILEGED (no-op on the data axis bar the privilege gate) |
| `STOP` | 37 | 0xFFFF / 0x4E72 | PRIVILEGED, +1 imm word (loads SR) |
| `ORI_CCR`/`ORI_SR` | 2/3 | 0xFFFF / 0x003C,0x007C | +1 imm word; _SR PRIVILEGED |
| `ANDI_CCR`/`ANDI_SR` | 4/5 | 0xFFFF / 0x023C,0x027C | +1 imm word; _SR PRIVILEGED |
| `EORI_CCR`/`EORI_SR` | 6/7 | 0xFFFF / 0x0A3C,0x0A7C | +1 imm word; _SR PRIVILEGED |

> **Decode notes (Builder confirms against vectors at recon):** (1) `Bcc`'s `.w` form (disp field == 0x00 → a
> following 16-bit displacement word) was deferred from M4.4a (ADR 0008 §3.1). The dataset row's `sizeWidth:1`
> may not yield the +1 disp word for the disp==0 case automatically — confirm empirically at Task 2 Step 0 (the
> M4.5b/c leading-imm-word precedent). The `.l` form (disp == 0xFF) is 68020+ → ILLEGAL on the 68000 (vector 4).
> (2) `STOP`/`*toSR`/`*toCCR` carry a +1 imm word; the dataset marks them `sizeWidth:1` (the M4.5b immediate
> precedent — confirm `ExtensionWords[0]` holds the imm). (3) `TRAP`'s vector = 32 + (operword & 0xF).

### R5 — The runner's exception machinery (the SOLE seam touch, DD3)
`M68000TomHarteRunner` (`tests/.../TomHarte/M68000TomHarteRunner.cs`): `IsExceptionCase(c)` (`:44`) returns true
when the case's transactions show an aligned vector-read pair (at `4·v, 4·v+2`, `v < 0x100`) composing to
`final.pc` — the un-fakeable "the CPU fetched a handler and jumped" signal. `RunCase(c, timingAxis=false)`
(`:70`) short-circuits to `DeferredException` (`:68`, `:77`) for any exception case. M4.5d-1 adds the
default-FALSE `assertExceptions` parameter so the M4.5d-1 sweep un-defers; M4.5a–c keep the default. The seed/
diff (`:79-139`) — register seed, `Step()`, the data-axis diff (D0-D7/A0-A6/USP/SSP/SR/RAM) — is UNCHANGED.

---

## Scope

**IN scope (ALL tasked at uniform fidelity below — ONE PR, data axis only):**
1. Branches: `Bcc`/`BSR`/`BRA` (the Bcc row sub-dispatches on cond 11-8) + `DBcc` + the shared `EvaluateCondition`
   reuse — Tasks 2-3.
2. Jumps/returns: `JMP`/`JSR`/`RTS`/`RTR`/`RTE` — Tasks 4-5.
3. Stack frame: `LINK`/`UNLK` — Task 6.
4. The exception model: ONE `RaiseException(vector, frameKind)` + the vector table + the small/large frame split
   + the S-bit/USP-SSP swap + the privilege gate — Task 7 (the highest-risk new code).
5. Vector/check ops: `TRAP`/`TRAPV`/`CHK`/`ILLEGAL` (+ `NOP`/`RESET`/`STOP` data-axis behavior) — Task 8.
6. The privilege promotions: RTE/`*toSR`/STOP/RESET + the M4.5a MOVE-to-SR/MOVE-USP gates → `RaiseException(8)`
   — Task 8 (folded with the vector ops).
7. The ÷0 vector-5 promotion (the M4.5b/c detect-and-defer becomes a real exception) — Task 9.
8. The to-CCR/SR forms: `ANDItoCCR`/`ORItoCCR`/`EORItoCCR` (unprivileged) + `ANDItoSR`/`ORItoSR`/`EORItoSR`
   (privileged) — Task 10.
9. The generator dispatch arms + partial-hook declarations for ALL of the above — Task 11.
10. The IPL thin synthetic-tested stub (DD5, droppable if the user picks E-alternative) — Task 12.
11. The default-FALSE `assertExceptions` runner flag (DD3 — the SOLE seam touch) — Task 13.
12. The single M4.5d-1 TomHarte data-axis sweep with `assertExceptions=true` (20 dedicated + the DIVU/DIVS ÷0
    re-run + the embedded exception-case re-run across M4.5a–c) — Task 14 (the gate).

**OUT of scope (M4.5d-2 — HELD for the owner per ADR 0008 §5):** the TIMING axis (`final.pc`/`final.prefetch`/
per-transaction trace/cycle) for ALL families (`timingAxis:false` throughout); the prefetch-queue mechanism +
the stateful `M68000FetchStream` rewrite; the bus-helper/Step cycle-model rework; the address-error large-frame
WORD contents IF timing-sensitive (DD4); the IPL acknowledge-cycle cycle-accuracy + the device-supplied-vector
path (DD5); turning `assertExceptions`/`timingAxis` on by default. The 68000 through the JIT is M4.6 (depends on
M4.5d-1's functional model, NOT on M4.5d-2 — ADR 0008 §6).

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Control.cs` | Create | Bcc/BSR/BRA/DBcc, JMP/JSR/RTS/RTR/RTE, LINK/UNLK (the M4.5c-like control core). |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Exceptions.cs` | Create | ONE `RaiseException(vector, frameKind)` + the vector table + the small/large frame split + the privilege gate helper; TRAP/TRAPV/CHK/ILLEGAL/NOP/RESET/STOP; the ÷0 + privilege + to-SR/CCR callers; the IPL stub. |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs` | **Modify (ALLOWED — NOT a seam file; M4.5b body)** | ONE line in `Div`: replace the ÷0 `return;` with `RaiseException(5, …)` (Task 9). *(See the seam note: `M68000Cpu.Alu.cs` was DO-NOT-TOUCH in M4.5c only because M4.5c reused it as a caller; ADR 0008 §3.2's ÷0 promotion EXPLICITLY edits the `Div` body. This is NOT a seam-invariant file — the seam names the fetch stream, the bus helpers, and the runner. Flagged so the diff is expected.)* |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Move.cs` | **Modify (ALLOWED — NOT a seam file; M4.5a body)** | The MOVE-to-SR / MOVE-USP privilege gates call `RaiseException(8, …)` when `!SupervisorMode` (Task 8). *(Same note: a body file, not a seam file. Flagged.)* |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.cs` | **Modify (NON-seam parts only)** | The IPL-level input field + `TryServiceInterrupt`/`InterruptPending` un-stub (Task 12, DD5). **Do NOT touch the bus helpers (`ReadWordBus`/`WriteWordBus`/`ReadLongBus`/`WriteLongBus`, `:79-104`) — those ARE the seam.** Flagged: only the interrupt-policy hooks (`:64-65`, `:118-121`) change; the bus helpers stay byte-identical. |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | Extend `EmitMoveDispatchArms` + the hook-declaration emit with ALL M4.5d-1 names (Task 11). Possibly the `Bcc.w`/`*toSR` leading-disp/imm-word decode arm (Task 2/10 Step 0, if red). |
| `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs` | **Modify (the SOLE seam-listed-file touch, DD3)** | Add `bool assertExceptions = false` to `RunCase`; gate the `IsExceptionCase` short-circuit on `!assertExceptions`. Default-off preserves M4.5a–c byte-for-byte. |
| `tests/.../Generators/M68000ControlExecuteTests.cs` `…ExceptionTests.cs` | Create | Synthetic execute + `RaiseException` + privilege + IPL unit tests (no vectors). |
| `tests/.../TomHarte/M68000M45d1TomHarteTests.cs` | Create | The skip-when-absent `[M68000TomHarteTheory]` over the 20 dedicated + the DIVU/DIVS files, with `assertExceptions:true`. |
| `tests/.../TomHarte/M68000M45cTomHarteTests.cs` `…AluTomHarteTests.cs` | Modify (test-only) | Re-run with `assertExceptions:true` to assert the embedded exception cases (axis c), OR a dedicated cross-corpus exception sweep — Task 14. |

---

## TDD tasks (ordered; the suite stays green after each; literal code for every load-bearing piece)

> **Hoist Task 11 (generator) EARLY** — right after Task 2 establishes the first control body — so all
> `partial void` declarations exist before Tasks 3-10 compile (the bodies are no-op `partial void` until filled
> — the M4.5b/c precedent). Task 7 (`RaiseException`) is built BEFORE its callers (Tasks 8-10) so they compile
> against a real routine. The single heavy gate is Task 14.

---

### Task 0: Baseline + recon (NO code change)

- [ ] **Step 1: Branch off `main`.** `git switch -c feat/m4-5d-1-control-flow-exceptions`. Confirm `5661857`.
  Confirm M4.5c present (`M68000Cpu.Scc.cs` with `EvaluateCondition`; `M68000Cpu.SystemMisc.cs` with
  `PeaExecute`; ADR 0008 present).
- [ ] **Step 2: Green baseline.** `dotnet test` → 0 failures (record the count). `dotnet build
  --no-incremental -warnaserror` → clean.
- [ ] **Step 3: Recon (read-only).** The Step arm (`CpuEmitter.cs:218-252` — confirm PC is advanced BEFORE
  dispatch, so the return PC = current PC); `EmitMoveDispatchArms` (`:4262`) + the hook `foreach` (`:322-338`);
  the reused substrate (R3) reachable from sibling partials (`EvaluateCondition`/`SupervisorMode`/`A7`/the bus
  helpers/`ComputeEa`/`Div`'s ÷0 point → yes); the dataset rows (R4 — all PRESENT, NO new row); the 20 dedicated
  + DIVU/DIVS vector files present; the runner's `IsExceptionCase`/`RunCase` (R5).
- [ ] **Step 4:** No commit. Proceed to Task 1.

---

### Task 1: The exception vector table + the frame-kind enum (the shared constants)

> Establish the vector assignments + the small/large frame distinction ONCE, so `RaiseException` (Task 7) and
> every caller reference named constants, not magic numbers. Pure data; no behavior.

**Files:** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.Exceptions.cs` (the scaffold + the enum).

- [ ] **Step 1: Create the scaffold:**

```csharp
namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5d-1 (ADR 0008 §3.2 — decision B): the 68000 exception model. ONE RaiseException(vector, frameKind)
/// routine funnels EVERY synchronous exception source (TRAP/TRAPV/CHK/ILLEGAL/÷0/privilege/address-error) and
/// the IPL interrupt acknowledge (DD5) — "integrate WITHOUT scattering". The sequence: capture SR-at-fault →
/// enter supervisor + clear trace (writing SR re-banks A7 to SSP automatically — the USP/SSP swap is free) →
/// push the frame on -(A7) (= -(SSP), the proven PEA mechanism) → PC = Read32(4·vector). Small frame (group
/// 1/2: PC long + SR word, 6 bytes) covers TRAP/TRAPV/CHK/ILLEGAL/÷0/privilege; large frame (group 0:
/// address error) adds access-info words whose exact contents may defer to M4.5d-2 (DD4 — assert trap-taken).
/// The TIMING axis (the exact cycle count of the sequence) is M4.5d-2; the DATA result here is frame + mode +
/// handler PC. Reuses the merged substrate (A7, WriteLongBus/WriteWordBus, ReadLongBus, SupervisorMode,
/// SrSupervisorBit); the fetch/bus SEAM is untouched (ADR 0007 §5.4).
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>The 68000 trace bit (SR bit 15). RaiseException clears it on entry.</summary>
    private const ushort SrTraceBit = 1 << 15;

    /// <summary>The 68000 exception vector assignments (ADR 0004 §2 Decision 3). The vector NUMBER; the table
    /// entry is at byte address 4·vector. reset(0/1) is not exercised by single-step vectors.</summary>
    private static class Vector
    {
        public const uint BusError = 2;
        public const uint AddressError = 3;
        public const uint Illegal = 4;
        public const uint DivideByZero = 5;
        public const uint Chk = 6;
        public const uint TrapV = 7;
        public const uint Privilege = 8;
        public const uint Trace = 9;
        public const uint TrapBase = 32;       // TRAP #n -> 32 + n
        public const uint AutovectorBase = 24; // IPL level L (1-7) -> 24 + L (DD5)
    }

    /// <summary>Small frame (group 1/2): PC + SR, 6 bytes. Large frame (group 0: address/bus error): the access-
    /// info words too (DD4 — M4.5d-1 asserts trap-taken; the precise contents may defer to M4.5d-2).</summary>
    private enum FrameKind { Small, Large }
}
```

- [ ] **Step 2:** Build (data only; no behavior). `dotnet test` unaffected. Commit
  (`feat(m68000): the 68000 exception vector table + frame-kind scaffold (M4.5d-1)`). **Est:** 0 new tests.

---

### Task 2: Bcc/BSR/BRA — the branch family (TDD)

> The Bcc dataset row (0xF000/0x6000) sub-dispatches on the CONDITION field (bits 11-8): 0000=BRA (always),
> 0001=BSR (push the return PC, then branch), 0010-1111 = the 14 conditionals via `EvaluateCondition`. The
> displacement is the 8-bit field (bits 7-0); when 0x00, a following 16-bit displacement word (`Bcc.w`); 0xFF
> (`.l`) is 68020+ → ILLEGAL (vector 4). **The branch base is the operword-address + 2** (the displacement is
> relative to the PC AFTER the operword) — capture it via `_eaPcBase` (the generated Step set it = operword+2).

**Files:** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.Control.cs`; create
`tests/CpuEmulator.Tests/Generators/M68000ControlExecuteTests.cs`.

- [ ] **Step 0: Confirm the `Bcc.w` disp-word decode.** Run a `Bcc_w_decode_captures_disp_word` test (operword
  `0x6000` with disp==0 → `ExtensionWords[0]` = the 16-bit displacement). If RED, add `Bcc` (when the disp byte
  is 0) to the leading-disp-word set in the generator's decode walk (the M4.5b leading-imm-word precedent). The
  8-bit form needs no extension word.
- [ ] **Step 1: Write the failing/skip tests** (`[Fact(Skip="dispatch wired in Task 11")]` until Task 11):
  BRA (cond 0) lands at base+disp8; BSR pushes the return PC then branches; a taken conditional (e.g. BEQ with
  Z set); a NOT-taken conditional (PC = the next instruction, no branch); the `.w` form (disp==0 → the disp
  word). Drive via `cpu.Step()` (mirror the M4.5c `M68000ControlExecuteTests` `Build(...)` shape).
- [ ] **Step 2: Create `M68000Cpu.Control.cs` with the branch body:**

```csharp
namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5d-1 (ADR 0008 §3.1): the control-flow core — Bcc/BSR/BRA, DBcc, JMP/JSR/RTS/RTR/RTE, LINK/UNLK. The most
/// M4.5c-like work in the arc: reuses EvaluateCondition (the shared cc evaluator, M68000Cpu.Scc.cs), pushes/pops
/// A7 exactly like the proven PEA, and the data-axis result (the landed PC, the pushed/popped stack, the
/// decremented Dn for DBcc) is fully diffed by the existing runner. RTE is privileged (vector 8 via
/// RaiseException when !SupervisorMode); writing the popped SR re-banks A7 (the USP/SSP swap is free). The TIMING
/// axis (final.pc/prefetch/trace) is M4.5d-2. Seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>The branch base = the address of the FIRST extension word = operword + 2. The generated Step
    /// set _eaPcBase = PC+2 BEFORE advancing PC, so PcForEa is that base; but _eaPcBase is cleared to 0 after
    /// dispatch, and the body runs DURING dispatch, so PcForEa is valid here. (For an 8-bit branch with no
    /// extension word, the base is still operword+2 — the displacement is relative to it.)</summary>
    partial void BccExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint cc = (operword >> 8) & 0xFu;
        uint disp8 = operword & 0xFFu;
        int disp;
        if (disp8 == 0x00u)            // Bcc.w : the 16-bit displacement word
            disp = (short)r.ExtensionWords[0];
        else if (disp8 == 0xFFu)       // Bcc.l : 68020+ -> ILLEGAL on the 68000
        { RaiseException(Vector.Illegal, FrameKind.Small, (ushort)(SR & 0xFFFF), PC); return; }
        else
            disp = (sbyte)(byte)disp8; // Bcc.b : the 8-bit displacement

        uint branchBase = PcForEa;     // = operword + 2 (the displacement origin)
        uint target = unchecked(branchBase + (uint)disp);

        if (cc == 0x1u)                // BSR: push the RETURN pc (the post-advance PC), then branch
        {
            uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, PC);
            PC = target; return;
        }
        if (cc == 0x0u || EvaluateCondition(cc, (byte)(SR & 0xFF)))   // BRA (cc 0) or a taken conditional
            PC = target;
        // a NOT-taken conditional: PC already points past the instruction (the generated Step advanced it).
    }
}
```

  > **Notes (Builder resolves against vectors):** (1) the BSR return PC is the post-advance `PC` (the generated
  > Step advanced past the operword + the `.w` disp word). (2) `PcForEa` returns `_eaPcBase` (= operword+2) while
  > a body runs — confirm it is NON-zero here (the Step clears it AFTER dispatch). If `PcForEa` is unreliable
  > mid-body, the base is `PC - r.Length + 2` (operword address + 2) — compute from the post-advance PC and the
  > decoded length. Pin against the `Bcc`/`BSR` vectors (Task 14). (3) the `.w` form's disp word is captured at
  > Step 0.

- [ ] **Step 3:** Build (compiles against `RaiseException` once Task 7 lands; until then, comment the ILLEGAL
  arm or stub `RaiseException` — but Task 7 is hoisted before Task 8, and Task 2's ILLEGAL path is the only early
  `RaiseException` use; if ordering bites, move Task 7 ahead of Task 2). **Step 4:** (after Task 11) un-skip →
  PASS the synthetic tests. **Step 5:** Full gate. **Step 6:** Commit. **Est:** ~6.

---

### Task 3: DBcc — decrement-and-branch (TDD)

> DBcc (0xF0F8/0x50C8): if the condition is FALSE, decrement Dn.w (the low 16 bits) and branch if Dn.w != -1
> (0xFFFF); if the condition is TRUE, fall through (no decrement, no branch). +1 displacement word. The classic
> bug: -1 terminates, NOT 0.

**Files:** Modify `M68000Cpu.Control.cs`; modify `M68000ControlExecuteTests.cs`.

- [ ] **Step 1: Write the failing/skip tests** — condition TRUE (fall through, Dn unchanged); condition FALSE
  with Dn.w = 5 (→ 4, branch); condition FALSE with Dn.w = 0 (→ 0xFFFF, branch — NOT terminate); condition
  FALSE with Dn.w = 0xFFFF... wait, that decrements to 0xFFFE; the terminate case is Dn.w==0 → 0xFFFF then STOP
  branching. Pin the off-by-one explicitly.
- [ ] **Step 2: Add the body:**

```csharp
    partial void DBccExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint cc = (operword >> 8) & 0xFu;
        uint dn = operword & 7u;
        uint branchBase = PcForEa;                 // = operword + 2 (the disp word's origin)
        int disp = (short)r.ExtensionWords[0];     // the +1 displacement word

        if (EvaluateCondition(cc, (byte)(SR & 0xFF)))
            return;                                // condition true: fall through (PC already past the insn)

        ushort counter = (ushort)(DataReg(dn) & 0xFFFFu);
        counter = (ushort)(counter - 1);           // decrement Dn.w
        SetDataRegPartial(dn, counter, 1u);        // .w partial write (upper word preserved)
        if (counter != 0xFFFFu)                    // branch unless the counter ran out (-1, NOT 0)
            PC = unchecked(branchBase + (uint)disp);
        // counter == 0xFFFF: loop terminates, PC stays past the instruction.
    }
```

- [ ] **Step 3-6:** failing-test → green (post Task 11) → full gate → commit. **Est:** ~4.

---

### Task 4: JMP/JSR — unconditional jump + call (TDD)

> JMP (0x4EC0)/JSR (0x4E80), legalEa Control: compute the EA via `ComputeEa(pureEa:true)` (a control EA, never
> dereferenced — the LEA/PEA precedent), set PC to it; JSR FIRST pushes the return PC to -(A7).

**Files:** Modify `M68000Cpu.Control.cs`; modify `M68000ControlExecuteTests.cs`.

- [ ] **Step 1: Write the failing/skip tests** — JMP (An) sets PC = An; JMP d16(An) sets PC = An+disp; JSR
  pushes the return PC then jumps (the pushed long = the post-advance PC).
- [ ] **Step 2: Add the bodies:**

```csharp
    partial void JmpExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint ea = ComputeEa(srcMode, srcReg, 2u, r.ExtensionWords, pureEa: true);   // control EA (no deref)
        PC = ea;
    }

    partial void JsrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint ea = ComputeEa(srcMode, srcReg, 2u, r.ExtensionWords, pureEa: true);
        uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, PC);   // push the RETURN pc (post-advance PC) -(A7)
        PC = ea;
    }
```

  > **Note:** the return PC is the post-advance `PC` (the generated Step advanced past the operword + the EA's
  > extension words). Confirm against the `JSR` vectors that the pushed long equals `final` PC-of-next +
  > extension-word count (Task 14).

- [ ] **Step 3-6:** as Task 3. **Est:** ~4.

---

### Task 5: RTS/RTR/RTE — the return family (TDD)

> RTS (0x4E75): pop PC from (A7)+. RTR (0x4E77): pop a word (the low byte → CCR) then pop PC. RTE (0x4E73) is
> PRIVILEGED: if !SupervisorMode → RaiseException(8); else pop SR (full 16 bits — re-banks A7 automatically) then
> pop PC.

**Files:** Modify `M68000Cpu.Control.cs`; modify `M68000ControlExecuteTests.cs`.

- [ ] **Step 1: Write the failing/skip tests** — RTS pops PC + advances A7 by 4; RTR pops CCR (low byte of the
  word) then PC (+6); RTE in supervisor pops SR then PC (+6, A7 re-banks if S flips); RTE in user mode →
  privilege violation (deferred/asserted per the runner flag — synthetic: assert it calls RaiseException(8)).
- [ ] **Step 2: Add the bodies:**

```csharp
    partial void RtsExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint sp = A7;
        PC = ReadLongBus(sp);          // pop PC
        A7 = sp + 4u;
    }

    partial void RtrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint sp = A7;
        ushort w = ReadWordBus(sp);    // pop the CCR word; only the low byte (X N Z V C) is restored
        Ccr = (byte)(w & 0x1Fu);
        PC = ReadLongBus(sp + 2u);     // then pop PC
        A7 = sp + 6u;
    }

    partial void RteExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if (!SupervisorMode) { RaiseException(Vector.Privilege, FrameKind.Small, (ushort)(SR & 0xFFFF), PC); return; }
        uint sp = SSP;                 // RTE always un-stacks from the SSP (supervisor)
        ushort restoredSr = ReadWordBus(sp);    // pop SR (full 16 bits — mode + CCR)
        uint restoredPc = ReadLongBus(sp + 2u); // pop PC
        SSP = sp + 6u;                 // advance the supervisor stack BEFORE writing SR (which may flip the bank)
        SR = (ushort)(restoredSr & 0xA71Fu);    // SR_VALID mask (the M4.5a MOVE-to-SR precedent); re-banks A7
        PC = restoredPc;
    }
```

  > **Notes:** (1) RTE writes `SSP` directly (not `A7`) for the pops because the pop happens in supervisor mode;
  > the SSP advance must complete BEFORE the `SR` write flips the S-bit and re-banks `A7` (otherwise the advance
  > lands on the wrong bank). (2) the `0xA71Fu` SR_VALID mask matches `MoveToSrExecute` (`M68000Cpu.Move.cs:131`)
  > — confirm against the `RTE` vectors. (3) RTR restores only the CCR low byte (bits 0-4), not the system byte
  > (it is unprivileged).

- [ ] **Step 3-6:** as Task 3 (RTE depends on `RaiseException`, Task 7 — hoist Task 7 before Task 5 if the
  build bites; see the task ordering note). **Est:** ~6.

---

### Task 6: LINK/UNLK — the stack frame (TDD)

> LINK An,#disp (0x4E50): push An to -(A7), set An = A7, then A7 += disp (a signed +1 word). UNLK An (0x4E58):
> set A7 = An, then pop An from (A7)+. Pure stack discipline — the PEA push mechanism.

**Files:** Modify `M68000Cpu.Control.cs`; modify `M68000ControlExecuteTests.cs`.

- [ ] **Step 1: Write the failing/skip tests** — LINK A0,#-8 pushes A0, sets A0 = new A7, A7 -= 8; UNLK A0
  restores A7 = A0 then pops A0; the LINK/UNLK round-trip restores the original A7 + An.
- [ ] **Step 2: Add the bodies:**

```csharp
    partial void LinkExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint an = operword & 7u;
        int disp = (short)r.ExtensionWords[0];     // the +1 signed displacement word
        uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, Areg(an));   // push An -(A7)
        SetAreg(an, A7);                           // An = the new A7 (the frame pointer)
        A7 = unchecked(A7 + (uint)disp);           // allocate the frame (disp is typically negative)
    }

    partial void UnlkExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint an = operword & 7u;
        uint sp = Areg(an);          // A7 = An (deallocate the frame)
        SetAreg(an, ReadLongBus(sp));// pop the saved An from (A7)+
        A7 = sp + 4u;
    }
```

  > **Note:** the `LINK An` where An == A7 edge (a7-as-the-frame-pointer) — the push-then-set order matters;
  > confirm against the `LINK` vectors.

- [ ] **Step 3-6:** as Task 3. **Est:** ~4.

---

### Task 7: `RaiseException` — the ONE exception routine (TDD — the HIGHEST-risk new code)

> The load-bearing centralization (ADR 0008 decision B). ONE routine all sources funnel through. Built BEFORE
> its callers (Tasks 8-10) so they compile against it. The small frame (PC long + SR word) covers
> TRAP/TRAPV/CHK/ILLEGAL/÷0/privilege; the large frame (address error) adds access-info words (DD4 — M4.5d-1
> pushes the small frame for the common path; the precise group-0 contents may defer to M4.5d-2).

**Files:** Modify `M68000Cpu.Exceptions.cs`; create `tests/CpuEmulator.Tests/Generators/M68000ExceptionTests.cs`.

- [ ] **Step 1: Write the failing tests** (`M68000ExceptionTests.cs`, NOT skipped — `RaiseException` is callable
  directly via a test seam): seed SR (user mode, some CCR), a vector-5 ÷0 raise; assert (a) SupervisorMode is
  now set, (b) the trace bit cleared, (c) SSP decremented by 6, (d) the pushed frame = the pre-fault PC (long) +
  the pre-fault SR (word), (e) PC = the vector-table entry `Read32(4·5)`, (f) USP unchanged. A second test:
  raise from supervisor mode (S already set) — SSP still used, mode stays supervisor.
- [ ] **Step 2: Add `RaiseException` + the test seam to `M68000Cpu.Exceptions.cs`:**

```csharp
    /// <summary>The 68000 exception sequence (decision B — ONE routine for EVERY synchronous source + the IPL
    /// acknowledge). srAtFault = the SR captured at the point of fault (BEFORE the mode change); pcAtFault = the
    /// PC to stack (the post-advance PC for software traps; the faulting-instruction PC for group-0 — DD4).
    /// Steps: (1) enter supervisor + clear trace (writing SR re-banks A7 to SSP automatically); (2) push the
    /// frame on -(A7) (= -(SSP)); (3) PC = Read32(4·vector). The TIMING axis (the exact cycle count) is M4.5d-2;
    /// the DATA result is frame + mode + handler PC.</summary>
    private void RaiseException(uint vector, FrameKind frameKind, ushort srAtFault, uint pcAtFault)
    {
        // 1. Enter supervisor mode + clear the trace bit. Writing SR re-banks A7 -> SSP (the USP/SSP swap is
        //    free; ADR 0008 §1.1). The CCR (low byte) of srAtFault carries forward — only S is forced, T cleared.
        SR = (ushort)((srAtFault | SrSupervisorBit) & ~SrTraceBit);

        // 2. Push the frame on -(A7) (= -(SSP)). Small frame (group 1/2): PC (long) then SR (word) = 6 bytes,
        //    PC pushed FIRST (higher address), SR on top (lower address) — the 68000 stacks PC then SR so the
        //    SR ends at the lowest address. (Push order: SR last => SR at -(A7) top.)
        if (frameKind == FrameKind.Large)
        {
            // DD4: the group-0 large frame adds access-info words whose precise contents are timing-coupled
            // (M4.5d-2). M4.5d-1 asserts only "trap taken" for address error — push the small frame's PC+SR so
            // the mode + handler-PC + SSP-moved data axis is satisfied; the extra access words defer to M4.5d-2.
            // (If Task 13 Step 0 finds the words ARE data-axis-stable in the corpus, extend here.)
        }
        uint sp = A7 - 4u; A7 = sp; WriteLongBus(sp, pcAtFault);   // push PC (long)
        sp = A7 - 2u; A7 = sp; WriteWordBus(sp, srAtFault);        // push SR (word) -> SR at the lowest address

        // 3. Vector through the table (VectorBase = 0): PC = the 32-bit handler at 4·vector.
        PC = ReadLongBus(4u * vector);
    }

    /// <summary>Test seam: drive RaiseException from a synthetic unit test (the ComputeEaProbe precedent).</summary>
    public void RaiseExceptionProbe(uint vector, bool large, ushort srAtFault, uint pcAtFault)
        => RaiseException(vector, large ? FrameKind.Large : FrameKind.Small, srAtFault, pcAtFault);

    /// <summary>The privilege gate: if in user mode, raise a privilege violation (vector 8) and return true (the
    /// caller must NOT execute). Centralizes the "integrate without scattering" privilege check (ADR 0008 §3.2).
    /// </summary>
    private bool TrapIfUserMode()
    {
        if (SupervisorMode) return false;
        RaiseException(Vector.Privilege, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);
        return true;
    }
```

  > **The frame STACK ORDER is the subtlest part** (after the mode/bank interaction). The 68000 small frame is
  > [SR @ SSP, PC @ SSP+2] — SR at the lowest address, PC above it. Pushed via two -(A7): PC first (lands at
  > A7-4), SR second (lands at A7-6). Confirm the byte layout + the SSP delta (6) against the un-deferred TRAP/
  > privilege/÷0 cases (Task 14). **Reconcile frame failures HERE (one place), never in the callers.**

- [ ] **Step 3: Run to verify it passes** the synthetic raise tests. **Step 4-6:** full gate → commit
  (`feat(m68000): the ONE RaiseException routine + the privilege gate (M4.5d-1)`). **Est:** ~5.

---

### Task 8: TRAP/TRAPV/CHK/ILLEGAL + NOP/RESET/STOP + the privilege promotions (TDD)

> The software/check exception sources, each mapping to its vector + calling RaiseException. NOP is a true no-op
> (data axis trivially green). RESET/STOP are privileged (the gate). The M4.5a MOVE-to-SR/MOVE-USP gates +
> RTE/STOP/RESET route through `TrapIfUserMode`.

**Files:** Modify `M68000Cpu.Exceptions.cs`; modify `M68000Cpu.Move.cs` (the MOVE-to-SR/USP privilege gates);
modify `M68000ExceptionTests.cs`.

- [ ] **Step 1: Write the failing/skip tests** — TRAP #3 raises vector 35; TRAPV with V set raises vector 7,
  with V clear is a no-op; CHK with Dn in [0,bound] is a no-op, out of range raises vector 6; ILLEGAL raises
  vector 4; NOP changes nothing; a user-mode RESET/STOP/MOVE-to-SR raises vector 8 (synthetic).
- [ ] **Step 2: Add the bodies to `M68000Cpu.Exceptions.cs`:**

```csharp
    partial void TrapExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => RaiseException(Vector.TrapBase + (operword & 0xFu), FrameKind.Small, (ushort)(SR & 0xFFFF), PC);

    partial void TrapVExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if ((Ccr & 0x02) != 0)   // V set -> trap
            RaiseException(Vector.TrapV, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);
        // V clear: no-op.
    }

    partial void ChkExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint dn = (operword >> 9) & 7u;
        int value = (short)(ushort)(DataReg(dn) & 0xFFFFu);                       // CHK is .w on the 68000
        int bound = (short)(ushort)(ReadEaOperand(srcMode, srcReg, 1u, r.ExtensionWords) & 0xFFFFu);
        // CHK sets N from the comparison (N = value < 0 on the low-bound trap, N = 0 on the high-bound trap;
        // Z/V/C undefined-but-pinned) BEFORE the trap. Set N then raise on out-of-range.
        if (value < 0 || value > bound)
        {
            byte ccr = (byte)(Ccr & ~0x08);
            if (value < 0) ccr |= 0x08;     // N set when below 0; cleared when above the bound (PRM)
            Ccr = ccr;
            RaiseException(Vector.Chk, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);
        }
        // in range [0, bound]: no trap. (N is undefined here; the vectors pin it — confirm Task 14.)
    }

    partial void IllegalExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => RaiseException(Vector.Illegal, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);

    partial void NopExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    { /* no state change on the data axis (the only observable effect is timing/prefetch — M4.5d-2). */ }

    partial void ResetExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if (TrapIfUserMode()) return;   // PRIVILEGED
        // RESET asserts the external reset line — no CPU-register data-axis effect (peripheral reset is a
        // device concern). Data-axis no-op in supervisor mode.
    }

    partial void StopExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if (TrapIfUserMode()) return;   // PRIVILEGED
        SR = (ushort)(r.ExtensionWords[0] & 0xA71Fu);   // STOP loads the +1 imm word into SR, then halts.
        // The halt/wake-on-interrupt is a timing/IPL concern (M4.5d-2 / DD5); the data-axis effect is the SR load.
    }
```

  > **Notes (Builder resolves against vectors):** (1) CHK's exact CCR (N/Z/V/C) on the trap is documented as
  > "N reflects the comparison, others undefined" — the vectors PIN it; reconcile in this body (Task 14). The
  > value/bound are signed .w. (2) TRAP's vector = 32 + (operword & 0xF). (3) STOP's data-axis effect is the SR
  > load; the halt is M4.5d-2. (4) `ReadEaOperand` is the M4.5a primitive (`M68000Cpu.Move.cs`); confirm the
  > CHK bound EA reads `.w`.

- [ ] **Step 3: Add the privilege gate to the M4.5a MOVE-to-SR/USP bodies** in `M68000Cpu.Move.cs` (the ADR
  0008 §3.2 promotion — these are M4.5a BODY files, NOT seam files; the edit is the gate the M4.5a `// PRIVILEGED`
  comments anticipated):

```csharp
    // In MoveToSrExecute (M68000Cpu.Move.cs:122), at the top of the body:
    //     if (TrapIfUserMode()) return;   // PRIVILEGED (ADR 0008 §3.2 — was an unvectored honor in M4.5a)
    // In MoveUspExecute (M68000Cpu.Move.cs:151), at the top of the body:
    //     if (TrapIfUserMode()) return;   // PRIVILEGED
```

  > **Builder applies these as exact Edits** (the literal `if (TrapIfUserMode()) return;` line at the body top,
  > preserving the existing comments). MOVE-FROM-SR is NOT privileged on the 68000 (it is on the 68010+) — leave
  > it. MOVE-to-CCR is unprivileged — leave it. **These two edits flip M4.5a's "honor the bit but do not vector"
  > to a real vector-8 trap; the M4.5a MOVE-to-SR/USP vectors were supervisor-mode cases (so they stay green),
  > and any user-mode case now ASSERTS the trap under the runner flag (Task 14).**

- [ ] **Step 4-6:** failing-test → green (post Task 11) → full gate → commit. **Est:** ~7.

---

### Task 9: The ÷0 vector-5 promotion (TDD)

> The M4.5b/c detect-and-defer comes due (ADR 0008 §3.2). The `Div` body (`M68000Cpu.Alu.cs:495-500`) detects
> divisorW == 0 and currently `return`s (the runner defers the case). M4.5d-1 replaces the bare `return` with
> `RaiseException(5, …)`. The detection stays; only the vectoring is added.

**Files:** Modify `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs` (the `Div` ÷0 line ONLY — a body file, NOT a
seam file; flagged in the File Structure note); modify `M68000ExceptionTests.cs`.

- [ ] **Step 1: Write the failing/skip test** — DIVU with divisor 0 raises vector 5 (push frame, supervisor,
  PC = Read32(20)); the non-zero divisor path is unchanged (the M4.5b/c green behavior).
- [ ] **Step 2: Edit the `Div` ÷0 branch** (`M68000Cpu.Alu.cs:499-500`):

```csharp
        // Replace:
        //     if (divisorW == 0u)
        //         return;   // DETECT ÷0; DEFER the vector-5 exception to M4.5d (no write, no CCR change)
        // With:
        if (divisorW == 0u)
        {
            RaiseException(Vector.DivideByZero, FrameKind.Small, (ushort)(SR & 0xFFFF), PC);   // ADR 0008 §3.2: the deferral comes due
            return;
        }
```

  > **The detection point is UNCHANGED** (the divisor read + the `== 0` test stay where they are at
  > `M68000Cpu.Alu.cs:498-499`). Only the action changes from "return, let the runner defer" to "raise vector 5,
  > then return". Update the body's comment (`:487-489`) to note the ÷0 now vectors (M4.5d-1). Confirm against
  > the `DIVU`/`DIVS` ÷0 cases (Task 14) — they flip deferred→asserted under `assertExceptions:true`.

- [ ] **Step 3-6:** failing-test → green → full gate → commit. **Est:** ~2.

---

### Task 10: The to-CCR/SR forms — ANDI/ORI/EORI -to-CCR and -to-SR (TDD)

> ANDItoCCR/ORItoCCR/EORItoCCR (0x023C/0x003C/0x0A3C) AND/OR/EOR an immediate byte into the CCR (low byte of SR)
> — UNPRIVILEGED. ANDItoSR/ORItoSR/EORItoSR (0x027C/0x007C/0x0A7C) do the same to the FULL 16-bit SR —
> PRIVILEGED (the gate; and writing SR may flip S, re-banking A7). The +1 imm word is in ExtensionWords[0].

**Files:** Modify `M68000Cpu.Exceptions.cs` (or a sibling — these are the to-system-byte ops, naturally grouped
with the privilege model); modify the relevant test file.

- [ ] **Step 0: Confirm the +1 imm word decode.** A `Ori_ccr_decode_captures_imm_word` test (operword 0x003C →
  `ExtensionWords[0]` = the imm). If RED, add the `*_CCR`/`*_SR` rows to the leading-imm-word set in the
  generator (the M4.5b Task-6 precedent). The dataset marks them `sizeWidth:1`.
- [ ] **Step 1: Write the failing/skip tests** — ORItoCCR #0x0F sets CCR bits; ANDItoCCR #0x00 clears CCR;
  EORItoCCR toggles; ORItoSR in supervisor sets SR bits (incl. possibly S — A7 re-banks); ANDItoSR in user mode
  → privilege violation (synthetic: assert RaiseException(8)).
- [ ] **Step 2: Add the bodies:**

```csharp
    // ── ANDI/ORI/EORI to CCR (unprivileged — the low byte of SR) ──────────────────────────────────────────
    partial void OriCcrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Ccr = (byte)((Ccr | (byte)(r.ExtensionWords[0] & 0xFFu)) & 0x1Fu);
    partial void AndiCcrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Ccr = (byte)((Ccr & (byte)(r.ExtensionWords[0] & 0xFFu)) & 0x1Fu);
    partial void EoriCcrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Ccr = (byte)((Ccr ^ (byte)(r.ExtensionWords[0] & 0xFFu)) & 0x1Fu);

    // ── ANDI/ORI/EORI to SR (PRIVILEGED — the full 16-bit SR; writing SR may flip S and re-bank A7) ────────
    partial void OriSrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    { if (TrapIfUserMode()) return; SR = (ushort)((SR | r.ExtensionWords[0]) & 0xA71Fu); }
    partial void AndiSrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    { if (TrapIfUserMode()) return; SR = (ushort)((SR & r.ExtensionWords[0]) & 0xA71Fu); }
    partial void EoriSrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    { if (TrapIfUserMode()) return; SR = (ushort)((SR ^ r.ExtensionWords[0]) & 0xA71Fu); }
```

  > **Notes:** (1) the `*toCCR` ops mask to bits 0-4 (the M4.5a MOVE-to-CCR precedent, `M68000Cpu.Move.cs:138`).
  > (2) the `*toSR` ops apply the `0xA71Fu` SR_VALID mask (the MOVE-to-SR precedent, `:131`); the AND form must
  > AND against the imm THEN mask valid bits — confirm the order against the `ANDItoSR` vectors (ANDI with an
  > imm that would clear S re-banks A7 mid-instruction). (3) the dataset operation strings are `ORI_CCR`/`ORI_SR`
  > etc. (R4); the Task 11 `op switch` maps them to these PascalCase hooks.

- [ ] **Step 3-6:** failing-test → green (post Task 11) → full gate → commit. **Est:** ~5.

---

### Task 11: The generator dispatch arms + partial-hook declarations (generator) (TDD)

> **HOIST this early** (right after Task 2 establishes the first body) so all `partial void` declarations exist
> before Tasks 3-10 compile (no-op `partial void` until filled — the M4.5b/c precedent). Extend
> `EmitMoveDispatchArms` (`CpuEmitter.cs:4332`, before `_ => null`) with ALL M4.5d-1 operation names → hooks, and
> add the matching `partial void *Execute(...)` declarations to the FieldGrammar-gated emit (`:339`, after the
> M4.5c block). NO other generator change (except the optional `Bcc.w`/`*toSR`/`*toCCR` leading-word decode arm
> — Task 2/10 Step 0 — if red). NO dataset edit (every row exists, R4 — so NO opIndex shift).

**Files:** Modify `src/CpuEmulator.Generators/CpuEmitter.cs`; modify the relevant test files (the un-skips).

- [ ] **Step 1: Extend `EmitMoveDispatchArms`** — add to the `op switch` (after the M4.5c arms, before
  `_ => null` at `:4332`):

```csharp
                // ── M4.5d-1: control flow + exceptions. All take (__operword,__r,__size,__srcMode,__srcReg). ──
                "Bcc"       => "BccExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "DBcc"      => "DBccExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "JMP"       => "JmpExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "JSR"       => "JsrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "RTS"       => "RtsExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "RTR"       => "RtrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "RTE"       => "RteExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "LINK"      => "LinkExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "UNLK"      => "UnlkExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "TRAP"      => "TrapExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "TRAPV"     => "TrapVExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "CHK"       => "ChkExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ILLEGAL"   => "IllegalExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "NOP"       => "NopExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "RESET"     => "ResetExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "STOP"      => "StopExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ORI_CCR"   => "OriCcrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ANDI_CCR"  => "AndiCcrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "EORI_CCR"  => "EoriCcrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ORI_SR"    => "OriSrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ANDI_SR"   => "AndiSrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "EORI_SR"   => "EoriSrExecute(__operword, __r, __size, __srcMode, __srcReg)",
```

- [ ] **Step 2: Add the partial-hook declarations** — a sibling `foreach` after the M4.5c one (`:338`):

```csharp
            sb.AppendLine();
            sb.AppendLine("    // M4.5d-1: the control-flow + exception op bodies — hand-written M68000Cpu.Control/Exceptions partials.");
            foreach (var name in new[] {
                "Bcc","DBcc","Jmp","Jsr","Rts","Rtr","Rte","Link","Unlk",
                "Trap","TrapV","Chk","Illegal","Nop","Reset","Stop",
                "OriCcr","AndiCcr","EoriCcr","OriSr","AndiSr","EoriSr" })
            {
                sb.AppendLine($"    partial void {name}Execute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg);");
            }
```

  > **Name consistency (load-bearing):** the declared `{name}Execute` MUST equal the body method names
  > (`BccExecute`, `DBccExecute`, `JmpExecute`…`EoriSrExecute`). The dataset strings (`Bcc`, `DBcc`, `JMP`,
  > `ORI_CCR`, `ANDI_SR`…) map to the PascalCase hooks via the explicit `op switch` — no automatic case
  > transform. `DBcc`/`TrapV` (mixed case) match the body names. The `_CCR`/`_SR` dataset suffixes map to
  > `Ccr`/`Sr` hooks (the M4.5c `CmpM`/`MoveM` precedent).

- [ ] **Step 3: Dispatch smoke tests** (not skipped — prove routing once the bodies land): e.g.
  `Step_routes_a_bra_operword` (BRA `0x6002`) + `Step_routes_a_trap_operword` (TRAP #0 = `0x4E40`) +
  `Step_routes_an_rts_operword` (`0x4E75`). Confirm the operword encodings against the dataset at recon.
- [ ] **Step 4: Build + run.** The generator emits arms + declarations; the bodies (Tasks 2-10) bind. The
  un-implemented `partial void` are no-ops until filled — the suite COMPILES at every intermediate state.
- [ ] **Step 5: Full gate.** `dotnet test` green; `-warnaserror` clean; `RegeneratedSpecTests` green (the
  M4.5d-1 arms + declarations emit ONLY inside `model.FieldGrammar is not null`; 6502/Z80 byte-identical). **NO
  dataset edit → NO opIndex shift → the existing M4.5a–c arms bind unchanged.**
- [ ] **Step 6: Commit.** **Est:** ~3 (the un-skips happen in Tasks 2-10).

---

### Task 12: The IPL interrupt thin stub (DD5 — synthetic-tested; DROPPABLE if the user picks E-alternative)

> ADR 0008 §4 / DD5. NO vector asserts it (the dataset has no async-interrupt file) — synthetic-tested only,
> clearly labeled (the M4.5b immediate-forms honesty precedent). The IPL-level input + the `TryServiceInterrupt`
> policy REUSING `RaiseException`. **If the user ratifies E-alternative (defer the whole IPL to M4.5d-2), DROP
> this task — it is self-contained.**

**Files:** Modify `src/CpuEmulator.Cpus.M68000/M68000Cpu.cs` (the interrupt-policy hooks ONLY — `:64-65`,
`:118-121`; NOT the bus helpers); modify `src/CpuEmulator.Cpus.M68000/M68000Cpu.Exceptions.cs` (the acknowledge
helper); create the IPL synthetic tests in `M68000ExceptionTests.cs`.

- [ ] **Step 1: Write the failing synthetic tests** — set IPL=5 with SR mask=3 → an interrupt is pending and the
  acknowledge fires (supervisor entered, frame pushed, mask set to 5, PC = autovector 24+5=29's table entry);
  IPL=2 with mask=3 → NOT pending (masked); IPL=7 always pending (non-maskable). NO vector file — labeled
  synthetic.
- [ ] **Step 2: Un-stub the interrupt hooks** in `M68000Cpu.cs` (the policy hooks — these are NOT the seam bus
  helpers):

```csharp
    // Replace the inert M4.1 stubs (M68000Cpu.cs:64-65, :118-121) with the IPL-level model (M4.5d-1, DD5):
    private int _iplLevel;   // the pending interrupt priority level (0-7); 7 is non-maskable.

    /// <summary>Set the IPL input (0-7). M4.5d-1 (DD5): the thin synthetic-tested IPL model; the
    /// acknowledge-cycle cycle-accuracy + the device-supplied vector are M4.5d-2.</summary>
    public void SetInterruptLevel(int level) => _iplLevel = level & 7;

    private uint SrInterruptMask => (uint)((SR >> 8) & 7u);

    /// <summary>True when IPL exceeds the SR interrupt mask (or is level 7, non-maskable).</summary>
    public partial bool InterruptPending => _iplLevel == 7 || (uint)_iplLevel > SrInterruptMask;

    /// <summary>The interrupt acknowledge: reuse RaiseException (the interrupt is "an exception sourced by the
    /// IPL line"). Enter supervisor, push the (PC, SR) frame, set the mask to the serviced level, vector through
    /// the autovector (24 + level). DD5: autovector default; the device-supplied vector is M4.5d-2.</summary>
    private partial bool TryServiceInterrupt()
    {
        if (!InterruptPending) return false;
        int level = _iplLevel;
        ushort srAtFault = (ushort)(SR & 0xFFFF);
        RaiseException(Vector.AutovectorBase + (uint)level, FrameKind.Small, srAtFault, PC);
        // After RaiseException entered supervisor + cleared trace, set the SR interrupt mask to the serviced
        // level (so a same-or-lower interrupt does not re-fire).
        SR = (ushort)((SR & ~0x0700) | ((uint)level << 8));
        _iplLevel = 0;   // the device de-asserts on acknowledge (synthetic model).
        return true;
    }

    // SetIrqLine/SetNmiLine (M68000Cpu.cs:64-65) stay as thin shims OR map to SetInterruptLevel — keep their
    // signatures (the generated partial requires them); the 68000's real input is the 3-bit IPL, set via
    // SetInterruptLevel. Document that SetIrqLine(true) => level 7 (the common "assert NMI-equivalent") if a
    // generic caller needs it; otherwise leave them inert (no generated caller asserts them in the test path).
```

  > **Notes:** (1) `TryServiceInterrupt`/`InterruptPending` are declared `partial`/`partial bool` in the
  > generated side (`M68000Cpu.cs:118-121` are the current `partial` implementations) — replace the bodies, keep
  > the signatures. (2) the generated `Step` calls `TryServiceInterrupt()` FIRST (`CpuEmitter.cs:202`), so the
  > acknowledge fires before the fetch — exactly the seam ADR 0004 §2 Decision 3 designed. (3) NO TomHarte vector
  > exercises this — the synthetic tests are the ONLY coverage (disclosed, DD5).

- [ ] **Step 3-6:** failing synthetic tests → green → full gate (6502/Z80 byte-identical — these hooks are
  M68000-only) → commit. **Est:** ~5. **(DROP the whole task if the user picks E-alternative.)**

---

### Task 13: The default-FALSE `assertExceptions` runner flag (the SOLE seam-listed-file touch, DD3) (TDD)

> **THE ONLY SEAM-LISTED FILE THIS PR EDITS.** Additive, default-off — preserves M4.5a–c byte-for-byte. The
> M4.5d-1 sweep (Task 14) passes `true`. Does NOT touch `DiffBusTrace`, the `timingAxis` path, the data-axis
> diff, the fetch stream, or the bus helpers.

**Files:** Modify `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs`.

- [ ] **Step 0 (DD4 — the address-error frame observation):** BEFORE writing the gate, run a scratch pass over
  one M4.5a MOVE file with `assertExceptions:true` to OBSERVE whether the un-deferred address-error (vector 3)
  cases pass the data-axis diff on the PUSHED FRAME WORDS. If they pass → the group-0 frame is data-axis-stable
  in the corpus (extend `RaiseException`'s Large branch to push the access words). If they FAIL on the frame
  words (timing-coupled) → add the address-error-subset deferral predicate (below) so vector-3 stays deferred
  while vector-4/5/6/7/8 assert. **This resolves F empirically.**
- [ ] **Step 1: Write the failing tests** — (a) `RunCase` with the default (no flag) STILL defers an exception
  case (byte-identical to today); (b) `RunCase(c, assertExceptions:true)` RUNS the case (returns null on a
  correct exception result, a report on a wrong one). Use a crafted TRAP case.
- [ ] **Step 2: Edit `RunCase`** — add the parameter + gate the short-circuit:

```csharp
    public static string? RunCase(M68000TomHarteCase c, bool timingAxis = false, bool assertExceptions = false)
    {
        // M4.5d-1 (ADR 0008 §3.4, sign-off D): default-off preserves M4.5a-c byte-for-byte. When
        // assertExceptions, the exception cases RUN and are diffed on the data axis (the M4.5d-1 exception model).
        if (!assertExceptions && IsExceptionCase(c)) return DeferredException;
        // (DD4) If the address-error large frame proves timing-coupled (Task 13 Step 0), keep vector-3 deferred
        // even under assertExceptions by an extra predicate — assert the small-frame exceptions, defer the
        // address-error frame-word precision to M4.5d-2:
        //   if (assertExceptions && IsAddressErrorCase(c)) return DeferredException;   // DD4, only if Step 0 red
        // ... (the existing seed state, Step(), data-axis diff — UNCHANGED) ...
```

  > **The ONLY change is the `assertExceptions` parameter + the `!assertExceptions &&` guard on the existing
  > short-circuit** (+ the optional DD4 predicate). The seed/diff body (`:79-139`) is byte-identical. The
  > `IsExceptionCase` heuristic (`:44`) is UNCHANGED (under `assertExceptions` it still IDENTIFIES exception
  > cases, but the runner no longer short-circuits them — it runs them and the modeled `RaiseException` produces
  > the same vector-fetch + frame the case expects). If DD4 Step 0 is red, add the tiny `IsAddressErrorCase`
  > predicate (vector 3 specifically — the read pair at `4·3, 4·3+2`).

- [ ] **Step 3-6:** failing-test → green → full gate (the M4.5a–c sweeps STILL pass with the default — confirm
  byte-identity: their executed/deferred counts are UNCHANGED) → commit. **Est:** ~3.

---

### Task 14: The SINGLE M4.5d-1 TomHarte data-axis sweep with `assertExceptions=true` (the gate)

> ONE sweep covering ALL M4.5d-1 coverage axes (DD7): the 20 dedicated control-flow/exception files + the
> DIVU/DIVS ÷0 re-run (2 files) + the embedded exception-case re-run across the M4.5a–c files (the un-deferred
> privilege/illegal/address-error cases). Run under `-c Release` with the vectors fetched. Heavy gate —
> SEQUENTIAL, coarse monitor.

**Files:** Create `tests/CpuEmulator.Tests/TomHarte/M68000M45d1TomHarteTests.cs`; modify the M4.5a–c sweep
theories (or add a cross-corpus exception theory) to pass `assertExceptions:true` for the embedded-exception
axis.

- [ ] **Step 1: Write the dedicated-file sweep theory** (copy the EXACT shape of the merged
  `M68000M45cTomHarteTests.cs` — `TryGetVectorDirectory`/`Assert.NotNull`, `File.Exists`,
  `M68000TomHarteLoader.LoadFile`, `M68000TomHarteRunner.RunCase` with `assertExceptions:true`, the
  `executed > 0` anti-fake guard). Exception cases now ASSERT (not defer):

```csharp
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.5d-1: the SINGLE control-flow + exception TomHarte green sweep (20 dedicated + the DIVU/DIVS ÷0
/// re-run) on the DATA axis with assertExceptions:true — the un-fakeable gate. Every M4.5d-1 control-flow op has
/// a dedicated v1 vector file (verified, ADR 0008 §2); ILLEGAL (vector 4) asserts via the embedded illegal cases
/// across the M4.5a-c files (the cross-corpus exception axis, the companion theory). The exception cases that
/// M4.5a-c DEFERRED (privilege/illegal/÷0) now ASSERT; the address-error large-frame WORD contents may defer to
/// M4.5d-2 (DD4 — assert trap-taken). The TIMING axis (final.pc/prefetch/trace/cycle) is M4.5d-2
/// (timingAxis:false). UNLK's file is UNLINK.json.gz.</summary>
public class M68000M45d1TomHarteTests
{
    public static IEnumerable<object[]> M45d1Files =>
    [
        // branches (3)
        ["Bcc.json.gz"], ["BSR.json.gz"], ["DBcc.json.gz"],
        // jumps/returns (5)
        ["JMP.json.gz"], ["JSR.json.gz"], ["RTS.json.gz"], ["RTR.json.gz"], ["RTE.json.gz"],
        // stack frame (2) — UNLK's file is UNLINK
        ["LINK.json.gz"], ["UNLINK.json.gz"],
        // vector/check (3)
        ["TRAP.json.gz"], ["TRAPV.json.gz"], ["CHK.json.gz"],
        // no-op (1)
        ["NOP.json.gz"],
        // to-CCR/SR (6)
        ["ANDItoCCR.json.gz"], ["ANDItoSR.json.gz"],
        ["ORItoCCR.json.gz"],  ["ORItoSR.json.gz"],
        ["EORItoCCR.json.gz"], ["EORItoSR.json.gz"],
        // the ÷0 vector-5 re-run (2) — the M4.5b/c detect-and-defer comes due
        ["DIVU.json.gz"], ["DIVS.json.gz"],
    ];

    [M68000TomHarteTheory]
    [MemberData(nameof(M45d1Files))]
    public void M45d1_family_is_TomHarte_green_on_the_data_axis(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"in-scope M4.5d-1 vector file missing: {path}");

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int executed = 0, deferred = 0;
        foreach (var c in cases)
        {
            string? rr = M68000TomHarteRunner.RunCase(c, assertExceptions: true);   // data axis; exceptions ASSERT
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }   // only address-error if DD4 red
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 10) break; }
        }
        Assert.True(executed > 0, $"{file}: 0 executed cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ data-axis failures over {executed} executed ({deferred} deferred):\n" +
            string.Join("\n", failures));
    }
}
```

  > **Confirm the exact dedicated file names at recon** (Task 0 Step 3) — the ADR 0008 §2 table names them
  > (`Bcc`/`BSR`/`DBcc`/`JMP`/`JSR`/`RTS`/`RTR`/`RTE`/`LINK`/`UNLINK`/`TRAP`/`TRAPV`/`CHK`/`NOP`/`ANDItoCCR`/
  > `ANDItoSR`/`ORItoCCR`/`ORItoSR`/`EORItoCCR`/`EORItoSR`). If a file's actual name differs (e.g. a suffix),
  > adjust the `MemberData` row — the dispatch is name-driven so the BODY is unaffected.

- [ ] **Step 2: Add the cross-corpus exception axis** — re-run the M4.5a–c sweep files with
  `assertExceptions:true` so the embedded privilege/illegal/address-error cases ASSERT. Either (a) add a
  parameter to the existing M4.5a/b/c theories (default false; a new `[Theory]` variant passes true), or (b) a
  dedicated `M68000ExceptionCorpusTomHarteTests` theory over the full M4.5a–c file list running
  `assertExceptions:true` and counting the newly-asserting cases. **Capture the COUNT of newly-asserting
  exception cases** (the un-fakeable proof the exception model is right across the whole corpus).
- [ ] **Step 3: Run the SINGLE gate under `-c Release`** with the vectors present:
  `pwsh tools/get-test-vectors-68000.ps1` (idempotent), then the sweep:
  ```bash
  dotnet test -c Release --filter "FullyQualifiedName~M68000M45d1TomHarteTests|FullyQualifiedName~M68000ExceptionCorpusTomHarteTests"
  ```
  Expected: all 20 dedicated + DIVU/DIVS files green on the data axis; the embedded exception cases assert green.
  COARSE monitor (wake on `Passed!`/`Failed!`/`error`/`Exception`); kill stray `testhost.exe` first. **Capture
  the per-file executed (non-skipped) count AND the count of newly-asserting exception cases** (the merge-gate
  evidence).
- [ ] **Step 4: Reconcile failures** (fix in the ONE place per concern):
  - **Branch/return PC** → the branch base (`PcForEa` vs `PC - length + 2`) (Tasks 2-5).
  - **DBcc off-by-one** → the `-1` (0xFFFF) terminate vs `0` (Task 3).
  - **Exception frame layout** → `RaiseException` (Task 7): the SR/PC stack order, the SSP delta (6), the
    mode/bank interaction. Reconcile HERE, never in the callers.
  - **Privilege gate** → `TrapIfUserMode` (Task 7) + the per-op call sites (Tasks 5/8/10).
  - **÷0 vector** → the `Div` raise (Task 9).
  - **CHK CCR (N/Z/V/C)** → the `ChkExecute` body (Task 8).
  - **to-SR/CCR mask order** → the AND/OR/EOR-then-mask (Task 10).
  - **A7 re-bank** → confirm writing SR flips USP/SSP correctly (RTE/`*toSR`/RaiseException).
  - **"0 executed"** → a dispatch arm did not wire (Task 11 name mismatch).
  - **byte-identity broken** → the `assertExceptions` default leaked (Task 13) OR a non-FieldGrammar emit.
  Each fix re-runs the FAST synthetic suite first, then the heavy gate.
- [ ] **Step 5: Full suite + byte-identity.** `dotnet test` (Debug) → 0 failures; the M4.5d-1 sweep SKIPPED when
  vectors absent; **6502/Z80 byte-identical** (`RegeneratedSpecTests`); the M4.5a–c sweeps with the DEFAULT flag
  UNCHANGED (their executed/deferred counts identical to pre-PR — the default-off proof). `-warnaserror` clean.
  `git diff --stat` confirms ONLY the M4.5d-1 files changed; the SEAM files
  (`M68000FetchStream.cs`, the `M68000Cpu.cs` bus helpers `:79-104`) UNCHANGED; the SOLE seam-listed-file touch
  is the `M68000TomHarteRunner.cs` `assertExceptions` flag.
- [ ] **Step 6: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000M45d1TomHarteTests.cs
git commit -m "$(cat <<'EOF'
test(680x0): the M4.5d-1 control-flow/exception data-axis sweep (20 dedicated + DIVU/DIVS ÷0 + embedded exception cases; assertExceptions=true; -c Release gate)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```
**Est:** ~2 (a 22-row MemberData theory + the cross-corpus exception theory).

---

## The single MERGE GATE (ADR 0008 §3 / the M4.5c discipline — all three required; merge blocked otherwise) — ONE PR

> The per-PR anti-drift acceptance cycle. The green TomHarte sweep with `assertExceptions=true` is the
> un-fakeable behavioral oracle.

1. **Full suite GREEN + 6502/Z80 BYTE-IDENTICAL.** `dotnet test` → 0 failures; the 6502 `RegeneratedSpecTests`
   AND the Z80 regen guard green; every change additive (gated to `model.FieldGrammar is not null` + the
   M68000-only `M68000Cpu.Control.cs`/`.Exceptions.cs` partials + the M4.5a/b body-file edits [`Div` ÷0,
   MOVE-to-SR/USP gates] + the IPL hooks + the 680x0-only test infra). **The default-FALSE `assertExceptions`
   flag GUARANTEES the M4.5a–c sweeps stay byte-identical** (DD3) — confirm their executed/deferred counts are
   UNCHANGED. `git status` confirms no 6502/Z80 spec/generated-CPU change. **NO dataset edit → NO opIndex shift.**
2. **The M4.5d-1 TomHarte data-axis sweep with `assertExceptions=true` ACTUALLY RUN GREEN — vectors PRESENT**
   under `-c Release`, on the DATA axis (`D0–D7, A0–A6, USP, SSP, SR, RAM`, byte-exact; the landed PC where the
   data axis implies it), operword from `initial.prefetch[0]`, NOT skipped:
   - the 20 dedicated control-flow/exception files (`Bcc`/`BSR`/`DBcc`/`JMP`/`JSR`/`RTS`/`RTR`/`RTE`/`LINK`/
     `UNLINK`/`TRAP`/`TRAPV`/`CHK`/`NOP`/`ANDItoCCR`/`ANDItoSR`/`ORItoCCR`/`ORItoSR`/`EORItoCCR`/`EORItoSR`) via
     `M68000M45d1TomHarteTests`;
   - the DIVU/DIVS ÷0 vector-5 re-run (the M4.5b/c detect-and-defer now asserting);
   - the embedded privilege/illegal/address-error exception cases across the M4.5a–c corpus, flipped
     deferred→asserted (the cross-corpus exception axis).
   **A SKIPPED TomHarte test is NOT a mergeable state.** **SHOW the non-zero executed count PER FILE AND the
   count of newly-asserting exception cases** (the un-fakeable proof). The address-error large-frame WORD
   contents may defer (DD4 — assert trap-taken); the TIMING axis is M4.5d-2 (`timingAxis:false`).
3. **ONE pre-merge code review** — pointed at the HIGHEST-bug-density area: **the exception model + `RaiseException`**
   (the frame stack order, the S-bit/USP-SSP swap via A7 re-banking, the SSP-advance-before-SR-write in RTE, the
   `TrapIfUserMode` privilege gate, the ÷0/CHK/TRAP vector mapping). Secondary: the branch base (Bcc/DBcc PC math),
   the BSR/JSR return-PC push, the LINK/UNLK frame discipline, the to-SR mask order, the generator dispatch arms,
   and — explicitly — **the default-FALSE `assertExceptions` flag (the SOLE seam touch, DD3): confirm it only
   strengthens the gate and the M4.5a–c default-off behavior is byte-identical.**

**HONESTY (M4.5d-1):** every control-flow op (Bcc/BSR/BRA/DBcc/JMP/JSR/RTS/RTR/RTE/LINK/UNLK/TRAP/TRAPV/CHK/NOP/
the six to-CCR/SR) has a dedicated v1 vector file and IS asserted green on the data axis. `ILLEGAL` (vector 4)
asserts via the embedded illegal cases across the corpus (no dedicated file — disclosed). The **IPL model has NO
vector** — synthetic-tested only (DD5 — disclosed, the M4.5b immediate-forms precedent). The **address-error
large-frame WORD contents** may defer to M4.5d-2 (DD4 — assert trap-taken; disclosed). The TIMING axis
(`final.pc`/`final.prefetch`/per-transaction trace/cycle) is M4.5d-2 — `timingAxis:false` throughout. State
plainly in the PR body; do NOT overclaim the timing axis or the IPL coverage.

## The SEAM INVARIANT (ADR 0007 §5.4 — binding; `git diff --stat` shows these UNCHANGED, ONE flagged exception)
Do NOT touch: `src/CpuEmulator.Core/Jit/M68000FetchStream.cs`, the `M68000Cpu.cs` bus helpers
(`ReadWordBus`/`WriteWordBus`/`ReadLongBus`/`WriteLongBus`, `:79-104`). **The SOLE seam-listed-file touch
permitted (DD3, ADR 0008 sign-off D): the default-FALSE `assertExceptions` flag on `M68000TomHarteRunner.RunCase`
— it only STRENGTHENS the gate (deferred→asserted for the new ops) and NEVER weakens the default-off behavior, so
M4.5a–c stay byte-for-byte identical.** M4.5d-1 ADDS: the two `M68000Cpu.Control.cs`/`.Exceptions.cs` partials +
the two generator edit-points (dispatch arms + hook declarations) + the new test files + the runner flag. It
EDITS (NON-seam body files, flagged): `M68000Cpu.Alu.cs` (the `Div` ÷0 line → `RaiseException(5)`),
`M68000Cpu.Move.cs` (the MOVE-to-SR/USP privilege gates → `RaiseException(8)`), `M68000Cpu.cs` (the IPL-policy
hooks — NOT the bus helpers). Every generator change gated to `model.FieldGrammar is not null`; 6502/Z80
byte-identity non-negotiable. **NO dataset edit; NO opIndex shift.**

---

## Plan self-review (completed at write time)

- **The ONE seam touch is explicit and prominent** (DD3 + the File Structure note + the SEAM INVARIANT section +
  the merge-gate review item): the default-FALSE `assertExceptions` flag on `M68000TomHarteRunner.RunCase`, which
  only strengthens the gate and keeps M4.5a–c byte-identical. The fetch stream + the bus helpers are UNCHANGED. ✓
- **Decisions block at the TOP** with A (ADR-pre-blessed), B (one `RaiseException`), D (the runner flag — the
  sole seam touch), E (the IPL thin stub — DD5, droppable), F (the address-error frame deferral — DD4), and C
  restated as out-of-scope (M4.5d-2). Each states the assumption (build per ADR rec), the alternative, and that
  the user ratifies C/E/F in the morning. ✓
- **Scope honored:** Bcc/BSR/DBcc, JMP/JSR/RTS/RTR/RTE, LINK/UNLK, TRAP/TRAPV/CHK/ILLEGAL, NOP, the six
  to-CCR/SR; the exception model (RaiseException + vector table + small/large frame + S-bit/USP-SSP swap +
  privilege violation + ÷0 vector-5 + ILLEGAL vector-4). Data-axis validation (regs + SR + USP/SSP + RAM +
  landed PC). NO timing axis, NO prefetch-queue rewrite, NO precise address-error frame words (assert
  trap-taken). ✓
- **Reuses the proven substrate** (R3): `EvaluateCondition` (Bcc/DBcc), the PEA `-(A7)` push (BSR/JSR/LINK/the
  frame push), the `A7` re-bank (the free USP/SSP swap), `ComputeEa(pureEa)` (JMP/JSR), the `Div` ÷0 point, the
  MOVE-to-SR privilege comments. ✓
- **`RaiseException` is the ONE routine** (DD2/Task 7) all sources funnel through; built before its callers; the
  privilege gate (`TrapIfUserMode`) centralizes the "integrate without scattering" check. The frame stack order
  + the SSP-advance-before-SR-write are flagged as the subtlest (reconcile in ONE place). ✓
- **Build-green-after-every-task:** Task 1 (data) + Task 2 (the first body) + Task 11 (generator, HOISTED so
  declarations precede bodies) + Task 7 (RaiseException, before its callers) + Tasks 3-10 (bodies whose
  declarations exist, no-op until filled, un-skip tests) + Task 12 (IPL, self-contained, droppable) + Task 13
  (the runner flag) + Task 14 (the single heavy gate). The 6502/Z80 byte-identity guard gates every task. ✓
- **HONESTY block:** control-flow fully vector-gated; ILLEGAL via embedded cases; the IPL synthetic-only (DD5);
  the address-error large-frame deferrable (DD4); the timing axis M4.5d-2. ✓
- **Placeholder scan:** every task has literal code. Bounded open choices: the `Bcc.w`/`*toSR` leading-word
  decode (Task 2/10 Step 0, empirical, the M4.5b precedent); the address-error frame contents (DD4/Task 13 Step
  0, empirical); the IPL task droppable (E-alternative). The subtlest reconciles flagged: the exception frame
  layout (Task 7), the branch PC base (Tasks 2-5), the DBcc off-by-one (Task 3), the CHK CCR (Task 8). No
  "TBD"/"similar to Task N". ✓
- **Type/name consistency:** `RaiseException(uint, FrameKind, ushort, uint)` / `TrapIfUserMode()` /
  `Vector.*` / `FrameKind.{Small,Large}`; the body names (`BccExecute`/`DBccExecute`/`JmpExecute`/`JsrExecute`/
  `RtsExecute`/`RtrExecute`/`RteExecute`/`LinkExecute`/`UnlkExecute`/`TrapExecute`/`TrapVExecute`/`ChkExecute`/
  `IllegalExecute`/`NopExecute`/`ResetExecute`/`StopExecute`/`OriCcrExecute`/`AndiCcrExecute`/`EoriCcrExecute`/
  `OriSrExecute`/`AndiSrExecute`/`EoriSrExecute`) match the generator's `name+"Execute"` table (Task 11); the
  dataset strings (`Bcc`/`DBcc`/`JMP`/`JSR`/`RTS`/`RTR`/`RTE`/`LINK`/`UNLK`/`TRAP`/`TRAPV`/`CHK`/`ILLEGAL`/`NOP`/
  `RESET`/`STOP`/`ORI_CCR`/`ANDI_CCR`/`EORI_CCR`/`ORI_SR`/`ANDI_SR`/`EORI_SR`) → the `op switch` arms (Task 11);
  reused merged symbols (`EvaluateCondition`/`A7`/`SupervisorMode`/`SrSupervisorBit`/`SetAreg`/`Areg`/`DataReg`/
  `SetDataRegPartial`/`ComputeEa`/`ReadLongBus`/`WriteLongBus`/`ReadWordBus`/`WriteWordBus`/`Ccr`/`SR`/`PC`/
  `USP`/`SSP`/`PcForEa`/`ReadEaOperand`) cited from R3. ✓
- **Altitude flags:** the exception model + `RaiseException` (Task 7) is the highest-risk new code — centralized
  in ONE routine so Task 14 reconciles the frame/mode/vector in one place; the pre-merge review points there. ✓

## Slice docs index
- **The governing decision (this plan implements the additive subset of):**
  `docs/architecture/0008-68000-control-flow-exceptions-and-the-timing-axis.md` (§3 the M4.5d-1 scope; §4 the IPL
  stub; §2.1 the address-error caveat; the sign-off block A/B/C/D/E/F).
- **The structural template:** `docs/superpowers/plans/2026-06-15-m4-5c-shift-rotate-bit-bcd.md` (the merged
  M4.5c plan — the Decisions block, the merge gate, the seam invariant, the HONESTY block, the closeout).
- **The decode/addressing/exception/vector decisions:**
  `docs/architecture/0004-68000-decode-addressing-and-exceptions.md` (§2 Decision 3 — the vector assignments +
  the privilege/IPL model).
- **The interpreter-structure decision + the seam invariant:**
  `docs/architecture/0007-68000-interpreter-op-body-structure.md` (option C; §5.4 the seam invariant).
- **The master status/resume pointer:** `docs/superpowers/plans/2026-06-15-m4-status-and-resume.md` (update the
  M4.5d line to mark M4.5d-1 done + point at M4.5d-2 [HELD] + M4.6 [unblocked by M4.5d-1] when this merges).

## Closeout (filled at completion)

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | _(fill)_ |
| Final test count | _(fill)_ |
| M4.5d-1 dedicated files TomHarte-green on the data axis (20)? | _(fill — 3 branch + 5 jump/return + 2 frame + 3 vector/check + 1 NOP + 6 to-CCR/SR)_ |
| DIVU/DIVS ÷0 vector-5 re-run green (÷0 now asserts)? | _(fill — the M4.5b/c detect-and-defer comes due)_ |
| Embedded exception cases asserting across M4.5a–c (count)? | _(fill — the cross-corpus exception axis; the newly-asserting count)_ |
| Total sweep executed (non-skipped) count + newly-asserting exception count | _(fill — per-file, the un-fakeable proof)_ |
| RaiseException frame layout (SR/PC order, SSP delta 6) green? | _(fill — vector-confirmed)_ |
| Privilege violation (vector 8) asserting (RTE/`*toSR`/STOP/RESET/MOVE-to-SR-USP in user mode)? | _(fill — vector-confirmed)_ |
| The S-bit/USP-SSP swap (A7 re-bank on SR write) green? | _(fill — RTE + RaiseException)_ |
| DBcc off-by-one (-1 terminates, not 0) green? | _(fill — vector-confirmed)_ |
| The SOLE seam touch = the default-off `assertExceptions` flag? | _(fill — git diff --stat: fetch stream + bus helpers UNCHANGED; runner flag default-off)_ |
| M4.5a–c sweeps byte-identical with the default flag? | _(fill — executed/deferred counts unchanged)_ |
| Address-error frame: trap-taken asserted; precise group-0 words (DD4)? | _(fill — asserted in M4.5d-1 OR deferred to M4.5d-2 per Task 13 Step 0)_ |
| IPL thin stub (DD5): synthetic-tested? | _(fill — synthetic only, no vector; OR dropped if E-alternative)_ |
| 6502/Z80 un-regressed? | _(fill — RegeneratedSpecTests byte-identical)_ |
| `-warnaserror` | _(fill — clean)_ |
| Honesty | Control flow fully vector-gated; ILLEGAL via embedded cases; IPL synthetic-only (DD5); address-error large-frame words deferrable (DD4); timing axis M4.5d-2. |
| Still deferred (M4.5d-2, HELD for the owner) | The timing axis (final.pc/final.prefetch/trace/cycle) for ALL families; the prefetch-queue mechanism + the stateful FetchStream rewrite; the bus-helper/Step cycle model; the address-error large-frame words (if DD4 timing-sensitive); the IPL acknowledge-cycle accuracy + the device-supplied vector. |
| Recommended next chunk | M4.5d-2 (timing + prefetch — SEAM-BREAKING, HOLD for the owner) OR M4.6 (the 68000 through the JIT — unblocked by M4.5d-1's functional model, ADR 0008 §6). |
