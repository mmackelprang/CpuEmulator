# CpuEmulator comparative benchmark suite

The comparative cross-language benchmark deliverable (design spec §9 item 9). It measures
**emulated CPU cycles per host wall-clock second** across our two tiers — the Tier-0 interpreter and
the Tier-1 IL-JIT — and, opt-in, third-party 6502 emulators in C#, C, Python, and JavaScript, behind
thin adapter shims that **skip-with-a-note when their runtime is absent**. The committed report ships
with whatever ran in the generating environment plus instructions to populate the rest.

> **Honesty first.** Our two tiers are in-process C# and always run. Third-party subjects are opt-in
> and degrade gracefully — an absent runtime is a skip-with-note, never a crash and never a fabricated
> number. The report commits **only measured data**.

## Quick start

```sh
# Our two tiers always run (in-process C#) + regenerate the report:
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report

# Add the third-party subjects (fetch their runtimes first):
bench/third-party/fetch-subjects.ps1        # or .sh — fetches fake6502.c+.h, a py65 venv, sfotty
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report --all

# Statistically-rigorous numbers on our two tiers (BenchmarkDotNet warmup/measurement windows):
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --bdn

# The two M2 revisit-gate micro-benches (Task 9 — dispatch + state-layout):
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --gates
```

The generated report is [`results/REPORT.md`](results/REPORT.md).

## Methodology

