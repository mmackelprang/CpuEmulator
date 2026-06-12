# M1 Chunk 3a: Spec-Importer Tool — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `tools/CpuEmulator.SpecImporter` — a console tool that generates CPU spec tables ("mostly automated" spec creation, roadmap decision 2026-06-11): a vendored, curated machine-readable 6502 opcode dataset (151 documented opcodes) crossed with a hand-authored mnemonic→micro-op semantics map emits the `Instructions` DSL source. Rows whose mnemonic semantics or addressing mode aren't yet expressible in the DSL become inventoried TODO comments — so the importer's real-row coverage grows automatically as chunk 3b expands the vocabulary. PR #4 of Milestone 1 (branched from main; PRs #1–#3 merged).

**Architecture:** a testable `SpecImportEngine` (load dataset → validate → cross with semantics → emit a complete spec-class file) wrapped by a thin CLI. The emitted source uses ONLY the literal DSL forms the syntax-only generator parses (literal opcodes/mnemonics, `AddrMode.X`/`Reg.X`/`Flag.X` member accesses, collection expressions — carry-forward from the 2b final review). The end-to-end gate feeds importer output through the REAL source generator via the existing `GeneratorTestHost` and requires zero CPUGEN diagnostics.

**Success criteria:** the 11 hand-written `Mos6502Spec` rows are reproduced exactly by the importer (regression anchor); all ~151 dataset rows are accounted for (emitted | todo-semantics | todo-mode, with a counts report); importer output compiles through the generator end-to-end.

**Out of scope (3b):** replacing the live `Mos6502Spec.cs` with importer output; new micro-ops/addressing modes; TomHarte validation; carry-forward items 6–10 from the 2b final review (emitter mode-asserts, emitted-local-name reservation, Reg whitelist widening, enum-mirror sync, classify-once-in-parser) — those seed the 3b plan.

---

## File structure

```
tools/CpuEmulator.SpecImporter/
    CpuEmulator.SpecImporter.csproj      — net10.0 console (NOT in the AOT-clean set; tooling)
    Program.cs                           — CLI: --dataset --semantics --out [--report]
    SpecImportEngine.cs                  — orchestration: load → validate → emit
    OpcodeDataset.cs                     — dataset record types + System.Text.Json loader + validation
    SemanticsMap.cs                      — semantics record types + loader + vocabulary whitelist
    SpecFileEmitter.cs                   — DSL source emission (rows, TODOs, file scaffold, report)
    data/mos6502-opcodes.json            — vendored curated dataset (151 documented opcodes)
    data/mos6502-semantics.json          — hand-authored semantics for the current vocabulary
    README.md                            — dataset provenance/attribution + usage
tests/CpuEmulator.Tests/
    Importer/OpcodeDatasetTests.cs
    Importer/SemanticsMapTests.cs
    Importer/SpecFileEmitterTests.cs
    Importer/ImporterEndToEndTests.cs    — importer output → real generator → zero diagnostics
```

Solution: add the tool project; test project references it.

## Data formats

`data/mos6502-opcodes.json` — array of 151 entries (documented opcodes only):

```json
{ "opcode": "0xA9", "mnemonic": "LDA", "mode": "Immediate", "bytes": 2, "cycles": 2, "pageCrossPenalty": false }
```

