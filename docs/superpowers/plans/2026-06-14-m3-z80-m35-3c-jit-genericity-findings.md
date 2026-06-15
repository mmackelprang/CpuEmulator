# M3.5-3c — The JIT genericity findings (the D9 boundary record)

> **Status:** ✅ COMPLETE — this is the retrospective that closes M3.5 at its D9 boundary. It is a
> documentation/synthesis artifact (no code). It records what M3.5-3a achieved (the generic JIT compiler +
> the Z80 tier-parity proof), fills in the ADR 0001 Decision-7 J1–J10 table with the *realized* answers, and
> hands the post-8086 cross-architecture JIT-optimization phase a concrete starting spec (the
> hot-op-vs-fallback emit list). It does NOT plan implementation work — 5-3b (emitting real IL for the hot
> Z80 ops) is explicitly DEFERRED to that optimization phase (see §5).
>
> **Sources (every claim is backed by a landed artifact):**
> - The M3.5-3a close-state: `docs/superpowers/plans/2026-06-14-m3-z80-m35-3-z80-jit.md` §"Close-state record
>   (5-3a)" (PR #31, merge `6a8139c`).
> - The merged JIT code: `src/CpuEmulator.Jit/` (`BlockCompiler.cs`, `JittedCpu.cs`, `CompiledBlock.cs`,
>   `BlockCache.cs`) + `src/CpuEmulator.Core/Jit/IJitTarget.cs` + `src/CpuEmulator.Generators/CpuEmitter.cs`.
> - The decision record this resolves: `docs/architecture/0001-z80-second-architecture.md` (Decision 7;
>   the J1–J10 risk table; risk-Q3 "where is the M3 line?"; the 2026-06-13 three-architecture checkpoint).

---

## 0. The one-paragraph result

The whole reason the Z80 was M3 (ADR 0001 §1.1): *"Today's JIT was written, tested, and tuned against the
6502 only. Before we optimize it we must discover and excise every place it secretly assumes '6502' …
The Z80 is the chisel we use to find those assumptions."* M3.5-3a swung that chisel. The block compiler is
now **generic over the CPU type** — `BlockCompiler<TCpu>` / `JittedCpu<TCpu>` / `CompiledBlock<TCpu>` /
`BlockCache<TCpu>` / `BlockDelegate<TCpu>` — and the `CpuEmulator.Jit` assembly no longer references any
concrete CPU assembly (it resolves all CPU-specific reflection + decode through a generated per-CPU
`IJitTarget` seam). The complete Z80 ISA (all 7 planes, 1604 opcodes) runs through `JittedCpu<Z80Cpu>` as
**all interpreter fallbacks**, with byte-identical tier parity proven by the Z80 JIT TomHarte sweep and
ZEXDOC/ZEXALL-through-the-JIT; the 6502 JIT is un-regressed. **The compiler is now generic** — which is the
M3 deliverable. **The compiler is not yet faster for the Z80** — emitting real IL for the hot Z80 ops (5-3b)
is the optimization, and it is deferred to the post-8086 cross-architecture phase so the hot-op emitter is
built once for all three ISAs rather than Z80-specifically now.

This is the boundary the ADR's "M3 line" (risk-Q3) lands on: **make the compiler generic (done); do not
optimize it now (deferred).**

---

## 1. What made the compiler generic (J1 / J2 / J3) — the seams that changed

ADR 0001 Decision 7's framing was exact: *"Today the JIT is, by construction (J1/J2/J3), a 6502 JIT wearing
a generic descriptor table. The descriptor TABLE is CPU-agnostic; the COMPILER that consumes it is not."*
The descriptor table was already data (the Z80 emitted well-formed keyed descriptors before 5-3a); 5-3a
generalized the consumer. Three seams changed, and three layers were confirmed already-generic.

### J1 — RESOLVED: the `IJitTarget` seam + `BlockCompiler<TCpu>`

The largest single JIT change. The 6502 concrete type was woven through six files (the `Mos6502Cpu _cpu`
field, the baked `typeof(Mos6502Cpu).GetField(...)` reflection handles, the `BlockDelegate(Mos6502Cpu …)`
signature, the `DynamicMethod` first-parameter type, the `JittedCpu(Mos6502Cpu inner)` ctor). All of it
became `TCpu`, threaded through a generic `BlockCompiler<TCpu> where TCpu : class`.

