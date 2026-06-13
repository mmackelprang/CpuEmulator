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
map entry is `[Transfer("X", "S")]` with no `SetNZ`. This is deliberate and
is pinned by `SemanticsMapTests.TXS_Has_No_SetNZ`.

**Known limitation — duplicate mnemonic keys:** JSON object semantics are
last-key-wins; `System.Text.Json` silently keeps the final occurrence of a
duplicated mnemonic key. The loader cannot detect this without a separate
`JsonDocument` pre-pass. Review diffs carefully when expanding the map —
a copy-pasted duplicate key will silently shadow the earlier entry.

### `data/z80-opcodes.json` (+ `-a.json`, `-b.json`, `z80-review.md`) — M3.3

The reconciled, cross-corroborated **documented** Z80 opcode dataset: **698 rows** across the seven
prefix planes (252 base, 248 CB, 58 ED, 39 DD, 39 FD, 31 DDCB, 31 FDCB). Produced by the
datasheet-extraction runbook (the first real non-6502 use — ADR 0001 Decision 6).

**Honest framing (read this):** this dataset is **structurally validated** (schema, prefix-key
uniqueness, mode/byte rules, decode-structure well-formedness) and **cross-corroborated** (two
independent sources agree on every committed row), but its behavioral correctness is
**UNVERIFIED-PENDING-M3.4-TomHarte**. M3.3 does NOT prove a single cycle count or flag effect is the
true silicon value. The real behavioral gate is M3.4's TomHarte per-cycle vectors + ZEXALL/ZEXDOC.

**Provenance — two genuinely independent sources, reconciled:**
- **Source A** (`z80-opcodes-a.json`, regenerated by `gen_z80_a.py`): the **Zilog Z80 CPU User Manual
  (UM0080)** — the primary authoritative reference (per-instruction descriptions, opcode bit-patterns,
  M-cycle/T-state tables).
- **Source B** (`z80-opcodes-b.json`, regenerated by `gen_z80_b.py`): the **clrhome.org Z80 opcode
  table** — a distinct community document with a different format and independent error surface
  (extracted WITHOUT reference to A, so the two error sets are independent — the point of the diff).
- The committed `z80-opcodes.json` is the **reconciled** result: `--diff A vs B` exits 0. The raw B
  extraction disagreed in 25 cells (1 field: JR C,d not-taken base 7 vs clrhome's taken 12; 24
  coverage: undocumented SLL + Z180/eZ80 extras). Each was adjudicated with **Zilog UM0080
  authoritative**; the undocumented/Z180 rows were reconciled OUT of the documented set (recorded gaps).
  `z80-review.md` is the Stage-2 review report (empty Disagreements section = clean; 698/698 provenance).

**Encoding decisions (recorded):**
- `cycles` = **T-states** (total clock periods), not M-cycles. The Z80 has **no page-cross penalty** —
  `pageCrossPenalty` is uniformly `false` (the field is retained for schema-sharing with the 6502; the
  loader ACCEPTS it rather than asserting it false — a Z80-policy assertion the loader does not bake).
- Conditional instructions record the **not-taken / single-iteration base** T-state count; the variable
  extra is an M3.4 interpreter concern (a `source` note flags each).
- DD/FD enumeration policy: the **documented subset** where the prefix genuinely changes the operation
  (the `HL`/`(HL)`/`H`/`L`-touching rows + the `(IX+d)`/`(IY+d)` indexed forms) — NOT a full base-plane
  re-enumeration. The exact count (39 per index plane) is pinned at extraction.

**The TODO(vocab) inventory — what M3.3 does NOT implement (it is a decode SKELETON, not a flag-correct
emulator):** the new Z80 micro-op vocabulary is M3.4, not M3.3. Inventoried for M3.4:
16-bit ALU (`ADD/ADC/SBC HL,rr`, `INC/DEC rr`); the bit group (`BIT/SET/RES n,r` — needs a bit-index
operand); the rotate/shift family (`RLC/RRC/RL/RR/SLA/SRA/SRL`, `RLCA/…`, `RLD/RRD`); block ops
(`LDIR/CPIR/INIR/OTIR/…`, self-repeating); exchange (`EX DE,HL`, `EX AF,AF'`, `EXX`, `EX (SP),HL`); the
`(IX+d)`/`(IY+d)` indexed EA; conditional flow (`JP/CALL/RET cc`, `JR/JR cc`, `DJNZ`, `RST`); 16-bit
`PUSH/POP rr`; `IN/OUT (C)`-indexed; misc (`DAA/CPL/SCF/CCF/DI/EI/IM 0,1,2`, `LD A,I`/`LD A,R`); and the
composable flag-model micro-ops (`SetSZ`/`SetParity`/`SetHalfCarry`/`SetXY`/`SetOverflow`/`SetAddSub`).
The Z80 flag bit layout for M3.4: `S(7) Z(6) Y(5) H(4) X(3) P/V(2) N(1) C(0)`.

**Two enumerated §9-item-10 findings fed to M3.4** (surfaced, not silently worked around):
1. The shipped `DecodeStructure`/`PrefixByte`/`Insn` model expresses only a **single-byte prefix**
   (`PrefixByte(byte)`; key `(prefix << 8) | opcode`). The two-deep `DD CB dd op` compound prefix
   (`0xDDCB`/`0xFDCB`) is **not expressible** without a decoder extension. The dataset carries the
   compound rows (valid `0xDDCB`/`0xFDCB` tokens); the emitted skeleton emits them as `// TODO`.
2. The Z80 register-shape addressing modes (`Register`, `RegisterIndirect`, `Indexed`,
   `ImmediateExtended`, `ExtendedAddress`, `RelativeJump`, `Bit`) are **not `AddrMode` enum members**.
   The loader ACCEPTS them (dataset truth) but the emitter cannot emit `AddrMode.<them>` (won't
   compile), so their rows are `// TODO(mode)`. Consequence: only the ED plane has an emittable
   Implied-mode representative (`NEG`) in the skeleton; full seven-plane Insn emission awaits the M3.4
   AddrMode + decoder vocabulary extension. The full seven-plane DATASET is nonetheless Rung-1 validated.

`gen_z80_a.py` / `gen_z80_b.py` are the reproducible extraction generators (run `RAW=1 python
gen_z80_b.py` to reproduce the un-reconciled clrhome extraction and re-observe the 25-cell diff).
