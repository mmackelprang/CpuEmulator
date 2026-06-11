# M1 Chunk 1: Core Contracts + Bus + Machine Skeleton — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `CpuEmulator.Core` — the AOT-clean contract assembly (CPU, bus, peripheral, scheduler, machine composition) — fully unit-tested, as PR #1 of Milestone 1.

**Architecture:** Page-table-backed address spaces (RAM/ROM fast path, `IPeripheral` MMIO slow path, MAME-style program/data/IO kinds), QOM-style two-phase device lifecycle (construct → `Realize`), and a `Machine` container built by a fluent `MachineBuilder`. No CPU implementation yet — `ICpuCore` is exercised by test doubles; the generated 6502 arrives in chunk 2.

**Tech Stack:** .NET 10 (LTS), C# (latest), xUnit. `CpuEmulator.Core` sets `IsAotCompatible` so trim/AOT analyzers enforce the spec's AOT-clean rule from day one.

**Spec:** `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md` (§4 Core contracts, §7 Error handling). Out of scope here: source generator, 6502 spec, TomHarte harness, peripherals, host, JIT — those are M1 chunks 2–4 and M2, each with its own plan. The undefined-opcode policy (spec §7) lives with the CPU spec in chunk 2.

**Plan series:** this is plan 1 of 4 for Milestone 1.

---

## File structure

```
CpuEmulator.sln
Directory.Build.props                          — shared quality settings (nullable, warnings-as-errors)
src/CpuEmulator.Core/
    CpuEmulator.Core.csproj                    — IsAotCompatible=true
    AddressSpaceKind.cs                        — enum: Program / Data / Io
    AccessWidth.cs                             — enum: Byte / Word / Long (bytes per access)
    EmulationException.cs                      — base host-world failure
    MachineConfigurationException.cs           — bad wiring/spec at build time
    StrictBusViolationException.cs             — opt-in strict bus mode
    ICpuCore.cs                                — Step/Run/Reset/IRQ/introspection contract
    IPeripheral.cs                             — Read/Write(offset,width) + Realize
    IAddressSpace.cs                           — bus contract
    AddressSpaceOptions.cs                     — open-bus value + strict flag
    AddressSpace.cs                            — page-table implementation
    IInterruptLine.cs                          — Assert/Release contract
    InterruptLine.cs                           — forwards to a CPU line input
    IScheduler.cs                              — cycle clock + event queue contract
    CycleScheduler.cs                          — PriorityQueue implementation
    IMachineContext.cs                         — what a peripheral sees during Realize
    Machine.cs                                 — container; two-phase build; run loop
    MachineBuilder.cs                          — fluent composition
tests/CpuEmulator.Tests/
    CpuEmulator.Tests.csproj
    TestDoubles/FakeCpu.cs                     — budget-consuming ICpuCore double
    TestDoubles/StuckCpu.cs                    — never-progresses double (run-loop guard test)
    TestDoubles/RecordingPeripheral.cs         — records Reads/Writes/Realize
    AddressSpaceMemoryTests.cs
    AddressSpacePeripheralTests.cs
    AddressSpacePolicyTests.cs                 — open bus, strict mode, mapping validation
    CycleSchedulerTests.cs
    InterruptLineTests.cs
    MachineBuilderTests.cs
    MachineRunTests.cs
```

Design notes locked by this plan:

- **Page size is 256 bytes** (`PageShift = 8`). Natural for 8-bit machines; a 64 KiB space is 256 entries. Mapping granularity = page granularity; sub-page decode is the peripheral's job (authentic partial decode — an Apple I mirrors its PIA across a page the same way). Address spaces support 8–24 bits; 32-bit spaces need a two-level table and are explicitly out of scope until a 32-bit CPU exists.
- **`IPeripheral` is width-aware now** (`AccessWidth`), returning/taking `uint`, so the 68000 never forces a contract break. 8-bit buses only ever pass `AccessWidth.Byte`.
- **Wider bus reads (`Read16` etc.) are NOT added yet** — YAGNI until a 16-bit CPU spec needs them; chunk-2+ plans add them alongside their first consumer.
- **Guest-world vs host-world failures** (spec §7): unmapped reads return the open-bus value, unmapped/ROM writes are ignored — unless `AddressSpaceOptions.Strict` is set, which throws `StrictBusViolationException`. Misconfiguration (overlap, misalignment, missing CPU) always throws `MachineConfigurationException` at build time, never mid-run.

---

### Task 1: Branch, solution scaffolding, quality settings

**Files:**
- Create: `CpuEmulator.sln`, `Directory.Build.props`
- Create: `src/CpuEmulator.Core/CpuEmulator.Core.csproj` (via template, then edit)
- Create: `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` (via template)
- Modify: `.gitignore` (only if `bin/`/`obj/` missing)

- [ ] **Step 1: Create the working branch** (never commit implementation to main)

```bash
git checkout -b feat/m1-core-contracts
```

- [ ] **Step 2: Verify SDK and scaffold the solution**

```bash
dotnet --version            # expect 10.x
dotnet new sln -n CpuEmulator
dotnet new classlib -o src/CpuEmulator.Core -n CpuEmulator.Core
dotnet new xunit -o tests/CpuEmulator.Tests -n CpuEmulator.Tests
dotnet sln add src/CpuEmulator.Core tests/CpuEmulator.Tests
dotnet add tests/CpuEmulator.Tests reference src/CpuEmulator.Core
```

Delete the template placeholder files `src/CpuEmulator.Core/Class1.cs` and `tests/CpuEmulator.Tests/UnitTest1.cs`.

- [ ] **Step 3: Create `Directory.Build.props`** at the repo root:

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Enforce AOT-cleanliness on Core.** Edit `src/CpuEmulator.Core/CpuEmulator.Core.csproj` to exactly:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>

</Project>
```

(Leave the test csproj as the template generated it.)

- [ ] **Step 5: Ensure build artifacts are ignored.** If `.gitignore` does not already cover them, append:

```
bin/
obj/
```

- [ ] **Step 6: Verify the empty solution builds and tests run**

Run: `dotnet build && dotnet test`
Expected: build succeeds; test run reports zero tests, exit code 0.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution with Core library and test project"
```

---

### Task 2: Contract types (enums, exceptions, interfaces)

Pure contracts — no behavior, so no TDD here; behavior tasks (3–9) test everything that moves. Compile check is the gate.

**Files:**
- Create: `src/CpuEmulator.Core/AddressSpaceKind.cs`
- Create: `src/CpuEmulator.Core/AccessWidth.cs`
- Create: `src/CpuEmulator.Core/EmulationException.cs`
- Create: `src/CpuEmulator.Core/MachineConfigurationException.cs`
- Create: `src/CpuEmulator.Core/StrictBusViolationException.cs`
- Create: `src/CpuEmulator.Core/ICpuCore.cs`
- Create: `src/CpuEmulator.Core/IPeripheral.cs`
- Create: `src/CpuEmulator.Core/IAddressSpace.cs`
- Create: `src/CpuEmulator.Core/IInterruptLine.cs`
- Create: `src/CpuEmulator.Core/IScheduler.cs`
- Create: `src/CpuEmulator.Core/IMachineContext.cs`

