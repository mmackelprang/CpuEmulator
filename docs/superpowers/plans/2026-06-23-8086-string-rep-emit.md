# Plan — Row STR: 8086 string/REP JIT emit

> **Spec:** `docs/superpowers/specs/2026-06-23-8086-muldiv-string-int-emit-design.md` (§3 Row STR).
> **Branch:** `feat/m8086-string-emit`. **One PR.** Priority 2 of ROADMAP #4. No row dependency
> (un-forces a disjoint opcode set; routes via a new dispatch predicate).
> **Grounded against `main` @ `4b46da2`.** TDD throughout.

## Scope

MOVS (`A4`/`A5`), CMPS (`A6`/`A7`), STOS (`AA`/`AB`), LODS (`AC`/`AD`), SCAS (`AE`/`AF`), byte+word,
with/without a REP prefix (`F3` REP/REPE, `F2` REPNE). Transcribes `StringExecute` + `StringStep`
(`src/CpuEmulator.Cpus.M8086/M8086Cpu.String.cs:38/29`) one-for-one. The string ops do NOT change CS and
the REP loop terminates within the single op's emission → **straight-line, block-continuing**
(DECISION STR-1; no endsBlock re-force).

## The shipped scaffolding this reuses (verified file:line)

- The offset-wrap word read/write over a survivor (seg, offset) pair: `EmitM8086PushPhysical(ctx,
  offsetPlusOne)` (`BlockCompiler.M8086.cs:502`) + `LoadByteFromBus`/`EmitStoreByte`. The string EA is
  exactly this shape over the RUNTIME SI/DI offset (not a compile-time disp).
- `EmitM8086SubFlags(ctx, width16)` (`:139`) — the CMPS/SCAS flag set (compare = SUB, flags only).
- `EmitLoadReg16`/`EmitStoreReg16`, `RegField("AL"|"AX")`, the `_m8086FLAGS!` field.
- The decode preamble (scan prefixes, capture `r.X86.SegOverride` + `r.X86.RepPrefix`): the
  `EmitM8086Alu` preamble (`:585`). The rep/override prefixes are compile-time constants from decode.
- `M8086FlagDF` / `M8086FlagZF` — DF is `1 << 10` (the M8086Spec `D` member; verify against the spec
  layout — the existing arm has `M8086FlagZF = 1 << 6`, `:38`; add `M8086FlagDF`).
- The discriminator-counter pattern (`BlockCompiler.cs:60-83`) + the Register-case routing (`:670-705`).
- `r.X86` fields: `SegOverride`, `RepPrefix` (`src/CpuEmulator.Core/Jit/DecodeResult.cs:36`).

> **Note on the override:** the string SOURCE is `DS:SI` (DS override-replaced via the captured prefix);
> the DESTINATION is `ES:DI` (ES is NON-overridable — String.cs:42-44). So the arm threads `over` into
> the source segment only, and uses the literal ES register for the destination, always.

---

## Task 1 — DF flag const + the string opcode predicate + the discriminator counter (red: routing)

**Test first** — `tests/CpuEmulator.Tests/Jit/M8086StringEmitTests.cs` (new), the JIT-vs-interpreter
harness (identical shape to `M8086MulDivEmitTests.RunBoth` — copy it, rename):

```csharp
[Fact]
public void Movsb_forward_emits_and_is_not_a_fallback()
{
    if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
    // A4 MOVSB: ES:DI <- DS:SI, then SI++/DI++ (DF=0). Seed DS,ES,SI,DI + a source byte.
    var (jit, interp) = RunBoth([0xA4], out int emit, out int fb,
        ("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020),
        seedMem: (((0x2000 << 4) + 0x0010), 0x5A));   // DS:SI = 0x5A
    Assert.True(emit > 0, "MOVSB was not emitted (the string arm never dispatched).");
    Assert.Equal(0, fb);
    Assert.Equal(interp.GetRegister("SI"), jit.GetRegister("SI"));   // SI++ (DF=0)
    Assert.Equal(interp.GetRegister("DI"), jit.GetRegister("DI"));   // DI++
    // the copied byte at ES:DI is byte-identical (RunBoth memcmp's the touched cells).
}
```

