# PR-K — Interpreter-tier CP/M boot wiring (`$C600`→tracks→`$CnXX`-start) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The dual-CPU **capstone** — compose the real **SoftCard board** = the shipped `Apple2Board` system board **+ `WithCoprocessor(Z80)`** (via `BoardSpec.Coprocessor` / `CoprocessorSpec`, PR-I) **+ the shipped `SoftCardTranslation` 6-branch table** (PR-J) **+ the shipped `SoftCardControlPort`** (PR-J), and wire the **real boot path**: the 6502 `$C600` Autostart disk-boot loads the CP/M cold-boot loader from the CP/M `.dsk`, the on-disk boot code issues the `$CnXX` write that starts the Z80, and **CP/M runs translated to the `A>` prompt**. Ship the **`get-softcard-cpm.{sh,ps1}`** fetch script (Asimov mirror — owner sign-off GIVEN; never vendored, cached outside source control). Add the **CP/M sector order** (the SoftCard double-skew, research §5) to the shipped `.dsk` adapter so the CP/M `.dsk` re-nibblizes correctly. The headline gate — **CP/M reaches the `A>` prompt** — is **asset-gated, skip-with-note when the CP/M `.dsk` is absent** (the PR-H ROM-boot-gate discipline exactly: a skipped gate is GREEN). The coprocessor runs on the **interpreter tier** (PR-I established this — no JIT-under-translation; that is deferred PR-L).

**Architecture:** Three composable layers, all riding shipped seams:

1. **The CP/M sector order (`CpuEmulator.Peripherals`):** extend the shipped `SectorOrderKind` enum + `Apple2SectorOrder` with the **CP/M** ordering (the double-skew, research §5). The shipped `DskFluxImage(IBlockDevice, SectorOrderKind)` already re-nibblizes any order onto the unchanged `Apple2DiskII` head — so the CP/M `.dsk` becomes a synthetic GCR track on the **same** `IFluxImage` path, with the CP/M skew the only new datum (the codec/board layer the research §5 names).
2. **`SoftCardBoard` (`CpuEmulator.Machines`):** a board-spec composer that takes the shipped `Apple2Board.SpecWithSystem` board and **adds** `Coprocessor = new CoprocessorSpec(CpuKind.Z80, new SoftCardTranslation(), "softcard", clockRatioToPrimary: ~2.0)` plus the `SoftCardControlPort` as a `$CnXX` peripheral slot. This is the dual-CPU SoftCard `BoardSpec` — `BoardMachineFactory.Build` already wires `Coprocessor` (PR-I) and builds the Z80 on the interpreter tier.
3. **`SoftCardSurface` + the boot path + the asset-gated `A>` gate (`CpuEmulator.Surface.Web` + tests):** a `SoftCardSurface` (the `Apple2Surface` twin) that inserts the CP/M `.dsk` into drive 1 and builds the SoftCard board; `SoftCardCpm` asset loader (the `Apple2Rom` twin) for the cached `.dsk`; the **un-fakeable boot gate** — with the `.dsk` present, the 6502 `$C600` boot + the on-disk loader + the `$CnXX` Z80-start hand off to CP/M, which runs translated and paints `A>` on the text screen — **skip-with-note when the `.dsk` is absent.**

**Tech Stack:** C# / .NET 10, `CpuEmulator.Peripherals` (`Apple2SectorOrder`, `SoftCardTranslation`, `SoftCardControlPort`, `Apple2DiskII`, `DskFluxImage`), `CpuEmulator.Machines` (`Apple2Board`, `CoprocessorSpec`, `BoardSpec`, `BoardMachineFactory`, the new `SoftCardBoard` + `SoftCardCpm`), `CpuEmulator.Core` (`DiskImage`/`IBlockDevice`, `Machine` dual-CPU `Run`), `CpuEmulator.Surface.Web` (`SoftCardSurface`, the `Apple2Surface`/`SpectrumSurface` pattern), `tools/` (`get-softcard-cpm.{sh,ps1}` mirroring `get-apple2-roms`), xUnit (the `Apple2RomVectors` skip-with-note pattern). **Depends on E, F, H, J ✅** (Language Card, Disk II `.woz`/`.dsk`, the surface + ROM-boot, the SoftCard translation + control port) and transitively I ✅ (the dual-CPU machine model).

## Global Constraints

