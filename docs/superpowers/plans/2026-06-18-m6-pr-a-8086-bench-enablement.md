# M6 PR-A — 8086 bench + profile enablement (the §0.3 measurement dependency)

> **STATUS: PLAN — preparatory doc. The implementation touches ONLY `bench/` + `bench/hotop-profiler/` +
> bench tests — NO `src/`. Per the workflow it still lands on a branch + PR (it changes source under `bench/`),
> but it is fully parallel-safe with every `CpuEmitter.cs`/`BlockCompiler.*` emit PR.**
> **For agentic workers:** REQUIRED SUB-SKILL once scheduled — use `superpowers:subagent-driven-development`
> or `superpowers:executing-plans`. Steps use checkbox (`- [ ]`) syntax.
> **Read first (binding):** ADR 0011 §0.3 (the missing-apparatus drift that makes this a PR, not a footnote),
> §8 PR-A row, §5 (the measured-data-only honesty gate), §6 (the profiler method — the ranked list orders the
> later 8086 emit PRs). And the M6 benchmarking plan (`2026-06-17-m6-benchmarking-comparison.md`) — its
> Milestone B (the `M68000TierDriver` template, the instructions/sec seam, the frozen-constant contract) is
> the EXACT pattern this PR mirrors for the 8086. Every fairness rule + frozen constant there is binding here.

---

## Objective (the ADR §8 PR-A row)

Build the measurement apparatus the §5 loop + §6 ROI ranking require for **any** future 8086 emit PR — none of
which exists post-M5 (ADR §0.3): the 8086 has no bench driver, no frozen W1/W2 workloads, and is absent from
the hot-op profiler. Without this, PR-B's honesty gate (a measured before/after on a frozen 8086 workload) is
**unsatisfiable**. PR-A delivers, **bench-only (no `src/` emit change)**:

1. An **`M8086TierDriver`** registered as `"m8086"` (mirrors `M68000TierDriver` — single bus, `SetRegister`
   seeding, budget-1 JIT advance, an instructions/sec counter).
2. Frozen 8086 **W1/W2/W3 workloads** + their pinned constants (the M6 plan §4 8086 row, currently FUTURE) —
   dependency-free hand-written kernels (the 8086 has no in-repo Klaus/ZEX equivalent), so the baseline ships now.
3. The 8086 **hot-op profiler arm** in `bench/hotop-profiler/Profiler.cs` — the ranked 8086 list that orders
   PR-B/C/D (it does not exist yet).
4. A committed **8086 all-fallback baseline** row in REPORT.md / comparison.json (the honest "before" the 8086
   emit PRs subtract from) + a committed **ranked 8086 hot-op list**.

**Why a PR, not a footnote (ADR §0.3):** the §5 honesty gate is binding on every emit PR; for the 8086 neither
the workload nor the profiler input exists. This is the binding precondition drift #2 surfaced. It touches only
`bench/` so it is **parallel-safe with the entire Z80/68000 emit chain** and can run anytime after M5 (i.e. now).

---

## What the recon CONFIRMED (file:line — load-bearing, verified against `main` @ `5eabddc`)