(`RunBoth` gains a `seedMem` param: a `(physAddr, value)` to seed in both buses before the run, and the
assertion memcmp's a small window around ES:DI.)

**Then** the DF const (`BlockCompiler.M8086.cs`, beside `M8086FlagZF` at `:38`):

```csharp
private const int M8086FlagDF = 1 << 10;   // direction (the spec's `D` — DF=0 increment, DF=1 decrement)
```

The counter (`BlockCompiler.cs:60-83`):

```csharp
/// <summary>Row STR: how many times an 8086 string row (MOVS/CMPS/STOS/LODS/SCAS, A4-A7/AA-AF, with or
/// without a REP prefix) was DISPATCHED to <see cref="EmitM8086String"/> and EMITTED (the non-vacuity
/// probe — asserted > 0 in the gate; the discriminator the parity sweep, which already passes via
/// fallback, cannot false-pass on).</summary>
public int M8086StringEmitSelections { get; private set; }
```

The predicate (`BlockCompiler.M8086.cs`):

```csharp
/// <summary>Row STR: is this an in-scope 8086 string row? The MOVS/CMPS/STOS/LODS/SCAS opcodes A4-A7,
/// AA-AF (each byte+word). d.Opcode is the byte; the REP prefix (if any) rides r.X86.RepPrefix, captured
/// in the arm. Mirrors IsM8086FlowOpcode (the by-opcode discriminator).</summary>
private static bool IsM8086StringOpcode(OpcodeDescriptor d) => d.Opcode is
    0xA4 or 0xA5 or 0xA6 or 0xA7 or 0xAA or 0xAB or 0xAC or 0xAD or 0xAE or 0xAF;
```

Route it in the Register case (`BlockCompiler.cs`, after the ALU/MUL-DIV checks, before far-flow). The
string ops need `r` (for `RepPrefix`) — the dispatch already has `r` in scope (it is the `DecodeResult`
the loop holds; the existing arms take `x86Seg = r.X86.SegOverride` at `:453`). Pass the rep prefix
similarly:

```csharp
if (TargetIsM8086 && IsM8086StringOpcode(d))
{
    M8086StringEmitSelections++;     // Row STR: the dead-arm-now-live probe (asserted > 0 in the gate)
    EmitM8086String(ctx, pc, d, length, r.X86.SegOverride, r.X86.RepPrefix);   // Row STR (DECISION STR-1/STR-2)
    break;
}
```

> **TDD-shaping note (bounded, mine):** confirm `r` is in scope at the dispatch site (the existing
> `x86Seg = TargetIsM8086 ? r.X86.SegOverride : (byte)0` at `BlockCompiler.cs:453` proves `r` is
> available in `EmitInstruction`). If `EmitInstruction`'s signature does not currently thread `r`'s
> `RepPrefix`, add it the same way `x86Seg` is threaded (a local captured before the `switch (d.Class)`).
> The Builder confirms the exact threading; the routing test gates it.

Red: `EmitM8086String` does not exist.

---

## Task 2 — the single-iteration body (non-REP), MOVS/STOS/LODS first

**Test first** — MOVSB/MOVSW forward + backward (DF=0, DF=1), STOSB, LODSB, each JIT==interpreter.

**Then** `EmitM8086String` with the decode preamble + a per-opcode single-iteration body, plus the
SI/DI step. Start with the unconditional ops (MOVS/STOS/LODS); add CMPS/SCAS in Task 3, REP in Task 4:

```csharp
/// <summary>Row STR: emit one 8086 string instruction (optionally REP-prefixed). Decodes the rep/override
/// prefixes at emit time (compile-time constants). Resolves the source as DS:SI (DS override-replaced) and
/// the destination as ES:DI (ES NON-overridable). The single-iteration body (EmitStringBody) + the SI/DI
/// step (EmitStringStep) mirror StringExecute.DoOnce + StringStep (String.cs:51/29). A REP prefix wraps
/// the body in a runtime CX-loop (EmitStringRepLoop, Task 4). The string ops do NOT change CS and the loop
/// terminates within the op → straight-line: advance IP by length-1 (DECISION STR-1, the MOV/ALU tail).</summary>
private void EmitM8086String(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg, byte repPrefix)
{
    M8086Cpu_Override over = M8086OverrideFromByte(x86Seg);
    byte opcode = d.Opcode;
    bool word = (opcode & 1) != 0;            // odd opcodes are the word forms (A5/A7/AB/AD/AF)
    bool isCompare = opcode is 0xA6 or 0xA7 or 0xAE or 0xAF;   // CMPS/SCAS — the ZF-conditioned repeat

    if (repPrefix == 0)
    {
        EmitStringBody(ctx, opcode, word, over);   // one iteration; CX untouched
    }
    else
    {
        EmitStringRepLoop(ctx, opcode, word, isCompare, repPrefix, over);   // Task 4
    }

    int tail = length - 1;                    // DECISION STR-1: straight-line tail (the MOV/ALU discipline)
    if (tail > 0) EmitIncrementPC(ctx, tail);
}
```

`EmitStringBody` — the per-opcode iteration, transcribing `DoOnce` (String.cs:51). The source/dest EAs
use the survivor-pair physical over the live SI/DI:

```csharp
/// <summary>Row STR: emit ONE string iteration for `opcode` (StringExecute.DoOnce, String.cs:51), then
/// step SI/DI (EmitStringStep). Source = DS:SI (DS override-replaced); dest = ES:DI (ES non-overridable).
/// For the compare ops (A6/A7/AE/AF) the ZF the SubFlags sets is the loop's early-exit signal (read by
/// EmitStringRepLoop after this returns) — the body itself just sets flags.</summary>
private void EmitStringBody(EmitContext ctx, byte opcode, bool word, M8086Cpu_Override over)
{
    ILGenerator il = ctx.Il;
    string srcSeg = M8086SegName(over, "DS");   // DS default, override-replaced (the source segment)
    switch (opcode)
    {
        case 0xA4: case 0xA5:   // MOVS: ES:DI <- DS:SI
            EmitStringLoad(ctx, srcSeg, "SI", word);    // push src value (DS:SI) -> stack
            EmitStringStore(ctx, "ES", "DI", word);     // store the stack value to ES:DI
            EmitStringStep(ctx, word, stepSi: true, stepDi: true);
            break;
        case 0xAA: case 0xAB:   // STOS: ES:DI <- AL/AX
            if (!word) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("AL")); }
            else EmitLoadReg16(ctx, "AX");
            EmitStringStoreValueOnStack(ctx, "ES", "DI", word);   // store the AL/AX value to ES:DI
            EmitStringStep(ctx, word, stepSi: false, stepDi: true);
            break;
        case 0xAC: case 0xAD:   // LODS: AL/AX <- DS:SI
            EmitStringLoad(ctx, srcSeg, "SI", word);
            if (!word) { il.Emit(OpCodes.Conv_U1); /* store AL */ il.Emit(OpCodes.Stloc, ctx.DataLocal);
                         il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Stfld, RegField("AL")); }
            else EmitStoreReg16(ctx, "AX");
            EmitStringStep(ctx, word, stepSi: true, stepDi: false);
            break;
        case 0xA6: case 0xA7:   // CMPS: compare DS:SI - ES:DI (flags only) — Task 3
        case 0xAE: case 0xAF:   // SCAS: compare AL/AX - ES:DI (flags only) — Task 3
            EmitStringCompareBody(ctx, opcode, word, srcSeg);
            break;
    }
}
```

> **TDD-shaping note (bounded, mine):** `EmitStringLoad(ctx, seg, indexReg, word)` resolves the survivor
> pair `M8086SegLocal = Reg16(seg)`, `M8086OffsetLocal = Reg16(indexReg)`, then reads byte (one
> `EmitM8086PushPhysical(false) + LoadByteFromBus`) or word (the offset-wrap two-byte read, `:502-509`
> shape) — leaving the value on the stack. `EmitStringStore`/`EmitStringStoreValueOnStack` are the
> offset-wrap store mirror (the `EmitM8086StoreWordEa` survivor shape, `:483`, staging the value through
> `AddrLocal`). These three small helpers are the only new operand machinery; they are the existing
> survivor-pair shapes with the index register (SI/DI) as the runtime offset instead of a compile-time
> disp. The Builder factors them; the per-direction MOVS test is the oracle.

`EmitStringStep` — transcribes `StringStep` (String.cs:29): delta = `(FLAGS & DF) ? -(word?2:1) :
(word?2:1)`; add to SI and/or DI with 16-bit wrap:

```csharp
/// <summary>Row STR: step SI and/or DI by the DF-directed delta (StringStep, String.cs:29). ±1 byte / ±2
/// word; DF=0 ⇒ +, DF=1 ⇒ -. Reads FLAGS&DF at runtime to pick the sign; the step amount + which index
/// advances are compile-time constants.</summary>
private void EmitStringStep(EmitContext ctx, bool word, bool stepSi, bool stepDi)
{
    ILGenerator il = ctx.Il;
    int amt = word ? 2 : 1;
    // delta = (FLAGS & DF) != 0 ? -amt : amt  -> DataLocal
    Label neg = il.DefineLabel(), have = il.DefineLabel();
    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagDF); il.Emit(OpCodes.And);
    il.Emit(OpCodes.Brtrue, neg);
    il.Emit(OpCodes.Ldc_I4, amt); il.Emit(OpCodes.Br, have);
    il.MarkLabel(neg); il.Emit(OpCodes.Ldc_I4, -amt);
    il.MarkLabel(have); il.Emit(OpCodes.Stloc, ctx.DataLocal);   // delta
    if (stepSi)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("SI")); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, RegField("SI"));
    }
    if (stepDi)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("DI")); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, RegField("DI"));
    }
}
```

> **TDD-shaping note (bounded, mine):** `EmitStringStep` clobbers `DataLocal` — so a body that still needs
> `DataLocal` (e.g. LODSB stashing AL through it) must step AFTER it is done with DataLocal. The MOVS/STOS
> bodies finish their memory access before the step; LODSB stores AL before stepping. The Builder orders
> the step last in each body (matching `DoOnce`, which steps after the access). The per-op test catches a
> clobber-ordering bug.

Run MOVS/STOS/LODS tests (both DF directions) green.

---

## Task 3 — CMPS/SCAS (the compare ops, flags-only)

**Test first** — CMPSB (equal → ZF=1; unequal → ZF=0, with the SUB-form CF/AF/OF/SF/PF byte-identical),
SCASW, each JIT==interpreter on FLAGS + SI/DI.

**Then** `EmitStringCompareBody` — transcribes the CMPS/SCAS cases (String.cs:69-122). CMPS compares
`DS:SI - ES:DI`; SCAS compares `AL/AX - ES:DI`. Reuse `EmitM8086SubFlags`:

```csharp
/// <summary>Row STR: the compare ops (CMPS A6/A7, SCAS AE/AF) — flags-only SubFlags (String.cs:69/109).
/// CMPS: a = DS:SI, b = ES:DI. SCAS: a = AL/AX, b = ES:DI. Sets flags exactly like CMP (EmitM8086SubFlags,
/// borrow-in 0), discards the result. Steps SI+DI for CMPS, DI only for SCAS. Leaves ZF in FLAGS for the
/// REP-loop early-exit read.</summary>
private void EmitStringCompareBody(EmitContext ctx, byte opcode, bool word, string srcSeg)
{
    ILGenerator il = ctx.Il;
    bool isScas = opcode is 0xAE or 0xAF;
    // a -> M8086ALocal
    if (isScas) { if (!word) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("AL")); } else EmitLoadReg16(ctx, "AX"); }
    else EmitStringLoad(ctx, srcSeg, "SI", word);   // DS:SI value on stack
    il.Emit(OpCodes.Stloc, ctx.M8086ALocal);
    // b = ES:DI -> M8086BLocal
    EmitStringLoad(ctx, "ES", "DI", word);
    il.Emit(OpCodes.Stloc, ctx.M8086BLocal);
    il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, ctx.M8086CarryInLocal);   // borrow-in 0
    EmitM8086SubFlags(ctx, word);   // sets CF/AF/OF/SF/ZF/PF; leaves result on stack
    il.Emit(OpCodes.Pop);           // compare discards the result
    EmitStringStep(ctx, word, stepSi: !isScas, stepDi: true);   // CMPS steps both; SCAS steps DI only
}
```

Run CMPS/SCAS tests green (non-REP).

---

## Task 4 — the REP/REPE/REPNE runtime loop (the one genuinely-new IL shape)

**Test first** — the load-bearing cases (transcribing String.cs:128-143):
- `Rep_movsb_copies_cx_bytes` — REP MOVSB, CX=4 → 4 bytes copied, CX=0, SI/DI advanced by 4.
- `Repe_cmpsb_stops_on_first_mismatch` — REPE CMPSB over a 4-byte run where byte 2 differs → CX stops at
  the exact interpreter value (not 0), SI/DI at the interpreter values, ZF=0.
- `Repne_scasw_stops_on_match` — REPNE SCASW stops when ZF=1.
- `Rep_with_cx_zero_does_nothing` — REP MOVSB, CX=0 → zero iterations, no register/memory change.

**Then** `EmitStringRepLoop` — the `while (CX != 0) { CX--; body; if (isCompare && zf != repWhileZfSet)
break; }` IL (String.cs:128-143). `repWhileZfSet = (repPrefix == 0xF3)` is a compile-time constant:

```csharp
/// <summary>Row STR: the REP/REPE/REPNE CX-loop (StringExecute REP path, String.cs:128-143). Emits a
/// runtime back-edge: while (CX != 0) { CX--; EmitStringBody; if (isCompare && zf != repWhileZfSet) break; }.
/// repWhileZfSet = (repPrefix == 0xF3) [REPE: repeat while ZF=1; REPNE F2: repeat while ZF=0] — a compile-
/// time constant. With CX==0 going in, zero iterations (the condition-first loop). For non-compare ops the
/// ZF check is omitted (the loop runs CX times unconditionally). This is the only new IL shape in #4 (an
/// in-op back-edge — the Z80 LDIR precedent, here the named #4 string deliverable).</summary>
private void EmitStringRepLoop(EmitContext ctx, byte opcode, bool word, bool isCompare, byte repPrefix, M8086Cpu_Override over)
{
    ILGenerator il = ctx.Il;
    bool repWhileZfSet = repPrefix == 0xF3;
    Label loopTop = il.DefineLabel(), loopExit = il.DefineLabel();

    il.MarkLabel(loopTop);
    // if (CX == 0) goto exit;
    EmitLoadReg16(ctx, "CX"); il.Emit(OpCodes.Brfalse, loopExit);
    // CX = (ushort)(CX - 1);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("CX")); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub);
    il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, RegField("CX"));
    // the iteration body (sets ZF for the compare ops)
    EmitStringBody(ctx, opcode, word, over);
    if (isCompare)
    {
        // if ((FLAGS & ZF) != 0) != repWhileZfSet ⇒ break.  Equivalent: zf = (FLAGS&ZF)!=0;
        // continue iff zf == repWhileZfSet; else goto exit.
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _m8086FLAGS!); il.Emit(OpCodes.Ldc_I4, M8086FlagZF); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un);   // zf 0/1
        il.Emit(OpCodes.Ldc_I4, repWhileZfSet ? 1 : 0);
        il.Emit(OpCodes.Bne_Un, loopExit);   // zf != repWhileZfSet ⇒ stop
    }
    il.Emit(OpCodes.Br, loopTop);
    il.MarkLabel(loopExit);
}
```

> **TDD-shaping note (bounded, mine):** the loop body re-resolves the source/dest EA from the LIVE SI/DI
> each iteration (DECISION STR-2) — which `EmitStringBody` already does (it reads `Reg16("SI")`/`"DI"`
> fresh each call). So no hoisting; the per-iteration EA is correct by construction. The compare early-
> exit semantics (String.cs:141: `if (isCompare && zf != repWhileZfSet) break;`) stop AFTER the
> iteration's compare+step — matching the IL above (the body, including the step, runs before the ZF
> check). The `Repe_cmpsb_stops_on_first_mismatch` test pins this exact boundary.

