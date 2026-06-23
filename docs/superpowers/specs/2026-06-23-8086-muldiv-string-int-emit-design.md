# 8086 MUL/DIV + string/REP + INT/INTO/IRET JIT emit — design spec

> **Status:** drafted (Claude Planner, 2026-06-23). The finale of the autonomous roadmap clear-out:
> **ROADMAP deferred item #4**. Grounded against fresh `main` @ `4b46da2` (FF-1/FF-2 shipped — the
> 8086 far-flow arc is complete; the linear `(CS<<4)+IP` block key is live). The autonomous run ends
> after these rows merge.
>
> **Relates to:** ADR 0011 (the M6 partial-emit philosophy — the interpreter is the oracle + the
> byte-exact fallback; emit is a pure perf dial, §2/§5/OQ5), ADR 0005 (the 8086 interrupt frame +
> the FLAGS reserved-bit forcing), ADR 0019 (the far-flow arc this builds directly on).

## 1. Context — what #4 is, and why it was fallback

The 8086 JIT today emits MOV (PR-B), the integer ALU + FLAGS (PR-C), near branch/call/return (PR-D),
and far flow (FF-2). Three op groups stay **interpreter-fallback by design** (ADR 0011 §2 names each):

1. **MUL/IMUL/DIV/IDIV** (`F6` /4../7, `F7` /4../7) — the microcoded multiply/divide. The ALU arm
   already emits `F6`/`F7` /0../3 (TEST/NOT/NEG) and *excludes* /4../7 automatically (their MUL/IMUL/
   DIV/IDIV mnemonics fail the ALU mnemonic whitelist — `IsEmittableX86Family`, CpuEmitter.cs:5096).
2. **REP/REPE/REPNE MOVS/STOS/LODS/CMPS/SCAS** (`A4`-`A7`, `AA`-`AF`) — the CX-counted, DF-directed
   string loops (the Z80 LDIR/CPIR precedent). A REP op is a single `Step` that loops `CX` times.
3. **INT/INT3/INTO/IRET** (`CD`/`CC`/`CE`/`CF`) — the soft-interrupt vectoring (push FLAGS:CS:IP,
   clear IF/TF, vector through the IVT at segment 0) + IRET (pop IP:CS:FLAGS, force the reserved bits).

The owner chose to **ship #4** (and park #2/#5/L). These were fallback for ROI reasons (rare,
high-emit-cost), not correctness reasons — the fallback valve makes coverage a pure performance dial.

The **interpreter oracles are small, self-contained, and already proven** against the 8088 TomHarte
corpus (M5.5b/d): `AluMul`/`AluDiv` (M8086Cpu.Alu.cs:369/412), `StringExecute` (M8086Cpu.String.cs:38),
`InterruptExecute`/`RaiseInterrupt` (M8086Cpu.Interrupt.cs:39/54). Each emit arm transcribes its oracle
one-for-one to IL — the same discipline PR-B/C/D/FF-2 already used.

## 2. The honored constraint — the partial-emit philosophy (ADR 0011 §2/§5/OQ5)

**The interpreter is the oracle and the byte-exact fallback; emit is a pure perf dial, never a
correctness path.** Three load-bearing consequences for #4:

- **Every emitted op is byte-identical to the interpreter** or it does not ship. The merge precondition
  per row is the 8088 TomHarte corpus **through the JIT** byte-identical to the interpreter (registers +
  FLAGS-mask-aware + changed RAM cells — the existing `M8088JitTom` sweep, already green via fallback).
- **A genuinely too-microcoded corner stays fallback by design** — noted, not forced. For #4 the
  candidate is the **AAM/AAD-family** (D4/D5 — NOT in #4's scope; they stay fallback) and the
  **divide-error UNDEFINED-flag fallout** (the silicon's undefined arithmetic flags from an aborted
  division — the documented DD6 resistant class the corpus classifier already defers). Emit must
  reproduce the interpreter's *modeled* behavior exactly (including its disclosed non-modeling of the
  undefined fallout), so the existing divide-error / IDIV-sign-quirk classifiers keep working unchanged.
- **The un-fakeable gate per row** = parity (above) **PLUS** a discriminator that proves the op now
  *emits* rather than silently still-falls-back. Without the discriminator the parity sweep false-passes
  (it already passes via fallback). Two complementary forms, both already in-tree:
  - the per-arm **`*EmitSelections` counter** (BlockCompiler.cs:60-83 — `M8086MovEmitSelections`,
    `M8086AluEmitSelections`, … — bumped only when the arm actually dispatches), asserted `> 0`;
  - **`FallbackEmitCount`** (BlockCompiler.cs:31) — asserted `== 0` for a block of the targeted
    opcodes (the op emits no `inner.Step` callout). The far-flow row proved both red→green; #4 mirrors it.

