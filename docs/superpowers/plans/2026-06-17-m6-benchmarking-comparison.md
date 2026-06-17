# Benchmarking Milestone B (68000 baseline) + the M6 cross-emulator comparison framework

> **STATUS: PLAN — preparatory docs, lands on `main`. No source/bench code touched by writing this.**
> **For agentic workers:** REQUIRED SUB-SKILL once scheduled — use `superpowers:subagent-driven-development` or
> `superpowers:executing-plans` to implement task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> This plan **EXTENDS** the working benchmark harness (`bench/`) shipped in Milestone A (PR #41); it does NOT invent
> a new one. Read **`bench/README.md`** (methodology + fairness rules), **`bench/results/REPORT.md`** (the committed
> 6502+Z80 baseline), and the Milestone-A plan **`docs/superpowers/plans/2026-06-15-cross-cpu-speed-benchmarking.md`**
> (its Decisions D1–D5 + the `ITierDriver` seam + the frozen-constant re-measure contract) BEFORE the first task. Every
> fairness rule and frozen constant there is **binding** here.

---

## CRITICAL scope boundary (why this is parallel-safe with the in-flight M5 / 8086 work)

This framework lives entirely in **`bench/`** (the benchmark library + reference-emulator integration + a table
generator) plus **docs**. It **MUST NOT touch `src/CpuEmulator.Generators/CpuEmitter.cs`** — that file is owned by the
concurrent **M5 (8086)** arc (PR #48 merged M5.1; M5.2 x86 decode is the first heavy `CpuEmitter` PR, in flight on
`feat/m5-2-x86-decode`). Verified file-ownership boundary:

- **This plan touches:** `bench/CpuEmulator.Benchmarks/**`, `bench/CpuEmulator.Benchmarks.Runner/**`,
  `bench/third-party/**`, `bench/results/**`, `bench/README.md`, `docs/user-guide/benchmarks.md`, this plan + a short
  comparison-methodology doc.
- **This plan does NOT touch:** anything under `src/` (no `CpuEmitter.cs`, no CPU cores, no JIT assembly), so it never
  collides with M5 or the deferred M4.5d-2b-continuation, both of which edit `src/`.
- **The 68000 interpreter + its JIT wiring already exist on `main`** (M4.5a–d-1 + M4.6, PR #46 merged `3f7ba7f`;
  M4.5d-2b foundation, PR #47 merged `637f13a`). So **Milestone B reads only already-merged 68000 production code** —
  it adds a bench *driver* + *workloads* + *adapters*, never a CPU-core edit. This is the right thing to build first:
  it establishes the "before" baseline + the reference numbers, so the optimized-JIT column is later just a re-run.

**The actual JIT-emit optimization (emitting 68000/Z80 hot-op IL, which DOES edit `CpuEmitter.cs`) is a SEPARATE,
later effort sequenced after M5.** This plan is ONLY the **measurement framework + baselines**. The "optimized JIT"
column is produced by re-running this framework's identical workloads after that later work lands (Milestone C, §6).

---

## Goal (the owner's headline deliverable)

**Comparison table(s) — per CPU (6502 / Z80 / 68000 / 8086) × per standardized workload — with columns: best existing
emulator(s) · our Tier-0 (interpreter) · our Tier-1 (JIT)**, in a normalized throughput unit, so "our optimized JIT ≈
best available" is visible head-to-head. Two halves, both built on the SAME apparatus:

1. **Milestone B — the 68000 baseline:** extend the bench suite to the 68000 (Tier-0 interpreter + Tier-1 all-fallback
   JIT). Capture **instructions/sec NOW** (data-axis correct on the merged M4.6 core); the **cycles/sec path is GATED on
   the M4.5d-2 timing axis** (ADR 0008 §6) — flagged as a dependency, not a blocker.
2. **The M6 cross-emulator comparison framework:** the **HYBRID** reference-comparison (owner decision, §3) + the
   **comparison-table generator** (§5) + the standardized workloads (§4) + the methodology (§7). Establish the BASELINE
   (current Tier-0 + all-fallback Tier-1) NOW; the "optimized JIT" column is re-measured after the later JIT-emit
   optimization, honoring Milestone A's W1/W2 frozen-constant re-measure contract (§6).

---

## Decisions baked in (owner, 2026-06-17) — restated as binding constraints

- **D-HYBRID — reference comparison is HYBRID.** Integrated head-to-head where a quality reference core is feasible to
  embed/run on the SAME workload + machine (extend Milestone A's embedded-ref pattern — evaluate **Musashi** for the
  68000, a fast C 6502, an 8086 reference), measured identically; PLUS published-throughput numbers (Musashi / MAME /
  etc.) as clearly-labeled **CONTEXT** where integration is impractical. **The table MUST visually distinguish
  head-to-head (measured here) rows from cited (published) rows.** (§3 + §5.)
- **D-DELIVERABLE — the deliverable is comparison table(s).** Per CPU × per standardized workload; columns: best
  existing emulator(s), our Tier-0, our Tier-1; normalized unit (**guest-MIPS** and/or **cycles/sec**) so "our JIT ≈
  best available" is head-to-head. Markdown for docs **AND** a machine-readable form. (§5.)
- **D1–D5 (inherited, Milestone A) remain binding:** the headline is the **per-CPU before/after RATIO** + the within-CPU
  cross-language/cross-emulator spread (D3/D4); commit **ONLY measured data**, skip-with-note for absent runtimes, **no
  fabricated numbers** (D2/D5); the `ITierDriver` seam is the per-CPU lever (Milestone-A architecture §); the W1/W2
  window constants are **FROZEN** as the re-measure contract (D4 + `bench/README.md` "Baseline → re-measure (M6)").

---

## What the recon CONFIRMED (file:line — load-bearing, verified against `main`)

| # | Fact | Evidence |
|---|---|---|
| R1 | **The `ITierDriver` seam exists and is CPU-agnostic; the driver registry already anticipates the 68000.** Adding a CPU is "register one line + add a driver + add workloads", never a runner edit. | `bench/CpuEmulator.Benchmarks/ITierDriver.cs`; `Tiers.cs:41-43` ("Each later CPU (68000, 8086) adds one line here, never re-touches the shared loop"); `Drivers/Mos6502TierDriver.cs`, `Drivers/Z80TierDriver.cs`. |
| R2 | **`BenchWorkload` already carries `Architecture` + `UsesCpmBdos`**, both defaulting to the 6502's values. A 68000 workload sets `Architecture: "m68000"`. | `IEmulatorAdapter.cs:34-43`. |
| R3 | **The 68000 interpreter + all-fallback JIT are merged + green.** `M68000Cpu(IAddressSpace bus)` — single ctor, **NO separate I/O space** (memory-mapped). Implements `ICpuCore` (`CycleCount`, `Step`, `Run(ref long)`, `Get/SetRegister`). `JittedCpu<M68000Cpu>` works (M4.6, PR #46). | `src/CpuEmulator.Cpus.M68000/M68000Cpu.cs:24`; the M4.6 plan + resume doc (PR #46 = `3f7ba7f`, 5747/0/0). |
| R4 | **The 68000 board is a 24-bit BigEndian `AddressSpace`** (16 MiB backing), constructed `new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian)` + `MapMemory(0x000000, new byte[0x1000000], writable:true)`. Registers seeded via `SetRegister("D0".."A6","USP","SSP","PC","SR")`. | `tests/.../TomHarte/M68000TomHarteRunner.cs:129-150`. |
| R5 | **The 68000 cycle/timing axis is PARTIAL on `main`.** M4.5d-2b FOUNDATION (PR #47) made **13 families cycle-exact**; **2b-continuation/2c is deferred** (resistant families: `.l`-register ALU idle, ADDQ/SUBQ-to-An, two-EA MOVE, MOVEM, MUL/DIV, control-transfer reseed; + full IPL + address-error frame). So `CycleCount` is trustworthy for SOME ops, not the whole ISA. | resume doc PR #47 close-state; ADR 0008 §6 ("until M4.5d-2 lands, 68000 benchmarks can measure *instructions/sec* but NOT cycles/sec"). |
| R6 | **The harness measures `cycles/sec` only — there is NO `instructions/sec` metric today.** `AdapterResult` carries `CyclesPerSecond` + `WallSeconds`; `ITierInstance` exposes `CycleCount`, not an instruction count. Adding instructions/sec needs a small additive seam (Task B2). | `IEmulatorAdapter.cs:8-20`; `ITierDriver.cs` (`ITierInstance.CycleCount`). |
| R7 | **The report is markdown-only; there is NO machine-readable emission and NO comparison-table (best-vs-ours) view.** `ReportWriter.Render` emits per-CPU `cycles/sec` tables + a JIT-vs-interp speedup block; it does not emit JSON, guest-MIPS, or a "best existing vs ours" column layout. Both are net-new (Tasks M2/M3). | `ReportWriter.cs:16-203` (markdown `StringBuilder`, no JSON path). |
| R8 | **The C-subprocess + fetch-not-vendored discipline is proven and reusable for a 68000 C reference (Musashi).** `Z80CAdapter` clones `Fake6502Adapter`: probe for `cc/gcc/clang` + fetched source, compile-once-cached, run via `SubprocessRunner` (`CYCLES n` / `WALL_SECONDS f`; `bdosMode` for CP/M). A Musashi adapter mirrors this exactly. | `Adapters/Z80CAdapter.cs`; `Adapters/SubprocessRunner.cs`; `third-party/z80c_runner.c`, `third-party/fetch-subjects.{sh,ps1}`. |
| R9 | **No Musashi / m68k reference tooling exists in `bench/` or `tools/`.** The only 68000 mention in `bench/` is the `Tiers.cs:41` "later CPU" comment. So 68000 reference-core integration is genuinely net-new + an open feasibility assessment (§3, §8 Q1). | `grep` of `bench/` + `tools/`. |
| R10 | **The Milestone-A skip-with-note invariant is the load-bearing safety net.** Every third-party adapter `Probe`s; absent runtime ⇒ "not run — {reason}" row; the our-tiers baseline ALWAYS commits. The harness `MeasureAdapters` loop records the skip and never throws. | `IEmulatorAdapter.Probe`; `bench/README.md` fairness rules; Milestone-A Task A10 invariant. |

**Net:** the seam, the C-subprocess pattern, the skip-with-note safety net, and the merged 68000 core are all ready.
Milestone B is **one driver + two workloads + an instructions/sec seam + (optional) a Musashi adapter**. The comparison
framework is **a normalization layer + a table generator (markdown + JSON) + a published-numbers registry** — all
additive to `ReportWriter`, none of it touching `src/`.

---

## The staged outline (one line each)

- **B1** — The `M68000TierDriver` (construct the 24-bit BE board + `M68000Cpu` + `JittedCpu<M68000Cpu>`; register `"m68000"`).
- **B2** — The instructions/sec seam (`ITierInstance.InstructionCount` + `AdapterResult` carries it) — the cycle-axis-independent metric.
- **B3** — The two 68000 workloads (m68k-W1 = a deterministic mixed-instruction image; m68k-W2 = a hand-written ALU/branch kernel), with FROZEN window constants.
- **B4** — Wire the 68000 workloads + the architecture-aware adapter set + the report grouping (cycle-unit label + the timing-axis caveat).
- **B5** — The bench smoke test (CI guard) + BDN coverage for the 68000 tiers.
- **B6** — Capture + commit the 68000 baseline (instructions/sec now; cycles/sec rows gated/labelled on M4.5d-2 status); update docs.
- **M1** — The normalization layer (guest-MIPS + cycles/sec; a `NormalizedThroughput` record) shared by every subject row.
- **M2** — The published-numbers registry (a committed, cited, machine-readable `reference-numbers.json` for CONTEXT rows).
- **M3** — The comparison-table generator: per-CPU × per-workload, columns best-existing · Tier-0 · Tier-1, head-to-head vs cited visually distinguished; markdown + machine-readable JSON.
- **M4** — The 68000 reference core (HYBRID): integrate **Musashi** head-to-head if feasible (Task M4a, time-boxed) else cite published Musashi/MAME numbers (Task M4b); skip-with-note either way.
- **M5** — Backfill the comparison-table view for 6502 + Z80 (re-use Milestone-A measured rows; add cited rows where head-to-head is impractical).
- **C1** — (FUTURE, gated on the later JIT-emit optimization) re-run the identical frozen workloads; the "optimized JIT" column.
- **C2** — (FUTURE) present the before/after delta + the "our JIT ≈ best available" headline in the comparison table.

---

## Milestone B — the 68000 baseline

> **Definition of done:** `bench/results/REPORT.md` contains a committed, reproducible **68000 section** alongside the
> 6502 + Z80 sections, regenerable with one documented command. The 68000 section includes OUR two tiers — Tier-0
> interpreter + the all-fallback Tier-1 JIT — on m68k-W1 + m68k-W2, reporting **instructions/sec** (always; cycle-axis-
> independent) AND **cycles/sec** (labelled with the M4.5d-2 timing-axis coverage caveat — see B4). 6502 + Z80 numbers
> are UNCHANGED. `docs/user-guide/benchmarks.md` + `bench/README.md` updated. The Musashi reference is integrated
> head-to-head **or** cited (M4) — either way skip-with-note never blocks the baseline.

### Task B1 — The `M68000TierDriver` (the new CPU wiring)

**Files:**
- Create: `bench/CpuEmulator.Benchmarks/Drivers/M68000TierDriver.cs`
- Modify: `bench/CpuEmulator.Benchmarks/Tiers.cs` (register the driver — ONE line, per the existing comment at `:41`)

Mirror `Z80TierDriver` (R1), but the 68000 has **NO I/O space** (R3) — a single bus, no `io` argument. The board is
24-bit BigEndian (R4). The driver reproduces the M4.6 / TomHarte construction so the JIT path is the proven one.

- [ ] **Step 1:** Add `Drivers/M68000TierDriver.cs`:

```csharp
namespace CpuEmulator.Benchmarks.Drivers;

using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Jit;

/// <summary>The 68000 tier driver (Milestone B). Constructs a 24-bit BigEndian program AddressSpace
/// (16 MiB backing) + an M68000Cpu (no separate I/O space — the 68000 is memory-mapped), and, for
/// Tier-1, the all-fallback JittedCpu&lt;M68000Cpu&gt; proven byte-identical to the interpreter in M4.6
/// (PR #46). Every 68000 op falls back to inner.Step in M4, so a green tier-parity baseline measures the
/// generic compiler's dispatch overhead honestly — the "before" the later 68000 hot-op IL emit subtracts
/// from. The 68000 cycle/timing axis is PARTIAL on main (M4.5d-2b foundation; 2b-continuation deferred):
/// CycleCount is exact for the cycle-exact families but not the whole ISA, so the INSTRUCTION count
/// (Task B2) is the cycle-axis-independent metric the baseline leads with; the cycles/sec row carries the
/// timing-axis-coverage caveat (Task B4).</summary>
public sealed class M68000TierDriver : ITierDriver
{
    public string Architecture => "m68000";

    public ITierInstance CreateTier0(BenchWorkload w) => Build(w, jit: false, new JitOptions());
    public ITierInstance CreateTier1(BenchWorkload w, JitOptions options) => Build(w, jit: true, options);

    private static ITierInstance Build(BenchWorkload w, bool jit, JitOptions options)
    {
        var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        mem.MapMemory(0x000000, new byte[0x1000000], writable: true);
        // The image is loaded at LoadAddress (0 for both 68000 workloads — a full 16 MiB image is wasteful,
        // so the workload provides a SMALL image copied at LoadAddress, NOT a 16 MiB byte[]; see B3).
        for (int i = 0; i < w.Image.Length; i++)
            mem.Write8((uint)((w.LoadAddress + i) & 0xFFFFFF), w.Image[i]);
        var cpu = new M68000Cpu(mem);
        cpu.SetRegister("PC", w.StartPc);
        cpu.SetRegister("SR", 0x2700);             // supervisor, interrupts masked — a benign live SR
        cpu.SetRegister("SSP", 0x00FFFC);          // a sane supervisor stack near the top of a small image's RAM
        JittedCpu<M68000Cpu>? j = jit ? new JittedCpu<M68000Cpu>(cpu, M68000Cpu.JitTarget, mem) : null;
        return new M68000Instance(cpu, j, w);
    }

    /// <summary>A 68000 tier instance. Tier-0 steps; Tier-1 runs the all-fallback JIT a slice at a time.
    /// Stop is the FIXED CYCLE/INSTRUCTION CAP for both workloads (no host-service boundary — neither 68000
    /// workload uses a CP/M-style monitor; the kernels spin via a back-edge and the cap terminates).
    /// Reports BOTH the cycle count (for cycles/sec) and the instruction count (Task B2, for the
    /// cycle-axis-independent instructions/sec the 68000 baseline leads with).</summary>
    private sealed class M68000Instance(M68000Cpu cpu, JittedCpu<M68000Cpu>? jit, BenchWorkload w) : ITierInstance
    {
        public long CycleCount => cpu.CycleCount;
        public long InstructionCount { get; private set; }   // Task B2 adds this to ITierInstance

        public void AdvanceSlice(long maxCycles)
        {
            // The 68000 baseline drives by INSTRUCTION budget when the workload is instruction-capped
            // (B3 sets FixedCycleCap, but the 68000 ALSO carries an instruction cap so the
            // instructions/sec measurement is timing-axis-independent). For the all-fallback JIT, one
            // block == one instruction (the M4.6 invariant), so InstructionCount increments per Run too.
            long target = cpu.CycleCount + maxCycles;
            while (cpu.CycleCount < target)
            {
                if (jit is not null) { long b = 1; jit.Run(ref b); }   // budget-1: one instruction per block (M4.6)
                else cpu.Step();
                InstructionCount++;
            }
        }

        public bool HasStopped(out ushort pc) { pc = (ushort)cpu.GetRegister("PC"); return false; }  // cap terminates
    }
}
```

> **Builder note:** the exact `ITierInstance` shape (whether `AdvanceSlice` takes a cycle budget or an instruction
> budget) follows what `TierRunner` actually does today — READ `Tiers.cs` `TierRunner.Run` + the existing
> `Mos6502TierDriver`/`Z80TierDriver` `*Instance` classes and match their idiom EXACTLY (the 6502/Z80 numbers must not
> move). The `InstructionCount` property is the one additive member Task B2 adds to the `ITierInstance` interface; if
> `TierRunner` drives by cycle budget, B2 records instructions alongside (the count is free — increment per Step / per
> budget-1 Run). The budget-1 JIT idiom is the M4.6 invariant (one block = one fallback instruction); confirm it
> against `M68000TomHarteRunner.RunCaseThroughJit` (the proven driver).

- [ ] **Step 2:** Register the driver in `Tiers.cs` `Drivers` dictionary — ONE line beside the `mos6502`/`z80` entries
  (the `:41-43` comment already reserves this): `["m68000"] = new Drivers.M68000TierDriver(),`.
- [ ] **Verify:** `dotnet build bench/CpuEmulator.Benchmarks -warnaserror` clean; a scratch run of an m68k-W2 workload
  (B3) advances + returns a plausible instruction + cycle count; the 6502/Z80 drivers are untouched.

### Task B2 — The instructions/sec seam (the cycle-axis-independent metric)

**Files:**
- Modify: `bench/CpuEmulator.Benchmarks/ITierDriver.cs` (add `long InstructionCount { get; }` to `ITierInstance`)
- Modify: `bench/CpuEmulator.Benchmarks/IEmulatorAdapter.cs` (`AdapterResult` carries an optional instructions/sec)
- Modify: `bench/CpuEmulator.Benchmarks/Tiers.cs` (`TierRunner` records the instruction count beside the cycle count)
- Modify: existing `Mos6502TierDriver` + `Z80TierDriver` `*Instance` classes (implement `InstructionCount` — trivial)

**Why:** ADR 0008 §6 + R5 — until M4.5d-2 (the full prefetch/timing axis) lands, the 68000's `cycles/sec` is only
trustworthy for the cycle-exact families, NOT the whole ISA. **`instructions/sec` is data-axis-correct on the merged
M4.6 core RIGHT NOW** (each `Step` / each budget-1 `Run` is exactly one instruction). So the 68000 baseline LEADS with
instructions/sec; cycles/sec is reported too but labelled with the coverage caveat (B4). This metric is also useful for
the 6502/Z80 (it normalizes cleanly to **guest-MIPS** in M1) — so add it for all CPUs, defaulting harmlessly.

- [ ] **Step 1:** Add `long InstructionCount { get; }` to `ITierInstance` (R6). Implement it on all three `*Instance`
  classes (6502 / Z80 / 68000) — increment per `Step()` and per budget-1 `Run()` iteration. For the 6502/Z80 this is
  additive and does not change any measured `cycles/sec` (the count is recorded, not used in the existing math).
- [ ] **Step 2:** Extend `AdapterResult` with an optional instruction throughput so third-party + our-tier rows can both
  carry it. Keep `CyclesPerSecond` as-is (no existing row moves):

```csharp
// IEmulatorAdapter.cs — additive: instructions/sec alongside cycles/sec. Default 0 = "not reported"
// (a subject/metric that has no instruction count, e.g. a subprocess subject that only prints CYCLES).
public readonly record struct AdapterResult(
    bool Ran,
    double CyclesPerSecond,
    double WallSeconds,
    string Note,
    double InstructionsPerSecond = 0);   // NEW — 0 means "not reported" (cycle-only subjects)
{
    public static AdapterResult Skipped(string reason) => new(false, 0, 0, reason);

    public static AdapterResult Measured(long cycles, double wallSeconds, string note) =>
        new(true, wallSeconds > 0 ? cycles / wallSeconds : 0, wallSeconds, note);

    /// <summary>A measured subject reporting BOTH cycles and instructions over the window (our tiers + any
    /// reference core that surfaces an instruction count). The 68000 baseline leads with instructions/sec
    /// because the cycle axis is partial (M4.5d-2 gating); guest-MIPS (M1) normalizes off this field.</summary>
    public static AdapterResult MeasuredWithInstructions(long cycles, long instructions, double wallSeconds, string note) =>
        new(true, wallSeconds > 0 ? cycles / wallSeconds : 0, wallSeconds, note,
            wallSeconds > 0 ? instructions / wallSeconds : 0);
}
```

- [ ] **Step 3:** In `TierRunner`, when our tiers run, capture `instance.InstructionCount` over the measured window and
  build the row via `MeasuredWithInstructions`. Third-party adapters that surface an instruction count (a Musashi build
  that prints `INSTRUCTIONS n`, see M4) use it too; subprocess subjects that print only `CYCLES`/`WALL_SECONDS` leave it
  0 (the `SubprocessRunner` is extended in M4 to OPTIONALLY parse an `INSTRUCTIONS n` line — additive, defaulting 0).
- [ ] **Verify:** build clean; the existing 6502/Z80 `cycles/sec` rows are numerically UNCHANGED (the instruction count
  is additive); a scratch 68000 run reports a non-zero `InstructionsPerSecond`.

### Task B3 — The two 68000 workloads (FROZEN window constants)

**Files:**
- Create: `bench/CpuEmulator.Benchmarks/Workloads.cs` → add a `M68000Workloads` static (mirrors `Z80Workloads`)

Mirror the 6502/Z80 W1/W2 shape, with one structural difference: the 68000 board is 16 MiB (R4), so the workload
provides a **small image** (a few KiB) copied at `LoadAddress` by the driver (B1), NOT a 16 MiB `byte[]` (the 6502/Z80
workloads carry a 64 KiB image because their board IS 64 KiB; the 68000 driver allocates the 16 MiB board and copies
the small image in).

- [ ] **m68k-W2 (arithmetic/branch kernel) — ALWAYS runs (no external dependency):** a hand-written 68000 ALU + branch
  loop committed as a `byte[]`, run to a FROZEN cap. The 68000 is big-endian and word-decoded, so each opword is two
  bytes high-byte-first. Use a tight `ADD`/`SUB`/`DBcc` (or `SUBQ` + `Bcc`) inner loop with a back-edge so it spins; the
  taken branch is the hot chain edge a future block-JIT stresses (the same rationale as the 6502/Z80 W2). Carry BOTH a
  `FixedCycleCap` AND a frozen `InstructionCap` (the instruction budget the instructions/sec window uses — timing-axis-
  independent). Include the assembled mnemonics + opword bytes in a comment block (the readability contract the 6502/Z80
  W2 kernels follow). Example shape (Builder verifies the exact opwords against the 68000 encoding + a real run):

```
;   D0 = accumulator, D1 = inner counter
;   00001000  MOVEQ #0,D0        7000           D0 = 0
;   00001002  MOVE.W #$0100,D1   323C 0100      D1 = 256 (inner counter)
;   inner (00001006):
;   00001006  ADDQ.W #7,D0       5C40           D0 += 7   (ALU + flags)
;   00001008  SUBQ.W #3,D0       5740           D0 -= 3
;   0000100A  EORI.W #$5A5A,D0   0A40 5A5A       mix
;   0000100E  SUBQ.W #1,D1       5341           D1--
;   00001010  BNE.S inner        66F4           loop inner (taken back-edge — the hot chain edge)
;   00001012  BRA.S start        60EC           restart forever (the cap terminates)
```

> **Builder note:** the opwords above are illustrative — the implementer MUST assemble + verify them against the merged
> 68000 interpreter (run the kernel through `M68000Cpu` once and confirm it loops + the data result is sane) before
> committing. Prefer opcodes that are in the M4.5d-2b **cycle-exact family set** (R5: NOP, MOVE.q, ADD.b, SUB.b, CMP.w,
> AND.b, OR.w, etc.) where possible, so the m68k-W2 `cycles/sec` row is cycle-trustworthy (not just instruction-
> trustworthy). If the kernel must use a not-yet-cycle-exact op (e.g. `SUBQ`-to-Dn idle), the cycles/sec row carries the
> coverage caveat (B4) and the instructions/sec row is the trustworthy headline.

- [ ] **m68k-W1 (deterministic mixed-instruction image) — runs when its source is present, else skip-with-note:** the
  "integration-realistic mixed stream" analog of Klaus / the ZEXDOC-prefix. **Sourcing — pick the lowest-friction option
  that is deterministic + in-repo or fetchable (record the choice + provenance in a comment):**
  - **Option A (RECOMMENDED default): a larger hand-written synthetic mixed kernel** — a 68000 program that exercises a
    representative spread (MOVE variants, ALU reg/EA, shifts, Bcc/DBcc/JSR/RTS, a LINK/UNLK frame) in a deterministic
    loop, run to a frozen instruction cap. Rationale: NO external dependency, deterministic, ALWAYS runs, and it is the
    fastest path to a committed 68000 baseline (the 8086/68000 don't have an in-repo Klaus/ZEX equivalent the way the
    6502/Z80 do). This is the analog of "W2 but broader" — a mixed instruction stream rather than a tight hot loop.
  - **Option B (if a deterministic 68000 exerciser is fetchable by then):** a fixed-instruction-window PREFIX of a 68000
    functional exerciser, loaded from the vector cache (the Klaus/ZEX fetch-not-vendored pattern). The 680x0 SingleStep
    vectors are per-instruction cases (not a runnable stream), so they are NOT a W1 source. If a runnable 68000 test ROM
    (e.g. a CP/M-68K or a bare-metal exerciser) is identified, use it as a fixed-window prefix with a small host monitor
    (mirroring the Z80 BDOS service). **Flag as §8 open question Q2 — Option A ships the baseline regardless.**
- [ ] **Freeze the constants** as named `const` with the re-measure comment (the Milestone-A discipline): e.g.
  `M68000W2CycleCap`, `M68000W2InstructionCap`, `M68000W1InstructionCap`. The later JIT-emit re-measure (Milestone C)
  reuses these EXACT values unchanged. Record the chosen values after the first measured run (the Milestone-A "pin after
  first run, then never change" rule) — recommend starting `M68000W2InstructionCap = 50_000_000` (mirrors the order of
  magnitude of the 6502/Z80 W2) and pinning after the first run.
- [ ] **Verify:** m68k-W2 builds a workload always; m68k-W1 (Option A) builds always (no dependency) OR (Option B)
  returns null + skip-with-note when its source is absent.

### Task B4 — Wire the 68000 workloads + the architecture-aware adapter set + the report grouping

**Files:**
- Modify: `bench/CpuEmulator.Benchmarks.Runner/Program.cs` (add the 68000 workloads to the list)
- Modify: `bench/CpuEmulator.Benchmarks/BenchHarness.cs` (`AdaptersFor("m68000")` — the 68000 reference set)
- Modify: `bench/CpuEmulator.Benchmarks/ReportWriter.cs` (the 68000 cycle-unit label + the timing-axis caveat + the
  instructions/sec column)

- [ ] **Step 1:** In `Program.cs`, after the 6502 + Z80 workloads, add the 68000 workloads (m68k-W2 always; m68k-W1 per
  B3's sourcing). The existing per-workload loop measures Tier-0 + Tier-1 for each via the driver registry — so the
  OUR-tier 68000 rows appear automatically (R1).
- [ ] **Step 2:** Extend `BenchHarness.AdaptersFor(architecture)` with the `"m68000"` set: `[ new MusashiAdapter() ]`
  (Task M4; skip-with-note when absent, so it is harmless before M4 lands — it just produces a "not run" row). The
  6502/Z80 sets are unchanged.
- [ ] **Step 3:** `ReportWriter` — add `"m68000" => "68000"` to `ArchLabel` and `"m68000" => "cycles"` to `UnitLabel`
  (68000 cycles are its own model — distinctly labelled, NOT cross-multiplied against 6502 machine cycles or Z80
  T-states, per D4). Add an **instructions/sec column** to the per-CPU results table (only populated when
  `InstructionsPerSecond > 0`) and emit the **68000 timing-axis caveat** automatically under the 68000 section:

> _68000 cycles/sec is reported for completeness but the cycle/timing axis is PARTIAL on `main` (M4.5d-2b foundation;
> the 2b-continuation is deferred): `CycleCount` is exact for the cycle-exact families, not the whole ISA. The 68000
> baseline's trustworthy headline is **instructions/sec** (data-axis-correct on the merged M4.6 core). Full cycle-exact
> 68000 cycles/sec gates on the M4.5d-2 timing axis (ADR 0008 §6) — the re-measure picks it up automatically when it
> lands._

- [ ] **Verify:** `dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report` produces a REPORT.md
  with a 68000 section (our two tiers, instructions/sec + cycles/sec, the caveat present); 6502 + Z80 rows numerically
  unchanged (diff only the 68000 additions + the timestamp).

### Task B5 — The bench smoke test (CI guard) + BDN coverage

**Files:**
- Modify: the bench smoke test (the "harness composes + self-verifies" fact — mirror the Z80 smoke added in Milestone A)
- Modify: `bench/CpuEmulator.Benchmarks.Runner/.../TierBenchmarks.cs` (the BDN harness) — add `Interpreter_M68000Kernel`
  / `Jit_M68000Kernel`

- [ ] **Step 1:** Extend the bench smoke to run a tiny bounded m68k-W2 window on BOTH our tiers and assert `Ran == true`
  + no divergence — NOT a throughput threshold (D5: the smoke proves wiring, never asserts speed). m68k-W1 Option B (if
  chosen) gates its smoke on its source being present (skip-with-note when absent); Option A always runs.
- [ ] **Step 2:** Add the 68000 BDN benchmarks mirroring the 6502/Z80 ones, so the statistically-rigorous twin covers
  the 68000 tiers too.
- [ ] **Verify:** `dotnet test` green (the smoke passes/skips correctly); `dotnet run … -- --bdn` lists the 68000
  benchmarks.

### Task B6 — Capture + commit the 68000 baseline; update docs

**Files:**
- Modify: `bench/results/REPORT.md` (regenerated — the 68000 section added)
- Modify: `bench/README.md` (the 68000 workloads + subjects rows + the timing-axis caveat + instructions/sec note)
- Modify: `docs/user-guide/benchmarks.md` (the 68000 paragraph)

- [ ] **Step 1:** On the canonical host (D3), run `dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner --
  --report --all` (with whatever reference runtimes are present — Musashi if M4 landed) and COMMIT the regenerated
  REPORT.md. **Commit ONLY measured data** (R10): m68k-W1 Option B absent ⇒ skip-with-note; Musashi absent ⇒
  skip-with-note; the our-tiers 68000 baseline commits regardless.
- [ ] **Step 2:** Update `bench/README.md`: add the 68000 to the **Workloads** + **Subjects** tables (m68k-W1, m68k-W2;
  our two tiers always; the Musashi reference as head-to-head-or-cited per M4, skip-with-note when absent — with its
  license + populate-instructions, mirroring the Z80 subjects table). State the 68000 cycle unit + the **timing-axis
  caveat** + the **instructions/sec headline** + the before/after-ratio framing.
- [ ] **Step 3:** Update `docs/user-guide/benchmarks.md`: add the 68000 paragraph (the two workloads, the reference, the
  instructions/sec-now-cycles/sec-later finding, the M6 re-measure intent) in sync with the README + REPORT.
- [ ] **Verify (definition-of-done gate):** REPORT.md diff shows 6502 + Z80 rows unchanged + new 68000 rows; the three
  docs cross-link consistently; `dotnet test` + `dotnet build -warnaserror` green; **disabling the Musashi reference
  (or m68k-W1 Option B's source) still produces a committable report with the our-tiers 68000 baseline intact** — the
  "baseline ships regardless" property, verified, not just asserted.

---

## The comparison framework (the M6 cross-emulator comparison deliverable)

> This is the layer that turns the per-CPU `cycles/sec` rows into the owner's headline: **per CPU × per workload,
> best-existing vs our-Tier-0 vs our-Tier-1, in a normalized unit, head-to-head vs cited visually distinguished, in
> markdown + a machine-readable form.** It is purely additive to `ReportWriter` + a small set of new files; it touches
> NO `src/` code and NO CPU core.

### Task M1 — The normalization layer (guest-MIPS + cycles/sec)

**Files:**
- Create: `bench/CpuEmulator.Benchmarks/NormalizedThroughput.cs`

**Why (D-DELIVERABLE):** the table needs a **normalized unit** so "our JIT ≈ best available" is visible. Two units,
both already derivable from the measured rows:
- **guest-MIPS** = millions of GUEST INSTRUCTIONS executed per host wall-second (`InstructionsPerSecond / 1e6`). This is
  the **cross-CPU-comparable** unit (an instruction is an instruction; it does NOT depend on each CPU's cycle model), so
  it is the headline normalization for the comparison table. It is exactly the metric the 68000 baseline can produce NOW
  (B2) without the timing axis.
- **cycles/sec** (the existing metric) — per-CPU, in the CPU's own cycle unit (machine cycles / T-states / 68000 cycles),
  NOT cross-CPU comparable, reported alongside as the sanity check + the within-CPU spread.

- [ ] **Step 1:** Add a `NormalizedThroughput` record + a pure function deriving it from an `AdapterResult` + the
  workload's architecture:

```csharp
namespace CpuEmulator.Benchmarks;

/// <summary>The normalized throughput of one subject on one workload, for the comparison table
/// (D-DELIVERABLE). guest-MIPS (millions of GUEST INSTRUCTIONS / host wall-second) is the
/// cross-CPU-comparable headline — an instruction is an instruction regardless of the CPU's cycle model,
/// so "our JIT vs the best existing emulator" is an apples-to-apples MIPS comparison. CyclesPerSecond is
/// the CPU's own cycle unit (machine cycles / T-states / 68000 cycles) — NOT cross-CPU comparable, kept
/// for the within-CPU sanity check + spread. A subject that reports no instruction count (cycle-only
/// subprocess subjects) has GuestMips == null (the table shows "—" for its MIPS cell and ranks it by
/// cycles/sec within its CPU only).</summary>
public readonly record struct NormalizedThroughput(double? GuestMips, double CyclesPerSecond)
{
    public static NormalizedThroughput From(AdapterResult r) =>
        new(r.InstructionsPerSecond > 0 ? r.InstructionsPerSecond / 1_000_000.0 : null, r.CyclesPerSecond);
}
```

> **Honest disclosure baked into the methodology (§7):** guest-MIPS across DIFFERENT emulators of the SAME CPU on the
> SAME workload bytes is a fair apples-to-apples comparison (same instructions retired, different host speed). guest-MIPS
> across DIFFERENT CPUs is indicative only (a 68000 instruction does more work than a 6502 instruction) — the table
> groups by CPU and the cross-CPU comparison is explicitly framed as "throughput class", not a race. This mirrors the
> existing "do NOT cross-multiply T-states vs machine cycles" rule.

- [ ] **Verify:** unit test the `From` mapping (instructions present ⇒ MIPS; absent ⇒ null); build clean.

### Task M2 — The published-numbers registry (the cited CONTEXT rows)

**Files:**
- Create: `bench/results/reference-numbers.json` (a committed, hand-curated, **cited** registry)
- Create: `bench/CpuEmulator.Benchmarks/ReferenceNumbers.cs` (loads + validates the registry)

**Why (D-HYBRID):** where a reference core is impractical to integrate head-to-head, the table carries a **clearly-
labelled CITED row** with a published throughput number + its source. This is the "PLUS published-throughput numbers as
CONTEXT" half of the hybrid. It is **hand-curated + committed** (not fetched/measured) — so it MUST carry provenance
(source URL/citation, the host the number was measured on, the date) and be visually distinct from head-to-head rows.

- [ ] **Step 1:** Define the JSON schema (an array of cited reference points):

```jsonc
// bench/results/reference-numbers.json — CITED context numbers (NOT measured here). Each row is a published
// throughput figure with provenance; the table renders these as "cited" rows, visually distinct from the
// head-to-head (measured-here) rows. Hand-curated; updated only with a source + a citation. NO fabricated numbers.
[
  {
    "cpu": "m68000",
    "subject": "Musashi (C)",
    "guestMips": null,            // fill ONLY from a cited measurement; null = "cited cycles/sec only"
    "cyclesPerSecond": null,      // fill from the cited source; null = "no comparable published figure"
    "note": "published throughput — see source",
    "source": "https://github.com/kstenerud/Musashi (and/or a cited benchmark post)",
    "measuredOn": "the host/config the cited number used (verbatim from the source)",
    "citedDate": "2026-06-17"
  }
]
```

- [ ] **Step 2:** `ReferenceNumbers.Load()` reads + validates the registry (every row MUST have a `source`; a row
  missing provenance is REJECTED with a clear error — the no-fabrication discipline enforced in code). Returns rows
  keyed by `(cpu, subject)`.
- [ ] **Verify:** loading the committed registry succeeds; a synthetic row missing `source` is rejected; build + a unit
  test green.

### Task M3 — The comparison-table generator (markdown + machine-readable)

**Files:**
- Create: `bench/CpuEmulator.Benchmarks/ComparisonTableWriter.cs`
- Modify: `bench/CpuEmulator.Benchmarks/ReportWriter.cs` (call the comparison-table writer into a new report section)
- Create: `bench/results/comparison.json` (the machine-readable emission — committed)

**Why (D-DELIVERABLE):** this is the headline. For each CPU × each standardized workload, render a table whose columns
are **best existing emulator(s) · our Tier-0 (interpreter) · our Tier-1 (JIT)** in the normalized unit, with
head-to-head rows (measured here) visually distinct from cited rows.

- [ ] **Step 1:** The table schema (the load-bearing design — see §5 for the full spec). The generator:
  - groups all rows by CPU, then by workload;
  - for each (CPU, workload) cell, picks the **best existing emulator** = the highest-throughput THIRD-PARTY subject
    that ran head-to-head on THIS workload (measured here), and lists our Tier-0 + our Tier-1;
  - if a head-to-head reference is absent for that CPU/workload, it falls back to the **cited** number (M2), rendered
    with a `[cited]` marker + a footnote linking the source;
  - the primary cell value is **guest-MIPS** (the cross-CPU-comparable headline) with the per-CPU **cycles/sec** in
    parentheses or a secondary column;
  - a final **"our Tier-1 vs best existing" ratio** column makes "our JIT ≈ best available" a single readable number.

- [ ] **Step 2:** Render BOTH forms from the same in-memory model:
  - **markdown** — appended to `REPORT.md` as a new `## Comparison — our emulator vs the best existing` section (per-CPU
    sub-tables), with a legend distinguishing **measured here (head-to-head)** vs **cited (published context)**.
  - **machine-readable** — `bench/results/comparison.json`: a stable, versioned schema (see §5) a downstream tool /
    dashboard can consume. Committed alongside REPORT.md.

- [ ] **Step 3:** Wire it into `ReportWriter.Render` (additive — a new section after the existing per-CPU results +
  speedup blocks). The existing sections are unchanged.
- [ ] **Verify:** running `--report --all` emits the comparison section in REPORT.md + `comparison.json`; a unit test
  feeds synthetic rows (one CPU, our two tiers + a head-to-head ref + a cited ref) and asserts the markdown + JSON shape
  (best-existing selection, the head-to-head/cited marker, the ratio column); build + test green.

### Task M4 — The 68000 reference core (HYBRID: Musashi head-to-head, else cited)

**Files (head-to-head path):**
- Create: `bench/third-party/musashi_runner.c` (the C glue — mirrors `z80c_runner.c`, R8)
- Create: `bench/CpuEmulator.Benchmarks/Adapters/MusashiAdapter.cs` (mirrors `Z80CAdapter`)
- Modify: `bench/third-party/fetch-subjects.{sh,ps1}` (a Musashi fetch arm — fetched-not-vendored)
- Modify: `bench/CpuEmulator.Benchmarks/Adapters/BenchPaths.cs` (a `MusashiSource` path)
- Modify: `bench/CpuEmulator.Benchmarks/Adapters/SubprocessRunner.cs` (OPTIONALLY parse `INSTRUCTIONS n` — additive)

**Files (cited fallback path):**
- Modify: `bench/results/reference-numbers.json` (a cited Musashi/MAME row, M2)

**Decision (D-HYBRID): attempt head-to-head Musashi integration, time-boxed; fall back to cited if it does not land.**

- [ ] **Task M4a (head-to-head — RECOMMENDED, time-boxed):** integrate **Musashi** (`kstenerud/Musashi`, MIT — a fast,
  widely-used C 68000 used by MAME-class projects) as a compiled subprocess, mirroring `Z80CAdapter` exactly (R8):
  - fetch-not-vendored into `<benchcache>/musashi/` via a `fetch-subjects` arm (record the commit/URL — the provenance
    discipline);
  - author `musashi_runner.c`: configure Musashi's memory callbacks over the loaded image, set PC/SR/SSP, run to the
    frozen instruction cap (count instructions via Musashi's instruction hook / `m68k_execute` return), print
    `CYCLES n` + `INSTRUCTIONS n` + `WALL_SECONDS f`;
  - `MusashiAdapter` probes for `cc/gcc/clang` + the fetched source, compiles-once-cached, runs via `SubprocessRunner`;
    absent compiler/source ⇒ skip-with-note (identical to fake6502/Z80C).
  - **Feasibility risk: MEDIUM** — Musashi is C + MIT + self-contained, BUT it uses a code-generation step
    (`m68kmake.c` generates `m68kops.c` from `m68k_in.c`) which adds a build step the Z80/6502 single-file cores don't
    have; AND the instruction-count surface needs the instruction hook wired. **Time-box M4a; if the build/instruction-
    count surface doesn't land cleanly in the box, ship M4b (cited) and finish M4a as a fast-follow.** (See §8 Q1.)
- [ ] **Task M4b (cited fallback):** add a cited Musashi (and/or MAME) 68000 throughput row to `reference-numbers.json`
  (M2) with a real source + provenance. The comparison table (M3) renders it as a `[cited]` context row. This guarantees
  the 68000 comparison table has a "best existing" column even if head-to-head Musashi doesn't land in the box.
- [ ] **Verify:** with a C compiler + Musashi fetched, the 68000 reference row populates head-to-head (M4a) with both
  cycles/sec + instructions/sec; absent ⇒ the cited row (M4b) carries the "best existing" column; either way the
  baseline is unaffected (R10).

### Task M5 — Backfill the comparison-table view for 6502 + Z80

**Files:**
- Modify: `bench/results/reference-numbers.json` (cited rows where head-to-head is impractical)
- (No new code — M3's generator already renders every CPU; this task ensures the 6502/Z80 comparison cells are populated)

- [ ] **Step 1:** The 6502 + Z80 already have head-to-head references measured here (fake6502/Asm6502/sfotty/py65 for the
  6502; superzazu/z80, Z80dotNet, Z80.js for the Z80 — see the committed REPORT.md). The comparison table (M3) picks the
  best of these as "best existing" automatically. **The one gap:** these were measured for `cycles/sec`; the
  instructions/sec column needs the subprocess runners to ALSO print `INSTRUCTIONS n` (the additive `SubprocessRunner`
  change in M4). For the 6502/Z80 cited-only context (e.g. a published MAME 6502 number), add cited rows to M2.
- [ ] **Step 2 (optional, additive):** extend the 6502/Z80 glue runners (`fake6502_runner.c`, `z80c_runner.c`,
  `sfotty_runner.mjs`, `z80js_runner.mjs`, `py65_runner.py`, `Asm6502Adapter`, `Z80*Adapter`) to print/return an
  instruction count where the subject exposes one. Where a subject does NOT expose an instruction count, its MIPS cell is
  "—" and it ranks by cycles/sec within its CPU (the `NormalizedThroughput.GuestMips == null` path, M1). **Honest:** not
  every third-party subject can report instructions; the table discloses this per-cell.
- [ ] **Verify:** the comparison table renders all three (4 with the future 8086) CPUs with a populated "best existing"
  column (head-to-head where measured, cited where not); the legend distinguishes the two; build + test green.

---

## Milestone C — the re-measure (the "optimized JIT" column) — FUTURE, gated

> **Gate:** the later **JIT-emit optimization** (emitting 68000 / Z80 / 6502 hot-op IL — the effort that DOES edit
> `src/CpuEmulator.Generators/CpuEmitter.cs`, sequenced AFTER M5) has landed for at least one CPU. This milestone is the
> PAYOFF: it re-runs the IDENTICAL frozen workloads and fills the "optimized JIT" column, head-to-head against the best
> existing emulator.

### Task C1 — Re-run the identical frozen workloads (FUTURE)
- [ ] On the canonical host (D3), with the SAME fetched inputs, run the SAME command. The workload CONSTANTS — the 6502
  W1/W2, the Z80 `Z80W1WindowTStates`/`Z80W2CycleCap`, the new `M68000W2CycleCap`/`M68000W2InstructionCap`/
  `M68000W1InstructionCap`, and the kernel bytes — MUST be byte-identical to the baseline (the Milestone-A frozen-
  constant contract, `bench/README.md` "Baseline → re-measure (M6)"). Retuning a window voids the comparison.
- [ ] **Verify:** a `git diff` of the workload constants between the baseline commit and the re-measure commit shows no
  change.

### Task C2 — The before/after delta + the "our JIT ≈ best available" headline (FUTURE)
- [ ] The comparison table (M3) now shows, per CPU × workload: best existing · our Tier-0 · our Tier-1 (optimized), with
  the **our-Tier-1-vs-best-existing ratio** as the headline number — the owner's deliverable made visible. The
  before/after section shows the baseline (all-fallback) Tier-1 vs the optimized Tier-1 (reference the baseline commit
  hash). CPUs that did NOT yet get hot-op emit at re-measure time show ≈1.0× honestly.
- [ ] **Verify (fairness gate):** every "after" number is measured (no fabrication); CPUs without hot-op emit show ≈1.0×
  honestly; the report links the baseline commit so a reader can reproduce the subtraction.

---

## §3 — Reference-core feasibility, per CPU (the HYBRID assessment)

The HYBRID rule: **integrate head-to-head where a quality reference core is feasible to embed/run on the same workload +
machine; cite published numbers as CONTEXT where integration is impractical.** Per-CPU assessment (recon-grounded):

| CPU | Head-to-head reference (measured here) | Mechanism | Feasibility | Cited fallback (CONTEXT) |
|---|---|---|---|---|
| **6502** | fake6502 (C), Asm6502 (C#), sfotty (JS), py65 (Python) — **ALREADY INTEGRATED** (Milestone A) | in-process (C#) / compiled subprocess (C) / node / python | **DONE** — measured rows in the committed REPORT.md | a published MAME/VICE 6502 number, if desired (M2) |
| **Z80** | superzazu/z80 (C), Z80dotNet (C#), Z80.js (JS) — **ALREADY INTEGRATED** (Milestone A) | compiled subprocess / in-process / node | **DONE** — measured rows in the committed REPORT.md | a published MAME Z80 number, if desired (M2) |
| **68000** | **Musashi (C)** — Task M4a | compiled subprocess (mirrors `Z80CAdapter`, R8) | **MEDIUM** — MIT, fast, widely-used, BUT a code-gen build step + the instruction-count hook to wire; time-boxed (§8 Q1). Fallback: cite (M4b) | published Musashi/MAME 68000 throughput (M4b) |
| **8086** | a C 8086 reference (candidate: a fast 8086/8088 C core) — **FUTURE, gated on M5** | compiled subprocess | **OPEN** — needs an evaluation pass when M5's interpreter + JIT land (§8 Q3); `tools/get-test-vectors-8088.ps1` suggests a vector source exists | published MAME/8086 throughput |

**Principled defaults (non-interactive):** integrate Musashi head-to-head for the 68000 IF the build + instruction-count
surface land in the time box; otherwise cite. The 6502/Z80 head-to-head refs are already done. The 8086 reference is
deferred to its own evaluation when M5 completes (this plan does NOT block on it — it is gated future work, §8 Q3).

---

## §4 — The standardized workload set

The discipline (inherited from Milestone A): **the SAME workload bytes run identically across our tiers AND every
reference core**, a warmup pass precedes the measured window, and the window is FROZEN (the re-measure contract).

| CPU | W1 (mixed-instruction stream) | W2 (hot ALU/branch kernel) | Source / build |
|---|---|---|---|
| **6502** | Klaus functional-test image → `$3469` trap (`KlausExpectedCycles = 96,241,367`) | hand-written ADC/SBC + branch loop (`ArithKernelCycleCap = 50,000,000`) | Klaus fetched-not-vendored; W2 committed `byte[]` — DONE |
| **Z80** | ZEXDOC PREFIX → frozen T-state window (`Z80W1WindowTStates = 2,000,000,000`) | hand-written ADD/SUB + DJNZ loop (`Z80W2CycleCap = 50,000,000`) | ZEX fetched; W2 committed `byte[]` — DONE |
| **68000** | Option A (default): a larger hand-written **mixed** kernel (MOVE/ALU/shift/Bcc/JSR/LINK), frozen instruction cap; Option B (if a deterministic exerciser is fetchable): a fixed-instruction-window prefix | hand-written ADD/SUB + DBcc/Bcc loop, frozen `M68000W2CycleCap` + `M68000W2InstructionCap` | W1/W2 committed `byte[]` (Option A) — Task B3 |
| **8086** | FUTURE (gated on M5) — candidate: a fixed-window prefix of an 8088 exerciser (8088 vectors fetched) | FUTURE — hand-written kernel | gated on M5 |

**Why the 68000 W1 is a synthetic mixed kernel, not a Klaus/ZEX equivalent:** the 6502 (Klaus) and Z80 (ZEXDOC) have
in-repo runnable deterministic exercisers; the 68000 does NOT (the 680x0 SingleStepTests are per-instruction cases, not
a runnable stream — R3 context). So the lowest-friction deterministic mixed-instruction W1 for the 68000 is a
hand-written broad kernel (Option A). This is honest + reproducible + dependency-free, and it ships the baseline now;
Option B (a fetchable 68000 test ROM) is an enhancement if a clean one surfaces (§8 Q2). **Dhrystone-class note:** a
Dhrystone or CoreMark port per CPU would be the gold-standard "all emulators run identical compute" workload, but it
needs a per-CPU C toolchain + a deterministic build — recorded as a future enhancement (§8 Q4), NOT the baseline (it
would block "baseline now" on cross-CPU toolchain provenance).

---

## §5 — The comparison-table schema (the deliverable's shape)

**Markdown (in `REPORT.md`, a new `## Comparison — our emulator vs the best existing` section):** per-CPU sub-tables.
Example shape (the 68000 sub-table, illustrative numbers):

```
### 68000 — guest-MIPS (cross-CPU-comparable); cycles/sec in its own model

| Workload    | Best existing            | our Tier-0 (interp) | our Tier-1 (JIT) | Tier-1 vs best |
|-------------|--------------------------|---------------------|------------------|----------------|
| m68k-W2     | Musashi (C) 95.2 MIPS ‡  | 41.0 MIPS           | 39.5 MIPS †      | 0.41×          |
| m68k-W1     | Musashi (C) 88.0 MIPS ‡  | 33.1 MIPS           | 31.8 MIPS †      | 0.36×          |

‡ = measured here, head-to-head (same workload bytes, same host).  [cited] = published context (see footnote).
† = Tier-1 is ALL-FALLBACK (no hot-op IL emit yet); this is the committed "before" for the re-measure.
cycles/sec reported in the per-CPU results table above; 68000 cycle axis is PARTIAL (M4.5d-2 gating).
```

- **Columns:** Workload · **Best existing** (subject + normalized value + a `‡`/`[cited]` marker) · **our Tier-0** ·
  **our Tier-1** · **Tier-1 vs best** (the ratio — the "our JIT ≈ best available" headline).
- **Primary unit:** guest-MIPS (cross-CPU-comparable). cycles/sec lives in the existing per-CPU results table (the CPU's
  own unit) + optionally a secondary column here.
- **Head-to-head vs cited:** `‡` = measured here (same workload + host); `[cited]` = published context with a footnote
  citing the source + the host it was measured on (M2). The legend states this explicitly.
- **All-fallback marker:** `†` on the current Tier-1 (baseline) so the ≈1.0×/below-1× ratio is read as the honest
  "before", not a defect — until the optimized-JIT re-measure (Milestone C) replaces it.

**Machine-readable (`bench/results/comparison.json`):** a stable, versioned schema:

```jsonc
{
  "schemaVersion": 1,
  "generatedUtc": "2026-06-17T00:00:00Z",
  "host": { "cpu": "...", "os": "...", "dotnet": "..." },     // mirrors the REPORT.md ## Environment block
  "cpus": [
    {
      "cpu": "m68000",
      "cycleUnit": "68000 cycles",
      "timingAxisPartial": true,                              // the M4.5d-2 caveat, machine-readable
      "workloads": [
        {
          "workload": "m68k-W2",
          "rows": [
            { "subject": "Musashi (C)",          "kind": "head-to-head", "guestMips": 95.2, "cyclesPerSecond": 0, "allFallback": false, "source": null },
            { "subject": "our Tier-0 interpreter","kind": "ours",        "guestMips": 41.0, "cyclesPerSecond": 0, "allFallback": false, "source": null },
            { "subject": "our Tier-1 JIT",        "kind": "ours",        "guestMips": 39.5, "cyclesPerSecond": 0, "allFallback": true,  "source": null }
          ],
          "bestExisting": "Musashi (C)",
          "tier1VsBest": 0.41
        }
      ]
    }
  ]
}
```

- `kind` ∈ `{ "head-to-head", "cited", "ours" }` — the visual/data distinction the owner required.
- `cited` rows carry a non-null `source` (provenance enforced by M2's validator).
- `allFallback` marks the current Tier-1 baseline so a downstream consumer renders the before/after correctly.

---

## §7 — Methodology (the apples-to-apples discipline)

All inherited from `bench/README.md` (binding) + the additions this plan makes:

- **Warm-up:** a warmup pass precedes the measured window for every subject (BenchmarkDotNet for our tiers via `--bdn`;
  an explicit warmup slice for the others) — UNCHANGED.
- **Repetition / variance:** `--bdn` is the statistically-rigorous twin (warmup + measurement windows + variance); the
  `--report` warmed-`Stopwatch` pass produces the headline rows. The 68000 tiers get `--bdn` coverage (B5).
- **The frozen-constant contract:** the 6502/Z80 windows are FROZEN (Milestone A); the new 68000 constants are FROZEN on
  first measurement (B3) and reused byte-identically by the re-measure (C1). A `git diff` of the constants between
  baseline + re-measure must show no change (the contract the README's "Baseline → re-measure (M6)" section pins).
- **Normalization:** guest-MIPS is the cross-CPU-comparable headline (M1); cycles/sec is the per-CPU sanity check in the
  CPU's own unit. The per-CPU before/after RATIO + the within-CPU spread are the machine-independent deliverables (D3).
- **Host-machine pinning:** the canonical reference host is recorded in the REPORT.md `## Environment` block + the
  `comparison.json` `host` object; re-measure on the same host for directly-comparable absolutes, else the ratio holds
  regardless (D3). The Musashi subprocess is launch-cheap (native) so its launch overhead amortizes over the window (the
  `SubprocessRunner` fairness note, R8).
- **Apples-to-apples for the integrated refs:** SAME workload bytes, SAME termination (frozen cap/window), SAME host. A
  reference core that diverges (finishes at a wildly different instruction/cycle count, or fails to run the image) is
  `Ran=false` ("subject diverged"), NEVER a fast-but-wrong number (the `SubprocessRunner` + adapter honesty mechanism,
  R8/R10). Different correct emulators use their OWN cycle model — so cycles/sec is indicative cross-emulator; guest-MIPS
  (same instructions retired) is the tighter apples-to-apples comparison.
- **The all-fallback honesty:** every CPU's current Tier-1 (6502 on its invalidation strategy; Z80 + 68000 all-fallback)
  is the honest "before". The comparison table marks it (`†`); the re-measure replaces it. No fabricated "optimized"
  numbers before the optimization lands.
- **68000 timing-axis caveat:** instructions/sec is the trustworthy 68000 headline NOW; cycles/sec is reported with the
  M4.5d-2-coverage caveat (B4) and becomes fully cycle-exact automatically when the timing axis lands (ADR 0008 §6).
- **Capture: manual + CI smoke (D5):** the REPORT.md + comparison.json are regenerated + committed by hand on the
  canonical host; CI runs a tiny bounded smoke that proves the harness composes for every wired CPU (asserts `Ran==true`
  + no divergence, NEVER a throughput threshold) — the 68000 smoke is B5.

---

## §8 — Open questions for the owner

1. **Q1 — Musashi head-to-head: integrate, or cite?** RECOMMENDED default = attempt head-to-head (M4a), time-boxed,
   falling back to cited (M4b). The risk is Musashi's code-generation build step (`m68kmake` generates `m68kops.c`) + the
   instruction-count hook — heavier than the single-file Z80/6502 C cores. **Owner fork:** is the head-to-head Musashi
   number worth the extra build complexity in the bench fetch/compile path, or is a cited Musashi/MAME number sufficient
   for the 68000 "best existing" column? (The plan ships a populated 68000 comparison column either way.)
2. **Q2 — The 68000 W1 source:** RECOMMENDED default = a hand-written synthetic MIXED kernel (Option A — dependency-free,
   ships the baseline now). **Owner fork:** is there a preferred deterministic 68000 exerciser / test ROM (CP/M-68K, a
   bare-metal functional test) the owner wants as the W1 "integration-realistic" stream instead (Option B)? If so, name
   it + its source and W1 becomes a fixed-instruction-window prefix of it (with a small host monitor).
3. **Q3 — The 8086 reference core:** gated on M5 (interpreter + JIT). **Owner fork:** which 8086/8088 C reference should
   the 8086 evaluation target when M5 lands (a specific fast C 8086 core)? This plan defers the 8086 comparison row to
   that evaluation; flag whether you want it pre-scoped now or when M5 completes.
4. **Q4 — Dhrystone/CoreMark-class compute workloads:** the gold-standard "all emulators run identical compiled compute"
   workload per CPU, but it needs a per-CPU C toolchain + a deterministic build (cross-CPU provenance work). RECOMMENDED:
   defer as a future enhancement; the hand-written W1/W2 kernels + Klaus/ZEX are the baseline. **Owner fork:** is a
   Dhrystone-per-CPU pass a priority for the headline (it would make the "best existing vs ours" comparison more
   recognizable to outside readers), or is the current workload set sufficient?
5. **Q5 — guest-MIPS vs cycles/sec as the table's PRIMARY unit:** RECOMMENDED = guest-MIPS primary (cross-CPU-comparable;
   producible now without the 68000 timing axis), cycles/sec secondary (per-CPU sanity). **Owner fork:** confirm
   guest-MIPS is the headline unit for the comparison table, or prefer cycles/sec primary (which would gate the 68000
   headline on the M4.5d-2 timing axis).

---

## Self-review (placeholder / consistency / scope / ambiguity)

- **Placeholder scan:** no `TBD` / "implement later" / "similar to Task N". The acknowledged FILL-INs are measured-data
  decisions the first run finalizes (the 68000 frozen window constants — B3 gives a recommended start + the "pin then
  never change" rule; the illustrative m68k-W2 opwords — B3 tells the implementer to assemble + verify against the
  merged core before committing; the illustrative comparison-table numbers — §5 are shape examples, not data). The
  Musashi reference is NAMED with its repo + license + mechanism + the concrete `Z80CAdapter` template to clone, not left
  as "pick a core". The published-numbers registry enforces provenance in code (M2), so no cited row can be fabricated.
- **Internal consistency:** the metric (guest-MIPS primary + cycles/sec secondary, M1/§5/§7), the HYBRID reference
  approach (head-to-head ‡ vs cited [cited], §3/§5/M2/M4), the all-fallback framing (`†`, §5/§7/C2), the frozen-constant
  re-measure contract (B3/C1/§7, inherited from Milestone A), the 68000 timing-axis caveat (instructions/sec now,
  cycles/sec gated — B2/B4/§7, grounded in ADR 0008 §6 + R5), and the manual-capture + CI-smoke split (B5/§7, D5) are
  consistent across the Decisions block, the tasks, and the schema.
- **Scope check:** PLAN ONLY — no implementation, no benchmark runs, no source/bench edits, no branch (per the operating
  mode). Milestone B + the comparison framework (M1–M5) are immediately schedulable (they read only merged 68000
  production code + extend `bench/`); Milestone C (the re-measure) is gated on the later JIT-emit optimization, with its
  gate named. **The parallel-safety boundary is explicit + verified:** `bench/` + docs only; `src/CpuEmitter.cs` and all
  of `src/` untouched (R3/R9 + the scope-boundary section) — so this runs concurrently with M5 (8086) and the deferred
  M4.5d-2b-continuation without collision.
- **Ambiguity check:** the genuine forks are collected in §8 (Q1 Musashi integrate-vs-cite; Q2 the 68000 W1 source; Q3
  the 8086 ref; Q4 Dhrystone; Q5 the primary unit), each with a principled non-interactive default so NO task blocks on
  an unresolved fork — the baseline ships on the defaults; the owner's answers refine (not gate) it.
- **Fairness-rule compliance:** commit only measured data (B6/M2/C); skip-with-note for absent Musashi + absent m68k-W1
  Option-B source (B3/B4/M4/R10); provenance enforced for cited rows (M2); the all-fallback Tier-1 captured as the honest
  "before", not hidden (§5/§7); guest-MIPS vs cycles/sec cross-CPU caveat stated (M1/§7); the 68000 timing-axis partial
  state disclosed (B4/§7).
```
