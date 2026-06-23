# CpuEmulator User Guide

Welcome to the CpuEmulator user guide. Use these pages to get the emulator running, understand its components, and extend it with new machines and CPU architectures.

## Contents

| Document | What it covers |
|---|---|
| [Getting Started](getting-started.md) | Prerequisites, clone, build, first session with the Breadboard6502 |
| [Running the Machines](running-the-machines.md) | How to run every system we ship — console boards (6502/z80/68000/8086) + the web-surface systems (Spectrum, Apple ][+, SoftCard CP/M, CP/M 3.1 + Videx), plus the asset-setup scripts |
| [Monitor Reference](monitor-reference.md) | Every REPL command with syntax, examples, and output formats |
| [Breadboard6502](breadboard6502.md) | Memory map, UART register reference, demo ROM listing |
| [Building Machines](building-machines.md) | Composing your own machine with MachineBuilder |
| [The JIT Tier](jit.md) | Tier 1 (IL-JIT): enabling it, chaining, the accuracy contract, troubleshooting |
| [Benchmarks](benchmarks.md) | The comparative cross-language benchmark suite — running it, reading the report, adding a subject |
| [Adding a CPU](adding-a-cpu.md) | The spec-table workflow, importer, generated artifacts |
| [Extraction Runbook](extraction-runbook.md) | LLM-assisted opcode extraction from CPU datasheets, cross-source diff, review report |
| [Testing](testing.md) | Running the suite, TomHarte vectors, Klaus, UAT sessions |
| [Roadmap](../ROADMAP.md) | Shipped milestones (M1–M6) and the deferred/candidate follow-ons |

## Architecture support status

The framework ships **four cycle-exact interpreters** (Tier 0). Three of the four also emit IL through
the **Tier-1 JIT** for their high-ROI op families (M6); the rare/exception/microcoded tail of each ISA
stays interpreter-fallback **by design** (the interpreter is always the correctness oracle and the
fallback). JIT parity is gated on byte-identical TomHarte-through-JIT execution.

| CPU | Interpreter (Tier 0) | JIT (Tier 1) | Monitor host | Validation |
|---|---|---|---|---|
| **MOS 6502** | ✅ full ISA | ✅ full ISA emits (BRK/RTI/undefined fall back) + SMC/recompile lever | ✅ Breadboard6502 | TomHarte 1,510,000 cases + Klaus (96,241,367 cycles), both tiers |
| **Zilog Z80** | ✅ full ISA (base + CB/ED/DD/FD/DDCB/FDCB planes) | ✅ emits LD · ALU+flags (Q/MEMPTR, X/Y) · ED 16-bit · branch/call/stack (prefix-plane tail falls back) | ⬜ not yet | TomHarte sweep + ZEXALL/ZEXDOC, both tiers |
| **Motorola 68000** | ✅ full ISA (data-axis; coarse-cycle timing by design) | ✅ emits MOVE · ALU+CCR (X-bit) · shifts · branch/DBcc (TRAP/CHK/÷0/MOVEM/MUL/DIV/RTE/LINK/UNLK fall back) | ⬜ not yet | 680x0 corpus, data-axis-exact, both tiers |
| **Intel 8086/8088** | ✅ full ISA | ✅ emits MOV (segmentation seam) · ALU+FLAGS · near branch (far flow/MUL/DIV/string-REP/INT-IRET/IN-OUT fall back) | ⬜ not yet | 8088 TomHarte corpus, both tiers |

Legend: ✅ shipped · ⬜ not yet. The interpreter remains the oracle for every CPU; "falls back" means
the JIT routes that op through the interpreter `Step` (byte-exact), not that it is unimplemented. The
deferred emit follow-ons (8086 far-flow, MUL/DIV, string/REP, INT/IRET; cycle-exact emitted 68000
timing) are tracked in the [Roadmap](../ROADMAP.md). See [The JIT Tier](jit.md) for the emit arms and
[Testing](testing.md) for exactly what is covered today.

## Quick links

- Project README: [`../../README.md`](../../README.md)
- License: [`../../LICENSE`](../../LICENSE) — MIT
- Architecture spec: [`../superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`](../superpowers/specs/2026-06-11-cpu-emulator-framework-design.md)
- Research notes: [`../research/emulation-framework-research.md`](../research/emulation-framework-research.md)
