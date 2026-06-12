# CpuEmulator

A project to build a **pluggable, multi-architecture CPU-emulation framework in modern C#**, using the .NET runtime's own JIT as a dynamic-recompilation backend (translate guest machine code → .NET IL at runtime → let RyuJIT compile to native), with an interpreter tier as the always-available correctness oracle.

## Goals

- **Pluggable CPU/ISA specifications** — add an architecture by writing a declarative instruction spec, not a hand-written emulator. Target ISAs: 6502, 6800, Z80, 8051, 8086/8088, 68000/68020.
- **Pluggable peripherals on a bus** — RAM/ROM, serial/UART, digital & analog I/O (GPIO/ADC/DAC), timers, interrupt controllers — composed onto address spaces like MAME devices / QEMU QOM.
- **Two execution tiers from one spec** — a `delegate*`-dispatched interpreter (AOT-safe) and an IL-emitting JIT (fast path), both generated from the same instruction description.

## Status

Milestone 1 in progress. `CpuEmulator.Core` (contracts) and the Roslyn source generator are
implemented and tested: CPU specs are typed C# tables, parsed with build-time diagnostics,
and the generator now emits a working interpreter — the 11-opcode 6502 subset executes with
cycle-exact bus traces (loads, stores, jumps, branches including page-cross timing, dummy
reads and all), plus a generated disassembler, pinned by literal cycle-by-cycle trace tests.
Next: the spec-importer tool (chunk 3a), then the full 6502 + SingleStepTests validation
(chunk 3b), then peripherals + console host (chunk 4).

- Design: `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`
- Research: `docs/research/emulation-framework-research.md`

Build and test: `dotnet test`

## Next steps

1. Finish Milestone 1: source-generated 6502 interpreter + one UART, validated against the [SingleStepTests/ProcessorTests](https://github.com/SingleStepTests/ProcessorTests) cycle-accurate vectors.
2. Milestone 2: add the IL-JIT tier for the 6502 and prove parity + speedup.
3. Milestone 3: add Z80 by writing only a spec — proving the pluggable abstraction.

## License

TBD.
