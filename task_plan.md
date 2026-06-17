# M6 enrichment — Dhrystone-class workloads + Musashi head-to-head

Branch: `feat/m6-musashi-dhrystone` (from main 86d6f51). Scope: ONLY `bench/` + bench docs.
NEVER touch `src/` or `tools/` (concurrent M5.5a Builder owns CpuEmitter/M8086).

## Part 1 (PRIORITY) — Dhrystone-class compute workload (Sieve of Eratosthenes)
Real Dhrystone needs per-CPU C cross-toolchains (cc65/sdcc/m68k-gcc) + a fragile build — NOT
time-box feasible and breaks the "always runs, dependency-free, deterministic" harness invariant.
Honest recognizable substitute: the **Sieve of Eratosthenes** (the classic BYTE compute benchmark) —
integer + branch + memory heavy, deterministic, verifiable by its prime count, hand-assembled, no
toolchain. Label it accurately as a Sieve compute kernel (Dhrystone-CLASS), never as literal Dhrystone.

- [ ] W3 Sieve kernel for **6502** (`Workloads.SieveKernel`), FROZEN `SieveCycleCap`. Verify prime count.
- [ ] W3 Sieve kernel for **Z80** (`Z80Workloads.Z80SieveKernel`), FROZEN cap. Verify.
- [ ] W3 Sieve kernel for **68000** (`M68000Workloads.SieveKernel`), FROZEN cap + instr cap. Verify.
- [ ] Register all three in `Program.cs` + `TierBenchmarks.cs` (BDN) + smoke test.
- [ ] If a CPU's port is hard, ship the others + DISCLOSE deferred (never fake).

## Part 2 (TIME-BOXED) — Musashi 68000 head-to-head
Toolchain present (gcc/clang 14.2/19.1). Attempt head-to-head; fall back to cited if FFI/build blocks.
- [ ] `bench/third-party/musashi_runner.c` (mirrors z80c_runner.c; Musashi callbacks + instr hook)
- [ ] `MusashiAdapter.cs` (mirrors Z80CAdapter; handles m68kmake codegen build step)
- [ ] `BenchPaths.MusashiSource` + fetch arm in fetch-subjects.{sh,ps1}
- [ ] `SubprocessRunner` optional `INSTRUCTIONS n` parse (additive, default 0)
- [ ] Wire `MusashiAdapter` into `AdaptersFor("m68000")`
- [ ] If build/FFI impractical: STOP, leave cited row, report blocker. Part 1 ships regardless.

## Validation (hard timeout on runs)
- [ ] `dotnet build CpuEmulator.slnx -c Release -warnaserror` clean
- [ ] Bench smoke + generator tests green (+ new workload/adapter tests)
- [ ] New Sieve workloads run + appear in table; Musashi `‡` row if integrated (else cited)
- [ ] `git diff --stat main...HEAD -- src/ tools/` EMPTY
- [ ] Numbers labeled INDICATIVE; do NOT clobber committed clean-host 6502/Z80 numbers

## Discipline
- Never blanket-kill dotnet; only testhost.exe/vstest.console.exe by name, never during a run
- Commit trailer: Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
- STOP before merge; report to owner.
