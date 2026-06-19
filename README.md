# CpuEmulator

A **pluggable, multi-architecture CPU-emulation framework in modern C#**, using the .NET runtime's own JIT as a dynamic-recompilation backend (translate guest machine code → .NET IL at runtime → let RyuJIT compile to native), with an interpreter tier as the always-available correctness oracle.

## Try it

```
dotnet run --project src/CpuEmulator.Host               # boot to the monitor
dotnet run --project src/CpuEmulator.Host -- --demo     # 5-second proof: ROM prints, exits
dotnet run --project src/CpuEmulator.Host -- --terminal # raw per-keystroke terminal; Ctrl-] to monitor
```

A first session — the demo ROM is already in ROM at $E000; `g` runs it, `i` talks to it,
and the monitor assembles new code anywhere in RAM:

```
CpuEmulator — Breadboard6502
6502 · RAM $0000-$CFFF · UART $D000 (DATA/STATUS/CTRL) · timer $D100 · ROM $E000-$FFFF (demo)
UART output prints inline; 'i TEXT' feeds UART input; 'g' runs (reset entry $E000); '?' help; 'q' quit.
* g 1000
Hello from Breadboard6502!
budget exhausted at $E011 after 1000 cycles
* i HI
injected $2 bytes
* g 200
HIbudget exhausted at $E011 after 200 cycles
* a $0200 LDA #$41
0200: A9 41     LDA #$41
* a STA $D000
0202: 8D 00 D0  STA $D000
* g $0200 until $0205 100
Atarget $0205 reached after 6 cycles
* q
```

Load a binary instead: `dotnet run --project src/CpuEmulator.Host -- --load prog.bin --at $0200 --pc $0200`

A few behaviors to know up front: monitor *display* reads (`m`/`d`/`s`) are side-effect-free over
devices with an honest peek (the UART and the timer) — `m` over $D000 shows the rx queue head
without consuming it; `a`/`m`-writes over ROM land nothing — the echo shows what is really there
(verify-after-write is feasible now that Peek exists; recorded backlog). `i` injects everything
after the first space verbatim — doubled spaces inject a leading space (quote the text to make
leading/trailing spaces explicit); nothing is appended. Ctrl+C kills the process in REPL mode
(in `--terminal` mode it is a guest byte; Ctrl-] exits the terminal); bounded `g` budgets (default
1,000,000 cycles) are the runaway protection, and EOF (Ctrl+Z+Enter on Windows, Ctrl+D elsewhere)
quits like `q`.

## Status

**Where it stands (M1–M6 complete):** four cycle-exact interpreters (6502, Z80, 68000, 8086), a
dual-tier IL-JIT in which three of the four CPUs now emit IL for their hot op families, a CPU-agnostic
monitor + device layer, and a comparative cross-language benchmark suite. The
[Roadmap](docs/ROADMAP.md) covers what is deferred next. The milestone-by-milestone history follows.

