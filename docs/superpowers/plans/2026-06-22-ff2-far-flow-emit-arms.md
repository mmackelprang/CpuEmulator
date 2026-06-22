# FF-2 — The far `JMP`/`CALL`/`RET` emit arms + the aliasing regression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **⛔ HARD DEPENDENCY — DO NOT START UNTIL FF-1 IS MERGED.** FF-2 depends on **FF-1** (the `(CS<<4)+IP` linear block key + `IJitTarget.ProjectBlockKey`). The far-flow arms are **unsound** until the key is widened: the aliasing bug only arms once an *emitted* op changes CS mid-chain, so FF-1 must land and pass its byte-for-byte identity gate **before** any far arm is emitted. **FF-1 and FF-2 MUST NOT be co-merged in one PR** (ADR 0019 §5 sequencing). Confirm `IJitTarget.ProjectBlockKey` exists, `CompiledBlock.EntryPc`/the cache key are `uint`, and the FF-1 identity regression is green on `main` before starting.

**Goal:** Emit the regular far transfers — far `JMP` (`EA`/`FF /5`), far `CALL` (`9A`/`FF /3`), and far `RET` (`CB`/`CA`) — through the 8086 JIT, byte-identical to the interpreter; keep `INT`/`INTO`/`IRET`/`BOUND` on the interpreter fallback (ADR 0019 Decision 3). The far arms write the CS field (in addition to IP) before exiting, so the next dispatch's `ProjectBlockKey` keys/decodes the successor under the new segment.

**Architecture:** A new `EmitM8086FarFlow` arm in `BlockCompiler.M8086.cs`, dispatched from `EmitInstruction` via a new `IsM8086FarFlowOpcode(d)` gate (mirroring `IsM8086FlowOpcode`, `BlockCompiler.cs:287`). It reuses the shipped stack machinery — `EmitM8086PushWord` (`:982`) twice (push CS then IP) for far CALL, `EmitM8086PopWord` (`:1002`) twice (pop IP then CS) for far RET — plus one new `EmitM8086SetCs` helper writing the `_m8086CS` field (mirroring `EmitM8086SetIp`, `:1245`). The direct forms (`9A`/`EA`) have a compile-time-constant `(newCS, newIP)`, so they chain through `EmitChainOrExit` to the **projected linear key** `((newCS<<4)+newIP)&0xFFFFF` (the FF-1 payoff). The indirect (`FF /3`/`FF /5`) and the far-RET (`CB`/`CA`) forms are dynamic → `EmitNormalExit`. The generation gate is un-forced for the far family in `CpuEmitter` (ADR 0011 drift #1).

**Tech Stack:** C# / .NET, xUnit v2.9.3, `System.Reflection.Emit` IL-JIT (`CpuEmulator.Jit`), the Roslyn `CpuEmitter` generator, the TomHarte 8088 corpus runner (`tests/CpuEmulator.Tests/TomHarte/`).

## Global Constraints

- **Interpreter-as-oracle.** Every far-emitted block is byte-identical to the interpreter (registers + FLAGS + **CS:IP** + the far stack frame + memory + cycles) through the TomHarte 8088 corpus (ADR 0011 §5 / ADR 0019 Decision 4 gate 1). A far op that is not byte-identical does not ship.
- **The far arms set NO flags.** Far `JMP`/`CALL`/`RET`, like near, touch no FLAGS (ADR 0019 §4). `INT`/`IRET`, which do touch flags + vector through the IVT, stay fallback.
- **FALLBACK stays fallback (ADR 0019 Decision 3).** `CD`/`CC`/`CE`/`CF` (`INT`/`INT3`/`INTO`/`IRET`), the divide-error `INT 0`, and `BOUND` (`62`/`63`) remain on the interpreter. FF-2 does **not** emit them.
- **The aliasing regression is the load-bearing un-fakeable gate.** Two segments with the SAME offset must compile to DISTINCT blocks — **FAILS on the old `(IP)` key, PASSES on the linear key** (ADR 0019 Decision 4 gate 2). It is observable via a segment-distinguishing side effect.
- **The non-segmented CPUs + the 8086 near flow stay byte-for-byte unchanged.** FF-2 only adds far arms + un-forces the far generation gate; the FF-1 identity gate and the existing near-flow parity remain green.
- **AOT-clean.** `EmitM8086SetCs` uses the JIT-internal `_m8086CS` `FieldInfo` handle (already resolved at `BlockCompiler.cs:261`) — `Reflection.Emit` in the JIT tier, no Core dependency.
- **Honesty gate.** A measured 8086 throughput delta on a far-call-bearing workload against the frozen M6 constants (the 8086 measurement apparatus shipped in ADR 0011 PR-A).
- **Branch + PR discipline.** All work on `feat/ff2-far-flow-emit`; one reviewable PR; merge on green gates per the auto-merge policy.
- **Splittable.** If the far-indirect `FF /3`/`FF /5` EA-resolution proves heavy, ship FF-2a (direct `9A`/`EA`/`CB`/`CA` — chainable + the aliasing gate) then FF-2b (indirect `FF /3`/`FF /5`). The fallback valve keeps a partial far family correct. This plan covers the full family; the split point is marked at Task 6.

---

## File Structure

| File | Responsibility / change |
|---|---|
| `src/CpuEmulator.Jit/BlockCompiler.M8086.cs` | **Add** `EmitM8086FarFlow(EmitContext, ushort pc, OpcodeDescriptor d, int length, byte x86Seg)`; **add** `EmitM8086SetCs(EmitContext, ushort)` + `EmitM8086SetCsFromStack(EmitContext)`. The arm: `9A`/`EA` (direct, chainable), `FF /3`/`FF /5` (indirect, dynamic), `CB`/`CA` (far RET, dynamic). |
| `src/CpuEmulator.Jit/BlockCompiler.cs` | **Add** `IsM8086FarFlowOpcode(OpcodeDescriptor d)` (mirror `:287`); **add** the dispatch branch in `EmitInstruction` (after the near-flow branch `:650-654`). |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | **Add** `IsEmittableX86FarFlow(InstructionModel)`; admit it in `IsEmittableX86Family` (`:4641`) so the far family is un-forced (descriptor `endsBlock=true`, not forced-fallback). |
| `tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs` | **New** — far CALL/JMP/RET emit-vs-oracle parity (CS:IP + far stack frame). |
| `tests/CpuEmulator.Tests/Jit/M8086AliasingRegressionTests.cs` | **New** — the far-transfer aliasing regression (two segments, same offset, distinct blocks). |
| `tests/CpuEmulator.Tests/TomHarte/` | Extend the 8088 corpus selection to run `9A`/`EA`/`CB`/`CA`/`FF /3`/`FF /5` through the JIT (parity gate 1). |

**Decomposition note:** the SS (stack segment) handling is already correct in `EmitM8086PushWord`/`EmitM8086PopWord` (they read `SS` via `EmitLoadReg16(ctx, "SS")`, `:988-989`/`:1005-1006`) — there is **no** `_m8086SS` field and none is needed; the far push/pop reuse the existing helpers unchanged. The only new state-write machinery is the CS field write (`EmitM8086SetCs`), since today CS is only ever **read** (`_m8086CS.GetValue` at `:380`), never emitted as a store.

---

## Task Sequencing

1. The `EmitM8086SetCs` + `EmitM8086SetCsFromStack` helpers (the one new piece of machinery) + their unit pins.
2. The `IsM8086FarFlowOpcode` gate + the `EmitInstruction` dispatch branch (with a temporary fallback body so the build stays green).
3. The far-RET arm (`CB`/`CA`) — pop IP then CS (simplest: dynamic exit, no chain).
4. The far-direct arm (`EA` JMP, `9A` CALL) — constant `(newCS,newIP)`, chainable.
5. The aliasing regression (the load-bearing un-fakeable gate) — fails on old key, passes on linear key.
6. The far-indirect arm (`FF /3`/`FF /5`) — dynamic `(CS,IP)` from memory. **(FF-2a/FF-2b split point.)**
7. Un-force the generation gate (`IsEmittableX86FarFlow`) + the TomHarte-through-JIT far parity + the honesty gate.

---

## Task 1: `EmitM8086SetCs` + `EmitM8086SetCsFromStack` helpers

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.M8086.cs` (add the two helpers, near `EmitM8086SetIp` `:1245`)
- Test: `tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs` (created in Task 3 — the first helper user; a direct pin is added in Step 4 here)

**Interfaces:**
- Consumes: `_m8086CS` (the `FieldInfo?` resolved at `BlockCompiler.cs:261`, today read-only); `ctx.Il`, `ctx.DataLocal`.
- Produces: `EmitM8086SetCs(EmitContext ctx, ushort target)` — emits `cpu.CS = target` (constant); `EmitM8086SetCsFromStack(EmitContext ctx)` — emits `cpu.CS = (ushort)<stack-top>` consuming the IL-stack top (mirrors `EmitM8086SetIpFromStack`).

- [ ] **Step 1: Add the two CS-write helpers**

In `src/CpuEmulator.Jit/BlockCompiler.M8086.cs`, immediately after `EmitM8086SetIpFromStack` (ends `:1261`), add:

```csharp
    /// <summary>ADR 0019 FF-2: write the CS field to a compile-time-constant segment (the far-direct
    /// 9A/EA target's CS). Mirrors EmitM8086SetIp (:1245) but stores _m8086CS. The far arm calls this
    /// BEFORE EmitChainOrExit/EmitNormalExit so the next dispatch's ProjectBlockKey keys/decodes the
    /// successor under the new segment (the linear-key payoff).</summary>
    private void EmitM8086SetCs(EmitContext ctx, ushort target)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)target); il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _m8086CS!);
    }

    /// <summary>ADR 0019 FF-2: write the CS field from the IL-stack top (the far-indirect / far-RET
    /// dynamic CS — popped off SS:SP or read from memory). Mirrors EmitM8086SetIpFromStack (:1255):
    /// narrows the stack value to ushort and stores _m8086CS.</summary>
    private void EmitM8086SetCsFromStack(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stloc, ctx.DataLocal);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Stfld, _m8086CS!);
    }
