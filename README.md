# CpuEmulator

A research-stage project to build a **pluggable, multi-architecture CPU-emulation framework in modern C#**, using the .NET runtime's own JIT as a dynamic-recompilation backend (translate guest machine code → .NET IL at runtime → let RyuJIT compile to native), with an interpreter tier as the always-available correctness oracle.

## Goals

- **Pluggable CPU/ISA specifications** — add an architecture by writing a declarative instruction spec, not a hand-written emulator. Target ISAs: 6502, 6800, Z80, 8051, 8086/8088, 68000/68020.
- **Pluggable peripherals on a bus** — RAM/ROM, serial/UART, digital & analog I/O (GPIO/ADC/DAC), timers, interrupt controllers — composed onto address spaces like MAME devices / QEMU QOM.
- **Two execution tiers from one spec** — a `delegate*`-dispatched interpreter (AOT-safe) and an IL-emitting JIT (fast path), both generated from the same instruction description.

## Status

**Research phase.** No production code yet. The design rationale, prior-art survey, codegen-backend analysis, and a proposed reference architecture live in:

- [`docs/research/emulation-framework-research.md`](docs/research/emulation-framework-research.md)

## Next steps

1. Architect ADR locking the **JIT-vs-NativeAOT** decision and the core interface contracts (`ICpuCore`, `IAddressSpace`, `IPeripheral`, `IScheduler`, `IInterruptController`).
2. Milestone 1 plan: 6502 interpreter + bus + RAM/ROM + one UART, validated against the [SingleStepTests/ProcessorTests](https://github.com/SingleStepTests/ProcessorTests) cycle-accurate vectors.
3. Milestone 2: add the IL-JIT tier for the 6502 and prove parity + speedup.
4. Milestone 3: add Z80 by writing only a spec — proving the pluggable abstraction.

## License

TBD.