| # | Fact | Evidence |
|---|---|---|
| A1 | The 8086 core is `M8086Cpu` in namespace `CpuEmulator.Cpus.M8086`, `public sealed partial class`. Constructor takes **ONE bus, no I/O**: `public M8086Cpu(IAddressSpace bus)`. The host builds the bus as `new AddressSpace(AddressSpaceKind.Program, addressBits: 20)` (20-bit, little-endian default). | `M8086Cpu.cs:18-31` |
| A2 | The JIT factory passes ONLY `program`: `new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, program)` — NO io bus (IN/OUT are interpreter-internal open-bus, no `IoBus` accessor; ADR §0.2). So the 8086 driver follows the **68000 single-bus shape, NOT the Z80 dual-bus shape**. | `M8086JittedCpuFactory.cs:15-20`; `M8086Cpu.Jit.cs:14-17` |
| A3 | The 8086's PC register is **`IP`** (NOT "PC"); status is **`FLAGS`** (NOT "SR"/"P"). `JitTarget` reports `ProgramCounterField.Name == "IP"`, `StatusField.Name == "FLAGS"`. The fetch is segmented `(CS<<4)+IP` internally — the driver seeds `CS` + `IP` (+ `DS`/`SS`/`ES`). | `M8086JitGenericityTests.cs:25-30`; generated `…M8086Cpu.g.cs:1707` |
| A4 | Register names seeded via `SetRegister` (the TomHarte runner, the canonical construction): `AX,BX,CX,DX,CS,SS,DS,ES,SP,BP,SI,DI,IP,FLAGS` + byte halves `AH/AL/…`. `AX/BX/CX/DX` are pair-view PROPERTIES (skipped by `_regFields`, like the Z80 pairs). | `M8088TomHarteRunner.cs:37-58`; `M8086JitGenericityTests.cs:51-68` |
| A5 | The 8086 is **all-fallback in M5** — every op routes through `inner.Step` (the descriptor table is populated-but-forced-fallback, 283 rows). The cycle model is rudimentary (ReadBus/WriteBus charge 1 each); `AdvanceCycles(long n) => _cycles += n` is the JIT seam. So, like the 68000, the 8086 baseline should **LEAD with instructions/sec** (data-axis-correct now; cycles/sec is partial). | `M8086JitGenericityTests.cs:34-48`; `M8086Cpu.Jit.cs:12`; `M8086Cpu.cs:51,54` |
| A6 | The generated 8086 descriptor table carries **REAL mnemonics** ("MOV", …) with `Ops: []` (empty micro-op list) — UNLIKE the all-fallback 68000 (empty table → "???"). So the profiler can use the **6502-style `unitBytes:1` + `MnemonicAt` direct path**: `target.Decode → target.DescriptorFor(key).Mnemonic` returns real op names. No 68000-style dataset-recovery path needed. | `…M8086Cpu.g.cs:1416-1425` (verified: `[0x88u]…"MOV"…[]`) |
| A7 | `M68000TierDriver` is the exact template (full body in recon): single bus, copy small image at `LoadAddress`, `SetRegister("PC"/"SR"/"SSP")`, single-bus `JittedCpu` ctor, an `M68000Instance` with `_instructions` counter, budget-1 `jit.Run`, a 0-cycle infinite-loop guard. | `M68000TierDriver.cs:1-85` |
| A8 | The driver registry is `Tiers.cs:58-64` (`["m68000"] = new M68000TierDriver()`); `ITierInstance` requires `CycleCount`, `InstructionCount`, `AdvanceSlice`, `ParkedThisSlice`, `CurrentPc` (`ITierDriver.cs:17-50`). | `Tiers.cs:58-64`, `ITierDriver.cs:17-66` |
| A9 | `M68000Workloads` (`Workloads.cs:658-940`) is the workload template: frozen cap consts (`M68000W2CycleCap`, `M68000W2InstructionCap`, `M68000W1InstructionCap` at `:665-677`), an opword-emit helper, assembled-mnemonic comment blocks, and `BenchWorkload` returns with `Architecture: "m68000"`. **But it uses a big-endian `W(ushort)` helper** — the 8086 is little-endian byte-granular, so use a byte-`Emit` like the Z80's (`Workloads.cs:438`). | `Workloads.cs:658-940`, `:438` |
| A10 | `BenchWorkload` (`IEmulatorAdapter.cs:49-58`) has NO segment field and NO instruction-cap field — `LoadAddress`/`StartPc` are `ushort`, the instruction cap is a separate `const` in the `*Workloads` class. The driver hardcodes `CS`/`DS`/`SS`/`ES` (or derives them). `AdapterResult.MeasuredWithInstructions` (`:32-34`) is the instructions/sec path. | `IEmulatorAdapter.cs:13-58` |
| A11 | The profiler (`Profiler.cs`, throwaway) imports workloads from `Workloads.cs` (`:156-167`), reads `pc = (ushort)(cpu.GetRegister("PC") & 0xFFFF)` (`:84`) and masks fetch to `0xFFFF` (`ByteFetchStream`, `:181-182`) — **both 16-bit assumptions are wrong for the 8086** (`IP` not "PC"; `(CS<<4)+IP & 0xFFFFF` not `& 0xFFFF`). The 6502 arm (`:119-126`) is the cleanest `unitBytes:1` model. | `Profiler.cs:84,119-126,156-167,181-183` |
| A12 | Wiring points: `Program.cs:45-52` (workload list), `BenchHarness.AdaptersFor` (`:29-52`, add `"m8086" => []`), `ReportWriter.ArchLabel`/`ArchitectureOrder`/`UnitLabel` (`:307-330`). Smoke: `BenchHarnessSmokeTests.cs:85-124` (the 68000 W1/W2 smokes). BDN: `TierBenchmarks.cs:21-101` (the `_m68k*` fields + `[Benchmark]` pairs). | as cited |

**Net:** every seam is proven by the 68000 path. The 8086-specific deltas are exactly four: 20-bit/little-endian
bus, `IP`/`CS` seeding (not `PC`/`SR`), little-endian byte workloads (not big-endian opwords), and the profiler's
`(CS<<4)+IP`/20-bit fetch fix. No `src/` change, no emit, no generator touch.

---

## The staged outline (one line each)

- **A1** — The `M8086TierDriver` (20-bit single bus, seed `CS`/`IP`/`DS`/`SS`/`ES`, budget-1 JIT advance,
  `_instructions` counter); register `"m8086"` in `Tiers.cs`.
- **A2** — The 8086 W1/W2/W3 workloads (little-endian hand-written kernels) + FROZEN cap/instruction consts.
- **A3** — Wire the workloads into `Program.cs`; `AdaptersFor("m8086") => []`; the `ReportWriter` 8086
  label/order/unit + the cycle-axis caveat.
- **A4** — The 8086 hot-op profiler arm (the `(CS<<4)+IP`/20-bit-fetch adaptation) + the ranked-list output.
- **A5** — The bench smoke tests + BDN coverage for the 8086 tiers.
- **A6** — Capture + commit the 8086 all-fallback baseline (REPORT.md + comparison.json) + the ranked hot-op
  list; update bench docs.

---

## Task A1 — The `M8086TierDriver`

**Files:**
- Create: `bench/CpuEmulator.Benchmarks/Drivers/M8086TierDriver.cs`
- Modify: `bench/CpuEmulator.Benchmarks/Tiers.cs` (register the driver — ONE line)

