# CpuEmulator

A project to build a **pluggable, multi-architecture CPU-emulation framework in modern C#**, using the .NET runtime's own JIT as a dynamic-recompilation backend (translate guest machine code → .NET IL at runtime → let RyuJIT compile to native), with an interpreter tier as the always-available correctness oracle.

## Goals

- **Pluggable CPU/ISA specifications** — add an architecture by writing a declarative instruction spec, not a hand-written emulator. Target ISAs: 6502, 6800, Z80, 8051, 8086/8088, 68000/68020.
- **Pluggable peripherals on a bus** — RAM/ROM, serial/UART, digital & analog I/O (GPIO/ADC/DAC), timers, interrupt controllers — composed onto address spaces like MAME devices / QEMU QOM.
- **Two execution tiers from one spec** — a `delegate*`-dispatched interpreter (AOT-safe) and an IL-emitting JIT (fast path), both generated from the same instruction description.

## Try it

```
dotnet run --project src/CpuEmulator.Host            # boot to the monitor
dotnet run --project src/CpuEmulator.Host -- --demo  # 5-second proof: ROM prints, exits
```

A first session — the demo ROM is already in ROM at $E000; `g` runs it, `i` talks to it,
and the monitor assembles new code anywhere in RAM:

```
CpuEmulator — Breadboard6502
6502 · RAM $0000-$CFFF · UART $D000 (DATA $D000, STATUS $D001) · ROM $E000-$FFFF (demo)
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

A few behaviors to know up front: monitor memory commands go through the live bus, so `m` over $D000
consumes pending UART input and `a`/`m`-writes over ROM land nothing — the echo shows what is really
there (a side-effect-free peek/poke API is recorded backlog). `i` injects everything after the first
space verbatim — doubled spaces inject a leading space (quote the text to make leading/trailing
spaces explicit); nothing is appended. Ctrl+C kills the process; bounded `g` budgets (default
1,000,000 cycles) are the runaway protection, and EOF (Ctrl+Z+Enter on Windows, Ctrl+D elsewhere)
quits like `q`.

## Status

Milestone 1 in progress. `CpuEmulator.Core` (contracts) and the Roslyn source generator are
implemented and tested: CPU specs are typed C# tables, parsed with build-time diagnostics,
and the generator emits a working interpreter plus a disassembler. **The 6502 is complete:
151/151 documented opcodes cycle-exact** — all 13 addressing modes, ALU/RMW/stack/flag/flow
vocabulary, NMOS decimal-mode ADC/SBC, BRK/RTI, silicon-true dummy reads and dummy writes,
the JMP ($xxFF) page-wrap bug, hardware-true P phantom bits, and IRQ/NMI servicing at
instruction boundaries (NMI edge-latched, IRQ level-sensitive gated by I) — validated
**per-cycle against the full TomHarte/SingleStepTests sweep (1,510,000 cases, zero skips)**
and the **Klaus Dörmann functional test run to its documented success trap**. `Mos6502Spec.cs`
is committed importer output: `tools/CpuEmulator.SpecImporter` generates it from the curated
151-opcode dataset + 56-mnemonic semantics map, and a byte-equality test pins the committed
file to fresh tool output. Vectors and the Klaus binary are fetched on demand
(`tools/get-test-vectors.ps1`/`.sh`, `tools/get-klaus.ps1`/`.sh`), never vendored; the
TomHarte theories sample 200 cases/opcode by default and run all 10,000 under
`CPUEMULATOR_UAT=full`. **The machine-language monitor is live as the acceptance surface**:
load/save memory and files, hex-dump/modify, disassemble, assemble single instructions at an
address (with branch-target resolution), inspect/set registers, interrupt-aware single-step,
run/run-until with trap detection — a CPU-agnostic engine + line-oriented REPL in
`CpuEmulator.Monitor` (AOT-clean, Core-only), surfaced per-CPU through the generated
`IMonitorSupport`. The **single-instruction assembler is the fifth generated artifact**, the
exact inverse of the disassembler from the same spec table, pinned by a 151-opcode roundtrip
identity test (`assemble(disassemble(op)) == op-bytes`); monitor-driven UAT sessions
(assemble/run/inspect/disassemble, Klaus-via-monitor) run in the suite under
`--filter "Category=UAT"`. **The live machine is runnable** (see "Try it" above):
`dotnet run --project src/CpuEmulator.Host` boots a `Breadboard6502` — 52 KiB RAM, a
partial-decode `SimpleUart` at $D000 (`CpuEmulator.Peripherals`, AOT-clean, Core-only), and
an 8 KiB demo ROM assembled at boot by the generated assembler — and drops into the monitor
REPL on stdio, with scheduler-aware `g`/`s` (runs route through `Machine.Run`, so scheduled
peripherals tick) and UART input via `i`; `--demo` and `--load <bin> [--at $addr]
[--pc $addr]` modes ship alongside, and host UAT sessions (demo-hello exact transcript,
echo, Klaus-through-the-host) run in the suite under `--filter "Category=UAT"`. Next: the
datasheet-extraction runbook (M1 item 6), then M2 (the IL-JIT tier).

- Design: `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`
- Research: `docs/research/emulation-framework-research.md`

Build and test: `dotnet test`

## Next steps

1. Finish Milestone 1: the spine is complete — source-generated 6502 interpreter + one UART + live host, validated against the [SingleStepTests/ProcessorTests](https://github.com/SingleStepTests/ProcessorTests) cycle-accurate vectors; remaining: the datasheet-extraction runbook (M1 item 6).
2. Milestone 2: add the IL-JIT tier for the 6502 and prove parity + speedup.
3. Milestone 3: add Z80 by writing only a spec — proving the pluggable abstraction.

## License

TBD.
