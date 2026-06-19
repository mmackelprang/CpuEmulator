# The JIT Tier (Tier 1)

The emulator has two execution tiers that share one source of truth (the generated CPU spec):

- **Tier 0 — the interpreter** (e.g. `Mos6502Cpu`, `Z80Cpu`, `M68000Cpu`, `M8086Cpu`): cycle-exact,
  always available, AOT-compatible. It is the correctness oracle and the only tier validated per-cycle
  against the full TomHarte sweep.
- **Tier 1 — the IL-JIT** (`JittedCpu`, in `CpuEmulator.Jit`): a speed path that translates guest
  machine code into .NET IL at runtime and lets RyuJIT compile it to native. It *wraps* the
  interpreter; the interpreter remains the oracle, the fallback, and the owner of all
  architectural state.

The JIT is **CPU-agnostic** — the same `JittedCpu` + `BlockCompiler` drives all four CPUs (6502, Z80,
68000, 8086) over a generated per-opcode descriptor table; the per-CPU IL is in hand-written emit arms
(`BlockCompiler.<Cpu>.cs`). This page uses the 6502 as the worked example, then summarizes the M6
per-CPU emit arms; the 6502 was the first CPU to emit IL (M2) and remains the most complete.

This page covers what the JIT is, how to run a machine on it, **block chaining** (the M2-ii speedup),
the accuracy contract that defines what "parity" means for Tier 1, the fallback caveat (which ops emit
vs interpret), the **M6 per-CPU emit arms** (Z80/68000/8086), benchmarks, and troubleshooting.

Source: `src/CpuEmulator.Jit/`

---

## What it is

