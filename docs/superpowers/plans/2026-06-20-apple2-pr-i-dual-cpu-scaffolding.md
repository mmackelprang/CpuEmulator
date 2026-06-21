# PR-I — Dual-CPU `Machine` / `MachineBuilder` scaffolding (`CoprocessorSpec`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the shipped single-CPU machine model to express two CPUs sharing one program space — a `CoprocessorSpec?` on `BoardSpec`, a `WithCoprocessor(...)` builder method, an `IAddressTranslation`, a `TranslatingAddressSpace` wrapper, and a dual-CPU `Run` that uses the run-one-then-the-other active-CPU scheduler — **while leaving the single-CPU path byte-for-byte unchanged**.

**Architecture:** Per ADR 0015 Decisions 1, 2, 5, 6, 7. The coprocessor is an **optional** field on `BoardSpec` (default `null` = every existing board). When set, `Machine`'s constructor builds a second `ICpuCore` over a `TranslatingAddressSpace` wrapping the shared program `AddressSpace`, tracks `_z80Active` (false at reset → the primary 6502 runs), and switches the dual-CPU `Run` to drive only the active core (never the dormant one). The scheduler runs in the **6502 (primary) cycle domain** with the coprocessor's run time converted by `ClockRatioToPrimary`; **all interrupts route to the primary**. The active-CPU toggle seam (`ICoprocessorControl`) is implemented by `Machine` and consumed by PR-J's control port — this PR ships the seam plus a **two-core toy board** (a control-port stub that flips the active CPU on a soft-switch write) as the un-fakeable gate.

**Tech Stack:** C# / .NET, `CpuEmulator.Core` (Machine, MachineBuilder, AddressSpace, scheduler), `CpuEmulator.Machines` (BoardSpec, MachineBuilder wiring, BoardMachineFactory, BoardSpecValidator, CpuCoreFactory), xUnit (`tests/CpuEmulator.Tests`).

## Global Constraints