- [ ] **Step 1: Write the enum files**

`src/CpuEmulator.Core/AddressSpaceKind.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// Identifies one of the up-to-three buses a CPU can own (MAME-style).
/// Von Neumann parts use only <see cref="Program"/>; Harvard parts (8051) add
/// <see cref="Data"/>; port-I/O parts (Z80, 8086) add <see cref="Io"/>.
/// </summary>
public enum AddressSpaceKind
{
    Program,
    Data,
    Io,
}
```

`src/CpuEmulator.Core/AccessWidth.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>Width of a single bus access, in bytes.</summary>
public enum AccessWidth : byte
{
    Byte = 1,
    Word = 2,
    Long = 4,
}
```

- [ ] **Step 2: Write the exception files**

`src/CpuEmulator.Core/EmulationException.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// Base type for host-world failures (configuration errors, codegen bugs, framework misuse).
/// Guest-world events (undefined opcodes, open-bus reads) are emulated behavior and never
/// throw unless an opt-in strict policy is enabled.
/// </summary>
public class EmulationException : Exception
{
    public EmulationException(string message) : base(message) { }
    public EmulationException(string message, Exception inner) : base(message, inner) { }
}
```

`src/CpuEmulator.Core/MachineConfigurationException.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>Bad machine wiring or spec misuse, detected at build/realize time — never mid-run.</summary>
public sealed class MachineConfigurationException : EmulationException
{
    public MachineConfigurationException(string message) : base(message) { }
    public MachineConfigurationException(string message, Exception inner) : base(message, inner) { }
}
```

`src/CpuEmulator.Core/StrictBusViolationException.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>Thrown only when <see cref="AddressSpaceOptions.Strict"/> is enabled — the
/// opt-in firmware-debugging posture for unmapped or read-only accesses.</summary>
public sealed class StrictBusViolationException : EmulationException
{
    public StrictBusViolationException(string message) : base(message) { }
    public StrictBusViolationException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] **Step 3: Write the interface files**

`src/CpuEmulator.Core/ICpuCore.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// A CPU core. Implementations are generated from ISA specs; both execution tiers
/// (interpreter, IL-JIT) sit behind this one interface.
/// </summary>
public interface ICpuCore
{
    /// <summary>Architecture identifier, e.g. "mos6502".</summary>
    string Architecture { get; }

    /// <summary>Total cycles executed since construction. Monotonic.</summary>
    long CycleCount { get; }

    void Reset();

    /// <summary>Execute exactly one instruction.</summary>
    void Step();

    /// <summary>
    /// Run instructions until <paramref name="cycleBudget"/> is exhausted, decrementing it
    /// by cycles actually executed. May overshoot by at most one instruction (budget may
    /// go slightly negative). The decrement always equals the increase in <see cref="CycleCount"/>.
    /// </summary>
    void Run(ref long cycleBudget);

    void SetIrqLine(bool asserted);
    void SetNmiLine(bool asserted);

    /// <summary>Register names for generic state introspection (test harness, debugger).</summary>
    IReadOnlyList<string> RegisterNames { get; }

    /// <summary>Get a register's current value, zero-extended to 64 bits.</summary>
    /// <exception cref="ArgumentException">The name is not in <see cref="RegisterNames"/>.</exception>
    ulong GetRegister(string name);

    /// <summary>Set a register. Values are truncated to the register's natural width.</summary>
    /// <exception cref="ArgumentException">The name is not in <see cref="RegisterNames"/>.</exception>
    void SetRegister(string name, ulong value);
}
```

`src/CpuEmulator.Core/IPeripheral.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// A device mapped into an address space over an address range. Lifecycle is two-phase:
/// constructor = configure; <see cref="Realize"/> = wire to the machine (claim IRQ lines,
/// schedule events). The Machine maps the device onto the bus before calling Realize.
/// </summary>
public interface IPeripheral
{
    string Name { get; }

    /// <summary>Called exactly once by the Machine, after all bus mappings exist.</summary>
    void Realize(IMachineContext context);

    /// <summary>Read from the device. <paramref name="offset"/> is relative to the mapping base.</summary>
    uint Read(uint offset, AccessWidth width);

    void Write(uint offset, AccessWidth width, uint value);
}
```

`src/CpuEmulator.Core/IAddressSpace.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// One bus: a page-table-backed address space. Pages resolve to backing memory
/// (RAM/ROM fast path) or an <see cref="IPeripheral"/> handler (MMIO slow path).
/// </summary>
public interface IAddressSpace
{
    AddressSpaceKind Kind { get; }
    int AddressBits { get; }

    byte Read8(uint address);
    void Write8(uint address, byte value);

    /// <summary>Map RAM (<paramref name="writable"/>=true) or ROM (false). The backing
    /// length must be a positive multiple of the page size; start must be page-aligned.</summary>
    void MapMemory(uint start, byte[] backing, bool writable);

    /// <summary>Map a device over [start, start+length). Same alignment rules.</summary>
    void MapPeripheral(uint start, uint length, IPeripheral peripheral);
}
```

`src/CpuEmulator.Core/IInterruptLine.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>A level-sensitive interrupt line input, as seen by the device asserting it.</summary>
public interface IInterruptLine
{
    bool IsAsserted { get; }
    void Assert();
    void Release();
}
```

`src/CpuEmulator.Core/IScheduler.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// The machine's clock as seen by devices: a cycle counter plus an event queue.
/// Deliberately minimal in M1; grows with the timer milestone. Defining it now prevents
/// peripherals from inventing their own notion of time.
/// Advancing time is the machine driver's job — the concrete CycleScheduler.AdvanceTo —
/// and is intentionally absent from this consumer-facing contract.
/// </summary>
public interface IScheduler
{
    long CurrentCycle { get; }

    /// <summary>Schedule a callback at an absolute cycle (must not be in the past).</summary>
    void ScheduleAt(long cycle, Action callback);
}
```

`src/CpuEmulator.Core/IMachineContext.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>What a peripheral (or CPU factory) may see of the machine during construction.</summary>
public interface IMachineContext
{
    IScheduler Scheduler { get; }
    IAddressSpace Space(AddressSpaceKind kind);
    IInterruptLine IrqLine { get; }
    IInterruptLine NmiLine { get; }
}
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build`
Expected: success, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core
git commit -m "feat: add core contract interfaces, enums, and exception types"
```

---

### Task 3: AddressSpace — memory fast path

