# CpuEmulator comparative benchmark suite

The comparative cross-language benchmark deliverable (design spec §9 item 9). It measures
**emulated CPU cycles per host wall-clock second** across our two tiers — the Tier-0 interpreter and
the Tier-1 IL-JIT — for each wired CPU (**6502**, **Z80**, and **68000**), and, opt-in, third-party
emulators of that CPU in C#, C, Python, and/or JavaScript, behind thin adapter shims that **skip-with-a-note
when their runtime is absent**. The committed report ships with whatever ran in the generating environment
plus instructions to populate the rest. Results are grouped per-CPU with the correct cycle-unit label
(6502 = machine cycles; **Z80 = T-states**; **68000 = its own cycle model** — NOT cross-architecture
comparable as raw numbers). The **68000 additionally reports guest-MIPS (instructions/sec)** — the
cross-CPU-comparable, cycle-axis-independent metric it leads with, because its cycle/timing axis is
**partial** on `main` (Milestone B; see the 68000 timing-axis caveat below).

> **Honesty first.** Our two tiers are in-process C# and always run. Third-party subjects are opt-in
> and degrade gracefully — an absent runtime is a skip-with-note, never a crash and never a fabricated
> number. The report commits **only measured data**.

## Quick start

```sh
# Our two tiers always run (in-process C#) + regenerate the report:
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report

# Add the third-party subjects (fetch their runtimes first):
bench/third-party/fetch-subjects.ps1        # or .sh — fetches fake6502, py65, sfotty (6502) + z80.c, Z80.js (Z80) + Musashi (68000)
tools/get-zexall.ps1                         # or .sh — fetches zexdoc.com for the Z80-W1 workload (into the vector cache)
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
| **Metric** | Emulated CPU cycles per host wall-clock second (cycles/sec). The absolute wall-clock for the run is reported too, so a reader can sanity-check. **The Z80 cycle unit is "T-states"** and the **68000 has its own cycle model** — each its own clock; **NOT cross-architecture comparable** to the 6502's machine cycles as a raw number (see the T-states note below). The report groups results by CPU and labels each section's unit. **The 68000 also reports guest-MIPS (instructions/sec)** — the cross-CPU-comparable, cycle-axis-independent metric (millions of guest instructions retired per host wall-second); it is the 68000's trustworthy headline because the 68000 cycle axis is partial on `main` (the timing-axis caveat below). |
| **Workloads (6502)** | **W1 (Klaus-deterministic):** the Klaus 6502 functional-test image run to its `$3469` success trap — 96,241,367 cycles of identical work, the integration-realistic mix (loads/stores/branches/ADC/SBC/SMC). Loaded from the shared vector cache (NOT vendored — the TomHarte-vector pattern); absent ⇒ W1 is skipped. **W2 (arithmetic kernel):** a tight, hand-written ADC/SBC + branch loop committed as a `byte[]` in `Workloads.cs`, run for a fixed cycle window — ADC/SBC + branch heavy, isolating the decimal-arm + chaining payoff from the I/O-free hot path; always runs (dependency-free). **W3 (sieve-kernel):** a hand-written Sieve of Eratosthenes (the classic BYTE compute benchmark; SIZE=8190 → 1899 primes/pass), a committed `byte[]` in `Workloads.cs`, run for a fixed cycle window (`SieveCycleCap = 50,000,000`) — a recognizable integer/branch/memory **compute** kernel (a Dhrystone-CLASS workload — NOT literal Dhrystone), the "all emulators run identical compute" workload (array clear + nested-loop multiple-striking + a running prime count). Like W2 it always runs (dependency-free). FROZEN: the M6 re-measure reuses it byte-identically. |
| **Workloads (Z80)** | **Z80-W1 (ZEXDOC-prefix):** the ZEXDOC instruction-set exerciser run to a fixed, **committed-and-frozen T-state window** (`Z80W1WindowTStates = 2,000,000,000`, NOT run-to-banner) — a deterministic slice of real ZEX code (the `adc/sbc hl,rr` exerciser: every register-pair × both ops × the full flag matrix + CRC accumulation), the Klaus-W1 analog. The driver services the CP/M BDOS CALL host-side (fn-2/fn-9 + RET) + honors the warm-boot sentinel, the same convention `CpmBdosHost` proves. Loaded from the shared vector cache (`zex/zexdoc.com`, NOT vendored); absent ⇒ Z80-W1 is skipped (run `tools/get-zexall`). **Z80-W2 (arithmetic kernel):** a tight, hand-written ADD/SUB + `DJNZ` loop committed as a `byte[]` in `Workloads.cs`, run for a fixed cap (`Z80W2CycleCap = 50,000,000` T-states) — the DJNZ taken branch is the hot chain edge a future block-JIT stresses. **Z80-W3 (sieve-kernel):** the Z80 Sieve of Eratosthenes (SIZE=8190 → 1899 primes/pass), a committed `byte[]` in `Workloads.cs`, run for a fixed T-state window (`Z80SieveCycleCap = 50,000,000`) — the recognizable integer/branch/memory **compute** kernel (Dhrystone-CLASS — NOT literal Dhrystone), the "all emulators run identical compute" workload; always runs (dependency-free), like Z80-W2. **These window constants are FROZEN: the M6 re-measure (the "after") reuses them byte-identically — see "Baseline → re-measure (M6)" below.** |
| **Workloads (68000)** | **m68k-W1 (mixed-instruction stream):** a deterministic, hand-written **mixed** kernel committed as a `byte[]` in `Workloads.cs` (`M68000Workloads.MixedKernel`) — `MOVEQ`/`MOVE.W`/`MOVE.L`, `ADDI`/`ADD`/`ADDQ`, `LSL`, `EORI`, a `BSR`/`RTS` subroutine, and a `DBF` counted back-edge — the integration-realistic stream (the 68000 has no in-repo Klaus/ZEX-equivalent runnable exerciser, so this synthetic mixed kernel is dependency-free + always runs; Option A in the plan). Run to a frozen instruction window (`M68000W1InstructionCap = 50,000,000`). **m68k-W2 (arithmetic kernel):** a tight hand-written `ADDQ`/`SUBQ`/`EORI` + `BNE` loop committed as a `byte[]` (`M68000Workloads.ArithmeticKernel`), run to a frozen cap (`M68000W2CycleCap = 50,000,000`; `M68000W2InstructionCap = 50,000,000`) — the taken `BNE` back-edge is the hot chain edge a future block-JIT stresses. **m68k-W3 (sieve-kernel):** the 68000 Sieve of Eratosthenes (SIZE=8190 → 1899 primes/pass), a committed `byte[]` (`M68000Workloads.SieveKernel`) run to a frozen cap (`M68000SieveCycleCap = 50,000,000`; `M68000SieveInstructionCap = 50,000,000`) — the recognizable integer/branch/memory **compute** kernel (Dhrystone-CLASS — NOT literal Dhrystone), the "all emulators run identical compute" workload; always runs (dependency-free), and like its sibling 68000 workloads it leads with **instructions/sec (guest-MIPS)** rather than cycles/sec. **These constants are FROZEN: the M6 re-measure reuses them byte-identically.** **68000 timing-axis caveat:** the 68000 cycle/timing axis is PARTIAL on `main` (the M4.5d-2b foundation made 13 families cycle-exact; the 2b-continuation is deferred) — so `CycleCount` is exact for the cycle-exact families, not the whole ISA. The 68000's trustworthy headline is therefore **instructions/sec (guest-MIPS)**, data-axis-correct on the merged M4.6 core right now; cycles/sec is reported alongside with the coverage caveat and becomes fully cycle-exact automatically when the M4.5d-2 timing axis lands (ADR 0008 §6). |
| **Subjects (6502)** | **(a) our Tier-0 interpreter, (b) our Tier-1 JIT (chaining on)** — always run. **(c) third-party:** Asm6502 (C#, in-process), fake6502 (C, compiled shim), py65 (Python subprocess), sfotty (JS via node) — opt-in, behind adapter shims, skipped-with-note when absent. |
| **Subjects (Z80)** | **(a) our Tier-0 interpreter, (b) our Tier-1 JIT** — always run. **(c) third-party cross-language refs:** Z80dotNet (C#, in-process, NuGet `Z80dotNet`, MIT), superzazu/z80 (C, compiled shim, MIT), Z80.js (JS via node, DrGoldfire/Z80.js, MIT) — opt-in, behind adapter shims, skipped-with-note when absent. The third-party refs **enrich** the Z80 table; they **never block** it — a missing toolchain degrades exactly one row, and the 6502+Z80 our-tiers baseline always commits. |
| **Subjects (68000)** | **(a) our Tier-0 interpreter, (b) our Tier-1 JIT (all-fallback — the merged M4.6 model)** — always run. **(c) third-party head-to-head ref:** **Musashi** (C, MIT, kstenerud/Musashi v4.60 — a fast, widely-used 68000 used by MAME-class projects) is now the INTEGRATED head-to-head reference: a compiled-once-cached C subprocess (mirrors superzazu/z80) that runs the same 68000 workload bytes on the same host, reporting cycles/sec + guest-MIPS; it skip-with-notes when its compiler/source is absent. It supersedes the former cited placeholder automatically. Our two tiers always run; the our-tiers baseline ships regardless. |
| **Harness for our tiers** | **BenchmarkDotNet** (`--bdn`): its warmup + measurement windows, statistical reporting, and environment capture (CPU, OS, .NET) are the methodology. A lighter warmed-`Stopwatch` pass (`--report`) produces the headline report rows + the smoke-test check. |
| **Harness for third-party** | Each adapter runs the SAME workload image, measures the same metric in the subject's natural runner (in-process for C#, a compiled-shim subprocess for C, a python/node subprocess for the scripting subjects), with a warmup pass excluded from the measured window. |
| **Environment metadata** | The report header records the host (CPU, core count, OS, .NET version) and, per third-party subject, its detected version or "not run — adapter absent (reason)". |
| **Regenerable report** | `--report` regenerates [`results/REPORT.md`](results/REPORT.md) from a fresh run. Absent subjects are "not run" rows + the exact command to populate them. **No fabricated numbers.** |

### Cross-language fairness rules (stated honestly)

- **Same workload bytes**, same termination condition (run to the trap for W1 / a fixed window for the
  W2/W3 kernels),
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
- **The Z80 unit is "T-states" — do NOT cross-multiply it against 6502 machine cycles.** 6502 cycles
  and Z80 T-states are different clock models; only the **per-CPU ratios** (our JIT-vs-interpreter, and
  the within-CPU cross-language spread) are meaningful comparisons. The cross-architecture raw cycles/sec
  rows are NOT a "6502 vs Z80 race" — the report keeps them in separate, per-CPU-labeled sections to
  make that explicit. Each Z80 subject (ours + the third-party refs) uses its OWN T-state model (some,
  like Z80.js, use the documented per-opcode counts rather than gate-level timing) — indicative
  cross-language, not a controlled microbench.
- **The Z80 and 68000 Tier-1 JITs are currently ALL-FALLBACK** (no hot-op IL emit yet — the deferred M6
  "5-3b hot-op emission" for the Z80; the merged M4.6 all-fallback model for the 68000, where every op
  falls back to the interpreter Step). So their JIT-vs-interpreter ratio is ≈ 1.0× minus block-dispatch
  overhead — the **honest "before"** the re-measure subtracts from, not a defect. The report states this
  caveat automatically under the Z80 + 68000 speedup pairs. (The 6502 already commits its
  all-fallback-equivalent row too — capturing the "before" is the whole point of the before/after exercise.)
- **The 68000 leads with guest-MIPS, not cycles/sec.** The 68000 cycle/timing axis is partial on `main`
  (the M4.5d-2b foundation; the 2b-continuation is deferred), so `CycleCount` is exact only for the
  cycle-exact families. The 68000's trustworthy headline is therefore **instructions/sec (guest-MIPS)** —
  data-axis-correct on the merged M4.6 core (each step / each budget-1 JIT block is exactly one
  instruction); cycles/sec is reported with the coverage caveat (the report emits it automatically under
  the 68000 section) and becomes fully cycle-exact automatically when the M4.5d-2 timing axis lands. The
  68000 instructions/sec rows are **measurable NOW** (this Milestone B baseline); the cycles/sec axis is
  gated (ADR 0008 §6). guest-MIPS is also the cross-CPU-comparable unit (an instruction is an instruction
  regardless of the cycle model) — but only WITHIN the same CPU's identical workload bytes is it a fair
  apples-to-apples comparison; across CPUs it is indicative ("throughput class", not a race).
- **The headline is the per-CPU before/after RATIO (machine-independent).** A ratio cancels host speed,
  so the M6 speedup claim survives running "before" and "after" on different machines/years. The absolute
  cycles/sec rows are useful sanity checks + the cross-language spread; re-measure on the same canonical
  host (recorded in `## Environment`) for directly-comparable absolutes.

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