```

- [ ] **Step 2: Build to confirm the helpers compile**

Run: `dotnet build src/CpuEmulator.Jit -c Debug`
Expected: SUCCEEDS. `_m8086CS` is a `FieldInfo?`; the `!` null-forgiveness is safe because the far arm only runs when `TargetIsM8086` (where `_m8086CS` is non-null, resolved at `:261`). (If the build warns on the `!`, that is acceptable — the arm's caller is 8086-gated.)

- [ ] **Step 3: Write a direct CS-write pin (proves the helper stores CS correctly in isolation)**

Create `tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs` with the harness (mirroring `M8086FlowEmitTests.AssertFlowMatchesOracle`, `:77`) and a first pin that compiles a far `JMP` and checks CS landed (the full arm is Task 4; here we pin via the simplest far op once it exists). **For Task 1's isolated proof, instead add the helper smoke pin into the existing flow tests** — a minimal emitted block that calls `EmitM8086SetCs(ctx, 0x4000)` via the far-direct arm is not available yet, so defer the *observable* CS pin to Task 4 and here only assert the build + the helper's IL shape by reflection is unnecessary. **Skip a standalone Task-1 test**; the helper is exercised + gated by Tasks 3/4 (far RET / far JMP land CS). Proceed to commit the helpers.

- [ ] **Step 4: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCompiler.M8086.cs
git commit -m "feat(jit): EmitM8086SetCs + EmitM8086SetCsFromStack — the 8086 CS-write helpers (ADR 0019 FF-2)"
```

---

## Task 2: The `IsM8086FarFlowOpcode` gate + the `EmitInstruction` dispatch branch

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.cs:287` (add `IsM8086FarFlowOpcode`) + `:650-654` (add the dispatch branch)

**Interfaces:**
- Consumes: `OpcodeDescriptor d` (its `.Opcode` and, for `FF`, the reg sub-field — the descriptor for `FF /3`/`FF /5` carries `Mnemonic == "CALL"/"JMP"` and a sub-field the keyed descriptor distinguishes; see the FF-group decode note below).
- Produces: `IsM8086FarFlowOpcode(OpcodeDescriptor d) → bool`; the `EmitInstruction` branch that calls `EmitM8086FarFlow(ctx, pc, d, length, x86Seg)`.

> **FF-group note.** The near arm distinguishes `FF /2`(CALL)/`FF /4`(JMP) from the far `FF /3`/`FF /5` by the descriptor's keyed sub-field. The near gate (`IsM8086FlowOpcode`, `:287`) admits `0xFF && Mnemonic is "CALL" or "JMP"` and the **near arm** then handles only `reg==2`/`reg==4`, `goto default` (fallback) for any other FF /reg (`:1235`). The far gate must admit `FF /3`/`FF /5` specifically. Because both near and far FF rows carry `CALL`/`JMP` mnemonics, the gate split must key on the **reg sub-field**, which the descriptor exposes. Read how `OpcodeDescriptor` carries the FF sub-field (the keyed descriptor for `0xFF` — `M8086Spec.cs` declares `Insn(0xFF, subfield: 3, …)` etc., so the descriptor's operation key encodes `/reg`). Use the same sub-field access the near arm's ModR/M decode uses (`reg = (modrm>>3)&7`), but at the **gate** level the descriptor's keyed sub-field is the discriminator. If `OpcodeDescriptor` does not expose the sub-field directly, gate `IsM8086FarFlowOpcode` on `0x9A`/`0xEA`/`0xCB`/`0xCA` only and handle the `FF /3`/`FF /5` discrimination **inside** the far arm by decoding the ModR/M reg field (the near arm already proves this works — `:1209`), routing `reg==3`/`reg==5` to far and `goto`-ing back to fallback otherwise. **Prefer the latter** (gate on the four plain far opcodes + let the arm decode FF /reg) — it matches the near arm's existing FF handling exactly and avoids depending on the descriptor's sub-field shape.

- [ ] **Step 1: Add the far-flow gate**

In `src/CpuEmulator.Jit/BlockCompiler.cs`, immediately after `IsM8086FlowOpcode` (`:287-289`), add:

```csharp
    /// <summary>ADR 0019 FF-2: the far-transfer opcodes the far arm emits — 9A/EA (far CALL/JMP direct),
    /// CB/CA (far RET/RET imm16), and the 0xFF group's far indirect /3 (CALL)/ /5 (JMP). The 0xFF rows
    /// carry CALL/JMP mnemonics like the near FF /2 /4; the far arm decodes the ModR/M reg field and
    /// routes /3 /5 to far, goto-ing back to fallback for any other FF /reg (mirrors the near arm's FF
    /// handling, :1235). INT/INTO/IRET/BOUND are NOT here — they stay fallback (ADR 0019 Decision 3).</summary>
    private static bool IsM8086FarFlowOpcode(OpcodeDescriptor d) =>
        d.Opcode is 0x9A or 0xEA or 0xCB or 0xCA
        || (d.Opcode == 0xFF && d.Mnemonic is "CALL" or "JMP");
```

> The `0xFF` clause here overlaps the near gate's `0xFF` clause. Order matters at the dispatch site (Step 2): the **near** branch runs first and handles `FF /2`/`FF /4` (returning), so a far `FF /3`/`FF /5` falls through to the far branch. To avoid double-claiming, the near arm already `goto default`s (fallback) for FF /reg != 2,4 (`:1235`) — that path will no longer fallback once the far arm exists. **Resolve the overlap explicitly in Step 2** by gating the far branch to run when the near arm would NOT handle it: decode the FF reg in the far gate is overkill; instead, make the dispatch try far FIRST for the four plain far opcodes, and for `0xFF` let the **arm** decide (near arm handles /2 /4, far arm handles /3 /5). See Step 2.

- [ ] **Step 2: Add the dispatch branch (far AFTER near, with FF /reg routing)**

In `src/CpuEmulator.Jit/BlockCompiler.cs`, the existing near-flow dispatch is at `:650-654`:
```csharp
                if (TargetIsM8086 && IsM8086FlowOpcode(d))
                {
                    M8086FlowEmitSelections++;
                    EmitM8086Flow(ctx, pc, d, length, x86Seg);
                    break;
                }
