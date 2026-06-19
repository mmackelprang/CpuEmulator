# Design — The Machine Model (the "CPUs → computers" foundation)

> **Status:** Approved design (brainstormed with the owner, 2026-06-19). Ready for an implementation plan.
> **Date:** 2026-06-19
> **Topic:** A reusable, CPU-agnostic abstraction for "an emulated computer system" — a declarative
> **board-spec** (memory map + peripherals + interrupt wiring) that instantiates into a runnable machine,
> plus lifting the existing 6502 SBC's peripherals into it so they are reusable across CPUs.

## 1. Context & where this fits

The framework today emulates four **bare CPU cores** (6502, Z80, 68000, 8086) over a generated spec, a
two-tier interpreter/JIT, and a device layer (`Machine`, `IAddressSpace`, `IPeripheral`, a device
scheduler, interrupt lines, `SimpleUart`, `IntervalTimer`). The only wired-up *computer* is the
`Breadboard6502`, which the monitor host boots.

This spec is **piece #1 of a larger "CPUs → computers" arc** (the move to a *library of emulated computer
systems*). The arc's pieces:

1. **The Machine model (this spec)** — the reusable board abstraction + the 6502 peripheral refactor.
2. **An SBC per CPU** — small uniform reference boards (6502 ✓, Z80, 68000, 8086).
3. **Monitor hosts** — the CPU-agnostic monitor/REPL booting each board.
4. **Per-bank `(PC, bankState)` JIT specialization** — a parallel, independent JIT track (separate ADR).

This spec covers **only piece #1**. Pieces #2/#3 build on it; #4 is independent.

## 2. Goal & success criteria

A single declarative **board-spec** type expresses an emulated computer, is validated, and instantiates
into a runnable `Machine`. Success for this spec:

- The `Breadboard6502` is **re-expressed as a `BoardSpec`** and runs **byte-for-byte / cycle-for-cycle
  identically** to today (the un-fakeable gate).
- A **Z80 reference board** is expressed as a `BoardSpec` and runs — **proving the model generalizes**
  across CPUs from one shared recipe.
- `SimpleUart` / `IntervalTimer` become **reusable, board-attachable components** decoupled from the
  6502 wiring.

## 3. The board-spec model

A board is **data + build-time/load diagnostics**, mirroring the CPU-spec philosophy (not codegen — a
board is an instance to assemble, not code to generate).

```csharp
public sealed record BoardSpec(
    string Name,                               // "Breadboard6502", "ReferenceSbc-Z80"
    CpuKind Cpu,                               // which core to instantiate
    IReadOnlyList<MemoryRegion> Memory,        // RAM / ROM / MMIO over the address space
    IReadOnlyList<PeripheralSlot> Peripherals, // device + where it attaches (memory-mapped)
    IrqWiring Irq,                             // which device IRQ line drives which CPU interrupt
    ResetConfig Reset);                        // ROM image + reset vector

public enum RegionKind { Ram, Rom, Mmio }
public sealed record MemoryRegion(uint Start, uint Length, RegionKind Kind, byte[]? Image = null);
public sealed record PeripheralSlot(string Name, IPeripheral Device, uint Base, uint Length);
public sealed record IrqWiring(/* device-line -> CPU-interrupt mappings */);
public sealed record ResetConfig(/* ROM image source + reset-vector handling */);
```

**Validation** (`BoardSpecValidator`, run at load; optionally surfaced as a build-time analyzer to stay
on-brand): regions do not overlap and fit the CPU's address width; peripheral slots land in/over a
declared `Mmio` region; every declared IRQ line is wired to a real CPU interrupt; the ROM image fits its
region; the reset vector lands in mapped memory. Validation failures are diagnostics, not exceptions
buried at runtime.

**Instantiation** (`MachineBuilder.Build(BoardSpec) -> Machine`): instantiate the CPU core; build an
`IAddressSpace` with the regions mapped (RAM backing, ROM backing from the image, MMIO routed to the
peripheral bus-callout — reusing the existing fastmem RAM/MMIO split); attach each peripheral at its
slot; wire IRQ lines into the CPU's interrupt input(s); apply reset. The result is the existing runnable
`Machine` (it keeps the device scheduler + interrupt plumbing — this refactors *how a machine is
described*, not the run loop).

**Introspection:** a `BoardSpec` is inspectable (the monitor host — piece #3 — renders the memory map +
peripheral list from it).

## 4. The peripheral model & refactor

`IPeripheral` becomes **board-attachable and CPU-agnostic**: a device exposes a memory-mapped register
window (read/write at an offset within its slot) and an optional outgoing IRQ line into the board's
`IrqWiring`. The existing device **scheduler** and **interrupt-line** plumbing are reused.

