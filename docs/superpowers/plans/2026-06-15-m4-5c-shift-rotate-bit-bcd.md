# M4.5c: The 68000 shift/rotate + bit + BCD + Scc + data-movement interpreter — the descriptor-generalization arc (ADR 0007 §7.1) — SINGLE PR

> **STATUS: DRAFT — awaiting Coordinator/user scope ratification of the M4.5d-deferral list (DC4). Otherwise
> APPROVED in content (verdict-b, CMPM folded in, single PR). Do NOT branch, queue, or implement until ratified.**
> This plan answers ADR 0007 §7.1 Open Question #1 (does the `(aluFn, ccrRule, shape)` descriptor generalize?)
> with file:line evidence + a recommendation, then tasks out ALL of M4.5c — shift/rotate, bit ops, BCD, Scc,
> CMPM, and data-movement misc — at uniform fidelity in ONE PR, to the same rigor as the merged M4.5b plan
> (`docs/superpowers/plans/2026-06-15-m4-5b-integer-alu.md`).
>
> **For agentic workers (once ratified):** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or
> superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. This is the THIRD of the four M4.5
> interpreter sub-PRs (a = MOVE ✅; b = integer ALU ✅ merged `d35362b`; **c = shift/rotate/bit/BCD/Scc/
> data-movement (this plan, ONE PR)**; d = exceptions/branches/IPL/prefetch + the control/stack/privileged tail).
> **M4.5b MUST be on `main`** (it is: `main` @ `d35362b`, PR #40) — this plan REUSES its shipped substrate
> verbatim: the `BinaryAluExecute` driver, the `AluFn`/`CcrRule`/`AluShape` descriptor types, the `Alu`/`AluCcr`
> static classes, `ResolveEaDest`/`WriteResolvedDest`/`AluDest` (the address-once RMW fix), `ReadSized`/
> `WriteSized`, the M4.5a `ReadEaOperand`/`WriteEaOperand`/`SetDataRegPartial`/`SizeMask` primitives + the wide
> bus, `ComputeEa`, the `M68000FetchStream`, and the `M68000TomHarteRunner` Step+diff.

---

## ⚠️ Decisions for Coordinator/user review

> M4.5c forced more forks than M4.5b. Each is stated with a recommendation + the alternative. The plan body is
> written assuming the resolved/recommended position of each. **DC2 is RESOLVED (single PR, per the user).
> DC4 (the M4.5c/M4.5d boundary) is STILL FLAGGED for user ratification — the explicit deferred list is in DC4
> and again in the "M4.5d deferral list" section for the checkpoint.**

### DC1 — The descriptor-generalization verdict (ADR 0007 §7.1 Open Question #1) → **(b): modest additive extension, NOT an ADR rewrite. Confidence: HIGH. APPROVED by user.**

**The question.** Does the merged `(AluFn, CcrRule, AluShape)` descriptor model (`M68000Cpu.Alu.cs:16-23`)
generalize to M4.5c's shift/rotate (count operand + X/C-from-last-bit-out + ASL's V-changed-during-shift), bit
ops (bit-number operand + Z-from-tested-bit), and BCD (decimal-adjust + X-in + sticky-Z)?

**Verdict: (b) — the descriptor generalizes for BCD and bit ops with NEW `CcrRule`s (existing signature) + thin
shapes, but shift/rotate need ONE modest, additive extension: a sibling `ShiftRotateExecute` driver + a richer
result carrier (last-bit-out + msb-changed) + new `Shift/Rotate/RotateX` CCR rules.** This stays inside ADR 0007
option (C) — no ADR rewrite, no Architect escalation, no premature jump to (B). It is the exact stabilization
evidence ADR 0007 §7 said M4.5c should produce. Recorded as a one-paragraph ADR 0007 §7.1 addendum (Task 1),
NOT a new ADR.

**Evidence the current descriptor does NOT fit shift/rotate as-is (file:line):**
1. **`AluFn` returns only the result value — the last-bit-shifted-out is lost.** `M68000Cpu.Alu.cs:16`:
   `public delegate uint AluFn(uint a, uint b, bool xIn, uint size)`. Shift/rotate set **X and C from the LAST
   bit shifted out**; for a count N>1 that bit is NOT recoverable from `(a, result)` — e.g. `LSR.b #3` of
   `0b00001000` → result `0b00000001`, but C = bit 2 of the ORIGINAL. The `CcrRule` at `:20` has `a` and
   `result` but NOT the carry chain — so the carry-out must be computed in the shift body and threaded explicitly.
2. **ASL's V = "MSB changed at ANY point during the shift" needs intermediate state.** The current
   `AluCcr.Arith` V-formula (`:211`, `((a ^ r) & (b ^ r)) & sb`) is a single-step overflow test; it structurally
   cannot express "changed during a multi-bit shift." A genuinely new CCR computation, not a reparameterization.
3. **The count-source is a THIRD operand-sourcing mode the `AluShape` switch does not cover.** `AluShape` (`:23`,
   `private enum { RegEa, ImmEa, QuickEa, UnaryEa }`) hardcodes A/B sourcing inside `BinaryAluExecute`
   (`:69-116`). Shift count = register **mod 64** / immediate **1-8** / implicit **1** (memory form). None is an
   existing shape. Cleaner to give shifts their own small driver than to bloat the binary driver.

**Evidence the descriptor DOES fit bit ops + BCD (so it is not (c)):**
- **Bit ops** are "A op B → maybe-write, set CCR" with a new well-behaved CCR rule: **Z from the tested bit,
  N/V/C/X untouched** — a new `BitCcr.BitTest` of the EXISTING signature (Z = `((a >> bit) & 1) == 0`). The RMW
  path reuses `ResolveEaDest`/`WriteResolvedDest` verbatim. Zero new shape.
- **BCD** is decimal add/sub with **X-in** + **sticky-Z** — sticky-Z is ALREADY solved (`AluCcr.ArithX`, `:266`),
  the X-in + `-(An),-(An)` predecrement pairing is ALREADY built (`XAlu`, `:433`). BCD = the `XAlu` shape with a
  decimal func + a BCD carry rule; the operand shape (bit 3) is IDENTICAL to ADDX/SUBX. The strongest "yes."

**Why NOT (c):** (c) (escalate to an Architect ADR toward the (B) op-table promotion) is warranted only if the
descriptor FAILED to generalize. It does not — BCD/bit slot in with one new rule each; shifts add a sibling
driver, not a redesign. The seam (fetch/bus/runner) is untouched, option (C) holds, reversibility is preserved.
The finding "the tuple holds for BCD/bit; shifts add a count-source + last-bit-out axis" is the input the
(B)-promotion Architect needs, produced as a §7.1 addendum rather than a blocking phase now.

- **Recommendation (APPROVED):** **(b)** — `ShiftRotateExecute` + richer carrier + new `Shift/Rotate/RotateX/
  BitTest/Bcd` rules; option C stands; record the §7.1 addendum.
- **Alternative:** **(c)** — bank the (B) op-table promotion now → escalate to Architect first. Rejected by user.

### DC2 — Single PR vs multi-PR split → **RESOLVED: SINGLE PR (per the user).**

The earlier draft recommended a 3-PR split (c1 shift/rotate; c2 bit+BCD+Scc+CMPM; c3 data-movement). **The user
chose to ship M4.5c as ONE PR.** Rationale recorded: the families, though five distinct machines, all (a) reuse
or directly extend the merged M4.5b table-driven layer, (b) are additive behind the same name-driven dispatch
seam, (c) gate against the same un-fakeable TomHarte runner on one axis (data), and (d) share the same merge
discipline — so one reviewable unit + one heavy gate run is acceptable and avoids three rounds of the dataset-
opIndex-shift + regen churn. The trade-off (a larger plan + a single bigger reconcile loop + 5 failure classes
in one gate) is accepted; this plan mitigates it by (i) centralizing each machine's CCR in ONE rule (the M4.5b
precedent — the dominant failure class is fixed in one place per machine), (ii) ordering the tasks
machine-by-machine so the suite stays green after each, and (iii) one consolidated 91-file sweep with a
per-file executed-count so a regression is localized.

- **Resolution:** **1 PR.** The plan below tasks out ALL families at uniform fidelity (Tasks 1-22), ONE merge
  gate, ONE sweep.
- **Superseded alternative:** the 3-PR split (recorded for provenance; not taken).

### DC3 — Fold the CMPM/CMPA vector-coverage fix INTO M4.5c → **FOLD IN as dedicated tasks (Tasks 14-15). Confidence: HIGH. APPROVED by user.**

CMPM is ABSENT from the FieldGrammar dataset (`grep -c '"CMPM"'` = 0), so its cases — bundled into `CMP.b/.w/.l`
and `CMPA.l` — are currently `outOfScope`-skipped via the `(operword & 0xF138u) == 0xB108u` filter at
`M68000AluTomHarteTests.cs:74` (that slot collides with EOR's mask and would mis-decode). The fix: add a
`"CMPM"` `FieldOp` dataset row (mask `0xF138`/match `0xB108`, ordered **BEFORE EOR** — EOR is at line 87 — so the
tighter mask wins, the ABCD-before-EXG precedent at line 92/93) + a `CmpMExecute` body (`(Ay)+,(Ax)+`
postincrement compare, no write, CMP CCR), REMOVE the filter, and re-run the 51 ALU files so the ~3,763 skipped
CMPM cases assert green.

- **Resolution:** **FOLD into M4.5c** (Tasks 14-15) — dataset row + body + filter removal + the ALU re-run as
  part of the single sweep. Closes the M4.5b honesty gap in the same PR. (Dataset edit → opIndex shift →
  name-driven arms track automatically; the regen guard re-pins; Builder re-runs the full sweep as the
  regression check.)
- **Superseded alternative:** a separate "M4.5b.1" hotfix PR.

### DC4 — The M4.5c / M4.5d boundary for the "system-misc" grab-bag → **⚠️ STILL FLAGGED for user ratification. Recommended: data-movement = M4.5c; stack/control/privileged/vectoring = M4.5d. Confidence: HIGH.**

The status doc (`2026-06-15-m4-status-and-resume.md:69`) lists "system/misc ops (MOVEM, LEA/PEA, SWAP, EXG,
LINK/UNLK, TRAP/TRAPV/CHK, NOP, RTS/RTR/JMP/JSR)" under M4.5c. But ADR 0007 §6 + the M4.5a/b axis split put
**anything that vectors, manipulates the stack as control flow, or is privileged into the M4.5d exception/
control axis.** Applying that line consistently:

**IN M4.5c (data-axis-assertable, no trap, no control transfer):**
`SWAP`, `EXG`, `LEA`, `PEA` (pushes via `-(A7)` but a plain memory write — data-axis-assertable like a MOVE to
`-(A7)`), `MOVEQ`, `TAS` (a read-modify-write; the indivisible-cycle quirk is a timing detail, M4.5d), `MOVEM`
(register-list load/store — plain bus traffic). [`MOVEP` is optional — present but least-used; recommend
including it since it is pure data movement, see DC5.]

**DEFER to M4.5d (control / stack-as-control / privileged / vectoring) — the explicit list to ratify:**
- `LINK` / `UNLK` — frame push/pop (pair with the call/return family for one coherent stack-discipline PR).
- `JMP` / `JSR` / `RTS` / `RTR` / `RTE` — control flow; `RTE` is privileged + un-stacks SR.
- `Bcc` / `BSR` / `DBcc` — program-control branches (already M4.5d).
- `TRAP` / `TRAPV` / `CHK` / `ILLEGAL` / `RESET` / `STOP` — vector/privileged.
- `ANDI` / `ORI` / `EORI` `-to-CCR` / `-to-SR` (the `*_CCR` / `*_SR` dataset rows) — touch the privileged system
  byte; M4.5d's privileged path.
- `NOP` — trivially could land in M4.5c, but grouped with the M4.5d control tail for one coherent PR. **Movable.**

- **Recommendation:** the boundary above. **The user ratifies the exact line** — the status doc and the
  axis-policy disagree at LINK/UNLK and NOP.
- **Alternative:** pull `LINK`/`UNLK`/`NOP` into M4.5c (they are simple). Acceptable; I keep them in M4.5d for
  stack-discipline coherence but flag them as movable.

### DC5 — Which TomHarte vector files cover M4.5c ops (VERIFIED against the actual cache) + honesty notes.

**Verified** against `~/.cache/cpuemulator/vectors/680x0/v1/*.json.gz` (124 files present):

| Family | Files PRESENT (verified) | Count |
|---|---|---|
| Shift/rotate | `ASL.{b,w,l}` `ASR.{b,w,l}` `LSL.{b,w,l}` `LSR.{b,w,l}` `ROL.{b,w,l}` `ROR.{b,w,l}` `ROXL.{b,w,l}` `ROXR.{b,w,l}` | **24** |
| Bit ops | `BTST` `BCHG` `BCLR` `BSET` (NO size suffix) | **4** |
| BCD | `ABCD` `SBCD` `NBCD` | **3** |
| Scc | `Scc` | **1** |
| Data-movement misc | `SWAP` `EXG` `LEA` `PEA` `MOVEQ` `TAS` `MOVEM.w` `MOVEM.l` | **8** |
| MOVEP (optional, DC5) | `MOVEP.w` `MOVEP.l` | (2) |
| CMPM | NONE (bundled in `CMP.{b,w,l}`/`CMPA.l`) | 0 |
| ALU re-run (CMPM now asserts) | the existing 51 M4.5b ALU files | **51** |