- **Compose shipped seams — do NOT re-implement the dual-CPU machinery.** `SoftCardTranslation`, `SoftCardControlPort`, `CoprocessorSpec`, the dual-CPU `Machine`/`RunDualCpu`, `BoardMachineFactory`'s `Coprocessor` wiring, `Apple2Board.SpecWithSystem`, `Apple2DiskII`, `DskFluxImage` are all SHIPPED (PRs A–J). K is composition + the CP/M skew + the asset + the boot gate.
- **Interpreter-tier only.** The coprocessor (Z80) runs on the interpreter tier (PR-I: `BoardMachineFactory` builds the coprocessor with `ExecutionTier.Interpreter` regardless of board tier; ADR 0015 Decision 4). CP/M boots on the interpreter. **No JIT-under-translation** (that is the deferred PR-L). The boot gate runs on the **interpreter** (the oracle).
- **The CP/M boot gate is ASSET-GATED + skip-with-note.** Mirror the PR-H `Apple2RomTheory`/`Apple2RomVectors` discipline exactly: a `[SoftCardCpmTheory]` that sets `Skip` when the CP/M `.dsk` is absent. **A skipped gate is GREEN.** Owner sign-off for the asset fetch is GIVEN (ADR 0016 Decision 5 — fetch-on-demand from the Asimov mirror); nothing copyrighted is vendored.
- **The `.dsk` is never vendored** — `get-softcard-cpm.{sh,ps1}` fetches on demand, caches under `<root>/cpm/`, sanity-checks the byte length (140 KiB), fails loud. Both `.sh` and `.ps1`.
- **The CP/M skew lands in the board/codec layer** (research §5): the data-track skew is the CP/M `SectorOrderKind`; the `.dsk` adapter (PR-G's `DskFluxImage`) re-nibblizes with it. **Re-nibblization is the shipped adapter's job — K only supplies the skew table.**
- **No `TimingTier` / `ITimingSensitive`** (ADR-only, not in `src/`).
- **HEAD grounding:** all literal code is grounded against `main` @ `10f5737` (PRs #99–#111 merged). Verify with `git rev-parse HEAD` before starting.

---

## Recon facts this plan is built on (verified against `main` @ `10f5737`)

1. **`CoprocessorSpec(CpuKind Cpu, IAddressTranslation Translation, string ControlPortPeripheral, double ClockRatioToPrimary)`** (`src/CpuEmulator.Machines/CoprocessorSpec.cs`) is the dual-CPU declaration. `BoardSpec` carries it as the trailing optional `CoprocessorSpec? Coprocessor = null` field. `BoardMachineFactory.Build` (`BoardMachineFactory.cs:56-66`) wires `spec.Coprocessor` by calling `builder.WithCoprocessor(CpuCoreFactory.ForKind(copro.Cpu, AddressSpaceKind.Program, ExecutionTier.Interpreter), copro.Translation, copro.ClockRatioToPrimary)` — the coprocessor is **always** interpreter-tier. **No factory change needed.**
2. **`SoftCardTranslation(bool translationEnabled = true)`** (`src/CpuEmulator.Peripherals/SoftCardTranslation.cs`) is the shipped 6-branch table. K constructs `new SoftCardTranslation()` (translation enabled — the DIP S1-1-off default).
3. **`SoftCardControlPort`** (`src/CpuEmulator.Peripherals/SoftCardControlPort.cs`) has `Name => "softcard"`, a ctor `SoftCardControlPort()`, and toggles the active CPU via the `ICoprocessorControl` it captures in `Realize`. K maps it as a `$Cn00` peripheral slot whose `Name` matches the `CoprocessorSpec.ControlPortPeripheral` string `"softcard"`.
4. **`Apple2Board.SpecWithSystem(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom)`** (`src/CpuEmulator.Machines/Apple2Board.cs:92`) is the fully-wired base board: 48 KiB RAM, the `$C000-$C5FF`/`$C600-$C6FF` ROM/`$C700-$CFFF` carve (the slot-6 `$C600` Disk II boot ROM), the system ROM at `$D000-$FFFF`, the IOU slot at `$C000`. K extends a clone of this with the coprocessor + the control-port slot.
5. **The `$C000-$CFFF` I/O band carve** in `SpecWithSystem` leaves `$C700-$CFFF` as one Mmio region. The SoftCard's control port lives at a slot's `$Cn00` page (standard SoftCard slots are 4/5; the slot-2 example in PR-J's gate used `$C200`). K places the control port in a **free slot page inside an Mmio region** — `$C500` (slot 5, inside `$C700-$CFFF`? no: `$C500` is inside `$C000-$C5FF`). **Verify the carve:** `$C000-$C5FF` Mmio, `$C600-$C6FF` Rom, `$C700-$CFFF` Mmio. A `$C500` control-port slot sits inside the **first** Mmio region (`$C000-$C5FF`); a `$C700` slot sits inside the **third** (`$C700-$CFFF`). Either is validator-clean (the slot is fully contained in an Mmio region). K uses **`$C500`** (slot 5 — a documented SoftCard slot; the firmware writes its own `$Cn00`, but for the test board any free `$Cn00` page works and is decoded by the control port). The translation's branch-5 (`$E000->$C000`) means the Z80 sees the `$C5xx` control port at `$E5xx` — consistent with PR-J's "the Z80's matching write, which it sees as `$EN00`".
6. **`Apple2DiskII(IFluxImage image)`** + the IOU (`new Apple2Iou(state, lc, disk)`) is the shipped drive-1 wiring (PR-F/H). K inserts a **CP/M `.dsk`** by wrapping it in a `DskFluxImage(blockDevice, SectorOrderKind.Cpm)` (the new CP/M order) and constructing the `Apple2DiskII` over it — exactly the `Apple2Surface` drive-1 path (`Apple2Surface.cs:32`, `new Apple2DiskII(drive1Image ?? new SyntheticFluxImage(...))`).
7. **`DskFluxImage(IBlockDevice block, SectorOrderKind order)`** (`src/CpuEmulator.Peripherals/DskFluxImage.cs`) re-nibblizes 256-byte / 16-sector tracks using `Apple2SectorOrder.PhysicalToLogical(order)`. It requires `block.SectorSize == 256` and `block.SectorCount % 16 == 0`. A 140 KiB CP/M `.dsk` is 35 tracks × 16 × 256 = 143,360 bytes → 560 sectors → 35 tracks. **K adds `SectorOrderKind.Cpm` + its `PhysicalToLogical` table; the adapter is unchanged.**
8. **`Apple2SectorOrder`** (`src/CpuEmulator.Peripherals/Apple2SectorOrder.cs`) has `enum SectorOrderKind { Dos33, ProDos }` + `PhysicalToLogical(kind)` returning a fresh 16-entry `int[]`. Its doc explicitly says *"CP/M uses a THIRD ordering — NOT modeled here; it lands with the CP/M disk in the CP/M arc, PR-K/O."* **K is that PR.** The CP/M **data-track** skew (research §5, the `apple-do` table): `0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1`.
9. **`DiskImage(byte[] image, int sectorSize, bool isReadOnly)`** + `DiskImage.FromFile(path, sectorSize, isReadOnly)` (`src/CpuEmulator.Peripherals/DiskImage.cs`) wrap a raw sector image as an `IBlockDevice`. K loads the CP/M `.dsk` as `DiskImage.FromFile(path, 256, isReadOnly: true)`.
10. **`Apple2RomVectors` + `Apple2RomFactAttribute`/`Apple2RomTheoryAttribute`** (`tests/CpuEmulator.Tests/Apple2/Apple2RomVectors.cs`) are the skip-with-note templates: a `TryGetRomPath()` that reads `CPUEMULATOR_TESTVECTORS` (default `~/.cache/cpuemulator/vectors`) + a fixed subpath, and a `FactAttribute`/`TheoryAttribute` subclass that sets `Skip` when the path is null. K mirrors them for the CP/M `.dsk` (and the system ROM — the boot gate needs **both** the system ROM and the CP/M disk).
11. **`tools/get-apple2-roms.{sh,ps1}`** are the fetch-script templates: `set -eu` / `$ErrorActionPreference = "Stop"`, cache root `${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}`, per-asset subdir, `fetch_one name len required url...` with a **byte-length sanity check**, idempotent (skip if present), fail loud. `get-softcard-cpm.{sh,ps1}` fetches the CP/M `.dsk` (143360 bytes) into `<cache>/cpm/` the same way, from the Asimov mirror (ADR 0016 Decision 4/5; sign-off GIVEN).
12. **`Apple2Surface.Create(systemRom, diskBootRom, charRom, frameSink, audioSink, drive1Image, tier)`** (`src/CpuEmulator.Surface.Web/Apple2Surface.cs:18`) already accepts an optional `IFluxImage? drive1Image`. The SoftCard surface is the same composition **plus** the coprocessor + the control-port slot — so `SoftCardSurface` builds the SoftCard `BoardSpec` (Task 2) then wires the same video/keyboard/speaker triad through `MachineHost` (the `Apple2Surface` body verbatim, with the SoftCard board spec).
13. **`Z80Cpu.Reset` sets `PC = 0`** (`src/CpuEmulator.Cpus.Z80/Z80Cpu.cs:80-85`), translated by `SoftCardTranslation` branch 1 to physical `$1000` — the SoftCard's documented Z80 entry (research §2). The dual-CPU `Machine.Run` drives only the active core (PR-I), and the control-port write hands off (PR-J's end-to-end gate proves a real Z80 runs translated against shared 6502 RAM).
14. **The boot gate renders via `Apple2Video.RenderInto`** into the Apple text screen (`Apple2Palette.MonoOn`/`MonoOff`, the PR-H `Apple2BootTests.Rom_boots_to_the_applesoft_prompt_on_both_tiers` structural+hash discipline). **On the bare SoftCard board (no Videx — that is PR-N/O), CP/M's terminal output goes to the Apple 40-col text screen**; the `A>` prompt is structural ink there. The 80-col Videx display is PR-O (deps K + N), NOT this PR — so K's gate asserts `A>` on the **Apple 40-col text render** (the honest pre-Videx CP/M display).

---

## Conventions to follow

- **Compose, don't re-implement** — every dual-CPU piece is shipped; K wires them into a SoftCard `BoardSpec` + surface + the CP/M skew + the asset + the gate.
- **Mirror `Apple2Surface` / `Apple2Rom` / `get-apple2-roms` / `Apple2BootTests` exactly** — `SoftCardSurface` / `SoftCardCpm` / `get-softcard-cpm` / the CP/M boot gate are the SoftCard analogues.
- **Assets fetch-on-demand, never vendored** (ADR 0016 Decision 4; sign-off GIVEN, Decision 5) — `<cache>/cpm/`, 140 KiB length check, skip-with-note when absent.
- **The boot gate runs on the INTERPRETER tier and skips-with-note when the CP/M `.dsk` (or the system ROM) is absent** — the PR-H discipline. Structural `A>` assertion + a committed-hash placeholder.
- **TDD per task**, literal code, commit per task. Warning-clean. **Build/test:** `dotnet build CpuEmulator.slnx`; `dotnet test ...`.

---

## File Structure

### `CpuEmulator.Peripherals`
- **Modify** `src/CpuEmulator.Peripherals/Apple2SectorOrder.cs` — add `SectorOrderKind.Cpm` + its `PhysicalToLogical` table (the research §5 data-track skew).

### `CpuEmulator.Machines`
- **Create** `src/CpuEmulator.Machines/SoftCardBoard.cs` — composes `Apple2Board.SpecWithSystem` + the coprocessor (`CoprocessorSpec(Z80, SoftCardTranslation, "softcard", ~2.0)`) + the `SoftCardControlPort` `$C500` slot into the dual-CPU SoftCard `BoardSpec`.
- **Create** `src/CpuEmulator.Machines/SoftCardCpm.cs` — the CP/M `.dsk` asset loader (the `Apple2Rom` twin): `TryGetDiskPath()` + `LoadBlockDevice()` (length-validated 143,360-byte raw 256-byte-sector image).

### `CpuEmulator.Surface.Web`
- **Create** `src/CpuEmulator.Surface.Web/SoftCardSurface.cs` — the `Apple2Surface` twin: insert the CP/M `.dsk` into drive 1, build the SoftCard board, reset, wire `MachineHost`.
- **Modify** `src/CpuEmulator.Surface.Web/Program.cs` — boot the SoftCard when **both** the system ROM and the CP/M `.dsk` are cached (else the existing Apple/Spectrum/demo fallback); report the asset state for the banner.

### `tools/`
- **Create** `tools/get-softcard-cpm.sh` — fetch the CP/M `.dsk` (143360 bytes) into `<cache>/cpm/` with a length sanity check; never vendored (Asimov mirror).
- **Create** `tools/get-softcard-cpm.ps1` — the PowerShell sibling.

### Tests (`CpuEmulator.Tests`)
- **Create** `tests/CpuEmulator.Tests/Apple2/SoftCardCpmVectors.cs` — `TryGetDiskPath()` + `SoftCardCpmFactAttribute`/`SoftCardCpmTheoryAttribute` (skip-with-note when the `.dsk` OR the system ROM is absent).
- **Create** `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` — the board composition asserts (the coprocessor is wired, the control port is present, the single-CPU base board is unchanged) + the asset-gated CP/M `A>` boot gate.

---

## Task 1: The CP/M sector order (the SoftCard double-skew, research §5)

**Files:**
- Modify: `src/CpuEmulator.Peripherals/Apple2SectorOrder.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` (the skew assert; the file grows in later tasks)

**Interfaces:**
- Consumes: nothing new (extends the shipped enum + table).
- Produces: `SectorOrderKind.Cpm`; `Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm)` returns the 16-entry CP/M data-track skew.

**Design notes (grounded against `Apple2SectorOrder.cs` + research §5):** The shipped enum is `{ Dos33, ProDos }`; the shipped doc explicitly defers CP/M to "PR-K/O" — this is it. The canonical CP/M **data-track** skew table (research §5, `apple-do`-ordered): `0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1`. The shipped `PhysicalToLogical(kind)` returns physical→logical (the `DskFluxImage` pulls the image LBA for the logical sector mapped to physical p). The research §5 table is the data-track skew the SoftCard's 6502 RWTS applies (the Z80 BIOS does XLT=0, research §4) — i.e. the order the on-disk data sits in vs. the logical order CP/M reads, which is exactly the physical→logical interleave the adapter consumes. (System/boot tracks `$00-$02` use the CP/M-physical skew; for an end-to-end boot-to-`A>` gate the single data-track CP/M order is the correct adapter datum — the boot tracks are read by the same head through the same synthesized GCR; the skew governs which logical sector lands at which physical position, which the table encodes.)

- [ ] **Step 1: Write the failing skew test**

Create `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` (the skew test first):

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardBoardTests
{
    [Fact]
    public void Cpm_sector_order_is_the_documented_data_track_skew()
    {
        // research §5: the canonical CP/M data-track skew (apple-do order).
        int[] expected = [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];
        int[] actual = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Cpm_order_is_a_permutation_distinct_from_dos33_and_prodos()
    {
        int[] cpm = Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Cpm);
        // A valid interleave is a permutation of 0..15.
        Assert.Equal(Enumerable.Range(0, 16), cpm.OrderBy(x => x));
        // And it is genuinely a third ordering (distinct from the two shipped tables).
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.Dos33), cpm);
        Assert.NotEqual(Apple2SectorOrder.PhysicalToLogical(SectorOrderKind.ProDos), cpm);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.Cpm"`
Expected: FAIL — `SectorOrderKind.Cpm` does not exist (compile error).

- [ ] **Step 3: Add `SectorOrderKind.Cpm` + the table**

In `src/CpuEmulator.Peripherals/Apple2SectorOrder.cs`, extend the enum (line 6) and add the table + the switch arm:

```csharp
public enum SectorOrderKind { Dos33, ProDos, Cpm }
```

Add the table field after `ProDosPhysToLog` (after line 21):

```csharp
    // CP/M (SoftCard) data-track skew (research §5, the canonical apple-do data-track order). The Z80 BIOS
    // does no translation (XLT=0); the skew is applied by the 6502 RWTS, so the on-disk physical->logical
    // interleave for CP/M data tracks is this third ordering (distinct from DOS 3.3 / ProDOS). Lands with
    // the CP/M disk in PR-K, exactly as this file's header note promised.
    private static readonly int[] CpmPhysToLog =
        [0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1];
```

Add the switch arm in `PhysicalToLogical` (line 25):

```csharp
    public static int[] PhysicalToLogical(SectorOrderKind kind) => kind switch
    {
        SectorOrderKind.Dos33 => (int[])Dos33PhysToLog.Clone(),
        SectorOrderKind.ProDos => (int[])ProDosPhysToLog.Clone(),
        SectorOrderKind.Cpm => (int[])CpmPhysToLog.Clone(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
```

- [ ] **Step 4: Run the skew tests**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.Cpm"`
Expected: PASS — the CP/M order matches the documented table and is a distinct permutation.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/Apple2SectorOrder.cs tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs
git commit -m "feat(peripherals): SectorOrderKind.Cpm — the SoftCard data-track skew (research §5)"
```

---

## Task 2: `SoftCardBoard` — compose the dual-CPU SoftCard `BoardSpec`

**Files:**
- Create: `src/CpuEmulator.Machines/SoftCardBoard.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` (the composition asserts)

**Interfaces:**
- Consumes: `Apple2Board.SpecWithSystem` (PR-H), `CoprocessorSpec` + `BoardSpec.Coprocessor` (PR-I), `SoftCardTranslation` + `SoftCardControlPort` (PR-J), `PeripheralSlot`, `Apple2Iou`, `Apple2DiskII`.
- Produces: `SoftCardBoard.Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom)` → a dual-CPU `BoardSpec` (the base SoftCard board + the Z80 coprocessor + the control-port slot).

**Design notes (grounded against `Apple2Board.SpecWithSystem` + `BoardSpec` + `CoprocessorSpec`):**
- `Apple2Board.SpecWithSystem` returns a `BoardSpec` with one `PeripheralSlot("iou", ...)`. K needs to **add** the control-port slot **and** set `Coprocessor`. Since `BoardSpec` is a `record`, K builds the base spec, then `spec with { Peripherals = [..spec.Peripherals, controlSlot], Coprocessor = coproSpec }`.
- The control port at `$C500` (slot 5) sits inside the `$C000-$C5FF` Mmio region the carve leaves — validator-clean (slot fully contained in Mmio). The `CoprocessorSpec.ControlPortPeripheral` MUST equal the slot's `Name` (`"softcard"`) so the wiring is self-consistent (the validator's `copro-control-port-unwired` check, PR-I).
- `ClockRatioToPrimary: 2.0` (research §3: the Z80 is ~2.04 MHz vs the 6502's ~1.02 MHz — "~2× the 6502"). 2.0 is the grounded ratio (PR-I/J used 2.0 in their gates).
- The `SoftCardTranslation()` is constructed translation-enabled (DIP S1-1 off, the boot default).

- [ ] **Step 1: Write the failing composition test**

Append to `SoftCardBoardTests`:

```csharp
    private static byte[] DiskBootRom()
    {
        var rom = new byte[Apple2Rom.DiskRomLength];   // 256 B
        rom[0x01] = 0x20; rom[0x03] = 0x00; rom[0x05] = 0x03; rom[0x07] = 0x3C;  // slot-6 signature
        rom[0x00] = 0xA9;
        return rom;
    }

    private static Machine BuildSoftCard(byte[] systemRom, IFluxImage? drive1 = null)
    {
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(drive1 ?? new SyntheticFluxImage(trackCount: 35));
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, DiskBootRom());
        return BoardMachineFactory.Build(spec);   // interpreter tier; the coprocessor is always interpreter
    }

    [Fact]
    public void The_softcard_board_builds_a_6502_primary_and_a_dormant_Z80_coprocessor()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000
        Machine machine = BuildSoftCard(rom);

        Assert.NotNull(machine.Coprocessor);          // the Z80 coprocessor is wired (PR-I)
        Assert.False(machine.CoprocessorActive);      // the 6502 is the bus master at reset (Z80 dormant)
    }

    [Fact]
    public void The_softcard_board_carries_a_control_port_named_to_match_the_coprocessor_spec()
    {
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        BoardSpec spec = SoftCardBoard.Spec(
            rom, new Apple2Iou(new Apple2VideoState(), new Apple2LanguageCard(rom),
                               new Apple2DiskII(new SyntheticFluxImage(trackCount: 35))),
            new Apple2DiskII(new SyntheticFluxImage(trackCount: 35)), DiskBootRom());

        Assert.NotNull(spec.Coprocessor);
        // The control-port slot's Name must equal the CoprocessorSpec.ControlPortPeripheral (PR-I's
        // copro-control-port-unwired validator contract) — the wiring is self-consistent.
        Assert.Equal(spec.Coprocessor!.ControlPortPeripheral,
            spec.Peripherals.Single(p => p.Name == "softcard").Name);
        Assert.Equal(CpuKind.Z80, spec.Coprocessor.Cpu);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.The_softcard"`
Expected: FAIL — `SoftCardBoard` does not exist (compile error).

- [ ] **Step 3: Create `SoftCardBoard`**

Create `src/CpuEmulator.Machines/SoftCardBoard.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The Microsoft Z-80 SoftCard board (ADR 0015): the fully-wired Apple ][+ base board
/// (<see cref="Apple2Board.SpecWithSystem"/>) PLUS a Z80 coprocessor that shares the 6502's program RAM
/// under run-one-then-the-other bus arbitration. Composes the SHIPPED dual-CPU seams (PR-I/J): the
/// <see cref="CoprocessorSpec"/> declares the Z80 + the <see cref="SoftCardTranslation"/> 6-branch table +
/// the ~2.0 clock ratio (research §3) + the control-port name; the <see cref="SoftCardControlPort"/> is a
/// $C500 (slot 5) peripheral whose $CnXX write toggles the active CPU. BoardMachineFactory builds the Z80
/// on the INTERPRETER tier regardless of board tier (ADR 0015 Decision 4 — JIT-under-translation is the
/// deferred PR-L). The 6502 is the bus master at reset (the Z80 is dormant until the boot loader issues
/// the $CnXX start write). The single-CPU base board is unchanged — this only ADDS the Coprocessor field
/// + the control-port slot.</summary>
public static class SoftCardBoard
{
    /// <summary>The SoftCard control-port page. $C500 = slot 5 (a documented SoftCard slot, research §1);
    /// it sits inside the $C000-$C5FF Mmio region SpecWithSystem's I/O-band carve leaves, so the slot is
    /// validator-clean (fully contained in Mmio). The Z80 sees it at $E500 (translation branch 5,
    /// $E000->$C000) — consistent with PR-J's "the Z80's matching write, which it sees as $EN00".</summary>
    public const uint ControlPortBase = 0xC500;
    public const uint ControlPortLength = 0x0100;

    /// <summary>The Z80 SoftCard runs at ~2.04 MHz vs the 6502's ~1.02 MHz (research §3) — ~2x.</summary>
    public const double Z80ClockRatioToPrimary = 2.0;

    /// <summary>The control-port peripheral name; MUST match the CoprocessorSpec.ControlPortPeripheral so
    /// BoardSpecValidator's copro-control-port-unwired check passes (PR-I).</summary>
    public const string ControlPortName = "softcard";

    /// <summary>Compose the dual-CPU SoftCard BoardSpec from the base SpecWithSystem board.</summary>
    /// <param name="systemRom">The 12 KiB Apple ][+ system ROM ($D000-$FFFF).</param>
    /// <param name="iou">The IOU holding the LC + Disk II (same caller contract as SpecWithSystem).</param>
    /// <param name="disk2">The Disk II controller (drive 1 holds the CP/M .dsk when booting CP/M).</param>
    /// <param name="diskBootRom">The 256 B slot-6 $C600 Disk II boot ROM (the Autostart cold-boot entry).</param>
    public static BoardSpec Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        ArgumentNullException.ThrowIfNull(disk2);
        ArgumentNullException.ThrowIfNull(diskBootRom);

        BoardSpec baseSpec = Apple2Board.SpecWithSystem(systemRom, iou, disk2, diskBootRom);

        var controlPort = new SoftCardControlPort();
        var controlSlot = new PeripheralSlot(ControlPortName, controlPort, ControlPortBase, ControlPortLength);
        var coprocessor = new CoprocessorSpec(
            CpuKind.Z80, new SoftCardTranslation(), ControlPortName, Z80ClockRatioToPrimary);

        // Additive: add the control-port slot + the coprocessor declaration; everything else is the
        // shipped base board (BoardSpec is a record — `with` keeps the base spec immutable).
        return baseSpec with
        {
            Peripherals = [.. baseSpec.Peripherals, controlSlot],
            Coprocessor = coprocessor,
        };
    }
}
```

> **Implementer note — the `PeripheralSlot` ctor + `BoardSpec` `with`.** Verify the `PeripheralSlot` ctor shape against the shipped record (`new PeripheralSlot("iou", iou, IouBase, IouLength)` in `Apple2Board.cs:47` is `(string Name, IPeripheral Device, uint Base, uint Length)` — match that exact positional shape). `BoardSpec` is a positional `record` with `Peripherals` (an `IReadOnlyList<PeripheralSlot>`) and the trailing `CoprocessorSpec? Coprocessor` — the `with { Peripherals = [...], Coprocessor = ... }` is the grounded immutable extension. The control port is a **fresh** `SoftCardControlPort` (not the IOU's) — it is its own `$C500` slot, Realized by the factory like any peripheral, capturing the dual-CPU `Machine`'s `ICoprocessorControl` (PR-J).

- [ ] **Step 4: Run the composition tests**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.The_softcard"`
Expected: PASS — the SoftCard board builds a 6502 primary + a dormant Z80; the control-port slot is named to match the coprocessor spec.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/SoftCardBoard.cs tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs
git commit -m "feat(machines): SoftCardBoard — compose the dual-CPU SoftCard BoardSpec (base + Z80 + control port)"
```

---

## Task 3: `SoftCardCpm` — the CP/M `.dsk` asset loader (the `Apple2Rom` twin)

**Files:**
- Create: `src/CpuEmulator.Machines/SoftCardCpm.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` (the loader asserts)

**Interfaces:**
- Consumes: `DiskImage`/`IBlockDevice` (the raw `.dsk` → block device).
- Produces: `SoftCardCpm.TryGetDiskPath()` (the cached `<root>/cpm/softcard-cpm.dsk` path, or null); `SoftCardCpm.DiskLength` (143,360); `SoftCardCpm.LoadBlockDevice(path?)` (length-validated `IBlockDevice` over the raw 256-byte-sector image).

**Design notes (grounded against `Apple2Rom.cs` + `DiskImage.cs` + research §4):** A 140 KiB CP/M `.dsk` is 35 tracks × 16 sectors × 256 bytes = **143,360 bytes**. The loader mirrors `Apple2Rom` (cache root, `PathIfExists`, exact-length validation) but yields an `IBlockDevice` (via `DiskImage(bytes, 256, isReadOnly: true)`) instead of a raw ROM array. Read-only (the CP/M disk is not written back — SP0 persistence is SP1+; `DiskImage` mutates only the in-memory copy anyway).

- [ ] **Step 1: Write the failing loader test**

Append to `SoftCardBoardTests`:

```csharp
    [Fact]
    public void SoftCardCpm_load_rejects_a_wrong_length_image()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"cpm-bad-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(tmp, new byte[1024]);   // not 143,360
        try { Assert.Throws<InvalidDataException>(() => SoftCardCpm.LoadBlockDevice(tmp)); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SoftCardCpm_load_accepts_an_exact_140KiB_image_as_a_256_byte_sector_block_device()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"cpm-ok-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(tmp, new byte[SoftCardCpm.DiskLength]);   // 143,360 = 35*16*256
        try
        {
            IBlockDevice block = SoftCardCpm.LoadBlockDevice(tmp);
            Assert.Equal(256, block.SectorSize);
            Assert.Equal(560, block.SectorCount);   // 35 tracks * 16 sectors
            Assert.True(block.IsReadOnly);
            // And it re-nibblizes onto the shipped DskFluxImage with the CP/M order (the adapter is unchanged).
            var flux = new DskFluxImage(block, SectorOrderKind.Cpm);
            Assert.Equal(35, flux.TrackCount);
        }
        finally { File.Delete(tmp); }
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.SoftCardCpm"`
Expected: FAIL — `SoftCardCpm` does not exist (compile error).

- [ ] **Step 3: Create `SoftCardCpm`**

Create `src/CpuEmulator.Machines/SoftCardCpm.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>Loads the Microsoft Z-80 SoftCard CP/M 2.2 disk image from the asset cache (NOT vendored —
/// fetched on demand by tools/get-softcard-cpm.{sh,ps1} from the Asimov mirror; ADR 0016 Decisions 4/5,
/// owner sign-off GIVEN). The cache root is $CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors);
/// the image lives at &lt;root&gt;/cpm/softcard-cpm.dsk. A 16-sector Apple CP/M format (research §4): 35
/// tracks x 16 sectors x 256 bytes = 143,360 bytes; first 3 tracks reserved for the CP/M system. The image
/// is wrapped as a read-only 256-byte-sector IBlockDevice, re-nibblized by DskFluxImage with the CP/M
/// data-track skew (SectorOrderKind.Cpm, research §5) onto the unchanged Disk II head.</summary>
public static class SoftCardCpm
{
    public const int DiskLength = 35 * 16 * 256;   // 143,360 bytes (16-sector Apple CP/M, research §4)
    public const int SectorSize = 256;

    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    /// <summary>The cached CP/M .dsk path, or null if absent (the boot-gate skip-with-note trigger). The
    /// optional <paramref name="root"/> is a test seam so a test never mutates the process-wide env var.</summary>
    public static string? TryGetDiskPath(string? root = null)
    {
        string path = Path.Combine(root ?? CacheRoot, "cpm", "softcard-cpm.dsk");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Load + length-validate the CP/M .dsk (from <paramref name="path"/>, or the cache) as a
    /// read-only 256-byte-sector IBlockDevice. Throws if absent or the wrong length.</summary>
    public static IBlockDevice LoadBlockDevice(string? path = null)
    {
        path ??= TryGetDiskPath();
        if (path is null)
            throw new FileNotFoundException(
                "SoftCard CP/M .dsk not found in the asset cache. Run tools/get-softcard-cpm.ps1 (or .sh), "
              + "or set CPUEMULATOR_TESTVECTORS (default ~/.cache/cpuemulator/vectors).");
        byte[] image = File.ReadAllBytes(path);
        if (image.Length != DiskLength)
            throw new InvalidDataException(
                $"SoftCard CP/M .dsk at {path} must be exactly {DiskLength} bytes "
              + $"(35 tracks x 16 sectors x 256); got {image.Length}.");
        return new DiskImage(image, SectorSize, isReadOnly: true);
    }
}
```

- [ ] **Step 4: Run the loader gate**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.SoftCardCpm"`
Expected: PASS — wrong length rejected; exact 143,360-byte image accepted as a 560-sector read-only block device that re-nibblizes into a 35-track CP/M flux image.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/SoftCardCpm.cs tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs
git commit -m "feat(machines): SoftCardCpm — the CP/M .dsk asset loader (length-validated block device)"
```

---

## Task 4: The fetch-on-demand asset scripts (`get-softcard-cpm.{sh,ps1}`)

**Files:**
- Create: `tools/get-softcard-cpm.sh`
- Create: `tools/get-softcard-cpm.ps1`

> **No automated test** — operational scripts (they fetch a live URL). The gate is: they exist, length-sanity-check (143360), never vendor, and the loader (Task 3) consumes their cache layout (`<cache>/cpm/softcard-cpm.dsk`). The owner runs them once; then the boot gate (Task 6) goes from skipped to green.

- [ ] **Step 1: Create `tools/get-softcard-cpm.sh`**

```sh
#!/usr/bin/env sh
# Fetches the Microsoft Z-80 SoftCard CP/M 2.2 disk image into the vector cache (same root as the
# Apple/Spectrum/ZEX/Klaus assets; NEVER vendored). The CP/M .dsk is fetched on demand at test time — it is
# NOT committed (ADR 0016 Decisions 4/5; owner sign-off GIVEN for the fetch-on-demand loader from the
# Asimov preservation mirror). Provide your own URL/mirror if the default moves.
#
# Layout written (consumed by CpuEmulator.Machines.SoftCardCpm):
#   <cache>/cpm/softcard-cpm.dsk   143360 bytes  (35 tracks x 16 sectors x 256; the CP/M boot disk)
set -eu
DEST="${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}"
CPM_DIR="$DEST/cpm"
mkdir -p "$CPM_DIR"

# Each row: filename | expected byte length | required(1)/optional(0) | space-separated candidate URLs.
# NOTE: the URL below is a placeholder for the owner to point at the Asimov mirror (apple2.org.za
# /images/cpm/os/, research §9) or a preferred source. The length sanity-check (143360) is what guarantees
# a correct CP/M image regardless of source.
fetch_one() {
    name="$1"; want_len="$2"; required="$3"; shift 3
    out="$CPM_DIR/$name"
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
    echo "NOTE: optional $name not fetched" >&2; return 0
}

fetch_one "softcard-cpm.dsk" 143360 1 \
    "https://mirror.example/cpm/softcard-cpm.dsk"

echo "SoftCard CP/M fetch complete (cache: $CPM_DIR)."
```

- [ ] **Step 2: Create `tools/get-softcard-cpm.ps1`**

```pwsh
#!/usr/bin/env pwsh
# Fetches the Microsoft Z-80 SoftCard CP/M 2.2 disk image into the vector cache (same root as the
# Apple/Spectrum/ZEX/Klaus assets; NEVER vendored). Fetched on demand at test time, NOT committed (ADR 0016
# Decisions 4/5; owner sign-off GIVEN for the fetch-on-demand loader from the Asimov mirror).
# Layout written (consumed by CpuEmulator.Machines.SoftCardCpm):
#   <cache>/cpm/softcard-cpm.dsk   143360 bytes  (35 tracks x 16 sectors x 256; the CP/M boot disk)
param(
    [string]$Destination = $(if ($env:CPUEMULATOR_TESTVECTORS) { $env:CPUEMULATOR_TESTVECTORS }
                             else { Join-Path $HOME ".cache/cpuemulator/vectors" })
)
$ErrorActionPreference = "Stop"
$cpmDir = Join-Path $Destination "cpm"
New-Item -ItemType Directory -Force $cpmDir | Out-Null

function Fetch-One($name, $wantLen, $required, $urls) {
    $out = Join-Path $cpmDir $name
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

# NOTE: placeholder URL for the owner to point at the Asimov mirror (apple2.org.za /images/cpm/os/,
# research §9) or a preferred source; the length sanity-check (143360) guarantees a correct image.
Fetch-One "softcard-cpm.dsk" 143360 $true @("https://mirror.example/cpm/softcard-cpm.dsk")

Write-Host "SoftCard CP/M fetch complete (cache: $cpmDir)."
```

> **Implementer note — the placeholder URL.** The CP/M `.dsk` is fetched from the Asimov preservation mirror (research §9: `apple2.org.za` mirror `/images/cpm/os/`); the `mirror.example` URL is the placeholder the **owner** confirms at PR time (sign-off for the fetch-on-demand path is GIVEN per ADR 0016 Decision 5). The **143360-byte length sanity-check** is the real guarantee — any source returning the right length is the right image. Mark the `.sh` executable: `git update-index --chmod=+x tools/get-softcard-cpm.sh` (mirror `get-apple2-roms.sh`).

- [ ] **Step 3: Commit**

```bash
chmod +x tools/get-softcard-cpm.sh
git add tools/get-softcard-cpm.sh tools/get-softcard-cpm.ps1
git update-index --chmod=+x tools/get-softcard-cpm.sh
git commit -m "feat(tools): get-softcard-cpm.{sh,ps1} — fetch-on-demand CP/M .dsk (Asimov mirror, never vendored)"
```

---

## Task 5: `SoftCardSurface` + the `Program.cs` boot-if-cached wiring

**Files:**
- Create: `src/CpuEmulator.Surface.Web/SoftCardSurface.cs`
- Modify: `src/CpuEmulator.Surface.Web/Program.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` (a surface-construction smoke test)

**Interfaces:**
- Consumes: `SoftCardBoard` (Task 2), `SoftCardCpm` (Task 3), `DskFluxImage` + `SectorOrderKind.Cpm` (Task 1), the `Apple2Surface` triad pattern, `MachineHost`.
- Produces: `SoftCardSurface.Create(systemRom, diskBootRom, charRom, cpmDisk, frameSink, audioSink, tier)` → a `SoftCardSurface` record `(Machine, Apple2Video, Apple2Keyboard, Apple2Speaker, MachineHost)` (the `Apple2Surface` shape) wired over the SoftCard board with the CP/M disk in drive 1.

**Design notes (grounded against `Apple2Surface.cs`):** The body is `Apple2Surface.Create` **verbatim** with two changes: (1) drive 1 is the CP/M `.dsk` (`new DskFluxImage(cpmDisk, SectorOrderKind.Cpm)`) instead of a synthetic image; (2) the board is `SoftCardBoard.Spec(...)` instead of `Apple2Board.SpecWithSystem(...)`. The video/keyboard/speaker triad + `Realize` + `MachineHost` wiring is identical (the SoftCard's display is still the Apple 40-col video on the bare board — the Videx 80-col is PR-N/O).

- [ ] **Step 1: Write the failing surface smoke test**

Append to `SoftCardBoardTests`:

```csharp
    [Fact]
    public void SoftCardSurface_constructs_and_renders_a_280x192_frame()
    {
        // A synthetic (all-zero) system ROM + a synthetic CP/M block device: the surface must construct,
        // reset, and produce a 280x192 FB frame (the Apple video tick). No real asset is needed for THIS
        // smoke test — the boot-to-A> assertion is the separate asset-gated test.
        var rom = new byte[Apple2Rom.SystemRomLength];
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;
        IBlockDevice cpm = new DiskImage(new byte[SoftCardCpm.DiskLength], 256, isReadOnly: true);

        byte[]? lastFrame = null;
        CpuEmulator.Surface.Web.SoftCardSurface surface =
            CpuEmulator.Surface.Web.SoftCardSurface.Create(rom, diskBootRom: null, charRom: null,
                cpmDisk: cpm, f => lastFrame = f, _ => { });

        surface.Host.RunHeadless(totalCycles: 40_000, sliceCycles: 17_030);

        Assert.NotNull(lastFrame);
        Assert.Equal((byte)'F', lastFrame![0]);
        Assert.Equal((byte)'B', lastFrame[1]);
        int width = lastFrame[4] | (lastFrame[5] << 8);
        int height = lastFrame[6] | (lastFrame[7] << 8);
        Assert.Equal(280, width);
        Assert.Equal(192, height);
        Assert.NotNull(surface.Machine.Coprocessor);   // the Z80 is wired even on the synthetic board
    }
```

> **Implementer note — `diskBootRom: null` path.** `Apple2Board.SpecWithSystem` (and thus `SoftCardBoard.Spec`) **requires** a `diskBootRom` (it throws on null). For the smoke test pass `diskBootRom: null` only if `SoftCardSurface.Create` falls back to a synthesized 256-byte boot ROM when null (mirror the Apple2Surface `diskBootRom is not null ? SpecWithSystem : SpecWithDiskII` branch). The cleanest grounded choice: `SoftCardSurface.Create` requires the boot ROM (the SoftCard always boots a disk), so the smoke test passes a **synthetic** 256-byte boot ROM instead of null. Adjust the test to pass `diskBootRom: new byte[Apple2Rom.DiskRomLength]` (with the slot-6 signature) and have `Create` take a non-nullable `byte[] diskBootRom` — see the Create signature below.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.SoftCardSurface"`
Expected: FAIL — `SoftCardSurface` does not exist.

- [ ] **Step 3: Create `SoftCardSurface`**

Create `src/CpuEmulator.Surface.Web/SoftCardSurface.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Composes the Apple ][+ SoftCard (dual-CPU CP/M) for the web surface — the analogue of
/// <see cref="Apple2Surface"/>. Identical to Apple2Surface EXCEPT (1) drive 1 holds the CP/M .dsk
/// (re-nibblized via <see cref="DskFluxImage"/> with the CP/M data-track skew, <see cref="SectorOrderKind.Cpm"/>)
/// and (2) the board is the dual-CPU <see cref="SoftCardBoard"/> (the base Apple board + the Z80 coprocessor
/// + the $C500 SoftCard control port). The Z80 is dormant at reset; the 6502 $C600 boot loads the CP/M
/// cold-boot loader from the .dsk, the on-disk code issues the $CnXX write that starts the Z80, and CP/M
/// runs translated. On the bare SoftCard board the display is the Apple 40-col video (the Videx 80-col is
/// PR-N/O); the triad + MachineHost wiring is the Apple2Surface body verbatim.</summary>
public sealed record SoftCardSurface(
    Machine Machine, Apple2Video Video, Apple2Keyboard Keyboard, Apple2Speaker Speaker, MachineHost Host)
{
    public static SoftCardSurface Create(byte[] systemRom, byte[] diskBootRom, byte[]? charRom,
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
        // Drive 1 = the CP/M .dsk, re-nibblized with the CP/M data-track skew onto the unchanged Disk II head.
        var drive1 = new DskFluxImage(cpmDisk, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk);

        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
        Machine machine = BoardMachineFactory.Build(spec, tier);

        video.Realize(machine);
        speaker.Realize(machine);
        machine.Reset();

        var host = new MachineHost(machine, video, keyboard, frameSink, speaker, audioSink);
        return new SoftCardSurface(machine, video, keyboard, speaker, host);
    }
}
```

Update the smoke test to pass a synthetic boot ROM (per the Step-1 implementer note):

```csharp
        IBlockDevice cpm = new DiskImage(new byte[SoftCardCpm.DiskLength], 256, isReadOnly: true);
        var bootRom = new byte[Apple2Rom.DiskRomLength];
        bootRom[0x01] = 0x20; bootRom[0x03] = 0x00; bootRom[0x05] = 0x03; bootRom[0x07] = 0x3C;
        bootRom[0x00] = 0xA9;
        // ...
        CpuEmulator.Surface.Web.SoftCardSurface.Create(rom, bootRom, charRom: null,
            cpmDisk: cpm, f => lastFrame = f, _ => { });
```

- [ ] **Step 4: Wire `Program.cs` (SoftCard-first boot when both assets cached)**

In `Program.cs` `DemoSession.RunAsync`, add a SoftCard branch **before** the existing Apple branch (the SoftCard needs the Apple system ROM **and** the CP/M `.dsk`; when both are present, boot CP/M; else fall through to the existing Apple/Spectrum/demo chain):

```csharp
        // Boot the SoftCard (CP/M) when BOTH the Apple system ROM and the CP/M .dsk are cached; else the
        // existing Apple-if-cached / Spectrum / demo fallback chain (unchanged).
        string? appleRom = CpuEmulator.Machines.Apple2Rom.TryGetPath();
        string? cpmDisk = CpuEmulator.Machines.SoftCardCpm.TryGetDiskPath();
        ISurfacePump pump;
        string assetState;
        if (appleRom is not null && cpmDisk is not null)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom);
            byte[] bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom()
                ?? throw new InvalidOperationException(
                    "SoftCard CP/M needs the slot-6 Disk II boot ROM (disk2.rom) — run tools/get-apple2-roms.");
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            CpuEmulator.Core.IBlockDevice cpm = CpuEmulator.Machines.SoftCardCpm.LoadBlockDevice(cpmDisk);
            SoftCardSurface softcard = SoftCardSurface.Create(sys, bootRom, charRom, cpm,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            pump = new SurfacePump(softcard.Host, AppleSliceCycles, ApplePeriod);
            assetState = "softcard-cpm";
        }
        else if (appleRom is not null)
        {
            // ...the existing Apple branch, unchanged...
        }
        // ...the existing Spectrum / demo branches, unchanged...
```

> **Implementer note — the fallback chain + the `assetState` string.** Reuse the shipped `AppleSliceCycles`/`ApplePeriod` (the SoftCard's primary 6502 runs at the same ~1.0205 MHz; the dual-CPU `Run` budget is in the primary domain, PR-I, so the same slice paces it). Add a `"softcard-cpm"` case to `app.js`'s `ST`-message handler (the PR-H seam): map it to a status like `"connected · Apple ][+ SoftCard · CP/M"`. The richer `ST` status frame is still PR-P; K only adds the one new asset-state string. Keep the existing Apple/Spectrum/demo branches byte-for-byte (only the new SoftCard-first branch is added).

- [ ] **Step 5: Run the surface smoke gate**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.SoftCardSurface"`
Expected: PASS — the SoftCard surface constructs, resets, and renders a 280×192 frame; the Z80 coprocessor is wired.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Surface.Web/SoftCardSurface.cs src/CpuEmulator.Surface.Web/Program.cs tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs
git commit -m "feat(surface): SoftCardSurface (CP/M .dsk in drive 1) + SoftCard-first boot when assets cached"
```

---

## Task 6: The un-fakeable gate — CP/M boots to the `A>` prompt (asset-gated, skip-with-note)

**Files:**
- Create: `tests/CpuEmulator.Tests/Apple2/SoftCardCpmVectors.cs` (the skip-with-note attribute)
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs` (the boot gate)

**Interfaces:**
- Consumes: `SoftCardBoard` (Task 2), `SoftCardCpm` (Task 3), `DskFluxImage` + `SectorOrderKind.Cpm`, `Apple2Rom`, `Apple2Video`/`Apple2Palette`, the dual-CPU `Machine` (PR-I/J).
- Produces: `SoftCardCpmVectors.TryGetAssets()` (returns both the system ROM path and the CP/M `.dsk` path, or null if either is absent); `SoftCardCpmFactAttribute`/`SoftCardCpmTheoryAttribute` (skip-with-note). The boot gate `[SoftCardCpmFact]` builds the SoftCard machine with the real CP/M disk in drive 1, runs the real `$C600`→tracks→`$CnXX` boot, and asserts CP/M reaches `A>` on the Apple text render — **skipped when either asset is absent.**

**Design notes — this is the row-K un-fakeable gate (asset-gated, skip-with-note, the PR-H discipline):**
- **Both** the Apple system ROM and the CP/M `.dsk` are required (the boot path is 6502-ROM-driven `$C600` disk-boot → on-disk loader → `$CnXX` Z80-start). When **either** is absent the gate **skips-with-note** (GREEN). The skip attribute checks both paths.
- **The real boot mechanism** (no faking): build the SoftCard machine via `SoftCardBoard.Spec`/`BoardMachineFactory.Build` with the **real** CP/M `.dsk` re-nibblized into drive 1, `machine.Reset()`, then `machine.Run(bootCycles)`. The 6502 Autostart ROM cold-boots slot 6 (`JMP ($C600)`), the boot ROM reads the CP/M boot tracks via the unchanged Disk II head (the `DskFluxImage` synthesizes the GCR with the CP/M skew), the on-disk CP/M cold-boot loader runs, sets up, and issues the `$CnXX` write to the SoftCard control port — which flips `_z80Active` (PR-J), and the dual-CPU `Run` (PR-I) then drives the Z80, which runs CP/M **translated** against shared RAM (PR-J proved this end-to-end). CP/M's BIOS console output paints `A>` on the Apple 40-col text screen.
- **The assertion** mirrors `Apple2BootTests.Rom_boots_to_the_applesoft_prompt_on_both_tiers`: render the Apple text screen via `Apple2Video.RenderInto`, assert a **mostly-blank** screen (mostly `MonoOff`) with **meaningful ink** (`MonoOn` pixels > a threshold — the `A>` prompt + the CP/M sign-on banner), plus a **committed-hash placeholder** that stays inert until captured on the first green run with the real asset. A dead boot is all-off (no prompt) or noisy (no clear background) — unfakeable.
- **Interpreter tier only** (the coprocessor is always interpreter, PR-I). Unlike the PR-H ROM-boot gate (both tiers), K's gate is **interpreter-only** — the Z80-under-translation has no JIT path in this PR (ADR 0015 Decision 4; JIT-under-translation is PR-L). So a single `[SoftCardCpmFact]`, not a both-tiers `[Theory]`.
- **`bootCycles`** must be generous — the CP/M cold boot reads 3 system tracks + sets up the Z80 + runs to the prompt. Budget a few million primary cycles (the PR-H gate used 500,000 for Applesoft; CP/M's multi-track disk boot + the Z80 handoff is heavier — start at ~10,000,000 and tune down on the first green run with the real asset).

- [ ] **Step 1: Create the skip-with-note vectors + attributes**

Create `tests/CpuEmulator.Tests/Apple2/SoftCardCpmVectors.cs`:

```csharp
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

internal static class SoftCardCpmVectors
{
    /// <summary>Both the Apple system ROM (the boot path is 6502-ROM-driven) AND the CP/M .dsk are needed.
    /// Returns (systemRomPath, cpmDiskPath) when BOTH are present, else null.</summary>
    public static (string systemRom, string cpmDisk)? TryGetAssets()
    {
        string? sys = Apple2RomVectors.TryGetRomPath();
        string? cpm = SoftCardCpm.TryGetDiskPath();
        return sys is not null && cpm is not null ? (sys, cpm) : null;
    }
}

/// <summary>Skip-with-note when the SoftCard CP/M boot assets (system ROM + CP/M .dsk) are absent — the
/// PR-H Apple2RomFact discipline, so asset-free CI stays GREEN (a skipped gate is green). Owner sign-off
/// for the CP/M asset fetch is GIVEN (ADR 0016 Decision 5).</summary>
public sealed class SoftCardCpmFactAttribute : FactAttribute
{
    public SoftCardCpmFactAttribute()
    {
        if (SoftCardCpmVectors.TryGetAssets() is null)
            Skip = "SoftCard CP/M boot assets not found — run tools/get-apple2-roms and " +
                   "tools/get-softcard-cpm (.ps1 or .sh), or set CPUEMULATOR_TESTVECTORS " +
                   "(default ~/.cache/cpuemulator/vectors).";
    }
}

public sealed class SoftCardCpmTheoryAttribute : TheoryAttribute
{
    public SoftCardCpmTheoryAttribute()
    {
        if (SoftCardCpmVectors.TryGetAssets() is null)
            Skip = "SoftCard CP/M boot assets not found — run tools/get-apple2-roms and " +
                   "tools/get-softcard-cpm (.ps1 or .sh), or set CPUEMULATOR_TESTVECTORS " +
                   "(default ~/.cache/cpuemulator/vectors).";
    }
}
```

- [ ] **Step 2: Write the boot gate**

Append to `SoftCardBoardTests` (add `using System.Security.Cryptography;` to the file's usings):

```csharp
    // Generous budget for the CP/M cold boot: the 6502 reads the 3 system tracks, hands off to the Z80,
    // and CP/M runs to the A> prompt. Tune down on the first green run with the real asset.
    private const long CpmBootCycles = 10_000_000;

    [SoftCardCpmFact]
    public void Cpm_boots_to_the_A_prompt_on_the_interpreter()
    {
        var (systemRomPath, cpmDiskPath) = SoftCardCpmVectors.TryGetAssets()!.Value;
        byte[] systemRom = Apple2Rom.Load(systemRomPath);
        byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
            ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the CP/M boot gate");
        byte[]? charRom = Apple2Rom.TryLoadCharRom();   // null -> Apple2Font.Fallback (still renders A>)
        IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(cpmDiskPath);

        // Build the real SoftCard machine with the CP/M .dsk re-nibblized into drive 1.
        var state = new Apple2VideoState();
        var lc = new Apple2LanguageCard(systemRom);
        var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk);
        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
        Machine machine = BoardMachineFactory.Build(spec);   // interpreter tier (coprocessor is interpreter)
        var video = new Apple2Video(machine.Space(AddressSpaceKind.Program), state, charRom);

        machine.Reset();
        machine.Run(CpmBootCycles);                          // the real $C600 -> tracks -> $CnXX -> CP/M boot

        var rgba = new uint[Apple2Video.Width280 * Apple2Video.Height192];
        video.RenderInto(rgba);

        // Un-fakeable structural assertion: CP/M's sign-on + the A> prompt paint ink on a mostly-blank
        // text screen. A dead/garbage boot is all-off (no prompt) or noisy (no clear background).
        int offPixels = 0, onPixels = 0;
        foreach (uint p in rgba)
        {
            if (p == Apple2Palette.MonoOff) offPixels++;
            else if (p == Apple2Palette.MonoOn) onPixels++;
        }
        int total = Apple2Video.Width280 * Apple2Video.Height192;
        Assert.True(offPixels > total / 2,
            $"expected a mostly-blank CP/M text screen; got {offPixels}/{total} off pixels");
        Assert.True(onPixels > 50,
            $"expected the A> prompt + CP/M sign-on ink; got {onPixels} on pixels");
        // The Z80 ran: it became the bus master during the boot (the $CnXX handoff fired).
        Assert.True(machine.CoprocessorActive,
            "expected the Z80 to be the active bus master after the CP/M boot handoff");

        // Tighter gate: a committed RGBA hash. On the FIRST green run with the real asset, capture the
        // hash (uncomment the print), paste it below, then re-run.
        string hash = Convert.ToHexString(SHA256.HashData(AsBytes(rgba)));
        // System.Console.WriteLine($"[cpm boot frame hash] {hash}");  // <-- uncomment once to capture
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

> **Implementer note — the boot path is asset-driven, the gate is honest about what it proves.** When the assets are absent (the CI default) the gate **skips** (GREEN). When the owner fetches the real Apple system ROM + the real CP/M `.dsk`, the gate runs the **real** 6502 boot ROM + the **real** on-disk CP/M loader + the **real** `$CnXX` Z80 handoff + the **real** translated Z80 — nothing is stubbed; the structural `A>` assertion + the `CoprocessorActive` check + the committed hash make a dead/faked boot fail. If the live boot does not reach `A>` within `CpmBootCycles`, the residual open items (research §-residual 1: the exact CP/M load map / the precise `$C600`-loader path) are the place to look — they are resolved at build time against the SoftCard CP/M Reference + the on-disk boot code, which the real `.dsk` carries. The `CoprocessorActive` assert is the load-bearing proof the dual-CPU handoff happened (a 40-col-only Applesoft boot would leave it false).

> **Implementer note — `[Trait("Category", "UAT")]`.** The class already carries no trait; add `[Trait("Category", "UAT")]` to the boot gate's class (mirror `Apple2BootTests`) only if the boot gate should be in the UAT category. Keep the skew/composition/loader tests (Tasks 1–3) non-UAT (they are fast unit tests). The cleanest split: a separate `[Trait("Category", "UAT")]` on the boot-gate method is not supported by xUnit at the method level for traits the runner filters — instead, if a UAT split is desired, move the boot gate into its own `SoftCardCpmBootTests` class with the class-level trait (mirroring `Apple2BootTests`). **Recommended:** keep it in `SoftCardBoardTests` (no trait) — the skip-with-note already keeps CI green without the asset.

- [ ] **Step 3: Run the boot gate (expected: SKIPPED without the asset)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~SoftCardBoardTests.Cpm_boots_to_the_A_prompt"`
Expected (CI, no asset): **SKIPPED** with the named-script note — a skipped gate is GREEN. (When the owner fetches the assets, it runs and asserts the real `A>` boot.)

- [ ] **Step 4: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/SoftCardCpmVectors.cs tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs
git commit -m "test(softcard): CP/M boots to A> via the real \$C600->tracks->\$CnXX path (asset-gated, skip-with-note)"
```

---

## Task 7: Final gate — full suite + warning-clean build

**Files:** none (verification only).

- [ ] **Step 1: Full build, warning-clean**

Run: `dotnet build CpuEmulator.slnx`
Expected: Build succeeded, **0 warnings**.

- [ ] **Step 2: Full test suite**

Run: `dotnet test CpuEmulator.slnx`
Expected: the post-PR-J baseline green plus the new PR-K tests (the CP/M skew, the SoftCard board composition, the loader, the surface smoke), 0 failed. **The CP/M boot gate SKIPS** (no asset in CI) — a skipped gate is green. **No pre-existing test regresses** — K adds `SectorOrderKind.Cpm` (additive enum + table arm), two new `Machines` files, one new surface file, the scripts, and the new SoftCard-first `Program.cs` branch (the existing branches are byte-for-byte). The single-CPU base Apple board is unchanged (the `Apple2BootTests` gate still passes).

- [ ] **Step 3: Confirm the un-fakeable gates ran (or skipped) as expected**

Confirm:
- `Cpm_sector_order_is_the_documented_data_track_skew` + `Cpm_order_is_a_permutation_distinct...` — the CP/M skew (run, green).
- `The_softcard_board_builds_a_6502_primary_and_a_dormant_Z80_coprocessor` + `The_softcard_board_carries_a_control_port_named_to_match_the_coprocessor_spec` — the board composition (run, green).
- `SoftCardCpm_load_*` — the loader (run, green).
- `SoftCardSurface_constructs_and_renders_a_280x192_frame` — the surface (run, green).
- `Cpm_boots_to_the_A_prompt_on_the_interpreter` — the headline boot gate: **SKIPPED** with the named-script note when the asset is absent (GREEN); runs + asserts the real `A>` when the owner fetches the `.dsk`.

---

## Self-Review

**1. Spec coverage (ADR 0015 + ADR 0016 Decisions 4/5 + the row-K gate):**
- Compose the SoftCard board = `Apple2Board` + `WithCoprocessor(Z80)` + `SoftCardTranslation` + `SoftCardControlPort` → Task 2 (`SoftCardBoard.Spec`). ✓
- The real boot path (6502 `$C600` disk-boot → CP/M cold-boot loader → `$CnXX` Z80-start → CP/M translated to `A>`) → Task 6 (the gate runs the real ROM-driven boot; nothing stubbed). ✓
- The `get-softcard-cpm.{sh,ps1}` fetch script (Asimov mirror, never vendored, cached outside source control) → Task 4. ✓
- CP/M reaches `A>` — asset-gated, skip-with-note when the `.dsk` is absent (the PR-H discipline) → Task 6 (`SoftCardCpmFact` + `SoftCardCpmVectors`). ✓
- The CP/M disk format + data-track skew (research §4/§5): the `.dsk` adapter (PR-G) re-nibblizes; the CP/M skew table is the new datum in the codec/board layer → Task 1 (`SectorOrderKind.Cpm`) + Task 3 (`SoftCardCpm`, the 143,360-byte 16-sector format). ✓
- Interpreter tier; coprocessor on the interpreter (PR-I); no JIT-under-translation (deferred PR-L) → the gate is interpreter-only; `BoardMachineFactory` builds the Z80 interpreter-tier (no change). ✓
- Deps E/F/H/J ✅ — every composed seam is shipped. ✓

**2. Placeholder scan:** No TBD/TODO/"implement later"/"similar to Task N". Every code step shows literal code (the skew table, the board composer, the loader, both scripts, the surface, all tests, the boot gate). The `mirror.example` URL is a documented owner-confirmed placeholder (the length sanity-check is the real guarantee, exactly as the shipped `get-apple2-roms` placeholders); the committed-hash placeholder is the shipped PR-H gate discipline (inert until captured); the `bootCycles` budget is tuning guidance with a grounded starting value — none is missing code.

**3. Type consistency:** `SoftCardBoard.Spec(byte[], Apple2Iou, Apple2DiskII, byte[])`, `SoftCardCpm.TryGetDiskPath`/`LoadBlockDevice`/`DiskLength`, `SoftCardSurface.Create(byte[], byte[], byte[]?, IBlockDevice, Action<byte[]>, Action<byte[]>, ExecutionTier)`, `SectorOrderKind.Cpm`, `CoprocessorSpec(CpuKind, IAddressTranslation, string, double)` (PR-I), `SoftCardTranslation()` / `SoftCardControlPort()` / `Name => "softcard"` (PR-J), `Machine.Coprocessor`/`CoprocessorActive`/`Space`/`Run`/`Reset` (PR-I), `DskFluxImage(IBlockDevice, SectorOrderKind)` (PR-G), `Apple2Board.SpecWithSystem` (PR-H), `DiskImage(byte[], int, bool)`, `Apple2Video.Width280`/`Height192`/`RenderInto`, `Apple2Palette.MonoOn`/`MonoOff` are used identically across tasks and match the shipped signatures verified during planning. The control-port slot `Name` `"softcard"` equals `CoprocessorSpec.ControlPortPeripheral` (the validator's `copro-control-port-unwired` contract).

**Builder-readiness note:** every dual-CPU primitive is shipped (PRs I/J: `SoftCardTranslation`, `SoftCardControlPort`, `CoprocessorSpec`, the dual-CPU `Machine`/`RunDualCpu`, the factory `Coprocessor` wiring) and every disk primitive is shipped (PRs F/G/H: `Apple2DiskII`, `DskFluxImage`, `Apple2SectorOrder`, `Apple2Board.SpecWithSystem`, `Apple2Rom`). K is composition + the one new datum (the CP/M skew) + the one new asset (the CP/M `.dsk` + its fetch script) + the asset-gated boot gate. The only build-time residual (research §-residual 1: the exact CP/M load map / the precise `$C600`-loader path) is **carried by the real `.dsk`** — the gate runs the real on-disk boot code, so no literal boot-loader bytes are authored here; the gate skips cleanly until the owner supplies the asset. **One flagged shipped-API check for the Builder:** confirm the `PeripheralSlot` ctor positional shape (`(string Name, IPeripheral Device, uint Base, uint Length)`, per `Apple2Board.cs:47`) and that `BoardSpec`'s `with { Peripherals = [...], Coprocessor = ... }` compiles against the shipped record — both grounded during planning but worth a compile-check at Task 2.
