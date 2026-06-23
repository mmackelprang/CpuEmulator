# Benchmarks

The emulator ships a **comparative cross-language benchmark suite** that measures emulated CPU cycles
per host wall-clock second across our two execution tiers — the Tier-0 interpreter and the Tier-1
IL-JIT — for each wired CPU (**6502**, **Z80**, **68000**, and **8086**), and, opt-in, third-party
emulators of that CPU in C#, C, Python, and/or JavaScript. Results are grouped per-CPU; the 6502 unit is
machine cycles, the **Z80 unit is "T-states"**, and the 68000 and 8086 have their own cycle models
(different clock models — NOT comparable as raw numbers; only the per-CPU ratios + the within-CPU
cross-language spread are). The **68000 and 8086 additionally report guest-MIPS (instructions/sec)** —
the cross-CPU-comparable, cycle-axis-independent metric they lead with (see below).

The full methodology, fairness rules, honesty caveats, and per-subject instructions live in
**[`bench/README.md`](../../bench/README.md)**. The generated report is
**[`bench/results/REPORT.md`](../../bench/results/REPORT.md)**.

## Running it

```sh
# Our two tiers always run (in-process C#) + regenerate the report:
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report

# Add the third-party subjects (fetch their runtimes first):
bench/third-party/fetch-subjects.ps1        # or .sh — 6502 (fake6502/py65/sfotty) + Z80 (z80.c/Z80.js) + 68000 (Musashi)
tools/get-zexall.ps1                         # or .sh — the Z80-W1 ZEXDOC image
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report --all

# Statistically-rigorous numbers on our two tiers (BenchmarkDotNet):
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --bdn
```

## What it measures

- **6502 — three workloads.** W1 is the Klaus functional-test image run to its `$3469` success trap
  (96,241,367 cycles — the integration-realistic mix). W2 is a tight, hand-written ADC/SBC + branch
  arithmetic kernel that isolates the decimal-arm + chaining payoff from the I/O-free hot path. W3 is a
  hand-written **Sieve of Eratosthenes** (the classic BYTE compute benchmark; SIZE=8190 → 1899
  primes/pass) run to a fixed cycle window — a recognizable integer/branch/memory **compute** kernel
  (Dhrystone-CLASS, NOT literal Dhrystone), the "all emulators run identical compute" workload. W2 and
  W3 are committed `byte[]` kernels that are dependency-free and always run.
- **Z80 — three workloads.** Z80-W1 is the ZEXDOC instruction-set exerciser run to a fixed, frozen
  T-state window (2,000,000,000 T-states — a deterministic slice of real ZEX code, the Klaus-W1 analog;
  the harness services the CP/M BDOS calls host-side). Z80-W2 is a tight hand-written ADD/SUB + `DJNZ`
  loop run to a 50,000,000-T-state cap (the DJNZ taken branch is the hot chain edge). Z80-W3 is the Z80
  **Sieve of Eratosthenes** (SIZE=8190 → 1899 primes/pass) run to a 50,000,000-T-state cap — the
  recognizable integer/branch/memory compute kernel (Dhrystone-CLASS, NOT literal Dhrystone),
  dependency-free and always runs. These window constants are **frozen** — the M6 re-measure (below)
  reuses them byte-identically.
- **68000 — three workloads (Milestone B).** m68k-W1 is a deterministic, hand-written **mixed** kernel
  (MOVE variants, ALU reg/EA, a shift, `BSR`/`RTS`, a `DBF` counted loop) — the integration-realistic
  stream (the 68000 has no in-repo Klaus/ZEX-equivalent runnable exerciser, so this synthetic mixed
  kernel is dependency-free and always runs). m68k-W2 is a tight hand-written ALU + `BNE` branch loop.
  m68k-W3 is the 68000 **Sieve of Eratosthenes** (SIZE=8190 → 1899 primes/pass) — the recognizable
  integer/branch/memory compute kernel (Dhrystone-CLASS, NOT literal Dhrystone), also dependency-free
  and always runs. All three run to frozen 50,000,000 caps. **The 68000 leads with guest-MIPS (instructions/sec)** because its
  cycle/timing axis is **partial** on `main` (the M4.5d-2b foundation made 13 families cycle-exact; the
  2b-continuation is deferred) — so `CycleCount` is exact for the cycle-exact families, not the whole
  ISA. instructions/sec is data-axis-correct on the merged M4.6 core *right now* (each step / each
  budget-1 JIT block is exactly one instruction); cycles/sec is reported alongside with a coverage
  caveat and becomes fully cycle-exact automatically when the timing axis lands (ADR 0008 §6).
