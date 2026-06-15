# Testing

CpuEmulator has a layered test suite. The base suite requires no external data and runs in seconds. Optional external vector sets unlock the full accuracy sweep.

---

## Running the suite

```
dotnet test
```

Expected with the external vectors fetched: `Passed! - Failed: 0, Passed: 994, Skipped: 0, Total: 994`

On a fresh clone, before fetching vectors: `Passed: 840, Skipped: 4, Total: 844` — the 4 skips are the vector-gated tests (the sampled TomHarte theory, Klaus functional, and the two Klaus UAT sessions), and the TomHarte theory expands to one row per opcode (+150) once vectors are present.

Tests that require external vectors skip cleanly with a message when the vectors are absent. No test fails due to missing vectors — it either passes or skips.

---

## Test layers

### Unit and integration tests

These always run:

- **Core contracts** (`AddressSpaceMemoryTests`, `AddressSpacePeripheralTests`, `AddressSpacePolicyTests`, `MachineBuilderTests`, `MachineRunTests`, `CycleSchedulerTests`, `InterruptLineTests`, `PeekTests`)
- **Roslyn generator** (`GeneratorHappyPathTests`, `GeneratorTestHost`, `PipelineHygieneTests`, `InstructionParsingTests`, `RegisterParsingTests`, `ModeOpValidationTests`, `DisassemblerEmissionTests`, `MonitorSupportEmissionTests`)
- **Spec importer** (`OpcodeDatasetTests`, `SemanticsMapTests`, `SpecFileEmitterTests`, `ImporterEndToEndTests`, `RegeneratedSpecTests`)
- **MOS 6502** (`Mos6502AluTests`, `Mos6502ProgramTests`, `Mos6502TraceTests`, `Mos6502IndexedTraceTests`, `Mos6502IndirectTraceTests`, `Mos6502RmwTraceTests`, `Mos6502StackFlowTraceTests`, `Mos6502BrkRtiTraceTests`, `Mos6502InterruptTests`, `Mos6502SkeletonTests`, `Mos6502MonitorSupportTests`)
- **Assembler/disassembler** (`AssemblerRoundtripTests` — 151-opcode roundtrip keystone)
- **Monitor** (`MonitorEngineTests`, `MonitorEngineExecutionTests`, `MonitorReplTests`, `MonitorRunDelegateTests`, `MonitorPeekTests`)
- **Peripherals** (`SimpleUartTests`, `IntervalTimerTests`)
- **Host** (`Breadboard6502Tests`, `DemoRomTests`, `HostOptionsTests`, `TerminalSessionTests`)

### UAT sessions (Category=UAT)

End-to-end scenarios that drive a real machine through the monitor REPL (or, for the
terminal session, the raw-keystroke terminal loop). Run them with:

```
dotnet test --filter "Category=UAT"
```

UAT tests that require Klaus vectors skip if Klaus is not present.

Currently 8 sessions:

| Test class | Sessions |
|---|---|
| `MonitorUatTests` | Countdown program (assemble/run/inspect/disassemble, 34-cycle exact result); Klaus-via-monitor (1M-cycle slice) |
| `HostUatTests` | Demo-hello exact transcript; echo session (inject + echo exact equality); Klaus-through-the-host smoke |
| `DeviceIrqUatTests` | Interrupt-driven echo (UART rx-IRQ, RAM-vector `IrqBoard`, WAI-free spin — exact `"HI"` round-trip); timer-IRQ counting (repeat timer at 64 cycles, handler counts 5 fires into `$10`, run-until-park) |
| `TerminalSessionTests` | Terminal session (demo hello + typed-key echo over the injectable console, Ctrl-] exit — byte-exact) |

### TomHarte single-step vectors

