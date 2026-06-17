# M4 (Motorola 68000) — Status + Resume Pointer

> **Purpose:** a single-file recovery point for resuming the 68000 work in a fresh session.
> Written at a deliberate pause: the entire 68000 **substrate** is complete and merged; the
> **interpreter (M4.5)** — where opcodes actually execute — is the next, large, multi-PR sub-arc.
> Read this first, then the cited ADRs/plans, then dispatch the M4.5 Planner.

## Current state (verified)

- **Branch:** `main`, synced with `origin/main`, clean tree. **HEAD: `c93e049`** (PR #38 merge).
- **Full suite:** `5180 passed / 0 failed / 1 skipped` (the 1 skip = the 680x0 real-file TomHarte theory, which runs only when the vectors are present in the cache — by design).
- **`dotnet build --no-incremental -warnaserror`:** clean.
- **6502 + Z80:** untouched / byte-identical throughout M4 (the `RegeneratedSpecTests` guard is green; every M4 change is additive).

## What is DONE (the 68000 substrate — PRs #33–#38)

| Layer | PR | What it delivered (synthetically proven; 6502/Z80 byte-identical) |
|---|---|---|
| 32-bit registers + state | #33 | `RegisterDef.Bits` → `8\|16\|32` (the `FieldType` `uint` arm); D0–D7, A0–A6, USP/SSP (named; **no `a7` register**), A7 as a mode-selected view, PC, SR (16-bit) + `Ccr`/`SupervisorMode`; `OperandSize{Byte,Word,Long}` enum staked (not threaded onto Ops yet) |
| Wide big-endian bus | #34 | `Read16/32`/`Write16/32` + `Endianness` on `IAddressSpace` (default interface methods, page-straddle-safe shift/or composition); `BusAlignment.IsMisaligned` (detection only); width-tagged tracing. JIT compiles unchanged (binds `Read8`/`Write8` by name) |
| Word-granular field decode | #35 (M4.3a) | `FieldGrammar`/`FieldOp` sibling carrier; big-endian operword fetch; opaque `(1<<24)\|(opIndex<<8)\|size` key; `ExtensionWordCount`/operand-computed length |
| EA descriptor + compute | #36 (M4.3b) | `EmitM68kEa`/`ComputeEa` for the 14 modes; `(An)+`/`-(An)` write-back (correct ordering); A7 `±2`; `pureEa` (LEA/PEA); `M68kEaLegality` (EA-category) — **no new `AddrMode` enum member** |
| FieldGrammar dataset + real spec | #37 (M4.4a) | 82 instruction families in `data/m68000-fieldgrammar.json` (each a `FieldOp` 8-tuple + PRM citation); the disjoint `--field-grammar` importer arm; the real importer-generated `M68000Spec.cs` + `M68000RegeneratedSpecTests`. **The 68000 now DECODES real instructions** (descriptors are Undefined stubs) |
| gzip TomHarte loader | #38 (M4.4b) | `M68000TomHarteLoader` (gzip + mnemonic-keyed); `680x0/v1` resolver + skip-when-absent attribute; `tools/get-test-vectors-68000.ps1`; committed gzip parse-proof fixture; state-set runner scaffold (`NotYetExecuted`). **Parses real vectors; executes nothing** |

**Honest close-state:** the 68000 decodes real instructions and the vector loader parses them, but **no 68000 opcode executes and no 680x0 vector is asserted green.** Every descriptor is an Undefined stub; `M68000Spec.Instructions = []`. The op bodies are M4.5.

## Verified 680x0 TomHarte vector schema (from the M4.4b recon — load-bearing for M4.5)

- **In-repo path:** `SingleStepTests/680x0` → `68000/v1/*.json.gz` (**124 files**, gzipped, mnemonic+size-keyed, e.g. `ADD.b.json.gz`, `MOVE.w.json.gz`, `NOP.json.gz`; several thousand cases each). Fetch via `tools/get-test-vectors-68000.ps1` (sparse-checks-out `68000/v1` → `<cache>/680x0/v1`).
- **Per-case state keys:** `d0..d7`, `a0..a6`, `usp`, `ssp` (no `a7`), `sr` (full 16-bit), `pc`, **`prefetch: [w0, w1]`** (exactly 2 words, present in BOTH `initial` and `final`; initial ≠ final), `ram`.
- **`transactions`:** field 2 is the **per-slot CYCLE COUNT** (confirmed: case top-level `length` == Σ field-2 across its transactions — this resolved ADR 0004's flagged ambiguity). Two tuple shapes: `["n", cycles]` (idle, len 2) and `[dir, cycles, fc, addr, sizeTag, value]` (bus, len 6). **Size tags are `.b`/`.w` only** — the 16-bit bus decomposes a `.l` access into two `.w` transactions.

## M4.5a — DONE (the first executing 68000 opcodes)

> **Status (PR for M4.5a):** the 68000 now EXECUTES the MOVE family. The substrate's Undefined stubs are real
> for MOVE: a live big-endian word fetch stream drives `Decode`, the FieldGrammar `Step` (replacing the M4.5
> `throw`) dispatches the MOVE opIndices to hand-written bodies that call the live `ComputeEa` + the M4.2 wide
> bus, with partial-write / MOVEA-sign-extend / privileged-SR-CCR-USP semantics and the two-EA MOVE length
> (deferred D5). **57,447 non-exception MOVE cases execute TomHarte-green on the DATA axis (0 failed) across all
> 10 in-scope MOVE-family files** under `-c Release`. 6502/Z80 stay byte-identical (`RegeneratedSpecTests`
> green); every change is additive (gated to `model.FieldGrammar is not null` + the M68000 partial + the
> 680x0-only test infra).
>
> **The corrected M4.5a/M4.5d boundary (authority: ADR 0004 §3) — so M4.5b/c/d plans do NOT repeat the
> over-specification.** The M4.5a plan's original TomHarte gate accidentally demanded the M4.5d **timing** axis
> as a precondition for the M4.5a **correctness** axis. Builder's real-vector reconciliation caught it; the gate
> was corrected to the split ADR 0004 §3 already drew:
> - **M4.5a (data/correctness axis):** `D0–D7, A0–A6, USP, SSP, SR, RAM`, byte-exact. The operword is seeded
>   from `initial.prefetch[0]` into the bus (the 680x0 v1 vectors place it there, NEVER in `bus[pc]`). Three
>   real MOVE bugs the gate caught + fixed: the brief-index `(d8,An,Xn)` index-register decode (D5, was an
>   M4.3b stub), the PC-relative `(d16/d8,PC)` base (= operword address + 2), and the `MOVE to SR` valid-bit
>   mask `0xA71F`.
> - **M4.5d (timing axis):** `final.pc`, `final.prefetch`, the per-transaction bus trace, the cycle count —
>   these are the prefetch-queue's observable state (the prefetch-queue mechanism + cycle-accurate sequencing
>   are M4.5d). Carried with `// TODO(M4.5d)` in the runner (a `timingAxis` flag, default off).
> - **M4.5d (exceptions):** the misaligned-wide-access **address error (vector 3)** + other exception/vector
>   sequences. ~23,200 in-scope MOVE cases take an exception (the 68000 traps before completing the MOVE,
>   pushing a frame + fetching a handler); M4.5a detects them (un-fakeable signal: a vector-table read pair
>   whose value IS `final.pc`) and DEFERS them to M4.5d rather than asserting (asserting would be a drift
>   false-positive). **M4.5b/c MUST keep this data/timing/exception axis split.**

## M4.6 — DONE (the 68000 through the JIT — all-fallback data-axis tier parity)

> **Status (PR for M4.6):** the functionally-finished 68000 interpreter (`M68000Cpu`, M4.5a–d-1) now runs
> through the generic `JittedCpu<M68000Cpu>` as **all-fallback**, and is **byte-identical Tier-0 (interpreter)
> vs Tier-1 (JIT) on the DATA axis** across the full family corpus — the same parity gate the Z80 uses (which
> went through the generic seam with ZERO JIT-assembly change). **123 data-axis family files tier-parity green
> through `JittedCpu<M68000Cpu>` (0 failed) at the CI sample**; `CPUEMULATOR_UAT=full` runs every case. The
> 6502/Z80 stay byte-identical (`RegeneratedSpecTests` green; the 6502/Z80 JIT parity + JIT-infra gates green);
> the 68000 INTERPRETER data-axis sweeps stay green (the JIT wiring touched no interpreter behavior). Per ADR
> 0008 §6, M4.6 depended only on M4.5d-1 (PR #43), NOT on M4.5d-2 (the timing axis runs in parallel).
>
> **What the gate proves.** A green tier-parity sweep proves the GENERIC COMPILER (the discovery walk, the keyed
> `DescriptorFor`, the per-CPU `BlockDelegate`, the data-driven register map, the cycle/budget/dispatch
> machinery) drives the complete 68000 faithfully — the same proof M3.5-3a delivered for the Z80, now on the
> 32-bit-register / 24-bit-address / big-endian / word-decode CPU. The 68000 is **all-fallback** (empty
> `JitDescriptorsByKey` → every op `Undefined`/`NeedsFallback`/`EndsBlock`), so a JIT block IS the interpreter,
> one instruction at a time; **no `BlockCompiler.Emit`/`Flow`/`Decimal` arm is reached; no 6502/Z80 emit logic
> changed.**
>
> **The THREE wiring gaps closed (pure-plumbing path):**
> - **GAP 1** — the `AdvanceCycles(long)` charge seam on a hand-written `M68000Cpu.Jit.cs` partial (increments
>   the generated `_cycles` field; the generated `JitTarget.AdvanceCyclesMethod` resolved to it, was `null`).
>   Mirrors `Z80Cpu.Jit.cs`. No emitted 68000 op calls it yet (all-fallback uses the `CycleCount` delta) — the
>   handle must merely RESOLVE for the `BlockCompiler` ctor.
> - **GAP 2** — `[assembly: InternalsVisibleTo("CpuEmulator.Jit")]` on `CpuEmulator.Cpus.M68000` (the JIT reaches
>   the internal `AdvanceCycles` via `DynamicMethod(skipVisibility:true)`).
> - **GAP 3** — the dispatcher's `ushort` PC cache key + byte-granular `BusFetchStream` (baked when the only CPUs
>   were 16-bit-PC). **Documented as a known all-fallback-safe limitation**, PINNED by a guard test (the
>   single-instruction-per-block invariant): in all-fallback mode the cache holds ≤1 block per single-instruction
>   case, so the `(ushort)PC` truncation never aliases, and the mis-fetched decode key is discarded (the empty
>   table routes every key to `Undefined`). The fallback `inner.Step()` sets the real 24-bit PC.
>
> **Decision A — RESOLVED = PURE PLUMBING (minimal).** The `uint`-key + per-CPU `IJitTarget.NewFetchStream`
> widening is **deferred to M6** (the cross-arch JIT-optimization phase), where the 68000's hot ops are first
> emitted as IL — at which point a 68000 block can statically continue past one instruction, the 24-bit PC
> arithmetic in `EmitChainOrExit`/`EmitBudgetCheck` (`Conv_U2`) goes live, and the widening is load-bearing and
> reviewed alongside the emit work it enables. The Task-2 **TRIPWIRE held**: the byte-granular `BusFetchStream`
> does NOT make `M68000Cpu.Decode` throw (an unmatched operword yields the illegal sentinel `0xFFFFFFFF`, routed
> to `Undefined`), so the minimal path was sufficient — no flip to the scoped widen.
>
> **Implementation finding (the one real bug the gate caught).** `JittedCpu.Run` is a budget-driven
> `while (budget > 0)` loop; the M4 interpreter charges a FLAT `UnitsConsumed*4` cycles, which is LESS than the
> case's real `c.Length` for most multi-cycle ops. So the original `Math.Max(c.Length,1)` budget left the loop
> positive after one instruction and ran a SECOND, garbage instruction at the advanced PC. The runner uses a
> **1-cycle budget** (`RunCaseThroughJit`) — every 68000 instruction charges ≥1 cycle, so the loop runs exactly
> one block == one instruction, mirroring the interpreter's single `Step()`. The data-axis result is produced
> entirely by the one fallback `Step`.
>
> **HONESTY carry-forwards (NOT overclaimed):**
> - The IMMEDIATE forms (ADDI/SUBI/ANDI/ORI/EORI/CMPI) + QUICK forms (ADDQ/SUBQ) have NO dedicated v1 vector
>   files (the M4.5b gap). Their tier parity is **TRANSITIVE** (their vector-proven reg↔EA counterparts go
>   through the identical fallback), NOT vector-green — the JIT sweep can only assert parity on files that EXIST.
> - The TIMING axis (final.pc/prefetch/trace/cycle) is NOT asserted through the JIT (gated on M4.5d-2) — same as
>   the interpreter data-axis sweeps. The `RunCaseThroughJit` path is structured so a follow-on can flip it on.
> - The per-transaction bus TRACE is NOT asserted under fastmem (the same scope the 6502/Z80 JIT sweeps disclose).
> - The corpus-artifact cases the interpreter sweeps exclude (the ASL.b inconsistent-register-shift vectors + the
>   CHK in-range UNPREDICTABLE-CCR cases) are excluded identically in the JIT sweep (`M68000DataAxisCorpus.IsExcludedCase`),
>   so the JIT corpus is identical in EXECUTED cases to the interpreter corpus.
>
> **Downstream unblocked.** M4.6 done unblocks **M5** (the 8086 arc) and the **post-M5 cross-arch
> JIT-optimization phase** (which folds in the deferred Z80 5-3b hot-op IL emission AND the 68000 hot-op emit +
> the `uint`-key widening — all reviewed together where the emitter is built once for all three ISAs).

## What is NEXT — M4.5 (the interpreter) and beyond

Per **ADR 0004 §3**, M4.5 splits family-by-family. Recommended order (highest-value/most-used first; each driven to TomHarte-green against the 124 vector files):

1. **M4.5a — MOVE** (incl. MOVEA, MOVE to/from SR/CCR/USP). ✅ **DONE** — the first executing 68000 opcodes; data axis TomHarte-green across all 10 MOVE-family files (see the "M4.5a — DONE" section above). Pulled forward the deferred two-EA length (D5). **Next = M4.5b (integer ALU).**
2. **M4.5b — the integer ALU families** (ADD/ADDA/ADDI/ADDQ, SUB/…, AND/ANDI, OR/ORI, EOR/EORI, CMP/CMPA/CMPI, NEG/NEGX/NOT/CLR/TST, MULU/MULS/DIVU/DIVS, EXT, ADDX/SUBX). ✅ **DONE** — the table-driven ALU layer (ADR 0007 option C: one `BinaryAluExecute` driver + the `Alu`/`AluCcr` descriptor tables, the irregular tail bespoke) behind the unchanged M4.5a dispatch seam. **Data axis TomHarte-green across the 51 in-scope ALU-family files (300,950 cases executed green; 106,602 deferred to M4.5d via `IsExceptionCase` — the `DIVU/DIVS` ÷0 + address-error/privilege cases).** **HONESTY:** the immediate forms (ADDI/SUBI/ANDI/ORI/EORI/CMPI) and quick forms (ADDQ/SUBQ) **EXECUTE but are NOT TomHarte-gated** — no `v1` vector files exist for them; they are covered by differential-equivalence (each ≡ its vector-proven reg↔EA counterpart through the same driver) + synthetic immediate-fetch tests only, NOT vector-green. `CMPM` is dropped (absent from the FieldGrammar dataset; its cases are bundled into `CMP.*`/`CMPA.l` and skipped as out-of-scope to M4.5c). The timing axis + the ÷0 vectoring are M4.5d. **Next = M4.5c (shift/rotate + bit + BCD + Scc + system-misc).**
3. **M4.5c — shift/rotate + bit + BCD + Scc + data-movement** (ASL/ASR/LSL/LSR/ROL/ROR/ROXL/ROXR reg/imm/memory-by-1, BTST/BCHG/BCLR/BSET dynamic+static, ABCD/SBCD/NBCD, Scc + the shared `EvaluateCondition`, CMPM, and the data-movement misc SWAP/EXG/LEA/PEA/MOVEQ/TAS/MOVEM/MOVEP). ✅ **DONE** — the ADR 0007 §7.1 verdict-(b) extension: BCD/bit slot into the merged `(AluFn,CcrRule,AluShape)` descriptor with new sibling CCR rules (`BcdCcr`/`BitCcr`, zero new shape), shift/rotate add ONE additive sibling driver (`ShiftRotateExecute` + the last-bit-out/msb-changed carrier + `ShiftCcr.Shift/Rotate/RotateX`); option (C) stands, the seam untouched. **Data axis TomHarte-green across all 42 dedicated v1 files (24 shift/rotate + 4 bit + 3 BCD + 1 Scc + 8 data-movement incl. MOVE.q + 2 MOVEP) PLUS the 51 ALU files re-run with the CMPM out-of-scope filter REMOVED — the bundled CMPM cases now ASSERT.** **HONESTY:** unlike M4.5b, NO vector-gap disclosure — every M4.5c core op has a dedicated vector file; CMPM (no dedicated file) asserts via the bundled `CMP.*`/`CMPA.l` files (the M4.5b honesty gap is CLOSED). Only the timing axis + exception cases (`IsExceptionCase`) are deferred. **The DC4 boundary was RATIFIED:** data-movement (SWAP/EXG/LEA/PEA/MOVEQ/TAS/MOVEM/MOVEP) landed in M4.5c; the stack/control/privileged/vectoring tail (LINK/UNLK, JMP/JSR/RTS/RTR/RTE, Bcc/BSR/DBcc, TRAP/TRAPV/CHK/ILLEGAL/RESET/STOP, ANDI/ORI/EORI-to-CCR/SR, NOP) moved to M4.5d. **Next = M4.5d (exceptions/branches/IPL/prefetch + the control/stack/privileged tail + the timing axis).**
4. **M4.5d — exceptions + control:** the program-control branches (Bcc/BSR/DBcc), the **Address-Error exception** (vector 3; uses `BusAlignment.IsMisaligned` from M4.2 — check before a wide access, vector instead), other exceptions (illegal/privilege/TRAP/CHK/divide-by-zero), the **IPL-level interrupt line** (the additive contract growth from ADR 0004), the **prefetch-queue mechanism** (so the `final` prefetch can be asserted), and the **cycle-accurate write-back-vs-bus-access sequencing + the per-transaction trace gate**.

Then: **M4.6** — the 68000 through the JIT (all-fallback, zero JIT-assembly change per the M3.5-3c findings; the generic seam is ready). ✅ **DONE** — byte-identical Tier-0-vs-Tier-1 data-axis parity across the full family corpus through `JittedCpu<M68000Cpu>` (see the "M4.6 — DONE" section above); pure plumbing (GAP 1 AdvanceCycles + GAP 2 InternalsVisibleTo + the documented all-fallback-safe `ushort`-key limitation); the `uint`-key + per-CPU fetch-stream widening deferred to M6. Then **M5** — the entire 8086 milestone (needs its own Architect pass: segmentation, ModRM decode, the flag model, the instruction set; its own ADRs + multi-PR arc). Then the **final cross-arch JIT-optimization phase** (which also folds in the deferred Z80 5-3b hot-op IL emission AND the 68000 hot-op emit + the `uint`-key widening) — **gated behind M5; checkpoint with the user before starting it.**

## Open just-in-time deferrals M4.5 MUST honor

- **The MOVE two-EA length (D5, deferred from M4.4a) — ✅ DONE in M4.5a** (the two-EA length arm + the dest-EA extension-word capture). The **branch-displacement length** half (`Bcc.w`/`.l`/`BSR` read a following displacement word) is still M4.5d. **The brief-index `(d8,An,Xn)` index-register decode (also tagged "M4.5 detail (D5)" in the M4.3b EA stub) is ✅ DONE in M4.5a** (it was load-bearing for the MOVE data axis).
- **The 55 no-size families' computed length** (the M4.4a `sizeWidth:0`→inert-1-bit deviation: the generator's CPUGEN015 requires `sizeWidth >= 1`). Their per-family length is single-EA-correct only; finalize when their bodies land in M4.5.
- **The prefetch-queue mechanism + the per-transaction trace + cycle gate + `final.prefetch`/`final.pc` assertion = the M4.5d TIMING axis** (M4.5a confirmed against real vectors that the operword lives in `initial.prefetch[0]` and the transactions are prefetch-refill reads interleaved with operand accesses + idle cycles — the queue's observable state). M4.5a seeds the initial fetch from `prefetch[0]` (required to be data-axis-green) but does NOT assert the timing axis (`// TODO(M4.5d)` in the runner). **The address-error / privilege exception vectors are M4.5d too** — M4.5a defers the ~23,200 exception-taking MOVE cases (see the "M4.5a — DONE" section).

## How to resume (the established loop)

1. Dispatch **Planner** to expand **M4.5a (MOVE)** into a full execution-ready plan (M3.4/M4-depth: TDD tasks, literal code, the recon against the verified schema above + `ExtensionWordCount`/`ComputeEa`/the descriptor-row + op-body shape). Commit the plan to `main`.
2. Coordinator reviews + resolves any flagged decisions.
3. Dispatch **Builder** to execute M4.5a to **TomHarte-green** against `MOVE.*.json.gz` (run the heavy gate under `dotnet test -c Release`; the 680x0 vectors must be fetched first via the script) → PR → merge.
4. Loop M4.5b → M4.5c → M4.5d, then M4.6, then M5.

## Operational notes (infra flakiness seen this session — mitigations)

- **Empty agent spawns** (a fresh subagent occasionally returns boilerplate with 0 tool uses, doing nothing): just re-dispatch a fresh agent; the repo is untouched (0 tool uses guarantees it). Happened ~every other dispatch late in the session.
- **API socket deaths** mid-run (esp. long Planner recons): re-dispatch fresh (resuming the dead agent has been unreliable). Verify nothing was committed first.
- **Heavy-gate contention:** running multiple heavy `dotnet test` gates concurrently starves them (a 42-min no-output stall happened). Run heavy gates **sequentially, under `-c Release`** (the ZEXALL/680x0 sweeps are billions of cycles — Release does them in minutes, Debug in hours). Use a **coarse** monitor (wake on terminal `Passed!`/`Failed!`/`error`/`Exception`, not per-test).
- **Leftover `testhost.exe` workers** can slow a fresh suite run (>10 min, no output); kill them and re-run clean (~4.5 min).

## Pointers

- ADRs: `docs/architecture/0003-68000-state-width-and-bus.md`, `docs/architecture/0004-68000-decode-addressing-and-exceptions.md`.
- M4 plans: `2026-06-15-m4-1-…`, `…-m4-2-…`, `…-m4-3a-…`, `…-m4-3b-…`, `…-m4-4a-…`, `…-m4-4b-…` (all in `docs/superpowers/plans/`).
- The Z80 finish-line overview (the JIT-genericity findings + the deferred 5-3b emit spec): `2026-06-14-m3-z80-finish-line-overview.md`, `…-m35-3c-jit-genericity-findings.md`.