- **8086 — three workloads (M6 PR-A).** 8086-W1 is a deterministic, hand-written **mixed** kernel
  (MOV reg,imm16, ALU reg/reg + reg/imm, INC/DEC, a `PUSH`/`POP` round-trip, a near `CALL`/`RET`, a
  `JNZ` counted loop) — the integration-realistic stream (the 8086 has no in-repo Klaus/ZEX-equivalent
  exerciser, so this synthetic mixed kernel is dependency-free and always runs). 8086-W2 is a tight
  hand-written ADD/SUB/DEC + `JNZ` branch loop (the taken back-edge is the hot chain edge). 8086-W3 is a
  nested compute-with-store loop (an arithmetic accumulate that sweeps a data region via `MOV [BX],AX`).
  All three are little-endian, byte-granular, assemble-verified against the merged M5.6 `M8086Cpu`, and
  run to frozen 50,000,000 caps. **The 8086 leads with guest-MIPS (instructions/sec)** because its cycle
  model is **rudimentary** on `main` (M5 charges one cycle per bus access; a cycle-exact 8086 timing
  model is post-M5) — instructions/sec is data-axis-correct on the M5.6 TomHarte-green core *right now*;
  cycles/sec is reported alongside with the rudimentary-axis caveat. There is no third-party 8086
  reference yet (the M6 plan §8 Q3 evaluation is deferred), so the 8086's "best existing" column is empty.
- **Always-on subjects.** Per CPU, our Tier-0 interpreter (the baseline) and our Tier-1 JIT (the
  headline) are in-process C# and always run.
