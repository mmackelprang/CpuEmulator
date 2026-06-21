# Plan — CPM-3: `RunDualCpu` yields at the `$CnXX` toggling instruction

> **Arc:** SoftCard CP/M boot-to-`A>` (ADR 0017). **PR 3 of 4.** Depends on CPM-1, CPM-2.
> **Grounded against:** `main` @ `1d0232c` + CPM-1/CPM-2 landed.
> **ADR:** Decision 3 (amends ADR 0015 Decision 1), PR-3 in §3. **Queue row:** **CPM-3**.

## Why this PR

The third defect (ADR 0017 §1.7, live-verified): `RunDualCpu` drives the active core with `Cpu.Run(ref budget)`
/ `copro.Run(ref budget)` for the **whole slice**. The generated `Run` is an un-interruptible
`while (budget > 0) { Step(); }`. When the active CPU writes `$CnXX` to hand control over,
`SetCoprocessorActive` sets `_z80Active` + `_sliceEndRequested`, but `Run` **keeps executing the rest of the
budget** — the just-disabled CPU runs thousands more (now cross-translated, meaningless) instructions before
the loop re-checks the flag. This corrupts every Z80↔6502 BIOS round-trip (disk read, CONOUT): the loaded
CP/M system at `$Axxx` is overwritten and the Z80 falls back to its reset stub.

The fix (ADR 0017 Decision 3): drive the active core **one instruction at a time** via `ICpuCore.Step()` and
check `_sliceEndRequested` after each step, so a `$CnXX` write yields control **at the writing instruction**.
This is localized to the `_coprocessor is not null` branch — the single-CPU `RunSingleCpu` path is
**byte-for-byte unchanged**.

**Definition of done:** a synthetic dual-CPU yield regression test proves the switch lands at the writing
instruction (the other core runs the very next instruction, not after the budget); the single-CPU path is
provably unchanged (the full pre-existing suite stays green); with CPM-1+2+3 the live boot reaches the CP/M
BIOS executing on the Z80 at `$Axxx` stably (no fallback to the reset stub). This PR still may not paint `A>`
— that is CPM-4 (the `$1010` bridge bring-up).

---

## Background: how `RunDualCpu` works today (grounded against `1d0232c`)

`src/CpuEmulator.Core/Machine.cs`:

- The scheduler clock is a **bound time source** (Machine ctor, lines 78-79):
  `Cpu.CycleCount + round(_coprocessorCyclesContributed / ratio)`. So `_scheduler.CurrentCycle` is *derived*
  — it advances automatically as `Cpu.CycleCount` and `_coprocessorCyclesContributed` grow. `AdvanceTo(x)`
  fires due events and commits time; it does NOT set the clock value.
- `SetCoprocessorActive(active)` (lines 97-101) sets `_z80Active = active` and `_sliceEndRequested = true`.
- The current loop (lines 150-207) runs the active core with `Run(ref budget)` for a whole slice, advances
  the scheduler, force-switches to the primary on a pending interrupt, then `if (_sliceEndRequested) continue;`
  (a documented no-op — the toggle already took effect via `_z80Active`).
- `ICpuCore` exposes `void Step()` (one instruction, always advances `CycleCount` by ≥1) and
  `long CycleCount` (monotonic).

The rewrite replaces the inner `Run(ref budget)` calls with a per-instruction `Step()` loop that breaks the
moment `_sliceEndRequested` is set inside a `Step()` (i.e. that instruction wrote `$CnXX`).

---

## Task 1 — The synthetic dual-CPU yield regression test (write first)

Model it on the shipped `SoftCardControlPortTests.Real_Z80_runs_translated_against_shared_6502_RAM_after_the_control_port_handoff`
(it already builds a real 2-CPU `BoardSpec` with a `SoftCardControlPort` + `SoftCardTranslation`). The new
test proves the **yield point**: the 6502 writes the control port, then executes ONE more instruction that
writes a sentinel; with the per-instruction yield, that sentinel write must NOT happen before the Z80 runs.

The cleanest un-fakeable proof: after the `$CnXX` write, the very next 6502 instruction stores a sentinel to
shared RAM. With the OLD whole-slice `Run`, the 6502 (already toggled-off but still running its budget)
executes that store before yielding. With the NEW per-instruction yield, the `$CnXX` write is the last 6502
instruction of the slice — the Z80 runs next, and the sentinel store is deferred until control returns to the
6502.