**M4.5c-core dedicated files = 24 + 4 + 3 + 1 + 8 = 40** (+ 2 with MOVEP). **The single merge-gate sweep = 40
dedicated + 51 ALU re-run = 91 files** (93 with MOVEP).

**Honesty notes (no gap for the core):**
- **Every shift/rotate, bit, BCD, Scc, and data-movement op HAS a dedicated vector file (verified)** — unlike
  M4.5b's immediate/quick forms, **there is NO vector-gap disclosure for M4.5c's core families.** All asserted
  green on the data axis.
- **CMPM has NO dedicated file** — its cases assert through `CMP.*`/`CMPA.l` once the DC3 dataset row lands
  (vector-backed via bundling).
- **Bit-op files have no size suffix** — the vectors mix `.b` (memory target) and `.l` (Dn target) within one
  file per op; the body size-selects from the EA mode (Dn → `.l` mod 32; memory → `.b` mod 8), NOT a decoded
  size field (these rows are `sizeWidth:1` inert). Flagged in Tasks 9-10.
- **MOVEP** is present + data-axis-assertable; recommend including it (Task 20). The only NON-asserted things in
  M4.5c are the timing axis (M4.5d) + any exception case (deferred via `IsExceptionCase`).

---

## Recon (verified read-only against `main` @ `d35362b` + the fetched vector cache)

> All facts confirmed against the merged tree and `~/.cache/cpuemulator/vectors/680x0/v1`. Builder re-confirms at
> Task 0. The dispatch is name-driven, so opIndices track the dataset automatically (the CMPM dataset edit,
> Task 14, shifts them — the regen guard re-pins, the arms follow).

### R1 — The governing decision (ADR 0007 — IMPLEMENT §5 option C; §7.1 is what this plan ANSWERS)
`docs/architecture/0007-68000-interpreter-op-body-structure.md` §3 = decision (C); §5.1 = the merged
`BinaryAluExecute`/`AluFn`/`CcrRule`/`AluShape` signatures; §5.4 = the seam invariant (binding); §6 = the
data-axis-first gate + timing/exception deferrals; **§7.1 Open Question #1 = the descriptor-generalization
question DC1 resolves**; §3's ⚠️ flag = the (B) promotion target is a 68000-local op-table (M4.5c banks the
stabilization evidence; this plan records the §7.1 addendum, does NOT promote).

### R2 — The M4.5b seam this plan EXTENDS (do not re-plumb — ADR 0007 §5.4)
- **The generated FieldGrammar `Step` arm** (`CpuEmitter.cs:218-252`): computes `__operword`/`__size`/`__srcMode`
  /`__srcReg`/`_eaPcBase`, dispatches by `__opIndex` via `EmitMoveDispatchArms`. M4.5c adds arms to that switch.
- **`EmitMoveDispatchArms`** (`CpuEmitter.cs:4237-4285`): the name-driven `op switch` (carries MOVE + the 30 ALU
  names). M4.5c adds the shift/bit/BCD/Scc/CMPM/data-movement operation names → their `*Execute` hooks here.
- **The partial-hook declaration emit** (`CpuEmitter.cs:306-329`, inside `if (model.FieldGrammar is not null)`):
  the ALU `foreach (var name in new[] {...})` block (`:322-328`). M4.5c adds sibling `foreach` blocks.
- **The fetch stream** (`M68000FetchStream.cs`), **the wide-bus helpers** (`M68000Cpu.cs:77-104`), **the Step+
  diff runner** (`M68000TomHarteRunner.cs`) — UNTOUCHED (seam invariant). M4.5c is a new CALLER only.

### R3 — The merged M4.5b layer M4.5c REUSES + EXTENDS (`M68000Cpu.Alu.cs`)
- **Descriptor types:** `AluFn` (`:16`), `CcrRule` (`:20`), `AluShape` (`:23`, `private`).
- **`Alu` static class** (`:28`, `public`): `Add/Sub/And/Or/Eor/AddX/SubX/NegFn/NotFn/TstFn/NegXFn`.
- **`BinaryAluExecute`** (`:55-121`) + **`AluDest`/`ResolveEaDest`/`WriteResolvedDest`** (`:129-175`, the
  address-once RMW fix) + `ShiftExt` (`:181`). REUSE for bit ops (RMW twin), Scc/TAS (address-once RMW).
- **`AluCcr` static class** (`:192`, `public`, **NON-partial**): `Arith`/`ArithAdd`/`ArithSub`/`Logic`/`NegRule`/
  `NegXRule`/`Cmp`/`ArithX`/`ArithXAdd`/`ArithXSub` + `*Probe`. **NOT `partial`, so M4.5c's new CCR rules live on
  SIBLING static classes** (`ShiftCcr`, `BitCcr`, `BcdCcr`) in the new partials — NOT reopening `AluCcr`. REUSE
  `AluCcr.Cmp`/`AluCcr.Logic` as callers.
- **`XAlu`** (`:433-460`): X-in + `-(An),-(An)` predecrement pairing — the SHAPE model BCD mirrors.
  `ReadSized`/`WriteSized` (`:462-464`) reuse for shift/CMPM memory access.
- **M4.5a primitives** (`M68000Cpu.Move.cs`): `ReadEaOperand`/`WriteEaOperand`/`SetDataRegPartial`/`DataReg`/
  `SizeMask`/`ReadByteAt`/`WriteByteAt`. `Areg`/`SetAreg`/`USP`/`SSP`/`A7` banking (generated `M68000Cpu.g.cs`:
  `Areg`/`SetAreg` at `:719`/`:722`) + `ComputeEa` (`pureEa:true` for LEA/PEA). The `Ccr` PROPERTY
  (`M68000Cpu.cs:41`) + `SR` field. REUSE all.

### R4 — The FieldGrammar dataset M4.5c rows (verified, `data/m68000-fieldgrammar.json`, 82 ops → 83 after CMPM)
Shift/rotate rows are **family-PAIR rows** (one row per L/R pair; operword bit 8 selects direction):

| Operation string | line | mask / match | shape |
|---|---|---|---|
| `"ASLR_REG"` | 101 | 0xF018 / 0xE000 | reg/imm-count shift; bit 8 = L/R, bit 5 = reg-vs-imm count |
| `"LSLR_REG"` | 102 | 0xF018 / 0xE008 | reg/imm-count logical shift |
| `"ROXLR_REG"` | 103 | 0xF018 / 0xE010 | reg/imm-count rotate-through-X |
| `"ROLR_REG"` | 104 | 0xF018 / 0xE018 | reg/imm-count rotate |
| `"SHIFT_MEM"` | 100 | 0xF8C0 / 0xE0C0 | memory shift-by-1, `.w` only; bits 10-9 = class, bit 8 = L/R |
| `"BTST"` `"BCHG"` `"BCLR"` `"BSET"` | 16-19 | 0xF1C0 / 0x0100..0x01C0 | dynamic bit# (Dn 11-9); BTST no write |
| `"BTST_STATIC"`..`"BSET_STATIC"` | 11-14 | 0xFFC0 / 0x0800..0x08C0 | static bit# (+1 imm word) |
| `"ABCD"` | 92 | 0xF1F0 / 0xC100 | BCD add; bit 3 = Dn-Dn vs -(An)-(An) (= ADDX shape) |
| `"SBCD"` | 79 | 0xF1F0 / 0x8100 | BCD sub (= SUBX shape) |
| `"NBCD"` | 50 | 0xFFC0 / 0x4800 | BCD negate (0 - dst - X), UnaryEa |
| `"Scc"` | 69 | 0xF0C0 / 0x50C0 | set byte 0xFF/0x00 by condition (bits 11-8) |
| `"SWAP"` | 40 | 0xFFF8 / 0x4840 | swap Dn halves |
| `"EXG"` | 93 | 0xF130 / 0xC100 | exchange registers (ordered AFTER ABCD — line-93 note) |
| `"LEA"` | 60 | 0xF1C0 / 0x41C0 | load EA (pureEa) → An |
| `"PEA"` | 51 | 0xFFC0 / 0x4840 | push EA (pureEa) → -(A7) |
| `"MOVEQ"` | 75 | 0xF100 / 0x7000 | sign-extend imm8 → Dn.l |
| `"TAS"` | 52 | 0xFFC0 / 0x4AC0 | test-and-set bit 7 (RMW) |
| `"MOVEM"` | 57 | 0xFB80 / 0x4880 | register-list load/store (+1 mask word) |
| `"MOVEP"` | 9 | 0xF038 / 0x0008 | peripheral move (+1 disp word) — optional |
| `"CMPM"` (NEW, Task 14) | insert before 87 | 0xF138 / 0xB108 | `(Ay)+,(Ax)+` compare; no write; CMP CCR |

> **Dataset ORDER hazards (load-bearing — the dataset documents the precedent at line 93):**
> 1. **EXG (0xF130/0xC100) collides with ABCD (0xF1F0/0xC100)** — ABCD's tighter mask MUST fire first (already
>    ordered: ABCD line 92 before EXG line 93). When M4.5c adds BOTH bodies, both decode correctly only because
>    of this order. Builder re-confirms the regen guard pins it.
> 2. **CMPM (NEW, 0xF138/0xB108) collides with EOR (0xF100/0xB100)** — CMPM's tighter mask MUST be inserted
>    BEFORE EOR (line 87) (Task 14). Verified: EOR is line 87, CMP line 88.
> 3. **PEA (0xFFC0/0x4840) and EXT (0xFFB8/0x4880) share the 0x4840-ish slot** — already ordered (EXT line 56,
>    PEA line 51); confirm PEA's body does not capture an EXT operword (Task 18 Step 0).

### R5 — The confirmed in-scope vector files (DC5, verified present)
40 dedicated M4.5c files (24 shift + 4 bit + 3 BCD + 1 Scc + 8 data-movement) + the 51 ALU re-run (CMPM
asserting) = **91-file single sweep** (93 with MOVEP). Each mnemonic+size-keyed, gzipped, ~8065 cases; operword
in `initial.prefetch[0]`; schema identical to M4.5a/b (runner unchanged).

### R6 — Static encoding facts the bodies need (PRM-derived; Builder confirms against vectors)
- **Shift/rotate register form (`*_REG`, 0xF018):** bits 11-9 = count reg OR imm (per bit 5); **bit 5 (i/r):**
  0 = immediate count (bits 11-9, 0→8), 1 = register count (Dn, **count = Dn mod 64**); **bit 8 (dr):** 0 =
  right, 1 = left; bits 7-6 = size; bits 2-0 = the Dn target. The shift FAMILY is the dataset ROW. CCR:
  - `ASL/ASR/LSL/LSR`: **C = X = last bit out** (count 0 → C=0, X UNCHANGED); N/Z from result; V: `ASL` = MSB
    changed during the shift, else 0.
  - `ROL/ROR`: **C = last bit rotated** (NOT X; X untouched); count 0 → C=0; V=0; N/Z from result.
  - `ROXL/ROXR` (through X): **C = X = last bit out**; count 0 → C = X (current), X unchanged; V=0; N/Z result.
- **Shift/rotate memory form (`SHIFT_MEM`, 0xF8C0):** `.w` only, count = 1; bits 10-9 = class (00=AS,01=LS,
  10=ROX,11=RO), bit 8 = L/R. Same CCR rules.
- **Bit ops:** bit# **mod 32 (Dn target, operand `.l`)** / **mod 8 (memory target, operand `.b`)**. Dynamic:
  bit# = `Dn(11-9)`. Static: bit# = the extension word low byte. **CCR: Z = (tested bit == 0); N/V/C/X
  UNCHANGED.** BTST no write; BCHG toggles / BCLR clears / BSET sets, then write back (RMW, address-once).
