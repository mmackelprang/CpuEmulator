# M6 PR-3 — Z80 branch / jump / call / stack (completes the Z80 emit)

> **STATUS: PLAN — preparatory doc.** The implementation touches BOTH
> `src/CpuEmulator.Generators/CpuEmitter.cs` (the descriptor-gate whitelist + the `Z80Flow` JIT-class remap) and
> `src/CpuEmulator.Jit/BlockCompiler.cs` + `BlockCompiler.Z80.cs` (the emit arms — including a NEW emittable
> control-flow dispatch path), so it lands on a branch + PR (per the workflow), NOT straight to main.
> **For agentic workers:** REQUIRED SUB-SKILL once scheduled — use `superpowers:subagent-driven-development`
> or `superpowers:executing-plans` to implement task-by-task. Steps use checkbox (`- [ ]`) syntax.
> **Read first (binding):** ADR 0011 §8 PR-3 row (JP/JR/CALL/RET/DJNZ + RST + PUSH/POP — "this is where Z80
> blocks finally span multiple instructions and chain"), §2 (the emit strategy; the exception/microcoded tail
> stays fallback), §3.1 (the chaining payoff), §4 (the `CpuEmitter.cs` serialization rule).
> **DEPENDS ON PR-1 [merged] + PR-2 [merged]** (and is INDEPENDENT of PR-2b). PR-3 reuses PR-2's
> `EmitZ80SetQFromF`/`EmitZ80ClearQ`/`EmitZ80SetWZ` and the structured emit path, AND — crucially — the
> **6502 control-flow precedent** in `BlockCompiler.Flow.cs` (`EmitBranch`/`EmitJump`/`EmitJsr`/`EmitRts`,
> the taken/not-taken edges, `EmitChainOrExit` for static targets, `EmitNormalExit` for dynamic targets).
> **Read `BlockCompiler.Flow.cs` FULLY before starting — PR-3 is the Z80 analogue of those four 6502 arms.**

---

## Objective (the ADR §8 PR-3 row — completes the Z80 for M6)

Make the Z80 **control-flow + stack** families emit real IL instead of falling back to `inner.Step`:

- **PUSH rr / POP rr** (incl. AF) — `Z80Stack` / `Push16`/`Pop16`, 11 / 10 T. Stack writes/reads via the fastmem
  split + SP updates. (POP AF writes F directly — the F-as-pair-half subtlety.)
- **JP nn (unconditional) / JP cc,nn (conditional)** — `Z80Flow` / `JumpAbs` (10 T) / `JumpIf` (always 10 T).
- **JR d (unconditional) / JR cc,d (conditional)** — `RelJump` (12 T) / `RelJumpIf` (7 T not-taken, +5 taken).
- **CALL nn / CALL cc,nn** — `CallAbs` (17 T) / `CallIf` (10 T not-taken, +7 taken).
- **RET / RET cc** — `Ret` (10 T) / `RetCc` (5 T not-taken, +6 taken).
- **RST n** — `Rst` (11 T) — push PC, PC = `opcode & 0x38`.
- **DJNZ d** — `Djnz` (8 T not-taken, +5 taken) — the hot counted-loop edge (20% of Z80-W2, §6).

These are **control-flow / block-ending** ops — fundamentally different from the straight-line LD/ALU of PR-1/
PR-2. The plan's load-bearing content is the **four control-flow design questions** (DECISIONS H–K below):
the target-PC + taken/not-taken cycle emission, the `EndsBlock`/chaining composition, the stack fastmem
writes + SP, and the WZ/MEMPTR side-effects. The 6502 arms already solve the structurally-identical problem
(JSR=CALL, RTS=RET, Bcc=JR cc, JMP=JP), so PR-3 is a transcription with the Z80 cycle/WZ model, NOT a new
mechanism — **except** the one genuinely new piece: teaching `EmitInstruction` to DISPATCH an emittable Z80
control-flow row (the `Z80Flow` JIT class has no emit arm today — DECISION H).

**The chaining payoff (§3.1 — why PR-3 matters most).** Until the hot branches emit, EVERY Z80 control-flow op
is a fallback that ENDS the block after one instruction, so Z80 blocks never span multiple instructions and
never chain — they pay the full dispatch cost per instruction. PR-3 is where Z80 blocks finally span and chain
(JP/JR/CALL/RST chain to their static targets; RET/POP-driven targets exit to the dispatcher). This is the
structural unlock that makes PR-1/PR-2's straight-line emit actually compound.

**Closes the Z80 for M6 (§2).** The block-op / prefix-plane long tail (LDIR/CPIR/ED-CB rarities, the
exception-capable ops) stays fallback BY DESIGN. After PR-3, the Z80 is "done" at the §6 cumulative-86–100%
line: LD (PR-1) + ALU/flags (PR-2) + 16-bit ALU (PR-2b) + branch/call/stack (PR-3) cover essentially all
executed instructions.

---

## NO benchmark gate (owner policy change, 2026-06-18)

The owner has turned OFF per-PR W2/W3 benchmark measurement. **This plan has NO "measured W1/W2/W3 delta"
gate.** The merge preconditions are the **fast correctness gates ONLY**:

1. **TomHarte-through-JIT parity** for the emitted opcodes (state + Q + WZ + CycleCount — including the
   taken/not-taken cycle split and the stack writes/reads — byte-identical to the interpreter).
2. **ZEXDOC-through-JIT smoke** green (and the periodic full ZEXDOC/ZEXALL — control-flow + stack ops are the
   test harness scaffolding ZEX itself runs on, so a green ZEX-through-JIT is strong integration proof).
3. **`FallbackEmitCount` drops** by exactly the emitted opcodes.
4. **Regression empty-diff / tripwire**: the 6502 + 68000 generated tables show an EMPTY `git diff`; no
   committed 6502/68000 cycle number moves.

Do NOT add a benchmark step (despite the §6 note that PUSH/POP/JP/CALL/RET dominate Z80-W1 — the arc-end
benchmark captures the cumulative delta).

---

## What the recon CONFIRMED (file:line — load-bearing, verified against `main` @ `851de3b`)

### The 6502 control-flow precedent (the proven shape PR-3 transcribes — `BlockCompiler.Flow.cs`)

This is the single most important reuse fact: **the structurally-identical arms already exist and are proven.**

| 6502 arm | Z80 analogue | The pattern PR-3 reuses |
|---|---|---|
| `EmitBranch` (`:377-455`) | JR cc,d / DJNZ | Read offset; test the condition; taken arm sets PC = target + chains to the STATIC taken target; not-taken arm chains to the STATIC fall-through. **Two `EmitChainOrExit` calls, both static.** |
| `EmitJump` Absolute (`:458-483`) | JP nn / JP cc,nn | Read the 16-bit target (a code-stream CONSTANT); `PC = target`; `EmitChainOrExit(ctx, target)` — chainable. |
| `EmitJsr` (`:539-579`) | CALL nn / CALL cc,nn / RST n | Read target; **push PC via `EmitStackAddress`+`EmitStoreByte`+S-decrement** (the proven stack-write shape); `PC = target`; `EmitChainOrExit(ctx, target)` — the call entry is static, chainable. |
| `EmitRts` (`:582-620`) | RET / RET cc / POP | **Pop via `EmitStackAddress`+`LoadByteFromBus`+S-increment**; `PC = popped`; `EmitNormalExit(ctx)` — the popped target is DYNAMIC, NOT chainable. |
| `EmitJump` Indirect (`:484-531`) | (n/a — Z80 JP (HL) is fallback) | Dynamic target → `EmitNormalExit`. |

**The two chaining primitives (the heart of DECISION I):**
- `EmitChainOrExit(ctx, staticTargetPc)` (`BlockCompiler.cs:808-840`): the block-ending op has ALREADY set PC;
  this clears the three chain-break gates (budget≤0, `dirty.Any`, `InterruptPending`) and, if clear, calls
  `ChainDispatch` with the COMPILE-TIME-CONSTANT target — a direct block→block edge, no dispatcher round-trip.
  **Use for a STATIC successor** (JP nn, CALL nn, JR d, RST n, the taken+not-taken edges of conditional
  branches with constant targets).
- `EmitNormalExit(ctx)` (`BlockCompiler.cs:790-797`): set exit=Normal, ret — the dispatcher resumes at the
  (dynamic) PC the arm set. **Use for a DYNAMIC successor** (RET/RET cc — popped from the stack; the value is
  not a compile-time constant).

### The Z80 flow oracles (the interpreter bodies the IL must mirror — `CpuEmitter.cs:2650-2778`)

Verbatim (the IL must match byte-for-byte). `pc`=PC, `sp`=SP. `CondExpr()` = `(((F >> bit) & 1) == when)`.

```csharp
// JumpAbs (JP nn) — 10 T. busReads=2.
byte jl = ReadBus(PC); PC = (ushort)(PC+1);
byte jh = ReadBus(PC); PC = (ushort)(jl | (jh << 8));
WZ = PC;                                              // WZ = nn (the new PC)

// CallAbs (CALL nn) — 17 T. busReads=2, busWrites=2.
byte cl = ReadBus(PC); PC = (ushort)(PC+1);
byte ch = ReadBus(PC); PC = (ushort)(PC+1);          // PC now past the operand = the RETURN address
SP = (ushort)(SP-1); WriteBus(SP, (byte)(PC >> 8));  // push PCH
SP = (ushort)(SP-1); WriteBus(SP, (byte)PC);         // push PCL
PC = (ushort)(cl | (ch << 8));
WZ = PC;                                              // WZ = nn

// Ret (RET) — 10 T. busReads=2.
byte rl = ReadBus(SP); SP = (ushort)(SP+1);
byte rh = ReadBus(SP); SP = (ushort)(SP+1);
PC = (ushort)(rl | (rh << 8));
WZ = PC;                                              // WZ = popped PC

// RelJump (JR d) — 12 T. busReads=1.
sbyte d = (sbyte)ReadBus(PC); PC = (ushort)(PC+1);    // PC now past the offset
PC = (ushort)(PC + d);
WZ = PC;                                              // WZ = dest

// RelJumpIf (JR cc,d) — 7 T not-taken; +5 taken. busReads=1.
sbyte d = (sbyte)ReadBus(PC); PC = (ushort)(PC+1);
if (CondExpr()) { PC = (ushort)(PC + d); _cycles += 5; WZ = PC; }   // WZ ONLY when taken

// Djnz (DJNZ d) — 8 T not-taken; +5 taken. busReads=1.
B = (byte)(B - 1);
sbyte d = (sbyte)ReadBus(PC); PC = (ushort)(PC+1);
if (B != 0) { PC = (ushort)(PC + d); _cycles += 5; WZ = PC; }       // WZ ONLY when taken

// JumpIf (JP cc,nn) — always 10 T. busReads=2.
byte jl = ReadBus(PC); PC = (ushort)(PC+1);
byte jh = ReadBus(PC); PC = (ushort)(PC+1);
WZ = (ushort)(jl | (jh << 8));                       // WZ = nn UNCONDITIONALLY (operand always fetched)
if (CondExpr()) PC = (ushort)(jl | (jh << 8));

// CallIf (CALL cc,nn) — 10 T not-taken; +7 taken. busReads=2.
byte cl = ReadBus(PC); PC = (ushort)(PC+1);
byte ch = ReadBus(PC); PC = (ushort)(PC+1);
WZ = (ushort)(cl | (ch << 8));                       // WZ = nn UNCONDITIONALLY
if (CondExpr()) {
    SP=(ushort)(SP-1); WriteBus(SP,(byte)(PC>>8));
    SP=(ushort)(SP-1); WriteBus(SP,(byte)PC);
    PC = (ushort)(cl | (ch << 8));
    _cycles += 5;                                     // taken: 10→17, MINUS the 2 push writes charged inline
}

// RetCc (RET cc) — 5 T not-taken; +6 taken. busReads=0 (the pops are inside the taken branch).
if (CondExpr()) {
    byte rl = ReadBus(SP); SP=(ushort)(SP+1);
    byte rh = ReadBus(SP); SP=(ushort)(SP+1);
    PC = (ushort)(rl | (rh << 8));
    _cycles += 4;                                     // taken: 5→11, MINUS the 2 pop reads charged inline
    WZ = PC;                                          // WZ = popped PC ONLY when taken
}

// Rst (RST n) — always 11 T. busWrites=2. vec = opcode & 0x38.
SP=(ushort)(SP-1); WriteBus(SP,(byte)(PC>>8));        // PC here is ALREADY past the 1-byte opcode = return addr
SP=(ushort)(SP-1); WriteBus(SP,(byte)PC);
PC = vec;                                             // 0x00/0x08/.../0x38
WZ = vec;                                             // WZ = the RST vector
```