Add `tests/CpuEmulator.Tests/Apple2/DualCpuYieldTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class DualCpuYieldTests
{
    // Build a minimal dual-CPU machine: 6502 primary + Z80 coprocessor over a SoftCardTranslation, with a
    // SoftCardControlPort at $C200. Mirrors SoftCardControlPortTests' synthetic board.
    private static (Machine machine, IAddressSpace bus) BuildDualCpu(byte[] rom)
    {
        var translation = new SoftCardTranslation();
        var control = new SoftCardControlPort();
        var spec = new BoardSpec("apple2-softcard-yield-test", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),
                new MemoryRegion(0xC000, 0x0200, RegionKind.Mmio),
                new MemoryRegion(0xC200, 0x0100, RegionKind.Mmio),
                new MemoryRegion(0xC300, 0x0D00, RegionKind.Mmio),
                new MemoryRegion(0xD000, 0x3000, RegionKind.Rom, rom),
            ],
            Peripherals: [ new PeripheralSlot("softcard", control, 0xC200, 0x0100) ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            Coprocessor: new CoprocessorSpec(
                CpuKind.Z80, translation, "softcard", ClockRatioToPrimary: 2.0));

        var machine = BoardMachineFactory.Build(spec);
        return (machine, machine.Space(AddressSpaceKind.Program));
    }

    [Fact]
    public void The_active_core_yields_at_the_control_port_write_not_after_the_slice_budget()
    {
        // 6502 ROM at $D000:
        //   $D000: 8D 00 C2   STA $C200   ; write the control port -> hand off to the Z80 (yield HERE)
        //   $D003: A9 5A      LDA #$5A
        //   $D005: 8D 00 02   STA $0200   ; sentinel: must NOT execute until control returns to the 6502
        //   $D008: 4C 08 D0   JMP $D008   ; spin
        var rom = new byte[0x3000];
        rom[0x0000] = 0x8D; rom[0x0001] = 0x00; rom[0x0002] = 0xC2;
        rom[0x0003] = 0xA9; rom[0x0004] = 0x5A;
        rom[0x0005] = 0x8D; rom[0x0006] = 0x00; rom[0x0007] = 0x02;
        rom[0x0008] = 0x4C; rom[0x0009] = 0x08; rom[0x000A] = 0xD0;
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000

        var (machine, bus) = BuildDualCpu(rom);

        // Pre-load a Z80 routine at PHYSICAL $1000 (= Z80 logical $0000, branch 1) that writes a DIFFERENT
        // sentinel to Z80 $F000 (= physical $0000) and spins.
        //   3E 99  LD A,$99 ; 32 00 F0  LD ($F000),A ; 18 FE  JR -2
        byte[] z80 = [0x3E, 0x99, 0x32, 0x00, 0xF0, 0x18, 0xFE];
        for (uint i = 0; i < z80.Length; i++) bus.Write8(0x1000 + i, z80[i]);

        machine.Reset();
        Assert.False(machine.CoprocessorActive);

        // Run a SMALL budget -- enough for the 6502 to reach + execute the STA $C200, but the per-instruction
        // yield must stop the 6502 at that write (so the $5A sentinel at $0200 stays UNWRITTEN this slice).
        machine.Run(20);

        Assert.True(machine.CoprocessorActive,
            "the $C200 write must have flipped the active CPU to the Z80");
        Assert.NotEqual(0x5A, bus.Read8(0x0200));
        // ^ With the OLD whole-slice Run, the 6502 ran past its toggle and wrote $5A before yielding.
        //   With the per-instruction yield, the $C200 write is the LAST 6502 instruction of the slice.

        // Now let the Z80 run: it writes $99 to physical $0000. The 6502 is DMA-suspended (no $5A yet).
        machine.Coprocessor!.Reset();   // Z80 PC=0
        machine.Run(50);
        Assert.Equal(0x99, bus.Read8(0x0000));
        Assert.NotEqual(0x5A, bus.Read8(0x0200));   // the suspended 6502 still has not written its sentinel
    }
}
```

Run: `dotnet test … --filter "FullyQualifiedName~The_active_core_yields_at_the_control_port_write"`
→ FAILS against the current whole-slice `Run` (the 6502 runs past the toggle and writes `$5A` to `$0200`).
That is the correct first-failure — it pins the exact defect.

> **Grounding caveat (Builder, verify on the first run):** the 6502's `Run(ref budget)` with a tiny budget
> (20 cycles) might already stop near the `$C200` write under the OLD code purely because the budget is
> small — which would make the test pass for the wrong reason and not actually exercise the defect. To make
> the OLD-code failure robust, the ROM places `LDA #$5A; STA $0200` **immediately** after `STA $C200`, and
> the test budget (20) is comfortably more than the ~6 cycles those three instructions take. If the OLD code
> still passes (budget-stopping masks the defect), increase the budget to a value that lets `Run` execute
> the whole sequence (e.g. 200) and re-confirm the OLD code writes `$5A` while the NEW code does not. The
> invariant under test is *position of the yield*, not budget size — tune the budget so the OLD whole-slice
> `Run` provably overshoots.

