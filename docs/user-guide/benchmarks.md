# Benchmarks

The emulator ships a **comparative cross-language benchmark suite** that measures emulated CPU cycles
per host wall-clock second across our two execution tiers — the Tier-0 interpreter and the Tier-1
IL-JIT — and, opt-in, third-party 6502 emulators in C#, C, Python, and JavaScript.

The full methodology, fairness rules, honesty caveats, and per-subject instructions live in
**[`bench/README.md`](../../bench/README.md)**. The generated report is
**[`bench/results/REPORT.md`](../../bench/results/REPORT.md)**.

## Running it

```sh
# Our two tiers always run (in-process C#) + regenerate the report:
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report

# Add the third-party subjects (fetch their runtimes first):
bench/third-party/fetch-subjects.ps1        # or .sh
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report --all

# Statistically-rigorous numbers on our two tiers (BenchmarkDotNet):
dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --bdn
```

## What it measures

- **Two workloads.** W1 is the Klaus functional-test image run to its `$3469` success trap
  (96,241,367 cycles — the integration-realistic mix). W2 is a tight, hand-written ADC/SBC + branch
  arithmetic kernel that isolates the decimal-arm + chaining payoff from the I/O-free hot path.
- **Always-on subjects.** Our Tier-0 interpreter (the baseline) and our Tier-1 JIT (the headline) are
  in-process C# and always run.
- **Opt-in third-party subjects.** Asm6502 (C#), fake6502 (C), py65 (Python), and sfotty (JS) run
  behind adapter shims that skip-with-a-note when their runtime is absent — the report commits only
  measured data, never a fabricated number.

The JIT-vs-interpreter comparison and the cross-language comparison table are in the generated report.
**The honest measured finding is that the Tier-1 JIT is currently slower than the Tier-0 interpreter
on both workloads** (SMC-invalidation thrash on the Klaus run; per-instruction overhead on the tiny
non-SMC kernel) — the JIT's M2 value is correctness parity, not raw throughput, and reducing the
SMC-invalidation cost is the recorded next optimization. See also [the JIT tier guide](jit.md) for the
accuracy contract, chaining, and the emitted decimal arms.