> **The cycle accounting (`EmitInternal`, `:2785-2788`).** `internalT = total − 1 (the opcode fetch Step
> charged) − busReads − busWrites`. So the body's `_cycles +=` for the NOT-TAKEN base is `total − 1 − bus`, and
> the conditional taken-penalty is added INLINE inside the `if`. **In the JIT:** `EmitInstruction` charges the
> fetch (1, base-plane), each `LoadByteFromBus`/`EmitStoreByte` charges 1, and the arm charges the residual to
> reach `BaseCycles` for the not-taken path, then charges the taken penalty INSIDE the taken branch (mirroring
> the oracle). This is DECISION J. **The taken penalty for CALL cc / RET cc is the oracle's
> `_cycles += 5`/`_cycles += 4` (NOT +7/+6) because the 2 push/pop bus accesses are charged inline by
> `EmitStoreByte`/`LoadByteFromBus`** — see DECISION J's worked table.

### The PUSH/POP oracles (`CpuEmitter.cs:2438-2462`)

```csharp
// Push16 (PUSH rr) — 11 T. busWrites=2. pair = BC/DE/HL/AF (+ IX/IY prefixed, not in PR-3).
SP = (ushort)(SP-1); WriteBus(SP, (byte)(pair >> 8));   // push hi
SP = (ushort)(SP-1); WriteBus(SP, (byte)pair);          // push lo
// internalT = 11 − 1 − 2 = 8

// Pop16 (POP rr) — 10 T. busReads=2.
byte lo = ReadBus(SP); SP = (ushort)(SP+1);
byte hi = ReadBus(SP); SP = (ushort)(SP+1);
pair = (ushort)(lo | (hi << 8));                        // POP AF writes A and F (the pair halves)
// internalT = 10 − 1 − 2 = 7
```

> **PUSH/POP touch NO flags and NO WZ.** `Z80WritesFlags(Z80Stack, …)` is false → `Q = 0` (`EmitZ80ClearQ`).
> **The POP AF subtlety:** the `AF` pair-view's low half is `F` (`PairHalves["AF"] = ("A","F")`,
> `BlockCompiler.cs:117`), so `EmitStoreReg16(ctx, "AF")` writes `A = hi, F = lo` — POP AF correctly loads F
> from the stack via the existing PR-0 pair-write. **PUSH/POP are block-CONTINUING** (they ride `Z80Stack` →
> `JitOpClass.Register`, NOT a flow class), so they do NOT end the block — they emit inline like PR-2's ALU and
> the block continues. This is the easy half of PR-3.

### The descriptor classes (the dispatch routing — the DECISION H crux)

| Family | InstructionClass | `JitOpClass` (via `ClassifyForJit`) | `EndsBlock` today | Emit arm today |
|---|---|---|---|---|
| PUSH/POP | `Z80Stack` | **`Register`** (`:4349-4354`) | false (block-continuing) | **none — falls back** (z80-forced) |
| JP/JR/CALL/RET/DJNZ/RST | `Z80Flow` | **`Flow`** (`:4348`) | **true** | **none — `Flow` is a fallback class** |

**The crux (DECISION H):** PUSH/POP ride `Register` → they arrive at `EmitRegister`, exactly where PR-1/PR-2's
Z80 guard lives — easy to route (add an `IsZ80StackKind` guard beside the LD/ALU guards). But the **flow ops
ride `JitOpClass.Flow`**, and `EmitInstruction`'s dispatch switch (`BlockCompiler.cs:364-380`) maps `Flow` to
the `default` throw — there is NO emittable `Flow` arm (the 6502's flow ops are split into `Jsr`/`Rts`/`Jump`/
`Branch` classes; only `Brk`/`Rti` ride `Flow`, and they are always fallback). So an emittable Z80 flow row has
**no JIT dispatch home today.** PR-3 must give it one — DECISION H resolves how.

### The footprint plumbing (`Z80EmitOperandBytes` — the flow-op operand bytes)

`Z80EmitOperandBytes` (`BlockCompiler.cs:204-224`) corrects the discovery walk's PC footprint. Today it handles
LD (PR-1) and ALU (PR-2). The flow/stack families' PC-operand footprints:
- JP nn / JP cc,nn / CALL nn / CALL cc,nn: 2 operand bytes (the 16-bit target).
- JR d / JR cc,d / DJNZ d: 1 operand byte (the signed displacement).
- RET / RET cc / RST n / PUSH / POP: 0 operand bytes.

**BUT — the flow ops END the block** (`EndsBlock=true` stays true even when emitted — see DECISION I), so
`Discover` stops at them and their `nextPc` is set by the ARM (PC = target), never read from the walk. So the
discovery-walk footprint matters ONLY for PUSH/POP (block-continuing) — and they read 0 operand bytes. **The
flow ops' footprint is consumed inside the arm** (the arm reads the operand bytes off PC and advances PC), so
`Z80EmitOperandBytes` does NOT need a flow-op entry for correctness of the chaining nextPc. **However**, the
arm needs the STATIC target as a compile-time constant for `EmitChainOrExit` — it reads it from the bus at
compile time (exactly as `EmitJsr`/`EmitJump`/`EmitBranch` do: `_bus.Read8((ushort)(pc + 1))`). See DECISION I.

