# PR-T2 — Per-worker allocation pooling across all CPUs (lever 2)

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans`, task-by-task. Steps use checkbox (`- [ ]`) syntax.
> **Read first (binding):** `2026-06-18-test-speedup-arc-overview.md` (sequencing + the two shared gates) and the
> reference implementation `M68000TomHarteRunner.cs:100-136` (the `[ThreadStatic] _ramArena` + `Array.Clear`
> pattern this PR PORTS to the other CPUs). Lands AFTER PR-T1 merges (T1 edits the sweep loop bodies; T2 edits the
> runners — keeping them in separate PRs avoids conflicts on the shared `*Tests.cs` files). Branch
> `test/speedup-pooling` → PR → main. **NO production-code behaviour change** (a single additive, opt-in seam on
> `AddressSpace` is permitted — see Task 1; it leaves the default `new AddressSpace` path byte-identical).

**Goal:** Stop allocating, per case, a fresh RAM backing (1 MB on 8088, 64 KB on 6502/Z80) + a fresh
`AddressSpace` (whose `PageEntry[]` page table is 2.1 MB/case on the 68000-width space, 128 KB on the 8088, …).
Port the 68000 interpreter runner's per-worker-thread pooling pattern to the 6502, Z80, and 8088 interpreter
runners, so each worker thread reuses ONE arena + ONE `AddressSpace`, re-zeroed (`Array.Clear`) per case.

**Architecture:** Each runner gets a `[ThreadStatic]` cache of its reusable bus + backing array(s), lazily built
on first use, `Array.Clear`'d per case before the case's initial RAM is installed — bit-identical to a fresh
`new byte[]` + fresh map. `[ThreadStatic]` is safe because `RunCase` is synchronous (no `await`), exactly as the
68000 reference notes. The `AddressSpace` mapping is stable across cases (same backing, same pages), so the
`PageEntry[]` is allocated ONCE per worker, not per case.

**Tech Stack:** C#, `[ThreadStatic]`, `Array.Clear`, the existing `AddressSpace` / `TracingAddressSpace`.

**Scope boundary:** This PR pools the INTERPRETER runners' RAM + AddressSpace + per-case CPU object. It does NOT
pool the JIT object graph (`Fastmem`/`BlockCache`/`BlockCompiler` in `JittedCpu`) — that requires a cache-flush
seam and is **PR-T4** (the hazard doc is in the overview). T2's JIT-runner edits stop at reusing the bus arena.

---

## What the recon CONFIRMED (file:line — verified against `main` @ `896f88b`)

| # | Fact | Evidence |
|---|------|----------|
| R1 | The 68000 interpreter runner ALREADY pools a `[ThreadStatic] _ramArena` (16 MiB) + `Array.Clear` per case | `M68000TomHarteRunner.cs:107` (decl), `:134-135` (lazy-init + clear) |
| R2 | The pattern was NOT ported: 6502 allocates 64 KB + a fresh AddressSpace per case (×2 paths) | `TomHarteRunner.cs:22-23` (RunCase), `:88-89` (RunCaseThroughJit) |
| R3 | Z80 allocates 64 KB program + 64 KB I/O + 2 fresh AddressSpaces per case (×2 paths) | `Z80TomHarteRunner.cs:27-28,32-33` (RunCase), `:107-108,111-112` (JIT path) |
| R4 | 8088 allocates 1 MB + a fresh AddressSpace per case at FOUR sites | `M8088TomHarteRunner.cs:37-38,124-125,208-209,292-293` |
| R5 | `AddressSpace` allocates the `PageEntry[]` page table per construction | `AddressSpace.cs:23,45` (`_pages = new PageEntry[(1<<bits)>>PageShift]`) |
| R6 | `RunCase` is synchronous on every runner (no `await`) → `[ThreadStatic]` is reentrancy-safe | all `*Runner.cs` `RunCase` bodies |

---

## File structure

- **Modify (additive seam):** `src/CpuEmulator.Core/AddressSpace.cs` — add `ClearAndReinstall` helper used by the
  pooled runners (opt-in; does not touch the existing `MapMemory` path). *Production change — forces the branch.*
- **Modify:** `tests/CpuEmulator.Tests/TomHarte/TomHarteRunner.cs` (6502 — both paths).
- **Modify:** `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs` (Z80 — both paths, program + I/O).
- **Modify:** `tests/CpuEmulator.Tests/TomHarte/M8088TomHarteRunner.cs` (8088 — all four sites).

---

## Task 1: Add the additive `AddressSpace` reuse seam (production, additive, opt-in)

The pooled runners need to re-zero a pooled backing AND keep the same `AddressSpace` mapping. Rather than have each
runner reach into mapping internals, add one additive helper that the pooled path calls. The existing
`MapMemory` + `Write8` path is UNTOUCHED, so every production caller and every non-pooled test is byte-identical.

**Files:**
- Modify: `src/CpuEmulator.Core/AddressSpace.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Core/AddressSpaceReuseTests.cs`:

```csharp
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Core;

