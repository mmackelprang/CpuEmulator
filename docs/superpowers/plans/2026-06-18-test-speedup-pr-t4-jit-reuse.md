# PR-T4 — Per-worker JIT reuse (lever 4, the most invasive — gated on an equivalence proof)

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans`, task-by-task. Steps use checkbox (`- [ ]`) syntax.
> **Read first (binding):** `2026-06-18-test-speedup-arc-overview.md` — ESPECIALLY the "lever-4 block-cache
> isolation hazard" section. Depends on **PR-T2** (pooled, re-zeroed bus) AND **PR-T3** (the JIT sweeps are split
> per-partition, so "per worker thread" is meaningful). Lands LAST. Branch `test/speedup-jit-reuse` → PR → main.
> **This PR touches production code** (`src/CpuEmulator.Jit/`) — it adds a cache-flush seam — so it lands on a
> branch + PR per the workflow.
> **DROP-GATE:** if Task 6's byte-for-byte equivalence proof fails, this PR is abandoned; the arc still banks T1–T3.

**Goal:** Stop reconstructing a fresh `JittedCpu`/`BlockCompiler` per case in the JIT TomHarte sweeps (JIT warm-up
is paid `sampleSize`× per file per opcode today). Reuse ONE `JittedCpu` per worker thread, RESET between cases
(flush the block cache + dirty map + chains; the pooled bus from T2 is already re-zeroed), so the compiler/cache
machinery is built once per worker, not once per case.

**Architecture:** Add a `BlockCache.FlushAll()` seam (clears `_blocks`, `_blocksByPage`, `Dirty`, `Chains`) and a
`JittedCpu.ResetForReuse()` that flushes the cache + clears the per-run chain state + resets the inner CPU. The JIT
runners rent a `[ThreadStatic]` `JittedCpu` bound to the T2 pooled bus; per case they re-zero the bus (T2),
re-install the case's RAM, `ResetForReuse()`, set initial registers, run. Because T2 pools the SAME backing array
(re-zeroed, not reallocated), Fastmem's `PageBacking[]` snapshot stays valid across cases — only the BLOCK CACHE
must be flushed.

**Tech Stack:** C# JIT layer (`JittedCpu<TCpu>`, `BlockCache<TCpu>`, `ChainTable<TCpu>`, `Fastmem`), `[ThreadStatic]`.

---

## What the recon CONFIRMED (file:line — verified against `main` @ `896f88b`)

| # | Fact | Evidence |
|---|------|----------|
| F1 | `JittedCpu` builds `new Fastmem`, `new BlockCache`, `new BlockCompiler` per construction | `JittedCpu.cs:76-78` |
| F2 | The only reset today is `Reset() => _inner.Reset()` — inner CPU ONLY, NOT the block cache | `JittedCpu.cs:91` |
| F3 | `BlockCache<TCpu>` state to flush: `_blocks` (keyed `ushort pc`), `_blocksByPage`, `Dirty`, `Chains` | `BlockCache.cs:25-31` |
| F4 | `ChainTable<TCpu>` is a single `_inbound` dictionary (trivially clearable) | `ChainTable.cs:10,30,34` |
| F5 | The dispatch cache key is `(ushort)IP` — REUSED across cases with DIFFERENT bytes → the hazard | `M8088TomHarteRunner.cs:115-119` |
| F6 | The 4 JIT factories build a fresh `JittedCpu` per case from the per-case bus | `*JittedCpuFactory.cs` (e.g. `M8086JittedCpuFactory.cs:15-20`) |
| F7 | T2 pools the SAME backing array and re-zeroes it (not realloc) → Fastmem's backing refs stay valid | PR-T2 Task 1 `ClearMappedBacking` (clears contents, keeps the array + mapping) |

**The hazard (restated):** across cases the same `(ushort)IP` maps to different bytes. A reused `JittedCpu` that
keeps case A's compiled block would run case A's code for case B at the same IP → silent wrong answer. `FlushAll()`
between cases is mandatory and is what makes reuse correct. The equivalence proof (Task 6) is the guarantee.

---

## File structure

- **Modify:** `src/CpuEmulator.Jit/ChainTable.cs` — add `Clear()`.
- **Modify:** `src/CpuEmulator.Jit/BlockCache.cs` — add `FlushAll()`.
- **Modify:** `src/CpuEmulator.Jit/JittedCpu.cs` — add `ResetForReuse()` (flush cache + clear chain state + inner reset).
- **Modify:** the 4 `*JittedCpuFactory.cs` + the JIT runner paths to rent a `[ThreadStatic]` reused `JittedCpu`.

---

## Task 1: Add `ChainTable.Clear()` and `BlockCache.FlushAll()` (production seam)

**Files:**
- Modify: `src/CpuEmulator.Jit/ChainTable.cs`
- Modify: `src/CpuEmulator.Jit/BlockCache.cs`

- [ ] **Step 1: Write the failing test.** Create `tests/CpuEmulator.Tests/Jit/BlockCacheFlushTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

