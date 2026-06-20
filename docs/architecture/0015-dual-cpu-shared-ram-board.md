# ADR 0015 — The dual-CPU shared-RAM board: two CPUs over one program space, a per-CPU address-translation seam, and a soft-switch-driven active-CPU scheduler (the Z80 SoftCard)

> **Status:** PROPOSED (Architect phase, Apple ][+ arc). **The load-bearing decision of the arc.** No implementation now
> — this ADR decides how the shipped single-CPU `Machine`/`MachineBuilder`/`BoardSpec`/`BoardMachineFactory` model must
> extend to express **two CPUs sharing one program space, with a per-CPU address-translation seam and a soft-switch-
> driven active-CPU scheduler**, so the Planner can break it into PRs. The base ][+ board is **ADR 0014**; the CP/M
> deliverable (Videx second-display seam + asset/licensing) is **ADR 0016**.
> **Date:** 2026-06-20
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Reads as ground truth:** `docs/research/apple-2-plus-z80-softcard-cpm-analysis.md` — the MAME-grounded blueprint:
> run-one-then-the-other arbitration (§7), the `$CnXX`-write toggle + DMA-halt (§1), all-interrupts-to-the-6502 (§1),
> the complete 6-branch Z80→Apple translation table (§2), and the ~2.04 MHz Z80 clock (§3). Section references are to
> that doc unless noted.
> **Supersedes / relates to:**
> - **The shipped single-CPU Machine model** (`src/CpuEmulator.Core/Machine.cs`, `MachineBuilder.cs`;
>   `src/CpuEmulator.Machines/BoardSpec.cs`, `BoardMachineFactory.cs`, `CpuCoreFactory.cs`). This ADR's whole job is to
>   extend these — they are hardcoded single-CPU at four exact points (§1.2).
> - **ADR 0014** (the base ][+ board) — the SoftCard board is the base ][+ board **plus** a second CPU + translation +
>   scheduler. It reuses the base board's RAM, ROM, IOU, video, Language Card, and Disk II unchanged; the Z80 reaches
>   them *through the translation table*.
> - **ADR 0009** (device↔JIT contract) — the Z80's entire 64 K view is a *translation* over the shared 6502 RAM; this
>   interacts with the JIT's fastmem (Decision 4). The Language Card remap seam ADR 0014 builds is reused by the Z80
>   translation (`$B000–$DFFF` maps onto LC bank 2 + ROM).
> - **ADR 0001** (Z80 as second architecture) — we own a full, parity-gated Z80 core (M3/M6); this is integration, **not**
>   new-CPU work.
> - **ADR 0002** (flat page table) — the Z80's translated view is still a 16-bit (`addressBits: 16`) space.

---

## 1. Context

### 1.1 What the SoftCard actually is (the hardware the model must capture)

The Microsoft Z-80 SoftCard turns the ][+ into a dual-CPU machine (research executive summary + §1–3):

- A **~2.04 MHz Z80** (≈2× the 6502, bus-synchronized off the 14.31818 MHz master — not free-running) and the host
  **~1.02 MHz 6502** share the **same DRAM**. Only **one CPU is bus-master at a time.**
- Control passes by a **single slot-dependent soft-switch write** (§1): from 6502 mode, a write to `$CN00` (N = slot)
  releases the Z80 and **DMA-suspends the 6502** (the card asserts the Apple DMA′ line; the 6502 sits halted). The Z80
  returns control by writing the *same* register — which it sees as `$EN00` in its translated space — re-suspending
  itself and resuming the 6502 where it stopped. *(Caveat §1: one source recalls a read; the documented protocol + MAME
  use a write. Model it as a write; the decoder likely fires on any access.)*
- **All interrupt processing is the 6502's** (§1): the 6502 is the CPU with the interrupt wiring, and when the Z80 runs
  the 6502 is DMA-suspended — so interrupts are routed to and serviced by the 6502, never the Z80.
- The Z80's entire 64 K logical space is **translated** onto the shared 6502 physical RAM (§2) so CP/M's zero page/TPA
  land on usable RAM while the Apple's immovable regions (6502 zero page/stack at `$0000`, the `$0400` text screen, the
  `$C0xx` I/O) shuffle to the top of the Z80 map. The translation is active **only while the Z80 runs**.
- The SoftCard carries **no onboard ROM/RAM** (§9) — it is pure CPU hardware; all CP/M software is on the disk. So there
  is no SoftCard firmware to source (ADR 0016 handles the CP/M *disk* asset).

### 1.2 The shipped model is single-CPU at four exact points (the precise extension surface)

`Machine` and its builder assume exactly one CPU. The four places (file:line verified):