- **BCD:** `ABCD/SBCD` = decimal add/sub WITH X-in, `.b` only, **sticky Z**; operand shape bit 3 (0 = Dy,Dx
  Dn-Dn; 1 = -(Ay),-(Ax)) = the `XAlu` shape. `NBCD` = `0 - dst - X` decimal, `.b`, UnaryEa, sticky Z. The
  decimal-adjust (nibble half-carry) is the new logic; C/X from the decimal carry-out (the "undefined N but
  vector-pinned" quirk — reconcile in the ONE `BcdCcr` rule).
- **Scc:** byte EA = `0xFF` if condition (bits 11-8) true else `0x00`; **no CCR change**; RMW dummy-read-then-
  write like CLR (address-once for `(An)+`/`-(An)`). The cc evaluator (cc code → bool from CCR) is SHARED with
  M4.5d (Bcc/DBcc).
- **CMPM:** `(Ay)+,(Ax)+` — Ay = bits 11-9 (operand A), Ax = bits 2-0 (operand B); compare with result =
  `(Ay)+ - (Ax)+`; both `(An)+` postincrement; size = bits 7-6; no write; CMP CCR (X untouched). Confirm the
  pairing order against the bundled CMP vectors.
- **Data-movement:** `SWAP` Dn = swap high/low 16 (CCR N/Z from 32-bit result, V=C=0, X kept). `EXG` exchanges
  two registers (bits 11-9 + 2-0; bits 7-3 mode = D-D 01000 / A-A 01001 / D-A 10001; no CCR). `LEA` =
  `ComputeEa(pureEa:true)` → An (no CCR). `PEA` = `ComputeEa(pureEa:true)` → `-(A7)` (no CCR). `MOVEQ` =
  sign-extend bits 7-0 → Dn.l (CCR N/Z, V=C=0, X kept). `TAS` = read byte, set N/Z from it, write back with bit 7
  set (the indivisible RMW). `MOVEM` = load/store a register list per the +1 mask word (the `(An)+`/`-(An)`
  mask-bit ORDER is the classic bug). `MOVEP` = byte-lane move over `d16(An)` (+1 disp word).

---

## Scope

**IN scope (ALL tasked at uniform fidelity below — ONE PR):**
1. The ADR 0007 §7.1 addendum (DC1 verdict) — Task 1.
2. Shift/rotate: the `ShiftRotateExecute` driver + `ShiftKind` + the result carrier + `ShiftCcr` rules + the 8
   ops (reg/imm count + memory-by-1) — Tasks 2-6.
3. Bit ops (BTST/BCHG/BCLR/BSET dynamic + static) + `BitCcr.BitTest` — Tasks 9-10.
4. BCD (ABCD/SBCD/NBCD) + `AbcdByte`/`SbcdByte` + `BcdCcr` — Task 11.
5. Scc + the shared cc evaluator — Task 13.
6. CMPM (dataset row before EOR + body + filter removal) — Tasks 14-15.
7. Data-movement misc (SWAP/EXG/LEA/PEA/MOVEQ/TAS/MOVEM [+MOVEP]) — Tasks 16-20.
8. The generator dispatch arms + partial-hook declarations for ALL of the above — Task 21.
9. The single 91-file (93 w/ MOVEP) TomHarte data-axis green sweep — Task 22.

**OUT of scope (M4.5d — DC4, ratify the list):** `LINK`/`UNLK`, `JMP`/`JSR`/`RTS`/`RTR`/`RTE`, `Bcc`/`BSR`/
`DBcc`, `TRAP`/`TRAPV`/`CHK`/`ILLEGAL`/`RESET`/`STOP`, `ANDI/ORI/EORI-to-CCR/SR`, `NOP`; the TIMING axis
(`final.pc`/`final.prefetch`/trace/cycle, `timingAxis:false` throughout); any exception/vector/privilege case
(detect-and-defer via `IsExceptionCase`, unchanged); the (B) generated op-table promotion (evidence banked
here); the 68000 through the JIT (M4.6).

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `docs/architecture/0007-68000-interpreter-op-body-structure.md` | Modify | The §7.1 descriptor-generalization addendum (Task 1). No decision reversal. |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Shift.cs` | Create | `ShiftRotateExecute` + `ShiftKind` + the result carrier + the 8 shift registrations + `SHIFT_MEM` + `ShiftCcr`. |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Bit.cs` | Create | `BitOpExecute` (the RMW twin) + the 8 bit registrations (dynamic + static) + `BitCcr.BitTest`. |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Bcd.cs` | Create | `AbcdByte`/`SbcdByte` + the ABCD/SBCD/NBCD bodies via `BcdXAlu` + `BcdCcr`. |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Scc.cs` | Create | `EvaluateCondition` (shared) + the `Scc` body + `CmpMExecute`. |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.SystemMisc.cs` | Create | SWAP/EXG/LEA/PEA/MOVEQ/TAS/MOVEM [+MOVEP]. |
| `tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar.json` | Modify | The CMPM row inserted BEFORE EOR (Task 14). |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | Extend `EmitMoveDispatchArms` + the hook-declaration emit with ALL M4.5c names (Task 21). NO other generator change (except the optional `*_STATIC` leading-word decode arm if its test is red — Task 10). |
| `tests/.../Generators/M68000ShiftCcrTests.cs` `…ShiftExecuteTests.cs` `…BitBcdSccExecuteTests.cs` `…SystemMiscExecuteTests.cs` | Create | Synthetic CCR + execute unit tests (no vectors). |
| `tests/.../TomHarte/M68000M45cTomHarteTests.cs` | Create | The single skip-when-absent `[M68000TomHarteTheory]` over the 40 dedicated M4.5c files. |
| `tests/.../TomHarte/M68000AluTomHarteTests.cs` | Modify | REMOVE the CMPM `outOfScope` filter (Task 15). |

---

## TDD tasks (ordered; the suite stays green after each; literal code for every load-bearing piece)

> **Hoist Task 21 (generator) EARLY** — right after Task 2 establishes the first shift body — so all `partial
> void` declarations exist before Tasks 3-20 compile (the bodies are no-op `partial void` until filled — the
> M4.5b precedent). The single heavy gate is Task 22. Tasks 7-8 and 12 are intentionally reserved (the families
> collapse into shared drivers); kept for numbering alignment.

---

### Task 0: Baseline + recon (NO code change)

- [ ] **Step 1: Branch off `main`.** `git switch -c feat/m4-5c-shift-bit-bcd`. Confirm `d35362b`. Confirm M4.5b
  present (`M68000Cpu.Alu.cs` with `BinaryAluExecute`/`AluCcr`/`XAlu`/`ResolveEaDest`; `M68000AluTomHarteTests`).
- [ ] **Step 2: Green baseline.** `dotnet test` → 0 failures (record the count). `dotnet build
  --no-incremental -warnaserror` → clean.
- [ ] **Step 3: Recon (read-only).** The Step arm (`CpuEmitter.cs:218-252`); `EmitMoveDispatchArms` (`:4237`) +
  the hook `foreach` (`:322-328`); the reused M4.5b surface (R3) reachable from sibling partials
  (`Alu`/`AluCcr` public; `BinaryAluExecute`/`XAlu`/`ResolveEaDest`/`WriteResolvedDest`/`ReadSized`/`WriteSized`/
  `ShiftExt` private on the same class → yes); the dataset rows (R4 — VERBATIM); the 40 dedicated vector files
  present; `grep -c '"CMPM"'` = 0; EOR is line 87.
- [ ] **Step 4:** No commit. Proceed to Task 1.

---

### Task 1: The ADR 0007 §7.1 addendum (DOC)

**Files:** Modify `docs/architecture/0007-68000-interpreter-op-body-structure.md`.

- [ ] **Step 1: Append to §7** a `> ### ✅ RESOLVED by M4.5c (2026-06-15)` block stating the DC1 verdict: the
  `(AluFn, CcrRule, AluShape)` tuple GENERALIZES to BCD (via the `XAlu` shape + a new `BcdCcr` rule — zero new
  shape) and bit ops (a new `BitCcr.BitTest` of the existing signature + a `BinaryAluExecute`-twin RMW path —
  zero new shape); shift/rotate need ONE additive extension (a sibling `ShiftRotateExecute` driver + a richer
  result carrier + `ShiftCcr.Shift/Rotate/RotateX`) because (a) `AluFn` loses the last-bit-out
  (`M68000Cpu.Alu.cs:16`), (b) ASL's V needs intermediate state (`:211`), (c) the count-source is a third
  operand axis (`:23`/`:69-116`). **Option (C) STANDS; the eventual (B) op-table must encode a `countSource` +
  a `lastBitOut`/`msbChanged` CCR-input axis. No ADR reversal.**
- [ ] **Step 2:** Doc-only; `dotnet test` unaffected. Commit.

```bash
git add docs/architecture/0007-68000-interpreter-op-body-structure.md
git commit -m "$(cat <<'EOF'
docs(arch): ADR 0007 §7.1 addendum — descriptor generalizes (BCD/bit fit; shifts add a count+last-bit axis), option C stands

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```
**New-test estimate:** 0.

---

### Task 2: The shift CCR rules — `ShiftCcr.Shift/Rotate/RotateX` (TDD)

> The highest-leverage move (M4.5b precedent): the shift CCR rules written + tested ONCE, taking carry-out +
> msb-changed as EXPLICIT inputs. They live on a SIBLING `ShiftCcr` static class in the new `M68000Cpu.Shift.cs`
> (the merged `AluCcr` is NON-partial — do NOT reopen it).

**Files:** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.Shift.cs`; create
`tests/CpuEmulator.Tests/Generators/M68000ShiftCcrTests.cs`.

- [ ] **Step 1: Write the failing tests** (`M68000ShiftCcrTests.cs`):

```csharp
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000ShiftCcrTests
{
    // CCR bits: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01
    [Fact]
    public void Shift_sets_C_and_X_from_last_bit_out_NZ_from_result()
        => Assert.Equal(0x04 | 0x01 | 0x10,
            M68000Cpu.ShiftCcr.ShiftProbe(result: 0x00u, size: 0u, lastBitOut: true, msbChanged: false, oldCcr: 0x00));

    [Fact]
    public void Shift_count_zero_clears_C_keeps_X_unchanged()
        => Assert.Equal(0x08 | 0x10,
            M68000Cpu.ShiftCcr.ShiftProbe(result: 0x80u, size: 0u, lastBitOut: false, msbChanged: false,
                                          oldCcr: 0x10, countZero: true));   // N + X preserved; C cleared

    [Fact]
    public void Asl_V_set_when_msb_changed_during_shift()
        => Assert.Equal(0x02, M68000Cpu.ShiftCcr.ShiftProbe(0x00u, 0u, true, true, 0x00) & 0x02);

    [Fact]
    public void Rotate_sets_C_from_last_bit_does_NOT_touch_X()
        => Assert.Equal(0x01 | 0x10,
            M68000Cpu.ShiftCcr.RotateProbe(result: 0x01u, size: 0u, lastBitOut: true, oldCcr: 0x10));

    [Fact]
    public void RotateX_through_X_sets_C_equals_X_from_last_bit()
        => Assert.Equal(0x04 | 0x01 | 0x10,
            M68000Cpu.ShiftCcr.RotateXProbe(result: 0x00u, size: 0u, lastBitOut: true, oldCcr: 0x00));
}
```

- [ ] **Step 2: Run to verify it fails** (`M68000Cpu.ShiftCcr` does not exist).
- [ ] **Step 3: Create `M68000Cpu.Shift.cs` with the `ShiftCcr` rules:**

```csharp
namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c (ADR 0007 §7.1 — option C, the modest extension): the shift/rotate helper layer. Shifts set X/C from
/// the LAST BIT SHIFTED OUT (not recoverable from a/result for count>1) and ASL sets V from "the MSB changed
/// during the shift" (intermediate state) — so the shift CCR rules take carry-out + msb-changed as EXPLICIT
/// inputs, and shifts run through a SIBLING ShiftRotateExecute driver (NOT BinaryAluExecute). Count = a register
/// (mod 64), an immediate (1-8), or implicitly 1 (the memory form). Reuses the M4.5b/M4.5a substrate
/// (ResolveEaDest/WriteResolvedDest, SetDataRegPartial, DataReg, SizeMask, the wide bus) — a new caller; the
/// seam is untouched (ADR 0007 §5.4). ShiftCcr is a SIBLING static class (AluCcr is non-partial — not reopened).
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>The shift/rotate CCR rules. carryOut/msbChanged are EXPLICIT inputs; countZero handles the
    /// count-0 quirk. CCR bits: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01.</summary>
    public static class ShiftCcr
    {
        private static uint SignBit(uint size) => size switch { 0u => 0x80u, 1u => 0x8000u, _ => 0x80000000u };
        private static uint Mask(uint size)    => size switch { 0u => 0xFFu, 1u => 0xFFFFu, _ => 0xFFFFFFFFu };

        private static byte NZ(uint result, uint size, byte ccr)
        {
            if ((result & SignBit(size)) != 0) ccr |= 0x08;
            if ((result & Mask(size)) == 0)    ccr |= 0x04;
            return ccr;
        }

        /// <summary>ASL/ASR/LSL/LSR: C=X=last bit out (count>0); count 0 -> C=0, X UNCHANGED. V: ASL=msbChanged,
        /// else 0. N/Z from result.</summary>
        internal static byte Shift(uint result, uint size, bool lastBitOut, bool msbChanged, byte oldCcr, bool countZero)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);            // clear N Z V C; X handled below
            ccr = NZ(result, size, ccr);
            if (msbChanged) ccr |= 0x02;                  // V (ASL only; the driver passes false otherwise)
            if (countZero)
            {
                ccr = (byte)(ccr & ~0x01);                // C = 0
                ccr = (byte)((ccr & ~0x10) | (oldCcr & 0x10));   // X UNCHANGED
            }
            else
            {
                if (lastBitOut) ccr |= 0x01;              // C
                ccr = (byte)((ccr & ~0x10) | (lastBitOut ? 0x10 : 0x00));   // X = C
            }
            return ccr;
        }

        /// <summary>ROL/ROR: C=last bit rotated; X UNTOUCHED; V=0; N/Z from result; count 0 -> C=0.</summary>
        internal static byte Rotate(uint result, uint size, bool lastBitOut, byte oldCcr, bool countZero)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);            // clear N Z V C; keep X
            ccr = NZ(result, size, ccr);
            if (!countZero && lastBitOut) ccr |= 0x01;    // C
            return ccr;                                    // X preserved
        }

        /// <summary>ROXL/ROXR (rotate through X): C=X=last bit out; count 0 -> C=X (current), X unchanged; V=0.</summary>
        internal static byte RotateX(uint result, uint size, bool lastBitOut, byte oldCcr, bool countZero)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);            // clear N Z V C
            ccr = NZ(result, size, ccr);
            bool x = countZero ? (oldCcr & 0x10) != 0 : lastBitOut;
            if (x) ccr |= 0x01;                            // C = X
            ccr = (byte)((ccr & ~0x10) | (x ? 0x10 : 0x00));
            return ccr;
        }

        // Test seams.
        public static byte ShiftProbe(uint result, uint size, bool lastBitOut, bool msbChanged, byte oldCcr, bool countZero = false)
            => Shift(result, size, lastBitOut, msbChanged, oldCcr, countZero);
        public static byte RotateProbe(uint result, uint size, bool lastBitOut, byte oldCcr, bool countZero = false)
            => Rotate(result, size, lastBitOut, oldCcr, countZero);
        public static byte RotateXProbe(uint result, uint size, bool lastBitOut, byte oldCcr, bool countZero = false)
            => RotateX(result, size, lastBitOut, oldCcr, countZero);
    }
}
```

  > **The shift CCR formulas (last-bit-out, count-0 X-quirk, ASL V-changed, ROXL/ROXR through-X) are the most
  > TomHarte-sensitive code in the shift half.** Reconcile sweep failures HERE (one place), never in the bodies.

- [ ] **Step 4: Run to verify it passes.** **Step 5: Full gate** (`dotnet test`, `-warnaserror`,
  `RegeneratedSpecTests`). **Step 6: Commit** (`feat(m68000): the shift/rotate CCR rules …`). **Est:** ~5.

---

### Task 3: The `ShiftRotateExecute` driver + `ShiftKind` + the result carrier (TDD)

> The sibling driver: read the count per source, loop the shift capturing last-bit-out + (ASL) msb-changed,
> write the result, set CCR via the right `ShiftCcr` rule. Driver tests via `cpu.Step()` —
> `[Fact(Skip="dispatch wired in Task 21")]` until dispatch lands.

**Files:** Modify `M68000Cpu.Shift.cs`; create `tests/CpuEmulator.Tests/Generators/M68000ShiftExecuteTests.cs`.

- [ ] **Step 1: Write the failing/skip tests** (mirror M4.5b's `M68000AluExecuteTests` `Build(...)` + `Step()`):
  reg-count mod 64, imm-count 0→8, each of the 8 kinds, `.b/.w/.l` partial write, the memory-by-1 form.
- [ ] **Step 2: Add the driver + enum** to `M68000Cpu.Shift.cs`:

```csharp
    /// <summary>The 8 shift/rotate kinds. The registration decodes operword bit 8 (direction) within each pair.</summary>
    private enum ShiftKind { Asl, Asr, Lsl, Lsr, Rol, Ror, Roxl, Roxr }

    /// <summary>The shift/rotate driver (ADR 0007 §7.1 sibling to BinaryAluExecute). REGISTER form (0xF018):
    /// count = reg(Dn mod 64) or imm(bits 11-9, 0->8) per bit 5, target Dn(bits 2-0), size bits 7-6. MEMORY form
    /// (SHIFT_MEM): count 1, .w, target EA. Captures last-bit-out + (ASL) msbChanged; sets CCR via ShiftCcr.</summary>
    private void ShiftRotateExecute(ShiftKind kind, uint operword, CpuEmulator.Core.Jit.DecodeResult r,
        uint size, uint srcMode, uint srcReg, bool memoryForm)
    {
        uint mask = SizeMask(size);
        byte oldCcr = (byte)(SR & 0xFF);
        bool xIn = (oldCcr & 0x10) != 0;

        int count;
        uint value;
        AluDest dest;
        uint targetDn = operword & 7u;
        if (memoryForm)
        {
            count = 1;
            dest = ResolveEaDest(srcMode, srcReg, size, r.ExtensionWords, out value);   // .w memory RMW (address-once)
            value &= mask;
        }
        else
        {
            bool regCount = (operword & 0x20u) != 0;                  // bit 5: 1 = register count
            if (regCount) count = (int)(DataReg((operword >> 9) & 7u) % 64u);   // Dn mod 64
            else { uint q = (operword >> 9) & 7u; count = q == 0u ? 8 : (int)q; } // imm 1-8 (0->8)
            value = DataReg(targetDn) & mask;
            dest = AluDest.DataRegister(targetDn);
        }

        uint sb = size switch { 0u => 0x80u, 1u => 0x8000u, _ => 0x80000000u };
        uint v = value & mask;
        bool lastBitOut = false, msbChanged = false;
        for (int i = 0; i < count; i++)
        {
            bool msbBefore = (v & sb) != 0;
            switch (kind)
            {
                case ShiftKind.Asl: case ShiftKind.Lsl:
                    lastBitOut = (v & sb) != 0; v = (v << 1) & mask; break;
                case ShiftKind.Asr:
                    lastBitOut = (v & 1u) != 0; v = ((v >> 1) | (msbBefore ? sb : 0u)) & mask; break;   // sign-fill
                case ShiftKind.Lsr:
                    lastBitOut = (v & 1u) != 0; v = (v >> 1) & mask; break;
                case ShiftKind.Rol:
                    lastBitOut = (v & sb) != 0; v = ((v << 1) | (lastBitOut ? 1u : 0u)) & mask; break;
                case ShiftKind.Ror:
                    lastBitOut = (v & 1u) != 0; v = ((v >> 1) | (lastBitOut ? sb : 0u)) & mask; break;
                case ShiftKind.Roxl:
                    lastBitOut = (v & sb) != 0; v = ((v << 1) | (xIn ? 1u : 0u)) & mask; xIn = lastBitOut; break;
                default: /* Roxr */
                    { bool lobit = (v & 1u) != 0; v = ((v >> 1) | (xIn ? sb : 0u)) & mask; lastBitOut = lobit; xIn = lobit; } break;
            }
            if (((v & sb) != 0) != msbBefore) msbChanged = true;
        }
        uint result = v & mask;

        if (memoryForm) WriteResolvedDest(dest, size, result);
        else SetDataRegPartial(targetDn, result, size);

        bool countZero = count == 0;
        byte ccr = kind switch
        {
            ShiftKind.Asl => ShiftCcr.Shift(result, size, lastBitOut, msbChanged, oldCcr, countZero),
            ShiftKind.Asr or ShiftKind.Lsl or ShiftKind.Lsr
                          => ShiftCcr.Shift(result, size, lastBitOut, msbChanged: false, oldCcr, countZero),
            ShiftKind.Rol or ShiftKind.Ror   => ShiftCcr.Rotate(result, size, lastBitOut, oldCcr, countZero),
            _ /* Roxl/Roxr */                => ShiftCcr.RotateX(result, size, lastBitOut, oldCcr, countZero),
        };
        SR = (ushort)((SR & 0xFF00) | ccr);
    }
