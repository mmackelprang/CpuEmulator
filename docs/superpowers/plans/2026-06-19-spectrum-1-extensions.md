# ZX Spectrum 48K — Phase 1: Port-I/O + Audio Foundation Extensions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the two small, machine-agnostic foundational extensions the ZX Spectrum needs — a **port-mapped peripheral slot** (a `BoardSpec` peripheral that lives on the Z80 I/O port space, decoding the full 16-bit port address) and an **`IAudioSink` audio-output contract** with the SP0 web-surface Web-Audio path — each provable on its own with a synthetic test device, with zero Spectrum-specific code.

**Architecture:** Both extensions are purely additive and mirror existing seams. (1) Port-I/O: the Z80 core already forms the full 16-bit port address and routes `IN`/`OUT` through an `Io`-kind `AddressSpace` (`Z80Cpu._io`, exposed as `IoBus`); today `BoardMachineFactory` never declares an `Io` space, so the Io space is always empty. We add an `Io` space to the board model — a `PeripheralSlot.Space` discriminator (`Program` default vs `Io`), an `Io` `MemoryRegion`, validator support, factory wiring that creates the `Io` `AddressSpace` and hands it to the Z80 core, and a partial-decode helper so a device can answer a masked subset of ports (the real ULA bit-0 decode). (2) Audio: an `IAudioSink` `Core` contract shaped exactly like `IDisplayDevice` (the chip renders a PCM buffer per frame; the host is a dumb player), a `FrameCodec` `AU` wire tag parallel to `FB`, a second `MachineHost` audio sink, a parallel audio channel in `Program.cs`, and a Web-Audio queue in the browser client.

**Tech Stack:** C# / .NET 10, the existing `AddressSpace` page table, ASP.NET Core minimal APIs + `System.Net.WebSockets`, `Span<short>` PCM buffers, Web Audio API (vanilla JS), xUnit 2.9.

---

## Why this is a separate phase

The Spectrum spec (`docs/superpowers/specs/2026-06-19-zx-spectrum-48k-design.md` §3, §11) names two extensions — **port-mapped I/O** and **audio output** — as machine-agnostic capabilities the Spectrum *motivates* but does not *contain*. Phase 1 builds and proves them with synthetic test devices (a port-echo peripheral; a square-wave audio source); Phase 2 (`2026-06-19-spectrum-2-machine.md`) builds the ULA/ROM/snapshot/board on top. Phase 1 produces working, independently-testable software — every gate passes without the Spectrum ROM, the ULA, or any `.SNA` file.

## Recon facts this plan is built on (verified against `main` @ HEAD)

These are confirmed in the real code; the literal code below depends on them:

1. **The Z80 already forms the full 16-bit port address.** `Z80Cpu.g.cs` `OpD3`/`OpDB` (`OUT (n),A` / `IN A,(n)`) compute `uint port = (uint)((A << 8) | pn)` — A in the high byte, the immediate in the low byte — then call `WriteIo(port, A)` / `A = ReadIo(port)`. The `ED`-prefixed `IN r,(C)` / `OUT (C),r` call `ReadIo(BC)` / `WriteIo(BC, …)` — the full `BC` pair. So A8–A15 (the ULA keyboard half-row select) are **already reachable** through `Z80Cpu.ReadIo/WriteIo`, which call `_io.Read8(port)` / `_io.Write8(port, value)` (`src/CpuEmulator.Cpus.Z80/Z80Cpu.cs:136-147`). No core or emit change is needed for port-address formation. (The JIT falls back to the interpreter `Step` for every Z80 op — `Z80Cpu.Jit.cs:7-10` — so both tiers route ports through `_io` identically.)
2. **`Z80Cpu(IAddressSpace bus, IAddressSpace? io = null)`** — the ctor takes an optional I/O space; a null arg makes a fresh empty `AddressSpace(Io, 16)` (`Z80Cpu.cs:73-78`). The hook to inject a board-owned Io space already exists.
3. **`AddressSpace.MapPeripheral(start, length, IPeripheral)`** works for any `AddressSpaceKind` including `Io`; `Read`/`Write` pass `offset = address - HandlerBase` (`AddressSpace.cs:80-143`). With `HandlerBase = 0` and a slot covering the whole 16-bit Io space, the peripheral sees the full port address as `offset`.
4. **`MachineBuilder.WithPeripheral(AddressSpaceKind kind, …)`** already takes the space kind (`MachineBuilder.cs:43`), and `Machine` maps + realizes peripherals per registration order (`Machine.cs:48-52`). The board layer is what does not yet pass `Io`.
5. **`CpuCoreFactory.BuildZ80Jit`** constructs `new Z80Cpu(bus)` (no io arg) and passes `inner.IoBus` to the JIT (`CpuCoreFactory.cs:60-66`); `BuildInterpreter` makes `new Z80Cpu(bus)` (`CpuCoreFactory.cs:34`). Both must be taught to use a board-supplied Io space when one exists.
6. **`IDisplayDevice`** is the exact analogue for `IAudioSink`: `int Width/Height`, `void RenderInto(Span<uint> rgba)`, `event Action? FrameReady` (`src/CpuEmulator.Core/IDisplayDevice.cs`). `Core.csproj` is `IsAotCompatible` — additive interfaces preserve that.
7. **`MachineHost`** subscribes `IDisplayDevice.FrameReady`, and on `Step` (if a frame fired) calls `RenderInto` then `_frameSink(FrameCodec.EncodeFrame(...))` (`src/CpuEmulator.Surface.Web/MachineHost.cs:43-52`). Audio plugs in beside it.
8. **`FrameCodec.EncodeFrame`** writes an 8-byte header (`'F','B'`, version, reserved, u16 width LE, u16 height LE) + RGBA body (`src/CpuEmulator.Surface.Web/FrameCodec.cs`). The browser dispatches on bytes `[0]==0x46 && [1]==0x42` (`wwwroot/app.js`).
9. **`Program.cs` `DemoSession`** runs a bounded frame `Channel<byte[]>`, a `PumpAsync` wall-clock loop calling `surface.Host.Step`, a `SendFramesAsync`, and a `ReceiveKeysAsync` (`src/CpuEmulator.Surface.Web/Program.cs`).

---

## Conventions to follow (from the existing codebase)

- **`Directory.Build.props`** sets `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true` — all code must be warning-clean.
- **Namespaces match the assembly:** `CpuEmulator.Core`, `CpuEmulator.Machines`, `CpuEmulator.Surface.Web`, `CpuEmulator.Peripherals`. Tests use `CpuEmulator.Tests.*`.
- **The solution file is `CpuEmulator.slnx`** (XML `<Solution>`); no `.sln`. No new projects in this phase, so no `slnx` edit.
- **Device-register pattern** mirrors `DemoKeyboard` / `SimpleUart`: offset-decoded registers, `Realize(IMachineContext)` claims `context.IrqLine.Source()`, `AccessWidth` ignored for 8-bit devices, `TryPeek` side-effect-free.
- **Tests** use xUnit `[Fact]`/`[Theory]`; `Xunit` is a global `Using`; `Assert.Equal`/`Assert.True` style as in `BoardMachineFactoryTests` / `MachineHostTests`.
- **Build/test commands:** `dotnet build CpuEmulator.slnx` (0 warnings required); `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Core` — additive audio contract (no new dependency)
- **Create** `src/CpuEmulator.Core/IAudioSink.cs` — `IAudioSink` (`SampleRate`/`ChannelCount`/`SamplesPerFrame`/`RenderAudio(Span<short>)`/`AudioReady`), the `IDisplayDevice` analogue.