Cycle-accurate per-opcode validation against the [SingleStepTests/ProcessorTests](https://github.com/SingleStepTests/ProcessorTests) corpus. Each vector provides: initial CPU state, initial memory state, one instruction execution, expected final state + cycle count + per-cycle bus trace.

By default, 200 cases per opcode (30,200 total for the 151 documented 6502 opcodes). The full sweep runs all 10,000 cases per opcode (1,510,000 total).

#### Fetch vectors

```
# Windows
pwsh tools/get-test-vectors.ps1

# Linux/macOS
bash tools/get-test-vectors.sh
```

Vectors are stored in `~/.cache/cpuemulator/vectors/` by default. Override with the `CPUEMULATOR_TESTVECTORS` environment variable.

#### Run sampled (default 200/opcode)

```
dotnet test --filter "FullyQualifiedName~TomHarte"
```

#### Run full sweep (10,000/opcode — 1,510,000 cases)

```
# Windows PowerShell
$env:CPUEMULATOR_UAT = "full"
dotnet test --filter "FullyQualifiedName~TomHarte"
Remove-Item Env:\CPUEMULATOR_UAT

# Linux/macOS bash
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~TomHarte"
```

A passing full sweep: 151 opcodes × 10,000 cases = **1,510,000 cases, zero skips, zero failures**.

Custom sample size (e.g. 500 cases/opcode):

```
CPUEMULATOR_TOMHARTE_SAMPLE=500 dotnet test --filter "FullyQualifiedName~TomHarte"
```

#### What TomHarte validates

For every case: sets CPU registers and memory to the initial state, steps one instruction, compares the final register state, cycle count, and the per-cycle bus trace (address, value, read/write) to the expected values. A failure prints the mnemonic, disassembly, and a cycle-by-cycle expected/actual table.

The 6502 is validated per-cycle against the full corpus including: BCD-mode ADC/SBC, dummy reads/writes, the JMP (`$xxFF`) page-wrap bug, BRK/RTI, IRQ/NMI servicing (7-cycle sequence including two dummy reads at PC), phantom P bits.

### Klaus Dörmann functional test

The Klaus test exercises the 6502 instruction set with a 64 KiB self-modifying test program that walks through hundreds of test sub-sequences. A passing run reaches the success trap (`JMP *` at `$3469`).

#### Fetch Klaus

```
# Windows
pwsh tools/get-klaus.ps1

# Linux/macOS
bash tools/get-klaus.sh
```

#### Run

```
dotnet test --filter "FullyQualifiedName~KlausFunctionalTests"
```

Expected output (from the test's `ITestOutputHelper`):

```
success trap reached after 96241367 cycles
```

A passing run needs approximately 96,241,367 cycles. Any deviation from the expected cycle count is a STOP — the interpreter did not change without a corresponding test change.

#### What Klaus validates

Integration-level correctness: all ALU operations, addressing modes, branch taken/not-taken, stack operations, subroutine call/return, interrupt handling, and decimal mode. The test self-modifies RAM and self-tests; it detects misimplemented instructions by failing at an error trap before `$3469`.

### Z80 TomHarte single-step vectors

The Z80 is the framework's second architecture. It uses a **separate** vector corpus — [SingleStepTests/z80](https://github.com/SingleStepTests/z80) — with a distinct schema from the 6502 set: 1000 cases per file, packed alt-register pairs (`af_`/`bc_`/`de_`/`hl_`), the `i`/`r`/`wz`/`iff1`/`iff2`/`im`/`p`/`q` state, a separate `ports` array for I/O, and per-T-state (not per-machine-cycle) bus signals.

**Current coverage — base plane + CB plane.** As of M3.4b, the **252 covered base-plane opcodes** (the un-prefixed instructions, now including the four rotate-accumulators 07/0F/17/1F) **and all 256 0xCB-prefix opcodes** (RLC/RRC/RL/RR/SLA/SRA/SLL/SRL, BIT, RES, SET — on registers and (HL)) pass the full sweep:

> base 252 × 1000 + CB 256 × 1000 = **508,000 cases, zero failures** — including F's undocumented X/Y bits (3 and 5, W-sourced for `BIT n,(HL)`), the WZ/MEMPTR and Q internal registers, the per-T-state bus-trace ordering, and (for base I/O ops) the ports array.

Not yet implemented (tracked on the genericity ladder): the **ED prefix plane** (block ops, 16-bit ADC/SBC, IM 0/1/2, NMI/IFF, RETI/RETN, RRD/RLD, (C) I/O — M3.4c), the **DD/FD/DDCB/FDCB planes** (IX/IY indexing — M3.4d), the **Z80 JIT tier** (M3.5), and a **Z80 monitor host** (no Z80 REPL machine ships yet — the host boots the Breadboard6502).

#### Fetch Z80 vectors

```
# Windows
pwsh tools/get-test-vectors-z80.ps1

# Linux/macOS
bash tools/get-test-vectors-z80.sh
```

They cache under `$TESTVECTORS/z80/v1/`.

#### Run

```
# Sampled (default)
dotnet test --filter "FullyQualifiedName~Z80TomHarte"

# Full base + CB sweep (1000/opcode = 508,000 cases)
#   Windows PowerShell
$env:CPUEMULATOR_UAT = "full"; dotnet test --filter "FullyQualifiedName~Z80TomHarte"; Remove-Item Env:\CPUEMULATOR_UAT
#   Linux/macOS bash
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarte"
```

A registers-only subset (skips the bus-trace/ports diff) is available via `CPUEMULATOR_Z80_REGS_ONLY=1` for fast triage; the merge gate always runs the full diff.

### 68000 TomHarte single-step vectors (loader infrastructure — M4.4b)

The 68000 is the framework's third architecture. It uses a **third** vector corpus —
[SingleStepTests/680x0](https://github.com/SingleStepTests/680x0) — structurally distinct from both the
6502 and Z80 sets on three axes:

- **gzip-compressed** files (`*.json.gz`) — the loader gunzips with `GZipStream` before parsing;
- **mnemonic + size-keyed** filenames (`ADD.b.json.gz`, `ABCD.json.gz`) — the filename is the disassembly,
  not the opcode hex; 124 files, several thousand cases each (e.g. `ADD.b` has 8065);
- a per-case schema carrying the **2-word prefetch queue** (`prefetch: [w0, w1]`, in both `initial` and
  `final`), the **separate `usp`/`ssp`** (never `a7`), the 16-bit `sr`, the 32-bit `d0..d7`/`a0..a6`/`pc`,
  the `ram` `[addr, value]` pairs, a top-level `length` (total instruction cycles), and a word-granular
  `transactions` array.

The `transactions` tuples come in two shapes (pinned against the live upstream repo):

| Shape | Meaning |
|---|---|
| `["n", cycles]` | an idle / internal slot — no bus access |
| `[dir, cycles, fc, addr, sizeTag, value]` | a bus access — `dir` is `"r"`/`"w"`, `fc` is the function code (5 = supervisor data, 6 = supervisor program), `sizeTag` is `.b`/`.w` (the 68000 bus is 16-bit, so a `.l` access decomposes into two `.w` transactions — no `.l` at the bus level) |

In both shapes **field 2 is the per-slot cycle count** — the case's top-level `length` equals the sum of
field 2 across its transactions (confirmed against the live data; this resolves the ADR 0004 §5 "field 2
unconfirmed" flag).

> **State as of M4.4b: the loader PARSES; no opcode executes yet.** M4.4b ships the gzip + mnemonic-keyed
> loader, the `680x0/v1` cache resolver, the skip-when-absent theory attribute, the fetch script, a
> committed gzip fixture (an always-on parse proof needing no download), a skip-gated real-file theory, and
> a runner **scaffold** that sets the full initial state on a fresh `M68000Cpu` over a tracing wide
> big-endian bus and returns a `NotYetExecuted` sentinel. The op bodies, the prefetch-queue mechanism, and
> the Step-and-diff (registers + ram + per-transaction bus trace + the final prefetch queue) are **M4.5**.

#### Fetch 68000 vectors

```
# Windows
pwsh tools/get-test-vectors-68000.ps1
```

The script sparse-checks-out the upstream `68000/v1/` tree and caches it under `$TESTVECTORS/680x0/v1/`
(matching the resolver). It is idempotent and `$LASTEXITCODE`-checked.

#### Run

```
# The committed gzip fixture parse proof always runs (no vectors needed):
dotnet test --filter "FullyQualifiedName~M68000TomHarteLoaderTests"

# The skip-gated real-file theory runs once vectors are fetched:
dotnet test --filter "FullyQualifiedName~Loads_one_real_vector_file_when_present"
```

---

## Pre-merge gate checklist

Before merging a PR that touches the CPU interpreter or the generator:

```
# 1. Fetch vectors (if not cached)
pwsh tools/get-test-vectors.ps1
pwsh tools/get-klaus.ps1

# 2. Build — 0 warnings required
dotnet build --no-incremental

# 3. Full suite
dotnet test

# 4. Full TomHarte sweep (1,510,000 cases)
$env:CPUEMULATOR_UAT = "full"
dotnet test --filter "FullyQualifiedName~TomHarte"
Remove-Item Env:\CPUEMULATOR_UAT

# 5. Klaus
dotnet test --filter "FullyQualifiedName~KlausFunctionalTests"

# 6. UAT sessions
dotnet test --filter "Category=UAT"
```

The PR body must include the total TomHarte case count (must equal 1,510,000), the Klaus cycle count (expected 96,241,367), and the UAT pass count.

---

## Vector cache location

| Environment variable | Default | Contents |
|---|---|---|
| `CPUEMULATOR_TESTVECTORS` | `~/.cache/cpuemulator/vectors` | All vectors |
| *(none)* | `$TESTVECTORS/6502/v1/` | TomHarte 6502 JSON files (one per opcode hex) |
| *(none)* | `$TESTVECTORS/z80/v1/` | TomHarte Z80 JSON files (separate corpus, distinct schema) |
| *(none)* | `$TESTVECTORS/680x0/v1/` | TomHarte 68000 gzip files (`*.json.gz`, mnemonic+size-keyed; M4.4b loader parses, M4.5 executes) |
| *(none)* | `$TESTVECTORS/klaus/6502_functional_test.bin` | Klaus 64 KiB binary |

Vectors are never vendored into the repository. The fetch scripts download and cache them on demand; they are safe to re-run (idempotent).
