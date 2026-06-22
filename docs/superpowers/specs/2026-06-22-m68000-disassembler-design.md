# Design — a real 68000 disassembler (field-grammar-walking)

**Date:** 2026-06-22
**Queue row:** D68
**Roadmap:** Deferred & candidate follow-on #6 — "A real 68000 disassembler" (`docs/ROADMAP.md:255-258`)
**Status:** Spec (autonomous scoping per owner authorization)
**Relation to prior work:** *extends* the M4 68000 field-grammar decoder + the M6 68000 JIT-emit arms. It
adds a disassembler that walks the **same** `FieldGrammar.Ops` table the decode walk and the JIT descriptor
synthesis already walk (`EmitFieldDecodeWalk` / `EmitM68kMoveFamilyRows` in
`src/CpuEmulator.Generators/CpuEmitter.cs`).

---

## 1. Problem

The 6502, Z80, and 8086 each render mnemonics in the monitor host because the generator emits a
`Disassemble(opcode, lo, hi)` switch from their flat `model.Instructions` table
(`EmitDisassembler`, `CpuEmulator.Generators/CpuEmitter.cs:3929-4012`). The 68000 is a **field-grammar**
CPU: it carries **no `InstructionDef` rows** (`M68000Spec.Instructions = []`,
`src/CpuEmulator.Cpus.M68000/M68000Spec.cs:133`) — its 83 operation families live in
`M68000Spec.Decode68k.Ops` as `(Mask, Match, Operation, SizeShift/Width/Encoding, EaShift, LegalEa)`
`FieldOp` records. Because `EmitDisassembler` iterates `model.Instructions` (empty for the 68000), the
generated `Disassemble` is a stub: `return opcode switch { _ => "???" };`. So the 68000 monitor host
(`--board m68000`) renders **every** instruction as `???`, while the other three render real assembly.

## 2. Goal & non-goals

**Goal:** the generator emits a real 68000 `Disassemble` that walks `FieldGrammar.Ops` — for a given opword
it finds the matching `FieldOp`, derives the mnemonic (with the `cc` condition suffix for `Bcc`/`DBcc`/`Scc`,
the `.B`/`.W`/`.L` size suffix where the family has one), and renders the effective-address operand from the
opword's EA field. The 68000 monitor host then shows `MOVE.W D0,D1`, `ADD.L (A2),D3`, `BNE.S *+8`, `JSR (A0)`,
`NOP`, etc. instead of `???`.

**Non-goals (scoped out — recorded so Builder does not over-build):**

- **A 68000 single-instruction assembler.** `IMonitorSupport.TryAssemble` for the 68000 stays the
  always-fail stub it is today (no `Instructions` rows → `AssembleOpcode` returns −1). The roadmap item is a
  *disassembler*; the 68000 assembler is a much larger separate effort (the field grammar is not trivially
  invertible from operand text). Because **no 68000 assembler exists**, the disassembler's round-trip gate
  uses a **curated encoded-instruction → expected-mnemonic table**, not assemble∘disassemble. *Decision D68-1.*
- **Full operand fidelity for operands living in extension words.** The monitor contract
  `Disassemble(byte opcode, byte operandLo, byte operandHi)` carries only the **first 3 bytes** of the
  instruction (opword bytes 0–1 = `opcode`/`operandLo`, plus `operandHi` = the first byte of the first
  extension word). The 68000 renders the **opword-encoded** operands fully (register numbers, EA mode,
  size, condition code, branch sign for the 8-bit `Bcc`/`MOVEQ` immediate that live *in* the opword). For
  operands that live in **extension words** (a `#imm`, a `d16(An)` displacement, an `abs.w`/`abs.l` address,
  a 16-bit `Bcc`/`DBcc` displacement), the disassembler renders a **mode placeholder** — e.g.
  `MOVE.W #<imm>,D0`, `LEA <abs>,A0`, `BNE *+<d16>` — exactly the way the Z80's `Indexed`/`Bit` modes render
  the mnemonic with the opcode-encoded part and a placeholder for bytes the 3-byte contract does not carry
  (`EmitDisassembler` Z80 arms, `CpuEmitter.cs:3980-4003`). *This is the contract-bounded honest rendering,
  not a shortcut* — see §4. *Decision D68-2.*