- **Opt-in third-party subjects.** For the 6502: Asm6502 (C#), fake6502 (C), py65 (Python), sfotty
  (JS). For the Z80: Z80dotNet (C#), superzazu/z80 (C), Z80.js (JS) — all MIT-licensed. Each runs
  behind an adapter shim that skips-with-a-note when its runtime is absent — the report commits only
  measured data, never a fabricated number, and any single absent ref degrades exactly one row while
  the our-tiers baseline always commits. The 68000 head-to-head reference — **Musashi** (C, MIT,
  kstenerud/Musashi v4.60) — is now **integrated**: a compiled-once-cached C subprocess that runs the
  same 68000 workload bytes on the same host and reports cycles/sec + guest-MIPS, skipping-with-a-note
  when its compiler/source is absent. Indicative guest-MIPS on a contended host: m68k-W1 ≈ 63.5,
  m68k-W2 ≈ 69.5, m68k-W3 ≈ 86.5 — indicative cross-language numbers, not a controlled microbench. The
  68000 section ships its two-tier baseline regardless.

The per-CPU JIT-vs-interpreter comparison and the cross-language comparison table are in the generated
report. **The original M6 baseline finding was that the Tier-1 JIT was slower than the Tier-0
interpreter** — on the 6502 (SMC-invalidation thrash on Klaus; per-instruction overhead on the tiny
non-SMC kernel) and on the Z80/68000/8086, whose Tier-1 was then **all-fallback** (every op fell back to
the interpreter Step — no hot-op IL emit; a ratio ≈ 1.0× minus block-dispatch overhead). **M6 closed
that gap** on the **compute kernels (W2/W3)**: the high-ROI op families of all four CPUs now emit IL.
(On the SMC-heavy W1 rows the JIT is still *slower* than the interpreter — the recompile-cost lever IS
engaged by default and cuts the 6502 Klaus recompiles ~6.8×, but the dominant remaining cost there is
**per-dispatch overhead, not recompilation**: the `InvalidateIfDirty` page scan that Klaus's constant
code-page dirtying triggers, plus all-fallback dispatch on the non-emitting W1/W3 paths. Reducing that
per-dispatch cost is a recorded next optimization — see the [Roadmap](../ROADMAP.md).) So this section is
the before/after speedup story with the "after" now landed.

**The "after" (arc-end re-measure, post-M6).** On the no-SMC **compute kernels**, the Tier-1 JIT now
*beats* the Tier-0 interpreter for the three CPUs M6 targeted (Tier-1 ÷ Tier-0):

| CPU | W2 kernel | W3 sieve | what emits |
|---|---:|---:|---|
| **Z80** | **2.28×** (569M vs 250M T-st/s) | 0.87× | LD, ALU+flags, branch/call/stack |
| **68000** | **3.08×** (27.2 vs 8.8 MIPS) | _W3 capped_ | MOVE, ALU+CCR, shifts, branch |
| **8086** | **1.18×** (30.9 vs 26.3 MIPS) | _W3 capped_ | MOV, ALU+FLAGS, near branch |
| 6502 | 0.51× | 0.60× | (M2 baseline — block-JIT vs a tight switch-interpreter on a hot loop; not a regression) |

The **SMC-heavy W1** runs (6502 Klaus, Z80 ZEXDOC, 68000 W1, 8086 W1) — and the 68000/8086 **W3** —
stay JIT-*slower* and are **capped at 10s** in the report: the recompiler thrashes on self-modifying
code, and the JIT's per-dispatch overhead dominates. Two honest caveats: (1) the recompile-cost lever
(PR-S) **IS engaged by default** — it cuts the 6502 Klaus recompiles ~6.8× — but the JIT still loses on
these rows because **per-dispatch overhead dominates** (the `InvalidateIfDirty` page scan on
constantly-dirtied code, plus all-fallback dispatch on the non-emitting paths), not recompilation;
reducing that is a recorded follow-on; and (2) the cap is a valid
measurement (cycles ÷ wall over a bounded window), not a truncation. The clean emit signal is the **W2**
column. _(Numbers from the [arc-end `REPORT.md`](../../bench/results/REPORT.md); the per-CPU ratio is
host-independent — it cancels machine speed.)_

The JIT's foundational value remains correctness parity (the full TomHarte sweep through the JIT, the
committed differential fuzzer, Klaus cycle-exact); M6 adds the throughput. See
[the JIT tier guide](jit.md) for the accuracy contract, chaining, the per-CPU emit arms, and the
SMC/recompile-cost lever. The deferred emit follow-ons (8086 far-flow, MUL/DIV, string/REP, INT/IRET;
cycle-exact emitted 68000 timing) are in the [Roadmap](../ROADMAP.md).

## Baseline → re-measure (M6)

This report is the before/after speedup story for the M6 hot-op emit work. The **"before"** is the
all-fallback / SMC-thrash baseline; the **"after"** re-runs the IDENTICAL committed workloads with the
hot-op IL emit landed, and the per-CPU ratio delta is the demonstrated speedup. The workload constants
are **frozen** so the comparison is apples-to-apples; the per-CPU ratio is machine-independent (it
cancels host speed). The full re-measure contract — same bytes, same metric, same command, same
canonical host — lives in [`bench/README.md`](../../bench/README.md) under "Baseline → re-measure (M6)".

**Before → after on the W2 compute kernel** (the cleanest emit signal — no SMC, frozen workload):

| CPU | before (pre-M6) | after (M6 emit) | swing |
|---|---|---|---|
| **Z80** W2 | ≈0.45× of interpreter (all-fallback) | **2.28×** | JIT now beats interp |
| **68000** W2 | ≈1.0× minus block overhead (all-fallback) | **3.08×** (guest-MIPS) | emit lands |
| **8086** W2 | ≈0.59–0.61× (all-fallback) | **1.18×** (guest-MIPS) | emit lands |

The "before" is the committed all-fallback baseline; the "after" re-runs the **identical frozen
workloads** with hot-op IL emit landed. The 6502 was already a partial-emit JIT before M6 (its W2/W3 sit
at 0.51×/0.60× — the established block-JIT-vs-tight-switch case), so its W2 is not part of the M6 emit
delta. SMC-heavy W1/W3 are excluded from the emit-signal comparison (they're dominated by per-dispatch
JIT overhead — see the caveat above — not by emit quality).

> **Benchmark-harness notes (resolved; not core correctness):** the 68000 W3 hot-op profiler arm **shipped**
> (`bc68ee7` — the profiler + `bench/results/REPORT.md` now carry the 68000 W3 sieve-kernel ranking), and the
> 68000 W2 cycle off-by-2 is the **expected coarse-cycle slack** (DECISION T2: the JIT charges coarse
> `BaseCycles + 1` fetch vs the interpreter's exact prefetch; gated within a `<= 16` tolerance in
> `BenchHarnessSmokeTests`), **not** a harness defect — the data axis is byte-identical.