```

  > **Notes (Builder resolves against vectors):** (1) `ShiftKind` has exactly 8 members; the `default` arm is
  > Roxr. (2) ROXL/ROXR count-0: `RotateX(countZero:true)` reads the current X from `oldCcr` — the subtlest CCR
  > edge; confirm against the `ROXL.*`/`ROXR.*` count-0 cases (Task 22). (3) the shift body lives in the driver
  > (needs the per-step carry chain — the §7.1 finding). (4) the memory write is a true RMW; `ResolveEaDest`
  > does the address-once `(An)+`/`-(An)` write-back — do NOT also call `ReadEaOperand`.

- [ ] **Step 3:** Build (driver compiles standalone). **Step 4:** (after Task 21) un-skip → PASS. **Step 5:**
  Full gate. **Step 6:** Commit. **Est:** ~8 (un-skipped after Task 21).

---

### Task 4: The register-form shift registrations — ASL/ASR/LSL/LSR (TDD)

**Files:** Modify `M68000Cpu.Shift.cs`; modify `M68000ShiftExecuteTests.cs`.

- [ ] **Step 1: Add the registrations** (body NAMES must match the generator's `name+"Execute"`, Task 21):

```csharp
    // bit 8 (dr): 0 = right, 1 = left. The dataset ROW picks the shift FAMILY; bit 8 picks the direction.
    partial void AslrRegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => ShiftRotateExecute((operword & 0x0100u) != 0 ? ShiftKind.Asl : ShiftKind.Asr, operword, r, size, srcMode, srcReg, memoryForm: false);
    partial void LslrRegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => ShiftRotateExecute((operword & 0x0100u) != 0 ? ShiftKind.Lsl : ShiftKind.Lsr, operword, r, size, srcMode, srcReg, memoryForm: false);
```

- [ ] **Step 2-6:** failing-test → green (post Task 21) → full gate → commit. **Est:** ~4.

---

### Task 5: The register-form rotate registrations — ROL/ROR/ROXL/ROXR (TDD)

**Files:** Modify `M68000Cpu.Shift.cs`; modify `M68000ShiftExecuteTests.cs`.

- [ ] **Step 1: Add the registrations:**

```csharp
    partial void RolrRegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => ShiftRotateExecute((operword & 0x0100u) != 0 ? ShiftKind.Rol : ShiftKind.Ror, operword, r, size, srcMode, srcReg, memoryForm: false);
    partial void RoxlrRegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => ShiftRotateExecute((operword & 0x0100u) != 0 ? ShiftKind.Roxl : ShiftKind.Roxr, operword, r, size, srcMode, srcReg, memoryForm: false);
```

- [ ] **Step 2-6:** as Task 4. **Est:** ~4.

---

### Task 6: The memory-by-1 shift form — SHIFT_MEM (TDD)

**Files:** Modify `M68000Cpu.Shift.cs`; modify `M68000ShiftExecuteTests.cs`.

- [ ] **Step 1: Add the body:**

```csharp
    partial void ShiftMemExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        // .w memory shift-by-1. bits 10-9: 00=AS, 01=LS, 10=ROX, 11=RO. bit 8: 1=left, 0=right.
        bool left = (operword & 0x0100u) != 0;
        uint cls = (operword >> 9) & 3u;
        ShiftKind kind = cls switch
        {
            0u => left ? ShiftKind.Asl  : ShiftKind.Asr,
            1u => left ? ShiftKind.Lsl  : ShiftKind.Lsr,
            2u => left ? ShiftKind.Roxl : ShiftKind.Roxr,
            _  => left ? ShiftKind.Rol  : ShiftKind.Ror,
        };
        ShiftRotateExecute(kind, operword, r, size: 1u /* .w */, srcMode, srcReg, memoryForm: true);
    }
```

- [ ] **Step 2-6:** as Task 4 (the EA modes are the `MemoryAlterable` set — confirm against `SHIFT_MEM` vectors,
  Task 22; size forced `.w`). **Est:** ~3.

---

### Tasks 7-8: (reserved — the 8 shifts collapse into Tasks 4-6, one driver). No code.

---

### Task 9: Bit ops — the `BitOpExecute` RMW twin + `BitCcr.BitTest` + the DYNAMIC forms (TDD)

> Bit ops are "A op bit# → maybe-write, Z from the tested bit." A `BinaryAluExecute`-twin RMW path (REUSE
> `ResolveEaDest`/`WriteResolvedDest`), bit# from `Dn(11-9)` (dynamic) or the extension word (static), operand
> size from the EA mode (Dn → `.l` mod 32; memory → `.b` mod 8). `BitCcr.BitTest`: Z from the tested bit;
> N/V/C/X UNCHANGED. (DC1 evidence: zero new shape.)

**Files:** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.Bit.cs`; create
`tests/CpuEmulator.Tests/Generators/M68000BitBcdSccExecuteTests.cs` (shared by Tasks 9-13).

- [ ] **Step 1: Write the failing tests** — `BitCcr.BitTest` (Z set when the tested bit is 0; N/V/C/X preserved)
  + execute tests via `Step()` for BTST(no write)/BCHG/BCLR/BSET, Dn (`.l` mod 32) + memory (`.b` mod 8).
  `[Fact(Skip="dispatch wired in Task 21")]` for the execute half.
- [ ] **Step 2: Create `M68000Cpu.Bit.cs`:**

```csharp
namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c bit ops (ADR 0007 §7.1 — the descriptor fits with a new CCR rule + the existing RMW path). BTST/BCHG/
/// BCLR/BSET, dynamic (bit# = Dn bits 11-9) and static (bit# = the extension word low byte). Operand size is
/// EA-mode-selected: a Dn target is .l (bit# mod 32); a memory target is .b (bit# mod 8). CCR: Z from the tested
/// bit; N/V/C/X UNCHANGED. BTST does not write; the others toggle/clear/set then write back (RMW, address-once
/// via ResolveEaDest — the M4.5b double-compute fix). Reuses the merged ALU layer as a caller; seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    private enum BitKind { Tst, Chg, Clr, Set }

    /// <summary>The bit-op CCR rule: Z from the tested bit (BEFORE any modification); N/V/C/X kept.</summary>
    public static class BitCcr
    {
        public static byte BitTest(uint operand, int bit, byte oldCcr)
        {
            byte ccr = (byte)(oldCcr & ~0x04);                 // clear Z; keep N V C X
            if (((operand >> bit) & 1u) == 0u) ccr |= 0x04;    // Z = tested bit was 0
            return ccr;
        }
        public static byte BitTestProbe(uint operand, int bit, byte oldCcr) => BitTest(operand, bit, oldCcr);
    }

    /// <summary>The bit-op driver. bitNumber pre-resolved (dynamic or static); writes is false only for BTST.</summary>
    private void BitOpExecute(BitKind kind, int bitNumber, uint srcMode, uint srcReg,
        CpuEmulator.Core.Jit.ExtensionWords ext)
    {
        bool isReg = srcMode == 0u;                    // Dn target -> .l, bit mod 32; memory -> .b, bit mod 8
        uint size = isReg ? 2u : 0u;
        int bit = isReg ? (bitNumber & 31) : (bitNumber & 7);

        AluDest dest = ResolveEaDest(srcMode, srcReg, size, ext, out uint operand);   // address-once read
        SR = (ushort)((SR & 0xFF00) | BitCcr.BitTest(operand, bit, (byte)(SR & 0xFF))); // Z from the tested bit

        if (kind == BitKind.Tst) return;               // BTST: no write
        uint mbit = 1u << bit;
        uint result = kind switch
        {
            BitKind.Chg => operand ^ mbit,
            BitKind.Clr => operand & ~mbit,
            _           => operand | mbit,             // Set
        };
        WriteResolvedDest(dest, size, result);
    }

    // Dynamic: bit# = Dn(bits 11-9). The EA is the target (bits 5-0 = srcMode/srcReg).
    partial void BtstExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Tst, (int)DataReg((operword >> 9) & 7u), srcMode, srcReg, r.ExtensionWords);
    partial void BchgExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Chg, (int)DataReg((operword >> 9) & 7u), srcMode, srcReg, r.ExtensionWords);
    partial void BclrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Clr, (int)DataReg((operword >> 9) & 7u), srcMode, srcReg, r.ExtensionWords);
    partial void BsetExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Set, (int)DataReg((operword >> 9) & 7u), srcMode, srcReg, r.ExtensionWords);
}
```