Clone `M68000TierDriver` exactly, with the four 8086 deltas (A1-A5): 20-bit little-endian bus mapped to 1 MB;
seed `CS`/`IP`/`DS`/`SS`/`ES` via `SetRegister` (not `PC`/`SR`/`SSP`); single-bus `JittedCpu` ctor (no io);
`CurrentPc => (ushort)(cpu.GetRegister("IP") & 0xFFFF)`. Lead with instructions/sec (A5) — the `_instructions`
counter is the trustworthy headline; cycles/sec is reported with the partial-axis caveat.

- [ ] **Step 1:** Create `Drivers/M8086TierDriver.cs`:

```csharp
namespace CpuEmulator.Benchmarks.Drivers;

using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;

/// <summary>The 8086 tier driver (M6 PR-A — the §0.3 measurement enablement). Constructs a 20-bit
/// little-endian program AddressSpace (1 MiB backing) + an M8086Cpu (ONE bus — the 8086 has no separate
/// I/O space in this core; IN/OUT are interpreter-internal open-bus, ADR 0011 §0.2), and, for Tier-1, the
/// all-fallback JittedCpu&lt;M8086Cpu&gt; proven byte-identical to the interpreter through M5.6 (TomHarte
/// green). Every 8086 op falls back to inner.Step in M5, so a green tier-parity baseline measures the
/// generic compiler's dispatch overhead honestly — the "before" the later 8086 hot-op IL emit subtracts
/// from. The 8086 cycle model is rudimentary on main (ReadBus/WriteBus charge 1 each; the timing axis is
/// post-M5), so the INSTRUCTION count is the cycle-axis-independent metric this baseline leads with (exactly
/// the 68000's instructions/sec lead, M6 plan B2); the cycles/sec row carries the partial-axis caveat (A3).
/// The image is loaded at CS:0 with IP = StartPc; CS/DS/SS/ES are seeded to 0 so a low-loaded flat image
/// runs without segmentation surprises (BenchWorkload has no segment field, A10 — the driver pins them).</summary>
public sealed class M8086TierDriver : ITierDriver
{
    public string Architecture => "m8086";

    public ITierInstance CreateTier0(BenchWorkload w) => Build(w, jit: false, new JitOptions());
    public ITierInstance CreateTier1(BenchWorkload w, JitOptions options) => Build(w, jit: true, options);

    private static ITierInstance Build(BenchWorkload w, bool jit, JitOptions options)
    {
        var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);   // little-endian default
        mem.MapMemory(0x00000, new byte[0x100000], writable: true);             // full 1 MiB
        for (int i = 0; i < w.Image.Length; i++)
            mem.Write8((uint)((w.LoadAddress + i) & 0xFFFFF), w.Image[i]);

        var cpu = new M8086Cpu(mem);
        cpu.SetRegister("CS", 0x0000);              // flat: CS=0 so the physical fetch is (0<<4)+IP = IP
        cpu.SetRegister("DS", 0x0000);
        cpu.SetRegister("SS", 0x0000);
        cpu.SetRegister("ES", 0x0000);
        cpu.SetRegister("IP", w.StartPc);           // the 8086 program counter is IP (A3)
        cpu.SetRegister("SP", 0xFFFE);              // a sane stack near the top of the flat segment
        cpu.SetRegister("FLAGS", 0x0002);           // bit 1 is the reserved-always-1 8086 FLAGS bit
        JittedCpu<M8086Cpu>? j = jit ? new JittedCpu<M8086Cpu>(cpu, M8086Cpu.JitTarget, mem, options: options) : null;
        return new M8086Instance(cpu, j);
    }

    /// <summary>An 8086 tier instance. Tier-0 steps; Tier-1 runs the all-fallback JIT a slice at a time
    /// (budget-1 == one instruction per block, the M5 all-fallback invariant — M8088TomHarteRunner:151-152).
    /// Stop is the FIXED CYCLE/INSTRUCTION CAP for every workload (no host-service boundary — the kernels
    /// spin via a back-edge and the cap terminates). Reports BOTH the cycle count and the instruction count;
    /// the 8086 baseline leads with instructions/sec (the cycle axis is partial, A5).</summary>
    private sealed class M8086Instance(M8086Cpu cpu, JittedCpu<M8086Cpu>? jit) : ITierInstance
    {
        private long _instructions;

        public long CycleCount => cpu.CycleCount;
        public long InstructionCount => _instructions;
        public ushort CurrentPc => (ushort)(cpu.GetRegister("IP") & 0xFFFF);   // IP, not PC (A3)
        public bool ParkedThisSlice => false;   // capped workloads: the cap terminates, never a trap-park

        public void AdvanceSlice(long maxCycles)
        {
            long target = cpu.CycleCount + maxCycles;
            while (cpu.CycleCount < target)
            {
                long prevCycles = cpu.CycleCount;
                if (jit is not null) { long budget = 1; jit.Run(ref budget); }
                else cpu.Step();
                _instructions++;
                if (cpu.CycleCount == prevCycles)
                    throw new InvalidOperationException(
                        "m8086: instruction advanced 0 cycles — infinite-loop guard (subject diverged)");
            }
        }
    }
}
```

