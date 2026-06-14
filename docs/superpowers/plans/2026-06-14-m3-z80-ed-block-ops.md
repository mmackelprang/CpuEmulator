# M3.4d: The Z80 ED Block Ops (0xA0–0xBB) — TomHarte-Green

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** bring the **16 Z80 ED block ops (0xA0–0xBB)** — LDI/LDD/LDIR/LDDR, CPI/CPD/CPIR/CPDR,
INI/IND/INIR/INDR, OUTI/OUTD/OTIR/OTDR — from the `// TODO(semantics)` skeleton to a flag-correct,
cycle-correct, port-correct, repeat-correct, **TomHarte-green** interpreter, modeling the repeating ops'
PC-rewind quirk, the F3/F5 (X/Y) undocumented-flag quirks, the BC/DE/HL auto-inc/dec, and the per-op WZ
rules.

**Architecture:** M3.4c shipped the machine the block ops reuse: the algorithmic ED semantics generator
(`Z80EdSemantics.OpsFor` — which currently returns `null` for 0xA0–0xBB), the two ED instruction classes
(`Z80EdIo`/`Z80EdOp`), the composable flag emitter (`EmitZ80FlagWord`/`EmitZ80Alu8`), the `EmitWz` helper,
the universal final Q/WZ/IM check (`checkInternal` retired), the I/O bus wiring (`ReadIo`/`WriteIo`), and
the per-T-state TomHarte harness. M3.4d is **additive and analogous**: a new `Z80EdBlock` instruction class
(the block ops are neither pure `Z80EdIo` nor `Z80EdOp` — they combine memory + ports + a conditional
PC-rewind), `Z80EdSemantics.OpsFor` extended to emit the block-op rows for 0xA0–0xBB (the dataset rows
ALREADY exist — no F1 gap), a new `EmitZ80EdBlockBody` emit arm reusing the existing flag helpers, and the
ED TomHarte theory widened to cover 0xA0–0xBB. Every 6502 artifact stays byte-identical.

**Tech Stack:** C# (.NET 10), a Roslyn incremental source generator (`CpuEmulator.Generators`), a console
spec importer (`CpuEmulator.SpecImporter`) that regenerates `Z80Spec.cs` from `z80-opcodes.json` +
`z80-semantics.json`, and xUnit + the SingleStepTests/z80 vectors (TomHarte).

---

## Scope

**IN scope (the 16 ED block ops come alive end-to-end):**

1. **The 16 ED block opcodes 0xA0–0xBB.** Octal `10 yyy zzz` (x=2): the family is `z ∈ {0,1,2,3}`
   (LD/CP/IN/OUT), `y ∈ {4,5,6,7}` (I-direction + repeat). Concretely:
   - **0xA0 LDI / 0xA8 LDD / 0xB0 LDIR / 0xB8 LDDR** — `(DE) ← (HL)`; HL±1; DE±1; BC−1.
     - Flags: H=0, N=0; P/V = (BC−1 ≠ 0); S/Z/C **preserved**. **X/Y from `A + transferred-byte`**
       (the undocumented quirk: bit 1 of `(A + n)` → F5/Y… see the exact rule below). **WZ unchanged**
       (LDI/LDD). The repeating forms (LDIR/LDDR): if BC−1 ≠ 0, **PC rewinds by 2** (re-execute) and
       **WZ = PC+1** (of the instruction); cycles 21. Final iteration (BC−1 == 0): PC advances; cycles 16.
   - **0xA1 CPI / 0xA9 CPD / 0xB1 CPIR / 0xB9 CPDR** — compare `A − (HL)` (result NOT stored); HL±1;
     BC−1.
     - Flags: S/Z/H from the compare; N=1; P/V = (BC−1 ≠ 0); C **preserved**. **X/Y from
       `A − (HL) − H`** (the n = compare-result minus half-carry quirk). **WZ = WZ ± 1** (CPI: WZ+1;
       CPD: WZ−1 — confirmed against `ed a1.json`: WZ 37025→37026). Repeating (CPIR/CPDR): repeats while
       BC−1 ≠ 0 AND Z == 0 (not yet matched); PC rewind + WZ=PC+1; cycles 21/16.
   - **0xA2 INI / 0xAA IND / 0xB2 INIR / 0xBA INDR** — `(HL) ← IN (C)`; B−1 (the byte counter); HL±1.
     - Flags: the documented S/Z/X/Y from B−1, N = bit 7 of the input byte, plus the messy
       H/C/P-V quirk: `k = inputByte + ((C ± 1) & 0xFF)`; H = C = (k > 0xFF); P/V = parity of
       `((k & 7) ^ B_after)`. **WZ = BC(after B−1) + 1** (INI/INIR) — confirmed `ed a2.json`: WZ=26211.
       Port READ in the ports array. Repeating: repeats while B−1 ≠ 0; PC rewind + WZ=PC+1; cycles 21/16.
   - **0xA3 OUTI / 0xAB OUTD / 0xB3 OTIR / 0xBB OTDR** — `OUT (C) ← (HL)`; B−1; HL±1.
     - Flags: S/Z/X/Y from B−1, N = bit 7 of the output byte, the H/C/P-V quirk: `k = outByte + L_after`;
       H = C = (k > 0xFF); P/V = parity of `((k & 7) ^ B_after)`. **WZ = BC(after B−1) + 1** for OUTI/OTIR
       (`BC−1` direction for OUTD/OTDR — derive each from the vector). Port WRITE in the ports array.
       Repeating: repeats while B−1 ≠ 0; PC rewind + WZ=PC+1; cycles 21/16.
2. **A new `Z80EdBlock` instruction class** (mode `Implied`) — the block ops combine memory, ports, a
   conditional PC-rewind, and per-op WZ, which neither `Z80EdIo` nor `Z80EdOp` models.
3. **`Z80EdSemantics.OpsFor` extended** to emit the block-op rows for opcodes 0xA0–0xBB (it currently
   returns `null` there — the explicit M3.4c deferral).
4. **The ED TomHarte gate widened** — the `CoveredEdPlaneOpcodes` theory's probe range extended from
   0x40–0x7F to ALSO cover 0xA0–0xBB, loading `ed a0.json` … `ed bb.json`.

**OUT of scope (each is a later PR — do NOT reach for it):**

- **The DD/FD/DDCB/FDCB planes** (IX/IY `(IX+d)`, the compound-prefix decoder) = M3.4e (next).
- **Interrupt SERVICING** — `TryServiceInterrupt` stays `=> false`. (Note: the repeating block ops are
  interruptible on real hardware between iterations, but TomHarte models each single-step vector as one
  uninterrupted execution — there is no interrupt-during-repeat to model in the single-step gate.)
- **The Z80 through the JIT (M3.5)** — the block-op rows emit as JIT FALLBACKS (the descriptor table must
  be well-formed) but the JIT never emits IL for them; the Z80 runs interpreter-only.

