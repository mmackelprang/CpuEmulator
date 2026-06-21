# PR-O — CP/M-on-Videx end-to-end (the CP/M-display capstone) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The CP/M-display **capstone** (ADR 0016, the "usable 80-column CP/M" deliverable): wire the **Videx** (PR-N) into the **SoftCard board** (PR-K) so the CP/M `A>` prompt renders on the **80-column Videx terminal**, and the host `DisplayMultiplexer` (PR-M) **auto-switches Apple-40 → Videx-80 when CP/M takes the Videx** — guest-driven, no UI toggle (the Videx's `ActiveChanged` drives `SetActive`). Add **`get-videx-roms.{sh,ps1}`** (the Videx 1 KiB firmware + 2 KiB char ROMs, Asimov mirror, never vendored). **The un-fakeable gate:** with all assets fetched, CP/M boots and renders the `A>` prompt to the **Videx 80-col output** (the multiplexer's active source is the Videx) — **interpreter-tier** (the coprocessor has no JIT path — PR-K/ADR 0015 Decision 4; the queue row's "both tiers" wording is imprecise, the CP/M/Z80 side is interpreter-only, so this gate is resolved as **interpreter-tier**), **asset-gated / skip-with-note** when the CP/M `.dsk`/ROMs are absent (the PR-K/PR-H discipline — a skipped gate is GREEN).

**Architecture:** Compose the two shipped capstones (K = CP/M boot; N = the Videx) into one surface + one auto-switch wiring, all riding shipped seams:

