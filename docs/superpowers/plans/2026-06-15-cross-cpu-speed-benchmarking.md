# Cross-CPU Speed-Benchmarking Strategy — Baseline Now, Re-measure After the JIT (M6)

> **STATUS: DRAFT plan — awaiting Coordinator/user review of the Decisions block before Builder/Tester execute.**
> **For agentic workers:** REQUIRED SUB-SKILL once approved — use `superpowers:subagent-driven-development` or
> `superpowers:executing-plans` to implement task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> This plan EXTENDS the existing, working benchmark harness (`bench/`); it does NOT invent a new one. Read
> `bench/README.md` (methodology + fairness rules) and `bench/results/REPORT.md` (the committed 6502 results)
> BEFORE the first task — every fairness rule there is binding here.

**Goal (from the user, verbatim intent).** Two halves:
1. **Capture the emulators' speeds AS THEY ARE NOW** as a committed, reproducible baseline — Tier-0 interpreter
   AND Tier-1 (currently **all-fallback**) JIT, for everything runnable today (6502 is already committed; **ADD Z80**).
2. **Re-measure IDENTICALLY once the JIT is fully working** (hot-op IL emit, milestone **M6**) and present the
   delta as the demonstrated speedup — same workload bytes, same machine-normalization, same headline metric.

**Non-goal.** This plan does NOT add hot-op IL emit (that IS M6, and for Z80 specifically the deferred
"5-3b hot-op emission"). It makes the *measurement apparatus* ready and captures the honest "before". The
"after" is a re-run of the identical, already-committed workloads once M6 lands — see Milestone C.

**The honest shape of the "before".** Today every non-6502 JIT runs **all-fallback** (byte-identical tier
parity, NO hot-op emit). So a "JIT speedup" measured now is ~0 or slightly negative (block-dispatch overhead) —
EXACTLY as `bench/results/REPORT.md` already shows honestly for the 6502 (W1 JIT is 0.00x, W2 JIT is 0.53x of
the interpreter). We capture that all-fallback JIT number anyway: it is the load-bearing "before" half of the
before/after story. Fabricating or omitting it would defeat the entire exercise.

**Tech stack.** C# (.NET 10); the existing `bench/CpuEmulator.Benchmarks` library + `…Runner` project;
`BenchmarkDotNet` (the `--bdn` rigorous twin); the `ICpuCore` interface (`CpuEmulator.Core`) that BOTH `Mos6502Cpu`
and `Z80Cpu` already implement; `JittedCpu<T>` + the per-CPU generated `JitTarget`; the existing fetch-not-vendor
vector-cache convention (`~/.cache/cpuemulator/vectors`, override `CPUEMULATOR_TESTVECTORS`).

---

## Decisions for Coordinator/user review

> Resolve these FIVE forks before Builder executes Milestone A. Each lists the recommendation + the alternative.
> Defaults are chosen to honor the existing `bench/` fairness rules (commit only measured data; skip-with-note for
> absent runtimes; no fabricated numbers) and to get a 6502+Z80 two-way baseline committed in days, not weeks.

**D1 — Which Z80 workload(s)?**
- **Recommendation: a 6502-W1/W2-MIRRORED PAIR.** **Z80-W1 = ZEXDOC run to a fixed, committed T-state window**
  (NOT to completion) as the "integration-realistic mix" analog of Klaus; **Z80-W2 = a hand-written tight Z80
  arithmetic/branch kernel** committed as a `byte[]` (the analog of the 6502 W2 kernel), run to a fixed cycle cap.
  Rationale: the two-axis split is the established, reviewer-legible shape, and it isolates the same two regimes
  (a realistic mixed instruction stream vs. a hot tight loop) the 6502 numbers already characterize.
- **Why ZEXDOC for W1 but NOT "to completion":** ZEXDOC/ZEXALL is ALREADY in-repo, deterministic, and proven
  (the `CpmBdosHost` drives it for both tiers; a full pass is a pinned **46,734,975,782 T-states**). BUT it is a
  *correctness exerciser*, not a throughput workload: a full pass is ~130 s in Release (× every subject × every
  re-measure = hours), its sub-tests have wildly uneven lengths, and it self-modifies test operands. So we use it
  as a **fixed-T-state-window** throughput stream (a deterministic prefix of the exerciser — same bytes, same
  start, capped at e.g. 2.0–5.0 B T-states so a representative spread of sub-tests runs), NOT run-to-banner. This
  keeps W1 "a realistic mixed stream of real Z80 code" while staying minutes, not hours. It also stays honest:
  cycles/sec is a rate, so a deterministic capped prefix is a fair measurement (the exact same reasoning the
  existing `SubprocessRunner` already uses for the third-party 6502 subjects — see `bench/README.md` fairness
  rule 3).
- **Alternative A (W1 = a known Z80 benchmark instead of ZEX):** e.g. a Z80 port of a fixed-work kernel
  (CoreMark-ish, or a "ZEXALL-free" synthetic). Rejected for the baseline: nothing else is already in-repo +
  deterministic + tier-proven; adding one is net-new provenance work that blocks the days-not-weeks baseline.
- **Alternative B (W2 only, skip W1):** ship just the hand-written kernel. Rejected: it would under-represent the
  realistic mixed-instruction regime that is exactly where a future hot-op JIT's block-formation wins show up —
  the W1/W2 split is what made the 6502 story legible.
- **NOTE on metric per workload:** Z80 cycles are **T-states**; 6502 cycles are machine cycles. These are NOT
  cross-architecture comparable as raw numbers (see D4) — the headline is the **per-CPU before/after ratio**, which
  is unit-free, plus the cross-LANGUAGE spread within a single CPU. The report must label the Z80 unit "T-states"
  explicitly.