- [ ] **Step 3-6:** failing-test → green (BitTest now; execute post-Task-21) → full gate → commit. **Est:** ~6.

---

### Task 10: Bit ops — the STATIC forms (BTST_STATIC..BSET_STATIC) (TDD)

> The bit# is the leading extension word (low byte); the EA's words follow (`ShiftExt` by 1).

**Files:** Modify `M68000Cpu.Bit.cs`; modify `M68000BitBcdSccExecuteTests.cs`.

- [ ] **Step 0: Confirm the leading bit-number word is captured by the decode walk.** Run a
  `Btst_static_decode_captures_bit_word` test (analogous to the M4.5b `Addi_w_decode_captures…`). The dataset
  marks the `*_STATIC` rows `+1 bit-number word` (`sizeWidth:1` → `ExtensionWordCount` likely yields 1 already);
  confirm empirically. If RED, add the `*_STATIC` rows to the leading-imm-word set in `EmitFieldDecodeWalk`
  (generator) per the M4.5b Task-6 precedent.
- [ ] **Step 1: Add the static registrations:**

```csharp
    // Static: bit# = the LEADING extension word low byte; the EA's words follow (ShiftExt by 1).
    partial void BtstStaticExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Tst, (int)(r.ExtensionWords[0] & 0xFFu), srcMode, srcReg, ShiftExt(r.ExtensionWords, 1));
    partial void BchgStaticExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Chg, (int)(r.ExtensionWords[0] & 0xFFu), srcMode, srcReg, ShiftExt(r.ExtensionWords, 1));
    partial void BclrStaticExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Clr, (int)(r.ExtensionWords[0] & 0xFFu), srcMode, srcReg, ShiftExt(r.ExtensionWords, 1));
    partial void BsetStaticExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Set, (int)(r.ExtensionWords[0] & 0xFFu), srcMode, srcReg, ShiftExt(r.ExtensionWords, 1));
```

  > **`ShiftExt` is `private static` on `M68000Cpu.Alu.cs:181` — reachable from this sibling partial.** The
  > indexer returns a `ushort`/`uint`; `& 0xFFu` yields the bit-number byte; the body's `& 31`/`& 7` masks it.

- [ ] **Step 2-6:** as Task 9. **Est:** ~5.

---

### Task 11: BCD — `AbcdByte`/`SbcdByte` + `BcdCcr` + the ABCD/SBCD/NBCD bodies (TDD)

> BCD = decimal add/sub with X-in + sticky-Z (the `XAlu` shape model). The new logic is the decimal-adjust
> (nibble half-carry) + the BCD carry rule. The decimal funcs surface the decimal carry-out (like shifts surface
> the last-bit-out) — `BcdCcr` takes it explicitly.

**Files:** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.Bcd.cs`; modify `M68000BitBcdSccExecuteTests.cs`.

- [ ] **Step 1: Write the failing tests** — `BcdCcr` (C/X from the decimal carry; sticky Z) + the decimal-adjust
  truth table (e.g. `0x09 ABCD 0x01 = 0x10` carry 0; `0x99 ABCD 0x01 = 0x00` carry 1). `[Fact(Skip=…)]` for the
  execute half.
- [ ] **Step 2: Create `M68000Cpu.Bcd.cs`:**

```csharp
namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c BCD (ADR 0007 §7.1 — the descriptor fits via the XAlu SHAPE + a new CCR rule, ZERO new shape). ABCD/
/// SBCD = decimal add/sub with X-in, .b only, sticky Z; the operand shape (bit 3: Dn-Dn vs -(An)-(An)) is
/// IDENTICAL to ADDX/SUBX, so BcdXAlu mirrors the merged XAlu but with decimal funcs + the BCD carry. NBCD =
/// 0 - dst - X decimal, .b, UnaryEa. The decimal carry drives C and X; Z is sticky. The "undefined N but
/// vector-pinned" 68000 quirk is reconciled in BcdCcr against the ABCD/SBCD/NBCD vectors. Seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>BCD CCR: C=X=decimal carry-out (explicit input); Z STICKY (cleared on non-zero, preserved on
    /// zero — never freshly set); N from .b MSB and V=0 are the "undefined but vector-pinned" pair (reconcile
    /// in Task 22). carryOut is the decimal carry from the body.</summary>
    public static class BcdCcr
    {
        public static byte Bcd(uint result, bool carryOut, byte oldCcr)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);                 // clear N Z V C; X handled below
            if ((result & 0x80u) != 0) ccr |= 0x08;            // N from .b MSB (vector-pinned)
            // V left 0 (vector-pinned for the common path; confirm in Task 22).
            if (carryOut) ccr |= 0x01;                          // C
            ccr = (byte)((ccr & ~0x10) | (carryOut ? 0x10 : 0x00));   // X = C
            // Sticky Z: clear it, then preserve oldCcr's Z only when the result byte is zero.
            ccr = (byte)(ccr & ~0x04);
            if ((result & 0xFFu) == 0u) ccr |= (byte)(oldCcr & 0x04);
            return ccr;
        }
        public static byte BcdProbe(uint result, bool carryOut, byte oldCcr) => Bcd(result, carryOut, oldCcr);
    }

    /// <summary>Decimal add of two BCD bytes with X-in. Returns the .b result; outputs the decimal carry.</summary>
    private static uint AbcdByte(uint a, uint b, bool xIn, out bool carry)
    {
        uint lo = (a & 0x0F) + (b & 0x0F) + (xIn ? 1u : 0u);
        uint hi = (a >> 4) + (b >> 4);
        if (lo > 9) { lo += 6; hi += 1; }
        bool c = hi > 9;
        if (c) hi += 6;
        carry = c;
        return ((hi << 4) | (lo & 0x0F)) & 0xFFu;
    }

    /// <summary>Decimal sub (a - b - X). Returns the .b result; outputs the borrow as 'carry' (C=X on borrow).</summary>
    private static uint SbcdByte(uint a, uint b, bool xIn, out bool carry)
    {
        int lo = (int)(a & 0x0F) - (int)(b & 0x0F) - (xIn ? 1 : 0);
        int hi = (int)(a >> 4) - (int)(b >> 4);
        if (lo < 0) { lo += 10; hi -= 1; }
        bool borrow = hi < 0;
        if (borrow) hi += 10;
        carry = borrow;
        return (((uint)hi << 4) | ((uint)lo & 0x0F)) & 0xFFu;
    }

    // ABCD/SBCD: bit 3 (R/M): 0 = Dy,Dx (Dn-Dn); 1 = -(Ay),-(Ax) (predecrement). Same shape as ADDX/SUBX.
    partial void AbcdExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BcdXAlu(operword, add: true);
    partial void SbcdExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BcdXAlu(operword, add: false);

    private void BcdXAlu(uint ow, bool add)
    {
        bool xIn = (Ccr & 0x10) != 0;
        byte oldCcr = (byte)(SR & 0xFF);
        uint yReg = (ow >> 9) & 7u;   // Dy / Ay (the dest, operand A)
        uint xReg = ow & 7u;          // Dx / Ax (the source, operand B)
        bool mem  = (ow & 0x0008u) != 0;

        uint a, b, result; bool carry;
        if (!mem)   // Dx,Dy -> Dy (.b)
        {
            a = DataReg(yReg) & 0xFFu;
            b = DataReg(xReg) & 0xFFu;
            result = add ? AbcdByte(a, b, xIn, out carry) : SbcdByte(a, b, xIn, out carry);
            SetDataRegPartial(yReg, result, 0u);
        }
        else        // -(Ax),-(Ay) -> (Ay) : predecrement BOTH (source Ax first, then dest Ay — the pairing)
        {
            uint aAddr = ComputeEa(4u, xReg, 0u, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ax)
            b = ReadByteAt(aAddr) & 0xFFu;
            uint dAddr = ComputeEa(4u, yReg, 0u, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ay)
            a = ReadByteAt(dAddr) & 0xFFu;
            result = add ? AbcdByte(a, b, xIn, out carry) : SbcdByte(a, b, xIn, out carry);
            WriteByteAt(dAddr, (byte)result);
        }
        SR = (ushort)((SR & 0xFF00) | BcdCcr.Bcd(result, carry, oldCcr));
    }

    // NBCD: 0 - dst - X (decimal), .b, UnaryEa (the EA is both source and dest).
    partial void NbcdExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        bool xIn = (Ccr & 0x10) != 0;
        byte oldCcr = (byte)(SR & 0xFF);
        AluDest dest = ResolveEaDest(srcMode, srcReg, 0u, r.ExtensionWords, out uint operand);
        uint result = SbcdByte(0u, operand & 0xFFu, xIn, out bool carry);    // 0 - operand - X
        WriteResolvedDest(dest, 0u, result);
        SR = (ushort)((SR & 0xFF00) | BcdCcr.Bcd(result, carry, oldCcr));
    }
}
```

  > **The decimal-adjust is the standard nibble-carry model; the 68000 has documented edge cases (the
  > `$0A..$0F` nibble + the N/V flags) the vectors pin.** Treat as the defensible first model; reconcile in the
  > ONE `BcdCcr` + the two byte helpers against `ABCD`/`SBCD`/`NBCD` (Task 22) — the subtlest after shifts.
  > `BcdXAlu` mirrors the merged `XAlu` (`M68000Cpu.Alu.cs:433`) — do NOT route BCD through `XAlu` (its `aluFn`
  > is binary, its CCR is `ArithX`, not decimal/`BcdCcr`).

- [ ] **Step 3-6:** failing-test → green (BcdCcr + byte helpers now; execute post-Task-21) → full gate → commit.
  **Est:** ~7.

---

### Task 12: (reserved — ABCD/SBCD/NBCD all landed in Task 11, shared `BcdXAlu`/`BcdCcr`). No code.

---

### Task 13: Scc — the shared cc evaluator + the byte-set body (TDD)

> Scc sets a byte EA to 0xFF/0x00 by the condition (bits 11-8); NO CCR change; RMW dummy-read-then-write
> (address-once). `EvaluateCondition(cc, ccr) -> bool` (the 16 cc codes) is SHARED — M4.5d's Bcc/DBcc reuse it.

**Files:** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.Scc.cs`; modify `M68000BitBcdSccExecuteTests.cs`.

- [ ] **Step 1: Write the failing tests** — `EvaluateConditionProbe` truth table for all 16 cc codes against
  crafted CCR bytes + Scc execute (0xFF/0x00 to Dn and memory). `[Fact(Skip=…)]` for the execute half.
- [ ] **Step 2: Create `M68000Cpu.Scc.cs`:**

```csharp
namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c Scc + the SHARED condition evaluator (reused by M4.5d's Bcc/DBcc). Scc writes a byte EA = 0xFF if the
/// condition (operword bits 11-8) is true else 0x00; NO CCR change; the 68000 reads the EA before writing (the
/// dummy read, like CLR) so the RMW is address-once via ResolveEaDest. CMPM (the M4.5b carried-forward fix,
/// Task 14) also lives here as a bespoke ALU-ish compare. Reuses the merged layer; seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>The 16 M68000 condition codes (operword bits 11-8) evaluated against a CCR byte.
    /// X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01.</summary>
    private static bool EvaluateCondition(uint cc, byte ccr)
    {
        bool n = (ccr & 0x08) != 0, z = (ccr & 0x04) != 0, v = (ccr & 0x02) != 0, c = (ccr & 0x01) != 0;
        return cc switch
        {
            0x0u => true,                  // T
            0x1u => false,                 // F
            0x2u => !c && !z,              // HI
            0x3u => c || z,                // LS
            0x4u => !c,                    // CC (HS)
            0x5u => c,                     // CS (LO)
            0x6u => !z,                    // NE
            0x7u => z,                     // EQ
            0x8u => !v,                    // VC
            0x9u => v,                     // VS
            0xAu => !n,                    // PL
            0xBu => n,                     // MI
            0xCu => n == v,                // GE
            0xDu => n != v,                // LT
            0xEu => !z && (n == v),        // GT
            _    => z || (n != v),         // LE (0xF)
        };
    }
    public static bool EvaluateConditionProbe(uint cc, byte ccr) => EvaluateCondition(cc, ccr);

    partial void SccExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint cc = (operword >> 8) & 0xFu;
        byte val = (byte)(EvaluateCondition(cc, (byte)(SR & 0xFF)) ? 0xFF : 0x00);
        AluDest dest = ResolveEaDest(srcMode, srcReg, 0u, r.ExtensionWords, out _);   // .b dummy read (address-once)
        WriteResolvedDest(dest, 0u, val);                                             // NO CCR change
    }
}
```