- `SimpleUart` and `IntervalTimer` are **lifted out of the `Breadboard6502`-specific wiring** into
  reusable components a `BoardSpec` slots in by address. Their behavior is unchanged — only their
  attachment is generalized (no hard-coded 6502 addresses or 6502 IRQ wiring inside the device).
- The refactor is **behavior-preserving**: the 6502 board-spec must reproduce the exact same UART byte
  stream, timer cadence, and IRQ timing as today's `Breadboard6502`.

## 5. The uniform reference recipe

`ReferenceSbc(CpuKind cpu) -> BoardSpec` — a shared convention instantiated per CPU:

- **RAM** in the low address range; **ROM** in the high range (carrying the CPU's reset vector).
- A **memory-mapped UART** + an **interval timer** at fixed MMIO addresses (the same convention for all
  CPUs, adjusted only for each CPU's address width / reset-vector mechanics).
- The timer/UART **IRQ wired to the CPU's maskable interrupt**.

One recipe, four CPUs. This is the vehicle that *drives* the model — the abstraction is "done" when one
recipe serves four genuinely different CPUs.

## 6. I/O model decision

The reference board attaches peripherals **memory-mapped on all four CPUs** — uniform, works everywhere
(6502/68000 have no separate I/O space; Z80/8086 support memory-mapped I/O fine). The **Z80/8086
I/O-port space is a deliberate later extension** (for idiomatic / faithful-replica boards), modeled by a
`PeripheralSlot` attachment kind (`Memory` vs `Port`) when needed. Out of scope here.

## 7. Scope of this spec (piece #1)

**In:**
- `BoardSpec` + `MemoryRegion`/`PeripheralSlot`/`IrqWiring`/`ResetConfig` types.
- `BoardSpecValidator` (+ tests).
- `MachineBuilder.Build(BoardSpec)` factory.
- The `IPeripheral` generalization + `SimpleUart`/`IntervalTimer` refactor into reusable components.
- **Board #1:** `Breadboard6502` re-expressed as a `BoardSpec` (zero-behavior-change gate).
- **Board #2:** a Z80 reference board (`ReferenceSbc(Z80)`) that runs — proving the model generalizes.
- The `ReferenceSbc(CpuKind)` recipe (used for the Z80 board; ready for 68000/8086 in piece #2).

**Out (later pieces / non-goals):**
- 68000 and 8086 reference boards (piece #2 — a fan-out once the model is proven on 6502 + Z80).
- Monitor hosts (piece #3).
- Port-mapped I/O (Z80/8086 I/O space).
- Bank switching / the per-bank JIT specialization (parallel track, separate ADR).
- Anything requiring code generation that a record + validator can't express.

## 8. Validation & testing

- **Board-spec validator tests** — overlap, address-width fit, unwired IRQ, ROM-too-big, reset-vector
  unmapped each produce the right diagnostic.
- **The 6502 zero-behavior-change gate (un-fakeable):** the `Breadboard6502`-via-`BoardSpec` produces a
  byte-identical UART stream, identical timer/IRQ timing, and identical cycle counts to today's
  hand-wired `Breadboard6502`, over the existing 6502 UAT/host sessions. This is the load-bearing proof
  the refactor preserved behavior.
- **Z80 reference-board smoke:** the Z80 board boots its ROM and runs a tiny program to a verifiable
  result (e.g. a known byte sequence out the UART), on both tiers (interpreter + JIT).
- **No regression** to the CPU cores or the existing device/monitor tests.

## 9. Relationship to existing code

This **extends and refactors** the existing `Machine` / `IAddressSpace` / `IPeripheral` / device-layer
code — it does not rebuild the run loop or the scheduler. The `Breadboard6502`'s current hand-wiring is
*replaced by* its `BoardSpec` + the shared `MachineBuilder`. Follow the existing fastmem RAM/MMIO split
(ADR 0009) for region mapping.

## 10. Open questions for the Planner

- **Build-time vs load-time validation:** start with a load-time `BoardSpecValidator` (simplest);
  whether to also surface diagnostics via a Roslyn analyzer (on-brand) is an enhancement, not required
  for piece #1.
- **`CpuKind` → core instantiation:** how the factory maps a `CpuKind` to the concrete CPU core + its
  JIT (a small registry / switch). Likely a thin per-CPU factory the four cores register into.
- **Reset-vector mechanics per CPU:** 6502 (vector at `$FFFC`), Z80 (PC=0), 68000 (SP+PC from `$0`),
  8086 (`CS:IP` from the reset vector) differ — `ResetConfig` must abstract this; the per-CPU detail
  lives in the core, surfaced through a uniform `ResetConfig`.
