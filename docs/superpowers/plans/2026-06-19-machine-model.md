# The Machine Model — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a declarative `BoardSpec` (memory map + peripherals + IRQ wiring + reset) that validates and instantiates into the existing runnable `Machine`, re-express the `Breadboard6502` as a `BoardSpec` byte-for-byte/cycle-for-cycle identically, and prove the model generalizes with a Z80 `ReferenceSbc` that boots and runs on both tiers.

**Architecture:** A new composition-root assembly `CpuEmulator.Machines` holds the declarative layer. It is the *only* assembly that may simultaneously name the concrete CPU cores (`Mos6502Cpu`, `Z80Cpu`) and the JIT (`JittedCpu<TCpu>`), so the `CpuKind`→core factory, the `BoardSpec` types, the `BoardSpecValidator`, the `BoardMachineFactory.Build(BoardSpec)` factory, the `ReferenceSbc(CpuKind)` recipe, and the re-expressed `Breadboard6502` board-spec all live here. The declarative layer *compiles down to* the existing fluent `Core.MachineBuilder` — it does not replace the run loop, the scheduler, the interrupt plumbing, or the `IPeripheral`/`SimpleUart`/`IntervalTimer` devices (which are already CPU-agnostic and board-attachable). `Core` stays AOT-clean and references nothing new.

**Tech Stack:** C# / .NET 10, xUnit (`tests/CpuEmulator.Tests`), records with `IsExternalInit` (C# `record`/`init`), the existing `CpuEmulator.Core` device layer (`Machine`, `MachineBuilder`, `AddressSpace`, `IPeripheral`, `IInterruptLine`, `IScheduler`), the `CpuEmulator.Jit` tier-1 IL-JIT (`JittedCpu<TCpu>` + generated `XxxCpu.JitTarget`), and the four CPU core assemblies.

---

## Context the implementer needs (grounded recon)

This plan was written after reading the actual code. The facts below are load-bearing; do not re-derive them.

### What already exists (do NOT rebuild)

- **`CpuEmulator.Core.Machine`** (`src/CpuEmulator.Core/Machine.cs`) — the runnable container. Built via the fluent **`MachineBuilder`** (`src/CpuEmulator.Core/MachineBuilder.cs`): `Machine.Create(name).WithAddressSpace(kind, bits).WithRam(kind, start, len).WithRom(kind, start, image).WithPeripheral(kind, start, len, dev).WithCpu(ctx => core).Build()`. The CPU factory is `Func<IMachineContext, ICpuCore>`; `IMachineContext.Space(kind)` returns the mapped `IAddressSpace` (memory already mapped when the factory runs — see `MachineBuilderTests.Cpu_factory_receives_context_with_memory_already_mapped`).
- **`IPeripheral`** (`src/CpuEmulator.Core/IPeripheral.cs`) — already board-attachable + CPU-agnostic: `Read(offset, width)`, `Write(offset, width, value)`, `Realize(IMachineContext)`, optional `TryPeek`. Offsets are relative to the mapping base.
- **`SimpleUart`** (`src/CpuEmulator.Peripherals/SimpleUart.cs`) and **`IntervalTimer`** (`src/CpuEmulator.Peripherals/IntervalTimer.cs`) — already reusable: no hard-coded 6502 addresses, no hard-coded board wiring. `SimpleUart.Realize` claims `context.IrqLine.Source()`; `IntervalTimer.Realize` claims `context.Scheduler` + `context.IrqLine.Source()`. **The spec's "peripheral generalization + refactor" is already satisfied by the existing fluent builder.** This plan therefore does NOT modify these devices; it adds tests that lock the no-regression property and slots them by address from a `BoardSpec`.
- **`AddressSpace`** (`src/CpuEmulator.Core/AddressSpace.cs`) — 256-byte pages (`PageSize = 256`); `addressBits` must be 8–24; `MapMemory(start, backing, writable)` requires page-aligned `start` and a backing length that is a positive multiple of 256; `MapPeripheral(start, length, dev)` same alignment. Open-bus reads return `AddressSpaceOptions.OpenBusValue` (default `0xFF`); ROM writes + unmapped writes are ignored (non-strict default).
- **The JIT** (`src/CpuEmulator.Jit/JittedCpu.cs`) — `new JittedCpu<TCpu>(inner, target, concreteAddressSpace, ioBus?, options?)`. Takes the **concrete `AddressSpace`** (not `IAddressSpace`) + the generated per-CPU `target`. Wraps an interpreter; `Step` delegates to the interpreter, `Run` runs compiled blocks. Implements `ICpuCore` + `IMonitorSupport`, so a `Machine` drives it identically.
- **The four cores** and their construction:
  - `Mos6502Cpu(IAddressSpace bus, ...)` — `Reset()` loads PC from `$FFFC/$FFFD`. JIT: `new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space)`.
  - `Z80Cpu(IAddressSpace bus, IAddressSpace? io = null)` — `Reset()` sets PC=0, SP=0xFFFF. Has an `IoBus` accessor. JIT: `new JittedCpu<Z80Cpu>(cpu, Z80Cpu.JitTarget, mem, io)`.
  - `M68000Cpu(IAddressSpace bus)` — `Reset()` is a **no-op stub** (`{ }`); does not load SP/PC. JIT: `new JittedCpu<M68000Cpu>(cpu, M68000Cpu.JitTarget, mem)`. (Out of scope here; the stub confirms why a 68000 board cannot boot yet.)
  - `M8086Cpu(IAddressSpace bus)` — `Reset()` is a **no-op stub** (`{ }`). (Out of scope here.)
- **The `Breadboard6502`** (`src/CpuEmulator.Host/Breadboard6502.cs`) — today's hand-wired board. v2 map: RAM `$0000`–`$CFFF` (52 KiB via `WithRam(Program, 0x0000, 0xD000)`), UART `$D000` (1 page, `0x0100`), IntervalTimer `$D100` (1 page), `$D200`–`$DFFF` open-bus, ROM `$E000`–`$FFFF` (8 KiB from `DemoRom.Build()`). CPU via `new Mos6502Cpu(ctx.Space(Program))`.
- **The zero-behavior-change oracle tests** — `tests/CpuEmulator.Tests/Host/HostUatTests.cs` (`Demo_rom_hello_arrives_on_the_uart_exactly` asserts `tx == DemoRom.Message`; `Echo_session_transmits_injected_input_back_exactly`) and `tests/CpuEmulator.Tests/Host/Breadboard6502Tests.cs` (RAM/ROM/UART/timer/open-bus map points). These are the un-fakeable behavior anchors.

### The two open questions, resolved

1. **`CpuKind`→core factory location.** `Core.MachineBuilder.WithCpu` takes `Func<IMachineContext, ICpuCore>` and `Core` references nothing. The JIT (`JittedCpu<TCpu>`) lives in `Jit`, takes the concrete `AddressSpace`, and the concrete cores live in their own assemblies. A factory that builds a JIT-wrapped Z80 therefore **cannot** live in `Core` (it would force `Core` to reference `Jit` + every CPU, breaking the AOT layering rule the csproj comments guard). **Decision:** create a new composition-root assembly **`CpuEmulator.Machines`** that references Core + Peripherals + Jit + all four CPU assemblies. The `CpuKind` enum, the core factory, `BoardSpec`, the validator, `BoardMachineFactory.Build`, `ReferenceSbc`, and the re-expressed `Breadboard6502` board-spec all live there. The factory returns a `Func<IMachineContext, ICpuCore>` for `MachineBuilder.WithCpu`, building either the bare interpreter or the JIT wrapper per a `Tier` flag.
2. **Per-CPU `ResetConfig`.** Reset mechanics already live in each core's `Reset()` (6502 reads `$FFFC`; Z80 sets PC=0). So `ResetConfig` carries only **board-level** reset inputs: the ROM image source (already a `MemoryRegion` with `Image`) and an optional list of `(address, byte)` reset-vector patches to write into a ROM image before mapping (for boards whose ROM image does not already carry its vectors). For Board #1 the `DemoRom` already embeds its `$FFFC` vector, so no patches are needed; for the Z80 board PC=0 needs no vector at all. `ResetConfig` therefore stays a thin, CPU-agnostic record; the per-CPU reset detail is the core's job, surfaced uniformly through `Machine.Reset() -> Cpu.Reset()`. **Tier** (interpreter vs JIT) is a parameter to `BoardMachineFactory.Build`, not part of `BoardSpec` (the same board runs on either tier).

---

## File Structure

### New assembly: `src/CpuEmulator.Machines/` (composition root)