## 3. The decomposition — three bite-sized TDD rows, owner-priority order

ROADMAP #4 lists the three groups in priority order. They are **independent** (no inter-row dependency
— each un-forces a disjoint opcode set in `IsEmittableX86Family` and adds a disjoint emit arm), so they
ship in priority order but could interleave. Each is one PR. The shared decode preamble (scan past
prefixes via `M8086CodePhys`, read ModR/M/disp/imm at emit time), the EA resolver, the FLAGS helpers,
the survivor-local discipline, the `EmitM8086PushWord`/`EmitM8086PopWord` stack helpers, and the
PC-advance accounting (`length - 1`) are **all already shipped** and reused verbatim.

### Row MD — 8086 MUL/DIV emit (priority 1)

**Scope:** `F6` /4 (MUL r/m8), /5 (IMUL r/m8), /6 (DIV r/m8), /7 (IDIV r/m8); `F7` /4../7 (the r/m16
forms). Eight emit branches transcribing `AluMul`/`AluDiv` (Alu.cs:369/412) one-for-one.

**The emit arm** extends `EmitM8086Alu`'s F6/F7 group switch (BlockCompiler.M8086.cs:658-666, where
/0../3 already emit and /4../7 currently fall through the gate). New helpers `EmitM8086Mul` /
`EmitM8086Div`:

- **MUL/IMUL** (no control-flow, no fault): read the r/m operand into a local; compute the product
  (byte: `AX = AL * src`, with `(sbyte)` casts for IMUL; word: `DX:AX = AX * src`, `(short)` casts for
  IMUL); write `AX` (and `DX` for word); set **CF=OF** iff the upper half is significant (MUL: high
  half != 0; IMUL: high half not the sign-extension of the low half — Alu.cs:379-381/393-395).
  SF/ZF/PF/AF are left as natural fallout (the 8086 leaves them undefined; the F6/F7 /4 /5 flags-mask
  excludes them, so the corpus FLAGS compare ignores them — the emit just must match the interpreter's
  *unchanged* SF/ZF/PF/AF, which it does by not touching them).
- **DIV/IDIV** (fault-capable — the wrinkle): compute the quotient/remainder; on **divide-by-zero OR
  quotient-overflow**, the interpreter calls `RaiseInterrupt(0)` (the divide-error INT0 — push FLAGS:CS:
  IP, clear IF/TF, vector through `[0:0]`) and returns *without* writing the result registers. The emit
  arm must reproduce this **exactly**, including the 8086 IDIV symmetric-range quirk (byte: reject
  `|quot| > 127`; word: reject `|quot| > 32767` — Alu.cs:439-440/463-465). The divide-error path
  **changes CS:IP** (it vectors), so a DIV/IDIV op **ends the block** (like the INT ops — see Decision
  MD-1 below). The non-faulting path writes the result and falls through to the chain edge / next op.

**DECISION MD-1 — DIV/IDIV ends the block; MUL/IMUL does not.** `AluMul` never changes CS:IP →
MUL/IMUL is straight-line, block-continuing (like any ALU op). `AluDiv` *conditionally* vectors (CS:IP
change on fault) → its emitted block must terminate so the next dispatch keys/decodes under the
possibly-new (CS,IP) (the FF-1 linear-key payoff — same reasoning as the far ops). Concretely: the DIV/
IDIV arm emits the compute + the fault-or-write branch, then **self-terminates via EmitChainOrExit/
EmitNormalExit + ret** (the FF-2 / near-flow arm pattern), and `IsEmittableX86Family` **re-forces
endsBlock=true** for the DIV/IDIV rows (`KeyedDescriptorLiteral`, mirroring the far/near re-force at
CpuEmitter.cs:4909). MUL/IMUL stay endsBlock=false. *(This split is the load-bearing correctness
decision of row MD — a DIV/IDIV that didn't end the block would let a stale-keyed successor run after a
fault-vector.)*