public class BlockCacheFlushTests
{
    // A reused JIT must NOT run a stale block when the SAME PC is recompiled from DIFFERENT bytes after a flush.
    [Fact]
    public void FlushAll_makes_the_same_PC_recompile_from_new_bytes()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;

        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var ram = new byte[0x10000];
        space.MapMemory(0x0000, ram, writable: true);

        // Case A at PC 0x0200: LDA #$11 ; (A9 11)  then a parking JMP * so Run stops.
        ram[0x0200] = 0xA9; ram[0x0201] = 0x11; ram[0x0202] = 0x4C; ram[0x0203] = 0x02; ram[0x0204] = 0x02;
        var cpu = new Mos6502Cpu(space) { PC = 0x0200 };
        var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space);
        long budget = 10; jit.Run(ref budget);
        Assert.Equal(0x11, cpu.A);   // ran case A

        // Reuse: re-zero, install Case B at the SAME PC 0x0200: LDA #$22 ; (A9 22) then park.
        space.ClearMappedBacking(ram);
        ram[0x0200] = 0xA9; ram[0x0201] = 0x22; ram[0x0202] = 0x4C; ram[0x0203] = 0x02; ram[0x0204] = 0x02;
        cpu.A = 0; cpu.PC = 0x0200;
        jit.ResetForReuse();         // <-- the new seam: flush cache so 0x0200 recompiles from the NEW bytes
        budget = 10; jit.Run(ref budget);
        Assert.Equal(0x22, cpu.A);   // ran case B, NOT the stale case-A block (which would leave A=0x11)
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~BlockCacheFlushTests" --no-restore`
Expected: FAIL — `ResetForReuse` not defined (and, if stubbed to no-op, the assert would catch the stale 0x11).

- [ ] **Step 3: Add `ChainTable.Clear()`.** In `src/CpuEmulator.Jit/ChainTable.cs`, after `Forget` (`:38`):

```csharp
    /// <summary>Drop ALL inbound links — the per-worker REUSE reset (lever 4). After this the chain table is
    /// empty, as if freshly constructed; the next run rebuilds links by PC on its chain edges.</summary>
    public void Clear() => _inbound.Clear();
```

- [ ] **Step 4: Add `BlockCache.FlushAll()`.** In `src/CpuEmulator.Jit/BlockCache.cs`, after `InvalidateIfDirty`
  (`:77`):

```csharp
    /// <summary>Evict EVERY block and reset all derived state — the per-worker REUSE reset (lever 4). After this
    /// the cache is byte-equivalent to a freshly constructed BlockCache(pageCount): no compiled blocks, no
    /// per-page index, no inbound chain links, no dirty marks. The next GetOrCompile recompiles from the CURRENT
    /// bus bytes — which is the whole point: the dispatch key is (ushort)PC and the SAME PC carries different bytes
    /// across reused cases, so a stale block would silently run the wrong case's code.</summary>
    public void FlushAll()
    {
        _blocks.Clear();
        _blocksByPage.Clear();
        Chains.Clear();
        Dirty.Clear();
    }
```

- [ ] **Step 5: Add `JittedCpu.ResetForReuse()`.** In `src/CpuEmulator.Jit/JittedCpu.cs`, after `Reset()` (`:91`):