> **Builder note:** match the EXACT `ITierInstance` member set + the `M68000Instance` idiom verbatim
> (`M68000TierDriver.cs:52-83`) — if `M68000Instance` differs from this transcription in any member (e.g. an
> additional property), mirror it so the 8086 instance satisfies the interface identically. Confirm the
> `JittedCpu<M8086Cpu>` ctor arity against `M8086JittedCpuFactory.cs:15-20` (it passes only `program`; the
> `options:` named arg matches the 68000 driver's `options: options`). The `FLAGS = 0x0002` seed (reserved
> bit 1 = 1) avoids a non-canonical FLAGS the interpreter might normalize differently — confirm against the
> 8086 reset/FLAGS handling, or seed `0x0000` if the core normalizes on first use; it does not affect a
> flag-free kernel.

- [ ] **Step 2:** Register in `Tiers.cs` (`:64`, beside the `["m68000"]` line):

```csharp
        ["m8086"] = new Drivers.M8086TierDriver(),   // M6 PR-A — the 8086 tier driver
```

- [ ] **Verify:** `dotnet build bench/CpuEmulator.Benchmarks -warnaserror` clean; a scratch run of an 8086-W2
  workload (A2) advances + returns a plausible instruction + cycle count; the 6502/Z80/68000 drivers untouched.

---

## Task A2 — The 8086 W1/W2/W3 workloads (FROZEN window constants)

**Files:**
- Modify: `bench/CpuEmulator.Benchmarks/Workloads.cs` → append an `M8086Workloads` static (after the M68000 block)

Mirror the 68000 workload shape (A9) but **little-endian byte-granular** (A9 — use a byte `Emit`, NOT the
big-endian `W(ushort)`). Carry BOTH a frozen `FixedCycleCap` AND a frozen `InstructionCap` per workload (the
instruction window is the cycle-axis-independent metric the baseline leads with). Three workloads, all
dependency-free (the 8086 has no in-repo Klaus/ZEX — the M6 plan §4 8086 row was FUTURE; hand-written kernels
ship the baseline now). Include the assembled-mnemonic comment block (the readability contract).

- [ ] **Step 1:** Add the static + the FROZEN consts:

```csharp
/// <summary>The 8086 benchmark workloads (M6 PR-A). All hand-written + dependency-free (the 8086 has no
/// in-repo Klaus/ZEX equivalent — the M6 plan §4 8086 row was FUTURE-gated-on-M5; these ship the baseline
/// now). Little-endian, byte-granular (NOT the 68000's big-endian opwords). Each carries a FROZEN cycle cap
/// AND a FROZEN instruction cap; the later 8086 hot-op-emit re-measure (the §5 contract) reuses these EXACT
/// values byte-identically — a git diff of the constants must show no change or the comparison is void.</summary>
public static class M8086Workloads
{
    // The flat load address: CS=0, IP=0x0100 (a low, DOS-COM-like origin; the driver seeds CS=0 so the
    // physical fetch is (0<<4)+IP = IP). Kept under 0xFFFF so BenchWorkload's ushort StartPc holds it (A10).
    public const ushort M8086LoadAddress = 0x0100;

    // FROZEN window constants — pin AFTER the first measured run, then NEVER change (the Milestone-A rule).
    public const long M8086W1InstructionCap = 50_000_000;   // W1 mixed-stream instruction window
    public const long M8086W2CycleCap       = 50_000_000;   // W2 ALU/branch kernel cycle cap
    public const long M8086W2InstructionCap = 50_000_000;   // W2 instruction window (cycle-axis-independent)
    public const long M8086W3CycleCap       = 50_000_000;   // W3 compute kernel cycle cap
    public const long M8086W3InstructionCap = 50_000_000;   // W3 instruction window
```

- [ ] **Step 2:** The W2 ALU/branch kernel (the hot counted loop — the chain-edge stress, mirroring the
  6502/Z80/68000 W2). 8086 mnemonics are byte-granular little-endian; the taken back-edge is the hot edge a
  future block-JIT stresses. Example shape (Builder verifies the exact bytes against the M8086 interpreter):

```csharp
    // W2 — a tight ADD/SUB/DEC/Jcc inner loop with a back-edge (the hot chain edge). Flag-free on the data
    // result (the loop counter drives the branch). Runs to a FROZEN cap; the BRA-equivalent (JMP $) restarts
    // so the cap terminates. AX = accumulator, CX = inner counter.
    //
    //   0100  B8 00 00     MOV AX, 0000      ; AX = 0
    //   0103  B9 00 01     MOV CX, 0100      ; CX = 256 (inner counter)
    //  inner (0106):
    //   0106  05 07 00     ADD AX, 0007      ; AX += 7   (ALU + flags)
    //   0109  2D 03 00     SUB AX, 0003      ; AX -= 3
    //   010C  49           DEC CX            ; CX--
    //   010D  75 F7        JNZ inner         ; loop (taken back-edge — the hot chain edge)  [-9 -> 0106]
    //   010F  EB EF        JMP start         ; restart forever (the cap terminates)         [-17 -> 0100]
    public static BenchWorkload ArithmeticKernel()
    {
        var code = new System.Collections.Generic.List<byte>();
        void Emit(params byte[] bytes) => code.AddRange(bytes);

        Emit(0xB8, 0x00, 0x00);              // MOV AX, 0000
        Emit(0xB9, 0x00, 0x01);              // MOV CX, 0100
        // inner:
        Emit(0x05, 0x07, 0x00);              // ADD AX, 0007
        Emit(0x2D, 0x03, 0x00);              // SUB AX, 0003
        Emit(0x49);                          // DEC CX
        Emit(0x75, 0xF7);                    // JNZ inner   (displacement to 0x0106; VERIFY)
        Emit(0xEB, 0xEF);                    // JMP start   (displacement to 0x0100; VERIFY)

        return new BenchWorkload(
            Name: "8086-W2 arith-kernel",
            Image: code.ToArray(),
            LoadAddress: M8086LoadAddress,
            StartPc: M8086LoadAddress,
            SuccessTrapPc: 0x0000,
            FixedCycleCap: M8086W2CycleCap,
            ExpectedCycles: M8086W2CycleCap,
            Architecture: "m8086",
            UsesCpmBdos: false);
    }
```

