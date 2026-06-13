# Extraction Runbook — Stages 1 and 2

This runbook describes how to extract an opcode dataset and draft semantics map for a new CPU architecture using an LLM assistant, then verify and review the result using the spec-importer tooling.

**What this runbook covers (Stages 1–2):**
- Stage 1: Prompting an LLM to extract opcode data from a CPU PDF or datasheet into the importer's two JSON schemas, with a complete prompt template and verification ladder.
- Stage 2: Generating a provenance and review report, interpreting it, and knowing when the data is ready to merge.

**What this runbook does NOT cover:**
- The micro-op vocabulary and mode cycle-templates — these are always hand work, per architecture, and are described in [Adding a CPU](adding-a-cpu.md).
- Stage 3 (spec linter) — a TomHarte-derived inference pass that cross-checks extracted data against SingleStepTests vectors. Stage 3 lands in M4+.
- Automating the LLM API calls — the workflow here is manual prompting. Automation is M4+.

**Audience:** someone adding a new CPU architecture who needs an opcode dataset (the `mos6502-opcodes.json` equivalent) and a first-pass semantics map.

---

## Stage 1: Extracting the Dataset

### 1.1 The Two Schemas

The importer takes two JSON files. The 6502 files in `tools/CpuEmulator.SpecImporter/data/` are the reference models. See [Adding a CPU](adding-a-cpu.md) for a full description.

**Opcode dataset** — one entry per documented opcode:

```json
{
  "opcode": "0xA9",
  "mnemonic": "LDA",
  "mode": "Immediate",
  "bytes": 2,
  "cycles": 2,
  "pageCrossPenalty": false,
  "source": "MOS MCS6500 Family Hardware Manual, p.149, Table A-2"
}
```

Field constraints:
- `opcode`: `"0xNN"` exactly (two uppercase hex digits)
- `mnemonic`: the instruction mnemonic string
- `mode`: one of the 13 canonical mode names (see [Adding a CPU](adding-a-cpu.md) — Assembler operand grammar table)
- `bytes`: integer, must match the mode (Implied/Accumulator = 1; Immediate/ZeroPage/ZeroPageX/ZeroPageY/IndirectX/IndirectY/Relative = 2; Absolute/AbsoluteX/AbsoluteY/Indirect = 3)
- `cycles`: integer base cycle count (page-cross penalty is separate)
- `pageCrossPenalty`: `true` if a page-cross adds a cycle, `false` otherwise
- `source`: optional citation string — include the manual title, page, and table. Strongly recommended: the review report flags uncited rows.

**Semantics map** — mnemonic to micro-op expression:

```json
{
  "architecture": "mos6502",
  "namespace": "CpuEmulator.Cpus.Mos6502",
  "specClassName": "Mos6502Spec",
  "registers": [
    { "name": "A",  "bits": 8 },
    { "name": "X",  "bits": 8 },
    { "name": "Y",  "bits": 8 },
    { "name": "S",  "bits": 8, "role": "StackPointer" },
    { "name": "P",  "bits": 8, "role": "Status" },
    { "name": "PC", "bits": 16, "role": "ProgramCounter" }
  ],
  "mnemonics": {
    "LDA": "[Load(\"A\"), SetNZ(\"A\")]",
    "NOP": "[]",
    "BRK": "[Brk()]"
  }
}
```

The addressing mode comes from the dataset row, so the semantics expression names only the register and flag effects — not the mode. One entry per mnemonic (not per opcode row): `LDA` covers all LDA variants regardless of addressing mode.

### 1.2 The LLM Prompt Template

Use this template verbatim. Replace `<CPU_MANUAL_TEXT>` with the relevant section(s) of the CPU PDF or datasheet (paste the opcode table, instruction descriptions, and cycle counts). Replace `<ARCH_NAME>`, `<NAMESPACE>`, and `<SPEC_CLASS_NAME>` with your architecture identifiers.

---