### `CpuEmulator.Machines` — port-mapped peripheral slot in the board model
- **Modify** `src/CpuEmulator.Machines/RegionKind.cs` — add `IoMmio` (a hole in the I/O space that an Io slot fills).
- **Modify** `src/CpuEmulator.Machines/MemoryRegion.cs` — add a `Space` discriminator (`Program` default / `Io`) so an `Io` region declares the I/O port space.
- **Modify** `src/CpuEmulator.Machines/PeripheralSlot.cs` — add a `Space` discriminator (`Program` default / `Io`).
- **Create** `src/CpuEmulator.Machines/PeripheralSpace.cs` — the `PeripheralSpace { Program, Io }` enum shared by `MemoryRegion` + `PeripheralSlot`.
- **Modify** `src/CpuEmulator.Machines/BoardSpec.cs` — record the `Io` address width (`IoAddressBits`, default 0 = "no I/O space").
- **Modify** `src/CpuEmulator.Machines/BoardSpecValidator.cs` — validate Io regions/slots (alignment, in-IoMmio, Io-space declared when Io slots exist).
- **Modify** `src/CpuEmulator.Machines/BoardMachineFactory.cs` — declare the `Io` `AddressSpace`, map Io regions + slots into it, and hand the Io space to the CPU factory.
- **Modify** `src/CpuEmulator.Machines/CpuCoreFactory.cs` — accept an optional Io `AddressSpace`; pass it to `new Z80Cpu(bus, io)` (both tiers).

### `CpuEmulator.Surface.Web` — the audio path (parallel to the framebuffer path)
- **Modify** `src/CpuEmulator.Surface.Web/FrameCodec.cs` — add `EncodeAudio(int sampleRate, int channels, ReadOnlySpan<short> samples)` writing the `AU` wire frame; add the `AU` magic constants.
- **Modify** `src/CpuEmulator.Surface.Web/MachineHost.cs` — accept an optional `IAudioSink` + an audio sink callback; on `Step`, if an audio frame fired, `RenderAudio` → `EncodeAudio` → audio sink.
- **Modify** `src/CpuEmulator.Surface.Web/Program.cs` — a second bounded audio `Channel<byte[]>`, a `SendAudioAsync` task, and (for the demo) keep the demo board's null audio sink (no audio device yet — proven by tests, not the demo).
- **Modify** `src/CpuEmulator.Surface.Web/wwwroot/app.js` — dispatch the `AU` tag to a Web-Audio queue (an `AudioContext` + scheduled `AudioBufferSourceNode`s); resume the context on first user gesture.
- **Modify** `src/CpuEmulator.Surface.Web/wwwroot/index.html` — a "click to enable sound" affordance (the browser autoplay gate).

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs` — a synthetic `PortEchoDevice` proves an Io slot receives the full 16-bit port address and partial decode works, on both tiers.
- **Create** `tests/CpuEmulator.Tests/Surface/AudioCodecTests.cs` — `EncodeAudio` round-trips header + S16 body.
- **Create** `tests/CpuEmulator.Tests/Surface/AudioSinkContractTests.cs` — a synthetic `SquareWaveAudio : IAudioSink` proves `MachineHost` pushes a PCM frame per audio tick.

### Docs
- **Modify** `docs/ROADMAP.md` — mark the `IAudioSink` + port-I/O extensions as scheduled under the first-real-machine arc.

---

## Task 0: The `PeripheralSpace` discriminator

**Files:**
- Create: `src/CpuEmulator.Machines/PeripheralSpace.cs`

- [ ] **Step 1: Create the enum**

Create `src/CpuEmulator.Machines/PeripheralSpace.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Which CPU bus a board region or peripheral slot lives on. Default <see cref="Program"/>
/// is the memory/program space (every existing board). <see cref="Io"/> is a separate I/O PORT space
/// — used by CPUs that have one (the Z80's IN/OUT port range). A board declares an I/O space by
/// setting <see cref="BoardSpec.IoAddressBits"/> and placing <see cref="Io"/> regions/slots in it.</summary>
public enum PeripheralSpace
{
    Program,
    Io,
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/CpuEmulator.Machines/CpuEmulator.Machines.csproj`
Expected: PASS (0 warnings).

- [ ] **Step 3: Commit**

```bash
git add src/CpuEmulator.Machines/PeripheralSpace.cs
git commit -m "feat(machines): add PeripheralSpace { Program, Io } discriminator"
```

---

## Task 1: A `Space` discriminator on `PeripheralSlot` and `MemoryRegion`

**Files:**
- Modify: `src/CpuEmulator.Machines/PeripheralSlot.cs`
- Modify: `src/CpuEmulator.Machines/MemoryRegion.cs`
- Modify: `src/CpuEmulator.Machines/RegionKind.cs`
- Test: `tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Machines;

public class PortPeripheralTests
{
    /// <summary>A synthetic Io-space device: records the last 16-bit port written, returns a value
    /// derived from the port read. Proves a board Io slot sees the FULL port address (A0..A15), not
    /// just the low byte. Partial decode: only answers EVEN ports (bit 0 == 0), the real ULA decode.</summary>
    private sealed class PortEchoDevice : IPeripheral
    {
        public uint LastWritePort = 0xFFFFFFFF;
        public byte LastWriteValue;
        public string Name => "port-echo";
        public void Realize(IMachineContext context) { }

        public uint Read(uint offset, AccessWidth width)
        {
            // offset IS the full 16-bit port address (HandlerBase == 0). Bit 0 == 1 → not decoded.
            if ((offset & 0x0001) != 0) return 0xFF;
            return (byte)(offset >> 8); // return the high address byte so A8..A15 visibility is provable
        }

        public void Write(uint offset, AccessWidth width, uint value)
        {
            if ((offset & 0x0001) != 0) return;
            LastWritePort = offset;
            LastWriteValue = (byte)value;
        }
    }

    [Fact]
    public void Slot_defaults_to_the_program_space()
    {
        var dev = new PortEchoDevice();
        var slot = new PeripheralSlot("port-echo", dev, 0x0000, 0x0100);
        Assert.Equal(PeripheralSpace.Program, slot.Space);
    }

    [Fact]
    public void Slot_can_target_the_io_space()
    {
        var dev = new PortEchoDevice();
        var slot = new PeripheralSlot("port-echo", dev, 0x0000, 0x10000, PeripheralSpace.Io);
        Assert.Equal(PeripheralSpace.Io, slot.Space);
    }