> **Builder note (load-bearing):** the opcode bytes + the Jcc/JMP displacements above are ILLUSTRATIVE. The
> implementer MUST assemble + verify them against the merged `M8086Cpu` — run the kernel through the
> interpreter once and confirm (a) it loops the expected number of times, (b) the data result is sane, (c)
> the back-edge displacement lands on `inner`, (d) the restart JMP lands on `start`. 8086 short-Jcc/JMP
> displacements are signed bytes relative to the END of the instruction (next IP) — get the sign + offset
> right (`JNZ inner` from IP=0x010F to 0x0106 is `0x0106 - 0x010F = -9 = 0xF7`; `JMP start` from IP=0x0111
> to 0x0100 is `0x0100 - 0x0111 = -17 = 0xEF`). Prefer ops the M5 interpreter has TomHarte-green coverage for.

- [ ] **Step 3:** The W1 mixed-instruction stream (the "integration-realistic" analog of Klaus/ZEX — the M6
  plan §4 Option A: a broader hand-written kernel exercising a representative spread: MOV variants, ALU
  reg/imm, PUSH/POP, Jcc/JMP/CALL/RET, an INC/DEC loop) run to a frozen instruction cap. Dependency-free, so
  it always runs. Same `Emit` + assembled-comment + `BenchWorkload` shape as W2, with `FixedCycleCap:
  M8086W2CycleCap`-class window driven by the instruction cap (the 8086 leads with instructions/sec).

- [ ] **Step 4:** The W3 compute kernel (a Sieve-class or arithmetic compute loop — the SMC-free compute
  workload where emit coverage pays off first, mirroring the 6502/Z80/68000 W3). Same shape; frozen
  `M8086W3CycleCap` / `M8086W3InstructionCap`.

- [ ] **Step 5:** Pin the constants AFTER the first measured run (A6) — the "pin then never change" rule. The
  recommended starting magnitude (`50_000_000`) mirrors the 6502/Z80/68000 W2; adjust to a clean window on the
  first run, then freeze.

- [ ] **Verify:** all three 8086 workloads build always (no external dependency); each runs through the
  `M8086TierDriver` Tier-0 + Tier-1 and returns a plausible instruction + cycle count; the W2/W3 kernels loop
  (cap terminates, no infinite-loop-guard throw).

---

## Task A3 — Wire the workloads + the adapter set + the report grouping

**Files:**
- Modify: `bench/CpuEmulator.Benchmarks.Runner/Program.cs` (`:52` — add the 8086 workloads)
- Modify: `bench/CpuEmulator.Benchmarks/BenchHarness.cs` (`:44` — `AdaptersFor("m8086")`)
- Modify: `bench/CpuEmulator.Benchmarks/ReportWriter.cs` (`ArchLabel`/`ArchitectureOrder`/`UnitLabel` + caveat)

- [ ] **Step 1:** `Program.cs` (after the 68000 workloads, `:52`):

```csharp
        workloads.Add(M8086Workloads.ArithmeticKernel());   // 8086-W2 — the hot ALU/branch kernel
        workloads.Add(M8086Workloads.MixedKernel());        // 8086-W1 — the mixed-instruction stream
        workloads.Add(M8086Workloads.SieveKernel());        // 8086-W3 — the compute kernel
```

The per-workload loop (`:57-79`) measures Tier-0 + Tier-1 for each via the driver registry — so the OUR-tier
8086 rows appear automatically (A8).

- [ ] **Step 2:** `BenchHarness.AdaptersFor` (`:29-52`) — add an explicit `"m8086"` arm so the 8086 does NOT
  fall into the `_` default (the four 6502 shims, which would be WRONG for an 8086 row, A12):

```csharp
        "m8086" => [],   // M6 PR-A: our-tiers-only baseline. A head-to-head 8086 C reference is the M6
                         // plan §8 Q3 evaluation (deferred); a cited row can be added to reference-numbers.json
                         // when chosen. [] = no third-party adapter -> the comparison "best existing" cell is
                         // empty/cited, never a mis-matched 6502 shim.
```

- [ ] **Step 3:** `ReportWriter.cs` — `ArchLabel` (`:315-321`) add `"m8086" => "8086"`; `ArchitectureOrder`
  `Rank` (`:307-312`) add `"m8086" => 3` (so it sorts after the 68000, not into the `_ => 3` alphabetical
  bucket); `UnitLabel` (`:326-330`) default already yields "cycles" (the 8086's bus-cycle unit — correct, no
  change). Add the **8086 timing-axis caveat** under the 8086 section, mirroring the 68000 caveat (B4):

