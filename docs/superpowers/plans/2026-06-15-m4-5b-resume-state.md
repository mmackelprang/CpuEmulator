# M4.5b (68000 Integer ALU) — In-Progress Resume State

> **Purpose:** a single-file recovery point to resume M4.5b in a FRESH session (context was running low
> mid-implementation; this hands off the exact state + the integration findings already paid for).
> **Read this first**, then ADR 0007 + the M4.5b plan, then continue from "What REMAINS" below.
>
> **Role context:** this session is **Claude Coordinator** orchestrating the M4.5 (68000 interpreter) arc.
> M4.5b's Builder subagent hit the "empty agent spawn" infra glitch **3× consecutively** (0-tool-use no-ops),
> so per the user's choice the Coordinator began **driving M4.5b directly in the main loop** (spawn-free). The
> foundation is committed; the rest can be finished by either a fresh Coordinator main-loop drive OR a fresh
> Builder subagent resuming from the committed foundation (the glitch is intermittent — may have cleared).

## Exact git state (verified)
- **Branch:** `feat/m4-5b-integer-alu`, **HEAD `bb2bc91`** (the ALU foundation commit), clean tree, +1 ahead of
  `main`. **`main` = `origin/main` = `78f4d4f`** (synced; carries the M4.5b plan + ADRs 0003-0007 + 8086 recon).
- **Branch adds (vs main):** `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs` +
  `tests/CpuEmulator.Tests/Generators/M68000AluCcrTests.cs`.
- Recommend `git push -u origin feat/m4-5b-integer-alu` so the branch survives off-box.

## Governing docs (authoritative — IMPLEMENT, do not re-decide)
- **`docs/architecture/0007-68000-interpreter-op-body-structure.md`** — decision **(C)**: the table-driven ALU
  helper layer. §5 structure, §5.4 seam invariant, §6 gate, §7 open questions.
- **`docs/superpowers/plans/2026-06-15-m4-5b-integer-alu.md`** — the full execution plan (2346 lines, literal
  code per task, `## Decisions (RESOLVED by Coordinator)` block: **D1 → implement imm/quick with hardening**).
  The remaining tasks below cite this plan's line ranges.

## What is DONE (committed at `bb2bc91`, 14 unit tests green)
`M68000Cpu.Alu.cs` (the new sibling partial) carries:
- The descriptor types: `AluFn` / `CcrRule` delegates, `AluShape{RegEa,ImmEa,QuickEa,UnaryEa}` enum.
- `Alu` static class (pure funcs): `Add`, `Sub` (NO-X form: `a+b` / `a-b`), `And`, `Or`, `Eor`, `AddX`, `SubX`
  (honor X), `NegFn` (`0-a`), `NotFn` (`~a`), `TstFn` (identity). **MISSING (add in Task 10): `NegXFn`.**
- `SizeMaskProbe` (test seam), `BinaryAluExecute` driver, `ShiftExt` (immediate-word slice helper).
- `AluCcr` static class (the CCR rules): `Arith`(internal) + `ArithAdd`/`ArithSub`, `Logic`, `NegRule`, `Cmp`,
  `ArithX`(internal) + `ArithXAdd`/`ArithXSub`, + `*Probe` test seams. **MISSING (add in Task 10): `NegXRule`.**
- `M68000AluCcrTests.cs`: 14 tests (Alu.* sums/logic + CCR carry/overflow/borrow/Logic-keeps-X/Cmp-no-X/
  sticky-Z) — **all green** (`dotnet test --filter "FullyQualifiedName~M68000AluCcrTests"` = 14/14).

## ⚠️ CRITICAL integration findings (already paid for — honor these; the plan's literal code does NOT account for them)

1. **RENAME `Ccr` → `AluCcr` everywhere.** `M68000Cpu` already exposes a live **`Ccr` property** (the CCR
   register accessor, `M68000Cpu.cs:41`), so the plan's nested rules class named `Ccr` is a CS0102 duplicate.
   The foundation already uses **`AluCcr`**. **Every body you transcribe from the plan that references
   `Ccr.ArithAdd` / `Ccr.ArithSub` / `Ccr.Logic` / `Ccr.Cmp` / `Ccr.NegRule` / `Ccr.ArithXAdd` /
   `Ccr.ArithXSub` / `Ccr.NegXRule` MUST be rewritten as `AluCcr.*`.** (The driver's `bool xIn = (Ccr & 0x10)`
   correctly reads the live `Ccr` PROPERTY — leave that as `Ccr`.)

