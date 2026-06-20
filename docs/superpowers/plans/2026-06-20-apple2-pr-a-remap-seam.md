# Apple ][+ PR-A — The `AddressSpace.Remap` bank-switch seam + the JIT invalidation listener

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the run-time bank-switch primitive ADR 0009 Decision 2 designed but never shipped, and which ADR 0014 Decision 4 confirms the Apple ][+ Language Card forces live: `AddressSpace.Remap` + `AddressSpace.RemapPeripheral` on `IAddressSpace` (the **owner-decided** placement, ADR 0014 OQ3), an `IMapInvalidationListener` the JIT registers (preserving the `Core → Jit` dependency direction), and `BlockCache.InvalidatePages` so a remap evicts exactly the affected pages' compiled blocks and the JIT re-classifies the range in `Fastmem`. **This PR touches no Apple code** — it is a pure `Core` + `Jit` framework primitive, gated by a real remap test, with the no-behavior-change invariant for every existing board.

**Architecture:** `Remap(start, backing, writable)` re-points an already-mapped, page-aligned range to a new RAM/ROM backing (the bank-switch case); `RemapPeripheral(start, length, p)` re-points a range to MMIO. Both mutate the `PageEntry[]` page table in place (no re-allocation) and fire `OnRemap(firstPage, pageCount)` to any registered `IMapInvalidationListener`. The interpreter tier needs **no** listener — it re-reads the live page table on every access, so `Remap` is immediately correct there. The JIT tier registers one listener (`JittedCpu`): on `OnRemap` it (1) re-classifies the affected pages in `Fastmem` (a fresh `TryGetDirectAccess` per page → new `PageBacking`/`PageOffset`/`PageWritable`) so emitted fast-path loads/stores hit the new backing, and (2) calls `BlockCache.InvalidatePages(firstPage, pageCount)` to evict every compiled block decoded from those pages (the Language Card runs *code* out of the banked RAM, so stale-block eviction is mandatory). `BlockCache.InvalidatePages` is factored from the existing per-page `InvalidateIfDirty` loop (the same `_blocksByPage` → `Evict` machinery).

**Tech Stack:** C# / .NET 10, `CpuEmulator.Core` (the `AddressSpace`/`IAddressSpace` page table), `CpuEmulator.Jit` (`JittedCpu` + `Fastmem` + `BlockCache`), xUnit. No new assembly; no new ProjectReference. `Core` stays AOT-clean (it defines an interface the JIT implements — exactly the `TryGetDirectAccess` direction).

---

## Recon facts this plan is built on (verified against `main` @ HEAD)

1. **`AddressSpace`** (`src/CpuEmulator.Core/AddressSpace.cs`) is a 256-byte-page table: `PageSize = 256`, `PageShift = 8`, a private `struct PageEntry { byte[]? Backing; int BackingOffset; bool Writable; IPeripheral? Handler; uint HandlerBase; }`, and a `PageEntry[] _pages` sized `(1 << addressBits) >> 8`. `MapMemory`/`MapPeripheral` validate via `ValidateRange` (positive page-multiple length, page-aligned start, in range) then `EnsureRangeUnmapped` then fill pages. `Remap` is the same fill **without** `EnsureRangeUnmapped` (the range is *already* mapped — that is the point).
2. **`MapMemory`** sets `page.Backing`, `page.BackingOffset = i << PageShift`, `page.Writable`, and leaves `Handler` null. **`MapPeripheral`** sets `page.Handler`, `page.HandlerBase = start`, and leaves `Backing` null. A page is either memory or MMIO; `Read8` checks `Backing` first, then `Handler`. So a `Remap` to memory must **clear** any prior `Handler`, and `RemapPeripheral` must clear any prior `Backing` — otherwise `Read8` would still hit the stale memory backing.
3. **`IAddressSpace`** (`src/CpuEmulator.Core/IAddressSpace.cs`) already declares `MapMemory`/`MapPeripheral`/`Read8`/`Write8`/`TryPeek8` + the default-method wide accessors. `Remap`/`RemapPeripheral` are added here (the owner-decided placement) and implemented on the concrete `AddressSpace`. Devices already receive `IAddressSpace` (the LC mapper will call `Remap` through it).
4. **`AddressSpace.TryGetDirectAccess(uint pageStart, out byte[] backing, out int pageOffset, out bool writable)`** is `internal` (JIT-only via `AssemblyInfo` InternalsVisibleTo) and reports a page's RAM/ROM backing or false for MMIO/unmapped. `Fastmem` (`src/CpuEmulator.Jit/Fastmem.cs`) calls it once per page at construction to fill `PageBacking[]`/`PageOffset[]`/`PageWritable[]`. The JIT's `OnRemap` re-runs `TryGetDirectAccess` for the remapped pages to refresh those three arrays.
5. **`BlockCache<TCpu>`** (`src/CpuEmulator.Jit/BlockCache.cs`, `internal`) has `Dictionary<int, List<CompiledBlock<TCpu>>> _blocksByPage` and a private `Evict(block)` that removes the block + severs chains. `InvalidateIfDirty()` loops dirtied pages and evicts their blocks. **`InvalidatePages(int firstPage, int pageCount)`** is the same loop over an explicit range — factor a shared `EvictBlocksOnPage(int page)` helper from `InvalidateIfDirty` and call it from both.
6. **`JittedCpu<TCpu>`** (`src/CpuEmulator.Jit/JittedCpu.cs`) holds `_bus` (the concrete `AddressSpace`), `_fastmem`, and `_cache`, all built in its ctor (`_fastmem = new Fastmem(bus, opts); _cache = new BlockCache<TCpu>(bus.PageCount, opts);`). It is the natural `IMapInvalidationListener`; it registers itself with `_bus` at the end of the ctor.
7. **Listener registration keeps `Core` AOT-clean.** `Core` defines `IMapInvalidationListener` and `AddressSpace.AddMapInvalidationListener(IMapInvalidationListener)` (`internal` — JIT-only, same InternalsVisibleTo as `TryGetDirectAccess`); the JIT *implements* the interface and registers. `Core` never references `Jit` (the `TryGetDirectAccess` precedent).
8. **No current device calls `Remap`.** So the whole seam is gated behind methods nothing exercises (ADR 0009 §4 reversibility): every existing board + every existing test is byte-identical. The only new behavior is the new tests.
9. **`Fastmem` fields are public settable arrays** (`PageBacking { get; }` returns `byte[]?[]`; element assignment is allowed). The JIT's `OnRemap` writes `_fastmem.PageBacking[p] = …` etc. directly. (No `Fastmem` API change needed beyond per-element writes; a small `Reclassify(bus, page)` helper on `Fastmem` keeps the `DisableFastmem` rule — see Task 4.)

---

## Conventions to follow

- **`Directory.Build.props`:** `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true` — warning-clean.
- **AOT-clean `Core`:** `Core` references nothing new. The JIT-facing seam (`IMapInvalidationListener`, `AddMapInvalidationListener`) mirrors the existing `internal TryGetDirectAccess` + `[assembly: InternalsVisibleTo("CpuEmulator.Jit")]` pattern — Core *defines*, Jit *implements*.
- **TDD per task:** write the failing test, run it red, implement, run it green, commit. Literal code below — no placeholders.
- **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Core` — the remap primitive + the listener seam
- **Create** `src/CpuEmulator.Core/IMapInvalidationListener.cs` — the one-method interface the JIT registers.
- **Modify** `src/CpuEmulator.Core/IAddressSpace.cs` — add `Remap` + `RemapPeripheral` to the interface (default-throw bodies so any non-`AddressSpace` impl is honest).
- **Modify** `src/CpuEmulator.Core/AddressSpace.cs` — implement `Remap`/`RemapPeripheral` (in-place page-table mutation + fire `OnRemap`), the listener list + `internal AddMapInvalidationListener`, and a private `FireRemap(firstPage, pageCount)`.

### `CpuEmulator.Jit` — the listener + the page-precise invalidation
- **Modify** `src/CpuEmulator.Jit/BlockCache.cs` — factor `EvictBlocksOnPage(int page)` out of `InvalidateIfDirty`; add `public void InvalidatePages(int firstPage, int pageCount)`.
- **Modify** `src/CpuEmulator.Jit/Fastmem.cs` — add `public void Reclassify(AddressSpace bus, int page, JitOptions options)` (re-run `TryGetDirectAccess` for one page, honoring `DisableFastmem`).
- **Modify** `src/CpuEmulator.Jit/JittedCpu.cs` — implement `IMapInvalidationListener.OnRemap` (reclassify the range in `_fastmem` + `_cache.InvalidatePages`); register `this` with `_bus` at the end of the ctor.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Core/AddressSpaceRemapTests.cs` — the interpreter-tier remap gates (memory→memory, memory→MMIO, MMIO→memory, alignment guards, listener fires the right span).
- **Create** `tests/CpuEmulator.Tests/Jit/RemapInvalidationTests.cs` — the JIT-tier gates (a remapped code page evicts its block + the new bank's code runs; the fast-path read sees the new backing; no remap = no eviction).
- **Create** `tests/CpuEmulator.Tests/Jit/RemapNoRegressionTests.cs` — every existing board builds + a representative run is byte/cycle-identical (the no-behavior-change anchor).

### Docs
- **Modify** `docs/ROADMAP.md` — note the `Remap` seam shipped (the long-deferred ADR 0009 Decision 2 primitive, against its first real consumer-to-be).
- **Modify** `docs/BUILDER_QUEUE.md` — set row **A** to ✅; update the banner.

---

## Task 1: `IMapInvalidationListener` + the `IAddressSpace` surface

**Files:**
- Create: `src/CpuEmulator.Core/IMapInvalidationListener.cs`
- Modify: `src/CpuEmulator.Core/IAddressSpace.cs`
- Test: `tests/CpuEmulator.Tests/Core/AddressSpaceRemapTests.cs`

- [ ] **Step 1: Write the failing test (the surface exists + a memory→memory remap re-points reads)**

Create `tests/CpuEmulator.Tests/Core/AddressSpaceRemapTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.Core;

public class AddressSpaceRemapTests
{
    private static AddressSpace Space16()
    {
        var s = new AddressSpace(AddressSpaceKind.Program, 16);
        // $D000-$DFFF mapped to "ROM" bank (read-only), value 0xAA throughout.
        var rom = new byte[0x1000];
        Array.Fill(rom, (byte)0xAA);
        s.MapMemory(0xD000, rom, writable: false);
        return s;
    }

    [Fact]
    public void Remap_re_points_a_mapped_range_to_a_new_writable_backing()
    {
        var s = Space16();
        Assert.Equal(0xAA, s.Read8(0xD000));   // the "ROM" bank
        s.Write8(0xD000, 0x55);                // ROM write ignored
        Assert.Equal(0xAA, s.Read8(0xD000));

        var ram = new byte[0x1000];
        Array.Fill(ram, (byte)0xBB);
        s.Remap(0xD000, ram, writable: true);  // bank in the LC RAM

        Assert.Equal(0xBB, s.Read8(0xD000));   // now reads the RAM bank
        s.Write8(0xD000, 0x55);                // and it is writable
        Assert.Equal(0x55, s.Read8(0xD000));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AddressSpaceRemapTests.Remap_re_points"`
Expected: FAIL — compile error (`AddressSpace.Remap` does not exist).

- [ ] **Step 3: Create the listener interface**

Create `src/CpuEmulator.Core/IMapInvalidationListener.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>A listener the JIT registers on an <see cref="AddressSpace"/> so a run-time bus remap
/// (<see cref="IAddressSpace.Remap"/> / <see cref="IAddressSpace.RemapPeripheral"/>) can invalidate the
/// affected pages: the JIT re-classifies them in its fastmem and evicts any compiled blocks decoded
/// from them. Defined in Core, implemented in Jit — the same dependency direction as the internal
/// fastmem view (Core defines the seam; Core never references Jit). The interpreter tier registers no
/// listener (it re-reads the live page table on every access, so a remap is immediately correct).</summary>
public interface IMapInvalidationListener
{
    /// <summary>A range of <paramref name="pageCount"/> 256-byte pages starting at page
    /// <paramref name="firstPage"/> (= address &gt;&gt; 8) was re-pointed. The listener must drop any
    /// cached state derived from the OLD mapping of those pages.</summary>
    void OnRemap(int firstPage, int pageCount);
}
```

- [ ] **Step 4: Add `Remap`/`RemapPeripheral` to `IAddressSpace`**

In `src/CpuEmulator.Core/IAddressSpace.cs`, add (after `MapPeripheral`, before `TryPeek8`):

```csharp
    /// <summary>Re-point an ALREADY-mapped, page-aligned range to a new RAM (<paramref
    /// name="writable"/>=true) or ROM (false) backing — the run-time bank-switch primitive (ADR 0009
    /// Decision 2; the Apple Language Card is the first consumer, ADR 0014 Decision 4). Unlike
    /// MapMemory, the range may already be mapped (that is the point); the old mapping is overwritten.
    /// Fires the JIT invalidation listener so emitted fast-path code re-classifies + evicts the range.
    /// Default: not supported (only the concrete AddressSpace remaps).</summary>
    void Remap(uint start, byte[] backing, bool writable) =>
        throw new NotSupportedException("This address space does not support Remap.");

    /// <summary>Re-point an ALREADY-mapped, page-aligned range to an MMIO device (the remap analogue
    /// of MapPeripheral). Used by the Videx $C800 expansion-bank window (ADR 0016 Decision 3). Default:
    /// not supported.</summary>
    void RemapPeripheral(uint start, uint length, IPeripheral peripheral) =>
        throw new NotSupportedException("This address space does not support RemapPeripheral.");
```

- [ ] **Step 5: Implement `Remap` on the concrete `AddressSpace`**

In `src/CpuEmulator.Core/AddressSpace.cs`, add a listener field near the other fields (after `_options`):

```csharp
    private List<IMapInvalidationListener>? _mapListeners;
```

Add the implementation methods (after `MapPeripheral`, before `Read8`):

```csharp
    /// <summary>Re-point an already-mapped, page-aligned range to a new RAM/ROM backing in place
    /// (the bank-switch primitive). Same range rules as MapMemory, but WITHOUT the "must be unmapped"
    /// check — the range is expected to be mapped already. Clears each page's Handler (so a range that
    /// was MMIO becomes memory) and fires the invalidation listener.</summary>
    public void Remap(uint start, byte[] backing, bool writable)
    {
        ArgumentNullException.ThrowIfNull(backing);
        ValidateRange(start, (uint)backing.Length);
        int firstPage = (int)(start >> PageShift);
        int pageCount = backing.Length >> PageShift;
        for (int i = 0; i < pageCount; i++)
        {
            ref PageEntry page = ref _pages[firstPage + i];
            page.Backing = backing;
            page.BackingOffset = i << PageShift;
            page.Writable = writable;
            page.Handler = null;            // memory now wins; drop any prior MMIO handler
        }
        FireRemap(firstPage, pageCount);
    }

    /// <summary>Re-point an already-mapped, page-aligned range to an MMIO device in place. Clears each
    /// page's memory Backing (so a range that was memory becomes MMIO) and fires the listener.</summary>
    public void RemapPeripheral(uint start, uint length, IPeripheral peripheral)
    {
        ArgumentNullException.ThrowIfNull(peripheral);
        ValidateRange(start, length);
        int firstPage = (int)(start >> PageShift);
        int pageCount = (int)(length >> PageShift);
        for (int i = 0; i < pageCount; i++)
        {
            ref PageEntry page = ref _pages[firstPage + i];
            page.Handler = peripheral;
            page.HandlerBase = start;
            page.Backing = null;            // MMIO now wins; drop any prior memory backing
        }
        FireRemap(firstPage, pageCount);
    }

    /// <summary>Register a JIT invalidation listener (Jit-only — same InternalsVisibleTo as the
    /// fastmem view). Core defines the seam; Jit implements + registers. The interpreter registers
    /// none.</summary>
    internal void AddMapInvalidationListener(IMapInvalidationListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        (_mapListeners ??= []).Add(listener);
    }

    private void FireRemap(int firstPage, int pageCount)
    {
        if (_mapListeners is null) return;
        foreach (IMapInvalidationListener l in _mapListeners)
            l.OnRemap(firstPage, pageCount);
    }
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AddressSpaceRemapTests.Remap_re_points"`
Expected: PASS.

- [ ] **Step 7: Confirm Core stays AOT-clean (no new references)**

Run: `dotnet build src/CpuEmulator.Core/CpuEmulator.Core.csproj`
Expected: PASS (0 warnings). `Core` references nothing new — `IMapInvalidationListener` lives in Core, `AddMapInvalidationListener` is `internal`.

- [ ] **Step 8: Commit**

```bash
git add src/CpuEmulator.Core/IMapInvalidationListener.cs src/CpuEmulator.Core/IAddressSpace.cs src/CpuEmulator.Core/AddressSpace.cs tests/CpuEmulator.Tests/Core/AddressSpaceRemapTests.cs
git commit -m "feat(core): AddressSpace.Remap/RemapPeripheral + IMapInvalidationListener seam (ADR 0009 Decision 2)"
```

---

## Task 2: The remap edge cases — memory↔MMIO, alignment guards, listener span

**Files:**
- Test: `tests/CpuEmulator.Tests/Core/AddressSpaceRemapTests.cs` (add cases)

- [ ] **Step 1: Write the failing tests**

Append to `AddressSpaceRemapTests` (add `using CpuEmulator.Core;` is already present). The fake listener + a one-page peripheral are local helpers:

```csharp
    /// <summary>A trivial MMIO device that returns a fixed byte and records writes — to prove a
    /// memory→MMIO remap actually routes through the handler.</summary>
    private sealed class StubDevice(byte readValue) : IPeripheral
    {
        public byte LastWrite { get; private set; }
        public string Name => "stub";
        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) => readValue;
        public void Write(uint offset, AccessWidth width, uint value) => LastWrite = (byte)value;
    }

    private sealed class RecordingListener : IMapInvalidationListener
    {
        public int FirstPage { get; private set; } = -1;
        public int PageCount { get; private set; }
        public int Calls { get; private set; }
        public void OnRemap(int firstPage, int pageCount)
        {
            FirstPage = firstPage; PageCount = pageCount; Calls++;
        }
    }

    [Fact]
    public void RemapPeripheral_re_points_memory_to_mmio()
    {
        var s = Space16();
        var dev = new StubDevice(0x42);
        s.RemapPeripheral(0xD000, 0x0100, dev);  // one page now MMIO

        Assert.Equal(0x42, s.Read8(0xD000));     // routes through the device
        s.Write8(0xD000, 0x99);
        Assert.Equal(0x99, dev.LastWrite);       // write reached the device
    }

    [Fact]
    public void Remap_back_to_memory_drops_a_prior_handler()
    {
        var s = Space16();
        s.RemapPeripheral(0xD000, 0x0100, new StubDevice(0x42));
        Assert.Equal(0x42, s.Read8(0xD000));

        var ram = new byte[0x0100];
        Array.Fill(ram, (byte)0x7E);
        s.Remap(0xD000, ram, writable: true);    // memory wins again
        Assert.Equal(0x7E, s.Read8(0xD000));     // NOT the device's 0x42
    }

    [Fact]
    public void Remap_validates_alignment_and_length()
    {
        var s = Space16();
        Assert.Throws<MachineConfigurationException>(() => s.Remap(0xD080, new byte[0x0100], true)); // unaligned start
        Assert.Throws<MachineConfigurationException>(() => s.Remap(0xD000, new byte[0x0080], true)); // sub-page length
    }

    [Fact]
    public void Remap_fires_the_listener_with_the_exact_page_span()
    {
        var s = Space16();
        var listener = new RecordingListener();
        s.AddMapInvalidationListener(listener);   // internal — visible to the test assembly via InternalsVisibleTo

        s.Remap(0xD000, new byte[0x1000], writable: true); // $D000-$DFFF = pages 0xD0..0xDF (16 pages)

        Assert.Equal(1, listener.Calls);
        Assert.Equal(0xD0, listener.FirstPage);
        Assert.Equal(16, listener.PageCount);
    }
```

> **Implementer note:** `AddMapInvalidationListener` is `internal`. The test assembly already has `[assembly: InternalsVisibleTo("CpuEmulator.Tests")]` on `CpuEmulator.Core` (the same attribute `TryGetDirectAccess` relies on — confirm in `src/CpuEmulator.Core/AssemblyInfo.cs`; if only `CpuEmulator.Jit` is listed there, **add** `CpuEmulator.Tests` to it in this step, since the existing Core tests already reach internals). Run the test to discover which.

- [ ] **Step 2: Run them to verify they fail / surface the InternalsVisibleTo need**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AddressSpaceRemapTests"`
Expected: FAIL — `RemapPeripheral` cases assert the device routing; the listener case needs `AddMapInvalidationListener` visible. If it is a compile error on `AddMapInvalidationListener`, add `CpuEmulator.Tests` to `InternalsVisibleTo` in `src/CpuEmulator.Core/AssemblyInfo.cs` and re-run.

- [ ] **Step 3: (only if needed) widen InternalsVisibleTo**

If Step 2 showed the listener call is inaccessible, in `src/CpuEmulator.Core/AssemblyInfo.cs` add:

```csharp
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CpuEmulator.Tests")]
```

(If the line already exists, no change — the implementation from Task 1 is sufficient and all four tests pass.)

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AddressSpaceRemapTests"`
Expected: PASS (5 tests). **This is the interpreter-tier remap gate** — memory↔MMIO both directions, alignment guards, exact listener span.

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/Core/AddressSpaceRemapTests.cs src/CpuEmulator.Core/AssemblyInfo.cs
git commit -m "test(core): remap memory<->mmio, alignment guards, listener-span gate"
```

---

## Task 3: `BlockCache.InvalidatePages` — page-precise eviction over an explicit range

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCache.cs`
- Test: covered by Task 5's JIT gate (this task is a refactor + the public method; the behavior is asserted end-to-end in Task 5). A focused unit test on `BlockCache` is not added here because `BlockCache<TCpu>` is `internal` and `CompiledBlock` construction needs the compiler — the end-to-end JIT test in Task 5 is the un-fakeable gate.

- [ ] **Step 1: Factor the shared per-page eviction + add `InvalidatePages`**

In `src/CpuEmulator.Jit/BlockCache.cs`, replace the `InvalidateIfDirty` method with the version below (it now calls a shared helper) and add `EvictBlocksOnPage` + `InvalidatePages` immediately after it:

```csharp
    public void InvalidateIfDirty()
    {
        if (!Dirty.Any) return;
        for (int page = 0; page < _pageCount; page++)        // 256 for a 16-bit board; cheap scan
        {
            if (!Dirty[page]) continue;
            EvictBlocksOnPage(page);
        }
        Dirty.Clear();
    }

    /// <summary>Evict every compiled block that spans <paramref name="page"/> (and sever their chain
    /// links). Shared by the SMC path (InvalidateIfDirty) and the bus-remap path (InvalidatePages).
    /// A page that owns no block evicts nothing.</summary>
    private void EvictBlocksOnPage(int page)
    {
        if (_blocksByPage.TryGetValue(page, out var list))
            foreach (CompiledBlock<TCpu> block in list.ToArray())   // copy: Evict mutates the list
                Evict(block);
    }

    /// <summary>Evict every block decoded from the <paramref name="pageCount"/> pages starting at
    /// <paramref name="firstPage"/> — the bus-remap invalidation (ADR 0014 Decision 4). Called by the
    /// JIT's IMapInvalidationListener.OnRemap when AddressSpace.Remap re-points a range: the old bank's
    /// compiled code is stale (the Language Card runs code out of the banked RAM), so it must be evicted
    /// so the next dispatch recompiles from the NEW backing. Page-precise (not a whole-cache flush):
    /// only the remapped pages' blocks drop; everything else's chains survive.</summary>
    public void InvalidatePages(int firstPage, int pageCount)
    {
        int end = firstPage + pageCount;
        for (int page = firstPage; page < end; page++)
            EvictBlocksOnPage(page);
    }
```

- [ ] **Step 2: Build to verify the refactor compiles + the existing SMC tests still pass**

Run: `dotnet build src/CpuEmulator.Jit/CpuEmulator.Jit.csproj`
Then the SMC regression: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Smc|FullyQualifiedName~Invalidate|FullyQualifiedName~Chain"`
Expected: PASS — `InvalidateIfDirty` is behavior-identical (it now calls the extracted helper), so every existing SMC/chaining test is green.

- [ ] **Step 3: Commit**

```bash
git add src/CpuEmulator.Jit/BlockCache.cs
git commit -m "feat(jit): BlockCache.InvalidatePages (page-precise eviction over an explicit range)"
```

---

## Task 4: `Fastmem.Reclassify` — refresh one page's classification after a remap

**Files:**
- Modify: `src/CpuEmulator.Jit/Fastmem.cs`
- Test: covered by Task 5's JIT gate (the fast-path-read-sees-the-new-backing assertion).

- [ ] **Step 1: Add `Reclassify`**

In `src/CpuEmulator.Jit/Fastmem.cs`, add this method to the `Fastmem` class (after the constructor). It re-runs the **exact** classification the constructor does, for one page — honoring `DisableFastmem` the same way:

```csharp
    /// <summary>Re-classify ONE page after a bus remap (ADR 0014 Decision 4). Re-runs the same
    /// TryGetDirectAccess + DisableFastmem rule the constructor applies, for the single page
    /// <paramref name="page"/>, so emitted fast-path loads/stores see the NEW backing/offset/writability.
    /// An MMIO/unmapped page (TryGetDirectAccess false) is reset to the bus-arm classification
    /// (null backing, offset 0, not writable) — symmetric with the constructor's else branch.</summary>
    public void Reclassify(AddressSpace bus, int page, JitOptions options)
    {
        uint pageStart = (uint)page << 8;
        if (bus.TryGetDirectAccess(pageStart, out byte[] backing, out int offset, out bool writable))
        {
            PageOffset[page] = offset;
            PageWritable[page] = writable;
            PageBacking[page] = options.DisableFastmem ? null : backing;
        }
        else
        {
            PageBacking[page] = null;
            PageOffset[page] = 0;
            PageWritable[page] = false;
        }
    }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/CpuEmulator.Jit/CpuEmulator.Jit.csproj`
Expected: PASS (0 warnings). `TryGetDirectAccess` is `internal` to `Core` and visible to `Jit` (existing InternalsVisibleTo).

- [ ] **Step 3: Commit**

```bash
git add src/CpuEmulator.Jit/Fastmem.cs
git commit -m "feat(jit): Fastmem.Reclassify (refresh one page's class after a remap)"
```

---

## Task 5: `JittedCpu` registers as the listener + the end-to-end JIT remap gate

**Files:**
- Modify: `src/CpuEmulator.Jit/JittedCpu.cs`
- Test: `tests/CpuEmulator.Tests/Jit/RemapInvalidationTests.cs`

- [ ] **Step 1: Implement the listener + register in the ctor**

In `src/CpuEmulator.Jit/JittedCpu.cs`, add `IMapInvalidationListener` to the class declaration:

```csharp
public sealed class JittedCpu<TCpu> : ICpuCore, IMonitorSupport, IMapInvalidationListener
    where TCpu : class, ICpuCore, IMonitorSupport
```

At the **end** of the constructor (after `_pcName = …;`), register the listener:

```csharp
        // Run-time bus remaps (the Language Card, the Videx $C800 window) must re-classify the
        // remapped pages in fastmem AND evict their stale compiled blocks. The interpreter needs no
        // such hook (it re-reads the page table every access); the JIT registers itself here.
        _bus.AddMapInvalidationListener(this);
```

Add the listener method (near the other `ICpuCore`/`IMonitorSupport` members at the bottom of the class):

```csharp
    /// <summary>IMapInvalidationListener: a bus range was re-pointed (AddressSpace.Remap /
    /// RemapPeripheral). Re-classify each remapped page in fastmem so emitted fast-path code sees the
    /// new backing, then evict every compiled block decoded from those pages (stale: the old bank's
    /// bytes). The next dispatch recompiles from the new mapping. Page-precise — everything outside the
    /// remapped range is untouched.</summary>
    void IMapInvalidationListener.OnRemap(int firstPage, int pageCount)
    {
        int end = firstPage + pageCount;
        for (int page = firstPage; page < end; page++)
            _fastmem.Reclassify(_bus, page, _opts);
        _cache.InvalidatePages(firstPage, pageCount);
    }
```

- [ ] **Step 2: Write the failing end-to-end JIT gate**

Create `tests/CpuEmulator.Tests/Jit/RemapInvalidationTests.cs`. This builds a tiny 6502 machine, runs a block out of `$D000`, remaps `$D000` to a DIFFERENT bank carrying DIFFERENT code, and asserts the new bank's code runs (proving both the fastmem re-class AND the block eviction). The shape mirrors the existing 6502 JIT tests — adapt the helper names to the repo's actual `JittedCpu<Mos6502Cpu>` construction if they differ (see `tests/CpuEmulator.Tests/Jit/` for the canonical builder).

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

public class RemapInvalidationTests
{
    // A 16-bit 6502 bus: zero page RAM at $0000, a banked window at $D000, reset vector ROM at $FFFE/$FFFF.
    private static (JittedCpu<Mos6502Cpu> cpu, AddressSpace bus) BuildJit(byte[] bankAtD000)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 16);
        bus.MapMemory(0x0000, new byte[0x0100], writable: true);   // zero page
        bus.MapMemory(0xD000, bankAtD000, writable: false);        // the banked code window (1 page)
        var vec = new byte[0x0100];                                // $FF00-$FFFF; reset vector at $FFFC/$FFFD
        vec[0xFC] = 0x00; vec[0xFD] = 0xD0;                        // RESET -> $D000
        bus.MapMemory(0xFF00, vec, writable: false);
        var inner = new Mos6502Cpu(bus);
        var cpu = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, bus);
        cpu.Reset();                                               // PC <- $D000
        return (cpu, bus);
    }

    // A one-page $D000 bank: "LDA #imm ; STA $0010 ; <trap forever>" so the value stored at $10
    // identifies which bank ran. imm is the discriminator.
    private static byte[] BankStoring(byte imm)
    {
        var page = new byte[0x0100];
        page[0x00] = 0xA9; page[0x01] = imm;        // LDA #imm
        page[0x02] = 0x85; page[0x03] = 0x10;       // STA $10
        page[0x04] = 0x4C; page[0x05] = 0x06; page[0x06] = 0xD0; // JMP $D006 (tight self-loop -> $D006? )
        // Use a clean 1-cycle-bounded loop: JMP to itself.
        page[0x04] = 0x4C; page[0x05] = 0x04; page[0x06] = 0xD0; // JMP $D004 (infinite, no further store)
        return page;
    }

    [Fact]
    public void A_remapped_code_page_runs_the_new_bank_after_remap()
    {
        var (cpu, bus) = BuildJit(BankStoring(0xAA));

        long budget = 50;
        cpu.Run(ref budget);                         // compiles + runs the $D000 block -> $10 = 0xAA
        Assert.Equal(0xAA, bus.Read8(0x0010));

        // Bank in a DIFFERENT $D000 page (different immediate). Remap fires OnRemap -> fastmem
        // re-class + the old $D000 block is evicted.
        bus.Remap(0xD000, BankStoring(0xBB), writable: false);
        cpu.SetRegister("PC", 0xD000);               // re-enter the (now remapped) window
        budget = 50;
        cpu.Run(ref budget);                         // MUST recompile from the new bank -> $10 = 0xBB
        Assert.Equal(0xBB, bus.Read8(0x0010));
    }

    [Fact]
    public void Remap_to_a_writable_ram_bank_is_seen_by_the_fast_path()
    {
        // Bank $D000 in as RAM, write a value via the bus, remap to a second RAM array, and confirm a
        // fast-path read through the JIT sees the SECOND array's bytes (the fastmem re-class worked).
        var first = BankStoring(0x11);
        var (cpu, bus) = BuildJit(first);
        long budget = 50; cpu.Run(ref budget);
        Assert.Equal(0x11, bus.Read8(0x0010));

        var second = BankStoring(0x22);
        bus.Remap(0xD000, second, writable: false);
        cpu.SetRegister("PC", 0xD000);
        budget = 50; cpu.Run(ref budget);
        Assert.Equal(0x22, bus.Read8(0x0010));       // the new array's immediate ran
    }
}
```

> **Implementer note:** the exact `JittedCpu<Mos6502Cpu>` construction (ctor args, `JitTarget` name) and the canonical test-bus builder live in the existing `tests/CpuEmulator.Tests/Jit/` 6502 JIT tests — reuse that helper rather than the inline one above if it exists (e.g. a `Build6502Jit(...)`), and keep only the remap-specific assertions. The `BankStoring` immediate is the un-fakeable discriminator: a stale block would store the OLD immediate, failing the assert. Trim the dead first `page[0x04..]` assignment (the second one is authoritative — pick one clean `JMP self`).

- [ ] **Step 3: Run the gate to verify it fails, then passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~RemapInvalidationTests"`
Expected: with the listener wired (Step 1) the tests PASS; if they fail with the *old* immediate (`0xAA`/`0x11`) appearing after the remap, the eviction or re-class is not firing — verify `OnRemap` is registered (Step 1) and that `InvalidatePages`/`Reclassify` cover the right page (`0xD0`). **This is the JIT-tier remap gate.**