- **No widening of the `IMonitorSupport` contract.** Widening `Disassemble`/`InstructionLength` to take a
  span (so the 68000 could read its extension words) is a cross-cutting change touching all four CPUs and
  the monitor engine. It is **out of scope** and explicitly deferred (see §7 "Considered & deferred"). The
  monitor still walks correctly: `InstructionLength` already routes through the field-decode descriptor's
  `FixedLength` (`EmitMonitorSupport`, `CpuEmitter.cs:3695-3696`), so the disassembly walk advances by the
  right per-instruction length even though the *text* uses placeholders for extension-word operands. *D68-3.*

## 3. Where it lives (all generator-side — the disassembler is generated, like the others)

| Artifact | Path | Change |
|---|---|---|
| `EmitDisassembler` dispatch | `src/CpuEmulator.Generators/CpuEmitter.cs:3936` | When `model.FieldGrammar is not null`, emit the field-grammar disassembler **instead of** the (empty) `model.Instructions` switch. |
| `EmitM68kDisassembler` (new) | `src/CpuEmulator.Generators/CpuEmitter.cs` (new private method, beside `EmitM68kMoveFamilyRows` et al.) | Generates the 68000 `Disassemble(uint opcode, byte operandLo, byte operandHi)` body that walks `FieldGrammar.Ops`. |
| Generated output | `src/CpuEmulator.Cpus.M68000/obj/generated/.../M68000Cpu.g.cs` | The `???`-only stub becomes a real walk (regenerated by the build). |
| Tests | `tests/CpuEmulator.Tests/Generators/M68kDisassembleTests.cs` (new) | The curated round-trip corpus + the monitor-host integration assertion. |

The 6502/Z80/8086 `Disassemble` emit is **byte-for-byte unchanged** (the new branch is gated on
`model.FieldGrammar is not null`, which only the 68000 has) — the regression-safety gate (the empty git-diff
on the other three generated tables, the same "DECISION R" gate the M6 68000 JIT rows used,
`CpuEmitter.cs:4093-4094`).

## 4. How the field-grammar disassembler works (the walk)

The emitted `Disassemble(uint opcode, byte operandLo, byte operandHi)` receives the 16-bit **opword** as
`opcode` (the explicit-interface bridge packs `(opcode<<8)|operandLo` into the `uint` key the same way the
decode walk does; `operandHi` is the first extension-word byte). The body:

1. **Find the family:** walk `Decode68k.Ops` in order; the first `op` with `(opword & op.Mask) == op.Match`
   wins (identical order to the decode walk — so the disassembler can never disagree with the decoder on
   *which* family an opword is). No match → `"???"` (a genuinely undefined opword, e.g. `$4AFC` ILLEGAL maps
   to the `ILLEGAL` family, but a line-A/line-F opword that matches nothing renders `???`).
2. **Derive the size suffix:** for families with `SizeWidth == 2`, extract the size field at
   `(opword >> SizeShift) & 3` and decode via `SizeEncoding` (Standard: `0→.B 1→.W 2→.L`; Move:
   `1→.B 3→.W 2→.L`). Families with `SizeWidth == 1` (control ops, `Bcc`, etc.) have no size suffix (or a
   fixed one — e.g. `LEA` is always `.L`, `Bcc`/`DBcc` use a `.S`/`.W` branch-width suffix derived from the
   8-bit displacement field). *The size-suffix table is per-family, derived from the family name + the
   `FieldOp` size fields — a small generated lookup.*
3. **Derive the condition code** for `Bcc`/`DBcc`/`Scc`: the 4-bit `cc` field at `(opword >> 8) & 0xF` →
   the 16-name table (`T,F,HI,LS,CC,CS,NE,EQ,VC,VS,PL,MI,GE,LT,GT,LE`). `Bcc` with `cc==0` is `BRA`,
   `cc==1` is `BSR` (the standard 68000 aliasing). *Decision D68-4.*
