# Apple ][+ PR-B — The `Apple2Board` BoardSpec skeleton + the `Apple2Iou` soft-switch decoder

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lay the base Apple ][+ board down as a declarative `BoardSpec` (ADR 0014 Decision 1) — 48 KiB RAM `$0000–$BFFF`, the `$C000–$CFFF` I/O hole, the 12 KiB system ROM `$D000–$FFFF`, memory-mapped I/O only (`IoAddressBits: 0`) — and build the **`Apple2Iou`** soft-switch decoder (ADR 0014 Decision 2): one `IPeripheral` owning the `$C000` page, decoding every soft switch by `offset`, with the load-bearing ][+ correctness rule that the video / Language-Card / speaker switches toggle on **any access — read OR write** (the inverse of the IIe), while `TryPeek` (the debugger path) has **no** side effect. This PR establishes the decode seam every later Apple peripheral (video, keyboard, speaker, Language Card, Disk II) delegates through, plus the small **shared mutable video-mode state** the IOU writes and `Apple2Video` (PR-C) reads.

**Architecture:** `Apple2Board.Spec(systemRom, iou)` returns a `BoardSpec("apple2plus", CpuKind.Mos6502, AddressBits: 16, …)` mirroring `SpectrumBoard.Spec`, but on the **Program** bus (no I/O port space — `IoAddressBits` defaults to 0): a `RegionKind.Ram` `$0000–$BFFF`, a `RegionKind.Mmio` hole `$C000–$CFFF`, a `RegionKind.Rom` `$D000–$FFFF` carrying the 12 KiB image, and **one** `PeripheralSlot("iou", iou, 0xC000, 0x0100)` over the first page of the Mmio hole. `Apple2Iou : IPeripheral` maps the `$C000` page and decodes by `offset & 0xFF`: video-mode switches `$C050–$C057`, keyboard `$C000`/`$C010`, speaker `$C030`, and (in later PRs) forwards `$C080–$C08F` to the Language Card and `$C0E0–$C0EF` to Disk II. The any-access rule is a structural invariant: one private `ApplyAnyAccessSideEffect(offset)` is called from **both** `Read` and `Write`; `TryPeek` calls a parallel side-effect-free value path. The IOU holds a reference to a small mutable `Apple2VideoState` (mode/page flags + the speaker toggle + the keyboard latch) — the **same object** PR-C's `Apple2Video` reads, so a `$C057` HIRES access is visible to the next render with no plumbing.

**Tech Stack:** C# / .NET 10, the shipped `BoardSpec`/`BoardMachineFactory`/`BoardSpecValidator`/`CpuKind` (the 6502 board path is fully supported — `Breadboard6502` proves it), `IPeripheral` (`Read`/`Write`/`TryPeek`/`Realize`), `AccessWidth`, xUnit. **Depends on PR-A only transitively** (PR-B does not call `Remap`; the Language Card PR-E does). Namespaces: `CpuEmulator.Machines` (the board), `CpuEmulator.Peripherals` (the IOU + the shared state).

---

## Recon facts this plan is built on (verified against `main` @ HEAD)

1. **`BoardSpec`** (`src/CpuEmulator.Machines/BoardSpec.cs`) is `record BoardSpec(string Name, CpuKind Cpu, int AddressBits, IReadOnlyList<MemoryRegion> Memory, IReadOnlyList<PeripheralSlot> Peripherals, IrqWiring Irq, ResetConfig Reset, Endianness Endianness = LittleEndian, int IoAddressBits = 0)`. The Apple board sets `IoAddressBits = 0` (default) — memory-mapped I/O only, **no** `Io` `PeripheralSpace` (unlike the Spectrum's port `$FE` ULA).
2. **`MemoryRegion`** (`src/CpuEmulator.Machines/MemoryRegion.cs`) — `record MemoryRegion(uint Start, uint Length, RegionKind Kind, byte[]? Image = null, PeripheralSpace Space = PeripheralSpace.Program)`. `RegionKind` has `Ram`/`Rom`/`Mmio`/`IoMmio`. The Apple uses `Ram`, `Mmio` (the `$C000` hole — a `Program`-space hole), and `Rom`.
3. **`PeripheralSlot`** (`src/CpuEmulator.Machines/PeripheralSlot.cs`) — `record PeripheralSlot(string Name, IPeripheral Device, uint Base, uint Length, PeripheralSpace Space = PeripheralSpace.Program)`. The Apple's slot defaults to `Program` (no `Space:` arg needed). `Base` must be page-aligned (256 B) and `Length` a positive page-multiple; the slot must land in/over an `Mmio` region (`BoardSpecValidator` `slot-not-in-mmio`/`slot-misaligned`).
4. **`BoardSpecValidator`** already checks: region overlap, address-width fit, page alignment (start + length), slot-in-Mmio, IRQ-wired-to-a-real-peripheral, ROM-image size match, vector-patch-in-mapped-memory. The Apple board passes all of them as written (the `$C000` slot is page-aligned at `0xC000`, length `0x0100`, inside the `$C000` Mmio region).
5. **`BoardMachineFactory.Build(spec, tier)`** validates → applies vector patches → maps RAM/ROM, skips Mmio/IoMmio holes (peripheral slots fill them) → maps peripheral slots → `WithCpu(CpuCoreFactory.ForKind(spec.Cpu, Program, tier))` → `Build`. The 6502 path is fully supported.
6. **The Apple ][+ ROM carries its own `$FFFC/$FFFD` reset vector** (→ `$FA62`, research §9), so `Reset: ResetConfig.None` — no `VectorPatch` (the `Mos6502Cpu.Reset()` reads `$FFFC/$FFFD` from the mapped ROM, the shipped mechanic, exactly as the `Breadboard6502` demo ROM does).
7. **The base ][+ has no interrupt source** (Disk II is polled; research §8). So `Irq: IrqWiring.None`.
8. **`IPeripheral`** (`src/CpuEmulator.Core/IPeripheral.cs`) — `string Name`, `void Realize(IMachineContext)`, `uint Read(uint offset, AccessWidth)`, `void Write(uint offset, AccessWidth, uint value)`, and a default `bool TryPeek(uint offset, out byte value) => (value = 0, false)`. The IOU overrides `TryPeek` to return the would-be value **without** side effect. `offset` is relative to the mapping base (`$C000`), so `$C050` arrives as `offset == 0x50`.
9. **A 6502 `STA $C030` issues a dummy read before the write** (the NMOS read-modify-write bus pattern; ADR 0014 Decision 2 + research §3/§6). The 6502 core is cycle-exact, so the speaker's double-toggle on a write opcode emerges naturally **if** the IOU models the toggle at the bus-access level (each `Read`/`Write` call = one access = one toggle). This is asserted as a build-time verification item in Task 5 — verify the core actually issues the dummy read against the real `Mos6502Cpu`; if it does not for a given store opcode, the speaker test documents the gap (it does not change the IOU, which is correct by the bus contract).
10. **`AccessWidth`** is the access-width enum the bus passes (`AccessWidth.Byte` for every 6502 access — the 6502 is byte-only). The IOU ignores width.

---

## Conventions to follow

- **Warning-clean** (`TreatWarningsAsErrors=true`).
- **`BoardSpec`** mirrors `SpectrumBoard` (a `static` class with a `Spec(...)` factory; address constants as `const`).
- **Device pattern** mirrors `SpectrumUla` / `SimpleUart` — `IPeripheral` with internal sub-page `offset` decode (the `$C000` page granularity forces one decoder per ADR 0014 Decision 1(A)).
- **Shared state** is one small mutable class both the IOU (writer) and `Apple2Video` (reader, PR-C) hold a reference to (ADR 0014 Decision 3's writer/reader split) — not duplicated, not events.
- **TDD per task**, literal code, commit per task.
- **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "<name>"`.

---

## File Structure

### `CpuEmulator.Peripherals` — the shared video state + the IOU decoder
- **Create** `src/CpuEmulator.Peripherals/Apple2VideoState.cs` — the small mutable state the IOU writes + the video chip reads (mode/page flags, the keyboard latch, the speaker toggle count).
- **Create** `src/CpuEmulator.Peripherals/Apple2Iou.cs` — `IPeripheral` over the `$C000` page: any-access side effects (`Read`/`Write` share `ApplyAnyAccessSideEffect`), the bus-value reads, peek-free `TryPeek`.

### `CpuEmulator.Machines` — the board skeleton
- **Create** `src/CpuEmulator.Machines/Apple2Board.cs` — `Apple2Board.Spec(byte[] systemRom, Apple2Iou iou)` → the `BoardSpec`.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2VideoStateTests.cs` — the flag round-trips.
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2IouTests.cs` — the any-access toggle gate, the peek-free gate, the speaker double-toggle, the keyboard latch + strobe-clear.
- **Create** `tests/CpuEmulator.Tests/Apple2/Apple2BoardTests.cs` — the board validates + builds; the map points (RAM/ROM/IOU); the IOU reachable through the bus at `$C050`/`$C030`.

### Docs
- **Modify** `docs/BUILDER_QUEUE.md` — set row **B** to ✅; update the banner.

---

## Task 1: `Apple2VideoState` — the shared mutable mode/latch/speaker state

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2VideoState.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2VideoStateTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Apple2/Apple2VideoStateTests.cs`:

```csharp
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2VideoStateTests
{
    [Fact]
    public void Defaults_are_text_page1_full_lores()
    {
        var s = new Apple2VideoState();
        Assert.False(s.GraphicsOn);   // TXTSET default (text)
        Assert.False(s.Mixed);
        Assert.False(s.Page2);
        Assert.False(s.HiRes);
    }

    [Fact]
    public void Mode_flags_round_trip()
    {
        var s = new Apple2VideoState
        {
            GraphicsOn = true,
            Mixed = true,
            Page2 = true,
            HiRes = true,
        };
        Assert.True(s.GraphicsOn && s.Mixed && s.Page2 && s.HiRes);
    }

    [Fact]
    public void Keyboard_latch_holds_code_with_strobe_bit_and_clears()
    {
        var s = new Apple2VideoState();
        s.LatchKey(0x41);                       // 'A'
        Assert.Equal(0xC1, s.KeyboardByte);     // bit7 strobe set + 0x41
        s.ClearStrobe();
        Assert.Equal(0x41, s.KeyboardByte);     // strobe cleared, code retained
    }

    [Fact]
    public void Speaker_toggle_count_increments_per_access()
    {
        var s = new Apple2VideoState();
        Assert.Equal(0, s.SpeakerToggles);
        s.ToggleSpeaker();
        s.ToggleSpeaker();
        Assert.Equal(2, s.SpeakerToggles);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2VideoStateTests"`
Expected: FAIL — `Apple2VideoState` does not exist.

- [ ] **Step 3: Create the shared state**

Create `src/CpuEmulator.Peripherals/Apple2VideoState.cs`:

```csharp
namespace CpuEmulator.Peripherals;

/// <summary>The small mutable state the <see cref="Apple2Iou"/> WRITES (via the $C0xx soft-switch
/// decode) and the Apple2Video chip + Apple2Speaker READ — one object both hold a reference to (ADR
/// 0014 Decision 3's writer/reader split), so a $C057 HIRES access is visible to the next render with
/// no plumbing. Flags default to the ][+ power-on state: text, page 1, full (not mixed), lo-res.</summary>
public sealed class Apple2VideoState
{
    // --- Video mode (the $C050-$C057 soft switches) ---
    /// <summary>$C050 TXTCLR sets true (graphics on); $C051 TXTSET sets false (text).</summary>
    public bool GraphicsOn { get; set; }
    /// <summary>$C052 MIXCLR sets false (full); $C053 MIXSET sets true (mixed text+gfx).</summary>
    public bool Mixed { get; set; }
    /// <summary>$C054 LOWSCR sets false (page 1); $C055 HISCR sets true (page 2).</summary>
    public bool Page2 { get; set; }
    /// <summary>$C056 LORES sets false; $C057 HIRES sets true.</summary>
    public bool HiRes { get; set; }

    // --- Keyboard latch ($C000 read / $C010 strobe clear) ---
    private byte _keyCode;     // 7-bit code (no strobe)
    private bool _strobe;      // bit 7

    /// <summary>The $C000 read value: bit 7 = strobe (a key is waiting), bits 6-0 = the code.</summary>
    public byte KeyboardByte => (byte)((_strobe ? 0x80 : 0x00) | (_keyCode & 0x7F));

    /// <summary>Latch a 7-bit ][+ key code and raise the strobe (a key arrived).</summary>
    public void LatchKey(byte code) { _keyCode = (byte)(code & 0x7F); _strobe = true; }

    /// <summary>$C010: clear the strobe (the program acknowledged the key); the code is retained.</summary>
    public void ClearStrobe() => _strobe = false;

    // --- Speaker ($C030: any access toggles the 1-bit flip-flop) ---
    /// <summary>How many times the speaker flip-flop has toggled (the Apple2Speaker reads + resets
    /// this each frame to rebuild the 1-bit waveform). One increment per $C030 bus access.</summary>
    public long SpeakerToggles { get; private set; }
    public void ToggleSpeaker() => SpeakerToggles++;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2VideoStateTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2VideoState.cs tests/CpuEmulator.Tests/Apple2/Apple2VideoStateTests.cs
git commit -m "feat(peripherals): Apple2VideoState — the shared IOU/video mutable state"
```

---

## Task 2: `Apple2Iou` — the `$C000` decoder with any-access video-mode toggles

**Files:**
- Create: `src/CpuEmulator.Peripherals/Apple2Iou.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2IouTests.cs`

- [ ] **Step 1: Write the failing test (the any-access video toggle + the peek-free gate)**

Create `tests/CpuEmulator.Tests/Apple2/Apple2IouTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2IouTests
{
    private static (Apple2Iou iou, Apple2VideoState state) Build()
    {
        var state = new Apple2VideoState();
        return (new Apple2Iou(state), state);
    }

    [Fact]
    public void A_READ_of_C057_HIRES_turns_hires_on()
    {
        var (iou, state) = Build();
        Assert.False(state.HiRes);
        iou.Read(0x57, AccessWidth.Byte);   // offset 0x57 == $C057 HIRES, READ
        Assert.True(state.HiRes);
    }

    [Fact]
    public void A_WRITE_of_C056_LORES_turns_hires_off_identically()
    {
        var (iou, state) = Build();
        iou.Read(0x57, AccessWidth.Byte);   // HIRES on
        Assert.True(state.HiRes);
        iou.Write(0x56, AccessWidth.Byte, 0x00); // $C056 LORES, WRITE — same any-access toggle
        Assert.False(state.HiRes);
    }

    [Theory]
    [InlineData(0x50, nameof(Apple2VideoState.GraphicsOn), true)]   // TXTCLR -> graphics on
    [InlineData(0x51, nameof(Apple2VideoState.GraphicsOn), false)]  // TXTSET -> text
    [InlineData(0x52, nameof(Apple2VideoState.Mixed), false)]       // MIXCLR -> full
    [InlineData(0x53, nameof(Apple2VideoState.Mixed), true)]        // MIXSET -> mixed
    [InlineData(0x54, nameof(Apple2VideoState.Page2), false)]       // LOWSCR -> page1
    [InlineData(0x55, nameof(Apple2VideoState.Page2), true)]        // HISCR -> page2
    [InlineData(0x56, nameof(Apple2VideoState.HiRes), false)]       // LORES
    [InlineData(0x57, nameof(Apple2VideoState.HiRes), true)]        // HIRES
    public void Every_video_switch_sets_its_flag_on_any_access(int offset, string flag, bool expected)
    {
        var (iou, state) = Build();
        // Seed the opposite so the assertion is meaningful for the "false" cases.
        if (!expected)
            iou.Read((uint)(offset ^ 1), AccessWidth.Byte); // the paired ON switch first
        iou.Read((uint)offset, AccessWidth.Byte);
        bool actual = flag switch
        {
            nameof(Apple2VideoState.GraphicsOn) => state.GraphicsOn,
            nameof(Apple2VideoState.Mixed) => state.Mixed,
            nameof(Apple2VideoState.Page2) => state.Page2,
            nameof(Apple2VideoState.HiRes) => state.HiRes,
            _ => throw new ArgumentOutOfRangeException(nameof(flag)),
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryPeek_of_a_video_switch_has_NO_side_effect()
    {
        var (iou, state) = Build();
        Assert.False(state.HiRes);
        bool ok = iou.TryPeek(0x57, out _);   // the debugger looks at $C057
        Assert.True(ok);
        Assert.False(state.HiRes);            // ... and HIRES stays OFF (peek-free)
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2IouTests"`
Expected: FAIL — `Apple2Iou` does not exist.

- [ ] **Step 3: Create the IOU**

Create `src/CpuEmulator.Peripherals/Apple2Iou.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ I/O Unit: one IPeripheral owning the $C000 page ($C000-$C0FF) and decoding
/// every soft switch by offset (ADR 0014 Decision 2). The load-bearing ][+ rule: the video / speaker /
/// (later) Language-Card switches toggle on ANY access — read OR write (the inverse of the IIe). So
/// Read and Write both call the SAME ApplyAnyAccessSideEffect(offset); only the returned bus value
/// differs. TryPeek (the debugger's side-effect-free path) calls the parallel BusValue path and applies
/// NO side effect — a class of "the monitor changed the video mode by looking at it" bugs is structurally
/// impossible. The keyboard latch + speaker live in the shared Apple2VideoState the video/speaker chips
/// read. The Language Card ($C080-$C08F) and Disk II ($C0E0-$C0EF) are delegated in later PRs (E, F);
/// for now those offsets are inert open-bus.</summary>
public sealed class Apple2Iou : IPeripheral
{
    private readonly Apple2VideoState _state;

    public Apple2Iou(Apple2VideoState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public string Name => "iou";

    public void Realize(IMachineContext context) { /* no IRQ/schedule on the bare IOU */ }

    public uint Read(uint offset, AccessWidth width)
    {
        ApplyAnyAccessSideEffect(offset);
        return BusValue(offset);
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        ApplyAnyAccessSideEffect(offset);
        // Soft switches ignore the written value; the side effect is the access itself.
    }

    public bool TryPeek(uint offset, out byte value)
    {
        value = BusValue(offset);   // the would-be read value, with NO side effect
        return true;
    }

    /// <summary>The any-access (read OR write) side effects. The single source of truth both Read and
    /// Write call — and TryPeek deliberately does NOT.</summary>
    private void ApplyAnyAccessSideEffect(uint offset)
    {
        byte o = (byte)offset;
        switch (o)
        {
            // --- Video mode $C050-$C057 (any access toggles) ---
            case 0x50: _state.GraphicsOn = true; break;   // TXTCLR -> graphics
            case 0x51: _state.GraphicsOn = false; break;  // TXTSET -> text
            case 0x52: _state.Mixed = false; break;       // MIXCLR -> full
            case 0x53: _state.Mixed = true; break;        // MIXSET -> mixed
            case 0x54: _state.Page2 = false; break;       // LOWSCR -> page 1
            case 0x55: _state.Page2 = true; break;        // HISCR  -> page 2
            case 0x56: _state.HiRes = false; break;       // LORES
            case 0x57: _state.HiRes = true; break;        // HIRES

            // --- Keyboard ---
            case 0x10: _state.ClearStrobe(); break;       // $C010: clear the strobe

            // --- Speaker $C030 (any reference toggles the 1-bit flip-flop) ---
            case 0x30: _state.ToggleSpeaker(); break;

            // $C000 (keyboard read) has no side effect on access; the value is in BusValue.
            // $C080-$C08F (Language Card) and $C0E0-$C0EF (Disk II) are delegated in PR-E / PR-F.
            default: break;
        }
    }

    /// <summary>The bus value a READ (or a peek) returns for an offset, WITHOUT side effects.</summary>
    private byte BusValue(uint offset)
    {
        byte o = (byte)offset;
        return o switch
        {
            0x00 => _state.KeyboardByte,   // $C000: bit7 strobe + 7-bit code
            // Most soft switches float the bus; return open-bus high-ish. The ][+ commonly leaves the
            // data bus with the high byte of the last fetch; a stable 0x00 here is adequate until a
            // switch-read-value gate needs more (a build-time fidelity dial, ADR 0014 Decision 8).
            _ => 0x00,
        };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2IouTests"`
Expected: PASS. **This is the any-access toggle gate + the peek-free gate** — the ][+'s defining I/O quirk, correct by construction.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2Iou.cs tests/CpuEmulator.Tests/Apple2/Apple2IouTests.cs
git commit -m "feat(peripherals): Apple2Iou soft-switch decoder (any-access toggle, peek-free)"
```

---

## Task 3: The keyboard latch read + the speaker double-toggle (bus-access level)

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2IouTests.cs` (add cases)

- [ ] **Step 1: Write the failing tests**

Append to `Apple2IouTests`:

```csharp
    [Fact]
    public void C000_read_returns_the_latched_key_and_C010_clears_the_strobe()
    {
        var (iou, state) = Build();
        state.LatchKey(0x41);                            // host pushed 'A'
        uint c000 = iou.Read(0x00, AccessWidth.Byte);   // $C000
        Assert.Equal(0xC1u, c000);                       // bit7 strobe + 0x41

        iou.Read(0x10, AccessWidth.Byte);                // $C010 clears strobe (any access)
        Assert.Equal(0x41u, iou.Read(0x00, AccessWidth.Byte) & 0xFF); // strobe gone, code retained
    }

    [Fact]
    public void A_single_C030_access_toggles_the_speaker_once()
    {
        var (iou, state) = Build();
        iou.Read(0x30, AccessWidth.Byte);   // one access (e.g. LDA $C030)
        Assert.Equal(1, state.SpeakerToggles);
    }

    [Fact]
    public void A_write_opcodes_read_before_write_double_toggles_the_speaker()
    {
        // A 6502 STA $C030 issues a dummy READ then the WRITE at the bus — TWO accesses. Modelled at
        // the bus-access level, that is two ToggleSpeaker() calls. We simulate the bus pattern here
        // (the real core issues it; the cross-check against the live Mos6502Cpu is Task 5).
        var (iou, state) = Build();
        iou.Read(0x30, AccessWidth.Byte);                // the dummy read
        iou.Write(0x30, AccessWidth.Byte, 0x00);         // the store
        Assert.Equal(2, state.SpeakerToggles);           // double toggle, as on real hardware
    }
```

- [ ] **Step 2: Run them to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2IouTests"`
Expected: PASS — the IOU from Task 2 already implements the latch read + the per-access speaker toggle. **This is the keyboard-latch + speaker-toggle gate** at the IOU level.

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2IouTests.cs
git commit -m "test(apple2): IOU keyboard-latch read + bus-access-level speaker double-toggle"
```

---

## Task 4: `Apple2Board.Spec` — the BoardSpec skeleton

**Files:**
- Create: `src/CpuEmulator.Machines/Apple2Board.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2BoardTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CpuEmulator.Tests/Apple2/Apple2BoardTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class Apple2BoardTests
{
    // A 12 KiB "system ROM" whose reset vector $FFFC/$FFFD points into the ROM (a NOP-loop).
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];                 // $D000-$FFFF
        rom[0x0000] = 0xEA;                          // NOP at $D000
        rom[0x0001] = 0x4C; rom[0x0002] = 0x00; rom[0x0003] = 0xD0; // JMP $D000
        // RESET vector at $FFFC/$FFFD (offset 0x2FFC/0x2FFD) -> $D000.
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        return rom;
    }

    private static (BoardSpec spec, Apple2Iou iou, Apple2VideoState state) BuildSpec()
    {
        var state = new Apple2VideoState();
        var iou = new Apple2Iou(state);
        return (Apple2Board.Spec(SystemRom(), iou), iou, state);
    }

    [Fact]
    public void The_board_validates_with_no_diagnostics()
    {
        var (spec, _, _) = BuildSpec();
        Assert.Empty(BoardSpecValidator.Validate(spec));
    }

    [Fact]
    public void Build_maps_ram_the_C000_hole_and_the_system_rom()
    {
        var (spec, _, _) = BuildSpec();
        Machine m = BoardMachineFactory.Build(spec);
        var bus = m.Space(AddressSpaceKind.Program);

        Assert.IsType<Mos6502Cpu>(m.Cpu);
        bus.Write8(0x0000, 0x5A); Assert.Equal(0x5A, bus.Read8(0x0000)); // RAM low writable
        bus.Write8(0xBFFF, 0x3C); Assert.Equal(0x3C, bus.Read8(0xBFFF)); // RAM top writable
        Assert.Equal(0xEA, bus.Read8(0xD000));                            // ROM byte present
        bus.Write8(0xD000, 0xFF); Assert.Equal(0xEA, bus.Read8(0xD000));  // ROM read-only
    }

    [Fact]
    public void Reset_loads_PC_from_the_rom_vector()
    {
        var (spec, _, _) = BuildSpec();
        Machine m = BoardMachineFactory.Build(spec);
        m.Reset();
        Assert.Equal(0xD000u, m.Cpu.GetRegister("PC"));
    }

    [Fact]
    public void The_IOU_is_reachable_through_the_bus_at_C057_and_C030()
    {
        var (spec, _, state) = BuildSpec();
        Machine m = BoardMachineFactory.Build(spec);
        var bus = m.Space(AddressSpaceKind.Program);

        Assert.False(state.HiRes);
        _ = bus.Read8(0xC057);          // a bus read of $C057 routes to the IOU -> HIRES on
        Assert.True(state.HiRes);

        long before = state.SpeakerToggles;
        _ = bus.Read8(0xC030);          // $C030 toggles the speaker
        Assert.Equal(before + 1, state.SpeakerToggles);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2BoardTests"`
Expected: FAIL — `Apple2Board` does not exist.

- [ ] **Step 3: Create the board**

Create `src/CpuEmulator.Machines/Apple2Board.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>
/// The base Apple ][+ as a declarative <see cref="BoardSpec"/> (ADR 0014 Decision 1): a 6502 with
/// 48 KiB RAM $0000-$BFFF, the $C000-$CFFF I/O + slot band as an Mmio hole, and the 12 KiB system ROM
/// (Applesoft + Monitor) at $D000-$FFFF. Memory-mapped I/O only — IoAddressBits stays 0 (no Z80-style
/// port space; the Apple's I/O is at $C0xx on the Program bus). The Apple2Iou soft-switch decoder owns
/// the $C000 page (any-access toggle, peek-free, ADR 0014 Decision 2). The system ROM carries its own
/// $FFFC/$FFFD reset vector (-> $FA62), so ResetConfig.None. The bare ][+ has no interrupt source
/// (Disk II is polled), so IrqWiring.None. Later PRs add the video/keyboard/speaker chips (C/D), the
/// Language Card ports (E), and Disk II (F); they delegate through this same IOU / fill the same hole.
/// </summary>
public static class Apple2Board
{
    public const uint RamBase = 0x0000;
    public const uint RamLength = 0xC000;   // 48 KiB $0000-$BFFF
    public const uint IoBase = 0xC000;
    public const uint IoLength = 0x1000;    // $C000-$CFFF (the soft-switch + slot band)
    public const uint RomBase = 0xD000;
    public const uint RomLength = 0x3000;   // 12 KiB $D000-$FFFF
    public const uint IouBase = 0xC000;
    public const uint IouLength = 0x0100;   // the $C000 page

    public static BoardSpec Spec(byte[] systemRom, Apple2Iou iou)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        if (systemRom.Length != RomLength)
            throw new ArgumentException(
                $"Apple ][+ system ROM must be exactly ${RomLength:X} bytes; got ${systemRom.Length:X}.",
                nameof(systemRom));

        return new BoardSpec("apple2plus", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),
                new MemoryRegion(IoBase, IoLength, RegionKind.Mmio),       // the $C000-$CFFF hole
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, systemRom),
            ],
            Peripherals:
            [
                new PeripheralSlot("iou", iou, IouBase, IouLength),        // the $C000 page decoder
            ],
            Irq: IrqWiring.None,        // the bare ][+ has no interrupt source (Disk II is polled)
            Reset: ResetConfig.None);   // the system ROM carries its own $FFFC/$FFFD vector
        // IoAddressBits defaults to 0: memory-mapped I/O only (no port space).
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2BoardTests"`
Expected: PASS. **This is the board-skeleton gate** — validates, builds, RAM/ROM/hole mapped, reset-vector honored, the IOU reachable through the bus.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/Apple2Board.cs tests/CpuEmulator.Tests/Apple2/Apple2BoardTests.cs
git commit -m "feat(machines): Apple2Board BoardSpec skeleton (RAM + $C000 hole + system ROM + IOU)"
```

---

## Task 5: The speaker double-toggle verification against the live 6502 core (build-time item)

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/Apple2BoardTests.cs` (add one integration case)

This closes ADR 0014 Decision 2's recorded **build-time verification item**: that a real `STA $C030` issues the dummy read so the speaker double-toggles via the bus-access-level model — *without* any IOU change (the IOU is correct by the bus contract; this verifies the core supplies the access pattern).

- [ ] **Step 1: Write the integration test**

Append to `Apple2BoardTests`:

```csharp
    [Fact]
    public void A_real_STA_C030_double_toggles_the_speaker_via_the_bus()
    {
        // Build a board whose RAM at $0300 holds: STA $C030 ; JMP $0300, and reset there.
        var state = new Apple2VideoState();
        var spec = Apple2Board.Spec(SystemRom(), new Apple2Iou(state));
        Machine m = BoardMachineFactory.Build(spec);
        var bus = m.Space(AddressSpaceKind.Program);
        // STA $C030 = 8D 30 C0 ; JMP $0300 = 4C 00 03
        bus.Write8(0x0300, 0x8D); bus.Write8(0x0301, 0x30); bus.Write8(0x0302, 0xC0);
        bus.Write8(0x0303, 0x4C); bus.Write8(0x0304, 0x00); bus.Write8(0x0305, 0x03);
        m.Cpu.SetRegister("PC", 0x0300);

        long before = state.SpeakerToggles;
        m.Run(8);                                   // run ~one STA (4 cyc) + part of the JMP
        // One STA $C030 must have toggled the speaker TWICE (the RMW dummy read + the store).
        Assert.True(state.SpeakerToggles >= before + 2,
            $"expected >= {before + 2} toggles after one STA $C030; got {state.SpeakerToggles}");
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Apple2BoardTests.A_real_STA"`
Expected: PASS — the cycle-exact `Mos6502Cpu` issues the dummy read on a `STA absolute`, so the speaker toggles twice per `STA $C030`. If it toggles only once, that documents a core RMW-bus gap (NOT an IOU bug): record it as a follow-up against the 6502 core (the IOU stays correct by the bus contract); adjust the assertion to `>= before + 1` and file the core note. **Do not change `Apple2Iou`.**

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/Apple2BoardTests.cs
git commit -m "test(apple2): real STA \$C030 double-toggles the speaker (ADR 0014 D2 build-time check)"
```

---

## Task 6: Queue update

**Files:**
- Modify: `docs/BUILDER_QUEUE.md`

- [ ] **Step 1: Flip the queue row**

In `docs/BUILDER_QUEUE.md`, set row **B** status to ✅, and update the **Last updated** banner with the date + "PR-B merged".

- [ ] **Step 2: Commit**

```bash
git add docs/BUILDER_QUEUE.md
git commit -m "docs(queue): Apple2 PR-B (board + IOU) done"
```

---

## Done-when

- `Apple2Board.Spec` validates + builds a runnable ][+ machine: 48 KiB RAM, the `$C000` Mmio hole, the 12 KiB system ROM, reset-vector-from-ROM, no IRQ, memory-mapped I/O only.
- `Apple2Iou` owns the `$C000` page and toggles every video / speaker switch on **any access** (read OR write) identically, with **no** side effect on `TryPeek` (the peek-free gate) — the ][+'s defining I/O quirk, correct by construction.
- The keyboard latch reads at `$C000` / clears at `$C010`; a real `STA $C030` double-toggles the speaker via the bus-access-level model (ADR 0014 Decision 2's build-time check).
- The shared `Apple2VideoState` is the one object the IOU writes and PR-C's video chip will read.
- Queue row **B** is ✅; PR-C (`Apple2Video`) and PR-D (keyboard/speaker) can plug into this IOU + state next.