The CPU-specific reflection that genuinely *was* 6502-baked — the status/PC/accumulator `FieldInfo`s and the
`Step`/`AdvanceCycles`/`CycleCount`/`InterruptPending` method handles — moved behind a single per-CPU seam:
the **`IJitTarget` interface** (`src/CpuEmulator.Core/Jit/IJitTarget.cs`), implemented by a generated
`JitTarget` class per CPU (`CpuEmitter.EmitJitTarget`). The seam lives in `Core` and is AOT-clean: it carries
reflection handles + a decode delegate, never `Reflection.Emit`.

The architecture decision the ADR left open (Decision 7 J1: *"generic over `ICpuCore` OR a generated per-CPU
state type"*) was resolved in favor of **the `IJitTarget` static seam + a generic `BlockCompiler<TCpu>`,
NOT an `ICpuCore`-virtual rewrite.** This is load-bearing: the JIT's speed premise is *direct field access*
emitted against the concrete CPU type (a baked `FieldInfo` → `Ldfld`, not a virtual property call). An
`ICpuCore`-virtual rewrite would have replaced every direct field touch with an interface dispatch and
thrown away the entire reason to JIT. The `IJitTarget` seam keeps the baked-`FieldInfo` direct emit while
making the *CPU type itself* data — the minimal change that preserves the emit model.

- **Files:** `src/CpuEmulator.Core/Jit/IJitTarget.cs` (the contract); `src/CpuEmulator.Jit/BlockCompiler.cs`
  (the generic class + ctor resolving `_fa`/`_fp`/`_fpc`/`_mAdvance`/`_mStep`/`_mCycleCount`/
  `_mInterruptPending` from the injected target, lines 18–108); `src/CpuEmulator.Jit/JittedCpu.cs`
  (`JittedCpu<TCpu>`, the 8-arg ctor taking `IJitTarget target`, lines 13–80);
  `src/CpuEmulator.Jit/CompiledBlock.cs` + `BlockCache.cs` (`CompiledBlock<TCpu>` / `BlockCache<TCpu>` /
  `BlockDelegate<TCpu>`); `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitJitTarget`).

- **The structural genericity proof:** `src/CpuEmulator.Jit/CpuEmulator.Jit.csproj` dropped its
  `CpuEmulator.Cpus.Mos6502` `ProjectReference` — the JIT assembly now references only `CpuEmulator.Core`.
  The caller injects `Mos6502Cpu.JitTarget` / `Z80Cpu.JitTarget`. This is the cleanest possible evidence
  that the compiler is no longer 6502-shaped: it cannot name a concrete CPU because it cannot see one.

### J2 — RESOLVED (for the field-backed registers): the data-driven register file