public class AddressSpaceReuseTests
{
    [Fact]
    public void ClearAndReinstall_zeroes_the_backing_and_keeps_the_mapping()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var ram = new byte[0x10000];
        space.MapMemory(0x0000, ram, writable: true);

        space.Write8(0x1234, 0xAB);
        Assert.Equal(0xAB, space.Read8(0x1234));

        // Reuse for the "next case": re-zero the SAME backing, same mapping, no re-alloc.
        space.ClearMappedBacking(ram);
        Assert.Equal(0x00, space.Read8(0x1234));     // cleared
        space.Write8(0x4321, 0xCD);                  // mapping still live
        Assert.Equal(0xCD, space.Read8(0x4321));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~AddressSpaceReuseTests" --no-restore`
Expected: FAIL — `ClearMappedBacking` not defined.

- [ ] **Step 3: Add the additive helper.** In `src/CpuEmulator.Core/AddressSpace.cs`, after `MapMemory`
  (around `:48`), add:

```csharp
    /// <summary>Re-zero a backing array that is ALREADY mapped, WITHOUT re-allocating the array or rebuilding the
    /// page table — the pooled-test-runner reuse seam (lever 2). Equivalent to allocating a fresh zeroed backing
    /// and re-mapping it, but with zero allocation: the mapping (the PageEntry[] page table) is unchanged, so
    /// every page still points at <paramref name="backing"/>. The caller then re-installs the case's initial RAM
    /// with Write8 exactly as it would on a fresh array. Additive: no existing caller uses it; the production
    /// hot path is untouched.</summary>
    public void ClearMappedBacking(byte[] backing)
    {
        System.ArgumentNullException.ThrowIfNull(backing);
        System.Array.Clear(backing, 0, backing.Length);
    }
```

> **Why a method and not just `Array.Clear` at the call site?** It documents the reuse contract at the type that
> owns the invariant (the page table still points at this backing) and gives a single place to extend if a future
> AddressSpace gains per-page dirty/cached state that also needs resetting on reuse. It is a thin, additive seam.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~AddressSpaceReuseTests" --no-restore`
Expected: PASS.

- [ ] **Step 5: Full Core + production regression (the seam is additive — prove nothing moved)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~Core" --no-restore`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Core/AddressSpace.cs tests/CpuEmulator.Tests/Core/AddressSpaceReuseTests.cs
git commit -m "feat(core): additive ClearMappedBacking reuse seam for pooled test runners (lever 2)"
```

---

## Task 2: Pool the 6502 interpreter runner

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/TomHarteRunner.cs:22-23` (RunCase), `:88-89` (RunCaseThroughJit bus)

- [ ] **Step 1: Add the `[ThreadStatic]` pool fields.** At the top of `TomHarteRunner` (after the class open),
  add:

```csharp
    // Per-worker-thread reusable 64 KiB program bus (lever 2 — the 68000 _ramArena pattern, ported). RunCase is
    // synchronous (no await), so [ThreadStatic] is reentrancy-safe: a worker thread never reenters RunCase.
    [ThreadStatic] private static AddressSpace? _busTls;
    [ThreadStatic] private static byte[]? _ramTls;

    private static (AddressSpace bus, byte[] ram) RentBus()
    {
        if (_busTls is null)
        {
            _ramTls = new byte[0x10000];
            _busTls = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
            _busTls.MapMemory(0x0000, _ramTls, writable: true);
        }
        _busTls.ClearMappedBacking(_ramTls!);   // re-zero; mapping persists → identical to a fresh new byte[0x10000]
        return (_busTls, _ramTls!);
    }
```

- [ ] **Step 2: Use the pool in `RunCase`.** Replace `:22-23`:

```csharp
        var (inner, _) = RentBus();
        foreach (var e in c.Initial.Ram) inner.Write8(e.Address, e.Value);
```

(Delete the old `var inner = new AddressSpace(...); inner.MapMemory(0x0000, new byte[0x10000], writable: true);`.
Everything downstream — `new TracingAddressSpace(inner)`, the CPU, Step, diff — is unchanged.)

- [ ] **Step 3: Use the pool in `RunCaseThroughJit`.** The JIT path at `:88-89` also allocates a 64 KB backing +
  AddressSpace per case. Replace it the same way — BUT note: the JIT path constructs a `JittedCpu` that builds
  Fastmem against the bus. Reusing the BUS is safe (the JIT reads the re-installed RAM); reusing the JittedCpu is
  T4. So here, only the bus is pooled:

```csharp
        var (space, _) = RentBus();
        foreach (var e in c.Initial.Ram) space.Write8(e.Address, e.Value);
```

(Keep the rest of `RunCaseThroughJit` — the fresh `JittedCpu` per case stays until T4.)

- [ ] **Step 4: Coverage-parity run at a fixed sample**

Run: `CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~Mos6502" --no-restore`
Expected: PASS, identical green.

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/TomHarteRunner.cs
git commit -m "test(speedup): pool the 6502 runner bus per worker thread (lever 2)"
```

---

## Task 3: Pool the Z80 interpreter runner (program + I/O)

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs:27-28,32-33` (RunCase), `:107-108,111-112` (JIT path)

- [ ] **Step 1: Add the pool fields** (two backings — program + I/O). At the top of `Z80TomHarteRunner`:

```csharp
    [ThreadStatic] private static AddressSpace? _progTls;
    [ThreadStatic] private static byte[]? _progRamTls;
    [ThreadStatic] private static AddressSpace? _ioTls;
    [ThreadStatic] private static byte[]? _ioRamTls;

    private static (AddressSpace prog, byte[] progRam, AddressSpace io, byte[] ioRam) RentBuses()
    {
        if (_progTls is null)
        {
            _progRamTls = new byte[0x10000];
            _progTls = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
            _progTls.MapMemory(0x0000, _progRamTls, writable: true);
            _ioRamTls = new byte[0x10000];
            _ioTls = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
            _ioTls.MapMemory(0x0000, _ioRamTls, writable: true);
        }
        _progTls.ClearMappedBacking(_progRamTls!);
        _ioTls!.ClearMappedBacking(_ioRamTls!);
        return (_progTls, _progRamTls!, _ioTls, _ioRamTls!);
    }
```

- [ ] **Step 2: Use the pool in `RunCase`.** Replace `:27-37` (the two `new AddressSpace` + `MapMemory` blocks)
  with:

```csharp
        var (inner, _, ioInner, _) = RentBuses();
        foreach (var e in c.Initial.Ram) inner.Write8(e.Address, e.Value);
        var bus = new TracingAddressSpace(inner);

        foreach (var port in c.Ports)
            if (port.IsRead) ioInner.Write8(port.Address, port.Value);
        var io = new TracingAddressSpace(ioInner);
```

(The `TracingAddressSpace` wrappers stay per-case — they hold the per-case trace; only the inner buses are pooled.)

- [ ] **Step 3: Use the pool in the JIT path** (`:107-112`). Replace the program + I/O `new AddressSpace` +
  `MapMemory` blocks with the `RentBuses()` rent (same shape as Step 2, minus the `TracingAddressSpace` if the
  JIT path uses the concrete bus directly — match the existing local names there). Keep the fresh `JittedCpu` per
  case (T4 territory).

- [ ] **Step 4: Coverage-parity run**

Run: `CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~Z80" --no-restore`
Expected: PASS, identical green (incl. the per-T-state bus-trace and ports diffs — proving the pooled+cleared bus
is bit-identical to a fresh one).

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs
git commit -m "test(speedup): pool the Z80 runner program+I/O buses per worker thread (lever 2)"
```

---

## Task 4: Pool the 8088 runner (all four sites — the biggest per-case allocation)

The 8088 allocates 1 MB per case at four sites (`RunCase`, `RunCaseThroughJit`, and two vector/exception
variants). All four build the identical bus.

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/M8088TomHarteRunner.cs:37-38,124-125,208-209,292-293`

- [ ] **Step 1: Add the pool field + rent helper.** At the top of `M8088TomHarteRunner`:

```csharp
    // Per-worker reusable 1 MiB 20-bit little-endian program bus (lever 2). 1 MB/case × ~millions of cases is the
    // single largest per-case allocation in the suite; pooling collapses it. RunCase is synchronous → [ThreadStatic]
    // is reentrancy-safe.
    [ThreadStatic] private static AddressSpace? _busTls;
    [ThreadStatic] private static byte[]? _ramTls;

    private static AddressSpace RentBus()
    {
        if (_busTls is null)
        {
            _ramTls = new byte[0x100000];
            _busTls = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
            _busTls.MapMemory(0, _ramTls, writable: true);
        }
        _busTls.ClearMappedBacking(_ramTls!);   // re-zero; mapping persists → identical to a fresh new byte[0x100000]
        return _busTls;
    }
```

- [ ] **Step 2: Replace all four allocation sites.** At each of `:37-38`, `:124-125`, `:208-209`, `:292-293`
  replace:

```csharp
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
```

with:

```csharp
        var bus = RentBus();
```

(Each site then continues with its existing `uint mask = bus.AddressMask;` + `foreach (cell in c.Initial.Ram)
bus.Write8(...)` — unchanged. The four sites are identical in this respect.)

- [ ] **Step 3: Coverage-parity run**

Run: `CPUEMULATOR_TOMHARTE_SAMPLE=200 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --filter "FullyQualifiedName~M8088" --no-restore`
Expected: PASS, identical green (data axis + JIT tier-parity + vectors).

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M8088TomHarteRunner.cs
git commit -m "test(speedup): pool the 8088 runner 1 MB bus per worker thread, all 4 sites (lever 2)"
```

---

## Task 5: MEASUREMENT GATE (prove the allocation/GC reduction)

The pooling win is primarily ALLOCATION/GC pressure (and the wall-clock that GC thrash costs under the parallel
sweep), so the gate measures both.

- [ ] **Step 1: Baseline (BEFORE — on `main`@base).**

```bash
git stash || true
CPUEMULATOR_TOMHARTE_SAMPLE=200 DOTNET_gcServer=1 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj \
  -c Release --filter "FullyQualifiedName~M8088" --no-restore 2>&1 | tee /tmp/t2-before.txt
git stash pop || true
```

Record: the `dotnet test` total elapsed. (The 8088 subset is the worst allocator — 1 MB/case — so it shows the
pooling win most.)

- [ ] **Step 2: After (PR branch).**

```bash
CPUEMULATOR_TOMHARTE_SAMPLE=200 DOTNET_gcServer=1 dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj \
  -c Release --filter "FullyQualifiedName~M8088" --no-restore 2>&1 | tee /tmp/t2-after.txt
```

- [ ] **Step 3: Optional sharper signal — a microbench of N pooled-vs-fresh RunCase iterations** capturing
  `GC.CollectionCount(0/1/2)` and `GC.GetTotalAllocatedBytes()` around a fixed 10,000-iteration loop. Add as a
  `[Fact(Skip="manual perf probe")]` in a `Perf` test class if a precise allocation number is wanted for the PR
  body; otherwise the wall-clock + the obvious 1MB×N → 1MB×threads reasoning suffices.

- [ ] **Step 4: Record in the PR body.** Table: `subset | before wall-clock | after wall-clock | speedup | note`.
  **Gate:** after ≤ before on the 8088 subset, AND the PR body states the allocation reduction
  (1 MB × `sampleSize` × files → 1 MB × thread-count, i.e. ~4-5 orders of magnitude fewer transient arenas, the
  same reasoning the 68000 reference comment makes at `M68000TomHarteRunner.cs:100-106`).

---

## Task 6: COVERAGE-PRESERVATION GATE

- [ ] **Step 1: Executed-count parity (lever 2 — zero coverage cost).** At a fixed sample, the executed/deferred
  counts MUST be byte-identical before↔after:

```bash
grep -hoE "ran [0-9]+, executed [0-9]+.*" /tmp/t2-before.txt | sort > /tmp/before-counts.txt
grep -hoE "ran [0-9]+, executed [0-9]+.*" /tmp/t2-after.txt  | sort > /tmp/after-counts.txt
diff /tmp/before-counts.txt /tmp/after-counts.txt && echo "COUNTS IDENTICAL"
```

Expected: `COUNTS IDENTICAL`.

- [ ] **Step 2: Cross-case contamination proof (the pooling-specific hazard).** A pooled, re-zeroed bus must NOT
  leak case N's RAM into case N+1. The existing sweeps already prove this implicitly (a green sweep over 200
  diverse cases on a reused bus = no leak), but make it explicit: confirm the per-case diff includes a RAM diff
  (it does — `M8088TomHarteRunner.cs:90-95` compares every `final.ram` cell against the bus read-back; a leaked
  byte from the prior case would fail this). State in the PR body: "the RAM-cell diff on every case is the leak
  detector; 200 green cases on one reused bus = `Array.Clear` is correct."

- [ ] **Step 3: Full-suite green**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj -c Release --no-restore`
Expected: PASS.

- [ ] **Step 4: Open the PR.** Body MUST include: the measurement table, the allocation-reduction statement, the
  `COUNTS IDENTICAL` proof, and the leak-detector note. **Docs Impact:** one additive `AddressSpace` public method
  (`ClearMappedBacking`) — note it as a test-only reuse seam in the PR description.

---

## Self-review (run before opening the PR)

- **Spec coverage:** lever 2 RAM-arena pooling = Tasks 2–4; AddressSpace/PageEntry[] pooling = achieved by reusing
  the SAME `AddressSpace` (Task 1 seam keeps the mapping, so the `PageEntry[]` is allocated once/worker, not
  per-case) — that satisfies "port pooling to … AddressSpace/PageEntry[]". JIT-container pooling is explicitly
  DEFERRED to T4 (overview scope boundary). ✔
- **Placeholder scan:** every code step shows literal code; no TBD. ✔
- **Type consistency:** `ClearMappedBacking(byte[])` matches Task 1 ↔ Tasks 2-4; `RentBus()`/`RentBuses()` return
  shapes match their call sites. ✔