Run all four REP tests green.

---

## Task 5 — un-force the gate (the headline parity flip)

`CpuEmitter.cs` — `IsEmittableX86Family` (`:5080`). The string mnemonics (MOVS/CMPS/STOS/LODS/SCAS) are
8086-unique, but add the explicit `isX86` self-gate consistent with the ALU comment (`:5082-5088`). Add a
clause:

```csharp
// Row STR (ROADMAP #4): the string family (MOVS/CMPS/STOS/LODS/SCAS, A4-A7/AA-AF, byte+word, REP-prefixed
// or not) now EMITS (the EmitM8086String arm — the CX-loop + DF-direction + REPE/REPNE ZF early-exit). The
// string ops do NOT change CS and the REP loop terminates in-op → straight-line (NO endsBlock re-force,
// unlike DIV/IDIV/flow). Admitted by mnemonic (8086-unique). The REP prefix rides r.X86.RepPrefix; the
// row itself is the string opcode (the prefix is consumed into the instruction footprint by the decoder).
if (insn.Mnemonic is "MOVS" or "CMPS" or "STOS" or "LODS" or "SCAS"
    or "MOVSB" or "MOVSW" or "CMPSB" or "CMPSW" or "STOSB" or "STOSW"
    or "LODSB" or "LODSW" or "SCASB" or "SCASW")
    return true;
```