```csharp
    /// <summary>Reset this JittedCpu for REUSE on a new test case bound to the SAME (re-zeroed, re-installed) bus
    /// — the per-worker reuse seam (lever 4). Flushes the block cache (so the SAME PC recompiles from the new
    /// case's bytes — the block-cache-isolation invariant), clears the per-run chain-walk state, and resets the
    /// inner CPU. Fastmem is NOT rebuilt: the pooled bus (PR-T2) re-zeroes the SAME backing array in place, so
    /// Fastmem's PageBacking[] snapshot still points at the live backing — only its CONTENTS changed, which the
    /// emitted code reads at run time. (If a future pooled bus REMAPS to a different backing array, also rebuild
    /// Fastmem here; today it does not.)</summary>
    public void ResetForReuse()
    {
        _cache.FlushAll();
        _chainPredecessor = null;
        _chainNext = null;
        _chainDispatch = null;
        _inner.Reset();
    }
```

- [ ] **Step 6: Run it to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~BlockCacheFlushTests" --no-restore`
Expected: PASS (A=0x22 — the flush forced recompilation from the new bytes).

- [ ] **Step 7: Full JIT-layer regression (the seam is additive; existing behaviour must not move)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~Jit" --no-restore`
Expected: PASS (the existing JIT spot tests, differential fuzzer, chain pins all green — `FlushAll`/`ResetForReuse`
are new methods nothing else calls yet).

- [ ] **Step 8: Commit**

```bash
git add src/CpuEmulator.Jit/ChainTable.cs src/CpuEmulator.Jit/BlockCache.cs src/CpuEmulator.Jit/JittedCpu.cs \
        tests/CpuEmulator.Tests/Jit/BlockCacheFlushTests.cs
git commit -m "feat(jit): BlockCache.FlushAll + JittedCpu.ResetForReuse reuse seam (lever 4)"
```

---

## Task 2: Reuse the JittedCpu per worker in the 8088 JIT path

The 8088 JIT runner (`RunCaseThroughJit`) builds a fresh `JittedCpu<M8086Cpu>` per case via `M8086JittedCpuFactory`.
Rent a `[ThreadStatic]` reused one bound to the T2 pooled bus.

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/M8088TomHarteRunner.cs` (the JIT path — `:121` onward, the
  `RunCaseThroughJit` site at `:124-125` already pools the bus after T2)
- Modify: `tests/CpuEmulator.Tests/TomHarte/M8086JittedCpuFactory.cs` (no change to its signature; reused via a rent)

- [ ] **Step 1: Add the `[ThreadStatic]` JIT rent.** In `M8088TomHarteRunner`, alongside the T2 `RentBus()` pool:

```csharp
    // Per-worker reused JIT (lever 4). Built ONCE per worker thread bound to the pooled bus; ResetForReuse() flushes
    // the block cache between cases so the SAME (ushort)IP recompiles from the new case's bytes (the isolation
    // invariant). The inner M8086Cpu is wrapped once; SetRegister re-seeds it per case.
    [ThreadStatic] private static JittedCpu<M8086Cpu>? _jitTls;
    [ThreadStatic] private static M8086Cpu? _jitInnerTls;

    private static (JittedCpu<M8086Cpu> Jit, M8086Cpu Inner) RentJit(AddressSpace bus)
    {
        if (_jitTls is null)
            (_jitTls, _jitInnerTls) = M8086JittedCpuFactory.Create(bus);
        else
            _jitTls.ResetForReuse();   // flush cache + clear chains + reset inner — bound to the SAME pooled bus
        return (_jitTls, _jitInnerTls!);
    }
```

> **Bus-binding note:** the reused `JittedCpu` is bound to the pooled bus at construction. Because `RentBus()`
> re-zeroes the SAME backing array (never reallocates), the JIT's Fastmem snapshot remains valid across cases —
> the rented JIT stays bound to the correct, live bus. This is why T4 DEPENDS ON T2 (a per-case fresh bus would
> break the binding).

- [ ] **Step 2: Use the rent in `RunCaseThroughJit`.** Replace the per-case factory call (the
  `var (jit, inner) = M8086JittedCpuFactory.Create(bus);` line in the JIT path) with:

```csharp
        var (jit, inner) = RentJit(bus);