| File | Responsibility |
|------|----------------|
| `CpuEmulator.Machines.csproj` | New library. References Core, Peripherals, Jit, and all four `Cpus.*` projects. Not AOT (it transitively references Jit). |
| `CpuKind.cs` | `enum CpuKind { Mos6502, Z80, M68000, I8086 }` — which core a board targets. |
| `ExecutionTier.cs` | `enum ExecutionTier { Interpreter, Jit }` — build-time tier selection for `BoardMachineFactory`. |
| `RegionKind.cs` | `enum RegionKind { Ram, Rom, Mmio }`. |
| `MemoryRegion.cs` | `record MemoryRegion(uint Start, uint Length, RegionKind Kind, byte[]? Image = null)`. |
| `PeripheralSlot.cs` | `record PeripheralSlot(string Name, IPeripheral Device, uint Base, uint Length)`. |
| `IrqWiring.cs` | `enum CpuInterrupt { Irq, Nmi }` + `record IrqWiring(IReadOnlyList<PeripheralIrq> Lines)` + `record PeripheralIrq(string PeripheralName, CpuInterrupt Target)`. |
| `ResetConfig.cs` | `record ResetConfig(IReadOnlyList<VectorPatch> VectorPatches)` + `record VectorPatch(uint Address, byte Value)` + a static `ResetConfig.None`. |
| `BoardSpec.cs` | `record BoardSpec(string Name, CpuKind Cpu, int AddressBits, IReadOnlyList<MemoryRegion> Memory, IReadOnlyList<PeripheralSlot> Peripherals, IrqWiring Irq, ResetConfig Reset)`. |
| `BoardDiagnostic.cs` | `record BoardDiagnostic(string Code, string Message)` — a validation finding (not an exception). |
| `BoardSpecValidator.cs` | `static IReadOnlyList<BoardDiagnostic> Validate(BoardSpec)` — overlap, address-width fit, page alignment, MMIO-slot-in-Mmio-region, IRQ-wired-to-real-peripheral, ROM-image-fits, vector-patch-in-mapped-memory. |
| `CpuCoreFactory.cs` | `static Func<IMachineContext, ICpuCore> ForKind(CpuKind, AddressSpaceKind, ExecutionTier)` — the `CpuKind`→core factory (interpreter or JIT-wrapped), the resolved open question #1. |
| `BoardMachineFactory.cs` | `static Machine Build(BoardSpec, ExecutionTier = Interpreter)` — validates (throws `BoardValidationException` on any diagnostic), patches ROM images, compiles the spec into `MachineBuilder` calls, returns the `Machine`. |
| `BoardValidationException.cs` | Thrown by `BoardMachineFactory.Build` when validation produces diagnostics; carries the `IReadOnlyList<BoardDiagnostic>`. |
| `Breadboard6502Board.cs` | `static BoardSpec Spec(byte[] rom)` — the `Breadboard6502` re-expressed as a `BoardSpec` (the zero-behavior-change board). |
| `ReferenceSbc.cs` | `static BoardSpec Build(CpuKind, SimpleUart, IntervalTimer, byte[] rom)` — the uniform per-CPU recipe (RAM low, ROM high, UART+timer at fixed MMIO, IRQ→maskable). Used by the Z80 board; ready for 68000/8086 in piece #2. |

### Test files: `tests/CpuEmulator.Tests/Machines/`

| File | Responsibility |
|------|----------------|
| `BoardSpecValidatorTests.cs` | One test per diagnostic (overlap, width, alignment, MMIO-not-in-Mmio, unwired IRQ, ROM-too-big, vector-unmapped) + a clean spec produces no diagnostics. |
| `BoardMachineFactoryTests.cs` | Build maps RAM/ROM/MMIO; CPU factory honored; invalid spec throws `BoardValidationException`; Jit tier produces a `JittedCpu`-backed machine. |
| `Breadboard6502BoardTests.cs` | **The zero-behavior-change gate**: the board-spec machine reproduces every `Breadboard6502Tests` map point AND the exact `HostUatTests` UART stream AND identical cycle counts vs the hand-wired board. |
| `ReferenceSbcZ80Tests.cs` | The Z80 reference board boots its ROM and runs a tiny program to a known UART byte sequence, on **both** tiers (interpreter + JIT). |

### Modified files

| File | Change |
|------|--------|
| `CpuEmulator.sln` | Add the `CpuEmulator.Machines` project. |
| `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` | Add a `ProjectReference` to `CpuEmulator.Machines`. |

No production file outside the new assembly is modified. `SimpleUart`, `IntervalTimer`, `IPeripheral`, `Machine`, `MachineBuilder`, and the cores are untouched.

---

## Task 1: Scaffold the `CpuEmulator.Machines` assembly + enums

**Files:**
- Create: `src/CpuEmulator.Machines/CpuEmulator.Machines.csproj`
- Create: `src/CpuEmulator.Machines/CpuKind.cs`
- Create: `src/CpuEmulator.Machines/ExecutionTier.cs`
- Create: `src/CpuEmulator.Machines/RegionKind.cs`
- Modify: `CpuEmulator.sln`
- Modify: `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`

- [ ] **Step 1: Create the project file**

`src/CpuEmulator.Machines/CpuEmulator.Machines.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    The composition-root assembly for declarative board specs. This is the ONLY library that
    references both the concrete CPU cores AND the Jit (Reflection.Emit) assembly, so it is the
    one place a CpuKind -> core factory can build a JIT-wrapped core. Deliberately NOT
    IsAotCompatible: it transitively references CpuEmulator.Jit (research section 4). NativeAOT
    consumers compose machines via the interpreter tier and do not reference this assembly's JIT path.
  -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\CpuEmulator.Core\CpuEmulator.Core.csproj" />
    <ProjectReference Include="..\CpuEmulator.Peripherals\CpuEmulator.Peripherals.csproj" />
    <ProjectReference Include="..\CpuEmulator.Jit\CpuEmulator.Jit.csproj" />
    <ProjectReference Include="..\CpuEmulator.Cpus.Mos6502\CpuEmulator.Cpus.Mos6502.csproj" />
    <ProjectReference Include="..\CpuEmulator.Cpus.Z80\CpuEmulator.Cpus.Z80.csproj" />
    <ProjectReference Include="..\CpuEmulator.Cpus.M68000\CpuEmulator.Cpus.M68000.csproj" />
    <ProjectReference Include="..\CpuEmulator.Cpus.M8086\CpuEmulator.Cpus.M8086.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the enums**

`src/CpuEmulator.Machines/CpuKind.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Which CPU core a board targets. The CpuCoreFactory maps each to its concrete core
/// (and, on the Jit tier, the JittedCpu&lt;TCpu&gt; wrapper + generated JitTarget).</summary>
public enum CpuKind
{
    Mos6502,
    Z80,
    M68000,
    I8086,
}
```

`src/CpuEmulator.Machines/ExecutionTier.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Build-time tier selection for BoardMachineFactory. The SAME BoardSpec runs on either
/// tier; the tier is a factory parameter, not part of the board's declarative description.</summary>
public enum ExecutionTier
{
    /// <summary>Tier-0: the bare interpreter core. AOT-clean.</summary>
    Interpreter,

    /// <summary>Tier-1: the interpreter wrapped in JittedCpu&lt;TCpu&gt; (Reflection.Emit; non-AOT).</summary>
    Jit,
}
```

`src/CpuEmulator.Machines/RegionKind.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>How a MemoryRegion is mapped onto the bus.</summary>
public enum RegionKind
{
    /// <summary>Writable backing memory.</summary>
    Ram,

    /// <summary>Read-only backing memory (carries the Image bytes).</summary>
    Rom,

    /// <summary>A device window (peripheral slots must land in/over an Mmio region).</summary>
    Mmio,
}
```

- [ ] **Step 3: Add the project to the solution**

Run: `dotnet sln CpuEmulator.sln add src/CpuEmulator.Machines/CpuEmulator.Machines.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 4: Add the test-project reference**

In `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`, inside the existing `<ItemGroup>` that holds `<ProjectReference>` elements, add:

```xml
    <ProjectReference Include="..\..\src\CpuEmulator.Machines\CpuEmulator.Machines.csproj" />
```

- [ ] **Step 5: Build to verify the assembly compiles and is referenced**