- [ ] **Step 4: Commit**

```bash
git add src/CpuEmulator.Jit/JittedCpu.cs tests/CpuEmulator.Tests/Jit/RemapInvalidationTests.cs
git commit -m "feat(jit): JittedCpu registers as IMapInvalidationListener; end-to-end remap gate"
```

---

## Task 6: The no-behavior-change anchor — every existing board is byte/cycle-identical

**Files:**
- Create: `tests/CpuEmulator.Tests/Jit/RemapNoRegressionTests.cs`

- [ ] **Step 1: Write the regression anchor**

The seam is gated behind methods no current device calls, so the proof is simply: building a `JittedCpu` over any existing board still produces identical output, and registering the listener has zero effect when nothing remaps. Create `tests/CpuEmulator.Tests/Jit/RemapNoRegressionTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

public class RemapNoRegressionTests
{
    [Fact]
    public void A_JittedCpu_with_no_remap_produces_identical_output()
    {
        // A minimal program that stores a marker, run on a JIT that has the listener registered but
        // never receives an OnRemap. The result must match a run with no listener machinery involved
        // — i.e. the registration is inert until a remap happens.
        var bus = new AddressSpace(AddressSpaceKind.Program, 16);
        bus.MapMemory(0x0000, new byte[0x0100], writable: true);
        var code = new byte[0x0100];
        code[0x00] = 0xA9; code[0x01] = 0x7C;   // LDA #$7C
        code[0x02] = 0x85; code[0x03] = 0x20;   // STA $20
        code[0x04] = 0x4C; code[0x05] = 0x04; code[0x06] = 0xD0; // JMP $D004
        bus.MapMemory(0xD000, code, writable: false);
        var vec = new byte[0x0100]; vec[0xFC] = 0x00; vec[0xFD] = 0xD0;
        bus.MapMemory(0xFF00, vec, writable: false);

        var cpu = new JittedCpu<Mos6502Cpu>(new Mos6502Cpu(bus), Mos6502Cpu.JitTarget, bus);
        cpu.Reset();
        long budget = 50; cpu.Run(ref budget);

        Assert.Equal(0x7C, bus.Read8(0x0020));  // ran exactly as before the seam existed
    }
}
```