> **Subtle footprint point to VERIFY (DECISION I):** for a CONDITIONAL branch, the NOT-TAKEN fall-through PC is
> `pc + footprint` (the instruction's full length). The 6502 `EmitBranch` gets this from the `length` parameter
> threaded into the arm (`BlockCompiler.cs:371`, `EmitBranch(ctx, pc, d, length)`). PR-3's conditional Z80 arms
> need the same `length` — confirm the Z80 flow dispatch passes `length` (the walk's computed length) to the arm
> so the not-taken fall-through target is exact. Since the flow op ends the block, `length` here is the walk's
> `r.Length + Z80EmitOperandBytes` — and because the row ends the block, `Z80EmitOperandBytes` returns 0 for it
> (the early `d.NeedsFallback` short-circuit does NOT apply once emitted, so **PR-3 MUST add the flow-op operand
> footprint to `Z80EmitOperandBytes` so `length` is correct for the not-taken fall-through PC**). This is the
> one footprint edit PR-3 needs — see Task 1 Step 3.

### The gate mechanism (PR-1/PR-2/PR-2b — PR-3 extends `IsEmittableZ80Family` + remaps `Z80Flow`)

`IsEmittableZ80Family` un-forces a whitelisted row in `KeyedDescriptorLiteral` (fallback flip) AND
`ClassifyForJit` (the `z80 = false` un-force). For PR-3:
- **PUSH/POP** (`Z80Stack`): admitting them in `IsEmittableZ80Family` flips `z80 = false`, so `ClassifyForJit`'s
  `Z80Stack → "Register"` mapping yields `NeedsFallback=false, EndsBlock=false` — they emit and continue. Clean.
- **Flow ops** (`Z80Flow`): admitting them flips `z80 = false`, but `ClassifyForJit` maps `Z80Flow → "Flow"`
  (`:4348`), and `endsBlock = jitClass is "Branch" or "Jump" or "Jsr" or "Rts" or "Flow" || fallback`
  (`:4359`) — so `"Flow"` keeps `EndsBlock=true` (correct — they DO end the block) but `"Flow"` has no emit arm.
  **PR-3 must remap the emittable Z80 flow rows to an EMITTABLE control-flow JIT class** (`Branch`/`Jump`/`Jsr`/
  `Rts`) OR add a Z80-flow emit arm under a Z80 guard. DECISION H.

---

## DECISIONS (the four control-flow design questions — surfaced for the owner)

### DECISION H — how an emittable Z80 control-flow row gets a JIT dispatch home (the crux)

The flow ops ride `JitOpClass.Flow`, which `EmitInstruction` routes to `default → throw`. Two clean options:

- **(H1) Remap to the existing emittable 6502 control-flow classes.** In `ClassifyForJit`, map the emittable Z80
  flow kinds to the matching 6502 JIT class: `JumpAbs`/`JumpIf` → `"Jump"`, `CallAbs`/`CallIf`/`Rst` → `"Jsr"`,
  `Ret`/`RetCc` → `"Rts"`, `RelJump`/`RelJumpIf`/`Djnz` → `"Branch"`. Then `EmitInstruction` already dispatches
  these to `EmitJump`/`EmitJsr`/`EmitRts`/`EmitBranch`, and each of those arms gets a `TargetIsZ80` guard at its
  top routing to a Z80 sibling (`EmitZ80Jump`/`EmitZ80Call`/`EmitZ80Ret`/`EmitZ80RelBranch`) — exactly the
  PR-1/PR-2 pattern (`EmitRegister` → `if (TargetIsZ80 && …) EmitZ80…`). **Pro:** reuses the proven dispatch +
  the `EndsBlock` derivation (all four target classes already set `EndsBlock=true`); the Z80 arms sit beside
  their 6502 analogues in `BlockCompiler.Flow.cs`. **Con:** the Z80 conditional forms don't map 1:1 to the
  6502's (JR cc is relative like Bcc, but JP cc is absolute-conditional — there is no 6502 "conditional
  absolute jump"; it would ride `"Jump"` but `EmitJump` is unconditional). So H1 needs care: the conditional
  forms (JP cc, CALL cc, RET cc, JR cc, DJNZ) need their own Z80 emit logic regardless — the 6502 class is just
  the dispatch label.
- **(H2) Add a dedicated emittable Z80 flow class + dispatch arm.** Add a `JitOpClass.Z80Flow` emittable value
  (or reuse a new dispatch key), map the emittable flow rows to it in `ClassifyForJit` (with `EndsBlock=true`),
  add a `case JitOpClass.Z80Flow: EmitZ80Flow(ctx, pc, d, length); break;` to `EmitInstruction`, and write
  `EmitZ80Flow` as a single arm switching on `op.Kind` (JumpAbs/JumpIf/CallAbs/CallIf/Ret/RetCc/RelJump/
  RelJumpIf/Djnz/Rst). **Pro:** ALL Z80 flow logic lives in ONE Z80 arm, switched on the exact op-kind — no
  forcing the Z80 ops through 6502 class labels that don't quite fit (esp. the conditional-absolute forms);
  cleanest separation. **Con:** adds a JIT dispatch class + an `EmitInstruction` case (a slightly larger
  structural change than H1's "guard at the top of the existing arm").

**Recommendation: H2 (a dedicated `EmitZ80Flow` arm dispatched by a new emittable class).** Rationale: the Z80
control-flow family does NOT line up cleanly with the 6502 classes (conditional-absolute JP cc / CALL cc have no
6502 analogue; the Z80 cycle model + the WZ side-effects + the taken/not-taken penalties are entirely Z80), so
forcing them through `"Jump"`/`"Jsr"`/`"Rts"`/`"Branch"` labels (H1) buys only the dispatch plumbing while every
arm body is Z80-specific anyway. A single `EmitZ80Flow` switched on `op.Kind` is the structured-CPU analogue of
PR-2's `EmitZ80Alu` (one arm, switch on kind) — consistent with how PR-1/PR-2 are organized, and it keeps the
6502 `EmitJump`/`EmitJsr`/`EmitRts`/`EmitBranch` arms untouched (zero 6502 regression surface). **Owner's call:
H1 (guard the 6502 arms) or H2 (a dedicated Z80 flow arm — recommended)?**

### DECISION I — `EndsBlock` / chaining composition (static vs dynamic targets)

Every PR-3 flow op KEEPS `EndsBlock=true` when emitted (they genuinely end the straight-line run — the block
compiler's `Discover` must stop, and the arm self-terminates with its own exit). The composition with chaining:

- **STATIC target → `EmitChainOrExit(ctx, staticTargetPc)`** (chainable; the target is a compile-time constant
  read from the code stream at compile time, exactly as the 6502 `EmitJsr`/`EmitJump`/`EmitBranch` do):
  - **JP nn / JP cc,nn:** `staticTargetPc = bus[pc+1] | (bus[pc+2] << 8)` — chain to it (the taken edge);
    JP cc's not-taken edge chains to `pc + 3` (the fall-through).
  - **JR d / JR cc,d / DJNZ d:** `staticTargetPc = (ushort)((pc + 2) + (sbyte)bus[pc+1])` — chain to it (taken);
    the not-taken edge chains to `pc + 2`.
  - **CALL nn / CALL cc,nn:** `staticTargetPc = bus[pc+1] | (bus[pc+2] << 8)` — chain to the call ENTRY (the
    return address is dynamic-on-the-stack, but the entry is static, exactly like 6502 JSR); CALL cc's not-taken
    edge chains to `pc + 3`.
  - **RST n:** `staticTargetPc = opcode & 0x38` — a compile-time constant; chain to it.
- **DYNAMIC target → `EmitNormalExit(ctx)`** (NOT chainable; the target is popped from the stack at run time):
  - **RET / RET cc (taken):** PC = popped — dynamic; exit to the dispatcher (exactly like 6502 RTS).
  - **RET cc (not-taken):** PC = `pc + 1` (fall-through) — this IS static, so the not-taken edge CAN chain to
    `pc + 1`. (RET cc taken → `EmitNormalExit`; not-taken → `EmitChainOrExit(ctx, pc+1)`.)

**The conditional-branch composition (the two-edge shape, proven by 6502 `EmitBranch`):** emit the operand read
+ the WZ side-effect (unconditional for JP cc / CALL cc; conditional for JR cc / DJNZ / RET cc — see DECISION K),
then test the condition; the TAKEN arm sets PC = target, charges the taken penalty, and chains/exits per the
target's static-or-dynamic nature; the NOT-TAKEN arm sets PC = fall-through and chains to it. **This is
`EmitBranch`'s exact two-`EmitChainOrExit` structure** (`BlockCompiler.Flow.cs:449/454`) — PR-3's conditional
arms transcribe it with the Z80 cycle/WZ model.

**The footprint edit (the one `Z80EmitOperandBytes` change PR-3 needs):** the not-taken fall-through PC for a
conditional branch is `pc + length`, where `length` is the walk's computed length. Because the emitted flow row
ends the block, the discovery walk reads `length = r.Length + Z80EmitOperandBytes(d)`, and
`Z80EmitOperandBytes` currently returns 0 for flow kinds. PR-3 adds the flow-op operand footprint (JP/CALL = 2,
JR/DJNZ = 1, RET/RST = 0) so `length` (threaded into the arm) yields the correct fall-through PC. **This is a
footprint correctness edit, not a chaining-nextPc edit** (the block ends, so the walk doesn't advance past it —
but the arm needs the right `length`). DECISION I-footprint, Task 1 Step 3.

> **No owner decision needed on I — it is the proven 6502 pattern.** The plan surfaces it so the implementer
> applies the static-vs-dynamic split correctly: JP/JR/CALL/RST/DJNZ + the not-taken edges → `EmitChainOrExit`;
> RET/RET-cc-taken → `EmitNormalExit`. The one judgement call folded in: **RST n's target is static (the
> vector), so RST chains** — confirm this is desired (it almost always is; a RST handler at a fixed vector is a
> hot chain edge). The plan recommends chaining RST.

### DECISION J — the taken/not-taken cycle emission (the per-edge T-state charge)

The Z80 conditional flow ops have DIFFERENT cycle counts for taken vs not-taken, and the `BaseCycles` in the
descriptor is the **not-taken base** (the oracle's `total` for the conditional kinds is the not-taken count;
`Z80Cycles` returns `JumpIf=>10`, `RelJumpIf=>7`, `Djnz=>8`, `CallIf=>10`, `RetCc=>5`). The taken penalty is
charged INSIDE the taken branch. The JIT mirrors: `EmitInstruction` charges the fetch (1), each
`LoadByteFromBus`/`EmitStoreByte` charges 1, the arm charges the not-taken residual to reach `BaseCycles`, and
charges the taken penalty inside the taken branch via `EmitChargeCycles`. Worked (all base-plane, fetch=1):

| Op | `BaseCycles` (not-taken) | bus accesses | not-taken residual | taken penalty (in-branch) | taken total |
|---|---|---|---|---|---|
| JP nn | 10 | 2 reads | 10−1−2 = 7 | — (unconditional) | 10 |
| JP cc,nn | 10 | 2 reads | 10−1−2 = 7 | 0 (always 10 T) | 10 |
| JR d | 12 | 1 read | 12−1−1 = 10 | — | 12 |
| JR cc,d | 7 | 1 read | 7−1−1 = 5 | +5 | 12 |
| DJNZ d | 8 | 1 read | 8−1−1 = 6 | +5 | 13 |
| CALL nn | 17 | 2 reads + 2 writes | 17−1−4 = 12 | — | 17 |
| CALL cc,nn | 10 | 2 reads (writes only if taken) | 10−1−2 = 7 | +5 (the 2 push writes charged inline by `EmitStoreByte`) | 17 (= 10 base + 5 + 2 writes) |
| RET | 10 | 2 reads | 10−1−2 = 7 | — | 10 |
| RET cc | 5 | 0 (reads only if taken) | 5−1−0 = 4 | +4 (the 2 pop reads charged inline by `LoadByteFromBus`) | 11 (= 5 base + 4 + 2 reads) |
| RST n | 11 | 2 writes | 11−1−2 = 8 | — | 11 |

> **The load-bearing subtlety (verbatim from the oracle):** CALL cc's taken penalty is `+5` (NOT +7) and RET
> cc's is `+4` (NOT +6) because the 2 push/pop bus accesses are charged inline by `EmitStoreByte`/
> `LoadByteFromBus` INSIDE the taken branch (each charges 1). The oracle comments say this explicitly
> (`CpuEmitter.cs:2747`: "taken penalty: 10 → 17, minus the 2 push writes charged inline"; `:2759`: "5 → 11,
> minus the 2 pop reads charged inline"). **The JIT MUST charge the not-taken residual OUTSIDE the condition
> (always), then INSIDE the taken branch do the bus accesses (each +1) + the reduced penalty (+5 / +4).** The
> TomHarte cycle-parity gate (Task 7) catches any off-by-one here. **No owner decision — this is mechanical;
> the plan surfaces the exact per-edge arithmetic so the implementer charges it right.**

### DECISION K — the WZ/MEMPTR side-effects (per-op, conditional-aware)

The Z80 control-flow ops have precise, vector-confirmed WZ behavior (the oracle's `EmitWz`/`EmitWzIndented`):

| Op | WZ side-effect | When |
|---|---|---|
| JP nn | WZ = nn | always |
| JP cc,nn | WZ = nn | **UNCONDITIONALLY** (operand always fetched — vector-confirmed) |
| JR d | WZ = dest | always |
| JR cc,d | WZ = dest | **ONLY when taken** |
| DJNZ d | WZ = dest | **ONLY when taken** |
| CALL nn | WZ = nn | always |
| CALL cc,nn | WZ = nn | **UNCONDITIONALLY** |
| RET | WZ = popped PC | always |
| RET cc | WZ = popped PC | **ONLY when taken** |
| RST n | WZ = vector | always |
| PUSH / POP | (none) | WZ unchanged |

> **The split is load-bearing and counterintuitive:** the conditional ABSOLUTE forms (JP cc, CALL cc) set WZ
> UNCONDITIONALLY (because the 16-bit operand is always fetched, and WZ tracks the fetched operand), while the
> conditional RELATIVE forms (JR cc, DJNZ) and RET cc set WZ ONLY when taken. The emit arm places
> `EmitZ80SetWZ` accordingly: for JP cc / CALL cc, after the operand read, OUTSIDE the condition; for JR cc /
> DJNZ / RET cc, INSIDE the taken branch. **The TomHarte gate checks WZ in the post-state, so a misplaced
> `EmitZ80SetWZ` is a red test.** No owner decision — the plan surfaces the exact placement; the implementer
> mirrors the oracle's `EmitWz` (unconditional) vs `EmitWzIndented` (taken-only) calls. **All PR-3 flow ops set
> Q = 0** (`Z80WritesFlags(Z80Flow/Z80Stack, …)` is false) — `EmitZ80ClearQ`, like PR-1's LD.

---

## The staged outline (one line each)

- **Task 1** — Extend `IsEmittableZ80Family` to admit the flow kinds (JumpAbs/JumpIf/CallAbs/CallIf/Ret/RetCc/
  RelJump/RelJumpIf/Djnz/Rst) + the stack kinds (Push16/Pop16); remap the emittable `Z80Flow` rows to the
  emittable dispatch class (DECISION H); add the flow-op operand footprint to `Z80EmitOperandBytes` (DECISION
  I-footprint). Regenerate; confirm the flip + no `BaseCycles` change.
- **Task 2** — Route the rows: PUSH/POP via an `IsZ80StackKind` guard in `EmitRegister` (→ `EmitZ80Stack`); the
  flow ops via the DECISION-H dispatch (→ `EmitZ80Flow`). Thread `pc` + `length` into the flow arm.
- **Task 3** — `EmitZ80Stack` (`BlockCompiler.Z80.cs`): PUSH (SP−=1, write hi; SP−=1, write lo; Q=0; residual 8)
  and POP (read lo, SP+=1; read hi, SP+=1; pair = lo|hi<<8; Q=0; residual 7), reusing PR-0's `EmitLoadReg16`/
  `EmitStoreReg16` and the SP wide field.
- **Task 4** — `EmitZ80Flow` (`BlockCompiler.Z80.cs`): the unconditional forms (JP nn / JR d / CALL nn / RST /
  RET) — operand read, push/pop via the fastmem split + SP, WZ, PC = target, the not-taken residual, then
  `EmitChainOrExit` (static) or `EmitNormalExit` (dynamic = RET).
- **Task 5** — `EmitZ80Flow` continued: the conditional forms (JP cc / JR cc / DJNZ / CALL cc / RET cc) — the
  two-edge structure (taken: penalty + WZ-if-relative + push-if-call + PC=target + chain/exit; not-taken: PC =
  fall-through + chain), the condition test from `op.FlagBit`/`op.BoolArg`, the DECISION-J per-edge cycles, the
  DECISION-K WZ placement.
- **Task 6** — The regression-safety gate (empty 6502/68000 diff + tripwire) + the `FallbackEmitCount` flips +
  the descriptor-state assertions (incl. the `EndsBlock=true` retained on the emitted flow rows).
- **Task 7** — The parity gate (TomHarte-through-JIT for all PR-3 opcodes incl. taken AND not-taken paths,
  cycles, WZ, the stack; ZEXDOC-through-JIT smoke). **NO benchmark.**

---

## Task 1 — Extend the gate (flow + stack kinds), remap `Z80Flow` dispatch, add the flow footprint

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`IsEmittableZ80Family`; `ClassifyForJit`'s `Z80Flow` map)
- Modify: `src/CpuEmulator.Jit/BlockCompiler.cs` (`Z80EmitOperandBytes` — the flow-op footprint)

- [ ] **Step 1:** Extend `IsEmittableZ80Family` (the base-plane kind set) with the flow + stack kinds. PUSH/POP
  and every flow op are base-plane (the prefixed PUSH/POP IX/IY and JP (IX) stay fallback via the base-plane
  guard). JP (HL) (`JumpIndirect`) stays fallback (dynamic target, rare — §2):

```csharp
        // ── M6 PR-3: the control-flow + stack families (base-plane) ──
        // Flow: JP/JR/CALL/RET/DJNZ/RST (NOT JumpIndirect = JP (HL) — dynamic target, stays fallback).
        // Stack: PUSH/POP rr (base-plane BC/DE/HL/AF; the DD/FD IX/IY forms stay fallback via the base gate).
        if (kind is "JumpAbs" or "JumpIf" or "CallAbs" or "CallIf" or "Ret" or "RetCc"
                  or "RelJump" or "RelJumpIf" or "Djnz" or "Rst"
                  or "Push16" or "Pop16")
            return true;
```

> **Builder note:** this slots into the base-plane kind `switch`/`if` chain alongside PR-2's ALU kinds and
> PR-2b's Inc16/Dec16 (place it after those). Confirm `JumpIndirect` (JP (HL), 0xE9) is NOT in the set — it has
> a dynamic target and stays fallback (the 6502 JMP-indirect analogue is also `EmitNormalExit`-only, but the Z80
> JP (HL) is rare enough to leave fallback per §2).

- [ ] **Step 2:** Remap the emittable `Z80Flow` rows to the emittable dispatch class (DECISION H). **If H2
  (recommended):** add a new emittable `JitOpClass` value (e.g. extend the enum with `Z80Flow`) and map the
  emittable flow rows to it in `ClassifyForJit`, keeping `EndsBlock=true`:

```csharp
        // M6 PR-3 (DECISION H2): an emittable Z80 flow row dispatches to the dedicated EmitZ80Flow arm. The
        // Z80Flow InstructionClass maps to the emittable JitOpClass.Z80Flow (a NEW enum value) ONLY when the
        // row is whitelisted; a non-whitelisted Z80Flow row (JP (HL), still-fallback forms) keeps "Flow" (the
        // fallback class). EndsBlock stays true (the flow op ends the straight-line run).
        InstructionClass.Z80Flow => IsEmittableZ80Family(insn) ? "Z80Flow" : "Flow",
```

  And ensure `endsBlock` includes the new class:
  `bool endsBlock = jitClass is "Branch" or "Jump" or "Jsr" or "Rts" or "Flow" or "Z80Flow" || fallback;`

> **Builder note — the `JitOpClass` enum edit.** `JitOpClass` lives in `CpuEmulator.Core/Jit/` (the descriptor's
> `Class` field type). Adding a `Z80Flow` value is a Core enum change consumed by `BlockCompiler`'s
> `EmitInstruction` switch (Task 2). This is the structural cost of H2. **If the owner picks H1 instead:** skip
> the enum add; map `JumpAbs`/`JumpIf` → `"Jump"`, `CallAbs`/`CallIf`/`Rst` → `"Jsr"`, `Ret`/`RetCc` → `"Rts"`,
> `RelJump`/`RelJumpIf`/`Djnz` → `"Branch"` (only for whitelisted rows), and add `TargetIsZ80` guards to the four
> 6502 arms instead. The plan's Tasks 4–5 are written for H2 (one `EmitZ80Flow` arm); H1 splits them across the
> four guarded arms but the per-op IL is identical.

- [ ] **Step 3:** Add the flow-op PC-operand footprint to `Z80EmitOperandBytes` (`BlockCompiler.cs`, DECISION
  I-footprint) so the conditional arms' not-taken fall-through `length` is correct:

```csharp
        // M6 PR-3: the control-flow family's PC-operand footprint (beyond the 1-byte opcode). An EMITTED flow
        // row ends the block, but its `length` is still threaded into the arm for the conditional not-taken
        // fall-through PC (pc + length), so the footprint must be exact. JP/CALL (absolute, 16-bit target) read
        // 2; JR/DJNZ (relative, 1-byte displacement) read 1; RET/RST/PUSH/POP read 0.
        if (IsZ80FlowKind(d))
            return d.Ops[0].Kind switch
            {
                "JumpAbs" or "JumpIf" or "CallAbs" or "CallIf" => 2,
                "RelJump" or "RelJumpIf" or "Djnz" => 1,
                _ => 0,   // Ret / RetCc / Rst (PUSH/POP ride the Register class, not here)
            };
```

  with a small `IsZ80FlowKind(d)` predicate (the flow kinds, NOT the stack kinds — PUSH/POP are
  `JitOpClass.Register` and read 0 PC operands, already covered by the `_ => 0` default):

```csharp
    private static bool IsZ80FlowKind(OpcodeDescriptor d) =>
        d.Ops.Length > 0 && d.Ops[0].Kind is "JumpAbs" or "JumpIf" or "CallAbs" or "CallIf"
            or "Ret" or "RetCc" or "RelJump" or "RelJumpIf" or "Djnz" or "Rst";
```

- [ ] **Step 4:** Regenerate; verify the flip + the no-cycle-change invariant.

```bash
dotnet build src/CpuEmulator.Cpus.Z80
```

- [ ] **Verify:** the flow rows (`0xC3` JP nn, `0xC2/0xCA/…` JP cc, `0x18` JR, `0x20/0x28/0x30/0x38` JR cc,
  `0x10` DJNZ, `0xCD` CALL, `0xC4/0xCC/…` CALL cc, `0xC9` RET, `0xC0/0xC8/…` RET cc, `0xC7/0xCF/…/0xFF` RST) and
  the stack rows (`0xC5/0xD5/0xE5/0xF5` PUSH, `0xC1/0xD1/0xE1/0xF1` POP) now carry `NeedsFallback=false` with
  `BaseCycles` UNCHANGED (JP=10, JR=12, JR cc=7, DJNZ=8, CALL=17, CALL cc=10, RET=10, RET cc=5, RST=11,
  PUSH=11, POP=10). The flow rows keep `EndsBlock=true`; the stack rows have `EndsBlock=false`. STILL fallback:
  `0xE9` (JP (HL), JumpIndirect), every DD/FD PUSH/POP, the block ops, `0xF9` (LD SP,HL).

---

## Task 2 — Route the rows (stack → EmitRegister guard; flow → the DECISION-H dispatch)

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.Emit.cs` (the `EmitRegister` Z80 guards — add the stack guard)
- Modify: `src/CpuEmulator.Jit/BlockCompiler.cs` (`EmitInstruction`'s dispatch switch — add the Z80Flow case)

- [ ] **Step 1:** Add an `IsZ80StackKind` predicate + route PUSH/POP in `EmitRegister` (beside PR-1's LD guard
  and PR-2's ALU guard):

```csharp
    private static bool IsZ80StackKind(OpcodeDescriptor d) =>
        d.Ops.Length > 0 && d.Ops[0].Kind is "Push16" or "Pop16";

    // in EmitRegister, after the LD + ALU guards:
        if (TargetIsZ80 && IsZ80StackKind(d)) { EmitZ80Stack(ctx, d); return; }   // M6 PR-3
```

- [ ] **Step 2:** Add the emittable-flow dispatch to `EmitInstruction`'s switch (DECISION H2). The flow arm
  needs `pc` (for the static target read) AND `length` (for the not-taken fall-through), like `EmitBranch`:

```csharp
            case JitOpClass.Z80Flow: EmitZ80Flow(ctx, pc, d, length); break;   // M6 PR-3 (DECISION H2)
```

> **Builder note — the dispatch switch already threads `pc` and `length`.** `EmitInstruction(ctx, pc, d, length)`
> passes `pc`/`length` to `EmitBranch` (`:371`); `EmitZ80Flow` takes the same signature. If H1 is chosen, the
> Z80 guards go at the top of `EmitJump`/`EmitJsr`/`EmitRts`/`EmitBranch` (which already receive `pc` (and
> `length` for Branch)) — confirm `EmitJump`/`EmitJsr`/`EmitRts` receive `length` if the conditional Z80 forms
> routed there need it (they do for the fall-through; H2 sidesteps this by giving `EmitZ80Flow` `length`
> directly).

---

## Task 3 — `EmitZ80Stack` (PUSH / POP — the easy, block-continuing half)

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.Z80.cs` (add `EmitZ80Stack`)

PUSH/POP ride `JitOpClass.Register` (block-continuing) — they emit inline like PR-2's ALU. Reuse PR-0's
`EmitLoadReg16`/`EmitStoreReg16` (the pair value) and the SP wide field via `EmitLoadReg16(ctx, "SP")` /
`EmitStoreReg16(ctx, "SP")`.

- [ ] **Step 1:** `EmitZ80Stack`:

```csharp
    /// <summary>M6 PR-3: PUSH rr / POP rr (Z80Stack, JitOpClass.Register — block-continuing). PUSH: SP−=1, write
    /// (byte)(pair>>8); SP−=1, write (byte)pair. POP: lo=ReadBus(SP), SP+=1; hi=ReadBus(SP), SP+=1; pair=lo|hi<<8
    /// (POP AF writes A=hi, F=lo via the AF pair-view). NO flags, NO WZ; Q=0. PUSH 11 T = fetch1 + 2 writes + 8;
    /// POP 10 T = fetch1 + 2 reads + 7. The pair is op.RegA.</summary>
    private void EmitZ80Stack(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        JitOp op = d.Ops[0];
        bool push = op.Kind == "Push16";

        if (push)
        {
            // SP -= 1; WriteBus(SP, (byte)(pair >> 8))
            EmitDecrementSp(ctx);
            EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4);          // address
            EmitLoadReg16(ctx, op.RegA); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Conv_U1);
            EmitStoreByte(ctx);                                          // charges 1; marks dirty
            // SP -= 1; WriteBus(SP, (byte)pair)
            EmitDecrementSp(ctx);
            EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4);
            EmitLoadReg16(ctx, op.RegA); il.Emit(OpCodes.Conv_U1);
            EmitStoreByte(ctx);                                          // charges 1
            EmitZ80ClearQ(ctx);
            EmitChargeCycles(ctx, 8);                                    // 11 T = fetch1 + 2 writes + 8
        }
        else // POP
        {
            // lo = ReadBus(SP) → LoLocal; SP += 1
            EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal);
            EmitIncrementSp(ctx);
            // hi = ReadBus(SP); SP += 1
            EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);   // hi (int) on stack
            il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
            il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);                   // hi<<8 | lo
            EmitStoreReg16(ctx, op.RegA);                                               // pair = value (AF → A=hi,F=lo)
            EmitIncrementSp(ctx);
            EmitZ80ClearQ(ctx);
            EmitChargeCycles(ctx, 7);                                    // 10 T = fetch1 + 2 reads + 7
        }
    }
```

- [ ] **Step 2:** The SP increment/decrement helpers (16-bit, wrapping — mirror PR-1's `EmitIncrementPC` shape
  but on the SP ushort field). If a generic 16-bit-field +/- helper is cleaner, add `EmitAddToReg16(ctx,"SP",±1)`;
  otherwise inline:

```csharp
    /// <summary>M6 PR-3: SP = (ushort)(SP ± 1). SP is a real ushort field (the PR-0 _regWideFields path).</summary>
    private void EmitDecrementSp(EmitContext ctx)
    {
        EmitLoadReg16(ctx, "SP"); ctx.Il.Emit(OpCodes.Ldc_I4_M1); ctx.Il.Emit(OpCodes.Add);
        ctx.Il.Emit(OpCodes.Ldc_I4, 0xFFFF); ctx.Il.Emit(OpCodes.And);
        EmitStoreReg16(ctx, "SP");
    }
    private void EmitIncrementSp(EmitContext ctx)
    {
        EmitLoadReg16(ctx, "SP"); ctx.Il.Emit(OpCodes.Ldc_I4_1); ctx.Il.Emit(OpCodes.Add);
        ctx.Il.Emit(OpCodes.Ldc_I4, 0xFFFF); ctx.Il.Emit(OpCodes.And);
        EmitStoreReg16(ctx, "SP");
    }
```

> **Builder note — `EmitStoreReg16(ctx, "SP")` takes the value off the stack.** Confirm its contract matches
> PR-1's `LD rr,nn` usage (int value on stack → ushort field). The `& 0xFFFF` wrap is belt-and-suspenders (the
> store does `Conv_U2`). **POP AF F-write:** `EmitStoreReg16(ctx, "AF")` writes `F = lo` (the AF pair-view's low
> half is F — `PairHalves["AF"]=("A","F")`), so POP AF loads F from the stack correctly; the TomHarte AF cases
> prove it. **SMC for PUSH:** `EmitStoreByte` already marks dirty + records the SMC page; PUSH writes go through
> it, so a PUSH onto a code page trips the intra-block SMC guard correctly (PUSH rides the Register class but
> writes RAM — confirm `EmitInstruction`'s `mayWriteRam` includes the Push16 kind, OR that the Store class
> check + the `Ops[0].Kind` check covers it; PR-1 added `StoreImm8`/`Store16` to that check — **PR-3 must add
> `Push16` to the `mayWriteRam` predicate** at `BlockCompiler.cs:357-358` so the SMC guard arms for PUSH).
>
> **SMC for CALL / RST (the block-ending stack writers).** CALL and RST also push to the stack via
> `EmitStoreByte` (which marks dirty + records the SMC page). But they END the block and self-terminate with
> `EmitChainOrExit`, whose `dirty.Any` gate (`BlockCompiler.cs:819-822`) already routes a self-modifying block
> to the dispatcher — so a CALL/RST that pushes onto its own code page is caught by the chain edge's coarse SMC
> backstop. The intra-block `EmitSmcGuard` runs only for block-CONTINUING `mayWriteRam` instructions (it returns
> from the middle of the block); the flow ops end the block, so the chain-edge `dirty.Any` gate is the correct
> backstop for their stack writes — **no `mayWriteRam` entry needed for the flow kinds** (only Push16). Confirm
> the flow arm's `EmitChainOrExit` runs (it does — Tasks 4/5), so the dirty backstop is in place.

---

## Task 4 — `EmitZ80Flow` (the unconditional forms: JP nn / JR d / CALL nn / RST / RET)

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.Z80.cs` (add `EmitZ80Flow` + its sub-emitters)

The arm switches on `op.Kind`. The unconditional forms read their operand off PC (advancing PC), apply the WZ
side-effect, set PC = target, charge the not-taken residual, then chain (static) or exit (dynamic = RET). The
STATIC target is read at COMPILE time from the bus (exactly as `EmitJsr`/`EmitJump`: `_bus.Read8((ushort)(pc+1))`).

- [ ] **Step 1:** The arm skeleton + the unconditional jumps/call/ret/rst:

```csharp
    /// <summary>M6 PR-3: the Z80 control-flow emit arm (JP/JR/CALL/RET/DJNZ/RST), the Z80 analogue of the 6502
    /// EmitJump/EmitJsr/EmitRts/EmitBranch arms. Each mirrors the generated oracle (CpuEmitter.cs:2650-2778)
    /// one-for-one: operand read via the fastmem split, the WZ side-effect (DECISION K), stack push/pop via
    /// EmitStoreByte/LoadByteFromBus + SP, PC = target, the Z80 T-state model with the taken/not-taken split
    /// (DECISION J), Q = 0. Block-ending: a STATIC target chains (EmitChainOrExit); a DYNAMIC (popped) target
    /// exits (EmitNormalExit). `pc` is the instruction's PC (for the compile-time static-target read); `length`
    /// is the walk's computed length (for the conditional not-taken fall-through).</summary>
    private void EmitZ80Flow(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length)
    {
        ILGenerator il = ctx.Il;
        JitOp op = d.Ops[0];
        switch (op.Kind)
        {
            case "JumpAbs":  EmitZ80JpAbs(ctx, pc); return;
            case "RelJump":  EmitZ80Jr(ctx, pc, length); return;
            case "CallAbs":  EmitZ80Call(ctx, pc); return;
            case "Rst":      EmitZ80Rst(ctx, d); return;
            case "Ret":      EmitZ80Ret(ctx); return;
            // conditional forms — Task 5
            case "JumpIf":   EmitZ80JpCc(ctx, pc, op); return;
            case "RelJumpIf":EmitZ80JrCc(ctx, pc, length, op); return;
            case "Djnz":     EmitZ80Djnz(ctx, pc, length, op); return;
            case "CallIf":   EmitZ80CallCc(ctx, pc, length, op); return;
            case "RetCc":    EmitZ80RetCc(ctx, pc, length, op); return;
            default:
                throw new EmulationException(
                    $"EmitZ80Flow: unhandled flow kind '{op.Kind}' (opcode=0x{d.Opcode:X2}). The whitelist "
                  + "(IsEmittableZ80Family) admitted a kind with no emit branch — keep it fallback until armed.");
        }
    }

    // JP nn — read jl,jh; PC = nn; WZ = nn; chain to the static target. 10 T = fetch1 + 2 reads + residual 7.
    private void EmitZ80JpAbs(EmitContext ctx, ushort pc)
    {
        ILGenerator il = ctx.Il;
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        // jl = ReadBus(PC); PC++; jh = ReadBus(PC); PC = jl | jh<<8
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal);  // jl; +1 cyc
        EmitIncrementPC(ctx, 1);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);                                       // jh; +1 cyc
        il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Dup); il.Emit(OpCodes.Stloc, ctx.HiLocal);     // stash the new PC for WZ
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);                                  // PC = nn   (pop the dup'd copy)
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); EmitZ80SetWZ(ctx);        // WZ = nn
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 7);                                      // 10 T = fetch1 + 2 reads + 7
        EmitChainOrExit(ctx, target);                                  // STATIC target — chainable
    }

    // JR d — read d (signed); PC = (PC after operand) + d; WZ = dest; chain. 12 T = fetch1 + 1 read + residual 10.
    private void EmitZ80Jr(EmitContext ctx, ushort pc, int length)
    {
        ILGenerator il = ctx.Il;
        sbyte off = (sbyte)_bus.Read8((ushort)(pc + 1));
        ushort target = (ushort)((pc + length) + off);                 // length == 2 (opcode + displacement)
        // d = (sbyte)ReadBus(PC); PC++   (then PC += d)
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); // d; +1 cyc
        il.Emit(OpCodes.Conv_I1); il.Emit(OpCodes.Stloc, ctx.LoLocal);   // (sbyte)d → LoLocal
        EmitIncrementPC(ctx, 1);
        // PC = (ushort)(PC + d)
        il.Emit(OpCodes.Ldarg_0); EmitLoadPC(ctx); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                            // WZ = dest (the new PC)
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 10);                                     // 12 T = fetch1 + 1 read + 10
        EmitChainOrExit(ctx, target);                                  // STATIC target — chainable
    }

    // CALL nn — read nn; push (PC after operand); PC = nn; WZ = nn; chain to the entry. 17 T.
    private void EmitZ80Call(EmitContext ctx, ushort pc)
    {
        ILGenerator il = ctx.Il;
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        // cl = ReadBus(PC); PC++; ch = ReadBus(PC); PC++   (PC now = the RETURN address)
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal);  // cl; +1
        EmitIncrementPC(ctx, 1);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.HiLocal);  // ch; +1
        EmitIncrementPC(ctx, 1);
        EmitZ80PushPc(ctx);                                            // SP-=1,write PCH; SP-=1,write PCL (2 writes)
        // PC = cl | ch<<8
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                            // WZ = nn (the new PC)
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 12);                                     // 17 T = fetch1 + 2 reads + 2 writes + 12
        EmitChainOrExit(ctx, target);                                  // STATIC call entry — chainable
    }

    // RST n — push PC (already past the 1-byte opcode); PC = vec; WZ = vec; chain. 11 T = fetch1 + 2 writes + 8.
    private void EmitZ80Rst(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        int vec = d.Opcode & 0x38;                                     // 0x00/0x08/.../0x38 — compile-time constant
        EmitZ80PushPc(ctx);                                            // SP-=1,write PCH; SP-=1,write PCL
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, vec); il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        il.Emit(OpCodes.Ldc_I4, vec); EmitZ80SetWZ(ctx);              // WZ = vec
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 8);                                      // 11 T = fetch1 + 2 writes + 8
        EmitChainOrExit(ctx, (ushort)vec);                            // STATIC vector — chainable
    }

    // RET — pop PC; WZ = popped; DYNAMIC target → exit. 10 T = fetch1 + 2 reads + residual 7.
    private void EmitZ80Ret(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitZ80PopPc(ctx);                                            // PC = pop (2 reads); leaves PC set
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                          // WZ = popped PC
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 7);                                     // 10 T = fetch1 + 2 reads + 7
        EmitNormalExit(ctx);                                         // DYNAMIC (popped) target — NOT chainable
    }