**DECISION MD-2 — the divide-error reuses an emitted IVT-push helper shared with row II.** The
`RaiseInterrupt(vector)` push sequence (PushWord FLAGS, clear IF/TF, PushWord CS, PushWord IP, load
CS:IP from `[0:vector*4]`) is **identical** machinery to the INT ops (row II). Row MD emits it as
`EmitM8086RaiseInterrupt(ctx, vectorConst)` (vector 0 a compile-time constant). To avoid an inter-row
dependency, **row MD owns the helper's introduction** (it is the higher-priority row); row II reuses it
for the general INT path. If the owner prefers II-before-MD ordering, the helper moves to whichever
lands first — but the priority order (MD first) makes MD the natural owner. *(Flagged as the one
shared-surface point between the otherwise-disjoint rows; the plans pin the helper in row MD's plan and
have row II's plan reference it.)*

**The gate (un-fakeable):**
- **Parity:** the `F6`/`F7` files in the `M8088JitTom` sweep are byte-identical through the JIT to the
  interpreter — *now via real emitted IL for /4../7* (was fallback). The existing divide-error (DD6) +
  IDIV-sign-quirk classifiers (M8088JitTomHarteTests.cs:102-113) still classify correctly **because emit
  is byte-identical to the interpreter** (the classifier re-runs the interpreter to confirm the quirk
  shape; emit == interpreter, so the classification holds). The stale "NOT emitted — they still fall
  back" comment in that file (lines 92-97) is corrected to "now emit" as part of row MD.
- **Discriminator:** a new `M8086MulDivEmitSelections` counter, asserted `> 0` over an `F6`/`F7` /4../7
  block; `FallbackEmitCount == 0` for a MUL block and a (non-faulting) DIV block. A focused
  `M8086MulDivEmitTests` file: MUL/IMUL byte+word (CF/OF significance pockets), DIV/IDIV byte+word
  (the valid quotient + the IDIV symmetric-range boundary), and the **divide-by-zero → INT0 frame**
  (the pushed FLAGS:CS:IP + the vectored CS:IP + SP-=6 + IF/TF cleared, byte-identical to a fresh
  interpreter from the same seed — the far-flow test's JIT-vs-interpreter shape).

### Row STR — 8086 string/REP emit (priority 2)

**Scope:** MOVS (`A4`/`A5`), CMPS (`A6`/`A7`), STOS (`AA`/`AB`), LODS (`AC`/`AD`), SCAS (`AE`/`AF`),
each byte+word, with and without a REP prefix (`F3` REP/REPE, `F2` REPNE). Transcribes `StringExecute`
+ `StringStep` (String.cs:38/29) one-for-one.

**The emit arm** `EmitM8086String`, a new dispatch case in `EmitInstruction` (a new
`IsM8086StringOpcode(d)` predicate routing `A4`-`A7`/`AA`-`AF` to it, mirroring the MOV/ALU/Flow
routing at BlockCompiler.cs:670-705):

- **The single-iteration body** is a per-opcode IL block: resolve the source EA as `DS:SI`
  (DS override-replaced via `r.X86.SegOverride` — the override is a compile-time constant from decode)
  and the destination EA as `ES:DI` (**ES non-overridable** — a prefix does not redirect the string
  destination; String.cs:42-44/64-65). MOVS copies src→dst; CMPS does `SubFlags(s, d, …)` (flags only,
  reusing `EmitM8086SubFlags`); SCAS does `SubFlags(AL/AX, d, …)`; LODS loads `AL/AX ← src`; STOS stores
  `AL/AX → dst`. Each iteration then **steps SI/DI by ±1/±2 directed by DF** (`EmitM8086StringStep` —
  read FLAGS&DF, pick the delta, add to SI and/or DI with 16-bit wrap; the `stepSi`/`stepDi` selection
  per op is a compile-time constant). The word forms reuse the offset-wrap word read/write
  (`ReadEaWordWrapped`/`WriteEaWordWrapped` — already emitted as `EmitM8086LoadWordEa`/`StoreWordEa`'s
  survivor-pair shape, but here over the *runtime* SI/DI offset, not a compile-time disp).
- **The REP loop** (`r.X86.RepPrefix` a compile-time constant): when present, emit a **runtime IL loop**
  — `while (CX != 0) { CX--; body; if (isCompare && zf != repWhileZfSet) break; }` (String.cs:128-143).
  This is the one genuinely new IL shape #4 adds (a back-edge within a single op's emission — the Z80
  block-op precedent, which stayed fallback there, but the 8086 string loop is the named #4 deliverable).
  `repWhileZfSet = (rep == 0xF3)` is a compile-time constant; the compare-op early-exit reads the ZF the
  iteration's `SubFlags` set. With `CX == 0` going in, zero iterations (the loop-condition-first shape).

**DECISION STR-1 — the string ops do NOT end the block (they advance only IP, by `length`).** Unlike
DIV/INT, no string op changes CS, and the REP loop terminates within the single op's emission — so a
string op is straight-line, block-continuing (the runner Steps once per case; the emit loops internally
then advances IP and chains). No endsBlock re-force needed. *(Confirmed against the interpreter: a REP
string op is one `Step`; the JIT emits the whole loop inline and continues the block.)*

**DECISION STR-2 — the REP word forms reuse the survivor-pair locals; the loop body re-resolves the EA
each iteration.** Because SI/DI mutate per iteration, the EA must be re-formed from the *current* SI/DI
inside the loop (not hoisted) — the survivor-local discipline (M8086SegLocal/OffsetLocal/AddrLocal/
DataLocal) is reused, and the loop body recomputes the physical from the live SI/DI each pass (the
8086 string EA has the per-iteration auto-step, so re-resolving is *required*, not just safe).

**The gate (un-fakeable):**
- **Parity:** the `A4`-`A7`/`AA`-`AF` files in `M8088JitTom` byte-identical through the JIT — now via
  emitted IL (was fallback). Includes the REP-prefixed cases (the corpus carries them) — the CX-loop +
  DF-direction + the REPE/REPNE ZF early-exit all proven against the oracle.
- **Discriminator:** a new `M8086StringEmitSelections` counter `> 0`; `FallbackEmitCount == 0` for a
  REP MOVS block and a REPE CMPS block. A focused `M8086StringEmitTests`: MOVSB/MOVSW (DF=0 and DF=1
  directions), STOSB/LODSB, CMPSB with REPE early-exit (a 3-byte run where byte 2 mismatches → CX stops
  early, SI/DI/CX at the exact interpreter values), SCASW with REPNE, and the **CX=0 zero-iteration**
  case (no register/memory change) — each JIT-vs-fresh-interpreter from the same seed.

### Row II — 8086 INT/INTO/IRET emit (priority 3)

**Scope:** `CD` (INT imm8), `CC` (INT3 → vector 3), `CE` (INTO → vector 4 iff OF set), `CF` (IRET).
Transcribes `InterruptExecute` + `RaiseInterrupt` (Interrupt.cs:54/39) one-for-one. **BOUND (`62`)
stays fallback** (out of #4 scope; ADR 0019 Decision 3 named INT/INTO/IRET/BOUND together — #4 emits the
first three, BOUND stays fallback as a deliberate corner — see §4).

**The emit arm** `EmitM8086Interrupt`, a new dispatch case (an `IsM8086InterruptOpcode(d)` predicate
routing `CD`/`CC`/`CE`/`CF`):

- **INT n / INT3** (`CD`/`CC`): emit `EmitM8086RaiseInterrupt(ctx, vector)` — vector is the imm8
  (compile-time constant from `r.X86.Imm`) for `CD`, the constant 3 for `CC`. The helper (introduced in
  row MD, reused here — DECISION MD-2) pushes FLAGS:CS:IP, clears IF/TF, loads CS:IP from `[0:vector*4]`.
  The pushed IP is the **return IP** — `EmitInstruction` + the arm advance IP past the 2-byte (CD) / 1-
  byte (CC) instruction *before* the push (mirroring `Step`'s `IP += length` then dispatch — Interrupt.cs
  comment lines 16/58-59). So the arm advances IP by `length` (not the usual write-result-then-advance),
  *then* raises.
- **INTO** (`CE`): emit `if ((FLAGS & OF) != 0) RaiseInterrupt(4); else no-op` — a runtime branch on OF;
  IP already advanced past the 1-byte op either way.
- **IRET** (`CF`): pop IP, then CS, then FLAGS — reusing `EmitM8086PopWord` (already shipped). FLAGS
  applies the 8086 reserved-bit forcing: `(popped & FlagsDefinedMask) | FlagsForcedBits` (the same POPF
  forcing — Interrupt.cs:84; the masks are compile-time constants the arm reads from the spec layout).

**DECISION II-1 — every INT/INTO/IRET op ends the block.** All four change CS:IP (INTO conditionally;
IRET always; INT/INT3 always) → each must self-terminate so the next dispatch keys under the new (CS,IP).
`EmitM8086RaiseInterrupt` + the IRET pop are followed by `EmitChainOrExit`/`EmitNormalExit` + ret, and
`IsEmittableX86Family` re-forces endsBlock=true for `CD`/`CC`/`CE`/`CF` (mirroring the far/near re-force).
**INTO is endsBlock=true even though it's conditionally a no-op** — the not-taken path falls through to
the chain edge under the *unchanged* (CS,IP), which is correct (the block ends, the next instruction
dispatches normally). *(Ending unconditionally is the simplest correct choice; the not-taken cost is one
chain-edge round-trip, negligible for the rare INTO.)*

**The gate (un-fakeable):**
- **Parity:** the `CD`/`CC`/`CE`/`CF` files in `M8088JitTom` byte-identical through the JIT — now via
  emitted IL. The pushed frame (FLAGS:CS:IP in stack RAM), the vectored CS:IP, the IF/TF clear, and
  IRET's reserved-bit forcing (the corpus's `popped 0x28CF → FLAGS 0xF8C7` case — Interrupt.cs:78) all
  proven against the oracle.
- **Discriminator:** a new `M8086InterruptEmitSelections` counter `> 0`; `FallbackEmitCount == 0` for an
  INT block and an IRET block. A focused `M8086InterruptEmitTests`: INT n (the full frame + vector),
  INT3 (vector 3), INTO with OF=1 (vectors) and OF=0 (no-op, IP advanced), IRET (the reserved-bit
  forcing) — each JIT-vs-fresh-interpreter from the same seed, plus a **MUL/DIV-shares-the-helper**
  cross-check (a DIV-by-zero block and an INT block produce byte-identical frames, confirming the shared
  `EmitM8086RaiseInterrupt`).

## 4. The corners that STAY fallback by design (noted, not forced — ADR 0011 §2/OQ5)

Per the partial-emit philosophy, these are disclosed as deliberate fallbacks, NOT failures to emit:

- **BOUND (`62`/`63`)** — out of #4 scope (the ROADMAP names #4 as "MUL/DIV + string/REP + INT/IRET";
  BOUND is the range-check exception op ADR 0019 grouped with INT but is rarer still and fault-capable).
  Stays fallback; the gate does NOT admit it. *(If a future profile shows BOUND hot — it won't for normal
  software — revisit.)*