> **TDD-shaping note (bounded, mine):** the generated `M8086Cpu.g.cs` may name these rows by the
> byte-suffixed mnemonic (`MOVSB`/`MOVSW`) OR the base (`MOVS`) — the clause above admits BOTH spellings
> so it is robust to the generator's choice. The Builder greps the generated table to confirm the actual
> mnemonic spelling and trims the clause to the real set (a 5-minute check; the routing predicate
> `IsM8086StringOpcode` keys on the OPCODE byte, not the mnemonic, so the arm dispatch is unaffected
> either way — only the gate's mnemonic list needs to match). **No endsBlock re-force for these rows**
> (DECISION STR-1) — the `:4909` re-force is NOT extended for string.

Run the full `M8086StringEmitTests` — green, `FallbackEmitCount == 0` for a REP MOVS block and a REPE
CMPS block.

---

## Task 6 — the headline parity gate (M8088JitTom, now emitting) + stale-comment fix

The `M8088JitTom` sweep already runs the A4-A7/AA-AF files through the JIT (passing via fallback today;
via emitted IL after this PR — the same sweep is the gate). The harness comment at `:92-97` lists "the
string/control/stack ops not yet emitted" as still-fallback; **correct it** to note the string family now
emits:

```csharp
// Row STR (ROADMAP #4): the string family (A4-A7/AA-AF, REP-prefixed or not) now EMITS through the JIT —
// so the A4-AF files in this sweep now prove genuine EMIT parity (the CX-loop + DF-direction + REPE/REPNE
// ZF early-exit), not fallback-passthrough. The remaining still-fallback ops are the control/stack tail
// not in #4's scope.
```

**Run the headline gate locally:** `CPUEMULATOR_UAT=full dotnet test` over the A4-A7/AA-AF partitions —
byte-identical through the JIT, executed > 0. Add a focused discriminator pin: a REP MOVS block has
`FallbackEmitCount == 0` (the emits-not-fallback proof, red→green vs the pre-PR fallback).

---

## Self-review (run before opening the PR)

- **Spec coverage:** MOVS/CMPS/STOS/LODS/SCAS byte+word (Task 2/3); REP/REPE/REPNE incl. the ZF early-exit
  + CX=0 zero-iteration (Task 4); the DF direction both ways (Task 2); STR-1 straight-line (no endsBlock
  re-force); STR-2 per-iteration EA re-resolution. ES non-overridable / DS overridable honored (Task 2).
- **Placeholders:** the TDD-shaping notes (the three operand helpers `EmitStringLoad`/`Store`/
  `StoreValueOnStack`, the DataLocal clobber ordering, the gate mnemonic spelling) are bounded, mine,
  each naming the oracle lines + the gating test — no `TBD`.
- **Type consistency:** all reads/writes via the shipped survivor-pair physical + `RegField`/`Reg16`
  helpers; `Conv_U2`/`Conv_U1` width-masks; `_m8086FLAGS!`; `EmitM8086SubFlags` reused unchanged for
  CMPS/SCAS.
- **AOT-clean Core:** all IL in `CpuEmulator.Jit`; one generator gate-flip (no endsBlock change);
  interpreter oracle unchanged.

## The un-fakeable gate (summary)

- **Parity:** the A4-A7/AA-AF files in `M8088JitTom` byte-identical through the JIT to the interpreter —
  now via emitted IL incl. the REP-prefixed cases.
- **Emits-not-fallback discriminator:** `M8086StringEmitSelections > 0` over a string block AND
  `FallbackEmitCount == 0` for a REP MOVS block and a REPE CMPS block — proven red→green.
