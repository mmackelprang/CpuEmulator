# CpuEmulator.SpecImporter

Console tool that generates CPU spec tables (DSL source files) from a vendored,
curated machine-readable opcode dataset crossed with a hand-authored mnemonic
semantics map.

## Usage

**Generation** (the canonical regeneration command — it also appears in the generated
file's header):

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset  tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json \
  --out      src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs \
  [--report]
```

As of chunk 3b-i the committed `Mos6502Spec.cs` **is** this tool's output —
`RegeneratedSpecTests.Committed_Mos6502Spec_is_exactly_the_tool_output` pins the
committed file to a fresh run, so hand-edits to the spec file will fail the suite;
edit the data files and regenerate instead.

**Validate-only** (PR #10 — load + validate both schemas, print the standard report +
provenance coverage, write nothing):

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --validate-only \
  --dataset <opcodes.json> --semantics <semantics.json> [--report]
```

**Cross-source diff** (PR #10 — row-by-row field comparison of two datasets keyed by
opcode; compares mnemonic/mode/bytes/cycles/pageCrossPenalty; `source` is deliberately
excluded — independent extractions are expected to cite different documents):

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset <opcodes-a.json> --diff <opcodes-b.json>
```

**Review report** (PR #10 — markdown review artifact: provenance coverage, rows lacking
`source`, the disagreement table when `--diff` is also given, missing-semantics
inventory):

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --validate-only \
  --dataset <opcodes.json> --semantics <semantics.json> \
  [--diff <opcodes-b.json>] --review-report <review.md>
```

The modes compose: `--diff` and `--review-report` work under both `--validate-only` and
generation (`--out`); `--validate-only` + `--out` is a usage error. See the
[extraction runbook](../../docs/user-guide/extraction-runbook.md) for the full Stage-1/2
workflow. Composition notes: `--out --diff` with disagreements still writes the spec file
(then exits 3); if a `--diff` dataset fails to load, the run exits 2 before any
`--review-report` is written.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | usage error / IO error in generation mode |
| 2 | validation failure (dataset or semantics schema error) |
| 3 | cross-source diff found disagreements (distinct from validation failure) |

## Data files

### `data/mos6502-opcodes.json`

Vendored, curated dataset of the 151 **documented** MOS 6502 opcodes.
Illegal/undocumented opcodes are intentionally excluded.

**Provenance:** Transcribed from the public-domain MOS 6502 documented-opcode
matrix as tabulated by the NESdev/6502.org communities (the standard reference
set). The dataset is independently verified by:

1. Loader validation (non-empty, unique opcodes, mode/byte-count consistency);
   the count = 151 is pinned by `OpcodeDatasetTests.Loads_Exactly_151_Entries`.
2. Spot-row unit tests (OpcodeDatasetTests.cs).
3. The 11-opcode regression anchor against the live hand-written `Mos6502Spec`.
4. Chunk 3b's TomHarte test-vector gate (covers every opcode exhaustively).

Each row may include an optional `"source"` string field (e.g. `"MOS hardware manual p.143, table A-1"`) that cites the datasheet or document from which the entry was extracted; the field is `null` when absent and is carried through to `OpcodeEntry.Source` for use by future extraction tooling.

Notable encoding decisions:
- `JMP Indirect` (0x6C): 5 cycles (not 6 — the 6-cycle figure is a common
  misquote; the actual 6502 takes 5 cycles for JMP (ind)).
- Accumulator-mode shifts (0x0A / 0x2A / 0x4A / 0x6A): mode = "Accumulator",
  1 byte, 2 cycles.
- All branch instructions: 2 bytes, Relative mode, 2 base cycles
  (penalty cycles for taken/page-cross are runtime, not static).
- Read-Modify-Write instructions (ASL/LSR/ROL/ROR/INC/DEC indexed abs,X):
  fixed cycle count, `pageCrossPenalty = false`.
- Page-cross penalty is `true` for indexed reads:
  LDA/LDX/LDY/ADC/SBC/AND/ORA/EOR/CMP abs,X / abs,Y / (zp),Y.

### `data/mos6502-semantics.json`

Hand-authored map of mnemonic → micro-op expression strings for all 56
dataset mnemonics (24 original + ALU 9, RMW 7 + DEX/DEY, stack 4, flag 7,
flow 2, + BRK/RTI landed in 3b-ii). Every dataset mnemonic now maps — the
tool emits zero `TODO(semantics)` rows for the 6502. Grows with each new
micro-op; the count is pinned by `SemanticsMapTests.Loads_56_Mnemonics`.

**NOTE — TXS has no SetNZ:** TXS (Transfer X → Stack Pointer, 0x9A) is the one
register transfer on the 6502 that does **not** affect any flags. The semantics
map entry is `[Transfer(Reg.X, Reg.S)]` with no `SetNZ`. This is deliberate and
is pinned by `SemanticsMapTests.TXS_Has_No_SetNZ`.

**Known limitation — duplicate mnemonic keys:** JSON object semantics are
last-key-wins; `System.Text.Json` silently keeps the final occurrence of a
duplicated mnemonic key. The loader cannot detect this without a separate
`JsonDocument` pre-pass. Review diffs carefully when expanding the map —
a copy-pasted duplicate key will silently shadow the earlier entry.