- **AAM/AAD (`D4`/`D5`)** — the BCD-adjust ops that *also* raise the divide-error on base 0. NOT in #4
  scope (they are not MUL/DIV/string/INT). Stay fallback. The corpus's AAM divide-error deferral
  (M8088JitTomHarteTests.cs:78/102) continues to work via fallback.
- **The divide-error UNDEFINED arithmetic-flag fallout (DD6)** — the silicon leaves SF/ZF/PF/AF/CF
  undefined after an aborted division; the interpreter does NOT model the exact undefined values (it
  pushes the flags "as they stand"). Emit reproduces the interpreter's modeled behavior exactly,
  including this non-modeling — so the existing DD6 classifier (which counts these as deferred after
  confirming the discrepancy is *precisely* the undefined fallout) keeps working unchanged. This is a
  **fallback-equivalent emit corner**: the op emits, but the disclosed-resistant sub-cases are deferred
  by the same classifier that already defers them for the interpreter. *(Emit does not make these cases
  pass; it makes them fail-identically-to-the-interpreter, which the classifier then defers — the honest
  outcome.)*

## 5. AOT-clean Core (honored)

No change to `CpuEmulator.Core`'s AOT cleanliness: all new IL emission lives in `CpuEmulator.Jit`
(`BlockCompiler.M8086.cs`); the generator change is the `IsEmittableX86Family` un-force (a
`CpuEmulator.Generators` edit, the same gate-flip class as PR-C/FF-2); the interpreter oracles are
unchanged (already shipped). The `JittedCpu` reflection seam is unchanged. No new `Reflection.Emit`
surface in Core.

