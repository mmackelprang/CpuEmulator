# CpuEmulator User Guide

Welcome to the CpuEmulator user guide. Use these pages to get the emulator running, understand its components, and extend it with new machines and CPU architectures.

## Contents

| Document | What it covers |
|---|---|
| [Getting Started](getting-started.md) | Prerequisites, clone, build, first session with the Breadboard6502 |
| [Monitor Reference](monitor-reference.md) | Every REPL command with syntax, examples, and output formats |
| [Breadboard6502](breadboard6502.md) | Memory map, UART register reference, demo ROM listing |
| [Building Machines](building-machines.md) | Composing your own machine with MachineBuilder |
| [The JIT Tier](jit.md) | Tier 1 (IL-JIT): enabling it, chaining, the accuracy contract, troubleshooting |
| [Benchmarks](benchmarks.md) | The comparative cross-language benchmark suite — running it, reading the report, adding a subject |
| [Adding a CPU](adding-a-cpu.md) | The spec-table workflow, importer, generated artifacts |
| [Extraction Runbook](extraction-runbook.md) | LLM-assisted opcode extraction from CPU datasheets, cross-source diff, review report |
| [Testing](testing.md) | Running the suite, TomHarte vectors, Klaus, UAT sessions |

## Architecture support status

| CPU | Interpreter (Tier 0) | JIT (Tier 1) | Monitor host | Validation |
|---|---|---|---|---|
| **MOS 6502** | ✅ full ISA | ✅ full ISA | ✅ Breadboard6502 | TomHarte 1,510,000 cases + Klaus (96,241,367 cycles), both tiers |
| **Zilog Z80** | 🟡 base plane (248 opcodes) | ⬜ planned (M3.5) | ⬜ not yet | TomHarte 248,000 cases (base plane), interpreter |

The Z80 base-plane interpreter is the framework's first non-6502 execution. The CB/ED/DD/FD prefix planes (bit/shift/block ops, IX/IY indexing, the remaining interrupt modes) are in progress; see [Testing](testing.md#z80-tomharte-single-step-vectors) for exactly what is covered today.

## Quick links

- Project README: [`../../README.md`](../../README.md)
- Architecture spec: [`../superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`](../superpowers/specs/2026-06-11-cpu-emulator-framework-design.md)
- Research notes: [`../research/emulation-framework-research.md`](../research/emulation-framework-research.md)