The 6502's six baked `FieldInfo`s (`FA/FX/FY/FS/FP/FPC`) and the `A=0,X=1,Y=2,S=3` index switch were the
second-loudest 6502 assumption (ADR Decision 3 / J2). 5-3a completed the move to data: the operand registers
(the ones a descriptor's `RegA`/`RegB` name) resolve through a per-compile `_regFields` name→`FieldInfo` map
built from `IJitTarget.RegisterNames`; the status/PC/accumulator handles resolve by name from the target.
No baked enum, no index switch — the register file is what the spec declares.

**The recorded J2 finding (a genuine per-CPU divergence the seam absorbs):** the 6502's register file is
*all fields*. The Z80's is *fields + composed pair-view properties*. The Z80's 16-bit pair-views
(`AF/BC/DE/HL/IX/IY` + the alternate set) are, on the generated `Z80Cpu`, **properties** composed over the
8-bit half *fields* — so `GetField("BC")` returns null. The `_regFields` builder therefore *tries* `GetField`
and **skips** the field-less names (`BlockCompiler.cs:104-107`), covering the field-backed registers (the
8-bit halves + I/R + WZ/SP/PC) and leaving the pair-views to a dedicated 16-bit register helper. In 5-3a no
emitted op references a pair (everything falls back), so the skip is harmless and correct; **completing the
16-bit register emit path is 5-3b's J2 obligation.** This divergence — "the data-driven map covers the
directly-emittable registers; the composed views are a per-CPU 16-bit concern" — is exactly the kind of
enumerated finding the framework was supposed to surface, and the seam absorbs it without a `Core` change.

### J3 — RESOLVED: keyed-descriptor consumption + the computed-length decode walk

`Discover` (`BlockCompiler.cs:117-130`) now runs the per-CPU `IJitTarget.Decode` / `DescriptorFor` instead
of the literal `Mos6502Cpu.JitDescriptors[opcode]` 256-slot array index. For the 6502 the key *is* the
opcode and `DescriptorFor` is the dense [256] lookup (byte-identical behavior); for the Z80 it is the
generated `JitDescriptorsByKey` dictionary keyed on the opaque multi-byte `OperationKey`. Critically,
`Discover` advances PC by the walk's **computed length** (`r.Length`), not a static field — so the compound
DDCB/FDCB 4-byte form (`DD CB d op`, the displacement-before-opcode shape no single-byte decoder can
express, ADR Decision 1) flows through the block walk unchanged. **Proven by the DDCB/FDCB planes passing
the JIT TomHarte sweep:** the discovery walk handled every key shape (base / CB / ED / DD / FD / DDCB / FDCB)
without special-casing.

### J4 / J6 / J8 — CONFIRMED GENERIC (the positive findings)

These three seams the Z80 reused **unchanged** — which is itself the evidence that they were never
6502-shaped (ADR Decision 7 J4/J6/J8; §3 "what the Z80 confirms generic").

- **J4 (fastmem):** `Fastmem` / `DirtyMap` / the page-table direct-array bus arm never named the CPU; they
  key on `IAddressSpace` and a `byte[]?[]` page table. The Z80 reuses the memory-bus fastmem verbatim. The
  Z80's separate I/O space correctly *never* enters fastmem — the Port callout routes through the dedicated
  `_ioBus` arg (the 8th `BlockDelegate` parameter, `BlockCompiler.cs:41`), which is the unconditional-callout
  path, never the fastmem branch (ADR Decision 2's load-bearing rule, satisfied).
- **J6 (interrupt seam + HALT):** the JIT's boundary-sampling of `InterruptPending` was already generic (it
  asks the CPU, not the policy). The one addition the Z80 forced: a uniform **`Halted`** member, added to
  `IMonitorSupport` and generated for every CPU (a `partial bool Halted` for a HALT-op CPU; a constant-false
  `Halted => false` for a no-HALT CPU). The 6502's former hand-written `Halted => false` hook is now
  generated — so the HALT fast path that was dead for the 6502 went **live** for the Z80 without a
  hand-written branch. The dispatcher reads the live PC via `_inner.GetRegister(_pcName)` (interface-only,
  no concrete field) — `JittedCpu.cs:79`.
- **J8 (SMC / dirty-page):** the 256-byte-page dirty bitmap + intra-block SMC guard are page-granular and
  CPU-agnostic; the page size is a `Core` constant, not a 6502 fact. The Z80 reuses them. The only nuance
  the ADR flagged — the Z80's larger instructions (up to 4 bytes for `DD CB d op`) span pages more often,
  exercising `PagesSpanned` harder — is exercised by the DDCB/FDCB sweep and held.

---

## 2. The tier-parity result (J2 — the proof) + what "all-fallback" means

**How parity is proven.** Tier parity means: the JIT's final state == the interpreter's final state == the
vector's expected state, for the same input. M3.5-3a proves it two ways:

1. **The Z80 JIT TomHarte sweep** (`tests/CpuEmulator.Tests/TomHarte/Z80JitTomHarteTests.cs`, via
   `Z80TomHarteRunner.RunCaseThroughJit`): all SEVEN planes (base / cb / ed / dd / fd / ddcb / fdcb), driving
   each SingleStepTests vector through `JittedCpu<Z80Cpu>.Run` and diffing the FULL Z80 state — every
   register, F's undocumented X/Y bits, WZ/MEMPTR, the Q latch, IM, IFF1/IFF2, RAM, ports, and the cycle
   COUNT. Sampled at CI scale (25/opcode = 1604 opcode-cases); `CPUEMULATOR_UAT=full` runs every case as the
   pre-merge gate. (Fastmem-on bypasses the per-T-state *bus trace* — the same scope the 6502 JIT sweep
   asserts: state + RAM + ports + cycle count, not the trace.)
2. **ZEXDOC + ZEXALL through `JittedCpu<Z80Cpu>`** (`tests/CpuEmulator.Tests/Zex/Z80ZexJitTests.cs`, reusing
   the M3.5-2 `CpmBdosHost`): the heaviest possible composition load — a multi-billion-T-state real exerciser
   program — run through the JIT, asserting the same "no ERROR + Tests complete" gate the interpreter passes.
   Env-gated (`CPUEMULATOR_ZEX=full`) with a fast wiring-smoke on every CI run.

**What "all-fallback" means.** Today every Z80 op emits a callout to `inner.Step()` rather than inlined IL.
A Z80 block is therefore *exactly* the interpreter, one op at a time (a fallback op also ends the block, so a
Z80 block is one op long). This is deliberate and is the ADR's "all-fallback first is the safety valve"
discipline made concrete: **the JIT path IS the interpreter path for the Z80 today.** That is why the parity
proof is byte-identical and was *expected* to be — the value of the all-fallback bring-up is that it proves
the **generic COMPILER** (the discovery walk, the keyed `DescriptorFor`, the per-CPU `BlockDelegate`, the
data-driven register map, the cycle/budget/dirty/chain machinery, the dispatcher) runs the complete Z80
faithfully **before any Z80 IL exists**. The genericity refactor (J1/J2/J3 — mechanical, low-semantic-risk)
was validated in isolation from the Z80 IL correctness (high-semantic-risk, cycle-exact) that 5-3b will add.

The 6502 JIT is un-regressed across all of this: `Mos6502JitTomHarte` + `KlausJit` + `JittedCpuGate` green;
the importer-tool output `Mos6502Spec.cs` is byte-identical (`RegeneratedSpec` guard green); `-warnaserror`
clean. (The source-generator `.g.cs` is not committed — it lives in `obj/generated/` — so the additive
`JitTarget` + generated `Halted` members changed no committed byte-identity baseline.)

---

## 3. The J5 finding — the cycle-model bug the all-fallback gate surfaced (a worked example)

This is the single most valuable correctness finding of M3.5-3a, and a textbook case of why the safety valve
mattered.

**The premise that turned out false.** The 5-3a plan assumed "every Z80 op is already a fallback" — that the
Z80's `InstructionClass`es were all `Z80*` classes the JIT's `ClassifyForJit` would route to fallback. The
all-fallback gate (the parity sweep) is what exposed that this was **false**.

**The bug.** The opcode importer had mapped a handful of Z80 ops to *generic* `InstructionClass`es shared
with the 6502 — Z80 `NOP` → `Register`, `LD A,n` → `Load` — rather than `Z80*` classes. `ClassifyForJit`'s
`z80` guard keys on the class being a `Z80*` class, so it **missed** these ops and emitted them with
`NeedsFallback = false`. The generic compiler would then have *emitted them via the 6502 arms* — with the
**6502 cycle model and 6502 flag convention**. Z80 `NOP` is 4 T-states; the 6502's `NOP` is 2 cycles. The
emitted block would have charged the wrong cycle count and broken tier parity on the cycle-count diff — and
this is precisely the J5 divergence the ADR predicted (Decision 7 J5: *"The Z80's timing is NOT
one-cycle-per-bus-access … the `PageCrossPenalty` field is 6502-specific"*).

**The fix (at the generator, the right altitude).** `CpuEmitter.KeyedDescriptorLiteral` now **forces**
`NeedsFallback = true` (and `EndsBlock = true`) for *every* structured-CPU (Z80) descriptor, regardless of
its class (`CpuEmitter.cs:3627-3636`). This is the generator-level realization of the all-fallback premise:
every Z80 op defers to the interpreter Step in 5-3a, so no Z80 op can ever take a 6502 arm. The comment in
the generator records exactly why: *"a Z80 op the importer happened to map to a generic (6502-shared) class
… would be wrongly emitted via the 6502 arm with the WRONG cycle model."*

**Why it matters as a finding.** Without the all-fallback parity gate forcing the JIT path to equal the
interpreter path byte-for-byte (including cycle count), this misclassification would have shipped silent: a
Z80 `NOP` JITted with 6502 timing, surfacing only as a subtle drift under a long real-program run. The
discipline "prove the generic compiler runs the Z80 as pure fallback first" caught a real cross-arm leak at
the generator before any IL was emitted. **This is the J5 cycle-model divergence, surfaced early** — and it
is a direct mandate for 5-3b: each hot Z80 op promoted to emitted IL must carry its OWN correct T-state count
and flag model, mirroring the interpreter body, never a 6502 arm.

---

## 4. The hot-op-vs-fallback split — the concrete starting spec for the optimization phase

This is the deliverable the optimization phase consumes: a proposed EMIT list and a FALLBACK list, derived
from ADR Decision 4/7's "emit the hot straight-line ops, fall back for the irregular ones" recommendation
and the Z80 micro-op vocabulary the interpreter already realizes. **It is framed as the starting spec to be
implemented for ALL THREE ISAs (Z80 + 68000 + 8086), not Z80-specifically** — see §5 for why. The final
emit list will be profile-driven (ranked by a frequency profile of the all-fallback ZEXALL run + the M4/M5
equivalents), but the *shape* of the split below is stable.

### EMIT (hot, straight-line, register-shape — promote to inlined IL)

| Family | Z80 ops | Interpreter micro-op kinds (the IL must mirror) |
|---|---|---|
| 8-bit load/transfer | `LD r,r'` / `LD r,n` / `LD r,(HL)` / `LD (HL),r` (the densest block, 0x40–0x7F) | `Transfer` / `Load` / `Store` |
| 8-bit ALU | `ADD/ADC/SUB/SBC/AND/OR/XOR/CP A,r` / `A,n` / `A,(HL)` | `Add8` … `Cp8` |
| 8-bit inc/dec | `INC r` / `DEC r` | `IncReg` / `DecReg` |
| 16-bit arith | `INC rr` / `DEC rr` / `ADD HL,rr` | `Inc16` / `Dec16` / `Add16` |
| 16-bit load | `LD rr,nn` | `Load16` |
| Control flow | `JR` / `JR cc` / `DJNZ` (DJNZ = the hottest Z80 loop primitive, ADR J9); `JP nn` / `JP cc,nn`; `CALL nn` / `CALL cc`; `RET` / `RET cc`; `RST n` (static vector, chainable) | `RelJump` / `RelJumpIf` / `Djnz` / `JumpAbs` / `JumpIf` / `CallAbs` / `CallIf` / `Ret` / `RetCc` / `Rst` |

The emitted IL for each must set **all eight Z80 flags (S/Z/Y/H/X/P/N/C) + the Q latch + WZ** bit-for-bit —
the interpreter is the oracle; the arm mirrors its proven body. This is where the J5 mandate bites: each arm
charges its OWN exact T-state count.

### FALL BACK (irregular — stays interpreter Step)

| Family | Z80 ops | Why fallback |
|---|---|---|
| ED block ops | `LDIR` / `LDDR` / `CPIR` / `CPDR` / `INIR` / `OTIR` etc. (`EdBlock`) | self-repeating one-instruction loops (PC does not advance until BC==0) — a loop the compiler must emit or fall back |
| `DAA` | the decimal-adjust | a *different* algorithm from the 6502 NMOS BCD arm (ADR J7: the 6502 `BlockCompiler.Decimal.cs` is DEAD CODE for the Z80 — the Z80 has no D flag; BCD is `DAA` after a binary op using H/N) |
| Exchange | `EX (SP),HL` / `EXX` / `EX DE,HL` / `EX AF,AF'` (`ExSpHl`/`Exx`/`ExDeHl`/`ExAfAf`) | eight-field wholesale swaps |
| I/O | `IN` / `OUT` / `IN r,(C)` / `OUT (C),r` (the Port class) | observable side effects on the Io bus — must hit the device every time, in cycle order, never inlined (ADR Decision 2) |
| Indexed | `(IX+d)` / `(IY+d)` ops (`DdFd*Indexed`) | the displacement EA + the DD/FD HL→IX re-interpretation |
| Compound | DDCB / FDCB (`DdCb`) | the densest undocumented-behavior slice; the 4-byte displacement-before-opcode form |
| CB plane | bit / rotate / shift (`CbRotate` / `CbBit`) | likely emit the common rotates eventually; fall back in the first cut |
| ED-core arith | `ADC/SBC HL,rr` / `NEG` / `RRD/RLD` (`EdAdcSbc16` / `EdNeg` / `EdRrdRld`) | irregular 16-bit flag rules + the BCD-rotate |
| Interrupt servicing | IM 0/1/2 + NMI vectoring | already interpreter-owned (M3.5-1); boundary-sampled, never inlined |

### The genericity work each EMIT family forces (the J-rows 5-3a deferred to the optimization phase)

- **J2 completion (16-bit registers):** the pair-view properties (`AF/BC/DE/HL/IX/IY` + alt set) the 5-3a
  map SKIPPED need a 16-bit register emit path (read/write the pair via its two half-fields, or via the
  property getter/setter). Required for `ADD HL,rr` / `INC rr` / `LD rr,nn`.
- **J5 (cycle model):** the emitted arms must charge the descriptor's total T-state count (the Z80 cycle
  table value), NOT a per-bus-access sum. `PageCrossPenalty` is 6502-specific — confirm it stays false on
  every Z80 descriptor. The 6502's `EmitChargeOneCycle` per-access model must loosen to a per-instruction
  total-charge for emitted Z80 ops. **This is the subtlest risk: the IL must charge the EXACT count the
  interpreter charges, or tier parity breaks on cycle count** (the J5 finding from §3, generalized).
- **J9 (block model):** the chainable-target analysis must handle `RST n` (static vector — chainable),
  conditional `CALL`/`JP` (two static successors — like the 6502 branch's taken/fall-through), and `DJNZ`
  (static backward target — chainable, the hottest loop primitive). Mirror the 6502 Branch/Jsr arms in
  `BlockCompiler.Flow.cs`.
- **J10 (operand shape):** the `JitOp` `(RegA,RegB,FlagBit,BoolArg)` shape already carries the Z80 kinds as
  data (the keyed table emits them); the `JitOpClass` enum grew to hold the Z80 classes. Confirm the operand
  model carries everything a Z80 emit arm reads (the bit-index for `BIT n` etc.). **CONFIRMED as data in
  5-3a** — the descriptors are well-formed; only the *emit arm* is missing.

---

## 5. The explicit deferral of 5-3b + its rationale

**Decision (Coordinator + user, recorded here as the D9 boundary):** M3.5 is **COMPLETE** at this boundary —
the JIT compiler is generic, the Z80 achieves tier parity, and the findings are documented. **5-3b (emitting
real IL for the hot Z80 ops) is DEFERRED to the post-8086 cross-architecture JIT-optimization phase**, after
M4 (68000) and M5 (8086).

**Why fold 5-3b into the optimization phase rather than do it now.** The ADR's organizing thesis (the
2026-06-13 three-architecture checkpoint) is that the optimization must be **valid across architectures, not
6502-shaped** — and by the same logic, not *Z80-shaped*. If we build the Z80 hot-op emitter now, we build a
Z80-specific emit layer; then we build a 68000-specific one at M4 and an 8086-specific one at M5, and only
*then* try to unify three already-divergent emit layers into a cross-arch optimizer. That is exactly the
"pay the cost once per arch" trap ADR risk-Q2 warns against. The cheaper, more genericity-honest path: build
the **hot-op emitter ONCE, in the optimization phase, against all three real register files / decode
structures / flag models / cycle models simultaneously** — so register allocation, block chaining, and
inlining are architecture-valid by construction (the ADR §3 verdict + the checkpoint's whole rationale).
5-3a's all-fallback posture is the safety net that makes this deferral free: the Z80 is *correct* through the
JIT today (tier parity proven); it is only *not yet faster*. Deferring 5-3b costs speed-now, not correctness
— the explicitly accepted trade-off ("the JIT remains slower-than-Tier-0 until the optimization phase;
thoroughness over speed-now").

**What the optimization phase will target (the D9 input this doc hands forward):**

1. **The actual speedup / fallback-elimination:** flip the §4 EMIT families from `NeedsFallback` to inlined
   IL, family-by-family, for all three ISAs, shrinking the fallback list to exactly the enumerated irregular
   set. Re-prove tier parity at each step (the all-fallback baseline is the regression net).
2. **Register allocation:** hoist architectural state into IL locals at block entry (ADR §6). J2 made the
   register file *data* — the prerequisite. The Z80's ~14+ live registers (halves + pairs + alt set + I/R)
   plus the 68000's 16 wide registers plus the 8086's segmented file force this to be allocation over an
   *arbitrary* register file, not the 6502's six.
3. **Block chaining across denser control flow (J9):** the Z80's conditional CALL/RET, JR/DJNZ, and RST n
   exercise the chain model far harder than the 6502's JMP/JSR/RTS — this is where the chain analysis most
   needs to be arch-valid.
4. **The cycle-model abstraction (J5):** generalize the per-bus-access charge to a per-instruction T-state
   total, with `PageCrossPenalty` reframed as one of several per-arch timing flags.
5. **Profiling:** the emit list is profile-driven — rank hot ops from the all-fallback ZEXALL run (Z80) and
   the M4/M5 equivalents, so the families promoted first are the ones that actually dominate real workloads.

---

## 6. The M3.5 close-state (what "done" means at the D9 boundary)

**The Z80 is COMPLETE as an architecture.** The full ISA is TomHarte-green across all 7 planes (1604
opcodes, every documented + undocumented op, per-T-state, including F's X/Y, WZ/MEMPTR, the Q latch, IM,
IFF1/IFF2); interrupt servicing works for IM 0/1/2 + NMI with a dedicated interrupt UAT (M3.5-1, PR #29);
ZEXDOC + ZEXALL pass as the integration composition proof (M3.5-2, PR #30); and the Z80 runs through the now-
generic JIT with byte-identical tier parity (M3.5-3a, PR #31). This is the honest finish-line one-liner from
the finish-line overview §7, satisfied.

**What remains is only the JIT speed-up**, and it is gated — by the 2026-06-13 human checkpoint — behind
**M4 (68000) and M5 (8086)**, to be built once across all three ISAs in the cross-architecture optimization
phase. The generic compiler is the M3 deliverable; the optimization is a separate, later milestone.

**The ADR Decision-7 J1–J10 table, filled in (the headline artifact):**

| # | ADR-predicted 6502 assumption | Realized outcome |
|---|---|---|
| J1 | `BlockCompiler` typed to `Mos6502Cpu` | **RESOLVED** — `IJitTarget` seam + generic `BlockCompiler<TCpu>`/`JittedCpu<TCpu>`/`CompiledBlock<TCpu>`/`BlockCache<TCpu>`/`BlockDelegate<TCpu>`; the Jit assembly no longer references a concrete CPU. The largest single JIT change. |
| J2 | Six baked `FieldInfo`s + index switch | **RESOLVED for field-backed registers** — operand + status/PC/accumulator handles are data (`IJitTarget` + `_regFields`). FINDING: the Z80's 16-bit pair-views are composed properties the 5-3a map SKIPS; the 16-bit emit path is the optimization phase's J2 completion. |
| J3 | `JitDescriptors[opcode]` 256-slot single-byte index | **RESOLVED** — `Discover` consumes the per-CPU `IJitTarget.Decode`/`DescriptorFor` (the keyed `JitDescriptorsByKey` + the structured walk); the compound DDCB 4-byte length flows through the computed-length walk. |
| J4 | Fastmem keyed to one bus | **CONFIRMED GENERIC** — the Z80 reuses memory-bus fastmem unchanged; the Io bus correctly never enters fastmem (the Port callout is unconditional). |
| J5 | Per-bus-access cycle model + `PageCrossPenalty` | **SURFACED** — the all-fallback gate caught a Z80 op (mis-mapped to a generic class) that would have emitted via the 6502 arm with the 6502 cycle model. Fixed by forcing `NeedsFallback` for all structured-CPU descriptors. The emitted arms (optimization phase) must charge the exact Z80 T-state count; `PageCrossPenalty` is 6502-specific. |
| J6 | Interrupt boundary-sample | **CONFIRMED GENERIC** + the HALT fast path went live for the Z80 (a uniform generated `Halted`); the dispatcher reads the live PC via the interface only. |
| J7 | The decimal arm (6502 NMOS BCD) | **CONFIRMED PER-CPU** — the 6502 decimal arm is DEAD CODE for the Z80 (no D flag); `DAA` is a separate fallback (optimization phase). |
| J8 | 256-byte-page SMC assumptions | **CONFIRMED GENERIC** — the Z80 reuses the dirty-page/SMC guard; the page size is a Core constant. The Z80's 4-byte ops exercise `PagesSpanned` harder; held. |
| J9 | 6502 control-flow block-ending | **DEFERRED to the optimization phase** — `RST n` / conditional CALL-RET-JP / `DJNZ` chainability is spec'd in §4 but not yet emitted (all-fallback today). |
| J10 | `JitOp` operand shape / `JitOpClass` | **CONFIRMED as data** — the keyed descriptors carry the Z80 kinds; the `JitOpClass` enum grew. Only the emit arm is missing (optimization phase). |

---

## 7. Observation for starting M4 (68000): is the generic seam ready for a third CPU?

**The seam is structurally ready to accept a third CPU; the gaps are exactly the dimensions the ADR §3
verdict flagged as surviving the Z80 untested.** Concretely:

- **What a 68000 gets for free.** The `IJitTarget` seam is CPU-agnostic by construction — a 68000 would
  declare its own generated `JitTarget` (status/PC/accumulator fields by name, `Step`/`AdvanceCycles`/
  `CycleCount`/`InterruptPending`, the keyed `Decode`/`DescriptorFor`, its `RegisterNames`), wrap in
  `JittedCpu<M68000Cpu>` as all-fallback, and inherit the entire tier-parity bring-up pattern (the TomHarte-
  through-JIT sweep + the all-fallback safety valve). The structural genericity (Jit references only Core)
  means adding the 68000 adds zero coupling to the JIT assembly. The all-fallback-first discipline that
  caught the J5 bug is directly reusable as the 68000's bring-up gate.
- **What the 68000 will force that the Z80 did NOT (the honest gaps, ADR §3).** The Z80 is still 8-bit,
  little-endian, byte-addressed, flat 16-bit address space — it shares the 6502's *memory model*, so it left
  these untested:
  - **32-bit registers.** `RegisterDef.Bits` is capped at 16 (`SpecParser` enforced). The 68000's 32-bit
    D0–D7/A0–A7 hit that wall — a real Core change. The `IJitTarget` register-field resolution is by name
    and width-agnostic, but the *emit arms* (when 5-3b/optimization lands) assume 8/16-bit math; 32-bit math
    + 32-bit flag rules are genuinely new emit code, not a seam change.
  - **Big-endian, word-granular decode.** The 68000 decodes a 16-bit word stream, big-endian. The J3
    decode walk *can* express it (the spec declares its decode structure), but the Z80 didn't *prove* it
    does — the 68000 is the first real test of the decode-driven discovery beyond byte-prefix chains.
  - **Word/long bus transactions + 24-bit address.** The JIT bus arms are byte-only (`Read8`/`Write8`); the
    Z80's 16-bit memory ops decompose into two byte accesses, so even *it* never exercised a true word bus
    transaction. The 68000's word/long transactions are new. (The 24-bit address fits the current
    `addressBits ≤ 24` ceiling — ADR 0002 — so no two-level page table is forced.)
- **Recommendation for M4.** The generic JIT seam is ready to *accept* the 68000 as all-fallback (correctness
  parity) with no JIT change — that bring-up should be cheap and is the right first step. The 68000's real
  pressure lands on `Core` (the 32-bit `RegisterDef.Bits` cap) and on the eventual *emit* layer (32-bit math,
  big-endian word decode, word/long bus arms) — which is precisely why deferring the hot-op emitter to the
  post-8086 optimization phase is the right call: the emitter should be designed knowing the 68000's 32-bit/
  big-endian/word-bus shape and the 8086's segmentation are both coming, not retrofitted onto a Z80-only
  emit layer.

---

## Slice docs index

- **This findings doc (5-3c):** `docs/superpowers/plans/2026-06-14-m3-z80-m35-3c-jit-genericity-findings.md`
- **M3.5-3a plan + close-state (the source):** `docs/superpowers/plans/2026-06-14-m3-z80-m35-3-z80-jit.md`
  (§"Close-state record (5-3a)"; the 5-3b/5-3c outlines)
- **The finish-line overview (updated to mark M3.5 complete):**
  `docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md`
- **The scoped M3.5 parent plan:** `docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md` (§M3.5-3)
- **M3.5-1 (interrupt servicing, PR #29):** `docs/superpowers/plans/2026-06-14-m3-z80-m35-1-interrupt-servicing.md`
- **M3.5-2 (ZEXALL, PR #30):** `docs/superpowers/plans/2026-06-14-m3-z80-m35-2-zexall.md`
- **The M2 JIT precedent (the template):** `docs/superpowers/plans/2026-06-12-m2-jit-i.md`,
  `docs/superpowers/plans/2026-06-13-m2-jit-ii.md`
- **Architecture record (Decision 7 J1–J10; risk-Q3; the 2026-06-13 three-arch checkpoint):**
  `docs/architecture/0001-z80-second-architecture.md`