Run: `dotnet build src/CpuEmulator.Machines/CpuEmulator.Machines.csproj -c Debug`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Machines CpuEmulator.sln tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj
git commit -m "feat(machines): scaffold CpuEmulator.Machines composition-root assembly + enums"
```

---

## Task 2: The `BoardSpec` record + its components

**Files:**
- Create: `src/CpuEmulator.Machines/MemoryRegion.cs`
- Create: `src/CpuEmulator.Machines/PeripheralSlot.cs`
- Create: `src/CpuEmulator.Machines/IrqWiring.cs`
- Create: `src/CpuEmulator.Machines/ResetConfig.cs`
- Create: `src/CpuEmulator.Machines/BoardSpec.cs`
- Test: `tests/CpuEmulator.Tests/Machines/BoardSpecShapeTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/CpuEmulator.Tests/Machines/BoardSpecShapeTests.cs`:

```csharp
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class BoardSpecShapeTests
{
    [Fact]
    public void BoardSpec_composes_its_parts()
    {
        var uart = new SimpleUart();
        var spec = new BoardSpec(
            Name: "demo",
            Cpu: CpuKind.Mos6502,
            AddressBits: 16,
            Memory: [new MemoryRegion(0x0000, 0x1000, RegionKind.Ram)],
            Peripherals: [new PeripheralSlot("uart", uart, 0xD000, 0x0100)],
            Irq: new IrqWiring([new PeripheralIrq("uart", CpuInterrupt.Irq)]),
            Reset: ResetConfig.None);

        Assert.Equal("demo", spec.Name);
        Assert.Equal(CpuKind.Mos6502, spec.Cpu);
        Assert.Equal(RegionKind.Ram, spec.Memory[0].Kind);
        Assert.Same(uart, spec.Peripherals[0].Device);
        Assert.Equal(CpuInterrupt.Irq, spec.Irq.Lines[0].Target);
        Assert.Empty(spec.Reset.VectorPatches);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BoardSpecShapeTests"`
Expected: FAIL — compile error (`MemoryRegion`, `BoardSpec`, etc. not defined).

- [ ] **Step 3: Create the component records**

`src/CpuEmulator.Machines/MemoryRegion.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>One contiguous span of the address space. Start must be 256-byte page-aligned and
/// Length a positive multiple of 256 (the AddressSpace page granularity). For Rom, Image carries
/// the bytes (its length must equal Length). For Ram and Mmio, Image is null.</summary>
public sealed record MemoryRegion(uint Start, uint Length, RegionKind Kind, byte[]? Image = null);
```

`src/CpuEmulator.Machines/PeripheralSlot.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>A device attached at [Base, Base+Length) of the program space. Base must be
/// page-aligned and Length a positive multiple of 256; the slot must land in/over an Mmio region.
/// Name is the wiring key the IrqWiring references.</summary>
public sealed record PeripheralSlot(string Name, IPeripheral Device, uint Base, uint Length);
```

`src/CpuEmulator.Machines/IrqWiring.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Which CPU interrupt input a peripheral's outgoing line drives.</summary>
public enum CpuInterrupt
{
    /// <summary>The maskable interrupt (6502 IRQ, Z80 INT). Machine.IrqLine.</summary>
    Irq,

    /// <summary>The non-maskable interrupt. Machine.NmiLine.</summary>
    Nmi,
}

/// <summary>One device-line -> CPU-interrupt mapping. PeripheralName must match a PeripheralSlot.Name.</summary>
public sealed record PeripheralIrq(string PeripheralName, CpuInterrupt Target);

/// <summary>Which device IRQ lines drive which CPU interrupts. Devices already claim their wired-OR
/// handle via context.IrqLine.Source()/NmiLine.Source() in Realize; this wiring declares, for the
/// validator, that each named line maps to a real peripheral and a real CPU input.</summary>
public sealed record IrqWiring(IReadOnlyList<PeripheralIrq> Lines)
{
    /// <summary>An empty wiring (no peripheral drives an interrupt).</summary>
    public static IrqWiring None { get; } = new([]);
}
```

`src/CpuEmulator.Machines/ResetConfig.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>A single byte written into a ROM image (before mapping) to seed a reset/interrupt
/// vector. Address is an absolute bus address; it must land inside a mapped region.</summary>
public sealed record VectorPatch(uint Address, byte Value);

/// <summary>Board-level reset inputs. The per-CPU reset MECHANICS live in the core's Reset()
/// (the 6502 reads $FFFC/$FFFD; the Z80 sets PC=0), so this record carries only the optional
/// vector bytes a board pokes into its ROM image when the image does not already embed them.
/// Most boards (whose ROM image carries its own vectors, e.g. the breadboard demo ROM) use None.</summary>
public sealed record ResetConfig(IReadOnlyList<VectorPatch> VectorPatches)
{
    /// <summary>No board-level vector patches (the ROM image carries its own vectors, or the CPU
    /// resets to a fixed PC needing no vector — e.g. the Z80's PC=0).</summary>
    public static ResetConfig None { get; } = new([]);
}
```

- [ ] **Step 4: Create the `BoardSpec` record**

`src/CpuEmulator.Machines/BoardSpec.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>A declarative description of an emulated computer: which CPU, the address width, the
/// memory map (RAM/ROM/MMIO), the peripheral slots, the IRQ wiring, and the reset inputs. Data, not
/// code (mirroring the CPU-spec philosophy): BoardSpecValidator checks it; BoardMachineFactory
/// instantiates it into the existing runnable Machine. AddressBits is the CPU's bus width (16 for
/// the 6502/Z80; 8-24 per AddressSpace).</summary>
public sealed record BoardSpec(
    string Name,
    CpuKind Cpu,
    int AddressBits,
    IReadOnlyList<MemoryRegion> Memory,
    IReadOnlyList<PeripheralSlot> Peripherals,
    IrqWiring Irq,
    ResetConfig Reset);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BoardSpecShapeTests"`
Expected: PASS (1 test).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Machines tests/CpuEmulator.Tests/Machines/BoardSpecShapeTests.cs
git commit -m "feat(machines): BoardSpec record + MemoryRegion/PeripheralSlot/IrqWiring/ResetConfig"
```

---

## Task 3: `BoardDiagnostic` + `BoardSpecValidator` (overlap + address-width + alignment)

**Files:**
- Create: `src/CpuEmulator.Machines/BoardDiagnostic.cs`
- Create: `src/CpuEmulator.Machines/BoardSpecValidator.cs`
- Test: `tests/CpuEmulator.Tests/Machines/BoardSpecValidatorTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/CpuEmulator.Tests/Machines/BoardSpecValidatorTests.cs`:

```csharp
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class BoardSpecValidatorTests
{
    private static BoardSpec Valid(
        IReadOnlyList<MemoryRegion>? memory = null,
        IReadOnlyList<PeripheralSlot>? peripherals = null,
        IrqWiring? irq = null,
        ResetConfig? reset = null,
        int addressBits = 16) =>
        new("test", CpuKind.Mos6502, addressBits,
            memory ?? [new MemoryRegion(0x0000, 0x1000, RegionKind.Ram)],
            peripherals ?? [],
            irq ?? IrqWiring.None,
            reset ?? ResetConfig.None);

    [Fact]
    public void Clean_spec_has_no_diagnostics()
    {
        Assert.Empty(BoardSpecValidator.Validate(Valid()));
    }

    [Fact]
    public void Overlapping_regions_are_flagged()
    {
        var spec = Valid(memory:
        [
            new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
            new MemoryRegion(0x0800, 0x1000, RegionKind.Ram),
        ]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "region-overlap");
    }

    [Fact]
    public void Region_past_address_width_is_flagged()
    {
        // addressBits 16 => top is 0xFFFF; a region ending at 0x1_0000 exceeds it.
        var spec = Valid(memory: [new MemoryRegion(0xF000, 0x2000, RegionKind.Ram)]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "region-out-of-range");
    }

    [Fact]
    public void Misaligned_region_start_is_flagged()
    {
        var spec = Valid(memory: [new MemoryRegion(0x0080, 0x0100, RegionKind.Ram)]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "region-misaligned");
    }

    [Fact]
    public void Region_length_not_a_page_multiple_is_flagged()
    {
        var spec = Valid(memory: [new MemoryRegion(0x0000, 0x0080, RegionKind.Ram)]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "region-misaligned");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BoardSpecValidatorTests"`
Expected: FAIL — compile error (`BoardSpecValidator` / `BoardDiagnostic` not defined).

- [ ] **Step 3: Create the diagnostic record**

`src/CpuEmulator.Machines/BoardDiagnostic.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>One board-validation finding. A diagnostic, not an exception: validation collects all
/// findings so a board author sees every problem at once (BoardMachineFactory turns a non-empty
/// list into a BoardValidationException at instantiation time).</summary>
public sealed record BoardDiagnostic(string Code, string Message);
```

- [ ] **Step 4: Create the validator (regions only, this task)**

`src/CpuEmulator.Machines/BoardSpecValidator.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Load-time validation of a BoardSpec (spec section 3). Returns every finding; an empty
/// list means the spec is well-formed. Checks: region overlap, address-width fit, 256-byte page
/// alignment (start + length), MMIO-slot-in-Mmio-region, IRQ-wired-to-a-real-peripheral, ROM-image
/// size, and vector-patch-in-mapped-memory. Page size and width rules mirror AddressSpace.</summary>
public static class BoardSpecValidator
{
    private const uint PageSize = 256; // AddressSpace.PageSize

    public static IReadOnlyList<BoardDiagnostic> Validate(BoardSpec spec)
    {
        var diagnostics = new List<BoardDiagnostic>();
        ValidateRegions(spec, diagnostics);
        return diagnostics;
    }

    private static void ValidateRegions(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        // Top of the bus for this address width (e.g. 0xFFFF for 16 bits).
        ulong addressCeiling = 1UL << spec.AddressBits;

        for (int i = 0; i < spec.Memory.Count; i++)
        {
            MemoryRegion r = spec.Memory[i];

            if (r.Length == 0 || r.Start % PageSize != 0 || r.Length % PageSize != 0)
                diagnostics.Add(new BoardDiagnostic("region-misaligned",
                    $"Region at ${r.Start:X} (length ${r.Length:X}) must be page-aligned: "
                  + $"start a multiple of {PageSize} and length a positive multiple of {PageSize}."));

            if ((ulong)r.Start + r.Length > addressCeiling)
                diagnostics.Add(new BoardDiagnostic("region-out-of-range",
                    $"Region [${r.Start:X}, ${(ulong)r.Start + r.Length:X}) exceeds the "
                  + $"{spec.AddressBits}-bit address space (ceiling ${addressCeiling:X})."));

            for (int j = i + 1; j < spec.Memory.Count; j++)
            {
                MemoryRegion other = spec.Memory[j];
                if (r.Start < other.Start + other.Length && other.Start < r.Start + r.Length)
                    diagnostics.Add(new BoardDiagnostic("region-overlap",
                        $"Region [${r.Start:X}, ${r.Start + r.Length:X}) overlaps "
                      + $"[${other.Start:X}, ${other.Start + other.Length:X})."));
            }
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BoardSpecValidatorTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Machines tests/CpuEmulator.Tests/Machines/BoardSpecValidatorTests.cs
git commit -m "feat(machines): BoardSpecValidator region checks (overlap, width, alignment)"
```

---

## Task 4: Validator — MMIO slot placement + IRQ wiring + ROM size + vector patch

**Files:**
- Modify: `src/CpuEmulator.Machines/BoardSpecValidator.cs`
- Test: `tests/CpuEmulator.Tests/Machines/BoardSpecValidatorTests.cs` (add tests)

- [ ] **Step 1: Add the failing tests**

Append these methods inside the existing `BoardSpecValidatorTests` class in `tests/CpuEmulator.Tests/Machines/BoardSpecValidatorTests.cs`:

```csharp
    [Fact]
    public void Peripheral_slot_outside_an_mmio_region_is_flagged()
    {
        // RAM at 0x0000-0x0FFF; the slot at 0xD000 lands in no Mmio region.
        var spec = Valid(
            memory: [new MemoryRegion(0x0000, 0x1000, RegionKind.Ram)],
            peripherals: [new PeripheralSlot("uart", new SimpleUart(), 0xD000, 0x0100)]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "slot-not-in-mmio");
    }

    [Fact]
    public void Peripheral_slot_inside_an_mmio_region_is_clean()
    {
        var spec = Valid(
            memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
                new MemoryRegion(0xD000, 0x1000, RegionKind.Mmio),
            ],
            peripherals: [new PeripheralSlot("uart", new SimpleUart(), 0xD000, 0x0100)]);

        Assert.DoesNotContain(BoardSpecValidator.Validate(spec), d => d.Code == "slot-not-in-mmio");
    }

    [Fact]
    public void Irq_line_naming_an_unknown_peripheral_is_flagged()
    {
        var spec = Valid(
            memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
                new MemoryRegion(0xD000, 0x1000, RegionKind.Mmio),
            ],
            peripherals: [new PeripheralSlot("uart", new SimpleUart(), 0xD000, 0x0100)],
            irq: new IrqWiring([new PeripheralIrq("nonexistent", CpuInterrupt.Irq)]));

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "irq-unwired");
    }

    [Fact]
    public void Rom_image_size_mismatch_is_flagged()
    {
        // Rom region declares length 0x1000 but the image is only 0x0800 bytes.
        var spec = Valid(memory:
        [
            new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
            new MemoryRegion(0xF000, 0x1000, RegionKind.Rom, new byte[0x0800]),
        ]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "rom-image-mismatch");
    }

    [Fact]
    public void Rom_region_without_an_image_is_flagged()
    {
        var spec = Valid(memory:
        [
            new MemoryRegion(0x0000, 0x1000, RegionKind.Ram),
            new MemoryRegion(0xF000, 0x1000, RegionKind.Rom),
        ]);

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "rom-image-mismatch");
    }

    [Fact]
    public void Vector_patch_outside_mapped_memory_is_flagged()
    {
        var spec = Valid(
            memory: [new MemoryRegion(0x0000, 0x1000, RegionKind.Ram)],
            reset: new ResetConfig([new VectorPatch(0xFFFC, 0x00)]));

        Assert.Contains(BoardSpecValidator.Validate(spec), d => d.Code == "vector-unmapped");
    }
```

Add `using CpuEmulator.Core;` is **not** needed (the test already references only `Machines`/`Peripherals` symbols). The class already has `using CpuEmulator.Peripherals;`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BoardSpecValidatorTests"`
Expected: FAIL — the new `slot-not-in-mmio` / `irq-unwired` / `rom-image-mismatch` / `vector-unmapped` diagnostics are never produced (assertions fail).

- [ ] **Step 3: Extend the validator**

In `src/CpuEmulator.Machines/BoardSpecValidator.cs`, change `Validate` to call the new checks and add the methods. Replace the `Validate` method body and add the four private methods:

```csharp
    public static IReadOnlyList<BoardDiagnostic> Validate(BoardSpec spec)
    {
        var diagnostics = new List<BoardDiagnostic>();
        ValidateRegions(spec, diagnostics);
        ValidateRomImages(spec, diagnostics);
        ValidatePeripheralSlots(spec, diagnostics);
        ValidateIrqWiring(spec, diagnostics);
        ValidateVectorPatches(spec, diagnostics);
        return diagnostics;
    }

    private static void ValidateRomImages(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        foreach (MemoryRegion r in spec.Memory)
        {
            if (r.Kind != RegionKind.Rom)
                continue;
            if (r.Image is null || r.Image.Length != r.Length)
                diagnostics.Add(new BoardDiagnostic("rom-image-mismatch",
                    $"Rom region at ${r.Start:X} (length ${r.Length:X}) needs an image of exactly "
                  + $"${r.Length:X} bytes; got {(r.Image is null ? "none" : $"${r.Image.Length:X}")}."));
        }
    }

    private static void ValidatePeripheralSlots(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        foreach (PeripheralSlot slot in spec.Peripherals)
        {
            if (slot.Base % PageSize != 0 || slot.Length == 0 || slot.Length % PageSize != 0)
                diagnostics.Add(new BoardDiagnostic("slot-misaligned",
                    $"Peripheral '{slot.Name}' slot at ${slot.Base:X} (length ${slot.Length:X}) "
                  + $"must be page-aligned: start a multiple of {PageSize}, length a positive multiple."));

            bool inMmio = spec.Memory.Any(r =>
                r.Kind == RegionKind.Mmio &&
                slot.Base >= r.Start &&
                (ulong)slot.Base + slot.Length <= (ulong)r.Start + r.Length);
            if (!inMmio)
                diagnostics.Add(new BoardDiagnostic("slot-not-in-mmio",
                    $"Peripheral '{slot.Name}' slot [${slot.Base:X}, ${(ulong)slot.Base + slot.Length:X}) "
                  + "is not fully contained in any Mmio region."));
        }
    }

    private static void ValidateIrqWiring(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        foreach (PeripheralIrq line in spec.Irq.Lines)
        {
            if (!spec.Peripherals.Any(p => p.Name == line.PeripheralName))
                diagnostics.Add(new BoardDiagnostic("irq-unwired",
                    $"IRQ wiring names peripheral '{line.PeripheralName}', which is not a declared slot."));
        }
    }

    private static void ValidateVectorPatches(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        foreach (VectorPatch patch in spec.Reset.VectorPatches)
        {
            bool mapped = spec.Memory.Any(r =>
                patch.Address >= r.Start && patch.Address < (ulong)r.Start + r.Length);
            if (!mapped)
                diagnostics.Add(new BoardDiagnostic("vector-unmapped",
                    $"Reset vector patch at ${patch.Address:X} lands in no declared region."));
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BoardSpecValidatorTests"`
Expected: PASS (12 tests total).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/BoardSpecValidator.cs tests/CpuEmulator.Tests/Machines/BoardSpecValidatorTests.cs
git commit -m "feat(machines): validator MMIO-slot/IRQ/ROM-size/vector-patch checks"
```

---

## Task 5: The `CpuKind` → core factory (interpreter tier)

**Files:**
- Create: `src/CpuEmulator.Machines/CpuCoreFactory.cs`
- Test: `tests/CpuEmulator.Tests/Machines/CpuCoreFactoryTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/CpuEmulator.Tests/Machines/CpuCoreFactoryTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Machines;

public class CpuCoreFactoryTests
{
    private static Machine MachineFor(CpuKind kind, ExecutionTier tier) =>
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x1000)
            .WithCpu(CpuCoreFactory.ForKind(kind, AddressSpaceKind.Program, tier))
            .Build();

    [Fact]
    public void Interpreter_tier_6502_builds_a_bare_core()
    {
        var machine = MachineFor(CpuKind.Mos6502, ExecutionTier.Interpreter);
        Assert.IsType<Mos6502Cpu>(machine.Cpu);
    }

    [Fact]
    public void Interpreter_tier_z80_builds_a_bare_core()
    {
        var machine = MachineFor(CpuKind.Z80, ExecutionTier.Interpreter);
        Assert.IsType<Z80Cpu>(machine.Cpu);
    }

    [Fact]
    public void Unsupported_kind_on_a_runnable_tier_throws()
    {
        // The 68000/8086 cores have no-op Reset stubs and cannot boot a board yet (piece #2).
        Assert.Throws<MachineConfigurationException>(() =>
            MachineFor(CpuKind.M68000, ExecutionTier.Interpreter));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~CpuCoreFactoryTests"`
Expected: FAIL — compile error (`CpuCoreFactory` not defined).

- [ ] **Step 3: Create the factory (interpreter tier only this task)**

`src/CpuEmulator.Machines/CpuCoreFactory.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;

namespace CpuEmulator.Machines;

/// <summary>The CpuKind -> ICpuCore factory (the resolved open question #1). Returns a
/// Func&lt;IMachineContext, ICpuCore&gt; suitable for MachineBuilder.WithCpu. This is the one place
/// allowed to name the concrete cores AND the JIT, so the layering rule (Core references nothing)
/// stays intact. Piece #1 boots the 6502 (real $FFFC reset) and the Z80 (real PC=0 reset); the
/// 68000/8086 have no-op Reset stubs and are deferred to piece #2 (a MachineConfigurationException
/// here makes that explicit rather than silently producing a board that cannot boot).</summary>
public static class CpuCoreFactory
{
    public static Func<IMachineContext, ICpuCore> ForKind(
        CpuKind kind, AddressSpaceKind programSpace, ExecutionTier tier) => tier switch
    {
        ExecutionTier.Interpreter => ctx => BuildInterpreter(kind, ctx, programSpace),
        _ => throw new MachineConfigurationException(
            $"Execution tier {tier} is not supported yet for {kind}."),
    };

    private static ICpuCore BuildInterpreter(CpuKind kind, IMachineContext ctx, AddressSpaceKind programSpace)
    {
        IAddressSpace bus = ctx.Space(programSpace);
        return kind switch
        {
            CpuKind.Mos6502 => new Mos6502Cpu(bus),
            CpuKind.Z80 => new Z80Cpu(bus),
            _ => throw new MachineConfigurationException(
                $"CpuKind {kind} cannot boot a board yet (no real reset). Deferred to piece #2."),
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~CpuCoreFactoryTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/CpuCoreFactory.cs tests/CpuEmulator.Tests/Machines/CpuCoreFactoryTests.cs
git commit -m "feat(machines): CpuKind->core factory (interpreter tier, 6502 + Z80)"
```

---

## Task 6: The core factory — JIT tier

**Files:**
- Modify: `src/CpuEmulator.Machines/CpuCoreFactory.cs`
- Test: `tests/CpuEmulator.Tests/Machines/CpuCoreFactoryTests.cs` (add tests)

- [ ] **Step 1: Add the failing tests**

Append to the `CpuCoreFactoryTests` class. Add `using CpuEmulator.Jit;` to the file's usings, then:

```csharp
    [Fact]
    public void Jit_tier_6502_builds_a_JittedCpu()
    {
        var machine = MachineFor(CpuKind.Mos6502, ExecutionTier.Jit);
        Assert.IsType<JittedCpu<Mos6502Cpu>>(machine.Cpu);
        Assert.Equal("mos6502", machine.Cpu.Architecture);
    }

    [Fact]
    public void Jit_tier_z80_builds_a_JittedCpu()
    {
        var machine = MachineFor(CpuKind.Z80, ExecutionTier.Jit);
        Assert.IsType<JittedCpu<Z80Cpu>>(machine.Cpu);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~CpuCoreFactoryTests"`
Expected: FAIL — the Jit tier throws `MachineConfigurationException` (the `_ =>` arm).

- [ ] **Step 3: Implement the JIT arm**

The JIT needs the **concrete `AddressSpace`** (not `IAddressSpace`) and, for the Z80, the Io bus. The mapped space IS an `AddressSpace` (the only `IAddressSpace` impl the Machine builds), so cast it. Replace `CpuCoreFactory` with:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;

namespace CpuEmulator.Machines;

/// <summary>The CpuKind -> ICpuCore factory (the resolved open question #1). Returns a
/// Func&lt;IMachineContext, ICpuCore&gt; suitable for MachineBuilder.WithCpu. This is the one place
/// allowed to name the concrete cores AND the JIT, so the layering rule (Core references nothing)
/// stays intact. Piece #1 boots the 6502 (real $FFFC reset) and the Z80 (real PC=0 reset); the
/// 68000/8086 have no-op Reset stubs and are deferred to piece #2 (a MachineConfigurationException
/// here makes that explicit rather than silently producing a board that cannot boot).</summary>
public static class CpuCoreFactory
{
    public static Func<IMachineContext, ICpuCore> ForKind(
        CpuKind kind, AddressSpaceKind programSpace, ExecutionTier tier) =>
        ctx => tier switch
        {
            ExecutionTier.Interpreter => BuildInterpreter(kind, ctx, programSpace),
            ExecutionTier.Jit => BuildJit(kind, ctx, programSpace),
            _ => throw new MachineConfigurationException(
                $"Execution tier {tier} is not supported."),
        };

    private static ICpuCore BuildInterpreter(CpuKind kind, IMachineContext ctx, AddressSpaceKind programSpace)
    {
        IAddressSpace bus = ctx.Space(programSpace);
        return kind switch
        {
            CpuKind.Mos6502 => new Mos6502Cpu(bus),
            CpuKind.Z80 => new Z80Cpu(bus),
            _ => throw new MachineConfigurationException(
                $"CpuKind {kind} cannot boot a board yet (no real reset). Deferred to piece #2."),
        };
    }

    private static ICpuCore BuildJit(CpuKind kind, IMachineContext ctx, AddressSpaceKind programSpace)
    {
        // The JIT binds fastmem to the CONCRETE AddressSpace (page table + backing arrays). The
        // Machine builds AddressSpace as the only IAddressSpace, so this cast always holds.
        var bus = (AddressSpace)ctx.Space(programSpace);
        return kind switch
        {
            CpuKind.Mos6502 => new JittedCpu<Mos6502Cpu>(new Mos6502Cpu(bus), Mos6502Cpu.JitTarget, bus),
            CpuKind.Z80 => BuildZ80Jit(bus),
            _ => throw new MachineConfigurationException(
                $"CpuKind {kind} cannot boot a board yet (no real reset). Deferred to piece #2."),
        };
    }

    private static ICpuCore BuildZ80Jit(AddressSpace bus)
    {
        var inner = new Z80Cpu(bus);
        // The Z80's JIT routes Port-op callouts to its own Io space (inner.IoBus). The board's
        // peripherals are memory-mapped (spec section 6), so the Io space stays empty here.
        return new JittedCpu<Z80Cpu>(inner, Z80Cpu.JitTarget, bus, inner.IoBus);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~CpuCoreFactoryTests"`
Expected: PASS (5 tests). (The JIT tier requires `RuntimeFeature.IsDynamicCodeSupported`; under `dotnet test` on a normal runtime this is true.)

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/CpuCoreFactory.cs tests/CpuEmulator.Tests/Machines/CpuCoreFactoryTests.cs
git commit -m "feat(machines): core factory JIT tier (JittedCpu for 6502 + Z80)"
```

---

## Task 7: `BoardValidationException` + `BoardMachineFactory.Build` (memory + CPU)

**Files:**
- Create: `src/CpuEmulator.Machines/BoardValidationException.cs`
- Create: `src/CpuEmulator.Machines/BoardMachineFactory.cs`
- Test: `tests/CpuEmulator.Tests/Machines/BoardMachineFactoryTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/CpuEmulator.Tests/Machines/BoardMachineFactoryTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class BoardMachineFactoryTests
{
    private static byte[] Rom8k()
    {
        var rom = new byte[0x2000];
        rom[0] = 0xEA;                       // NOP at $E000
        rom[0x1FFC] = 0x00; rom[0x1FFD] = 0xE0; // RESET vector $FFFC/$FFFD -> $E000
        return rom;
    }

    private static BoardSpec MiniSpec(byte[] rom) =>
        new("mini", CpuKind.Mos6502, 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xD000, RegionKind.Ram),
                new MemoryRegion(0xD000, 0x1000, RegionKind.Mmio),
                new MemoryRegion(0xE000, 0x2000, RegionKind.Rom, rom),
            ],
            Peripherals: [new PeripheralSlot("uart", new SimpleUart(), 0xD000, 0x0100)],
            Irq: new IrqWiring([new PeripheralIrq("uart", CpuInterrupt.Irq)]),
            Reset: ResetConfig.None);

    [Fact]
    public void Build_maps_ram_rom_and_the_cpu()
    {
        Machine machine = BoardMachineFactory.Build(MiniSpec(Rom8k()));
        var space = machine.Space(AddressSpaceKind.Program);

        Assert.IsType<Mos6502Cpu>(machine.Cpu);
        space.Write8(0x0000, 0x5A);
        Assert.Equal(0x5A, space.Read8(0x0000));   // RAM writable
        Assert.Equal(0xEA, space.Read8(0xE000));   // ROM byte present
        space.Write8(0xE000, 0xFF);
        Assert.Equal(0xEA, space.Read8(0xE000));   // ROM read-only
    }

    [Fact]
    public void Build_resets_the_cpu_to_the_rom_vector()
    {
        Machine machine = BoardMachineFactory.Build(MiniSpec(Rom8k()));
        machine.Reset();

        Assert.Equal(0xE000u, machine.Cpu.GetRegister("PC"));
    }

    [Fact]
    public void Build_on_an_invalid_spec_throws_with_diagnostics()
    {
        var bad = MiniSpec(Rom8k()) with
        {
            Memory = [new MemoryRegion(0x0000, 0x2000, RegionKind.Ram),
                      new MemoryRegion(0x0800, 0x2000, RegionKind.Ram)], // overlap
        };

        var ex = Assert.Throws<BoardValidationException>(() => BoardMachineFactory.Build(bad));
        Assert.Contains(ex.Diagnostics, d => d.Code == "region-overlap");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BoardMachineFactoryTests"`
Expected: FAIL — compile error (`BoardMachineFactory` / `BoardValidationException` not defined).

- [ ] **Step 3: Create the exception**

`src/CpuEmulator.Machines/BoardValidationException.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Thrown by BoardMachineFactory.Build when BoardSpecValidator returns any diagnostic.
/// Carries every finding so the board author sees all problems at once.</summary>
public sealed class BoardValidationException : Exception
{
    public IReadOnlyList<BoardDiagnostic> Diagnostics { get; }

    public BoardValidationException(string boardName, IReadOnlyList<BoardDiagnostic> diagnostics)
        : base($"Board '{boardName}' is invalid: "
             + string.Join("; ", diagnostics.Select(d => $"[{d.Code}] {d.Message}")))
        => Diagnostics = diagnostics;
}
```

- [ ] **Step 4: Create the factory**

`src/CpuEmulator.Machines/BoardMachineFactory.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>Instantiates a validated BoardSpec into the existing runnable Machine (spec section 3).
/// Validates first (throws BoardValidationException on any diagnostic), applies the ResetConfig's
/// vector patches into the relevant ROM image, then compiles the spec into the fluent MachineBuilder:
/// the program AddressSpace, the RAM/ROM regions, the peripheral slots, and the CpuKind-resolved core.
/// IRQ wiring needs no explicit step here: devices claim their own wired-OR handle via
/// context.IrqLine.Source()/NmiLine.Source() in Realize (the IrqWiring is the validator's contract,
/// not a runtime mapping). The result keeps the device scheduler + interrupt plumbing unchanged.</summary>
public static class BoardMachineFactory
{
    public static Machine Build(BoardSpec spec, ExecutionTier tier = ExecutionTier.Interpreter)
    {
        IReadOnlyList<BoardDiagnostic> diagnostics = BoardSpecValidator.Validate(spec);
        if (diagnostics.Count > 0)
            throw new BoardValidationException(spec.Name, diagnostics);

        ApplyVectorPatches(spec);

        MachineBuilder builder = Machine.Create(spec.Name)
            .WithAddressSpace(AddressSpaceKind.Program, spec.AddressBits);

        foreach (MemoryRegion region in spec.Memory)
        {
            switch (region.Kind)
            {
                case RegionKind.Ram:
                    builder.WithRam(AddressSpaceKind.Program, region.Start, region.Length);
                    break;
                case RegionKind.Rom:
                    builder.WithRom(AddressSpaceKind.Program, region.Start, region.Image!);
                    break;
                case RegionKind.Mmio:
                    // An Mmio region is a hole that peripheral slots fill; no backing to map.
                    break;
            }
        }

        foreach (PeripheralSlot slot in spec.Peripherals)
            builder.WithPeripheral(AddressSpaceKind.Program, slot.Base, slot.Length, slot.Device);

        builder.WithCpu(CpuCoreFactory.ForKind(spec.Cpu, AddressSpaceKind.Program, tier));
        return builder.Build();
    }

    /// <summary>Write each ResetConfig vector byte into the ROM image whose region contains the
    /// patch address. Validation has already confirmed the address is mapped; a patch that lands in
    /// a non-Rom region is a board-author error surfaced here as an explicit exception.</summary>
    private static void ApplyVectorPatches(BoardSpec spec)
    {
        foreach (VectorPatch patch in spec.Reset.VectorPatches)
        {
            MemoryRegion? target = spec.Memory.FirstOrDefault(r =>
                r.Kind == RegionKind.Rom && r.Image is not null &&
                patch.Address >= r.Start && patch.Address < (ulong)r.Start + r.Length);
            if (target is null)
                throw new BoardValidationException(spec.Name,
                    [new BoardDiagnostic("vector-not-rom",
                        $"Reset vector patch at ${patch.Address:X} does not land in a ROM image.")]);
            target.Image![(int)(patch.Address - target.Start)] = patch.Value;
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~BoardMachineFactoryTests"`
Expected: PASS (3 tests). (`GetRegister("PC")` is the 6502's program-counter register name — see `Mos6502Cpu`.)

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Machines/BoardValidationException.cs src/CpuEmulator.Machines/BoardMachineFactory.cs tests/CpuEmulator.Tests/Machines/BoardMachineFactoryTests.cs
git commit -m "feat(machines): BoardMachineFactory.Build compiles a spec into a Machine"
```

---

## Task 8: Board #1 — `Breadboard6502` re-expressed as a `BoardSpec`

**Files:**
- Create: `src/CpuEmulator.Machines/Breadboard6502Board.cs`
- Test: `tests/CpuEmulator.Tests/Machines/Breadboard6502BoardTests.cs`

This is the board-spec; Task 9 is the un-fakeable behavior gate that drives it against the hand-wired board.

- [ ] **Step 1: Write the failing test (the map mirrors the hand-wired board)**

`tests/CpuEmulator.Tests/Machines/Breadboard6502BoardTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Host;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class Breadboard6502BoardTests
{
    private static (Machine Machine, SimpleUart Uart, IntervalTimer Timer) NewBoard()
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        BoardSpec spec = Breadboard6502Board.Spec(DemoRom.Build(), uart, timer);
        return (BoardMachineFactory.Build(spec), uart, timer);
    }

    [Fact]
    public void Ram_is_writable_across_the_low_52k()
    {
        var (machine, _, _) = NewBoard();
        var space = machine.Space(AddressSpaceKind.Program);
        space.Write8(0xCFFF, 0xCD);
        Assert.Equal(0xCD, space.Read8(0xCFFF));
    }

    [Fact]
    public void Uart_status_reads_0x02_when_empty()
    {
        var (machine, _, _) = NewBoard();
        Assert.Equal(0x02u, machine.Space(AddressSpaceKind.Program).Read8(0xD001));
    }

    [Fact]
    public void Timer_ctrl_reads_zero_at_boot()
    {
        var (machine, _, _) = NewBoard();
        Assert.Equal(0x00u, machine.Space(AddressSpaceKind.Program).Read8(0xD100));
    }

    [Fact]
    public void Open_bus_at_D200_reads_0xFF()
    {
        var (machine, _, _) = NewBoard();
        Assert.Equal(0xFFu, machine.Space(AddressSpaceKind.Program).Read8(0xD200));
    }

    [Fact]
    public void Rom_at_E000_is_the_demo_rom_first_byte()
    {
        var (machine, _, _) = NewBoard();
        // DemoRom's first instruction is LDX #$00 (opcode 0xA2).
        Assert.Equal(0xA2, machine.Space(AddressSpaceKind.Program).Read8(0xE000));
    }

    [Fact]
    public void Spec_validates_clean()
    {
        BoardSpec spec = Breadboard6502Board.Spec(DemoRom.Build(), new SimpleUart(), new IntervalTimer());
        Assert.Empty(BoardSpecValidator.Validate(spec));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Breadboard6502BoardTests"`
Expected: FAIL — compile error (`Breadboard6502Board` not defined). (The test references `CpuEmulator.Host.DemoRom`; `CpuEmulator.Tests` already references `CpuEmulator.Host`.)

- [ ] **Step 3: Create the board-spec**

`src/CpuEmulator.Machines/Breadboard6502Board.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The canonical 6502 breadboard, re-expressed as a declarative BoardSpec (spec section 7,
/// Board #1 — the zero-behavior-change gate). The map is byte-for-byte the hand-wired
/// Breadboard6502's v2 layout: RAM $0000-$CFFF (52 KiB), UART at $D000 (1 page), IntervalTimer at
/// $D100 (1 page), $D200-$DFFF open-bus, ROM $E000-$FFFF (8 KiB). The MMIO region spans the whole
/// $D000 page-block so the two device slots land inside it and the validator passes; the open-bus
/// $D200-$DFFF span is simply left unmapped (no region), reproducing the hand-wired board's
/// open-bus reads. The demo ROM image already carries its $FFFC reset vector, so ResetConfig.None.</summary>
public static class Breadboard6502Board
{
    public const uint UartBase = 0xD000;
    public const uint TimerBase = 0xD100;

    /// <summary>Build the board-spec over a caller-supplied ROM image and the two devices (so the
    /// caller keeps handles to FeedInput / OnTransmit, matching how Breadboard6502 exposes them).</summary>
    public static BoardSpec Spec(byte[] rom, SimpleUart uart, IntervalTimer timer) =>
        new("breadboard6502", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xD000, RegionKind.Ram),
                new MemoryRegion(0xD000, 0x1000, RegionKind.Mmio), // $D000-$DFFF: slots + open-bus hole
                new MemoryRegion(0xE000, 0x2000, RegionKind.Rom, rom),
            ],
            Peripherals:
            [
                new PeripheralSlot("uart", uart, UartBase, 0x0100),
                new PeripheralSlot("timer", timer, TimerBase, 0x0100),
            ],
            Irq: new IrqWiring(
            [
                new PeripheralIrq("uart", CpuInterrupt.Irq),
                new PeripheralIrq("timer", CpuInterrupt.Irq),
            ]),
            Reset: ResetConfig.None);
}
```

Note on the open-bus hole: the hand-wired board only maps the UART ($D000) and timer ($D100) pages and leaves $D200-$DFFF unmapped. The `Mmio` region here spans $D000-$DFFF for *validation* (so both slots are "in an Mmio region"), but `BoardMachineFactory` maps **no backing** for an `Mmio` region — only the two peripheral slots get mapped pages. So $D200-$DFFF stays unmapped exactly as before, reading open-bus `0xFF`. This is the behavior Task 9's gate proves.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Breadboard6502BoardTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/Breadboard6502Board.cs tests/CpuEmulator.Tests/Machines/Breadboard6502BoardTests.cs
git commit -m "feat(machines): Board #1 — Breadboard6502 re-expressed as a BoardSpec"
```

---

## Task 9: The zero-behavior-change gate (un-fakeable)

**Files:**
- Test: `tests/CpuEmulator.Tests/Machines/Breadboard6502GateTests.cs`

The gate runs the board-spec machine and the hand-wired `Breadboard6502` from the **same reset**, the **same monitor sessions**, and asserts a **byte-identical UART stream** AND **identical cycle counts**. It must compare the two machines side by side (not against hard-coded constants), so a refactor that drifts behavior cannot pass by editing an expectation.

- [ ] **Step 1: Write the failing gate test**

`tests/CpuEmulator.Tests/Machines/Breadboard6502GateTests.cs`:

```csharp
using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Host;
using CpuEmulator.Machines;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

/// <summary>The un-fakeable zero-behavior-change gate (spec section 8): the Breadboard6502-via-
/// BoardSpec must reproduce, byte for byte and cycle for cycle, the hand-wired Breadboard6502 over
/// the EXACT host sessions HostUatTests exercises. Both machines run the same monitor script from
/// the same reset; the test asserts the two transmit streams and the two cycle counts are equal to
/// EACH OTHER (not to a constant), so a behavioral drift cannot be hidden by editing an expectation.</summary>
[Trait("Category", "UAT")]
public class Breadboard6502GateTests
{
    private sealed record Rig(Machine Machine, SimpleUart Uart, MonitorEngine Engine, StringBuilder Tx);

    private static Rig HandWired()
    {
        var board = new Breadboard6502();
        var tx = new StringBuilder();
        board.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();
        return new Rig(board.Machine, board.Uart, board.NewMonitor(), tx);
    }

    private static Rig BoardSpecRig()
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);
        Machine machine = BoardMachineFactory.Build(Breadboard6502Board.Spec(DemoRom.Build(), uart, timer));
        machine.Reset();
        var engine = new MonitorEngine(
            (CpuEmulator.Cpus.Mos6502.Mos6502Cpu)machine.Cpu,
            machine.Space(AddressSpaceKind.Program),
            (CpuEmulator.Cpus.Mos6502.Mos6502Cpu)machine.Cpu,
            machine.Run);
        return new Rig(machine, uart, engine, tx);
    }

    private static string RunSession(Rig rig, string session)
    {
        var output = new StringWriter();
        new MonitorRepl(rig.Engine, new StringReader(session), output, inject: rig.Uart.FeedInput).Run();
        return output.ToString();
    }

    [Fact]
    public void Hello_stream_and_cycles_match_the_hand_wired_board()
    {
        Rig hand = HandWired();
        Rig spec = BoardSpecRig();

        const string session = """
            g 1000
            q
            """;
        RunSession(hand, session);
        RunSession(spec, session);

        Assert.Equal(hand.Tx.ToString(), spec.Tx.ToString());          // byte-identical UART stream
        Assert.Equal(DemoRom.Message, spec.Tx.ToString());             // and it IS the hello message
        Assert.Equal(hand.Machine.Cpu.CycleCount, spec.Machine.Cpu.CycleCount); // cycle-identical
    }

    [Fact]
    public void Echo_stream_and_cycles_match_the_hand_wired_board()
    {
        Rig hand = HandWired();
        Rig spec = BoardSpecRig();

        const string session = """
            g 1000
            i HI
            g 200
            q
            """;
        RunSession(hand, session);
        RunSession(spec, session);

        Assert.Equal(hand.Tx.ToString(), spec.Tx.ToString());
        Assert.Equal(DemoRom.Message + "HI", spec.Tx.ToString());
        Assert.Equal(hand.Machine.Cpu.CycleCount, spec.Machine.Cpu.CycleCount);
    }
}
```

- [ ] **Step 2: Run the gate to verify it fails (then drives the implementation)**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Breadboard6502GateTests"`
Expected: at first, FAIL only if a map/behavior difference exists. If Tasks 7-8 are correct it may PASS immediately — that is acceptable for a gate test (the gate's value is that it CANNOT pass if behavior drifts). If it FAILS, the failure message shows the first differing byte or the cycle delta; fix the board-spec map (Task 8) until both streams and both cycle counts are equal. Do NOT edit the expectations to match — they compare the two machines to each other.

- [ ] **Step 3: Confirm the gate passes**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~Breadboard6502GateTests"`
Expected: PASS (2 tests). Both UART streams equal; both cycle counts equal.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Machines/Breadboard6502GateTests.cs
git commit -m "test(machines): zero-behavior-change gate — BoardSpec vs hand-wired 6502 (bytes + cycles)"
```

---

## Task 10: The `ReferenceSbc(CpuKind)` recipe

**Files:**
- Create: `src/CpuEmulator.Machines/ReferenceSbc.cs`
- Test: `tests/CpuEmulator.Tests/Machines/ReferenceSbcTests.cs`

The uniform recipe: RAM low, ROM high, UART + timer at fixed MMIO, IRQ → maskable. Piece #1 implements the recipe and uses it for the Z80; the 68000/8086 arms throw (deferred to piece #2 — their cores have no real reset).

- [ ] **Step 1: Write the failing test**

`tests/CpuEmulator.Tests/Machines/ReferenceSbcTests.cs`:

```csharp
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class ReferenceSbcTests
{
    [Fact]
    public void Z80_recipe_validates_clean()
    {
        var rom = new byte[0x2000];
        BoardSpec spec = ReferenceSbc.Build(CpuKind.Z80, new SimpleUart(), new IntervalTimer(), rom);

        Assert.Empty(BoardSpecValidator.Validate(spec));
        Assert.Equal(CpuKind.Z80, spec.Cpu);
        Assert.Equal(16, spec.AddressBits);
    }

    [Fact]
    public void Z80_recipe_puts_ram_low_and_rom_high()
    {
        var rom = new byte[0x2000];
        BoardSpec spec = ReferenceSbc.Build(CpuKind.Z80, new SimpleUart(), new IntervalTimer(), rom);

        Assert.Contains(spec.Memory, r => r.Kind == RegionKind.Ram && r.Start == 0x0000);
        Assert.Contains(spec.Memory, r => r.Kind == RegionKind.Rom && r.Start == 0xE000);
        Assert.Contains(spec.Peripherals, p => p.Name == "uart");
        Assert.Contains(spec.Peripherals, p => p.Name == "timer");
        Assert.Contains(spec.Irq.Lines, l => l.Target == CpuInterrupt.Irq);
    }

    [Fact]
    public void Deferred_cpu_kinds_throw()
    {
        var rom = new byte[0x2000];
        Assert.Throws<NotSupportedException>(() =>
            ReferenceSbc.Build(CpuKind.M68000, new SimpleUart(), new IntervalTimer(), rom));
        Assert.Throws<NotSupportedException>(() =>
            ReferenceSbc.Build(CpuKind.I8086, new SimpleUart(), new IntervalTimer(), rom));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~ReferenceSbcTests"`
Expected: FAIL — compile error (`ReferenceSbc` not defined).

- [ ] **Step 3: Create the recipe**

`src/CpuEmulator.Machines/ReferenceSbc.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The uniform reference-board recipe (spec section 5): one convention, several CPUs.
/// RAM in the low range, ROM in the high range (carrying the reset vector / entry), a memory-mapped
/// UART + interval timer at fixed MMIO addresses, both IRQs wired to the CPU's maskable interrupt.
/// Piece #1 ships the recipe and the Z80 board; the 68000/8086 arms are deferred to piece #2 (their
/// cores have no real reset yet). Addresses follow the breadboard convention so the Z80 board reads
/// the same as the 6502 one: RAM $0000-$DFFF, UART $E000, timer $E100, ROM... see per-CPU notes.</summary>
public static class ReferenceSbc
{
    // The shared MMIO convention for a 16-bit reference board.
    private const uint RamBase = 0x0000;
    private const uint MmioBase = 0xC000;
    private const uint MmioLength = 0x1000;   // $C000-$CFFF: the UART + timer slots
    private const uint UartBase = 0xC000;
    private const uint TimerBase = 0xC100;
    private const uint RomBase = 0xE000;
    private const uint RomLength = 0x2000;     // $E000-$FFFF (8 KiB)
    private const uint RamLength = MmioBase;   // $0000-$BFFF (48 KiB), below the MMIO block

    public static BoardSpec Build(CpuKind cpu, SimpleUart uart, IntervalTimer timer, byte[] rom)
    {
        if (cpu is not (CpuKind.Mos6502 or CpuKind.Z80))
            throw new NotSupportedException(
                $"ReferenceSbc({cpu}) is deferred to piece #2: the {cpu} core has no real reset yet. "
              + "Piece #1 ships the 6502 + Z80 reference boards.");

        if (rom.Length != RomLength)
            throw new ArgumentException(
                $"ReferenceSbc ROM image must be exactly ${RomLength:X} bytes; got ${rom.Length:X}.",
                nameof(rom));

        return new BoardSpec($"ReferenceSbc-{cpu}", cpu, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),
                new MemoryRegion(MmioBase, MmioLength, RegionKind.Mmio),
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, rom),
            ],
            Peripherals:
            [
                new PeripheralSlot("uart", uart, UartBase, 0x0100),
                new PeripheralSlot("timer", timer, TimerBase, 0x0100),
            ],
            Irq: new IrqWiring(
            [
                new PeripheralIrq("uart", CpuInterrupt.Irq),
                new PeripheralIrq("timer", CpuInterrupt.Irq),
            ]),
            Reset: ResetConfig.None); // Z80 resets to PC=0 (RAM); the 6502 image carries $FFFC.
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~ReferenceSbcTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/ReferenceSbc.cs tests/CpuEmulator.Tests/Machines/ReferenceSbcTests.cs
git commit -m "feat(machines): ReferenceSbc(CpuKind) uniform recipe (Z80 board; 68000/8086 deferred)"
```

---

## Task 11: Board #2 — the Z80 reference board boots + runs (both tiers)

**Files:**
- Create: `src/CpuEmulator.Machines/Z80ReferenceRom.cs` (a tiny boot program assembled as raw bytes)
- Test: `tests/CpuEmulator.Tests/Machines/ReferenceSbcZ80Tests.cs`

The Z80 resets to PC=0, so its boot program lives in **RAM at $0000** (the recipe's RAM region) — the test pokes the program into RAM after Build, then runs. This proves the model generalizes across a genuinely different CPU + reset mechanic from the same recipe, on both tiers.

The boot program (Z80 machine code) writes three bytes to the memory-mapped UART DATA register at $C000, then halts:

```
; org 0000h  (Z80 reset entry)
LD A,'O'     ; 3E 4F
LD (0C000h),A; 32 00 C0   ; UART DATA <- 'O'
LD A,'K'     ; 3E 4B
LD (0C000h),A; 32 00 C0   ; UART DATA <- 'K'
LD A,0Dh     ; 3E 0D
LD (0C000h),A; 32 00 C0   ; UART DATA <- CR
HALT         ; 76
```

- [ ] **Step 1: Write the failing test**

`tests/CpuEmulator.Tests/Machines/ReferenceSbcZ80Tests.cs`:

```csharp
using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

/// <summary>Board #2 (spec section 8 "Z80 reference-board smoke"): the Z80 ReferenceSbc boots from
/// its PC=0 reset, runs a tiny program that writes "OK\r" out the memory-mapped UART, and halts —
/// on BOTH tiers (interpreter + JIT). Proves the BoardSpec model generalizes across a genuinely
/// different CPU + reset mechanic from the same recipe.</summary>
public class ReferenceSbcZ80Tests
{
    private static string RunBoot(ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var tx = new StringBuilder();
        uart.OnTransmit = b => tx.Append((char)b);

        var rom = new byte[0x2000]; // unused by the Z80 boot (it runs from RAM at $0000)
        BoardSpec spec = ReferenceSbc.Build(CpuKind.Z80, uart, timer, rom);
        Machine machine = BoardMachineFactory.Build(spec, tier);

        // Poke the boot program into RAM at $0000 (the Z80 reset entry).
        var space = machine.Space(AddressSpaceKind.Program);
        byte[] program =
        [
            0x3E, 0x4F,             // LD A,'O'
            0x32, 0x00, 0xC0,       // LD ($C000),A
            0x3E, 0x4B,             // LD A,'K'
            0x32, 0x00, 0xC0,       // LD ($C000),A
            0x3E, 0x0D,             // LD A,CR
            0x32, 0x00, 0xC0,       // LD ($C000),A
            0x76,                   // HALT
        ];
        for (int i = 0; i < program.Length; i++)
            space.Write8((uint)i, program[i]);

        machine.Reset();          // Z80: PC = 0
        machine.Run(1000);        // ample budget; the program halts well within it
        return tx.ToString();
    }

    [Fact]
    public void Z80_board_boots_and_prints_OK_on_the_interpreter()
    {
        Assert.Equal("OK\r", RunBoot(ExecutionTier.Interpreter));
    }

    [Fact]
    public void Z80_board_boots_and_prints_OK_on_the_jit()
    {
        Assert.Equal("OK\r", RunBoot(ExecutionTier.Jit));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~ReferenceSbcZ80Tests"`
Expected: FAIL — if the Z80 `LD (nn),A` / `HALT` / memory-mapped store path runs correctly, this may pass; if the boot does not produce "OK\r" the failure shows the actual transmitted string. (The Z80 interpreter supports `LD`, absolute store, and `HALT` per the M3 work; the JIT all-fallback path delegates to the interpreter, so both tiers must agree.)

If either tier transmits the wrong bytes, debug the Z80 store path (do NOT weaken the assertion). The most likely issue is the `Machine.Run` halt handling: a halted Z80 idles cycles via `inner.Step`, so the budget drains after HALT — the three bytes must already be on `tx` before then. The assertion is on the transmitted bytes, which the three `LD ($C000),A` stores produce before HALT.

- [ ] **Step 3: If a real boot ROM is cleaner, add `Z80ReferenceRom.cs`**

Only if the inline byte array proves awkward to maintain across both tests: extract the program to `src/CpuEmulator.Machines/Z80ReferenceRom.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>The Z80 ReferenceSbc smoke program (assembled by hand to raw bytes): write "OK\r" to the
/// memory-mapped UART DATA register at $C000, then HALT. The Z80 resets to PC=0, so this loads into
/// RAM at $0000 (not the ROM region). Kept tiny + literal so the board smoke test is self-evidently
/// correct.</summary>
public static class Z80ReferenceRom
{
    /// <summary>The boot bytes, loaded at $0000.</summary>
    public static byte[] BootProgram =>
    [
        0x3E, 0x4F,       // LD A,'O'
        0x32, 0x00, 0xC0, // LD ($C000),A  ; UART DATA
        0x3E, 0x4B,       // LD A,'K'
        0x32, 0x00, 0xC0, // LD ($C000),A
        0x3E, 0x0D,       // LD A,CR
        0x32, 0x00, 0xC0, // LD ($C000),A
        0x76,             // HALT
    ];
}
```

Then replace the inline `byte[] program = [...]` in the test with `byte[] program = Z80ReferenceRom.BootProgram;`. (This step is optional DRY; skip if the inline array is fine.)

- [ ] **Step 4: Run test to verify it passes (both tiers)**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~ReferenceSbcZ80Tests"`
Expected: PASS (2 tests — interpreter + JIT both print "OK\r").

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines tests/CpuEmulator.Tests/Machines/ReferenceSbcZ80Tests.cs
git commit -m "feat(machines): Board #2 — Z80 ReferenceSbc boots + prints OK on both tiers"
```

---

## Task 12: Full-suite no-regression run + final commit

**Files:** none (verification task).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build CpuEmulator.sln -c Debug`
Expected: `Build succeeded.` 0 errors. (Confirms the new assembly did not break the AOT-clean projects: `Core`, `Host`, the cores still compile; only `Machines` + `Tests` reference the JIT.)

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test CpuEmulator.sln -c Debug`
Expected: all tests pass, including the existing `HostUatTests`, `Breadboard6502Tests`, `MachineBuilderTests`, `SimpleUartTests`, `IntervalTimerTests`, the TomHarte/Klaus/Zex CPU suites, and the new `Machines/*` tests. No regression to the cores, the device layer, or the monitor (spec section 8 "No regression").

- [ ] **Step 3: Confirm the AOT-clean publish path still works (the layering guard)**

Run: `dotnet build src/CpuEmulator.Host/CpuEmulator.Host.csproj -c Release`
Expected: `Build succeeded.` The Host still does NOT reference `Machines`/`Jit` (it keeps its own hand-wired `Breadboard6502` for now; wiring the host to the board-spec is piece #3). This confirms the new assembly is additive and the Host's NativeAOT graph is unchanged.

- [ ] **Step 4: Final commit (if any verification fix was needed)**

```bash
git add -A
git commit -m "test(machines): full-suite no-regression pass for the machine model"
```

(If steps 1-3 pass with no changes, skip this commit.)

---

## Self-Review

**1. Spec coverage** — each spec requirement mapped to a task:

| Spec section / requirement | Task(s) |
|---|---|
| §3 `BoardSpec` + `MemoryRegion`/`PeripheralSlot`/`IrqWiring`/`ResetConfig` types | Task 1 (enums), Task 2 (records) |
| §3 `BoardSpecValidator` (overlap, width-fit, slot-in-Mmio, IRQ-wired, ROM-fits, reset-vector-mapped) + tests | Task 3 (regions), Task 4 (slot/IRQ/ROM/vector) |
| §3 `MachineBuilder.Build(BoardSpec) -> Machine` instantiation (reuse fastmem RAM/MMIO split + existing scheduler/IRQ) | Task 7 (`BoardMachineFactory.Build`, resolved as a static factory — see Decision note below) |
| §4 `IPeripheral` generalization + `SimpleUart`/`IntervalTimer` refactor (board-attachable, no 6502 wiring) | Already satisfied by existing code; locked by Task 8/9 (devices slotted by address, behavior preserved). No device edits — documented in "What already exists". |
| §5 `ReferenceSbc(CpuKind)` recipe (RAM low/ROM high/UART+timer MMIO/IRQ→maskable) | Task 10 |
| §6 I/O model: peripherals memory-mapped on all CPUs (port space out of scope) | Tasks 8 + 10 (all slots memory-mapped; Io space left empty in the Z80 JIT arm) |
| §7 Board #1: `Breadboard6502` as a `BoardSpec` (zero-behavior-change gate) | Task 8 (spec) + Task 9 (gate) |
| §7 Board #2: Z80 `ReferenceSbc(Z80)` that runs | Task 11 |
| §8 validator tests (each diagnostic) | Tasks 3 + 4 |
| §8 6502 zero-behavior-change gate (byte-identical UART + cycles, over existing host sessions) | Task 9 (compares against the hand-wired board over the `HostUatTests` sessions) |
| §8 Z80 smoke on both tiers (interpreter + JIT) | Task 11 (both `ExecutionTier` values) |
| §8 no regression to cores / device / monitor | Task 12 |
| §10 open question: build-time vs load-time validation → load-time validator | Tasks 3/4 (load-time; Roslyn analyzer explicitly deferred per spec) |
| §10 open question: `CpuKind`→core instantiation | Task 5 (interpreter) + Task 6 (JIT); Decision #1 documents the composition-root placement |
| §10 open question: per-CPU `ResetConfig` mechanics | Task 2 (`ResetConfig` carries board-level vector patches only; per-CPU reset stays in the core) + Decision #2 |

A subtle spec-vs-reality note: the spec writes `MachineBuilder.Build(BoardSpec)`, implying an overload on the existing `Core.MachineBuilder`. That is **not possible without breaking the layering rule** (it would force `Core` to reference `Jit` + the CPU assemblies). The plan resolves this by introducing `BoardMachineFactory.Build(BoardSpec)` in the new composition-root assembly — same behavior, correct layering. This is called out in the Architecture header and the resolved open questions, and is the single intentional deviation from the spec's literal wording.

Also: the spec's `BoardSpec` record omits an explicit address-width field (it says regions must "fit the CPU's address width"). The plan adds `AddressBits` to `BoardSpec` so the validator can check width without hard-coding a per-`CpuKind` table — a minor, additive shape decision the validator needs.

**2. Placeholder scan** — searched the plan for `TBD`, `TODO`, "implement later", "add validation/error handling" (without code), "similar to Task N", and undefined-symbol references. None present: every code step shows complete code; every type used in a later task (`BoardDiagnostic`, `BoardSpec`, `CpuCoreFactory.ForKind`, `BoardMachineFactory.Build`, `Breadboard6502Board.Spec`, `ReferenceSbc.Build`, `ResetConfig.None`, `IrqWiring.None`, `PeripheralIrq`, `CpuInterrupt`, `VectorPatch`) is defined in an earlier task. The one "optional" step (Task 11 Step 3) is explicitly marked optional DRY, not a placeholder.

**3. Type consistency** — checked signatures across tasks:
- `BoardSpec(string Name, CpuKind Cpu, int AddressBits, IReadOnlyList<MemoryRegion> Memory, IReadOnlyList<PeripheralSlot> Peripherals, IrqWiring Irq, ResetConfig Reset)` — used identically in Tasks 2, 7, 8, 10 (named args `Memory:`/`Peripherals:`/`Irq:`/`Reset:` in the multi-line uses; positional in the shape test).
- `MemoryRegion(uint Start, uint Length, RegionKind Kind, byte[]? Image = null)` — consistent everywhere; `Image` length checked == `Length` (Task 4 `rom-image-mismatch`) and applied via `WithRom(..., region.Image!)` (Task 7).
- `PeripheralSlot(string Name, IPeripheral Device, uint Base, uint Length)` — consistent; `.Base`/`.Length`/`.Name`/`.Device` referenced in Tasks 4, 7, 8, 10.
- `IrqWiring(IReadOnlyList<PeripheralIrq> Lines)` + `PeripheralIrq(string PeripheralName, CpuInterrupt Target)` + `IrqWiring.None` — consistent; validator reads `.Lines`/`.PeripheralName`/`.Target` (Task 4), boards build them (Tasks 8, 10).
- `ResetConfig(IReadOnlyList<VectorPatch> VectorPatches)` + `VectorPatch(uint Address, byte Value)` + `ResetConfig.None` — consistent; validator reads `.VectorPatches`/`.Address` (Task 4), factory applies `.Value` (Task 7).
- `CpuCoreFactory.ForKind(CpuKind, AddressSpaceKind, ExecutionTier)` → `Func<IMachineContext, ICpuCore>` — same signature Task 5 (defined) and Task 7 (`BoardMachineFactory` calls it). Task 6 rewrites the body but keeps the signature.
- `BoardMachineFactory.Build(BoardSpec, ExecutionTier = Interpreter)` — same in Tasks 7, 9, 11.
- `BoardSpecValidator.Validate(BoardSpec) -> IReadOnlyList<BoardDiagnostic>` — same in Tasks 3, 4, 8, 10.
- JIT construction matches the real ctor `JittedCpu<TCpu>(TCpu inner, IJitTarget target, AddressSpace bus, IAddressSpace? ioBus = null, ...)` — Task 6 passes `(inner, Xxx.JitTarget, bus)` for the 6502 and `(inner, Z80Cpu.JitTarget, bus, inner.IoBus)` for the Z80, exactly as the bench drivers and `CpmBdosHost` do.
- Register name `"PC"` (Task 7) matches the 6502 program-counter register; `GetRegister`/`CycleCount` are `ICpuCore` members (used in Tasks 7, 9).

No inconsistencies found. The plan is internally consistent and grounded in the real signatures.