**Files:**
- Create: `src/CpuEmulator.Core/AddressSpaceOptions.cs`
- Create: `src/CpuEmulator.Core/AddressSpace.cs`
- Test: `tests/CpuEmulator.Tests/AddressSpaceMemoryTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/CpuEmulator.Tests/AddressSpaceMemoryTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class AddressSpaceMemoryTests
{
    private static AddressSpace NewSpace(AddressSpaceOptions? options = null) =>
        new(AddressSpaceKind.Program, addressBits: 16, options);

    [Fact]
    public void Ram_read_returns_what_was_written()
    {
        var space = NewSpace();
        space.MapMemory(0x0000, new byte[0x1000], writable: true);

        space.Write8(0x0123, 0xAB);

        Assert.Equal(0xAB, space.Read8(0x0123));
    }

    [Fact]
    public void Rom_exposes_image_contents()
    {
        var space = NewSpace();
        var image = new byte[0x100];
        image[0x10] = 0x42;
        space.MapMemory(0xFF00, image, writable: false);

        Assert.Equal(0x42, space.Read8(0xFF10));
    }

    [Fact]
    public void Rom_write_is_silently_ignored()
    {
        var space = NewSpace();
        var image = new byte[0x100];
        image[0x10] = 0x42;
        space.MapMemory(0xFF00, image, writable: false);

        space.Write8(0xFF10, 0x00); // authentic bus behavior: write to ROM does nothing

        Assert.Equal(0x42, space.Read8(0xFF10));
    }

    [Fact]
    public void Multi_page_ram_addresses_correct_backing_byte()
    {
        var space = NewSpace();
        var ram = new byte[0x1000];           // 16 pages
        space.MapMemory(0x2000, ram, writable: true);

        space.Write8(0x2ABC, 0x77);           // page 10 of the mapping

        Assert.Equal(0x77, ram[0x0ABC]);      // backing offset is mapping-relative
    }

    [Fact]
    public void Address_above_space_width_wraps()
    {
        var space = NewSpace();
        space.MapMemory(0x0000, new byte[0x100], writable: true);

        space.Write8(0x0042, 0x99);

        Assert.Equal(0x99, space.Read8(0x1_0042)); // 17-bit address masks to 16 bits
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AddressSpaceMemoryTests"`
Expected: FAIL — compile error `CS0246: The type or namespace name 'AddressSpace' could not be found` (a compile failure is the failing state for a not-yet-written type).

- [ ] **Step 3: Write the implementation**

`src/CpuEmulator.Core/AddressSpaceOptions.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>Per-space bus behavior policy (spec §7).</summary>
public sealed class AddressSpaceOptions
{
    /// <summary>Value returned by reads from unmapped addresses.</summary>
    public byte OpenBusValue { get; init; } = 0xFF;

    /// <summary>When true, unmapped reads/writes and ROM writes throw
    /// <see cref="StrictBusViolationException"/> instead of using open-bus semantics.</summary>
    public bool Strict { get; init; }
}
```

`src/CpuEmulator.Core/AddressSpace.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// Page-table-backed address space. 256-byte pages: each page resolves to backing
/// memory (fast path) or a peripheral handler (MMIO slow path). Mapping granularity
/// is one page; sub-page decode is the peripheral's job (authentic partial decode).
/// </summary>
public sealed class AddressSpace : IAddressSpace
{
    public const int PageSize = 256;
    private const int PageShift = 8;
    private const uint PageMask = PageSize - 1;

    private struct PageEntry
    {
        public byte[]? Backing;      // non-null => memory fast path
        public int BackingOffset;    // index into Backing of this page's first byte
        public bool Writable;
        public IPeripheral? Handler; // non-null => MMIO slow path
        public uint HandlerBase;     // absolute address of the handler's mapping start
    }

    private readonly PageEntry[] _pages;
    private readonly AddressSpaceOptions _options;

    public AddressSpaceKind Kind { get; }
    public int AddressBits { get; }
    public uint AddressMask { get; }

    public AddressSpace(AddressSpaceKind kind, int addressBits, AddressSpaceOptions? options = null)
    {
        // 32-bit spaces need a two-level table (a flat one would be ~16M entries);
        // out of scope until a 32-bit CPU exists.
        if (addressBits is < 8 or > 24)
            throw new MachineConfigurationException(
                $"addressBits must be between 8 and 24, got {addressBits}.");

        Kind = kind;
        AddressBits = addressBits;
        AddressMask = (1u << addressBits) - 1;
        _options = options ?? new AddressSpaceOptions();
        _pages = new PageEntry[(1 << addressBits) >> PageShift];
    }

    public void MapMemory(uint start, byte[] backing, bool writable)
    {
        ValidateRange(start, (uint)backing.Length);
        int firstPage = (int)(start >> PageShift);
        int pageCount = backing.Length >> PageShift;
        for (int i = 0; i < pageCount; i++)
        {
            ref PageEntry page = ref _pages[firstPage + i];
            EnsureUnmapped(in page, start + (uint)(i << PageShift));
            page.Backing = backing;
            page.BackingOffset = i << PageShift;
            page.Writable = writable;
        }
    }

    public void MapPeripheral(uint start, uint length, IPeripheral peripheral)
    {
        ValidateRange(start, length);
        int firstPage = (int)(start >> PageShift);
        int pageCount = (int)(length >> PageShift);
        for (int i = 0; i < pageCount; i++)
        {
            ref PageEntry page = ref _pages[firstPage + i];
            EnsureUnmapped(in page, start + (uint)(i << PageShift));
            page.Handler = peripheral;
            page.HandlerBase = start;
        }
    }

    public byte Read8(uint address)
    {
        address &= AddressMask;
        ref PageEntry page = ref _pages[address >> PageShift];
        if (page.Backing is not null)
            return page.Backing[page.BackingOffset + (int)(address & PageMask)];
        if (page.Handler is not null)
            return (byte)page.Handler.Read(address - page.HandlerBase, AccessWidth.Byte);
        if (_options.Strict)
            throw new StrictBusViolationException($"Read from unmapped address 0x{address:X4}.");
        return _options.OpenBusValue;
    }

    public void Write8(uint address, byte value)
    {
        address &= AddressMask;
        ref PageEntry page = ref _pages[address >> PageShift];
        if (page.Backing is not null)
        {
            if (page.Writable)
                page.Backing[page.BackingOffset + (int)(address & PageMask)] = value;
            else if (_options.Strict)
                throw new StrictBusViolationException($"Write to read-only memory at 0x{address:X4}.");
            return; // ROM write silently ignored (authentic bus behavior)
        }
        if (page.Handler is not null)
        {
            page.Handler.Write(address - page.HandlerBase, AccessWidth.Byte, value);
            return;
        }
        if (_options.Strict)
            throw new StrictBusViolationException($"Write to unmapped address 0x{address:X4}.");
        // unmapped write silently ignored
    }

    private void ValidateRange(uint start, uint length)
    {
        if (length == 0 || (length & PageMask) != 0)
            throw new MachineConfigurationException(
                $"Mapping length 0x{length:X} is not a positive multiple of the {PageSize}-byte page size.");
        if ((start & PageMask) != 0)
            throw new MachineConfigurationException(
                $"Mapping start 0x{start:X} is not {PageSize}-byte page aligned.");
        if (start > AddressMask || length - 1 > AddressMask - start)
            throw new MachineConfigurationException(
                $"Mapping 0x{start:X}..0x{start + (length - 1):X} exceeds the {AddressBits}-bit address space.");
    }

    private static void EnsureUnmapped(in PageEntry page, uint pageAddress)
    {
        if (page.Backing is not null || page.Handler is not null)
            throw new MachineConfigurationException(
                $"Page at 0x{pageAddress:X} is already mapped; overlapping mappings are not allowed.");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AddressSpaceMemoryTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core tests/CpuEmulator.Tests
git commit -m "feat: add page-table AddressSpace with RAM/ROM fast path"
```