---

## Task 2 — Rewrite `RunDualCpu` to step the active core

### 2a. Implement

Edit `src/CpuEmulator.Core/Machine.cs`. Replace the body of `RunDualCpu` (lines 150-207) with a
per-instruction stepping loop. Keep: the virtual-clock domain (`_scheduler.CurrentCycle`), the slice-to-next
-event bound, the coprocessor-cycle contribution (ratio conversion), the interrupt-forces-switch rule, and
the no-progress guard. Move them from "after the whole `Run`" to "after each `Step`".

```csharp
    private long RunDualCpu(long cycles)
    {
        long virtualStart = _scheduler.CurrentCycle;
        long target = virtualStart + cycles;
        while (_scheduler.CurrentCycle < target)
        {
            _sliceEndRequested = false;
            long sliceEnd = _scheduler.TryPeekNextEventCycle(out long eventCycle)
                            && eventCycle < target
                ? Math.Max(eventCycle, _scheduler.CurrentCycle + 1)
                : target;

            // Drive the ACTIVE core ONE INSTRUCTION AT A TIME, yielding the instant a $CnXX write flips it
            // (SetCoprocessorActive sets _sliceEndRequested INSIDE Step). ADR 0017 Decision 3.
            while (_scheduler.CurrentCycle < sliceEnd && !_sliceEndRequested)
            {
                if (!_z80Active)
                {
                    long before = Cpu.CycleCount;
                    Cpu.Step();                                   // exactly one 6502 instruction
                    if (Cpu.CycleCount <= before)
                        throw new EmulationException(
                            $"CPU '{Cpu.Architecture}' made no progress during Step; aborting to avoid a hang.");
                }
                else
                {
                    ICpuCore copro = _coprocessor!;
                    long coproBefore = copro.CycleCount;
                    copro.Step();                                 // exactly one Z80 instruction
                    long coproRan = copro.CycleCount - coproBefore;
                    if (coproRan <= 0)
                        throw new EmulationException(
                            $"Coprocessor '{copro.Architecture}' made no progress during Step; aborting to avoid a hang.");
                    _coprocessorCyclesContributed += coproRan;    // convert to the virtual clock via the ratio
                }

                // Fire any events due at the new (derived) virtual time.
                _scheduler.AdvanceTo(_scheduler.CurrentCycle);

                // A pending interrupt forces a switch to the primary (ADR 0015 Decision 5: all interrupts to
                // the 6502; while the coprocessor runs the primary is DMA-suspended, so an IRQ resumes it).
                if (_z80Active && (IrqLine.IsAsserted || NmiLine.IsAsserted))
                    _z80Active = false;
                // _sliceEndRequested (set by a $CnXX write this instruction) breaks the inner loop; _z80Active
                // already selects the other core for the next instruction (the writing instruction completed
                // first -- ADR 0015 OQ5: the switch takes effect on the next dispatch).
            }
        }
        return _scheduler.CurrentCycle - virtualStart;
    }
```

Notes for the Builder:

- `_scheduler.CurrentCycle` is the bound time source — after `Cpu.Step()` it reflects the new
  `Cpu.CycleCount`; after `copro.Step()` it reflects `_coprocessorCyclesContributed / ratio`. So the inner
  `while (_scheduler.CurrentCycle < sliceEnd)` advances correctly without manually tracking cycles.
- The `+1` floor on `sliceEnd` guarantees forward progress when an event sits at/behind the clock (same as
  the old code).
- The old `if (_sliceEndRequested) continue;` no-op + the CS0414 comment are gone — `_sliceEndRequested` is
  now genuinely read by the inner `while` condition, so the warning it worked around no longer applies. The
  field is genuinely used.
- The `_scheduler.AdvanceTo(_scheduler.CurrentCycle)` per-step call matches `RunSingleCpu`'s
  `AdvanceTo(Cpu.CycleCount)` (fire due events). It is cheap; the dormant core is still never stepped.

### 2b. Confirm the regression test passes

Run the Task-1 filter → green: the 6502 yields at `STA $C200`, `$0200` stays un-written until the 6502
resumes.

---

## Task 3 — The single-CPU path is byte-for-byte unchanged (the load-bearing gate)

This is the regression the whole arc rests on. The change is entirely inside `RunDualCpu`
(`_coprocessor is not null`); `RunSingleCpu` (lines 123-142) and the `Run` dispatcher (lines 112-119) are
untouched. Prove it:

1. **Run the FULL pre-existing suite** in Release: `dotnet test -c Release --nologo`. Every single-CPU board
   (6502 Klaus/TomHarte vectors, Spectrum, Apple2 boot, M68k, 8088, Z80 zex) must stay green and
   regression-identical. The single-CPU path never enters `RunDualCpu`.
2. **Targeted dual-CPU regressions:** the existing
   `SoftCardControlPortTests.Real_Z80_runs_translated_against_shared_6502_RAM_after_the_control_port_handoff`
   must still pass — it asserts the Z80 runs translated AND the suspended 6502 does not advance while the Z80
   is the bus master (`Assert.Equal(six502Cycles, machine.Cpu.CycleCount)`). Under per-instruction stepping
   the 6502 is still never stepped while `_z80Active`, so this invariant holds. Run it explicitly.

> **Grounding caveat — the dual-CPU clock conversion is now finer-grained.** ADR 0017 OQ3 (inherited from
> ADR 0015 OQ3): per-`Step` virtual-clock conversion (`round(coproCycles / ratio)` accumulated per
> instruction) is finer than per-`Run`. The accumulation is on the running TOTAL
> (`_coprocessorCyclesContributed += coproRan` each step), and the bound time source rounds the total — so
> rounding does not drift per-step (it rounds once, on the running sum). Confirm the existing dual-CPU tests'
> cycle assertions still hold exactly. If any timing-exact dual-CPU assertion shifts by 1 cycle, it is the
> rounding granularity — document it; CP/M is coarse-timed and unaffected (ADR 0017 Decision 3 rationale).

---

## Task 4 — Live: CP/M BIOS runs stably on the Z80 at `$Axxx`

### 4a. The CPM-3 live gate (asset-gated)

ADR 0017 PR-3's gate: with CPM-1+2+3 the live boot reaches the CP/M BIOS executing on the Z80 stably (the
disk advances to data tracks; the Z80 PC reaches `$Axxx`) with no fallback to the reset stub. A robust,
un-fakeable proxy for "the Z80 ran real CP/M BIOS code stably": sample the Z80's PC across the boot and assert
it reaches the `$Axxx` BIOS region AND stays out of the `$0000` reset-stub region in the LATER part of the
boot (the stability the run-loop yield delivers).

Add to `tests/CpuEmulator.Tests/Apple2/SoftCardBoardTests.cs`:

```csharp
[SoftCardCpmFact]
public void Cpm_boot_runs_the_z80_bios_at_Axxx_stably_after_the_run_loop_yield()
{
    // ADR 0017 PR-3: with the per-track skew (CPM-1) + open-bus Read (CPM-2) + the run-loop yield (CPM-3),
    // the Z80 executes real CP/M BIOS code in the $Axxx region and -- crucially -- does NOT collapse back to
    // its $0000 reset stub once it gets there (the instability the whole-slice Run caused). We sample the
    // Z80 PC over the boot and assert it reached $A000-$AFFF and that, in the LATER boot window, it is no
    // longer stuck at the reset stub ($0000-$00FF).
    var (systemRomPath, cpmDiskPath) = SoftCardCpmVectors.TryGetAssets()!.Value;
    byte[] systemRom = Apple2Rom.Load(systemRomPath);
    byte[] diskBootRom = Apple2Rom.TryLoadDiskRom()
        ?? throw new InvalidOperationException("the slot-6 disk2.rom is required for the CP/M boot gate");
    IBlockDevice cpm = SoftCardCpm.LoadBlockDevice(cpmDiskPath);

    var state = new Apple2VideoState();
    var lc = new Apple2LanguageCard(systemRom);
    var drive1 = new DskFluxImage(cpm, SectorOrderKind.Cpm);
    var disk = new Apple2DiskII(drive1);
    var iou = new Apple2Iou(state, lc, disk);
    BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
    Machine machine = BoardMachineFactory.Build(spec);

    machine.Reset();
    bool reachedAxxx = false;
    bool lateInResetStub = false;
    const long slice = 50_000;
    long lateThreshold = CpmBootCycles * 3 / 4;   // the last quarter of the boot is the "stable" window
    for (long run = 0; run < CpmBootCycles; run += slice)
    {
        machine.Run(slice);
        if (machine.Coprocessor is { } z80 && machine.CoprocessorActive)
        {
            ulong pc = z80.GetRegister("PC");            // see grounding note for the exact register name
            if (pc is >= 0xA000 and <= 0xAFFF) reachedAxxx = true;
            if (run >= lateThreshold && pc <= 0x00FF) lateInResetStub = true;
        }
    }

    Assert.True(reachedAxxx, "expected the Z80 to execute CP/M BIOS code in the $Axxx region during the boot");
    Assert.False(lateInResetStub,
        "the Z80 fell back to its $0000 reset stub late in the boot -- the run-loop yield did not stabilise " +
        "the BIOS handshake");
}
```