**6502 subjects:**

| Subject | Lang | How it runs | Populate it |
|---|---|---|---|
| **our Tier-0 interpreter** | C# | in-process, always | — |
| **our Tier-1 JIT** | C# | in-process, always | — |
| **Asm6502** | C# | in-process (a cycle-accurate C# 6502, NuGet `Asm6502`) | restores via NuGet at build time; needs nuget.org reachable once. Disable offline with `-p:UseAsm6502=false`. |
| **fake6502** | C | a `gcc`/`clang`/`cc`-compiled native shim run as a subprocess | a C compiler + `fetch-subjects` (downloads `fake6502.c`/`.h` from the omarandlorraine fork; compiled with `-DNMOS6502 -DDECIMALMODE` to match our NMOS+BCD core). |
| **py65** | Python | a `python` subprocess over `py65_runner.py` | python + `fetch-subjects` (creates a venv, `pip install py65`). py65 is pure-Python — the slowest subject by orders of magnitude, an honest cross-language data point. |
| **sfotty** | JS | a `node` subprocess over `sfotty_runner.mjs` (`@sfotty-pie/sfotty`, a cycle-exact JS 6502) | node + `fetch-subjects` (`npm install @sfotty-pie/sfotty`). |

**Z80 subjects** (the cross-language Z80 refs; each skip-with-note when absent — they enrich the Z80
table but never block it):