```
The near arm `EmitM8086Flow` already `goto default`s (→ interpreter fallback) for `FF /reg` ∉ {2,4} (`:1235`). To make `FF /3`/`FF /5` reach the far arm instead of fallback, change the near arm's FF handling to route far there — but that couples the arms. **Cleaner: keep the near arm as-is and add the far branch to handle the four plain far opcodes + the far FF rows, BEFORE the near branch, so the far arm claims `FF /3`/`FF /5` and the near arm never sees them.** Replace the near branch with the far-first ordering:

```csharp
                // ADR 0019 FF-2: the far arm claims 9A/EA/CB/CA + FF /3 /5 (far indirect). It runs BEFORE
                // the near arm; for 0xFF it decodes the reg field and goto-fallbacks for /reg ∉ {3,5}, so
                // the near arm (next) still handles FF /2 /4. The plain far opcodes never collide with near.
                if (TargetIsM8086 && IsM8086FarFlowOpcode(d))
                {
                    M8086FarFlowEmitSelections++;       // FF-2 probe: the far-arm-now-live counter (asserted > 0)
                    if (EmitM8086FarFlow(ctx, pc, d, length, x86Seg))   // returns false ⇒ not a far FF /reg; fall to near
                        break;
                }
                if (TargetIsM8086 && IsM8086FlowOpcode(d))
                {
                    M8086FlowEmitSelections++;
                    EmitM8086Flow(ctx, pc, d, length, x86Seg);
                    break;
                }
```

Make `EmitM8086FarFlow` return `bool` (`true` = handled-and-emitted; `false` = an FF /reg the far arm does not own, so the near arm should try). The four plain far opcodes always return `true`. For `0xFF`, the far arm decodes the ModR/M reg: `reg==3`/`reg==5` → emit far + return `true`; else → return `false` (the near arm then handles /2 /4, or fallback). Add the probe field near `M8086FlowEmitSelections` (search for its declaration, ~`BlockCompiler.cs`):

```csharp
    internal int M8086FarFlowEmitSelections;   // FF-2: far-arm dispatch count (the non-vacuous gate asserts > 0)
```

- [ ] **Step 3: Add a temporary `EmitM8086FarFlow` stub (build-green placeholder)**

In `src/CpuEmulator.Jit/BlockCompiler.M8086.cs`, add the method skeleton (filled by Tasks 3/4/6). For now, route everything to fallback so the build is green and behavior is unchanged:

```csharp
    /// <summary>ADR 0019 FF-2: the far-transfer arm (9A/EA/CB/CA + FF /3 /5). Returns true if it emitted
    /// the op; false for an FF /reg it does not own (the near arm then handles /2 /4). Filled per-opcode
    /// across FF-2 Tasks 3/4/6. Until an arm exists for an opcode, it returns false → the caller falls
    /// through to the near arm / interpreter fallback (no behavior change).</summary>
    private bool EmitM8086FarFlow(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg)
    {
        byte opcode = d.Opcode;
        switch (opcode)
        {
            // Tasks 3/4/6 fill these. Until then, fall through to false (fallback / near arm).
            default:
                return false;
        }
    }
```

- [ ] **Step 4: Build + run the existing 8086 parity (no behavior change yet)**

Run: `dotnet build -c Debug && dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~M8086" -c Debug`
Expected: PASS — `EmitM8086FarFlow` returns `false` for everything, so the far opcodes still fall back (the near arm + interpreter behave exactly as before FF-2). The dispatch plumbing is in place.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCompiler.cs src/CpuEmulator.Jit/BlockCompiler.M8086.cs
git commit -m "feat(jit): IsM8086FarFlowOpcode gate + EmitM8086FarFlow dispatch skeleton (ADR 0019 FF-2)"
```

---

## Task 3: The far-RET arm (`CB` / `CA`) — pop IP then CS

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.M8086.cs` (`EmitM8086FarFlow` — add the `CB`/`CA` cases)
- Test: `tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs`

**Interfaces:**
- Consumes: `EmitM8086PopWord` (`:1002` — leaves the popped word on the IL stack), `EmitM8086SetIpFromStack` (`:1255`), `EmitM8086SetCsFromStack` (Task 1), `EmitNormalExit` (`BlockCompiler.cs:1373`), `RegField("SP")`, `M8086CodePhys`.
- Produces: the far-RET emit. `CB`: pop IP, pop CS (in that order — IP is at the lower address), dynamic exit. `CA`: same, then `SP += imm16`.

> **Far RET stack order (ADR 0005 far frame).** A far CALL pushes CS **then** IP (so IP ends at the lower address, CS just above). Far RET pops **IP first** (lower address, the new SP), **then CS**. Each `EmitM8086PopWord` reads SS:SP, leaves the word on the IL stack, and bumps SP += 2.

- [ ] **Step 1: Write the failing far-RET parity test**