- [ ] **Step 3-6:** failing-test → green (the evaluator now; Scc execute post-Task-21) → full gate → commit.
  **Est:** ~6 (incl. the 16-cc truth table).

---

### Task 14: CMPM — add the dataset row (before EOR) + the body (TDD)

> The M4.5b carried-forward fix (DC3). Insert a `"CMPM"` `FieldOp` row (mask `0xF138`/match `0xB108`) BEFORE EOR
> (line 87) so the tighter mask wins (the ABCD-before-EXG precedent), then a `CmpMExecute` body. DATASET edit →
> the regen guard re-pins opIndices → the name-driven dispatch arms (incl. the existing ALU arms) track
> automatically. Builder re-runs the FULL ALU sweep as the regression check (Task 22 includes it).

**Files:** Modify `tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar.json`; modify `M68000Cpu.Scc.cs`;
modify a decode/regen test.

- [ ] **Step 1: Insert the CMPM row** immediately BEFORE the `"EOR"` row (line 87):

```json
  { "operation": "CMPM", "mask": "0xF138", "match": "0xB108", "sizeShift": 6, "sizeWidth": 2, "sizeEncoding": "Standard", "eaShift": 0, "legalEa": "All", "source": "M68000PRM 4-77 (CMPM): (Ay)+,(Ax)+ - tighter mask than EOR, MUST precede it" },
```

  > **ORDER is load-bearing** (R4 hazard #2): CMPM's mask `0xF138` is tighter than EOR's `0xF100` and they share
  > the `0xB1xx` space — CMPM MUST be matched first. After the edit, regenerate (`dotnet build`) and confirm the
  > `M68000RegeneratedSpecTests` regen guard re-pins cleanly (it shows the opIndex shift; expected — the dispatch
  > is name-driven). Confirm the existing M4.5a/b dispatch arms still bind (they will — name-resolved).

- [ ] **Step 2: Add the `CmpMExecute` body** to `M68000Cpu.Scc.cs`:

```csharp
    // CMPM (Ay)+,(Ax)+ : compare two postincrement-memory operands; NO write; CMP CCR (X untouched).
    // Ay = bits 11-9 (operand A); Ax = bits 2-0 (operand B). Both (An)+; size = bits 7-6 (.b/.w/.l).
    partial void CmpMExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint ay = (operword >> 9) & 7u;   // (Ay)+ operand A
        uint ax = operword & 7u;          // (Ax)+ operand B
        byte oldCcr = (byte)(SR & 0xFF);
        // Postincrement BOTH (Ax first as the source, then Ay — confirm the order against the bundled CMP vectors).
        uint axAddr = ComputeEa(3u, ax, size, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false);  // (Ax)+
        uint b = ReadSized(axAddr, size) & SizeMask(size);
        uint ayAddr = ComputeEa(3u, ay, size, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false);  // (Ay)+
        uint a = ReadSized(ayAddr, size) & SizeMask(size);
        uint result = (a - b) & SizeMask(size);
        SR = (ushort)((SR & 0xFF00) | AluCcr.Cmp(a, b, result, size, false, oldCcr));   // CMP CCR (X kept)
    }
```

  > **The (Ay)+ vs (Ax)+ pairing order is the classic CMPM bug** (like ADDX's -(An) pairing). result =
  > `(Ay)+ - (Ax)+`. Confirm the operand-A/B + the postincrement ORDER against the bundled CMPM cases (Task 22 —
  > the filter is removed in Task 15). REUSE `ReadSized`/`AluCcr.Cmp`/`SizeMask` from the merged layer.

- [ ] **Step 3-6:** decode test (a CMPM operword `0xB1x8`-family decodes as `CMPM`, NOT `EOR`) → green → full
  gate (the regen guard re-pins; the existing ALU arms still bind; 6502/Z80 untouched) → commit. **Est:** ~3.

---

### Task 15: Remove the CMPM out-of-scope filter from the ALU sweep (TDD)

> Now that CMPM decodes (Task 14), the ~3,763 bundled CMPM cases in `CMP.*`/`CMPA.l` must ASSERT, not skip.

**Files:** Modify `tests/CpuEmulator.Tests/TomHarte/M68000AluTomHarteTests.cs`.

- [ ] **Step 1: Remove the filter** at `M68000AluTomHarteTests.cs:73-74` (the
  `if ((operword & 0xF138u) == 0xB108u) { outOfScope++; continue; }` line) + the now-unused `outOfScope` counter
  and its mention in the failure message. The CMPM cases flow through `RunCase` like any other case.
- [ ] **Step 2: Update the doc comment** at the top of the file (`:16-20`) to state CMPM is NOW decodable +
  asserted (M4.5c), no longer "dropped / out-of-scope."
- [ ] **Step 3:** The verification is the heavy sweep (Task 22 re-runs the 51 ALU files — the CMPM cases now
  count `executed`). On a no-vector box this is a no-op edit.
- [ ] **Step 4-6:** full gate (Debug; ALU theory SKIPS without vectors) → commit. **Est:** 0 (un-skips existing
  cases; the count grows in Task 22).

---

### Task 16: SWAP + MOVEQ (TDD)

> SWAP exchanges Dn's halves (CCR N/Z from the 32-bit result, V=C=0, X kept). MOVEQ sign-extends bits 7-0 → Dn.l
> (CCR N/Z, V=C=0, X kept).

**Files:** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.SystemMisc.cs`; create
`tests/CpuEmulator.Tests/Generators/M68000SystemMiscExecuteTests.cs`.

- [ ] **Step 1: Write the failing/skip tests** for SWAP + MOVEQ (value + CCR).
- [ ] **Step 2: Create `M68000Cpu.SystemMisc.cs` with SWAP + MOVEQ:**

```csharp
namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c data-movement system-misc (DC4 boundary: data-axis-assertable, no trap/control-transfer). SWAP/EXG/LEA/
/// PEA/MOVEQ/TAS/MOVEM [+MOVEP]. The stack/control/privileged tail (LINK/UNLK, JMP/JSR/RTS/RTR/RTE, TRAP/TRAPV/
/// CHK, ANDI/ORI/EORI-to-CCR/SR, NOP) is M4.5d. Reuses ComputeEa(pureEa) + the merged layer; seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    partial void SwapExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint dn = operword & 7u;
        uint cur = DataReg(dn);
        uint result = (cur >> 16) | (cur << 16);
        SetDataRegPartial(dn, result, 2u);                  // whole-long write
        SR = (ushort)((SR & 0xFF00) | AluCcr.Logic(0, 0, result, 2u, false, (byte)(SR & 0xFF)));  // N/Z, V=C=0, X kept
    }

    partial void MoveQExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint dn = (operword >> 9) & 7u;
        uint result = unchecked((uint)(int)(sbyte)(byte)(operword & 0xFFu));   // sign-extend imm8 -> .l
        SetDataRegPartial(dn, result, 2u);
        SR = (ushort)((SR & 0xFF00) | AluCcr.Logic(0, 0, result, 2u, false, (byte)(SR & 0xFF)));
    }
}
```

- [ ] **Step 3-6:** failing-test → green (post-Task-21) → full gate → commit. **Est:** ~4.

---

### Task 17: EXG (TDD)

> Exchange two registers. bits 11-9 = Rx, bits 2-0 = Ry; bits 7-3 mode: `01000` = D-D, `01001` = A-A, `10001` =
> D-A (Rx is Dn, Ry is An). No CCR. Decodes AFTER ABCD (the line-93 collision — Task 21/regen confirm).

**Files:** Modify `M68000Cpu.SystemMisc.cs`; modify `M68000SystemMiscExecuteTests.cs`.

- [ ] **Step 1: Add the body:**

```csharp
    partial void ExgExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint rx = (operword >> 9) & 7u;
        uint ry = operword & 7u;
        uint mode = (operword >> 3) & 0x1Fu;   // bits 7-3
        switch (mode)
        {
            case 0x08u: { uint t = DataReg(rx); SetDataRegPartial(rx, DataReg(ry), 2u); SetDataRegPartial(ry, t, 2u); break; } // D-D
            case 0x09u: { uint t = Areg(rx);    SetAreg(rx, Areg(ry));               SetAreg(ry, t);                break; } // A-A
            default:    { uint t = DataReg(rx); SetDataRegPartial(rx, Areg(ry), 2u);  SetAreg(ry, t);                break; } // D-A (0x11): Rx=Dn, Ry=An
        }
        // EXG sets NO CCR.
    }
```

- [ ] **Step 2-6:** as Task 16 (confirm the D-A direction against the `EXG` vectors). **Est:** ~3.

---

### Task 18: LEA + PEA (TDD)

> LEA = `ComputeEa(pureEa:true)` → An (no write-back, no CCR). PEA = `ComputeEa(pureEa:true)` → push via
> `-(A7)` (no CCR).

**Files:** Modify `M68000Cpu.SystemMisc.cs`; modify `M68000SystemMiscExecuteTests.cs`.

- [ ] **Step 0: Confirm PEA does not capture an EXT operword** (R4 hazard #3 — EXT 0xFFB8/0x4880 vs PEA 0xFFC0/
  0x4840). The dataset already orders EXT before PEA; a decode test (`Pea_operword_decodes_as_PEA_not_EXT`)
  confirms. If RED, adjust the dataset order (unlikely — the merged decode handles EXT).
- [ ] **Step 1: Add the bodies:**

```csharp
    partial void LeaExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint an = (operword >> 9) & 7u;                                            // dest An (bits 11-9)
        uint ea = ComputeEa(srcMode, srcReg, 2u, r.ExtensionWords, pureEa: true);  // address only (no write-back)
        SetAreg(an, ea);                                                            // whole An; no CCR
    }

    partial void PeaExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint ea = ComputeEa(srcMode, srcReg, 2u, r.ExtensionWords, pureEa: true);
        uint sp = A7 - 4u;                                                          // push: -(A7)
        A7 = sp;
        WriteLongBus(sp, ea);                                                       // no CCR
    }
```

  > **`A7` is the banked stack view** (`M68000Cpu.cs:52`). PEA pushes a long (`WriteLongBus` = two `.w`
  > transactions). Confirm against the `PEA` vectors that `-(A7)` predecrements by 4 and the long lands BE.

- [ ] **Step 2-6:** as Task 16. **Est:** ~4.

---

### Task 19: TAS (TDD)

> Test-and-set: read the byte EA, set N/Z from it, write back with bit 7 set. A `.b` RMW (address-once).

**Files:** Modify `M68000Cpu.SystemMisc.cs`; modify `M68000SystemMiscExecuteTests.cs`.

- [ ] **Step 1: Add the body:**

```csharp
    partial void TasExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        AluDest dest = ResolveEaDest(srcMode, srcReg, 0u, r.ExtensionWords, out uint operand);   // .b read (address-once)
        uint b = operand & 0xFFu;
        byte ccr = (byte)(SR & 0xFF);
        ccr = (byte)(ccr & ~0x0F);                          // clear N Z V C; keep X
        if ((b & 0x80u) != 0) ccr |= 0x08;                  // N from bit 7 of the ORIGINAL
        if (b == 0u) ccr |= 0x04;                           // Z
        SR = (ushort)((SR & 0xFF00) | ccr);                 // V=C=0
        WriteResolvedDest(dest, 0u, b | 0x80u);             // write back with bit 7 set
    }
```

  > **The TAS RMW is INDIVISIBLE on hardware** (a locked bus cycle) — a TIMING-axis detail (M4.5d). On the data
  > axis, read-then-write-with-bit-7 is what the vectors assert; the address-once `ResolveEaDest` keeps the
  > `(An)+`/`-(An)` write-back correct.

- [ ] **Step 2-6:** as Task 16. **Est:** ~3.

---

### Task 20: MOVEM (+ optional MOVEP) (TDD)

> MOVEM loads/stores a register list per the +1 mask word. dr (bit 10): 0 = registers→memory, 1 = memory→
> registers. sz (bit 6): 0 = .w, 1 = .l. The mask-bit ORDER: `-(An)` predecrement = REVERSED (A7..D0); all other
> modes = D0..A7. This ordering is the classic MOVEM bug. MOVEP (optional, DC5) is a byte-lane move over
> `d16(An)`.

**Files:** Modify `M68000Cpu.SystemMisc.cs`; modify `M68000SystemMiscExecuteTests.cs`.

- [ ] **Step 1: Write the failing/skip tests** — register→memory + memory→register, `.w` + `.l`, the `-(An)`
  reversed-mask case + a `(An)+` forward-mask case, the `.w`-to-register sign-extension. [+ MOVEP if included.]
- [ ] **Step 2: Add the MOVEM body:**

```csharp
    partial void MoveMExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        // r.ExtensionWords[0] = the register-list mask. dr (bit 10): 0 = regs->mem, 1 = mem->regs.
        // sz (bit 6): 0 = .w (2 bytes, sign-extend on load), 1 = .l (4 bytes). The EA (bits 5-0) is the base.
        uint mask16 = r.ExtensionWords[0];
        bool toRegs = (operword & 0x0400u) != 0;
        uint opSize = (operword & 0x0040u) != 0 ? 2u : 1u;          // .l : .w
        int unit = opSize == 2u ? 4 : 2;
        var eaExt = ShiftExt(r.ExtensionWords, 1);                  // the mask word precedes the EA's words

        if (srcMode == 4u && !toRegs)   // -(An) predecrement STORE: mask is A7..D0 (REVERSED), pre-decrement each
        {
            uint addr = Areg(srcReg);
            for (int i = 0; i < 16; i++)
            {
                if ((mask16 & (1u << i)) == 0) continue;
                int regIndex = 15 - i;                              // bit0 -> A7 (15) ... bit15 -> D0 (0)
                uint val = regIndex < 8 ? DataReg((uint)regIndex) : Areg((uint)(regIndex - 8));
                addr -= (uint)unit;
                if (opSize == 2u) WriteWordBus(addr, (ushort)val); else WriteLongBus(addr, val);
            }
            SetAreg(srcReg, addr);                                  // write back the final -(An)
            return;
        }

        // All other modes: mask is D0..A7 (forward); compute the base ONCE (pureEa), walk ascending.
        uint ea = ComputeEa(srcMode, srcReg, opSize, eaExt, pureEa: true);
        uint cursor = ea;
        for (int i = 0; i < 16; i++)
        {
            if ((mask16 & (1u << i)) == 0) continue;                // bit0 -> D0 ... bit8 -> A0 ... bit15 -> A7
            if (toRegs)
            {
                uint raw = opSize == 2u ? ReadWordBus(cursor) : ReadLongBus(cursor);
                uint val = opSize == 2u ? unchecked((uint)(int)(short)(ushort)raw) : raw;   // .w sign-extends to 32
                if (i < 8) SetDataRegPartial((uint)i, val, 2u); else SetAreg((uint)(i - 8), val);
            }
            else
            {
                uint val = i < 8 ? DataReg((uint)i) : Areg((uint)(i - 8));
                if (opSize == 2u) WriteWordBus(cursor, (ushort)val); else WriteLongBus(cursor, val);
            }
            cursor += (uint)unit;
        }
        if (srcMode == 3u && toRegs) SetAreg(srcReg, cursor);       // (An)+ load writes back the advanced An
    }