> **Grounding notes (Builder, verify against the shipped Z80 core):**
> - The Z80 core's program-counter register is `"PC"` (verified: `Z80Spec.cs:47`,
>   `new("PC", 16, RegisterRole.ProgramCounter)`). `ICpuCore.GetRegister("PC")` returns it zero-extended to
>   64 bits.
> - Sampling PC only at slice boundaries observes the PC *between* `Run` calls, which is sufficient for a
>   coarse "did it reach $Axxx / is it stuck at the stub" check. If slice-boundary sampling is too coarse to
>   ever catch the $Axxx window, shrink `slice` (e.g. 5_000) — it only affects observation granularity, not
>   the emulation.
> - The exact `$Axxx` BIOS load region + the `lateThreshold` fraction are pinned by the ADR's live trace
>   ("the Z80 executes real CP/M BIOS code at `$Axxx`"). On the first live run, if the Z80 settles in a
>   different but clearly-not-reset-stub region, adjust the `reachedAxxx` band to the observed BIOS region
>   and record the actual addresses in a comment. The *stability* assertion (`!lateInResetStub`) is the
>   load-bearing one for CPM-3; the `$Axxx` reach is the "it loaded CP/M" companion.

### 4b. Verify the gate FAILS without CPM-3

Before applying Task 2 (or by stashing it), run `Cpm_boot_runs_the_z80_bios_at_Axxx_stably_*` → it should
FAIL (`lateInResetStub` true — the whole-slice `Run` corrupts the handshake and the Z80 collapses back to the
reset stub, per the ADR's live trace). With CPM-3 applied → it PASSES. Un-fakeable gate.

---

## Task 5 — Verify

1. `dotnet test … --filter "FullyQualifiedName~DualCpuYield"` → green.
2. `dotnet test … --filter "FullyQualifiedName~Cpm_boot_runs_the_z80_bios_at_Axxx_stably"` → green (assets).
3. **Full solution Release** → 0 failed, warning-clean. The single-CPU path regression is the whole-suite
   green (Task 3.1).

---

## Self-review checklist

- [ ] **Single-CPU path untouched:** the diff to `Machine.cs` is confined to the `RunDualCpu` method body;
      `RunSingleCpu` + `Run` dispatcher are unchanged. Full pre-existing suite green.
- [ ] **Yield point proven:** the synthetic regression FAILS pre-fix (6502 writes `$5A` past its toggle),
      PASSES post-fix.
- [ ] **No-progress guards:** present on both `Step` branches (an undefined-opcode core still advances ≥1
      cycle per `ICpuCore.Step` contract, so they never false-fire).
- [ ] **`_sliceEndRequested` genuinely read:** the inner `while` condition reads it; the old CS0414 no-op
      `continue` + its comment are removed.
- [ ] **Interrupt-forces-primary preserved:** the `if (_z80Active && (IrqLine||NmiLine)) _z80Active = false;`
      rule moved to per-step (finer, not different).
- [ ] **Clock conversion:** `_coprocessorCyclesContributed` accumulates the running total per step; the bound
      time source rounds the total (no per-step drift). Dual-CPU cycle assertions still exact.
- [ ] **Live stability gate is un-fakeable:** FAILS pre-fix (reset-stub collapse), PASSES post-fix.
- [ ] Full solution 0-failed in Release.

---

## Drift from ADR 0017 (flag in the PR body)

1. **The ADR pseudocode references `AdvanceSchedulerAndMaybeForceInterruptSwitch()`** as a placeholder; this
   plan inlines it as `_scheduler.AdvanceTo(_scheduler.CurrentCycle)` + the existing interrupt-force rule,
   matching the shipped scheduler/clock semantics (the bound time source). No behavioral difference from the
   ADR's intent.
2. **The ADR pseudocode's `_coprocessorCyclesContributed += ran` is per-instruction** here (vs. the old
   per-`Run` accumulation). Grounded as safe (running-total rounding) in Task 3; flagged because it is the
   one numeric-behavior change in an otherwise structural rewrite.
3. **PR-3 may still not paint `A>`** — Decision 4 says the `$1010` bridge completion is CPM-4's bring-up. The
   CPM-3 gate asserts BIOS-at-`$Axxx` stability, not `A>`.