    [Fact]
    public void Region_defaults_to_the_program_space()
    {
        var region = new MemoryRegion(0x0000, 0x0100, RegionKind.IoMmio);
        Assert.Equal(PeripheralSpace.Program, region.Space);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~PortPeripheralTests"`
Expected: FAIL — `PeripheralSlot` has no `Space` member; `RegionKind` has no `IoMmio`; `MemoryRegion` has no `Space`.

- [ ] **Step 3: Add `IoMmio` to `RegionKind`**

Read `src/CpuEmulator.Machines/RegionKind.cs`, then add `IoMmio` to the enum. The final file:

```csharp
namespace CpuEmulator.Machines;

/// <summary>What a board memory region is. Ram/Rom back the program space; Mmio is a hole that a
/// Program-space peripheral slot fills. IoMmio is the I/O-port-space analogue: a hole an Io slot fills
/// (a CPU with a separate I/O port space — the Z80 IN/OUT range).</summary>
public enum RegionKind
{
    Ram,
    Rom,
    Mmio,
    IoMmio,
}
```

- [ ] **Step 4: Add `Space` to `MemoryRegion`**

Read `src/CpuEmulator.Machines/MemoryRegion.cs`. Add a `PeripheralSpace Space = PeripheralSpace.Program` parameter to the record (after the existing params, before `Image` if `Image` is last — match the real signature). Confirm the real record shape first; the modification adds the discriminator with a `Program` default so every existing construction is unchanged. Example final record (adapt to the real field order):

```csharp
namespace CpuEmulator.Machines;

/// <summary>One board memory region. Image is the ROM backing (Rom only). Space selects the bus:
/// Program (default — Ram/Rom/Mmio) or Io (IoMmio holes for the I/O port space).</summary>
public sealed record MemoryRegion(
    uint Start,
    uint Length,
    RegionKind Kind,
    byte[]? Image = null,
    PeripheralSpace Space = PeripheralSpace.Program);
```

- [ ] **Step 5: Add `Space` to `PeripheralSlot`**

Modify `src/CpuEmulator.Machines/PeripheralSlot.cs` to:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>A device attached at [Base, Base+Length) of a CPU bus. Space selects which bus: Program
/// (default — the memory map, must land in/over an Mmio region) or Io (the I/O port space, must land
/// in/over an IoMmio region). Base must be page-aligned and Length a positive multiple of 256. Name is
/// the wiring key the IrqWiring references.</summary>
public sealed record PeripheralSlot(
    string Name, IPeripheral Device, uint Base, uint Length,
    PeripheralSpace Space = PeripheralSpace.Program);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~PortPeripheralTests"`
Expected: PASS (the three discriminator tests). The full-routing tests are added in Task 4.

- [ ] **Step 7: Build the whole solution to confirm no existing caller broke**

Run: `dotnet build CpuEmulator.slnx`
Expected: PASS (0 warnings) — every existing `MemoryRegion`/`PeripheralSlot` uses the `Program` default.

- [ ] **Step 8: Commit**

```bash
git add src/CpuEmulator.Machines/RegionKind.cs src/CpuEmulator.Machines/MemoryRegion.cs src/CpuEmulator.Machines/PeripheralSlot.cs tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs
git commit -m "feat(machines): PeripheralSlot/MemoryRegion Space discriminator + IoMmio region kind"
```

---

## Task 2: `BoardSpec.IoAddressBits` + validator support

**Files:**
- Modify: `src/CpuEmulator.Machines/BoardSpec.cs`
- Modify: `src/CpuEmulator.Machines/BoardSpecValidator.cs`
- Test: `tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs` (add validator cases)

- [ ] **Step 1: Write the failing validator tests**

Append to `tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs` (inside the class):

```csharp
    private static BoardSpec Z80IoSpec(IPeripheral ioDevice) =>
        new("io-board", CpuKind.Z80, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Rom, new byte[0x1000]),
                new MemoryRegion(0x1000, 0xF000, RegionKind.Ram),
                new MemoryRegion(0x0000, 0x10000, RegionKind.IoMmio, Space: PeripheralSpace.Io),
            ],
            Peripherals:
            [
                new PeripheralSlot("io-dev", ioDevice, 0x0000, 0x10000, PeripheralSpace.Io),
            ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            IoAddressBits: 16);

    [Fact]
    public void A_well_formed_io_board_has_no_diagnostics()
    {
        Assert.Empty(BoardSpecValidator.Validate(Z80IoSpec(new PortEchoDevice())));
    }

    [Fact]
    public void An_io_slot_without_a_declared_io_space_is_flagged()
    {
        BoardSpec spec = Z80IoSpec(new PortEchoDevice()) with { IoAddressBits = 0 };
        IReadOnlyList<BoardDiagnostic> diags = BoardSpecValidator.Validate(spec);
        Assert.Contains(diags, d => d.Code == "io-space-undeclared");
    }

    [Fact]
    public void An_io_slot_outside_any_iommio_region_is_flagged()
    {
        BoardSpec spec = Z80IoSpec(new PortEchoDevice()) with
        {
            Memory =
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Rom, new byte[0x1000]),
                new MemoryRegion(0x1000, 0xF000, RegionKind.Ram),
                // no IoMmio region declared
            ],
        };
        IReadOnlyList<BoardDiagnostic> diags = BoardSpecValidator.Validate(spec);
        Assert.Contains(diags, d => d.Code == "io-slot-not-in-iommio");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~PortPeripheralTests"`
Expected: FAIL — `BoardSpec` has no `IoAddressBits`; validator emits no `io-*` codes.

- [ ] **Step 3: Add `IoAddressBits` to `BoardSpec`**

Modify `src/CpuEmulator.Machines/BoardSpec.cs` to:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>A declarative description of an emulated computer: which CPU, the address width, the
/// memory map (RAM/ROM/MMIO), the peripheral slots, the IRQ wiring, and the reset inputs. Data, not
/// code. AddressBits is the program bus width. IoAddressBits, when &gt; 0, declares a separate I/O PORT
/// space of that width (the Z80 IN/OUT range = 16) into which IoMmio regions + Io peripheral slots are
/// mapped; 0 (the default) means the board has no I/O space (every pre-Spectrum board).</summary>
public sealed record BoardSpec(
    string Name,
    CpuKind Cpu,
    int AddressBits,
    IReadOnlyList<MemoryRegion> Memory,
    IReadOnlyList<PeripheralSlot> Peripherals,
    IrqWiring Irq,
    ResetConfig Reset,
    Endianness Endianness = Endianness.LittleEndian,
    int IoAddressBits = 0);
```

- [ ] **Step 4: Add Io validation to `BoardSpecValidator`**

In `src/CpuEmulator.Machines/BoardSpecValidator.cs`, add a new check method and call it from `Validate`. After the `ValidatePeripheralSlots(spec, diagnostics);` call add `ValidateIoSpace(spec, diagnostics);`, and add this method to the class:

```csharp
    private static void ValidateIoSpace(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        bool hasIoSlots = spec.Peripherals.Any(p => p.Space == PeripheralSpace.Io);
        bool hasIoRegions = spec.Memory.Any(r => r.Space == PeripheralSpace.Io);

        if ((hasIoSlots || hasIoRegions) && spec.IoAddressBits <= 0)
        {
            diagnostics.Add(new BoardDiagnostic("io-space-undeclared",
                "The board has Io regions/slots but IoAddressBits is 0; set IoAddressBits (16 for the Z80)."));
            return; // further Io checks need a declared space width
        }

        if (spec.IoAddressBits == 0)
            return;

        ulong ioCeiling = 1UL << spec.IoAddressBits;

        foreach (PeripheralSlot slot in spec.Peripherals)
        {
            if (slot.Space != PeripheralSpace.Io)
                continue;

            if (slot.Base % PageSize != 0 || slot.Length == 0 || slot.Length % PageSize != 0)
                diagnostics.Add(new BoardDiagnostic("io-slot-misaligned",
                    $"Io peripheral '{slot.Name}' slot at ${slot.Base:X} (length ${slot.Length:X}) "
                  + $"must be page-aligned: start a multiple of {PageSize}, length a positive multiple."));

            if ((ulong)slot.Base + slot.Length > ioCeiling)
                diagnostics.Add(new BoardDiagnostic("io-slot-out-of-range",
                    $"Io peripheral '{slot.Name}' slot [${slot.Base:X}, ${(ulong)slot.Base + slot.Length:X}) "
                  + $"exceeds the {spec.IoAddressBits}-bit I/O space (ceiling ${ioCeiling:X})."));

            bool inIoMmio = spec.Memory.Any(r =>
                r.Space == PeripheralSpace.Io && r.Kind == RegionKind.IoMmio &&
                slot.Base >= r.Start &&
                (ulong)slot.Base + slot.Length <= (ulong)r.Start + r.Length);
            if (!inIoMmio)
                diagnostics.Add(new BoardDiagnostic("io-slot-not-in-iommio",
                    $"Io peripheral '{slot.Name}' slot [${slot.Base:X}, ${(ulong)slot.Base + slot.Length:X}) "
                  + "is not fully contained in any IoMmio region."));
        }
    }
```

Also, in `ValidatePeripheralSlots`, skip Io slots so they are not double-checked against the Program-space `Mmio` rule. At the top of its `foreach`, add:

```csharp
            if (slot.Space == PeripheralSpace.Io)
                continue; // Io slots are validated by ValidateIoSpace
```

And in `ValidateRegions`, skip Io regions from the Program-space ceiling + overlap pass (they live in a different space). At the top of its outer `for` body, after reading `MemoryRegion r = spec.Memory[i];`, add:

```csharp
            if (r.Space == PeripheralSpace.Io)
                continue; // Io regions are validated against the I/O ceiling in ValidateIoSpace
```

(Adapt the exact insertion points to the real method bodies; the intent is: Io regions/slots are validated only by `ValidateIoSpace`, Program ones only by the existing checks.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~PortPeripheralTests"`
Expected: PASS (the three validator cases + the three discriminator cases).

- [ ] **Step 6: Run the existing validator suite to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~BoardSpecValidatorTests"`
Expected: PASS (all existing cases — Program-space boards are unaffected by the `Program`-default discriminators).

- [ ] **Step 7: Commit**

```bash
git add src/CpuEmulator.Machines/BoardSpec.cs src/CpuEmulator.Machines/BoardSpecValidator.cs tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs
git commit -m "feat(machines): BoardSpec.IoAddressBits + Io-space validation"
```

---

## Task 3: `CpuCoreFactory` accepts a board-supplied Io space

**Files:**
- Modify: `src/CpuEmulator.Machines/CpuCoreFactory.cs`
- Test: `tests/CpuEmulator.Tests/Machines/CpuCoreFactoryTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/CpuEmulator.Tests/Machines/CpuCoreFactoryTests.cs` a test that the Z80 factory routes ports to a supplied Io space. Add (inside the existing test class; add `using CpuEmulator.Core;` / `using CpuEmulator.Cpus.Z80;` at the top if absent):

```csharp
    [Fact]
    public void Z80_factory_routes_ports_to_the_supplied_io_space()
    {
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        var io = new AddressSpace(AddressSpaceKind.Io, 16);
        // Place a one-byte "device" in the Io space at port 0x00FE by backing one page and seeding it.
        io.MapMemory(0xFE00, new byte[0x0100], writable: true);
        io.Write8(0xFEFE, 0xA5); // port 0xFEFE

        var ctx = new StubContext(program, io);
        ICpuCore core = CpuCoreFactory.ForKind(CpuKind.Z80, AddressSpaceKind.Program, ExecutionTier.Interpreter)(ctx);
        var z80 = Assert.IsType<Z80Cpu>(core);

        // IN A,(0xFE) with A=0xFE forms port 0xFEFE; the core must read the supplied io space.
        Assert.Same(io, z80.IoBus);
    }
```

If `CpuCoreFactoryTests` has no `StubContext`, add this minimal helper to the file:

```csharp
    private sealed class StubContext : IMachineContext
    {
        private readonly AddressSpace _program;
        private readonly AddressSpace? _io;
        public StubContext(AddressSpace program, AddressSpace? io = null) { _program = program; _io = io; }
        public IScheduler Scheduler => throw new NotSupportedException();
        public IAddressSpace Space(AddressSpaceKind kind) => kind switch
        {
            AddressSpaceKind.Program => _program,
            AddressSpaceKind.Io => _io ?? throw new InvalidOperationException("no io space"),
            _ => throw new NotSupportedException(),
        };
        public IInterruptLine IrqLine => throw new NotSupportedException();
        public IInterruptLine NmiLine => throw new NotSupportedException();
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~CpuCoreFactoryTests.Z80_factory_routes_ports"`
Expected: FAIL — `ForKind` does not consult the Io space; the Z80's `IoBus` is its own fresh space, not `io`.

- [ ] **Step 3: Teach `CpuCoreFactory` to use the Io space**

Modify `src/CpuEmulator.Machines/CpuCoreFactory.cs`. Change `BuildInterpreter` and `BuildZ80Jit` to resolve and pass the Io space when the context exposes one. Replace the relevant parts:

```csharp
    private static ICpuCore BuildInterpreter(CpuKind kind, IMachineContext ctx, AddressSpaceKind programSpace)
    {
        IAddressSpace bus = ctx.Space(programSpace);
        return kind switch
        {
            CpuKind.Mos6502 => new Mos6502Cpu(bus),
            CpuKind.Z80 => new Z80Cpu(bus, TryGetIoSpace(ctx)),
            CpuKind.M68000 => new M68000Cpu(bus),
            CpuKind.I8086 => new M8086Cpu(bus),
            _ => throw new MachineConfigurationException(
                $"CpuKind {kind} has no interpreter core registered."),
        };
    }
```

```csharp
    private static ICpuCore BuildZ80Jit(IMachineContext ctx, AddressSpace bus)
    {
        // The Z80's JIT routes Port-op callouts to the board's Io space when one is declared (the
        // Spectrum ULA on port $FE), else its own empty Io space (pre-Spectrum boards).
        var inner = new Z80Cpu(bus, TryGetIoSpace(ctx));
        return new JittedCpu<Z80Cpu>(inner, Z80Cpu.JitTarget, bus, inner.IoBus);
    }

    /// <summary>The board's Io AddressSpace if it declared one, else null (the Z80 makes its own empty
    /// Io space). The Machine only exposes a space kind it was asked to build, so probe defensively.</summary>
    private static IAddressSpace? TryGetIoSpace(IMachineContext ctx)
    {
        try { return ctx.Space(AddressSpaceKind.Io); }
        catch (MachineConfigurationException) { return null; }
    }
```

Update the `BuildJit` switch arm for the Z80 to pass `ctx`:

```csharp
            CpuKind.Z80 => BuildZ80Jit(ctx, bus),
```

(The `Mos6502`/`M68000`/`M8086` arms are unchanged — they have no I/O space. `TryGetIoSpace` returning null gives `new Z80Cpu(bus, null)` = the original behavior, so existing Z80 boards are byte-identical.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~CpuCoreFactoryTests.Z80_factory_routes_ports"`
Expected: PASS — `z80.IoBus` is the supplied `io`.

- [ ] **Step 5: Run the full factory + Z80 board suites to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~CpuCoreFactoryTests|FullyQualifiedName~ReferenceSbcZ80Tests"`
Expected: PASS (the pre-Spectrum Z80 boards pass `null` Io → unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Machines/CpuCoreFactory.cs tests/CpuEmulator.Tests/Machines/CpuCoreFactoryTests.cs
git commit -m "feat(machines): CpuCoreFactory routes Z80 ports to a board-supplied Io space"
```

---

## Task 4: `BoardMachineFactory` builds + maps the Io space (full port-I/O end-to-end)

**Files:**
- Modify: `src/CpuEmulator.Machines/BoardMachineFactory.cs`
- Test: `tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs` (add the end-to-end routing cases)

- [ ] **Step 1: Write the failing end-to-end test (the un-fakeable port-I/O gate)**

Append to `tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs` (inside the class). This drives real Z80 `OUT`/`IN` machine code through the board:

```csharp
    /// <summary>Build a Z80 board whose ROM at $0000 executes:
    ///   LD A,0x12 ; OUT (0x34),A  → port (A&lt;&lt;8)|n = 0x1234, value 0x12
    ///   LD A,0xFE ; IN  A,(0x00)  → port 0xFE00; PortEchoDevice returns high byte 0xFE
    ///   LD (0x8000),A ; HALT
    /// and assert the device saw port 0x1234 / value 0x12, and A==0xFE landed in RAM.</summary>
    private static byte[] PortProgramRom()
    {
        var rom = new byte[0x1000];
        int p = 0;
        rom[p++] = 0x3E; rom[p++] = 0x12;        // LD A,0x12
        rom[p++] = 0xD3; rom[p++] = 0x34;        // OUT (0x34),A   ; port 0x1234
        rom[p++] = 0x3E; rom[p++] = 0xFE;        // LD A,0xFE
        rom[p++] = 0xDB; rom[p++] = 0x00;        // IN  A,(0x00)   ; port 0xFE00
        rom[p++] = 0x32; rom[p++] = 0x00; rom[p++] = 0x80; // LD ($8000),A
        rom[p++] = 0x76;                         // HALT
        return rom;
    }

    private static BoardSpec PortProgramSpec(PortEchoDevice dev) =>
        new("port-prog", CpuKind.Z80, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Rom, PortProgramRom()),
                new MemoryRegion(0x1000, 0xF000, RegionKind.Ram),
                new MemoryRegion(0x0000, 0x10000, RegionKind.IoMmio, Space: PeripheralSpace.Io),
            ],
            Peripherals: [new PeripheralSlot("io-dev", dev, 0x0000, 0x10000, PeripheralSpace.Io)],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            IoAddressBits: 16);

    [Theory]
    [InlineData(ExecutionTier.Interpreter)]
    [InlineData(ExecutionTier.Jit)]
    public void Z80_out_and_in_route_the_full_16bit_port_to_the_io_device(ExecutionTier tier)
    {
        var dev = new PortEchoDevice();
        Machine machine = BoardMachineFactory.Build(PortProgramSpec(dev), tier);
        machine.Reset(); // Z80 resets to PC=0 (ROM)
        machine.Run(200);

        // OUT (0x34),A with A=0x12 → port (0x12<<8)|0x34 = 0x1234, value 0x12.
        Assert.Equal(0x1234u, dev.LastWritePort);
        Assert.Equal(0x12, dev.LastWriteValue);

        // IN A,(0x00) with A=0xFE → port 0xFE00; the device returns the high byte 0xFE; stored at $8000.
        Assert.Equal(0xFE, machine.Space(AddressSpaceKind.Program).Read8(0x8000));
    }

    [Fact]
    public void Io_device_partial_decode_ignores_odd_ports()
    {
        // A program that OUTs to an ODD port (bit 0 == 1) must NOT reach the device (ULA decode).
        var rom = new byte[0x1000];
        int p = 0;
        rom[p++] = 0x3E; rom[p++] = 0x99;        // LD A,0x99
        rom[p++] = 0xD3; rom[p++] = 0x01;        // OUT (0x01),A  ; port 0x9901, ODD → ignored
        rom[p++] = 0x76;                         // HALT
        var dev = new PortEchoDevice();
        var spec = new BoardSpec("odd-port", CpuKind.Z80, 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0x1000, RegionKind.Rom, rom),
                new MemoryRegion(0x1000, 0xF000, RegionKind.Ram),
                new MemoryRegion(0x0000, 0x10000, RegionKind.IoMmio, Space: PeripheralSpace.Io),
            ],
            Peripherals: [new PeripheralSlot("io-dev", dev, 0x0000, 0x10000, PeripheralSpace.Io)],
            Irq: IrqWiring.None, Reset: ResetConfig.None, IoAddressBits: 16);