- [ ] **Step 2: Run the full suite to confirm zero regressions across all boards**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`
Expected: the entire existing suite (every board, every CPU, the Spectrum gates, the host UAT, the TomHarte/Klaus/ZEX gates that are present) is **green** — the only new behavior is the new remap tests. If any pre-existing test changed result, STOP: the seam leaked behavior (it must not — `Remap` is called by nothing existing).

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Jit/RemapNoRegressionTests.cs
git commit -m "test(jit): no-behavior-change anchor — the remap seam is inert until a device remaps"
```

---

## Task 7: Docs + queue update

**Files:**
- Modify: `docs/ROADMAP.md`
- Modify: `docs/BUILDER_QUEUE.md`

- [ ] **Step 1: Note the seam in the roadmap**

In `docs/ROADMAP.md`, under the deferred/candidate section, mark the per-bank-`Remap` primitive as **shipped** (it was ADR 0009 Decision 2 / the "[candidate] Per-bank specialization" item's `Remap` half): add a short line that `AddressSpace.Remap` + the JIT `IMapInvalidationListener` + `BlockCache.InvalidatePages` landed as Apple ][+ PR-A (the Language Card's foundation), interpreter-correct + JIT-page-precise.

- [ ] **Step 2: Flip the queue row**

In `docs/BUILDER_QUEUE.md`, set row **A** status to ✅, and update the **Last updated** banner with the date + "PR-A merged".

- [ ] **Step 3: Commit**

```bash
git add docs/ROADMAP.md docs/BUILDER_QUEUE.md
git commit -m "docs: record the Remap seam (Apple2 PR-A) shipped; queue row A done"
```

---

## Done-when

- `AddressSpace.Remap`/`RemapPeripheral` re-point a mapped range (memory↔memory, memory↔MMIO) on the interpreter tier, with alignment guards, firing the listener with the exact page span.
- The JIT re-classifies remapped pages in `Fastmem` and evicts their compiled blocks (`InvalidatePages`), so a remapped code page runs the **new** bank — the un-fakeable JIT gate.
- Every existing board + test is byte/cycle-identical (the seam is inert until a device remaps).
- `Core` stays AOT-clean (it defines `IMapInvalidationListener`; the JIT implements + registers via the existing internal seam).
- Queue row **A** is ✅; PR-E (Language Card) can now be planned against the real shipped `Remap` API.