| Subject | Lang | License | How it runs | Populate it |
|---|---|---|---|---|
| **our Tier-0 interpreter** | C# | — | in-process, always | — |
| **our Tier-1 JIT** (all-fallback) | C# | — | in-process, always | — |
| **Z80dotNet** | C# | MIT | in-process (Konamiman's cycle-accurate C# Z80, NuGet `Z80dotNet`) | restores via NuGet at build time; needs nuget.org reachable once. Disable offline with `-p:UseZ80Sharp=false`. |
| **superzazu/z80** | C | MIT | a `gcc`/`clang`/`cc`-compiled native shim run as a subprocess | a C compiler + `fetch-subjects` (downloads `z80.c`/`z80.h` from `superzazu/z80` into `<cache>/z80c`). |
| **Z80.js** | JS | MIT | a `node` subprocess over `z80js_runner.mjs` (DrGoldfire/Z80.js — an instruction interpreter using documented per-opcode T-state counts) | node + `fetch-subjects` (downloads `Z80.js` from `DrGoldfire/Z80.js` into `<cache>/z80js`). |

The Z80-W1 ZEXDOC image is fetched separately into the **vector** cache (not the bench cache) by
`tools/get-zexall.{ps1,sh}` (→ `<vectors>/zex/zexdoc.com`); absent ⇒ all Z80-W1 rows skip-with-note,
Z80-W2 still runs.

