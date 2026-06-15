# ADR 0007 — 68000 interpreter op-body structure (the M4.5a→M4.5b deferred D-A resolution)

> **Status:** Accepted (architecture pass — the M4.5b Planner consumes this).
> **Date:** 2026-06-15
> **Deciders:** Mark (owner). This ADR resolves the promotion decision the M4.5a plan **explicitly deferred** to "an Architect ADR-addendum at the M4.5a→M4.5b boundary" (`docs/superpowers/plans/2026-06-15-m4-5a-move.md`, the D-A resolution block).
> **Supersedes / relates to:**
> - **ADR 0004** (`0004-68000-decode-addressing-and-exceptions.md`) — §3's family-by-family M4.5 split + §4's `OperandSize`-threaded `Op` / `EmitOpcodeMethod` data-model end-state. This ADR decides **when** (not whether) that end-state lands, and revises its *scope* to a 68000-local op-record rather than a global `Op`-record change.
> - **The M4.5a plan** D-A resolution (hand-written bodies behind a generated `opIndex` switch) + its **binding seam constraint** (the migration must stay mechanical: no rewrite of the fetch stream / cycle-charging bus helpers / Step+diff runner). This ADR confirms the constraint held against the shipped code and carries the data/timing/exception axis split forward.
> - **The M4 status/resume doc** (`docs/superpowers/plans/2026-06-15-m4-status-and-resume.md`) — the M4.5b integer-ALU family list + the data-axis-first gate correction.

---

## 1. Context