---

### Task 4: AddressSpace — peripheral (MMIO) dispatch

**Files:**
- Create: `tests/CpuEmulator.Tests/TestDoubles/RecordingPeripheral.cs`
- Test: `tests/CpuEmulator.Tests/AddressSpacePeripheralTests.cs`

(The implementation already exists in `AddressSpace.cs` from Task 3 — these tests pin the MMIO contract: offset translation, width, value routing. TDD here verifies behavior that was written ahead of its tests; if any test fails, fix `AddressSpace.cs`.)

- [ ] **Step 1: Write the test double**

`tests/CpuEmulator.Tests/TestDoubles/RecordingPeripheral.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.TestDoubles;

/// <summary>Records every bus access and Realize call; returns a programmable read value.</summary>
internal sealed class RecordingPeripheral : IPeripheral
{
    public string Name { get; init; } = "recorder";
    public IMachineContext? RealizedWith { get; private set; }
    public int RealizeCount { get; private set; }
    public List<string>? RealizeLog { get; init; }
    public List<(uint Offset, AccessWidth Width)> Reads { get; } = [];
    public List<(uint Offset, AccessWidth Width, uint Value)> Writes { get; } = [];
    public uint NextReadValue { get; set; }

    public void Realize(IMachineContext context)
    {
        RealizedWith = context;
        RealizeCount++;
        RealizeLog?.Add(Name);
    }

    public uint Read(uint offset, AccessWidth width)
    {
        Reads.Add((offset, width));
        return NextReadValue;
    }

    public void Write(uint offset, AccessWidth width, uint value) =>
        Writes.Add((offset, width, value));
}
```

- [ ] **Step 2: Write the tests**

