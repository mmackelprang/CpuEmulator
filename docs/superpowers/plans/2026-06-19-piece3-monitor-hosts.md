# Piece #3 — Monitor Hosts (boot any BoardSpec) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the console host (`CpuEmulator.Host`) boot **any** of the five reference boards (`6502`, `Z80`, `68000`, `8086` ReferenceSbc + the `Breadboard6502` board-spec) in the CPU-agnostic monitor/REPL, selected by `--board <name>`, building the `Machine` from a `BoardSpec` via `BoardMachineFactory` instead of hand-wiring `Breadboard6502`.

**Architecture:** A new `BoardRegistry` in `CpuEmulator.Host` enumerates the available boards by name and produces, for each, a `BootedBoard` (its built `Machine` + the `SimpleUart` instance the host bridges stdin/stdout through + the board banner). The four `ReferenceSbc` boards reuse the **exact boot ROMs proven to print `OK\r`** in the piece-#2 smokes; the `breadboard6502` board reuses `Breadboard6502Board.Spec` + `DemoRom.Build()` (proven byte-identical in piece #1). `Program.Main` parses `--board`, looks the board up in the registry, wires `uart.OnTransmit`/`FeedInput` to the console, and runs the existing `MonitorEngine` + `MonitorRepl` unchanged. One genuine monitor generalization ships: the engine's `TryParseAbsoluteTarget` is widened from 16-bit-only to the engine's address width, so the `a` command's branch-offset resolution works on the 24-bit (68000) and 20-bit (8086) boards.

**Tech Stack:** C# / .NET, xUnit, the existing `CpuEmulator.Core` / `.Machines` / `.Monitor` / `.Peripherals` assemblies. Build with `dotnet build`, test with `dotnet test`.

---

## The monitor-agnosticism finding (recon result that shapes this plan)

The monitor **engine** (`MonitorEngine` / `MonitorRepl`) is genuinely CPU-agnostic: it sits over `ICpuCore` + `IAddressSpace` + `IMonitorSupport`, enumerates registers via `_cpu.RegisterNames` + `_support.RegisterBits(name)`, disassembles via `_support.Disassemble`/`InstructionLength`, derives address width from `IAddressSpace.AddressBits`, and names the PC via `_support.ProgramCounterName`. No 6502 register names or mnemonics are hard-coded. **Two findings constrain the plan:**

1. **The `a`-command absolute-target parser is 16-bit-only (a real gap this plan closes).** `MonitorEngine.TryParseAbsoluteTarget` (`MonitorEngine.cs:325`) requires exactly `$HHHH` (`t.Length == 5`) and parses with `ushort.TryParse`. On the 68000 (24-bit) and 8086 (20-bit) boards an absolute branch target like `$012345` is rejected, so `a $ADDR BNE $TARGET`-style auto-offset assembly fails. **Task 8 generalizes it** to the engine's `_addressDigits`/`_addressMask`.

2. **The 68000 disassembler is a stub (a scoped limitation, NOT closed here).** The generated `M68000Cpu.Disassemble` is `opcode switch { _ => "???" }` — it returns `"???"` for every opcode (the 68000 uses the field-grammar decoder, so the generator never populated a flat per-opcode disassembly table; `M68000Cpu.AssembleOpcode`/`KnownMnemonic` are likewise stubs). `InstructionLength` **is** real (routes through `DescriptorFor(key).FixedLength`), so the monitor's byte-walk and step/run are correct — only the mnemonic text is `???`. The other three CPUs have real disassembly (6502: 152 arms with operands; Z80: 1605 arms; 8086: 284 arms — mnemonics present, ModR/M operands not rendered). Writing a full 68000 field-grammar disassembler is large net-new work, outside "wire the host onto a board-spec," and the design's non-goals don't ask for it. **This plan therefore:** (a) renders 68000 instructions honestly as `???` (the byte-walk + length are still right); (b) makes the **68000 host smoke assert registers + `OK\r` + PC-advance, NOT mnemonics** (Task 14); (c) documents the limitation in the user guide + records a follow-on candidate in the roadmap (Task 17). The 6502/Z80/8086 host smokes assert **real disassembly mnemonics** (Tasks 11, 12, 15).

---

## File Structure

**New files (all in `CpuEmulator.Host`):**

