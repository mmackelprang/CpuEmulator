# CpuEmulator — Framework Design

> **Status:** Approved design (brainstorm output)
> **Date:** 2026-06-11
> **Basis:** `docs/research/emulation-framework-research.md`
> **Next step:** implementation plan via writing-plans

## 1. Purpose and posture

CpuEmulator is a **pluggable, multi-architecture CPU/SBC emulation framework in modern C#** that
uses the .NET runtime's JIT as a dynamic-recompilation backend. Two goals drive the design now,
one shapes it later:

- **The framework is the product** — a reusable .NET library with a clean public API, eventually
  NuGet-shaped. Reference machines exist to prove the abstractions.
- **A learning vehicle** — dynarec techniques, ISA modeling, Roslyn source generation, and IL
  emission are pursued deliberately, not minimized.
- **Later phase:** running real retro systems (working SBC recreations). The design keeps that
  door open (machine composition, cycle-accurate oracle tier) without gold-plating any single
  machine yet.

## 2. Locked decisions

| # | Decision | Choice | Rationale |
|---|---|---|---|
| 1 | Purpose | Framework + learning now; real machines later | Drives API-first design and dual-tier execution |
| 2 | Deployment | **Tiered dual-support.** Interpreter tier is AOT-safe and always present; IL-JIT tier lights up only where `RuntimeFeature.IsDynamicCodeSupported` | NativeAOT has no runtime JIT; a credible library cannot exclude AOT consumers |
| 3 | Accuracy | **Per-tier accuracy.** Interpreter is cycle-accurate (per bus transaction); JIT is instruction/block-accurate with a cycle budget | Cycle-exactness and dynarec fight each other; consumers choose oracle fidelity or speed |
| 4 | CPU spec authoring | **Typed C# tables + Roslyn source generator from day one**, emitting both execution tiers from one spec | The Pydgin trick; generator pressure-tests the micro-op IR from the start |
| 5 | First milestone | Research ladder (6502 → JIT → Z80) **plus a minimal console host in M1** | A running machine proves the composition seam; tests alone don't |

## 3. Solution shape

Target: **.NET 10 (LTS)**, xUnit for tests, `unsafe` enabled only where `delegate*` dispatch lives.

```
CpuEmulator.Core         — contracts + runtime primitives (AOT-clean, zero Reflection.Emit)
CpuEmulator.Generators   — Roslyn source generator (netstandard2.0, build-time only)
CpuEmulator.Cpus.Mos6502 — the 6502 spec tables; generated tiers land here
CpuEmulator.Peripherals  — SimpleUart (Timer, IrqController in later milestones; Ram/Rom
                           are MachineBuilder fast-path mappings, not IPeripheral devices)
CpuEmulator.Monitor      — CPU-agnostic machine-language monitor: engine + REPL (AOT-clean)
CpuEmulator.Jit          — Tier-1 block compiler (Reflection.Emit lives ONLY here)
CpuEmulator.Host         — console host: boots a 6502 board, UART ↔ stdio
CpuEmulator.Tests        — unit tests + TomHarte harness + Klaus integration
```

**Load-bearing packaging rule:** `CpuEmulator.Jit` is a separate assembly. NativeAOT consumers
never reference it, so the AOT story is enforced by packaging, not by runtime guards sprinkled
through Core. `Core` + `Cpus.*` + `Peripherals` stay trim-safe and AOT-clean by construction.
The `RuntimeFeature.IsDynamicCodeSupported` gate matters only at the seam where a machine
optionally attaches the JIT tier.

## 4. Core contracts (`CpuEmulator.Core`)

- **`ICpuCore`** — `Reset()`, `Step()` (one instruction), `Run(ref long cycleBudget)`, IRQ/NMI
  line inputs, and state introspection (register name → value) so the test harness and a future
  debugger work against any CPU uniformly. Each CPU gets a generated, strongly-typed state struct
  (e.g. `Mos6502State`: A/X/Y/S/P/PC + cycle counter); the introspection interface is the slow
  generic view over it.
- **`IAddressSpace`** — page-table-backed. Page entries point at either backing memory (RAM/ROM
  `byte[]`, the fast path) or an `IPeripheral` handler (the MMIO slow path). The
  **program / data / IO multi-space design exists from day one** (8051/Z80/8086 demand it); the
  6502 maps a single space. This is the seam the M2 fastmem split compiles against, so it must be
  right early.