```

(Everything downstream — `inner.SetRegister(...)` for the 14 registers, `jit.Run(ref budget)` with budget=1, the
data-axis diff — is unchanged. The register re-seed per case overwrites any residual inner state; `ResetForReuse`'s
`_inner.Reset()` is belt-and-braces.)

- [ ] **Step 3: Coverage-parity run**

Run: `CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~M8088JitTom" --no-restore`
Expected: PASS, identical green (the reused JIT, flushed per case, must match the fresh-JIT result exactly).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M8088TomHarteRunner.cs
git commit -m "test(speedup): reuse the 8088 JittedCpu per worker, flush between cases (lever 4)"
```

---

## Task 3: Reuse the JittedCpu per worker in the 68000, 6502, Z80 JIT paths

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs` (`RunCaseThroughJit` + `M68000JittedCpuFactory`)
- Modify: `tests/CpuEmulator.Tests/TomHarte/TomHarteRunner.cs` (6502 `RunCaseThroughJit` + `JittedCpuFactory`)
- Modify: `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs` (Z80 JIT path + `Z80JittedCpuFactory`)

- [ ] **Step 1: 68000.** Add a `[ThreadStatic]` `JittedCpu<M68000Cpu>` rent mirroring Task 2 (built via
  `M68000JittedCpuFactory`, bound to the 68000 runner's pooled bus — the 68000 runner ALREADY pools its
  `_ramArena`, so the bus is reusable; confirm the JIT path uses the pooled arena, and if it builds its own bus
  reuse the T2 pattern). Replace the per-case factory call in `RunCaseThroughJit` with the rent +
  `ResetForReuse()`. Run:

```bash
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~M68000JitTom" --no-restore
```
Expected: PASS, identical green.

- [ ] **Step 2: 6502.** Same rent for `JittedCpu<Mos6502Cpu>` (via `JittedCpuFactory`), bound to the 6502 runner's
  T2 pooled bus, in `TomHarteRunner.RunCaseThroughJit`. Run:

```bash
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~Mos6502JitTomHarteTests" --no-restore
```
Expected: PASS.

- [ ] **Step 3: Z80.** Same rent for `JittedCpu<Z80Cpu>` (via `Z80JittedCpuFactory`), bound to the Z80 runner's T2
  pooled program + I/O buses, in the Z80 JIT path. Run:

```bash
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~Z80JitTomHarteTests" --no-restore
```
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs \
        tests/CpuEmulator.Tests/TomHarte/TomHarteRunner.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs
git commit -m "test(speedup): reuse the 68000/6502/Z80 JittedCpu per worker, flush between cases (lever 4)"
```

---

## Task 4: SMC / dirty-page hazard audit (the subtle case)

Some JIT cases self-modify code (the Klaus pin proves the SMC path; a TomHarte case CAN write a code page within
its single instruction). The reused cache must not carry a dirty mark or a stale per-page entry into the next case.
`FlushAll()` clears `Dirty` and `_blocksByPage`, so a fresh case starts clean — but verify explicitly.

- [ ] **Step 1: Add a regression test for SMC-across-reuse.** Append to `BlockCacheFlushTests.cs`:

```csharp
    [Fact]
    public void FlushAll_clears_dirty_marks_so_a_reused_case_starts_clean()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;

        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var ram = new byte[0x10000];
        space.MapMemory(0x0000, ram, writable: true);

        // Case A: STA $0300 (8D 00 03) writes a code page (0x03), then park — marks page 3 dirty.
        ram[0x0200] = 0x8D; ram[0x0201] = 0x00; ram[0x0202] = 0x03;
        ram[0x0203] = 0x4C; ram[0x0204] = 0x03; ram[0x0205] = 0x02; // JMP $0203 park
        var cpu = new Mos6502Cpu(space) { PC = 0x0200, A = 0x99 };
        var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space);
        long budget = 20; jit.Run(ref budget);

        // Reuse for Case B: a trivial NOP-park at 0x0200; the prior dirty mark must NOT leak.
        space.ClearMappedBacking(ram);
        ram[0x0200] = 0xA9; ram[0x0201] = 0x07; ram[0x0202] = 0x4C; ram[0x0203] = 0x02; ram[0x0204] = 0x02; // LDA #$07 ; park
        cpu.A = 0; cpu.PC = 0x0200;
        jit.ResetForReuse();
        budget = 20; jit.Run(ref budget);
        Assert.Equal(0x07, cpu.A);  // clean run, no stale block / dirty-mark interference
    }