- `src/CpuEmulator.Host/BootedBoard.cs` — a small record bundling what the host needs to run a board: the built `Machine`, the `SimpleUart` to bridge, and the banner string. One responsibility: the host's view of a booted board.
- `src/CpuEmulator.Host/BoardRoms.cs` — the per-CPU boot-ROM image builders for the four `ReferenceSbc` boards (the exact byte sequences proven to print `OK\r` in the piece-#2 smokes), plus a re-export of the 6502 `DemoRom`. One responsibility: boot-ROM images.
- `src/CpuEmulator.Host/BoardRegistry.cs` — the name→board map: enumerable board names + a `TryBoot(name, out BootedBoard, out error)` that builds the `Machine` via `BoardMachineFactory`. One responsibility: board lookup + construction.
- `tests/CpuEmulator.Tests/Host/BoardRegistryTests.cs` — registry unit tests (names enumerable; each name boots; unknown name fails cleanly; default).
- `tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs` — the four per-CPU host smokes + the 6502 board smoke (boot → monitor renders the right per-CPU registers/disasm → step/run advances → UART round-trips).

**Modified files:**

- `src/CpuEmulator.Host/CpuEmulator.Host.csproj` — add the `CpuEmulator.Machines` project reference + the three CPU project references the registry needs (Z80/68000/8086 cores) so `BoardMachineFactory` can resolve them. *(Confirmed in Task 1 whether the factory already brings them transitively.)*
- `src/CpuEmulator.Host/HostOptions.cs` — add the `--board <name>` option + a `Board` property + a `ListBoards` flag (`--board list`), and update `Usage`.
- `src/CpuEmulator.Host/Program.cs` — replace `new Breadboard6502()` with a `BoardRegistry.TryBoot(...)` lookup; generalize the UART bridge + REPL launch over the returned `BootedBoard`; handle `--board list`.
- `src/CpuEmulator.Monitor/MonitorEngine.cs` — generalize `TryParseAbsoluteTarget` from 16-bit to the engine's address width.
- `tests/CpuEmulator.Tests/Monitor/` — a test for the widened `TryParseAbsoluteTarget` (via the public `TryAssembleAt`).
- `docs/user-guide/monitor-reference.md` — document `--board`, the board list, and the 68000-disassembly limitation.
- `docs/ROADMAP.md` — move "non-6502 monitor host" from deferred to shipped; record the 68000-disassembler follow-on candidate.

**Removed / retired:**

- `src/CpuEmulator.Host/Breadboard6502.cs` — the hand-wired board class is retired once `Program` boots via the registry (the design's non-goal is "keeping a separate hand-wired path"). Its `DemoRom.Build()` ROM survives via `BoardRoms`/`Breadboard6502Board.Spec`. *(Retired in Task 9, after the registry's 6502 path is proven byte-identical in Task 7.)*

---

## Conventions for every task

- **Branch:** all implementation lands on a feature branch (per the repo's branch-per-change policy): `git switch -c feat/piece3-monitor-hosts` before the first code commit. *(This plan document itself lands separately on `docs/piece3-plan`.)*
- **Build:** `dotnet build CpuEmulator.sln -c Debug` from the repo root.
- **Test (one class):** `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`.
- **Test (one method):** append `.<MethodName>` to the filter.
- Commit after each task's tests pass (conventional commits, scope `piece3`).

---

## Task 0: Branch + reference recon (no code yet)

**Files:** none (verification only)

- [ ] **Step 1: Create the feature branch**

```bash
git switch main
git pull --ff-only
git switch -c feat/piece3-monitor-hosts
```

- [ ] **Step 2: Confirm the host does NOT yet reference Machines**

Run: `grep -n "CpuEmulator.Machines" src/CpuEmulator.Host/CpuEmulator.Host.csproj`
Expected: no output (the reference is absent — Task 1 adds it).

- [ ] **Step 3: Confirm the piece-#2 ROM idioms are present (the smokes this plan reuses)**

Run: `ls tests/CpuEmulator.Tests/Machines/ReferenceSbc8086Tests.cs tests/CpuEmulator.Tests/Machines/ReferenceSbc68000Tests.cs tests/CpuEmulator.Tests/Machines/ReferenceSbcZ80Tests.cs`
Expected: all three paths exist (their `BuildRom`/program bytes are copied verbatim into `BoardRoms` in Task 4).

---

## Task 1: Add the project references the registry needs

**Files:**
- Modify: `src/CpuEmulator.Host/CpuEmulator.Host.csproj`

- [ ] **Step 1: Write a failing reference-resolution probe test**

This proves the Host assembly can name `BoardMachineFactory` and the four `CpuKind`s. Create the test file:

**Create:** `tests/CpuEmulator.Tests/Host/BoardRegistryTests.cs`

```csharp
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Host;

public class BoardRegistryTests
{
    [Fact]
    public void Machines_assembly_is_referable_from_host_test_context()
    {
        // A trivial proof the Host project graph can resolve BoardMachineFactory + ReferenceSbc:
        // build a 68000 spec and a machine from it. (Replaced by real registry tests in Task 6.)
        BoardSpec spec = ReferenceSbc.Build(
            CpuKind.M68000, new SimpleUart(), new IntervalTimer(), new byte[0x1_0000]);
        Machine machine = BoardMachineFactory.Build(spec, ExecutionTier.Interpreter);
        Assert.Equal("referencesbc-68000", machine.Name);
    }
}
```

> NOTE: the expected `machine.Name` string is confirmed in Step 2 below — read the actual name `ReferenceSbc.Build68000` assigns and use it verbatim. If it differs, fix the literal here before running.

- [ ] **Step 2: Confirm the 68000 board's actual `Name` and set the literal**

Run: `grep -n "\"referencesbc\|new BoardSpec\|Name:\|^        new(" src/CpuEmulator.Machines/ReferenceSbc.cs | head -20`
Read the `Name` the `Build68000` path assigns (the first positional arg of the `BoardSpec` it returns). Edit the `Assert.Equal(...)` literal in Step 1 to match it exactly.

- [ ] **Step 3: Run the test to verify it fails (unresolved reference)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~BoardRegistryTests.Machines_assembly_is_referable"`
Expected: FAIL — but check the failure mode. The test project already references `CpuEmulator.Machines` (the piece-#2 smokes use it), so this test likely **compiles and passes** immediately. That is fine: its purpose is to confirm the test-side graph. The Host-side reference is what Task 5 needs; verify it next.

- [ ] **Step 4: Add the Machines reference (+ the three CPU cores) to the HOST csproj**

The Host needs these so `Program.cs` (Task 9) can call `BoardMachineFactory` and the registry can name the boards. Edit `src/CpuEmulator.Host/CpuEmulator.Host.csproj`, adding to the existing `<ItemGroup>` of `ProjectReference`s:

```xml
    <ProjectReference Include="..\CpuEmulator.Machines\CpuEmulator.Machines.csproj" />
    <ProjectReference Include="..\CpuEmulator.Cpus.Z80\CpuEmulator.Cpus.Z80.csproj" />
    <ProjectReference Include="..\CpuEmulator.Cpus.M68000\CpuEmulator.Cpus.M68000.csproj" />
    <ProjectReference Include="..\CpuEmulator.Cpus.M8086\CpuEmulator.Cpus.M8086.csproj" />
```

> NOTE: `CpuEmulator.Machines` may already transitively reference the CPU cores via `CpuCoreFactory`. If `dotnet build` in Step 5 succeeds with ONLY the Machines reference, the three CPU references are redundant — remove them. Add them only if the build complains about an unresolvable core type at the Host layer. The Machines reference itself is always required.

- [ ] **Step 5: Build to verify the Host project resolves Machines**

Run: `dotnet build src/CpuEmulator.Host/CpuEmulator.Host.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Host/CpuEmulator.Host.csproj tests/CpuEmulator.Tests/Host/BoardRegistryTests.cs
git commit -m "build(piece3): host references CpuEmulator.Machines (board-spec boot path)"
```

---

## Task 2: `BootedBoard` — the host's view of a booted board

**Files:**
- Create: `src/CpuEmulator.Host/BootedBoard.cs`
- Test: covered by Task 6 (registry) — this is a pure data record, tested through its consumer.

- [ ] **Step 1: Write the record**

**Create:** `src/CpuEmulator.Host/BootedBoard.cs`

```csharp
using CpuEmulator.Core;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Host;

/// <summary>
/// What the console host needs to run one board: the built <see cref="Machine"/>, the
/// <see cref="SimpleUart"/> the host bridges console stdin/stdout through (the board's
/// memory-mapped UART instance — the host wires <c>OnTransmit</c> and <c>FeedInput</c> to it),
/// and a one-line banner. Produced by <see cref="BoardRegistry"/>; consumed by Program.Main.
/// </summary>
public sealed record BootedBoard(Machine Machine, SimpleUart Uart, string Banner)
{
    /// <summary>A monitor engine over this board's CPU + program space, wired through
    /// Machine.Run so monitor g/s tick the scheduled peripherals (matching the retired
    /// Breadboard6502.NewMonitor wiring exactly).</summary>
    public MonitorEngine NewMonitor() =>
        new(Machine.Cpu, Machine.Space(AddressSpaceKind.Program), (IMonitorSupport)Machine.Cpu,
            Machine.Run);
}
```

> NOTE on the cast: every CPU core implements both `ICpuCore` and `IMonitorSupport` (confirmed: `Mos6502Cpu`, `Z80Cpu`, `M68000Cpu`, `M8086Cpu` all `: ICpuCore, IMonitorSupport`). `Machine.Cpu` is typed `ICpuCore`, so the `support` argument needs the cast.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/CpuEmulator.Host/CpuEmulator.Host.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/CpuEmulator.Host/BootedBoard.cs
git commit -m "feat(piece3): BootedBoard record (machine + bridged UART + banner)"
```

---

## Task 3: `BoardRoms` — the 6502 demo ROM accessor

**Files:**
- Create: `src/CpuEmulator.Host/BoardRoms.cs`
- Test: `tests/CpuEmulator.Tests/Host/BoardRomsTests.cs`

This task lifts the 6502 ROM source. The remaining three CPU ROMs are added in Task 4 (kept separate so each is its own commit + test).

- [ ] **Step 1: Write the failing test**

**Create:** `tests/CpuEmulator.Tests/Host/BoardRomsTests.cs`

```csharp
using CpuEmulator.Host;
using Xunit;

namespace CpuEmulator.Tests.Host;

public class BoardRomsTests
{
    [Fact]
    public void Mos6502_demo_rom_is_8_kib()
    {
        byte[] rom = BoardRoms.Mos6502Demo();
        Assert.Equal(0x2000, rom.Length);
    }

    [Fact]
    public void Mos6502_demo_rom_carries_the_reset_vector_to_entry()
    {
        // The demo ROM image carries RESET ($FFFC/$FFFD) -> $E000. In the 8 KiB image
        // (base $E000) that is offset $1FFC/$1FFD = 0x00, 0xE0 (little-endian $E000).
        byte[] rom = BoardRoms.Mos6502Demo();
        Assert.Equal(0x00, rom[0x1FFC]);
        Assert.Equal(0xE0, rom[0x1FFD]);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~BoardRomsTests"`
Expected: FAIL — `BoardRoms` does not exist.

- [ ] **Step 3: Write `BoardRoms` with the 6502 accessor**

**Create:** `src/CpuEmulator.Host/BoardRoms.cs`

```csharp
namespace CpuEmulator.Host;

/// <summary>
/// Boot-ROM images for the host's reference boards. The 6502 image is the assembled
/// breadboard demo (hello-print + polled echo); the Z80/68000/8086 images are the tiny
/// "print OK\r then self-loop" boot programs proven to round-trip in the piece-#2 smokes
/// (tests/CpuEmulator.Tests/Machines/ReferenceSbc*Tests.cs) — copied here byte-for-byte so
/// the host boots the same provably-runnable programs.
/// </summary>
public static class BoardRoms
{
    /// <summary>The 6502 breadboard demo ROM ($E000-$FFFF, 8 KiB): hello-print then a polled
    /// echo loop, with all vectors -> $E000. Identical to the retired Breadboard6502's ROM.</summary>
    public static byte[] Mos6502Demo() => DemoRom.Build();
}
```

> NOTE: `DemoRom` stays in `CpuEmulator.Host/DemoRom.cs` (it is unchanged and still public-static). `BoardRoms.Mos6502Demo()` is the single seam the registry calls, so the Z80/68000/8086 ROMs (Task 4) join the same class.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~BoardRomsTests"`
Expected: PASS (both methods).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Host/BoardRoms.cs tests/CpuEmulator.Tests/Host/BoardRomsTests.cs
git commit -m "feat(piece3): BoardRoms.Mos6502Demo (6502 boot image accessor)"
```

---

## Task 4: `BoardRoms` — the Z80 / 68000 / 8086 boot ROMs

**Files:**
- Modify: `src/CpuEmulator.Host/BoardRoms.cs`
- Modify: `tests/CpuEmulator.Tests/Host/BoardRomsTests.cs`

These are the exact byte sequences from the piece-#2 smokes (proven to print `OK\r`).

- [ ] **Step 1: Write the failing tests for the three ROMs' shape**

Append to `tests/CpuEmulator.Tests/Host/BoardRomsTests.cs` (inside the class):

```csharp
    [Fact]
    public void Z80_boot_rom_is_8_kib_and_blank_the_program_runs_from_ram()
    {
        // The Z80 boots from RAM at $0000; its ROM image is unused by the boot, but the
        // recipe requires an 8 KiB image. The registry pokes the program into RAM at boot.
        byte[] rom = BoardRoms.Z80Boot();
        Assert.Equal(0x2000, rom.Length);
    }

    [Fact]
    public void Z80_boot_program_is_the_OK_writer()
    {
        // LD A,'O' / LD ($C000),A ... ends with HALT (0x76).
        byte[] prog = BoardRoms.Z80BootProgram();
        Assert.Equal(0x3E, prog[0]);          // LD A,imm
        Assert.Equal((byte)'O', prog[1]);
        Assert.Equal(0x76, prog[^1]);         // HALT
    }

    [Fact]
    public void M68000_boot_rom_is_64_kib_with_reset_vectors()
    {
        byte[] rom = BoardRoms.M68000Boot();
        Assert.Equal(0x1_0000, rom.Length);
        // PC vector (big-endian long at $4) -> program entry $00000008.
        Assert.Equal(0x00, rom[0x4]);
        Assert.Equal(0x00, rom[0x5]);
        Assert.Equal(0x00, rom[0x6]);
        Assert.Equal(0x08, rom[0x7]);
    }

    [Fact]
    public void I8086_boot_rom_is_64_kib_with_far_jmp_at_the_reset_entry()
    {
        byte[] rom = BoardRoms.I8086Boot();
        Assert.Equal(0x1_0000, rom.Length);
        // Reset entry at image offset 0xFFF0 = physical 0xFFFF0: FAR JMP F000:0000 = EA 00 00 00 F0.
        Assert.Equal(0xEA, rom[0xFFF0]);
        Assert.Equal(0x00, rom[0xFFF1]);
        Assert.Equal(0x00, rom[0xFFF2]);
        Assert.Equal(0x00, rom[0xFFF3]);
        Assert.Equal(0xF0, rom[0xFFF4]);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~BoardRomsTests"`
Expected: FAIL — `Z80Boot` / `Z80BootProgram` / `M68000Boot` / `I8086Boot` do not exist.

- [ ] **Step 3: Add the three ROM builders to `BoardRoms`**

Append to `src/CpuEmulator.Host/BoardRoms.cs` (inside the class):

```csharp
    /// <summary>The Z80 boot ROM image (8 KiB). Unused by the Z80 boot itself (the Z80 runs
    /// from RAM at $0000); the registry pokes <see cref="Z80BootProgram"/> into RAM. Present
    /// because the ReferenceSbc(Z80) recipe requires an 8 KiB ROM image.</summary>
    public static byte[] Z80Boot() => new byte[0x2000];

    /// <summary>The Z80 "print OK\r then HALT" program, poked into RAM at $0000 at boot.
    /// Copied verbatim from ReferenceSbcZ80Tests (the piece-#2 OK\r smoke).</summary>
    public static byte[] Z80BootProgram() =>
    [
        0x3E, 0x4F,             // LD A,'O'
        0x32, 0x00, 0xC0,       // LD ($C000),A   ; UART DATA at $C000
        0x3E, 0x4B,             // LD A,'K'
        0x32, 0x00, 0xC0,       // LD ($C000),A
        0x3E, 0x0D,             // LD A,CR
        0x32, 0x00, 0xC0,       // LD ($C000),A
        0x76,                   // HALT
    ];

    /// <summary>The 68000 boot ROM (64 KiB low ROM): reset vectors (SSP at $0, PC at $4) +
    /// a program at $0008 that writes "OK\r" out the UART at $010000, then self-loops. The
    /// bus is BIG-ENDIAN, so vectors + opcode words are MSB-first. Copied verbatim from
    /// ReferenceSbc68000Tests (the piece-#2 OK\r smoke).</summary>
    public static byte[] M68000Boot()
    {
        const uint programEntry = 0x0000_0008;
        const uint uartData = 0x0001_0000;
        var rom = new byte[0x1_0000];

        WriteLongBE(rom, 0x0, 0x0002_0000);    // initial SSP -> a mapped supervisor stack
        WriteLongBE(rom, 0x4, programEntry);   // initial PC -> the program

        int p = (int)programEntry;
        foreach (byte ch in new byte[] { (byte)'O', (byte)'K', (byte)'\r' })
        {
            rom[p++] = 0x70; rom[p++] = ch;                                      // MOVEQ #ch,D0
            rom[p++] = 0x13; rom[p++] = 0xC0;                                    // MOVE.B D0,(abs).L
            rom[p++] = (byte)(uartData >> 24); rom[p++] = (byte)(uartData >> 16); // abs-long hi word
            rom[p++] = unchecked((byte)(uartData >> 8)); rom[p++] = unchecked((byte)uartData); // lo word
        }
        rom[p++] = 0x60; rom[p++] = 0xFE;      // BRA.s *  (1-instruction self-loop)
        return rom;
    }

    /// <summary>The 8086 boot ROM (64 KiB high ROM, $F0000-$FFFFF). The body at offset 0
    /// (physical 0xF0000) sets DS=0xA000 and writes "OK\r" out the UART at physical 0xA0000,
    /// then self-loops; the reset entry at offset 0xFFF0 (physical 0xFFFF0) FAR-JMPs to the
    /// body (the body is too big for the 16 bytes below the top of memory). Copied verbatim
    /// from ReferenceSbc8086Tests (the piece-#2 OK\r smoke).</summary>
    public static byte[] I8086Boot()
    {
        const uint romBase = 0xF_0000;
        const uint resetEntryPhysical = 0xF_FFF0;
        var rom = new byte[0x1_0000];

        int p = 0;
        rom[p++] = 0xB8; rom[p++] = 0x00; rom[p++] = 0xA0;   // MOV AX,0xA000
        rom[p++] = 0x8E; rom[p++] = 0xD8;                    // MOV DS,AX  (DS:0000 = physical 0xA0000)
        foreach (byte ch in new byte[] { (byte)'O', (byte)'K', (byte)'\r' })
        {
            rom[p++] = 0xB0; rom[p++] = ch;                  // MOV AL,ch
            rom[p++] = 0xA2; rom[p++] = 0x00; rom[p++] = 0x00; // MOV [0x0000],AL
        }
        rom[p++] = 0xEB; rom[p++] = 0xFE;                    // JMP short *  (self-loop)

        int e = (int)(resetEntryPhysical - romBase);         // 0xFFF0
        rom[e++] = 0xEA; rom[e++] = 0x00; rom[e++] = 0x00; rom[e++] = 0x00; rom[e++] = 0xF0; // JMP F000:0000
        return rom;
    }

    private static void WriteLongBE(byte[] buf, int at, uint value)
    {
        buf[at + 0] = (byte)(value >> 24);
        buf[at + 1] = (byte)(value >> 16);
        buf[at + 2] = (byte)(value >> 8);
        buf[at + 3] = (byte)value;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~BoardRomsTests"`
Expected: PASS (all six methods).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Host/BoardRoms.cs tests/CpuEmulator.Tests/Host/BoardRomsTests.cs
git commit -m "feat(piece3): BoardRoms Z80/68000/8086 boot images (piece-2 OK\\r programs)"
```

---

## Task 5: `BoardRegistry` — names + `TryBoot` (the four ReferenceSbc boards)

**Files:**
- Create: `src/CpuEmulator.Host/BoardRegistry.cs`
- Test: `tests/CpuEmulator.Tests/Host/BoardRegistryTests.cs` (extended in Task 6)

- [ ] **Step 1: Write the registry**

**Create:** `src/CpuEmulator.Host/BoardRegistry.cs`

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Host;

/// <summary>
/// The host's catalog of bootable boards, keyed by lowercase name. Each entry builds a
/// validated <see cref="BoardSpec"/> (via <see cref="ReferenceSbc"/> or
/// <see cref="Breadboard6502Board"/>), compiles it to a <see cref="Machine"/> through
/// <see cref="BoardMachineFactory"/>, and returns a <see cref="BootedBoard"/> the host runs.
/// Adding a CPU is adding one row. The default board (no --board given) is "6502".
/// </summary>
public static class BoardRegistry
{
    /// <summary>The board selected when --board is omitted.</summary>
    public const string DefaultBoard = "6502";

    private static readonly string[] Names =
        ["6502", "z80", "68000", "8086", "breadboard6502"];

    /// <summary>The available board names, in catalog order, for --board list + usage text.</summary>
    public static IReadOnlyList<string> AvailableBoards => Names;

    /// <summary>
    /// Build and boot a board by name (case-insensitive). On success returns true with a
    /// <see cref="BootedBoard"/>; on an unknown name returns false with an error message.
    /// The caller resets the machine and wires the UART. <paramref name="tier"/> selects
    /// interpreter (default) or JIT.
    /// </summary>
    public static bool TryBoot(string name, ExecutionTier tier,
                               out BootedBoard? board, out string? error)
    {
        board = null;
        error = null;
        string key = name.Trim().ToLowerInvariant();
        switch (key)
        {
            case "6502":
            case "breadboard6502":
                board = BootBreadboard6502(tier);
                return true;
            case "z80":
                board = BootReferenceSbc(CpuKind.Z80, tier);
                return true;
            case "68000":
                board = BootReferenceSbc(CpuKind.M68000, tier);
                return true;
            case "8086":
                board = BootReferenceSbc(CpuKind.I8086, tier);
                return true;
            default:
                error = $"unknown board '{name}' (available: {string.Join(", ", Names)})";
                return false;
        }
    }

    private static BootedBoard BootBreadboard6502(ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        BoardSpec spec = Breadboard6502Board.Spec(BoardRoms.Mos6502Demo(), uart, timer);
        Machine machine = BoardMachineFactory.Build(spec, tier);
        return new BootedBoard(machine, uart, Banner.For(spec));
    }

    private static BootedBoard BootReferenceSbc(CpuKind cpu, ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        byte[] rom = cpu switch
        {
            CpuKind.Z80 => BoardRoms.Z80Boot(),
            CpuKind.M68000 => BoardRoms.M68000Boot(),
            CpuKind.I8086 => BoardRoms.I8086Boot(),
            _ => throw new System.NotSupportedException($"no host boot ROM for {cpu}"),
        };
        BoardSpec spec = ReferenceSbc.Build(cpu, uart, timer, rom);
        Machine machine = BoardMachineFactory.Build(spec, tier);
        BootedBoard board = new(machine, uart, Banner.For(spec));

        // The Z80 boots from RAM at $0000, so poke its program into RAM after the machine is
        // built (the ROM image is a recipe placeholder). The other CPUs boot from ROM directly.
        if (cpu == CpuKind.Z80)
        {
            IAddressSpace space = machine.Space(AddressSpaceKind.Program);
            byte[] program = BoardRoms.Z80BootProgram();
            for (int i = 0; i < program.Length; i++)
                space.Write8((uint)i, program[i]);
        }
        return board;
    }
}
```

> NOTE: `Banner.For(spec)` is defined in Task 6 (a tiny per-board banner helper). Until Task 6 lands, this will not compile — that is intentional TDD ordering; Task 6 writes the banner test first, then the helper, then this file builds. If executing strictly, fold the `Banner` helper in as Step 0 of this task. Here we keep banner as its own task for a focused test.

- [ ] **Step 2: Do NOT build yet — proceed to Task 6 which adds the `Banner` helper and the registry tests, then build.**

(Commit is deferred to Task 6's Step, where the registry first builds + passes its tests.)

---

## Task 6: `Banner` helper + registry tests (boot each board)

**Files:**
- Create: `src/CpuEmulator.Host/Banner.cs`
- Modify: `tests/CpuEmulator.Tests/Host/BoardRegistryTests.cs`

- [ ] **Step 1: Write the failing registry tests**

Replace the contents of `tests/CpuEmulator.Tests/Host/BoardRegistryTests.cs` with:

```csharp
using CpuEmulator.Host;
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Host;

public class BoardRegistryTests
{
    [Fact]
    public void Available_boards_lists_all_five_in_catalog_order()
    {
        Assert.Equal(
            new[] { "6502", "z80", "68000", "8086", "breadboard6502" },
            BoardRegistry.AvailableBoards);
    }

    [Fact]
    public void Default_board_is_6502()
    {
        Assert.Equal("6502", BoardRegistry.DefaultBoard);
    }

    [Theory]
    [InlineData("6502")]
    [InlineData("Z80")]          // case-insensitive
    [InlineData("68000")]
    [InlineData("8086")]
    [InlineData("breadboard6502")]
    public void TryBoot_builds_a_machine_for_each_known_name(string name)
    {
        bool ok = BoardRegistry.TryBoot(name, ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error);

        Assert.True(ok, error);
        Assert.NotNull(board);
        Assert.NotNull(board!.Machine);
        Assert.NotNull(board.Uart);
        Assert.False(string.IsNullOrWhiteSpace(board.Banner));
    }

    [Fact]
    public void TryBoot_rejects_an_unknown_name_with_a_clean_error()
    {
        bool ok = BoardRegistry.TryBoot("6809", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error);

        Assert.False(ok);
        Assert.Null(board);
        Assert.Contains("unknown board '6809'", error);
    }

    [Fact]
    public void TryBoot_on_the_jit_tier_also_builds()
    {
        bool ok = BoardRegistry.TryBoot("z80", ExecutionTier.Jit,
            out BootedBoard? board, out string? error);
        Assert.True(ok, error);
        Assert.NotNull(board);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail (do not compile)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~BoardRegistryTests"`
Expected: FAIL to compile — `Banner` is undefined (referenced by `BoardRegistry`).

- [ ] **Step 3: Write the `Banner` helper**

**Create:** `src/CpuEmulator.Host/Banner.cs`

```csharp
using System.Linq;
using CpuEmulator.Machines;

namespace CpuEmulator.Host;

/// <summary>One-line board banners for the REPL, derived from the BoardSpec so each board
/// describes itself (name · CPU · address width · the UART/timer MMIO bases · region map).</summary>
public static class Banner
{
    public static string For(BoardSpec spec)
    {
        string uart = SlotBase(spec, "uart");
        string timer = SlotBase(spec, "timer");
        string regions = string.Join(" · ", spec.Memory.Select(r =>
            $"{r.Kind} ${r.Start:X}-${r.Start + r.Length - 1:X}"));
        return $"CpuEmulator — {spec.Name}\n" +
               $"{spec.Cpu} · {spec.AddressBits}-bit · UART {uart} · timer {timer}\n" +
               $"{regions}";
    }

    private static string SlotBase(BoardSpec spec, string name)
    {
        foreach (var slot in spec.Peripherals)
            if (slot.Name == name)
                return $"${slot.Base:X}";
        return "(none)";
    }
}
```

> NOTE: `PeripheralSlot` has `Name` + `Base` (confirmed: `record PeripheralSlot(string Name, IPeripheral Device, uint Base, uint Length)`); `MemoryRegion` has `Start`, `Length`, `Kind` (confirmed from `Breadboard6502Board.Spec`). If `MemoryRegion`'s property is `Size` rather than `Length`, adjust — verify with `grep -n "record MemoryRegion" src/CpuEmulator.Machines/*.cs` before running and use the actual names.

- [ ] **Step 4: Verify `MemoryRegion`'s property names before building**

Run: `grep -rn "record MemoryRegion\|enum RegionKind" src/CpuEmulator.Machines/*.cs`
Confirm the positional names are `Start`, `Length`, `Kind` (used in `Banner`). If they differ, edit `Banner.For` to match. (The Task-4 ROM tests already assume `MemoryRegion` is constructed positionally; this just confirms the property names for the banner.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~BoardRegistryTests"`
Expected: PASS (all methods, including the 5-way theory + the JIT-tier build).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Host/BoardRegistry.cs src/CpuEmulator.Host/Banner.cs tests/CpuEmulator.Tests/Host/BoardRegistryTests.cs
git commit -m "feat(piece3): BoardRegistry (name->BootedBoard) + per-board banner"
```

---

## Task 7: The 6502 registry path is byte-identical to the retired hand-wiring

**Files:**
- Test: `tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs`

This is the zero-behavior-change gate for the 6502 (the design says the 6502 path uses Breadboard6502-as-BoardSpec, proven byte-identical in piece #1 — this re-proves it through the host's registry).

- [ ] **Step 1: Write the failing smoke**

**Create:** `tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs`

```csharp
using System.Text;
using CpuEmulator.Host;
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Host;

/// <summary>The per-board host smokes: each board boots through BoardRegistry, the monitor
/// renders the right per-CPU registers (+ disassembly where the CPU has one), step/run
/// advances, and the UART round-trips. These are the un-fakeable "the host boots any board"
/// proofs for piece #3.</summary>
public class HostBoardSmokeTests
{
    [Fact]
    public void Mos6502_registry_path_prints_the_demo_banner_message_byte_identically()
    {
        // Boot the 6502 through the registry, reset, run the demo on a bounded budget — the
        // captured UART stream must be the breadboard demo's hello message (the same bytes the
        // retired hand-wired path produced).
        var tx = new StringBuilder();
        bool ok = BoardRegistry.TryBoot("6502", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error);
        Assert.True(ok, error);
        board!.Uart.OnTransmit = b => tx.Append((char)b);

        board.Machine.Reset();        // PC = $E000 via the ROM reset vector
        board.Machine.Run(10_000);    // hello completes well within this budget

        Assert.Contains("Hello from Breadboard6502!", tx.ToString());
    }
}
```

- [ ] **Step 2: Run the smoke to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.Mos6502_registry_path"`
Expected: FAIL — but check the mode. If `BoardRegistry`/`BootedBoard` compile (Tasks 2/5/6 landed), this likely **passes immediately** (the registry already boots the 6502). That is acceptable — the test exists to lock the behavior. If it fails on the assertion, the demo ROM or boot wiring drifted; debug before continuing.

- [ ] **Step 3: (If needed) no implementation change — the registry already produces this**

If Step 2 passed, no code change is needed; the smoke documents + guards the 6502 path. If it failed, fix the registry's 6502 path (Task 5 `BootBreadboard6502`) until the message round-trips.

- [ ] **Step 4: Run to confirm pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.Mos6502_registry_path"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs
git commit -m "test(piece3): 6502 registry path is byte-identical (demo message round-trips)"
```

---

## Task 8: Generalize the monitor's absolute-target parser to the address width

**Files:**
- Modify: `src/CpuEmulator.Monitor/MonitorEngine.cs:325-337`
- Test: `tests/CpuEmulator.Tests/Monitor/MonitorWideAddressTests.cs`

This closes the one real 6502-assuming gap in the monitor engine (the `a`-command absolute-target parser is `ushort`-only).

- [ ] **Step 1: Write the failing test**

**Create:** `tests/CpuEmulator.Tests/Monitor/MonitorWideAddressTests.cs`

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using Xunit;

namespace CpuEmulator.Tests.Monitor;

/// <summary>The monitor engine's a-command absolute-target resolution must respect the address
/// space width, not assume 16 bits. We can't easily exercise a 24-bit branch with the 6502
/// assembler, so we test the boundary directly: in a 16-bit space a 4-hex-digit '$hhhh'
/// absolute target still resolves (the prior behavior is preserved), and a too-wide token is
/// rejected. The 24/20-bit acceptance is exercised end-to-end by the 68000/8086 host smokes.</summary>
public class MonitorWideAddressTests
{
    private static MonitorEngine New16BitEngine(out AddressSpace space)
    {
        space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var image = new byte[0x10000];
        space.MapMemory(0x0000, image, writable: true);
        var cpu = new Mos6502Cpu(space);
        return new MonitorEngine(cpu, space, cpu);
    }

    [Fact]
    public void Branch_with_a_4_digit_absolute_target_still_resolves_in_a_16_bit_space()
    {
        MonitorEngine engine = New16BitEngine(out _);
        // 'BNE $0205' at $0200: the table rejects absolute on a branch, so the engine resolves
        // it to a relative offset (target - (addr + len)). offset = 0x0205 - 0x0202 = 3.
        bool ok = engine.TryAssembleAt(0x0200, "BNE $0205", out byte[] bytes, out string? error);
        Assert.True(ok, error);
        Assert.Equal(2, bytes.Length);     // 6502 relative branch is 2 bytes
        Assert.Equal(0xD0, bytes[0]);      // BNE opcode
        Assert.Equal(0x03, bytes[1]);      // +3
    }

    [Fact]
    public void Branch_with_a_5_digit_target_in_a_16_bit_space_does_not_resolve()
    {
        MonitorEngine engine = New16BitEngine(out _);
        // '$01205' is wider than the 16-bit space's 4 digits — the engine must NOT treat it as
        // an absolute target (it would have, naively, before this was width-aware? No — the old
        // code keyed on length==5 i.e. '$hhhh'. The new code keys on _addressDigits, so a
        // 5-digit token in a 4-digit space is not an absolute target → assembly fails cleanly.)
        bool ok = engine.TryAssembleAt(0x0200, "BNE $01205", out _, out string? error);
        Assert.False(ok);
        Assert.NotNull(error);
    }
}
```

- [ ] **Step 2: Run the test to verify the second case fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~MonitorWideAddressTests"`
Expected: the first test PASSES (current `$hhhh` behavior); the second FAILS — the current code's `t.Length == 5` check matches `$01205` (5 chars after `$`? no: `$01205` is 6 chars, length 6, so `t.Length == 5` is already false and it would already not resolve). **Before implementing, run and observe the actual failure.** If both already pass, the change in Step 3 is still required for the *wide* direction (24/20-bit), which the 16-bit space cannot exercise — in that case Step 3 makes the parser width-aware and the host smokes (Tasks 14/15) are the real proof. Treat Step 3 as required regardless.

- [ ] **Step 3: Make `TryParseAbsoluteTarget` width-aware**

`TryParseAbsoluteTarget` is currently `static` and ignores the engine's width. Make it an instance method keyed on `_addressDigits`/`_addressMask`. Replace lines 324-337 of `src/CpuEmulator.Monitor/MonitorEngine.cs`:

```csharp
    /// <summary>Parse a '$' + N-hex-digit absolute address, where N is the address space's
    /// digit width (4 for 16-bit, 5 for 20-bit, 6 for 24-bit). Width-aware so the a-command's
    /// branch-offset resolution works on every board, not just 16-bit ones. Returns false for
    /// the wrong width, a non-'$' token, a non-hex body, or a value past the address mask.</summary>
    private bool TryParseAbsoluteTarget(string operand, out uint target)
    {
        target = 0;
        string t = operand.Trim();
        if (t.Length == _addressDigits + 1 && t[0] == '$'
            && uint.TryParse(t.Substring(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint v)
            && v <= _addressMask)
        {
            target = v;
            return true;
        }
        return false;
    }
```

> NOTE: the method changes from `private static bool` to `private bool` (it now reads `_addressDigits`/`_addressMask` instance fields). The single call site at line 299 (`TryParseAbsoluteTarget(operand, out uint target)`) is unchanged — instance call resolves automatically.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~MonitorWideAddressTests"`
Expected: PASS (both).

- [ ] **Step 5: Run the existing monitor test suite to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~Monitor"`
Expected: PASS (the existing monitor/REPL tests still green — the 6502 `$hhhh` path is preserved).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Monitor/MonitorEngine.cs tests/CpuEmulator.Tests/Monitor/MonitorWideAddressTests.cs
git commit -m "fix(piece3): monitor a-command absolute target is address-width-aware (not 16-bit-only)"
```

---

## Task 9: Add `--board` to `HostOptions`

**Files:**
- Modify: `src/CpuEmulator.Host/HostOptions.cs`
- Test: `tests/CpuEmulator.Tests/Host/HostOptionsTests.cs` (extend if it exists; else create)

- [ ] **Step 1: Check for an existing HostOptions test file**

Run: `ls tests/CpuEmulator.Tests/Host/HostOptionsTests.cs 2>/dev/null || echo MISSING`
If it exists, extend it; if MISSING, create it with the test below.

- [ ] **Step 2: Write the failing test**

**Create (or append to):** `tests/CpuEmulator.Tests/Host/HostOptionsTests.cs`

```csharp
using CpuEmulator.Host;
using Xunit;

namespace CpuEmulator.Tests.Host;

public class HostOptionsBoardTests
{
    [Fact]
    public void Board_defaults_to_6502_when_absent()
    {
        Assert.True(HostOptions.TryParse([], out HostOptions options, out string? error));
        Assert.Null(error);
        Assert.Equal("6502", options.Board);
        Assert.False(options.ListBoards);
    }

    [Fact]
    public void Board_flag_selects_a_named_board()
    {
        Assert.True(HostOptions.TryParse(["--board", "z80"], out HostOptions options, out _));
        Assert.Equal("z80", options.Board);
        Assert.False(options.ListBoards);
    }

    [Fact]
    public void Board_list_sets_the_list_flag()
    {
        Assert.True(HostOptions.TryParse(["--board", "list"], out HostOptions options, out _));
        Assert.True(options.ListBoards);
    }

    [Fact]
    public void Board_requires_a_value()
    {
        Assert.False(HostOptions.TryParse(["--board"], out _, out string? error));
        Assert.Contains("--board requires a board name", error);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostOptionsBoardTests"`
Expected: FAIL — `HostOptions` has no `Board` / `ListBoards` members.

- [ ] **Step 4: Add `Board` + `ListBoards` to `HostOptions`**

Edit `src/CpuEmulator.Host/HostOptions.cs`. Change the record declaration (add the two members, with `--board list` represented by `ListBoards`):

```csharp
public sealed record HostOptions(
    bool Demo, string? LoadPath, uint LoadAt, uint? Pc, bool Terminal,
    string Board, bool ListBoards)
{
    public const string Usage =
        "usage: CpuEmulator.Host [--board <name|list>] [--demo | [--terminal] " +
        "[--load <bin> [--at $addr] [--pc $addr]]]";

    private const uint DefaultLoadAt = 0x0200;
    private const string DefaultBoard = "6502";
```

Update the in-method default `options` initializer and the success initializer, and add the `--board` case + a local. Replace the body of `TryParse` from the local declarations through the final success return:

```csharp
    public static bool TryParse(string[] args, out HostOptions options, out string? error)
    {
        bool demo = false;
        bool terminal = false;
        string? loadPath = null;
        uint loadAt = DefaultLoadAt;
        uint? pc = null;
        bool sawAt = false, sawPc = false;
        string board = DefaultBoard;
        bool listBoards = false;

        options = new HostOptions(Demo: false, LoadPath: null, LoadAt: DefaultLoadAt, Pc: null,
                                  Terminal: false, Board: DefaultBoard, ListBoards: false);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo":
                    demo = true;
                    break;

                case "--terminal":
                    terminal = true;
                    break;

                case "--board":
                    if (++i >= args.Length)
                        return Fail("--board requires a board name", out error);
                    if (string.Equals(args[i], "list", System.StringComparison.OrdinalIgnoreCase))
                        listBoards = true;
                    else
                        board = args[i];
                    break;

                case "--load":
                    if (++i >= args.Length)
                        return Fail("--load requires a file path", out error);
                    loadPath = args[i];
                    break;

                case "--at":
                    if (++i >= args.Length)
                        return Fail("--at requires an address", out error);
                    if (!TryParseAddress(args[i], out loadAt))
                        return Fail($"bad address '{args[i]}' for --at", out error);
                    sawAt = true;
                    break;

                case "--pc":
                    if (++i >= args.Length)
                        return Fail("--pc requires an address", out error);
                    if (!TryParseAddress(args[i], out uint pcValue))
                        return Fail($"bad address '{args[i]}' for --pc", out error);
                    pc = pcValue;
                    sawPc = true;
                    break;

                default:
                    return Fail($"unknown option '{args[i]}'", out error);
            }
        }

        if (demo && loadPath is not null)
            return Fail("--demo and --load are mutually exclusive", out error);
        if (demo && terminal)
            return Fail("--demo and --terminal are mutually exclusive", out error);
        if (loadPath is null && sawAt)
            return Fail("--at requires --load", out error);
        if (loadPath is null && sawPc)
            return Fail("--pc requires --load", out error);

        options = new HostOptions(demo, loadPath, loadAt, pc, terminal, board, listBoards);
        error = null;
        return true;
    }
```

> NOTE: every existing `new HostOptions(...)` call site in this file gains the two trailing args (`Board`, `ListBoards`) — both initializers above are already updated. No other file constructs `HostOptions` (confirm with `grep -rn "new HostOptions(" src tests` before building; update any test-side constructor calls to the new arity).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostOptionsBoardTests"`
Expected: PASS (all four).

- [ ] **Step 6: Run the existing HostOptions tests for no regression**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostOptions"`
Expected: PASS (any pre-existing parser tests + the new ones).

- [ ] **Step 7: Commit**

```bash
git add src/CpuEmulator.Host/HostOptions.cs tests/CpuEmulator.Tests/Host/HostOptionsTests.cs
git commit -m "feat(piece3): --board <name|list> host option (default 6502)"
```

---

## Task 10: Wire `Program.Main` onto the registry; retire `Breadboard6502.cs`

**Files:**
- Modify: `src/CpuEmulator.Host/Program.cs`
- Remove: `src/CpuEmulator.Host/Breadboard6502.cs`
- Test: covered by the host smokes (Tasks 11-15) + a manual run check here.

- [ ] **Step 1: Rewrite `Program.Main` to boot via the registry**

Replace the body of `src/CpuEmulator.Host/Program.cs` with:

```csharp
using CpuEmulator.Machines;
using CpuEmulator.Monitor;

namespace CpuEmulator.Host;

/// <summary>Console host: boots ANY registered board (default 6502) from a BoardSpec via
/// BoardMachineFactory, wires the board's UART to the console, and either runs the boot
/// program on a bounded budget (--demo), or drops into the CPU-agnostic monitor REPL on
/// stdio (default; --load preloads a binary first). '--board list' prints the catalog.</summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (!HostOptions.TryParse(args, out HostOptions options, out string? error))
        {
            Console.Error.WriteLine($"? {error}");
            Console.Error.WriteLine(HostOptions.Usage);
            return 2;
        }

        if (options.ListBoards)
        {
            Console.WriteLine("available boards:");
            foreach (string name in BoardRegistry.AvailableBoards)
                Console.WriteLine($"  {name}");
            return 0;
        }

        if (!BoardRegistry.TryBoot(options.Board, ExecutionTier.Interpreter,
                                   out BootedBoard? booted, out string? bootError))
        {
            Console.Error.WriteLine($"? {bootError}");
            Console.Error.WriteLine(HostOptions.Usage);
            return 2;
        }
        BootedBoard board = booted!;

        board.Uart.OnTransmit = b => Console.Write((char)b); // raw passthrough
        board.Machine.Reset();                               // CPU lands at its reset entry

        if (options.Demo)
        {
            board.Machine.Run(10_000); // bounded; the boot program completes, then exit
            return 0;
        }

        MonitorEngine engine = board.NewMonitor();
        Console.WriteLine(board.Banner);
        if (options.LoadPath is not null)
        {
            int count;
            try
            {
                count = engine.LoadFile(options.LoadAt, options.LoadPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"? {ex.Message}");
                return 2;
            }
            Console.WriteLine($"loaded ${count:X} bytes at ${options.LoadAt:X{engine.AddressDigits}}");
            if (options.Pc is uint pc)
                engine.ProgramCounter = pc;
        }

        if (options.Terminal)
        {
            Console.WriteLine("(terminal — Ctrl-] exits to the monitor)");
            try
            {
                bool priorCtrlC = Console.TreatControlCAsInput;
                Console.TreatControlCAsInput = true;
                try
                {
                    new TerminalSession(board.Machine, board.Uart, new SystemTerminalConsole())
                        .Run();
                }
                finally
                {
                    Console.TreatControlCAsInput = priorCtrlC;
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"? --terminal needs an interactive console: {ex.Message}");
                return 2;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"? --terminal needs an interactive console: {ex.Message}");
                return 2;
            }
        }

        new MonitorRepl(engine, Console.In, Console.Out,
                        prompt: true, inject: board.Uart.FeedInput).Run();
        return 0;
    }
}
```

> NOTE on changes from the old `Program`: (a) the hard-coded `Banner` const is gone — the banner now comes from `board.Banner` (per-board); (b) `new Breadboard6502()` → `BoardRegistry.TryBoot`; (c) the `--load` echo uses `engine.AddressDigits` instead of a fixed `X4` so wide-address boards print full addresses; (d) `TerminalSession` still takes `(Machine, SimpleUart, console)` — `board.Machine` + `board.Uart` satisfy it unchanged.

- [ ] **Step 2: Delete the retired hand-wired board**

```bash
git rm src/CpuEmulator.Host/Breadboard6502.cs
```

> NOTE: `DemoRom.cs` stays (the registry's 6502 ROM source). Confirm nothing else references the deleted `Breadboard6502` type: `grep -rn "new Breadboard6502\|Breadboard6502 " src tests --include=*.cs`. Any test that constructed the old class must move to `BoardRegistry.TryBoot("6502", ...)`. If `tests/CpuEmulator.Tests/Host/` has a `Breadboard6502`-typed test, port it to the registry path in this step (show the edit and re-run it).

- [ ] **Step 3: Build to verify the host compiles without the retired class**

Run: `dotnet build src/CpuEmulator.Host/CpuEmulator.Host.csproj -c Debug`
Expected: build succeeds (no dangling reference to `Breadboard6502`).

- [ ] **Step 4: Manual run check — default board + list**

Run: `printf "q\n" | dotnet run --project src/CpuEmulator.Host -c Debug`
Expected: prints the 6502 banner (`CpuEmulator — breadboard6502` ... `Mos6502 · 16-bit · UART $D000 · timer $D100` ...) then exits at `q`.

Run: `dotnet run --project src/CpuEmulator.Host -c Debug -- --board list`
Expected: prints `available boards:` followed by `6502`, `z80`, `68000`, `8086`, `breadboard6502`.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Host/Program.cs
git commit -m "feat(piece3): Program boots any board via BoardRegistry; retire hand-wired Breadboard6502"
```

---

## Task 11: 6502 host smoke — registers + real disassembly + UART echo round-trip

**Files:**
- Modify: `tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs`

The design's validation bar: boot, the monitor shows the right per-CPU registers + disassembly, step/run works, and the UART round-trips. This task does it for the 6502 (which has real disassembly + a polled-echo loop in its ROM, so the round-trip is genuine).

- [ ] **Step 1: Write the failing smoke**

Append to `HostBoardSmokeTests` (the class created in Task 7):

```csharp
    [Fact]
    public void Mos6502_host_smoke_registers_disasm_step_and_uart_echo()
    {
        Assert.True(BoardRegistry.TryBoot("6502", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);
        board!.Machine.Reset();
        var engine = board.NewMonitor();

        // Registers: the 6502 names — A, X, Y, S(P), P, PC — appear in the rendered line.
        string regs = engine.Registers();
        Assert.Contains("A=", regs);
        Assert.Contains("PC=", regs);
        Assert.Contains("P=", regs);   // the 6502 status register (proves 6502-shaped state)

        // Disassembly at the reset entry $E000 is the demo's 'LDX #$00' (real 6502 mnemonic).
        string dis = engine.Disassemble(0xE000, 1);
        Assert.Contains("LDX", dis);

        // Step advances PC past the 2-byte LDX.
        var step = engine.Step();
        Assert.Equal(0xE000u, step.PcBefore);
        Assert.Equal(0xE002u, engine.ProgramCounter);

        // UART round-trip: run the demo to its echo loop, feed a byte, run, observe it echoed.
        var tx = new StringBuilder();
        board.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Run(20_000);     // hello prints, then the ROM parks in the polled echo loop
        tx.Clear();
        board.Uart.FeedInput((byte)'Z');
        board.Machine.Run(20_000);     // the echo loop dequeues + retransmits
        Assert.Contains("Z", tx.ToString());
    }
```

- [ ] **Step 2: Run the smoke to verify it fails (or observe behavior)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.Mos6502_host_smoke"`
Expected: FAIL initially if any assertion is off (e.g. the disasm string format). Read the actual rendered strings from the failure and adjust the `Contains` literals to match the real output (the disassembler text, the register names) — these are observation-pinned, not invented.

- [ ] **Step 3: No production change — adjust the test to the real output**

This is a smoke over already-built behavior; the "implementation" is making the assertions match the engine's real rendering. Pin each `Contains` to the observed substring.

- [ ] **Step 4: Run to confirm pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.Mos6502_host_smoke"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs
git commit -m "test(piece3): 6502 host smoke (registers + LDX disasm + step + UART echo)"
```

---

## Task 12: Z80 host smoke — registers + real disassembly + UART round-trip

**Files:**
- Modify: `tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs`

- [ ] **Step 1: Write the failing smoke**

Append to `HostBoardSmokeTests`:

```csharp
    [Fact]
    public void Z80_host_smoke_registers_disasm_and_uart_prints_OK()
    {
        Assert.True(BoardRegistry.TryBoot("z80", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);

        var tx = new StringBuilder();
        board!.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();        // Z80: PC = 0 (the program was poked into RAM at boot)
        var engine = board.NewMonitor();

        // Registers: the Z80 names a PC + an A; it does NOT have a 6502 'P' status register.
        string regs = engine.Registers();
        Assert.Contains("PC=", regs);
        Assert.Contains("A=", regs);

        // Disassembly at $0000 is the boot program's first op: LD A,'O' (opcode 0x3E).
        // The Z80 disassembler renders a real mnemonic (1605 arms), not '???'.
        string dis = engine.Disassemble(0x0000, 1);
        Assert.Contains("LD", dis);
        Assert.DoesNotContain("???", dis);

        // Step advances PC off $0000 (the boot program is executing real instructions).
        engine.Step();
        Assert.NotEqual(0x0000u, engine.ProgramCounter);

        // UART round-trip: run to completion; the boot writes "OK\r" out the UART.
        board.Machine.Run(2000);
        Assert.Equal("OK\r", tx.ToString());
    }
```

- [ ] **Step 2: Run the smoke to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.Z80_host_smoke"`
Expected: FAIL if any literal is off. Read the real disasm/register strings and pin the `Contains` literals (e.g. confirm the Z80 register line uses `A=`; confirm `0x3E` disassembles to a string containing `LD`).

- [ ] **Step 3: Adjust assertions to real output (no production change)**

Pin `Contains` substrings to the engine's actual rendering. The `Assert.Equal("OK\r", ...)` and `DoesNotContain("???")` are hard requirements (the design's "right disassembly" + "UART round-trips" bars) and must not be loosened.

- [ ] **Step 4: Run to confirm pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.Z80_host_smoke"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs
git commit -m "test(piece3): Z80 host smoke (PC/A registers + LD disasm + OK\\r round-trip)"
```

---

## Task 13: Z80 host smoke on the JIT tier (proves both tiers boot)

**Files:**
- Modify: `tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs`

The registry boots interpreter by default; this proves the JIT tier also boots through the host path (matching the piece-#2 both-tiers gate).

- [ ] **Step 1: Write the failing smoke**

Append to `HostBoardSmokeTests`:

```csharp
    [Fact]
    public void Z80_host_smoke_on_the_jit_tier_also_prints_OK()
    {
        Assert.True(BoardRegistry.TryBoot("z80", ExecutionTier.Jit,
            out BootedBoard? board, out string? error), error);

        var tx = new StringBuilder();
        board!.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();
        board.Machine.Run(2000);
        Assert.Equal("OK\r", tx.ToString());
    }
```

- [ ] **Step 2: Run the smoke to verify it fails / observe**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.Z80_host_smoke_on_the_jit"`
Expected: FAIL if the registry's JIT path or RAM-poke ordering is wrong; otherwise PASS. If it fails, verify the Z80 program is poked AFTER `BoardMachineFactory.Build` (Task 5 does this) so the JIT sees the program in RAM.

- [ ] **Step 3: Fix any tier-ordering issue (if needed)**

If the JIT smoke fails because the program isn't visible, ensure `BootReferenceSbc` (Task 5) pokes the Z80 program after the machine is built and before `Reset`/`Run`. No change if Task 5's ordering already holds.

- [ ] **Step 4: Run to confirm pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.Z80_host_smoke_on_the_jit"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs
git commit -m "test(piece3): Z80 host smoke on the JIT tier (both tiers boot through the host)"
```

---

## Task 14: 68000 host smoke — registers + UART round-trip + the disassembly limitation

**Files:**
- Modify: `tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs`

The 68000 disassembler is a `???` stub (see the agnosticism finding). This smoke asserts the parts that ARE correct — 68000-shaped registers, a 24-bit address render, step/run advancing PC, and the `OK\r` round-trip — and pins the disassembly limitation as a guarded fact (`???`), so a future real 68000 disassembler will flip this test as the signal to update it.

- [ ] **Step 1: Write the failing smoke**

Append to `HostBoardSmokeTests`:

```csharp
    [Fact]
    public void M68000_host_smoke_registers_24bit_address_step_and_uart_prints_OK()
    {
        Assert.True(BoardRegistry.TryBoot("68000", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);

        var tx = new StringBuilder();
        board!.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();        // 68000: SSP/PC from $0/$4, SR=0x2700 (supervisor)
        var engine = board.NewMonitor();

        // Registers: the 68000 names D-registers + a PC + SR (NOT a 6502 'A'/'P').
        string regs = engine.Registers();
        Assert.Contains("PC=", regs);
        Assert.Contains("D0=", regs);    // a 68000 data register — proves 68000-shaped state

        // 24-bit address width: the memory dump renders 6 hex digits (e.g. "000008:").
        Assert.Equal(6, engine.AddressDigits);
        string dump = engine.ReadMemory(0x000008, 1);
        Assert.StartsWith("000008:", dump);

        // KNOWN LIMITATION (piece #3): the generated 68000 disassembler is a '???' stub
        // (the field-grammar CPU has no flat per-opcode disasm table). The monitor renders
        // '???' honestly; the InstructionLength byte-walk + step/run are still correct. This
        // assertion guards the limitation: when a real 68000 disassembler lands, it flips,
        // signalling this test (and the docs/roadmap note) to update.
        string dis = engine.Disassemble(0x000008, 1);
        Assert.Contains("???", dis);

        // Step still advances PC (length comes from the real DescriptorFor, not the disasm).
        uint pcBefore = engine.ProgramCounter;
        engine.Step();
        Assert.NotEqual(pcBefore, engine.ProgramCounter);

        // UART round-trip: run to completion; the boot writes "OK\r".
        board.Machine.Run(2000);
        Assert.Equal("OK\r", tx.ToString());
    }
```

- [ ] **Step 2: Run the smoke to verify it fails / observe**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.M68000_host_smoke"`
Expected: FAIL if any literal is off. Pin `Contains`/`StartsWith` to the real output — in particular confirm the 68000 register line's data-register name (it may render `D0=00000000`; the substring `D0=` should hold) and that `Disassemble` returns a string containing `???`.

- [ ] **Step 3: Adjust assertions to real output (no production change)**

If the 68000 register naming differs (e.g. `D0` vs `D0.L`), pin to the actual substring. The `Assert.Equal("OK\r", ...)`, `Assert.Equal(6, engine.AddressDigits)`, and `Assert.Contains("???", dis)` are the hard, design-pinned facts.

- [ ] **Step 4: Run to confirm pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.M68000_host_smoke"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs
git commit -m "test(piece3): 68000 host smoke (D-regs + 24-bit addr + OK\\r; disasm '???' limitation guarded)"
```

---

## Task 15: 8086 host smoke — registers + real disassembly + UART round-trip

**Files:**
- Modify: `tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs`

- [ ] **Step 1: Write the failing smoke**

Append to `HostBoardSmokeTests`:

```csharp
    [Fact]
    public void I8086_host_smoke_registers_20bit_address_disasm_and_uart_prints_OK()
    {
        Assert.True(BoardRegistry.TryBoot("8086", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);

        var tx = new StringBuilder();
        board!.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();        // 8086: CS=FFFF, IP=0, DS=ES=SS=0, FLAGS=0
        var engine = board.NewMonitor();

        // Registers: the 8086 names segment + general registers (AX, CS, ...) + a PC.
        string regs = engine.Registers();
        Assert.Contains("PC=", regs);
        Assert.Contains("AX=", regs);    // an 8086 general register
        Assert.Contains("CS=", regs);    // a segment register — proves 8086-shaped state

        // 20-bit address width: 5 hex digits.
        Assert.Equal(5, engine.AddressDigits);

        // Disassembly at the body (physical 0xF0000) is MOV (opcode 0xB8). The 8086 disasm
        // renders a real mnemonic (284 arms), not '???'.
        string dis = engine.Disassemble(0xF0000, 1);
        Assert.Contains("MOV", dis);
        Assert.DoesNotContain("???", dis);

        // UART round-trip: run to completion; the boot FAR-JMPs to the body and writes "OK\r".
        board.Machine.Run(2000);
        Assert.Equal("OK\r", tx.ToString());
    }
```

- [ ] **Step 2: Run the smoke to verify it fails / observe**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.I8086_host_smoke"`
Expected: FAIL if literals are off. Pin `Contains` to the real register-line substrings (confirm the 8086 renders `AX=`/`CS=`; confirm `0xB8` at `0xF0000` disassembles to a string containing `MOV`). If the engine's PC name for the 8086 renders as `IP` not `PC`, pin `Assert.Contains("IP=", regs)` to whatever `RegisterNames` actually yields — observe, don't assume.

- [ ] **Step 3: Adjust assertions to real output (no production change)**

Pin substrings to observed rendering. `Assert.Equal("OK\r", ...)`, `Assert.Equal(5, engine.AddressDigits)`, and `DoesNotContain("???", dis)` are the hard, design-pinned facts.

- [ ] **Step 4: Run to confirm pass**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~HostBoardSmokeTests.I8086_host_smoke"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/CpuEmulator.Tests/Host/HostBoardSmokeTests.cs
git commit -m "test(piece3): 8086 host smoke (AX/CS registers + 20-bit addr + MOV disasm + OK\\r)"
```

---

## Task 16: Full-suite green + manual cross-board run

**Files:** none (verification)

- [ ] **Step 1: Run the whole test suite**

Run: `dotnet test CpuEmulator.sln -c Debug`
Expected: PASS — all tests green, including the five new host smokes, the registry tests, the ROM tests, the HostOptions tests, and the widened-address monitor test. No regressions in the existing 6502/Z80/68000/8086 + monitor + machine suites.

- [ ] **Step 2: Manual cross-board REPL spot-check (each board boots + renders registers)**

Run each and confirm the banner + a register line render (type `r` then `q`):

```bash
printf "r\nq\n" | dotnet run --project src/CpuEmulator.Host -c Debug -- --board 6502
printf "r\nq\n" | dotnet run --project src/CpuEmulator.Host -c Debug -- --board z80
printf "r\nq\n" | dotnet run --project src/CpuEmulator.Host -c Debug -- --board 68000
printf "r\nq\n" | dotnet run --project src/CpuEmulator.Host -c Debug -- --board 8086
```

Expected: each prints its own banner (right CPU + address width) and a register line in that CPU's register names (6502: `A= X= Y= ... P= PC=`; Z80: `A= BC= ... PC=`; 68000: `D0= ... PC=`; 8086: `AX= ... CS= ... PC=`/`IP=`). The 68000's `d` disassembly shows `???`; the others show real mnemonics.

- [ ] **Step 3: Manual UART round-trip on a ReferenceSbc board via --demo**

Run: `dotnet run --project src/CpuEmulator.Host -c Debug -- --board z80 --demo`
Expected: prints `OK` then a carriage return (the boot program's UART stream), then exits.

- [ ] **Step 4: Commit (if any test-only literal fixes were needed)**

```bash
git add -A
git commit -m "test(piece3): full-suite green across all five boards" --allow-empty
```

---

## Task 17: Docs — monitor reference + roadmap

**Files:**
- Modify: `docs/user-guide/monitor-reference.md`
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Document `--board` + the board list in the monitor reference**

Add a section to `docs/user-guide/monitor-reference.md` describing board selection. Insert (after the existing intro/launch section — place it where the launch invocation is documented):

```markdown
## Selecting a board (`--board`)

The host boots any registered board into the same CPU-agnostic monitor:

```
CpuEmulator.Host --board <name>
```

| Name | CPU | Address bus | Boot behavior |
|---|---|---|---|
| `6502` (default) | MOS 6502 | 16-bit | the breadboard demo (hello-print + polled echo) |
| `z80` | Zilog Z80 | 16-bit | prints `OK\r`, then halts |
| `68000` | Motorola 68000 | 24-bit | prints `OK\r`, then self-loops |
| `8086` | Intel 8086 | 20-bit | prints `OK\r`, then self-loops |
| `breadboard6502` | MOS 6502 | 16-bit | alias of `6502` |

`--board list` prints the catalog. With no `--board`, the host boots `6502`. The monitor renders
each CPU's own registers and address width automatically.

**Known limitation — 68000 disassembly.** The `d` (disassemble) command renders `???` for 68000
instructions: the 68000 uses the field-grammar decoder and has no flat per-opcode disassembly table
yet, so only the mnemonic text is unavailable. Instruction *lengths*, the byte dump, register
rendering, and step/run are all correct on the 68000. (6502/Z80/8086 disassemble normally.)
```

- [ ] **Step 2: Update the roadmap — ship the host, record the 68000-disasm follow-on**

Edit `docs/ROADMAP.md`. In the "Recently shipped — the CPUs → computers arc" table, add a `#3` row:

```markdown
| **#3 — the monitor hosts** | The console host boots **any** board into the CPU-agnostic monitor/REPL via `--board <name>` (default `6502`; `--board list` enumerates). A `BoardRegistry` (in `CpuEmulator.Host`) maps names → a built `Machine` (through `BoardMachineFactory`, no more hand-wiring) + the `SimpleUart` the host bridges console stdin/stdout through. The 6502 path runs the `Breadboard6502`-as-`BoardSpec` (byte-identical to the retired hand-wired board); the Z80/68000/8086 paths run the piece-#2 `OK\r` boot ROMs. Each board's **host smoke** proves boot → right per-CPU registers + (real) disassembly → step/run → UART round-trip on the interpreter (Z80 also on the JIT). The hand-wired `Breadboard6502` host class is retired (the design's no-separate-path non-goal). One monitor generalization shipped: the `a`-command absolute-target parser is now address-width-aware (was 16-bit-only), so branch-offset resolution works on the 24/20-bit boards. |
```

Then, in the deferred/candidate list, REMOVE item **6 ("[candidate] A non-6502 monitor host")** (now shipped) and ADD a new candidate for the 68000 disassembler:

```markdown
- **[candidate] A real 68000 disassembler.** The monitor renders `???` for 68000 instructions —
  the field-grammar 68000 has no flat per-opcode disassembly table (the generated `Disassemble`
  is a stub). A field-grammar-walking disassembler would give the 68000 monitor host the same
  mnemonic rendering the 6502/Z80/8086 already have. Surfaced by "CPUs → computers" piece #3.
```

> NOTE: confirm the exact deferred-item numbering before editing — re-read `docs/ROADMAP.md` §"Deferred & candidate follow-ons" and remove the non-6502-monitor-host item by its current text, not a stale index. Renumber the remaining items if the list is numbered sequentially.

- [ ] **Step 3: Build the docs sanity (no broken links) + commit**

Run: `grep -n "monitor host\|--board" docs/ROADMAP.md docs/user-guide/monitor-reference.md`
Expected: the new `--board` references appear; the old "[candidate] A non-6502 monitor host" text is gone.

```bash
git add docs/user-guide/monitor-reference.md docs/ROADMAP.md
git commit -m "docs(piece3): document --board + the 68000-disasm limitation; ship the monitor host in the roadmap"
```

---

## Task 18: Finish the branch (PR)

**Files:** none

- [ ] **Step 1: Final full-suite run**

Run: `dotnet test CpuEmulator.sln -c Debug`
Expected: all green.

- [ ] **Step 2: Push + open the PR**

```bash
git push -u origin feat/piece3-monitor-hosts
gh pr create --base main --title "Piece #3 — the monitor hosts (boot any BoardSpec)" \
  --body "$(cat <<'EOF'
Boots any registered board into the CPU-agnostic monitor via `--board <name>` (default 6502).

## What changed
- `BoardRegistry` (Host) maps board names → a built `Machine` (via `BoardMachineFactory`) + the bridged `SimpleUart`.
- `Program` boots via the registry; the hand-wired `Breadboard6502` host class is retired.
- `--board <name|list>` host option (default 6502).
- Per-board host smokes: 6502 / Z80 / 68000 / 8086 (boot → registers → disasm → step/run → UART round-trip); Z80 on both tiers.
- Monitor `a`-command absolute-target parser is now address-width-aware (was 16-bit-only).

## Known limitation
68000 disassembly renders `???` (field-grammar CPU has no flat disasm table). Lengths, registers, step/run, and the UART round-trip are all correct. Recorded as a roadmap follow-on.

## Docs Impact
- `docs/user-guide/monitor-reference.md` — `--board` section + the 68000-disasm limitation.
- `docs/ROADMAP.md` — piece #3 shipped; non-6502-monitor-host removed from deferred; 68000-disassembler added as a candidate.
EOF
)"
```

- [ ] **Step 3: Stop — the PR is the handoff.** Do not merge without the owner's review.

---

## Self-Review

**1. Spec coverage** (against the approved design's five bullets + non-goals + validation bar):

- *A board registry — enumerable boards by name (ReferenceSbc 6502/Z80/68000/8086 + the Breadboard6502 board-spec):* Tasks 5–6 (`BoardRegistry.AvailableBoards` lists all five; `TryBoot` builds each). ✓
- *CLI `--board <name>` (default: list, or the 6502); Host builds the Machine from the BoardSpec via BoardMachineFactory; 6502 uses Breadboard6502-as-BoardSpec:* Tasks 9 (`--board`/`--board list`, default 6502), 10 (`Program` → registry → `BoardMachineFactory`), 5 (6502 path uses `Breadboard6502Board.Spec`), 7 (byte-identical proof). ✓
- *Monitor I/O ↔ board UART — generalize the 6502 stdin/stdout↔UART bridge:* Tasks 2 (`BootedBoard.Uart`), 10 (`board.Uart.OnTransmit`/`FeedInput`), validated in every smoke (Tasks 11–15). ✓
- *Validation — Host boots each of the four boards; monitor shows right per-CPU registers + disassembly; step/run works; UART round-trips (a host smoke per board):* Tasks 11 (6502), 12+13 (Z80, both tiers), 14 (68000), 15 (8086). The disassembly bar is met for 6502/Z80/8086; the 68000 disassembly gap is explicitly scoped (finding section + Task 14 + Task 17 docs). ✓ — with the documented 68000-disasm caveat.
- *Non-goals — no new peripherals; no replica boards; no separate hand-wired path:* honored — no peripheral added; only the existing ReferenceSbc/Breadboard boards used; `Breadboard6502.cs` retired in Task 10. ✓

**2. Placeholder scan:** No `TBD`/`TODO`/"implement later"/"similar to Task N"/"add error handling" left. Every code step carries literal code. The "NOTE" blocks are *verification instructions* (confirm a name before building), not placeholders — each names the exact `grep` to run and what to do with the result; they exist because generated/cross-assembly names (e.g. `MemoryRegion.Length` vs `Size`, the 68000 board `Name`, the 8086 PC register name) must be pinned to the real source rather than guessed, which is correct plan hygiene, not a deferral. The test tasks (7, 11–15) legitimately "adjust assertions to real output" — these are observation-pinned smokes over already-built behavior; the hard design-pinned asserts (`OK\r`, `AddressDigits`, `???` for 68000, `DoesNotContain("???")` for the others) are fixed and may not be loosened.

**3. Type consistency:** Cross-checked against verified source —
- `BoardSpec` ctor: `(string Name, CpuKind Cpu, int AddressBits, IReadOnlyList<MemoryRegion> Memory, IReadOnlyList<PeripheralSlot> Peripherals, IrqWiring Irq, ResetConfig Reset, Endianness = LittleEndian)` — used only via `ReferenceSbc.Build`/`Breadboard6502Board.Spec`, never reconstructed by hand. ✓
- `BoardMachineFactory.Build(BoardSpec spec, ExecutionTier tier = Interpreter) → Machine` — Tasks 1, 5. ✓
- `Machine`: `.Cpu` (ICpuCore), `.Space(AddressSpaceKind) → IAddressSpace`, `.Reset()`, `.Run(long)`, `.Name` — Tasks 2, 5, 10, 11–15. ✓
- `SimpleUart`: `.OnTransmit` (Action<byte>?), `.FeedInput(byte)` — Tasks 2, 5, 10, 11–15. ✓
- `MonitorEngine` ctor `(ICpuCore, IAddressSpace, IMonitorSupport, Func<long,long>? run = null)`; members `.Registers()`, `.Disassemble(uint,int)`, `.ReadMemory(uint,int)`, `.Step() → MonitorStepReport`, `.ProgramCounter`, `.AddressDigits`, `.TryAssembleAt(uint,string,out byte[],out string?)`, `.LoadFile(uint,string)` — Tasks 2, 8, 10, 11–15. `MonitorStepReport.PcBefore` — Task 11. ✓
- `MonitorRepl` ctor `(MonitorEngine, TextReader, TextWriter, bool prompt = false, Action<byte>? inject = null)` — Task 10. ✓
- `ExecutionTier.{Interpreter,Jit}`, `CpuKind.{Mos6502,Z80,M68000,I8086}`, `AddressSpaceKind.Program` — throughout. ✓
- `PeripheralSlot(Name, Device, Base, Length)`, `MemoryRegion(Start, Length, Kind[, image])`, `RegionKind` — Task 6 banner (flagged for name-confirm in its Step 4). ✓
- Method names are stable across tasks: `BoardRegistry.TryBoot` / `AvailableBoards` / `DefaultBoard`; `BoardRoms.Mos6502Demo` / `Z80Boot` / `Z80BootProgram` / `M68000Boot` / `I8086Boot`; `BootedBoard.NewMonitor` / `.Machine` / `.Uart` / `.Banner`; `Banner.For`. No drift. ✓

Fixes applied inline during review: the 6502 demo-message assertion (Task 7) uses `Contains("Hello from Breadboard6502!")` matching `DemoRom.Message`; the `--load` echo (Task 10) uses `engine.AddressDigits` so wide-address boards print full addresses; the 68000 smoke (Task 14) asserts `???` rather than a mnemonic, consistent with the agnosticism finding. No spec requirement is left without a task.