Add to `tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs` (use the same harness shape as `M8086FlowEmitTests`'s `Call_rel16_pushes_return_ip_and_jumps`, `:161`). Pre-seed SS:SP with a far return frame (IP at SS:SP, CS at SS:SP+2):

```csharp
[Fact]
public void Retf_pops_ip_then_cs_and_exits()
{
    if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
    const ushort cs = 0x2000, ip = 0x0000, ss = 0x3000, sp = 0x0100;
    const ushort retIp = 0x1234, retCs = 0x5678;
    byte[] code = [0xCB];   // RETF

    // ── JIT run: seed the far return frame at SS:SP (IP lo) and SS:SP+2 (CS lo) ──
    var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
    jbus.MapMemory(0, new byte[0x100000], writable: true);
    uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
    jbus.Write8(cphys, code[0]);
    uint sphys = (uint)(((ss << 4) + sp) & 0xFFFFF);
    jbus.Write8(sphys, (byte)retIp); jbus.Write8(sphys + 1, (byte)(retIp >> 8));
    jbus.Write8(sphys + 2, (byte)retCs); jbus.Write8(sphys + 3, (byte)(retCs >> 8));
    var inner = new M8086Cpu(jbus);
    inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
    inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
    var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
    long budget = 1; jit.Run(ref budget);

    // ── interpreter oracle: same seed, one Step ──
    var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
    ibus.MapMemory(0, new byte[0x100000], writable: true);
    ibus.Write8(cphys, code[0]);
    ibus.Write8(sphys, (byte)retIp); ibus.Write8(sphys + 1, (byte)(retIp >> 8));
    ibus.Write8(sphys + 2, (byte)retCs); ibus.Write8(sphys + 3, (byte)(retCs >> 8));
    var interp = new M8086Cpu(ibus);
    interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
    interp.SetRegister("SS", ss); interp.SetRegister("SP", sp);
    interp.Step();

    Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // == retIp
    Assert.Equal(interp.GetRegister("CS"), inner.GetRegister("CS"));   // == retCs (the far half — the new thing)
    Assert.Equal(interp.GetRegister("SP"), inner.GetRegister("SP"));   // SP += 4
    Assert.Equal((ushort)retCs, (ushort)inner.GetRegister("CS"));
}
```

- [ ] **Step 2: Run it to verify it FAILS**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Retf_pops_ip_then_cs_and_exits" -c Debug`
Expected: FAIL — `EmitM8086FarFlow` returns `false` for `0xCB` (Task 2 stub), so the op falls back to the interpreter for the JIT run too... **wait — that would pass.** Confirm the failure mode: with the stub returning `false`, the `0xCB` is NOT in `IsM8086FlowOpcode` either, so it hits the interpreter fallback in BOTH runs → the test would PASS vacuously. To make this a real red, the test must observe that the op is **emitted, not fallback**. Add an emit-selection assertion: after `jit.Run`, assert `jit` compiled an emitted block for `0xCB` (the `M8086FarFlowEmitSelections` probe > 0). Since the probe is on the compiler, expose it via a test seam on `JittedCpu` (mirror `M8086FlowEmitSelections` exposure if present). If the probe is not yet observable, the honest red is at Task 7's TomHarte gate (`FallbackEmitCount` drops). **For Task 3, gate the red on the probe:** add `internal int M8086FarFlowEmitSelections => _compiler.M8086FarFlowEmitSelections;` to `JittedCpu` and assert `jit.M8086FarFlowEmitSelections > 0` in the test — this FAILS with the stub (0 emissions) and PASSES once Step 3 emits.

Run again after adding the probe assertion.
Expected: FAIL on `Assert.True(jit.M8086FarFlowEmitSelections > 0)` (the stub emits nothing).

- [ ] **Step 3: Implement the `CB`/`CA` cases**

In `EmitM8086FarFlow`, replace the `default` with the far-RET cases (and keep `default: return false`):

```csharp
        switch (opcode)
        {
            // ── CB RETF / CA RETF imm16: pop IP (lower addr), then pop CS; CA also adds imm16 to SP. ──
            case 0xCB:
            case 0xCA:
            {
                ILGenerator il = ctx.Il;
                EmitM8086PopWord(ctx); EmitM8086SetIpFromStack(ctx);   // IP = PopWord()  (the lower word)
                EmitM8086PopWord(ctx); EmitM8086SetCsFromStack(ctx);   // CS = PopWord()  (the upper word)
                if (opcode == 0xCA)                                    // SP += imm16
                {
                    int operandPc = pc + 1;
                    ushort imm16 = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                            | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("SP")); il.Emit(OpCodes.Ldc_I4, (int)imm16); il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, RegField("SP"));
                }
                EmitNormalExit(ctx);                                   // DYNAMIC popped (CS:IP) target — NOT chainable
                return true;
            }
            default:
                return false;
        }
```

> **Note the SP/operand addressing:** `pc` is the far-RET opcode's offset; `operandPc = pc + 1` is the `imm16` for `CA`. This mirrors the near `C2 RET imm16` arm (`:1172-1179`) exactly, only with the extra CS pop before the SP adjust. `M8086CodePhys` reads the imm16 from the **code** segment (correct — the imm is in the instruction stream), while the pops read **SS:SP** (correct — the stack).

- [ ] **Step 4: Run the far-RET parity to verify PASS**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Retf_pops_ip_then_cs_and_exits" -c Debug`
Expected: PASS — IP, CS, and SP all match the interpreter, and `M8086FarFlowEmitSelections > 0` (the op was emitted).

- [ ] **Step 5: Add a `CA RETF imm16` parity row**

Add a second test mirroring Step 1 with `code = [0xCA, 0x04, 0x00]` (RETF 4) and assert `SP == sp + 4 (pops) + 4 (imm)`. Run it.
Expected: PASS — the imm16 SP adjust matches the oracle.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCompiler.M8086.cs src/CpuEmulator.Jit/JittedCpu.cs tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs
git commit -m "feat(jit): emit far RET (CB/CA) — pop IP then CS, dynamic exit (ADR 0019 FF-2)"
```

---

## Task 4: The far-direct arm (`EA` JMP, `9A` CALL) — constant target, chainable

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.M8086.cs` (`EmitM8086FarFlow` — add `EA`/`9A`)
- Test: `tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs`

**Interfaces:**
- Consumes: `EmitM8086SetCs`/`EmitM8086SetIp` (constants), `EmitM8086PushWord` (`:982`), `EmitChainOrExit` (`BlockCompiler.cs:1391` — **after FF-1** takes the `uint` linear key), `M8086CodePhys`.
- Produces: `EA` (far JMP `ptr16:16`): set IP then CS from the immediate, chain to `((newCS<<4)+newIP)&0xFFFFF`. `9A` (far CALL `ptr16:16`): push CS then IP (the far return frame), set IP then CS, chain to the same projected key.

> **`ptr16:16` operand layout.** `EA`/`9A` are followed by 4 immediate bytes: `IP_lo IP_hi CS_lo CS_hi` (the offset first, then the segment). Read all four from the code segment via `M8086CodePhys`.
>
> **Far CALL push order (ADR 0005).** Far CALL pushes **CS first, then IP** (so the far return frame has IP at the lower address — matching the far-RET pop order in Task 3). The pushed CS is the **current** CS (the return segment), the pushed IP is `fallThrough` (the offset of the instruction after the 5-byte `9A`).
>
> **The chain target is the FF-1 payoff.** Because `(newCS, newIP)` are compile-time constants, the projected successor key `((newCS<<4)+newIP)&0xFFFFF` is a compile-time `uint` constant → `EmitChainOrExit` chains across the segment change. This is exactly what FF-1's widened key makes sound.

- [ ] **Step 1: Write the failing far-JMP (`EA`) parity test**

Add to `M8086FarFlowEmitTests.cs`:

```csharp
[Fact]
public void Far_jmp_ea_sets_cs_and_ip_from_the_immediate()
{
    if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
    const ushort cs = 0x2000, ip = 0x0000;
    const ushort newIp = 0x0100, newCs = 0x4000;
    // EA ptr16:16 = EA  IP_lo IP_hi CS_lo CS_hi
    byte[] code = [0xEA, (byte)newIp, (byte)(newIp >> 8), (byte)newCs, (byte)(newCs >> 8)];

    var (innerCs, innerIp) = RunJitOne(cs, ip, code, out _);   // helper that builds the JIT, runs one dispatch, returns CS/IP
    var (interpCs, interpIp) = RunInterpOne(cs, ip, code);     // interpreter oracle, one Step

    Assert.Equal(interpIp, innerIp);   // == newIp
    Assert.Equal(interpCs, innerCs);   // == newCs (the far half)
}
```

Add the two small helpers `RunJitOne`/`RunInterpOne` to the fixture (factoring the JIT-build + interpreter-build from Task 3's inline code — both build an `AddressSpace(20)`, write the code at `(cs<<4)+ip`, seed CS/IP, and run one dispatch/Step; `RunJitOne` returns `((ushort)inner.CS, (ushort)inner.IP)` and the bus via `out`).

- [ ] **Step 2: Run to verify FAIL**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Far_jmp_ea_sets_cs_and_ip_from_the_immediate" -c Debug`
Expected: FAIL — `EA` returns `false` from the stub, falls back; the JIT path matches the interpreter (both fallback) so CS/IP agree, BUT the emit-probe assertion (add `Assert.True(...M8086FarFlowEmitSelections > 0)` as in Task 3) fails. The op must be EMITTED, not fallback.

- [ ] **Step 3: Implement `EA` and `9A`**

Add the cases to `EmitM8086FarFlow`'s switch (before `default`):

```csharp
            // ── EA far JMP ptr16:16: IP, CS from the immediate (IP_lo IP_hi CS_lo CS_hi); chainable. ──
            case 0xEA:
            {
                int operandPc = pc + 1;
                ushort newIp = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                ushort newCs = (ushort)(_bus.Read8(M8086CodePhys((ushort)(operandPc + 2)))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 3))) << 8));
                EmitM8086SetIp(ctx, newIp);
                EmitM8086SetCs(ctx, newCs);
                uint targetKey = (uint)(((newCs << 4) + newIp) & 0xFFFFF);
                EmitChainOrExit(ctx, targetKey);          // constant (newCS,newIP) → chainable across the segment change
                return true;
            }
            // ── 9A far CALL ptr16:16: push CS then IP (far frame), set IP,CS from imm; chainable. ──
            case 0x9A:
            {
                int operandPc = pc + 1;
                ushort newIp = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                ushort newCs = (ushort)(_bus.Read8(M8086CodePhys((ushort)(operandPc + 2)))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 3))) << 8));
                ushort fallThrough = (ushort)(pc + length);   // the return IP (after the 5-byte 9A)
                // Far frame: push CS (return segment) first, then IP (return offset) — IP ends lower.
                EmitM8086PushWord(ctx, () => EmitLoadReg16(ctx, "CS"));         // PushWord(CS)
                EmitM8086PushWord(ctx, () => il_LdcReturnIp(ctx, fallThrough)); // PushWord(IP)
                EmitM8086SetIp(ctx, newIp);
                EmitM8086SetCs(ctx, newCs);
                uint targetKey = (uint)(((newCs << 4) + newIp) & 0xFFFFF);
                EmitChainOrExit(ctx, targetKey);          // constant entry → chainable
                return true;
            }
```

The near arm pushes the return IP via `EmitM8086PushWord(ctx, () => il.Emit(OpCodes.Ldc_I4, (int)fallThrough))` (`:1162`). Use the same inline form (drop the `il_LdcReturnIp` placeholder — write it inline):
```csharp
                EmitM8086PushWord(ctx, () => ctx.Il.Emit(OpCodes.Ldc_I4, (int)fallThrough));   // PushWord(IP)
```
For the CS push, `EmitLoadReg16(ctx, "CS")` loads the current CS onto the IL stack (the same helper `EmitM8086PushWord`/`EmitM8086PopWord` use for SS, `:988`). Confirm `EmitLoadReg16` leaves a `ushort`/`int` on the stack (it does — it is the reg-load primitive); `EmitM8086PushWord`'s `pushValue` action is expected to leave the word on the stack, then it `Stloc ctx.AddrLocal` (`:992`). So:
```csharp
                EmitM8086PushWord(ctx, () => EmitLoadReg16(ctx, "CS"));   // PushWord(CS) — the return segment
```

- [ ] **Step 4: Run the far-JMP + far-CALL parity to verify PASS**

Add a `9A` far-CALL parity test mirroring Task 3's far-RET (assert CS, IP land at the immediate's `(newCS,newIP)`, SP -= 4, and the pushed frame in SS:SP is `IP` at the lower word + `CS` at the upper word — the exact inverse of the far-RET pop).
Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Far_jmp_ea|FullyQualifiedName~Far_call_9a" -c Debug`
Expected: PASS — CS:IP and the far stack frame match the interpreter; the ops are emitted.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCompiler.M8086.cs tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs
git commit -m "feat(jit): emit far direct JMP (EA) + CALL (9A) — set CS:IP, push far frame, chainable (ADR 0019 FF-2)"
```

---

## Task 5: The far-transfer aliasing regression (the load-bearing un-fakeable gate)

**Files:**
- Create: `tests/CpuEmulator.Tests/Jit/M8086AliasingRegressionTests.cs`

**Interfaces:**
- Consumes: `JittedCpu<M8086Cpu>`, the far-direct arm (Task 4 — a `9A`/`EA` that changes CS mid-chain), a segment-distinguishing side effect (each segment's code writes a segment-unique byte to a distinct memory address).
- Produces: ADR 0019 Decision 4 gate 2 — two segments with the SAME offset compile to DISTINCT blocks. **FAILS on the old `(IP)` key (both alias to one block), PASSES on the FF-1 linear key.**

> **Why this is the load-bearing gate.** The aliasing bug only arms once an *emitted* op changes CS mid-chain — which is exactly what Task 4's far-direct `9A`/`EA` now does. Place distinct code at `CS=0x1000,IP=0x0100` (physical `0x10100`) and at `CS=0x2000,IP=0x0100` (physical `0x20100`); far-transfer between them; assert each runs **its own** segment's code. On the old `ushort`-IP key both alias to `_blocks[0x0100]` (the second segment runs the first's compiled block — the silent wrong-execution bug). On the linear key the two are distinct physical entries → distinct blocks.

- [ ] **Step 1: Write the aliasing regression**

Create `tests/CpuEmulator.Tests/Jit/M8086AliasingRegressionTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0019 Decision 4 gate 2 — the far-transfer aliasing regression. Two segments at the SAME
/// IP offset (0x0100) hold DIFFERENT code (each writes a segment-unique byte). A far JMP from segment A
/// into segment B must run B's code, not A's. On the OLD ushort-IP key both alias to _blocks[0x0100] and
/// B runs A's block (silent corruption). On the FF-1 linear (CS<<4)+IP key they are distinct blocks.
/// This test FAILS pre-FF-1 / pre-far-emit and PASSES with FF-1 + the far arms.</summary>
public class M8086AliasingRegressionTests
{
    [Fact]
    public void Far_jmp_between_segments_at_the_same_offset_runs_each_segments_own_code()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;

        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);

        // Segment A @ CS=0x1000, IP=0x0100 (phys 0x10100): MOV [0x00080],0xAA ; far JMP 0x2000:0x0100
        //   C6 06 80 00 AA   = MOV byte [0x0080], 0xAA   (writes A's marker to DS:0x0080 = phys 0x0080)
        //   EA 00 01 00 20   = JMP 0x2000:0x0100
        uint a = 0x10100;
        byte[] segA = [0xC6, 0x06, 0x80, 0x00, 0xAA,  0xEA, 0x00, 0x01, 0x00, 0x20];
        for (int i = 0; i < segA.Length; i++) bus.Write8(a + (uint)i, segA[i]);

        // Segment B @ CS=0x2000, IP=0x0100 (phys 0x20100): MOV [0x00082],0xBB ; HLT
        //   C6 06 82 00 BB   = MOV byte [0x0082], 0xBB   (B's marker)
        //   F4               = HLT
        uint b = 0x20100;
        byte[] segB = [0xC6, 0x06, 0x82, 0x00, 0xBB,  0xF4];
        for (int i = 0; i < segB.Length; i++) bus.Write8(b + (uint)i, segB[i]);

        var inner = new M8086Cpu(bus);
        inner.SetRegister("CS", 0x1000); inner.SetRegister("IP", 0x0100);
        inner.SetRegister("DS", 0x0000);   // markers land at absolute 0x0080/0x0082
        inner.SetRegister("SS", 0x0000); inner.SetRegister("SP", 0x1000);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);

        long budget = 64; jit.Run(ref budget);   // run A → far JMP → B → HLT

        // A's marker AND B's marker must BOTH be present — proving B's own code ran (not A's block re-run).
        Assert.Equal(0xAA, bus.Read8(0x0080));   // segment A executed
        Assert.Equal(0xBB, bus.Read8(0x0082));   // segment B executed ITS OWN code (the aliasing fix)
    }

    [Fact]
    public void Two_segments_same_offset_compile_to_distinct_blocks()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // Direct key-level assertion: the cache holds two entries (0x10100 and 0x20100), not one (0x0100).
        // Reuses the run above; after it, both linear keys are cached.
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint a = 0x10100; byte[] segA = [0xC6, 0x06, 0x80, 0x00, 0xAA,  0xEA, 0x00, 0x01, 0x00, 0x20];
        for (int i = 0; i < segA.Length; i++) bus.Write8(a + (uint)i, segA[i]);
        uint b = 0x20100; byte[] segB = [0xC6, 0x06, 0x82, 0x00, 0xBB,  0xF4];
        for (int i = 0; i < segB.Length; i++) bus.Write8(b + (uint)i, segB[i]);
        var inner = new M8086Cpu(bus);
        inner.SetRegister("CS", 0x1000); inner.SetRegister("IP", 0x0100);
        inner.SetRegister("DS", 0x0000); inner.SetRegister("SS", 0x0000); inner.SetRegister("SP", 0x1000);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);
        long budget = 64; jit.Run(ref budget);

        Assert.True(jit.CacheContainsBlockKey(0x10100u));   // segment A's block
        Assert.True(jit.CacheContainsBlockKey(0x20100u));   // segment B's block — DISTINCT (the fix)
        Assert.False(jit.CacheContainsBlockKey(0x00100u));  // NOT keyed on the bare IP (the old bug)
    }
}
```

**Test seam (TDD-shaping):** `jit.CacheContainsBlockKey(uint)` exposes `_cache.ContainsBlockKey(uint)` (added in FF-1 Task 7's harness note as `internal bool BlockCache.ContainsBlockKey(uint)`). If FF-1 did not add it, add the two-line seam now: `internal bool ContainsBlockKey(uint key) => _blocks.ContainsKey(key);` on `BlockCache` and `internal bool CacheContainsBlockKey(uint key) => _cache.ContainsBlockKey(key);` on `JittedCpu`. The `InternalsVisibleTo` for the test assembly is already in place (the existing JIT tests read internal seams).

- [ ] **Step 2: Run the aliasing regression — verify PASS (with FF-1 + the far arms)**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~M8086AliasingRegressionTests" -c Debug`
Expected: PASS — both markers present, both linear keys cached, the bare-IP key absent.

