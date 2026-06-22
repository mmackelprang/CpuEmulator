# FF-1 — The linear `(CS<<4)+IP` block key + the `IJitTarget.ProjectBlockKey` seam Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Widen the shared JIT block-cache key from `ushort` to the generic 32-bit linear `uint` `(CS<<4)+IP`, and add a per-CPU `IJitTarget.ProjectBlockKey` projection (8086 → `((CS<<4)+IP)&0xFFFFF`; the non-segmented 6502/Z80/68000 → identity `(uint)PC`) — proven SAFE by a byte-for-byte identity regression, with **no far op emitted** in this PR.

**Architecture:** This is the cache-key infrastructure ADR 0019 Decision 1 + 2 prescribes. The widening is a mechanical `ushort → uint` type change across `BlockCache<TCpu>`, `ChainTable<TCpu>`, `CompiledBlock<TCpu>`, `JittedCpu<TCpu>`, and `BlockCompiler<TCpu>`, plus one additive interface member `uint ProjectBlockKey(ICpuCore cpu)` on `IJitTarget` (generated per-CPU by `CpuEmitter`). The non-segmented CPUs' projection is the identity over their PC register, so they collapse to today's behavior byte-for-byte — the SAFE proof. The 8086's projection folds the segmented origin the decode/fetch already compute. **No far-flow emit arm changes in FF-1** — far ops stay fallback; this PR only makes the key correct so the FF-2 far arms become sound.

**Tech Stack:** C# / .NET, xUnit v2.9.3 (no `Assert.SkipWhen`), `System.Reflection.Emit` IL-JIT (the `CpuEmulator.Jit` dynamic-code tier), a Roslyn source generator (`CpuEmulator.Generators/CpuEmitter.cs`) that emits each CPU's `GeneratedJitTarget`.

## Global Constraints

- **Interpreter-as-oracle.** The interpreter is the source of truth; every JIT result must remain byte-identical to it (ADR 0011 / ADR 0019 §2).
- **Byte-identical TomHarte-through-JIT parity** stays green for ALL four CPUs across this PR. The 8086 corpus is single-step (one instruction per case), so the far-aliasing bug is latent there — the SAFE gate does **not** rely on TomHarte to catch it.
- **The non-segmented CPUs (6502/Z80/68000) are byte-for-byte unchanged** — same block count, same `ChainStepCount`, same `TotalEvictions`/`TotalRecompiles`, same `FallbackEmitCount`, same emitted bytes (ADR 0019 Decision 2). This is the merge precondition.
- **AOT-clean Core.** The projection reads the CPU through the `ICpuCore` interface (`GetRegister(string)`) — interface-only, no concrete-CPU dependency in `Core`, no per-dispatch reflection. `CpuEmulator.Jit` is the dynamic-code tier and may use baked `FieldInfo`/`Reflection.Emit` (the existing discipline).
- **No far op is emitted in FF-1.** `FallbackEmitCount` for the far opcodes (`9A`/`EA`/`CB`/`CA`/`FF /3`/`FF /5`) is unchanged — they remain fallback. FF-2 emits them.
- **Branch + PR discipline.** All work on a `feat/ff1-linear-block-key` branch; one reviewable PR; merge on green gates per the auto-merge policy. **FF-1 MUST land and pass its identity gate before FF-2 is started** (ADR 0019 §5 sequencing).
- **The widening is `ushort → uint`, never wider.** `uint` covers the 20-bit `(CS<<4)+IP` and leaves headroom for future 80286/386 (ADR 0019 §6 OQ2). Do **not** use `ulong`.

---

## File Structure

Files touched (all in `CpuEmulator.Jit` + the `IJitTarget` seam in `Core.Jit` + the generator), per ADR 0019 Decision 2's blast-radius table:

| File | Responsibility / change |
|---|---|
| `src/CpuEmulator.Core/Jit/IJitTarget.cs` | **Add** `uint ProjectBlockKey(ICpuCore cpu);` (additive interface member). |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | `EmitJitTarget` emits the per-CPU `ProjectBlockKey` body — identity over the PC register for the flat CPUs; the `((CS<<4)+IP)&0xFFFFF` fold for a spec that declares a `CS` register (the 8086). |
| `src/CpuEmulator.Jit/BlockCache.cs` | `_blocks`/`_recompiles`/`_cooldown`/`_everHotPcs` key `ushort→uint`; `GetOrCompile`/`ResolveChain`/`ShouldInterpret`/`NoteInterpretedDispatch` param `ushort→uint`. |
| `src/CpuEmulator.Jit/ChainTable.cs` | `_inbound` key + `Link`/`InboundTo`/`Sever` param `ushort→uint`. |
| `src/CpuEmulator.Jit/CompiledBlock.cs` | `EntryPc` `ushort→uint`; the `ChainDispatch` delegate's `targetPc` `ushort→uint`; ctor `entryPc` `ushort→uint`. |
| `src/CpuEmulator.Jit/JittedCpu.cs` | the dispatcher local `ushort pc → uint key = _target.ProjectBlockKey(_inner)`; `ChainEdge`'s `targetPc` `ushort→uint`; all call sites that pass it. |
| `src/CpuEmulator.Jit/BlockCompiler.cs` | `Compile(uint entryPc)`/`Discover(uint pc)`/`EmitChainOrExit(EmitContext, uint)`; the emitted chain-target constant widens to `uint`; for the 8086 the near-flow static chain target is folded through the baked `_m8086CodePhysBase`. |
| `src/CpuEmulator.Jit/BlockCompiler.M8086.cs` | the near-flow `EmitChainOrExit` call sites fold their static target through the baked code-phys base (so the near arm's chain edge key equals the dispatch-time `ProjectBlockKey`). **No new far arm in FF-1.** |
| `tests/CpuEmulator.Tests/Jit/ProjectBlockKeyTests.cs` | **New** — the `ProjectBlockKey == (uint)PC` identity unit test (flat CPUs) + the 8086 `((CS<<4)+IP)&0xFFFFF` fold + the overlapping-segment coherence check. |
| `tests/CpuEmulator.Tests/Jit/KeyWideningIdentityTests.cs` | **New** — the byte-for-byte identity regression for the non-segmented CPUs (block count / chain / eviction / `FallbackEmitCount` pins survive the widening). |

**Decomposition note (the one TDD-shaping call I made, per ADR 0019 §6 OQ1):** the ADR offers two seam shapes — (1) `ProjectBlockKey` on `IJitTarget`, or (2) a new `RegisterRole.CodeSegment` in the spec. The ADR **recommends option 1** (smaller surface, no spec/generator role the 80286+ might redefine). This plan takes option 1, and generates the per-CPU body by **detecting a `CS` register in the spec** (the 8086 has one; the flat CPUs do not) rather than adding a register role — the narrowest possible change that keeps the seam declarative-enough and AOT-clean. The interface member takes `ICpuCore` (not the ADR's shorthand `TCpu`) because `IJitTarget` is **non-generic** and `GetRegister(string)` lives on `ICpuCore`.

---

## Task Sequencing

Order is load-bearing — the projection seam (Task 1) and the identity unit test (Task 2) come **first** so the rest of the widening has its oracle. Then the type widening proceeds inside-out (the leaf types `CompiledBlock`/`ChainTable`/`BlockCache`, then the `BlockCompiler` producers, then the `JittedCpu` dispatcher), each step keeping the build green. The full identity regression (Task 9) is the final merge gate.

1. Add `ProjectBlockKey` to `IJitTarget` + generate the per-CPU body (the seam).
2. The `ProjectBlockKey` identity + 8086-fold + overlapping-segment unit tests (the seam's gate).
3. Widen `CompiledBlock.EntryPc` + the `ChainDispatch` delegate to `uint`.
4. Widen `ChainTable` to `uint`.
5. Widen `BlockCache` to `uint`.
6. Widen `BlockCompiler.Compile`/`Discover`/`EmitChainOrExit` to `uint` (non-8086 path; the emitted chain constant widens).
7. Fold the 8086 near-flow static chain target through the baked code-phys base.
8. Wire the dispatcher: `uint key = _target.ProjectBlockKey(_inner)`; widen `ChainEdge`.
9. The byte-for-byte non-segmented identity regression (the SAFE merge gate) + the green full suite.

---

## Task 1: Add `ProjectBlockKey` to `IJitTarget` and generate the per-CPU body

**Files:**
- Modify: `src/CpuEmulator.Core/Jit/IJitTarget.cs:11-51` (add the interface member)
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs:167-207` (`EmitJitTarget` — emit the per-CPU body)
- Test: `tests/CpuEmulator.Tests/Jit/ProjectBlockKeyTests.cs` (added in Task 2)

**Interfaces:**
- Consumes: `ICpuCore.GetRegister(string)` (returns `ulong`, zero-extended — `src/CpuEmulator.Core/ICpuCore.cs:38`); the spec's role-named ProgramCounter register (`CpuEmitter` already resolves `pcRegisterModel` at `EmitJitTarget`).
- Produces: `uint IJitTarget.ProjectBlockKey(ICpuCore cpu)` — the dispatcher (Task 8) calls this once per block dispatch. For the flat CPUs it returns `(uint)cpu.GetRegister("<PC>")`; for the 8086 it returns `(uint)(((cpu.GetRegister("CS") << 4) + cpu.GetRegister("IP")) & 0xFFFFF)`.

- [ ] **Step 1: Add the interface member**

In `src/CpuEmulator.Core/Jit/IJitTarget.cs`, after the `RegisterNames` property (line 50), before the closing brace (line 51), add:

```csharp
    /// <summary>Project the CPU's current execution point to the 32-bit block-cache key (ADR 0019).
    /// For a flat-PC CPU this is <c>(uint)PC</c> — the identity, byte-identical to the old ushort key.
    /// For the 8086 it folds the segmented origin: <c>((CS&lt;&lt;4)+IP)&amp;0xFFFFF</c> — the same physical
    /// the decode/fetch already compute (the linear entry, ADR 0019 Decision 1). Read once per block
    /// dispatch (NOT per instruction — chaining stays inside the emitted block). Reads the CPU through
    /// <see cref="ICpuCore.GetRegister(string)"/> — interface-only, AOT-clean, no per-dispatch reflection.</summary>
    uint ProjectBlockKey(ICpuCore cpu);
```

`ICpuCore` is in `CpuEmulator.Core`; `IJitTarget` is in `CpuEmulator.Core.Jit` — add `using CpuEmulator.Core;` only if not already resolvable (the file is in the `CpuEmulator.Core.Jit` namespace; `ICpuCore` is `CpuEmulator.Core.ICpuCore` — reference it fully-qualified as `CpuEmulator.Core.ICpuCore` to avoid a using churn). Use the fully-qualified spelling:

```csharp
    uint ProjectBlockKey(CpuEmulator.Core.ICpuCore cpu);
```

- [ ] **Step 2: Generate the per-CPU body in `EmitJitTarget`**

In `src/CpuEmulator.Generators/CpuEmitter.cs`, inside `EmitJitTarget`, after the `pc` local is resolved (line 182: `string pc = pcRegisterModel.Name;`), add a CS-detection local:

```csharp
        // ADR 0019 FF-1: the block-key projection. A spec that declares a "CS" register (the 8086)
        // folds the segmented origin ((CS<<4)+IP)&0xFFFFF — the linear physical the decode/fetch use.
        // A flat-PC CPU projects the identity (uint)PC (byte-identical to the old ushort key).
        bool hasCodeSegment = model.Registers.Any(r => r.Name == "CS");
```

Then, inside the `GeneratedJitTarget` class body emission (after the `RegisterNames` line, line 205, before the closing-brace line 206), append the projection member:

```csharp
        if (hasCodeSegment)
            sb.AppendLine(
                $"        public uint ProjectBlockKey(CpuEmulator.Core.ICpuCore cpu) => " +
                $"(uint)(((cpu.GetRegister(\"CS\") << 4) + cpu.GetRegister(\"{pc}\")) & 0xFFFFF);");
        else
            sb.AppendLine(
                $"        public uint ProjectBlockKey(CpuEmulator.Core.ICpuCore cpu) => " +
                $"(uint)cpu.GetRegister(\"{pc}\");");
```

- [ ] **Step 3: Build the generator + the CPU assemblies**

Run: `dotnet build src/CpuEmulator.Cpus.M8086 src/CpuEmulator.Cpus.Mos6502 src/CpuEmulator.Cpus.Z80 src/CpuEmulator.Cpus.M68000 -c Debug`
Expected: build SUCCEEDS — each CPU's generated partial now compiles a `GeneratedJitTarget.ProjectBlockKey`. (If a CPU build fails because `IJitTarget` now has an unimplemented member, the generator did not emit the body for it — verify `statusRegister`/`pcRegisterModel` are non-null for that CPU; only the minimal synthetic decode/flag fixtures skip `JitTarget` emission, and those are never driven through the JIT.)

- [ ] **Step 4: Inspect the generated 8086 + 6502 bodies**

Run: `dotnet build src/CpuEmulator.Cpus.M8086 -c Debug` then locate the generated file (under `obj/.../generated/` or the committed `*.g.cs`), and confirm the 8086 emitted:
```csharp
public uint ProjectBlockKey(CpuEmulator.Core.ICpuCore cpu) => (uint)(((cpu.GetRegister("CS") << 4) + cpu.GetRegister("IP")) & 0xFFFFF);
```
and the 6502/Z80 emitted `(uint)cpu.GetRegister("PC")`, the 68000 `(uint)cpu.GetRegister("PC")`.
Expected: the bodies match. (This is a sanity inspection, not a gate — Task 2 makes it un-fakeable.)

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core/Jit/IJitTarget.cs src/CpuEmulator.Generators/CpuEmitter.cs
git commit -m "feat(jit): add IJitTarget.ProjectBlockKey — per-CPU block-key projection (ADR 0019 FF-1)"
```

---

## Task 2: The `ProjectBlockKey` identity + 8086-fold + overlapping-segment unit tests

**Files:**
- Create: `tests/CpuEmulator.Tests/Jit/ProjectBlockKeyTests.cs`

**Interfaces:**
- Consumes: `<Cpu>.JitTarget` (the generated `public static readonly IJitTarget JitTarget` on each CPU partial); `IJitTarget.ProjectBlockKey(ICpuCore)`; `ICpuCore.SetRegister(string, ulong)`.
- Produces: the un-fakeable proof that (a) the flat CPUs' projection is the identity `(uint)PC`, (b) the 8086's fold is `((CS<<4)+IP)&0xFFFFF`, and (c) two `(CS,IP)` pairs that fold to the same physical produce the **same** key (the overlapping-segment coherence check, ADR 0019 Decision 4 gate 3).

- [ ] **Step 1: Write the failing tests**

Create `tests/CpuEmulator.Tests/Jit/ProjectBlockKeyTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Jit;

public class ProjectBlockKeyTests
{
    // The flat-PC CPUs project the identity (uint)PC — the ADR 0019 Decision 2 SAFE premise.
    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0x0001u)]
    [InlineData(0x00FFu)]
    [InlineData(0x0100u)]
    [InlineData(0x8000u)]
    [InlineData(0xFFFFu)]
    public void Mos6502_projects_the_identity_over_PC(uint pc)
    {
        var cpu = new Mos6502Cpu();
        cpu.SetRegister("PC", pc);
        Assert.Equal(pc, Mos6502Cpu.JitTarget.ProjectBlockKey(cpu));
    }

    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0x0100u)]
    [InlineData(0xFFFFu)]
    public void Z80_projects_the_identity_over_PC(uint pc)
    {
        var cpu = new Z80Cpu();
        cpu.SetRegister("PC", pc);
        Assert.Equal(pc, Z80Cpu.JitTarget.ProjectBlockKey(cpu));
    }

    [Theory]
    [InlineData(0x0000_0000u)]
    [InlineData(0x0000_1000u)]
    [InlineData(0x00FF_FFFEu)]
    public void M68000_projects_the_identity_over_PC(uint pc)
    {
        var cpu = new M68000Cpu();
        cpu.SetRegister("PC", pc);
        Assert.Equal(pc, M68000Cpu.JitTarget.ProjectBlockKey(cpu));
    }

    // The 8086 folds the segmented origin ((CS<<4)+IP)&0xFFFFF — the linear physical entry.
    [Theory]
    [InlineData(0x1000, 0x0100, 0x10100u)]
    [InlineData(0x2000, 0x0100, 0x20100u)]
    [InlineData(0x0000, 0x0000, 0x00000u)]
    [InlineData(0xFFFF, 0xFFFF, 0x0FFEFu)]   // (0xFFFF<<4 + 0xFFFF) & 0xFFFFF = 0x10FFEF & 0xFFFFF
    public void M8086_folds_the_segmented_origin(int cs, int ip, uint expected)
    {
        var cpu = new M8086Cpu();
        cpu.SetRegister("CS", (ulong)cs);
        cpu.SetRegister("IP", (ulong)ip);
        Assert.Equal(expected, M8086Cpu.JitTarget.ProjectBlockKey(cpu));
    }

    // ADR 0019 Decision 4 gate 3 — two (CS,IP) pairs that fold to the SAME physical byte produce the
    // SAME key (overlapping segments execute the same code; the linear key collapses them — the positive
    // case justifying linear over a composite (CS,IP) struct).
    [Fact]
    public void M8086_overlapping_segments_at_the_same_physical_project_the_same_key()
    {
        var a = new M8086Cpu();
        a.SetRegister("CS", 0x1000);   // (0x1000<<4)+0x0100 = 0x10100
        a.SetRegister("IP", 0x0100);

        var b = new M8086Cpu();
        b.SetRegister("CS", 0x1010);   // (0x1010<<4)+0x0000 = 0x10100 — same physical
        b.SetRegister("IP", 0x0000);

        Assert.Equal(
            M8086Cpu.JitTarget.ProjectBlockKey(a),
            M8086Cpu.JitTarget.ProjectBlockKey(b));
    }

    // The aliasing precondition (the FF-2 gate's positive half lives here at the projection layer): two
    // segments at the SAME IP offset but DIFFERENT physical fold to DIFFERENT keys.
    [Fact]
    public void M8086_same_offset_different_segment_projects_different_keys()
    {
        var a = new M8086Cpu();
        a.SetRegister("CS", 0x1000);
        a.SetRegister("IP", 0x0100);

        var b = new M8086Cpu();
        b.SetRegister("CS", 0x2000);
        b.SetRegister("IP", 0x0100);

        Assert.NotEqual(
            M8086Cpu.JitTarget.ProjectBlockKey(a),
            M8086Cpu.JitTarget.ProjectBlockKey(b));
    }
}
```

- [ ] **Step 2: Run the tests to verify they pass (the body shipped in Task 1)**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~ProjectBlockKeyTests" -c Debug`
Expected: PASS (all rows). The projection body was generated in Task 1; this test is its un-fakeable pin. If the 8086 fold row fails, the generator's `hasCodeSegment` branch or the mask is wrong — fix `EmitJitTarget`, not the test.

- [ ] **Step 3: Confirm the `0xFFFF/0xFFFF` row math**

The row `[InlineData(0xFFFF, 0xFFFF, 0x0FFEFu)]` encodes `((0xFFFF << 4) + 0xFFFF) & 0xFFFFF`. Compute: `0xFFFF << 4 = 0xFFFF0`; `+ 0xFFFF = 0x10FFEF`; `& 0xFFFFF = 0x0FFEF`. Confirm the test asserts `0x0FFEFu`.
Expected: matches (this guards the mask is applied AFTER the add, not before — the real 8086 segment wrap).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Jit/ProjectBlockKeyTests.cs
git commit -m "test(jit): ProjectBlockKey identity + 8086 fold + overlapping-segment coherence (ADR 0019 FF-1)"
```

---

## Task 3: Widen `CompiledBlock.EntryPc` and the `ChainDispatch` delegate to `uint`

**Files:**
- Modify: `src/CpuEmulator.Jit/CompiledBlock.cs:48-70` (`ChainDispatch` delegate `targetPc`; the ctor `entryPc`; the `EntryPc` property)

**Interfaces:**
- Consumes: nothing new.
- Produces: `CompiledBlock<TCpu>.EntryPc` is now `uint`; `ChainDispatch(uint targetPc, ref long budget, out BlockExit exit)`; the ctor takes `uint entryPc`. The widened delegate is the emitted chain-edge call target — its signature change ripples to `JittedCpu.ChainEdge` (Task 8) and the emitted IL constant (Task 6).

- [ ] **Step 1: Widen the `ChainDispatch` delegate**

In `src/CpuEmulator.Jit/CompiledBlock.cs:63`, change:
```csharp
    public delegate void ChainDispatch(ushort targetPc, ref long budget, out BlockExit exit);
```
to:
```csharp
    public delegate void ChainDispatch(uint targetPc, ref long budget, out BlockExit exit);
```

- [ ] **Step 2: Widen the ctor parameter and `EntryPc`**

In `src/CpuEmulator.Jit/CompiledBlock.cs:67-70`, change the ctor signature:
```csharp
internal sealed class CompiledBlock<TCpu>(ushort entryPc, BlockDelegate<TCpu> del, IReadOnlyCollection<int> spannedPages) where TCpu : class
```
to:
```csharp
internal sealed class CompiledBlock<TCpu>(uint entryPc, BlockDelegate<TCpu> del, IReadOnlyCollection<int> spannedPages) where TCpu : class
```
and the property at line 70:
```csharp
    public ushort EntryPc { get; } = entryPc;
```
to:
```csharp
    public uint EntryPc { get; } = entryPc;
```

- [ ] **Step 3: Build (expect downstream errors — that is the next tasks' work)**

Run: `dotnet build src/CpuEmulator.Jit -c Debug`
Expected: build FAILS with type-mismatch errors in `BlockCache.cs`/`ChainTable.cs`/`BlockCompiler.cs`/`JittedCpu.cs` (they still pass `ushort`). This is expected — Tasks 4–8 widen each consumer. Do not "fix" by re-narrowing `CompiledBlock`.

- [ ] **Step 4: Commit (WIP — the build is intentionally red mid-widening)**

```bash
git add src/CpuEmulator.Jit/CompiledBlock.cs
git commit -m "refactor(jit): widen CompiledBlock.EntryPc + ChainDispatch to uint (ADR 0019 FF-1) [WIP]"
```

---

## Task 4: Widen `ChainTable` to `uint`

**Files:**
- Modify: `src/CpuEmulator.Jit/ChainTable.cs:10-30`

**Interfaces:**
- Consumes: `CompiledBlock<TCpu>` (unchanged shape).
- Produces: `Link(uint successorPc, …)`, `InboundTo(uint successorPc)`, `Sever(uint successorPc)`; `_inbound` is `Dictionary<uint, HashSet<CompiledBlock<TCpu>>>`.

- [ ] **Step 1: Widen the `_inbound` field**

In `src/CpuEmulator.Jit/ChainTable.cs:10`, change:
```csharp
    private readonly System.Collections.Generic.Dictionary<ushort, System.Collections.Generic.HashSet<CompiledBlock<TCpu>>> _inbound = new();
```
to:
```csharp
    private readonly System.Collections.Generic.Dictionary<uint, System.Collections.Generic.HashSet<CompiledBlock<TCpu>>> _inbound = new();
```

- [ ] **Step 2: Widen the method signatures**

In `src/CpuEmulator.Jit/ChainTable.cs`, change every `ushort successorPc` parameter to `uint successorPc`:
- line 14: `public void Link(uint successorPc, CompiledBlock<TCpu> predecessor)`
- line 22: `public System.Collections.Generic.IReadOnlyCollection<CompiledBlock<TCpu>> InboundTo(uint successorPc)`
- line 30: `public void Sever(uint successorPc) => _inbound.Remove(successorPc);`

(Leave the method bodies unchanged — they only key/lookup by the parameter.)

- [ ] **Step 3: Build (still red downstream — `BlockCache`/`BlockCompiler` next)**

Run: `dotnet build src/CpuEmulator.Jit -c Debug`
Expected: fewer errors than Task 3, still FAILS in `BlockCache.cs`/`BlockCompiler.cs`/`JittedCpu.cs`.

- [ ] **Step 4: Commit**

```bash
git add src/CpuEmulator.Jit/ChainTable.cs
git commit -m "refactor(jit): widen ChainTable key to uint (ADR 0019 FF-1) [WIP]"
```

---

## Task 5: Widen `BlockCache` to `uint`

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCache.cs:26-104`

**Interfaces:**
- Consumes: `CompiledBlock<TCpu>` (now `uint` `EntryPc`), `BlockCompiler<TCpu>` (widened in Task 6).
- Produces: `GetOrCompile(uint pc, …)`, `ResolveChain(uint targetPc, …)`, `ShouldInterpret(uint pc)`, `NoteInterpretedDispatch(uint pc)`; `_blocks`/`_recompiles`/`_cooldown`/`_everHotPcs` keyed on `uint`. `TotalRecompiles`/`TotalEvictions`/`SmcHotPcCount` unchanged in type (they count, not key).

- [ ] **Step 1: Widen the keyed fields**

In `src/CpuEmulator.Jit/BlockCache.cs`, change the `ushort`-keyed collections:
- line 26: `private readonly System.Collections.Generic.Dictionary<uint, CompiledBlock<TCpu>> _blocks = new();`
- line 34: `private readonly System.Collections.Generic.Dictionary<uint, int> _recompiles = new();`
- line 35: `private readonly System.Collections.Generic.Dictionary<uint, int> _cooldown = new();`
- line 43: `private readonly System.Collections.Generic.HashSet<uint> _everHotPcs = new();`

(Leave `_blocksByPage` at line 27 unchanged — it is keyed on the **page index** `int`, not the block key, per ADR 0019 Decision 2.3. Do **not** touch it.)

- [ ] **Step 2: Widen the method signatures**

Change every `ushort pc`/`ushort targetPc` parameter to `uint`:
- line 51: `public bool ShouldInterpret(uint pc) => …` (body unchanged)
- line 57: `public void NoteInterpretedDispatch(uint pc)` (body unchanged)
- line 66: `public CompiledBlock<TCpu> GetOrCompile(uint pc, BlockCompiler<TCpu> compiler)` (body unchanged — it passes `pc` to `compiler.Compile` and keys `_blocks`/`_recompiles`/`_cooldown` by it; all now `uint`)
- line 99: `public CompiledBlock<TCpu> ResolveChain(uint targetPc, CompiledBlock<TCpu> predecessor, BlockCompiler<TCpu> compiler)` (body unchanged)

- [ ] **Step 3: Verify `Evict` keys by `block.EntryPc` (now `uint`)**

Read the `Evict` method (it removes from `_blocks` by `block.EntryPc`). Confirm it now compiles against `uint` keys with no body change. If `Evict` has an explicit `ushort` local for the entry PC, widen it to `uint`.
Expected: `Evict` compiles unchanged (it reads `block.EntryPc`, now `uint`).

- [ ] **Step 4: Build**

Run: `dotnet build src/CpuEmulator.Jit -c Debug`
Expected: still FAILS in `BlockCompiler.cs`/`JittedCpu.cs` (next tasks), but `BlockCache.cs` errors are gone.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCache.cs
git commit -m "refactor(jit): widen BlockCache keys to uint (page index untouched) (ADR 0019 FF-1) [WIP]"
```

---

## Task 6: Widen `BlockCompiler.Compile`/`Discover`/`EmitChainOrExit` to `uint`

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.cs:373` (`Discover`), `:443` (`Compile`), `:1391-1415` (`EmitChainOrExit` + the emitted constant)

**Interfaces:**
- Consumes: `IJitTarget.ProjectBlockKey` (not called here — the compiler keys by the entry it was handed); `M8086CodePhys(ushort)` (`:807`, unchanged — the 8086 decode origin); `_m8086CodePhysBase` (`:152`, the baked CS fold).
- Produces: `Compile(uint entryPc)`, `Discover(uint pc)`, `EmitChainOrExit(EmitContext ctx, uint staticTargetKey)`; the emitted chain-edge IL pushes a `uint` constant (the projected successor key).

- [ ] **Step 1: Widen `Discover`**

In `src/CpuEmulator.Jit/BlockCompiler.cs:373`, change:
```csharp
    public System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D, int Length, byte X86Seg)> Discover(ushort pc)
```
to:
```csharp
    public System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D, int Length, byte X86Seg)> Discover(uint pc)
```
Inside `Discover`, the loop walks instructions from the entry; the per-instruction `Pc` in the tuple stays `ushort` (it is the 16-bit IP/PC the decode + `M8086CodePhys(ushort)` consume — the **offset**, not the linear key). Where the entry `pc` (now `uint`) is first used to seed the walk's offset, narrow it explicitly to the offset: at the head of `Discover`, add/confirm a local:
```csharp
        ushort offset = (ushort)pc;   // the walk steps the 16-bit IP/PC; the uint entry IS the offset for a flat CPU, and the IP for the 8086 (CS folded separately into _m8086CodePhysBase).
```
and use `offset` where the old body used `pc` as the 16-bit walk cursor. (For a flat CPU `(ushort)pc == pc`; for the 8086 the `uint` entry equals `(CS<<4)+IP`, but the decode origin is `M8086CodePhys(offset)` with `_m8086CodePhysBase` carrying the CS half — so the 16-bit `offset` the walk uses is the IP, recovered as `(ushort)pc` ONLY when the base is consistent. See Step 2's note: the cleaner invariant is that `Discover`/`Compile` are still called with the **offset-space** value the dispatcher hands them; confirm in Step 2.)

- [ ] **Step 2: Widen `Compile` and reconcile the entry/offset relationship**

In `src/CpuEmulator.Jit/BlockCompiler.cs:443`, change:
```csharp
    public CompiledBlock<TCpu> Compile(ushort entryPc)
```
to:
```csharp
    public CompiledBlock<TCpu> Compile(uint entryPc)
```

**Reconcile the key vs offset (the load-bearing FF-1 invariant).** Today the dispatcher hands `Compile` the **16-bit IP** and `Discover` walks from it; `_m8086CodePhysBase` is baked from the live CS (`:380`) so `M8086CodePhys(ip)` is correct. After FF-1, the dispatcher hands `Compile` the **`uint` projected key** (`(CS<<4)+IP` for the 8086, `(uint)PC` for the flat CPUs). For the **flat CPUs the key equals the offset** — no change. For the **8086 the key is the linear physical**, but the decode still needs the 16-bit IP offset. Recover it inside `Compile`/`Discover`:
```csharp
        // ADR 0019 FF-1: the dispatcher hands the uint linear key. The flat CPUs' key IS the offset.
        // The 8086's key is (CS<<4)+IP; the decode origin needs the 16-bit IP offset, which is the key
        // minus the baked code-phys base (the CS<<4 half). _m8086CodePhysBase is set from the live CS
        // at the head of Discover/Compile (:380-382), so this recovers the live IP exactly.
        ushort offset = TargetIsM8086
            ? (ushort)((entryPc - _m8086CodePhysBase) & 0xFFFF)
            : (ushort)entryPc;
```
Pass `offset` (not `entryPc`) where the body previously used the 16-bit cursor (the decode walk, `M8086CodePhys(offset)`, the per-instruction `Pc`). Pass `entryPc` (the `uint` key) to the `CompiledBlock<TCpu>` ctor's `entryPc` (so `EntryPc` is the linear key, matching the cache key and `_blocks` lookup).

**Order note:** `_m8086CodePhysBase` is assigned at `:380-382` at the head of `Discover` from the live CS. `Compile` calls `Discover` (or sets the base itself). Ensure the base is set **before** the `offset` recovery — if `Compile` sets the base inline (mirroring `:380`), put the `offset` line after it; if `Compile` delegates to `Discover` for the base, recover `offset` inside `Discover` after the base assignment and thread it through. Read the actual `Compile`/`Discover` body to place the recovery correctly; the invariant is: **base set → offset recovered → decode uses offset → block keyed on the `uint` entry.**

- [ ] **Step 3: Widen `EmitChainOrExit` and the emitted constant**

In `src/CpuEmulator.Jit/BlockCompiler.cs:1391`, change:
```csharp
    private void EmitChainOrExit(EmitContext ctx, ushort staticTargetPc)
```
to:
```csharp
    private void EmitChainOrExit(EmitContext ctx, uint staticTargetKey)
```
and the emitted constant push at `:1414-1415`:
```csharp
        il.Emit(OpCodes.Ldc_I4, (int)staticTargetPc);
        il.Emit(OpCodes.Conv_U2);
```
to push the full `uint` key (no `Conv_U2` narrowing):
```csharp
        il.Emit(OpCodes.Ldc_I4, unchecked((int)staticTargetKey));
        // No Conv_U2 — the chain target is now the full uint linear key (ADR 0019 FF-1). The chain
        // callback (ChainDispatch) takes uint; pushing the 32-bit constant as-is.
```
(The `ChainDispatch` delegate is `uint` after Task 3, so the IL stack type matches. Confirm the call-site IL that invokes the chain delegate expects a `uint`/`int` on the stack — it does, the delegate's first param is now `uint`.)

- [ ] **Step 4: Build `CpuEmulator.Jit`**

Run: `dotnet build src/CpuEmulator.Jit -c Debug`
Expected: now only `JittedCpu.cs` errors remain (the dispatcher local + `ChainEdge`), plus the 8086 near-flow `EmitChainOrExit` call sites in `BlockCompiler.M8086.cs` (Task 7) now pass a `ushort` to a `uint` param — those are widening-implicit and compile, but the **value** is wrong for the 8086 until Task 7 folds them. The flat-CPU call sites compile and are correct (`(ushort)target` widens to the same `uint` key).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCompiler.cs
git commit -m "refactor(jit): Compile/Discover/EmitChainOrExit take the uint linear key; recover the 8086 IP offset (ADR 0019 FF-1) [WIP]"
```

---

## Task 7: Fold the 8086 near-flow static chain target through the baked code-phys base

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.M8086.cs` (the near-flow `EmitChainOrExit` call sites: `:1192`, `:1194`, and any other `EmitChainOrExit(ctx, <ushort target>)` in the near arm)

**Interfaces:**
- Consumes: `_m8086CodePhysBase` (`:152`); `EmitChainOrExit(EmitContext, uint)` (Task 6); the near arm's `target`/`fallThrough` `ushort` IP locals.
- Produces: the 8086 near-flow chain edges now push the **projected `uint` key** `(_m8086CodePhysBase + target) & 0xFFFFF` — equal to the dispatch-time `ProjectBlockKey` for the same `(CS, IP)`. **No new far arm.**

> **Why this task exists (ADR 0019 Decision 2 note):** the near arm's static chain target is an IP **within the same CS**. Before FF-1 the chain edge pushed the bare 16-bit IP; after FF-1 the cache is keyed on the linear physical, so the near edge must push `(CS<<4)+IP` — the **same** physical the successor will be keyed/decoded under. The base is baked and the target is a compile-time constant, so the folded key is still a compile-time constant. This keeps the existing near-flow chaining correct under the widened key; **it is not a far-emit change.**

- [ ] **Step 1: Write a failing 8086 near-flow chaining pin**

Add to `tests/CpuEmulator.Tests/Jit/` (extend the existing 8086 flow-emit fixture, e.g. `M8086FlowEmitTests.cs`, or create `M8086NearChainKeyTests.cs`). The test compiles a near `JMP`/`CALL` at a non-zero CS and asserts the resulting chain still links + the block keys equal the projected linear key:

```csharp
[Fact]
public void Near_jmp_under_a_nonzero_cs_chains_to_the_linear_key()
{
    // A near JMP at CS=0x2000, IP=0x0100 to IP=0x0120: the successor block must be keyed on the
    // LINEAR physical (0x2000<<4)+0x0120 = 0x20120, not the bare IP 0x0120.
    var harness = M8086JitHarness.AtSegment(cs: 0x2000, ip: 0x0100);   // see harness note below
    harness.WriteCode(0x0100, /* JMP +0x1E (near, to IP 0x0120) */ 0xEB, 0x1E);
    harness.WriteCode(0x0120, /* NOP */ 0x90);

    harness.RunOneDispatch();   // compiles the block at the entry, takes the near chain edge

    Assert.True(harness.Cache.ContainsBlockKey(0x20120u));   // the linear successor key
    Assert.False(harness.Cache.ContainsBlockKey(0x00120u));  // NOT the bare IP
    Assert.True(harness.ChainStepCount > 0);                 // the chain edge was taken
}
```

**Harness note (TDD-shaping):** if no `M8086JitHarness`/`ContainsBlockKey` seam exists, add a minimal internal test seam: a `BlockCache.ContainsBlockKey(uint)` (`internal bool ContainsBlockKey(uint key) => _blocks.ContainsKey(key);`) exposed via `InternalsVisibleTo` (already present for the Jit test assembly — confirm `AssemblyInfo`/`csproj`), and drive the dispatch via the existing `JittedCpu<M8086Cpu>` construction the other 8086 flow-emit tests already use (mirror `M8086FlowEmitTests.cs`'s setup — read it for the exact wiring; do not invent a new harness if one exists).

- [ ] **Step 2: Run it to verify it FAILS pre-fold**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Near_jmp_under_a_nonzero_cs_chains_to_the_linear_key" -c Debug`
Expected: FAIL — the near arm still pushes the bare IP `0x0120` (the chain edge keys `0x00120`, not `0x20120`), so `ContainsBlockKey(0x20120u)` is false. This is the pre-fold red.

- [ ] **Step 3: Fold the near-flow chain targets**

In `src/CpuEmulator.Jit/BlockCompiler.M8086.cs`, at each near-flow `EmitChainOrExit(ctx, <target>)` call site (e.g. `:1192` `EmitChainOrExit(ctx, target);` and `:1194` `EmitChainOrExit(ctx, fallThrough);`), wrap the 16-bit target in the baked-base fold. Add a private helper near `M8086CodePhys`:

```csharp
    /// <summary>ADR 0019 FF-1: project a same-segment near-flow IP target to the linear block key the
    /// dispatcher will compute for it — (_m8086CodePhysBase + ip) & 0xFFFFF. The base is the baked CS<<4
    /// (set at the head of Discover/Compile, :380-382), so for a compile-time-constant IP this is a
    /// compile-time-constant uint key. Used ONLY for the near arm's static chain edges (a near transfer
    /// cannot change CS, so the successor is in the same baked segment).</summary>
    private uint M8086NearChainKey(ushort ip) => (_m8086CodePhysBase + ip) & 0xFFFFFu;
```
and change the call sites:
```csharp
        EmitM8086SetIp(ctx, target); EmitChainOrExit(ctx, M8086NearChainKey(target));
```
```csharp
        EmitM8086SetIp(ctx, fallThrough); EmitChainOrExit(ctx, M8086NearChainKey(fallThrough));
```
(Apply to **every** near-flow `EmitChainOrExit` in `BlockCompiler.M8086.cs` — read the file and fold each. The flat-CPU `EmitChainOrExit` call sites in `BlockCompiler.cs` are NOT touched here — for them `(uint)target` is already the correct identity key.)

- [ ] **Step 4: Run the pin to verify it PASSES**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Near_jmp_under_a_nonzero_cs_chains_to_the_linear_key" -c Debug`
Expected: PASS — the successor is keyed `0x20120`, the chain edge is taken.

- [ ] **Step 5: Run the existing 8086 near-flow parity to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~M8086FlowEmit|FullyQualifiedName~M8086Mov" -c Debug`
Expected: PASS — the existing near-flow TomHarte-through-JIT parity is unchanged (the fold only changes the chain **key**, not the emitted control flow or flags).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCompiler.M8086.cs tests/CpuEmulator.Tests/Jit/
git commit -m "fix(jit): fold 8086 near-flow chain targets through the baked CS base — same-segment linear key (ADR 0019 FF-1)"
```

---

## Task 8: Wire the dispatcher — `uint key = _target.ProjectBlockKey(_inner)` + widen `ChainEdge`

**Files:**
- Modify: `src/CpuEmulator.Jit/JittedCpu.cs:129-174` (the `Run` dispatcher) + `:210-221` (`ChainEdge`)

**Interfaces:**
- Consumes: `IJitTarget.ProjectBlockKey(ICpuCore)` (Task 1); `_cache.ShouldInterpret(uint)`/`NoteInterpretedDispatch(uint)`/`GetOrCompile(uint)`/`ResolveChain(uint)` (Task 5).
- Produces: the dispatcher computes the `uint` key once per block dispatch and threads it through. The `ChainEdge` callback takes `uint targetPc` (matching the widened `ChainDispatch`).

- [ ] **Step 1: Replace the dispatcher's PC read with the projection**

In `src/CpuEmulator.Jit/JittedCpu.cs:153`, change:
```csharp
            var pc = (ushort)_inner.GetRegister(_pcName);
```
to:
```csharp
            // ADR 0019 FF-1: the block-cache key is the per-CPU linear projection (the flat CPUs' is
            // (uint)PC — identical to the old ushort read; the 8086's folds (CS<<4)+IP). Read once per
            // block dispatch (chaining stays inside the emitted block).
            uint key = _target.ProjectBlockKey(_inner);
```
Then update the three call sites that used `pc`:
- line 160: `if (_cache.ShouldInterpret(key))`
- line 165: `_cache.NoteInterpretedDispatch(key);`
- line 168: `CompiledBlock<TCpu> block = _cache.GetOrCompile(key, _compiler);`

(Read the full `Run` body — if `pc` is used anywhere else in the loop, e.g. a trace/log line, change it to `key`. The `_pcName` field stays — it is still the ProgramCounterName the projection's generated body reads via `GetRegister`, and may be used elsewhere; do not remove it unless the compiler flags it unused.)

- [ ] **Step 2: Widen `ChainEdge`**

In `src/CpuEmulator.Jit/JittedCpu.cs:210`, change:
```csharp
    private void ChainEdge(ushort targetPc, ref long budget, out BlockExit exit)
```
to:
```csharp
    private void ChainEdge(uint targetPc, ref long budget, out BlockExit exit)
```
The body at `:220-221` calls `_cache.ShouldInterpret(targetPc)` and `_cache.ResolveChain(targetPc, …)` — both now `uint`, compiles unchanged. Confirm `_chainDispatch` is assigned `ChainEdge` (a `ChainDispatch` — now `uint`-first); the assignment compiles since the delegate widened in Task 3.

- [ ] **Step 3: Build the whole JIT + CPU stack**

Run: `dotnet build src/CpuEmulator.Jit -c Debug && dotnet build -c Debug`
Expected: build SUCCEEDS — the widening is complete end to end.

- [ ] **Step 4: Run the full 8086 + flat-CPU JIT parity smoke**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Jit" -c Debug`
Expected: PASS — TomHarte-through-JIT (all four CPUs), chaining, SMC, the new `ProjectBlockKey`/near-chain pins all green. (The full byte-for-byte identity regression is Task 9.)

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Jit/JittedCpu.cs
git commit -m "feat(jit): dispatcher keys on the uint linear ProjectBlockKey; widen ChainEdge (ADR 0019 FF-1)"
```

---

## Task 9: The byte-for-byte non-segmented identity regression (the SAFE merge gate)

**Files:**
- Create: `tests/CpuEmulator.Tests/Jit/KeyWideningIdentityTests.cs`

**Interfaces:**
- Consumes: the existing JIT test seams `CompileCount`, `TotalRecompiles`, `TotalEvictions`, `SmcHotPcCount`, `ChainStepCount` (on `JittedCpu<TCpu>`, `:87-97`); `FallbackEmitCount` (on `BlockCompiler<TCpu>`, `:31`); the existing 6502/Z80/68000 TomHarte/Klaus/ZEXALL/SingleStep JIT runners.
- Produces: the un-fakeable SAFE proof — running the flat-CPU sweeps and asserting the **measured invariants** (block count, chain steps, evictions, recompiles, `FallbackEmitCount`) match the pinned pre-widening values. ADR 0019 Decision 2's "non-segmented CPUs unchanged" claim, made un-fakeable.

> **TDD-shaping note (the identity gate's honest form).** A literal "before vs after" diff requires capturing the pre-widening numbers. Because the widening is the same PR, the gate is expressed as **pinned invariants**: the flat CPUs' JIT-sweep counters equal the values they have on `main` *before* FF-1. Capture those values once at the start of FF-1 (run the existing sweeps on pre-FF-1 `main`, record the counters) and hard-code them as the expected constants here, with a comment citing the capture commit. The gate then **fails** if the widening perturbs any flat-CPU counter. This is the "fails-if-it-changed-anything" discipline ADR 0019 §2.1 / Decision 2 require.

- [ ] **Step 1: Capture the pre-widening baselines (do this BEFORE Task 3)**

> **Run this step at the START of FF-1, on `main` before any widening commit.** `git stash` or branch-point: with `main` checked out at the FF-1 base commit, run the flat-CPU JIT sweeps and record the counters. Concretely, add a temporary throwaway test (or run the existing sweeps with a counter dump) that prints, for each of 6502/Z80/68000: total blocks compiled (`CompileCount`), `ChainStepCount`, `TotalEvictions`, `TotalRecompiles`, and the CPU's `FallbackEmitCount` over a fixed, deterministic workload (e.g. a fixed Klaus run to a fixed cycle budget for the 6502; a fixed ZEXDOC slice for the Z80; a fixed SingleStep slice for the 68000). Record the exact numbers.

Run (on pre-FF-1 `main`): the existing flat-CPU JIT sweep, with counter output.
Expected: a recorded tuple per CPU, e.g. `6502 Klaus@<budget>: blocks=<N>, chains=<C>, evict=<E>, recompile=<R>, fallback=<F>`. **Write these numbers into the test as the expected constants in Step 2.**

- [ ] **Step 2: Write the identity regression test with the captured constants**

Create `tests/CpuEmulator.Tests/Jit/KeyWideningIdentityTests.cs`. Use the **actual captured values** from Step 1 in place of the `<…>` placeholders below (the structure is fixed; the constants are the recorded baselines):

```csharp
using CpuEmulator.Core.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0019 FF-1 SAFE gate: the ushort->uint key widening leaves the non-segmented CPUs
/// (6502/Z80/68000) byte-for-byte unchanged — same blocks, chains, evictions, recompiles, fallback.
/// The expected constants are the pre-FF-1 baselines captured on main @ &lt;BASE_COMMIT&gt; over the
/// fixed deterministic workloads below. The gate FAILS if the widening perturbs any flat-CPU counter.</summary>
public class KeyWideningIdentityTests
{
    [Fact]
    public void Mos6502_jit_sweep_is_byte_identical_after_the_key_widening()
    {
        var r = JitSweepHarness.RunMos6502Klaus(cycleBudget: /* <fixed budget> */ 0);
        Assert.Equal(/* <captured blocks> */ 0, r.CompileCount);
        Assert.Equal(/* <captured chains> */ 0L, r.ChainStepCount);
        Assert.Equal(/* <captured evict> */ 0L, r.TotalEvictions);
        Assert.Equal(/* <captured recompile> */ 0L, r.TotalRecompiles);
        Assert.Equal(/* <captured fallback> */ 0, r.FallbackEmitCount);
    }

    [Fact]
    public void Z80_jit_sweep_is_byte_identical_after_the_key_widening()
    {
        var r = JitSweepHarness.RunZ80Zexdoc(cycleBudget: /* <fixed budget> */ 0);
        Assert.Equal(/* <captured blocks> */ 0, r.CompileCount);
        Assert.Equal(/* <captured chains> */ 0L, r.ChainStepCount);
        Assert.Equal(/* <captured evict> */ 0L, r.TotalEvictions);
        Assert.Equal(/* <captured recompile> */ 0L, r.TotalRecompiles);
        Assert.Equal(/* <captured fallback> */ 0, r.FallbackEmitCount);
    }

    [Fact]
    public void M68000_jit_sweep_is_byte_identical_after_the_key_widening()
    {
        var r = JitSweepHarness.RunM68000SingleStepSlice(count: /* <fixed N> */ 0);
        Assert.Equal(/* <captured blocks> */ 0, r.CompileCount);
        Assert.Equal(/* <captured chains> */ 0L, r.ChainStepCount);
        Assert.Equal(/* <captured evict> */ 0L, r.TotalEvictions);
        Assert.Equal(/* <captured recompile> */ 0L, r.TotalRecompiles);
        Assert.Equal(/* <captured fallback> */ 0, r.FallbackEmitCount);
    }
}
```

**Harness note (TDD-shaping):** if no `JitSweepHarness` with these exact run methods exists, write a thin one in the test project that constructs `JittedCpu<Mos6502Cpu>`/`<Z80Cpu>`/`<M68000Cpu>` over the existing Klaus/ZEXDOC/SingleStep fixtures (mirror how the existing JIT-parity tests build each — read them first), runs the fixed workload, and returns a record `(int CompileCount, long ChainStepCount, long TotalEvictions, long TotalRecompiles, int FallbackEmitCount)`. Reuse the existing fixture-loading the parity tests already use — do not re-implement Klaus/ZEXDOC loading. Prefer extending an existing sweep test over a new harness if one already exposes these counters.

- [ ] **Step 3: Run the identity regression — verify PASS post-widening**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~KeyWideningIdentityTests" -c Debug`
Expected: PASS — every flat-CPU counter equals its captured baseline. **If any fails, the widening perturbed a non-segmented CPU — STOP and investigate (this would flip the classification away from SAFE); do not adjust the baselines to match.**

- [ ] **Step 4: Run the FULL Release suite (the load-bearing regression)**

Run: `dotnet test -c Release`
Expected: green + warning-clean. The full suite includes all four CPUs' TomHarte-through-JIT parity, the chaining/SMC/`FallbackEmitCount` pins, and the new FF-1 tests. The 8086 corpus stays green (it is single-step — the far-aliasing case is not exercised here; FF-2 adds it). Record the pass/fail/skip counts for the PR body.

- [ ] **Step 5: Confirm the far opcodes are STILL fallback (no far emit in FF-1)**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~M8086" -c Debug`
Verify (read the test output / add a one-line assertion if a far-fallback pin exists) that the 8086's `FallbackEmitCount` over a corpus containing `9A`/`EA`/`CB`/`CA`/`FF /3`/`FF /5` is **unchanged from pre-FF-1** — those opcodes are still fallback. FF-1 must not emit any far op.
Expected: the far opcodes remain in the fallback path (`FallbackEmitCount` unchanged for them).

- [ ] **Step 6: Commit**

```bash
git add tests/CpuEmulator.Tests/Jit/KeyWideningIdentityTests.cs
git commit -m "test(jit): byte-for-byte non-segmented identity regression — the FF-1 SAFE gate (ADR 0019)"
```

---

## Self-Review

**Spec coverage (against ADR 0019 §5 FF-1 scope + Decision 2 + Decision 4 gate 3):**
- Widen the cache key `ushort → uint` across `BlockCache`/`ChainTable`/`CompiledBlock`/`JittedCpu`/`BlockCompiler` — Tasks 3, 4, 5, 6, 8. ✓
- Add `IJitTarget.ProjectBlockKey` with identity (6502/Z80/68000) + `((CS<<4)+IP)&0xFFFFF` (8086) — Task 1. ✓
- Fold the existing 8086 near-flow static chain target through the baked code-phys base (Decision 2 note) — Task 7. ✓
- Replace the dispatcher's `ushort pc` with `uint key = _target.ProjectBlockKey(_inner)` — Task 8. ✓
- Gate: the key-projection identity regression (flat CPUs byte-identical) — Task 9. ✓
- Gate: `ProjectBlockKey == (uint)PC` unit test per non-segmented CPU — Task 2. ✓
- Gate: the existing 8086 near-flow parity stays green — Task 7 Step 5 + Task 9 Step 4. ✓
- Gate: the overlapping-segment coherence check (Decision 4 gate 3) — Task 2 (`M8086_overlapping_segments_at_the_same_physical_project_the_same_key`). ✓
- No far emit; far opcodes' `FallbackEmitCount` unchanged — Task 9 Step 5 + the Global Constraints. ✓
- `_blocksByPage` (page index) and the dirty-page/SMC machinery untouched (Decision 2.3) — Task 5 Step 1 explicitly excludes `_blocksByPage`. ✓

**Placeholder scan:** the only intentional `<…>` placeholders are the **captured baseline constants** in Task 9 Step 2 — these are filled from the Step 1 measurement (the plan cannot pre-know them; the procedure to obtain them is explicit). All code steps show literal code. No "TBD"/"implement later"/"similar to Task N".

**Type consistency:** `ProjectBlockKey(ICpuCore) → uint` is consistent across Task 1 (interface + generator), Task 2 (tests), Task 8 (call site). `ChainDispatch(uint targetPc, …)` consistent across Task 3 (delegate), Task 6 (emitted constant), Task 8 (`ChainEdge`). `EntryPc:uint` (Task 3) keyed in `_blocks` (Task 5) and constructed from the `uint` entry (Task 6 Step 2). The 8086 `offset = (ushort)((entryPc - _m8086CodePhysBase) & 0xFFFF)` (Task 6) and `M8086NearChainKey(ip) = (_m8086CodePhysBase + ip) & 0xFFFFF` (Task 7) are the inverse fold pair — consistent. `_blocksByPage` stays `int`-keyed (page index) throughout.

**One open dependency for the implementer:** Task 6 Step 2 requires reading the real `Compile`/`Discover` body to place the `_m8086CodePhysBase`-set-before-`offset`-recover ordering correctly — the plan states the invariant precisely (base set → offset recovered → decode uses offset → block keyed on the `uint` entry) but the exact line placement depends on whether `Compile` sets the base inline or via `Discover`. This is a read-and-place step, not a design gap.