```

  > **MOVEM is the largest, most bug-prone body in M4.5c** (the reversed `-(An)` mask, the `(An)+` write-back,
  > the `.w`-to-register sign-extension, pre-vs-post addressing). The literal is the defensible model; reconcile
  > against `MOVEM.w`/`MOVEM.l` (Task 22) — expect 1-2 rounds. The `pureEa:true` base is deliberate (MOVEM
  > computes the base once, walks — it must NOT trigger a per-register `(An)+` write-back via `ComputeEa`).

- [ ] **Step 2b (optional, MOVEP):** if DC5-included, add `MovePExecute` (byte-lane move over `d16(An)`: dr bit 7
  = direction, sz bit 6 = .w/.l; each byte to/from every OTHER byte of the `d16(An)` address). Gate against
  `MOVEP.w`/`MOVEP.l` in Task 22. If excluded, MOVEP stays `HandleUndefinedOpcode` (M4.5d tail).
- [ ] **Step 3-6:** as Task 16. **Est:** ~6 (+2 if MOVEP).

---

### Task 21: The generator dispatch arms + partial-hook declarations (generator) (TDD)

> **HOIST this early** (right after Task 2 establishes the first body) so all `partial void` declarations exist
> before Tasks 3-20 compile (the bodies are no-op `partial void` until filled — the M4.5b precedent). Extend
> `EmitMoveDispatchArms` (`CpuEmitter.cs:4280`, add to the `op switch`) with ALL M4.5c operation names → hooks,
> and add the matching `partial void *Execute(...)` declarations to the FieldGrammar-gated emit (`:322-328`).
> NO other generator change (except the optional `*_STATIC` leading-word decode arm — Task 10 Step 0 — if red).

**Files:** Modify `src/CpuEmulator.Generators/CpuEmitter.cs`; modify the relevant test files (the un-skips).

- [ ] **Step 1: Extend `EmitMoveDispatchArms`** — add to the `op switch` (after `"DIVS"`, before `_ => null`):

```csharp
                // ── M4.5c: shift/rotate, bit, BCD, Scc, CMPM, data-movement. All take (__operword,__r,__size,__srcMode,__srcReg). ──
                "ASLR_REG"     => "AslrRegExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "LSLR_REG"     => "LslrRegExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ROLR_REG"     => "RolrRegExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ROXLR_REG"    => "RoxlrRegExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "SHIFT_MEM"    => "ShiftMemExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "BTST"         => "BtstExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "BCHG"         => "BchgExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "BCLR"         => "BclrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "BSET"         => "BsetExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "BTST_STATIC"  => "BtstStaticExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "BCHG_STATIC"  => "BchgStaticExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "BCLR_STATIC"  => "BclrStaticExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "BSET_STATIC"  => "BsetStaticExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ABCD"         => "AbcdExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "SBCD"         => "SbcdExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "NBCD"         => "NbcdExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "Scc"          => "SccExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "CMPM"         => "CmpMExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "SWAP"         => "SwapExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "MOVEQ"        => "MoveQExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "EXG"          => "ExgExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "LEA"          => "LeaExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "PEA"          => "PeaExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "TAS"          => "TasExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "MOVEM"        => "MoveMExecute(__operword, __r, __size, __srcMode, __srcReg)",
                // "MOVEP"     => "MovePExecute(__operword, __r, __size, __srcMode, __srcReg)",   // optional (DC5)
```

  > **CMPM is wired here** (Task 14 added its dataset row). If MOVEP is included (Task 20 Step 2b), un-comment.

- [ ] **Step 2: Add the partial-hook declarations** — a sibling `foreach` after the ALU one (`:328`):

```csharp
            // M4.5c: the shift/bit/BCD/Scc/CMPM/data-movement op bodies — hand-written M68000Cpu.* partials.
            foreach (var name in new[] {
                "AslrReg","LslrReg","RolrReg","RoxlrReg","ShiftMem",
                "Btst","Bchg","Bclr","Bset","BtstStatic","BchgStatic","BclrStatic","BsetStatic",
                "Abcd","Sbcd","Nbcd","Scc","CmpM",
                "Swap","MoveQ","Exg","Lea","Pea","Tas","MoveM" /*, "MoveP"*/ })
            {
                sb.AppendLine($"    partial void {name}Execute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg);");
            }