## 6. Self-review

- **Placeholder scan:** none — every emit branch references a concrete oracle line (Alu.cs:369/412,
  String.cs:38/29, Interrupt.cs:39/54) and a concrete shipped helper (EA resolver, FLAGS helpers, stack
  push/pop, the decode preamble).
- **Internal consistency:** the three rows un-force disjoint opcode sets; the one shared surface (the
  IVT-push helper, DECISION MD-2) is pinned to row MD with row II referencing it. The endsBlock re-force
  decisions (MD-1 DIV ends; STR-1 string does not; II-1 INT ends) are each justified against whether the
  op changes CS:IP.
- **Scope check:** matches ROADMAP #4 exactly (MUL/DIV + string/REP + INT/IRET); BOUND/AAM/AAD/DD6
  explicitly excluded as named fallback corners (§4).
- **Ambiguity check:** the one genuinely-new IL shape (the REP runtime loop, STR) and the one
  shared-helper coupling (MD-2) are both called out; the rest is one-for-one transcription of proven
  oracles using shipped helpers.

## 7. The plans

- Row MD: `docs/superpowers/plans/2026-06-23-8086-muldiv-emit.md`
- Row STR: `docs/superpowers/plans/2026-06-23-8086-string-rep-emit.md`
- Row II: `docs/superpowers/plans/2026-06-23-8086-int-iret-emit.md`