**68000 subjects** (the head-to-head 68000 ref skip-with-notes when absent — it enriches the 68000
table but never blocks it; our two tiers always run):

| Subject | Lang | License | How it runs | Populate it |
|---|---|---|---|---|
| **our Tier-0 interpreter** | C# | — | in-process, always | — |
| **our Tier-1 JIT** (all-fallback) | C# | — | in-process, always | — |
| **Musashi** | C | MIT | a `gcc`/`clang`/`cc`-compiled native shim (with a `m68kmake` codegen step + the `mamesf.h` SoftFloat shim) run as a subprocess | a C compiler + `fetch-subjects` (downloads the Musashi sources from `kstenerud/Musashi` into `<cache>/musashi`; `m68kops.*` are generated by `m68kmake`, not fetched). |

m68k-W1/W2/W3 are dependency-free committed `byte[]` kernels — they **always run** on our two tiers
regardless of whether the Musashi toolchain is present.

### Adding a new subject

Implement `IEmulatorAdapter` (see `IEmulatorAdapter.cs`): a `Name`, a side-effect-free `Probe(out
reason)` that returns false-with-a-note when the runtime/source is absent, and a `Measure(workload)`
that runs the workload to its termination condition over a warmed window and self-verifies against
`ExpectedCycles`. Add it to the right `BenchHarness.AdaptersFor(architecture)` set. For a scripting
subject, drop a glue runner in `bench/third-party/` that prints `CYCLES <n>` + `WALL_SECONDS <f>` and
reuse `SubprocessRunner` (pass `bdosMode: true` for a CP/M Z80-W1-style workload so the runner services
the BDOS CALL host-side).