```
8086 cycles/sec is reported for completeness but the cycle/timing axis is RUDIMENTARY on `main` (M5 charges
one cycle per bus access; a cycle-exact 8086 timing model is post-M5). The 8086 baseline's trustworthy
headline is instructions/sec (data-axis-correct on the M5.6 TomHarte-green core). The re-measure picks up a
cycle-exact model automatically if/when it lands.
```

- [ ] **Verify:** `dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report` produces a
  REPORT.md with an 8086 section (our two tiers, instructions/sec + cycles/sec, the caveat present, no
  third-party adapter rows); 6502/Z80/68000 rows numerically unchanged (diff only the 8086 additions + the
  timestamp).

---

## Task A4 — The 8086 hot-op profiler arm (the ranked list that orders PR-B/C/D)

**Files:**
- Modify: `bench/hotop-profiler/Profiler.cs` (add the `using`, the 8086 arm + call sites, the 20-bit fetch fix)

The profiler is a throwaway harness (A11). The 8086 arm mirrors the 6502 `unitBytes:1` model (A6 — the 8086
descriptors carry real mnemonics, so the direct `MnemonicAt` path works), BUT the shared `Profile`/
`ByteFetchStream` assume a 16-bit "PC" (A11) — the 8086 needs `IP` + the `(CS<<4)+IP` 20-bit physical fetch.
Because it is throwaway, add an 8086-specific profile loop (or parameterize the existing one) rather than
forcing the 8086 through the 16-bit assumption.

- [ ] **Step 1:** Add the import (`Profiler.cs:12-19`): `using CpuEmulator.Cpus.M8086;`.

- [ ] **Step 2:** Add an 8086-specific profile path. The cleanest throwaway shape is a small parallel loop
  that reads `IP` (not "PC"), computes the physical fetch from `CS`/`IP`, decodes via the target, and counts
  mnemonics — reusing the existing `MnemonicAt`/`Decode`/`DescriptorFor` machinery with an 8086 fetch stream:

```csharp
    // M6 PR-A: the 8086 hot-op profiler arm. The 8086 has a REAL-mnemonic descriptor table (unlike the empty
    // all-fallback 68000), so the 6502-style direct MnemonicAt path works — but the live fetch is (CS<<4)+IP
    // on a 20-bit bus, NOT a bare 16-bit "PC", so this uses an IP-aware fetch + a 20-bit mask. Throwaway code.
    void Run8086(string wname, BenchWorkload? w)
    {
        if (w is null) { Line($"## 8086 — {wname}   SKIPPED (workload source absent)"); Line(); return; }
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        for (int i = 0; i < w.Image.Length; i++)
            bus.Write8((uint)((w.LoadAddress + i) & 0xFFFFF), w.Image[i]);
        var cpu = new M8086Cpu(bus);
        cpu.SetRegister("CS", 0); cpu.SetRegister("DS", 0); cpu.SetRegister("SS", 0); cpu.SetRegister("ES", 0);
        cpu.SetRegister("IP", w.StartPc); cpu.SetRegister("SP", 0xFFFE); cpu.SetRegister("FLAGS", 0x0002);

        var target = M8086Cpu.JitTarget;
        var counts = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.Ordinal);
        const long instrBudget = 20_000_000;   // the same 20M-instruction window the other arms profile
        for (long n = 0; n < instrBudget; n++)
        {
            // physical fetch address = (CS<<4) + IP, masked to 20 bits
            uint phys = (uint)(((cpu.GetRegister("CS") << 4) + (cpu.GetRegister("IP") & 0xFFFF)) & 0xFFFFF);
            string m = MnemonicAt(target, () => new ByteFetchStream20(bus, phys));   // 20-bit fetch stream
            counts[m] = counts.TryGetValue(m, out var c) ? c + 1 : 1;
            cpu.Step();
        }
        EmitRanked("8086", wname, counts);   // the same top-15 OrderByDescending the other arms use
    }
```

> **Builder note:** the exact integration depends on `MnemonicAt`'s signature (`Profiler.cs:32-41`) and
> whether it takes a fetch-stream factory or a `(bus, origin)` pair. If `MnemonicAt` is hard-wired to the
> 16-bit `ByteFetchStream` (`:176-183`), add a sibling `ByteFetchStream20` (a copy with `& 0xFFFFF` instead of
> `& 0xFFFF` and a 20-bit origin) OR generalize `ByteFetchStream` to take a mask. `EmitRanked` stands in for
> the existing top-15 ranking + `Line(...)` output the 6502/Z80/68000 arms use (`Profiler.cs:106-113`) —
> reuse it verbatim; do NOT invent a new output format. The 20M-instruction budget matches §6's "20,000,000
> instructions per workload."

- [ ] **Step 3:** Add the 8086 call sites (`Profiler.cs:156-167`, beside the other arms):

```csharp
        Run8086("W1 mixed-kernel", M8086Workloads.MixedKernel());
        Run8086("W2 arith-kernel", M8086Workloads.ArithmeticKernel());
        Run8086("W3 sieve-kernel", M8086Workloads.SieveKernel());
```

- [ ] **Verify:** `dotnet run -c Release --project bench/hotop-profiler` emits an 8086 section in
  `hotop-profile-results.txt` with a ranked mnemonic histogram (real op names — MOV/ADD/SUB/Jcc/…, NOT "???"),
  top-15 per workload, cumulative-% consistent with the other CPUs' 86-100% top-8 finding (§6). This ranked
  list is the input that orders PR-B (MOV expected top) / PR-C (ALU+FLAGS) / PR-D (branch).