| Dimension | Rule |
|---|---|
| **Metric** | Emulated CPU cycles per host wall-clock second (cycles/sec). The absolute wall-clock for the run is reported too, so a reader can sanity-check. |
| **Workloads** | **W1 (Klaus-deterministic):** the Klaus 6502 functional-test image run to its `$3469` success trap — 96,241,367 cycles of identical work, the integration-realistic mix (loads/stores/branches/ADC/SBC/SMC). Loaded from the shared vector cache (NOT vendored — the TomHarte-vector pattern); absent ⇒ W1 is skipped. **W2 (arithmetic kernel):** a tight, hand-written ADC/SBC + branch loop committed as a `byte[]` in `Workloads.cs`, run for a fixed cycle window — ADC/SBC + branch heavy, isolating the decimal-arm + chaining payoff from the I/O-free hot path. |
| **Subjects** | **(a) our Tier-0 interpreter, (b) our Tier-1 JIT (chaining on)** — always run. **(c) third-party:** Asm6502 (C#, in-process), fake6502 (C, compiled shim), py65 (Python subprocess), sfotty (JS via node) — opt-in, behind adapter shims, skipped-with-note when absent. |
| **Harness for our tiers** | **BenchmarkDotNet** (`--bdn`): its warmup + measurement windows, statistical reporting, and environment capture (CPU, OS, .NET) are the methodology. A lighter warmed-`Stopwatch` pass (`--report`) produces the headline report rows + the smoke-test check. |
| **Harness for third-party** | Each adapter runs the SAME workload image, measures the same metric in the subject's natural runner (in-process for C#, a compiled-shim subprocess for C, a python/node subprocess for the scripting subjects), with a warmup pass excluded from the measured window. |
| **Environment metadata** | The report header records the host (CPU, core count, OS, .NET version) and, per third-party subject, its detected version or "not run — adapter absent (reason)". |
| **Regenerable report** | `--report` regenerates [`results/REPORT.md`](results/REPORT.md) from a fresh run. Absent subjects are "not run" rows + the exact command to populate them. **No fabricated numbers.** |

### Cross-language fairness rules (stated honestly)

- **Same workload bytes**, same termination condition (run to the trap for W1 / a fixed window for W2),
  a **warmup pass** before the measured pass, and the subject's language + runtime version recorded.
- We do **NOT** compare a JIT-warmed measurement against a cold interpreter — each subject warms in its
  own idiom (BenchmarkDotNet for our tiers; an explicit warmup slice for the others).
- **Subprocess subjects (py65, sfotty) include process-launch overhead** amortized across the run. W1
  is long enough that this is negligible; W2 is run for a fixed large window per subject. The C subject
  is also a subprocess but, being native, is launch-cheap.
- **Cycle-count models differ legitimately** between distinct-but-correct 6502 emulators on edge cases
  (page-cross penalties, RMW timing). For W1 the **functional gate is reaching the `$3469` success
  trap** — a subject that never parks at the trap (or parks elsewhere) diverged and is reported as
  `not run — subject diverged`, never as a fast-but-wrong number. The cycles/sec each subject reports
  uses **its own** cycle model. These are therefore **indicative cross-language** numbers, not a
  controlled microbenchmark — honesty over false precision.

### Verification-in-the-adapter (the honesty mechanism)

`Measure` is handed `ExpectedCycles`. A diverging subject typically terminates at a different cycle
count or never reaches the trap; the adapter (and our own tiers' runner) detect this and report
`Ran=false` with a "subject diverged" note, so a wrong emulator never contributes a misleadingly fast
number to the report.

## Third-party subjects — availability + how to populate

The third-party emulator *sources/runtimes* are **not vendored** (license + size + the TomHarte-vector
principle). `bench/third-party/fetch-subjects.{sh,ps1}` populates a cache dir
(`~/.cache/cpuemulator/bench`, override with `CPUEMULATOR_BENCHCACHE`). Each adapter probes for its
runtime/source and skips-with-note if absent — so the suite works even on a box with none of them.

| Subject | Lang | How it runs | Populate it |
|---|---|---|---|
| **our Tier-0 interpreter** | C# | in-process, always | — |
| **our Tier-1 JIT** | C# | in-process, always | — |
| **Asm6502** | C# | in-process (a cycle-accurate C# 6502, NuGet `Asm6502`) | restores via NuGet at build time; needs nuget.org reachable once. Disable offline with `-p:UseAsm6502=false`. |
| **fake6502** | C | a `gcc`/`clang`/`cc`-compiled native shim run as a subprocess | a C compiler + `fetch-subjects` (downloads `fake6502.c`/`.h` from the omarandlorraine fork; compiled with `-DNMOS6502 -DDECIMALMODE` to match our NMOS+BCD core). |
| **py65** | Python | a `python` subprocess over `py65_runner.py` | python + `fetch-subjects` (creates a venv, `pip install py65`). py65 is pure-Python — the slowest subject by orders of magnitude, an honest cross-language data point. |
| **sfotty** | JS | a `node` subprocess over `sfotty_runner.mjs` (`@sfotty-pie/sfotty`, a cycle-exact JS 6502) | node + `fetch-subjects` (`npm install @sfotty-pie/sfotty`). |

### Adding a new subject

Implement `IEmulatorAdapter` (see `IEmulatorAdapter.cs`): a `Name`, a side-effect-free `Probe(out
reason)` that returns false-with-a-note when the runtime/source is absent, and a `Measure(workload)`
that runs the workload to its termination condition over a warmed window and self-verifies against
`ExpectedCycles`. Add it to `BenchHarness.DefaultAdapters()`. For a scripting subject, drop a glue
runner in `bench/third-party/` that prints `CYCLES <n>` + `WALL_SECONDS <f>` and reuse
`SubprocessRunner`.

## The two M2 revisit gates (Task 9)

`--gates` runs two model micro-benches whose recorded decisions live in [`results/REPORT.md`](results/REPORT.md)
(the "Revisit gates" section, regenerated with the numbers):

- **Gate A — dispatch: switch vs `delegate*`.** The realized Tier-0 interpreter dispatches with a dense
  `switch (opcode)`. The gate measures it against a `delegate*<void>[256]` function-pointer table.
- **Gate B — state layout: fields-on-class vs struct.** The realized `Mos6502Cpu` holds A/X/Y/S/P/PC as
  class fields. The gate measures that against a mutable struct layout.

The bar (recorded): change the implementation **only on a material win (> 10%)**; otherwise record
"measured, kept current, here are the numbers." The Tier-1 JIT — where the speed now lives (chaining +
emitted decimal arms) — uses neither shape (it emits straight-line IL), so both gates are purely
Tier-0 questions.
