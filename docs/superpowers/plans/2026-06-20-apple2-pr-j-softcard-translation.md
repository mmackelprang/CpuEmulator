# PR-J — `SoftCardTranslation` + `SoftCardControlPort` (the 6-branch Z80→Apple translation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the concrete `SoftCardTranslation : IAddressTranslation` (the complete 6-branch MAME-verified Z80→Apple address-translation table — NOT the refuted `+$1000` shortcut) and the `SoftCardControlPort : IPeripheral` whose `$CnXX` write toggles which CPU runs, so a real Z80 routine runs translated against shared 6502 RAM and the toggle flips the active CPU and ends the slice.

**Architecture:** Per ADR 0015 Decision 3 + research §2/§1. `SoftCardTranslation.ToPhysical` is a 6-way branch on the top nibble of the 16-bit logical address. The `TranslatingAddressSpace` wrapper and the `ICoprocessorControl` toggle seam are **already shipped by PR-I**; this PR supplies the concrete translation data + the control port that drives the toggle. The control port captures the dual-CPU `Machine` via its `Realize(IMachineContext)` context (the context IS the Machine, which implements `ICoprocessorControl`) and, on a write, calls `SetCoprocessorActive`. A construction-time `translationEnabled` flag (the DIP-switch S1-1, research §2) makes the translation identity when disabled.

**Tech Stack:** C# / .NET, `CpuEmulator.Peripherals` (where `Apple2*` devices live), `CpuEmulator.Core` (`IAddressTranslation`, `ICoprocessorControl`, `IPeripheral`), the shipped Z80 + 6502 cores via `CpuCoreFactory`/`BoardMachineFactory`, xUnit (`tests/CpuEmulator.Tests/Apple2`).

## Global Constraints