2. **The systematic read-modify-write DOUBLE-COMPUTE bug (the #1 thing the sweep will catch).** The driver's
   RMW shapes (RegEa-toEa, ImmEa, QuickEa, UnaryEa with a memory dest) read the dest EA via `ReadEaOperand`
   then write via `WriteEaOperand` — each calls `ComputeEa(...,pureEa:false)`, which performs the `(An)+` /
   `-(An)` **write-back**. So for dest modes **3 (`(An)+`)** and **4 (`-(An)`)** the address register advances
   **TWICE** and the write lands at the wrong address. **Fix with the "address-once" pattern** the plan already
   shows for CLR (plan Task 9, lines 1540-1554): for modes 3/4, call `ComputeEa(...)` ONCE to get `ea` (single
   write-back), then `ReadByteAt`/`ReadWordBus`/`ReadLongBus` + `WriteByteAt`/`WriteWordBus`/`WriteLongBus`
   directly at `ea`. Apply this to the driver's RMW path (RegEa-toEa / ImmEa / QuickEa / UnaryEa-write), not
   just CLR. Register/simple-memory modes are unaffected (no write-back). **Recommend fixing this proactively in
   `BinaryAluExecute` before running the sweep** — it WILL fail `(An)+`/`-(An)` dest cases otherwise.

3. **CCR formulas are the highest-bug-density code; the heavy sweep is ground truth.** Expect 1-3 reconciliation
   rounds on signed-V, the SUBX/ADDX sticky-Z + `-(An),-(An)` pairing order, and the DIV V-overflow / N-Z-on-
   quotient rules. Fix in `AluCcr` (ONE place) — never scatter into bodies. (M4.5a needed 3 such bug fixes.)

## What REMAINS (execution order — the plan has literal code for each)

> Execute **Task 13 (generator) FIRST** — the bodies' `partial void` declarations must exist before Tasks 3-12
> compile. Then fill bodies 3-12, then the sweep (Task 14). All `*Execute` partials share the signature
> `(uint operword, DecodeResult r, uint size, uint srcMode, uint srcReg)`.

1. **Task 13 — generator dispatch (plan lines 1929-2057).** In `src/CpuEmulator.Generators/CpuEmitter.cs`:
   (a) extend the `op switch` in `EmitMoveDispatchArms` (~`:4209`) with the 30 ALU operation-name → `*Execute`
   arms (literal at plan 1944-1985 — these reference hook NAMES, no `Ccr` rename needed); (b) add the 29
   `private partial void {name}Execute(...)` declarations to the FieldGrammar-gated emit (~`:307-318`, literal
   at plan 1995-2003). NO other generator change. Gated to `model.FieldGrammar is not null` (6502/Z80 untouched).
2. **Tasks 3-12 — bodies, appended to `M68000Cpu.Alu.cs` (apply the `Ccr`→`AluCcr` rename to ALL):**
   - T3 RegEa registrations ADD/SUB/AND/OR/EOR/CMP (plan 808-824).
   - T4 ADDA/SUBA/CMPA via `AddrAlu` (plan 905-931; uses `AluCcr.Cmp`).
   - T5 NEG/NOT/TST via UnaryEa (plan 1003-1027; `Alu.NegFn`/`NotFn`/`TstFn` + `AluCcr.NegRule`/`Logic`).
   - T6 ADDI/SUBI/ANDI/ORI/EORI/CMPI via ImmEa (plan 1123-1140). **First confirm the decode walk captures the
     leading immediate word** (plan Step 2, lines 1100-1121): run the `Addi_w_decode_captures...` test; if RED,
     add the immediate-form decode arm in `EmitFieldDecodeWalk` (generator) per the plan note.
   - T7 ADDQ/SUBQ via `QuickAlu` (plan 1320-1339; An-dest = full-32 + no CCR).
   - T8 EXT bespoke (plan 1440-1465; `AluCcr.Logic`).
   - T9 CLR bespoke (plan 1522-1554) — **use the address-once form** (lines 1544-1551).
   - T10 ADDX/SUBX via `XAlu` + NEGX (plan 1584-1703) — **also ADD `Alu.NegXFn` + `AluCcr.NegXRule`** (plan
     1587-1598) which are NOT in the foundation yet. `AluCcr.ArithXAdd`/`ArithXSub` already exist.
   - T11 MULU/MULS via `Mul` (plan 1770-1789; `AluCcr.Logic`).
   - T12 DIVU/DIVS via `Div` (plan 1849-1901; ÷0 detect-and-defer; V-overflow; `AluCcr` not used — sets CCR
     inline, but if it references `Ccr.Logic` rename to `AluCcr.Logic`).
3. **Test file `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs`** — the Tasks 3-12 execute tests
   (most `[Fact(Skip="dispatch wired in Task 13")]` → un-skip after Task 13) + the **D1 differential-equivalence
   tests** (`RunImm`/`RunReg`, imm≡reg & quick≡reg — plan 1150-1216, 1347-1360) + the synthetic immediate/quick
   FETCH tests + the dispatch smoke test (plan 2017-2028). These are the load-bearing D1 honesty hardening.
4. **Task 14 — the sweep `tests/CpuEmulator.Tests/TomHarte/M68000AluTomHarteTests.cs`** (plan 2074-2135, the
   51-file MemberData theory). **FIRST verify the real runner/loader API** against `M68000TomHarteRunner.cs` /
   `M68000TomHarteLoader.cs` / `M68000TomHarteVectors.cs` (the plan assumes `RunCase` returns `string?`,
   a `DeferredException` sentinel, `LoadFile`, `TryGetVectorDirectory`, `[M68000TomHarteTheory]` — confirm the
   exact names; M4.5a's `M68000TomHarteTests.cs` is the working reference to copy the API from).
5. **Run the heavy gate:** `pwsh tools/get-test-vectors-68000.ps1` (idempotent), then
   `dotnet test -c Release --filter "FullyQualifiedName~M68000AluTomHarteTests"`. Reconcile failures (finding #2
   + #3 above). COARSE monitor (wake on `Passed!`/`Failed!`/`error`/`Exception`); kill stray `testhost.exe`
   first. Show the green EXECUTED count across the 51 files (NOT skipped).

## The three-part MERGE GATE (ADR 0007 §6 — all three required; merge blocked otherwise)
1. **Full suite green + 6502/Z80 byte-identical** (`RegeneratedSpecTests`); every change additive (gated to
   `model.FieldGrammar is not null` + the `M68000Cpu.Alu.cs` partial + the 680x0-only test infra).
2. **The 51-file ALU TomHarte data-axis sweep RUN GREEN WITH VECTORS PRESENT** under `-c Release` — NOT skipped;
   show the executed count. Data axis = `D0-D7, A0-A6, USP, SSP, SR, RAM` (operword seeded from
   `initial.prefetch[0]`). DEFER to M4.5d: `final.pc`/`final.prefetch`/trace/cycle + the DIVU/DIVS ÷0 exception +
   address-error/privilege (detect-and-defer via the runner's exception heuristic).
3. **Pre-merge code review** (point it at CCR correctness — the highest-bug-density area).
**HONESTY (non-negotiable):** ADDI/SUBI/ANDI/ORI/EORI/CMPI/ADDQ/SUBQ EXECUTE but are **NOT TomHarte-gated** (no
v1 vectors) — covered by differential-equivalence + synthetic fetch tests only. State this plainly in the PR
body + the status/resume doc; never claim/imply they are vector-green. CMPM is dropped (absent from dataset).

## The SEAM INVARIANT (ADR 0007 §5.4 — binding; a `git diff --stat` should show these UNCHANGED)
Do NOT touch: `src/CpuEmulator.Core/Jit/M68000FetchStream.cs`, the `M68000Cpu.cs` bus helpers
(`ReadWordBus`/`WriteWordBus`/`ReadLongBus`/`WriteLongBus`, `:72-110`), the Step+diff runner
`tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs`, and `M68000Cpu.Move.cs`. M4.5b only ADDS the ALU
layer (`M68000Cpu.Alu.cs`) + the two generator edits + the two new test files. (Exception: Task 6 MAY add an
immediate-form decode arm in `EmitFieldDecodeWalk` IF the decode test is red — that's the generator, allowed.)

## After M4.5b merges (the arc)
Update `2026-06-15-m4-status-and-resume.md` (M4.5b done). Then loop: **M4.5c** (shift/rotate/bit/BCD/Scc/
system-misc — also where ADR 0007 §7.1's descriptor-generalization question is answered empirically, gating the
eventual (B) op-table promotion) → **M4.5d** (exceptions/branches/IPL/prefetch + the deferred TIMING axis) →
**M4.6** (68000 through the JIT) → **M5** (8086 — foundation banked in ADRs 0005/0006 + the 8088 vector schema/
`tools/get-test-vectors-8088.ps1`/fixture; implementation sequenced after M4.6). The user wants the full
test/auto-UAT/review cycle on EVERY PR (anti-drift): the un-fakeable TomHarte green sweep is that gate.

## Operational notes (infra)
- **Empty agent spawns** (0-tool-use boilerplate no-ops) hit the Builder ~every-other-dispatch late in sessions
  — happened 3× consecutively for M4.5b. Re-dispatch fresh; the repo is untouched (0 tool uses guarantees it).
- **Heavy gates SEQUENTIAL under `-c Release`** (Debug takes hours; the 51×8065 sweep is minutes in Release).
  Coarse monitor. Kill leftover `testhost.exe` before a fresh suite run.
- `TreatWarningsAsErrors=true` but no `EnforceCodeStyleInBuild` — unused-private-member (IDE0051) does NOT fail
  the build, so the driver compiled standalone in the foundation.