```

- [ ] **Step 2:** The shared push/pop-PC sub-emitters (the proven 6502 `EmitJsr`/`EmitRts` stack shape, on the
  Z80 SP + WZ-free):

```csharp
    /// <summary>M6 PR-3: SP-=1, WriteBus(SP,(byte)(PC>>8)); SP-=1, WriteBus(SP,(byte)PC). Two writes (each +1 cyc).
    /// Mirrors the oracle's CALL/RST push order (PCH then PCL). PC must already be the return address.</summary>
    private void EmitZ80PushPc(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitDecrementSp(ctx);
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4);
        EmitLoadPC(ctx); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Conv_U1);
        EmitStoreByte(ctx);                                          // write PCH; +1 cyc; marks dirty
        EmitDecrementSp(ctx);
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U1);
        EmitStoreByte(ctx);                                          // write PCL; +1 cyc
    }

    /// <summary>M6 PR-3: lo=ReadBus(SP),SP+=1; hi=ReadBus(SP),SP+=1; PC = lo|hi<<8. Two reads (each +1 cyc).</summary>
    private void EmitZ80PopPc(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal);
        EmitIncrementSp(ctx);
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);
        il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);
        EmitIncrementSp(ctx);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Stfld, _fpc);   // PC = popped
    }
```

> **Builder note — local reuse + EaLocal clobber.** `LoadByteFromBus`/`EmitStoreByte` clobber `ctx.EaLocal` and
> `ctx.DataLocal`; the flow arms stash through `ctx.LoLocal`/`ctx.HiLocal` (which the bus helpers never touch),
> exactly as PR-1's `EmitZ80ReadAbsEa` does. Confirm no `LoLocal`/`HiLocal` is live across a bus access that
> needs it (each arm writes before it reads). **The CALL/RST push happens AFTER PC is the return address** (the
> oracle increments PC past the operand BEFORE pushing) — the IL above advances PC, then `EmitZ80PushPc` reads
> the current (return-address) PC. Confirm the ordering matches the oracle exactly (`CpuEmitter.cs:2674-2678`
> for CALL: read ch, PC++, THEN push).

---

## Task 5 — `EmitZ80Flow` continued (the conditional forms: JP cc / JR cc / DJNZ / CALL cc / RET cc)

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.Z80.cs` (the conditional sub-emitters)