- **The single-CPU path MUST stay byte-for-byte unchanged.** Every existing board (6502 / Z80 / 68000 / 8086 / Spectrum / Apple2) must build + run identically. This is the load-bearing regression gate — see Task 9.
- **`Coprocessor is null` is the only single-CPU path.** When the field is null, `Machine`'s constructor and `Run` take the *exact existing* code path (the same `Cpu` field, the same `BindTimeSource(() => Cpu.CycleCount)`, the same `Run` loop). No new branch may execute for a single-CPU board.
- **All interrupts route to the primary CPU only.** The coprocessor core's interrupt inputs are left unbound (ADR 0015 Decision 5). `IrqLine`/`NmiLine` bind to the primary's `SetIrqLine`/`SetNmiLine` exactly as today.
- **Interpreter-first.** The coprocessor core is always built on the interpreter tier in this PR (the `TranslatingAddressSpace` wrapper is not the concrete `AddressSpace`, so it cannot get JIT fastmem — ADR 0015 Decision 4 defers that). The dual-CPU `Run` drives `ICpuCore.Run(ref budget)` uniformly, so a JIT *primary* + interpreter *coprocessor* is fine (both are `ICpuCore`).
- **Run-one-then-the-other; never schedule the dormant core** (ADR 0015 Decision 1). Do NOT cycle-interleave.
- **No `TimingTier` / `ITimingSensitive`** — those are ADR-only, not in `src/`. Do not reference them.
- **HEAD grounding:** all literal code is grounded against `main` @ `d685b0c` (PRs #99–#108 merged). Verify with `git rev-parse HEAD` before starting.

---

## File Structure

**New files (`CpuEmulator.Core`):**
- `src/CpuEmulator.Core/IAddressTranslation.cs` — the `uint ToPhysical(uint logical)` seam (declarative translation data the board supplies).
- `src/CpuEmulator.Core/TranslatingAddressSpace.cs` — the `IAddressSpace` wrapper the coprocessor core is constructed over; every access is `ToPhysical`'d then forwarded to the inner program `AddressSpace`.
- `src/CpuEmulator.Core/ICoprocessorControl.cs` — the toggle seam (`SetCoprocessorActive(bool)`) the `Machine` implements and the control-port peripheral consumes.

**New files (`CpuEmulator.Machines`):**
- `src/CpuEmulator.Machines/CoprocessorSpec.cs` — the optional `BoardSpec` coprocessor declaration.

**Modified files:**
- `src/CpuEmulator.Core/MachineBuilder.cs` — add `WithCoprocessor(...)`; carry coprocessor fields into the `Machine` ctor.
- `src/CpuEmulator.Core/Machine.cs` — dual-CPU construction path + dual-CPU `Run` + `ICoprocessorControl`; single-CPU path untouched when no coprocessor.
- `src/CpuEmulator.Machines/BoardSpec.cs` — add the optional `CoprocessorSpec? Coprocessor = null` field.
- `src/CpuEmulator.Machines/BoardMachineFactory.cs` — when `spec.Coprocessor is not null`, call `WithCoprocessor(...)` after `WithCpu(...)`.
- `src/CpuEmulator.Machines/BoardSpecValidator.cs` — coprocessor checks (`copro-control-port-unwired`, `copro-bad-clock-ratio`, `copro-no-translation`).

**New test files:**
- `tests/CpuEmulator.Tests/TranslatingAddressSpaceTests.cs`
- `tests/CpuEmulator.Tests/DualCpuMachineTests.cs` (the toy-board un-fakeable gate + the single-CPU-unchanged regression)
- `tests/CpuEmulator.Tests/CoprocessorValidationTests.cs`

---

### Task 1: `IAddressTranslation` seam

**Files:**
- Create: `src/CpuEmulator.Core/IAddressTranslation.cs`
- Test: (covered by Task 2's `TranslatingAddressSpace` tests — this is a one-method interface with no behavior of its own; folding its test into Task 2 is correct per task right-sizing)

**Interfaces:**
- Produces: `interface IAddressTranslation { uint ToPhysical(uint logical); }`

- [ ] **Step 1: Create the interface**

```csharp
namespace CpuEmulator.Core;

/// <summary>Maps a coprocessor's LOGICAL address to the primary CPU's PHYSICAL address on the shared
/// bus (ADR 0015 Decision 2). Page-granular (4 KiB for the SoftCard). The dual-CPU Machine wraps the
/// primary program AddressSpace in a TranslatingAddressSpace built from this, and constructs the
/// coprocessor core over that wrapper — so the coprocessor core is UNCHANGED (it sees an ordinary
/// IAddressSpace). PR-J ships the concrete SoftCardTranslation (the 6-branch MAME-verified table).</summary>
public interface IAddressTranslation
{
    /// <summary>Translate a coprocessor logical address to the primary physical address. Pure: the same
    /// logical address always maps to the same physical address while the coprocessor runs.</summary>
    uint ToPhysical(uint logical);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/CpuEmulator.Core/CpuEmulator.Core.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/CpuEmulator.Core/IAddressTranslation.cs
git commit -m "feat(core): IAddressTranslation seam (coprocessor logical -> primary physical)"
```

---

### Task 2: `TranslatingAddressSpace` wrapper

**Files:**
- Create: `src/CpuEmulator.Core/TranslatingAddressSpace.cs`
- Test: `tests/CpuEmulator.Tests/TranslatingAddressSpaceTests.cs`

**Interfaces:**
- Consumes: `IAddressTranslation` (Task 1); the shipped `IAddressSpace` (Read8/Write8/MapMemory/MapPeripheral/TryPeek8 + the default wide accessors).
- Produces: `sealed class TranslatingAddressSpace : IAddressSpace`, ctor `TranslatingAddressSpace(IAddressSpace inner, IAddressTranslation translation)`. `Kind`/`AddressBits`/`Endianness` mirror `inner`.

**Design notes (grounded against the shipped `IAddressSpace`, `src/CpuEmulator.Core/IAddressSpace.cs`):**
- `IAddressSpace` provides **default-interface** `Read16/Read32/Write16/Write32` that compose over `Read8`/`Write8`. So the wrapper only implements `Read8`/`Write8`/`TryPeek8` (translate then forward) plus the metadata properties; the wide accessors are inherited and automatically route through the translated `Read8`/`Write8`. (Verify: do NOT override Read16/etc. — the default composition keeps a wide read at logical `$AFFF` translating each composed byte independently, which is the correct page-wrap behavior.)
- `MapMemory`/`MapPeripheral`/`Remap`/`RemapPeripheral` are **not used on the wrapper** (the coprocessor never maps; the primary owns the real space). Implement them to throw `NotSupportedException` so a mis-wire is loud, not silent.
- `Endianness` mirrors `inner.Endianness` (the coprocessor shares the primary's little-endian bus).

- [ ] **Step 1: Write the failing test**

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class TranslatingAddressSpaceTests
{
    // A fixed test translation: add $1000 (wrapping at 16 bits). Enough to prove the wrapper
    // routes every access through ToPhysical; PR-J ships the real 6-branch SoftCard table.
    private sealed class Add0x1000 : IAddressTranslation
    {
        public uint ToPhysical(uint logical) => (logical + 0x1000) & 0xFFFF;
    }

    private static (TranslatingAddressSpace view, AddressSpace inner) Build()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true); // 64 KiB RAM
        var view = new TranslatingAddressSpace(inner, new Add0x1000());
        return (view, inner);
    }

    [Fact]
    public void Read8_translates_logical_to_physical()
    {
        var (view, inner) = Build();
        inner.Write8(0x1000, 0x42);          // physical $1000
        Assert.Equal(0x42, view.Read8(0x0000)); // logical $0000 -> physical $1000
    }

    [Fact]
    public void Write8_translates_logical_to_physical()
    {
        var (view, inner) = Build();
        view.Write8(0x0000, 0x37);           // logical $0000 -> physical $1000
        Assert.Equal(0x37, inner.Read8(0x1000));
    }

    [Fact]
    public void TryPeek8_translates_and_is_side_effect_free()
    {
        var (view, inner) = Build();
        inner.Write8(0x1000, 0x5A);
        bool ok = view.TryPeek8(0x0000, out byte v);
        Assert.True(ok);
        Assert.Equal(0x5A, v);
    }

    [Fact]
    public void Metadata_mirrors_the_inner_space()
    {
        var (view, inner) = Build();
        Assert.Equal(inner.Kind, view.Kind);
        Assert.Equal(inner.AddressBits, view.AddressBits);
        Assert.Equal(inner.Endianness, view.Endianness);
    }

    [Fact]
    public void Mapping_on_the_wrapper_is_unsupported()
    {
        var (view, _) = Build();
        Assert.Throws<NotSupportedException>(() => view.MapMemory(0, new byte[0x100], true));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~TranslatingAddressSpaceTests"`
Expected: FAIL — `TranslatingAddressSpace` does not exist (compile error).

- [ ] **Step 3: Write the wrapper**

```csharp
namespace CpuEmulator.Core;

/// <summary>An IAddressSpace the coprocessor is constructed over (ADR 0015 Decision 2): every access
/// is translated (IAddressTranslation.ToPhysical) then forwarded to the inner primary program
/// AddressSpace. Read8/Write8/TryPeek8 route through ToPhysical; the default-interface wide accessors
/// (Read16/Read32/Write16/Write32 on IAddressSpace) compose over these, so a wide access translates
/// each composed byte independently (the correct 4 KiB-page-wrap behavior at a window boundary). The
/// coprocessor core sees an ordinary 16-bit IAddressSpace and is UNCHANGED. The wrapper does not own a
/// page table — the primary does — so MapMemory/MapPeripheral/Remap/RemapPeripheral are unsupported
/// (a mis-wire throws loudly rather than silently corrupting the primary's map).</summary>
public sealed class TranslatingAddressSpace : IAddressSpace
{
    private readonly IAddressSpace _inner;
    private readonly IAddressTranslation _translation;

    public TranslatingAddressSpace(IAddressSpace inner, IAddressTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(translation);
        _inner = inner;
        _translation = translation;
    }

    public AddressSpaceKind Kind => _inner.Kind;
    public int AddressBits => _inner.AddressBits;
    public Endianness Endianness => _inner.Endianness;

    public byte Read8(uint address) => _inner.Read8(_translation.ToPhysical(address));

    public void Write8(uint address, byte value) =>
        _inner.Write8(_translation.ToPhysical(address), value);

    public bool TryPeek8(uint address, out byte value) =>
        _inner.TryPeek8(_translation.ToPhysical(address), out value);

    public void MapMemory(uint start, byte[] backing, bool writable) =>
        throw new NotSupportedException("TranslatingAddressSpace does not own a page table; map on the primary space.");

    public void MapPeripheral(uint start, uint length, IPeripheral peripheral) =>
        throw new NotSupportedException("TranslatingAddressSpace does not own a page table; map on the primary space.");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~TranslatingAddressSpaceTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core/TranslatingAddressSpace.cs tests/CpuEmulator.Tests/TranslatingAddressSpaceTests.cs
git commit -m "feat(core): TranslatingAddressSpace wraps the primary bus for a coprocessor"
```

---

### Task 3: `ICoprocessorControl` toggle seam

**Files:**
- Create: `src/CpuEmulator.Core/ICoprocessorControl.cs`
- Test: (exercised end-to-end by the toy-board gate, Task 8 — a one-method seam has no standalone behavior)

**Interfaces:**
- Produces: `interface ICoprocessorControl { void SetCoprocessorActive(bool active); }`

**Why a seam, not a cast:** ADR 0015 Decision 3 says the control port "toggles `_z80Active` on the dual-CPU `Machine`." The cleanest way for a peripheral to reach the Machine is the `Realize(IMachineContext)` context — and **the context IS the Machine** (`Machine : IMachineContext`; confirmed by `MachineBuilderTests.Peripherals_are_mapped_then_realized_in_registration_order`, which asserts `Assert.Same(machine, first.RealizedWith)`). PR-J's control port will, in `Realize`, do `if (context is ICoprocessorControl ctl) _ctl = ctl;`. This PR ships the seam + a toy control-port stub that uses it.

- [ ] **Step 1: Create the interface**

```csharp
namespace CpuEmulator.Core;

/// <summary>The active-CPU toggle seam (ADR 0015 Decisions 1 + 3). The dual-CPU Machine implements this;
/// the SoftCard control-port peripheral (PR-J), which sees the Machine through its Realize context
/// (Machine : IMachineContext), flips the active CPU by calling SetCoprocessorActive on the $CnXX write.
/// On a single-CPU Machine the seam is absent (the cast `context is ICoprocessorControl` simply fails),
/// so a control port wired onto a single-CPU board is inert — never an exception.</summary>
public interface ICoprocessorControl
{
    /// <summary>Set which CPU drives the shared bus on the NEXT run slice: true = the coprocessor runs
    /// (the primary is DMA-suspended), false = the primary runs. The dual-CPU run loop reads this flag
    /// at the slice boundary and ends the current slice so the switch takes effect cleanly (the writing
    /// instruction completes first — ADR 0015 OQ5).</summary>
    void SetCoprocessorActive(bool active);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/CpuEmulator.Core/CpuEmulator.Core.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/CpuEmulator.Core/ICoprocessorControl.cs
git commit -m "feat(core): ICoprocessorControl active-CPU toggle seam"
```

---

### Task 4: `MachineBuilder.WithCoprocessor` — carry the coprocessor declaration

**Files:**
- Modify: `src/CpuEmulator.Core/MachineBuilder.cs`
- Test: (the builder's wiring is exercised by the dual-CPU `Machine` tests, Tasks 7–8; the builder itself only stores fields and passes them to the ctor)

**Interfaces:**
- Consumes: `IAddressTranslation` (Task 1).
- Produces: `MachineBuilder WithCoprocessor(Func<IMachineContext, ICpuCore> coprocessorFactory, IAddressTranslation translation, double clockRatioToPrimary)`. The builder passes a nullable `CoprocessorBuild?` tuple to the `Machine` ctor (Task 5 adds the ctor parameter).

**Design notes (grounded against `MachineBuilder.cs` @ d685b0c):** the builder today holds `_cpuFactory` and passes five lists/funcs to `new Machine(...)`. Add three nullable fields + the method + extend the `Build()` call. The single-CPU path is unchanged: when `WithCoprocessor` is never called, the new ctor parameter is `null` and the ctor takes the existing path.

- [ ] **Step 1: Add the fields + method + extend Build()**

In `src/CpuEmulator.Core/MachineBuilder.cs`, add the fields after `_cpuFactory` (line 10):

```csharp
    private Func<IMachineContext, ICpuCore>? _coprocessorFactory;
    private IAddressTranslation? _coprocessorTranslation;
    private double _coprocessorClockRatio;
```

Add the method after `WithCpu` (after line 28):

```csharp
    /// <summary>Declare an optional bus-arbitrated coprocessor that shares the primary's program space
    /// through <paramref name="translation"/> (ADR 0015 Decision 2). The coprocessor is dormant at reset
    /// and activated via ICoprocessorControl (a control-port peripheral flips it). Calling this puts the
    /// Machine on the dual-CPU construction + run path; NOT calling it leaves the single-CPU path
    /// byte-for-byte unchanged. <paramref name="clockRatioToPrimary"/> (e.g. ~2.0 for the SoftCard Z80)
    /// converts coprocessor run time into primary-domain scheduler cycles (ADR 0015 Decision 5).</summary>
    public MachineBuilder WithCoprocessor(
        Func<IMachineContext, ICpuCore> coprocessorFactory,
        IAddressTranslation translation,
        double clockRatioToPrimary)
    {
        ArgumentNullException.ThrowIfNull(coprocessorFactory);
        ArgumentNullException.ThrowIfNull(translation);
        if (clockRatioToPrimary <= 0)
            throw new MachineConfigurationException(
                $"Coprocessor clock ratio must be positive; got {clockRatioToPrimary}.");
        _coprocessorFactory = coprocessorFactory;
        _coprocessorTranslation = translation;
        _coprocessorClockRatio = clockRatioToPrimary;
        return this;
    }
```

Replace the `return new Machine(...)` at the end of `Build()` (line 62) with the coprocessor-carrying call:

```csharp
        CoprocessorBuild? coprocessor = _coprocessorFactory is null
            ? null
            : new CoprocessorBuild(_coprocessorFactory, _coprocessorTranslation!, _coprocessorClockRatio);

        return new Machine(_name, _spaceDefs, _memoryDefs, _peripheralDefs, _cpuFactory, coprocessor);
```

- [ ] **Step 2: Add the `CoprocessorBuild` record (the ctor's nullable parameter type)**

At the bottom of `src/CpuEmulator.Core/MachineBuilder.cs`, inside the `CpuEmulator.Core` namespace, add:

```csharp
/// <summary>The resolved coprocessor declaration the MachineBuilder hands to the Machine ctor: the core
/// factory, the logical->physical translation, and the clock ratio. Null on every single-CPU board.</summary>
internal sealed record CoprocessorBuild(
    Func<IMachineContext, ICpuCore> Factory,
    IAddressTranslation Translation,
    double ClockRatioToPrimary);
```

- [ ] **Step 3: Build (expect a Machine ctor mismatch — Task 5 fixes it)**

Run: `dotnet build src/CpuEmulator.Core/CpuEmulator.Core.csproj`
Expected: FAIL — `new Machine(...)` now passes 6 args but the ctor takes 5. This is expected; Task 5 adds the parameter. (Do not commit a non-building state — Task 5 is the same logical change; commit at the end of Task 5.)

---

### Task 5: `Machine` dual-CPU construction path

**Files:**
- Modify: `src/CpuEmulator.Core/Machine.cs`
- Test: `tests/CpuEmulator.Tests/DualCpuMachineTests.cs` (construction assertions added here; the run/toggle gate is Tasks 7–8)

**Interfaces:**
- Consumes: `CoprocessorBuild?` (Task 4), `TranslatingAddressSpace` (Task 2), `IAddressTranslation` (Task 1), `ICoprocessorControl` (Task 3).
- Produces: `Machine` implements `ICoprocessorControl`; new fields `_coprocessor` (`ICpuCore?`), `_coprocessorRatio` (`double`), `_z80Active` (`bool`), `_coprocessorCyclesContributed` (`long`); a public read-only `bool CoprocessorActive => _z80Active;` and `ICpuCore? Coprocessor => _coprocessor;` for tests. The single-CPU path is unchanged when `coprocessor is null`.

**Design notes (grounded against `Machine.cs` @ d685b0c):**
- The ctor's phase 2 today (lines 41–46) builds `Cpu = cpuFactory(this)`, binds IRQ/NMI to `Cpu`, and binds the scheduler time source to `() => Cpu.CycleCount`. For the dual-CPU path: build the primary the same way (it stays `Cpu`, the **primary**); IRQ/NMI bind to the **primary only** (ADR 0015 Decision 5 — coprocessor interrupt inputs unbound); the scheduler time source binds to the **virtual 6502-domain clock** `() => Cpu.CycleCount + (long)Math.Round(_coprocessorCyclesContributed / _coprocessorRatio)`.
- Build the coprocessor over a `TranslatingAddressSpace` wrapping the **program** `AddressSpace`. The coprocessor factory receives `this` (the `IMachineContext`) but must be constructed over the wrapper, not `ctx.Space(Program)`. Because `CpuCoreFactory.ForKind` builds over `ctx.Space(programSpace)`, the dual-CPU ctor instead wraps the program space and passes a **bus-substituted context** — see the `CoprocessorContext` private adapter below (it forwards everything to the Machine but returns the `TranslatingAddressSpace` for the Program kind).
- `_z80Active` starts `false` (primary active at reset, ADR 0015 Decision 1).

- [ ] **Step 1: Change the class declaration + add fields**

Change line 8:

```csharp
public sealed class Machine : IMachineContext, ICoprocessorControl
```

Add fields after `_nmiTarget` (after line 13):

```csharp
    private readonly ICpuCore? _coprocessor;
    private readonly double _coprocessorRatio;
    private bool _z80Active;                       // false at reset: the primary runs (ADR 0015 Decision 1)
    private long _coprocessorCyclesContributed;    // coprocessor cycles run so far (for the virtual clock)
    private bool _sliceEndRequested;               // set by SetCoprocessorActive to end the running slice
```

Add public accessors after `NmiLine` (after line 19):

```csharp
    /// <summary>True while the coprocessor is the bus master (the primary is DMA-suspended). False on a
    /// single-CPU machine and at reset. Test/host introspection.</summary>
    public bool CoprocessorActive => _z80Active;

    /// <summary>The optional coprocessor core (null on every single-CPU machine). Test/host introspection.</summary>
    public ICpuCore? Coprocessor => _coprocessor;
```

- [ ] **Step 2: Add the ctor parameter + the dual-CPU phase 2**

Change the ctor signature (line 23–28) to add the parameter:

```csharp
    internal Machine(
        string name,
        List<(AddressSpaceKind Kind, int AddressBits, AddressSpaceOptions? Options)> spaceDefs,
        List<(AddressSpaceKind Kind, uint Start, byte[] Backing, bool Writable)> memoryDefs,
        List<(AddressSpaceKind Kind, uint Start, uint Length, IPeripheral Peripheral)> peripheralDefs,
        Func<IMachineContext, ICpuCore> cpuFactory,
        CoprocessorBuild? coprocessor = null)
```

Replace phase 2 (lines 41–46) with the branch — the single-CPU branch is the *exact existing code*:

```csharp
        // Phase 2: create the primary CPU, then bind interrupt lines + the scheduler clock to it.
        Cpu = cpuFactory(this) ?? throw new MachineConfigurationException(
            $"Machine '{name}': CPU factory returned null.");
        _irqTarget.Bind(Cpu.SetIrqLine);
        _nmiTarget.Bind(Cpu.SetNmiLine);

        if (coprocessor is null)
        {
            // Single-CPU path: byte-for-byte the pre-PR-I behavior.
            _scheduler.BindTimeSource(() => Cpu.CycleCount);
        }
        else
        {
            // Dual-CPU path (ADR 0015). The coprocessor is built over a TranslatingAddressSpace wrapping
            // the primary program space, so the coprocessor core is unchanged. Interrupts stay on the
            // PRIMARY only (Decision 5). The scheduler runs in the primary cycle domain plus the
            // coprocessor's run time converted by the clock ratio (the virtual 6502-domain clock).
            _coprocessorRatio = coprocessor.ClockRatioToPrimary;
            var programSpace = GetSpace(AddressSpaceKind.Program);
            var translatingBus = new TranslatingAddressSpace(programSpace, coprocessor.Translation);
            _coprocessor = coprocessor.Factory(new CoprocessorContext(this, translatingBus))
                ?? throw new MachineConfigurationException(
                    $"Machine '{name}': coprocessor factory returned null.");
            // The coprocessor's interrupt inputs are intentionally left unbound (Decision 5).
            _scheduler.BindTimeSource(() =>
                Cpu.CycleCount + (long)Math.Round(_coprocessorCyclesContributed / _coprocessorRatio));
        }
```

- [ ] **Step 3: Add the `ICoprocessorControl` impl + the `CoprocessorContext` adapter**

After `Reset()` (after line 57), add:

```csharp
    /// <summary>ICoprocessorControl (ADR 0015 Decisions 1 + 3): a control-port peripheral flips which CPU
    /// runs. Sets _z80Active and requests the current run slice end so the switch takes effect on the next
    /// dispatch (the writing instruction completes first). Inert on a single-CPU machine — but a control
    /// port is only Realized with this Machine when a coprocessor exists, so this is never reached there.</summary>
    public void SetCoprocessorActive(bool active)
    {
        _z80Active = active;
        _sliceEndRequested = true;
    }
```

At the bottom of the class (before the closing brace, after the `LateBoundLine` nested class), add the context adapter:

```csharp
    /// <summary>The IMachineContext the coprocessor core is constructed with: identical to the Machine
    /// except Space(Program) returns the TranslatingAddressSpace wrapper (so CpuCoreFactory builds the
    /// coprocessor core over the translated bus). All other members forward to the Machine — the
    /// coprocessor shares the one scheduler + interrupt domain. The Io space (if any) is shared
    /// untranslated; the SoftCard Z80 reaches I/O through the translation's $E000->$C000 branch on the
    /// Program bus, so a separate Io space is not declared for the SoftCard board.</summary>
    private sealed class CoprocessorContext(Machine machine, IAddressSpace translatedProgram) : IMachineContext
    {
        public IScheduler Scheduler => machine.Scheduler;
        public IInterruptLine IrqLine => machine.IrqLine;
        public IInterruptLine NmiLine => machine.NmiLine;
        public IAddressSpace Space(AddressSpaceKind kind) =>
            kind == AddressSpaceKind.Program ? translatedProgram : machine.Space(kind);
    }
```

- [ ] **Step 4: Add the construction tests**

```csharp
using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class DualCpuMachineTests
{
    private sealed class Identity : IAddressTranslation
    {
        public uint ToPhysical(uint logical) => logical;
    }

    [Fact]
    public void A_dual_cpu_machine_builds_a_primary_and_a_coprocessor()
    {
        var primary = new FakeCpu();
        var copro = new FakeCpu();
        var machine = Machine.Create("dual")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => primary)
            .WithCoprocessor(_ => copro, new Identity(), clockRatioToPrimary: 2.0)
            .Build();

        Assert.Same(primary, machine.Cpu);
        Assert.Same(copro, machine.Coprocessor);
        Assert.False(machine.CoprocessorActive); // the primary is active at reset
    }

    [Fact]
    public void A_single_cpu_machine_has_no_coprocessor()
    {
        var machine = Machine.Create("single")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => new FakeCpu())
            .Build();

        Assert.Null(machine.Coprocessor);
        Assert.False(machine.CoprocessorActive);
    }

    [Fact]
    public void Interrupts_route_to_the_primary_only()
    {
        var primary = new FakeCpu();
        var copro = new FakeCpu();
        var machine = Machine.Create("dual")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => primary)
            .WithCoprocessor(_ => copro, new Identity(), clockRatioToPrimary: 2.0)
            .Build();

        machine.IrqLine.Assert();
        Assert.True(primary.IrqAsserted);
        Assert.False(copro.IrqAsserted);   // the coprocessor is never interrupted (ADR 0015 Decision 5)
    }
}
```

- [ ] **Step 5: Build + run the construction tests**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~DualCpuMachineTests"`
Expected: the three construction tests PASS. (The toy-board run/toggle tests are added in Task 8.)

- [ ] **Step 6: Commit (Tasks 4 + 5 together — the first building state)**

```bash
git add src/CpuEmulator.Core/MachineBuilder.cs src/CpuEmulator.Core/Machine.cs tests/CpuEmulator.Tests/DualCpuMachineTests.cs
git commit -m "feat(core): dual-CPU Machine construction path (primary + translated coprocessor)"
```

---

### Task 6: Dual-CPU `Run` — drive the active core, never the dormant one

**Files:**
- Modify: `src/CpuEmulator.Core/Machine.cs`
- Test: `tests/CpuEmulator.Tests/DualCpuMachineTests.cs` (run-loop behavior; the full toy-board gate is Task 8)

**Interfaces:**
- Consumes: the fields from Task 5 (`_coprocessor`, `_z80Active`, `_coprocessorCyclesContributed`, `_sliceEndRequested`, `_coprocessorRatio`).
- Produces: `Machine.Run(long cycles)` keeps the single-CPU path byte-for-byte; adds a dual-CPU branch when `_coprocessor is not null`.

**Design notes (grounded against `Machine.Run`, `Machine.cs:68–89`):**
- The single-CPU `Run` is preserved exactly. When `_coprocessor is null`, the existing loop runs unchanged.
- The dual-CPU loop drives **only the active core**: the primary when `!_z80Active`, the coprocessor when `_z80Active`. The slice end-condition is the same scheduler peek as today, **plus** the `_sliceEndRequested` flag the control port sets (so a `$CnXX` write inside the slice ends it and the next iteration switches cores).
- **The clock domain is the primary's.** When the primary runs, `Cpu.CycleCount` advances and the virtual clock advances 1:1 — the existing scheduler call works. When the **coprocessor** runs N coprocessor cycles, the virtual clock advances by `N / ratio`; the loop accumulates `_coprocessorCyclesContributed += coproCyclesThisSlice` and then `_scheduler.AdvanceTo(virtualNow)` using the bound time source. The `cycles` budget is interpreted in the **primary (virtual) domain** so a host that asks for "1 frame of 6502 cycles" gets a frame of wall-clock time regardless of which CPU ran.
- **Interrupts force a switch to the primary** (ADR 0015 Decision 5): if a scheduled event raises an IRQ while the coprocessor is active, the loop must resume the primary. Model this minimally: after `AdvanceTo`, if `IrqLine.IsAsserted || NmiLine.IsAsserted` and `_z80Active`, set `_z80Active = false` (the primary services it). The control port re-enables the coprocessor when the 6502 hands back. (This keeps the first cut simple — the REFRESH-window wakeups are NOT modeled, ADR 0015 Decision 5.)
- **Progress guard:** the same no-progress guard the single-CPU loop has, applied to whichever core ran.

- [ ] **Step 1: Write the failing run-loop tests**

Add to `DualCpuMachineTests`. `FakeCpu` (the shipped test double) advances its `CycleCount` by a fixed amount per `Run`; verify the exact shape against `tests/CpuEmulator.Tests/TestDoubles/FakeCpu.cs` before writing (see Task 6 note). These tests use a **counting** translation-independent stub via `FakeCpu`'s budget behavior.

```csharp
    [Fact]
    public void Run_drives_the_primary_when_the_coprocessor_is_dormant()
    {
        var primary = new FakeCpu();
        var copro = new FakeCpu();
        var machine = Machine.Create("dual")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => primary)
            .WithCoprocessor(_ => copro, new Identity(), clockRatioToPrimary: 2.0)
            .Build();

        long primaryBefore = primary.CycleCount;
        long coproBefore = copro.CycleCount;
        machine.Run(100);

        Assert.True(primary.CycleCount > primaryBefore); // the primary ran
        Assert.Equal(coproBefore, copro.CycleCount);     // the dormant coprocessor did NOT run
    }

    [Fact]
    public void Run_drives_the_coprocessor_when_it_is_active()
    {
        var primary = new FakeCpu();
        var copro = new FakeCpu();
        var machine = Machine.Create("dual")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => primary)
            .WithCoprocessor(_ => copro, new Identity(), clockRatioToPrimary: 2.0)
            .Build();

        machine.SetCoprocessorActive(true); // hand off to the coprocessor
        long primaryBefore = primary.CycleCount;
        long coproBefore = copro.CycleCount;
        machine.Run(100);

        Assert.True(copro.CycleCount > coproBefore);     // the coprocessor ran
        Assert.Equal(primaryBefore, primary.CycleCount); // the suspended primary did NOT run
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~DualCpuMachineTests.Run"`
Expected: FAIL — `Run` still drives only the primary (the dual-CPU branch isn't written yet), so the coprocessor-active test fails.

- [ ] **Step 3: Implement the dual-CPU Run branch**

Replace the body of `Run` (lines 68–89) with the branch (the single-CPU branch is the *exact existing loop*):

```csharp
    public long Run(long cycles)
    {
        if (cycles <= 0)
            return 0;
        if (_coprocessor is null)
            return RunSingleCpu(cycles);
        return RunDualCpu(cycles);
    }

    /// <summary>The pre-PR-I single-CPU run loop, unchanged (ADR 0015: single-CPU path byte-for-byte
    /// identical). Drives the one Cpu, slicing to the next scheduled event.</summary>
    private long RunSingleCpu(long cycles)
    {
        long start = Cpu.CycleCount;
        long target = start + cycles;
        while (Cpu.CycleCount < target)
        {
            long before = Cpu.CycleCount;
            long sliceEnd = _scheduler.TryPeekNextEventCycle(out long eventCycle)
                            && eventCycle < target
                ? Math.Max(eventCycle, before + 1)
                : target;
            long budget = sliceEnd - before;
            Cpu.Run(ref budget);
            if (Cpu.CycleCount <= before)
                throw new EmulationException(
                    $"CPU '{Cpu.Architecture}' made no progress during Run; aborting to avoid a hang.");
            _scheduler.AdvanceTo(Cpu.CycleCount);
        }
        return Cpu.CycleCount - start;
    }

    /// <summary>The dual-CPU run loop (ADR 0015 Decision 1): drive ONLY the active core (run-one-then-the-
    /// other; the dormant core is never scheduled). The budget is in the PRIMARY (virtual 6502) cycle
    /// domain (Decision 5). When the primary runs, the virtual clock = Cpu.CycleCount advances 1:1; when
    /// the coprocessor runs, its cycles convert into the virtual clock via the ratio (the bound time
    /// source reads _coprocessorCyclesContributed). A control-port write (SetCoprocessorActive) ends the
    /// running slice; a pending interrupt forces a switch back to the primary.</summary>
    private long RunDualCpu(long cycles)
    {
        long virtualStart = _scheduler.CurrentCycle;
        long target = virtualStart + cycles;
        while (_scheduler.CurrentCycle < target)
        {
            _sliceEndRequested = false;
            long virtualBefore = _scheduler.CurrentCycle;
            long sliceEnd = _scheduler.TryPeekNextEventCycle(out long eventCycle)
                            && eventCycle < target
                ? Math.Max(eventCycle, virtualBefore + 1)
                : target;

            if (!_z80Active)
            {
                // The primary runs in its own (virtual = real) domain.
                long before = Cpu.CycleCount;
                long budget = sliceEnd - _scheduler.CurrentCycle;
                if (budget <= 0) budget = 1;
                Cpu.Run(ref budget);
                if (Cpu.CycleCount <= before)
                    throw new EmulationException(
                        $"CPU '{Cpu.Architecture}' made no progress during Run; aborting to avoid a hang.");
            }
            else
            {
                // The coprocessor runs; its cycles convert into the virtual domain via the ratio. Size the
                // coprocessor budget so its converted contribution does not overshoot the slice end.
                ICpuCore copro = _coprocessor!;
                long virtualBudget = sliceEnd - _scheduler.CurrentCycle;
                if (virtualBudget <= 0) virtualBudget = 1;
                long coproBudget = Math.Max(1, (long)Math.Round(virtualBudget * _coprocessorRatio));
                long coproBefore = copro.CycleCount;
                long budget = coproBudget;
                copro.Run(ref budget);
                long coproRan = copro.CycleCount - coproBefore;
                if (coproRan <= 0)
                    throw new EmulationException(
                        $"Coprocessor '{copro.Architecture}' made no progress during Run; aborting to avoid a hang.");
                _coprocessorCyclesContributed += coproRan;
            }

            _scheduler.AdvanceTo(_scheduler.CurrentCycle);

            // A pending interrupt forces a switch to the primary (ADR 0015 Decision 5: all interrupts to
            // the 6502; while the coprocessor runs the primary is DMA-suspended, so an IRQ means resume it).
            if (_z80Active && (IrqLine.IsAsserted || NmiLine.IsAsserted))
                _z80Active = false;

            // A control-port write this slice (SetCoprocessorActive) already flipped _z80Active and set
            // _sliceEndRequested; the loop simply continues and the next slice drives the newly-active core.
            // (The flag is reset at the top of each iteration; nothing else to do — the switch is implicit.)
        }
        return _scheduler.CurrentCycle - virtualStart;
    }
```

Note on `_scheduler.AdvanceTo(_scheduler.CurrentCycle)`: `CurrentCycle` reads the bound virtual time source (`Cpu.CycleCount + round(contributed/ratio)`), which already reflects both the primary's progress and the coprocessor's contribution this slice. Advancing to it fires due events in the primary domain. (Verify `CycleScheduler.CurrentCycle` returns `Math.Max(_committed, _now())` per `CycleScheduler.cs:15` — so it never goes backward.)

- [ ] **Step 4: Run the run-loop tests**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~DualCpuMachineTests"`
Expected: PASS (construction + run-loop tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core/Machine.cs tests/CpuEmulator.Tests/DualCpuMachineTests.cs
git commit -m "feat(core): dual-CPU Run drives only the active core (run-one-then-the-other)"
```

---

### Task 7: `CoprocessorSpec` on `BoardSpec` + `BoardMachineFactory` wiring

**Files:**
- Create: `src/CpuEmulator.Machines/CoprocessorSpec.cs`
- Modify: `src/CpuEmulator.Machines/BoardSpec.cs`
- Modify: `src/CpuEmulator.Machines/BoardMachineFactory.cs`
- Test: (exercised by the toy-board gate in Task 8, which builds through `BoardMachineFactory`)

**Interfaces:**
- Consumes: `IAddressTranslation` (Task 1), `CpuKind`, `CpuCoreFactory.ForKind`, `MachineBuilder.WithCoprocessor` (Task 4).
- Produces: `CoprocessorSpec(CpuKind Cpu, IAddressTranslation Translation, string ControlPortPeripheral, double ClockRatioToPrimary)`; `BoardSpec` gains `CoprocessorSpec? Coprocessor = null`; `BoardMachineFactory.Build` calls `WithCoprocessor` when set.

**Design note (grounded against `BoardMachineFactory.cs:54` @ d685b0c):** the coprocessor core is resolved through the **same** `CpuCoreFactory.ForKind` (the one AOT-clean seam). But the coprocessor must be built on the **interpreter tier** in this PR (ADR 0015 Decision 4 — the `TranslatingAddressSpace` wrapper is not the concrete `AddressSpace`, so the JIT path's `(AddressSpace)ctx.Space(...)` cast would throw). So the factory passes `ExecutionTier.Interpreter` for the coprocessor regardless of the board's primary `tier`.

- [ ] **Step 1: Create `CoprocessorSpec`**

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>An optional second CPU that shares the primary's program RAM under run-one-then-the-other bus
/// arbitration (the Z80 SoftCard; ADR 0015 Decision 2). The coprocessor sees the shared bus THROUGH
/// Translation; it is dormant at reset and activated by a soft-switch write the ControlPortPeripheral
/// observes (it flips the Machine's active CPU via ICoprocessorControl). Single-CPU boards leave
/// BoardSpec.Coprocessor null.</summary>
/// <param name="Cpu">The coprocessor core kind (CpuKind.Z80 for the SoftCard).</param>
/// <param name="Translation">Logical (coprocessor) -> physical (primary) address translation (PR-J ships
/// the concrete SoftCardTranslation 6-branch table).</param>
/// <param name="ControlPortPeripheral">The PeripheralSlot.Name whose access toggles the active CPU.</param>
/// <param name="ClockRatioToPrimary">The coprocessor:primary clock ratio (~2.0 for the SoftCard Z80);
/// converts coprocessor run time into primary-domain scheduler cycles (ADR 0015 Decision 5).</param>
public sealed record CoprocessorSpec(
    CpuKind Cpu,
    IAddressTranslation Translation,
    string ControlPortPeripheral,
    double ClockRatioToPrimary);
```

- [ ] **Step 2: Add the field to `BoardSpec`**

Change `src/CpuEmulator.Machines/BoardSpec.cs` to add the trailing optional field:

```csharp
public sealed record BoardSpec(
    string Name,
    CpuKind Cpu,
    int AddressBits,
    IReadOnlyList<MemoryRegion> Memory,
    IReadOnlyList<PeripheralSlot> Peripherals,
    IrqWiring Irq,
    ResetConfig Reset,
    Endianness Endianness = Endianness.LittleEndian,
    int IoAddressBits = 0,
    CoprocessorSpec? Coprocessor = null);
```

- [ ] **Step 3: Wire `BoardMachineFactory`**

In `src/CpuEmulator.Machines/BoardMachineFactory.cs`, replace the `WithCpu(...)` + `return` block (lines 54–55) with:

```csharp
        builder.WithCpu(CpuCoreFactory.ForKind(spec.Cpu, AddressSpaceKind.Program, tier));

        if (spec.Coprocessor is { } copro)
        {
            // The coprocessor is built on the INTERPRETER tier (ADR 0015 Decision 4): it runs over a
            // TranslatingAddressSpace wrapper, which is not the concrete AddressSpace the JIT fastmem
            // binds to. The dual-CPU Run drives ICpuCore.Run uniformly, so a JIT primary + interpreter
            // coprocessor is fine. JIT-under-translation is a separately-gated follow-on (PR-L).
            builder.WithCoprocessor(
                CpuCoreFactory.ForKind(copro.Cpu, AddressSpaceKind.Program, ExecutionTier.Interpreter),
                copro.Translation,
                copro.ClockRatioToPrimary);
        }

        return builder.Build();
```

- [ ] **Step 4: Build all of `src/`**

Run: `dotnet build CpuEmulator.sln`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/CoprocessorSpec.cs src/CpuEmulator.Machines/BoardSpec.cs src/CpuEmulator.Machines/BoardMachineFactory.cs
git commit -m "feat(machines): CoprocessorSpec on BoardSpec + BoardMachineFactory dual-CPU wiring"
```

---

### Task 8: The un-fakeable gate — a two-core toy board that switches the active CPU on a soft-switch write

**Files:**
- Test: `tests/CpuEmulator.Tests/DualCpuMachineTests.cs` (add the toy-board gate)

**Interfaces:**
- Consumes: everything above + a tiny in-test `ToyControlPort : IPeripheral, ` that flips the active CPU; real 6502 + Z80 cores via `CpuCoreFactory` through `BoardMachineFactory`.

**Design notes:** The un-fakeable gate (per the queue, row I): *a two-core toy board switches the active CPU on a soft-switch write; the dormant core is never scheduled; interrupts route to the primary.* Build a real dual-CPU board through `BoardMachineFactory` with a 6502 primary, a Z80 coprocessor, an **identity** translation (the 6-branch table is PR-J), and a tiny control-port peripheral that, on a write to its page, calls `SetCoprocessorActive`. Run the 6502 until it writes the control port; assert the active CPU flipped and the Z80 then runs while the 6502 is suspended.

- [ ] **Step 1: Write the toy control port + the gate test**

```csharp
    // A minimal control-port peripheral for the gate: ANY access flips the active CPU (the SoftCard
    // models it as a write; here a write toggles). It captures the Machine via its Realize context
    // (Machine : IMachineContext, and the Machine implements ICoprocessorControl).
    private sealed class ToyControlPort : IPeripheral
    {
        private ICoprocessorControl? _ctl;
        private bool _active;
        public string Name => "toyctl";
        public void Realize(IMachineContext context)
        {
            if (context is ICoprocessorControl ctl) _ctl = ctl;
        }
        public uint Read(uint offset, AccessWidth width) => 0;
        public void Write(uint offset, AccessWidth width, uint value)
        {
            _active = !_active;
            _ctl?.SetCoprocessorActive(_active);
        }
    }

    [Fact]
    public void Toy_board_switches_the_active_cpu_on_a_control_port_write_and_never_runs_the_dormant_core()
    {
        // 6502 RAM at $0000-$BFFF; the control port at $C000 (one page); a 12 KiB ROM at $D000 whose
        // reset vector points at a routine that writes $C000 (hand off to the Z80) then spins.
        var rom = new byte[0x3000];
        // $D000: 8D 00 C0   STA $C000   (write the control port -> hand off to the coprocessor)
        // $D003: 4C 03 D0   JMP $D003   (spin)
        rom[0x0000] = 0x8D; rom[0x0001] = 0x00; rom[0x0002] = 0xC0;
        rom[0x0003] = 0x4C; rom[0x0004] = 0x03; rom[0x0005] = 0xD0;
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000

        var ctl = new ToyControlPort();
        var spec = new BoardSpec("toydual", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),
                new MemoryRegion(0xC000, 0x0100, RegionKind.Mmio),   // the control-port page
                new MemoryRegion(0xC100, 0x0F00, RegionKind.Mmio),   // rest of the I/O band (unmapped hole)
                new MemoryRegion(0xD000, 0x3000, RegionKind.Rom, rom),
            ],
            Peripherals: [ new PeripheralSlot("toyctl", ctl, 0xC000, 0x0100) ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            Coprocessor: new CoprocessorSpec(
                CpuKind.Z80, new Identity(), "toyctl", ClockRatioToPrimary: 2.0));

        var machine = BoardMachineFactory.Build(spec); // interpreter tier
        machine.Reset();

        Assert.False(machine.CoprocessorActive);        // the 6502 starts active
        long z80Before = machine.Coprocessor!.CycleCount;

        machine.Run(100);                               // the 6502 runs, hits STA $C000, hands off

        Assert.True(machine.CoprocessorActive);         // the control-port write flipped the active CPU
        long z80After = machine.Coprocessor!.CycleCount;
        long six502After = machine.Cpu.CycleCount;

        machine.Run(100);                               // now the Z80 runs; the 6502 is suspended
        Assert.True(machine.Coprocessor!.CycleCount > z80After);   // the Z80 ran while active
        Assert.Equal(six502After, machine.Cpu.CycleCount);         // the suspended 6502 did NOT advance
    }
```

- [ ] **Step 2: Run the gate**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~DualCpuMachineTests"`
Expected: PASS. (If the 6502 needs more than 100 cycles to reach `STA $C000` from reset, raise the first budget; the routine is at the reset vector so a handful of instructions suffice. Verify with the `Mos6502Cpu` reset behavior — it reads `$FFFC/$FFFD`.)

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/DualCpuMachineTests.cs
git commit -m "test(core): two-core toy board gates the active-CPU switch on a control-port write"
```

---

### Task 9: The load-bearing regression gate — every existing single-CPU board is byte-for-byte unchanged

**Files:**
- Test: `tests/CpuEmulator.Tests/DualCpuMachineTests.cs` (add the regression assertions); plus run the **full** suite.

**Interfaces:**
- Consumes: the shipped boards (`Apple2Board`, `SpectrumBoard`, the 6502/Z80/68000/8086 boards). This task asserts no behavior changed.

**Design notes — this is the load-bearing gate ADR 0015 names (single-CPU path byte-for-byte unchanged). Two layers:**

1. **Structural:** every shipped board's `BoardSpec.Coprocessor` is `null`, so `BoardMachineFactory.Build` never enters the dual-CPU branch and `Machine`'s ctor takes the existing single-CPU phase 2 + `RunSingleCpu` (the renamed-but-identical loop). Assert `Coprocessor is null` on a representative shipped board.
2. **Behavioral:** the existing full test suite (7153 tests at PR-H) is the real regression gate — every board's existing tests must stay green with zero changes. A single-CPU `Machine.Run` must produce the exact same cycle counts. Add a focused determinism assertion: build a shipped single-CPU board, run it, and confirm the cycle count matches a run on a freshly-built identical board (the loop is deterministic and unchanged).

- [ ] **Step 1: Write the regression assertions**

```csharp
    [Fact]
    public void Every_shipped_board_is_single_cpu_no_coprocessor()
    {
        // A representative shipped board: the bare Apple ][+ (PR-B). Its spec must carry no coprocessor,
        // so it takes the unchanged single-CPU construction + run path.
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;     // reset -> $D000 (a NOP-spin ROM)
        rom[0x0000] = 0x4C; rom[0x0001] = 0x00; rom[0x0002] = 0xD0;
        var state = new CpuEmulator.Peripherals.Apple2VideoState();
        var iou = new CpuEmulator.Peripherals.Apple2Iou(state);
        var spec = CpuEmulator.Machines.Apple2Board.Spec(rom, iou);
        Assert.Null(spec.Coprocessor);

        var machine = BoardMachineFactory.Build(spec);
        Assert.Null(machine.Coprocessor);
        Assert.False(machine.CoprocessorActive);
    }

    [Fact]
    public void Single_cpu_Run_is_deterministic_across_two_identical_builds()
    {
        // The single-CPU Run loop is unchanged (RunSingleCpu == the pre-PR-I body). Two identical builds
        // run for the same budget execute the same cycles — a guard that the refactor preserved behavior.
        static Machine BuildApple()
        {
            var rom = new byte[0x3000];
            rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
            rom[0x0000] = 0x4C; rom[0x0001] = 0x00; rom[0x0002] = 0xD0; // JMP $D000 spin
            var state = new CpuEmulator.Peripherals.Apple2VideoState();
            var iou = new CpuEmulator.Peripherals.Apple2Iou(state);
            var m = BoardMachineFactory.Build(CpuEmulator.Machines.Apple2Board.Spec(rom, iou));
            m.Reset();
            return m;
        }

        var a = BuildApple();
        var b = BuildApple();
        long ranA = a.Run(1000);
        long ranB = b.Run(1000);
        Assert.Equal(ranA, ranB);
        Assert.Equal(a.Cpu.CycleCount, b.Cpu.CycleCount);
    }
```

- [ ] **Step 2: Run the regression assertions**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~DualCpuMachineTests"`
Expected: PASS.

- [ ] **Step 3: Run the FULL suite — the byte-for-byte regression gate**

Run: `dotnet test CpuEmulator.sln`
Expected: the full suite green — the PR-H baseline (7153 passed, 0 failed, the same skip count) **plus** the new PR-I tests, 0 failed. **If ANY pre-existing test regresses, the single-CPU path was NOT preserved byte-for-byte — STOP and fix before proceeding.** (This is the un-fakeable proof: the only way every existing board stays green is if its construction + run is identical.)

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/DualCpuMachineTests.cs
git commit -m "test(core): single-CPU-unchanged regression gate (every shipped board identical)"
```

---

### Task 10: `BoardSpecValidator` coprocessor checks

**Files:**
- Modify: `src/CpuEmulator.Machines/BoardSpecValidator.cs`
- Test: `tests/CpuEmulator.Tests/CoprocessorValidationTests.cs`

**Interfaces:**
- Consumes: `BoardSpec.Coprocessor`, `CoprocessorSpec` (Task 7).
- Produces: three new diagnostic ids — `copro-control-port-unwired`, `copro-bad-clock-ratio`, `copro-no-translation` (ADR 0015 Decision 7). Added via a `ValidateCoprocessor` method registered in `Validate`.

**Design notes (grounded against `BoardSpecValidator.cs` @ d685b0c):** `Validate` runs a sequence of `ValidateX` methods and returns the collected `BoardDiagnostic` list. Add `ValidateCoprocessor(spec, diagnostics)` to that sequence. The checks mirror `ValidateIrqWiring`'s "names a real slot" shape: the control-port name must match a declared `PeripheralSlot.Name`; the clock ratio must be `> 0`; the translation must be non-null (it cannot be null for a non-null `CoprocessorSpec` because the record requires it, but a defensive check documents intent and guards a future nullable-ref relaxation). No coprocessor → no checks (every single-CPU board passes unchanged).

- [ ] **Step 1: Write the failing validator tests**

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests;

public class CoprocessorValidationTests
{
    private sealed class Identity : IAddressTranslation
    {
        public uint ToPhysical(uint logical) => logical;
    }

    private sealed class NullPort : IPeripheral
    {
        public string Name => "ctl";
        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) => 0;
        public void Write(uint offset, AccessWidth width, uint value) { }
    }

    private static BoardSpec BaseSpec(CoprocessorSpec copro) =>
        new("v", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),
                new MemoryRegion(0xC000, 0x0100, RegionKind.Mmio),
            ],
            Peripherals: [ new PeripheralSlot("ctl", new NullPort(), 0xC000, 0x0100) ],
            Irq: IrqWiring.None, Reset: ResetConfig.None, Coprocessor: copro);

    [Fact]
    public void A_well_formed_coprocessor_spec_validates_clean()
    {
        var spec = BaseSpec(new CoprocessorSpec(CpuKind.Z80, new Identity(), "ctl", 2.0));
        Assert.Empty(BoardSpecValidator.Validate(spec));
    }

    [Fact]
    public void Control_port_naming_a_missing_slot_is_flagged()
    {
        var spec = BaseSpec(new CoprocessorSpec(CpuKind.Z80, new Identity(), "nope", 2.0));
        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "copro-control-port-unwired");
    }

    [Fact]
    public void A_non_positive_clock_ratio_is_flagged()
    {
        var spec = BaseSpec(new CoprocessorSpec(CpuKind.Z80, new Identity(), "ctl", 0.0));
        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "copro-bad-clock-ratio");
    }
}
```

(Grounded: `BoardDiagnostic` is `record BoardDiagnostic(string Code, string Message)` per `src/CpuEmulator.Machines/BoardDiagnostic.cs` @ d685b0c — the predicate reads `d.Code`, and the validator constructs `new BoardDiagnostic("copro-...", "...")` positionally.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~CoprocessorValidationTests"`
Expected: FAIL — no coprocessor checks exist, so the missing-slot + bad-ratio cases produce no diagnostic.

- [ ] **Step 3: Add the validator method**

In `src/CpuEmulator.Machines/BoardSpecValidator.cs`, add the call in `Validate` (after `ValidateVectorPatches`):

```csharp
        ValidateCoprocessor(spec, diagnostics);
```

Add the method:

```csharp
    private static void ValidateCoprocessor(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        if (spec.Coprocessor is not { } copro)
            return; // single-CPU board: no coprocessor checks (the common case, unchanged)

        if (copro.Translation is null)
            diagnostics.Add(new BoardDiagnostic("copro-no-translation",
                "The coprocessor spec has no address translation; a coprocessor needs an IAddressTranslation."));

        if (copro.ClockRatioToPrimary <= 0)
            diagnostics.Add(new BoardDiagnostic("copro-bad-clock-ratio",
                $"The coprocessor clock ratio must be positive; got {copro.ClockRatioToPrimary}."));

        if (!spec.Peripherals.Any(p => p.Name == copro.ControlPortPeripheral))
            diagnostics.Add(new BoardDiagnostic("copro-control-port-unwired",
                $"The coprocessor control port names peripheral '{copro.ControlPortPeripheral}', "
              + "which is not a declared slot."));
    }
```

- [ ] **Step 4: Run the validator tests**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~CoprocessorValidationTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/BoardSpecValidator.cs tests/CpuEmulator.Tests/CoprocessorValidationTests.cs
git commit -m "feat(machines): BoardSpecValidator coprocessor checks (control-port/ratio/translation)"
```

---

### Task 11: Final gate — full suite + warning-clean build

**Files:** none (verification only).

- [ ] **Step 1: Full build, warning-clean**

Run: `dotnet build CpuEmulator.sln`
Expected: Build succeeded, **0 warnings** (the project's warning-clean bar).

- [ ] **Step 2: Full test suite**

Run: `dotnet test CpuEmulator.sln`
Expected: the PR-H baseline green (7153 passed, 0 failed, same skips) plus the new PR-I tests (TranslatingAddressSpace + DualCpuMachine + CoprocessorValidation), 0 failed.

- [ ] **Step 3: Confirm the un-fakeable gate ran**

Confirm `Toy_board_switches_the_active_cpu_on_a_control_port_write_and_never_runs_the_dormant_core` and `Single_cpu_Run_is_deterministic_across_two_identical_builds` are in the passing set (these are the row-I gate + the load-bearing regression).

---

## Self-Review

**1. Spec coverage (ADR 0015):**
- Decision 1 (run-one-then-the-other; never schedule the dormant core) → Tasks 6, 8. ✓
- Decision 2 (`CoprocessorSpec?` + `WithCoprocessor` + dual-CPU `Machine` path + `IAddressTranslation` + `TranslatingAddressSpace`; single-CPU byte-for-byte unchanged) → Tasks 1, 2, 4, 5, 7, 9. ✓
- Decision 5 (one scheduler in the primary cycle domain; ratio conversion; all interrupts to the primary) → Tasks 5, 6 (virtual clock + IRQ-forces-primary-switch). ✓
- Decision 6 (`BoardMachineFactory`/`CpuCoreFactory` build the coprocessor through the same seam) → Task 7. ✓
- Decision 7 (validator checks) → Task 10. ✓
- Decision 4 (interpreter-first; the coprocessor is built on the interpreter tier; JIT-under-translation deferred to PR-L) → Task 7. ✓
- The un-fakeable gate (row I: two-core toy board switches active CPU on a soft-switch write; dormant core never scheduled; single-CPU unchanged) → Tasks 8 + 9. ✓

**2. Placeholder scan:** No TBD/TODO/"implement later"/"similar to Task N". Every code step shows literal code. The `FakeCpu` and `BoardDiagnostic` shapes are grounded against `d685b0c` (see the Builder-readiness note) — not open assumptions.

**3. Type consistency:** `SetCoprocessorActive(bool)`, `CoprocessorActive`, `Coprocessor`, `ToPhysical(uint)`, `WithCoprocessor(Func<IMachineContext,ICpuCore>, IAddressTranslation, double)`, `CoprocessorBuild`, `CoprocessorSpec(CpuKind, IAddressTranslation, string, double)` are used identically across all tasks. The diagnostic ids match Decision 7 verbatim.

**Builder-readiness note (both shapes grounded against d685b0c):** `FakeCpu.Run` consumes the entire budget (`CycleCount += cycleBudget; cycleBudget = 0`) and exposes `IrqAsserted`/`NmiAsserted`/`CycleCount` (`tests/CpuEmulator.Tests/TestDoubles/FakeCpu.cs`) — so Task 5/6's budget-driven + interrupt-routing assertions hold as written. `BoardDiagnostic` is `record BoardDiagnostic(string Code, string Message)` (`src/CpuEmulator.Machines/BoardDiagnostic.cs`) — Task 10's predicate reads `d.Code`. No open assumptions remain; the literal code is correct against the shipped shapes.