- [ ] **Step 3: Prove it FAILS on the old key (the un-fakeable demonstration)**

> This is the "fails pre-fix" half. You cannot run it against pre-FF-1 `main` directly (the far arm did not exist there), so demonstrate the load-bearing-ness by **temporarily** reverting the projection to the bare IP and confirming the test fails, then restoring it. Concretely: in `IJitTarget`'s 8086 `ProjectBlockKey` (the generated body), temporarily hard-edit the **generated** output OR add a throwaway `JitOptions.ForceBareIpKeyForTest` that makes the dispatcher key on `(uint)(ushort)inner.GetRegister("IP")` instead of the projection. Run the aliasing test under that flag.

Run (under the temporary bare-IP key): the aliasing test.
Expected: FAIL — `bus.Read8(0x0082)` is `0x00` (B's code never ran; the dispatcher re-ran A's block at IP `0x0100`), and `CacheContainsBlockKey(0x20100u)` is false. **Then remove the throwaway flag/edit** and re-confirm Step 2 passes. Record the FAIL output in the PR body as the un-fakeable proof.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Jit/M8086AliasingRegressionTests.cs src/CpuEmulator.Jit/BlockCache.cs src/CpuEmulator.Jit/JittedCpu.cs
git commit -m "test(jit): far-transfer aliasing regression — distinct blocks per segment (ADR 0019 FF-2 gate 2)"
```

---

## Task 6: The far-indirect arm (`FF /3` CALL, `FF /5` JMP) — dynamic `(CS,IP)` from memory

> **⟂ FF-2a / FF-2b SPLIT POINT.** If the EA-resolution for the FF-group indirect proves heavy, ship Tasks 1–5 + 7 as **FF-2a** (direct `9A`/`EA`/`CB`/`CA` + the aliasing gate — the headline correctness fix) and this Task 6 as **FF-2b**. The fallback valve keeps a partial far family correct: `FF /3`/`FF /5` stay fallback in FF-2a (they `return false` from `EmitM8086FarFlow`, hitting the interpreter), still correctly keyed under the linear key. If split, FF-2b is a new queue row depending on FF-2a.

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.M8086.cs` (`EmitM8086FarFlow` — the `0xFF` case)
- Test: `tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs`