4. **Render the operand(s):** decode the EA field at `(opword >> EaShift) & 0x3F` → mode `[5:3]` + register
   `[2:0]`, and format per mode:
   - `0 Dn` → `D{r}`; `1 An` → `A{r}`; `2 (An)` → `(A{r})`; `3 (An)+` → `(A{r})+`; `4 -(An)` → `-(A{r})`;
   - `5 d16(An)` → `<d16>(A{r})`; `6 d8(An,Xn)` → `<d8>(A{r},Xn)` (placeholder — extension word);
   - `7/0 abs.w` → `<abs>.W`; `7/1 abs.l` → `<abs>.L`; `7/2 d16(PC)` → `<d16>(PC)`; `7/3 d8(PC,Xn)`;
     `7/4 #imm` → `#<imm>` (placeholder — extension word).
   - Register-direct operands that ARE in the opword (the `MOVE` dual-EA, the data-register field of
     `ADD`/`SUB`/`AND`/`OR`/`CMP`/`EOR`, `MOVEQ`'s `D{r}`, the shift-count register) render fully.
   - `MOVE`/`MOVEA` render **both** EAs. The dst EA is at opword bits `[11:6]` with mode/register **swapped**
     vs the source: dst **register** = `(opword >> 9) & 7`, dst **mode** = `(opword >> 6) & 7` (confirmed
     `src/CpuEmulator.Cpus.M68000/M68000Cpu.Move.cs:81-83`). The disassembler mirrors that exact extraction.
     *Decision D68-5.*

The output is a single canonical line per instruction. Where a true operand value lives in an extension word
the 3-byte contract can't see, the placeholder (`<imm>`, `<abs>`, `<d16>`) is rendered — honest and
non-throwing, and the byte column in the monitor (which DOES read all `InstructionLength` bytes,
`MonitorEngine.cs:170-176`) shows the raw hex so the user has the full encoding.

## 5. Architecture & data flow

```
opword (16b) ─► Decode68k.Ops walk ─► matched FieldOp
                                        │
        ┌───────────────────────────────┼───────────────────────────────┐
        ▼                                ▼                               ▼
  mnemonic(family)               size suffix (.B/.W/.L)          EA operand(s)
  + cc suffix (Bcc/DBcc/Scc)     from SizeShift/Width/Encoding   from EaShift mode/reg
        └───────────────────────────────┴───────────────────────────────┘
                                        ▼
                          "MNEMONIC.S  src,dst"  (placeholders for ext-word operands)
```

The disassembler shares the **exact** field-extraction helpers the decode walk emits (size decode, EA
mode/reg split, cc table) — so it is structurally a sibling of the decoder, not a parallel re-implementation.
*Decision D68-6:* factor the cc-name table and the size-decode into small generated statics the disassembler
and (where useful) the decode walk both reference, so they cannot drift.

## 6. Testing — the un-fakeable gates

**The round-trip gate (curated table — no assembler exists, per D68-1):** a curated corpus of
`(encoded bytes, expected mnemonic string)` pairs covering every family class:
`MOVE.W D0,D1` (`0x3200`), `MOVEQ #<imm>,D0` (`0x7000`), `ADD.L (A2),D3` (`0xD692`), `SUB.B D1,D2`,
`AND`/`OR`/`EOR`/`CMP` reg forms, `ADDA.L`/`SUBA.L`/`CMPA.W`, `Bcc` all 14 conditions + `BRA`/`BSR`,
`DBcc D0,*` , `Scc`, `JMP (A0)` (`0x4ED0`), `JSR (A1)`, `RTS` (`0x4E75`), `NOP` (`0x4E71`), `LEA <abs>,A0`,
`CLR`/`NEG`/`NOT`/`TST`/`NEGX`, `SWAP D0`, `EXT.W`/`EXT.L`, `LINK`/`UNLK`, `TRAP #n`, the shift/rotate
register forms (`ASL`/`LSR`/`ROL`/`ROXR`), the bit ops (`BTST`/`BCHG`/`BCLR`/`BSET` static+dynamic). Each
encoded opword feeds `M68000Cpu.Disassemble(opword, extByte0, extByte1)` and asserts the **exact** expected
string. This is un-fakeable: the curated strings are derived from the Motorola programmer's reference (the
canonical mnemonics), independently of our walk; a wrong family match, size suffix, cc name, or EA mode fails
its row. *(The corpus is the spec's source of truth; ~60-80 rows covering all 83 families' representative
forms.)*

