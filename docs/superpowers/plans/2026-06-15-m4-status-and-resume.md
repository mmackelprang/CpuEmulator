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

## What is NEXT — M4.5 (the interpreter) and beyond

Per **ADR 0004 §3**, M4.5 splits family-by-family. Recommended order (highest-value/most-used first; each driven to TomHarte-green against the 124 vector files):

1. **M4.5a — MOVE** (incl. MOVEA, MOVE to/from SR/CCR/USP). The proof the interpreter pipeline works end-to-end. **Pulls forward the deferred two-EA length** (see deferrals below).
2. **M4.5b — the integer ALU families** (ADD/ADDA/ADDI/ADDQ, SUB/…, AND/ANDI, OR/ORI, EOR/EORI, CMP/CMPA/CMPI, NEG/NOT/CLR/TST, MULU/MULS/DIVU/DIVS, EXT).
3. **M4.5c — shift/rotate + bit ops** (ASL/ASR/LSL/LSR/ROL/ROR/ROXL/ROXR, BTST/BCHG/BCLR/BSET), Scc, the BCD ops (ABCD/SBCD/NBCD), and the system/misc ops (MOVEM, LEA/PEA, SWAP, EXG, LINK/UNLK, TRAP/TRAPV/CHK, NOP, RTS/RTR/JMP/JSR).
4. **M4.5d — exceptions + control:** the program-control branches (Bcc/BSR/DBcc), the **Address-Error exception** (vector 3; uses `BusAlignment.IsMisaligned` from M4.2 — check before a wide access, vector instead), other exceptions (illegal/privilege/TRAP/CHK/divide-by-zero), the **IPL-level interrupt line** (the additive contract growth from ADR 0004), the **prefetch-queue mechanism** (so the `final` prefetch can be asserted), and the **cycle-accurate write-back-vs-bus-access sequencing + the per-transaction trace gate**.

Then: **M4.6** — the 68000 through the JIT (all-fallback, zero JIT-assembly change per the M3.5-3c findings; the generic seam is ready). Then **M5** — the entire 8086 milestone (needs its own Architect pass: segmentation, ModRM decode, the flag model, the instruction set; its own ADRs + multi-PR arc). Then the **final cross-arch JIT-optimization phase** (which also folds in the deferred Z80 5-3b hot-op IL emission) — **gated behind M5; checkpoint with the user before starting it.**

## Open just-in-time deferrals M4.5 MUST honor

- **The MOVE two-EA + branch-displacement LENGTH (D5, deferred from M4.4a).** `ExtensionWordCount` is single-EA today; MOVE's true length = source-EA + dest-EA extension words (dest EA is at bits 11–6 with mode/register *swapped*), and `Bcc.w`/`.l`/`BSR` read a following displacement word. M4.5a adds the two-EA length arm (a generator change) when the MOVE descriptor rows that read those words land.
- **The 55 no-size families' computed length** (the M4.4a `sizeWidth:0`→inert-1-bit deviation: the generator's CPUGEN015 requires `sizeWidth >= 1`). Their per-family length is single-EA-correct only; finalize when their bodies land in M4.5.
- **The prefetch-queue final-state assertion** (M4.4b carries it but asserts nothing — M4.5d wires the prefetch mechanism, then asserts).
- **The cycle-accurate write-back sequencing + the per-transaction trace gate** (M4.3b shipped the correct EA helper in isolation; M4.5 wires it into op bodies under the vector trace gate).

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