**Interfaces:**
- Consumes: the near arm's ModR/M decode pattern (`:1205-1212`), `EmitM8086LoadRmWordTarget` (the near arm's r/m16 loader, `:1216`), a far-pointer memory read (read **two** words from the EA: offset at EA, segment at EA+2), `EmitM8086SetIpFromStack`/`EmitM8086SetCsFromStack`, `EmitM8086PushWord` (for `FF /3` CALL), `EmitNormalExit`.
- Produces: `FF /3` (far CALL indirect): read `(IP,CS)` from the memory operand, push CS then IP, set IP then CS, dynamic exit. `FF /5` (far JMP indirect): read `(IP,CS)`, set IP then CS, dynamic exit. Returns `true` for `reg ∈ {3,5}`, `false` otherwise (→ near arm handles /2 /4).

> **Far-pointer memory layout.** `FF /3`/`FF /5` take a memory operand `m16:16`: the **offset** word at the EA, the **segment** word at EA+2 (low-address offset, high-address segment — the same order as the far frame). Both reads use the operand's segment (DS or the segment override), NOT the code segment.

- [ ] **Step 1: Write the failing `FF /5` far-indirect JMP parity test**

Add to `M8086FarFlowEmitTests.cs`. Place a far pointer `(newIp, newCs)` in memory at a fixed DS offset; `FF /5 /m` jumps through it:

```csharp
[Fact]
public void Far_jmp_indirect_ff5_loads_cs_ip_from_memory()
{
    if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
    const ushort cs = 0x2000, ip = 0x0000;
    const ushort newIp = 0x0100, newCs = 0x4000;
    // FF /5 with mod=00 rm=110 (disp16) → JMP FAR [0x0200]: FF 2E 00 02
    byte[] code = [0xFF, 0x2E, 0x00, 0x02];
    // far pointer at DS:0x0200 = offset(newIp) then segment(newCs)

    var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
    jbus.MapMemory(0, new byte[0x100000], writable: true);
    uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
    for (int i = 0; i < code.Length; i++) jbus.Write8(cphys + (uint)i, code[i]);
    // DS=0 → the far pointer is at absolute 0x0200
    jbus.Write8(0x0200, (byte)newIp); jbus.Write8(0x0201, (byte)(newIp >> 8));
    jbus.Write8(0x0202, (byte)newCs); jbus.Write8(0x0203, (byte)(newCs >> 8));
    var inner = new M8086Cpu(jbus);
    inner.SetRegister("CS", cs); inner.SetRegister("IP", ip); inner.SetRegister("DS", 0x0000);
    var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
    long budget = 1; jit.Run(ref budget);

    // interpreter oracle: same seed, one Step
    var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
    ibus.MapMemory(0, new byte[0x100000], writable: true);
    for (int i = 0; i < code.Length; i++) ibus.Write8(cphys + (uint)i, code[i]);
    ibus.Write8(0x0200, (byte)newIp); ibus.Write8(0x0201, (byte)(newIp >> 8));
    ibus.Write8(0x0202, (byte)newCs); ibus.Write8(0x0203, (byte)(newCs >> 8));
    var interp = new M8086Cpu(ibus);
    interp.SetRegister("CS", cs); interp.SetRegister("IP", ip); interp.SetRegister("DS", 0x0000);
    interp.Step();

    Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // == newIp
    Assert.Equal(interp.GetRegister("CS"), inner.GetRegister("CS"));   // == newCs
    Assert.True(jit.M8086FarFlowEmitSelections > 0);                   // emitted, not fallback
}
```

- [ ] **Step 2: Run to verify FAIL**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Far_jmp_indirect_ff5" -c Debug`
Expected: FAIL on `M8086FarFlowEmitSelections > 0` (the `0xFF` case still `return false`s from the stub for /5).

- [ ] **Step 3: Implement the `0xFF` far-indirect case**

Add the `0xFF` case to `EmitM8086FarFlow`, decoding the ModR/M reg to route /3 /5 (and `return false` for others so the near arm handles /2 /4). Mirror the near arm's ModR/M decode (`:1205-1212`), then read **two** words from the EA:

```csharp
            // ── FF group: /3 far CALL indirect, /5 far JMP indirect (m16:16). Other /reg → false (near arm). ──
            case 0xFF:
            {
                ILGenerator il = ctx.Il;
                int operandPc = pc + 1;
                byte modrm = _bus.Read8(M8086CodePhys((ushort)operandPc)); operandPc++;
                uint mod = (uint)(modrm >> 6) & 3u;
                uint reg = (uint)(modrm >> 3) & 7u;
                uint rm  = (uint)modrm & 7u;
                if (reg != 3u && reg != 5u)
                    return false;   // /2 /4 (near) or /0 /1 /6 /7 (not flow) — let the near arm / fallback handle it

                int dispLen = mod switch { 0u => rm == 6u ? 2 : 0, 1u => 1, 2u => 2, _ => 0 };
                ushort disp = 0;
                if (dispLen == 1) disp = unchecked((ushort)(sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc)));
                else if (dispLen == 2)
                    disp = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                    | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));

                // Compute the EA into ctx.M8086OffsetLocal/M8086SegLocal (the operand's seg = DS or override),
                // then read offset word at EA and segment word at EA+2. Reuse the near arm's EA computation:
                // EmitM8086FarPtrFromEa loads (newIp, newCs) onto the IL stack as two words (see helper note).
                if (reg == 3u)        // FF /3 far CALL: push CS, push IP (return frame), then set IP,CS from mem.
                {
                    ushort fallThrough = (ushort)(pc + length);
                    EmitM8086PushWord(ctx, () => EmitLoadReg16(ctx, "CS"));                       // PushWord(CS)
                    EmitM8086PushWord(ctx, () => ctx.Il.Emit(OpCodes.Ldc_I4, (int)fallThrough));  // PushWord(IP)
                    EmitM8086LoadFarPtr(ctx, mod, rm, disp, x86Seg, out var _);   // leaves newCs then newIp staged
                    EmitM8086SetIpFromStack(ctx);   // IP = newIp (top)
                    EmitM8086SetCsFromStack(ctx);   // CS = newCs
                    EmitNormalExit(ctx);
                    return true;
                }
                else                  // reg == 5u — FF /5 far JMP: set IP,CS from mem (no push).
                {
                    EmitM8086LoadFarPtr(ctx, mod, rm, disp, x86Seg, out var _);
                    EmitM8086SetIpFromStack(ctx);
                    EmitM8086SetCsFromStack(ctx);
                    EmitNormalExit(ctx);
                    return true;
                }
            }
```