`tests/CpuEmulator.Tests/AddressSpacePeripheralTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class AddressSpacePeripheralTests
{
    private static AddressSpace NewSpace() =>
        new(AddressSpaceKind.Program, addressBits: 16);

    [Fact]
    public void Read_routes_to_peripheral_with_mapping_relative_offset()
    {
        var space = NewSpace();
        var device = new RecordingPeripheral { NextReadValue = 0x5A };
        space.MapPeripheral(0xD000, 0x100, device);

        byte value = space.Read8(0xD010);

        Assert.Equal(0x5A, value);
        Assert.Equal((0x10u, AccessWidth.Byte), Assert.Single(device.Reads));
    }

    [Fact]
    public void Write_routes_to_peripheral_with_offset_width_and_value()
    {
        var space = NewSpace();
        var device = new RecordingPeripheral();
        space.MapPeripheral(0xD000, 0x100, device);

        space.Write8(0xD012, 0xCD);

        Assert.Equal((0x12u, AccessWidth.Byte, 0xCDu), Assert.Single(device.Writes));
    }

    [Fact]
    public void Multi_page_mapping_offsets_are_relative_to_mapping_base_not_page_base()
    {
        var space = NewSpace();
        var device = new RecordingPeripheral();
        space.MapPeripheral(0xC000, 0x200, device); // two pages

        space.Read8(0xC180);                        // second page of the mapping

        Assert.Equal(0x180u, Assert.Single(device.Reads).Offset);
    }

    [Fact]
    public void Peripheral_read_value_is_truncated_to_byte_on_a_byte_read()
    {
        var space = NewSpace();
        var device = new RecordingPeripheral { NextReadValue = 0x1FF };
        space.MapPeripheral(0xD000, 0x100, device);

        Assert.Equal(0xFF, space.Read8(0xD000));
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~AddressSpacePeripheralTests"`
Expected: PASS (4 tests). If any fail, the bug is in `AddressSpace.cs` — fix it there; do not weaken a test.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests
git commit -m "test: pin MMIO dispatch contract (offset translation, width, truncation)"
```

---

### Task 5: AddressSpace — open bus, strict mode, mapping validation

**Files:**
- Test: `tests/CpuEmulator.Tests/AddressSpacePolicyTests.cs`

(As with Task 4, implementation exists; these tests pin the spec-§7 policy surface. Any failure is an `AddressSpace.cs` bug.)

- [ ] **Step 1: Write the tests**

`tests/CpuEmulator.Tests/AddressSpacePolicyTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class AddressSpacePolicyTests
{
    private static AddressSpace NewSpace(AddressSpaceOptions? options = null) =>
        new(AddressSpaceKind.Program, addressBits: 16, options);

    // --- open bus (guest-world behavior, never throws by default) ---

    [Fact]
    public void Unmapped_read_returns_default_open_bus_value()
    {
        Assert.Equal(0xFF, NewSpace().Read8(0x8000));
    }

    [Fact]
    public void Open_bus_value_is_configurable()
    {
        var space = NewSpace(new AddressSpaceOptions { OpenBusValue = 0x00 });
        Assert.Equal(0x00, space.Read8(0x8000));
    }

    [Fact]
    public void Unmapped_write_is_silently_ignored()
    {
        var space = NewSpace();
        space.Write8(0x8000, 0xAB); // must not throw
    }

    // --- strict mode (opt-in host-visible failures) ---

    [Fact]
    public void Strict_read_from_unmapped_address_throws()
    {
        var space = NewSpace(new AddressSpaceOptions { Strict = true });
        Assert.Throws<StrictBusViolationException>(() => space.Read8(0x8000));
    }

    [Fact]
    public void Strict_write_to_unmapped_address_throws()
    {
        var space = NewSpace(new AddressSpaceOptions { Strict = true });
        Assert.Throws<StrictBusViolationException>(() => space.Write8(0x8000, 0x01));
    }

    [Fact]
    public void Strict_write_to_rom_throws()
    {
        var space = NewSpace(new AddressSpaceOptions { Strict = true });
        space.MapMemory(0xFF00, new byte[0x100], writable: false);
        Assert.Throws<StrictBusViolationException>(() => space.Write8(0xFF00, 0x01));
    }

    // --- mapping validation (host-world configuration errors) ---

    [Fact]
    public void Misaligned_mapping_start_throws()
    {
        Assert.Throws<MachineConfigurationException>(
            () => NewSpace().MapMemory(0x0080, new byte[0x100], writable: true));
    }

    [Fact]
    public void Non_page_multiple_length_throws()
    {
        Assert.Throws<MachineConfigurationException>(
            () => NewSpace().MapMemory(0x0000, new byte[0x80], writable: true));
    }

    [Fact]
    public void Overlapping_mappings_throw()
    {
        var space = NewSpace();
        space.MapMemory(0x0000, new byte[0x200], writable: true);
        Assert.Throws<MachineConfigurationException>(
            () => space.MapPeripheral(0x0100, 0x100, new RecordingPeripheral()));
    }

    [Fact]
    public void Mapping_beyond_address_space_throws()
    {
        Assert.Throws<MachineConfigurationException>(
            () => NewSpace().MapMemory(0xFF00, new byte[0x200], writable: true));
    }

    [Fact]
    public void Address_bits_outside_8_to_24_throw()
    {
        Assert.Throws<MachineConfigurationException>(
            () => new AddressSpace(AddressSpaceKind.Program, addressBits: 25));
        Assert.Throws<MachineConfigurationException>(
            () => new AddressSpace(AddressSpaceKind.Program, addressBits: 7));
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~AddressSpacePolicyTests"`
Expected: PASS (11 tests). Fix `AddressSpace.cs` if not.

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests
git commit -m "test: pin open-bus, strict-mode, and mapping-validation policies"
```

> **Post-review amendments (applied after Tasks 3–5):** Write8-wrap and zero-length-peripheral tests added; MapMemory/MapPeripheral gained null guards and validate-then-commit atomic mapping (`EnsureRangeUnmapped` replaces per-page `EnsureUnmapped`); out-of-range message now computes its end address in ulong; IAddressSpace.Read8/Write8 document masking + open-bus/strict policy. Test count: 20 → 22.

---

### Task 6: CycleScheduler

**Files:**
- Create: `src/CpuEmulator.Core/CycleScheduler.cs`
- Test: `tests/CpuEmulator.Tests/CycleSchedulerTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/CpuEmulator.Tests/CycleSchedulerTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class CycleSchedulerTests
{
    [Fact]
    public void Events_fire_in_cycle_order_regardless_of_scheduling_order()
    {
        var scheduler = new CycleScheduler();
        var log = new List<string>();
        scheduler.ScheduleAt(20, () => log.Add("b"));
        scheduler.ScheduleAt(10, () => log.Add("a"));

        scheduler.AdvanceTo(100);

        Assert.Equal(["a", "b"], log);
    }

    [Fact]
    public void Event_at_exact_advance_boundary_fires()
    {
        var scheduler = new CycleScheduler();
        bool fired = false;
        scheduler.ScheduleAt(50, () => fired = true);

        scheduler.AdvanceTo(50);

        Assert.True(fired);
    }

    [Fact]
    public void Events_beyond_target_do_not_fire()
    {
        var scheduler = new CycleScheduler();
        bool fired = false;
        scheduler.ScheduleAt(51, () => fired = true);

        scheduler.AdvanceTo(50);

        Assert.False(fired);
        scheduler.AdvanceTo(51);
        Assert.True(fired);
    }

    [Fact]
    public void Callback_may_schedule_a_followup_within_the_same_advance()
    {
        var scheduler = new CycleScheduler();
        var log = new List<long>();
        scheduler.ScheduleAt(10, () =>
        {
            log.Add(scheduler.CurrentCycle);
            scheduler.ScheduleAt(20, () => log.Add(scheduler.CurrentCycle));
        });

        scheduler.AdvanceTo(100);

        Assert.Equal([10L, 20L], log);
    }

    [Fact]
    public void Scheduling_in_the_past_throws()
    {
        var scheduler = new CycleScheduler();
        scheduler.AdvanceTo(100);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => scheduler.ScheduleAt(99, () => { }));
    }

    [Fact]
    public void CurrentCycle_reaches_target_even_with_no_events()
    {
        var scheduler = new CycleScheduler();
        scheduler.AdvanceTo(42);
        Assert.Equal(42, scheduler.CurrentCycle);
    }

    [Fact]
    public void CurrentCycle_equals_event_cycle_inside_a_callback()
    {
        var scheduler = new CycleScheduler();
        long seen = -1;
        scheduler.ScheduleAt(10, () => seen = scheduler.CurrentCycle);

        scheduler.AdvanceTo(100);

        Assert.Equal(10, seen);
        Assert.Equal(100, scheduler.CurrentCycle);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CycleSchedulerTests"`
Expected: FAIL — `CS0246: 'CycleScheduler' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/CpuEmulator.Core/CycleScheduler.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>Minimal M1 scheduler: a cycle counter plus a priority-queue event list.</summary>
public sealed class CycleScheduler : IScheduler
{
    private readonly PriorityQueue<Action, long> _queue = new();

    public long CurrentCycle { get; private set; }

    public void ScheduleAt(long cycle, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, CurrentCycle);
        _queue.Enqueue(callback, cycle);
    }

    /// <summary>Advance time to <paramref name="cycle"/>, firing due callbacks in cycle order. Machine-driver only — not part of <see cref="IScheduler"/>.</summary>
    public void AdvanceTo(long cycle)
    {
        while (_queue.TryPeek(out _, out long due) && due <= cycle)
        {
            _queue.TryDequeue(out Action? callback, out long at);
            CurrentCycle = at;
            callback!();
        }
        if (cycle > CurrentCycle)
            CurrentCycle = cycle;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CycleSchedulerTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core tests/CpuEmulator.Tests
git commit -m "feat: add CycleScheduler with ordered event firing"
```

---

### Task 7: InterruptLine

**Files:**
- Create: `src/CpuEmulator.Core/InterruptLine.cs`
- Test: `tests/CpuEmulator.Tests/InterruptLineTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/CpuEmulator.Tests/InterruptLineTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class InterruptLineTests
{
    [Fact]
    public void Assert_forwards_true_to_target()
    {
        bool? seen = null;
        var line = new InterruptLine(v => seen = v);

        line.Assert();

        Assert.True(seen);
        Assert.True(line.IsAsserted);
    }

    [Fact]
    public void Release_forwards_false_to_target()
    {
        bool? seen = null;
        var line = new InterruptLine(v => seen = v);
        line.Assert();

        line.Release();

        Assert.False(seen);
        Assert.False(line.IsAsserted);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~InterruptLineTests"`
Expected: FAIL — `CS0246: 'InterruptLine' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/CpuEmulator.Core/InterruptLine.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// Forwards assert/release to a CPU line input. Single-source in M1; wired-OR sharing
/// between multiple devices arrives with the interrupt-controller milestone.
/// </summary>
public sealed class InterruptLine : IInterruptLine
{
    private readonly Action<bool> _setLine;

    public InterruptLine(Action<bool> setLine)
    {
        ArgumentNullException.ThrowIfNull(setLine);
        _setLine = setLine;
    }

    public bool IsAsserted { get; private set; }

    public void Assert()
    {
        IsAsserted = true;
        _setLine(true);
    }

    public void Release()
    {
        IsAsserted = false;
        _setLine(false);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~InterruptLineTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Core tests/CpuEmulator.Tests
git commit -m "feat: add InterruptLine forwarding to CPU line inputs"
```

> **Post-review amendments (applied after Tasks 6–7):** CycleScheduler gained a monotonic sequence tie-break — `PriorityQueue<Action, (long Cycle, ulong Seq)>` — so same-cycle events fire in FIFO scheduling order (PriorityQueue's equal-priority order is unspecified and can drift across .NET versions, which would break deterministic bus traces). ScheduleAt's `== CurrentCycle` boundary (allowed, fires on next/current advance) and AdvanceTo's no-op/exception semantics are now documented and pinned by 4 new scheduler tests; 2 InterruptLine edge tests (re-assert, release-without-assert) added. Test count: 31 → 37.

---

### Task 8: Machine + MachineBuilder — composition and lifecycle

**Files:**
- Create: `src/CpuEmulator.Core/Machine.cs`
- Create: `src/CpuEmulator.Core/MachineBuilder.cs`
- Create: `tests/CpuEmulator.Tests/TestDoubles/FakeCpu.cs`
- Test: `tests/CpuEmulator.Tests/MachineBuilderTests.cs`

- [ ] **Step 1: Write the test double**

`tests/CpuEmulator.Tests/TestDoubles/FakeCpu.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.TestDoubles;

/// <summary>An ICpuCore double that consumes its entire cycle budget on each Run call.</summary>
internal sealed class FakeCpu : ICpuCore
{
    public string Architecture => "fake";
    public long CycleCount { get; private set; }
    public int ResetCount { get; private set; }
    public bool IrqAsserted { get; private set; }
    public bool NmiAsserted { get; private set; }
    public List<long> RunBudgets { get; } = [];

    public void Reset() => ResetCount++;

    public void Step() => CycleCount += 1;

    public void Run(ref long cycleBudget)
    {
        RunBudgets.Add(cycleBudget);
        CycleCount += cycleBudget;
        cycleBudget = 0;
    }

    public void SetIrqLine(bool asserted) => IrqAsserted = asserted;
    public void SetNmiLine(bool asserted) => NmiAsserted = asserted;

    public IReadOnlyList<string> RegisterNames => ["PC"];
    public ulong GetRegister(string name) => 0;
    public void SetRegister(string name, ulong value) { }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/CpuEmulator.Tests/MachineBuilderTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class MachineBuilderTests
{
    private static MachineBuilder MinimalBuilder() =>
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => new FakeCpu());

    [Fact]
    public void Build_requires_a_cpu()
    {
        var builder = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16);

        Assert.Throws<MachineConfigurationException>(() => builder.Build());
    }

    [Fact]
    public void Build_requires_a_program_space()
    {
        var builder = Machine.Create("test").WithCpu(_ => new FakeCpu());

        Assert.Throws<MachineConfigurationException>(() => builder.Build());
    }

    [Fact]
    public void Build_may_only_be_called_once()
    {
        var builder = MinimalBuilder();
        builder.Build();

        Assert.Throws<MachineConfigurationException>(() => builder.Build());
    }

    [Fact]
    public void Duplicate_space_declaration_throws()
    {
        Assert.Throws<MachineConfigurationException>(() =>
            Machine.Create("test")
                .WithAddressSpace(AddressSpaceKind.Program, 16)
                .WithAddressSpace(AddressSpaceKind.Program, 16));
    }

    [Fact]
    public void Cpu_factory_receives_context_with_memory_already_mapped()
    {
        var rom = new byte[0x100];
        rom[0] = 0xEA;
        IAddressSpace? seenSpace = null;

        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRom(AddressSpaceKind.Program, 0xFF00, rom)
            .WithCpu(ctx => { seenSpace = ctx.Space(AddressSpaceKind.Program); return new FakeCpu(); })
            .Build();

        Assert.NotNull(seenSpace);
        Assert.Equal(0xEA, seenSpace.Read8(0xFF00));
    }

    [Fact]
    public void Ram_and_rom_are_mapped_with_correct_writability()
    {
        var rom = new byte[0x100];
        rom[0] = 0x42;
        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x1000)
            .WithRom(AddressSpaceKind.Program, 0xFF00, rom)
            .WithCpu(_ => new FakeCpu())
            .Build();

        var space = machine.Space(AddressSpaceKind.Program);
        space.Write8(0x0010, 0x55);
        Assert.Equal(0x55, space.Read8(0x0010)); // RAM is writable
        space.Write8(0xFF00, 0x00);
        Assert.Equal(0x42, space.Read8(0xFF00)); // ROM is not
    }

    [Fact]
    public void Peripherals_are_mapped_then_realized_in_registration_order()
    {
        var log = new List<string>();
        var first = new RecordingPeripheral { Name = "first", RealizeLog = log };
        var second = new RecordingPeripheral { Name = "second", RealizeLog = log };

        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x100, first)
            .WithPeripheral(AddressSpaceKind.Program, 0xD100, 0x100, second)
            .WithCpu(_ => new FakeCpu())
            .Build();

        Assert.Equal(["first", "second"], log);
        Assert.Equal(1, first.RealizeCount);
        Assert.Same(machine, first.RealizedWith); // context IS the machine

        first.NextReadValue = 0x77;
        Assert.Equal(0x77, machine.Space(AddressSpaceKind.Program).Read8(0xD000));
    }

    [Fact]
    public void Irq_line_asserted_by_a_peripheral_reaches_the_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.IrqLine.Assert();
        Assert.True(cpu.IrqAsserted);
        machine.IrqLine.Release();
        Assert.False(cpu.IrqAsserted);
    }

    [Fact]
    public void Nmi_line_asserted_by_a_peripheral_reaches_the_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.NmiLine.Assert();
        Assert.True(cpu.NmiAsserted);
    }

    [Fact]
    public void Irq_asserted_during_cpu_construction_is_replayed_at_bind()
    {
        FakeCpu? cpu = null;
        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(ctx => { ctx.IrqLine.Assert(); cpu = new FakeCpu(); return cpu; })
            .Build();

        Assert.True(cpu!.IrqAsserted);
        Assert.True(machine.IrqLine.IsAsserted);
    }

    [Fact]
    public void Space_lookup_for_undeclared_kind_throws()
    {
        var machine = MinimalBuilder().Build();

        Assert.Throws<MachineConfigurationException>(
            () => machine.Space(AddressSpaceKind.Io));
    }

    [Fact]
    public void Reset_resets_the_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Reset();

        Assert.Equal(1, cpu.ResetCount);
    }

    private static Machine MachineWith(FakeCpu cpu) =>
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => cpu)
            .Build();
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MachineBuilderTests"`
Expected: FAIL — `CS0246: 'Machine' could not be found`.

- [ ] **Step 4: Write the implementation**

`src/CpuEmulator.Core/MachineBuilder.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>Fluent composition of a machine: declare spaces, memory, CPU, peripherals; Build() wires it.</summary>
public sealed class MachineBuilder
{
    private readonly string _name;
    private readonly List<(AddressSpaceKind Kind, int AddressBits, AddressSpaceOptions? Options)> _spaceDefs = [];
    private readonly List<(AddressSpaceKind Kind, uint Start, byte[] Backing, bool Writable)> _memoryDefs = [];
    private readonly List<(AddressSpaceKind Kind, uint Start, uint Length, IPeripheral Peripheral)> _peripheralDefs = [];
    private Func<IMachineContext, ICpuCore>? _cpuFactory;
    private bool _built;

    internal MachineBuilder(string name) => _name = name;

    public MachineBuilder WithAddressSpace(AddressSpaceKind kind, int addressBits, AddressSpaceOptions? options = null)
    {
        if (_spaceDefs.Any(d => d.Kind == kind))
            throw new MachineConfigurationException($"Address space {kind} is declared twice.");
        _spaceDefs.Add((kind, addressBits, options));
        return this;
    }

    public MachineBuilder WithCpu(Func<IMachineContext, ICpuCore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _cpuFactory = factory;
        return this;
    }

    public MachineBuilder WithRam(AddressSpaceKind kind, uint start, uint length)
    {
        _memoryDefs.Add((kind, start, new byte[length], true));
        return this;
    }

    public MachineBuilder WithRom(AddressSpaceKind kind, uint start, byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _memoryDefs.Add((kind, start, image, false));
        return this;
    }

    public MachineBuilder WithPeripheral(AddressSpaceKind kind, uint start, uint length, IPeripheral peripheral)
    {
        ArgumentNullException.ThrowIfNull(peripheral);
        _peripheralDefs.Add((kind, start, length, peripheral));
        return this;
    }

    public Machine Build()
    {
        if (_built)
            throw new MachineConfigurationException("Build() may only be called once per builder.");
        _built = true;

        if (_cpuFactory is null)
            throw new MachineConfigurationException($"Machine '{_name}' has no CPU. Call WithCpu().");
        if (_spaceDefs.All(d => d.Kind != AddressSpaceKind.Program))
            throw new MachineConfigurationException($"Machine '{_name}' has no Program address space.");

        return new Machine(_name, _spaceDefs, _memoryDefs, _peripheralDefs, _cpuFactory);
    }
}
```

`src/CpuEmulator.Core/Machine.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// The container device (QOM-style): owns the CPU, address spaces, scheduler, and
/// peripherals. Construction is two-phase — all bus mappings exist before any
/// peripheral's Realize runs — and deterministic (registration order).
/// </summary>
public sealed class Machine : IMachineContext
{
    private readonly Dictionary<AddressSpaceKind, AddressSpace> _spaces = [];
    private readonly LateBoundLine _irqTarget = new();
    private readonly LateBoundLine _nmiTarget = new();
    private readonly CycleScheduler _scheduler;

    public string Name { get; }
    public ICpuCore Cpu { get; }
    public IScheduler Scheduler => _scheduler;
    public IInterruptLine IrqLine { get; }
    public IInterruptLine NmiLine { get; }

    public static MachineBuilder Create(string name) => new(name);

    internal Machine(
        string name,
        List<(AddressSpaceKind Kind, int AddressBits, AddressSpaceOptions? Options)> spaceDefs,
        List<(AddressSpaceKind Kind, uint Start, byte[] Backing, bool Writable)> memoryDefs,
        List<(AddressSpaceKind Kind, uint Start, uint Length, IPeripheral Peripheral)> peripheralDefs,
        Func<IMachineContext, ICpuCore> cpuFactory)
    {
        Name = name;
        _scheduler = new CycleScheduler();
        IrqLine = new InterruptLine(_irqTarget.Set);
        NmiLine = new InterruptLine(_nmiTarget.Set);

        // Phase 1: construct spaces and map memory.
        foreach (var (kind, addressBits, options) in spaceDefs)
            _spaces[kind] = new AddressSpace(kind, addressBits, options);
        foreach (var (kind, start, backing, writable) in memoryDefs)
            GetSpace(kind).MapMemory(start, backing, writable);

        // Phase 2: create the CPU (it may capture spaces), then bind interrupt lines to it.
        Cpu = cpuFactory(this);
        _irqTarget.Bind(Cpu.SetIrqLine);
        _nmiTarget.Bind(Cpu.SetNmiLine);

        // Phase 3: map peripherals, then Realize them in registration order.
        foreach (var (kind, start, length, peripheral) in peripheralDefs)
            GetSpace(kind).MapPeripheral(start, length, peripheral);
        foreach (var (_, _, _, peripheral) in peripheralDefs)
            peripheral.Realize(this);
    }

    public IAddressSpace Space(AddressSpaceKind kind) => GetSpace(kind);

    public void Reset() => Cpu.Reset();

    /// <summary>
    /// Run the machine for a cycle budget. M1 semantics are coarse: the CPU runs a slice,
    /// then the scheduler catches up to the CPU's cycle count. The timer milestone will
    /// chunk CPU slices to the next pending event for tighter event timing.
    /// </summary>
    public void Run(long cycles)
    {
        if (cycles <= 0)
            return;
        long target = Cpu.CycleCount + cycles;
        while (Cpu.CycleCount < target)
        {
            long before = Cpu.CycleCount;
            long budget = target - Cpu.CycleCount;
            Cpu.Run(ref budget);
            if (Cpu.CycleCount == before)
                throw new EmulationException(
                    $"CPU '{Cpu.Architecture}' made no progress during Run; aborting to avoid a hang.");
            _scheduler.AdvanceTo(Cpu.CycleCount);
        }
    }

    private AddressSpace GetSpace(AddressSpaceKind kind) =>
        _spaces.TryGetValue(kind, out var space)
            ? space
            : throw new MachineConfigurationException($"Machine '{Name}' has no {kind} address space.");

    /// <summary>Lets interrupt lines exist before the CPU does (the CPU factory may consult
    /// or even assert them). Binding replays the line's current state so an assert raised
    /// during CPU construction is not lost.</summary>
    private sealed class LateBoundLine
    {
        private Action<bool>? _target;
        private bool _lastValue;

        public void Set(bool value)
        {
            _lastValue = value;
            _target?.Invoke(value);
        }

        public void Bind(Action<bool> target)
        {
            _target = target;
            if (_lastValue)
                target(true);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MachineBuilderTests"`
Expected: PASS (12 tests).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Core tests/CpuEmulator.Tests
git commit -m "feat: add Machine container and fluent MachineBuilder with two-phase lifecycle"
```

> **Post-review amendment (applied at Task 8):** `LateBoundLine` gained bind-time state replay — `Bind(target)` invokes the target with `true` if the line was asserted before the CPU existed (e.g. by the CPU factory itself), so no assert is lost in the construction window. Pinned by `Irq_asserted_during_cpu_construction_is_replayed_at_bind`.

---

### Task 9: Machine.Run — cycle budget, scheduler advance, no-progress guard

**Files:**
- Create: `tests/CpuEmulator.Tests/TestDoubles/StuckCpu.cs`
- Test: `tests/CpuEmulator.Tests/MachineRunTests.cs`

(Run-loop implementation landed in Task 8; these tests pin its semantics.)

- [ ] **Step 1: Write the test double**

`tests/CpuEmulator.Tests/TestDoubles/StuckCpu.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.TestDoubles;

/// <summary>An ICpuCore double that never makes progress — exercises the run-loop guard.</summary>
internal sealed class StuckCpu : ICpuCore
{
    public string Architecture => "stuck";
    public long CycleCount => 0;
    public void Reset() { }
    public void Step() { }
    public void Run(ref long cycleBudget) { /* consumes nothing, advances nothing */ }
    public void SetIrqLine(bool asserted) { }
    public void SetNmiLine(bool asserted) { }
    public IReadOnlyList<string> RegisterNames => [];
    public ulong GetRegister(string name) => 0;
    public void SetRegister(string name, ulong value) { }
}
```

- [ ] **Step 2: Write the tests**

`tests/CpuEmulator.Tests/MachineRunTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class MachineRunTests
{
    private static Machine MachineWith(ICpuCore cpu) =>
        Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => cpu)
            .Build();

    [Fact]
    public void Run_passes_the_full_budget_to_the_cpu()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(100);

        Assert.Equal([100L], cpu.RunBudgets);
        Assert.Equal(100, cpu.CycleCount);
    }

    [Fact]
    public void Run_advances_the_scheduler_to_the_cpu_cycle_count()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(100);

        Assert.Equal(100, machine.Scheduler.CurrentCycle);
    }

    [Fact]
    public void Run_fires_events_scheduled_within_the_budget()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);
        bool fired = false;
        machine.Scheduler.ScheduleAt(50, () => fired = true);

        machine.Run(100);

        Assert.True(fired);
    }

    [Fact]
    public void Consecutive_runs_accumulate_cycles()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(60);
        machine.Run(40);

        Assert.Equal(100, cpu.CycleCount);
        Assert.Equal(100, machine.Scheduler.CurrentCycle);
    }

    [Fact]
    public void Run_with_zero_or_negative_cycles_is_a_no_op()
    {
        var cpu = new FakeCpu();
        var machine = MachineWith(cpu);

        machine.Run(0);
        machine.Run(-5);

        Assert.Empty(cpu.RunBudgets);
    }

    [Fact]
    public void Run_with_a_stuck_cpu_throws_instead_of_hanging()
    {
        var machine = MachineWith(new StuckCpu());

        var ex = Assert.Throws<EmulationException>(() => machine.Run(100));
        Assert.Contains("no progress", ex.Message);
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~MachineRunTests"`
Expected: PASS (6 tests). Any failure is a `Machine.cs` bug — fix it there.

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests
git commit -m "test: pin Machine.Run budget, scheduler-advance, and no-progress semantics"
```

---

### Task 10: Full verification, push, PR

**Files:**
- Modify: `README.md` (add a short status section)

- [ ] **Step 1: Run the full suite with AOT analyzers**

Run: `dotnet build && dotnet test`
Expected: build succeeds with zero warnings (warnings are errors, and `IsAotCompatible` makes trim/AOT violations warnings); all ~47 tests pass.

- [ ] **Step 2: Update README.** Append to `README.md`:

```markdown
## Status

Milestone 1 in progress. `CpuEmulator.Core` (contracts: CPU, bus, peripherals,
scheduler, machine composition) is implemented and unit-tested. Next: the Roslyn
source generator and the 6502 spec.

- Design: `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`
- Research: `docs/research/emulation-framework-research.md`

Build and test: `dotnet test`
```

- [ ] **Step 3: Commit and push**

```bash
git add README.md
git commit -m "docs: note M1 chunk-1 status in README"
git push -u origin feat/m1-core-contracts
```

- [ ] **Step 4: Open the PR** (do not merge — the user approves first)

```bash
gh pr create --title "M1 chunk 1: Core contracts, bus, and machine skeleton" --body "$(cat <<'EOF'
## Summary
- `CpuEmulator.Core`: AOT-clean contract assembly (`IsAotCompatible=true`)
- Page-table `AddressSpace` (256-byte pages): RAM/ROM fast path, `IPeripheral` MMIO slow path, open-bus + opt-in strict mode
- `Machine` + fluent `MachineBuilder` with QOM-style two-phase lifecycle (map everything, then `Realize` in order)
- `CycleScheduler` (priority-queue event firing) and `InterruptLine` (device → CPU line)
- ~47 unit tests; no CPU implementation yet (test doubles) — the generated 6502 is chunk 2

## Spec
`docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md` §4, §7

## Test plan
- [x] `dotnet test` green (all tests)
- [x] Zero build warnings (warnings-as-errors + AOT analyzers)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL printed. Report it to the user and stop — merging is the user's call.

---

## Plan self-review (completed at write time)

- **Spec coverage (§4, §7):** `ICpuCore` ✓ (Task 2), `IAddressSpace` + multi-space kinds ✓ (Tasks 2–5), `IPeripheral` width-aware + two-phase lifecycle ✓ (Tasks 2, 4, 8), `Machine` container ✓ (Task 8), `IScheduler` minimal ✓ (Task 6), raw IRQ/NMI lines ✓ (Tasks 7–8), open-bus/strict policies ✓ (Task 5), host-vs-guest exception split ✓ (Tasks 2, 5). Deferred by design: undefined-opcode policy (chunk 2, lives with the CPU spec), wider bus reads (first 16-bit consumer), interrupt controller (timer milestone).
- **Placeholder scan:** every code step contains complete, literal code; no TBDs.
- **Type consistency:** `AccessWidth` flows `IPeripheral` ⇆ `AddressSpace` ⇆ `RecordingPeripheral` consistently; `MachineBuilder` tuple lists match `Machine`'s constructor signature; `InterruptLine(Action<bool>)` matches `LateBoundLine.Set`; `FakeCpu`/`StuckCpu` implement every `ICpuCore` member used in tests.