1. **`Machine.Cpu` is a single `ICpuCore`** (`Machine.cs:16`), built by a single `cpuFactory` (`Machine.cs:42`).
2. **`Machine.Run` drives that one CPU** (`Machine.cs:68–89`): `Cpu.Run(ref budget)`, `Cpu.CycleCount` is the clock.
3. **The scheduler's time source is bound to that one CPU** (`Machine.cs:46`:
   `_scheduler.BindTimeSource(() => Cpu.CycleCount)`), and the IRQ/NMI lines bind to `Cpu.SetIrqLine`/`SetNmiLine`
   (`:44–45`).
4. **`MachineBuilder` holds one `_cpuFactory`** (`MachineBuilder.cs:10`, `WithCpu` `:23`), and `BoardSpec` names one
   `CpuKind` (`BoardSpec.cs:16`); `BoardMachineFactory.Build` calls `WithCpu(CpuCoreFactory.ForKind(spec.Cpu, …))` once
   (`BoardMachineFactory.cs:54`).

The translation seam has a clean substrate already: **`AddressSpace.Read8/Write8` are the bus the CPU is constructed
over** (`CpuCoreFactory.cs:30` passes `ctx.Space(programSpace)` to each core's ctor), and the Z80 core takes its bus as an
`IAddressSpace` (`Z80Cpu(bus, ioBus)`). So a **translating `IAddressSpace` wrapper** that maps Z80 logical → 6502
physical and forwards to the real `AddressSpace` is the natural place for the translation — no change to the Z80 core, no
change to `AddressSpace` itself.

The JIT is the wrinkle: `CpuCoreFactory.BuildJit` casts the bus to the concrete `AddressSpace` because the JIT's fastmem
binds to its page table + backing arrays (`CpuCoreFactory.cs:46`). A *translating wrapper* is not the concrete
`AddressSpace`, so the Z80-under-translation does not trivially get fastmem. This is the core JIT-tier decision
(Decision 4).

---

## 2. Decisions

### Decision 1 — Bus arbitration model: run-one-then-the-other (NOT cycle-interleave); a `SoftCardScheduler` runs the **active** CPU and never schedules the dormant one

The board models the two CPUs exactly as the research recommends (§7, MAME-grounded): **run-one-then-the-other bus
arbitration** — only one CPU drives shared RAM at a time, switched by the `$CnXX` write. **Do NOT cycle-interleave.**
Cleaner than MAME (whose dormant Z80 spins in WAIT): **simply do not schedule the disabled CPU at all.**

This requires a **dual-CPU `Machine` variant** (Decision 2). The new run loop:

- At reset, the **6502 is active**, the **Z80 is dormant** (held disabled). A `bool _z80Active` (the soft-switch state)
  selects which core `Run` drives.
- `Run(cycles)` drives the **active** core's `Run(ref budget)`, slicing to the next scheduled event exactly as the
  single-CPU loop does (`Machine.cs:77`), **but the slice also breaks on the control-transfer soft-switch write** — when
  the active CPU writes the `$CnXX`/`$EN00` toggle, the active-CPU flips and the run loop switches which core it drives
  on the next slice. (The toggle is observed by a peripheral on the MMIO path, Decision 3, which sets `_z80Active` and
  signals the loop to end the current slice.)
- **The scheduler's time source switches with the active CPU.** This is the subtle part: `BindTimeSource` is bound once
  to `Cpu.CycleCount` today (`Machine.cs:46`). For dual-CPU, the time source is `() => ActiveCpu.CycleCount` — but the
  two cores run at **different rates** (Z80 ~2.04 MHz vs 6502 ~1.02 MHz) and each has its **own** monotonic
  `CycleCount`. Decision 5 resolves the unified-clock question; for arbitration, the key invariant is: **scheduled
  device events (the 60 Hz frame tick, the Disk II motor-off delay) are 6502-domain events** (the devices live on the
  6502's bus and the 6502 owns interrupts), so the scheduler runs in the **6502 cycle domain**, and the Z80's run time is
  *converted* into 6502-equivalent cycles when the Z80 is active so device events still fire on wall-clock schedule
  (Decision 5).

**Rationale.** Run-one-then-the-other is the real hardware (single bus master via DMA halt) and the MAME-validated model;
cycle-interleave would be both wrong (the buses are never simultaneously active) and far slower (it would defeat block
chaining on both cores). Not scheduling the dormant core is strictly better than MAME's spin-in-WAIT — the dormant core
burns zero host cycles. The substrate (`Machine.Run`'s slice-to-next-event loop) is reused; the new logic is "which core
+ break on the toggle write."

**Alternatives considered.**
- **(A) Cycle-interleave the two cores.** *Rejected* — not the hardware (DMA halt = single bus master), and it would
  shorten every block on both cores to one cycle, erasing the JIT.
- **(B) Two independent `Machine`s sharing a backing array.** *Rejected* — they must share *one* scheduler (device events
  are global), one interrupt domain (all to the 6502), and one notion of "who is active"; two `Machine`s would duplicate
  and desynchronize all three. One dual-CPU `Machine` owning both cores is the coherent unit.
- **(C) Model the Z80 as a "peripheral" the 6502 hands off to.** *Rejected* — the Z80 is a full CPU running guest code
  out of shared RAM through a translation, not a device with registers; forcing it into `IPeripheral` would be a
  fiction that breaks the moment it executes (it needs the run loop, the block cache, the fastmem).

**Consequences.** *Good:* matches hardware + MAME; reuses the run-loop substrate; dormant core costs nothing.
*Bad/accepted:* a dual-CPU `Machine` variant is a real new type (Decision 2) — the largest framework addition of the arc.

### Decision 2 — Express two CPUs in the model: `BoardSpec` gains an optional **coprocessor** declaration; `Machine`/`MachineBuilder` gain a dual-CPU construction path; the single-CPU path is byte-for-byte unchanged

The model extends to express "a primary CPU plus an optional bus-arbitrated coprocessor with a per-CPU address
translation." Concretely, the additive surface (shapes, not impl):

**`BoardSpec` (additive, optional — single-CPU boards set it to `None`):**

```csharp
// src/CpuEmulator.Machines/  (additive)
/// <summary>An optional second CPU that shares the primary's program RAM under run-one-then-the-other
/// bus arbitration (the Z80 SoftCard). The coprocessor sees the shared bus THROUGH AddressTranslation;
/// it is dormant at reset and activated by a soft-switch write the ControlPort peripheral observes.</summary>
public sealed record CoprocessorSpec(
    CpuKind Cpu,                          // CpuKind.Z80 for the SoftCard
    IAddressTranslation Translation,      // Z80 logical -> primary physical (Decision 3)
    string ControlPortPeripheral,         // the PeripheralSlot.Name whose write toggles active-CPU (Decision 3)
    double ClockRatioToPrimary);          // ~2.0 for the SoftCard Z80 (Decision 5)

// BoardSpec gains one optional field (default null = every existing board, unchanged):
public sealed record BoardSpec(
    /* …existing… */,
    CoprocessorSpec? Coprocessor = null);
```

**`IAddressTranslation` (new, in `CpuEmulator.Core` — it wraps an `IAddressSpace`):**

```csharp
namespace CpuEmulator.Core;
/// <summary>Maps a coprocessor's LOGICAL address to the primary CPU's PHYSICAL address on the shared
/// bus. Page-granular (4 KiB for the SoftCard). The dual-CPU Machine wraps the primary program
/// AddressSpace in a TranslatingAddressSpace built from this, and constructs the coprocessor core over
/// that wrapper — so the coprocessor core is UNCHANGED (it sees an ordinary IAddressSpace).</summary>
public interface IAddressTranslation
{
    uint ToPhysical(uint logical);   // e.g. Z80 $0000 -> 6502 $1000 (Decision 3 table)
}
```

**`Machine`/`MachineBuilder` (additive dual-CPU path):** `MachineBuilder` gains
`WithCoprocessor(Func<IMachineContext, ICpuCore> factory, IAddressTranslation translation, double clockRatio)`. When set,
`Machine`'s constructor builds **both** cores in phase 2 (the primary over the real program space, the coprocessor over a
`TranslatingAddressSpace` wrapping it), tracks `_z80Active`, and uses the dual-CPU `Run` (Decision 1). When unset,
**construction and `Run` are byte-for-byte the current single-CPU path** — the existing `Cpu`/`Run`/`BindTimeSource`
behavior is preserved exactly (the no-behavior-change gate every prior board piece honored).

The translating wrapper:

```csharp
namespace CpuEmulator.Core;
/// <summary>An IAddressSpace the coprocessor is constructed over: every access is translated
/// (IAddressTranslation) then forwarded to the primary program AddressSpace. Read8/Write8/Read16/...
/// all route through ToPhysical. The coprocessor core sees an ordinary 16-bit IAddressSpace.</summary>
public sealed class TranslatingAddressSpace : IAddressSpace { /* wraps AddressSpace + IAddressTranslation */ }
```

**Rationale.** Making the coprocessor *optional* on `BoardSpec` (default `null`) means **every existing board is
untouched** and the dual-CPU path is purely additive — the same "additive, no separate path for the common case"
discipline ADR 0009/0010 and every board piece followed. A `TranslatingAddressSpace` wrapper is the minimal seam: it
needs **no change to the Z80 core** (it consumes an `IAddressSpace`) and **no change to `AddressSpace`** (the wrapper
forwards). The translation is data (an `IAddressTranslation` the board supplies), keeping the *what-maps-where* declarative
while the *wrapping/forwarding* is shared framework code.

**Alternatives considered.**
- **(A) Bake the translation into a new Z80 core variant.** *Rejected* — it forks the parity-gated Z80 core for one
  board; the translation is a *bus* concern, not an ISA concern. A bus wrapper keeps the one Z80 core.
- **(B) A general N-CPU `Machine` (a list of cores).** *Rejected as overreach* — only one coprocessor exists in the
  foreseeable arc (the SoftCard); a general N-CPU scheduler is speculative generality (YAGNI). The optional single
  `CoprocessorSpec` covers the real case and can generalize later if a second multi-CPU machine appears.
- **(C) Translation inside `AddressSpace` (a per-access translation mode).** *Rejected* — it taxes every access on every
  board with a translation branch, and `AddressSpace` is the JIT's fastmem substrate (it must stay the plain page table).
  The wrapper isolates translation to the coprocessor's bus.

**Consequences.** *Good:* single-CPU boards unchanged; the Z80 core unchanged; translation is declarative data; the
dual-CPU path is one optional field + one builder method + one constructor branch. *Bad/accepted:* a dual-CPU `Machine`
construction + run path is genuine new surface (the arc's biggest), and `TranslatingAddressSpace` adds an indirection on
the Z80's bus (its JIT-fastmem consequence is Decision 4).

### Decision 3 — The translation table + the control port: implement the **6-branch MAME-verified** map exactly; a `SoftCardControlPort` peripheral observes the toggle write

**Implement the address translation from the enumerated 6-branch table (§2) — NOT the refuted "+$1000 mod 64K"
shortcut** (§2 marks it refuted 1-2; it is correct only for the low region). The `IAddressTranslation` for the SoftCard
(`SoftCardTranslation : IAddressTranslation`) encodes exactly:

| Z80 logical | → Apple physical | Mapping (§2) |
|---|---|---|
| `$0000–$AFFF` | `+$1000` (→ `$1000–$BFFF`) | true additive offset (CP/M zero page/TPA on usable RAM) |
| `$B000–$BFFF` | `(off&$FFF)+$D000` | Language Card **bank 2** |
| `$C000–$CFFF` | `(off&$FFF)+$E000` | |
| `$D000–$DFFF` | `(off&$FFF)+$F000` | ROM / LC `$F000–$FFFF` |
| `$E000–$EFFF` | `(off&$FFF)+$C000` | 6502 **I/O space** (incl. Disk II controller for the BIOS) |
| `$F000–$FFFF` | `off&$FFF` (→ `$0000–$0FFF`) | 6502 zero page, stack, Apple screen, CP/M RWTS |

Branches 2–6 mask `&$FFF` then add a 4 KiB-window base (page-wrap); only branch 1 is additive. **Granularity is 4 KiB
pages** — so `ToPhysical` is a 6-way branch on the top nibble of the logical address. **A regression test asserts all 6
branches at boundaries** (e.g. `$AFFF→$BFFF`, `$B000→$D000`, `$EFFF→$CFFF`, `$F000→$0000`) — the refuted shortcut would
pass branch 1 and fail the rest, so the test is the un-fakeable guard against re-introducing it.

Two important consequences of the table:

- **The Z80 reaches the Apple I/O space** (`$E000–$EFFF` → `$C000–$CFFF`), so a Z80 access to `$EN00` lands on the same
  `$CN00` control port the 6502 toggles — which is exactly how the Z80 returns control (§1). The control port is one
  peripheral seen from both sides through the translation.
- **The Z80 reaches the Language Card region** (`$B000–$BFFF` → `$D000` LC bank 2; `$D000–$DFFF` → `$F000`), so CP/M's
  use of high RAM depends on the LC being banked appropriately. The LC mapper (ADR 0014 Decision 4) and the translation
  interact: the **6502 boot loader sets up the LC banking before starting the Z80**; the translation then routes the
  Z80's `$B000`/`$D000` onto whatever the LC has mapped. This is a **build-time sequencing item** (the exact LC state
  CP/M expects), defaulted in Decision 7.

**The control port** is a small `SoftCardControlPort : IPeripheral` mapped at the slot's `$CN00` page (it is the slot-N
ROM/register window; for the standard slot it is one page in the `$C100–$C7FF` band, ADR 0014 Decision 5). Its `Write`
(any access, per the §1 caveat — model as write, fire on any access) **toggles `_z80Active` on the dual-CPU `Machine`**
and signals the run loop to end the current slice (Decision 1). It is named by `CoprocessorSpec.ControlPortPeripheral` so
the dual-CPU `Machine` knows which peripheral drives the toggle. The DIP-switch S1-1 (translation disable, §2) is a
construction-time config bit on `SoftCardTranslation` (identity translation when disabled) — a board param, defaulted on.

**Rationale.** The table is the hardware (MAME-verified, gap-fill-nailed); encoding it literally as a 6-way branch is
both correct and trivially fast (a nibble switch). Making the control port a peripheral that flips a `Machine` flag keeps
the toggle on the proven MMIO path (a control-register write, ADR 0009 Decision 1) at the exact cycle the guest writes
it. The translation-disable DIP as a construction param matches the real card's config bit.

**Alternatives considered.**
- **(A) The "+$1000 mod 64K" shortcut.** *Rejected — explicitly refuted (§2).* Correct only for branch 1.
- **(B) A 16-entry (4 KiB) lookup array instead of a 6-way branch.** *Acceptable* and arguably cleaner (a `uint[16]` of
  window bases + a per-window additive-vs-masked flag); either is fine. Recommend the explicit branch for readability
  and because branch 1 is additive while 2–6 are masked (a uniform table needs a per-entry mode flag anyway).

**Consequences.** *Good:* the translation is correct and fast; the control port reuses the MMIO path; one port serves
both CPUs through the table. *Bad/accepted:* the LC↔translation interaction is a sequencing subtlety (build-time item).

### Decision 4 — The JIT tier under translation: ship the Z80-under-SoftCard on the **interpreter tier first**; JIT-via-physical-page-fastmem is a follow-on optimization

The JIT binds fastmem to the **concrete** `AddressSpace` page table + backing arrays (`CpuCoreFactory.cs:46`,
`Fastmem.cs`). A `TranslatingAddressSpace` wrapper is **not** the concrete `AddressSpace`, so the Z80-under-translation
does not get fastmem for free. There are two viable paths; this ADR chooses the staged one:

- **Ship the SoftCard Z80 on the interpreter tier first.** The interpreter consumes any `IAddressSpace`
  (`Z80Cpu(bus, ioBus)`), so the `TranslatingAddressSpace` wrapper works **immediately, unchanged** on the interpreter —
  the SoftCard boots CP/M on the interpreter with zero JIT work. This is consistent with the project's interpreter-as-
  oracle principle (the interpreter is always correct; the JIT is a perf dial) and the partial-emit philosophy (CPUs ship
  interpreter-correct, then earn JIT emit by profile).
- **JIT under translation is a follow-on optimization** with a clear shape: because the translation is **4 KiB-page
  granular and static while the Z80 runs**, the JIT's fastmem for the Z80 can be built over the **physical** backing
  arrays with the Z80's page table pre-translated — i.e. the Z80's `Fastmem.PageBacking[z80page]` points at the *physical*
  backing array the translation maps that Z80 page to (a one-time build over the 6 windows, since the translation is
  static per active-session). A Z80 store to a translated RAM page then hits the physical backing directly at full tier-1
  speed, and the existing per-page SMC + the LC `Remap` invalidation (ADR 0014 Decision 4) handle the `$D000` window's
  ROM/RAM swaps. **This is a real, tractable JIT design — but it is an optimization, not a blocker**, and it should be
  reverse-engineered against the *running interpreter* SoftCard (the oracle) rather than designed speculatively.

**Rationale.** The interpreter path is *free* (the wrapper just works) and *correct* (the oracle), so CP/M can boot and
be gated end-to-end without touching the JIT — exactly how every CPU shipped (interpreter-correct first, JIT by profile).
The JIT-under-translation design is sound (static 4 KiB translation → pre-translated physical fastmem) but is the kind of
optimization ADR 0011 ranks by ROI; it should be measured (is CP/M throughput on the interpreter even a problem? CP/M is
a 1981 OS) before building. Staging it keeps the arc unblocked and avoids designing a JIT-fastmem-over-translation
mechanism against an imaginary workload.

**Alternatives considered.**
- **(A) Block CP/M until the Z80 JITs under translation.** *Rejected* — it blocks the deliverable on an optimization for
  a workload (CP/M) that may not need it; violates interpreter-first.
- **(B) Translate inside the JIT's emitted store/load (per-access translation in IL).** *Rejected as the default* — it
  adds a translation step to every emitted Z80 access (the opposite of fastmem's "bake the backing ref, skip the
  lookup"). The pre-translated-physical-fastmem approach (above) does the translation **once at fastmem build**, not per
  access — strictly better, and the recommended shape *if/when* the JIT path is built.

**Consequences.** *Good:* CP/M boots + gates on the interpreter with no JIT work; the JIT path has a clear, correct
design when profiling justifies it. *Bad/accepted:* the SoftCard Z80 runs interpreter-speed until the JIT follow-on
lands (acceptable — it is correct, and CP/M is light); the dual-CPU `Run` loop must handle the active core being either
tier (the 6502 may be JIT, the Z80 interpreter — both are `ICpuCore`, so `Run(ref budget)` is uniform; no special case).

### Decision 5 — One scheduler in the 6502 cycle domain; the Z80's run time converts via `ClockRatioToPrimary`; **all interrupts to the 6502**

The two cores run at different rates and each has its own monotonic `CycleCount`. The scheduler (device events: the
~60 Hz frame tick, the Disk II motor-off one-shot) must fire on **wall-clock** schedule regardless of which CPU is
active. Decision:

- **The scheduler runs in the 6502 cycle domain** (the devices live on the 6502's bus; the 6502 owns interrupts). The
  scheduler's time source is the 6502's cycle count **plus** the Z80's run time converted to 6502-equivalent cycles:
  while the Z80 is active for *N* Z80 cycles, the scheduler advances by `N / ClockRatioToPrimary` ≈ `N / 2.04`
  6502-equivalent cycles. The dual-CPU `Machine` maintains a single monotonic **virtual 6502-domain clock** =
  `primary.CycleCount + round(z80CyclesRun / ratio)`, and binds `BindTimeSource` to *that* (replacing the single-CPU
  `() => Cpu.CycleCount`). So a frame tick scheduled every ~17030 6502 cycles fires at the right wall-clock moment even
  across stretches where the Z80 was the one running.
- **All interrupts are routed to the 6502** (§1). The `IrqLine`/`NmiLine` bind to `primary.SetIrqLine`/`SetNmiLine`
  **only** (never the Z80). When the Z80 is active and a scheduled device raises an IRQ, the dual-CPU `Run` loop **ends
  the Z80 slice and resumes the 6502** to service it — which is the hardware truth (an interrupt implies the 6502 must
  run; on real hardware the card uses the Z80 REFRESH line to grant the 6502 brief windows, §1, which we approximate by
  switching to the 6502 on a pending interrupt). **The Z80 core's interrupt inputs are left unbound** (the SoftCard Z80
  is never interrupted directly).
- **REFRESH-window 6502 wakeups (§1, research open item 3) are NOT modeled initially** — the simpler single-bus-master
  model (only the active CPU runs; interrupts force a switch to the 6502) is the default. MAME does not model the refresh
  wakeups either; whether any CP/M software depends on them is unknown. Recorded as a build-time fidelity item with the
  default = don't model them.

**Rationale.** A single 6502-domain clock keeps device timing correct across CPU switches with one conversion factor (the
clock ratio), and matches the hardware reality (the devices and interrupts are 6502-side). Routing all interrupts to the
6502 is not a modeling choice — it is the hardware (§1) — and "switch to the 6502 on a pending interrupt" is the faithful
consequence of the 6502 being DMA-suspended while the Z80 runs. Deferring REFRESH wakeups matches MAME and keeps the
first cut simple; it is a fidelity dial, not a correctness gap for the documented protocol.

**Alternatives considered.**
- **(A) Two independent scheduler clocks.** *Rejected* — device events are global (a frame tick is a frame tick); two
  clocks desynchronize them across CPU switches.
- **(B) Schedule in the active CPU's domain, converting on switch.** *Rejected as more error-prone* — it re-bases the
  scheduler's outstanding events on every switch (frequent, on every CP/M disk call); one fixed 6502-domain clock with a
  conversion on the Z80's contribution is simpler and switch-count-independent.
- **(C) Route interrupts to whichever CPU is active.** *Rejected — contradicts the hardware* (§1: all to the 6502; the
  Z80 is uninterruptible here).

**Consequences.** *Good:* one coherent clock; device timing correct across switches; interrupt routing matches hardware.
*Bad/accepted:* the clock-ratio conversion introduces a small rounding (sub-cycle) per Z80 stretch — invisible to CP/M
(a coarse-timed workload); the REFRESH-wakeup omission is a documented fidelity dial.

### Decision 6 — `BoardMachineFactory` + `CpuCoreFactory` extensions: build the coprocessor over the translating wrapper; keep `CpuCoreFactory` the one place that names cores

`BoardMachineFactory.Build` (today: validate → patch vectors → builder → `WithCpu(CpuCoreFactory.ForKind(spec.Cpu, …))`
→ `Build`) extends: **if `spec.Coprocessor is not null`**, after `WithCpu` for the primary, call
`WithCoprocessor(CpuCoreFactory.ForKind(copro.Cpu, …), copro.Translation, copro.ClockRatioToPrimary)`. The coprocessor's
core factory is resolved through the **same `CpuCoreFactory`** (the one place allowed to name concrete cores + the JIT,
keeping `Core` AOT-clean — `CpuCoreFactory.cs:10`), but the dual-CPU `Machine` constructs it over the
`TranslatingAddressSpace` instead of the raw program space. `BoardSpecValidator` gains coprocessor checks (Decision 7).

**Rationale.** Reuses the single `CpuCoreFactory` indirection (the AOT-clean seam) for both cores; the only new wiring is
"build the second core over the wrapper." Keeps `BoardMachineFactory` the compile-spec-to-builder front-end it already is.

**Consequences.** *Good:* one core-factory seam for both CPUs; the factory change is small + additive. *Bad/accepted:*
`CpuCoreFactory` must build a core over a *wrapper* `IAddressSpace`, which on the JIT tier is the Decision-4 follow-on
(the interpreter path takes any `IAddressSpace` already).

### Decision 7 — Validation + residual sequencing items: dual-CPU `BoardSpecValidator` checks; defaults for the CP/M load map and LC pre-state

**`BoardSpecValidator` gains coprocessor checks** (additive, in the spirit of its existing diagnostics): a
`CoprocessorSpec` with a `ControlPortPeripheral` naming a real `PeripheralSlot` (analogue of `irq-unwired`), a
`ClockRatioToPrimary > 0`, a non-null `Translation`, and a `Cpu` the `CpuCoreFactory` can build. New diagnostic ids:
`copro-control-port-unwired`, `copro-bad-clock-ratio`, `copro-no-translation`.

**Residual sequencing items (build-time, non-blocking, defaulted per the directive):**

| Residual item (research §) | Recommended default | Closed at |
|---|---|---|
| Exact CP/M CCP/BDOS/BIOS load addresses + Z80 entry point (§ res-1) | Drive from the **fetched CP/M disk's** boot loader — the 6502 `$C600` boot reads tracks `$00–$02` and issues the `$CnXX` start (§ res-1); let the real boot loader place CP/M, don't hardcode addresses | Planner/Builder wiring the boot |
| The 6502 `$C600`→tracks-`$00–$02`→`$CnXX`-start boot path (§ res-1) | Run the **real** SoftCard boot sequence (the 6502 boots the disk, sets LC banking, writes `$CN00`) | Build-time, against the fetched disk |
| LC pre-state the Z80 translation expects (Decision 3) | The SoftCard boot loader sets it; replay whatever it does (don't pre-assume a bank) | Build-time |
| REFRESH-window 6502 wakeups (§ res-3 / Decision 5) | Not modeled (MAME parity) | Escalate only if a title needs it |
| Z80→Apple clock ratio exactness (§3) | `2.0` (≈2×), refine to the bus-synchronized effective rate if a timing gate needs it | Build-time |
| AppleWin SoftCard status (§ res-2) | Ignore (MAME is the model) | — |

**Rationale.** The *mechanism* is fully decided (arbitration, translation, scheduler, interrupts); the *exact CP/M load
map* is data the real boot loader produces — so the right default is "run the real boot, don't hardcode," which the
fetch-on-demand CP/M disk (ADR 0016) makes possible. This honors the directive (note residual items with a recommended
default; don't block the ADR).

**Consequences.** *Good:* validation extends naturally; the boot map is data, not a guess. *Bad/accepted:* the boot path
can't be fully gated until the CP/M disk asset is fetchable (ADR 0016) — sequencing dependency, noted in the PR plan.

---

## 3. Consequences (cross-cutting)

**Good.**
- The dual-CPU board is expressed as an **optional, additive** extension: one `CoprocessorSpec?` field, one
  `WithCoprocessor` builder method, one dual-CPU `Machine` construction/run branch, one `TranslatingAddressSpace` wrapper,
  one `IAddressTranslation`. **Every existing single-CPU board is byte-for-byte unchanged.**
- The Z80 core is **unchanged** (it consumes the translating wrapper as an ordinary `IAddressSpace`); the parity-gated
  core is reused, not forked.
- CP/M boots + gates on the **interpreter tier with zero JIT work** (Decision 4), honoring interpreter-first; the JIT
  path has a correct, tractable design for when profiling justifies it.
- The translation table is the **MAME-verified 6-branch** map with an un-fakeable boundary test that guards against the
  refuted shortcut.

**Bad / accepted costs.**
- A dual-CPU `Machine` construction + run path is the arc's largest framework addition — genuine new surface (mitigated:
  optional, additive, single-CPU path untouched).
- `TranslatingAddressSpace` adds an indirection on the Z80's bus; full JIT-tier speed for the SoftCard Z80 is a follow-on
  (Decision 4).
- The unified 6502-domain clock introduces a sub-cycle rounding per Z80 stretch (invisible to CP/M).
- The exact CP/M load map + LC pre-state are build-time items closed against the real (fetched) disk, so end-to-end CP/M
  boot gating depends on ADR 0016's asset fetch.

**Reversibility.** High at the model level (the coprocessor is an optional field; remove it and the board is single-CPU).
The dual-CPU `Run` loop is the one piece with teeth (it changes how `Machine.Run` drives cores), but it is gated behind
`Coprocessor is not null` — single-CPU `Run` is the unchanged existing code path.

---

## 4. Open questions

1. **`Remap` placement (inherited from ADR 0014 Decision 4 / ADR 0009 OQ4).** The translation's interaction with the LC
   means the Z80's `$B000`/`$D000` view depends on LC state; this is read-only from the Z80's side (it doesn't remap),
   but confirms `Remap` must be reachable. Settled with ADR 0014 Decision 4.
2. **JIT-under-translation ROI (Decision 4).** Is CP/M throughput on the interpreter actually a problem worth the
   pre-translated-physical-fastmem build? Measure the interpreter SoftCard first; build the JIT path only if a target
   workload is slow. Resolve by profiling, post-interpreter-boot.
3. **Clock-ratio exactness (Decision 5 / §3).** Is `2.0` close enough, or does a CP/M timing-dependent program need the
   bus-synchronized effective rate (below 2×, §3)? Default `2.0`; refine against a timing-sensitive CP/M title if one
   surfaces.
4. **REFRESH-window 6502 wakeups (Decision 5 / §res-3).** Does any CP/M software depend on the 6502 getting brief
   execution windows while the Z80 runs? Default no (MAME parity); escalate only on a concrete failure.
5. **Active-CPU switch granularity (Decision 1).** The toggle ends the current slice; confirm the run loop switches
   cleanly when the toggle write is mid-block (the writing instruction completes, then the switch takes effect on the
   next dispatch — the control port's `Write` sets the flag; the loop checks it at the slice boundary). Verify against the
   real boot's `$CN00` write at implementation time.

---

*End of ADR 0015 — the load-bearing decision of the arc. The Z80 SoftCard is a **dual-CPU shared-RAM board**: an optional
`CoprocessorSpec` on `BoardSpec` (Z80 + an `IAddressTranslation` + the control-port name + the ~2× clock ratio); a
dual-CPU `Machine`/`MachineBuilder` path (additive — single-CPU boards byte-for-byte unchanged) that builds the Z80 over
a `TranslatingAddressSpace` wrapping the shared 6502 program space, so the **parity-gated Z80 core is unchanged**.
Arbitration is **run-one-then-the-other** (Decision 1): a `SoftCardControlPort` peripheral observes the `$CnXX`/`$EN00`
toggle write and flips `_z80Active`; the run loop drives only the **active** core and never schedules the dormant one
(cleaner than MAME's spin-in-WAIT). The translation is the **MAME-verified 6-branch table** (Decision 3 — NOT the refuted
`+$1000` shortcut), guarded by a boundary regression test. The JIT ships **interpreter-first** (Decision 4: the wrapper
works unchanged on the interpreter; pre-translated-physical-fastmem is the follow-on optimization). One scheduler runs in
the **6502 cycle domain** with the Z80's time converted by the clock ratio, and **all interrupts route to the 6502**
(Decision 5 — the hardware truth). `BoardMachineFactory`/`CpuCoreFactory` build the coprocessor through the same AOT-clean
core-factory seam (Decision 6); `BoardSpecValidator` gains coprocessor checks (Decision 7). The exact CP/M load map + LC
pre-state are build-time items closed against the **fetched** CP/M disk (ADR 0016). Designer: the only surface implication
is a "which CPU is running" indicator is *optional* (CP/M just looks like a terminal — the Videx 80-col display, ADR
0016, is the real CP/M surface). Planner: this is the arc's biggest piece — see the sibling report's PR decomposition,
which sequences the dual-CPU Machine scaffolding, the translation, the control port, and the interpreter-tier CP/M boot
before any JIT-under-translation work.*