1. **`SoftCardVidexBoard` (`CpuEmulator.Machines`):** a board-spec composer = the **SoftCard board** (PR-K's `SoftCardBoard.Spec`: base Apple + Z80 coprocessor + `$C500` control port) **+ the Videx** (PR-N's `$C800` window carve + the `"videx"` slot + the IOU `$C0Bx` delegate). It re-uses PR-N's `$C800-$CDFF` carve and PR-K's coprocessor/control-port additions over the same base board — so CP/M (on the Z80) drives the Videx (on the 6502's bus) exactly as the real SoftCard+Videx machine does.
2. **`SoftCardVidexSurface` (`CpuEmulator.Surface.Web`):** the `SoftCardSurface` twin (PR-K) **plus** the Videx: it builds a `DisplayMultiplexer([apple40, videx80])` (PR-M), wires the Videx's `ActiveChanged` → `mux.SetActive` (the guest-driven auto-switch, ADR 0016 Decision 2), and gives `MachineHost` the **multiplexer** as its display (so the host re-sizes 280×192 ↔ 560×216 on the switch, the PR-M re-size). CP/M in drive 1, the Z80 coprocessor, the Videx as the second display.
3. **`get-videx-roms.{sh,ps1}` + the asset-gated end-to-end gate (`tools/` + tests):** the Videx ROM fetch scripts (mirroring `get-apple2-roms`), and the **un-fakeable boot gate** — with the Apple system ROM + CP/M `.dsk` present (Videx ROMs optional, synthetic-font fallback), the real `$C600`→tracks→`$CnXX` boot hands off to the Z80, CP/M's terminal driver enables the Videx (`ActiveChanged(true)` → the multiplexer switches), and `A>` paints on the **Videx 80-col render** — **interpreter-tier, skip-with-note when the assets are absent.**

**Tech Stack:** C# / .NET 10, `CpuEmulator.Core` (`DisplayMultiplexer` [PR-M]), `CpuEmulator.Peripherals` (`VidexVideoterm`/`VidexFont` [PR-N], `Apple2Iou`, `Apple2DiskII`, `DskFluxImage`), `CpuEmulator.Machines` (`SoftCardBoard`/`SoftCardCpm` [PR-K], `Apple2Board.SpecWithVidex` [PR-N], the new `SoftCardVidexBoard`, `Apple2Rom`/`VidexRom`), `CpuEmulator.Surface.Web` (`SoftCardSurface` [PR-K] → the new `SoftCardVidexSurface`, `MachineHost`, `Program.cs`), `tools/` (`get-videx-roms.{sh,ps1}` mirroring `get-apple2-roms`), xUnit (the `SoftCardCpmVectors` skip-with-note pattern). **Depends on K, N ✅.**

## Global Constraints

- **Compose shipped seams — do NOT re-implement K or N.** `SoftCardBoard.Spec`/`SoftCardCpm`/`SoftCardSurface`/`SectorOrderKind.Cpm`/`get-softcard-cpm` (PR-K) and `VidexVideoterm`/`VidexFont`/`Apple2Board.SpecWithVidex`/the IOU `$C0Bx` delegate (PR-N) are all SHIPPED. O is composition: one board composer (SoftCard + Videx), one surface (SoftCardSurface + the multiplexer auto-switch), the Videx ROM fetch, and the end-to-end gate.
- **Interpreter-tier only — resolve the queue's "both tiers" as INTERPRETER-tier.** ⚠️ The queue row O reads *"reaches the `A>` prompt on both tiers."* That wording is **imprecise**: the coprocessor (Z80) runs on the **interpreter tier regardless of board tier** (PR-I; `BoardMachineFactory` builds the Z80 with `ExecutionTier.Interpreter`; ADR 0015 Decision 4 — JIT-under-translation is the deferred PR-L). CP/M's display is produced *by the Z80's writes to the Videx VRAM through the translation*, so the CP/M-on-Videx path **has no JIT tier**. The gate is a single `[SoftCardCpmFact]` (interpreter), NOT a both-tiers `[Theory]` — exactly like PR-K's interpreter-only CP/M boot gate. **Flag the queue-text imprecision in the PR body.**
- **The auto-switch is guest-driven, no UI toggle** (ADR 0016 Decision 1/2): the Videx raises `ActiveChanged(bool)` from its `$C800`-window-enable state (PR-N); the surface wires `ActiveChanged` → `DisplayMultiplexer.SetActive`. The user never picks the display — CP/M's terminal driver enabling the Videx is the switch (the hardware truth).
- **The Videx ROMs are optional for the gate** — the CP/M-on-Videx gate runs with the synthetic `VidexFont` (PR-N) when the real char ROM is absent; the **required** assets are the Apple system ROM + the CP/M `.dsk` (the boot path is 6502-ROM-driven; the Videx render uses the synthetic font if the real one is unfetched). The Videx firmware ROM is only needed if the CP/M driver executes Videx firmware — the SoftCard CP/M terminal driver writes the CRTC directly (research §8), so the synthetic firmware (all-zero) suffices for the boot gate; the real firmware is fetched for fidelity. **`get-videx-roms` fetches both; the gate gates on the Apple ROM + CP/M `.dsk` (the PR-K trigger), with the Videx ROMs as a fidelity opt-in.**
- **Assets fetch-on-demand, never vendored** (ADR 0016 Decision 4; owner sign-off GIVEN, Decision 5): `<cache>/videx/` (firmware 1024 B + char 2048 B), `<cache>/cpm/` (the PR-K CP/M `.dsk`). Both `.sh` and `.ps1`, byte-length sanity-checked, skip-with-note when absent.
- **No `TimingTier` / `ITimingSensitive`** (ADR-only, not in `src/`).
- **HEAD grounding:** all literal code is grounded against `main` @ `59c1c05` (PRs #99–#114 merged) **plus the shipped PR-N** (`VidexVideoterm`, `VidexFont`, `Apple2Board.SpecWithVidex`, the IOU `$C0Bx` delegate). Verify with `git rev-parse HEAD` before starting; **O is planned to land after N ships**, so re-confirm N's shipped signatures (the IOU 4-arg ctor, `SpecWithVidex`, `VidexVideoterm.ActiveChanged`/`Width`/`Height`) at build time.

---

## Recon facts this plan is built on (verified against `main` @ `59c1c05` + the PR-N plan's shipped surface)

1. **`SoftCardBoard.Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom)`** (`src/CpuEmulator.Machines/SoftCardBoard.cs`, PR-K) returns `Apple2Board.SpecWithSystem(...) with { Peripherals = [..base, controlSlot], Coprocessor = coproSpec }` — the base Apple board + the Z80 `CoprocessorSpec(Z80, SoftCardTranslation, "softcard", 2.0)` + the `SoftCardControlPort` at `$C500`. O composes the **Videx carve** (PR-N) onto this same base, so it cannot just call `SoftCardBoard.Spec` (which carves the `$C600` disk-boot band, not the Videx `$C800` band). **Resolution:** O's `SoftCardVidexBoard.Spec` builds on `Apple2Board.SpecWithVidex` (PR-N's Videx carve, which keeps the disk-boot path open via the IOU) **then** adds the coprocessor + control-port slot the same way `SoftCardBoard` does — see Task 1's design note for the exact carve reconciliation (the Videx `$C800` carve + the `$C600` disk-boot ROM + the `$C500` control port must all coexist validator-clean).
2. **`Apple2Board.SpecWithVidex(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, VidexVideoterm videx)`** (PR-N) carves `$C000-$C7FF` Mmio / `$C800-$CBFF` Mmio (the Videx slot) / `$CC00-$CDFF` Ram (VRAM) / `$CE00-$CFFF` Mmio + the `"iou"` and `"videx"` slots. It has **no disk-boot ROM** (PR-N is render-only). **For O the board needs BOTH the `$C600` disk-boot ROM (CP/M boots from disk) AND the Videx `$C800` window.** So O carves: `$C000-$C5FF` Mmio / `$C600-$C6FF` Rom (disk boot) / `$C700-$C7FF` Mmio / `$C800-$CBFF` Mmio (Videx slot) / `$CC00-$CDFF` Ram (VRAM) / `$CE00-$CFFF` Mmio — every region 256-aligned, the Videx slot Mmio-contained, no overlaps (Task 1).
3. **`SoftCardControlPort` at `$C500`** (PR-K, `SoftCardBoard.ControlPortBase = 0xC500`, `ControlPortName = "softcard"`) is a `PeripheralSlot("softcard", controlPort, 0xC500, 0x0100)` inside the `$C000-$C5FF` Mmio region; `CoprocessorSpec.ControlPortPeripheral = "softcard"`. O reuses these (a fresh `SoftCardControlPort` + the same `CoprocessorSpec` shape).
4. **`SoftCardSurface.Create(byte[] systemRom, byte[] diskBootRom, byte[]? charRom, IBlockDevice cpmDisk, Action<byte[]> frameSink, Action<byte[]> audioSink, ExecutionTier tier = Interpreter)`** (`src/CpuEmulator.Surface.Web/SoftCardSurface.cs`, PR-K) builds the SoftCard board, Realizes the video/speaker, resets, and wires `MachineHost(machine, video, keyboard, frameSink, speaker, audioSink)` — the display is the Apple 40-col `video`. O's `SoftCardVidexSurface` is this **plus** the Videx as a second `IDisplayDevice` behind a `DisplayMultiplexer`, with the host's display = the multiplexer.
5. **`DisplayMultiplexer([IDisplayDevice...], int initialActive = 0)`** (`src/CpuEmulator.Core/DisplayMultiplexer.cs`, PR-M): `SetActive(int)` fires `FrameReady` on a real change; `Width`/`Height`/`RenderInto` delegate to the active source. **`MachineHost`** (PR-M) re-sizes its `_rgba` to the active display's geometry per frame (`EnsureFrameBuffer`), so a 280×192 → 560×216 switch re-pulls at the new size (the PR-M `MachineHostResizeTests` gate). O passes the multiplexer as the host's display.
6. **`VidexVideoterm`** (PR-N): `IPeripheral, IDisplayDevice`; ctor `(byte[]? charRom = null, byte[]? firmwareRom = null)`; `event Action<bool>? ActiveChanged`; `Width`/`Height` (80×7=560 / 24×9=216 once programmed, valid default before); `Access(byte, bool)` (the IOU delegate). The CRTC is programmed by the guest (CP/M's terminal driver writes `$C0B0`/`$C0B1`); the Videx raises `ActiveChanged(true)` on the `$C800`-enable. O wires `videx.ActiveChanged += a => mux.SetActive(a ? videxIndex : appleIndex)`.
7. **`Apple2Iou(state, lc, disk2, videx)`** (PR-N's 4-arg ctor): the IOU delegates `$C0Bx` to the Videx. O constructs the IOU with all four (the `SpecWithVidex` caller contract). The Videx is a board peripheral (its own `"videx"` slot) — the factory Realizes it; the IOU does NOT re-Realize it (PR-N's IOU `Realize` leaves the Videx to the factory).
8. **`Apple2Rom.Load`/`TryGetPath`/`TryLoadDiskRom`/`TryLoadCharRom`** (`src/CpuEmulator.Machines/Apple2Rom.cs`): `SystemRomLength = 0x3000`, `DiskRomLength = 0x100`, `CharRomLength = 0x800`; the loaders + `TryGet*Path` cache probes. O adds `VidexRom` (the twin) for the Videx firmware (1024 B) + char (2048 B) ROMs.
9. **`SoftCardCpm.TryGetDiskPath`/`LoadBlockDevice`/`DiskLength` (143,360)** (`src/CpuEmulator.Machines/SoftCardCpm.cs`, PR-K) + **`SoftCardCpmVectors.TryGetAssets()`** + `SoftCardCpmFactAttribute`/`SoftCardCpmTheoryAttribute` (`tests/CpuEmulator.Tests/Apple2/SoftCardCpmVectors.cs`, PR-K) — the skip-with-note machinery O reuses verbatim for the end-to-end gate (the gate gates on the same Apple-ROM + CP/M-`.dsk` pair).
10. **`Program.cs` `DemoSession.RunAsync`** (`src/CpuEmulator.Surface.Web/Program.cs`): the SoftCard boots when `appleRom is not null && cpmDisk is not null` (assetState `"softcard-cpm"`). O **replaces** that branch's `SoftCardSurface.Create` with `SoftCardVidexSurface.Create` (the Videx-equipped surface) — the CP/M deliverable always ships with the Videx (the owner scope decision: "SoftCard + CP/M + Videx 80-col ship together"; research §7). The assetState becomes `"softcard-cpm-videx"` (the banner string; `app.js` maps it).
11. **`get-apple2-roms.{sh,ps1}`** + **`get-softcard-cpm.{sh,ps1}`** (`tools/`) are the fetch-script templates: cache root `${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}`, per-asset subdir, `fetch_one name len required url...` with a byte-length sanity check, idempotent, fail loud, both `.sh` + `.ps1`. `get-videx-roms` fetches `videx-firmware.rom` (1024 B) + `videx-char.rom` (2048 B) into `<cache>/videx/` from the Asimov mirror (`asimov.net/emulators/rom_images/videx/`, research §9).
12. **The Videx 80-col render gate asserts ink on the Videx frame** (PR-N's `MonoOn`/`MonoOff` discipline). O's end-to-end gate renders the **multiplexer's active source** after the boot: if CP/M enabled the Videx, the active source is the Videx (80×24, 560×216) and `A>` is structural ink there; the `mux.ActiveIndex` == the Videx index is the load-bearing auto-switch proof (a 40-col-only CP/M boot would leave the Apple video active).

---

## Conventions to follow

- **Compose, don't re-implement** — every CP/M-boot piece (K) and every Videx piece (N) is shipped; O wires SoftCard + Videx into one board + one surface + the Videx ROM fetch + the end-to-end gate.
- **Mirror `SoftCardSurface` / `SoftCardCpm` / `get-softcard-cpm` / `SoftCardCpmVectors` exactly** (PR-K) — `SoftCardVidexSurface` / `VidexRom` / `get-videx-roms` / the CP/M-on-Videx gate are the Videx-equipped analogues.
- **The auto-switch wiring is the one new behavior** — `videx.ActiveChanged += a => mux.SetActive(...)` in `SoftCardVidexSurface.Create`; everything else is the shipped SoftCard surface + the shipped multiplexer.
- **Assets fetch-on-demand, never vendored** (ADR 0016 Decision 4; sign-off GIVEN, Decision 5) — `<cache>/videx/`, length checks, skip-with-note. The gate gates on the Apple ROM + CP/M `.dsk` (Videx ROMs optional, synthetic-font fallback).
- **The gate runs on the INTERPRETER tier** (the coprocessor is interpreter-only) **and skips-with-note** when the assets are absent — the PR-K discipline. Structural `A>` ink on the **Videx** render + `mux.ActiveIndex == videxIndex` + a committed-hash placeholder.
- **TDD per task**, literal code, commit per task. Warning-clean. **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test ...`.

---

## File Structure

### `CpuEmulator.Machines`
- **Create** `src/CpuEmulator.Machines/SoftCardVidexBoard.cs` — composes the SoftCard board (Z80 coprocessor + `$C500` control port) with the Videx `$C800` window carve into one dual-CPU + Videx `BoardSpec`.
- **Create** `src/CpuEmulator.Machines/VidexRom.cs` — the Videx firmware (1 KiB) + char (2 KiB) ROM asset loader (the `Apple2Rom` twin), both optional (synthetic fallback).

### `CpuEmulator.Surface.Web`
- **Create** `src/CpuEmulator.Surface.Web/SoftCardVidexSurface.cs` — the `SoftCardSurface` twin + the Videx + the `DisplayMultiplexer` auto-switch.
- **Modify** `src/CpuEmulator.Surface.Web/Program.cs` — the SoftCard branch boots the Videx-equipped surface (assetState `"softcard-cpm-videx"`).

### `tools/`
- **Create** `tools/get-videx-roms.sh` — fetch the Videx firmware + char ROMs into `<cache>/videx/` with length checks; never vendored (Asimov mirror).
- **Create** `tools/get-videx-roms.ps1` — the PowerShell sibling.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs` — the board composition (Z80 + control port + Videx all wired), the surface smoke (a 280×192 frame before the switch + the Videx wired), and the asset-gated CP/M-on-Videx end-to-end `A>` gate.

---

## Task 1: `SoftCardVidexBoard` — compose the SoftCard (dual-CPU) + Videx `BoardSpec`

**Files:**
- Create: `src/CpuEmulator.Machines/SoftCardVidexBoard.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs` (the composition asserts)

**Interfaces:**
- Consumes: `Apple2Board` constants + the `$C800`/`$C600` carve, `VidexVideoterm` (PR-N), `SoftCardControlPort`/`SoftCardTranslation`/`CoprocessorSpec` (PR-J/I), `SoftCardBoard.ControlPortBase`/`ControlPortName`/`Z80ClockRatioToPrimary` (PR-K), `BoardSpec`/`MemoryRegion`/`PeripheralSlot`.
- Produces: `SoftCardVidexBoard.Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom, VidexVideoterm videx)` → a dual-CPU + Videx `BoardSpec`.

**Design notes (grounded against `SoftCardBoard.cs` + `Apple2Board.SpecWithVidex` + the validator):**
- The CP/M deliverable board needs **all of**: the `$C600` disk-boot ROM (CP/M boots from disk), the `$C500` SoftCard control port (the Z80 handoff), the Z80 coprocessor (`CoprocessorSpec`), AND the Videx `$C800` window + `"videx"` slot + the IOU `$C0Bx` delegate. None of the shipped composers does all four:
  - `SoftCardBoard.Spec` = base + `$C600` + `$C500` + coprocessor, **no Videx**.
  - `Apple2Board.SpecWithVidex` = base + `$C800`/`$CC00` Videx carve + `"videx"` slot, **no `$C600`/`$C500`/coprocessor**.
- So O carves the full `$C000-$CFFF` band once, with every window, and adds all the slots + the coprocessor. The carve (every region 256-aligned + 256-multiple; every slot Mmio-contained; no overlaps):
  - `$C000-$C5FF` Mmio — the IOU (`$C000` page) + the `$C500` control port slot + the `$C0Bx` Videx CRTC (delegated by the IOU).
  - `$C600-$C6FF` Rom — the slot-6 disk-boot ROM.
  - `$C700-$C7FF` Mmio.
  - `$C800-$CBFF` Mmio — the `"videx"` firmware slot (the Videx Remaps it to ROM in Realize).
  - `$CC00-$CDFF` Ram — the VRAM window (the Videx Remaps it to bank 0).
  - `$CE00-$CFFF` Mmio.
  - `$D000-$FFFF` Rom — the system ROM.
- Slots: `"iou"` (`$C000`, `$0100`), `"softcard"` (`$C500`, `$0100`, the control port), `"videx"` (`$C800`, `$0400`, the firmware window). The control port slot (`$C500`) is inside `$C000-$C5FF` Mmio ✅; the videx slot (`$C800`) is inside `$C800-$CBFF` Mmio ✅; the iou slot inside `$C000-$C5FF` Mmio ✅.
- The coprocessor: `new CoprocessorSpec(CpuKind.Z80, new SoftCardTranslation(), SoftCardBoard.ControlPortName, SoftCardBoard.Z80ClockRatioToPrimary)` — identical to PR-K (the control-port name `"softcard"` matches the `"softcard"` slot, satisfying `copro-control-port-unwired`).

- [ ] **Step 1: Write the failing composition test**

Create `tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs`:

```csharp
using System.Security.Cryptography;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardVidexBoardTests
{
    private static byte[] DiskBootRom()
    {
        var rom = new byte[Apple2Rom.DiskRomLength];   // 256 B
        rom[0x01] = 0x20; rom[0x03] = 0x00; rom[0x05] = 0x03; rom[0x07] = 0x3C;  // slot-6 signature
        rom[0x00] = 0xA9;
        return rom;
    }

    private static (Machine machine, VidexVideoterm videx) BuildSoftCardVidex(byte[] systemRom)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);   // PR-N's 4-arg ctor (the Videx delegate)
        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, DiskBootRom(), videx);
        return (BoardMachineFactory.Build(spec), videx);   // interpreter tier; the coprocessor is interpreter
    }

    [Fact]
    public void The_board_wires_a_Z80_coprocessor_a_control_port_and_the_Videx_window()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;            // reset -> $D000
        (Machine machine, VidexVideoterm videx) = BuildSoftCardVidex(rom);
        IAddressSpace bus = machine.Space(AddressSpaceKind.Program);

        // The Z80 coprocessor is wired + dormant at reset (PR-I).
        Assert.NotNull(machine.Coprocessor);
        Assert.False(machine.CoprocessorActive);

        // The Videx $CC00 VRAM window is live writable RAM (the Videx Remapped it in Realize, PR-N).
        bus.Write8(0xCC00, 0x42);
        Assert.Equal(0x42, videx.PeekVramForTest(0, 0));

        // The $C800 firmware window is ROM (Remapped read-only).
        byte before = bus.Read8(0xC800);
        bus.Write8(0xC800, 0x99);
        Assert.Equal(before, bus.Read8(0xC800));
    }

    [Fact]
    public void The_board_carries_both_the_softcard_control_port_and_the_videx_slot()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(rom);
        var disk = new Apple2DiskII(new SyntheticFluxImage(trackCount: 35));
        var videx = new VidexVideoterm();
        var iou = new Apple2Iou(state, lc, disk, videx);
        BoardSpec spec = SoftCardVidexBoard.Spec(rom, iou, disk, DiskBootRom(), videx);

        Assert.NotNull(spec.Coprocessor);
        Assert.Equal(CpuKind.Z80, spec.Coprocessor!.Cpu);
        // The control-port slot name matches the coprocessor's ControlPortPeripheral (PR-I's validator
        // contract), and the Videx slot is present.
        Assert.Equal(spec.Coprocessor.ControlPortPeripheral,
            spec.Peripherals.Single(p => p.Name == "softcard").Name);
        Assert.Contains(spec.Peripherals, p => p.Name == "videx");
        Assert.Contains(spec.Peripherals, p => p.Name == "iou");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardVidexBoardTests.The_board"`
Expected: FAIL — `SoftCardVidexBoard` does not exist.

- [ ] **Step 3: Create `SoftCardVidexBoard`**

Create `src/CpuEmulator.Machines/SoftCardVidexBoard.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The Microsoft Z-80 SoftCard + Videx Videoterm board — the CP/M-display capstone (ADR 0016,
/// PR-O). Composes the dual-CPU SoftCard machinery (PR-K: the Z80 CoprocessorSpec + the $C500
/// SoftCardControlPort) AND the Videx 80-column card (PR-N: the $C800 firmware window + the $CC00 banked
/// VRAM + the "videx" slot + the IOU $C0Bx delegate) over one base Apple board. CP/M runs on the Z80
/// (translated against shared 6502 RAM) and drives the Videx terminal; the Videx's ActiveChanged signal
/// (consumed by the surface's DisplayMultiplexer) switches the host display Apple-40 -> Videx-80. The
/// $C000-$CFFF band is carved so every window is validator-clean: the IOU at $C000, the $C500 control
/// port, the $C600 disk-boot ROM, the $C800 Videx firmware slot, and the $CC00 VRAM RAM window all
/// coexist. BoardMachineFactory builds the Z80 on the INTERPRETER tier (ADR 0015 Decision 4 — no
/// JIT-under-translation). The 6502 is bus master at reset; the Z80 is dormant until the boot loader's
/// $CnXX write.</summary>
public static class SoftCardVidexBoard
{
    /// <summary>Compose the dual-CPU + Videx CP/M board.</summary>
    /// <param name="systemRom">The 12 KiB Apple ][+ system ROM ($D000-$FFFF).</param>
    /// <param name="iou">The IOU holding the LC + Disk II + Videx (new Apple2Iou(state, lc, disk2, videx)).</param>
    /// <param name="disk2">The Disk II controller (drive 1 holds the CP/M .dsk).</param>
    /// <param name="diskBootRom">The 256 B slot-6 $C600 Disk II boot ROM (the Autostart cold-boot entry).</param>
    /// <param name="videx">The Videx Videoterm (the same instance the IOU delegates $C0Bx to).</param>
    public static BoardSpec Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom,
                                 VidexVideoterm videx)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        ArgumentNullException.ThrowIfNull(disk2);
        ArgumentNullException.ThrowIfNull(diskBootRom);
        ArgumentNullException.ThrowIfNull(videx);
        if (systemRom.Length != Apple2Board.RomLength)
            throw new ArgumentException(
                $"Apple ][+ system ROM must be exactly ${Apple2Board.RomLength:X} bytes; "
              + $"got ${systemRom.Length:X}.", nameof(systemRom));
        if (diskBootRom.Length != Apple2Board.DiskBootRomLength)
            throw new ArgumentException(
                $"Disk II boot ROM must be exactly ${Apple2Board.DiskBootRomLength:X} bytes; "
              + $"got ${diskBootRom.Length:X}.", nameof(diskBootRom));

        var controlPort = new SoftCardControlPort();
        var coprocessor = new CoprocessorSpec(
            CpuKind.Z80, new SoftCardTranslation(),
            SoftCardBoard.ControlPortName, SoftCardBoard.Z80ClockRatioToPrimary);

        return new BoardSpec("softcard-videx-cpm", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(Apple2Board.RamBase, Apple2Board.RamLength, RegionKind.Ram),     // $0000-$BFFF
                new MemoryRegion(Apple2Board.IoBase,                                              // $C000-$C5FF I/O
                    Apple2Board.DiskBootRomBase - Apple2Board.IoBase, RegionKind.Mmio),
                new MemoryRegion(Apple2Board.DiskBootRomBase, Apple2Board.DiskBootRomLength,      // $C600-$C6FF Rom
                    RegionKind.Rom, diskBootRom),
                new MemoryRegion(Apple2Board.DiskBootRomBase + Apple2Board.DiskBootRomLength,     // $C700-$C7FF I/O
                    Apple2Board.VidexFirmwareBase
                        - (Apple2Board.DiskBootRomBase + Apple2Board.DiskBootRomLength), RegionKind.Mmio),
                new MemoryRegion(Apple2Board.VidexFirmwareBase, Apple2Board.VidexFirmwareLength,  // $C800-$CBFF Videx slot
                    RegionKind.Mmio),
                new MemoryRegion(Apple2Board.VidexVramBase, Apple2Board.VidexVramLength,          // $CC00-$CDFF VRAM
                    RegionKind.Ram),
                new MemoryRegion(Apple2Board.VidexVramBase + Apple2Board.VidexVramLength,         // $CE00-$CFFF I/O
                    Apple2Board.IoBase + Apple2Board.IoLength
                        - (Apple2Board.VidexVramBase + Apple2Board.VidexVramLength), RegionKind.Mmio),
                new MemoryRegion(Apple2Board.RomBase, Apple2Board.RomLength,                      // $D000-$FFFF Rom
                    RegionKind.Rom, systemRom),
            ],
            Peripherals:
            [
                new PeripheralSlot("iou", iou, Apple2Board.IouBase, Apple2Board.IouLength),       // $C000 page
                new PeripheralSlot(SoftCardBoard.ControlPortName, controlPort,                    // $C500 control port
                    SoftCardBoard.ControlPortBase, SoftCardBoard.ControlPortLength),
                new PeripheralSlot("videx", videx,                                               // $C800 Videx slot
                    Apple2Board.VidexFirmwareBase, Apple2Board.VidexFirmwareLength),
            ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            Coprocessor: coprocessor);
    }
}
```

> **Implementer note — the constant references + the BoardSpec positional ctor.** This references `Apple2Board.VidexFirmwareBase`/`VidexFirmwareLength`/`VidexVramBase`/`VidexVramLength` (the constants PR-N adds to `Apple2Board.cs`) and `SoftCardBoard.ControlPortBase`/`ControlPortLength`/`ControlPortName`/`Z80ClockRatioToPrimary` (PR-K). Confirm those are `public const` (PR-N/PR-K both declare them `public const` — re-verify after N ships). The `BoardSpec` positional ctor is `(string Name, CpuKind Cpu, int AddressBits, IReadOnlyList<MemoryRegion> Memory, IReadOnlyList<PeripheralSlot> Peripherals, IrqWiring Irq, ResetConfig Reset, Endianness = LittleEndian, int IoAddressBits = 0, CoprocessorSpec? Coprocessor = null)` — pass `Coprocessor:` by name (skipping the two defaulted params). This mirrors how `SoftCardBoard.Spec` sets `Coprocessor` (via `with`), just spelled out positionally here because O carves a fresh `Memory` list (the Videx band differs from `SpecWithSystem`'s).

- [ ] **Step 4: Run the composition tests**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardVidexBoardTests.The_board"`
Expected: PASS — the board validates + builds with a Z80 coprocessor + control port + the Videx `$C800`/`$CC00` windows; the control-port name matches the coprocessor spec; both `"softcard"` and `"videx"` slots are present.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/SoftCardVidexBoard.cs tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs
git commit -m "feat(machines): SoftCardVidexBoard — compose the dual-CPU SoftCard + Videx CP/M board"
```

---

## Task 2: `VidexRom` — the Videx firmware + char ROM asset loader (optional)

**Files:**
- Create: `src/CpuEmulator.Machines/VidexRom.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs` (the loader asserts)

**Interfaces:**
- Consumes: the cache-root probe pattern (the `Apple2Rom` twin).
- Produces: `VidexRom.FirmwareLength` (1024), `VidexRom.CharLength` (2048), `VidexRom.TryGetCharRomPath(string? root = null)`, `VidexRom.TryLoadCharRom()` (→ `byte[]?`, null when absent), `VidexRom.TryGetFirmwarePath`/`TryLoadFirmware()` (→ `byte[]?`).

**Design notes (grounded against `Apple2Rom.cs`):** The Videx ROMs are **both optional** — the synthetic `VidexFont.Fallback` (PR-N) covers the char ROM, and the synthetic all-zero firmware covers the firmware window (CP/M writes the CRTC directly; the firmware is fidelity, not required for the boot gate). The loader mirrors `Apple2Rom.TryLoadCharRom` (a `TryGet*Path` cache probe + exact-length `LoadExact`, returning `byte[]?` so absence is null, not an exception). Cache subdir `<root>/videx/`.

- [ ] **Step 1: Write the failing loader tests**

Append to `SoftCardVidexBoardTests`:

```csharp
    [Fact]
    public void VidexRom_char_path_is_null_when_absent_under_an_empty_root()
    {
        string emptyRoot = Path.Combine(Path.GetTempPath(), $"empty-videx-{Guid.NewGuid():N}");
        Assert.Null(VidexRom.TryGetCharRomPath(emptyRoot));
        Assert.Null(VidexRom.TryGetFirmwarePath(emptyRoot));
    }

    [Fact]
    public void VidexRom_loads_an_exact_2KiB_char_rom_and_rejects_a_wrong_length()
    {
        string root = Path.Combine(Path.GetTempPath(), $"videx-ok-{Guid.NewGuid():N}");
        string dir = Path.Combine(root, "videx");
        Directory.CreateDirectory(dir);
        try
        {
            string good = Path.Combine(dir, "videx-char.rom");
            File.WriteAllBytes(good, new byte[VidexRom.CharLength]);   // 2048
            byte[]? rom = VidexRom.TryLoadCharRom(root);
            Assert.NotNull(rom);
            Assert.Equal(VidexRom.CharLength, rom!.Length);

            File.WriteAllBytes(good, new byte[100]);                   // wrong length
            Assert.Throws<InvalidDataException>(() => VidexRom.TryLoadCharRom(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardVidexBoardTests.VidexRom"`
Expected: FAIL — `VidexRom` does not exist.

- [ ] **Step 3: Create `VidexRom`**

Create `src/CpuEmulator.Machines/VidexRom.cs`:

```csharp
namespace CpuEmulator.Machines;

/// <summary>Loads the Videx Videoterm ROMs from the asset cache (NOT vendored — fetched on demand by
/// tools/get-videx-roms.{sh,ps1} from the Asimov mirror, asimov.net/emulators/rom_images/videx/; ADR 0016
/// Decision 4). The cache root is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors); the ROMs
/// live at &lt;root&gt;/videx/. Both ROMs are OPTIONAL — the synthetic VidexFont.Fallback (PR-N) covers the
/// char ROM and an all-zero synthetic image covers the firmware window (CP/M's terminal driver writes the
/// 6845 CRTC directly, research §8 — the firmware is fidelity, not required to boot CP/M to A>). The twin
/// of Apple2Rom: a TryGet*Path cache probe + exact-length validation, returning null when absent (the
/// surface falls back to the synthetic assets — never an exception on absence).</summary>
public static class VidexRom
{
    public const int FirmwareLength = 0x0400;   // 1 KiB $C800-$CBFF firmware
    public const int CharLength = 0x0800;       // 2 KiB char ROM (256 glyphs x 8 rows)

    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    private static string? PathIfExists(string name, string? root)
    {
        string path = Path.Combine(root ?? CacheRoot, "videx", name);
        return File.Exists(path) ? path : null;
    }

    public static string? TryGetCharRomPath(string? root = null) => PathIfExists("videx-char.rom", root);
    public static string? TryGetFirmwarePath(string? root = null) => PathIfExists("videx-firmware.rom", root);

    /// <summary>The 2 KiB char ROM, or null when absent (the surface uses VidexFont.Fallback). Throws on a
    /// wrong-length file (a corrupt fetch).</summary>
    public static byte[]? TryLoadCharRom(string? root = null) =>
        TryGetCharRomPath(root) is { } p ? LoadExact(p, CharLength, "Videx char") : null;

    /// <summary>The 1 KiB firmware ROM, or null when absent (the surface uses an all-zero synthetic image).</summary>
    public static byte[]? TryLoadFirmware(string? root = null) =>
        TryGetFirmwarePath(root) is { } p ? LoadExact(p, FirmwareLength, "Videx firmware") : null;

    private static byte[] LoadExact(string path, int length, string what)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length != length)
            throw new InvalidDataException(
                $"{what} ROM at {path} must be exactly {length} bytes; got {bytes.Length}.");
        return bytes;
    }
}
```

- [ ] **Step 4: Run the loader tests**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardVidexBoardTests.VidexRom"`
Expected: PASS — absent paths are null; an exact 2 KiB char ROM loads; a wrong-length file throws.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/VidexRom.cs tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs
git commit -m "feat(machines): VidexRom — optional Videx firmware + char ROM loader (synthetic fallback)"
```

---

## Task 3: The Videx ROM fetch scripts (`get-videx-roms.{sh,ps1}`)

**Files:**
- Create: `tools/get-videx-roms.sh`
- Create: `tools/get-videx-roms.ps1`

> **No automated test** — operational scripts (they fetch live URLs). The gate is: they exist, length-sanity-check (1024 / 2048), never vendor, and `VidexRom` (Task 2) consumes their cache layout (`<cache>/videx/videx-firmware.rom`, `<cache>/videx/videx-char.rom`).

- [ ] **Step 1: Create `tools/get-videx-roms.sh`** (mirror `get-apple2-roms.sh` exactly):

```sh
#!/usr/bin/env sh
# Fetches the Videx Videoterm ROMs into the vector cache (same root as the Apple/Spectrum/ZEX/Klaus assets;
# NEVER vendored). The 1 KiB firmware ROM ($C800-$CBFF) and the 2 KiB character-generator ROM are fetched
# on demand at test time — they are NOT committed (ADR 0016 Decision 4). BOTH are OPTIONAL: a synthetic
# fallback font + an all-zero firmware cover the CP/M-on-Videx boot gate; the real ROMs add glyph fidelity.
# Provide your own URLs/mirror if the defaults move (research §9: asimov.net/emulators/rom_images/videx/).
#
# Layout written (consumed by CpuEmulator.Machines.VidexRom):
#   <cache>/videx/videx-firmware.rom   1024 bytes  (OPTIONAL — $C800 firmware; synthetic zero covers it)
#   <cache>/videx/videx-char.rom       2048 bytes  (OPTIONAL — 256x8 glyphs; VidexFont.Fallback covers it)
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
VIDEX_DIR="$DEST/videx"
mkdir -p "$VIDEX_DIR"

# Each row: filename | expected byte length | required(1)/optional(0) | space-separated candidate URLs.
# NOTE: the URLs below are placeholders for the owner to point at the Asimov Videx mirror or a preferred
# source; the length sanity-check is what guarantees a correct image regardless of source.
fetch_one() {
    name="$1"; want_len="$2"; required="$3"; shift 3
    out="$VIDEX_DIR/$name"
    if [ -f "$out" ]; then echo "$name already present at $out"; return 0; fi
    for url in "$@"; do
        if curl -fsSL "$url" -o "$out" 2>/dev/null; then
            len=$(wc -c < "$out")
            if [ "$len" -eq "$want_len" ]; then
                echo "$name fetched to $out ($len bytes) from $url"; return 0
            fi
            rm -f "$out"; echo "WARN: $url failed sanity (len=$len, want $want_len) — trying next" >&2
        else
            rm -f "$out"; echo "WARN: fetch of $url failed — trying next" >&2
        fi
    done
    if [ "$required" -eq 1 ]; then
        echo "ERROR: could not fetch the required $name from any source" >&2; return 1
    fi
    echo "NOTE: optional $name not fetched — the built-in synthetic Videx asset will be used" >&2; return 0
}

fetch_one "videx-firmware.rom" 1024 0 \
    "https://mirror.example/videx/videx-firmware.rom"
fetch_one "videx-char.rom" 2048 0 \
    "https://mirror.example/videx/videx-character.rom"

echo "Videx ROM fetch complete (cache: $VIDEX_DIR)."
```

- [ ] **Step 2: Create `tools/get-videx-roms.ps1`** (mirror `get-softcard-cpm.ps1` exactly):

```pwsh
#!/usr/bin/env pwsh
# Fetches the Videx Videoterm ROMs into the vector cache (same root as the Apple/Spectrum/ZEX/Klaus assets;
# NEVER vendored). The 1 KiB firmware ROM and the 2 KiB char ROM are fetched on demand, NOT committed (ADR
# 0016 Decision 4). BOTH are OPTIONAL: a synthetic fallback font + all-zero firmware cover the CP/M-on-Videx
# boot gate; the real ROMs add glyph fidelity.
# Layout written (consumed by CpuEmulator.Machines.VidexRom):
#   <cache>/videx/videx-firmware.rom   1024 bytes  (OPTIONAL)
#   <cache>/videx/videx-char.rom       2048 bytes  (OPTIONAL)
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$videxDir = Join-Path $Destination "videx"
New-Item -ItemType Directory -Force $videxDir | Out-Null

function Fetch-One($name, $wantLen, $required, $urls) {
    $out = Join-Path $videxDir $name
    if (Test-Path $out) { Write-Host "$name already present at $out"; return }
    foreach ($url in $urls) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $out -ErrorAction Stop
            $len = (Get-Item $out).Length
            if ($len -eq $wantLen) { Write-Host "$name fetched to $out ($len bytes) from $url"; return }
            Remove-Item $out -ErrorAction SilentlyContinue
            Write-Warning "$url failed the sanity check (len=$len, want $wantLen) — trying next"
        } catch {
            Remove-Item $out -ErrorAction SilentlyContinue
            Write-Warning "fetch of $url failed ($_) — trying next"
        }
    }
    if ($required) { Write-Error "could not fetch the required $name from any source" }
}

# NOTE: placeholder URLs for the owner to point at the Asimov Videx mirror (research §9) or a preferred
# source; the length sanity-checks (1024 / 2048) guarantee a correct image.
Fetch-One "videx-firmware.rom" 1024 $false @("https://mirror.example/videx/videx-firmware.rom")
Fetch-One "videx-char.rom" 2048 $false @("https://mirror.example/videx/videx-character.rom")

Write-Host "Videx ROM fetch complete (cache: $videxDir)."
```

> **Implementer note — the placeholder URLs + the `.sh` exec bit.** The Videx ROMs are fetched from the Asimov mirror (research §9: `asimov.net/emulators/rom_images/videx/`); the `mirror.example` URLs are owner-confirmed placeholders, with the length sanity-checks the real guarantee (the shipped `get-apple2-roms`/`get-softcard-cpm` pattern). Mark the `.sh` executable: `git update-index --chmod=+x tools/get-videx-roms.sh` (mirror the other fetch scripts).

- [ ] **Step 3: Commit**

```bash
chmod +x tools/get-videx-roms.sh
git add tools/get-videx-roms.sh tools/get-videx-roms.ps1
git update-index --chmod=+x tools/get-videx-roms.sh
git commit -m "feat(tools): get-videx-roms.{sh,ps1} — fetch-on-demand Videx ROMs (Asimov mirror, never vendored)"
```

---

## Task 4: `SoftCardVidexSurface` + the `Program.cs` boot wiring (the auto-switch)

**Files:**
- Create: `src/CpuEmulator.Surface.Web/SoftCardVidexSurface.cs`
- Modify: `src/CpuEmulator.Surface.Web/Program.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs` (a surface-construction smoke test)

**Interfaces:**
- Consumes: `SoftCardVidexBoard` (Task 1), `VidexRom` (Task 2), `VidexVideoterm`/`VidexFont` (PR-N), `DskFluxImage` + `SectorOrderKind.Cpm` (PR-K), `DisplayMultiplexer` (PR-M), the `Apple2Surface`/`SoftCardSurface` triad pattern, `MachineHost`.
- Produces: `SoftCardVidexSurface.Create(systemRom, diskBootRom, charRom, videxCharRom, videxFirmware, cpmDisk, frameSink, audioSink, tier)` → a `SoftCardVidexSurface` record `(Machine, Apple2Video, VidexVideoterm, DisplayMultiplexer, Apple2Keyboard, Apple2Speaker, MachineHost)` wired with the CP/M disk in drive 1, the Videx as the second display, and the guest-driven auto-switch.

**Design notes (grounded against `SoftCardSurface.cs`):** The body is `SoftCardSurface.Create` **verbatim** with three changes: (1) construct the Videx (`new VidexVideoterm(videxCharRom, videxFirmware)`) and pass it to the IOU (the 4-arg ctor) + the board (`SoftCardVidexBoard.Spec`); (2) the host's display is a `DisplayMultiplexer([video, videx], initialActive: 0)` (the Apple 40-col active at boot, the Videx index 1); (3) wire `videx.ActiveChanged += a => mux.SetActive(a ? VidexIndex : AppleIndex)` (the guest-driven auto-switch — CP/M's terminal driver enabling the Videx flips the active source). The video/speaker `Realize` + `machine.Reset()` are identical (the Videx is Realized by the factory as a board peripheral).

- [ ] **Step 1: Write the failing surface smoke test**

Append to `SoftCardVidexBoardTests`:

```csharp
    [Fact]
    public void SoftCardVidexSurface_constructs_renders_and_wires_the_auto_switch()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        var bootRom = DiskBootRom();
        IBlockDevice cpm = new DiskImage(new byte[SoftCardCpm.DiskLength], 256, isReadOnly: true);

        byte[]? lastFrame = null;
        CpuEmulator.Surface.Web.SoftCardVidexSurface surface =
            CpuEmulator.Surface.Web.SoftCardVidexSurface.Create(rom, bootRom, charRom: null,
                videxCharRom: null, videxFirmware: null, cpmDisk: cpm, f => lastFrame = f, _ => { });

        surface.Host.RunHeadless(totalCycles: 40_000, sliceCycles: 17_030);

        // At boot the Apple 40-col video is the active display source (index 0): a 280x192 frame.
        Assert.NotNull(lastFrame);
        Assert.Equal((byte)'F', lastFrame![0]);
        Assert.Equal((byte)'B', lastFrame[1]);
        int width = lastFrame[4] | (lastFrame[5] << 8);
        int height = lastFrame[6] | (lastFrame[7] << 8);
        Assert.Equal(280, width);
        Assert.Equal(192, height);
        Assert.Equal(0, surface.Display.ActiveIndex);   // Apple-40 active at boot
        Assert.NotNull(surface.Machine.Coprocessor);    // the Z80 is wired

        // The auto-switch is wired: when the Videx signals active, the multiplexer follows (the same path
        // CP/M's terminal driver drives). This proves the ActiveChanged -> SetActive wiring without a boot.
        surface.Videx.SetActiveForTest(true);
        Assert.Equal(1, surface.Display.ActiveIndex);   // now the Videx 80-col is active
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardVidexBoardTests.SoftCardVidexSurface"`
Expected: FAIL — `SoftCardVidexSurface` does not exist.

- [ ] **Step 3: Create `SoftCardVidexSurface`**

Create `src/CpuEmulator.Surface.Web/SoftCardVidexSurface.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Composes the Apple ][+ SoftCard + Videx for the web surface — the CP/M-display capstone
/// (ADR 0016, PR-O), the SoftCardSurface twin PLUS the Videx 80-column display. Identical to
/// SoftCardSurface EXCEPT (1) a VidexVideoterm is wired into the IOU + the SoftCardVidexBoard, and (2) the
/// host's display is a DisplayMultiplexer([apple40, videx80]) whose active source follows the Videx's
/// guest-driven ActiveChanged signal (ADR 0016 Decision 1/2 — CP/M's terminal driver enabling the Videx
/// switches the host display Apple-40 -> Videx-80, no UI toggle). The MachineHost re-sizes its buffer
/// 280x192 -> 560x216 on the switch (PR-M). CP/M boots on the Z80 (interpreter tier) translated against
/// shared RAM and drives the Videx terminal. The Videx ROMs are optional (synthetic fallback).</summary>
public sealed record SoftCardVidexSurface(
    Machine Machine, Apple2Video Video, VidexVideoterm Videx, DisplayMultiplexer Display,
    Apple2Keyboard Keyboard, Apple2Speaker Speaker, MachineHost Host)
{
    private const int AppleIndex = 0;
    private const int VidexIndex = 1;

    public static SoftCardVidexSurface Create(byte[] systemRom, byte[] diskBootRom, byte[]? charRom,
                                              byte[]? videxCharRom, byte[]? videxFirmware,
                                              IBlockDevice cpmDisk,
                                              Action<byte[]> frameSink, Action<byte[]> audioSink,
                                              ExecutionTier tier = ExecutionTier.Interpreter)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(diskBootRom);
        ArgumentNullException.ThrowIfNull(cpmDisk);

        var state = new Apple2VideoState();
        var placeholder = new AddressSpace(AddressSpaceKind.Program, 16);
        placeholder.MapMemory(0x0000, new byte[0x10000], writable: true);
        var video = new Apple2Video(placeholder, state, charRom);
        var keyboard = new Apple2Keyboard(state);
        var speaker = new Apple2Speaker(state);
        var lc = new Apple2LanguageCard(systemRom);
        var videx = new VidexVideoterm(videxCharRom, videxFirmware);
        // Drive 1 = the CP/M .dsk, re-nibblized with the CP/M data-track skew onto the unchanged Disk II head.
        var drive1 = new DskFluxImage(cpmDisk, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk, videx);   // PR-N's 4-arg ctor (the Videx $C0Bx delegate)

        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx);
        Machine machine = BoardMachineFactory.Build(spec, tier);

        video.Realize(machine);
        speaker.Realize(machine);
        // The Videx is Realized by the factory (its own "videx" board slot) — no explicit Realize here.
        machine.Reset();

        // The two display sources behind the host: Apple 40-col (index 0, active at boot) + the Videx
        // 80-col (index 1). The guest-driven auto-switch: the Videx's ActiveChanged drives SetActive
        // (ADR 0016 Decision 2 — the user never picks; CP/M's terminal driver enabling the Videx is the
        // switch). The MachineHost re-sizes its buffer on the switch (PR-M).
        var mux = new DisplayMultiplexer([video, videx], initialActive: AppleIndex);
        videx.ActiveChanged += active => mux.SetActive(active ? VidexIndex : AppleIndex);

        var host = new MachineHost(machine, mux, keyboard, frameSink, speaker, audioSink);
        return new SoftCardVidexSurface(machine, video, videx, mux, keyboard, speaker, host);
    }
}
```

- [ ] **Step 4: Wire `Program.cs` (the SoftCard branch boots the Videx-equipped surface)**

In `Program.cs` `DemoSession.RunAsync`, **replace** the `SoftCardSurface.Create` call in the existing `if (appleRom is not null && cpmDisk is not null)` branch with `SoftCardVidexSurface.Create` (the CP/M deliverable always ships with the Videx — the owner scope decision, research §7):

```csharp
        if (appleRom is not null && cpmDisk is not null)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom);
            byte[] bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom()
                ?? throw new InvalidOperationException(
                    "SoftCard CP/M needs the slot-6 Disk II boot ROM (disk2.rom) — run tools/get-apple2-roms.");
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            byte[]? videxChar = CpuEmulator.Machines.VidexRom.TryLoadCharRom();      // optional (synthetic fallback)
            byte[]? videxFirmware = CpuEmulator.Machines.VidexRom.TryLoadFirmware(); // optional
            CpuEmulator.Core.IBlockDevice cpm = CpuEmulator.Machines.SoftCardCpm.LoadBlockDevice(cpmDisk);
            SoftCardVidexSurface softcard = SoftCardVidexSurface.Create(sys, bootRom, charRom,
                videxChar, videxFirmware, cpm,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            pump = new SurfacePump(softcard.Host, AppleSliceCycles, ApplePeriod);
            assetState = "softcard-cpm-videx";
        }
```

> **Implementer note — the assetState string + `app.js`.** Change the assetState from `"softcard-cpm"` to `"softcard-cpm-videx"` and add the case to `app.js`'s `ST`-message handler (the PR-H/PR-K seam): map it to a status like `"connected · Apple ][+ SoftCard · CP/M · Videx 80-col"`. The richer `ST` status frame is still PR-P; O only updates the one asset-state string. The existing Apple/Spectrum/demo branches are byte-for-byte unchanged (only the SoftCard branch's surface type + assetState change). The client canvas auto-sizes from the per-frame FB width/height (PR-M), so a mid-session 280×192 → 560×216 switch needs no client change.

- [ ] **Step 5: Run the surface smoke gate**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardVidexBoardTests.SoftCardVidexSurface"`
Expected: PASS — the surface constructs, resets, renders a 280×192 frame (Apple-40 active at boot), the Z80 is wired, and the auto-switch wiring flips the multiplexer to the Videx (index 1) on `ActiveChanged`.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Surface.Web/SoftCardVidexSurface.cs src/CpuEmulator.Surface.Web/Program.cs tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs
git commit -m "feat(surface): SoftCardVidexSurface — CP/M + Videx + the guest-driven display auto-switch"
```

---

## Task 5: The un-fakeable gate — CP/M boots + renders `A>` on the Videx 80-col (interpreter, asset-gated)

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs` (the boot gate)

**Interfaces:**
- Consumes: `SoftCardCpmVectors.TryGetAssets()` + `SoftCardCpmFactAttribute` (PR-K, reused verbatim — the gate gates on the same Apple-ROM + CP/M-`.dsk` pair), `SoftCardVidexSurface` (Task 4), `VidexVideoterm`/`Apple2Palette`.

**Design notes — this is the row-O un-fakeable gate (interpreter-tier, asset-gated, skip-with-note):**
- **Resolve the queue's "both tiers" as INTERPRETER-tier.** The coprocessor (Z80) is built interpreter-only (PR-I; ADR 0015 Decision 4), and CP/M's Videx output is produced by the Z80, so there is no JIT tier for this path. A single `[SoftCardCpmFact]` (interpreter), like PR-K — NOT a both-tiers `[Theory]`.
- **Reuse PR-K's `SoftCardCpmVectors` + `SoftCardCpmFactAttribute`** — the gate gates on the Apple system ROM + the CP/M `.dsk` (the boot path is 6502-ROM-driven `$C600` disk-boot → `$CnXX` Z80-start). The Videx ROMs are NOT required (the synthetic `VidexFont` renders `A>` fine; the real char ROM is a fidelity opt-in). So the gate's skip trigger is the same PR-K pair — when the Apple ROM OR the CP/M `.dsk` is absent, the gate **skips-with-note** (GREEN).
- **The real boot mechanism** (no faking): build the `SoftCardVidexSurface` with the real CP/M `.dsk` re-nibblized into drive 1, `RunHeadless` for a generous CP/M-boot budget. The real 6502 `$C600` Autostart boot reads the CP/M boot tracks (the `DskFluxImage` synthesizes the GCR with the CP/M skew, PR-K), the on-disk cold-boot loader issues the `$CnXX` write that starts the Z80 (PR-J), the **real Z80** runs CP/M **translated** against shared RAM, and CP/M's terminal driver programs the Videx CRTC (`$C0B0`/`$C0B1`) + enables the Videx (`$C800` window) → the Videx raises `ActiveChanged(true)` → the multiplexer switches to the Videx (the auto-switch). CP/M's BIOS console output paints `A>` on the **Videx 80×24 render** (the multiplexer's now-active source).
- **The assertion** (mirroring PR-K's `Cpm_boots_to_the_A_prompt`, but on the **Videx** render): after the boot, assert (1) `surface.Display.ActiveIndex == 1` (the Videx is the active display — the auto-switch fired, the load-bearing proof CP/M took the Videx; a 40-col-only CP/M boot leaves it 0); (2) render the **multiplexer's active source** (the Videx) and assert a mostly-blank 80×24 screen with **structural ink** (the `A>` prompt + the CP/M sign-on banner) — `MonoOn` pixels > a threshold on a mostly-`MonoOff` field; (3) the geometry is the Videx 80-col (560×216), not the Apple 40-col (280×192); (4) `machine.CoprocessorActive` (the Z80 ran — the `$CnXX` handoff fired); (5) a committed-hash placeholder (inert until captured on the first green run with the real asset). A dead boot is all-off / wrong-geometry / Apple-active — unfakeable.
- **`bootCycles`** generous (the CP/M cold boot reads 3 system tracks + the Z80 handoff + CP/M to the prompt + the Videx CRTC program): start at PR-K's `10_000_000` primary cycles, tune down on the first green run with the real asset.

- [ ] **Step 1: Write the boot gate**

Append to `SoftCardVidexBoardTests`:

```csharp
    // Generous budget for the CP/M cold boot + the Videx switch: the 6502 reads the 3 system tracks, hands
    // off to the Z80, CP/M runs to A>, and the terminal driver enables the Videx. Tune on the first green run.
    private const long CpmBootCycles = 10_000_000;

    [SoftCardCpmFact]
    public void Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter()
    {
        var (systemRomPath, cpmDiskPath) = SoftCardCpmVectors.TryGetAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the CP/M boot gate");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();             // Apple text font (null -> fallback)
        byte[]? videxChar = VidexRom.TryLoadCharRom();            // OPTIONAL (null -> VidexFont.Fallback)
        byte[]? videxFirmware = VidexRom.TryLoadFirmware();       // OPTIONAL (null -> synthetic zero firmware)
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(cpmDiskPath);

        byte[]? lastFrame = null;
        CpuEmulator.Surface.Web.SoftCardVidexSurface surface =
            CpuEmulator.Surface.Web.SoftCardVidexSurface.Create(systemRom, diskBootRom, charRom,
                videxChar, videxFirmware, cpm, f => lastFrame = f, _ => { });

        // The real $C600 -> tracks -> $CnXX -> CP/M boot; the terminal driver programs + enables the Videx.
        surface.Host.RunHeadless(totalCycles: CpmBootCycles, sliceCycles: 17_030);

        // (1) The auto-switch fired: CP/M took the Videx (the active display is the 80-col, not the 40-col).
        Assert.Equal(1, surface.Display.ActiveIndex);            // a 40-col-only CP/M boot would leave this 0
        // (2) The active source is the Videx 80-col geometry (560x216), not the Apple 40-col (280x192).
        Assert.Equal(surface.Videx.Width, surface.Display.Width);
        Assert.Equal(surface.Videx.Height, surface.Display.Height);
        Assert.Equal(80 * VidexFont.CellWidth, surface.Display.Width);

        // (3) Render the active (Videx) source: CP/M's sign-on + the A> prompt paint ink on a mostly-blank
        // 80x24 terminal. A dead/garbage boot is all-off (no prompt) or noisy (no clear background).
        var rgba = new uint[surface.Display.Width * surface.Display.Height];
        surface.Display.RenderInto(rgba);
        int offPixels = 0, onPixels = 0;
        foreach (uint p in rgba)
        {
            if (p == Apple2Palette.MonoOff) offPixels++;
            else if (p == Apple2Palette.MonoOn) onPixels++;
        }
        int total = rgba.Length;
        Assert.True(offPixels > total / 2,
            $"expected a mostly-blank CP/M Videx terminal; got {offPixels}/{total} off pixels");
        Assert.True(onPixels > 50, $"expected the A> prompt + CP/M sign-on ink; got {onPixels} on pixels");

        // (4) The Z80 ran: it became the bus master during the boot (the $CnXX handoff fired).
        Assert.True(surface.Machine.CoprocessorActive,
            "expected the Z80 to be the active bus master after the CP/M boot handoff");

        // (5) Tighter gate: a committed RGBA hash of the Videx frame. On the FIRST green run with the real
        // assets, capture the hash (uncomment the print), paste it below, then re-run.
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[cpm-on-videx frame hash] {hash}");  // <-- uncomment once to capture
        string ExpectedBootHash = "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN";
        if (ExpectedBootHash != "PLACEHOLDER_CAPTURE_ON_FIRST_GREEN_RUN")
            Assert.Equal(ExpectedBootHash, hash);
    }

    private static byte[] AsBytes(uint[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }
```

> **Implementer note — interpreter-tier resolution of the queue's "both tiers" + what the gate proves.** The queue row O reads "reaches the A> prompt on both tiers," but the CP/M/Z80 path is **interpreter-only** (PR-I/PR-K; ADR 0015 Decision 4 — JIT-under-translation is the deferred PR-L). So this is a single `[SoftCardCpmFact]` (interpreter), like PR-K's gate — flag the queue-text imprecision in the PR body. When the assets are absent (CI default) the gate **skips** (GREEN). When the owner fetches the Apple system ROM + the CP/M `.dsk` (Videx ROMs optional), the gate runs the **real** boot + the **real** translated Z80 + the **real** Videx CRTC programming + the **real** auto-switch — nothing is stubbed. The `ActiveIndex == 1` + the Videx geometry + the structural `A>` ink + `CoprocessorActive` + the committed hash make a dead/faked/40-col-only boot fail. If the live boot does not switch to the Videx within `CpmBootCycles`, the build-time residuals (research §-residual 1: the exact CP/M load map / the `$C600`-loader path; ADR 0016 OQ1: the exact Videx-enable register condition the CP/M driver uses) are where to look — they are resolved against the real `.dsk`'s on-disk boot code + the Videx firmware 2.4 behavior (PR-N's `$C0B8-$C0BF` bank-enable is the default active-display trigger; refine here if the real driver's enable differs).

- [ ] **Step 2: Run the boot gate (expected: SKIPPED without the assets)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardVidexBoardTests.Cpm_boots_and_renders"`
Expected (CI, no asset): **SKIPPED** with the named-script note (the PR-K `SoftCardCpmFact` skip) — a skipped gate is GREEN. (When the owner fetches the Apple ROM + CP/M `.dsk`, it runs + asserts the real `A>` on the Videx 80-col.)

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/SoftCardVidexBoardTests.cs
git commit -m "test(softcard-videx): CP/M renders A> on the Videx 80-col (interpreter, asset-gated, skip-with-note)"
```

---

## Task 6: Final gate — full suite + warning-clean build

**Files:** none (verification only).

- [ ] **Step 1: Full build, warning-clean**

Run: `dotnet build CpuEmulator.slnx`
Expected: Build succeeded, **0 warnings**.

- [ ] **Step 2: Full test suite**

Run: `dotnet test CpuEmulator.slnx`
Expected: the post-PR-N baseline green plus the new PR-O tests (`SoftCardVidexBoardTests`: the board composition, the `VidexRom` loader, the surface smoke), 0 failed. **The CP/M-on-Videx boot gate SKIPS** (no asset in CI) — a skipped gate is green. **No pre-existing test regresses** — O adds two new `Machines` files, one new surface file, the scripts, and changes only the SoftCard branch of `Program.cs` (the surface type + the assetState string; the Apple/Spectrum/demo branches are byte-for-byte). PR-K's `SoftCardSurface` is untouched (still its own type + the smoke test); PR-N's `Apple2Board.SpecWithVidex`/`VidexVideoterm` are untouched (O composes them, not modifies).

- [ ] **Step 3: Confirm the un-fakeable gates ran (or skipped) as expected**

Confirm:
- `The_board_wires_a_Z80_coprocessor_a_control_port_and_the_Videx_window` + `The_board_carries_both_the_softcard_control_port_and_the_videx_slot` — the board composition (run, green).
- `VidexRom_*` — the Videx ROM loader (run, green).
- `SoftCardVidexSurface_constructs_renders_and_wires_the_auto_switch` — the surface + the auto-switch wiring (run, green).
- `Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter` — the headline gate: **SKIPPED** with the named-script note when the asset is absent (GREEN); runs + asserts the real `A>` on the Videx when the owner fetches the Apple ROM + CP/M `.dsk`.

---

## Self-Review

**1. Spec coverage (ADR 0016 + the row-O gate):**
- Wire the Videx (N) into the SoftCard board (K) → Task 1 (`SoftCardVidexBoard.Spec` — SoftCard coprocessor + control port + the Videx `$C800` carve). ✓
- The CP/M `A>` renders on the 80-col Videx → Task 5 (the gate renders the multiplexer's active Videx source). ✓
- The `DisplayMultiplexer` auto-switches Apple-40 → Videx-80 when CP/M takes the Videx (guest-driven, no UI toggle) → Task 4 (`videx.ActiveChanged += a => mux.SetActive(...)`) + Task 5 (`ActiveIndex == 1` asserted). ✓
- CP/M boots + renders to the Videx 80-col output, **interpreter-tier** (the queue's "both tiers" resolved to interpreter — the coprocessor has no JIT path) → Task 5 (a single `[SoftCardCpmFact]`, interpreter). ✓
- **Asset-gated / skip-with-note** when the CP/M `.dsk`/ROM is absent → Task 5 (reuses PR-K's `SoftCardCpmVectors`/`SoftCardCpmFactAttribute`). ✓
- Adds `get-videx-roms.{sh,ps1}` → Task 3 + Task 2 (`VidexRom` consumes the cache layout). ✓
- Deps K, N ✅ — every composed seam is shipped (K: SoftCard board/CP/M; N: the Videx). ✓

**2. Placeholder scan:** No TBD/TODO/"implement later"/"similar to Task N". Every code step shows literal code (the board composer, the ROM loader, both scripts, the surface, the auto-switch wiring, all tests, the boot gate). The `mirror.example` URLs are owner-confirmed placeholders (the length sanity-checks are the real guarantee, the shipped `get-apple2-roms`/`get-softcard-cpm` pattern); the committed-hash placeholder is the shipped PR-K/PR-H gate discipline (inert until captured); the `CpmBootCycles` budget is tuning guidance with a grounded starting value — none is missing code.

**3. Type consistency:** `SoftCardVidexBoard.Spec(byte[], Apple2Iou, Apple2DiskII, byte[], VidexVideoterm)`, `VidexRom.TryLoadCharRom`/`TryLoadFirmware`/`CharLength`/`FirmwareLength`, `SoftCardVidexSurface.Create(byte[], byte[], byte[]?, byte[]?, byte[]?, IBlockDevice, Action<byte[]>, Action<byte[]>, ExecutionTier)` + the record `(Machine, Apple2Video, VidexVideoterm, DisplayMultiplexer, Apple2Keyboard, Apple2Speaker, MachineHost)`, `DisplayMultiplexer([IDisplayDevice...], int)` / `SetActive(int)` / `ActiveIndex` / `Width`/`Height`/`RenderInto` (PR-M), `VidexVideoterm.ActiveChanged`/`Width`/`Height`/`SetActiveForTest`/`PeekVramForTest` (PR-N), `Apple2Iou(state, lc, disk2, videx)` (PR-N's 4-arg), `SoftCardBoard.ControlPortBase`/`ControlPortLength`/`ControlPortName`/`Z80ClockRatioToPrimary` (PR-K), `Apple2Board.VidexFirmwareBase`/`VidexFirmwareLength`/`VidexVramBase`/`VidexVramLength`/`RamBase`/`RamLength`/`IoBase`/`IoLength`/`DiskBootRomBase`/`DiskBootRomLength`/`RomBase`/`RomLength`/`IouBase`/`IouLength` (PR-N adds the Videx consts; the rest shipped), `CoprocessorSpec(CpuKind, IAddressTranslation, string, double)` (PR-I), `SoftCardTranslation()`/`SoftCardControlPort()` (PR-J), `SoftCardCpm.LoadBlockDevice`/`DiskLength` + `SoftCardCpmVectors.TryGetAssets`/`SoftCardCpmFactAttribute` (PR-K), `DskFluxImage(IBlockDevice, SectorOrderKind.Cpm)` (PR-K/G), `Machine.Coprocessor`/`CoprocessorActive`/`Space` (PR-I), `MachineHost(Machine, IDisplayDevice, IKeyboardSink, Action<byte[]>, IAudioSink, Action<byte[]>)` — used identically across tasks and matching the shipped signatures.

**Builder-readiness note:** every CP/M-boot primitive (K) and every Videx primitive (N) is shipped; O is composition + the Videx ROM fetch + the auto-switch + the end-to-end gate. The cross-file touches are `Program.cs` (the SoftCard branch's surface type + the assetState string — the other branches byte-for-byte) and the new `Machines`/`Surface.Web` files. The board composer reconciles the three carves (the `$C600` disk-boot + the `$C500` control port + the `$C800` Videx window) into one validator-clean band — the **one piece worth a compile-check** (the slot/region alignment + Mmio-containment + no-overlap, all grounded against the validator during planning). **Three flagged items carried to the queue + the PR body:** (1) **the queue row O's "both tiers" is imprecise** — the CP/M/Z80 path is interpreter-only (resolved to a single interpreter `[SoftCardCpmFact]`); (2) **PR-N's `IFastMemoryProvider` drift** (the Videx ships `IPeripheral, IDisplayDevice`, the fast-RAM intent via `Remap`-to-RAM) carries into O unchanged; (3) **build-time residuals** — the exact Videx-enable register condition the CP/M terminal driver uses (ADR 0016 OQ1; PR-N defaults the `$C0B8-$C0BF` bank-enable as the active-display trigger) is confirmed against the real `.dsk`'s driver when the asset is fetched (the gate skips cleanly until then). **O depends on N shipping** — re-confirm N's shipped signatures (the IOU 4-arg ctor, `SpecWithVidex`'s Videx consts, `VidexVideoterm.ActiveChanged`/`Width`/`Height`/`SetActiveForTest`/`PeekVramForTest`) at build time.