```

- [ ] **Step 2: Run both flush tests**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~BlockCacheFlushTests" --no-restore`
Expected: PASS (2 tests).

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Jit/BlockCacheFlushTests.cs
git commit -m "test(jit): SMC-across-reuse regression for the flush seam (lever 4)"
```

---

## Task 5: MEASUREMENT GATE

- [ ] **Step 1: Before↔after the JIT sweeps** (the JIT warm-up was paid per case; reuse pays it per worker):

```bash
# BEFORE (on the T3-merged base — fresh JIT per case):
git stash || true
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~M8088JitTom|FullyQualifiedName~M68000JitTom" --no-restore 2>&1 | tee /tmp/t4-before.txt
git stash pop || true
# AFTER (PR branch — reused JIT):
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~M8088JitTom|FullyQualifiedName~M68000JitTom" --no-restore 2>&1 | tee /tmp/t4-after.txt
```

- [ ] **Step 2: Record in the PR body.** Table: `subset | before | after | speedup`. **Gate: after < before** on
  the JIT subsets (the win is the eliminated per-case `new Fastmem`/`new BlockCache`/`new BlockCompiler` +
  per-case JIT warm-up). Note the allocation reduction (3 arrays + 2 dictionaries + compiler per case →
  per worker).

---

## Task 6: COVERAGE-PRESERVATION GATE — the byte-for-byte EQUIVALENCE PROOF (the DROP-GATE)

This is the gate that makes lever 4 safe. The reused-JIT sweep MUST produce the IDENTICAL result (executed /
deferred / excluded counts AND pass/fail) as the fresh-JIT sweep on the same corpus. If it does not, the flush is
incomplete (state leaked) and **the PR is dropped**.

- [ ] **Step 1: Capture the fresh-JIT baseline (on the T3 base, before T4).**

```bash
git stash || true
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~JitTom" --no-restore 2>&1 | grep -hoE "ran [0-9]+, executed [0-9]+.*" | sort > /tmp/jit-fresh.txt
git stash pop || true
```

- [ ] **Step 2: Capture the reused-JIT counts (T4 branch).**

```bash
CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~JitTom" --no-restore 2>&1 | grep -hoE "ran [0-9]+, executed [0-9]+.*" | sort > /tmp/jit-reused.txt
```

- [ ] **Step 3: Diff — MUST be identical.**

```bash
diff /tmp/jit-fresh.txt /tmp/jit-reused.txt && echo "EQUIVALENCE PROVEN — counts byte-identical"
```

Expected: `EQUIVALENCE PROVEN`. **If the diff is non-empty, STOP: the flush is leaking state. Either fix the flush
(extend `ResetForReuse`/`FlushAll`) or DROP this PR.** A non-empty diff is a correctness failure, not a perf miss.

- [ ] **Step 4: Higher-confidence proof — run at a LARGER sample once.** A 200-sample diff is strong; a one-time
  larger run hardens it (more IP collisions across cases = more chances for a stale block to surface):

```bash
CPUEMULATOR_TOMHARTE_SAMPLE=1000 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release \
  --filter "FullyQualifiedName~JitTom" --no-restore 2>&1 | tail -20
```

Expected: PASS (green at 1000/file — if a stale block leaked, the data-axis diff in the runner would FAIL the case).

- [ ] **Step 5: Full-suite green**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --no-restore`
Expected: PASS.

- [ ] **Step 6: Open the PR.** Body MUST include: the measurement table, the `EQUIVALENCE PROVEN` diff output, the
  1000-sample green, and an explicit statement of the hazard + how `FlushAll`/`ResetForReuse` neutralize it.
  **Docs Impact:** two new public JIT methods (`BlockCache.FlushAll`, `JittedCpu.ResetForReuse`) + `ChainTable.Clear`
  — note them as the per-worker reuse seam in the PR description.

---

## Self-review (run before opening the PR)

- **Spec coverage:** lever 4 = Tasks 1 (seam) + 2–3 (rent the reused JIT across all 4 CPUs) + 4 (SMC audit). ✔
- **Placeholder scan:** every code step shows literal code; the only `// ...` are explicit "unchanged downstream"
  notes, not missing logic. ✔
- **Type consistency:** `FlushAll()` (BlockCache) ↔ called by `ResetForReuse()` (JittedCpu) ↔ called by `RentJit`
  (runners); `ChainTable.Clear()` ↔ called by `FlushAll`; `ResetForReuse()` signature matches Task 1 ↔ Tasks 2-3. ✔
- **Hazard coverage:** the block-cache-isolation hazard (F5) is addressed by Task 1's flush + proven by Task 1's
  test (same-PC-new-bytes), Task 4's SMC test, and Task 6's equivalence drop-gate. ✔