**`EmitM8086LoadFarPtr` (new helper — the one non-trivial piece).** Add a helper that computes the operand EA (reusing the near arm's EA machinery — read `EmitM8086LoadRmWordTarget`, `:1216`, and the EA-computation it calls; the far variant reads TWO words: offset at EA, segment at EA+2). The simplest correct shape stages the two words into locals and pushes them in the order the two `…FromStack` calls consume (IP popped first / on top, then CS):

```csharp
    /// <summary>ADR 0019 FF-2: read a far pointer m16:16 from the operand EA — the offset word at EA and
    /// the segment word at EA+2 (the operand's segment = DS or the x86Seg override). Stages newCs then
    /// newIp on the IL stack so the caller's EmitM8086SetIpFromStack (top) then EmitM8086SetCsFromStack
    /// consume them in order. Reuses the near arm's EA computation (EmitM8086ComputeEa / the rm-target
    /// addressing at :1216) — read that and call it to land (seg, offset) in M8086SegLocal/M8086OffsetLocal,
    /// then read word@offset and word@offset+2.</summary>
    private void EmitM8086LoadFarPtr(EmitContext ctx, uint mod, uint rm, ushort disp, byte x86Seg, out bool dynamic)
    {
        // Implementation: compute the EA (seg,offset) the same way EmitM8086LoadRmWordTarget does for a
        // memory operand; read the offset word (lo@EA, hi@EA+1) and the segment word (lo@EA+2, hi@EA+3),
        // each via the EmitM8086PushPhysical + LoadByteFromBus byte-pair the push/pop helpers use (:993-997).
        // Stage: push newCs first, then newIp (so newIp is on top for SetIpFromStack). dynamic = true.
        dynamic = true;
        // ... (mirror the near arm's EA + the EmitM8086PopWord byte-pair reads; see :1002-1011 for the
        //      two-byte word-read shape applied at offset and offset+2).
    }
```

> The implementer fills `EmitM8086LoadFarPtr` by mirroring (a) the near arm's EA computation for the `(mod, rm, disp)` memory operand and (b) the `EmitM8086PopWord` two-byte word-read shape (`:1005-1011`) applied at the EA and EA+2. This is the "heavy" part flagged as the FF-2a/FF-2b split: if it proves intricate, defer Task 6 to FF-2b and leave `FF /3`/`FF /5` returning `false` (fallback). **The register-indirect forms (`mod==3`) are illegal for a far indirect (the operand must be memory) — the interpreter raises/handles them; the arm should `return false` (fallback) for `mod==3`, matching the oracle.**

Add `if (mod == 3u) return false;` near the top of the `0xFF` case (after computing `mod`), before the EA work.

- [ ] **Step 4: Run the far-indirect parity (JMP + CALL) to verify PASS**

Add an `FF /3` far-CALL-indirect parity test (mirror Step 1 with `FF 1E 00 02` = CALL FAR [0x0200], asserting CS:IP land + the far frame pushed). Run both.
Expected: PASS — CS:IP and the far frame match the interpreter; emitted.

- [ ] **Step 5: Run the full 8086 parity to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~M8086" -c Debug`
Expected: PASS — near flow, far flow, MOV, ALU all green; the near FF /2 /4 still handled by the near arm (the far arm `return false`d for them).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCompiler.M8086.cs tests/CpuEmulator.Tests/Jit/M8086FarFlowEmitTests.cs
git commit -m "feat(jit): emit far indirect CALL (FF /3) + JMP (FF /5) from m16:16 (ADR 0019 FF-2)"
```

---

## Task 7: Un-force the generation gate + TomHarte-through-JIT far parity + the honesty gate

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs:4641` (`IsEmittableX86Family` — admit the far family) + add `IsEmittableX86FarFlow`
- Modify: `tests/CpuEmulator.Tests/TomHarte/` (extend the 8088 JIT-corpus selection to the far opcodes)

**Interfaces:**
- Consumes: `InstructionModel insn` (`.Mnemonic`, `.Opcode`, `.SubField`); the TomHarte 8088 corpus runner + `M8086JittedCpuFactory.Create` (`tests/CpuEmulator.Tests/TomHarte/M8086JittedCpuFactory.cs:16`).
- Produces: the far family is un-forced (descriptors carry `endsBlock=true`, not forced-fallback); the TomHarte far opcodes run through the JIT byte-identical to the interpreter; `FallbackEmitCount` drops by exactly the emitted far opcodes.

> **The generation gate (ADR 0011 drift #1).** Today `IsEmittableX86NearFlow` (`CpuEmitter.cs:4693`) explicitly excludes the far forms (`_ => false, // 9A/EA (far direct), CB/CA (far return) stay fallback`; `0xFF` admits only `SubField is 2 or 4`). The descriptor is generated forced-fallback for the far family. FF-2 must un-force it so the runtime arm's emission is reachable. **Important:** un-forcing must keep `endsBlock=true` for the far rows (a far transfer ends the block, like near flow — the re-force at `KeyedDescriptorLiteral` for near flow, noted at `:4663-4666`, must also cover the far rows).

- [ ] **Step 1: Add `IsEmittableX86FarFlow` and admit it**

In `src/CpuEmulator.Generators/CpuEmitter.cs`, after `IsEmittableX86NearFlow` (`:4710`), add:

```csharp
    /// <summary>ADR 0019 FF-2: the FAR control-flow family the JIT now emits — 9A/EA (far CALL/JMP direct),
    /// CB/CA (far RET/RET imm16), and the 0xFF group's far indirect /3 (CALL)/ /5 (JMP). Un-forced here so
    /// the runtime EmitM8086FarFlow arm is reachable (the descriptor must NOT be forced-fallback). Like the
    /// near family, these END the block (endsBlock=true is re-forced at KeyedDescriptorLiteral). INT/INTO/
    /// IRET/BOUND are NOT here — they stay fallback (ADR 0019 Decision 3).</summary>
    private static bool IsEmittableX86FarFlow(InstructionModel insn)
    {
        if (insn.Opcode is 0x9A or 0xEA or 0xCB or 0xCA) return true;
        if (insn.Opcode == 0xFF && insn.Mnemonic is "CALL" or "JMP")
            return insn.SubField is 3 or 5;   // far indirect only; /2 /4 are near (IsEmittableX86NearFlow)
        return false;
    }
```

In `IsEmittableX86Family` (`:4641`), after the `IsEmittableX86NearFlow` admit (`:4665`), add the far admit:

```csharp
        if (IsEmittableX86NearFlow(insn)) return true;
        if (IsEmittableX86FarFlow(insn)) return true;   // ADR 0019 FF-2: the far transfers are now emitted
```

- [ ] **Step 2: Ensure the far rows keep `endsBlock=true` (the re-force)**

Find where `KeyedDescriptorLiteral` re-forces `endsBlock=true` for the near-flow rows (the comment at `:4663-4666` references it). Extend that re-force condition to include `IsEmittableX86FarFlow(insn)` so the far descriptors are block-ending. Read the `KeyedDescriptorLiteral` method, locate the `endsBlock` re-force for `IsEmittableX86NearFlow`, and change it to `IsEmittableX86NearFlow(insn) || IsEmittableX86FarFlow(insn)`.
Expected: the generated 8086 descriptor for `9A`/`EA`/`CB`/`CA`/`FF /3`/`FF /5` is now emittable AND `endsBlock=true`.

- [ ] **Step 3: Rebuild the 8086 + run the full 8086 parity**

Run: `dotnet build src/CpuEmulator.Cpus.M8086 -c Debug && dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~M8086" -c Debug`
Expected: PASS — the descriptors are un-forced, the arm emits, and the per-opcode parity tests (Tasks 3/4/6) confirm byte-identity.

- [ ] **Step 4: Extend the TomHarte-through-JIT corpus to the far opcodes**

In `tests/CpuEmulator.Tests/TomHarte/`, find the 8088 JIT-corpus parity test (the one using `M8086JittedCpuFactory.Create`) and its opcode selection. Add `9A`/`EA`/`CB`/`CA`/`FF.3`/`FF.5` to the through-JIT opcode set (read how it enumerates the corpus files / opcodes — mirror the existing near-flow inclusion). The corpus runs each case through the JIT and asserts byte-identity to the interpreter (registers + FLAGS + CS:IP + memory + cycles).

```csharp
// In the TomHarte 8088 through-JIT opcode selection, add the far-flow opcodes (ADR 0019 FF-2):
//   "9A", "EA", "CB", "CA", "FF.3", "FF.5"
// (mirror the existing near-flow entries — the corpus file naming follows the opcode/subfield).
```

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~TomHarte" -c Debug`
Expected: PASS — every far-flow TomHarte case is byte-identical through the JIT. (If a case diverges, the arm has a bug — fix the arm, not the test; the interpreter is the oracle.)

- [ ] **Step 5: Assert `FallbackEmitCount` drops by exactly the far opcodes**

Add a pin (in the 8086 JIT tests) that compiles a block containing the far opcodes and asserts `FallbackEmitCount` is lower than pre-FF-2 by exactly the count of distinct far opcodes now emitted (`9A`/`EA`/`CB`/`CA`/`FF /3`/`FF /5` = 6 if Task 6 landed; 4 if split as FF-2a). Mirror the near-flow `FallbackEmitCount`-drop pin if one exists.
Run it.
Expected: PASS — the far opcodes left the fallback path.

- [ ] **Step 6: The honesty gate — measured far-call throughput delta**

Run the 8086 measurement apparatus (ADR 0011 PR-A — the `bench/hotop-profiler` or the equivalent 8086 bench) on a far-call-bearing workload, against the frozen M6 constants. Record the throughput delta in the PR body (the far transfers should now emit rather than fallback → measurable speedup on far-heavy code; no regression on near-heavy code).
Run: the 8086 bench harness on a far-call workload.
Expected: a recorded delta (emitted far flow ≥ fallback far flow throughput; the frozen-constant gate is satisfied — no honesty-gate violation).

- [ ] **Step 7: Full Release suite**

Run: `dotnet test -c Release`
Expected: green + warning-clean — all four CPUs' parity, the FF-1 identity regression (still green), the FF-2 far parity + aliasing regression + the honesty gate. Record the pass/fail/skip counts for the PR body.

- [ ] **Step 8: Commit**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs tests/CpuEmulator.Tests/
git commit -m "feat(jit): un-force the 8086 far-flow generation gate + TomHarte-through-JIT far parity (ADR 0019 FF-2)"
```

---

## Self-Review

**Spec coverage (against ADR 0019 §4 emit shape + §5 FF-2 scope + Decision 3 + Decision 4):**
- Emit far `JMP` `EA` (direct, chainable) — Task 4. ✓
- Emit far `JMP` `FF /5` (indirect, dynamic) — Task 6. ✓
- Emit far `CALL` `9A` (direct, push CS:IP, chainable) — Task 4. ✓
- Emit far `CALL` `FF /3` (indirect, dynamic) — Task 6. ✓
- Emit far `RET` `CB`/`CA` (pop IP then CS; `CA` adds imm16) — Task 3. ✓
- The CS write before exit (`EmitM8086SetCs`/`EmitM8086SetCsFromStack`) — Task 1, consumed by 3/4/6. ✓
- `INT`/`INTO`/`IRET`/`BOUND` stay fallback — Global Constraints + `IsM8086FarFlowOpcode`/`IsEmittableX86FarFlow` exclude them (Tasks 2, 7). ✓
- The far arms set NO flags — Tasks 3/4/6 emit no FLAGS write (mirrors the near arm). ✓
- Gate 1: far-flow TomHarte-through-JIT byte-identical + `FallbackEmitCount` drops by the far opcodes — Task 7 Steps 4–5. ✓
- Gate 2: the far-transfer aliasing regression (fails on old key, passes on linear) — Task 5. ✓
- The overlapping-segment coherence (Decision 4 gate 3) — covered at the projection layer in FF-1 Task 2; the aliasing regression (Task 5) is the FF-2 complement. ✓
- Honesty gate: measured far-call throughput delta — Task 7 Step 6. ✓
- Un-force the `CpuEmitter` far-flow gate (drift #1) — Task 7 Steps 1–2. ✓
- Splittable FF-2a/FF-2b — marked at Task 6. ✓

**Placeholder scan:** the one deliberately-deferred implementation body is `EmitM8086LoadFarPtr` (Task 6 Step 3) — the plan gives its exact contract (read offset@EA, segment@EA+2, stage newCs then newIp, `mod==3`→fallback) and the two existing shapes to mirror (`EmitM8086LoadRmWordTarget` EA computation + `EmitM8086PopWord` byte-pair word-read), and flags it as the FF-2a/FF-2b split point. This is a "mirror these two named shapes" instruction with the exact byte layout specified, not an open "implement later" — and it is gated by the Task 6 parity tests. All other steps show literal IL/test code. No "TBD"/"similar to Task N".

**Type consistency:** `EmitM8086SetCs(EmitContext, ushort)` / `EmitM8086SetCsFromStack(EmitContext)` (Task 1) match their call sites (Tasks 3/4/6). `EmitM8086FarFlow(EmitContext, ushort pc, OpcodeDescriptor d, int length, byte x86Seg) → bool` is consistent across the declaration (Task 2), the dispatch (Task 2 Step 2), and every case `return true/false`. `EmitChainOrExit(ctx, uint targetKey)` matches FF-1's widened signature (Task 4 passes `(uint)(((newCs<<4)+newIp)&0xFFFFF)`). `M8086FarFlowEmitSelections` is declared (Task 2) and read (Tasks 3/4/6 via the `JittedCpu` seam). `CacheContainsBlockKey(uint)` (Task 5) matches FF-1's `BlockCache.ContainsBlockKey(uint)` seam.

**FF-1 dependency restated:** every code block assumes `EmitChainOrExit` takes a `uint` key, `CompiledBlock.EntryPc`/the cache are `uint`-keyed, and `IJitTarget.ProjectBlockKey` exists with the 8086 fold. If any is absent, **FF-1 is not merged — STOP** (the far arms are unsound). Do not back-port the key widening into FF-2.
