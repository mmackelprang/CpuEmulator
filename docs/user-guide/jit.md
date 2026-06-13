# The JIT Tier (Tier 1)

The emulator has two execution tiers that share one source of truth (the generated CPU spec):

- **Tier 0 — the interpreter** (`Mos6502Cpu`): cycle-exact, always available, AOT-compatible.
  It is the correctness oracle and the only tier validated per-cycle against the full TomHarte
  sweep.
- **Tier 1 — the IL-JIT** (`JittedCpu`, in `CpuEmulator.Jit`): a speed path that translates guest
  machine code into .NET IL at runtime and lets RyuJIT compile it to native. It *wraps* the
  interpreter; the interpreter remains the oracle, the fallback, and the owner of all
  architectural state.

This page covers what the JIT is, how to run a machine on it, the accuracy contract that defines
what "parity" means for Tier 1, the M2-i fallback caveat, and troubleshooting.

Source: `src/CpuEmulator.Jit/`

---

## What it is

`JittedCpu` is an `ICpuCore` + `IMonitorSupport` wrapper around a real `Mos6502Cpu`. The wrapped
interpreter owns the architectural state (`A/X/Y/S/P/PC`, the cycle counter); the JIT discovers
straight-line **blocks** of guest code, compiles each block once into a `DynamicMethod`, caches it
keyed by entry PC, and re-executes the cached native code on subsequent visits. Reads and writes to
RAM/ROM use a **fastmem** fast path (direct access to the page's backing array); accesses to
memory-mapped I/O fall back to a bus callout so devices still see them.

Three properties make the tier trustworthy:

- **The interpreter is the oracle.** Any instruction the compiler does not emit (see the fallback
  caveat below) runs as a callout to the interpreter's `Step()`, so correctness never depends on
  the JIT being clever.
- **One spec drives both tiers.** The block compiler walks a generated per-opcode descriptor table
  (`Mos6502Cpu.JitDescriptors`) produced from the same spec the interpreter, disassembler, and
  assembler are generated from — so the JIT's instruction lengths and cycle counts cannot drift
  from the interpreter's.
- **`Step()` always delegates to the interpreter.** Single-instruction stepping (the monitor's
  primitive, the TomHarte harness's primitive) demands exact per-instruction fidelity, which is the
  interpreter's job. Blocks earn their keep in `Run`.

---

## How to enable it on a machine

The JIT is **opt-in**. No shipped machine uses it by default in M2-i — `Breadboard6502` and every
existing board stay interpreter-only. You enable it by having your machine's CPU factory return a
`JittedCpu` wrapping a `Mos6502Cpu`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

var machine = Machine.Create("jit-board")
    .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
    .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
    .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x0100, uart)
    .WithRom(AddressSpaceKind.Program, 0xE000, romImage)
    .WithCpu(ctx =>
    {
        // Fastmem binds to the concrete AddressSpace (the page table + backing arrays).
        // ctx.Space returns IAddressSpace, but its runtime type is the concrete AddressSpace.
        var space = (AddressSpace)ctx.Space(AddressSpaceKind.Program);
        return new JittedCpu(new Mos6502Cpu(space), space);
    })
    .Build();
```

The `JittedCpu` is now the machine's `Cpu`, so `Machine.Run`, the `MonitorEngine`, and the REPL drive
it identically to the interpreter. A documented host flag to select the tier from the command line
is planned for a later milestone (M2-ii+); for now the selection is the `WithCpu` factory.

`JittedCpu` binds to a **concrete `AddressSpace`**, not the `IAddressSpace` interface, because
fastmem needs the page table's backing `byte[]` and writability. Constructing a `JittedCpu` over a
non-`AddressSpace` bus is a construction error.

---

## The accuracy contract vs the interpreter

The JIT is **block-accurate**: it promises the interpreter's final state and total cycle count, not
the interpreter's per-cycle bus-transaction order. The precise contract:

| Dimension | Interpreter (Tier 0) | JIT (Tier 1), fastmem ON (default) | JIT, fastmem OFF (`DisableFastmem`) |
|---|---|---|---|
| Final register/flag state | cycle-exact | **identical** | identical |
| `CycleCount` total | cycle-exact (per bus transaction) | **identical** (same cycle templates + page-cross + interpreter fallbacks) | identical |
| Per-cycle bus trace order | exact (every read/write, dummy reads) | **NOT preserved for fastmem RAM/ROM** — direct array access emits no bus transaction; MMIO accesses DO hit the bus in order | **data accesses preserved** — every operand fetch, memory read/write, and RMW dummy write routes through the bus, in order; opcode-fetch reads are resolved at compile time (see note) |
| Interrupt latency | instruction-boundary | **block-boundary** (checked at block entry; documented) | block-boundary |

**The contract, in one sentence:** *JIT parity is state + cycle-count equivalence; it is NOT
bus-trace equivalence while fastmem is on, because fastmem RAM/ROM access deliberately bypasses the
bus for speed.*

Bus-trace equivalence is available on demand as a **mode**, not a tier promise. Construct the
`JittedCpu` with `JitOptions { DisableFastmem = true }`, which routes every access through the bus
and restores an identical bus trace to the interpreter's:

```csharp
var jit = new JittedCpu(inner, space, new JitOptions { DisableFastmem = true }, traceBus: tracingBus);
```

This is the mode the trace spot tests use to prove the equivalence, and the mode to reach for when
you want a bus trace out of the JIT for debugging.

**One nuance in trace mode (the opcode-fetch reads):** even under `DisableFastmem`, the JIT does
not reproduce the interpreter's *opcode-fetch* bus reads — the instruction's first byte. The JIT
reads the opcode stream once at compile time (block discovery) and bakes it into the emitted IL;
the executing block never re-fetches opcodes at run time. So a `DisableFastmem` trace contains every
operand fetch, every effective-address read/write, and every silicon-true RMW dummy write — matching
the interpreter element-for-element — but omits the per-instruction opcode fetch. This is inherent to
compilation (re-fetching every opcode per execution would defeat the tier) and is the precise reading
of "every access routes through the bus": every *data/operand* access does. The trace spot tests pin
exactly this (the interpreter trace minus its opcode fetches equals the JIT trace).

**Interrupt latency is block-boundary.** The JIT checks for a pending interrupt at each block entry
(serviced by the inner interpreter's authentic 7-cycle sequence). Under tight cycle budgets — the
monitor's single-instruction stepping, or `Machine.Run` chunked to the next scheduled event —
block-boundary collapses to instruction-boundary, so device-driven IRQ timing is unaffected in
practice.

---

## The decimal-mode / interpreter-fallback caveat (M2-i)

In M2-i the block compiler does **not** emit IL for a few opcodes; it emits an interpreter-`Step`
fallback for them instead, and ends the block there:

- **`ADC` / `SBC`** (every addressing mode) — the only opcodes whose semantics fork on a runtime
  flag (the decimal `D` bit), with densely-branchy NMOS nibble-correction logic. Emitting them is
  the M2-ii ambition, once the full parity battery and the differential fuzzer exist to vet the
  emitted decimal arms.
- **`BRK` / `RTI`** — they touch the interrupt/vector machinery the interpreter owns.
- **Undefined opcodes** — not in the dispatch table; the interpreter owns the undefined-opcode policy.

A fallback runs one authentic interpreter `Step` (advancing PC and cycles exactly), so correctness
is identical whether an instruction is emitted or fallen-back. The **performance** consequence is
that ADC/SBC-heavy code (notably the Klaus functional test, which is decimal-inclusive) runs as many
short blocks separated by interpreter steps — provably correct, but not yet fully JIT-accelerated.
M2-ii emits the decimal arms and quantifies the speed-up with benchmarks.

---

## Troubleshooting

- **`PlatformNotSupportedException` at `JittedCpu` construction.** The JIT requires a runtime JIT
  (`RuntimeFeature.IsDynamicCodeSupported`). Under NativeAOT — or any dynamic-code-disabled process —
  that feature is false and the gate throws with guidance to use the interpreter (`Mos6502Cpu`)
  directly. NativeAOT consumers never reference `CpuEmulator.Jit`; the interpreter is the AOT path.
- **A parity divergence (state or cycle count differs from the interpreter).** This is a **JIT bug**
  by definition — the interpreter is the oracle. File it with the program bytes + the diverging
  register/cycle. To capture a per-cycle bus trace for the report, re-run with
  `JitOptions { DisableFastmem = true }` and a `TracingAddressSpace` to get the full ordered access
  list (which the default fastmem path does not produce for RAM/ROM).
- **SMC (self-modifying code) seems stale.** The JIT discards cached blocks when a RAM store dirties
  a page that owns a cached block, and ends the current block when a store patches an opcode ahead of
  PC within that same block. Both modes (fastmem on/off) track writable-RAM stores for invalidation.
  A genuine staleness would be a parity divergence — see above.

---

## The gate: `RuntimeFeature.IsDynamicCodeSupported`

`JittedCpu`'s constructor checks `System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported`
and throws `PlatformNotSupportedException` when it is false. This is the construction-seam gate that
keeps the AOT story enforced by construction: `CpuEmulator.Jit` is the only assembly that uses
`Reflection.Emit`, and it is the only non-AOT member of the build graph. The AOT-clean assemblies
(`Core`, `Cpus.Mos6502`, `Peripherals`, `Monitor`, `Host`) never reference it — a build-time check
(`AotCleanlinessTests`) pins that reference-graph law.