Mode vocabulary in the DATASET is the full 6502 set (13 modes): `Implied, Accumulator, Immediate, ZeroPage, ZeroPageX, ZeroPageY, Absolute, AbsoluteX, AbsoluteY, IndirectX, IndirectY, Indirect, Relative`. The DSL currently supports 5; the loader accepts all 13 (they're dataset truth), and the EMITTER decides supported-vs-todo. `bytes`/`cycles`/`pageCrossPenalty` are carried for 3b's use (cycle cross-checks) and validated for consistency now (mode → expected byte count).

Dataset provenance: transcribed from the public-domain MOS 6502 documented-opcode matrix (the standard reference set; e.g. as tabulated by the NESdev/6502.org communities). `README.md` records this. Correctness backstops: loader validation (count = 151, unique opcodes, mode/byte consistency), spot-row unit tests, the 11-row regression anchor against the live hand-written spec, and ultimately chunk 3b's TomHarte vectors over every opcode.

`data/mos6502-semantics.json` — config + mnemonic map:

```json
{
  "architecture": "mos6502",
  "namespace": "CpuEmulator.Cpus.Mos6502",
  "specClassName": "Mos6502Spec",
  "registers": [
    { "name": "A", "bits": 8 }, { "name": "X", "bits": 8 }, { "name": "Y", "bits": 8 },
    { "name": "S", "bits": 8, "role": "StackPointer" },
    { "name": "P", "bits": 8, "role": "Status" },
    { "name": "PC", "bits": 16, "role": "ProgramCounter" }
  ],
  "mnemonics": {
    "LDA": "[Load(Reg.A), SetNZ(Reg.A)]",
    "LDX": "[Load(Reg.X), SetNZ(Reg.X)]",
    "LDY": "[Load(Reg.Y), SetNZ(Reg.Y)]",
    "STA": "[Store(Reg.A)]",
    "STX": "[Store(Reg.X)]",
    "STY": "[Store(Reg.Y)]",
    "TAX": "[Transfer(Reg.A, Reg.X), SetNZ(Reg.X)]",
    "TXA": "[Transfer(Reg.X, Reg.A), SetNZ(Reg.A)]",
    "TAY": "[Transfer(Reg.A, Reg.Y), SetNZ(Reg.Y)]",
    "TYA": "[Transfer(Reg.Y, Reg.A), SetNZ(Reg.A)]",
    "TSX": "[Transfer(Reg.S, Reg.X), SetNZ(Reg.X)]",
    "TXS": "[Transfer(Reg.X, Reg.S)]",
    "INX": "[Increment(Reg.X), SetNZ(Reg.X)]",
    "INY": "[Increment(Reg.Y), SetNZ(Reg.Y)]",
    "JMP": "[Jump()]",
    "NOP": "[]",
    "BNE": "[BranchIf(Flag.Z, false)]",
    "BEQ": "[BranchIf(Flag.Z, true)]",
    "BCC": "[BranchIf(Flag.C, false)]",
    "BCS": "[BranchIf(Flag.C, true)]",
    "BPL": "[BranchIf(Flag.N, false)]",
    "BMI": "[BranchIf(Flag.N, true)]",
    "BVC": "[BranchIf(Flag.V, false)]",
    "BVS": "[BranchIf(Flag.V, true)]"
  }
}
```

(24 mnemonics expressible in today's vocabulary. NOTE the deliberate TXS subtlety: no SetNZ — TXS is the one transfer that sets no flags. A semantics-map review comment must say so.)

Loader validation: ops text must be a bracketed list of calls drawn from a vocabulary whitelist (`Load/Store/Transfer/Increment/SetNZ/Jump/BranchIf` with `Reg.`/`Flag.`/bool args — a regex/char-scan acceptance, NOT a full parser; the generator is the real gate and runs in the e2e test). The whitelist mirrors the DSL (sync hazard — comment it, same class as the recorded enum mirrors).

## Emission rules

For each dataset row, in opcode order:
- mnemonic has semantics AND mode ∈ DSL's `AddrMode` (5 supported) → emit a real row:
  `Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),`
- mnemonic has semantics, mode unsupported → `// TODO(mode): 0xBD LDA AbsoluteX — awaiting AddrMode support`
- no semantics → `// TODO(semantics): 0x69 ADC Immediate — awaiting micro-op vocabulary`
- The file scaffold: auto-generated header (tool name + dataset path + counts + "regenerate with: dotnet run --project tools/CpuEmulator.SpecImporter -- ..."), usings, namespace, `[CpuSpecification]`, Registers table from config, Instructions collection with real rows first? NO — **dataset (opcode) order throughout**, TODOs inline where they fall, so diffs against regenerations are stable.
- Report (stdout + returned record): `total=151 emitted=N todoSemantics=N todoMode=N` (and per-mnemonic missing-semantics inventory).

Expected coverage with the 24-mnemonic map (verify, don't trust): emitted rows = every (mnemonic ∈ map) × (mode ∈ {Implied, Immediate, ZeroPage, Absolute, Relative}) pairing present in the dataset — includes the current 11 plus LDY/STX/STY/TXA/TAY/TYA/TSX/TXS/INY zp/abs/imm/implied variants and the other 7 branches; roughly 35–40 rows. The unit test asserts the EXACT count found and pins it with a comment explaining the derivation.

> **Amendment (2026-06-12, Task 6 closeout):** the "roughly 35–40" estimate resolved to exactly **33** emitted rows (8 branches + 6 transfers + INX/INY + JMP abs + NOP + 9 load + 6 store variants), pinned with its derivation in `SpecFileEmitterTests.Report_Emitted_Matches_Filter_Derivation`. Task 5's "~142 + ~30" test estimate resolved to **216** (74 new). Three review fix-loops landed mid-branch without changing planned scope: loader strictness (188c8d9), the `--report` per-mnemonic inventory implementation (894c5e7 — Task 5 had initially deferred it), and the e2e collision-rationale correction (8398e62 — the generator host's TPA closure DOES reference the Mos6502 assembly; no collision occurs because spec discovery is syntax-only).

---

### Task 1: Branch + tool project scaffold

- [ ] `git branch --show-current` → `feat/m1-spec-importer` (already created).
- [ ] `dotnet new console -o tools/CpuEmulator.SpecImporter -n CpuEmulator.SpecImporter`; edit csproj: net10.0 only (Directory.Build.props supplies the rest; `IsAotCompatible` NOT set — tooling); `dotnet sln add`; `dotnet add tests/CpuEmulator.Tests reference tools/CpuEmulator.SpecImporter`.
- [ ] Data files are content-copied: `<Content Include="data\**" CopyToOutputDirectory="PreserveNewest" />` (tests locate them via a `DataPath` helper walking from `AppContext.BaseDirectory`; simpler alternative: tests reference the repo-relative path via a compile-time constant — implementer's choice, report it).
- [ ] Gate: build 0 warnings, suite still 142/142. Commit: `chore: scaffold SpecImporter tool project`

### Task 2: Dataset + loader (TDD)

- [ ] Failing tests (`OpcodeDatasetTests`): loads 151 entries; opcodes unique; spot rows (0xA9 LDA Immediate 2 bytes 2 cycles; 0xBD LDA AbsoluteX pageCrossPenalty=true; 0x4C JMP Absolute 3 bytes 3 cycles; 0xEA NOP Implied; 0x00 BRK Implied 1 byte 7 cycles; 0x6C JMP Indirect 3 bytes 5 cycles); every mode string ∈ the 13-mode vocabulary; bytes consistent with mode (Implied/Accumulator=1, Immediate/ZeroPage*/IndirectX/IndirectY/Relative=2, Absolute*/Indirect=3); loader rejects: duplicate opcode, unknown mode, wrong byte count (three malformed-JSON tests via temp files or inline JSON strings).
- [ ] Implement `OpcodeDataset.cs` (record `OpcodeEntry`, `Load(string path)` / `Parse(string json)` with `JsonSerializerOptions` camelCase; validation throws `InvalidDataException` with row context) and author `data/mos6502-opcodes.json` — all 151 documented opcodes. This is the task's bulk: transcribe carefully from the standard documented-opcode matrix; the validation suite + later anchors are the safety net. Include `README.md` (provenance, regeneration usage).
- [ ] Gate: tests green, suite green. Commit: `feat: vendored 6502 opcode dataset with validating loader`

### Task 3: Semantics map + loader (TDD)

- [ ] Failing tests (`SemanticsMapTests`): loads config (architecture/namespace/class/registers) + 24 mnemonics; TXS has no SetNZ (explicit pin with the comment rationale); ops-text acceptance: rejects unknown factory name, rejects non-`Reg.`/`Flag.`/bool argument text, rejects unbracketed text; all 8 branches present with correct flag/polarity (table-driven).
- [ ] Implement `SemanticsMap.cs` + author `data/mos6502-semantics.json` exactly as specified above.
- [ ] Gate green. Commit: `feat: hand-authored mnemonic semantics map with vocabulary validation`

### Task 4: Emission engine (TDD)

- [ ] Failing tests (`SpecFileEmitterTests`):
  - the 11 anchor rows: emitted output CONTAINS each of the 11 row strings exactly as they appear in `src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs` today (copy the expected strings literally into the test — single-space normalized; if the live spec's alignment padding differs, normalize whitespace in the comparison and note it);
  - TODO rows: `0x69` (ADC, no semantics) → `TODO(semantics)` comment containing "ADC"; `0xBD` (LDA AbsoluteX) → `TODO(mode)` containing "AbsoluteX";
  - ordering: output rows in ascending opcode order (spot-check 0x4C before 0x85 — note hand-written spec groups by mnemonic; the IMPORTER is opcode-ordered and 3b will adopt that order);
  - counts report: `total == 151`, `emitted + todoSemantics + todoMode == 151`, emitted == the derived expectation (assert exact number once computed honestly — compute by filtering the dataset against the map + 5 supported modes IN THE TEST, then also assert it equals the engine's report; document the resulting constant);
  - scaffold: file contains the auto-generated header, `[CpuSpecification("mos6502")]`, the registers table from config, valid `using static` lines.
- [ ] Implement `SpecFileEmitter.cs` + `SpecImportEngine.cs`.
- [ ] Gate green. Commit: `feat: spec-file emission with TODO inventory and counts report`

### Task 5: End-to-end + CLI (TDD)

- [ ] Failing tests (`ImporterEndToEndTests`):
  - **the keystone:** run the engine on the real data files, take the emitted source, append the minimal hand-written partial (`ReadBus`/`WriteBus`/`HandleUndefinedOpcode`/ctor/Reset/lines — adapt the `ValidSpecSource` partial, class name `Mos6502Cpu`), push through `GeneratorTestHost.Run` → assert **zero generator diagnostics and zero compilation errors**;
  - the generated CPU from imported spec exposes all 6 registers (text assertion on generated output is fine);
  - CLI: invoke `Program.Main` in-proc (`--dataset … --semantics … --out <temp>`), assert exit 0, file written, report line on stdout; bad path → nonzero exit + error message (no stack trace).
- [ ] Implement `Program.cs` (args parsing — plain loop, no package; `--report` prints the inventory).
- [ ] Gate: full suite green (expect ≈ 142 + ~30; report actuals), build 0 warnings. Commit: `feat: importer CLI with generator-verified end-to-end gate`

### Task 6: Docs, final review, push, PR #4

- [ ] README (repo root) `## Status`: add the importer line ("`tools/CpuEmulator.SpecImporter` generates spec tables from the curated opcode dataset; 3b wires its output live"). Tool README already exists from Task 2.
- [ ] Full verification; commit `docs: note spec-importer status in README`; NO push until the controller's final whole-branch review passes; PR base `main`; controller merges on green (standing authorization).

---

## Plan self-review (completed at write time)

- **Scope:** tool + data only; the live spec is untouched (3b's move). Roadmap decision C honored: curated-dataset importer now; TomHarte-derived linter stays parked at M4+.
- **Placeholders:** none — data formats fully specified (including the complete 24-mnemonic map), emission rules concrete, the 151-row dataset is authored-by-reference with four independent correctness nets (loader validation, spot rows, 11-row anchor, 3b TomHarte).
- **Type consistency:** dataset mode names superset DSL `AddrMode` names exactly for the 5 shared members; semantics ops text matches the DSL factories; register config mirrors the live spec's table.
- **Risk noted:** dataset transcription errors in rows not covered by the 11 anchors or spot tests survive until 3b's TomHarte gate — accepted and recorded (that gate covers every opcode exhaustively).
