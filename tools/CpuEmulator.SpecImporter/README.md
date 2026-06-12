# CpuEmulator.SpecImporter

Console tool that generates CPU spec tables (DSL source files) from a vendored,
curated machine-readable opcode dataset crossed with a hand-authored mnemonic
semantics map.

## Usage

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset  tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json \
  --out      <output-path>/Mos6502Spec.g.cs \
  [--report]
```

## Data files

### `data/mos6502-opcodes.json`

Vendored, curated dataset of the 151 **documented** MOS 6502 opcodes.
Illegal/undocumented opcodes are intentionally excluded.

**Provenance:** Transcribed from the public-domain MOS 6502 documented-opcode
matrix as tabulated by the NESdev/6502.org communities (the standard reference
set). The dataset is independently verified by:

1. Loader validation (count = 151, unique opcodes, mode/byte-count consistency).
2. Spot-row unit tests (OpcodeDatasetTests.cs).
3. The 11-opcode regression anchor against the live hand-written `Mos6502Spec`.
4. Chunk 3b's TomHarte test-vector gate (covers every opcode exhaustively).

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

Hand-authored map of mnemonic → micro-op expression strings for the 24
mnemonics expressible in today's DSL vocabulary. Grows with each chunk as new
micro-ops are added.

**NOTE — TXS has no SetNZ:** TXS (Transfer X → Stack Pointer, 0x9A) is the one
register transfer on the 6502 that does **not** affect any flags. The semantics
map entry is `[Transfer(Reg.X, Reg.S)]` with no `SetNZ`. This is deliberate and
is pinned by `SemanticsMapTests.TXS_Has_No_SetNZ`.