```
You are a precise technical extractor. Emit only valid JSON. Do not invent opcodes;
extract only what the manual documents. Do not guess cycle counts or addressing modes —
if the manual is ambiguous, mark the row with a note in the "source" field.

## Task

Extract the complete opcode table from the CPU manual text below into two JSON files:
an opcode dataset and a semantics map, using the exact schemas defined here.

## Opcode Dataset Schema

Each documented opcode is one JSON object in an array with these fields:
- "opcode"          (string)  — exactly "0xNN" (two uppercase hex digits, e.g. "0xA9")
- "mnemonic"        (string)  — the instruction mnemonic (e.g. "LDA")
- "mode"            (string)  — one of these 13 exact strings:
    Implied, Accumulator, Immediate,
    ZeroPage, ZeroPageX, ZeroPageY,
    Absolute, AbsoluteX, AbsoluteY,
    IndirectX, IndirectY, Indirect, Relative
- "bytes"           (integer) — total instruction length in bytes:
    Implied or Accumulator → 1
    Immediate, ZeroPage, ZeroPageX, ZeroPageY, IndirectX, IndirectY, Relative → 2
    Absolute, AbsoluteX, AbsoluteY, Indirect → 3
- "cycles"          (integer) — base cycle count (without page-cross penalty)
- "pageCrossPenalty" (boolean) — true if a page boundary crossing adds one cycle
- "source"          (string, optional but strongly preferred) — cite the specific
    manual section, page number, and table name where you read this row.
    Format: "<Manual Title>, p.<page>, <section or table name>"
    If you are uncertain about a field, include a note: "uncertain: <reason>"

## Semantics Map Schema

One entry per mnemonic (not per addressing mode) mapping to a micro-op expression.

The ops text is a bracketed list of factory calls: [Factory(args...), ...]

Allowed factories and their exact signatures (use ONLY these — no other expressions):
  Load("<name>")             — load operand into register
  Store("<name>")            — store register to operand address
  Transfer("<src>", "<dst>") — copy one register to another
  Increment("<name>")        — increment register by 1
  Decrement("<name>")        — decrement register by 1
  SetNZ("<name>")            — set N and Z flags from register value
  Jump()                     — set PC to operand address
  BranchIf(Flag.<name>, true/false) — branch if flag equals the boolean
  Adc()                      — add with carry (A + operand + C → A, sets NZCV)
  Sbc()                      — subtract with borrow
  And()                      — bitwise AND into A
  Ora()                      — bitwise OR into A
  Eor()                      — bitwise XOR into A
  Compare("<name>")          — compare register to operand (sets NZC, no store)
  Bit()                      — BIT test (N←bit7, V←bit6, Z←A&mem)
  ShiftLeft()                — arithmetic shift left (operand or accumulator)
  ShiftRight()               — logical shift right
  RotateLeft()               — rotate left through carry
  RotateRight()              — rotate right through carry
  IncrementMem()             — increment memory operand
  DecrementMem()             — decrement memory operand
  Push("<name>")             — push register onto stack
  Pull("<name>")             — pull register from stack
  PushP()                    — push P (status) with B flag set
  PullP()                    — pull P from stack
  SetFlag(Flag.<name>, true/false) — set or clear a flag
  Jsr()                      — jump to subroutine (push PC-1, set PC)
  Rts()                      — return from subroutine (pull PC, increment)
  Brk()                      — software interrupt (push PC+1, push P, load IRQ vector)
  Rti()                      — return from interrupt (pull P, pull PC)

Arguments MUST be one of:
  "<RegisterName>"     a quoted register-name string (e.g. "A", "X", "PC") validated
                       against the spec's Registers table — there is no closed Reg enum
  Flag.<FlagName>      (e.g. Flag.N, Flag.Z, Flag.C, Flag.V, Flag.I, Flag.D, Flag.B)
  true
  false

No arbitrary C# expressions, no non-register-name string literals, no lambdas.
An empty ops text is valid for NOP: "[]"

## Output

Produce two JSON outputs:

### opcodes.json

[array of opcode entries as described above]

### semantics.json

{
  "architecture": "<ARCH_NAME>",
  "namespace": "<NAMESPACE>",
  "specClassName": "<SPEC_CLASS_NAME>",
  "registers": [<your register definitions>],
  "mnemonics": {
    "<MNEMONIC>": "<ops text>"
  }
}

## CPU Manual Text

<CPU_MANUAL_TEXT>
```

