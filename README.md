# CpuEmulator

A **pluggable, multi-architecture CPU-emulation framework in modern C#**, using the .NET runtime's own JIT as a dynamic-recompilation backend (translate guest machine code → .NET IL at runtime → let RyuJIT compile to native), with an interpreter tier as the always-available correctness oracle.

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
`Breadboard6502` (52 KiB RAM, `SimpleUart` at $D000, 8 KiB demo ROM assembled at boot by
the generated assembler) and drops into the monitor REPL. Next: M2 (the IL-JIT tier).

For full detail see the [User Guide](docs/user-guide/README.md).

## User Guide

- [Getting Started](docs/user-guide/getting-started.md) — prerequisites, build, first session
- [Monitor Reference](docs/user-guide/monitor-reference.md) — every REPL command
- [Breadboard6502](docs/user-guide/breadboard6502.md) — memory map, UART, demo ROM
- [Building Machines](docs/user-guide/building-machines.md) — MachineBuilder, peripherals, monitor wiring
- [Adding a CPU](docs/user-guide/adding-a-cpu.md) — spec tables, importer, generated artifacts
- [Testing](docs/user-guide/testing.md) — suite, TomHarte vectors, Klaus, UAT sessions

## Architecture

The framework is a set of .NET 10 libraries. `CpuEmulator.Core` defines the contracts (`ICpuCore`, `IAddressSpace`, `IPeripheral`, `Machine`); `CpuEmulator.Generators` is a Roslyn source generator that reads a typed C# spec table and emits five artifacts at build time (state struct, interpreter, IL emitters, disassembler, assembler); `CpuEmulator.Cpus.Mos6502` holds the 6502 spec table (importer output) plus the hand-written partial; `CpuEmulator.Peripherals` ships `SimpleUart`; `CpuEmulator.Monitor` is the CPU-agnostic monitor engine + REPL; `CpuEmulator.Host` is the console entry point. All library projects are AOT-compatible; the future JIT tier (`CpuEmulator.Jit`) will be the only project that uses `Reflection.Emit`.

Full design: [`docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`](docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md)

## Development workflow

Build and test:

```
dotnet build    # 0 warnings required
dotnet test     # 848 tests
```

All work happens on short-lived feature branches; changes merge to `main` via pull request. See [Testing](docs/user-guide/testing.md) for the full pre-merge gate (TomHarte full sweep, Klaus, UAT sessions).

## License

TBD.
