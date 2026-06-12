# CpuEmulator.SpecImporter

Console tool that generates CPU spec tables (DSL source files) from a vendored,
curated machine-readable opcode dataset crossed with a hand-authored mnemonic
semantics map.

## Usage

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset  tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json \
  --out      src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs \
  [--report]
```

This is the canonical regeneration command (it also appears in the generated file's
header). As of chunk 3b-i the committed `Mos6502Spec.cs` **is** this tool's output —
`RegeneratedSpecTests.Committed_Mos6502Spec_is_exactly_the_tool_output` pins the
committed file to a fresh run, so hand-edits to the spec file will fail the suite;
edit the data files and regenerate instead.

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

Hand-authored map of mnemonic → micro-op expression strings for the 54
mnemonics expressible in the DSL vocabulary as of chunk 3b-i (24 original +
ALU 9, RMW 6 + DEX/DEY, stack 4, flag 7, flow 2). The only dataset mnemonics
still absent are BRK and RTI (chunk 3b-ii), which the tool emits as
`TODO(semantics)` rows. Grows with each chunk as new micro-ops are added;
the count is pinned by `SemanticsMapTests.Loads_54_Mnemonics`.

**NOTE — TXS has no SetNZ:** TXS (Transfer X → Stack Pointer, 0x9A) is the one
register transfer on the 6502 that does **not** affect any flags. The semantics
map entry is `[Transfer(Reg.X, Reg.S)]` with no `SetNZ`. This is deliberate and
is pinned by `SemanticsMapTests.TXS_Has_No_SetNZ`.

**Known limitation — duplicate mnemonic keys:** JSON object semantics are
last-key-wins; `System.Text.Json` silently keeps the final occurrence of a
duplicated mnemonic key. The loader cannot detect this without a separate
`JsonDocument` pre-pass. Review diffs carefully when expanding the map
(next: BRK/RTI in 3b-ii) — a copy-pasted duplicate key will silently shadow
the earlier entry.