The conditional forms transcribe the 6502 `EmitBranch` two-edge structure (`BlockCompiler.Flow.cs:377-455`)
with the Z80 cycle/WZ model. The condition is `(((F >> op.FlagBit) & 1) == (op.BoolArg ? 1 : 0))` — the SAME
encoding `EmitBranch` reads (`d.Ops[0].FlagBit` / `d.Ops[0].BoolArg`).

- [ ] **Step 1:** A shared condition-test emitter (push 1 if the cc holds, else 0):

```csharp
    /// <summary>M6 PR-3: push 1 (int) if the Z80 condition code holds, else 0. cc = (((F >> bit) & 1) == sense).
    /// bit = op.FlagBit (the flag's bit position), sense = op.BoolArg (the expected bit value). Mirrors the
    /// oracle's CondExpr() (CpuEmitter.cs:2643-2648).</summary>
    private void EmitZ80Cond(EmitContext ctx, JitOp op)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _z80F!);
        il.Emit(OpCodes.Ldc_I4, op.FlagBit); il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, op.BoolArg ? 1 : 0);
        il.Emit(OpCodes.Ceq);                                        // == sense → 1/0
    }
```

- [ ] **Step 2:** JP cc,nn — WZ = nn UNCONDITIONALLY (DECISION K); if taken PC = nn; always 10 T. Two static
  edges (taken target + fall-through), both chainable:

```csharp
    private void EmitZ80JpCc(EmitContext ctx, ushort pc, JitOp op)
    {
        ILGenerator il = ctx.Il;
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        ushort fallThrough = (ushort)(pc + 3);                       // JP cc is 3 bytes
        // jl,jh read; WZ = nn UNCONDITIONALLY; both stashed for the taken PC set.
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal); // jl;+1
        EmitIncrementPC(ctx, 1);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.HiLocal); // jh;+1
        EmitIncrementPC(ctx, 1);
        // WZ = jl | jh<<8  (unconditional)
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); EmitZ80SetWZ(ctx);
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 7);                                    // 10 T = fetch1 + 2 reads + 7 (always 10)
        // if (cc) { PC = nn; chain target } else { PC already at fall-through; chain fall-through }
        Label notTaken = il.DefineLabel();
        EmitZ80Cond(ctx, op); il.Emit(OpCodes.Brfalse, notTaken);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);                               // PC = nn
        EmitChainOrExit(ctx, target);
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);                          // PC already at pc+3 (both reads advanced it)
    }
```

- [ ] **Step 3:** JR cc,d / DJNZ d — WZ = dest ONLY when taken (DECISION K); +5 taken penalty; the taken target
  is static, the fall-through static. DJNZ first does `B = (byte)(B-1)` then tests `B != 0`:

```csharp
    private void EmitZ80JrCc(EmitContext ctx, ushort pc, int length, JitOp op)
    {
        ILGenerator il = ctx.Il;
        sbyte off = (sbyte)_bus.Read8((ushort)(pc + 1));
        ushort target = (ushort)((pc + length) + off);              // length == 2
        ushort fallThrough = (ushort)(pc + length);
        // d read; PC++   (PC now at fall-through)
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Conv_I1); il.Emit(OpCodes.Stloc, ctx.LoLocal); // (sbyte)d; +1
        EmitIncrementPC(ctx, 1);
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 5);                                   // 7 T not-taken = fetch1 + 1 read + 5
        Label notTaken = il.DefineLabel();
        EmitZ80Cond(ctx, op); il.Emit(OpCodes.Brfalse, notTaken);
        // taken: PC += d; WZ = PC; +5; chain target
        il.Emit(OpCodes.Ldarg_0); EmitLoadPC(ctx); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                        // WZ = dest (taken only)
        EmitChargeCycles(ctx, 5);                                   // taken penalty 7→12
        EmitChainOrExit(ctx, target);
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);                         // PC already at pc+2
    }

    private void EmitZ80Djnz(EmitContext ctx, ushort pc, int length, JitOp op)
    {
        ILGenerator il = ctx.Il;
        sbyte off = (sbyte)_bus.Read8((ushort)(pc + 1));
        ushort target = (ushort)((pc + length) + off);
        ushort fallThrough = (ushort)(pc + length);
        // B = (byte)(B - 1)   (op.RegA == "B")
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(op.RegA));
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stfld, RegField(op.RegA));
        // d read; PC++
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Conv_I1); il.Emit(OpCodes.Stloc, ctx.LoLocal); // +1
        EmitIncrementPC(ctx, 1);
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 6);                                   // 8 T not-taken = fetch1 + 1 read + 6
        Label notTaken = il.DefineLabel();
        // if (B != 0) taken
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(op.RegA)); il.Emit(OpCodes.Brfalse, notTaken);
        il.Emit(OpCodes.Ldarg_0); EmitLoadPC(ctx); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                        // WZ = dest (taken only)
        EmitChargeCycles(ctx, 5);                                   // taken penalty 8→13
        EmitChainOrExit(ctx, target);
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);
    }
```

> **Builder note — DJNZ's `op.RegA`.** The descriptor carries `Djnz("B")` → `RegA = "B"` (the `JitOpLiteral`
> `Djnz` case is in the `regA = Quote(op.Args[0])` group, `CpuEmitter.cs:4408-4409`). Use `RegField(op.RegA)`
> for B. The B-- happens BEFORE the d read (the oracle order, `:2713-2714`) and the `B != 0` test is on the
> DECREMENTED B. DJNZ touches NO flags (Q=0) — the B-- is a plain register write, not an INC/DEC flag op.

- [ ] **Step 4:** CALL cc,nn / RET cc — WZ unconditional (CALL cc) / taken-only (RET cc); the push/pop is INSIDE
  the taken branch; the taken penalty is +5 (CALL cc) / +4 (RET cc) PLUS the inline bus accesses (DECISION J):

```csharp
    private void EmitZ80CallCc(EmitContext ctx, ushort pc, int length, JitOp op)
    {
        ILGenerator il = ctx.Il;
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        ushort fallThrough = (ushort)(pc + 3);
        // cl,ch read; PC past operand; WZ = nn UNCONDITIONALLY.
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal); // cl;+1
        EmitIncrementPC(ctx, 1);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.HiLocal); // ch;+1
        EmitIncrementPC(ctx, 1);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); EmitZ80SetWZ(ctx);   // WZ = nn (unconditional)
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 7);                                   // 10 T not-taken = fetch1 + 2 reads + 7
        Label notTaken = il.DefineLabel();
        EmitZ80Cond(ctx, op); il.Emit(OpCodes.Brfalse, notTaken);
        // taken: push PC (return addr, already past operand) — 2 writes +1 each; PC = nn; +5
        EmitZ80PushPc(ctx);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);
        EmitChargeCycles(ctx, 5);                                   // taken penalty (10→17 minus the 2 writes charged inline)
        EmitChainOrExit(ctx, target);                              // STATIC call entry
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);                         // PC at pc+3
    }

    private void EmitZ80RetCc(EmitContext ctx, ushort pc, int length, JitOp op)
    {
        ILGenerator il = ctx.Il;
        ushort fallThrough = (ushort)(pc + length);                // RET cc is 1 byte; fall-through = pc+1
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 4);                                   // 5 T not-taken = fetch1 + 0 bus + 4
        Label notTaken = il.DefineLabel();
        EmitZ80Cond(ctx, op); il.Emit(OpCodes.Brfalse, notTaken);
        // taken: pop PC — 2 reads +1 each; WZ = popped; +4; DYNAMIC target → exit
        EmitZ80PopPc(ctx);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                        // WZ = popped (taken only)
        EmitChargeCycles(ctx, 4);                                   // taken penalty (5→11 minus the 2 reads charged inline)
        EmitNormalExit(ctx);                                       // DYNAMIC popped target — NOT chainable
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);                         // not-taken fall-through IS static — chainable
    }
```

> **Builder note — the conditional-edge cycle ordering (DECISION J).** Charge the not-taken residual ALWAYS
> (outside the condition), THEN inside the taken branch do the bus accesses (each +1 via EmitStoreByte/
> LoadByteFromBus) + the reduced penalty (+5 CALL cc, +4 RET cc). This yields: CALL cc taken = 1(fetch) +
> 2(reads) + 7(not-taken residual) + 2(writes) + 5(penalty) = 17; RET cc taken = 1 + 0 + 4 + 2(reads) + 4 = 11.
> Not-taken: CALL cc = 1+2+7 = 10; RET cc = 1+0+4 = 5. **Cross-check every count against the worked table in
> DECISION J — the TomHarte cycle-parity gate catches any off-by-one.** **RET cc not-taken chains** (the
> fall-through pc+1 is static); RET cc taken exits (popped = dynamic). This split mirrors the oracle's
> conditional structure exactly.