`JittedCpu` is an `ICpuCore` + `IMonitorSupport` wrapper around a real `Mos6502Cpu`. The wrapped
interpreter owns the architectural state (`A/X/Y/S/P/PC`, the cycle counter); the JIT discovers
straight-line **blocks** of guest code, compiles each block once into a `DynamicMethod`, caches it
keyed by entry PC, and re-executes the cached native code on subsequent visits. Reads and writes to
RAM/ROM use a **fastmem** fast path (direct access to the page's backing array); accesses to
memory-mapped I/O fall back to a bus callout so devices still see them.

Three properties make the tier trustworthy:

- **The interpreter is the oracle.** Any instruction the compiler does not emit (see the fallback
  caveat below) runs as a callout to the interpreter's `Step()`, so correctness never depends on
  the JIT being clever.
- **One spec drives both tiers.** The block compiler walks a generated per-opcode descriptor table
  (`Mos6502Cpu.JitDescriptors`) produced from the same spec the interpreter, disassembler, and
  assembler are generated from — so the JIT's instruction lengths and cycle counts cannot drift
  from the interpreter's.
- **`Step()` always delegates to the interpreter.** Single-instruction stepping (the monitor's
  primitive, the TomHarte harness's primitive) demands exact per-instruction fidelity, which is the
  interpreter's job. Blocks earn their keep in `Run`.

---

## How to enable it on a machine

The JIT is **opt-in**. No shipped machine uses it by default in M2-i — `Breadboard6502` and every
existing board stay interpreter-only. You enable it by having your machine's CPU factory return a
`JittedCpu` wrapping a `Mos6502Cpu`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

var machine = Machine.Create("jit-board")
    .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
    .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
    .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x0100, uart)
    .WithRom(AddressSpaceKind.Program, 0xE000, romImage)
    .WithCpu(ctx =>
    {
        // Fastmem binds to the concrete AddressSpace (the page table + backing arrays).
        // ctx.Space returns IAddressSpace, but its runtime type is the concrete AddressSpace.
        var space = (AddressSpace)ctx.Space(AddressSpaceKind.Program);
        return new JittedCpu(new Mos6502Cpu(space), space);
    })
    .Build();
```

The `JittedCpu` is now the machine's `Cpu`, so `Machine.Run`, the `MonitorEngine`, and the REPL drive
it identically to the interpreter. A documented host flag to select the tier from the command line
is planned for a later milestone (M2-ii+); for now the selection is the `WithCpu` factory.

`JittedCpu` binds to a **concrete `AddressSpace`**, not the `IAddressSpace` interface, because
fastmem needs the page table's backing `byte[]` and writability. Constructing a `JittedCpu` over a
non-`AddressSpace` bus is a construction error.

---

## Block chaining (M2-ii — the speedup)

A compiled block whose exit target PC is **statically known at compile time** does not return to the
dispatcher; it transfers control directly to its successor block. The statically-known exits:

| Block-ending opcode | Successor PC | Chains? |
|---|---|---|
| `JMP abs` | the absolute operand (constant) | yes |
| `Bxx` taken | `PC_after_operand + offset` (constant) | yes (both arms) |
| `Bxx` untaken | `PC_after_operand` (constant) | yes (both arms) |
| fall-through past the 64-instruction block cap | `lastPc + lastLen` (constant) | yes |
| `JSR abs` | the absolute operand (constant) | yes |
| `RTS`, `JMP (ind)`, a `BRK`/`RTI`/undefined fallback | dynamic (run-time) | no — returns to the dispatcher |

This removes most dispatcher round-trips on the hot path — a tight branch-dominated loop runs
block→block with no return to the dispatch loop. Chaining is **automatic and transparent**: no API
change, and it changes only the transfer of control, never the cycle charge (a chained run and a
`DisableChaining` run reach identical cycle counts).

**Stack safety.** The chain transfer is realized as a loop in `JittedCpu`, not emitted recursion —
the emitted block's chain edge records the resolved successor and returns; a `JittedCpu`-side loop
runs the successor in the same call frame. So an arbitrarily long chain (e.g. Klaus's 96M-cycle run)
runs at bounded host-stack depth.

**SMC safety (the chaining-vs-self-modifying-code rule).** A chain transition proceeds ONLY from a
clean block end with no outstanding dirty marks. Two mechanisms guard it: (1) the intra-block SMC
guard exits with a distinct `BlockExit.Recompile` (the precise signal that this block patched one of
its own pages) — never chainable; and (2) every chain edge is gated on `!Dirty.Any` (the coarse
backstop for a store to a *different* block's code page). Any SMC activity forces a dispatcher
round-trip, where the per-page block index evicts only the dirtied pages' blocks and severs their
inbound chain links, then re-decodes the modified bytes. The committed differential fuzzer runs
chaining-on AND chaining-off — and, since M6 PR-S, the SMC/recompile-cost lever on AND off — and
asserts all match the interpreter, so neither chaining nor the lever can silently defeat the SMC guard.
A PC that thrashes this round-trip past the recompile cap is routed through the interpreter for a
cooldown window (see *Benchmarks → the SMC/recompile-cost lever*); the interpreter still dirty-marks
its own SMC stores, so the cooldown changes only which tier runs the PC, never whether SMC is observed.

To disable chaining (for isolating a suspected chaining bug, or the M2-i one-block-per-dispatch
behavior), construct with `new JitOptions { DisableChaining = true }`.

---

## The accuracy contract vs the interpreter

The JIT is **block-accurate**: it promises the interpreter's final state and total cycle count, not
the interpreter's per-cycle bus-transaction order. The precise contract:

| Dimension | Interpreter (Tier 0) | JIT (Tier 1), fastmem ON (default) | JIT, fastmem OFF (`DisableFastmem`) |
|---|---|---|---|
| Final register/flag state | cycle-exact | **identical** | identical |
| `CycleCount` total | cycle-exact (per bus transaction) | **identical** (same cycle templates + page-cross + interpreter fallbacks) | identical |
| Per-cycle bus trace order | exact (every read/write, dummy reads) | **NOT preserved for fastmem RAM/ROM** — direct array access emits no bus transaction; MMIO accesses DO hit the bus in order | **data accesses preserved** — every operand fetch, memory read/write, and RMW dummy write routes through the bus, in order; opcode-fetch reads are resolved at compile time (see note) |
| Interrupt latency | instruction-boundary | **block-boundary** (checked at block entry; documented) | block-boundary |

**The contract, in one sentence:** *JIT parity is state + cycle-count equivalence; it is NOT
bus-trace equivalence while fastmem is on, because fastmem RAM/ROM access deliberately bypasses the
bus for speed.*

Bus-trace equivalence is available on demand as a **mode**, not a tier promise. Construct the
`JittedCpu` with `JitOptions { DisableFastmem = true }`, which routes every access through the bus
and restores an identical bus trace to the interpreter's:

```csharp
var jit = new JittedCpu(inner, space, new JitOptions { DisableFastmem = true }, traceBus: tracingBus);
```

This is the mode the trace spot tests use to prove the equivalence, and the mode to reach for when
you want a bus trace out of the JIT for debugging.

**One nuance in trace mode (the opcode-fetch reads):** even under `DisableFastmem`, the JIT does
not reproduce the interpreter's *opcode-fetch* bus reads — the instruction's first byte. The JIT
reads the opcode stream once at compile time (block discovery) and bakes it into the emitted IL;
the executing block never re-fetches opcodes at run time. So a `DisableFastmem` trace contains every
operand fetch, every effective-address read/write, and every silicon-true RMW dummy write — matching
the interpreter element-for-element — but omits the per-instruction opcode fetch. This is inherent to
compilation (re-fetching every opcode per execution would defeat the tier) and is the precise reading
of "every access routes through the bus": every *data/operand* access does. The trace spot tests pin
exactly this (the interpreter trace minus its opcode fetches equals the JIT trace).

**Interrupt latency is block-boundary.** The JIT checks for a pending interrupt at each block entry
(serviced by the inner interpreter's authentic 7-cycle sequence), and **chaining samples
`InterruptPending` at each chain edge** — so a pending interrupt breaks the chain back to the
dispatcher, and latency is bounded by the chained-block length, the same per-block bound as M2-i.
Under tight cycle budgets — the monitor's single-instruction stepping, or `Machine.Run` chunked to
the next scheduled event — block-boundary collapses to instruction-boundary, so device-driven IRQ
timing is unaffected in practice.

---

## The interpreter-fallback caveat (M2-ii)

The block compiler emits IL for nearly all opcodes, including **`ADC` / `SBC` in both the binary and
decimal arms** (M2-ii emits the NMOS BCD nibble-correction logic behind an emitted `if ((P & 0x08)
!= 0)`, byte-for-byte matching the interpreter — see the decimal flag quirks: ADC's Z comes from the
binary sum, N/V from the pre-correction sum; SBC's flags are all the binary-path flags, only the
result byte is BCD-corrected). The 80,093 decimal-mode TomHarte cases pass through the JIT by EMIT,
not fallback. ADC/SBC no longer end a block, so blocks are longer and chaining has more straight-line
runs to link.

Only three classes still emit an interpreter-`Step` fallback (and end the block there):

- **`BRK` / `RTI`** — they touch the interrupt/vector machinery (the 7-cycle push sequence, vector
  fetch) the interpreter owns in one place; interrupt-rare, so emitting them buys ~0 throughput and
  risks the highest-cost parity hole. Recorded decision: they stay fallbacks.
- **Undefined opcodes** — the interpreter owns the per-machine undefined-opcode policy.

A fallback runs one authentic interpreter `Step` (advancing PC and cycles exactly), so correctness
is identical whether an instruction is emitted or fallen-back. A fallback exit is a **dynamic** PC,
so it is never a chainable target — it always returns to the dispatcher.

---

## The M6 per-CPU emit arms (Z80 / 68000 / 8086)

M2 proved the dual-tier path on the 6502; **M6 generalized it.** Three more CPUs now emit IL for their
high-ROI op families, each gated on byte-identical TomHarte-through-JIT parity. The shared machinery —
the block scaffold (opcode-fetch cycle, PC increment, chain edge + its three gates, the SMC guard, the
budget check), the fastmem operand split, the data-driven register-file access — is **CPU-agnostic** and
reused unchanged; the per-CPU content is the **flag/cycle model**, hand-written in `BlockCompiler.<Cpu>.cs`
to mirror that CPU's generated interpreter `Step` body one-for-one. Each CPU's rare/exception/microcoded
tail stays interpreter-fallback **by design** — a fallback op is always exactly the oracle, so partial
emit is a pure performance dial with no correctness risk.

| CPU | Emit arm | Families emitted | Stays fallback (by design) | Notable seam |
|---|---|---|---|---|
| **Z80** | `BlockCompiler.Z80.cs` | LD; ALU + flags (the **Q/MEMPTR** lifecycle + undocumented **X/Y** bits); ED 16-bit (`ADC`/`SBC HL,rr`, `INC`/`DEC rr`); branch/call/stack | the `ED`/`DD`/`FD`/`CB` prefix-plane long tail (block ops `LDIR`/`CPIR`, the rarities) | the wide-register helper for AF/BC/DE/HL/IX/IY pair-views (PR-0); the Z80 T-state cycle model. The Z80 JIT now **exceeds its own interpreter** on the W2 kernel. |
| **68000** | `BlockCompiler.M68000.cs` | MOVE (the only **net-new descriptor generation** in M6 — required a **word-granular `Discover`** fetch-stream fix); ALU + CCR (the **X-bit**, distinct from C); shifts; branch/`DBcc` | `TRAP`/`TRAPV`/`CHK`/÷0/`ILLEGAL`/`MOVEM`/`MUL`/`DIV`/`RTE`/`LINK`/`UNLK`, address-error, privilege | data-axis-exact (coarse-cycle by design, ADR 0011 DECISION T / ADR 0008 §6); `-(A7)`/`(A7)+` stack push/pop is the interpreter's `ReadLongBus`/`WriteLongBus`. |
| **8086** | `BlockCompiler.M8086.cs` | MOV (over the **`(CS<<4)+IP` segmentation seam**); ALU + FLAGS (the **AF/PF** wrinkle); near branch (`Jcc`/`JMP`/`CALL`/`RET`/`LOOP`, near `FF`-indirect) | **far flow** (CS-invariant block-cache key); `MUL`/`DIV`, `REP MOVS/STOS/CMPS/SCAS` string loops, `INT`/`INTO`/`IRET`/`BOUND`, the divide-error INT0, `IN`/`OUT` | the segmented EA resolver (`ModR/M` over a segment + offset); the variable-length decode. Far flow stays fallback because the block-cache key is `(IP)` not `(CS,IP)`. |

Two cross-CPU notes the arms rely on:

- **The `Discover` granularity distinction.** The 6502/Z80/8086 fetch the instruction stream
  **byte-granular**; the 68000's field-grammar decode is **word-granular** (16-bit operwords,
  big-endian). The 68000 emit arm required teaching block discovery to walk the fetch stream a word at a
  time (PR-4a) before its MOVE arm could even dispatch.
- **The descriptor gate.** Enabling a Z80 or 8086 family is *un-forcing* a generator gate (their
  descriptor tables are populated-but-forced-fallback); the 68000 needed its `JitDescriptorsByKey`
  *populated* from the field-grammar (net-new generation) — the one bigger generator change in M6.

For the design rationale (the emit-vs-fallback boundary, the per-CPU rollout order, the shared-vs-per-CPU
split, and the profiling-ranked ROI), see **ADR 0011** (`docs/architecture/0011-jit-hot-op-emission-optimization.md`).

---

## Benchmarks

The comparative cross-language benchmark suite (`bench/`) measures emulated cycles per host-second
across the two tiers and, opt-in, third-party 6502 emulators. The Klaus functional test under the
JIT (chaining on, decimal arms emitted) reaches its `$3469` success trap at the exact interpreter
cycle count (96,241,367) and is **dramatically faster than the M2-i fallback + dispatcher-round-trip
path** (M2-i: ~40.9 min; M2-ii: well under two minutes — a large multiple over the prior JIT).

**The SMC/recompile-cost lever (M6 PR-S).** The original headline was that the Tier-1 JIT was *slower*
than the Tier-0 interpreter on the SMC-heavy Klaus run, because the per-dispatch `InvalidateIfDirty`
**thrashed** — Klaus writes test-vector bytes into code-adjacent pages on nearly every iteration, so
the hot block was evicted and **recompiled per dispatch**, and `Compile()` (a discover walk + an IL
emit) costs far more than the interpreter's own `Step()`. PR-S adds a **recompile-cost cap with an
interpreter cooldown**: the block cache tracks per-PC recompiles, and once a PC recompiles past
`SmcRecompileCap` (the thrash signature) the dispatcher stops re-JITing it and runs it through the
interpreter oracle (`inner.Step`, the same byte-exact fallback) for `SmcCooldownDispatches` dispatches,
then re-arms the JIT. This is a pure **performance policy** — the cooldown path is the interpreter, so
the architectural result and the exact cycle count are unchanged (the differential fuzzer runs the lever
ON *and* OFF and asserts both match the interpreter; the Klaus anchor stays 96,241,367). Over a bounded
5M-cycle Klaus window the lever collapses recompiles by **several-fold** and the wall-clock improves
correspondingly; on SMC-free workloads (the arithmetic kernel, the sieve) no PC ever recompiles, so the
lever never trips and those runs are byte-identical to before. The JIT's M2 value remains **correctness
parity** (the full TomHarte sweep through the JIT, the committed differential fuzzer, Klaus cycle-exact);
PR-S removes the SMC pathology that was the recorded next optimization. The full W1/W2/W3 throughput
re-capture is the arc-end benchmark. See the report for the numbers, the cross-language spread, and the
full analysis.

Tune or disable the lever via `JitOptions`: `SmcRecompileCap` (default 16), `SmcCooldownDispatches`
(default 256), and `DisableSmcLever` (default false — the lever is on).

For the full methodology, the JIT-vs-interpreter table, and the cross-language numbers, see
[`bench/README.md`](../../bench/README.md) and the regenerated
[`bench/results/REPORT.md`](../../bench/results/REPORT.md), or the
[Benchmarks user-guide page](benchmarks.md).

Regenerate the report:

```sh
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report --all
```

---

## Troubleshooting

- **`PlatformNotSupportedException` at `JittedCpu` construction.** The JIT requires a runtime JIT
  (`RuntimeFeature.IsDynamicCodeSupported`). Under NativeAOT — or any dynamic-code-disabled process —
  that feature is false and the gate throws with guidance to use the interpreter (`Mos6502Cpu`)
  directly. NativeAOT consumers never reference `CpuEmulator.Jit`; the interpreter is the AOT path.
- **A parity divergence (state or cycle count differs from the interpreter).** This is a **JIT bug**
  by definition — the interpreter is the oracle. File it with the program bytes + the diverging
  register/cycle. **To localize a suspected chaining bug, re-run with `JitOptions { DisableChaining =
  true }`:** a divergence that disappears with chaining off is in the chaining layer; one that
  persists is in the base emit. The committed differential fuzzer reproduces any divergence from the
  seed integer alone (`CPUEMULATOR_FUZZ=full` raises the seed count to the pre-merge gate of 4096);
  it runs chaining-on AND chaining-off for exactly this localization. To capture a per-cycle bus
  trace for the report, re-run with `JitOptions { DisableFastmem = true }` and a `TracingAddressSpace`
  to get the full ordered access list (which the default fastmem path does not produce for RAM/ROM).
- **SMC (self-modifying code) seems stale.** The JIT evicts only the dirtied pages' cached blocks
  (the per-page block index) and severs their inbound chain links when a RAM store dirties a code
  page, and ends the current block (exiting `BlockExit.Recompile`) when a store patches an opcode
  within that same block. A chain edge also breaks on any outstanding dirty mark. Both modes (fastmem
  on/off) track writable-RAM stores for invalidation. A genuine staleness would be a parity
  divergence — see above.

---

## The gate: `RuntimeFeature.IsDynamicCodeSupported`

`JittedCpu`'s constructor checks `System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported`
and throws `PlatformNotSupportedException` when it is false. This is the construction-seam gate that
keeps the AOT story enforced by construction: `CpuEmulator.Jit` is the only assembly that uses
`Reflection.Emit`, and it is the only non-AOT member of the build graph. The AOT-clean assemblies
(`Core`, `Cpus.Mos6502`, `Peripherals`, `Monitor`, `Host`) never reference it — a build-time check
(`AotCleanlinessTests`) pins that reference-graph law (it also asserts none of them reference the
dev-only `CpuEmulator.Benchmarks` bench tool, which references `Jit`).

### NativeAOT publishing the Host (the PublishAot CI scoping)

A NativeAOT publish of the Host **succeeds** because its runtime graph excludes `Jit`:

```sh
dotnet publish src/CpuEmulator.Host -c Release -r <rid>      # e.g. win-x64, linux-x64
```

`PublishAot` is set **in `CpuEmulator.Host.csproj`**, NOT passed as a global `-p:PublishAot=true` on
the command line. This is load-bearing: a global property propagates to *every* project in the build
graph — including the netstandard2.0 Roslyn analyzer `CpuEmulator.Generators` (referenced
transitively as an `Analyzer` with `ReferenceOutputAssembly="false"`). Analyzers inherently cannot
AOT-publish, so a global `PublishAot` fails with `NETSDK1207` on `Generators` even though the
analyzer is build-time-only and not in the runtime graph. Scoping the property to the Host csproj
keeps it off the analyzer's build. (On Windows the native-link step needs the MSVC linker on PATH —
run from a Developer prompt or with the VS Installer dir on PATH so the toolchain's `vswhere.exe`/
`link.exe` resolve.) The `AotCleanlinessTests` reference-graph check is the enforced-by-construction
backstop regardless of whether the full publish runs in a given environment.