Milestone 1 is complete. `CpuEmulator.Core` (contracts) and the Roslyn source generator are
implemented and tested: CPU specs are typed C# tables, parsed with build-time diagnostics,
and the generator emits a working interpreter plus a disassembler. **The 6502 is complete:
151/151 documented opcodes cycle-exact** — all 13 addressing modes, ALU/RMW/stack/flag/flow
vocabulary, NMOS decimal-mode ADC/SBC, BRK/RTI, silicon-true dummy reads and dummy writes,
the JMP ($xxFF) page-wrap bug, hardware-true P phantom bits, and IRQ/NMI servicing at
instruction boundaries — validated **per-cycle against the full TomHarte/SingleStepTests
sweep (1,510,000 cases, zero skips)** and the **Klaus Dörmann functional test (success trap
at $3469, ~96M cycles)**. The single-instruction assembler (artifact ⑤) is the exact inverse
of the disassembler from the same spec table, pinned by a 151-opcode roundtrip identity test.
The **live machine is runnable**: `dotnet run --project src/CpuEmulator.Host` boots a
`Breadboard6502` (52 KiB RAM, `SimpleUart` at $D000, `IntervalTimer` at $D100, 8 KiB demo
ROM assembled at boot by the generated assembler) and drops into the monitor REPL. The
**datasheet-extraction tooling is complete**: `--validate-only` validates both schemas and
reports provenance coverage; `--diff` cross-checks two independent extractions (exit 3 on
disagreements); `--review-report` generates a markdown review artifact. The
[extraction runbook](docs/user-guide/extraction-runbook.md) documents the full LLM-assisted
Stage-1 workflow. **M1 is complete.** **The device layer is real (PR #11):** the scheduler
has its planned teeth (`ScheduledEvent` cancellation, `ScheduleEvery`, event-chunked
`Machine.Run`), interrupt lines are wired-OR multi-source, the UART has a level rx-IRQ
(CTRL at $D002), a 16-bit cycle-exact `IntervalTimer` lives at $D100, monitor display
reads are side-effect-free over honest devices (`TryPeek`), and `--terminal` opens a raw
per-keystroke terminal onto the guest — all pinned by interrupt-driven UAT sessions
(a WAI-free echo and a timer-IRQ counter, both monitor-assembled).

**M2 — the IL-JIT tier — is complete (PR #12 stood it up; PR #13 finished it, this branch).** Tier 1
(`CpuEmulator.Jit`, the only `Reflection.Emit` assembly, deliberately not AOT-compatible) is a
*provably-equivalent* dual-tier execution path: a generated per-opcode descriptor table drives a
CPU-agnostic block compiler that walks descriptors into one `DynamicMethod` per block, with a PC-keyed
block cache, the RAM/ROM-direct **fastmem split** (MMIO falls back to a bus callout), a per-block cycle
budget + exit, block-entry interrupt checks, **block chaining** (direct block→block transitions at
statically-known exits, with an unlink table + per-page-precise SMC invalidation), and **emitted
decimal-arm ADC/SBC** (both binary and BCD arms behind the D-bit test). `JittedCpu` wraps the
interpreter, which remains the oracle, the fallback (only BRK/RTI/undefined run through it now), and
the state owner. The tier is gated at construction on `RuntimeFeature.IsDynamicCodeSupported`, keeping
Core/Cpus/Peripherals/Monitor/Host AOT-clean by construction (pinned by a reference-graph build check;
a real NativeAOT publish of the Host succeeds with `PublishAot` scoped to the runtime graph).
**Validated to full parity:** the `CPUEMULATOR_UAT=full` **1,510,000-case TomHarte sweep through the
JIT** (0 failures, including the 80,093 decimal-mode cases via the emitted decimal arm), a **committed
seeded SMC-biased differential fuzzer** (JIT vs interpreter, chaining on+off; CI N=64,
`CPUEMULATOR_FUZZ=full` N=4096), the **Klaus functional test to the success trap ($3469) under the JIT
at the interpreter's exact cycle count (96,241,367)**, the 8 UAT sessions JIT-wrapped, and
trace-equivalence spot tests. A **comparative cross-language benchmark suite** (`bench/`) measures
emulated cycles/host-second across both tiers and third-party 6502 emulators (C# Asm6502, C fake6502,
Python py65, JS sfotty) — see [Benchmarks](docs/user-guide/benchmarks.md). The measured headline is
honest: the JIT delivers correctness parity first. M2 stood up the dual-tier path; **M6 made it fast**
— the high-ROI op families of the 6502, Z80, 68000, and 8086 now emit IL (rather than falling back to
the interpreter per instruction), and the M6 SMC/recompile-cost lever closed the self-modifying-code
thrash that made the JIT slower than the interpreter on the Klaus workload. See
[The JIT Tier](docs/user-guide/jit.md) for the accuracy contract, the per-CPU emit arms, and chaining,
and [Benchmarks](docs/user-guide/benchmarks.md) for the measured throughput.

**M3–M5 — three more architectures — are complete.** The point of every ISA after the 6502 is to
*prove the abstractions generalize*: every seam built for the 6502 (the register file, the decoder,
bus I/O, flags) is re-validated against a genuinely different processor while the earlier CPUs stay
byte-identical. The framework now ships **four interpreters**, each validated against the
SingleStepTests/TomHarte corpus for its ISA:

- **M3 — Zilog Z80:** the full ISA — base + `CB`/`ED`/`DD`/`FD`/`DDCB`/`FDCB` prefix planes, IX/IY
  indexing, block ops, 16-bit `ADC`/`SBC`, the interrupt modes — with the **per-spec flag-bit map**
  (S Z Y H X P/V N C), **bidirectional register-pair aliasing** (8-bit halves are storage, 16-bit
  pairs are computed views), F's undocumented X/Y bits, the **WZ/MEMPTR and Q** internal registers,
  the per-T-state bus trace, and the SCF/CCF NMOS X/Y quirk. Validated against the Z80 TomHarte sweep
  and ZEXALL/ZEXDOC.
- **M4 — Motorola 68000:** 32-bit registers over a 16-bit big-endian bus, the field-grammar decoder,
  the full effective-address mode set, the MOVE/integer-ALU/shift-rotate/bit/BCD families, the CCR
  (with the X-bit distinct from C), and control-flow/exception handling. Validated **data-axis-exact**
  against the gzip 680x0 corpus (coarse-cycle timing by design — see ADR 0008/0011).
- **M5 — Intel 8086/8088:** the framework's 4th ISA — variable-length ModR/M decode, `(CS<<4)+IP`
  segmentation, the integer-ALU/MOV/shift/string/control families, and the FLAGS model (with AF/PF).
  Validated against the 8088 TomHarte corpus.

**M6 — the cross-architecture JIT-optimization pass — is complete.** Three of the four CPUs now emit
IL through the Tier-1 JIT for their high-ROI op families (each gated on byte-identical
TomHarte-through-JIT parity); the rare/exception/microcoded tail of each ISA stays interpreter-fallback
**by design**, and the 6502's self-modifying-code thrash was closed with a recompile-cost lever. See
the [Roadmap](docs/ROADMAP.md) for what shipped in M6 and what is deferred next. The 6502 stays the
reference oracle: empty source diff per ISA addition, full both-tier sweep, Klaus cycle-exact. See
[Testing](docs/user-guide/testing.md) for exactly what executes today and
[The JIT Tier](docs/user-guide/jit.md) for the emit arms and the accuracy contract.

For full detail see the [User Guide](docs/user-guide/README.md) and the [Roadmap](docs/ROADMAP.md).

## User Guide

- [Getting Started](docs/user-guide/getting-started.md) — prerequisites, build, first session
- [Monitor Reference](docs/user-guide/monitor-reference.md) — every REPL command
- [Breadboard6502](docs/user-guide/breadboard6502.md) — memory map, UART, interval timer, demo ROM
- [Building Machines](docs/user-guide/building-machines.md) — MachineBuilder, peripherals, monitor wiring
- [The JIT Tier](docs/user-guide/jit.md) — Tier 1 (IL-JIT): enabling it, the accuracy contract, troubleshooting
- [Adding a CPU](docs/user-guide/adding-a-cpu.md) — spec tables, importer, generated artifacts
- [Testing](docs/user-guide/testing.md) — suite, TomHarte vectors, Klaus, UAT sessions

## Architecture

The framework is a set of .NET 10 libraries. `CpuEmulator.Core` defines the contracts (`ICpuCore`, `IAddressSpace`, `IPeripheral`, `Machine`); `CpuEmulator.Generators` is a Roslyn source generator that reads a typed C# spec table and emits the per-CPU artifacts at build time (register state + introspection, cycle-exact interpreter, disassembler, single-instruction assembler, and a per-opcode JIT descriptor table the IL-JIT consumes); **four CPU spec assemblies hold each ISA's spec table (importer output) plus its hand-written partials** — `CpuEmulator.Cpus.Mos6502`, `CpuEmulator.Cpus.Z80`, `CpuEmulator.Cpus.M68000`, and `CpuEmulator.Cpus.M8086`; `CpuEmulator.Peripherals` ships `SimpleUart` and `IntervalTimer`; `CpuEmulator.Monitor` is the CPU-agnostic monitor engine + REPL; `CpuEmulator.Host` is the console entry point; `CpuEmulator.Jit` is the IL-JIT tier — block chaining, the SMC/recompile-cost lever, and the **per-CPU IL-emit arms** (`BlockCompiler.<Cpu>.cs`: the hand-written emit logic the generated descriptor table dispatches into; see [The JIT Tier](docs/user-guide/jit.md)). All library projects are AOT-compatible except `CpuEmulator.Jit`, which is the only project that uses `Reflection.Emit` and is therefore the only non-AOT member of the build graph (the others never reference it — a packaging law pinned by a build-time reference-graph check; a NativeAOT publish of the Host succeeds with `PublishAot` scoped to its csproj). The comparative benchmark suite lives in `bench/` (a `CpuEmulator.Benchmarks` core library + a BenchmarkDotNet runner) — a dev tool, never in any shipped graph.

Full design: [`docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`](docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md)

## Development workflow

Build and test:

```
dotnet build    # 0 warnings required
dotnet test     # full suite (routine: fuzzer at N=64); CPUEMULATOR_UAT=full + CPUEMULATOR_FUZZ=full pre-merge
```

All work happens on short-lived feature branches; changes merge to `main` via pull request. See [Testing](docs/user-guide/testing.md) for the full pre-merge gate (TomHarte full sweep, Klaus, UAT sessions).

## License

CpuEmulator is released under the [MIT License](LICENSE).