- **Implement the 6-branch table verbatim (ADR 0015 Decision 3 / research §2).** The refuted `+$1000 mod 64K` shortcut is forbidden.
- **The boundary regression is the un-fakeable gate.** Critical precision (verified by enumeration during planning): the refuted `+$1000 mod 64K` shortcut **coincides** with the real table on branch 1 (`$0000–$AFFF`) **and** branch 6 (`$F000–$FFFF` → `$0000–$0FFF`, since `($F000+$1000) mod 64K = $0000`). The shortcut **differs only on branches 2–5** (`$B000–$EFFF`). So the shortcut-killers — the boundaries where the real table MUST NOT equal the shortcut — are exactly:
  - branch 2: `$B000→$D000` (shortcut wrongly gives `$C000`)
  - branch 3: `$C000→$E000` (shortcut wrongly gives `$D000`)
  - branch 4: `$D000→$F000` (shortcut wrongly gives `$E000`)
  - branch 5: `$E000→$C000` (shortcut wrongly gives `$F000`)
  The boundary test asserts the exact physical address at **all six** branches (to pin the full map) AND adds an explicit `NotEqual(shortcut, real)` at the four shortcut-killer boundaries (branches 2–5) — the structural guard against re-introducing the refuted map. (The queue's row-J wording "the refuted shortcut must fail branches 2–6" is approximate; branch 6 coincides, so the precise killers are 2–5. This is flagged to the owner as a row-J gate-wording refinement, not a design change.)
- **Depends on PR-I** (shipped: `IAddressTranslation`, `TranslatingAddressSpace`, `ICoprocessorControl`, `CoprocessorSpec`, the dual-CPU `Machine`/`Run`, `BoardMachineFactory` wiring). Ground all literal code against `main` after PR-I merges; the signatures used here are the ones PR-I's plan defines.
- **Interpreter-first.** The Z80-under-translation runs on the interpreter tier (ADR 0015 Decision 4); the gate runs the Z80 interpreter against shared 6502 RAM. No JIT work.
- **No `TimingTier` / `ITimingSensitive`** (ADR-only, not in `src/`).
- **HEAD grounding:** literal code is grounded against the shipped PR-A..H surface @ `d685b0c` plus PR-I's added `Core`/`Machines` surface. Verify PR-I is merged (`git log --oneline | grep dual-cpu`) before starting.

---

## File Structure

**New files (`CpuEmulator.Peripherals`):**
- `src/CpuEmulator.Peripherals/SoftCardTranslation.cs` — `IAddressTranslation` with the 6-branch table + the `translationEnabled` DIP flag (identity when disabled).
- `src/CpuEmulator.Peripherals/SoftCardControlPort.cs` — `IPeripheral` whose access toggles the active CPU via the captured `ICoprocessorControl`.

**New test files:**
- `tests/CpuEmulator.Tests/Apple2/SoftCardTranslationTests.cs` — the 6-branch boundary regression (the shortcut-killer) + the DIP-disable identity path.
- `tests/CpuEmulator.Tests/Apple2/SoftCardControlPortTests.cs` — the toggle flips the active CPU + ends the slice; the real-Z80-translated-run end-to-end gate.

No production source other than the two new peripheral files is touched (the translation + control port are pure additions that ride PR-I's seams).

---

### Task 1: `SoftCardTranslation` — the 6-branch table

**Files:**
- Create: `src/CpuEmulator.Peripherals/SoftCardTranslation.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardTranslationTests.cs`

**Interfaces:**
- Consumes: `IAddressTranslation` (PR-I, `CpuEmulator.Core`).
- Produces: `sealed class SoftCardTranslation : IAddressTranslation`, ctor `SoftCardTranslation(bool translationEnabled = true)`. `ToPhysical(uint logical)` implements the 6-branch table; when `translationEnabled` is false it returns `logical & 0xFFFF` (identity — the DIP-switch S1-1 disable, research §2).

**The verified table (ADR 0015 Decision 3 / research §2), top nibble of `logical & $FFFF`:**

| nibble | Z80 logical | → Apple physical | formula |
|---|---|---|---|
| 0–A | `$0000–$AFFF` | `$1000–$BFFF` | `logical + $1000` (additive) |
| B | `$B000–$BFFF` | `$D000–$DFFF` | `(logical & $FFF) + $D000` |
| C | `$C000–$CFFF` | `$E000–$EFFF` | `(logical & $FFF) + $E000` |
| D | `$D000–$DFFF` | `$F000–$FFFF` | `(logical & $FFF) + $F000` |
| E | `$E000–$EFFF` | `$C000–$CFFF` | `(logical & $FFF) + $C000` |
| F | `$F000–$FFFF` | `$0000–$0FFF` | `(logical & $FFF) + $0000` |

- [ ] **Step 1: Write the failing boundary test (the shortcut-killer)**

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardTranslationTests
{
    // The REFUTED shortcut (research §2, refuted 1-2): correct only for the low region. Used here ONLY
    // to prove the boundary test rejects it — branches 2-5 differ from the real table.
    private static uint Shortcut(uint logical) => (logical + 0x1000) & 0xFFFF;

    [Theory]
    // Branch 1 (additive) — the shortcut AGREES here, so this alone cannot detect the shortcut.
    [InlineData(0x0000u, 0x1000u)]
    [InlineData(0xAFFFu, 0xBFFFu)]
    // Branch 2 ($B000-$BFFF -> $D000-$DFFF) — SHORTCUT-KILLER (shortcut gives $C000/$CFFF).
    [InlineData(0xB000u, 0xD000u)]
    [InlineData(0xBFFFu, 0xDFFFu)]
    // Branch 3 ($C000-$CFFF -> $E000-$EFFF) — SHORTCUT-KILLER (shortcut gives $D000/$DFFF).
    [InlineData(0xC000u, 0xE000u)]
    [InlineData(0xCFFFu, 0xEFFFu)]
    // Branch 4 ($D000-$DFFF -> $F000-$FFFF) — SHORTCUT-KILLER (shortcut gives $E000/$EFFF).
    [InlineData(0xD000u, 0xF000u)]
    [InlineData(0xDFFFu, 0xFFFFu)]
    // Branch 5 ($E000-$EFFF -> $C000-$CFFF) — SHORTCUT-KILLER (shortcut gives $F000/$FFFF).
    [InlineData(0xE000u, 0xC000u)]
    [InlineData(0xEFFFu, 0xCFFFu)]
    // Branch 6 ($F000-$FFFF -> $0000-$0FFF) — the shortcut AGREES here (($F000+$1000) mod 64K = $0000),
    // so it pins the map but does NOT detect the shortcut on its own.
    [InlineData(0xF000u, 0x0000u)]
    [InlineData(0xFFFFu, 0x0FFFu)]
    public void ToPhysical_matches_the_six_branch_table_at_boundaries(uint logical, uint expected)
    {
        var t = new SoftCardTranslation();
        Assert.Equal(expected, t.ToPhysical(logical));
    }

    [Theory]
    // The four shortcut-killer boundaries: the real table MUST NOT equal the refuted shortcut here.
    // This is the structural guard against re-introducing the refuted map.
    [InlineData(0xB000u)]
    [InlineData(0xC000u)]
    [InlineData(0xD000u)]
    [InlineData(0xE000u)]
    public void ToPhysical_differs_from_the_refuted_shortcut_on_branches_2_through_5(uint logical)
    {
        var t = new SoftCardTranslation();
        Assert.NotEqual(Shortcut(logical), t.ToPhysical(logical));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~SoftCardTranslationTests"`
Expected: FAIL — `SoftCardTranslation` does not exist (compile error).

- [ ] **Step 3: Write the translation**

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Microsoft Z-80 SoftCard's Z80-logical -> Apple-physical address translation (ADR 0015
/// Decision 3; research §2, the MAME-verified a2softcard.cpp dma_r/dma_w table). A 6-way branch on the
/// top nibble of the 16-bit logical address. ONLY branch 1 ($0000-$AFFF) is a true additive +$1000;
/// branches 2-6 mask the low 12 bits and add a 4 KiB-window base (page-wrap) so CP/M's zero page/TPA land
/// on usable RAM while the Apple's immovable regions (6502 zero page/stack, the $0400 screen, the $C0xx
/// I/O) shuffle to the top of the Z80 map.
/// <para>The "+$1000 mod 64K" shortcut is REFUTED (research §2, 1-2): it is correct only for branch 1 and
/// the coincidental branch 6, and WRONG for branches 2-5. The boundary regression test guards against
/// re-introducing it.</para>
/// <para>The DIP switch S1-1 (research §2) disables translation: when <c>translationEnabled</c> is false,
/// ToPhysical is the identity (the Z80 sees the raw 6502 space). Construction-time config, defaulted on.</para></summary>
public sealed class SoftCardTranslation : IAddressTranslation
{
    private readonly bool _translationEnabled;

    public SoftCardTranslation(bool translationEnabled = true) => _translationEnabled = translationEnabled;

    public uint ToPhysical(uint logical)
    {
        logical &= 0xFFFF;
        if (!_translationEnabled)
            return logical;                 // DIP S1-1 ON: identity (no translation)

        uint nibble = logical >> 12;        // the top nibble selects the branch
        uint low = logical & 0x0FFF;        // the in-window offset (branches 2-6)
        return nibble switch
        {
            <= 0xA => logical + 0x1000,      // branch 1: $0000-$AFFF -> $1000-$BFFF (additive)
            0xB    => low + 0xD000,          // branch 2: $B000-$BFFF -> $D000-$DFFF (LC bank 2)
            0xC    => low + 0xE000,          // branch 3: $C000-$CFFF -> $E000-$EFFF
            0xD    => low + 0xF000,          // branch 4: $D000-$DFFF -> $F000-$FFFF (ROM / LC $F000)
            0xE    => low + 0xC000,          // branch 5: $E000-$EFFF -> $C000-$CFFF (6502 I/O space)
            _      => low + 0x0000,          // branch 6: $F000-$FFFF -> $0000-$0FFF (ZP/stack/screen/RWTS)
        };
    }
}
```

- [ ] **Step 4: Run the boundary tests**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~SoftCardTranslationTests"`
Expected: PASS (the 12 boundary cases + the 4 shortcut-killer cases).

- [ ] **Step 5: Add the DIP-disable identity test, then re-run**

Add to `SoftCardTranslationTests`:

```csharp
    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0xB000u)]
    [InlineData(0xE000u)]
    [InlineData(0xFFFFu)]
    public void DIP_disabled_translation_is_the_identity(uint logical)
    {
        var t = new SoftCardTranslation(translationEnabled: false);
        Assert.Equal(logical, t.ToPhysical(logical)); // identity: the Z80 sees the raw 6502 space
    }
```

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~SoftCardTranslationTests"`
Expected: PASS (all cases).

- [ ] **Step 6: Commit**

```bash
git add src/CpuEmulator.Peripherals/SoftCardTranslation.cs tests/CpuEmulator.Tests/Apple2/SoftCardTranslationTests.cs
git commit -m "feat(peripherals): SoftCardTranslation 6-branch table (boundary-gated vs refuted shortcut)"
```

---

### Task 2: A translated read/write proof against shared RAM (the `TranslatingAddressSpace` + `SoftCardTranslation` composition)

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardTranslationTests.cs` (add the composition test)

**Interfaces:**
- Consumes: `TranslatingAddressSpace` (PR-I), `SoftCardTranslation` (Task 1), the shipped `AddressSpace`.

**Design note:** Prove the translation does the right thing *through the wrapper PR-I ships* (not just the bare `ToPhysical`): a write to a Z80-logical address lands at the translated 6502-physical address in shared RAM. This pins branch 2 (`$B000`→`$D000`) and branch 6 (`$F000`→`$0000`) end-to-end through the bus, the two CP/M cares about most (high RAM + zero page).

- [ ] **Step 1: Write the composition test**

```csharp
    [Fact]
    public void A_translated_view_routes_writes_to_the_shared_6502_physical_address()
    {
        var ram = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        ram.MapMemory(0x0000, new byte[0x10000], writable: true);   // 64 KiB shared 6502 RAM
        var z80View = new TranslatingAddressSpace(ram, new SoftCardTranslation());

        // Branch 6: Z80 $F000 -> 6502 $0000 (CP/M's zero page lands on the Apple's low RAM).
        z80View.Write8(0xF000, 0xCA);
        Assert.Equal(0xCA, ram.Read8(0x0000));

        // Branch 2: Z80 $B000 -> 6502 $D000 (CP/M high RAM lands on the Language Card region).
        z80View.Write8(0xB000, 0x5A);
        Assert.Equal(0x5A, ram.Read8(0xD000));

        // And a Z80 read sees what the 6502 wrote at the translated address.
        ram.Write8(0x1000, 0x99);            // 6502 $1000 == Z80 $0000 (branch 1, +$1000)
        Assert.Equal(0x99, z80View.Read8(0x0000));
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~SoftCardTranslationTests.A_translated_view"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/SoftCardTranslationTests.cs
git commit -m "test(peripherals): translated view routes to shared 6502 physical RAM (branches 1/2/6)"
```

---

### Task 3: `SoftCardControlPort` — the `$CnXX` write toggles the active CPU

**Files:**
- Create: `src/CpuEmulator.Peripherals/SoftCardControlPort.cs`
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardControlPortTests.cs`

**Interfaces:**
- Consumes: `IPeripheral`, `IMachineContext`, `ICoprocessorControl`, `AccessWidth` (all `CpuEmulator.Core`).
- Produces: `sealed class SoftCardControlPort : IPeripheral`, ctor `SoftCardControlPort()`. `Name => "softcard"`. In `Realize`, captures the Machine via `context is ICoprocessorControl`. `Write` (any access — model as write, fire on any access per research §1 caveat) flips an internal `_coprocessorActive` and calls `SetCoprocessorActive`. `Read` mirrors the write side effect (the §1 "decoder likely fires on any access"). `TryPeek` is **peek-free** (a debugger looking at the control port must NOT switch CPUs — the ][+ peek-free invariant, ADR 0014 Decision 2 / the pattern every Apple2 device follows).

**Design notes (grounded against the shipped `Apple2Iou` peek-free pattern + the `IPeripheral` contract):**
- The toggle is a flip: from 6502 mode a `$CN00` write hands off to the Z80 (`_coprocessorActive = true`); the Z80's matching write (which it sees as `$EN00`, translated by branch 5 back to `$CN00`) hands back (`_coprocessorActive = false`). Modeling it as a **flip on each access** matches the hardware's single-register toggle (research §1).
- **Peek-free:** `TryPeek` returns open-bus 0 with NO state change (no `SetCoprocessorActive`), exactly as `Apple2Iou.TryPeek` short-circuits its side-effecting switches.
- If the control port is wired onto a board whose context is NOT an `ICoprocessorControl` (a single-CPU board), `_ctl` stays null and the writes are inert — never an exception (the seam degrades gracefully, ADR 0015 Decision 3).

- [ ] **Step 1: Write the failing control-port test**

```csharp
using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardControlPortTests
{
    // A minimal ICoprocessorControl spy: records the last SetCoprocessorActive value + the call count.
    private sealed class ControlSpy : IMachineContext, ICoprocessorControl
    {
        public bool? LastActive { get; private set; }
        public int Calls { get; private set; }
        public void SetCoprocessorActive(bool active) { LastActive = active; Calls++; }

        // IMachineContext members are unused by the control port's Realize (it only needs the cast).
        public IScheduler Scheduler => throw new NotSupportedException();
        public IAddressSpace Space(AddressSpaceKind kind) => throw new NotSupportedException();
        public IInterruptLine IrqLine => throw new NotSupportedException();
        public IInterruptLine NmiLine => throw new NotSupportedException();
    }

    [Fact]
    public void A_write_flips_the_active_cpu_via_the_coprocessor_control()
    {
        var spy = new ControlSpy();
        var port = new SoftCardControlPort();
        port.Realize(spy);

        port.Write(0x00, AccessWidth.Byte, 0x00);   // first $CN00 write: hand off to the coprocessor
        Assert.Equal(true, spy.LastActive);
        Assert.Equal(1, spy.Calls);

        port.Write(0x00, AccessWidth.Byte, 0x00);   // the matching write: hand back to the primary
        Assert.Equal(false, spy.LastActive);
        Assert.Equal(2, spy.Calls);
    }

    [Fact]
    public void TryPeek_is_side_effect_free_and_does_not_switch_cpus()
    {
        var spy = new ControlSpy();
        var port = new SoftCardControlPort();
        port.Realize(spy);

        bool ok = port.TryPeek(0x00, out byte v);
        Assert.True(ok);
        Assert.Equal(0x00, v);            // open-bus, side-effect-free
        Assert.Equal(0, spy.Calls);       // a debugger peek did NOT toggle the active CPU
    }

    [Fact]
    public void On_a_non_coprocessor_context_the_port_is_inert()
    {
        var port = new SoftCardControlPort();
        // Realize with a context that is NOT an ICoprocessorControl: the cast fails, _ctl stays null.
        port.Realize(new PlainContext());
        port.Write(0x00, AccessWidth.Byte, 0x00);   // must not throw (degrades gracefully)
    }

    private sealed class PlainContext : IMachineContext
    {
        public IScheduler Scheduler => throw new NotSupportedException();
        public IAddressSpace Space(AddressSpaceKind kind) => throw new NotSupportedException();
        public IInterruptLine IrqLine => throw new NotSupportedException();
        public IInterruptLine NmiLine => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~SoftCardControlPortTests"`
Expected: FAIL — `SoftCardControlPort` does not exist (compile error).

- [ ] **Step 3: Write the control port**

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Z-80 SoftCard control register at the slot's $CN00 page (ADR 0015 Decision 3; research
/// §1). A write toggles which CPU is the bus master: from 6502 mode a $CN00 write hands control to the
/// Z80 (and DMA-suspends the 6502); the Z80's matching write (which it sees as $EN00, translated back to
/// $CN00 by SoftCardTranslation branch 5) hands control back to the 6502. Modeled as a FLIP on each
/// access (the single-register toggle; research §1 "the decoder likely fires on any access" — so Read
/// mirrors Write). The flip is performed through ICoprocessorControl, captured from the Realize context
/// (the dual-CPU Machine IS the IMachineContext and implements ICoprocessorControl). On a single-CPU
/// board the cast fails and the port is inert (never an exception).
/// <para>PEEK-FREE (the ][+ invariant, ADR 0014 Decision 2): a debugger LOOKING at the control register
/// must NOT switch CPUs — TryPeek returns open-bus 0 with no side effect, mirroring Apple2Iou's peek-free
/// short-circuits on its side-effecting switches.</para></summary>
public sealed class SoftCardControlPort : IPeripheral
{
    private ICoprocessorControl? _ctl;
    private bool _coprocessorActive;

    public string Name => "softcard";

    public void Realize(IMachineContext context)
    {
        // The dual-CPU Machine is the IMachineContext and implements ICoprocessorControl; capture it so a
        // bus access can flip the active CPU. A single-CPU context fails the cast -> the port is inert.
        if (context is ICoprocessorControl ctl)
            _ctl = ctl;
    }

    public uint Read(uint offset, AccessWidth width)
    {
        Toggle();                 // any access fires the toggle (research §1)
        return 0x00;
    }

    public void Write(uint offset, AccessWidth width, uint value) => Toggle();

    public bool TryPeek(uint offset, out byte value)
    {
        // PEEK-FREE: a debugger view must not switch CPUs. No Toggle().
        value = 0x00;
        return true;
    }

    private void Toggle()
    {
        _coprocessorActive = !_coprocessorActive;
        _ctl?.SetCoprocessorActive(_coprocessorActive);
    }
}
```

- [ ] **Step 4: Run the control-port tests**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~SoftCardControlPortTests"`
Expected: PASS (the flip, the peek-free, and the inert-on-single-CPU cases).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Peripherals/SoftCardControlPort.cs tests/CpuEmulator.Tests/Apple2/SoftCardControlPortTests.cs
git commit -m "feat(peripherals): SoftCardControlPort toggles the active CPU on a $CnXX access (peek-free)"
```

---

### Task 4: The un-fakeable end-to-end gate — a real Z80 routine runs translated against shared 6502 RAM, and the `$CnXX` toggle flips the active CPU and ends the slice

**Files:**
- Test: `tests/CpuEmulator.Tests/Apple2/SoftCardControlPortTests.cs` (add the end-to-end gate)

**Interfaces:**
- Consumes: `BoardMachineFactory`, `BoardSpec`, `CoprocessorSpec`, `SoftCardTranslation` (Task 1), `SoftCardControlPort` (Task 3), the real 6502 + Z80 cores; `Machine.CoprocessorActive`/`Coprocessor`/`Cpu` (PR-I).

**Design notes — this is the row-J un-fakeable gate. Two halves, both end-to-end through a built `Machine`:**

1. **The `$CnXX` toggle flips the active CPU and ends the slice.** Build a real dual-CPU SoftCard-shaped board: 6502 primary, Z80 coprocessor, `SoftCardTranslation`, a `SoftCardControlPort` at the slot page (`$C200` — slot 2, a standard SoftCard slot; any `$Cn00` page works). The 6502 ROM writes the control port then spins; running the machine flips `CoprocessorActive` and the 6502 stops advancing (suspended), the Z80 advances (active).
2. **A real Z80 routine runs translated against shared 6502 RAM.** After the handoff, the Z80 (whose reset PC = 0, translated by branch 1 to physical `$1000`) executes a routine pre-loaded into shared RAM at physical `$1000` (= Z80 logical `$0000`) that writes a sentinel to Z80 `$F000` (branch 6 → physical `$0000`). Assert the 6502 (reading physical `$0000` directly) sees the sentinel — proving the Z80 ran *through the translation* against the *shared* RAM.

**Z80 routine (placed at physical `$1000` = Z80 logical `$0000`), writing $42 to Z80 `$F000` (→ physical `$0000`):**
```
; Z80 logical $0000 (physical $1000):
3E 42        LD A,$42
32 00 F0     LD ($F000),A   ; Z80 $F000 -> physical $0000 (branch 6)
18 FE        JR $-2         ; spin (relative -2: jump to self)
```
Bytes: `3E 42 32 00 F0 18 FE`. (Verify the Z80 opcodes against the shipped Z80 core's behavior: `LD A,n` = `$3E n`; `LD (nn),A` = `$32 nn`; `JR e` = `$18 e`, `e=$FE` is -2 = spin. These are core Z80 ops the M3/M6 Z80 core covers.)

- [ ] **Step 1: Write the end-to-end gate**

```csharp
    [Fact]
    public void Real_Z80_runs_translated_against_shared_6502_RAM_after_the_control_port_handoff()
    {
        // --- 6502 system ROM at $D000-$FFFF: write the control port at $C200 (hand off to the Z80), spin.
        var rom = new byte[0x3000];
        // $D000: 8D 00 C2   STA $C200   (write the SoftCard control port -> hand off to the Z80)
        // $D003: 4C 03 D0   JMP $D003   (spin; the 6502 is now DMA-suspended)
        rom[0x0000] = 0x8D; rom[0x0001] = 0x00; rom[0x0002] = 0xC2;
        rom[0x0003] = 0x4C; rom[0x0004] = 0x03; rom[0x0005] = 0xD0;
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000

        var translation = new SoftCardTranslation();
        var control = new SoftCardControlPort();
        var spec = new BoardSpec("apple2-softcard-test", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),                 // $0000-$BFFF shared RAM
                new MemoryRegion(0xC000, 0x0200, RegionKind.Mmio),                // $C000-$C1FF I/O band
                new MemoryRegion(0xC200, 0x0100, RegionKind.Mmio),               // $C200 control-port page
                new MemoryRegion(0xC300, 0x0D00, RegionKind.Mmio),               // rest of the I/O band
                new MemoryRegion(0xD000, 0x3000, RegionKind.Rom, rom),            // $D000-$FFFF ROM
            ],
            Peripherals: [ new PeripheralSlot("softcard", control, 0xC200, 0x0100) ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            Coprocessor: new CoprocessorSpec(
                CpuKind.Z80, translation, "softcard", ClockRatioToPrimary: 2.0));

        var machine = BoardMachineFactory.Build(spec);   // interpreter tier (coprocessor always interpreter)

        // Pre-load the Z80 routine into shared RAM at PHYSICAL $1000 (= Z80 logical $0000, branch 1).
        var bus = machine.Space(AddressSpaceKind.Program);
        byte[] z80Routine = [0x3E, 0x42, 0x32, 0x00, 0xF0, 0x18, 0xFE]; // LD A,$42; LD ($F000),A; JR -2
        for (uint i = 0; i < z80Routine.Length; i++)
            bus.Write8(0x1000 + i, z80Routine[i]);

        machine.Reset();
        Assert.False(machine.CoprocessorActive);          // the 6502 starts active

        // 1) Run the 6502 until it writes $C200 and hands off.
        machine.Run(100);
        Assert.True(machine.CoprocessorActive);           // the $CnXX write flipped the active CPU
        long six502Cycles = machine.Cpu.CycleCount;

        // 2) The Z80 resets to PC=0; run it. It fetches from physical $1000 (Z80 $0000), runs the routine,
        //    and writes $42 to Z80 $F000 -> physical $0000.
        machine.Coprocessor!.Reset();                      // Z80 reset: PC=0
        machine.Run(200);

        // The Z80 ran THROUGH the translation against the SHARED RAM: the 6502 reads physical $0000.
        Assert.Equal(0x42, bus.Read8(0x0000));
        // The suspended 6502 did NOT advance while the Z80 was the bus master.
        Assert.Equal(six502Cycles, machine.Cpu.CycleCount);
    }
```

- [ ] **Step 2: Run the gate**

Run: `dotnet test tests/CpuEmulator.Tests --filter "FullyQualifiedName~SoftCardControlPortTests.Real_Z80"`
Expected: PASS.

Implementation notes if it fails:
- If the 6502 needs more cycles to reach `STA $C200`, raise the first `Run` budget (the routine is at the reset vector, so a handful of instructions; ~20–50 cycles suffice).
- If the Z80 has not finished the 3-instruction routine in 200 virtual cycles, raise the second budget — note the Z80 runs at ~2× via the ratio, so 200 virtual ≈ 400 Z80 cycles, ample for 3 instructions.
- Confirm the Z80 core's reset sets PC=0 (it does — `Z80Cpu.Reset` sets `PC = 0`, grounded at `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs`). The `machine.Coprocessor!.Reset()` is explicit so the test does not depend on whether `BoardMachineFactory` reset the coprocessor at build (it resets the primary via `machine.Reset()` only).

- [ ] **Step 3: Commit**

```bash
git add tests/CpuEmulator.Tests/Apple2/SoftCardControlPortTests.cs
git commit -m "test(peripherals): real Z80 runs translated vs shared 6502 RAM; $CnXX toggle ends the slice"
```

---

### Task 5: Final gate — full suite + warning-clean build

**Files:** none (verification only).

- [ ] **Step 1: Full build, warning-clean**

Run: `dotnet build CpuEmulator.sln`
Expected: Build succeeded, **0 warnings**.

- [ ] **Step 2: Full test suite**

Run: `dotnet test CpuEmulator.sln`
Expected: the post-PR-I baseline green plus the new PR-J tests (SoftCardTranslation + SoftCardControlPort), 0 failed. **No pre-existing test regresses** (PR-J adds only two new peripheral files + tests; it touches no shipped source — the single-CPU + base-Apple paths are untouched).

- [ ] **Step 3: Confirm the un-fakeable gates ran**

Confirm these are in the passing set:
- `ToPhysical_matches_the_six_branch_table_at_boundaries` (12 cases) + `ToPhysical_differs_from_the_refuted_shortcut_on_branches_2_through_5` (4 cases) — the 6-branch boundary regression (the shortcut-killer).
- `Real_Z80_runs_translated_against_shared_6502_RAM_after_the_control_port_handoff` — the real Z80 runs translated + the `$CnXX` toggle flips active-CPU and ends the slice.

---

## Self-Review

**1. Spec coverage (ADR 0015 Decision 3 + the row-J gate):**
- The complete 6-branch table (NOT the `+$1000` shortcut) → Task 1. ✓
- The boundary regression at every branch, with the refuted shortcut failing where it differs → Task 1 (precise: shortcut-killers are branches 2–5; branches 1 + 6 coincide, so they pin the map but the killers are the 4 middle branches — spelled out in the Global Constraints + the test comments). ✓
- The DIP-switch S1-1 translation-disable (identity) → Task 1. ✓
- The per-CPU translating address-space view (composition over the shared RAM, incl. branch 2 `$B000→$D000` LC-bank-2 and branch 5 `$E000→$C000` I/O) → Tasks 1 + 2. ✓
- The `$CnXX`-write control port that toggles which CPU runs, peek-free → Task 3. ✓
- A real Z80 routine runs translated against shared 6502 RAM; the toggle flips active-CPU and ends the slice → Task 4. ✓

**2. Placeholder scan:** No TBD/TODO/"implement later"/"similar to Task N". Every code step shows literal code (translation, control port, all tests, the Z80 routine bytes). The "if it fails" notes in Task 4 are budget-tuning guidance, not missing code — the literal test is complete.

**3. Type consistency:** `SoftCardTranslation(bool translationEnabled = true)`, `ToPhysical(uint)`, `SoftCardControlPort()`, `Name => "softcard"`, `SetCoprocessorActive(bool)` (consumed from PR-I), `CoprocessorSpec(CpuKind, IAddressTranslation, string, double)` (PR-I), `Machine.CoprocessorActive`/`Coprocessor`/`Cpu`/`Space` (PR-I) are used identically across tasks. The control-port slot name `"softcard"` matches `CoprocessorSpec.ControlPortPeripheral` in the Task 4 gate.

**Builder-readiness note:** the only cross-PR dependency is PR-I's shipped surface (`IAddressTranslation`, `TranslatingAddressSpace`, `ICoprocessorControl`, `CoprocessorSpec`, dual-CPU `Machine`/`Run`, `BoardMachineFactory` wiring). The Z80 routine bytes (`3E 42 32 00 F0 18 FE`) are core Z80 ops the shipped M3/M6 core covers; the 6502 routine bytes are the same `STA abs`/`JMP abs`/reset-vector pattern the shipped PR-E LanguageCard test uses. No open assumptions.
