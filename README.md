# CpuEmulator

A project to build a **pluggable, multi-architecture CPU-emulation framework in modern C#**, using the .NET runtime's own JIT as a dynamic-recompilation backend (translate guest machine code → .NET IL at runtime → let RyuJIT compile to native), with an interpreter tier as the always-available correctness oracle.

## Goals

- **Pluggable CPU/ISA specifications** — add an architecture by writing a declarative instruction spec, not a hand-written emulator. Target ISAs: 6502, 6800, Z80, 8051, 8086/8088, 68000/68020.
- **Pluggable peripherals on a bus** — RAM/ROM, serial/UART, digital & analog I/O (GPIO/ADC/DAC), timers, interrupt controllers — composed onto address spaces like MAME devices / QEMU QOM.
- **Two execution tiers from one spec** — a `delegate*`-dispatched interpreter (AOT-safe) and an IL-emitting JIT (fast path), both generated from the same instruction description.

## Status

Milestone 1 in progress. `CpuEmulator.Core` (contracts) and the Roslyn source generator are
implemented and tested: CPU specs are typed C# tables, parsed with build-time diagnostics,
and the generator emits a working interpreter plus a disassembler. The 6502 now executes
**149 of 151 documented opcodes cycle-exactly** (everything except BRK and RTI — chunk 3b-ii):
all 13 addressing modes, ALU/RMW/stack/flag/flow vocabulary, silicon-true dummy reads and
dummy writes, the JMP ($xxFF) page-wrap bug, and hardware-true P phantom bits on PHP/PLP —
validated **per-cycle against the TomHarte/SingleStepTests vectors** (~1.41M cases swept; the
~80k decimal-mode ADC/SBC cases are counted skips until 3b-ii lands BCD). `Mos6502Spec.cs` is
committed importer output: `tools/CpuEmulator.SpecImporter` generates it from the curated
151-opcode dataset + 54-mnemonic semantics map, and a byte-equality test pins the committed
file to fresh tool output. Vectors are fetched on demand (`tools/get-test-vectors.ps1`/`.sh`),
never vendored; the TomHarte theories sample 200 cases/opcode by default and run all 10,000
under `CPUEMULATOR_UAT=full`. Next: 3b-ii (BCD, BRK/RTI + interrupts, full-151 green, Klaus
functional test), then the monitor, the datasheet-extraction runbook (M1 item 6), and
peripherals + console host (chunk 4).

- Design: `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`
- Research: `docs/research/emulation-framework-research.md`

Build and test: `dotnet test`

## Next steps

1. Finish Milestone 1: source-generated 6502 interpreter + one UART, validated against the [SingleStepTests/ProcessorTests](https://github.com/SingleStepTests/ProcessorTests) cycle-accurate vectors.
2. Milestone 2: add the IL-JIT tier for the 6502 and prove parity + speedup.
3. Milestone 3: add Z80 by writing only a spec — proving the pluggable abstraction.

## License

TBD.