```

  > **Name consistency (load-bearing):** the declared `{name}Execute` MUST equal the body method names
  > (`AslrRegExecute`…`MoveMExecute`, `CmpMExecute`, `BtstStaticExecute`). The dataset strings (`ASLR_REG`,
  > `BTST_STATIC`, `CMPM`, `MOVEM`) map to the PascalCase hook via the explicit `op switch` — no automatic case
  > transform. `CmpM`/`MoveM`/`MoveQ` (trailing upper) match the body names (the `MulU`/`MulS` precedent).

- [ ] **Step 3: Dispatch smoke tests** (not skipped — prove routing once the bodies land): e.g.
  `Step_routes_an_asl_operword` (ASL.w #1,D0 = `0xE340`) + `Step_routes_a_swap_operword` (SWAP D0 = `0x4840`).
  Confirm the operword encodings against the dataset at recon.
- [ ] **Step 4: Build + run.** The generator emits arms + declarations; the bodies (Tasks 3-20) bind. The
  un-implemented `partial void` are no-ops until filled — the suite COMPILES at every intermediate state.
- [ ] **Step 5: Full gate.** `dotnet test` green; `-warnaserror` clean; `RegeneratedSpecTests` green (the M4.5c
  arms + declarations emit ONLY inside `model.FieldGrammar is not null`; 6502/Z80 byte-identical). The CMPM
  dataset edit (Task 14) shifted opIndices — the regen guard re-pinned; confirm no 6502/Z80 spec changed.
- [ ] **Step 6: Commit.** **Est:** ~2 (the un-skips happen in Tasks 3-20).

---

### Task 22: The SINGLE M4.5c TomHarte data-axis green sweep (the gate)

> ONE sweep covering ALL M4.5c vector files: the 40 dedicated (24 shift + 4 bit + 3 BCD + 1 Scc + 8 data-
> movement) + the 51 ALU re-run (CMPM now asserting via Task 15) = **91 files** (93 w/ MOVEP). Run under
> `-c Release` with the vectors fetched. Heavy gate — SEQUENTIAL, coarse monitor.

**Files:** Create `tests/CpuEmulator.Tests/TomHarte/M68000M45cTomHarteTests.cs` (the 40 dedicated files); the 51
ALU re-run is the EXISTING `M68000AluTomHarteTests` (CMPM cases asserting, Task 15).

- [ ] **Step 1: Write the sweep theory** (copy the EXACT shape of the merged `M68000AluTomHarteTests.cs` —
  `TryGetVectorDirectory`/`Assert.NotNull`, `File.Exists`, `M68000TomHarteLoader.LoadFile`,
  `M68000TomHarteRunner.RunCase`, the `DeferredException` ReferenceEquals check, the `executed > 0` anti-fake
  guard):

```csharp
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.5c: the SINGLE shift/rotate + bit + BCD + Scc + data-movement TomHarte green sweep (40 dedicated
/// files) — the un-fakeable data-axis gate. EVERY M4.5c core op has a dedicated v1 vector file (verified), so
/// there is NO honesty gap (CMPM asserts through the existing M68000AluTomHarteTests, Task 15). The TIMING axis
/// is M4.5d; exception cases defer via IsExceptionCase.</summary>
public class M68000M45cTomHarteTests
{
    public static IEnumerable<object[]> M45cFiles =>
    [
        // shift/rotate (24)
        ["ASL.b.json.gz"], ["ASL.w.json.gz"], ["ASL.l.json.gz"],
        ["ASR.b.json.gz"], ["ASR.w.json.gz"], ["ASR.l.json.gz"],
        ["LSL.b.json.gz"], ["LSL.w.json.gz"], ["LSL.l.json.gz"],
        ["LSR.b.json.gz"], ["LSR.w.json.gz"], ["LSR.l.json.gz"],
        ["ROL.b.json.gz"], ["ROL.w.json.gz"], ["ROL.l.json.gz"],
        ["ROR.b.json.gz"], ["ROR.w.json.gz"], ["ROR.l.json.gz"],
        ["ROXL.b.json.gz"], ["ROXL.w.json.gz"], ["ROXL.l.json.gz"],
        ["ROXR.b.json.gz"], ["ROXR.w.json.gz"], ["ROXR.l.json.gz"],
        // bit (4)
        ["BTST.json.gz"], ["BCHG.json.gz"], ["BCLR.json.gz"], ["BSET.json.gz"],
        // BCD (3)
        ["ABCD.json.gz"], ["SBCD.json.gz"], ["NBCD.json.gz"],
        // Scc (1)
        ["Scc.json.gz"],
        // data-movement (8)
        ["SWAP.json.gz"], ["EXG.json.gz"], ["LEA.json.gz"], ["PEA.json.gz"],
        ["MOVEQ.json.gz"], ["TAS.json.gz"], ["MOVEM.w.json.gz"], ["MOVEM.l.json.gz"],
        // ["MOVEP.w.json.gz"], ["MOVEP.l.json.gz"],   // optional (DC5)
    ];

    [M68000TomHarteTheory]
    [MemberData(nameof(M45cFiles))]
    public void M45c_family_is_TomHarte_green_on_the_data_axis(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"in-scope M4.5c vector file missing: {path}");

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int executed = 0, deferred = 0;
        foreach (var c in cases)
        {
            string? rr = M68000TomHarteRunner.RunCase(c);            // data axis (timingAxis: false)
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
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

- [ ] **Step 2: Run the SINGLE gate under `-c Release`** with the vectors present:
  `pwsh tools/get-test-vectors-68000.ps1` (idempotent), then BOTH theories (the new M4.5c sweep + the existing
  ALU re-run with CMPM now asserting):
  ```bash
  dotnet test -c Release --filter "FullyQualifiedName~M68000M45cTomHarteTests|FullyQualifiedName~M68000AluTomHarteTests"
  ```
  Expected: all 40 dedicated files + all 51 ALU files green on the data axis. COARSE monitor (wake on
  `Passed!`/`Failed!`/`error`/`Exception`); kill stray `testhost.exe` first. **Capture the per-file executed
  count** (the ALU files' CMPM cases now count as `executed`, ~3,763 more total).
- [ ] **Step 3: Reconcile failures** (fix in the ONE rule per machine):
  - **Shift CCR** → `ShiftCcr.*` (Task 2): count-0 X-quirk, ASL V-changed, ROXL/ROXR through-X.
  - **Shift register/RAM** → the per-step loop (Task 3): `.b/.w/.l` sign-fill (ASR), the memory-form `.w` RMW.
  - **Bit-op Z/result** → `BitCcr.BitTest` / the size-select (Dn `.l` mod 32 vs memory `.b` mod 8, Tasks 9-10).
  - **BCD** → `BcdCcr` + `AbcdByte`/`SbcdByte` (Task 11): the nibble half-carry, N/V "pinned", sticky Z, the
    `-(An)` pairing. Expect 1-2 rounds (the subtlest after shifts).
  - **Scc** → `EvaluateCondition` (Task 13) / the byte RMW.
  - **CMPM** → the `(Ay)+,(Ax)+` pairing/order (Task 14) — now flowing through the ALU sweep.
  - **MOVEM** → the reversed `-(An)` mask, the `(An)+` write-back, the `.w` sign-extension (Task 20).
  - **EXG/LEA/PEA/SWAP/MOVEQ/TAS** → the per-body decode (direction/An-vs-Dn/sign-extend).
  - **"0 executed"** → a dispatch arm did not wire (Task 21 name mismatch).
  Each fix re-runs the FAST synthetic suite first, then the heavy gate.
- [ ] **Step 4: Full suite + byte-identity.** `dotnet test` (Debug) → 0 failures; the M4.5c + ALU theories
  SKIPPED when vectors absent; 6502/Z80 byte-identical (`RegeneratedSpecTests`). `-warnaserror` clean.
  `git diff --stat` confirms ONLY the M4.5c files changed; the seam files (`M68000FetchStream.cs`, the
  `M68000Cpu.cs` bus helpers, `M68000TomHarteRunner.cs`, `M68000Cpu.Move.cs`, `M68000Cpu.Alu.cs`) UNCHANGED.
- [ ] **Step 5: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000M45cTomHarteTests.cs
git commit -m "$(cat <<'EOF'
test(680x0): the single M4.5c TomHarte data-axis sweep (40 dedicated + 51 ALU re-run incl. CMPM; -c Release gate)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```
**Est:** ~1 (a 40-row MemberData theory; the ALU re-run reuses the existing theory).

---

## The single MERGE GATE (ADR 0007 §6 — all three required; merge blocked otherwise) — ONE PR

> The per-PR anti-drift acceptance cycle. The green TomHarte sweep is the un-fakeable behavioral oracle.

1. **Full suite green + 6502/Z80 byte-identical.** `dotnet test` → 0 failures; the 6502 `RegeneratedSpecTests`
   AND the Z80 regen guard green; every change additive (gated to `model.FieldGrammar is not null` + the
   M68000-only `M68000Cpu.Shift.cs`/`.Bit.cs`/`.Bcd.cs`/`.Scc.cs`/`.SystemMisc.cs` partials + the CMPM dataset
   row + the 680x0-only test infra). `git status` confirms no 6502/Z80 spec/generated-CPU change beyond the
   expected CMPM-driven opIndex re-pin; the SEAM files UNCHANGED.
2. **The single 91-file (93 w/ MOVEP) TomHarte sweep ACTUALLY RUN GREEN — vectors PRESENT** under `-c Release`,
   on the DATA axis (`D0–D7, A0–A6, USP, SSP, SR, RAM`, byte-exact), operword from `initial.prefetch[0]`:
   - 40 dedicated M4.5c files (24 shift + 4 bit + 3 BCD + 1 Scc + 8 data-movement) via `M68000M45cTomHarteTests`.
   - the 51 ALU files via the EXISTING `M68000AluTomHarteTests` (CMPM cases now ASSERTING, filter removed).
   **A SKIPPED TomHarte test is NOT a mergeable state.** Show the non-zero executed count PER FILE (incl. the
   ~3,763 newly-asserting CMPM cases). Exception cases DEFER via `IsExceptionCase`. The timing axis is M4.5d
   (`timingAxis:false`).
3. **ONE pre-merge code review** — pointed at the HIGHEST-bug-density area: the new shift CCR rules
   (`ShiftCcr` — last-bit-out / count-0 X-quirk / ASL-V-changed / ROXL-ROXR-through-X) AND the BCD CCR + decimal-
   adjust (`BcdCcr`/`AbcdByte`/`SbcdByte` — the nibble half-carry, the sticky-Z, the N/V-pinned pair). Secondary:
   the MOVEM mask order, the CMPM pairing, the bit-op size-select, the generator dispatch arms + the CMPM dataset
   ordering (before EOR).

**HONESTY (M4.5c):** unlike M4.5b, **there is NO vector-gap disclosure** — every M4.5c core op
(shift/rotate all sizes incl. memory-by-1; BTST/BCHG/BCLR/BSET dynamic+static; ABCD/SBCD/NBCD; Scc; SWAP/EXG/LEA/
PEA/MOVEQ/TAS/MOVEM) has a dedicated v1 vector file and IS asserted green on the data axis. CMPM (no dedicated
file) asserts through the bundled `CMP.*`/`CMPA.l` files (the M4.5b honesty gap is CLOSED in this PR). The only
NON-asserted things are (a) the timing axis (M4.5d) and (b) any exception case (deferred). State plainly in the
PR body; do NOT overclaim the timing axis.

## The SEAM INVARIANT (ADR 0007 §5.4 — binding; `git diff --stat` shows these UNCHANGED)
Do NOT touch: `src/CpuEmulator.Core/Jit/M68000FetchStream.cs`, the `M68000Cpu.cs` bus helpers
(`ReadWordBus`/`WriteWordBus`/`ReadLongBus`/`WriteLongBus`, `:79-104`), the Step+diff runner
`M68000TomHarteRunner.cs`, `M68000Cpu.Move.cs`, AND `M68000Cpu.Alu.cs` (the merged M4.5b layer — M4.5c REUSES it
as a CALLER; new CCR rules live on SIBLING static classes in the new partials, NOT reopening the non-partial
`AluCcr`). M4.5c ADDS: the five `M68000Cpu.*` partials + the CMPM dataset row + the two generator edit-points
(dispatch arms + hook declarations) + the new test files + the CMPM-filter removal in `M68000AluTomHarteTests`.
Every change gated to `model.FieldGrammar is not null`; 6502/Z80 byte-identity non-negotiable.

---

## The M4.5d deferral list (for the Coordinator's user checkpoint — DC4 to ratify)

These ops are EXPLICITLY deferred to M4.5d (the control/stack/privileged/vectoring + timing axis). **The status
doc lists some under "M4.5c"; this is the boundary the user ratifies:**

- **Stack-as-control:** `LINK`, `UNLK`.
- **Control flow:** `JMP`, `JSR`, `RTS`, `RTR`, `RTE` (RTE privileged), `Bcc`, `BSR`, `DBcc`.
- **Vector/privileged:** `TRAP`, `TRAPV`, `CHK`, `ILLEGAL`, `RESET`, `STOP`.
- **Privileged system-byte forms:** `ANDI-to-CCR`, `ANDI-to-SR`, `ORI-to-CCR`, `ORI-to-SR`, `EORI-to-CCR`,
  `EORI-to-SR` (the `*_CCR`/`*_SR` dataset rows).
- **No-op:** `NOP` (grouped with the control tail; MOVABLE to M4.5c if the user wants a freebie).
- **Cross-cutting (always M4.5d):** the TIMING axis (`final.pc`/`final.prefetch`/per-transaction trace/cycle) for
  ALL families; every exception/vector/privilege CASE (detect-and-defer via `IsExceptionCase`); the DIVU/DIVS ÷0
  vector-5 (M4.5b deferred); the IPL-level interrupt line; the prefetch-queue mechanism.
- **MOVEP:** if NOT included in M4.5c (DC5 optional), it defers to the M4.5d tail.

---

## Plan self-review (completed at write time)

- **Single-PR consolidation (DC2 resolved):** ALL families tasked at uniform fidelity (Tasks 1-22) — NO skeletons
  remain; the former c2/c3 "approved skeletons" are now full literal-code TDD tasks (bit ops Tasks 9-10, BCD
  Task 11, Scc Task 13, CMPM Tasks 14-15, data-movement Tasks 16-20). ONE merge gate, ONE 91-file sweep, ONE
  review. ✓
- **DC4 boundary stated PROMINENTLY** (DC4 + the dedicated "M4.5d deferral list" section): data-movement
  (SWAP/EXG/LEA/PEA/MOVEQ/TAS/MOVEM) IN; stack/control/privileged/vectoring/NOP DEFERRED, with the explicit list
  for ratification. ✓
- **Verdict-b kept:** the additive `ShiftRotateExecute` driver (Task 3) + the richer result carrier (last-bit-out
  + msb-changed) + new `ShiftCcr`/`BitCcr`/`BcdCcr` rules + the ADR 0007 §7.1 addendum (Task 1). Option (C)
  stands; no ADR reversal. ✓
- **CMPM folded in** (Tasks 14-15): dataset row before EOR (the order hazard surfaced, R4 #2; EOR verified line
  87) + the body + the filter removal + the ALU re-run in the single sweep — the M4.5b honesty gap CLOSED. ✓
- **Seam invariant:** the five new `M68000Cpu.*` partials + the CMPM dataset row + the two generator edits + the
  test files; the seam files (fetch/bus/runner/Move/**Alu**) UNCHANGED; new CCR rules on SIBLING static classes
  (the non-partial `AluCcr` is not reopened). Gated to `model.FieldGrammar is not null`. ✓
- **HONESTY block:** NO vector gap for the M4.5c core (every op vector-gated); CMPM asserts via bundling; timing
  axis + exception cases deferred. ✓
- **Placeholder scan:** every task has literal code. Bounded open choices: the `*_STATIC` leading-word decode
  (Task 10 Step 0 decides empirically, the M4.5b Task-6 precedent); MOVEP optional (DC5, commented arm). The
  subtlest reconciles flagged: ROXL/ROXR count-0 (Task 3), the BCD nibble half-carry + N/V-pinned (Task 11), the
  MOVEM reversed mask (Task 20), the CMPM pairing (Task 14). No "TBD"/"similar to Task N". ✓
- **Type/name consistency:** `ShiftRotateExecute`/`ShiftKind`/`ShiftCcr`; `BitOpExecute`/`BitKind`/`BitCcr`;
  `AbcdByte`/`SbcdByte`/`BcdXAlu`/`BcdCcr`; `EvaluateCondition`/`SccExecute`; `CmpMExecute`; `SwapExecute`/
  `MoveQExecute`/`ExgExecute`/`LeaExecute`/`PeaExecute`/`TasExecute`/`MoveMExecute`. Body names match the
  generator's `name+"Execute"` table (Task 21); dataset strings → the `op switch` arms (Task 21); reused merged
  symbols (`ResolveEaDest`/`WriteResolvedDest`/`AluDest`/`ReadSized`/`WriteSized`/`ShiftExt`/`AluCcr.Cmp`/
  `AluCcr.Logic`/`SetDataRegPartial`/`Areg`/`SetAreg`/`A7`/`ComputeEa`/`ReadByteAt`/`WriteByteAt`/`SizeMask`)
  cited from R3. ✓
- **Build-green-after-every-task:** Task 1 (doc) + Task 2 (CCR rules, additive) + Task 3 (driver, compiles
  standalone) + Task 21 (generator, HOISTED so declarations precede bodies) + Tasks 4-20 (bodies whose
  declarations exist, no-op until filled, un-skip tests) + Task 22 (the single heavy gate). The 6502/Z80
  byte-identity guard gates every task; the CMPM dataset edit's opIndex shift is absorbed by the name-driven
  dispatch + the regen guard. ✓
- **Altitude flags:** the shift CCR (Task 2) + the BCD CCR/decimal-adjust (Task 11) are the most TomHarte-
  sensitive code — centralized in ONE rule each so Task 22 reconciles them in one place. ✓

## Slice docs index
- **The governing decision + the §7.1 question this plan answers:**
  `docs/architecture/0007-68000-interpreter-op-body-structure.md` (option C; §7.1 addendum from Task 1).
- **The structural template:** `docs/superpowers/plans/2026-06-15-m4-5b-integer-alu.md` (the merged M4.5b plan).
- **The master status/resume pointer:** `docs/superpowers/plans/2026-06-15-m4-status-and-resume.md` (update
  item 3 to mark M4.5c done + point at M4.5d when this merges).
- **The decode/addressing/exception decisions:** `docs/architecture/0004-68000-decode-addressing-and-exceptions.md`.

## Closeout (filled at completion)

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | _(fill)_ |
| Final test count | _(fill)_ |
| M4.5c dedicated files TomHarte-green on the data axis (40)? | _(fill — 24 shift + 4 bit + 3 BCD + 1 Scc + 8 data-movement)_ |
| ALU re-run green WITH CMPM asserting (51 files)? | _(fill — the ~3,763 CMPM cases now executed, not skipped)_ |
| Total single-sweep executed (non-skipped) count | _(fill — per-file, the un-fakeable proof)_ |
| Descriptor-generalization verdict (DC1) | (b) — option C stands; BCD/bit fit the tuple, shifts add a count-source + last-bit-out axis via ShiftRotateExecute; ADR 0007 §7.1 addendum recorded (Task 1). |
| ShiftCcr count-0 X-quirk + ROXL/ROXR-through-X green? | _(fill — vector-confirmed)_ |
| BCD decimal-adjust + sticky-Z + N/V-pinned green? | _(fill — vector-confirmed)_ |
| MOVEM mask order ((An)+ forward / -(An) reversed) green? | _(fill — vector-confirmed)_ |
| Seam invariant held (fetch/bus/runner/Move/Alu unchanged)? | _(fill — git diff --stat)_ |
| 6502/Z80 un-regressed? | _(fill — RegeneratedSpecTests byte-identical)_ |
| `-warnaserror` | _(fill — clean)_ |
| Honesty | NO vector gap (every M4.5c core op vector-gated; CMPM asserts via bundling); timing axis + exception cases deferred to M4.5d. |
| Still deferred (M4.5d, DC4 ratified) | LINK/UNLK, JMP/JSR/RTS/RTR/RTE, Bcc/BSR/DBcc, TRAP/TRAPV/CHK/ILLEGAL/RESET/STOP, ANDI/ORI/EORI-to-CCR/SR, NOP; the timing axis; all exception/vector cases; DIVU/DIVS ÷0; IPL line; prefetch queue. [MOVEP if not included.] |
| Recommended next chunk | M4.5d — exceptions/branches/IPL/prefetch + the control/stack/privileged tail + the timing axis |