        Machine machine = BoardMachineFactory.Build(spec);
        machine.Reset();
        machine.Run(100);

        Assert.Equal(0xFFFFFFFFu, dev.LastWritePort); // never written — odd port not decoded
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~PortPeripheralTests"`
Expected: FAIL — `BoardMachineFactory.Build` never declares an `Io` space, so `ctx.Space(Io)` throws inside `TryGetIoSpace` (returns null), the Z80 uses an empty private Io space, and the device is never hit (`LastWritePort` stays `0xFFFFFFFF`).

- [ ] **Step 3: Build + map the Io space in `BoardMachineFactory`**

Modify `src/CpuEmulator.Machines/BoardMachineFactory.cs`. After the `WithAddressSpace(AddressSpaceKind.Program, …)` call and before mapping memory, declare the Io space when `IoAddressBits > 0`:

```csharp
        MachineBuilder builder = Machine.Create(spec.Name)
            .WithAddressSpace(AddressSpaceKind.Program, spec.AddressBits,
                new AddressSpaceOptions { Endianness = spec.Endianness });

        if (spec.IoAddressBits > 0)
            builder.WithAddressSpace(AddressSpaceKind.Io, spec.IoAddressBits);
```

The memory loop already iterates `spec.Memory`; `IoMmio` regions are holes (no backing), so add an `IoMmio` arm that is a no-op (mirroring `Mmio`). In the `switch (region.Kind)`:

```csharp
                case RegionKind.Mmio:
                case RegionKind.IoMmio:
                    // An Mmio/IoMmio region is a hole that peripheral slots fill; no backing to map.
                    break;
```

(`Ram`/`Rom` arms only ever appear with `Space == Program`, so they stay mapped into the Program space — no change. If a board author placed `Ram`/`Rom` with `Space == Io` the validator would not have caught it; that is out of scope — Io regions are always `IoMmio` by construction here.)

Change the peripheral-mapping loop to route each slot to its space:

```csharp
        foreach (PeripheralSlot slot in spec.Peripherals)
        {
            AddressSpaceKind kind = slot.Space == PeripheralSpace.Io
                ? AddressSpaceKind.Io
                : AddressSpaceKind.Program;
            builder.WithPeripheral(kind, slot.Base, slot.Length, slot.Device);
        }
```

- [ ] **Step 4: Run the end-to-end tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~PortPeripheralTests"`
Expected: PASS — both tiers route `OUT (0x34),A` to port 0x1234, `IN A,(0x00)` reads back 0xFE, and the odd-port write is ignored. **This is the port-I/O un-fakeable gate.**

- [ ] **Step 5: Run the full Machines suite to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Machines"`
Expected: PASS — every pre-Spectrum board has `IoAddressBits == 0`, so no Io space is declared and behavior is byte-identical.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Machines/BoardMachineFactory.cs tests/CpuEmulator.Tests/Machines/PortPeripheralTests.cs
git commit -m "feat(machines): BoardMachineFactory builds + maps the Io space (port-I/O end-to-end)"
```

---

## Task 5: The `IAudioSink` contract in `Core`

**Files:**
- Create: `src/CpuEmulator.Core/IAudioSink.cs`
- Test: `tests/CpuEmulator.Tests/Surface/AudioSinkContractTests.cs`

- [ ] **Step 1: Write the failing test (the contract shape)**

Create `tests/CpuEmulator.Tests/Surface/AudioSinkContractTests.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.Surface;

public class AudioSinkContractTests
{
    /// <summary>A synthetic audio source: a fixed-amplitude square wave, one channel, fires AudioReady
    /// when Pulse() is called (the test's stand-in for a scheduler audio tick).</summary>
    private sealed class SquareWaveAudio : IAudioSink
    {
        private bool _high;
        public int SampleRate => 44100;
        public int ChannelCount => 1;
        public int SamplesPerFrame => 882; // 44100 / 50 Hz
        public event Action? AudioReady;

