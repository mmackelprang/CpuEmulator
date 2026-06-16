# Benchmarks

The emulator ships a **comparative cross-language benchmark suite** that measures emulated CPU cycles
per host wall-clock second across our two execution tiers — the Tier-0 interpreter and the Tier-1
IL-JIT — for each wired CPU (**6502** and **Z80**), and, opt-in, third-party emulators of that CPU in
C#, C, Python, and/or JavaScript. Results are grouped per-CPU; the 6502 unit is machine cycles and the
**Z80 unit is "T-states"** (a different clock model — the two are NOT comparable as raw numbers; only
the per-CPU ratios + the within-CPU cross-language spread are).

The full methodology, fairness rules, honesty caveats, and per-subject instructions live in
**[`bench/README.md`](../../bench/README.md)**. The generated report is
**[`bench/results/REPORT.md`](../../bench/results/REPORT.md)**.

## Running it

```sh
# Our two tiers always run (in-process C#) + regenerate the report:
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report

# Add the third-party subjects (fetch their runtimes first):
bench/third-party/fetch-subjects.ps1        # or .sh — 6502 (fake6502/py65/sfotty) + Z80 (z80.c/Z80.js)
tools/get-zexall.ps1                         # or .sh — the Z80-W1 ZEXDOC image
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report --all

# Statistically-rigorous numbers on our two tiers (BenchmarkDotNet):
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --bdn
```

## What it measures

- **6502 — two workloads.** W1 is the Klaus functional-test image run to its `$3469` success trap
  (96,241,367 cycles — the integration-realistic mix). W2 is a tight, hand-written ADC/SBC + branch
  arithmetic kernel that isolates the decimal-arm + chaining payoff from the I/O-free hot path.
- **Z80 — two workloads.** Z80-W1 is the ZEXDOC instruction-set exerciser run to a fixed, frozen
  T-state window (2,000,000,000 T-states — a deterministic slice of real ZEX code, the Klaus-W1 analog;
  the harness services the CP/M BDOS calls host-side). Z80-W2 is a tight hand-written ADD/SUB + `DJNZ`
  loop run to a 50,000,000-T-state cap (the DJNZ taken branch is the hot chain edge). These window
  constants are **frozen** — the M6 re-measure (below) reuses them byte-identically.
- **Always-on subjects.** Per CPU, our Tier-0 interpreter (the baseline) and our Tier-1 JIT (the
  headline) are in-process C# and always run.
- **Opt-in third-party subjects.** For the 6502: Asm6502 (C#), fake6502 (C), py65 (Python), sfotty
  (JS). For the Z80: Z80dotNet (C#), superzazu/z80 (C), Z80.js (JS) — all MIT-licensed. Each runs
  behind an adapter shim that skips-with-a-note when its runtime is absent — the report commits only
  measured data, never a fabricated number, and any single absent ref degrades exactly one row while
  the 6502+Z80 our-tiers baseline always commits.

The per-CPU JIT-vs-interpreter comparison and the cross-language comparison table are in the generated
report. **The honest measured finding is that the Tier-1 JIT is currently slower than the Tier-0
interpreter** — on the 6502 (SMC-invalidation thrash on Klaus; per-instruction overhead on the tiny
non-SMC kernel) and on the Z80, whose Tier-1 is **all-fallback** (no hot-op IL emit yet — the deferred
M6 "5-3b hot-op emission"; a ratio ≈ 1.0× minus block overhead is expected). This all-fallback row is
the deliberately-captured **"before"**: the [JIT speedup re-measure (M6)](#baseline--re-measure-m6)
subtracts from it. The JIT's current value is correctness parity, not raw throughput. See also
[the JIT tier guide](jit.md) for the accuracy contract, chaining, and the emitted decimal arms.

## Baseline → re-measure (M6)

This report is the committed **"before"** half of a before/after speedup story. The **"after"** re-runs
the IDENTICAL committed workloads once the JIT's hot-op IL emit lands (milestone M6), and the per-CPU
ratio delta is the demonstrated speedup. The workload constants are **frozen** so the comparison is
apples-to-apples; the per-CPU ratio is machine-independent (it cancels host speed). The full re-measure
contract — same bytes, same metric, same command, same canonical host — lives in
[`bench/README.md`](../../bench/README.md) under "Baseline → re-measure (M6)".