---

## Task A5 — The bench smoke tests + BDN coverage

**Files:**
- Modify: `tests/CpuEmulator.Tests/Jit/BenchHarnessSmokeTests.cs` (add the 8086 W1/W2/W3 smokes)
- Modify: `bench/CpuEmulator.Benchmarks.Runner/TierBenchmarks.cs` (add `_m8086*` fields + `[Benchmark]` pairs)

- [ ] **Step 1:** The smoke — model the three 68000 smokes (`BenchHarnessSmokeTests.cs:85-124`): a tiny
  bounded 8086-W2 window on BOTH tiers asserting `Tier0.Run == Tier1.Run` (tier agreement) + both
  `MeasureTier` rows `Ran && CyclesPerSecond > 0` — NOT a throughput threshold (D5: the smoke proves wiring,
  never asserts speed). Use the tiny-window `with`-clone idiom (`:94`):

```csharp
    [Fact]
    public void The_two_8086_tiers_run_and_agree_on_the_W2_cycle_count()
    {
        var w = M8086Workloads.ArithmeticKernel() with { FixedCycleCap = 2_000_000, ExpectedCycles = 2_000_000 };
        long t0 = Tier0.Run(w);
        long t1 = Tier1.Run(w);
        Assert.True(t0 >= 2_000_000);
        Assert.Equal(t0, t1);                                  // tier-0 == tier-1 (all-fallback parity)
        var r0 = BenchHarness.MeasureTier("our Tier-0 interpreter", Tier0.Run, w);
        var r1 = BenchHarness.MeasureTier("our Tier-1 JIT", Tier1.Run, w);
        Assert.True(r0.Ran && r0.CyclesPerSecond > 0);
        Assert.True(r1.Ran && r1.CyclesPerSecond > 0);
    }
```

And the instructions/sec smoke (model `:107-124`) asserting `t0.InstructionsPerSecond > 0` /
`t1.InstructionsPerSecond > 0` via `MeasureTierCounted` + `RunCounted` (the Task-B2 seam the 8086 leads with),
plus a W3 cycle-agreement smoke (model `:167-185`).

> **Builder note:** confirm `Tier0`/`Tier1`/`MeasureTier`/`MeasureTierCounted` are the same statics the 68000
> smokes call. The 8086 kernels are dependency-free, so unlike Klaus/ZEX they need NO `Require…()` gating —
> the smoke always runs.

- [ ] **Step 2:** BDN — add `_8086w1/_8086w2/_8086w3` fields (`TierBenchmarks.cs:21-23`), seed them in
  `[GlobalSetup] Setup()` (`:34-36`), and add the six `[Benchmark]` methods mirroring the 68000 pairs
  (`:81-101`):

```csharp
    [Benchmark] public long Interpreter_M8086Kernel() => Tier0.Run(_8086w2);
    [Benchmark] public long Jit_M8086Kernel()         => Tier1.Run(_8086w2);
    [Benchmark] public long Interpreter_M8086Mixed()  => Tier0.Run(_8086w1);
    [Benchmark] public long Jit_M8086Mixed()          => Tier1.Run(_8086w1);
    [Benchmark] public long Interpreter_M8086Sieve()  => Tier0.Run(_8086w3);
    [Benchmark] public long Jit_M8086Sieve()          => Tier1.Run(_8086w3);
```

- [ ] **Verify:** `dotnet test --filter "FullyQualifiedName~BenchHarnessSmokeTests"` green (the 8086 smokes
  pass); `dotnet run … -- --bdn` lists the 8086 benchmarks; 6502/Z80/68000 smokes + BDN unchanged.

---

## Task A6 — Capture + commit the 8086 baseline + the ranked hot-op list; update docs

**Files:**
- Modify: `bench/results/REPORT.md` + `bench/results/comparison.json` (regenerated — the 8086 section)
- Modify: `bench/hotop-profiler/hotop-profile-results.txt` (the committed ranked 8086 list)
- Modify: `bench/README.md` (the 8086 workloads + subjects + the timing-axis caveat + instructions/sec note)
- Modify: `docs/user-guide/benchmarks.md` (the 8086 paragraph)

- [ ] **Step 1:** On the canonical host, run
  `dotnet run -c Release --project bench/CpuEmulator.Benchmarks.Runner -- --report --all` and COMMIT the
  regenerated REPORT.md + comparison.json with the 8086 all-fallback baseline (the honest "before" — Tier-1
  ≈ Tier-0, the `†` all-fallback marker; §5). Run `dotnet run -c Release --project bench/hotop-profiler` and
  COMMIT the regenerated `hotop-profile-results.txt` with the ranked 8086 list. **Commit ONLY measured data**
  (the no-fabrication rule).

- [ ] **Step 2:** PIN the frozen 8086 constants (A2 Step 5) to the windows the first run used — record them in
  `Workloads.cs` as the final `const` values + the re-measure comment, and never change them (the later 8086
  emit re-measure reuses them byte-identically — §5).

- [ ] **Step 3:** Update `bench/README.md` (the 8086 Workloads + Subjects tables — three kernels; our two
  tiers always; no third-party reference yet, the §8 Q3 evaluation deferred; the cycle-axis caveat + the
  instructions/sec headline) and `docs/user-guide/benchmarks.md` (the 8086 paragraph) in sync with the REPORT.