M4.5a **shipped** (PR #39, merge `1bcd202`): the 68000 executes the MOVE family — `MOVE.b/.w/.l`, `MOVEA.w/.l`, `MOVE to/from SR`, `MOVE to CCR`, `MOVE USP` — **57,447 non-exception cases TomHarte-green on the data axis** across all 10 in-scope files. It did so with **hand-written op bodies dispatched by a generated `opIndex` switch** (the M4.5a D-A resolution), and with a **binding constraint** attached: the dispatch seam had to be built so a *later* promotion to ADR 0004 §4's `Op(OperandSize)` / `EmitOpcodeMethod` data-model end-state would be **mechanical** — moving C# from the partial into the emitter, not re-plumbing the fetch/bus/runner. The promotion decision was deferred to **this ADR**, to be made "once the ALU families (M4.5b) reveal whether the hand-written pattern scales."

That is the decision here: **does the integer ALU arc (M4.5b) continue the hand-written pattern, promote now to the data model, or take a hybrid?**

**M4.5b's scope** (ADR 0004 §3 + the resume doc), the families that force the decision:

| Group | Families | Shape |
|---|---|---|
| Two-operand reg↔EA | `ADD` `SUB` `AND` `OR` `EOR` `CMP` | Dn operand (bits 11-9) `op` EA (bits 5-0); opmode bit 8 = direction; 3 sizes; regular CCR (NZVCX, EOR/AND/OR clear V/C) |
| Address-reg variants | `ADDA` `SUBA` `CMPA` | dest = An; size from opmode (.w/.l); **no CCR** (`CMPA` does set CCR) |
| Immediate forms | `ADDI` `SUBI` `ANDI` `ORI` `EORI` `CMPI` | `#imm` source (extension words) `op` EA; 3 sizes; same CCR rule as the reg form |
| Quick forms | `ADDQ` `SUBQ` | 3-bit immediate (bits 11-9, 0→8) `op` EA; An dest = no CCR |
| Unary | `NEG` `NEGX` `NOT` `CLR` `TST` | one EA operand; 3 sizes; CCR (`CLR` always Z=1 N=V=C=0; `TST` no write) |
| Extend | `EXT` | Dn sign-extend .b→.w / .w→.l; CCR from result |
| With-X | `ADDX` `SUBX` | reg↔reg or -(An)↔-(An); X-flag in; Z is *sticky* |
| Mul/Div | `MULU` `MULS` `DIVU` `DIVS` | 16×16→32 / 32÷16→16:16; result to Dn; CCR from result; **DIVU/DIVS divide-by-zero → exception (M4.5d)** |

This is **~30 families** (the dataset carries them at `tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar.json` lines 21–98), versus MOVE's ~6. They are **far more regular** than MOVE: each two-operand ALU op is the *same machine* — read operand A, read operand B, apply `(a,b) → (result, ccr)`, write result, set CCR — differing only in (1) the per-family ALU function, (2) the CCR rule, (3) which operand is reg vs EA vs immediate, and (4) the size. MOVE, by contrast, has no binary ALU function, has a unique two-EA encoding (dest mode/reg *swapped* in bits 11-6), and its system variants (SR/CCR/USP) are sui-generis privileged moves. **MOVE was the *least* data-shaped family in the whole ISA; the ALU families are the *most*.** That asymmetry is the crux of this decision.

### 1.1 The shipped seam — assessed against the real code, not the plan

The M4.5a seam constraint **held**. Concretely, in the *generated* Step arm (`src/CpuEmulator.Generators/CpuEmitter.cs:218-252`), everything up to the dispatch is operand-model-agnostic:

```
var __stream = new M68000FetchStream(_bus, PC);   // live BE word fetch
var __r = Decode(__stream);                        // field-decode walk → (opIndex, size, length)
uint __operword = __r.Operword;                    // read exactly once
_cycles += __stream.UnitsConsumed * 4;             // fetch cycles
_eaPcBase = PC + 2u;  PC += (uint)__r.Length;       // PC-relative base + advance
... unpack __opIndex, __size, __srcMode, __srcReg
switch (__opIndex) { case N: MoveExecute(__operword, __r, __size, __srcMode, __srcReg); ... }
```

The hand-written bodies (`src/CpuEmulator.Cpus.M68000/M68000Cpu.Move.cs`, 158 lines) own **only** the per-instruction semantics: `ReadEaOperand` → compute → `WriteEaOperand` → `SetMoveCcr`. They consume exactly the inputs a future data-model emit would (`operword`, `DecodeResult r`, `size`, `srcMode`, `srcReg`) — see the `partial void Move*Execute(...)` signatures the generator emits at `CpuEmitter.cs:312-317`. The three load-bearing seams the constraint protected are demonstrably agnostic:

- **The fetch stream** (`src/CpuEmulator.Core/Jit/M68000FetchStream.cs`, 33 lines) — pure `Read16`-walk over the bus. Knows nothing about ops.
- **The cycle-charging wide-bus helpers** (`src/CpuEmulator.Cpus.M68000/M68000Cpu.cs:72-110`, `ReadWordBus`/`ReadLongBus`/`WriteWordBus`/`WriteLongBus`) — `.l` decomposes to two `.w`; charges `WordAccessCycles`. Knows nothing about ops.
- **The Step+diff runner** (`tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs`, 172 lines) — seeds state, `cpu.Step()`, diffs D/A/USP/SSP/SR/RAM (+ optional timing axis). Knows nothing about ops, opIndices, the dispatch switch, or the bodies.

**Verdict on the constraint:** a promotion that replaces the `partial void *Execute` bodies with generated bodies (or a data-driven dispatch) touches **only** `CpuEmitter.cs` (the dispatch + the body emit) and **deletes** `M68000Cpu.Move.cs`. The fetch stream, the bus helpers, and the runner are **untouched by construction** — exactly as the constraint demanded. The one additive contract change M4.5a made to the shared layer, `DecodeResult.Operword` (`src/CpuEmulator.Core/Jit/DecodeResult.cs:18`), is a defaulted field (6502/Z80 leave it 0) and is *already* the agnostic plumbing a data model would consume. **The seam is migration-ready.**

---

## 2. Options considered

### (A) Continue hand-written bodies behind the generated `opIndex` switch for all of M4.5b

Each ALU family gets a hand-written `*Execute` partial method (or a small set of shared helpers the bodies call), dispatched by the same `switch (__opIndex)`. The data model is deferred again — to M4.5c, or indefinitely.

- **Pros.** Zero new generator surface; the proven M4.5a pattern continues unchanged; every family is independently debuggable against its TomHarte file; the seam stays exactly as shipped. Reversible.
- **Cons.** The ALU families are *regular in precisely the way that punishes hand-writing*. A hand-written `AddExecute` and `SubExecute` differ in **one line** (`a + b` vs `a - b`) plus a CCR carry/borrow sign — yet under (A) each is a full body with its own operand-read/operand-write/CCR-set boilerplate (the MOVE body is ~25 lines of that scaffolding *per family*). Across ~30 families × the shared read-A/read-B/write/CCR scaffold, (A) produces an estimated **600–900 lines of near-duplicated procedural code**, where the *actual* per-family content is one ALU lambda + one CCR descriptor. This is the duplication ADR 0004 §4 named ("`ADD`, `MOVE`, etc. each fan out across legal sizes × EA-modes × registers from one operation"). Worse, the CCR rules (V from signed overflow, C from carry-out, X = C for arithmetic, Z-sticky for the X-ops) are **subtle and shared** — hand-copying them 30 times multiplies the chance of a per-family CCR bug, and CCR errors are the dominant TomHarte failure class for ALU ops.

### (B) Promote NOW to the full `Op(OperandSize)` / `EmitOpcodeMethod` data model (ADR 0004 §4 end-state), migrating MOVE's bodies as part of the promotion

Generalize the M4.5a `Move*Execute` hooks into a 68000 op record threaded with `OperandSize`, drive the dispatch + the bodies from `EmitOpcodeMethod`, and re-express MOVE as data in the new model.

- **Pros.** Reaches ADR 0004's declared end-state; the ALU regularity becomes data; in principle the leanest long-term surface.
- **Cons.** **Premature on three counts, and the migration is not as cheap as "mechanical body-move" implies for the *full* `EmitOpcodeMethod` path.** (1) `EmitOpcodeMethod` is the **6502/Z80/8086 opcode-row** emitter — it is *row-shaped* (one method per opcode descriptor), which is exactly the model the M4.5a plan rejected for the field-grammar CPU ("would force the field-grammar CPU into a row model it does not have"). Reusing it literally re-introduces the impedance mismatch M4.5a spent effort escaping. (2) MOVE is the **worst** family to validate a generalized ALU op record against — its two-EA swapped-dest encoding and its three sui-generis privileged system moves (SR/CCR/USP) do not fit a binary-ALU record; forcing them in distorts the record before the regular families have shaped it. (3) Building the full record + the threaded-`OperandSize` emit + migrating MOVE is a large generator PR landing *before* a single ALU family has executed — it generalizes against zero ALU evidence, the precise "premature generalization against an unproven shape" ADR 0004 §4 flagged as the M4 risk. The migration *of the seam* is mechanical; the *construction of the data model itself* is not, and doing it MOVE-first inverts the evidence order.

### (C) Hybrid — a table-driven ALU helper layer the hand-written bodies call, no record/`EmitOpcodeMethod` promotion

Keep the hand-written-body + generated-`switch` seam exactly as shipped, but factor the **regular ALU machine** into a small, hand-written, **table-driven helper layer** in the M68000 partial: one `BinaryAluExecute(aluFn, ccrRule, operword, r, size, srcMode, srcReg)` driver that does read-operand-A / read-operand-B / write-result / set-CCR once, parameterized by a per-family `(aluFn, ccrRule, operandShape)` descriptor. The ~30 family `*Execute` hooks collapse to **one-line registrations** (`case N: BinaryAluExecute(Alu.Add, Ccr.Arith, ...); break;`), plus the genuinely-irregular families (`MUL`/`DIV`/`EXT`/`CLR`/the X-ops) keep bespoke bodies. The CCR rules live in **one** place. No `Op` record, no `EmitOpcodeMethod`, no generator change beyond the dispatch arms M4.5a already emits.

- **Pros.** Kills the (A) duplication where it actually hurts (the shared read/compute/write/CCR scaffold) **without** building the (B) data model against unproven shape. The shared CCR layer is written and tested **once** — directly attacking the dominant ALU failure class. The seam is **unchanged** — this is still "hand-written bodies behind the generated switch," so the M4.5a constraint is trivially preserved and the option stays fully reversible. It is the **natural data-shape the ALU families have** (a binary function + a CCR rule + an operand shape) expressed as *runtime data the bodies consult*, which is the cheapest possible step toward (B) and informs it: if M4.5c/d confirm the descriptor stabilizes across shift/rotate/bit families too, *that* is the evidence-backed moment to lift the descriptor table from the partial into the generator (the true (B)).
- **Cons.** A second small abstraction (the ALU driver + the descriptor table) that is *not yet* the generated end-state — so M4.5c/d may still face a promotion decision later (but now evidence-backed, against a stabilized descriptor). The irregular families still carry bespoke bodies (correctly — they are irregular).

---

## 3. Decision

**(C) — the hybrid: a table-driven ALU helper layer the hand-written bodies call, behind the unchanged M4.5a `opIndex` dispatch seam. Defer the full `EmitOpcodeMethod` data-model promotion until M4.5c/d, when the descriptor will have been shaped by more than one instruction class.**

Three load-bearing reasons:

1. **The shipped evidence says the *scaffold* is the cost, not the *dispatch*.** The MOVE bodies are ~25 lines each, and the bulk of that is read-operand / write-operand / set-CCR plumbing — *not* MOVE-specific logic. The ALU families multiply that scaffold ~30× while their unique content is one ALU function + one CCR rule. (A) pays the scaffold tax 30 times; (C) pays it once. The duplication (A) creates is real and large (est. 600–900 lines), and it concentrates in the **CCR code** — the highest-bug-density, most-shared logic in the whole arc. Centralizing CCR is the single highest-leverage structural move available, and (C) is the smallest change that achieves it.

2. **Promoting now (B) generalizes against zero ALU evidence and validates the record against the worst-fit family.** The M4.5a deferral was explicit that the promotion should happen "once the ALU families *reveal* whether the data model is warranted." MOVE is the *least* representative family (two-EA swapped dest + privileged system moves). Building the `Op(OperandSize)` record MOVE-first, before any ALU family exists, repeats the premature-generalization risk ADR 0004 §4 named. (C) lets the **regular** ALU families shape the descriptor first; the descriptor *is* the proto-`Op`-record, written in C# data, validated by the TomHarte sweep — exactly the evidence (B) needs and currently lacks.

3. **The seam constraint is satisfied for free, and reversibility is preserved.** Because (C) does not touch the dispatch seam, the fetch stream, the bus helpers, or the runner, it inherits the migration-readiness M4.5a proved. The eventual (B) promotion — lifting the stabilized descriptor table from the partial into the generator's `EmitOpcodeMethod`-analog — becomes *more* mechanical, not less, because the descriptor will already encode `(aluFn, ccrRule, operandShape, size)` in the exact tuple a generated body would. (C) is a strict subset of the path to (B); choosing it forecloses nothing.

> ### ⚠️ DECISION FOR COORDINATOR
> **One sub-decision is flagged for you to confirm before the M4.5b Planner runs** (the primary A/B/C call above is made; this is a scope nuance):
>
> **The `EmitOpcodeMethod`-vs-68000-local-record scope of the eventual (B).** ADR 0004 §4 sketched the end-state as the shared `Op(OperandSize)` / `EmitOpcodeMethod` path (the 6502/Z80/8086 row emitter, generalized). This ADR's analysis (Option B cons, point 1) finds that path **row-shaped and a poor fit** for the field-grammar CPU, and recommends the eventual (B) be a **68000-local generated op-table** (lifting the (C) descriptor into the generator) rather than literal `EmitOpcodeMethod` reuse. **This is a forward-looking refinement of ADR 0004 §4, not an M4.5b deliverable** — M4.5b ships (C). Confirm you accept "the end-state is a 68000-local op-table, not shared-`EmitOpcodeMethod` reuse" as the standing direction, or flag if you want the eventual promotion held to the literal ADR 0004 §4 `EmitOpcodeMethod` wording. Either way **M4.5b is unaffected** (it ships (C)); this only pins what M4.5c/d's promotion *target* is.

---

## 4. Consequences

**Good.**
- The ALU CCR rules are written and tested **once** (one `Ccr.Arith` / `Ccr.Logic` / `Ccr.ArithX` rule set), directly attacking the dominant ALU TomHarte-failure class.
- ~30 regular families collapse to one-line dispatch registrations; per-family code is the ALU lambda + the CCR rule + the operand shape — its irreducible content.
- The M4.5a seam is unchanged ⇒ the constraint holds trivially, the fetch/bus/runner stay agnostic, and 6502/Z80 byte-identity is preserved (every change stays gated to `model.FieldGrammar is not null` + the M68000 partial).
- The (C) descriptor *is* the evidence (B) needs: by M4.5c/d the team will know whether the descriptor generalizes across shift/rotate/bit/BCD too, making the promotion decision evidence-backed instead of speculative.
- Fully reversible; the promotion path to a generated op-table is strictly shorter from (C) than from (A).

**Bad / accepted costs.**
- A second abstraction (the ALU driver + descriptor table) now lives in the M68000 partial that is *not yet* the generated end-state — a deliberate intermediate. M4.5c/d will revisit promotion (now with evidence). This is accepted: the alternative (B-now) generalizes blind, and (A) duplicates.
- The irregular families (`MUL`/`DIV`/`EXT`/`CLR`/`TST`/`ADDX`/`SUBX`) keep bespoke hand-written bodies. This is correct — they *are* irregular — but it means M4.5b is "table-driven core + bespoke tail," not uniform. The split is along the natural regularity boundary and is documented per-family.
- The descriptor table is hand-maintained data in C# (not yet generated from the dataset). If a dataset edit shifts opIndices, the dispatch arms (already name-driven via `EmitMoveDispatchArms`, `CpuEmitter.cs:4204`) track automatically, but the descriptor *registrations* are by family name in the partial and must stay in sync — guarded by the per-family TomHarte sweep.

---

## 5. Structural guidance for the M4.5b Planner

Concrete shape to task out. This is op-body *structure*, not an implementation plan — the Planner expands TDD tasks.

### 5.1 The ALU driver + descriptor (new, in the M68000 partial — e.g. `M68000Cpu.Alu.cs`)

A single binary-ALU driver, parameterized by a per-family descriptor. Shapes (signatures, not bodies):

```csharp
// The per-family ALU function: (a, b, xIn, size) -> result. Pure; no state, no CCR.
private delegate uint AluFn(uint a, uint b, bool xIn, uint size);

// The per-family CCR rule: (a, b, result, size, xIn) -> the new CCR byte (given the old).
// One instance per CCR family: Arith (NZVCX from carry/overflow), Logic (NZ, V=C=0, X untouched),
// ArithX (Arith but Z is STICKY — ADDX/SUBX/NEGX clear Z only, never set it).
private delegate byte CcrRule(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr);

// The operand SHAPE: where A and B come from + where the result goes. The four ALU shapes:
//   RegEa     — Dn (bits 11-9) op EA(bits 5-0); direction bit 8 picks which is dest
//   ImmEa     — #imm (extension words) op EA(bits 5-0); dest = EA          (ADDI/ANDI/...)
//   QuickEa   — imm3 (bits 11-9, 0->8) op EA(bits 5-0); dest = EA          (ADDQ/SUBQ)
//   UnaryEa   — op EA(bits 5-0); dest = EA                                  (NEG/NOT/CLR/TST)
private enum AluShape { RegEa, ImmEa, QuickEa, UnaryEa }

// The driver: read A and B per the shape, apply aluFn, write the result (unless TST/CMP — compare-only),
// set CCR via ccrRule. ONE implementation of read-A/read-B/write/CCR for the whole regular core.
private void BinaryAluExecute(
    AluFn aluFn, CcrRule ccrRule, AluShape shape, bool writesResult,
    uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg);
```

The dispatch arms (already emitted name-driven by `EmitMoveDispatchArms` — extend the `op switch` in `CpuEmitter.cs:4209` with the ALU family names → their `*Execute` hooks) call thin per-family `*Execute` methods that are **one line each**:

```csharp
partial void AddExecute (uint ow, DecodeResult r, uint sz, uint sm, uint sr) => BinaryAluExecute(Alu.Add, Ccr.Arith, AluShape.RegEa, writesResult: true,  ow, r, sz, sm, sr);
partial void SubExecute (uint ow, DecodeResult r, uint sz, uint sm, uint sr) => BinaryAluExecute(Alu.Sub, Ccr.Arith, AluShape.RegEa, writesResult: true,  ow, r, sz, sm, sr);
partial void CmpExecute (uint ow, DecodeResult r, uint sz, uint sm, uint sr) => BinaryAluExecute(Alu.Sub, Ccr.Cmp,   AluShape.RegEa, writesResult: false, ow, r, sz, sm, sr);
partial void AndExecute (uint ow, DecodeResult r, uint sz, uint sm, uint sr) => BinaryAluExecute(Alu.And, Ccr.Logic, AluShape.RegEa, writesResult: true,  ow, r, sz, sm, sr);
// ... Or, Eor, AddI/SubI/AndI/OrI/EorI/CmpI (ImmEa), AddQ/SubQ (QuickEa), Neg/Not/Clr/Tst (UnaryEa)
```

### 5.2 Reuse the M4.5a substrate verbatim (do NOT re-plumb)

The ALU bodies reuse, unchanged: `ReadEaOperand` / `WriteEaOperand` / `SizeMask` / the `DataReg` / `SetDataRegPartial` partial-write / the wide-bus helpers (`M68000Cpu.Move.cs:24-60`, `M68000Cpu.cs:79-110`). These are the operand-model-agnostic primitives the seam constraint protected — **the ALU layer is a new caller of them, nothing in them changes.** The `RegEa` direction bit and the An-dest "no CCR" rule (`ADDA`/`SUBA`) are the only new operand-shape logic.

### 5.3 The bespoke tail (irregular families — hand-written, not table-driven)

Keep separate hand-written bodies, each with its own TomHarte file:
- **`EXT`** — Dn-only sign-extend (.b→.w, .w→.l); no EA; CCR from result.
- **`CLR`** — writes 0; CCR always `Z=1, N=V=C=0` (a quirk: `CLR` *reads* the EA on the 68000 before writing — a vector-confirmed dummy read; model it to match the transaction trace when the timing axis lands).
- **`TST`** — `UnaryEa`, `writesResult: false`, `Ccr.Logic`-ish (NZ from operand, V=C=0). May ride `BinaryAluExecute(UnaryEa, writesResult:false)` if the unary path is clean; Planner's call.
- **`ADDX`/`SUBX`/`NEGX`** — `ArithX` CCR (sticky Z) + the reg↔reg / -(An)↔-(An) operand shapes + X-flag in. The sticky-Z rule is the classic bug; isolate it in `Ccr.ArithX`.
- **`MULU`/`MULS`/`DIVU`/`DIVS`** — wide result to Dn (16×16→32; 32÷16→16:16). CCR from the result. **`DIVU`/`DIVS` divide-by-zero is an EXCEPTION → M4.5d** (detect-and-defer, exactly as MOVE deferred its ~23,200 exception cases — see §6).

### 5.4 The seam invariant (binding on M4.5b, same as M4.5a)

The fetch stream (`M68000FetchStream.cs`), the cycle-charging bus helpers (`M68000Cpu.cs:72-110`), and the Step+diff runner (`M68000TomHarteRunner.cs`) **stay operand-model-agnostic** — M4.5b adds the ALU driver + descriptors + dispatch arms + bespoke bodies, and touches **none** of those three. Every change stays gated to `model.FieldGrammar is not null` + the M68000 partial + the 680x0-only test infra. **6502/Z80 byte-identity is non-negotiable** (`RegeneratedSpecTests` green; additive only).

### 5.5 The eventual (B) promotion — what M4.5c/d inherits (NOT M4.5b work)

When M4.5c/d have shown whether the §5.1 descriptor generalizes across shift/rotate/bit/BCD, the promotion to a generated op-table is: (1) move the descriptor *table* from the partial into the generator (sourced from the dataset's already-present `sizeEncoding`/`legalEa`/operand-shape fields, `m68000-fieldgrammar.json`); (2) have the generator emit the per-family bodies that today are one-line `*Execute` registrations; (3) delete the hand-written registrations. The driver itself (`BinaryAluExecute`) can stay hand-written (it is the runtime, not the data) or be emitted — the Planner of that PR decides. **This is the 68000-local op-table, NOT literal `EmitOpcodeMethod` reuse** (see the ⚠️ Coordinator flag, §3).

---

## 6. The data-axis-first gate + the deferral discipline (standing policy M4.5b inherits)

Carried forward verbatim from the M4.5a correction (authority: ADR 0004 §3; the resume doc's gate-correction block). **M4.5b MUST keep this three-axis split** — do NOT repeat M4.5a's original over-specification:

- **Data/correctness axis (M4.5b asserts this, always):** `D0–D7, A0–A6, USP, SSP, SR, RAM`, byte-exact, across every in-scope ALU-family TomHarte file. The operword is seeded from `initial.prefetch[0]` into the bus (the v1 vectors place it there, never in `bus[pc]`).
- **Timing axis → M4.5d:** `final.pc`, `final.prefetch`, the per-transaction bus trace, the cycle count. The prefetch-queue mechanism + cycle-accurate sequencing are M4.5d; M4.5b carries them with `// TODO(M4.5d)` in the runner (the `timingAxis` flag, default off — already wired, `M68000TomHarteRunner.cs:70`).
- **Exceptions → M4.5d (detect-and-defer):** the **`DIVU`/`DIVS` divide-by-zero exception** (vector 5) and any address-error / privilege cases. M4.5b detects them and DEFERS (the un-fakeable signal: the vector-table-read-pair-equals-`final.pc` heuristic, `M68000TomHarteRunner.IsExceptionCase`, `:44`), exactly as MOVE deferred its exception cases — it does NOT assert them (asserting would be a drift false-positive). The divide-by-zero *detection* (quotient/divisor == 0) is computed in the `DIV` body; the *vectoring* is M4.5d.

**The per-PR three-part merge gate stands** (unchanged from the established loop): (1) full suite green + 6502/Z80 byte-identity (`RegeneratedSpecTests`); (2) the ALU-family TomHarte sweep run **green with the vectors PRESENT** (fetched first via `tools/get-test-vectors-68000.ps1`, run under `dotnet test -c Release` — not skipped); (3) pre-merge code review. Run heavy gates sequentially under `-c Release` (resume doc, operational notes).

---

## 7. Open questions

1. **Does the §5.1 descriptor survive M4.5c?** The shift/rotate/bit families (`ASL/LSR/ROXL/...`, `BTST/BCHG/...`) add a *shift-count / bit-number* operand and a different CCR shape (the X/C-from-shifted-out-bit rule). If the `(aluFn, ccrRule, shape)` descriptor extends cleanly to them, that is the green light to promote (B) in M4.5c/d. If it does not, (C) stays the terminal structure and (B) is reconsidered. **Resolve empirically in M4.5c — do not pre-commit.**
2. **`CLR`'s dummy read** — the 68000 reads the EA before writing 0 (a vector-confirmed quirk). On the data axis it is invisible (RAM unchanged by a read); on the M4.5d timing axis it is a real transaction. Confirm the `CLR` body issues the read so the M4.5d trace matches without a re-fix. (Detail, not a structural decision.)
3. **`TST` on the unary path** — whether `TST` rides `BinaryAluExecute(UnaryEa, writesResult:false)` or needs a bespoke body depends on how clean the unary read-only path is. Planner's call at implementation; either satisfies the data axis.
4. **The `ArithX` sticky-Z and the `ADDX`/`SUBX` -(An)↔-(An) operand shape** — confirm against the `ADDX.*`/`SUBX.*` vectors that the sticky-Z and the pre-decrement operand pairing are modeled before merge (the classic ALU-extend bug class).

---

*End of ADR 0007. The seam constraint held against the shipped code (the promotion is mechanical; fetch/bus/runner are agnostic by construction). M4.5b ships (C) — the table-driven ALU helper layer behind the unchanged dispatch seam — with the data/timing/exception axis split intact. Designer: no UX surface (headless framework). Planner can pick up the §5 structural guidance + the §6 gate from here.*