---

## Task 6 — The regression-safety gate + FallbackEmitCount flips + descriptor assertions

**Files:**
- Modify (test): `tests/CpuEmulator.Tests/Jit/Z80JitGenericityTests.cs` (the per-form `FallbackEmitCount` flips)
- Add (test): descriptor-state assertions for the PR-3 rows

**The regression-safety argument (the tripwire / empty-diff gate).** PR-3's generator changes are
`IsEmittableZ80Family` (flips `NeedsFallback`/`EndsBlock`), the `ClassifyForJit` `Z80Flow`→`Z80Flow` remap (a
new emittable class for whitelisted rows only), and a `JitOpClass` enum value add. **None reaches a 6502/68000
row:** `IsEmittableZ80Family` is structured-CPU-only; the `Z80Flow` remap fires only for `InstructionClass.Z80Flow`
rows (Z80-only — the 6502 has no `Z80Flow`); the enum add is additive (existing values unchanged). **No
`JitBaseCycles`/`ComputeCycles`/`Z80Cycles` edit** (every PR-3 family's cycles were already correct in
`Z80Cycles`). So no committed 6502/68000 cycle number moves.

- [ ] **(a) 6502 + 68000 generated tables UNTOUCHED:** `git diff` of the regenerated `…Mos6502Cpu.g.cs` and the
  68000 table → EMPTY. Capture as the PR proof (the tripwire).
- [ ] **(b) Z80 PR-3 rows' `BaseCycles` UNCHANGED:** `git diff` the regenerated `…Z80Cpu.g.cs`: the flow/stack
  rows change `NeedsFallback`/`EndsBlock` (and the flow rows' `Class` from `Flow`→`Z80Flow`); every `BaseCycles`
  is identical (10/12/7/8/17/10/5/11/11/10). Focused tests: `DescriptorFor(0xC3).BaseCycles == 10` (JP nn),
  `DescriptorFor(0xCD).BaseCycles == 17` (CALL), `DescriptorFor(0xC9).BaseCycles == 10` (RET),
  `DescriptorFor(0x10).BaseCycles == 8` (DJNZ), `DescriptorFor(0xC5).BaseCycles == 11` (PUSH BC) — AND
  `DescriptorFor(0xC3).NeedsFallback == false`, `DescriptorFor(0xC3).EndsBlock == true` (the flow op still ends
  the block), `DescriptorFor(0xC5).EndsBlock == false` (PUSH continues).
- [ ] **(c) Untouched controls:** `DescriptorFor(0xE9).NeedsFallback == true` (JP (HL), still fallback),
  the DD/FD PUSH rows still fallback, `DescriptorFor(0xF9).NeedsFallback == true`; PR-1/PR-2 controls
  (`DescriptorFor(0x06).BaseCycles == 7`, `DescriptorFor(0x80).BaseCycles == 4`,
  `Mos6502Cpu.DescriptorFor(0xA9).BaseCycles == 2`).
- [ ] **(d) Z80 INTERPRETER TomHarte unchanged:** `Z80TomHarteTests` (interpreter, NOT JIT) still green.

- [ ] **Step 1:** The `FallbackEmitCount` flips. For BLOCK-CONTINUING ops (PUSH/POP), use the PR-2 pattern (op +
  HALT, assert `FallbackEmitCount == 1`). For BLOCK-ENDING ops (the flow ops), the op IS the block terminator —
  assert `FallbackEmitCount == 0` (the whole block emits, no fallback):

```csharp
    [Theory]
    [InlineData(new byte[] { 0xC5, 0x76 }, 1)]              // PUSH BC + HALT → 1 (the HALT)
    [InlineData(new byte[] { 0xC1, 0x76 }, 1)]              // POP BC + HALT → 1
    [InlineData(new byte[] { 0xF5, 0x76 }, 1)]              // PUSH AF + HALT → 1
    [InlineData(new byte[] { 0xC3, 0x00, 0x02 }, 0)]        // JP 0x0200 — block-ending, 0 fallbacks
    [InlineData(new byte[] { 0x18, 0xFE }, 0)]              // JR -2 — block-ending
    [InlineData(new byte[] { 0xCD, 0x00, 0x02 }, 0)]        // CALL 0x0200
    [InlineData(new byte[] { 0xC9 }, 0)]                    // RET
    [InlineData(new byte[] { 0xC2, 0x00, 0x02 }, 0)]        // JP NZ,0x0200
    [InlineData(new byte[] { 0x20, 0x05 }, 0)]              // JR NZ,+5
    [InlineData(new byte[] { 0x10, 0x05 }, 0)]              // DJNZ +5
    [InlineData(new byte[] { 0xC4, 0x00, 0x02 }, 0)]        // CALL NZ,0x0200
    [InlineData(new byte[] { 0xC0 }, 0)]                    // RET NZ
    [InlineData(new byte[] { 0xC7 }, 0)]                    // RST 0
    public void Z80_PR3_block_fallback_count(byte[] program, int expected)
    {
        var (z80, bus, opts) = NewZ80();
        for (int i = 0; i < program.Length; i++) bus.Write8((ushort)(0x0100 + i), program[i]);
        var compiler = new BlockCompiler<Z80Cpu>(z80, Z80Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);
        compiler.Compile(0x0100);
        Assert.Equal(expected, compiler.FallbackEmitCount);
    }
```

> **Builder note:** the JP/CALL targets (0x0200) must be a valid address the discovery walk can chain to (the
> compiler reads the static target at compile time via `_bus.Read8`). The block-ending forms assert 0 fallbacks
> AND prove the arm self-terminates (the block has exactly one emitted instruction that ends it). Add an
> assertion that the compiled block's discovered run length is 1 for the flow ops (they end the block
> immediately).

---

## Task 7 — The parity gate (TomHarte-through-JIT + ZEXDOC smoke) — NO benchmark

**Files:**
- Confirm: `tests/CpuEmulator.Tests/TomHarte/Z80JitTomHarteTests.cs` (the flow + stack sweep)
- Confirm: `tests/CpuEmulator.Tests/.../Z80ZexJitTests.cs` (ZEXDOC-through-JIT smoke)

- [ ] **Step 1 — TomHarte-through-JIT parity (THE load-bearing gate).** `Z80JitTomHarteTests` must match the
  interpreter byte-for-byte for every PR-3 opcode, **exercising BOTH the taken and not-taken paths of every
  conditional** (TomHarte vectors carry both flag states per cc opcode):
  - **State:** registers (incl. SP after push/pop), memory (the pushed bytes), PC (the target — static OR
    popped), **WZ** (per DECISION K: unconditional for JP/CALL/JP cc/CALL cc; taken-only for JR cc/DJNZ/RET cc),
    **Q = 0** (no flow op writes flags — verify no stray flag write), and **CycleCount** (per DECISION J: the
    taken/not-taken split — JR cc 7/12, DJNZ 8/13, CALL cc 10/17, RET cc 5/11, the unconditional JP=10/JR=12/
    CALL=17/RET=10/RST=11, PUSH=11/POP=10).
  - **Explicitly confirm:** POP AF restores F from the stack (the AF pair-write); CALL/RST push the correct
    return address (PC past the operand); the conditional cycle split is exact for both edges; DJNZ decrements B
    and tests the decremented value; RET cc not-taken does NOT pop (SP unchanged) and chains to pc+1.
  - Run: `CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80JitTomHarteTests"`.
- [ ] **Step 2 — ZEXDOC-through-JIT smoke.** `Z80ZexJitTests` (env-gated per `GatingPolicy.cs`): the SMOKE slice
  per-PR; the periodic full ZEXDOC/ZEXALL as the standing gate. **ZEX is built ENTIRELY on CALL/RET + the stack
  + conditional branches** (its test harness CALLs each test routine and RETs; the per-test loops are DJNZ/JR
  cc), so a green ZEX-through-JIT smoke is the strongest integration proof that PR-3's control-flow + stack emit
  is exact — if CALL/RET/the stack were wrong, ZEX would not even reach its first test. This is the gate that
  most validates PR-3.
- [ ] **Step 3 — NO benchmark.** Per the owner's 2026-06-18 policy, do NOT run or commit a W1/W2/W3 delta. Even
  though §6 notes PUSH/POP/JP/CALL/RET dominate Z80-W1 and DJNZ is 20% of W2 (so PR-3 is where the chaining
  payoff lands), the cumulative arc delta is captured ONCE at arc-end. **Do not add a bench/results edit.** The
  PR body may NOTE that this is the chaining-unlock PR (§3.1) for the arc-end measurement to attribute, but
  commits NO number.

> **The success bar for PR-3 is purely correctness:** TomHarte-through-JIT green for every flow + stack opcode
> on BOTH edges (state + WZ + Q + the taken/not-taken cycle split + the stack), ZEXDOC-through-JIT smoke green
> (the CALL/RET/stack integration proof), `FallbackEmitCount` drops by exactly the emitted opcodes, and the
> regression empty-diff/tripwire holds. No throughput claim.

---

## Test Plan (the fast correctness gates ONLY — NO benchmark)

**Unit:**
- `Z80_PR3_block_fallback_count` (the 13-form theory) — block-continuing PUSH/POP contribute 0 (HALT is the only
  fallback → count 1); block-ending flow ops yield count 0 (the whole single-instruction block emits). Proves
  the "FallbackEmitCount drops by exactly the emitted opcodes" gate + the gate/arm lockstep (DECISION H dispatch)
  + the self-terminating block-ending arm.
- **Descriptor-state assertions:** flow rows `NeedsFallback == false`, `EndsBlock == true`, `BaseCycles`
  unchanged (JP=10, JR=12, JR cc=7, DJNZ=8, CALL=17, CALL cc=10, RET=10, RET cc=5, RST=11); stack rows
  `NeedsFallback == false`, `EndsBlock == false`, `BaseCycles` (PUSH=11, POP=10) unchanged. Untouched controls:
  `DescriptorFor(0xE9).NeedsFallback == true` (JP (HL)), DD/FD PUSH/POP fallback, `DescriptorFor(0xF9)` fallback,
  plus PR-1/PR-2 controls.
- Build clean with `-warnaserror`; the regenerated `…Z80Cpu.g.cs` shows ONLY the whitelisted flow/stack rows
  flipped (flow rows also `Class: Flow → Z80Flow`); every other row unchanged.

**Regression-safety gate (binding — the tripwire / empty-diff):**
- **6502 / 68000 generated tables UNTOUCHED:** `git diff` of the regenerated `…Mos6502Cpu.g.cs` and the 68000
  table is EMPTY. No `JitBaseCycles`/`ComputeCycles`/`Z80Cycles` edit; the `Z80Flow` remap + the `JitOpClass`
  enum add cannot reach a non-Z80 row. Capture the empty diff as the PR proof.
- **Z80 PR-3 `BaseCycles` UNCHANGED:** the only `BaseCycles`-adjacent Z80 diff is the bool flip + the flow rows'
  `Class` relabel; every per-row `BaseCycles` integer is identical pre/post.
- **Z80 INTERPRETER TomHarte unchanged:** `Z80TomHarteTests` (interpreter, NOT JIT) still green, no cycle deltas.