- **`IPeripheral`** — `Read(offset, width)` / `Write(offset, width, value)`. Width is in the
  contract now, even though 8-bit CPUs only use byte access, so the 68000 doesn't force a contract
  break later. Lifecycle is QOM-style two-phase: constructor = configure,
  `Realize(IMachineContext)` = wire to bus, claim IRQ lines.
- **`Machine`** — the container device. Composes CPU + address spaces + peripherals, drives
  Construct → Realize ordering, owns the clock. The Host builds a tiny `Breadboard6502` machine
  from this.
- **`IScheduler`** — deliberately minimal in M1: a cycle counter plus an event-queue interface.
  DELIVERED to its planned shape in the devices chunk (PR #11): `ScheduleAt` returns a
  `ScheduledEvent` cancellation handle, `ScheduleEvery` repeats, `CurrentCycle` is
  device-honest (the machine binds the CPU's live cycle counter), and `Machine.Run` chunks
  CPU slices to the next pending event so callbacks fire at their exact cycle.
- **Interrupt controller** — partially delivered. M1 CPUs exposed raw IRQ/NMI boolean lines;
  PR #11 made the lines **wired-OR multi-source** (`IInterruptLine.Source()` per-device
  handles — N devices share one pin, open-collector style). A prioritized
  `IInterruptController` *device* (8259-style) remains M4+.

## 5. The ISA spec and micro-op IR

A CPU spec is a static table of typed records in an attributed partial class:

```csharp
[CpuSpecification("mos6502")]
public static partial class Mos6502Spec
{
    // Addressing modes are micro-op templates: cycle-by-cycle bus patterns
    static readonly AddressingMode AbsX = Mode("abs,X", FetchLo, FetchHi, AddIndexX);

    static readonly InstructionDef[] Instructions = [
        Insn(0xA9, "LDA", Imm,  [ Load(A, Operand), SetNZ(A) ]),
        Insn(0xBD, "LDA", AbsX, [ Load(A, Mem),     SetNZ(A) ]),
        // ...
    ];
}
```

- **Micro-ops are at bus-transaction granularity.** On the 6502 every cycle is a bus cycle, so
  "one micro-op step = one cycle" makes the interpreter cycle-true — including dummy reads. This
  granularity is what lets the harness diff TomHarte's per-cycle bus traces (decision #3).
- **Accepted constraint:** Roslyn generators read syntax; they do not execute code. The spec
  combinators (`Insn`, `Mode`, `Load`, `SetNZ`, …) therefore form a deliberately **constrained
  DSL-in-C#** — declarative compositions the generator statically analyzes. No arbitrary lambdas
  in semantics. This is the price of "one spec, generated tiers," and where most framework design
  effort concentrates.
- **One spec → four generated artifacts:**
  1. the CPU state struct;
  2. the Tier-0 interpreter — per-opcode methods behind a `delegate*[256]` dispatch table,
     stepping micro-ops in true cycle order;
  3. a generated per-opcode descriptor table (`Mos6502Cpu.JitDescriptors`) consumed by a
     CPU-agnostic block compiler in `CpuEmulator.Jit` (M2-ii closeout: the realization is a
     descriptor table the compiler walks, not per-opcode emission methods on the generated type —
     the compiler emits the IL, keyed by each opcode's descriptor: length, cycles, kind,
     ends-block, needs-fallback);
  4. a disassembler table — nearly free from mnemonic + mode data, and it turns harness failures
     from "opcode 0xBD mismatch" into "`LDA $1234,X` at $C010 mismatch."
  5. a single-instruction assembler — the inverse of ④, from the same table (mnemonic + operand
     text → opcode bytes), feeding the per-CPU monitor (decision 2026-06-12).

## 6. Execution tiers

### Tier 0 — interpreter (M1, the correctness oracle)

Generated per-opcode methods step micro-ops in true cycle order: every bus transaction happens at
the cycle it would on silicon; the cycle counter advances per micro-op; the bus sees reads/writes
(including dummy reads) in real order. Dispatch is a
`delegate*<ref Mos6502State, Bus, void>[256]` table — zero-alloc, AOT-safe.

IRQ/NMI are sampled **and serviced** at instruction boundaries (since 3b-ii): NMI is
edge-latched (rising edge of `SetNmiLine`; the latch clears when serviced and on Reset),
IRQ is level-sensitive gated by the I flag, and NMI wins when both are pending. The service
sequence is the authentic 7 cycles — two dummy reads at PC, push PCH/PCL/P with B clear,
vector fetch from $FFFA/$FFFB (NMI) or $FFFE/$FFFF (IRQ), I set. The 6502's
**mid-instruction** sampling quirks (branch-cycle polling, the CLI/SEI/PLP one-instruction
I-flag delay, BRK/NMI vector hijacking) remain a documented deviation, revisited when a
phase-A machine needs them.

### Tier 1 — IL-JIT (M2, the speed path; lives entirely in `CpuEmulator.Jit`)

- **Block discovery:** decode from PC until an unconditional control-flow change; translate the
  run into one `DynamicMethod`.
- **Codegen:** per-opcode emitters (generated in M1, exercised in M2) compose into the block.
  Architectural state is hoisted into IL locals at block entry and flushed at exit so RyuJIT can
  register-allocate it.
- **Fastmem split:** memory micro-ops consult the page table at emit time where the map is static
  (true for fixed-map 8-bit boards): RAM/ROM pages become direct array accesses; MMIO pages become
  `IPeripheral` call-outs. Pages not provably static get an emitted runtime check.
- **Cycle budget:** each block decrements a counter by its cycle cost; crossing zero exits to the
  dispatcher, which checks IRQ lines (the mupen64plus `cc_interrupt` pattern). Interrupt latency
  is block-boundary — the documented accuracy contract of this tier.
- **Invalidation:** dirty-page bitmap — a write to a page that owns cached blocks invalidates
  them. Non-optional: 6502 software runs code from RAM.
- **Block cache + chaining:** the dispatch table is pre-filled with compile-me stubs; blocks chain
  directly once both ends exist, with an unlink table so invalidation stays correct.
- **Safety valve:** any block that fails to compile falls back to Tier 0 permanently for that
  address. The JIT is never required for correctness.

## 7. Error handling

Two strictly separated failure domains:

- **Guest-world events are emulated behavior, not host exceptions.**
  - Undefined opcodes route to a per-machine policy: `Throw` (default for development), `Nop`, or
    a user callback. Illegal-6502-opcode *support* is a later increment (TomHarte has vectors).
  - Unmapped bus reads return a configurable open-bus value (default `0xFF`); unmapped writes are
    ignored. An opt-in **strict bus mode** throws instead — the firmware-debugging posture.
- **Host-world failures** (bad spec table, generator misuse, JIT emission bug) throw
  `EmulationException`-derived types eagerly and loudly. A spec error fails at build or at
  `Realize`, never mid-run.

## 8. Testing and validation

- **TomHarte harness (the centerpiece):** a generic runner that, for any `ICpuCore`, loads the
  SingleStepTests JSON vectors, sets initial state via the introspection interface, executes one
  instruction against an instrumented recording bus, and diffs **final state + cycle count + the
  per-cycle bus trace** (address / value / read-write). Failures print disassembly plus a
  cycle-by-cycle expected/actual table.
- **Vector logistics:** the vector sets are hundreds of MB — never vendored into the repo. A fetch
  script populates a cache directory (`CPUEMULATOR_TESTVECTORS` env var override); vector tests
  skip with a clear message when vectors are absent; CI fetches and caches them.
- **Klaus Dörmann functional test** as the integration tier: boots in the Host's `Breadboard6502`
  machine and runs to the success trap. Doubles as the M1 demo.
- **Tier parity (M2):** differential testing — the same vectors plus randomized state/program fuzz
  run through Tier 0 and Tier 1; any divergence is a JIT bug by definition. BenchmarkDotNet proves
  the speedup is real.
- **Unit tests** for the page table, peripheral lifecycle, scheduler, and the generator itself
  (generated-source snapshot tests so codegen diffs surface at PR time).
- **Machine-language monitor as the acceptance surface** (decision 2026-06-12): every CPU gets a
  monitor — load code, save memory, disassemble memory, modify memory, assemble single
  instructions at an address, inspect/set registers, step/run — built from a CPU-agnostic engine
  over `ICpuCore` introspection + `IAddressSpace`, with the per-CPU disassembler/assembler
  artifacts generated from the spec table. Monitor scripts drive extensive automated acceptance
  tests as the system fleshes out.
- **Automated UAT is a pre-merge gate** (decision 2026-06-12): every PR runs acceptance-level
  validation appropriate to its scope before merge — e.g. the full TomHarte sweep, the Klaus
  functional test, and monitor-driven end-to-end scenarios once available — in addition to the
  unit/integration suite and review gates.
- **Feature documentation ships with every user-facing change** (decision 2026-06-12): any PR
  that adds or changes user-visible behavior must include corresponding additions or updates to
  `docs/user-guide/`. Documentation completeness is a merge gate enforced by the PR checklist
  (`.github/pull_request_template.md`). Docs-only PRs are exempt from UAT requirements.

## 9. Milestones

Each numbered item ≈ one PR-sized chunk on a branch.

- **M1 — the spine**
  1. Core contracts + bus + machine skeleton, unit-tested.
  2. Generator pipeline end-to-end with a ~10-opcode 6502 subset — proves spec → generator →
     interpreter before scaling.
  3. Spec-importer tool (`tools/`): generates the per-opcode rows of a spec table from a
     curated machine-readable opcode dataset; micro-op semantics stay hand-authored once per
     mnemonic (~56 for the 6502) — "mostly automated" spec creation (decision 2026-06-11).
  4. Full documented-opcode 6502 spec (rows generated by the importer), TomHarte green +
     Klaus functional test as the automated-UAT gate. (Split into two PRs: 3b-i engine
     restructure + new modes/vocabulary + TomHarte harness; 3b-ii BCD + interrupts/BRK +
     full-151 green + importer-regenerated live spec.)
  5. Monitor + generated single-instruction assembler (decision 2026-06-12): CPU-agnostic
     monitor engine (load/save/disassemble/modify/assemble/registers/step) — the automated
     acceptance-test surface.
  6. Datasheet-extraction runbook, Stages 1–2 (decision 2026-06-12): LLM-assisted extraction
     of opcode datasets + draft semantics maps from CPU PDFs/manuals directly into the
     importer's schemas; a cross-source diff tool (two independent documents must agree) and a
     `--validate-only` importer mode; per-row provenance citations (schema field added 3b-i)
     and a review-report generator. Verification stack: strict loaders → CPUGEN diagnostics →
     e2e generator gate → SingleStepTests sweeps where vectors exist. Extraction eliminates
     transcription and drafts semantics; per-family micro-op vocabulary and mode cycle-templates
     remain hand work by design. **DELIVERED PR #10.**
  7. Peripherals (SimpleUart; Ram/Rom realized as MachineBuilder fast-path mappings —
     recorded deviation, PR #8) + Host (with monitor REPL) + Klaus demo running. DELIVERED:
     the `Breadboard6502` live host, PR #8.
- **M2 — the JIT**
  8. Block compiler + cache + fastmem split. **DELIVERED PR #12 (M2-i, stood up) + PR #13 (M2-ii):
     block chaining/linking with an unlink table + per-page invalidation, decimal-arm ADC/SBC IL
     emission, and full parity proven — the `CPUEMULATOR_UAT=full` 1.51M-case TomHarte sweep through
     the JIT (0 failures), the committed seeded SMC-biased differential fuzzer (N=4096, chaining
     on+off, 0 divergences), and Klaus-under-JIT cycle-exact at 96,241,367 and faster.**
  9. Parity harness + benchmarks — including **comparative performance data vs other
     available emulators** (decision 2026-06-12): a `bench/` harness measuring emulated
     cycles per host-second on a canonical, portable workload (the Klaus functional test's
     deterministic 96.2M-cycle run, plus a tight arithmetic kernel), run identically against
     (a) our Tier-0 interpreter, (b) our Tier-1 JIT, and (c) a small set of third-party 6502
     emulators across languages (candidates: fake6502 in C, py65 in Python, Asm6502 in C#,
     one JS implementation) behind thin adapter shims. Deliverables: a methodology document
     (same workload, same machine, warmup/measurement windows, environment metadata — honest
     cross-language comparison rules) and a regenerable results report committed with the
     numbers and the hardware/runtime captured. Our two recorded M2 revisit gates
     (switch-vs-`delegate*` dispatch; fields-vs-state-struct) are decided from this same data.
     **DELIVERED PR #13 (M2-ii):** the `bench/` suite (a `CpuEmulator.Benchmarks` core library +
     a BenchmarkDotNet runner) with the methodology doc (`bench/README.md`), the four adapter shims
     (Asm6502/C#, fake6502/C, py65/Python, sfotty/JS) behind a graceful-degradation Probe/skip-with-
     note seam, and a regenerable `bench/results/REPORT.md` committed with only measured data. Both
     revisit gates were measured (`--gates`) and recorded with their numbers; the decision on each
     is "measured, kept current" (the Tier-1 JIT — chaining + emitted decimal arms — is where the
     speed now lives; a Tier-0 dispatch/layout micro-opt is not material to the overall picture).
- **M3 — the pluggability proof (Z80).** ADR: `docs/architecture/0001-z80-second-architecture.md`.
  Z80 by spec **and by extraction** (the runbook's first real non-6502 use): prefix opcodes
  (CB/ED/DD/FD + DDCB/FDCB), separate 16-bit I/O space, the alternate register set + IX/IY/I/R,
  the S Z Y H X P/V N C flag model, 16-bit ALU, block ops, IM 0/1/2. Proven against the
  SingleStepTests Z80 vectors. Cross-cutting decisions locked (checkpoint 2026-06-13):
  data-driven register file (retire the closed `Reg` enum), a generic multi-byte-key decoder
  (retire the `[256]`/`switch(opcode)` shape), partial Z80-through-JIT in M3 (hot straight-line
  ops emitted, block-ops/DAA/EX/IO fall back), full cycle + undocumented-flag fidelity.
  M3 proves the framework's **front half** is architecture-generic.
- **The genericity ladder before optimization (decision 2026-06-13).** The cross-architecture
  JIT optimization is gated behind THREE diverse architectures, in this order, each through both
  tiers and TomHarte-validated:
  - **M3 — Z80:** decode structure, register file, flag model, block/chain model.
  - **M4 — 68000:** 32-bit registers, **big-endian**, word/long bus transactions, the 24-bit
    address space (the bus + register-width model). Fits the current `addressBits ≤ 24` wall.
  - **M5 — 8086:** **segmentation** (non-flat addressing), variable-length decode (hammers the
    generic decoder), instruction prefixes. 20-bit physical; also under the ≤24 wall.
  - **M6 — cross-architecture JIT optimization:** make Tier-1 beat Tier-0 (cheaper SMC
    invalidation, register allocation, lower per-block dispatch cost) — only optimizations that
    help across all four ISAs count, validated by the multi-architecture parity + bench suites.
- **Phase-A horizon (post-M6):** interrupt-controller *device* (timers + wired-OR multi-source
  lines DELIVERED PR #11; a prioritized 8259-style controller device remains), real board
  recreations, NuGet packaging polish, and a TomHarte-derived **spec linter** that infers
  per-opcode behavior (operand size, addressing mode, cycle shape) from the SingleStepTests
  vectors and cross-checks the curated opcode dataset — Stage 3 of the datasheet-extraction
  pipeline (decision 2026-06-12): extraction proposes, vector inference disposes.

## 10. Success criteria

- **M1:** TomHarte 6502 vectors green (documented opcodes) + Klaus functional test passes inside
  the console Host.
- **M2:** zero parity divergence between tiers + a measured, reported speedup.
- **M3:** the Z80 lands with zero — or enumerated and justified — changes to `CpuEmulator.Core`.

## 11. Out of scope (for now)

- Cycle-exact timing in the JIT tier; mid-instruction interrupt sampling.
- Illegal/undocumented 6502 opcodes (vectors exist; later increment).
- Video, sound, and any full retro-machine recreation (phase A).
- A native (non-IL) codegen backend — the Ryujinx lesson says don't pre-optimize toward it.
- GUI/debugger frontends; the introspection interface is the only debugger hook for now.