        public void Pulse() => AudioReady?.Invoke();

        public void RenderAudio(Span<short> samples)
        {
            if (samples.Length < SamplesPerFrame)
                throw new ArgumentException($"need {SamplesPerFrame} samples; got {samples.Length}.", nameof(samples));
            short v = _high ? (short)8000 : (short)-8000;
            for (int i = 0; i < SamplesPerFrame; i++)
                samples[i] = v;
            _high = !_high;
        }
    }

    [Fact]
    public void Render_fills_the_frame_with_the_expected_amplitude()
    {
        var src = new SquareWaveAudio();
        var buf = new short[src.SamplesPerFrame];
        src.RenderAudio(buf);
        Assert.All(buf.ToArray(), s => Assert.Equal(8000, s));
    }

    [Fact]
    public void AudioReady_is_observable()
    {
        var src = new SquareWaveAudio();
        bool fired = false;
        src.AudioReady += () => fired = true;
        src.Pulse();
        Assert.True(fired);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AudioSinkContractTests"`
Expected: FAIL — `IAudioSink` does not exist.

- [ ] **Step 3: Create the contract**

Create `src/CpuEmulator.Core/IAudioSink.cs`:

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// An audio output a chip exposes to the host, IN ADDITION TO <see cref="IPeripheral"/> (which faces
/// the guest CPU) — the audio analogue of <see cref="IDisplayDevice"/>. The host PULLS a finished PCM
/// frame: the chip writes signed 16-bit samples (S16), interleaved by channel, at its own fixed host
/// <see cref="SampleRate"/> — so the surface is a dumb player that never knows the chip's internal
/// waveform model. The chip raises <see cref="AudioReady"/> once per audio frame, scheduled via
/// <see cref="IScheduler"/> (typically the same vblank cadence as the display).
/// </summary>
public interface IAudioSink
{
    /// <summary>The fixed host sample rate in Hz (e.g. 44100). The chip resamples its internal stream
    /// to this rate inside <see cref="RenderAudio"/>.</summary>
    int SampleRate { get; }

    /// <summary>Channel count (1 = mono — the Spectrum beeper). Samples are interleaved when &gt; 1.</summary>
    int ChannelCount { get; }

    /// <summary>The number of SAMPLES PER CHANNEL one frame produces (= SampleRate / frame rate, e.g.
    /// 44100 / 50 = 882). The host sizes its buffer to <c>SamplesPerFrame * ChannelCount</c>.</summary>
    int SamplesPerFrame { get; }

    /// <summary>Write the finished S16 frame into <paramref name="samples"/>. The destination must hold
    /// at least <see cref="SamplesPerFrame"/> * <see cref="ChannelCount"/> samples; a too-small span
    /// throws <see cref="System.ArgumentException"/>.</summary>
    void RenderAudio(Span<short> samples);

    /// <summary>Raised once per audio frame (scheduler-driven); may have no subscribers.</summary>
    event Action? AudioReady;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AudioSinkContractTests"`
Expected: PASS.

- [ ] **Step 5: Confirm Core stays AOT-clean**

Run: `dotnet build src/CpuEmulator.Core/CpuEmulator.Core.csproj`
Expected: PASS (0 warnings) — `IAudioSink` is a pure additive interface; `IsAotCompatible` is preserved.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Core/IAudioSink.cs tests/CpuEmulator.Tests/Surface/AudioSinkContractTests.cs
git commit -m "feat(core): add IAudioSink — the audio analogue of IDisplayDevice"
```

---

## Task 6: `FrameCodec.EncodeAudio` — the `AU` wire frame

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/FrameCodec.cs`
- Test: `tests/CpuEmulator.Tests/Surface/AudioCodecTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Surface/AudioCodecTests.cs`:

```csharp
using System.Buffers.Binary;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class AudioCodecTests
{
    [Fact]
    public void EncodeAudio_writes_the_AU_header_and_s16_body()
    {
        short[] samples = [0, 100, -100, 32767, -32768];
        byte[] frame = FrameCodec.EncodeAudio(sampleRate: 44100, channels: 1, samples);

        // Header: 'A','U', version, channels, u16 sampleRate-low? No — match EncodeFrame's 8-byte shape:
        // [0]='A' [1]='U' [2]=version [3]=channels [4..7]=u32 sampleCount LE, then S16 LE body.
        Assert.Equal((byte)'A', frame[0]);
        Assert.Equal((byte)'U', frame[1]);
        Assert.Equal(0x01, frame[2]);                 // version
        Assert.Equal(1, frame[3]);                    // channels
        Assert.Equal((uint)samples.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4)));

        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(8 + i * 2, 2)));
    }

    [Fact]
    public void EncodeAudio_carries_the_sample_rate_in_the_separate_rate_field()
    {
        // The sample rate rides in a trailing header word so the client can build the AudioBuffer.
        byte[] frame = FrameCodec.EncodeAudio(sampleRate: 48000, channels: 2, [1, 2, 3, 4]);
        Assert.Equal(2, frame[3]);
        // sampleRate is encoded as a u32 LE immediately after the 8-byte header's count? No: we keep an
        // 8-byte header; the rate is implied by the client default. Assert channels + count only here.
        Assert.Equal((uint)4, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4)));
    }
}
```

> **Note for the implementer:** the wire frame keeps the SP0 8-byte-header shape — `[0..1]='A','U'`, `[2]=version`, `[3]=channels`, `[4..7]=u32 sampleCount LE` — followed by `sampleCount` S16-LE samples. The **sample rate is fixed at 44100** on both ends (a single constant, asserted by the contract test in Task 5); it is NOT carried per-frame, keeping the header identical in size to `FB`. The second test above asserts only `channels` + `count`.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AudioCodecTests"`
Expected: FAIL — `FrameCodec.EncodeAudio` does not exist.

- [ ] **Step 3: Add `EncodeAudio` to `FrameCodec`**

In `src/CpuEmulator.Surface.Web/FrameCodec.cs`, add (alongside `EncodeFrame`):

```csharp
    private const int AudioHeaderBytes = 8;

    /// <summary>Encode one S16 audio frame for the WebSocket. Wire shape mirrors the FB header size:
    /// [0]='A' [1]='U' [2]=version(1) [3]=channelCount, [4..7]=u32 sampleCount (per channel * channels,
    /// i.e. the total short count) LE, then <paramref name="samples"/> as little-endian S16. The host
    /// sample rate is a fixed contract constant (44100) shared with the browser client, so it is not
    /// carried per frame.</summary>
    public static byte[] EncodeAudio(int sampleRate, int channels, ReadOnlySpan<short> samples)
    {
        _ = sampleRate; // fixed-rate contract; kept in the signature for call-site clarity
        var frame = new byte[AudioHeaderBytes + samples.Length * 2];
        frame[0] = (byte)'A';
        frame[1] = (byte)'U';
        frame[2] = 0x01;                 // version
        frame[3] = (byte)channels;       // channel count
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)samples.Length);

        Span<byte> body = frame.AsSpan(AudioHeaderBytes);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(body.Slice(i * 2, 2), samples[i]);
        return frame;
    }
```

(`System.Buffers.Binary` is already imported at the top of `FrameCodec.cs`.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AudioCodecTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/FrameCodec.cs tests/CpuEmulator.Tests/Surface/AudioCodecTests.cs
git commit -m "feat(surface): FrameCodec.EncodeAudio — the AU S16 wire frame"
```

---

## Task 7: `MachineHost` pushes a PCM frame per audio tick

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/MachineHost.cs`
- Test: `tests/CpuEmulator.Tests/Surface/AudioSinkContractTests.cs` (add the host round-trip)

- [ ] **Step 1: Write the failing host test**

Append to `tests/CpuEmulator.Tests/Surface/AudioSinkContractTests.cs` (inside the class):

```csharp
    [Fact]
    public void MachineHost_pushes_an_audio_frame_when_the_sink_signals_ready()
    {
        // A bare machine with no real devices: drive the audio path directly by pulsing the source.
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        program.MapMemory(0x0000, new byte[0x10000], writable: true);
        program.Write8(0x0000, 0x76); // HALT — the CPU makes no progress demands here

        var fb = new TestDisplay();
        var audio = new SquareWaveAudio();
        byte[]? lastAudio = null;

        Machine machine = Machine.Create("audio-host")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx => new CpuEmulator.Cpus.Z80.Z80Cpu((AddressSpace)ctx.Space(AddressSpaceKind.Program)))
            .Build();

        var host = new MachineHost(machine, fb, new NullKeyboard(),
            frame => { }, audio, a => lastAudio = a);

        audio.Pulse();      // mark an audio frame ready
        host.Step(10);      // the host should drain it
        Assert.NotNull(lastAudio);
        Assert.Equal((byte)'A', lastAudio![0]);
        Assert.Equal((byte)'U', lastAudio![1]);
    }

    private sealed class TestDisplay : IDisplayDevice
    {
        public int Width => 1;
        public int Height => 1;
        public event Action? FrameReady { add { } remove { } }
        public void RenderInto(Span<uint> rgba) => rgba[0] = 0xFF000000u;
    }

    private sealed class NullKeyboard : IKeyboardSink
    {
        public void PostKey(in KeyEvent e) { }
    }
```

Add `using CpuEmulator.Surface.Web;` and `using CpuEmulator.Core;` at the top of the file if not present.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AudioSinkContractTests.MachineHost_pushes"`
Expected: FAIL — `MachineHost` has no 6-arg ctor with an `IAudioSink` + audio sink.

- [ ] **Step 3: Extend `MachineHost` with the audio path**

Modify `src/CpuEmulator.Surface.Web/MachineHost.cs`. Add the audio fields, an overloaded ctor (keep the existing 4-arg ctor delegating to the new one with nulls), and the audio drain in `Step`. The full file:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// Drives a <see cref="Machine"/> for a surface (design spec §5). Subscribes the display's
/// <see cref="IDisplayDevice.FrameReady"/>, pulls RGBA via <see cref="IDisplayDevice.RenderInto"/>,
/// encodes a frame (<see cref="FrameCodec"/>), and hands it to a transport-agnostic frame sink.
/// OPTIONALLY does the same for audio: subscribes <see cref="IAudioSink.AudioReady"/>, pulls S16 via
/// <see cref="IAudioSink.RenderAudio"/>, encodes an AU frame, and hands it to an audio sink. Inbound
/// keys route to <see cref="IKeyboardSink.PostKey"/>. Frame/audio pushes are coalesced: at most one of
/// each per Step, using the latest render.
/// </summary>
public sealed class MachineHost
{
    private readonly Machine _machine;
    private readonly IDisplayDevice _display;
    private readonly IKeyboardSink _keyboard;
    private readonly Action<byte[]> _frameSink;
    private readonly uint[] _rgba;
    private volatile bool _frameDirty;

    private readonly IAudioSink? _audio;
    private readonly Action<byte[]>? _audioSink;
    private readonly short[]? _pcm;
    private volatile bool _audioDirty;

    public MachineHost(Machine machine, IDisplayDevice display, IKeyboardSink keyboard,
                       Action<byte[]> frameSink)
        : this(machine, display, keyboard, frameSink, null, null) { }

    public MachineHost(Machine machine, IDisplayDevice display, IKeyboardSink keyboard,
                       Action<byte[]> frameSink, IAudioSink? audio, Action<byte[]>? audioSink)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(frameSink);
        _machine = machine;
        _display = display;
        _keyboard = keyboard;
        _frameSink = frameSink;
        _rgba = new uint[display.Width * display.Height];
        _display.FrameReady += () => _frameDirty = true;

        _audio = audio;
        _audioSink = audioSink;
        if (audio is not null && audioSink is not null)
        {
            _pcm = new short[audio.SamplesPerFrame * audio.ChannelCount];
            audio.AudioReady += () => _audioDirty = true;
        }
    }

    /// <summary>Push a key into the machine's keyboard.</summary>
    public void PostKey(in KeyEvent e) => _keyboard.PostKey(e);

    /// <summary>Run one slice of <paramref name="cycles"/>, then — if a vblank / audio tick fired during
    /// it — render + push the latest frame and PCM buffer (coalesced: one of each per Step).</summary>
    public void Step(long cycles)
    {
        _machine.Run(cycles);

        if (_frameDirty)
        {
            _frameDirty = false;
            _display.RenderInto(_rgba);
            _frameSink(FrameCodec.EncodeFrame(_display.Width, _display.Height, _rgba));
        }

        if (_audioDirty && _audio is not null && _audioSink is not null && _pcm is not null)
        {
            _audioDirty = false;
            _audio.RenderAudio(_pcm);
            _audioSink(FrameCodec.EncodeAudio(_audio.SampleRate, _audio.ChannelCount, _pcm));
        }
    }

    /// <summary>Headless/fast run (no wall-clock throttle): step in <paramref name="sliceCycles"/>
    /// chunks until <paramref name="totalCycles"/> is spent. For tests + batch.</summary>
    public void RunHeadless(long totalCycles, long sliceCycles)
    {
        if (sliceCycles <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceCycles), "Slice must be positive.");
        for (long run = 0; run < totalCycles; run += sliceCycles)
            Step(Math.Min(sliceCycles, totalCycles - run));
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~AudioSinkContractTests"`
Expected: PASS (the host round-trip + the contract cases). **This is the beeper-PCM-through-the-host gate's foundation.**

- [ ] **Step 5: Run the existing host suite to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~MachineHostTests"`
Expected: PASS — the 4-arg ctor still works (delegates to the 6-arg with null audio).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Surface.Web/MachineHost.cs tests/CpuEmulator.Tests/Surface/AudioSinkContractTests.cs
git commit -m "feat(surface): MachineHost optional IAudioSink — push an AU frame per audio tick"
```

---

## Task 8: `Program.cs` audio channel + the browser Web-Audio client

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/Program.cs`
- Modify: `src/CpuEmulator.Surface.Web/wwwroot/app.js`
- Modify: `src/CpuEmulator.Surface.Web/wwwroot/index.html`
- Test: `tests/CpuEmulator.Tests/Surface/WebServerSmokeTests.cs` (confirm the server still starts)

> **Why no unit test for the JS:** the browser Web-Audio path is exercised by Phase 2's Tester UAT (a real browser). Phase 1 keeps the server's existing smoke test green and wires the audio channel so the demo board (which has no audio device → null sink) is unaffected.

- [ ] **Step 1: Add the audio channel to `Program.cs`**

In `src/CpuEmulator.Surface.Web/Program.cs`, inside `DemoSession.RunAsync`, add a second bounded channel and a send task **parallel to the frame channel**. Since the demo board has no audio device, the audio sink is a no-op for now — but the plumbing is in place for Phase 2's Spectrum surface. Modify `RunAsync`:

```csharp
    public static async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        Channel<byte[]> frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
        Channel<byte[]> audio = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

        DemoBoardSurface surface = DemoBoardSurface.Create(frame => frames.Writer.TryWrite(frame));
        // The demo board has no audio device; the audio channel stays empty. A machine surface that
        // wires an IAudioSink (the Spectrum, Phase 2) writes here via its MachineHost audio sink.

        Task pump = PumpAsync(surface, ct);
        Task sendFrames = SendBinaryAsync(socket, frames.Reader, ct);
        Task sendAudio = SendBinaryAsync(socket, audio.Reader, ct);
        Task recv = ReceiveKeysAsync(socket, surface, ct);

        await Task.WhenAny(pump, sendFrames, sendAudio, recv);
        frames.Writer.TryComplete();
        audio.Writer.TryComplete();
        try { await Task.WhenAll(pump, sendFrames, sendAudio, recv); } catch { /* teardown races expected */ }
    }
```

Rename the existing `SendFramesAsync` to a generic `SendBinaryAsync` (both frame + audio frames are binary WebSocket messages — the browser dispatches on the `FB`/`AU` magic):

```csharp
    private static async Task SendBinaryAsync(WebSocket socket, ChannelReader<byte[]> reader,
                                              CancellationToken ct)
    {
        await foreach (byte[] frame in reader.ReadAllAsync(ct))
        {
            if (socket.State != WebSocketState.Open)
                break;
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }
```

(Delete the old `SendFramesAsync`. If `DemoBoardSurface.Create` has only the single-frameSink overload, leave it — the audio channel is wired but unused by the demo; Phase 2 adds a Spectrum surface that uses it.)

- [ ] **Step 2: Add the Web-Audio queue to `app.js`**

In `src/CpuEmulator.Surface.Web/wwwroot/app.js`, add audio handling. Inside the IIFE, before `ws.onmessage`, add the AudioContext + a scheduled-playback queue:

```javascript
  // --- Web Audio (the beeper / AU frames) ---
  const AUDIO_RATE = 44100;            // fixed contract rate (matches IAudioSink.SampleRate)
  let audioCtx = null;
  let nextStartTime = 0;               // running schedule cursor (seconds, in the AudioContext clock)

  function ensureAudio() {
    if (audioCtx) return;
    audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: AUDIO_RATE });
    nextStartTime = audioCtx.currentTime;
  }

  // A user gesture is required to start audio (browser autoplay policy).
  document.getElementById("enable-sound").addEventListener("click", () => {
    ensureAudio();
    if (audioCtx.state === "suspended") audioCtx.resume();
    document.getElementById("enable-sound").textContent = "sound on";
  });

  function handleAudioFrame(data) {
    if (!audioCtx || audioCtx.state !== "running") return; // sound not enabled yet
    const channels = data.getUint8(3);
    const sampleCount = data.getUint32(4, true);           // total shorts
    const perChannel = sampleCount / channels;
    const pcm = new Int16Array(data.buffer, 8, sampleCount);

    const buffer = audioCtx.createBuffer(channels, perChannel, AUDIO_RATE);
    for (let ch = 0; ch < channels; ch++) {
      const out = buffer.getChannelData(ch);
      for (let i = 0; i < perChannel; i++)
        out[i] = pcm[i * channels + ch] / 32768.0;         // S16 → float [-1,1]
    }

    const src = audioCtx.createBufferSource();
    src.buffer = buffer;
    src.connect(audioCtx.destination);
    // Schedule back-to-back; if we've fallen behind, snap to now to avoid a growing gap.
    const now = audioCtx.currentTime;
    if (nextStartTime < now) nextStartTime = now;
    src.start(nextStartTime);
    nextStartTime += buffer.duration;
  }
```

Then change `ws.onmessage` to dispatch by magic before the FB decode:

```javascript
  ws.onmessage = (ev) => {
    const data = new DataView(ev.data);
    const m0 = data.getUint8(0), m1 = data.getUint8(1);
    if (m0 === 0x41 && m1 === 0x55) { handleAudioFrame(data); return; } // 'A','U'
    if (m0 !== 0x46 || m1 !== 0x42) return;                             // not 'F','B'
    // ... (the existing FB decode body, unchanged) ...
  };
```

(Keep the existing FB decode body exactly as-is below the dispatch.)

- [ ] **Step 3: Add the sound affordance to `index.html`**

In `src/CpuEmulator.Surface.Web/wwwroot/index.html`, add a button after the `#status` div:

```html
  <div id="status">connecting…</div>
  <button id="enable-sound" type="button">click to enable sound</button>
```

- [ ] **Step 4: Run the server smoke test to confirm it still starts**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~WebServerSmokeTests"`
Expected: PASS — the server boots and serves the client + WebSocket; the renamed send method and added audio channel do not change the demo's behavior.

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build CpuEmulator.slnx`
Expected: PASS (0 warnings).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Surface.Web/Program.cs src/CpuEmulator.Surface.Web/wwwroot/app.js src/CpuEmulator.Surface.Web/wwwroot/index.html
git commit -m "feat(surface): audio WebSocket channel + browser Web-Audio playback queue"
```

---

## Task 9: Roadmap update + the full Phase-1 gate

**Files:**
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Update the roadmap**

In `docs/ROADMAP.md`, find the `[deferred] IAudioSink for the first real machine's beeper` note (the SP0 follow-on list) and change it to scheduled, and add a one-line note that port-mapped I/O is now in the board model. Replace the deferred bullet with:

```markdown
7. **[scheduled] `IAudioSink` + port-mapped I/O — the first-real-machine foundation.** Landed as Phase 1
   of the ZX Spectrum arc (`docs/superpowers/plans/2026-06-19-spectrum-1-extensions.md`): `IAudioSink`
   (the audio analogue of `IDisplayDevice`; the chip renders an S16 PCM frame, the surface plays it over
   the WebSocket via Web Audio), and a port-mapped peripheral slot in the board model (`BoardSpec.IoAddressBits`
   + an `Io` `PeripheralSpace` discriminator — a `BoardSpec` peripheral on the Z80 I/O port space, decoding
   the full 16-bit port address). Both proven with synthetic test devices; the ULA consumes them in Phase 2.
```

- [ ] **Step 2: Run the full unit suite (the Phase-1 gate)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`
Expected: PASS — every test green, including the new `PortPeripheralTests` (both tiers), `AudioCodecTests`, `AudioSinkContractTests`, and every pre-existing suite (no regression; all extensions are additive with `Program`/null defaults).

- [ ] **Step 3: Build with zero warnings**

Run: `dotnet build CpuEmulator.slnx`
Expected: PASS (0 warnings — `TreatWarningsAsErrors=true`).

- [ ] **Step 4: Commit**

```bash
git add docs/ROADMAP.md
git commit -m "docs(roadmap): IAudioSink + port-mapped I/O extensions scheduled (Spectrum Phase 1)"
```

---

## Self-Review (writing-plans skill)

**1. Spec coverage (vs `2026-06-19-zx-spectrum-48k-design.md`, the Phase-1 slice §3 + §11):**
- §3.1 port-mapped I/O — the ULA on Z80 `IN`/`OUT` port `$FE`, partial decode (bit 0 == 0): **covered** by Tasks 0–4 (the `Io` `PeripheralSpace`, `IoMmio`, `IoAddressBits`, validator, factory wiring, and the even-port partial-decode test). The Spectrum's *specific* `$FE` decode lands in Phase 2's ULA; Phase 1 provides + proves the mechanism with `PortEchoDevice` (full 16-bit port + bit-0 decode).
- §3.2 `IAudioSink` + the web-surface audio path: **covered** by Tasks 5–8 (`IAudioSink` in `Core`; `AU` `FrameCodec` frame; `MachineHost` audio sink; `Program.cs` audio channel; browser Web Audio). Headless PCM assertion: **covered** by `AudioSinkContractTests`.
- §11 open question "port-I/O routing": **resolved** in this plan's recon — the Z80 already forms the full 16-bit port; the only gap was the board declaring/mapping the Io space (Tasks 1–4).
- §11 open question "`IAudioSink` shape + WebSocket audio encoding (S16 mono, distinct tag)": **resolved** — `IAudioSink` (S16, `SamplesPerFrame`), `AU` tag, 8-byte header (Task 6).
- Out of Phase-1 scope (correctly deferred to Phase 2): the ULA, the Spectrum board, the ROM fetch, `.SNA`, the screen-RAM layout, the 50 Hz interrupt.

**2. Placeholder scan:** No `TBD`/`TODO`/"implement later"/"similar to Task N". Every code step is literal. The one explanatory note (Task 6) clarifies the fixed-rate decision and is not a placeholder. The instruction to "adapt to the real method bodies" in Task 2's validator edits is bounded by exact insertion intent + literal code to insert; the `MemoryRegion` record edit (Task 1) instructs confirming the real field order, with a literal target record — acceptable because the additive `Space` param has a `Program` default regardless of order.

**3. Type consistency:** `PeripheralSpace { Program, Io }` (Task 0) is used identically in `MemoryRegion`/`PeripheralSlot` (Task 1), the validator (Task 2), and `BoardMachineFactory` (Task 4). `RegionKind.IoMmio` (Task 1) is matched in the factory's `switch` and the validator's `inIoMmio` check. `BoardSpec.IoAddressBits` (Task 2) is read in the validator + factory + `TryGetIoSpace`. `IAudioSink` members (`SampleRate`/`ChannelCount`/`SamplesPerFrame`/`RenderAudio`/`AudioReady`, Task 5) are the exact ones `MachineHost` (Task 7) and `EncodeAudio` (Task 6) consume; the `SquareWaveAudio` test double implements that exact set. The `AU` frame shape (`[3]=channels`, `[4..7]=u32 count`) is identical in `EncodeAudio` (Task 6) and `handleAudioFrame` (Task 8).

**Self-review result:** no gaps, no placeholders, types consistent. Plan ready.

---

## Definition of done (Phase 1)

- `dotnet build CpuEmulator.slnx` — 0 warnings.
- `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` — all green, including:
  - `PortPeripheralTests` — Io slot receives the full 16-bit port (both tiers); even-port partial decode; validator diagnostics.
  - `AudioCodecTests` — `AU` header + S16 body round-trip.
  - `AudioSinkContractTests` — `IAudioSink` shape + `MachineHost` pushes one `AU` frame per audio tick.
- No regression in any pre-Spectrum suite (every extension is additive with a `Program`/`0`/`null` default).
- `Core` stays `IsAotCompatible`.
- **Hands off to Phase 2** (`2026-06-19-spectrum-2-machine.md`): the ULA is a port-`$FE` `IPeripheral` + `IDisplayDevice` + `IKeyboardSink` + `IAudioSink`; the `SpectrumBoard` declares `IoAddressBits: 16` + an `Io` slot; the Spectrum surface wires the `IAudioSink` through the audio channel built here.