- [ ] **Verify (definition-of-done gate):** REPORT.md diff shows 6502/Z80/68000 rows unchanged + new 8086
  rows; comparison.json has an `"m8086"` cpu block (our two tiers, `allFallback: true` on Tier-1); the
  ranked 8086 hot-op list is committed; the three docs cross-link consistently; `dotnet test` +
  `dotnet build -warnaserror` green.

---

## Test Plan

**Unit / wiring (the §8 PR-A gate — wiring proven, no throughput threshold):**
- `The_two_8086_tiers_run_and_agree_on_the_W2_cycle_count` + the instructions/sec smoke + the W3 smoke —
  Tier-0 == Tier-1 (all-fallback parity), both tiers `Ran && CyclesPerSecond > 0`, instructions/sec > 0.
- Build clean with `-warnaserror`; the profiler emits a real-mnemonic ranked 8086 list (not "???").

**Parity / honesty gate (ADR §5 — adapted for a bench-only PR):**
- **Tier-0 vs Tier-1 parity:** the 8086 is all-fallback, so Tier-1 MUST equal Tier-0 byte-for-byte on every
  workload (the smoke's `Assert.Equal(t0, t1)`). This is the all-fallback honesty: the baseline Tier-1 is the
  interpreter wrapped in a block, and the smoke proves it diverges nowhere. (The deeper per-op 8086 parity is
  already owned by M5.6's TomHarte-green gate — PR-A does not re-prove it; it consumes it.)
- **Measured-data-only:** the committed 8086 REPORT.md / comparison.json rows + the ranked hot-op list are
  REAL measured output (not fabricated). The Tier-1 column carries the `†` all-fallback marker (the honest
  "before" the emit PRs subtract from). The frozen constants are pinned and will be re-used byte-identically
  by PR-B's re-measure (the §5 contract — established here, honored later).
- **No TomHarte/ZEXALL re-run needed in THIS PR:** PR-A adds no emit, so there is no new parity surface; the
  8086's correctness gate is M5.6's existing TomHarte green (a consumed precondition, not a PR-A deliverable).

**The §8 PR-A definition of "gate satisfied":** a committed 8086 all-fallback baseline row in REPORT.md /
comparison.json (the honest "before") + a committed ranked 8086 hot-op list. Both are produced + committed in
Task A6 — that is the PR's exit criterion.

---

## Dependencies

- **None.** PR-A touches only `bench/` + `bench/hotop-profiler/` + bench tests — **NO `src/`** (no
  `CpuEmitter.cs`, no `BlockCompiler.*`, no CPU core). So it is OUTSIDE the §4 CpuEmitter.cs serialization rule
  and is **fully parallel-safe** with the entire Z80 chain (PR-0→1→2→3), the 68000 chain (PR-4→5→6), and the
  6502 SMC lever (PR-S). Dispatch it alongside PR-0/PR-1 to keep the team busy without `CpuEmitter.cs`
  contention (ADR §8 "recommended dispatch: PR-0 + PR-A in parallel").
- **Consumes (precondition, already met):** M5.6 (the 8086 interpreter + all-fallback JIT, TomHarte-green) is
  landed on `main` — `M8086Cpu`, `M8086Cpu.JitTarget`, and the populated-but-forced-fallback descriptor table
  all exist (A1-A6). PR-A needs nothing beyond merged `main`.
- **Unblocks:** PR-B / PR-C / PR-D (the 8086 emit arms) — their §5 honesty gate (a measured before/after on a
  frozen 8086 workload) and their §6 emit ordering (the ranked hot-op list) are BOTH delivered here. Until
  PR-A lands, those PRs cannot satisfy their merge precondition.

---

## Definition of done

- `bench/CpuEmulator.Benchmarks/Drivers/M8086TierDriver.cs` exists (20-bit single-bus, `IP`/`CS` seeding,
  budget-1 JIT advance, instructions/sec counter); `Tiers.cs` registers `["m8086"]`.
- `M8086Workloads` exists with three dependency-free little-endian kernels (W1/W2/W3) + FROZEN cap +
  instruction-cap consts, pinned after the first run.
- `Program.cs` runs the 8086 workloads; `AdaptersFor("m8086") => []`; `ReportWriter` labels/orders the 8086
  and emits the cycle-axis caveat.
- `bench/hotop-profiler/Profiler.cs` has an 8086 arm (20-bit `(CS<<4)+IP` fetch, real-mnemonic ranking);
  `hotop-profile-results.txt` carries the committed ranked 8086 list.
- The 8086 bench smoke tests + BDN benchmarks are green/listed; 6502/Z80/68000 numbers unchanged.
- A committed 8086 all-fallback baseline (REPORT.md + comparison.json, Tier-1 `†`-marked) — the honest
  "before"; `bench/README.md` + `docs/user-guide/benchmarks.md` updated.
- NO `src/` change (the parallel-safety invariant, verified by the diff being confined to `bench/` + tests +
  docs).
- The PR body notes "PR-A of the M6 arc (ADR 0011 §8 / §0.3) — the 8086 measurement enablement; bench-only,
  parallel-safe; delivers the all-fallback baseline + the ranked hot-op list that unblock PR-B/C/D."