---

**Notes on prompt usage:**
- For large manuals, paste one section at a time (e.g. opcode table first, then instruction descriptions for cycles and flags).
- Ask the LLM to add a `source` field to every row citing the page and table. If it omits citations, re-prompt with: "Please add a 'source' field to each row citing the specific page and table you read that value from."
- If the LLM produces an unrecognized factory name, correct it and re-run. The loader validates all factory names.

**Vocabulary scope (important for non-6502 families):** the 13 mode names, the byte-count rules, the `"0xNN"` opcode format, and the 30-factory list in this template are the **current 6502-family loader vocabulary** — they are hardcoded in `OpcodeDataset` and `SemanticsMap` today. A new CPU family (e.g. the Z80, with its CB/DD/ED/FD-prefixed opcodes, separate I/O space, and different mode set) **extends the loaders first**: new mode names and byte rules in `OpcodeDataset`, new factories in `SemanticsMap.FactoryArity` (and the generator's mirror tables — see the SYNC HAZARD comments in both files), then updates this template to match. Per spec §9 item 10, the framework changes a new family forces are measured and treated as findings, not failures — expect this template to grow per family.

### 1.3 The Verification Ladder

Each rung gates the next. A clean rung is required before proceeding.

**Rung 1 — Loader validation (`--validate-only`)**

Catches: malformed JSON, unknown mode strings, byte-count mismatches, duplicate opcodes, missing required fields, unknown factory names in the semantics map.

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --validate-only \
  --dataset   <path>/opcodes.json \
  --semantics <path>/semantics.json
```

Exit 0 = both schemas load and validate clean. Exit 2 = schema error (message to stderr describes the exact problem). Fix the JSON, re-run.

**Rung 2 — Cross-source diff (`--diff`)**

Extract from a second independent document (a different edition, an errata sheet, a third-party datasheet, or a different section of the same manual). Diff the two datasets. Every disagreement requires a manual decision — pick the value that the primary source supports and note the discrepancy.

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset   <path>/opcodes-a.json \
  --diff      <path>/opcodes-b.json
```

Exit 0 = identical. Exit 3 = disagreements found (table printed to stdout). Zero disagreements required before Rung 3.

**Rung 3 — CPUGEN diagnostics**

Generate the spec file and build. The Roslyn generator emits structured errors (CPUGEN001–011) for DSL mistakes the loader does not catch (e.g. an `Insn` referencing a register not in `Registers`, an unsupported mode/op combination).

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset   <path>/opcodes.json \
  --semantics <path>/semantics.json \
  --out       src/CpuEmulator.Cpus.<YourArch>/<YourArch>Spec.cs

dotnet build 2>&1 | grep CPUGEN
```

Zero CPUGEN diagnostics required.

**Rung 4 — End-to-end generator gate**

The committed-spec roundtrip test confirms that the importer output feeds through the real Roslyn generator with zero compilation errors:

```
dotnet test --filter "ImporterEndToEndTests"
```

This test runs the full pipeline (dataset + semantics → emitted spec → generator → compilation) in-process. Green = the extraction is generator-clean.

**Rung 5 — SingleStepTests / TomHarte (where vectors exist)**

For architectures with a TomHarte/SingleStepTests vector set, run the full per-cycle sweep after implementing the hand-written partial class:

```
$env:CPUEMULATOR_UAT = "full"
dotnet test --filter "FullyQualifiedName~TomHarte"
Remove-Item Env:\CPUEMULATOR_UAT
```

For architectures without vectors, manual validation against known programs or hardware substitutes. The Z80 (M3) will exercise this rung when the Z80 vectors become available.

### 1.4 The `source` Field Convention

Every row in the opcode dataset should carry a `source` citation naming the manual, page, and table or section where the cycle count, mode, and page-cross flag were read. The format is:

```
"<Manual Title>, p.<page>, <section or table name>"
```

Example:
```json
"source": "MOS MCS6500 Family Hardware Manual, p.149, Table A-2"
```

If a value is uncertain, use:
```json
"source": "uncertain: MOS Hardware Manual p.47 — cycle count for indirect mode ambiguous"
```

The review report (Stage 2) flags all rows lacking a `source` citation. The goal is 100% provenance coverage before merging a new CPU's dataset.

---

## Stage 2: Review and Provenance

### 2.1 Generating the Review Report

After passing Rung 1 (and optionally after a Rung 2 diff), generate the full review report:

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --validate-only \
  --dataset   <path>/opcodes.json \
  --semantics <path>/semantics.json \
  --diff      <path>/opcodes-b.json \
  --review-report <path>/review.md
```

The `--diff` argument is optional; include it when you have a second dataset for cross-source verification.

### 2.2 Interpreting the Report

The report has four sections:

**Provenance Coverage** — `N/total dataset rows carry source citations`. A fresh extraction will show `0/N`; that is the expected and documented starting state, not an error. Increase coverage by re-running the LLM with specific page references, or by hand-annotating rows where the citation is unambiguous from context.

**Rows Lacking Source** — lists every uncited row (opcode, mnemonic, mode). Work through this list: look up each row in the primary manual, add the citation, re-run validate and re-generate the report. Target 100% coverage before merging.

**Disagreements** — present only when `--diff` was given and the datasets differ. Every row here represents a conflict between your two independent extractions. Resolve each: find the authoritative manual statement, pick the correct value, update both datasets to agree, and re-diff until the section disappears.

**Missing Semantics** — mnemonics in the dataset that have no entry in the semantics map. These will emit as `// TODO(semantics):` placeholders in the spec file. Complete the semantics map entries (they are hand work — see [Adding a CPU](adding-a-cpu.md) for the factory vocabulary) and re-run validate.

**The dataset is ready to merge when:**
1. `--validate-only` exits 0 (both schemas clean).
2. `--diff` against the second independent extraction exits 0 (zero disagreements).
3. The review report shows zero missing semantics.
4. CPUGEN build is clean (Rung 3).
5. The importer roundtrip test passes (Rung 4).
6. TomHarte vectors are green where they exist (Rung 5).

100% provenance coverage is strongly recommended before merge but is not a hard gate — partial coverage is acceptable for an initial port if the uncited rows are noted as known gaps.

---

## Worked Micro-Example: 5 Opcodes

This example walks through a hypothetical extraction of five opcodes for a fictional "MyCPU" architecture from two imaginary source documents. The seeded errors in Document A are caught at each ladder rung.

**Example fixture files** (in `docs/user-guide/examples/`):
- `mycpu-opcodes-a.json` — Document A extraction (two errors: BRK cycles wrong; LDA AbsoluteX cycles wrong; three rows lack source)
- `mycpu-opcodes-b.json` — Document B extraction (independent; correct values)
- `mycpu-semantics.json` — hand-authored semantics for the five mnemonics

### 4.1 The Extraction Prompt (Fragment)

Using the Section 1.2 template with `<CPU_MANUAL_TEXT>` replaced by the relevant table excerpt from Document A, the LLM produces a 5-row `mycpu-opcodes-a.json`. Two rows have incorrect cycle counts (LDA AbsoluteX: 4 instead of 5; BRK: 8 instead of 7) and three rows lack source citations.

### 4.2 Rung 1: `--validate-only`

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --validate-only \
  --dataset   docs/user-guide/examples/mycpu-opcodes-a.json \
  --semantics docs/user-guide/examples/mycpu-semantics.json
```

Captured output:
```
total=5 emitted=5 todoSemantics=0 todoMode=0
provenance: 2/5 rows carry source citations
```

Exit 0 — both schemas are valid. The cycle-count errors are not schema errors (the schema allows any positive integer for cycles; correctness is a semantic property verified by TomHarte). The provenance report shows 3 uncited rows to address. Proceed to Rung 2.

### 4.3 Rung 2: `--diff` against Document B

Document B is extracted independently:

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset docs/user-guide/examples/mycpu-opcodes-a.json \
  --diff    docs/user-guide/examples/mycpu-opcodes-b.json
```

Captured output (exit 3):
```
diff: 2 disagreement(s), 0 missing opcode(s), 0 extra opcode(s)
opcode    field             left                  right               
0x00      cycles            8                     7                   
0xBD      cycles            4                     5                   
```

Two disagreements caught: `0x00` (BRK) cycles disagree (8 vs 7), and `0xBD` (LDA AbsoluteX) cycles disagree (4 vs 5). Check the primary manual. Both Document B values are correct. Fix `mycpu-opcodes-a.json` and re-diff → exit 0.

### 4.4 Rung 2 Resolved: Re-diff

After fixing both rows in Document A:
```
diff: 0 disagreement(s), 0 missing opcode(s), 0 extra opcode(s)
```

Exit 0. Proceed to Rung 3.

### 4.5 Rung 3: CPUGEN

Generate the spec file and build. For this example architecture (not a real project target), the CPUGEN step verifies the semantics DSL is syntactically correct. In a real new-arch workflow, the generated file would be placed at `src/CpuEmulator.Cpus.MyCpu/MyCpuSpec.cs` and the build would emit zero CPUGEN diagnostics.

### 4.6 Stage 2: Review Report

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --validate-only \
  --dataset   docs/user-guide/examples/mycpu-opcodes-a.json \
  --semantics docs/user-guide/examples/mycpu-semantics.json \
  --diff      docs/user-guide/examples/mycpu-opcodes-b.json \
  --review-report /tmp/mycpu-review.md
```

Captured output (pre-fix state — the seeded errors are still in Document A, so the report captures everything the reviewer needs to see at once):
```
total=5 emitted=5 todoSemantics=0 todoMode=0
provenance: 2/5 rows carry source citations
diff: 2 disagreement(s), 0 missing opcode(s), 0 extra opcode(s)
opcode    field             left                  right               
0x00      cycles            8                     7                   
0xBD      cycles            4                     5                   
```

(After fixing the two cycle errors, re-run: the diff output reports 0 disagreements with exit 0, and the review report's Disagreements section disappears.)

Captured review report content (same pre-fix run — the Disagreements section is present because the seeded errors are still in Document A):
```markdown
# Extraction Review: mycpu

Generated: 2026-06-12

## Provenance Coverage

2/5 dataset rows carry `source` citations.

## Rows Lacking Source

| Opcode | Mnemonic | Mode |
|---|---|---|
| 0x4C | JMP | Absolute |
| 0xEA | NOP | Implied |
| 0x00 | BRK | Implied |

## Disagreements

| Opcode | Field | Left | Right |
|---|---|---|---|
| 0x00 | cycles | 8 | 7 |
| 0xBD | cycles | 4 | 5 |

## Missing Semantics

All mnemonics have semantics defined.
```

The review report shows: 3 rows still need source citations (JMP, NOP, BRK); the two cycle-count disagreements (shown here for illustration — fix them and re-run to get a clean report). Missing Semantics is clean. Add citations to the three uncited rows and re-generate the report until `Rows Lacking Source` is empty, then the dataset is ready for merge.

---

## Reference: Real 6502 Validate Output

For comparison, the full 6502 dataset:

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --validate-only \
  --dataset   tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json
```

Output:
```
total=151 emitted=151 todoSemantics=0 todoMode=0
provenance: 0/151 rows carry source citations
```

Exit 0. The 6502 dataset has 0/151 source citations today — this is the expected and documented state (the dataset predates the `source` field addition). Adding citations to the 6502 dataset is a recorded improvement item.