**Parity gate (the binding merge precondition):**
- **TomHarte-through-JIT parity:** `Z80JitTomHarteTests` for every PR-3 opcode on BOTH edges — JIT final state
  (registers incl. SP, memory incl. pushed bytes, PC, **WZ** per DECISION K, **Q=0**, **cycles** per the
  DECISION-J taken/not-taken split) byte-identical to the interpreter. Incl. POP AF (F-restore), CALL/RST
  return-address push, DJNZ B-decrement-and-test, RET cc not-taken (no pop, chains to pc+1).
- **ZEXDOC-through-JIT smoke green:** `Z80ZexJitTests` — the SMOKE slice per-PR; the periodic full ZEXDOC/ZEXALL
  as the standing gate. ZEX's CALL/RET/stack/conditional-branch scaffolding makes a green ZEX-through-JIT the
  decisive integration proof for PR-3.
- **`FallbackEmitCount` drop:** exactly the emitted flow + stack opcodes leave the fallback set; the un-emitted
  Z80 tail (JP (HL), the block ops, the prefix planes) stays fallback honestly.

**Cycle-axis cross-check:** for every conditional form, the JIT `CycleCount` on BOTH the taken and not-taken
vectors equals the interpreter's (via the TomHarte JIT runner's full-state compare) — catches DECISION-J
off-by-ones (the inline-bus-access vs reduced-penalty split is the highest-risk arithmetic in PR-3).

**NO benchmark:** per the owner's 2026-06-18 policy, there is NO per-PR W1/W2/W3 measurement. Do not add a
bench/results edit (even though PR-3 is the chaining-unlock PR — the arc-end benchmark attributes the cumulative
delta).

---

## Dependencies

- **PR-1 (Z80 LD) + PR-2 (Z80 ALU+flags) — HARD DEPENDENCIES [both merged].** PR-3 reuses PR-2's
  `EmitZ80SetQFromF`/`EmitZ80ClearQ`/`EmitZ80SetWZ`, PR-1's `EmitLoadPC`/`EmitIncrementPC`/`LoadByteFromBus`/
  `EmitStoreByte`/`RegField`/`EmitChargeCycles`, PR-0's `EmitLoadReg16`/`EmitStoreReg16` (the SP + pair views),
  and the `IsEmittableZ80Family`/`EmitRegister`-guard mechanism. It also reuses the 6502 control-flow precedent
  (`EmitBranch`/`EmitJsr`/`EmitRts`/`EmitChainOrExit`/`EmitNormalExit`) — read `BlockCompiler.Flow.cs` first.
- **INDEPENDENT of PR-2b.** PR-2b (16-bit ALU) and PR-3 (branch/call/stack) are siblings off PR-2. Recommended
  order: PR-2b first (smaller, proves the ED lane), then PR-3 — but either order works (modulo the `CpuEmitter.cs`
  serialization rule, since both touch `IsEmittableZ80Family`).
- **CpuEmitter.cs serialization rule (ADR §4):** Tasks 1 edits `CpuEmitter.cs` (`IsEmittableZ80Family` +
  `ClassifyForJit`). Serialize against any concurrent generator-touching PR (PR-2b, PR-4's 68000 descriptor-gen).
- **Parallel-safe with:** PR-A (8086 bench, no `src/`), PR-S (6502 SMC lever, different surface).
- **Closes the Z80 for M6.** After PR-3, the Z80 emit arc is complete (LD + ALU/flags + 16-bit ALU +
  branch/call/stack = the §6 cumulative-86–100% line). The block-op/prefix-plane/exception tail stays fallback
  by design (§2). No further Z80 emit PR is planned for M6.

---

## Definition of done

- `CpuEmitter.cs` extends `IsEmittableZ80Family` to admit the flow kinds (JumpAbs/JumpIf/CallAbs/CallIf/Ret/RetCc/
  RelJump/RelJumpIf/Djnz/Rst) + the stack kinds (Push16/Pop16); remaps the emittable `Z80Flow` rows to the
  emittable dispatch class (DECISION H); the regenerated Z80 table flips ONLY these rows to `NeedsFallback=false`
  (flow rows keep `EndsBlock=true`, stack rows `EndsBlock=false`) with `BaseCycles` unchanged; JP (HL), the DD/FD
  forms, the block ops, and `LD SP,HL` stay fallback. **No `JitBaseCycles`/`ComputeCycles`/`Z80Cycles` edit.**
- `BlockCompiler.cs` dispatches the emittable Z80 flow class to `EmitZ80Flow` (DECISION H2: the `JitOpClass` enum
  add + the `EmitInstruction` case), adds the flow-op footprint to `Z80EmitOperandBytes` (DECISION I-footprint),
  and adds `Push16` to the `mayWriteRam` predicate (the PUSH SMC guard).
- `BlockCompiler.Z80.cs` carries `EmitZ80Stack` (PUSH/POP), `EmitZ80Flow` + its sub-emitters
  (`EmitZ80JpAbs`/`EmitZ80Jr`/`EmitZ80Call`/`EmitZ80Rst`/`EmitZ80Ret`/`EmitZ80JpCc`/`EmitZ80JrCc`/`EmitZ80Djnz`/
  `EmitZ80CallCc`/`EmitZ80RetCc`), and the shared sub-emitters (`EmitZ80Cond`/`EmitZ80PushPc`/`EmitZ80PopPc`/
  `EmitDecrementSp`/`EmitIncrementSp`); the `EmitRegister` carries the `IsZ80StackKind` guard; the default arms
  throw (gate/arm lockstep).
- All PR-3 `FallbackEmitCount` unit tests green (block-continuing PUSH/POP → 1; block-ending flow → 0); the
  descriptor-state assertions green (flow `EndsBlock=true`, stack `EndsBlock=false`, `BaseCycles` unchanged; JP
  (HL)/DD-FD/LD SP,HL still fallback); the 6502/68000 generated-table `git diff` is EMPTY (the tripwire); the Z80
  interpreter TomHarte sweep unchanged.
- `Z80JitTomHarteTests` for every PR-3 opcode green on BOTH the taken and not-taken edges (state incl. SP/memory/
  PC + WZ per DECISION K + Q=0 + cycles per the DECISION-J split); ZEXDOC-through-JIT smoke green (the CALL/RET/
  stack integration proof).
- **NO benchmark** committed (owner policy). No bench/results edit in this PR.
- `dotnet build -warnaserror` + the standing 6502/Z80 suites green.
- The PR body notes "PR-3 of the M6 arc (ADR 0011 §8) — Z80 branch/jump/call/stack; **completes the Z80 emit**
  (LD + ALU + 16-bit ALU + control-flow/stack = the §6 cumulative line). The chaining-unlock PR (§3.1): Z80
  blocks now span multiple instructions and chain (JP/JR/CALL/RST → static targets; RET/POP-driven → dispatcher
  exit)." It includes the empty-non-Z80-diff proof, the unchanged-`BaseCycles` proof, the DECISION-H dispatch
  choice, and the DECISION-J/K cycle+WZ correctness notes. It states there is NO benchmark gate (owner policy)
  and that the arc-end benchmark attributes the cumulative chaining delta.

---

## Design decisions surfaced for the owner (Coordinator) — confirm before / during implementation

- **DECISION H — the emittable Z80 control-flow dispatch home (RECOMMENDED: H2 — a dedicated `EmitZ80Flow`
  arm).** The flow ops ride `JitOpClass.Flow`, which `EmitInstruction` routes to the `default` throw (no
  emittable `Flow` arm). **H1:** remap the Z80 flow kinds to the existing emittable 6502 classes (`Jump`/`Jsr`/
  `Rts`/`Branch`) and guard each 6502 arm with `TargetIsZ80`. **H2:** add an emittable `JitOpClass.Z80Flow` value
  + an `EmitInstruction` case + a single `EmitZ80Flow` arm switched on op-kind. **Recommendation: H2** — the Z80
  control-flow family does NOT line up with the 6502 classes (conditional-absolute JP cc/CALL cc have no 6502
  analogue; the cycle/WZ/taken-penalty model is entirely Z80), so H1 buys only dispatch plumbing while every arm
  body is Z80-specific; H2 keeps all Z80 flow logic in one arm (the `EmitZ80Alu` pattern) and leaves the 6502
  arms untouched (zero 6502 regression surface). **Cost of H2:** a `JitOpClass` Core enum value + one
  `EmitInstruction` case. **Owner's call: H1 (guard the four 6502 arms) or H2 (dedicated Z80 flow arm —
  recommended)?**
- **DECISION I — `EndsBlock`/chaining composition (RESOLVED: static→chain, dynamic→exit; the proven 6502
  pattern).** Every flow op keeps `EndsBlock=true` and self-terminates. STATIC targets (JP/JR/CALL/RST + every
  conditional NOT-taken fall-through + RET cc not-taken) → `EmitChainOrExit` (chainable, compile-time-constant
  target read from the bus). DYNAMIC targets (RET / RET cc taken — popped from the stack) → `EmitNormalExit`. The
  one judgement call folded in: **RST n chains to its static vector** (recommended — a fixed-vector RST handler
  is a hot chain edge). The one mechanical edit: the flow-op operand footprint in `Z80EmitOperandBytes` so the
  conditional not-taken fall-through `length` is exact. **No owner decision needed — surfaced for implementer
  correctness; confirm the RST-chains recommendation.**
- **DECISION J — the taken/not-taken cycle emission (RESOLVED: not-taken residual always + reduced taken penalty
  with inline bus accesses).** The descriptor `BaseCycles` is the NOT-TAKEN base; the arm charges the not-taken
  residual unconditionally, then INSIDE the taken branch charges the bus accesses (each +1 via EmitStoreByte/
  LoadByteFromBus) + the reduced penalty (+5 CALL cc, +4 RET cc, +5 JR cc/DJNZ) — exactly mirroring the oracle
  (the oracle's penalty is "minus the N bus accesses charged inline"). The DECISION-J worked table pins every
  edge's count. **No owner decision — the plan surfaces the exact arithmetic; the TomHarte cycle gate proves it
  on both edges.**
- **DECISION K — the WZ/MEMPTR placement (RESOLVED: per-op, conditional-aware).** JP/CALL (and JP cc/CALL cc) set
  WZ UNCONDITIONALLY (the 16-bit operand is always fetched); JR cc/DJNZ/RET cc set WZ ONLY when taken; JR/RET/RST
  set WZ unconditionally; PUSH/POP touch no WZ. The arm places `EmitZ80SetWZ` outside vs inside the taken branch
  accordingly (mirroring the oracle's `EmitWz` vs `EmitWzIndented`). **No owner decision — surfaced for
  implementer correctness; the TomHarte WZ check is the gate.**
- **Scope (RESOLVED in this plan): JP (HL) stays fallback.** `JumpIndirect` (JP (HL), 0xE9) has a DYNAMIC target
  (PC = HL, register-sourced, not a code-stream constant). It is rare and would need an `EmitNormalExit`-only arm
  with no chaining payoff. Per §2 it stays fallback (like the 6502 JMP-indirect's dynamic-target handling). **If
  the owner wants JP (HL) emitted** (it is cheap — PC = HL, no bus, exit to dispatcher), it can fold in; the plan
  recommends leaving it fallback to keep PR-3 focused on the chainable hot path.