> **The honest one-liner for M3.4d's close-state:** the Z80 base + CB + ED-core (0x40–0x7F) + the 16 ED
> block ops (0xA0–0xBB) run and are TomHarte-green (registers incl. F's X/Y, I/R, IM, IFF1/IFF2, WZ, Q,
> memory, ports, AND per-T-state bus trace, including the repeating ops' PC-rewind). The DD/FD/DDCB/FDCB
> planes remain `// TODO`; interrupt servicing + the JIT are unimplemented for the Z80. "TomHarte-green" is
> asserted over the 16 ED block opcodes (16,000 cases) + the re-validated base + CB + ED-core, enumerated
> honestly in the closeout.

---

## Ground truth — what M3.4a/b/c ALREADY shipped (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 0** — the block ops REUSE them.

- **The ED semantics generator** — `tools/CpuEmulator.SpecImporter/Z80EdSemantics.cs`: `OpsFor(int opcode)`
  returns the ED-core ops-text for 0x40–0x7F and **`null` for 0xA0–0xBB** (the `if (opcode is < 0x40 or >
  0x7F) return null;` guard — M3.4c's explicit deferral). M3.4d adds a block-op arm BEFORE that guard so
  0xA0–0xBB returns real ops.
- **The importer routing** — `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs:165-172` (the `z80Ops`
  computation routes `Prefix == "0xED"` through `Z80EdSemantics.OpsFor`; when it returns non-null the row
  emits, when null the row stays `// TODO(semantics)`). The 16 block-op dataset rows ALREADY exist
  (`z80-opcodes.json:5470-5629`, `mode: Implied`, `bytes: 2`, `cycles: 16`) and currently route to the
  per-mnemonic map (LDI/CPI/… → `[]`, like NEG did pre-M3.4c) so they emit empty. Once `Z80EdSemantics`
  returns non-null for them, the per-mnemonic map is BYPASSED (the M3.4c F3 mechanism).
- **The two existing ED classes** — `src/CpuEmulator.Generators/SpecModel.cs` (`Z80EdIo`/`Z80EdOp` in the
  `InstructionClass` enum, M3.4c). M3.4d adds `Z80EdBlock`.
- **The composable flag/ALU helpers** — `src/CpuEmulator.Generators/CpuEmitter.cs`: `EmitZ80Alu8`
  (the 8-bit ALU + flag template, used for CPI's `A − (HL)` compare), `EmitZ80FlagWord`, the `FlagBitMap`
  + `flags.BitOf("X")`/`BitOf("Y")` mask idiom (M3.4c `EmitZ80EdIoBody` is the exact pattern for setting
  X/Y from a byte), the `EmitWz` helper (M3.4c, `WZ = unchecked((ushort)(<expr>));`), `EmitInternal`
  (the cycle balancer), `Z80WritesFlags` (the Q-write predicate), `Z80Cycles` (the cycle table), the
  dispatch `switch (opClass)`, the `isZ80` interpreter predicate, the JIT predicates (`ClassifyForJit`,
  `JitBaseCycles`, `JitOpLiteral`).
- **The PC-rewind primitive** — the structured `Step` reads `__r.Length` key bytes and advances PC by the
  decode length. A block op's body must, on a repeat, SUBTRACT the key length from PC so the next Step
  re-fetches the same instruction. Confirm how `Step` advances PC (`CpuEmitter.cs` — the
  `PC = (ushort)(PC + __r.Length)` after `OnInstructionFetched`) so the rewind expression is exact:
  `PC = unchecked((ushort)(PC - 2));` (ED block ops are 2 key bytes). **CONFIRMED against `ed b0.json`
  (LDIR, BC≠1):** initial pc=11557, final pc=11557 (NO net advance — Step advanced +2, the body rewound
  −2), 21 cycles, wz=11558 (=instruction PC + 1).
- **The I/O bus wiring** — `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs:100-111` (`ReadIo(uint)`/`WriteIo(uint, byte)`,
  each charges one cycle). The INI/IND/OUTI/OUTD ops call these (the M3.4c `EmitZ80EdIoBody` is the model).
- **The runner** — `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs:24` (`RunCase(Z80TomHarteCase c,
  bool registersOnly = false)` — `checkInternal` retired M3.4c; sets `cpu.Im = s.Im` at `:51`; checks final
  WZ/Q/IM at `:69-71`). The repeating ops' final PC is checked like any other register, so the rewind is
  gated automatically.
- **The harness theory** — `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs`: `CoveredEdPlaneOpcodes`
  (probes `0xED00 | op` over 0x40–0x7F) + `Ed_opcode_matches_TomHarte_vectors` (loads `ed {op:x2}.json`,
  the SPACE in the filename). M3.4d widens the probe to also cover 0xA0–0xBB.
- **The case schema** — `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteCase.cs:36-93` (the `Z80State` carries
  every field the block ops touch — BC/DE/HL via B/C/D/E/H/L, WZ, Q, ports; `ReadState` parses them all).
  **No loader change.**
- **The vectors** — `~/.cache/cpuemulator/vectors/z80/v1/ed a0.json` … `ed bb.json` (16 files: a0–a3,
  a8–ab, b0–b3, b8–bb), 1000 cases each = 16,000 — ALL CONFIRMED present. Files are named with a SPACE.

### RECON FINDINGS that contradict / refine the prose (the code/vectors WIN — flagged)

> These were discovered during the write-time recon by reading the dataset + sampling the vectors. The
> implementer MUST re-confirm each at Task 0 and treat the vector as ground truth.

- **F1 (the M3.4c gap does NOT recur here) — the dataset ALREADY has all 16 block-op rows.** Unlike the
  ED-core (which was missing 22 of 64), `z80-opcodes.json` carries LDI through OTDR at lines 5470–5629
  (`prefix: "0xED"`, `opcode: 0xA0`..`0xBB`, `mode: Implied`, `bytes: 2`, `cycles: 16`). So **Group B has
  NO "add the missing rows" task** — the rows exist; they merely route to `[]` today. The work is purely
  the `Z80EdSemantics` block arm + the emit arm + the regen. CONFIRM the row count is 16 at Task 0.
- **F2 — No new AddrMode is needed.** All 16 block ops are `mode: Implied` (the operands are the implicit
  BC/DE/HL/A/C registers, opcode-encoded). `Implied` is already in `SupportedModes`. The new `Z80EdBlock`
  class validates to `Implied` only.
- **F3 — The repeating ops do NOT advance PC when they repeat.** CONFIRMED `ed b0.json` (LDIR, BC≠1):
  init.pc == final.pc (the body rewinds −2 to cancel Step's +2), cycles 21, wz = instruction-PC + 1. The
  NON-repeating single forms (LDI/LDD/CPI/CPD/INI/IND/OUTI/OUTD) ALWAYS advance PC (cycles 16). The
  repeating forms on their FINAL iteration (count hit zero / CP matched) advance PC (cycles 16). So the
  rewind is conditional: `if (<repeats>) { PC -= 2; WZ = PC_of_instruction + 1; _cycles += 5; }`. Derive
  the exact repeat condition per family from the vectors (LD/IN/OUT: BC−1 ≠ 0 for LDIR/etc, B−1 ≠ 0 for
  INIR/etc; CP: BC−1 ≠ 0 AND Z == 0).
- **F4 — The X/Y (F3/F5) undocumented-flag quirk differs per family** and is the load-bearing block-op
  detail (the SingleStepTests vectors check X/Y):
  - **LDI/LDD/LDIR/LDDR:** `n = (A + transferredByte) & 0xFF`; F5(Y) = bit 1 of n; F3(X) = bit 3 of n.
    (NOTE: Y comes from bit **1**, not bit 5 — the block-op anomaly. Re-derive from `ed a0.json`.)
  - **CPI/CPD/CPIR/CPDR:** `n = (A − (HL) − H_flag) & 0xFF`; F5(Y) = bit 1 of n; F3(X) = bit 3 of n.
  - **INI/IND/OUTI/OUTD (+repeats):** S/Z/X/Y from the DECREMENTED B (the normal bit 7/0/3/5 of B), NOT the
    bit-1 anomaly. The H/C/P-V come from the messy `k` quirk (below).
  **Re-derive EVERY mask from the vectors at Task 0 — do NOT trust this prose.** The block-op X/Y rules are
  the single most error-prone Z80 detail; the vector is the oracle.
- **F5 — The IN/OUT block ops' H/C/P-V quirk** (the genuinely messy part): for INI/INIR, `k = inByte +
  ((C + 1) & 0xFF)`; for IND/INDR, `k = inByte + ((C − 1) & 0xFF)`; for OUTI/OTIR, `k = outByte + L`
  (L AFTER the HL increment); for OUTD/OTDR, `k = outByte + L` (L after decrement). Then H = C = (k > 0xFF);
  N = bit 7 of the transferred byte; P/V = parity of `((k & 7) ^ B_after)`. **Re-derive each `k` from the
  vectors** (`ed a2.json` INI, `ed a3.json` OUTI, etc.) — the exact `C ± 1` / `L` operand is the trap.
- **F6 — WZ rules per family** (CONFIRMED / to-confirm against the vectors):
  - LDI/LDD: **WZ unchanged** (`ed a0.json`: 21852→21852).
  - CPI: **WZ + 1**; CPD: **WZ − 1** (`ed a1.json` CPI: 37025→37026). The repeating CP forms on repeat set
    **WZ = instruction-PC + 1** (the rewind WZ overrides).
  - INI/INIR/OUTI/OTIR: **WZ = BC(after B−1) + 1**; IND/INDR/OUTD/OTDR: **WZ = BC(after B−1) − 1**
    (`ed a2.json` INI: WZ=26211 = (BC after B--) + 1). On repeat, **WZ = instruction-PC + 1** (the rewind).
  Re-derive all eight from the vectors at Task 0.
- **F7 — The Q lifecycle:** every block op WRITES flags (even OUTI/etc set S/Z/X/Y/H/C/P-V/N), so final.q =
  final.f for all 16. `Z80WritesFlags` must classify `Z80EdBlock` as always-flag-writing.
- **F8 — Cycle counts:** non-repeating + final-iteration = 16 T; repeating (PC-rewound) = 21 T (the dataset
  records `cycles: 16` = the base/final; the +5 for the repeat is added in the body). The bus-trace check
  (UAT=full) verifies the exact per-T-state pattern — the internal-cycle padding must match (re-derive the
  `_cycles += …` from the cycles-array length per family).

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Core/Specification/Op.cs` | Modify | The block-op `Op` records (`EdBlockOp`). |
| `src/CpuEmulator.Core/Specification/Spec.cs` | Modify | The `EdBlock` factory method. |
| `src/CpuEmulator.Generators/SpecModel.cs` | Modify | The `Z80EdBlock` `InstructionClass` member. |
| `src/CpuEmulator.Generators/SpecParser.cs` | Modify | The `EdBlock` `s_microOpSignatures`; the `Z80EdBlock` op-kind set + `ClassifyOps` + `ValidateModeForClass` + the status-touch predicate. |
| `tools/CpuEmulator.SpecImporter/SemanticsMap.cs` | Modify | The `EdBlock` arity in `FactoryArity`. |
| `tools/CpuEmulator.SpecImporter/Z80EdSemantics.cs` | Modify | The block-op arm: opcodes 0xA0–0xBB → `[EdBlock("<MNEMONIC>")]` (remove the `null` for that range). |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | `EmitZ80EdBlockBody` (the LD/CP/IN/OUT + repeat + flags) + `Z80Cycles` EdBlock arm + `Z80WritesFlags` EdBlock + `isZ80`/JIT predicates + dispatch. |
| `src/CpuEmulator.Cpus.Z80/Z80Spec.cs` | Modify (regenerated) | The regenerated spec — the 16 block-op rows now carry `[EdBlock(...)]` ops. |
| `tools/CpuEmulator.SpecImporter/data/z80-opcodes.json` | (NO change) | The 16 block-op rows already exist (F1). |
| `tests/CpuEmulator.Tests/Generators/Z80EdBlockVocabularyTests.cs` | Create | The `EdBlock` record + factory + classify (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80EdBlockLoadTests.cs` | Create | LDI/LDD flags (X/Y from A+n) + BC/DE/HL deltas + WZ unchanged + repeat PC-rewind (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80EdBlockCpTests.cs` | Create | CPI/CPD flags (N, the compare, X/Y) + WZ±1 + repeat condition (BC≠0 AND Z==0) (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80EdBlockIoTests.cs` | Create | INI/IND/OUTI/OUTD ports + the H/C/P-V `k` quirk + WZ = BC±1 + repeat (synthetic). |
| `tests/CpuEmulator.Tests/Importer/Z80EdBlockSemanticsTests.cs` | Create | `Z80EdSemantics.OpsFor` returns the right `EdBlock("…")` for each of the 16; null still for non-block ED. |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502
> additivity guards + the base/CB/ED-core planes staying green at the universal Q/WZ/IM bar), then commit.
> Tasks are dependency-ordered so the suite builds and stays green after every task. Literal code is given
> for every load-bearing piece. The synthetic-spec tests (via `GeneratorTestHost.CompileAndLoadType`)
> decouple from the real `Z80Spec.cs` regen, which lands atomically late (Task 6). Per the M3.4c deviation
> #1, structured synthetic fixtures use `IAddressSpace _bus` (NOT a raw `byte[]`) and declare
> `public byte Q;` + `public int Im;`.

### Task 0: Baseline + shipped-surface recon (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch.** Create the branch off the current main (PR #22 merge `8b9feab`):
  Run: `git switch -c feat/m3-z80-ed-block-ops`
  Expected: on the new branch, head at `8b9feab`.

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test`
  Expected: 2252 passed / 0 failed / 0 skipped (the M3.4c close-state). Record the EXACT count.
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean.

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each cited surface holds:**
  - `tools/CpuEmulator.SpecImporter/Z80EdSemantics.cs` — confirm `OpsFor` returns `null` for 0xA0–0xBB
    (the `if (opcode is < 0x40 or > 0x7F) return null;` guard); confirm the `Reg8`/`RpSp`/`ImMode` tables
    + the `MiscZ7` helper shape (the block arm mirrors this).
  - `src/CpuEmulator.Generators/CpuEmitter.cs` — `EmitZ80EdIoBody` (the X/Y-from-byte mask idiom +
    `ReadIo`/`WriteIo` + `EmitWz` + the `_cycles += …` convention — the block IN/OUT arm copies this),
    `EmitZ80Alu8` (the compare template for CPI), `Z80WritesFlags` (the ED arms), `Z80Cycles` (the ED
    arms), the dispatch `switch (opClass)` (the `Z80EdIo`/`Z80EdOp` cases — the block case slots beside
    them), the `isZ80` predicate, the JIT predicates (`ClassifyForJit`/`JitBaseCycles`/`JitOpLiteral`).
  - Confirm how `Step` advances PC after the body (the `PC = (ushort)(PC + __r.Length)` site) so the
    rewind expression `PC -= 2` is correct (ED block ops are 2 key bytes; CONFIRM `__r.Length == 2`).
  - `src/CpuEmulator.Generators/SpecModel.cs` (the `InstructionClass` enum — `Z80EdIo`/`Z80EdOp` present),
    `SpecParser.cs` (the ED op-kind sets `s_z80EdIoOpKinds`/`s_z80EdOpKinds`, `ClassifyOps`,
    `ValidateModeForClass`, the status-touch predicate, `s_microOpSignatures`, the `ArgKind` extractor).
  - `tools/CpuEmulator.SpecImporter/SemanticsMap.cs` (`FactoryArity` — the ED entries).
  - `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs` (`CoveredEdPlaneOpcodes` + the ED theory;
    confirm the probe range is 0x40–0x7F and the filename is `ed {op:x2}.json`).
  - `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs:100-111` (`ReadIo`/`WriteIo` signatures + cycle charge).

- [ ] **Step 4: Confirm the dataset has all 16 block-op rows (RECON FINDING F1).** Open
  `tools/CpuEmulator.SpecImporter/data/z80-opcodes.json` and confirm 16 rows with `prefix: "0xED"` and
  opcode in {A0,A1,A2,A3,A8,A9,AA,AB,B0,B1,B2,B3,B8,B9,BA,BB}, each `mode: Implied`, `bytes: 2`,
  `cycles: 16` (lines ~5470–5629). Expected: **16 rows present** (no F1 gap — no rows to add).

- [ ] **Step 5: Re-derive the flag/WZ/repeat rules from the vectors (the oracle — do NOT trust the prose).**
  Open and CONFIRM (pin these into the per-op tests):
  - **LDI** (`ed a0.json`): non-repeat, PC advances; BC−1, DE+1, HL+1; H=0,N=0; P/V=(BC−1≠0); S/Z/C
    preserved; **WZ unchanged**; X/Y from `(A + transferredByte)` — derive the EXACT bit mapping
    (bit 1→F5, bit 3→F3).
  - **LDIR** (`ed b0.json`): the BC≠1 case — init.pc==final.pc (rewind), cycles 21, wz=instruction-PC+1;
    the BC==1 case — PC advances, cycles 16.
  - **CPI** (`ed a1.json`): N=1; the compare flags; **WZ+1**; X/Y from `(A − (HL) − H)`.
  - **CPIR** (`ed b1.json`): repeat while BC−1≠0 AND Z==0; on match (Z==1) PC advances.
  - **INI** (`ed a2.json`): B−1, HL+1; the port read (in `ports`/the cycles bus-trace); **WZ = (BC after
    B−1) + 1** (=26211); the H/C/P-V `k = inByte + ((C+1)&0xFF)` quirk; N = bit7 of inByte.
  - **IND** (`ed aa.json`): the `C−1` direction; **WZ = (BC after B−1) − 1**.
  - **OUTI** (`ed a3.json`): the port write; `k = outByte + L_after`; the WZ rule.
  - **OUTD** (`ed ab.json`): the decrement direction.

- [ ] **Step 6:** No commit (read-only). Proceed to Task 1.

---

### Task 1: The `EdBlock` vocabulary + the `Z80EdBlock` class (Op record + factory + parser) (TDD)

> Add the `EdBlock(string Mnemonic)` op record + `Spec` factory + parser `s_microOpSignatures` + importer
> `FactoryArity` + the `Z80EdBlock` instruction class + a STUB emit arm so the spec table type-checks and
> the importer validates. No real body yet (Tasks 2–4).

**Design decision (recorded):** the 16 block ops map to ONE op record `EdBlockOp(string Mnemonic)` (the
mnemonic is the discriminator the emit arm switches on — LDI/LDD/.../OTDR) in ONE new class `Z80EdBlock`
(mode `Implied`). Rationale: the 16 ops share the body shape (memory/port transfer + BC/HL adjust +
conditional repeat + family-specific flags) — a single class with a per-mnemonic `switch` in
`EmitZ80EdBlockBody` is DRYer than 16 records or 4 classes, and the mnemonic is already the dataset's
discriminator (it routes by mnemonic today). (Alternative considered: 4 records LD/CP/IN/OUT — REJECTED:
the direction (I vs D) and repeat (R suffix) are orthogonal flags; carrying the full mnemonic string is the
simplest unambiguous key and matches how `Z80EdSemantics` already emits.)

**Files:**
- Modify: `src/CpuEmulator.Core/Specification/Op.cs`
- Modify: `src/CpuEmulator.Core/Specification/Spec.cs`
- Modify: `src/CpuEmulator.Generators/SpecModel.cs`
- Modify: `src/CpuEmulator.Generators/SpecParser.cs`
- Modify: `tools/CpuEmulator.SpecImporter/SemanticsMap.cs`
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (dispatch + STUB body + predicates)
- Test: `tests/CpuEmulator.Tests/Generators/Z80EdBlockVocabularyTests.cs` (create)

- [ ] **Step 1: Write the failing vocabulary test.** Create
  `tests/CpuEmulator.Tests/Generators/Z80EdBlockVocabularyTests.cs`:

```csharp
using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdBlockVocabularyTests
{
    [Fact]
    public void EdBlock_factory_carries_its_mnemonic()
    {
        Assert.Equal("LDIR", ((EdBlockOp)EdBlock("LDIR")).Mnemonic);
        Assert.Equal("CPD", ((EdBlockOp)EdBlock("CPD")).Mnemonic);
        Assert.Equal("OTDR", ((EdBlockOp)EdBlock("OTDR")).Mnemonic);
    }

    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edblk")]
        public static class EdblkSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
                new("DE", 16, HighHalf: "D", LowHalf: "E"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0xA0, "LDI",  AddrMode.Implied, [EdBlock("LDI")]),
                Insn(0xED, 0xB0, "LDIR", AddrMode.Implied, [EdBlock("LDIR")]),
            ];
        }
        """;

    [Fact]
    public void EdBlock_rows_classify_and_compile()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("private void OpEDA0()", result.GeneratedText);
        Assert.Contains("private void OpEDB0()", result.GeneratedText);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockVocabularyTests"`
  Expected: FAIL — `EdBlock`/`EdBlockOp` do not exist.

- [ ] **Step 3: Add the `Op` record.** In `src/CpuEmulator.Core/Specification/Op.cs`, after the M3.4c ED
  records (the `EdNopOp` line), add:

```csharp

// ── M3.4d ED block ops (0xA0–0xBB; additive) ──
// The 16 block ops LDI/LDD/LDIR/LDDR, CPI/CPD/CPIR/CPDR, INI/IND/INIR/INDR, OUTI/OUTD/OTIR/OTDR.
// Mnemonic is the discriminator the emit arm switches on. All are mode Implied; all combine a
// memory/port transfer + BC/HL adjust + (for the *R repeating forms) a conditional PC-rewind.
public sealed record EdBlockOp(string Mnemonic) : Op;
```

- [ ] **Step 4: Add the `Spec` factory.** In `src/CpuEmulator.Core/Specification/Spec.cs`, after the M3.4c
  ED factories (the `EdNop` line), add:

```csharp
    // ── M3.4d ED block ops (additive) ──
    public static Op EdBlock(string mnemonic) => new EdBlockOp(mnemonic);
```

- [ ] **Step 5: Add the parser `s_microOpSignatures`.** In `src/CpuEmulator.Generators/SpecParser.cs`, in
  `s_microOpSignatures`, after the M3.4c ED entries (`EdNop`), add:

```csharp
        // M3.4d: the ED block ops.
        ["EdBlock"]    = new[] { ArgKind.Str },                 // EdBlock("LDIR")
```

- [ ] **Step 6: Add the `InstructionClass` member.** In `src/CpuEmulator.Generators/SpecModel.cs`, in the
  `InstructionClass` enum, after `Z80EdOp,` (M3.4c), add:

```csharp
    Z80EdBlock,   // M3.4d: ED block ops (LDI/LDD/LDIR/LDDR/CPI/.../OTDR) — memory/port transfer + repeat
```

- [ ] **Step 7: Add the op-kind set + `ClassifyOps` + `ValidateModeForClass` + status-touch.** In
  `src/CpuEmulator.Generators/SpecParser.cs`:
  - After `s_z80EdOpKinds` (M3.4c), add:

```csharp
    // ── M3.4d ED block-op kind set (additive) ──
    private static readonly HashSet<string> s_z80EdBlockOpKinds = new(System.StringComparer.Ordinal)
    {
        "EdBlock",
    };
```
  - In `ClassifyOps`, after the `s_z80EdOpKinds` arm, add:

```csharp
        if (s_z80EdBlockOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 ED block class must contain exactly one op"; return null; }
            return InstructionClass.Z80EdBlock;
        }
```
  - In `ValidateModeForClass`, after the `InstructionClass.Z80EdOp =>` arm, add:

```csharp
            // M3.4d: the block ops are all Implied (operands are the implicit BC/DE/HL/A/C registers).
            InstructionClass.Z80EdBlock =>
                mode == "Implied" ? null : "Z80 ED block class requires Implied mode",
```
  - In the status-touch predicate, add `Z80EdBlock` to the `is … or …` chain (every block op writes F):

```csharp
                or InstructionClass.Z80EdIo or InstructionClass.Z80EdOp
                or InstructionClass.Z80EdBlock
```

- [ ] **Step 8: Add the importer `FactoryArity`.** In `tools/CpuEmulator.SpecImporter/SemanticsMap.cs`, in
  `FactoryArity`, after the M3.4c ED entries (`EdNop`), add:

```csharp
        // M3.4d: the ED block ops.
        ["EdBlock"]     = 1,
```

  > `AllowedArgPattern` already accepts `"\w+"` (the mnemonic strings LDI/.../OTDR are bare words) — no
  > widening needed.

- [ ] **Step 9: Add the dispatch arm + STUB body + the predicates.** In
  `src/CpuEmulator.Generators/CpuEmitter.cs`:
  - In `EmitOpcodeMethod`'s `switch (opClass)`, after the `case InstructionClass.Z80EdOp:` block, add:

```csharp
            case InstructionClass.Z80EdBlock:
                EmitZ80EdBlockBody(sb, instruction, pc, pcType, statusReg, flags);
                break;
```
  - Add the STUB method (filled in Tasks 2–4) near `EmitZ80EdOpBody`:

```csharp
    private static void EmitZ80EdBlockBody(
        StringBuilder sb, InstructionModel insn, string pc, string pcType, string? statusReg,
        FlagBitMap flags)
    {
        sb.AppendLine("        _ = 0;   // TODO M3.4d Tasks 2-4 (LD/CP/IN/OUT block ops)");
    }
```
  - Extend the `isZ80` interpreter predicate with `Z80EdBlock`:

```csharp
            or InstructionClass.Z80EdIo or InstructionClass.Z80EdOp
            or InstructionClass.Z80EdBlock;
```
  - In `Z80Cycles`, add a placeholder arm BEFORE the final `_ => throw` (real cycles in Tasks 2–4):

```csharp
        (InstructionClass.Z80EdBlock, _, _) => 16,   // placeholder — base/final cycles; +5 on repeat in body
```
  - Add `Z80EdBlock` to the JIT predicates (`JitBaseCycles`, `ClassifyForJit`'s `z80` bool + the `jitClass`
    "Register"/fallback arm) exactly as M3.4c did for `Z80EdIo`/`Z80EdOp`. In `JitOpLiteral`, add
    `case "EdBlock": break;` (JIT fallback — the block ops are NOT emitted as IL in M3.4d) before
    `default:`.

  > **Why the stub:** Task 1 proves CLASSIFICATION + the descriptor table stays well-formed. The real
  > `Z80Spec.cs` is NOT regenerated until Task 6, so `Z80Cpu` still compiles from the M3.4c spec where no
  > `EdBlock` row exists — the stub arm is dormant until Task 6. `Z80WritesFlags` does NOT yet need the
  > `Z80EdBlock` arm (the stub writes nothing; the Q epilogue falls to `Q=0`, harmless until the real body).

- [ ] **Step 10: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockVocabularyTests"`
  Expected: PASS.

- [ ] **Step 11: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical — no
  `Z80EdBlock` in the 6502 spec).

- [ ] **Step 12: Commit.**

```bash
git add src/CpuEmulator.Core/Specification/Op.cs src/CpuEmulator.Core/Specification/Spec.cs \
        src/CpuEmulator.Generators/SpecModel.cs src/CpuEmulator.Generators/SpecParser.cs \
        src/CpuEmulator.Generators/CpuEmitter.cs tools/CpuEmulator.SpecImporter/SemanticsMap.cs \
        tests/CpuEmulator.Tests/Generators/Z80EdBlockVocabularyTests.cs
git commit -m "$(cat <<'EOF'
feat(generators): ED block-op vocabulary + Z80EdBlock instruction class (classification only)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 2: LDI/LDD/LDIR/LDDR — the block-transfer arm + repeat + the A+n X/Y quirk (TDD)

> Implement the LD arm of `EmitZ80EdBlockBody`: `(DE) ← (HL)`; HL±1; DE±1; BC−1. Flags H=0, N=0,
> P/V=(BC−1≠0), S/Z/C preserved, X/Y from `(A + transferredByte)` (bit1→Y/F5, bit3→X/F3). WZ unchanged.
> The repeating forms (LDIR/LDDR): if BC−1≠0, PC−=2 (rewind), WZ=instruction-PC+1, +5 cycles.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitZ80EdBlockBody` LD arm + the repeat helper +
  `Z80WritesFlags` EdBlock + `Z80Cycles` EdBlock)
- Modify: `tools/CpuEmulator.SpecImporter/Z80EdSemantics.cs` (add the block-op arm so 0xA0–0xBB emit
  `[EdBlock("<MNEMONIC>")]` — write ALL 16 now so subsequent tasks only TEST against it)
- Test: `tests/CpuEmulator.Tests/Generators/Z80EdBlockLoadTests.cs` (create)

- [ ] **Step 1: Extend `Z80EdSemantics` with the block-op arm (the complete mapping).** In
  `tools/CpuEmulator.SpecImporter/Z80EdSemantics.cs`, REPLACE the early `null`-return guard so 0xA0–0xBB
  map to their `EdBlock` rows. Add a block table + arm at the TOP of `OpsFor`:

```csharp
    // M3.4d: the 16 ED block ops, keyed by opcode. Outside this set + the 0x40–0x7F core, OpsFor returns
    // null (the block ops are 0xA0–0xBB; everything else in the ED plane is still out of scope).
    private static readonly System.Collections.Generic.Dictionary<int, string> Block = new()
    {
        [0xA0] = "LDI",  [0xA1] = "CPI",  [0xA2] = "INI",  [0xA3] = "OUTI",
        [0xA8] = "LDD",  [0xA9] = "CPD",  [0xAA] = "IND",  [0xAB] = "OUTD",
        [0xB0] = "LDIR", [0xB1] = "CPIR", [0xB2] = "INIR", [0xB3] = "OTIR",
        [0xB8] = "LDDR", [0xB9] = "CPDR", [0xBA] = "INDR", [0xBB] = "OTDR",
    };
```
  and at the start of `OpsFor`, BEFORE the `if (opcode is < 0x40 or > 0x7F) return null;` guard, add:

```csharp
        if (Block.TryGetValue(opcode, out var blk)) return $"[EdBlock(\"{blk}\")]";
```

  > The existing `< 0x40 or > 0x7F` guard still returns null for the rest of the ED plane (e.g. the
  > yet-unimplemented 0x80–0x9F / 0xBC–0xFF), so DD/FD-adjacent ED rows stay `// TODO`. Only the 16 block
  > opcodes newly resolve.

- [ ] **Step 2: Write the failing test.** Create `tests/CpuEmulator.Tests/Generators/Z80EdBlockLoadTests.cs`
  (a synthetic ED CPU exposing LDI (0xA0), LDD (0xA8), LDIR (0xB0)). Mirror the M3.4c synthetic-fixture
  shape (deviation #1: `IAddressSpace _bus`, `public byte Q;`, `public int Im;`). Pin the exact flag byte
  from `ed a0.json` or compute by hand. Assert: `(DE)` got `(HL)`'s byte; HL+1; DE+1; BC−1; H=0; N=0;
  P/V=(BC−1≠0); WZ unchanged; X/Y from `(A + transferredByte)`. For LDIR with BC−1≠0: PC rewound
  (final.pc == instruction.pc); for LDIR with BC==1: PC advanced.

```csharp
using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdBlockLoadTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edbl")]
        public static class EdblSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
                new("DE", 16, HighHalf: "D", LowHalf: "E"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0xA0, "LDI",  AddrMode.Implied, [EdBlock("LDI")]),
                Insn(0xED, 0xA8, "LDD",  AddrMode.Implied, [EdBlock("LDD")]),
                Insn(0xED, 0xB0, "LDIR", AddrMode.Implied, [EdBlock("LDIR")]),
            ];
        }

        public sealed partial class EdblCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public EdblCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) => _bus.Read8(a);
            private void WriteBus(uint a, byte v) => _bus.Write8(a, v);
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Bus) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdblCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t, bus);
    }
    private static void Set(object cpu, Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;

    [Fact]
    public void LDI_transfers_byte_adjusts_pointers_sets_flags_WZ_unchanged()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA0);     // LDI
        bus.Write8(0x4000, 0x37);                     // (HL) source byte
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "DE", 0x5000);
        Set(cpu, t, "BC", 0x0003); Set(cpu, t, "A", 0x01); Set(cpu, t, "WZ", 0xABCD);
        Set(cpu, t, "F", 0xFF);                        // S/Z/C should survive; H/N/PV recomputed
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x37, bus.Read8(0x5000));         // (DE) <- (HL)
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "HL"));// HL+1
        Assert.Equal(0x5001u, (uint)Get(cpu, t, "DE"));// DE+1
        Assert.Equal(0x0002u, (uint)Get(cpu, t, "BC"));// BC-1
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f & 0x10);                  // H = 0
        Assert.Equal(0x00, f & 0x02);                  // N = 0
        Assert.Equal(0x04, f & 0x04);                  // P/V = (BC-1 != 0) = 1 (0x02 remaining)
        Assert.Equal(0x80, f & 0x80);                  // S preserved (was set)
        Assert.Equal(0x01, f & 0x01);                  // C preserved
        // X/Y from (A + transferredByte) = (1 + 0x37) = 0x38: bit3(X)=1, bit5(Y)=... use bit1 anomaly:
        // n=0x38 -> bit1=0 -> F5(Y)=0; bit3=1 -> F3(X)=1.  Re-derive against ed a0.json and pin exactly.
        Assert.Equal(0x08, f & 0x08);                  // X (F3) = bit3 of (A+n)
        Assert.Equal(0xABCDu, (uint)Get(cpu, t, "WZ"));// WZ UNCHANGED
    }

    [Fact]
    public void LDIR_rewinds_PC_when_BC_not_exhausted()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0x100, 0xED); bus.Write8(0x101, 0xB0);   // LDIR at 0x100
        bus.Write8(0x4000, 0x99);
        Set(cpu, t, "PC", 0x100); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "DE", 0x5000);
        Set(cpu, t, "BC", 0x0003);                          // BC-1 = 2 != 0 -> repeat
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x100u, (uint)Get(cpu, t, "PC"));      // PC rewound to the instruction
        Assert.Equal(0x101u, (uint)Get(cpu, t, "WZ"));      // WZ = instruction-PC + 1
    }

    [Fact]
    public void LDIR_advances_PC_on_final_iteration()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0x100, 0xED); bus.Write8(0x101, 0xB0);
        bus.Write8(0x4000, 0x99);
        Set(cpu, t, "PC", 0x100); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "DE", 0x5000);
        Set(cpu, t, "BC", 0x0001);                          // BC-1 = 0 -> final, no repeat
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x102u, (uint)Get(cpu, t, "PC"));      // PC advanced past the 2-byte instruction
    }
}
```

- [ ] **Step 3: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockLoadTests"`
  Expected: FAIL — `EmitZ80EdBlockBody`'s stub does nothing.

- [ ] **Step 4: Implement the LD arm of `EmitZ80EdBlockBody`.** REPLACE the stub. Make the body a
  `switch (mnemonic)` dispatcher; this task fills the four LD mnemonics. Use the `flags.BitOf(...)` mask
  idiom (read `EmitZ80EdIoBody`). The shared repeat tail is factored into a local helper string.

```csharp
    private static void EmitZ80EdBlockBody(
        StringBuilder sb, InstructionModel insn, string pc, string pcType, string? statusReg,
        FlagBitMap flags)
    {
        string f = statusReg ?? "F";
        string mn = Unquote(insn.Ops[0].Args[0]);   // "LDI".."OTDR"
        // Flag masks (computed once).
        string sM = $"0x{(byte)(1 << flags.BitOf("S")):X2}";
        string zM = $"0x{(byte)(1 << flags.BitOf("Z")):X2}";
        string yM = $"0x{(byte)(1 << flags.BitOf("Y")):X2}";
        string hM = $"0x{(byte)(1 << flags.BitOf("H")):X2}";
        string xM = $"0x{(byte)(1 << flags.BitOf("X")):X2}";
        string pM = $"0x{(byte)(1 << flags.BitOf("P")):X2}";
        string nM = $"0x{(byte)(1 << flags.BitOf("N")):X2}";
        string cM = $"0x{(byte)(1 << flags.BitOf("C")):X2}";

        switch (mn)
        {
            case "LDI":  case "LDD":  case "LDIR": case "LDDR":
            {
                bool inc = mn is "LDI" or "LDIR";
                bool repeat = mn is "LDIR" or "LDDR";
                string delta = inc ? "+ 1" : "- 1";
                sb.AppendLine("        byte __n = ReadBus(HL);");
                sb.AppendLine("        WriteBus(DE, __n);");
                sb.AppendLine($"        HL = unchecked((ushort)(HL {delta}));");
                sb.AppendLine($"        DE = unchecked((ushort)(DE {delta}));");
                sb.AppendLine("        BC = unchecked((ushort)(BC - 1));");
                // n_xy = A + transferred byte (the LD-family X/Y quirk).
                sb.AppendLine("        int __xy = (A + __n) & 0xFF;");
                // H=0, N=0; P/V = (BC != 0); S/Z/C preserved; X = bit3 of __xy, Y = bit1 of __xy.
                sb.AppendLine($"        {f} = unchecked((byte)(({f} & ({sM} | {zM} | {cM}))");
                sb.AppendLine($"            | (BC != 0 ? {pM} : 0x00)");
                sb.AppendLine($"            | ((__xy & 0x08) != 0 ? {xM} : 0x00)");
                sb.AppendLine($"            | ((__xy & 0x02) != 0 ? {yM} : 0x00)));");
                EmitBlockRepeatTail(sb, repeat, f);   // WZ unchanged for non-repeat; repeat sets WZ=PC+1
                break;
            }
            // CP / IN / OUT arms are added in Tasks 3-4.
            default:
                sb.AppendLine("        _ = 0;   // TODO Tasks 3-4 (CP/IN/OUT block ops)");
                break;
        }
    }

    /// <summary>M3.4d: the shared repeat tail. For a repeating *R op, when the loop is NOT done (BC != 0
    /// for LD/CP; B != 0 for IN/OUT — the caller has already computed the right condition into __more),
    /// rewind PC by the 2 key bytes and set WZ = instruction-PC + 1 (= the original PC, which Step has
    /// already advanced by 2 — so the rewound PC + 1). Non-repeating ops leave PC as Step set it.</summary>
    private static void EmitBlockRepeatTail(StringBuilder sb, bool repeat, string f)
    {
        if (!repeat)
            return;   // single ops: PC advances (Step did it); WZ rule is per-family (LD: unchanged).
        // For LD/CP the repeat condition is "BC != 0"; CP also requires "not matched" — Task 3 passes a
        // family-specific __more. Here (LD) the condition is BC != 0.
        sb.AppendLine("        if (BC != 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            PC = unchecked((ushort)(PC - 2));");          // rewind to re-execute
        sb.AppendLine("            WZ = unchecked((ushort)(PC + 1));");          // MEMPTR = instr PC + 1
        sb.AppendLine("            _cycles += 5;");                              // 21 - 16 = +5 on repeat
        sb.AppendLine("        }");
    }
```

  > **The repeat condition for LD is `BC != 0`** (after the decrement). CP adds an "AND not matched"
  > clause (Task 3); IN/OUT use `B != 0` (Task 4). The helper above hard-codes the LD condition; Tasks 3–4
  > either generalize `EmitBlockRepeatTail` to take a condition string or inline the tail per family.
  > **Decision:** generalize `EmitBlockRepeatTail(sb, repeat, f, condition)` — Task 3 changes the signature
  > to accept the condition (`"BC != 0"`, `"BC != 0 && (F & zM) == 0"`, `"B != 0"`). Apply the LD call as
  > `EmitBlockRepeatTail(sb, repeat, f, "BC != 0");` now and refactor the signature in Task 3.

- [ ] **Step 5: Update `Z80WritesFlags` + `Z80Cycles`.** In `Z80WritesFlags`, add:

```csharp
            InstructionClass.Z80EdBlock => true,   // every block op writes F
```
  In `Z80Cycles`, keep the placeholder `(InstructionClass.Z80EdBlock, _, _) => 16,` (16 is the base/final
  count; the +5 repeat is in the body). Confirm the LD ops' internal-cycle padding against the cycles array
  (the dataset's 16 = 2 fetch + 1 read (HL) + 1 write (DE) + 12 internal? re-derive from `ed a0.json`'s
  cycles length; add the residual `_cycles += <N>` if the bus-reads alone do not total 16).

- [ ] **Step 6: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockLoadTests"`
  Expected: PASS.

- [ ] **Step 7: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 8: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs tools/CpuEmulator.SpecImporter/Z80EdSemantics.cs \
        tests/CpuEmulator.Tests/Generators/Z80EdBlockLoadTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): ED LDI/LDD/LDIR/LDDR — block transfer + repeat PC-rewind + A+n X/Y quirk

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 3: CPI/CPD/CPIR/CPDR — the block-compare arm + the match-or-exhaust repeat (TDD)

> Implement the CP arm: compare `A − (HL)` (result NOT stored); HL±1; BC−1. Flags S/Z/H from the compare;
> N=1; P/V=(BC−1≠0); C preserved; X/Y from `(A − (HL) − H)`. **WZ = WZ+1 (CPI/CPIR) / WZ−1 (CPD/CPDR)**.
> The repeating forms repeat while `BC≠0 AND Z==0` (not yet matched); on repeat PC−=2, WZ=instruction-PC+1.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (the CP arm of `EmitZ80EdBlockBody`; generalize
  `EmitBlockRepeatTail` to take a condition string)
- Test: `tests/CpuEmulator.Tests/Generators/Z80EdBlockCpTests.cs` (create)

- [ ] **Step 1: Generalize the repeat tail.** Change `EmitBlockRepeatTail`'s signature to accept the repeat
  condition (the LD call from Task 2 becomes `EmitBlockRepeatTail(sb, repeat, f, "BC != 0");`):

```csharp
    private static void EmitBlockRepeatTail(StringBuilder sb, bool repeat, string f, string condition)
    {
        if (!repeat)
            return;
        sb.AppendLine($"        if ({condition})");
        sb.AppendLine("        {");
        sb.AppendLine("            PC = unchecked((ushort)(PC - 2));");
        sb.AppendLine("            WZ = unchecked((ushort)(PC + 1));");
        sb.AppendLine("            _cycles += 5;");
        sb.AppendLine("        }");
    }
```

- [ ] **Step 2: Write the failing test.** Create `tests/CpuEmulator.Tests/Generators/Z80EdBlockCpTests.cs`
  (synthetic ED CPU exposing CPI (0xA1), CPD (0xA9), CPIR (0xB1)). Mirror the Task 2 fixture. Pin from
  `ed a1.json`. Assert: HL+1 (CPI); BC−1; N=1; Z set when A==(HL); the H/X/Y; C preserved; **WZ = WZ+1**.
  For CPIR with BC−1≠0 AND no match: PC rewound; with a match (A==(HL)): PC advanced even if BC≠0.

```csharp
    [Fact]
    public void CPI_compares_sets_N_HL_inc_BC_dec_WZ_plus1()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA1);     // CPI
        bus.Write8(0x6000, 0x20);                     // (HL)
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x6000); Set(cpu, t, "BC", 0x0005);
        Set(cpu, t, "A", 0x20); Set(cpu, t, "WZ", 0x1000); Set(cpu, t, "F", 0x01);  // C preserved
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x6001u, (uint)Get(cpu, t, "HL"));
        Assert.Equal(0x0004u, (uint)Get(cpu, t, "BC"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x40, f & 0x40);                 // Z = 1 (A == (HL))
        Assert.Equal(0x02, f & 0x02);                 // N = 1
        Assert.Equal(0x04, f & 0x04);                 // P/V = (BC-1 != 0)
        Assert.Equal(0x01, f & 0x01);                 // C preserved
        Assert.Equal(0x1001u, (uint)Get(cpu, t, "WZ"));// WZ + 1
    }
```

- [ ] **Step 3: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockCpTests"`
  Expected: FAIL — the CP arm is the Task 2 `default` stub.

- [ ] **Step 4: Implement the CP arm.** In `EmitZ80EdBlockBody`'s `switch (mn)`, REPLACE the `default` stub
  with the CP case (the IN/OUT arm follows in Task 4):

```csharp
            case "CPI":  case "CPD":  case "CPIR": case "CPDR":
            {
                bool inc = mn is "CPI" or "CPIR";
                bool repeat = mn is "CPIR" or "CPDR";
                string delta = inc ? "+ 1" : "- 1";
                sb.AppendLine("        byte __m = ReadBus(HL);");
                sb.AppendLine("        int __r = (A - __m) & 0xFF;");
                sb.AppendLine("        int __hc = ((A & 0x0F) - (__m & 0x0F)) < 0 ? 1 : 0;");  // half-borrow
                sb.AppendLine($"        HL = unchecked((ushort)(HL {delta}));");
                sb.AppendLine("        BC = unchecked((ushort)(BC - 1));");
                sb.AppendLine($"        WZ = unchecked((ushort)(WZ {(inc ? "+ 1" : "- 1")}));");
                // n_xy = (A - (HL) - H) for the X/Y quirk.
                sb.AppendLine("        int __xy = (__r - __hc) & 0xFF;");
                // S/Z from __r; H from __hc; N=1; P/V=(BC!=0); C preserved; X=bit3, Y=bit1 of __xy.
                sb.AppendLine($"        {f} = unchecked((byte)(({f} & {cM})");
                sb.AppendLine($"            | ((__r & 0x80) != 0 ? {sM} : 0x00)");
                sb.AppendLine($"            | (__r == 0 ? {zM} : 0x00)");
                sb.AppendLine($"            | (__hc != 0 ? {hM} : 0x00)");
                sb.AppendLine($"            | {nM}");
                sb.AppendLine($"            | (BC != 0 ? {pM} : 0x00)");
                sb.AppendLine($"            | ((__xy & 0x08) != 0 ? {xM} : 0x00)");
                sb.AppendLine($"            | ((__xy & 0x02) != 0 ? {yM} : 0x00)));");
                // Repeat while BC != 0 AND not matched (Z == 0).
                EmitBlockRepeatTail(sb, repeat, f, $"BC != 0 && (__r != 0)");
                break;
            }
            default:
                sb.AppendLine("        _ = 0;   // TODO Task 4 (IN/OUT block ops)");
                break;
```

  > **WZ on CP repeat.** The repeat tail sets `WZ = PC+1` (instruction PC). But the body ALSO did `WZ ±= 1`
  > BEFORE the tail. The vector is the oracle: confirm whether a REPEATING CPIR (BC≠0, no match) ends with
  > WZ = instruction-PC+1 (the rewind value) or WZ ±1. The tail runs AFTER the body's `WZ ±= 1`, so the
  > rewind value wins on repeat — CONFIRM against `ed b1.json` (a repeating CPIR case). If the vector shows
  > WZ ±1 even on repeat, MOVE the body's `WZ ±= 1` to AFTER the tail / guard it. Re-derive at Step 6.

- [ ] **Step 5: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockCpTests"`
  Expected: PASS.

- [ ] **Step 6: Full gate.** As Task 2 Step 7.

- [ ] **Step 7: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/Z80EdBlockCpTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): ED CPI/CPD/CPIR/CPDR — block compare + match-or-exhaust repeat + WZ +/-1

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 4: INI/IND/INIR/INDR + OUTI/OUTD/OTIR/OTDR — the block-port arm + the H/C/P-V `k` quirk (TDD)

> Implement the IN and OUT arms: IN reads port (BC) → (HL), OUT writes (HL) → port (BC); B−1 (the counter);
> HL±1. Flags S/Z/X/Y from B−1, N = bit7 of the transferred byte, and the messy H/C/P-V from `k` (F5).
> WZ = BC(after B−1) ± 1. Repeating forms repeat while `B≠0`; on repeat PC−=2, WZ=instruction-PC+1.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (the IN + OUT arms of `EmitZ80EdBlockBody`)
- Test: `tests/CpuEmulator.Tests/Generators/Z80EdBlockIoTests.cs` (create — needs the `ReadIo`/`WriteIo`
  seam, like the M3.4c `Z80EdIoTests`)

- [ ] **Step 1: Write the failing test.** Create `tests/CpuEmulator.Tests/Generators/Z80EdBlockIoTests.cs`
  (synthetic ED CPU with the I/O seam — add `_io` + `ReadIo`/`WriteIo` to the partial, like M3.4c
  `Z80EdIoTests`; expose INI (0xA2), IND (0xAA), OUTI (0xA3), INIR (0xB2)). Pin from `ed a2.json`/
  `ed a3.json`. Assert: INI reads port (BC) → (HL); B−1; HL+1; the port appears in the bus (`_io` read);
  **WZ = (BC after B−1) + 1**; the H/C/P-V `k` quirk; N = bit7 of the input byte. OUTI writes (HL) → port;
  the `k = outByte + L_after` quirk.

```csharp
    [Fact]
    public void INI_reads_port_to_HL_decrements_B_sets_WZ_BCplus1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA2);     // INI
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x7000);
        Set(cpu, t, "BC", 0x0562);                    // B=0x05, C=0x62
        io.Write8(0x0562, 0x5B);                      // port (BC) byte
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5B, bus.Read8(0x7000));        // (HL) <- IN (C)
        Assert.Equal(0x0462u, (uint)Get(cpu, t, "BC"));// B-1
        Assert.Equal(0x7001u, (uint)Get(cpu, t, "HL"));// HL+1
        // WZ = (BC after B--) + 1 = 0x0462 + 1 = 0x0463.
        Assert.Equal(0x0463u, (uint)Get(cpu, t, "WZ"));
    }
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockIoTests"`
  Expected: FAIL — the IN/OUT arm is the Task 3 `default` stub.

- [ ] **Step 3: Implement the IN + OUT arms.** In `EmitZ80EdBlockBody`'s `switch (mn)`, REPLACE the
  `default` stub with the IN and OUT cases:

```csharp
            case "INI":  case "IND":  case "INIR": case "INDR":
            {
                bool inc = mn is "INI" or "INIR";
                bool repeat = mn is "INIR" or "INDR";
                string hlDelta = inc ? "+ 1" : "- 1";
                sb.AppendLine("        byte __v = ReadIo(BC);");           // port read (BC)
                sb.AppendLine("        WriteBus(HL, __v);");               // (HL) <- input
                sb.AppendLine("        B = unchecked((byte)(B - 1));");    // the counter
                sb.AppendLine($"        HL = unchecked((ushort)(HL {hlDelta}));");
                // WZ = (BC after B--) +/- 1 : INI/INIR -> +1 ; IND/INDR -> -1.
                sb.AppendLine($"        WZ = unchecked((ushort)(BC {(inc ? "+ 1" : "- 1")}));");
                // k = input + ((C +/- 1) & 0xFF) ; INI/INIR uses C+1, IND/INDR uses C-1.
                sb.AppendLine($"        int __k = __v + ((C {(inc ? "+ 1" : "- 1")}) & 0xFF);");
                EmitBlockIoFlags(sb, f, sM, zM, yM, hM, xM, pM, nM, cM, inputByte: "__v");
                EmitBlockRepeatTail(sb, repeat, f, "B != 0");
                break;
            }
            case "OUTI": case "OUTD": case "OTIR": case "OTDR":
            {
                bool inc = mn is "OUTI" or "OTIR";
                bool repeat = mn is "OTIR" or "OTDR";
                string hlDelta = inc ? "+ 1" : "- 1";
                sb.AppendLine("        byte __v = ReadBus(HL);");          // (HL) -> output
                sb.AppendLine("        B = unchecked((byte)(B - 1));");    // OUT decrements B BEFORE WZ
                sb.AppendLine("        WriteIo(BC, __v);");                // port write (BC)
                sb.AppendLine($"        HL = unchecked((ushort)(HL {hlDelta}));");
                sb.AppendLine($"        WZ = unchecked((ushort)(BC {(inc ? "+ 1" : "- 1")}));");
                // k = output + L (L AFTER the HL adjust).
                sb.AppendLine("        int __k = __v + L;");
                EmitBlockIoFlags(sb, f, sM, zM, yM, hM, xM, pM, nM, cM, inputByte: "__v");
                EmitBlockRepeatTail(sb, repeat, f, "B != 0");
                break;
            }
            default:
                throw new System.InvalidOperationException($"unknown ED block op '{mn}'");
```

  Add the shared IN/OUT flag helper near `EmitZ80EdBlockBody`:

```csharp
    /// <summary>M3.4d: the IN/OUT block-op flag word. S/Z/X/Y from the DECREMENTED B; N = bit7 of the
    /// transferred byte; H = C = (k &gt; 0xFF); P/V = parity of ((k &amp; 7) ^ B). `__k` is in scope
    /// (the caller computed it per family); B is already decremented.</summary>
    private static void EmitBlockIoFlags(
        StringBuilder sb, string f, string sM, string zM, string yM, string hM, string xM,
        string pM, string nM, string cM, string inputByte)
    {
        sb.AppendLine($"        {f} = unchecked((byte)(");
        sb.AppendLine($"              ((B & 0x80) != 0 ? {sM} : 0x00)");
        sb.AppendLine($"            | (B == 0 ? {zM} : 0x00)");
        sb.AppendLine($"            | ((B & 0x20) != 0 ? {yM} : 0x00)");
        sb.AppendLine($"            | ((B & 0x08) != 0 ? {xM} : 0x00)");
        sb.AppendLine($"            | (({inputByte} & 0x80) != 0 ? {nM} : 0x00)");
        sb.AppendLine($"            | (__k > 0xFF ? ({hM} | {cM}) : 0x00)");
        sb.AppendLine($"            | ((System.Numerics.BitOperations.PopCount((uint)(((__k & 7) ^ B))) & 1) == 0 ? {pM} : 0x00)));");
    }
```

  > **The `k` operand is the trap.** INI uses `C+1`, IND uses `C−1`; OUTI/OUTD use `L` (AFTER the HL
  > adjust). The OUT family decrements B BEFORE the port write (the documented order). Re-derive EACH `k`
  > and the WZ ± direction from the vectors (`ed a2`/`ed aa`/`ed a3`/`ed ab`) at Step 5 — do NOT trust the
  > prose. The P/V-parity-of-`((k&7)^B)` is the messiest single Z80 flag rule; the bus-trace UAT is the gate.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockIoTests"`
  Expected: PASS.

- [ ] **Step 5: Full gate.** As Task 2 Step 7.

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/Z80EdBlockIoTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): ED INI/IND/OUTI/OUTD (+ repeats) — block port I/O + the H/C/P-V k quirk + WZ=BC+/-1

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~4.

---

### Task 5: The importer semantics test (the 16 block-op rows resolve) (TDD)

> Prove `Z80EdSemantics.OpsFor` returns the right `[EdBlock("…")]` for each of the 16 block opcodes and
> still returns `null` for non-block ED opcodes outside the core.

**Files:**
- Test: `tests/CpuEmulator.Tests/Importer/Z80EdBlockSemanticsTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Importer/Z80EdBlockSemanticsTests.cs`:

```csharp
using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80EdBlockSemanticsTests
{
    [Theory]
    [InlineData(0xA0, "LDI")]  [InlineData(0xA1, "CPI")]  [InlineData(0xA2, "INI")]  [InlineData(0xA3, "OUTI")]
    [InlineData(0xA8, "LDD")]  [InlineData(0xA9, "CPD")]  [InlineData(0xAA, "IND")]  [InlineData(0xAB, "OUTD")]
    [InlineData(0xB0, "LDIR")] [InlineData(0xB1, "CPIR")] [InlineData(0xB2, "INIR")] [InlineData(0xB3, "OTIR")]
    [InlineData(0xB8, "LDDR")] [InlineData(0xB9, "CPDR")] [InlineData(0xBA, "INDR")] [InlineData(0xBB, "OTDR")]
    public void Block_opcodes_map_to_EdBlock(int opcode, string mnemonic)
    {
        Assert.Equal($"[EdBlock(\"{mnemonic}\")]", Z80EdSemantics.OpsFor(opcode));
    }

    [Theory]
    [InlineData(0x80)]   // ED plane but not core, not block
    [InlineData(0x9F)]
    [InlineData(0xBC)]   // just past the block range
    [InlineData(0xFF)]
    public void NonBlock_nonCore_ED_returns_null(int opcode)
    {
        Assert.Null(Z80EdSemantics.OpsFor(opcode));
    }
}
```

- [ ] **Step 2: Run the test.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EdBlockSemanticsTests"`
  Expected: PASS (the block arm was added in Task 2 Step 1; the `< 0x40 or > 0x7F` guard still nulls the
  non-block range). If a non-block opcode in 0x80–0x9F unexpectedly resolves, the guard logic is wrong —
  fix so ONLY the 16 block opcodes + the 0x40–0x7F core return non-null.

- [ ] **Step 3: Full gate.** As Task 2 Step 7.

- [ ] **Step 4: Commit.**

```bash
git add tests/CpuEmulator.Tests/Importer/Z80EdBlockSemanticsTests.cs
git commit -m "$(cat <<'EOF'
test(z80): Z80EdSemantics block-op mapping (16 opcodes -> EdBlock; non-block ED still null)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~20 (the parameterized cases).

---

### Task 6: Regenerate `Z80Spec.cs` — the 16 block-op rows go live (gate)

> Regenerate the spec so the 16 block-op rows carry `[EdBlock("…")]` (they were `[]`-routed before). No
> dataset change (F1: the rows exist). This is the atomic flip from `// TODO`-empty to live block ops.

**Files:**
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Spec.cs` (regenerated)

- [ ] **Step 1: Regenerate.**
  Run:
```bash
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset tools/CpuEmulator.SpecImporter/data/z80-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/z80-semantics.json \
  --out src/CpuEmulator.Cpus.Z80/Z80Spec.cs
```
  Expected: the 16 ED rows at 0xA0–0xBB change from `Insn(0xED, 0xA0, "LDI", AddrMode.Implied, [])` to
  `Insn(0xED, 0xA0, "LDI", AddrMode.Implied, [EdBlock("LDI")])` (etc.). NO other row changes. The emitted
  count rises by 16 (the 16 rows now emit non-empty). 6502 spec untouched.

- [ ] **Step 2: Build + confirm the spec diff is exactly the 16 rows.**
  Run: `git diff --stat src/CpuEmulator.Cpus.Z80/Z80Spec.cs`
  Expected: ~16 lines changed (the block-op rows). Review the diff — each row gains `[EdBlock("<MN>")]`.
  Run: `dotnet build --no-incremental -warnaserror` → clean (the generated `Z80Cpu.g.cs` now has real
  `OpEDA0()`… bodies from `EmitZ80EdBlockBody`).

- [ ] **Step 3: Update any importer-count fixtures.** If `SpecFileEmitterTests.cs` pins the emitted count
  (M3.4c bumped it to 572), bump it by 16 (572 → 588) and update any per-mnemonic-map expectation for
  LDI…OTDR (`[]` → `[EdBlock("…")]`) the way M3.4c updated the NEG expectation. Run:
  `dotnet test --filter "FullyQualifiedName~SpecFileEmitterTests"` → green.

- [ ] **Step 4: Full gate (unit level — the TomHarte ED theory is widened in Task 7).**
  Run: `dotnet test` → all green (the synthetic block tests + the base/CB/ED-core unit tests).
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical).

- [ ] **Step 5: Commit (include the regenerated spec for review).**

```bash
git add src/CpuEmulator.Cpus.Z80/Z80Spec.cs
git commit -m "$(cat <<'EOF'
feat(z80): ED block ops live in spec — route 0xA0-0xBB through Z80EdSemantics (regen Z80Spec.cs)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~0 (regen; count-fixture updates only).

---

### Task 7: The TomHarte ED block-op gate — green + universal regression + closeout (TDD + exit criterion)

> Widen the ED-core theory's probe range to also cover 0xA0–0xBB, drive the full sweep green over the 16
> block opcodes (incl. i/r/im/iff1/iff2/wz/q + ports + the per-T-state trace + the repeat PC-rewind),
> confirm base+CB+ED-core stay green at the universal Q/WZ/IM bar, confirm the 6502 un-regressed, fill the
> closeout.

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs` (widen `CoveredEdPlaneOpcodes`)
- Iterative fixes to the Task 2–4 emit arms as vectors surface divergences.
- Modify: this plan (the closeout table).

- [ ] **Step 1: Widen the ED-core theory's probe range.** In
  `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs`, change `CoveredEdPlaneOpcodes` so it probes BOTH
  the core (0x40–0x7F) AND the block ops (0xA0–0xBB). The theory method `Ed_opcode_matches_TomHarte_vectors`
  already loads `ed {opcode:x2}.json` and uses the universal `RunCase` — no change there. Update the probe:

```csharp
    public static TheoryData<byte> CoveredEdPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        // ED core (M3.4c) + ED block ops (M3.4d). Probed via the prefixed key (0xED00 | op).
        for (int op = 0x40; op <= 0x7F; op++)
            if (Z80Cpu.Disassemble((uint)(0xED00 | op), 0, 0) != "???")
                data.Add((byte)op);
        for (int op = 0xA0; op <= 0xBB; op++)
            if (Z80Cpu.Disassemble((uint)(0xED00 | op), 0, 0) != "???")
                data.Add((byte)op);
        return data;
    }
```

  > Update the XML-doc on `CoveredEdPlaneOpcodes` to name the now-covered block range. The probe finds the
  > 16 block opcodes ONLY if their rows emit (Task 6); if it finds fewer than 64+16=80, a row failed to
  > emit — check the Task 6 regen diff.

- [ ] **Step 2: Run the STAGED gate over the block ops.**
  Run the registers-first stage:
```bash
CPUEMULATOR_Z80_REGS_ONLY=1 dotnet test --filter "FullyQualifiedName~Z80TomHarteTests.Ed_opcode"
```
  Expected: green (registers + RAM + ports + cycle COUNT, incl. the repeat PC-rewind in the final PC). Then
  the FULL trace:
```bash
dotnet test --filter "FullyQualifiedName~Z80TomHarteTests.Ed_opcode"
```
  Expected: green (200/opcode sample, full per-T-state bus trace).

- [ ] **Step 3: Iterate to green over divergences.** Apply `superpowers:systematic-debugging`. The likely
  surprises (flagged):
  - **The X/Y (F3/F5) quirk** — the LD family's `(A+n)` bit-1/bit-3 mapping; the CP family's `(A−(HL)−H)`;
    the IN/OUT family's S/Z/X/Y from the decremented B. A wrong bit (5 vs 1) surfaces as an F mismatch.
  - **The repeat PC-rewind** — final.pc must equal init.pc on a repeating case (BC≠0 / B≠0, no match) and
    init.pc+2 on the final iteration. A missing/extra rewind surfaces as a PC mismatch.
  - **The WZ rule per family** — LD unchanged; CP ±1; IN/OUT BC±1; on repeat, WZ=instruction-PC+1. Confirm
    the CP-repeat WZ ordering (Task 3 Step 4 watch-point).
  - **The IN/OUT `k` quirk** — the `C±1` (IN) vs `L` (OUT) operand; the P/V-parity-of-`((k&7)^B)`.
  - **The port array** — INI/IND read once; OUTI/OUTD write once; the `DiffPorts` check is exact (address,
    value, direction).
  - **Cycle counts** — non-repeat/final = 16, repeat = 21; the bus-trace internal-cycle padding.

- [ ] **Step 4: The FULL UAT sweep — the block-op exit criterion.**
  Run:
```bash
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarteTests.Ed_opcode"
```
  Expected: ALL ED opcodes (64 core + 16 block) × 1000 = **80,000 cases, 0 failures** (registers incl.
  F's X/Y, I/R, IM, IFF1/IFF2, WZ, Q, RAM, ports, per-T-state trace, the repeat PC-rewind). Record the
  exact covered count (probe == emitted == green; target 80 ED opcodes).

- [ ] **Step 5: Confirm the UNIVERSAL regression bar.**
  Run the WHOLE Z80 UAT (base + CB + ED-core + ED-block):
```bash
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
```
  Expected: base (≈252k) + CB (256k) + ED (80k) all **0 failures** with final Q/WZ/IM checked on every
  case. Then the 6502 + Klaus un-regression:
  Run: `dotnet test` → full suite green; record the count (expect ~2252 + the new block tests).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.
  Run the 6502 both-tier + Klaus sweep (the M3.4c Task 9 Step 5 invocation) → green (the Z80 added NO 6502
  path).

- [ ] **Step 6: Fill the closeout table (below) + commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs \
        docs/superpowers/plans/2026-06-14-m3-z80-ed-block-ops.md
git commit -m "$(cat <<'EOF'
feat(z80): ED block ops TomHarte-green — 16 opcodes (16k cases, 0 failures); universal regression green

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 7: Push + open the PR.**
  Run: `git push -u origin feat/m3-z80-ed-block-ops` (after the user approves; merge via PR per CLAUDE.md).
  Open a PR targeting `main`. The PR body claims EXACTLY: the 16 ED block ops (0xA0–0xBB) are TomHarte-green
  (incl. the repeat PC-rewind, the F3/F5 X/Y quirks, the H/C/P-V port quirk, the per-op WZ rules); the
  whole Z80 (base + CB + ED) re-validated at the universal Q/WZ/IM bar; 6502 byte-identical. Name what is
  STILL deferred: the DD/FD/DDCB/FDCB planes = the NEXT slice (M3.4e); interrupt SERVICING + ZEXALL + the
  JIT = M3.5. NEVER overstate. Include a **Docs Impact** section linking the overview doc.

**New-test estimate:** ~0 new files (the theory widens; the bulk is iterative green-driving).

---

## Plan self-review (completed at write time)

- **Scope coverage (the 4 IN-scope items):**
  - **(1) the 16 block opcodes** — Task 2 (LD), Task 3 (CP), Task 4 (IN/OUT), green in Task 7. ✓
  - **(2) the `Z80EdBlock` class** — Task 1. ✓
  - **(3) `Z80EdSemantics.OpsFor` extended** — Task 2 Step 1 (the block arm) + Task 5 (test). ✓
  - **(4) the ED TomHarte gate widened** — Task 7. ✓
  - **The load-bearing quirks** — the repeat PC-rewind (Task 2 `EmitBlockRepeatTail`); the F3/F5 X/Y
    quirks (Task 2 LD `A+n`, Task 3 CP `A−(HL)−H`, Task 4 IN/OUT B-based); the H/C/P-V `k` quirk (Task 4);
    the BC/DE/HL auto-inc/dec (Tasks 2–4); the per-op WZ rules (Tasks 2–4 + the repeat tail). ✓
- **OUT-of-scope honored:** DD/FD/DDCB/FDCB stay `// TODO`; interrupt servicing not added; the block-op
  rows are JIT fallbacks only (`JitOpLiteral` `case "EdBlock": break;`). ✓
- **Placeholder scan:** every code step shows literal code; the only `// TODO` in EMITTED code is the
  Task 1 STUB body, replaced in Task 2, and the Task 2/3 `default`-arm placeholders, replaced in Tasks 3/4
  (each names its replacement). No "TBD"/"similar to Task N". ✓
- **Type/name consistency:** `EdBlockOp`/`EdBlock` (Op.cs/Spec.cs), `s_microOpSignatures`/`FactoryArity`
  `"EdBlock"`, `Z80EdBlock` (the enum, `ClassifyOps`, `ValidateModeForClass`, status-touch, `isZ80`,
  `Z80Cycles`, `Z80WritesFlags`, the JIT predicates), `EmitZ80EdBlockBody` + `EmitBlockRepeatTail` +
  `EmitBlockIoFlags` (the helper signatures match across Tasks 2–4 — `EmitBlockRepeatTail` gains the
  `condition` param in Task 3 and the LD call is updated then), `Z80EdSemantics.Block` table + the arm. ✓
- **Code/vector contradictions surfaced (the code/vectors win):** (F1) NO dataset gap (unlike M3.4c) —
  recorded, no add-rows task; (F2) no new AddrMode (Implied); (F3) the repeat PC-rewind confirmed against
  `ed b0.json`; (F4) the per-family X/Y quirk (re-derive at Task 0 Step 5); (F5) the IN/OUT `k` quirk
  (re-derive); (F6) the per-family WZ rules (confirmed LDI/CPI/INI; re-derive the rest); (F7) every block
  op writes F (Q=F); (F8) cycle 16/21. ✓
- **Build-green-after-every-task:** synthetic tests (Tasks 1–4) decouple from the regen (Task 6); the regen
  + the theory widening (Task 7) are the only TomHarte-affecting tasks. ✓
- **WATCH-POINTS recorded:** the LD X/Y bit-1-not-5 anomaly (re-derive); the CP-repeat WZ ordering (Task 3
  Step 4); the IN/OUT `k` operand (`C±1` vs `L`) + the B-decrement-before-WZ order (Task 4); the cycle
  internal-padding (Tasks 2–4 Step 5). ✓

## Closeout (filled at completion)

| Commit | Content | Suite |
|---|---|---|
| (Task 1) | EdBlock vocabulary + Z80EdBlock class | green |
| (Task 2) | LDI/LDD/LDIR/LDDR + repeat + A+n X/Y; Z80EdSemantics block arm | green |
| (Task 3) | CPI/CPD/CPIR/CPDR + match-or-exhaust repeat + WZ±1 | green |
| (Task 4) | INI/IND/OUTI/OUTD (+repeats) + the k quirk + WZ=BC±1 | green |
| (Task 5) | importer semantics test (16 -> EdBlock) | green |
| (Task 6) | 16 rows live + spec regen | green |
| (Task 7) | ED block ops TomHarte-green + universal regression + closeout | green |

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | 2252 passed / 0 failed / 0 skipped |
| Final test count | (fill: 2252 + ~33 new) |
| ED block opcodes made live | 16 (0xA0–0xBB); probe == emitted == covered == 16 |
| ED block TomHarte (full UAT) | 16 × 1000 = 16,000 cases, 0 failures (registers incl. F's X/Y, I/R, IM, IFF1/IFF2, WZ, Q, RAM, ports, per-T-state trace, repeat PC-rewind) |
| ED total now green | 80 opcodes (64 core + 16 block), 80,000 cases |
| WZ/MEMPTR modeled? | YES — incl. the per-family block-op rules + the repeat WZ=PC+1 |
| Q lifecycle | every block op sets Q=F |
| Base + CB + ED re-validated? | YES — full Z80 UAT 0 failures with final Q/WZ/IM on every case |
| 6502 un-regressed? | (fill: YES — both tiers + Klaus, byte-identity guard green) |
| Any 6502 file changed? | NONE (additive) |
| `-warnaserror` | clean |
| Still deferred | DD/FD/DDCB/FDCB = next slice (M3.4e); interrupt SERVICING + ZEXALL + JIT = M3.5 |
| Recommended next chunk | DD/FD/DDCB/FDCB IX/IY prefixes (M3.4e) — see the overview doc §4 |

### Deviations from the plan's literal code (fill at completion; all gates green)

1. (record any WZ/flag-rule corrections forced by the vectors — esp. the X/Y bit mapping, the IN/OUT `k`
   operand, and the CP-repeat WZ ordering, since these were flagged as re-derive-from-vector.)
2. (record any cycle-padding adjustments.)