**D2 — Integrate third-party Z80 reference emulators now, or defer (skip-with-note)? — RESOLVED: INTEGRATE NOW (user decision, 2026-06-15).**
- **RESOLUTION: integrate the available third-party Z80 references INTO Milestone A**, so the first committed
  `REPORT.md` Z80 section ships the cross-language axis (C / C# / JS) alongside our Tier-0/Tier-1 rows — the full
  Z80 picture in the first commit, matching the 6502 section's shape. The "baseline ships regardless" property is
  preserved by the EXISTING fairness rule, not weakened: any single third-party ref that cannot be integrated
  cleanly (toolchain absent, license, source unavailable) is a **skip-with-note** row, and the 6502+Z80 our-tiers
  baseline still commits. The third-party refs ENRICH the table; they MUST never block it (Task A10 hard-codes this
  as the definition-of-done gate, and Tasks A6–A8 each implement their adapter as skip-with-note). The old
  "Milestone B" is folded into Milestone A as Tasks A6–A8; later milestones renumber (B = 68000/8086, C = M6 re-measure).
- **Rationale for integrate-now (the user's reasons, recorded):** (i) the first committed Z80 section is then a
  complete cross-language story, not a two-row teaser; (ii) the cross-language spread is the most reviewer-legible
  headline (it is what made the 6502 report interesting); (iii) the adapter seams already exist for the 6502 —
  z80emu/C mirrors `Fake6502Adapter`, a C# core mirrors `Asm6502Adapter`, a JS core mirrors `JsEmulatorAdapter` —
  so the marginal engineering is "clone an adapter + author a glue runner", not new infrastructure; (iv) skip-with-
  note makes integrate-now low-risk: a missing toolchain degrades a row, it does not block the commit.
- **The named candidates (license + integration mechanism + provenance/availability risk) — implemented in Tasks A6 (C#) / A7 (C) / A8 (JS):**
  - **C — `superzazu/z80`** (MIT). A single-file (`z80.h`/`z80.c`) cycle-accurate C Z80, ZEXALL/ZEXDOC-proven by
    its own author. Mirrors `fake6502`: fetched-not-vendored into `<benchcache>/z80c/`, compiled-once-cached by a
    `cc`/`gcc`/`clang`, run as a subprocess via `SubprocessRunner`. **Risk: LOW** — permissive license, tiny self-
    contained source, no deps; needs a C compiler present (absent ⇒ skip-with-note, exactly like fake6502).
    *Alternative C core: `kpetan/z80emu` (Lin Ke-Fong, MIT-like)* — also single-translation-unit + ZEXALL-proven;
    pick `superzazu/z80` first for the cleaner single-header API, fall back to z80emu if its fetch URL is unstable.
  - **C# — `Zem80` (Adam Klemmt, `Zem80_core` on NuGet, MIT)** OR **`Z80dotNet` (Konamiman, MIT, NuGet
    `Z80dotNet`)**. In-process via NuGet at build time, mirrors `Asm6502Adapter` (`Probe` false-with-note when the
    package/feature is disabled; an offline disable switch like `-p:UseZ80Sharp=false`). **Risk: LOW–MEDIUM** —
    permissive licenses; the one check Builder must do (Task A6) is that the chosen core exposes a steppable core +
    a readable T-state/cycle count + settable PC/SP/registers (both expose an `IZ80` / `Z80Processor` with a step
    + a `TStatesElapsed`/`ClockCycles` surface). **Recommend `Z80dotNet` first** (mature, widely used, explicit
    `Z80Processor.TStatesElapsed`); fall back to `Zem80` if the API fit is poor. Lowest-friction subject (no
    compiler/runtime to fetch — restores via NuGet), so it is the minimal-viable-first pick.
  - **JS (OPTIONAL) — `DrGoldfire/Z80.js`** (MIT) or the `jsspeccy3` Z80 core (GPL — license-check before adopting;
    prefer the MIT `Z80.js`). A `node` subprocess, mirrors `JsEmulatorAdapter`/`sfotty` (`z80js_runner.mjs` prints
    `CYCLES n` + `WALL_SECONDS f`). **Risk: MEDIUM** — needs node present (absent ⇒ skip-with-note) AND a quick
    determinism/cycle-model check; marked OPTIONAL so it never gates Milestone A. If it does not integrate cleanly
    in the time box, it commits as a skip-with-note row and is finished as a fast-follow.
  - **(no pure-Python Z80 by default)** — the 6502 `py65` row covers the slow-language end of the spectrum; a Z80
    Python core is not worth the provenance hunt for the baseline. Add later only if a clean deterministic one
    surfaces.
- **Minimal-viable-first set (the recommended integration order within Milestone A):** **(1) the C# core
  (`Z80dotNet`)** — lowest friction, in-process, no fetch; **(2) the C core (`superzazu/z80`)** — the strongest
  cross-language anchor, one compile-once-cached subprocess; **(3) the JS core — OPTIONAL**, time-boxed, commits as
  skip-with-note if it does not land cleanly. Our two tiers + #1 + #2 are the target for the first commit; #3
  enriches it if it fits the time box.
- **Cost against the "baseline in days" goal (honest estimate):** the our-tiers Z80 baseline (Tasks A1–A5 + the
  smoke/capture A9–A10) is the days-scale core and is unchanged by this decision. Each third-party adapter
  (Tasks A6–A8) adds roughly: **C# (A6) ≈ +0.5 day** (clone `Asm6502Adapter`, wire the NuGet core's step+cycle
  surface, the disable switch); **C (A7) ≈ +1 day** (author the `z80c_runner.c` glue + the fetch arm + clone
  `Fake6502Adapter`); **JS (A8) ≈ +0.5–1 day** (glue runner + clone `JsEmulatorAdapter` + the determinism check).
  So integrate-now adds ≈ **+1.5 days** for the C#+C minimal set (≈ +2.5 with JS) on top of the our-tiers baseline.
  Because each is skip-with-note, the user can ALSO choose to commit the our-tiers baseline first (Tasks A1–A5 +
  A9–A10 green, with A6–A8 rows skip-with-note) and let the third-party rows fill in over the following day or two
  WITHOUT a separate milestone — the report is regenerable, so a later `--all` re-run on the canonical host just
  enriches the same committed file. **This preserves "baseline in days" while delivering the cross-language table
  in the same Milestone A.**
- **Alternative (the prior recommendation — defer to a separate fast-follow milestone):** REJECTED by the user.
  Recorded for history: deferring kept the first deliverable shortest, but the user prefers the complete cross-
  language Z80 table in the first committed baseline, and skip-with-note already de-risks integrate-now.

**D3 — Machine normalization: pin to one machine, or commit relative ratios?**
- **Recommendation: BOTH, layered.** (i) Treat the **per-CPU before/after RATIO** (and the within-report
  cross-subject ratios) as the **primary, machine-independent deliverable** — a ratio cancels host speed, so the
  M6 speedup claim survives running "before" and "after" on different machines/years. (ii) ALSO pin a **canonical
  reference machine** in the report header (the existing `## Environment` block already captures CPU/OS/.NET) and
  recommend the user re-runs the M6 "after" on the SAME machine when feasible, so the absolute cycles/sec rows are
  directly comparable too. The report already records host metadata; we add a one-line "canonical baseline host"
  note + a "re-measure on this host for absolute comparability; the ratio holds regardless" caveat.
- **Alternative (pin only):** require identical hardware for any comparison. Rejected: brittle (hardware ages out),
  and unnecessary because the ratio is the real headline.
- **Alternative (ratios only):** drop absolute numbers. Rejected: the absolute cycles/sec rows are useful sanity
  checks and the cross-language spread is interesting in absolute terms; the existing report commits both.

**D4 — Headline metric: cy/s vs ns/op vs MIPS? And do we capture the all-fallback JIT baseline now?**
- **Recommendation: KEEP `cycles/sec` (emulated CPU cycles per host wall-second) as the headline**, exactly as the
  existing harness + REPORT.md use. It is already wired end-to-end (`AdapterResult.Measured(cycles, wall, …)`), it
  is the natural unit for "how fast does this emulator run target code", and the before/after RATIO of cycles/sec
  is the clean speedup number. Report the absolute wall-clock alongside (already done) for sanity. Do NOT switch to
  ns/op or MIPS — ns/op needs a stable per-op denominator across self-modifying/variable workloads (Klaus/ZEX make
  that fragile), and MIPS hides the cycle-model differences the fairness rules deliberately surface. **Label the
  Z80 cycle unit "T-states" so no reader cross-multiplies it against 6502 machine cycles.**
- **Capture the all-fallback JIT baseline NOW: YES (strongly).** It is the honest "before" half. The existing 6502
  report already commits the all-fallback-equivalent (chaining-on) JIT row at 0.00x/0.53x; the Z80 all-fallback JIT
  row will be ~1.0x the interpreter minus block overhead. Committing it is the WHOLE POINT — M6 re-measures the
  identical row and the delta IS the demonstrated speedup. Omitting it would leave nothing to subtract from.
- **Alternative (defer JIT capture to M6):** measure only the interpreter now. Rejected: it discards the "before"
  baseline for the exact tier whose improvement we want to demonstrate.

**D5 — Baseline capture: CI or manual?**
- **Recommendation: MANUAL, committed by a human, with a CI SMOKE guard.** The 6502 `REPORT.md` is manually
  committed (its `Generated (UTC)` timestamp + single-host metadata make that clear), and benchmark numbers are
  host-sensitive — auto-committing from CI would either thrash the file with noise across heterogeneous runners or
  require a dedicated pinned runner the project doesn't have. So: the **measured REPORT.md is regenerated + committed
  by hand** (the established workflow), on the canonical host (D3). SEPARATELY, add a **CI smoke** that runs the
  bench harness for a tiny bounded window on every CI invocation purely to prove "the harness still composes + self-
  verifies for every wired CPU" (it must NOT assert on throughput numbers, only on Ran==true + no divergence) —
  mirroring the existing ZEX wiring-smoke pattern (`Smoke_zexdoc_harness_composes_the_real_binary`).
- **Alternative (full CI capture):** a dedicated pinned benchmarking runner commits numbers automatically.
  Deferred as a possible future enhancement; out of scope for the baseline. Note it in the methodology as "future".

---

## Architecture: the ONE refactor that unlocks every additional CPU

The existing tier runner is **6502-hardwired**. `bench/CpuEmulator.Benchmarks/Tiers.cs` → `TierRunner.Run`
constructs `new Mos6502Cpu(space, …)`, a 16-bit `AddressSpace`, seeds `S=0xFD, P=0x34`, and detects the W1 stop by
`inner.PC` against `SuccessTrapPc`. None of that is portable to the Z80 (different ctor — two buses; different
reset state; different stop condition — the CP/M warm-boot / BDOS-CALL convention, not a PC trap).

**The lever:** BOTH cores already implement `ICpuCore` (`CpuEmulator.Core/ICpuCore.cs`): `Architecture`,
`long CycleCount`, `Step()`, `Run(ref long budget)`, `GetRegister/SetRegister(string)`. And `JittedCpu<T>` exposes
`Run(ref long)` for the Tier-1 path. So the per-CPU differences reduce to FOUR things:
1. **Construct** the CPU + its address space(s) from a `BenchWorkload` (6502: one bus; Z80: program bus + IO bus).
2. **Seed** the initial register state (6502: `PC=StartPc, S=0xFD, P=0x34`; Z80: `PC=StartPc, SP=…`, plus any
   workload-specific Page-Zero seeding for the ZEX/CP/M stream).
3. **Build the Tier-1 `JittedCpu<T>`** with the right `JitTarget` + bus handles.
4. **Detect the stop condition** (6502 W1: `PC == SuccessTrapPc`; Z80 ZEX-W1: `PC == 0x0000` warm-boot or a
   BDOS-CALL boundary serviced host-side; both W2: the fixed cycle cap).

We introduce a small **`ITierDriver`** seam (per-CPU) so `TierRunner` becomes CPU-agnostic and the existing 6502
behavior is preserved byte-for-byte. This is the spine of Tasks A1–A3; every later CPU (68000, 8086) adds a driver,
never re-touches the runner.

---

## Milestone A — the 6502+Z80 BASELINE *WITH available third-party refs* (the first concrete deliverable)

> **Definition of done:** `bench/results/REPORT.md` contains a committed, reproducible Z80 section ALONGSIDE the
> existing 6502 section, regenerable with one documented command, with the Z80 cycle unit labeled "T-states" and
> the all-fallback caveat stated. The Z80 section includes (i) OUR two tiers — Tier-0 interpreter + the all-fallback
> Tier-1 JIT — on Z80-W1 + Z80-W2 (ALWAYS), AND (ii) the **available third-party Z80 references** on the cross-
> language axis (C# in-process, C subprocess, optionally JS subprocess), each a measured row when its runtime is
> present or a **skip-with-note** row when absent (D2). 6502 numbers are UNCHANGED (the refactor is behavior-
> preserving for the 6502). `docs/user-guide/benchmarks.md` + `bench/README.md` updated.
>
> **THE LOAD-BEARING INVARIANT (Task A10 verifies it; Tasks A6–A8 each implement it): the third-party refs ENRICH the table; they NEVER block it.**
> If ANY single third-party ref cannot be integrated cleanly — C compiler absent, NuGet unreachable, node absent,
> a license/provenance problem, a determinism failure — that subject commits as a skip-with-note row and **the
> 6502+Z80 OUR-TIERS baseline still commits**. The existing `bench/README.md` skip-with-note fairness rule is the
> mechanism; this plan does NOT weaken it. No fabricated numbers, ever.
>
> **Minimal-viable-first ordering (D2):** our two tiers (Tasks A1–A5) → the C# ref `Z80dotNet` (Task A6 — lowest
> friction, in-process, no fetch) → the C ref `superzazu/z80` (Task A7 — the strongest cross-language anchor) → the
> JS ref (Task A8 — OPTIONAL, time-boxed, skip-with-note if it does not land). The user may commit the our-tiers
> baseline first (Tasks A1–A5 + A9–A10 green, the A6–A8 rows skip-with-note) and let the third-party rows fill in
> over the next day via a later `--all` regen on the canonical host — same milestone, same committed file (it is
> regenerable), preserving "baseline in days".

### Task A1 — Generalize the tier runner behind `ITierDriver` (behavior-preserving for the 6502)
- [ ] In `bench/CpuEmulator.Benchmarks/`, add `ITierDriver.cs` defining the per-CPU seam:
  ```csharp
  namespace CpuEmulator.Benchmarks;

  using CpuEmulator.Core;

  /// <summary>A live, seeded tier instance ready to be advanced + measured. Wraps an ICpuCore (the
  /// interpreter inner) and, for the JIT tier, the JittedCpu&lt;T&gt; that drives it — but exposes only
  /// what the runner needs: advance-a-slice, the cycle count, and the stop check. This is the per-CPU
  /// seam that makes TierRunner CPU-agnostic: the 6502 PC-trap and the Z80 CP/M warm-boot are both just
  /// implementations of HasStopped.</summary>
  public interface ITierInstance
  {
      long CycleCount { get; }
      /// <summary>Advance up to <paramref name="maxCycles"/> emulated cycles (Tier-0 = a Step loop or a
      /// budgeted Run; Tier-1 = JittedCpu.Run with that budget). Returns having advanced at least one
      /// instruction unless already stopped.</summary>
      void AdvanceSlice(long maxCycles);
      /// <summary>True when the workload's termination condition is met (W1: the success trap / warm-boot;
      /// W2: never — the cycle cap in TierRunner terminates). Implementations service any host-side
      /// boundary (e.g. the Z80 BDOS CALL) here or inside AdvanceSlice.</summary>
      bool HasStopped(out ushort stoppedAtPc);
  }

  /// <summary>Per-CPU factory: builds a Tier-0 (interpreter) or Tier-1 (JIT) live instance for a workload.
  /// One driver per architecture; the runner never names a concrete CPU type. The 6502 driver reproduces
  /// the existing TierRunner construction EXACTLY (same AddressSpace, same S=0xFD/P=0x34 seed, same PC
  /// trap), so the committed 6502 numbers do not move.</summary>
  public interface ITierDriver
  {
      string Architecture { get; }
      ITierInstance CreateTier0(BenchWorkload w);
      ITierInstance CreateTier1(BenchWorkload w, CpuEmulator.Jit.JitOptions options);
  }
  ```
- [ ] Add `Drivers/Mos6502TierDriver.cs` that moves the EXISTING `TierRunner.Run` 6502 logic verbatim behind the
  seam — same `AddressSpace(Program, 16)`, same `MapMemory(w.LoadAddress, w.Image.Clone(), writable:true)`, same
  `new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop){ PC=w.StartPc, S=0xFD, P=0x34 }`, same `JittedCpu<Mos6502Cpu>`
  ctor, same `BulkSlice = 8_000_000`, same PC-equals-`SuccessTrapPc` stop:
  ```csharp
  namespace CpuEmulator.Benchmarks.Drivers;

  using CpuEmulator.Core;
  using CpuEmulator.Cpus.Mos6502;
  using CpuEmulator.Jit;

  public sealed class Mos6502TierDriver : ITierDriver
  {
      public string Architecture => "mos6502";

      public ITierInstance CreateTier0(BenchWorkload w)
      {
          var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
          space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
          var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
          return new InterpInstance(cpu, w);
      }

      public ITierInstance CreateTier1(BenchWorkload w, JitOptions options)
      {
          var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
          space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
          var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
          var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space, options: options);
          return new JitInstance(cpu, jit, w);
      }

      // Tier-0: a Step loop, stops at the success-trap PC (W1) — the existing RunInterpreter shape.
      private sealed class InterpInstance(Mos6502Cpu cpu, BenchWorkload w) : ITierInstance
      {
          public long CycleCount => cpu.CycleCount;
          public void AdvanceSlice(long maxCycles)
          {
              long target = cpu.CycleCount + maxCycles;
              while (cpu.CycleCount < target)
              {
                  ushort before = cpu.PC;
                  cpu.Step();
                  if (w.FixedCycleCap is null && cpu.PC == before) return; // parked
              }
          }
          public bool HasStopped(out ushort pc) { pc = cpu.PC; return w.FixedCycleCap is null && IsParked(cpu); }
          private static bool IsParked(Mos6502Cpu c) => false; // parked-detection handled in AdvanceSlice/runner
      }

      // Tier-1: JittedCpu.Run with the slice budget; same parked-trap detection as the existing RunJit.
      private sealed class JitInstance(Mos6502Cpu cpu, JittedCpu<Mos6502Cpu> jit, BenchWorkload w) : ITierInstance
      {
          public long CycleCount => cpu.CycleCount;
          public void AdvanceSlice(long maxCycles) { long b = maxCycles; jit.Run(ref b); }
          public bool HasStopped(out ushort pc) { pc = cpu.PC; return false; }
      }
  }
  ```
  > **Implementation note for Builder:** the exact parked-trap detection currently lives in `TierRunner.RunInterpreter`
  > / `RunJit` (compare `inner.PC` before/after a slice; `VerifyTrap`). Preserve that logic — the cleanest move is to
  > keep the before/after-PC comparison and the `VerifyTrap` divergence throw IN `TierRunner` (Task A2), and have the
  > driver expose just `CreateTierN` + `CycleCount` + `AdvanceSlice`. The `HasStopped` shape above is illustrative;
  > Builder picks whichever split keeps the 6502 byte-identical. The binding constraint is the **smoke test in
  > Task A6**: the 6502 W1/W2 cycle counts + self-verification must not change.
- [ ] Rewrite `TierRunner.Run(BenchWorkload, bool jit, JitOptions)` to: pick the driver by `w` (Task A2 registry),
  create the Tier-0 or Tier-1 instance, run the same `BulkSlice`-budgeted loop it runs today, keep `VerifyTrap`,
  return the cycle count. The 6502 `Tier0.Run`/`Tier1.Run` facades in `Tiers.cs` stay as-is (they call `TierRunner`).
- [ ] **Verify:** `dotnet build bench/CpuEmulator.Benchmarks -warnaserror` clean.

### Task A2 — A driver registry keyed by workload architecture
- [ ] Add an `Architecture` tag to the workload so the runner can pick a driver without sniffing bytes. Extend
  `BenchWorkload` (in `IEmulatorAdapter.cs`) with a `string Architecture` field (default `"mos6502"` for the two
  existing 6502 workloads so nothing else changes), OR carry the arch on the workload-builder side and pass it into
  `TierRunner`. **Recommendation: add the field** — it is the least surprising and the report already groups by
  subject/workload.
  ```csharp
  public sealed record BenchWorkload(
      string Name, byte[] Image, ushort LoadAddress, ushort StartPc,
      ushort SuccessTrapPc, long? FixedCycleCap, long ExpectedCycles,
      string Architecture = "mos6502");   // NEW — selects the ITierDriver
  ```
- [ ] In `TierRunner`, hold a small dictionary `{ "mos6502" → Mos6502TierDriver, "z80" → Z80TierDriver }` and select
  by `w.Architecture`. (8086/68000 register their drivers here later — one line each.)
- [ ] **Verify:** the existing 6502 W1/W2 workloads still resolve to the 6502 driver; build clean.

### Task A3 — The Z80 tier driver (the new CPU wiring)
- [ ] Add `Drivers/Z80TierDriver.cs`. Reuse the proven construction from `tests/.../Zex/CpmBdosHost.cs`: a 16-bit
  program `AddressSpace` + a 16-bit `Io` space; `new Z80Cpu(mem, io)`; `JittedCpu<Z80Cpu>(cpu, Z80Cpu.JitTarget,
  mem, io)` for Tier-1. Seed via `SetRegister("PC", w.StartPc)` / `SetRegister("SP", …)`.
  ```csharp
  namespace CpuEmulator.Benchmarks.Drivers;

  using CpuEmulator.Core;
  using CpuEmulator.Cpus.Z80;
  using CpuEmulator.Jit;

  public sealed class Z80TierDriver : ITierDriver
  {
      public string Architecture => "z80";

      public ITierInstance CreateTier0(BenchWorkload w) => Build(w, jit: false, new JitOptions());
      public ITierInstance CreateTier1(BenchWorkload w, JitOptions options) => Build(w, jit: true, options);

      private static ITierInstance Build(BenchWorkload w, bool jit, JitOptions options)
      {
          var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
          mem.MapMemory(0x0000, (byte[])w.Image.Clone(), writable: true);
          var io = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
          var cpu = new Z80Cpu(mem, io);
          cpu.SetRegister("PC", w.StartPc);
          cpu.SetRegister("SP", 0xFFFE);     // a sane stack; W1's ZEX prefix sets up its own early
          JittedCpu<Z80Cpu>? j = jit ? new JittedCpu<Z80Cpu>(cpu, Z80Cpu.JitTarget, mem, io) : null;
          return new Z80Instance(cpu, mem, j, w);
      }

      /// <summary>The Z80 stop condition is workload-dependent: Z80-W2 (the kernel) terminates on the
      /// fixed cycle cap (no host service); Z80-W1 (the ZEXDOC prefix) needs the CP/M BDOS-CALL service +
      /// the warm-boot sentinel — the SAME convention CpmBdosHost implements. We fold a minimal BDOS
      /// service into AdvanceSlice for W1 so the prefix runs real ZEX code; W1 terminates on the cycle
      /// WINDOW (D1), not the warm-boot banner, so the service only needs fn-2/fn-9 console-out + RET.</summary>
      private sealed class Z80Instance(Z80Cpu cpu, AddressSpace mem, JittedCpu<Z80Cpu>? jit, BenchWorkload w)
          : ITierInstance
      {
          public long CycleCount => cpu.CycleCount;

          public void AdvanceSlice(long maxCycles)
          {
              long target = cpu.CycleCount + maxCycles;
              while (cpu.CycleCount < target)
              {
                  ushort pc = (ushort)cpu.GetRegister("PC");
                  if (w.UsesCpmBdos())                       // W1 only — see workload flag (Task A4)
                  {
                      if (pc == 0x0000) return;              // warm-boot sentinel (early-stop guard)
                      if (pc == 0x0005) { ServiceBdos(cpu, mem); continue; }
                  }
                  if (jit is not null) { long b = 1; jit.Run(ref b); }   // budget-1: exact PC surfacing
                  else cpu.Step();
              }
          }

          public bool HasStopped(out ushort pc)
          {
              pc = (ushort)cpu.GetRegister("PC");
              return w.UsesCpmBdos() && pc == 0x0000;        // W2 never stops here (cap terminates)
          }

          // fn-2 (console out, char in E) + fn-9 ($-string at DE), then host-side RET — lifted verbatim
          // from CpmBdosHost.ServiceBdos/ReturnFromBdos (the proven convention). Console output is
          // DISCARDED here (throughput run; the correctness transcript is the ZEX test's job).
          private static void ServiceBdos(Z80Cpu cpu, AddressSpace mem) { /* …port of CpmBdosHost… */ }
      }
  }
  ```
  > **Builder note:** factor the BDOS service so it is shared with (not copy-pasted from) `CpmBdosHost` if a clean
  > shared home exists; otherwise a small private port is acceptable (it is ~15 lines and the test version stays the
  > correctness source of truth). The throughput run discards the console transcript — only cycle count + wall time
  > matter. The budget-1 JIT idiom mirrors `CpmBdosHost.Run` (every all-fallback block == one op, so PC surfaces at
  > the BDOS boundary exactly).
- [ ] **Verify:** `dotnet build` clean; a scratch run of Z80-W2 advances + returns a plausible T-state count.

### Task A4 — The two Z80 workloads (W1 = ZEXDOC prefix, W2 = arithmetic kernel)
- [ ] Add Z80 workloads to `Workloads.cs` (a new `Z80Workloads` static or new methods), mirroring the 6502 W1/W2 shape:
  - **Z80-W1 (ZEXDOC prefix):** locate `zexdoc.com` via the existing cache scheme (`CPUEMULATOR_TESTVECTORS` →
    `<root>/zex/zexdoc.com`, the same path `ZexVectors.TryGetBinaryPath` uses); when absent, return `null` and the
    runner SKIPS W1 with the fetch instruction (`tools/get-zexall.ps1`), exactly like `Workloads.KlausOrNull()`.
    Build a 64 KiB image with the `.com` loaded at `0x0100`, Page-Zero seeded (`0x0000` warm-boot sentinel,
    `0x0005` = `RET`), `StartPc = 0x0100`, `FixedCycleCap = <the committed Z80-W1 window>` (a constant chosen so a
    representative spread of ZEX sub-tests runs — recommend starting at `2_000_000_000` T-states and pinning the
    exact value after the first measured run; document it as the committed window). Tag `Architecture = "z80"`,
    `UsesCpmBdos = true`.
  - **Z80-W2 (arithmetic kernel):** a hand-written tight Z80 ADD/SUB/`DJNZ`-loop kernel committed as a `byte[]`
    (the analog of the 6502 W2 — touches a small scratch area, exercises the carry/flag ALU path, loops via a
    taken branch which is the hot chain edge a future block-JIT stresses). `StartPc = 0x0100` (or `0x0000`),
    `FixedCycleCap = <Z80-W2 cap>` (recommend `50_000_000` T-states to mirror the 6502 W2 cap's order of
    magnitude), `Architecture = "z80"`, `UsesCpmBdos = false`. Include a short comment block with the assembled
    mnemonics + opcode bytes (the 6502 W2 does this — it is the readability contract).
  - Add a `bool UsesCpmBdos` flag to `BenchWorkload` (default `false`) so the Z80 driver knows whether to run the
    BDOS service path. (Alternatively carry it on the Z80 workload only — Builder's choice; the field default keeps
    the 6502 path untouched.)
- [ ] **Choosing the committed windows is load-bearing — record them as named constants with a comment** ("the
  committed Z80-W1 window — a deterministic ZEXDOC prefix; re-measure uses this EXACT value so before/after are the
  same work"). The M6 re-measure (Milestone C) MUST reuse these exact constants unchanged.
- [ ] **Verify:** with the ZEX binary fetched, Z80-W1 builds a workload; without it, `Workloads.Z80W1OrNull()`
  returns null and the runner prints the skip + fetch command.

### Task A5 — Wire the Z80 workloads into the runner + report (incl. the per-architecture adapter set)
- [ ] In `Program.cs`, after the 6502 W1/W2 workloads are added, add the Z80 workloads to the same `workloads`
  list (Z80-W1 only when its binary is present; Z80-W2 always). The existing per-workload loop already measures
  Tier-0 + Tier-1 for each — now via the driver registry, so OUR-tier Z80 rows appear automatically.
- [ ] **Make the third-party adapter set architecture-aware.** Today `BenchHarness.DefaultAdapters()` returns the
  four 6502 shims unconditionally, and `Program.cs` runs them for EVERY workload under `--all`. Generalize it to
  pick the adapter set by `w.Architecture` so the 6502 workloads get the 6502 shims and the Z80 workloads get the
  Z80 shims (Tasks A6–A8):
  ```csharp
  // BenchHarness.cs — adapters keyed by architecture (default = the existing 6502 set).
  public static IReadOnlyList<IEmulatorAdapter> AdaptersFor(string architecture) => architecture switch
  {
      "z80"  => [ new Z80SharpAdapter(), new Z80CAdapter(), new Z80JsAdapter() ],   // A6/A7/A8
      _      => [ new Asm6502Adapter(), new Fake6502Adapter(), new Py65Adapter(), new JsEmulatorAdapter() ],
  };
  ```
  In `Program.cs`'s `--all` branch, call `BenchHarness.AdaptersFor(w.Architecture)` instead of `DefaultAdapters()`.
  `DefaultAdapters()` stays (now delegating to `AdaptersFor("mos6502")`) so nothing else moves. Each Z80 adapter
  `Probe`s for its runtime/source and **skip-with-notes when absent** — the existing `MeasureAdapters` loop already
  records a "not run — {reason}" row and never throws, so a missing C compiler / unreachable NuGet / absent node
  degrades exactly one row (the load-bearing invariant).
- [ ] **The report needs a per-CPU view.** Today `ReportWriter` renders one flat results table + one "JIT vs
  interpreter speedup" block that pairs interp/JIT per workload. That generalizes for free (Z80 workloads + Z80
  third-party subjects are just more rows + more pairs). ADD: (i) the Z80 cycle unit must read "T-states" — either a
  per-row unit column or a per-CPU sub-heading ("### 6502 — cycles" / "### Z80 — T-states"); recommend **grouping
  the results table by architecture** so the unit + the all-fallback caveat + the third-party Z80 rows sit with the
  right CPU. (ii) Under the Z80 speedup pairs, emit the **all-fallback caveat** automatically: "Z80 Tier-1 is all-
  fallback (no hot-op IL emit yet, M6); a ratio ≈1.0× minus block overhead is EXPECTED and is the committed
  'before' for the M6 re-measure." (iii) The "Reading the numbers" prose's cross-language paragraph should note the
  Z80 cross-language spread too (mirroring the 6502 narrative) once those rows are present.
- [ ] **Verify:** `dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report --all` (with ZEX
  + Klaus fetched, and whichever third-party runtimes are present) produces a `REPORT.md` with both a 6502 section
  and a Z80 section; the Z80 section shows our two tiers PLUS the available third-party rows (absent ones as
  skip-with-note); Z80 rows labeled T-states; the all-fallback caveat present; and the 6502 rows numerically
  unchanged vs the prior commit (diff only the Z80 additions + the timestamp).

### Task A6 — Third-party Z80 ref #1: the C# core (in-process; mirrors Asm6502) — MINIMAL-VIABLE-FIRST
- [ ] Add `Adapters/Z80SharpAdapter.cs`, mirroring `Asm6502Adapter` (in-process via NuGet). **Recommended core:
  `Z80dotNet` (Konamiman, MIT, NuGet `Z80dotNet`)** — exposes `Z80Processor` with an `ExecuteNextInstruction()`
  step + a `TStatesElapsed` cycle surface + settable registers (PC/SP/AF/BC/DE/HL). Reference it from the bench
  project behind an MSBuild property so it can be disabled offline (mirroring `-p:UseAsm6502=false`):
  `-p:UseZ80Sharp=false`. `Probe` returns false-with-note when the package/feature is disabled or unrestorable.
  - **License:** MIT. **Mechanism:** in-process, NuGet-restored at build time (no fetch script). **Provenance/
    availability risk: LOW–MEDIUM** — the one fit-check Builder must confirm (a `Probe`-time assertion) is that the
    core can: load the workload image into its memory, set PC/SP, step instruction-by-instruction, expose a T-state
    count, AND detect the same termination (W2 cap; W1 the BDOS-CALL boundary serviced the same host-side way).
    If `Z80dotNet`'s API fit is poor, **fall back to `Zem80` (MIT, NuGet `Zem80_core`)** — same mechanism, verify
    its step + `ClockCycles`/T-state surface. Pick ONE; record which in the adapter doc-comment + the README.
  - For W1 the C# adapter services the CP/M BDOS CALL the same way the driver does (fn-2/fn-9 + host RET), so it
    runs the identical ZEXDOC prefix. Self-verify: a diverged run that under/overshoots the committed window is
    `Ran=false` ("subject diverged"), never a fast-but-wrong number (the existing honesty mechanism).
- [ ] **Verify:** with NuGet reachable, the C# Z80 rows (W1 if ZEX present, W2 always) populate in-process; with
  `-p:UseZ80Sharp=false` (or NuGet unreachable), the adapter `Probe`s false and the rows are skip-with-note —
  the rest of the report is unaffected.

### Task A7 — Third-party Z80 ref #2: the C core (compiled subprocess; mirrors fake6502) — the cross-language anchor
- [ ] Add a `fetch-subjects` arm that downloads the C Z80 source into `<benchcache>/z80c/` (NOT vendored — the
  `fake6502.c` discipline). **Recommended core: `superzazu/z80`** — a single-file (`z80.h` + `z80.c`) cycle-
  accurate C Z80, ZEXALL/ZEXDOC-proven by its author. Author `bench/third-party/z80c_runner.c` (mirrors
  `fake6502_runner.c`): `#include` the fetched source, load the workload image, implement the same termination
  (W2 cap; W1 the BDOS-CALL fn-2/fn-9 + RET service + the warm-boot/window stop), print `CYCLES n` + `WALL_SECONDS
  f`. Add `Adapters/Z80CAdapter.cs` (mirrors `Fake6502Adapter`: probe for `cc`/`gcc`/`clang` + the fetched source,
  compile-once-cached, run via `SubprocessRunner`).
  - **License:** MIT (`superzazu/z80`). **Mechanism:** fetched-not-vendored source, compiled-once-cached by a local
    C compiler, run as a subprocess (the `SubprocessRunner` `CYCLES`/`WALL_SECONDS` contract). **Provenance/
    availability risk: LOW** — permissive license, tiny self-contained source, no deps; the only environmental need
    is a C compiler (absent ⇒ skip-with-note, identical to fake6502's behavior today).
  - **Alternative C core: `kpetan/z80emu` / Lin Ke-Fong's `z80emu` (MIT-like)** — also single-translation-unit +
    ZEXALL-proven; use it if `superzazu/z80`'s fetch URL is unstable. Record the chosen core + its commit/URL in the
    fetch script comment (the provenance discipline the existing fetch-subjects scripts already follow).
- [ ] **Verify:** with a C compiler + the source fetched, the Z80 C rows populate; with no compiler or no fetched
  source, the rows are skip-with-note — baseline unaffected.

### Task A8 — Third-party Z80 ref #3: the JS core (node subprocess; mirrors sfotty) — OPTIONAL, time-boxed
- [ ] **OPTIONAL — must NOT gate Milestone A.** If a clean, deterministic, permissively-licensed node Z80 core
  exists, add `bench/third-party/z80js_runner.mjs` (mirrors `sfotty_runner.mjs`) + `Adapters/Z80JsAdapter.cs`
  (mirrors `JsEmulatorAdapter`) + a `fetch-subjects` `npm install` arm. **Recommended core: `DrGoldfire/Z80.js`
  (MIT)**; the `jsspeccy3` Z80 core is a fallback but is **GPL — license-check before adopting** (prefer the MIT
  one). The runner loads the image, services the BDOS CALL the same way, runs to the cap/window, prints `CYCLES n`
  + `WALL_SECONDS f`.
  - **License:** MIT (`DrGoldfire/Z80.js`) — verify at adoption; reject a GPL core if the bench's license posture
    requires permissive. **Mechanism:** `node` subprocess, `npm install`-fetched into `<benchcache>/node_modules`
    (the `sfotty` discipline). **Provenance/availability risk: MEDIUM** — needs node present (absent ⇒ skip-with-
    note) AND a quick determinism + cycle-model check (some JS cores are T-state-approximate). **Time-box it:** if
    it does not integrate cleanly within the Milestone-A window, commit it as a skip-with-note row and finish it as
    a fast-follow — the our-tiers + C# + C rows are already the committed cross-language baseline.
- [ ] **Verify:** with node present + a clean core, the JS Z80 rows populate; absent/unfit → skip-with-note,
  baseline unaffected.

### Task A9 — The bench smoke test (CI guard, D5) + BDN coverage
- [ ] Extend the existing bench smoke test (the one that proves "the harness runs + self-verifies" — it lives
  alongside the 6502 tiers; if none exists as an xUnit fact, add one in `tests/CpuEmulator.Tests/`) to ALSO run a
  tiny bounded Z80-W2 window on BOTH our tiers and assert `Ran == true` + no divergence — NOT a throughput
  threshold (D5: the smoke proves wiring, never asserts speed). Gate Z80-W1's smoke on the ZEX binary being present
  (skip-with-note when absent, mirroring `ZexFactAttribute`). The smoke covers OUR two tiers only — the third-party
  adapters are already self-skipping (Probe), so they need no extra smoke.
- [ ] Add Z80 benchmarks to `TierBenchmarks.cs` (the BDN harness) mirroring the 6502 ones: `Interpreter_Z80Kernel`
  / `Jit_Z80Kernel` (always), `Interpreter_Z80Zex` / `Jit_Z80Zex` (present-only). This gives the statistically-
  rigorous twin for the Z80 numbers too.
- [ ] **Verify:** `dotnet test` green (the smoke passes/skips correctly); `dotnet run … -- --bdn` lists the Z80
  benchmarks.

### Task A10 — Capture + commit the 6502+Z80 baseline (incl. available third-party refs); update docs
- [ ] On the canonical host (D3), fetch the inputs (`tools/get-klaus.*`, `tools/get-zexall.*`, and
  `bench/third-party/fetch-subjects.*` for whichever third-party refs you are integrating), run
  `dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report --all`, and COMMIT the
  regenerated `bench/results/REPORT.md` (now with the Z80 section: our two tiers + the available third-party refs).
  **Commit ONLY measured data** (fairness rule). Absent inputs are skip-with-note, never blank/fabricated:
  - ZEX binary absent ⇒ Z80-W1 rows are "not run — run tools/get-zexall".
  - NuGet unreachable / `-p:UseZ80Sharp=false` ⇒ the C# Z80 row is skip-with-note.
  - No C compiler / source not fetched ⇒ the C Z80 row is skip-with-note.
  - No node / JS core not integrated ⇒ the JS Z80 row is skip-with-note.
  **The our-tiers 6502+Z80 baseline commits regardless** (the load-bearing invariant — Task A6/A7/A8 each preserve
  it; this task asserts it as the definition-of-done gate).
- [ ] Update `bench/README.md`: add Z80 to the **Workloads** + **Subjects** rows (Z80-W1 ZEXDOC-prefix, Z80-W2
  kernel; our two tiers always run; the third-party Z80 subjects = `Z80dotNet`/C#, `superzazu/z80`/C, `Z80.js`/JS,
  each skip-with-note when absent — with their licenses + populate-instructions, mirroring the 6502 subjects
  table); state the T-state unit + the all-fallback caveat + the before/after-ratio framing (D3/D4).
- [ ] Update `docs/user-guide/benchmarks.md`: add the Z80 paragraph (the two Z80 workloads, the third-party cross-
  language subjects, the honest all-fallback finding, the M6-re-measure intent) — keep it in sync with the README
  + REPORT.
- [ ] Add a short **"## Baseline → re-measure (M6)"** section to `bench/README.md` documenting the methodology for
  the "after" run (Milestone C): the EXACT same workload constants, the same metric, the canonical host (or the
  ratio if a different host), the same command. This is the written contract the M6 re-measure follows.
- [ ] **Verify (definition-of-done gate):** `REPORT.md` diff shows 6502 rows unchanged + new Z80 rows (our two
  tiers + the available third-party refs; any absent ref as skip-with-note); the three docs cross-link consistently;
  `dotnet test` + `dotnet build -warnaserror` green. **Explicitly confirm: temporarily disabling EACH third-party
  ref one at a time (no C compiler / `-p:UseZ80Sharp=false` / no node) still produces a committable report with the
  our-tiers 6502+Z80 baseline intact** — the "baseline ships regardless" property, verified, not just asserted.

---

## Milestone B — the 68000 slot (FUTURE — gated behind M4.6) and the 8086 slot (FUTURE — gated behind M5)

> Planned now so the apparatus is ready; NOT executable until the interpreters + their JIT wiring exist. No work
> items here are dispatchable today — they are the forward sequence + the gates.

### Task B1 — 68000 (gated: M4.5c + M4.5d interpreter complete, THEN M4.6 JIT wiring)
- [ ] **Gate:** the 68000 interpreter is currently IN PROGRESS — M4.5a (MOVE) + M4.5b (integer ALU) are merged;
  **M4.5c (shift/rotate/bit/BCD/system-misc) + M4.5d (exceptions/branches/IPL/prefetch) remain**, then **M4.6**
  wires the 68000 through the JIT (all-fallback, per the M3.5-3c findings). The 68000 is NOT benchmarkable until at
  least the interpreter (M4.5a–d) is complete; the all-fallback Tier-1 baseline needs M4.6. (Source:
  `docs/superpowers/plans/2026-06-15-m4-status-and-resume.md`.)
- [ ] **When the gate opens:** add `Drivers/M68000TierDriver.cs` (construct `M68000Cpu` + its wide big-endian
  `AddressSpace`; seed the supervisor/SR/PC initial state; `JittedCpu<M68000Cpu>` with `M68000Cpu.JitTarget`).
  Add 68000-W1 + 68000-W2 workloads (W2 = a hand-written 68000 ALU/branch kernel; W1 = a deterministic mixed-
  instruction image — candidate: a fixed-window prefix of a 68000 exerciser if one is in-repo by then, else a
  larger synthetic kernel). Register the driver + adapter-set by `Architecture = "m68000"`. Capture the
  interpreter + all-fallback-JIT baseline; commit. (Optional third-party: a C 68000 like Musashi — skip-with-note.)
- [ ] **Note the cycle unit:** 68000 cycles are its own model; label distinctly; the headline stays the per-CPU
  before/after ratio.

### Task B2 — 8086 (gated: M5 milestone — not started)
- [ ] **Gate:** the 8086 is milestone **M5**, NOT STARTED (needs its own Architect pass: segmentation, ModRM
  decode, the flag model, the instruction set; its own ADRs + multi-PR arc, then its JIT wiring). Source: the M4
  status doc's forward sequence. NOT benchmarkable until M5's interpreter + JIT wiring exist.
- [ ] **When the gate opens:** add `Drivers/I8086TierDriver.cs` + 8086-W1/W2 workloads + register by
  `Architecture = "i8086"`; capture the interpreter + all-fallback-JIT baseline; commit. (Optional third-party: a C
  8086 core — skip-with-note.) The `tools/get-test-vectors-8088.ps1` already present suggests a vector source may
  exist by then for a W1 prefix candidate.

---

## Milestone C — the M6 RE-MEASUREMENT (the demonstrated speedup) — the "after"

> **Gate: M6 (hot-op IL emit) has landed** for at least one CPU (for Z80 specifically, the deferred "5-3b hot-op
> emission"; the M4 status doc places the cross-arch JIT-optimization phase — which "also folds in the deferred Z80
> 5-3b hot-op IL emission" — AFTER M5, checkpointed with the user). This milestone is the PAYOFF: it re-runs the
> IDENTICAL committed workloads and presents the delta.

### Task C1 — Re-run the identical workloads, unchanged
- [ ] On the canonical host (D3), with the SAME fetched inputs, run the SAME command
  (`dotnet run -c Release … -- --report`, `--all` for third-party). The workload CONSTANTS (Z80-W1 window,
  Z80-W2 cap, the kernels' bytes, the 6502 W1/W2) MUST be byte-identical to the baseline — they are committed
  constants; do NOT retune them for the "after" run (retuning would void the comparison). This is the contract the
  Task A10 "Baseline → re-measure" section pins.
- [ ] **Verify:** the workload definitions are unchanged in git history between the baseline commit and the
  re-measure commit (a `git diff` of `Workloads.cs` workload constants shows no change).

### Task C2 — Present the before/after delta
- [ ] Add a **"## Before/after — the M6 JIT speedup"** section to `REPORT.md` (or a sibling committed file) that, for
  each CPU + workload, shows the BASELINE Tier-1 cycles/sec (from the committed baseline — reference the baseline
  commit hash) vs the M6 Tier-1 cycles/sec, and the ratio (the demonstrated speedup). Keep the baseline rows as the
  honest "all-fallback before"; the new rows are the "hot-op-emit after". The per-CPU ratio is the headline (D3 —
  machine-independent). State honestly which CPUs got hot-op emit (the speedup) and which are still all-fallback
  (ratio ≈ 1.0, unchanged) at the time of the re-measure.
- [ ] **Verify (fairness gate):** every "after" number is measured (no fabrication); CPUs without M6 hot-op emit
  show ≈1.0× honestly; the report links the baseline commit so a reader can reproduce the subtraction.

---

## Self-review (placeholder / consistency / scope / ambiguity)

- **Placeholder scan:** no `TBD`/"implement later"/"similar to Task N" — the one acknowledged FILL-IN is the
  numeric value of the committed Z80-W1 window + Z80-W2 cap (Task A4): the plan specifies a recommended starting
  value (`2_000_000_000` / `50_000_000` T-states) AND the rule (pin the exact value after the first measured run,
  then NEVER change it). That is a measured-data decision the baseline run finalizes, not a hidden placeholder —
  Builder commits the chosen constant with a comment, and Milestone C reuses it verbatim. The BDOS-service port
  in `Z80TierDriver` is specified by reference to the verbatim source (`CpmBdosHost.ServiceBdos/ReturnFromBdos`),
  not left blank. The third-party Z80 cores (Tasks A6–A8) are NAMED with concrete recommendations + fallbacks +
  licenses, not left as "pick a core".
- **Internal consistency:** the metric (cycles/sec, D4), the normalization (ratio-primary + canonical host, D3),
  the all-fallback-now framing (D4), the integrate-now-with-skip-with-note discipline (D2 RESOLVED + the fairness
  rules), and the manual-capture + CI-smoke split (D5) are consistent across the Decisions block, the tasks, and
  Milestone C. The "third-party refs enrich but never block" invariant is stated in the D2 resolution, the
  Milestone A definition-of-done, each of Tasks A5–A8, and is explicitly VERIFIED in Task A10.
- **Scope check:** PLAN ONLY — no implementation, no benchmark runs, no `BUILDER_QUEUE` row, no branch (per the
  subagent constraints). Milestone A is the only immediately-dispatchable milestone (now including the available
  third-party Z80 refs, per the user's integrate-now decision); Milestones B (68000/8086) and C (the M6 re-measure)
  are gated future work with their gates named.
- **Ambiguity check:** the one real fork that could block Builder — the Z80 workload choice — is D1, resolved with a
  recommendation + alternatives. D2 (third-party integration) is RESOLVED to integrate-now with a named, ordered,
  minimal-viable-first set. The other three Decisions are resolved with recommendations the user can override.
  No task depends on an unresolved fork.
- **Fairness-rule compliance:** commit only measured data (A7/B4/D1); skip-with-note for absent ZEX binary
  (A4/A5) + absent third-party runtimes (B1–B3); no fabricated numbers anywhere; the all-fallback JIT row is
  captured as the honest "before", not hidden.