**The decoder-agreement gate (un-fakeable, no curation):** for a sweep of opwords, assert the disassembler's
**family selection** matches the decode walk's family selection (both walk `Decode68k.Ops` in order). This
proves the disassembler and the executor can never disagree on *what* an opword is — a wrong walk fails
against the shipped decoder. *Decision D68-7.*

**The monitor-host integration gate:** load a tiny 68000 program (e.g. `NOP; MOVEQ #1,D0; ADD.W D0,D1; RTS`)
into the `m68000` board and assert `MonitorEngine.Disassemble(addr, 4)` renders the four real mnemonics (not
`???`) with the right `InstructionLength` advance. This proves the end-to-end host path the roadmap item is
about ("the 68000 monitor host gets the mnemonic rendering the 6502/Z80/8086 already have").

## 7. Invariants honored + considered/deferred

**Honored:**

- **AOT-clean Core:** the disassembler is generated C# (a `switch`/walk over a static `FieldOp[]`), no
  reflection, no `Reflection.Emit`. It lives in the generated CPU partial, like the other three.
- **Interpreter-as-oracle / byte-identical JIT parity:** D68 emits **no IL** and changes **no execution
  path** — it is monitor-display-only. The TomHarte / decode / JIT-parity gates are untouched (the
  disassembler is never on the execution gate). Zero risk to the data-axis-exact 68000 contract.
- **No regression to 6502/Z80/8086:** the new emit branch is `FieldGrammar`-gated; the other three
  generated `Disassemble` tables have an empty git-diff (the DECISION-R regression gate).

**Considered & deferred (recorded so the scope boundary is explicit):**

- **Widening `IMonitorSupport.Disassemble` to a span** so the 68000 renders full extension-word operand
  values (real immediates / displacements / absolute addresses instead of placeholders). This is the "nicer"
  outcome but is a **cross-cutting contract change** across all four CPUs + `MonitorEngine`. It is **NOT** in
  D68's scope; D68 delivers the mnemonic + opword-encoded operands + honest extension-word placeholders,
  which is exactly the roadmap item ("the mnemonic rendering the others have"). If the owner later wants full
  operand values, that is a separate, clearly-bounded follow-on (`IMonitorSupport` v2). *This is the one place
  a future architecture decision lives; it is deferred, not taken — no Architect gate needed to ship D68.*

## 8. Dependencies & priority

- **Deps:** none (the field grammar, decode walk, and monitor host all shipped in M4 / "CPUs → computers"
  piece #3). Independent of W and B68-DOC.
- **Priority:** second of the three (no user-facing surface impact like W, but a real capability gap; larger
  than B68-DOC).

## 9. Scoping decisions (recorded — autonomous per owner authorization)

- **D68-1:** disassembler only; no 68000 assembler; round-trip gate is a curated table.
- **D68-2:** opword-encoded operands rendered fully; extension-word operands rendered as honest placeholders.
- **D68-3:** no `IMonitorSupport` contract widening; the walk advances by the real `InstructionLength`.
- **D68-4:** `Bcc cc==0/1` → `BRA`/`BSR`; the 16-name cc table for the suffix.
- **D68-5:** `MOVE`/`MOVEA` render both EAs, dst from the reversed `[11:6]` field (mirrors the decode walk).
- **D68-6:** share the cc-name + size-decode statics between disassembler and decode walk (no drift).
- **D68-7:** a decoder-agreement gate asserts the disassembler and decoder pick the same family.

None of these are cross-cutting architecture except the explicitly-*deferred* contract widening (§7), which
D68 does not take. No Architect escalation needed.