## Baseline → re-measure (M6)

This committed report is the **"before"** half of a before/after speedup story. The **"after"** is a
re-run once the JIT's hot-op IL emit lands (milestone **M6**; for Z80 specifically the deferred "5-3b
hot-op emission"). The re-measure is a CONTRACT, not a fresh design:

- **Same workload bytes, byte-identical.** The frozen constants — `Workloads.KlausExpectedCycles`,
  `Workloads.ArithKernelCycleCap`, `Workloads.SieveCycleCap`, the 6502 W2/W3 kernel bytes,
  `Z80Workloads.Z80W1WindowTStates`, `Z80Workloads.Z80W2CycleCap`, `Z80Workloads.Z80SieveCycleCap`,
  the Z80-W2/W3 kernel bytes, `M68000Workloads.M68000W2CycleCap`,
  `M68000Workloads.M68000W2InstructionCap`, `M68000Workloads.M68000W1InstructionCap`,
  `M68000Workloads.M68000SieveCycleCap`, `M68000Workloads.M68000SieveInstructionCap`, and the 68000
  W1/W2/W3 kernel bytes — MUST NOT change between the baseline commit and the M6 re-measure. Retuning a
  window would void the comparison. A `git diff` of the workload constants between the two commits must
  show no change.
- **Same metric** (cycles/sec, per-CPU), **same command** (`dotnet run -c Release --project
  bench/CpuEmulator.Benchmarks.Runner -- --report --all`), **same canonical host** where feasible (the
  `## Environment` block in `results/REPORT.md` records it). If a different host is used, the **per-CPU
  before/after ratio still holds** (it cancels host speed); only the absolute cycles/sec rows need the
  same host to be directly comparable.
- **The all-fallback rows are the "before".** Today every non-6502 JIT (and the 6502 on its current
  invalidation strategy) is the honest all-fallback/recompilation-thrash baseline. M6 re-measures the
  IDENTICAL rows; the delta IS the demonstrated speedup. CPUs that have NOT yet got hot-op emit at
  re-measure time show ≈ 1.0× honestly.

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
