# M3.3: The Z80 Opcode Dataset by Extraction — the Runbook's First Non-6502 Acceptance Test

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** produce, vendor, and *structurally* VALIDATE the Z80 opcode DATASET by running the
datasheet-extraction runbook (`docs/user-guide/extraction-runbook.md`) against the Z80 manual — the
runbook's **first real non-6502 use** and the realization of **ADR 0001 Decision 6**
(extraction-as-acceptance-test, `docs/architecture/0001-z80-second-architecture.md:449-495`). M3.3
extends the importer's three 6502-vocabularied loaders (`OpcodeDataset`, `SemanticsMap`,
`SpecFileEmitter`) to the Z80's prefixed-opcode / extended-mode / extended-flag vocabulary, then
extracts the full documented Z80 instruction set across the **base + CB + ED + DD + FD + DDCB + FDCB**
prefix planes (~1100+ rows) into `tools/CpuEmulator.SpecImporter/data/z80-opcodes.json`, cross-source
diffs it against a second independent extraction, reconciles the disagreements into one committed
table, and proves the dataset + the M3.1b generic decoder accommodate the Z80's prefix structure
**end-to-end** by emitting a Z80 spec SKELETON that COMPILES through the Roslyn generator producing a
valid decode skeleton (semantics mostly deferred). The decode structure is expressed via the M3.1b
`DecodeStructure`/`PrefixByte` + the `Insn(prefix, opcode, …)` overloads
(`src/CpuEmulator.Core/Specification/DecodeStructure.cs:9-14`,
`src/CpuEmulator.Core/Specification/Spec.cs:18-25`) and the computed-length `ModRm` marker
(`OpcodeDataset.cs:52-55`) the M3.1b plan added for the `DDCB dd op` displacement-then-opcode form.

**The discipline that is load-bearing, not decorative (READ THIS FIRST):** M3.3 does **NOT** prove
the extracted opcode bytes are *behaviorally correct*. The LLM-assisted extraction of ~1100 rows
across seven prefix planes is error-prone at this scale by the runbook's own admission
(`extraction-runbook.md:79-81, 189`). M3.3 delivers a **structurally-validated, cross-corroborated,
provisional** table — the cross-source `--diff` (`DatasetDiff.cs`) is the headline anti-hallucination
gate and its reconciliation protocol (§"Ground truth C") is the load-bearing artifact, not a
checkbox. The **real behavioral gate is M3.4's TomHarte Z80 per-cycle vectors + ZEXALL/ZEXDOC**
(`0001-…:482-490`, `extraction-runbook.md:245-255`). Every claim in this plan about the dataset is
framed **unverified-pending-M3.4-TomHarte**. Stating a cycle count "validated" here would be a lie;
stating it "cross-corroborated against two sources, structurally consistent, pending the per-cycle
vectors" is the truth this plan commits to.

**PR:** branch `feat/m3-z80-extraction` (base `main`, head `fc76db4` — the M3.2 bus/interrupt-seams
merge; **~1491 tests green** is the baseline — confirm the EXACT number at Task 0 and record it). This
plan file is a preparatory doc commit on that branch; the implementation tasks follow.

---

## Scope

**IN scope (the DATASET dimension + the loaders it needs + the skeleton that proves it):**

1. **The Z80 opcode dataset** (`data/z80-opcodes.json`): the full documented Z80 instruction set
   across the seven prefix planes (~1100+ rows — derived honestly in §"Derived numbers"). Each row
   carries `opcode`/`prefix`-key, `mnemonic`, `mode` (Z80-extended vocabulary), `bytes`, `cycles`
   (**T-states** — chosen and documented in §"Ground truth B"), `pageCrossPenalty` (always `false`
   for the Z80 — it has no 6502-style page-cross penalty; recorded), and a **`source` provenance
   citation** per row.

2. **The three loader extensions** the Z80 forces (Decision 6, `0001-…:457-469`;
   `extraction-runbook.md:189`):
   - `OpcodeDataset` — a **prefix-keyed opcode format** (the regex `^0x[0-9A-Fa-f]{2}$` is single-byte
     today, `OpcodeDataset.cs:61-62`; it cannot represent `ED B0`), the Z80 **mode vocabulary** + its
     byte rules, and the **`DatasetDiff` keying fix** (today it keys on `Opcode` string alone,
     `DatasetDiff.cs:35` — two `0xB0` rows in different planes would collide).
   - `SemanticsMap` — the Z80 **register/flag declarations** (data) + the **covered factories** that
     overlap today's vocabulary; the new Z80 factories are inventoried as **TODO(vocab)** (§"Ground
     truth E"), not implemented (that is M3.4).
   - `SpecFileEmitter` — the Z80 **`SupportedModes`** + the **prefixed-`Insn` emission** (emit
     `Insn(prefix, opcode, …)` for a prefixed row; emit the `DecodeStructure` declaration).

3. **The verification ladder for M3.3** (what CAN be checked NOW — §"Ground truth D"): loader
   validation (count, prefix-key uniqueness, mode/byte consistency, decode-structure well-formedness),
   the cross-source `--diff` reconciliation gate, and the **structural-generation check** (the
   skeleton compiles through the generator producing a valid decode skeleton).

4. **The Z80 register/flag DECLARATIONS** (data, for the skeleton — §"Ground truth F"): the register
   set (`A F B C D E H L` + pairs `AF BC DE HL` + the alternate set `AF' BC' DE' HL'` + `IX IY SP I R
   PC`) and the `S Z Y H X P/V N C` flag model — declared as spec DATA the M3.1a data-driven register
   file consumes. **DECLARATIONS only**; the flag-MODEL micro-ops are M3.4.

**NOT in scope (stated so an implementer does not reach for it):**

- **The Z80 interpreter (M3.4).** No hand-written `Z80Cpu` partial, no reset / `R`-refresh / `IM
  0/1/2` / NMI / IFF1/IFF2 logic, no block-op self-repeat, no `DAA`, no `EX`/`EXX` execution. The
  skeleton's semantics are *mostly* TODO by design (§"Ground truth G"). The dataset feeds M3.4; it
  does not implement it.
- **Full Z80 semantics / the new micro-op vocabulary (M3.4).** The 16-bit ALU (`ADD HL,rr`,
  `ADC/SBC HL,rr`), the `(IX+d)` indexed EA computation, the block ops (`LDIR`/`CPIR`/…), the bit
  group (`BIT`/`SET`/`RES`), the rotate/shift family (`RLC`/`RL`/`SLA`/`RLD`/…), `EX`/`EXX`, `IN`/`OUT`
  beyond the M3.2 `PortIn`/`PortOut` primitive, `IM`, and the half-carry `H` computation are all
  **inventoried as TODO(vocab)** (§"Ground truth E"), exactly the 3a pattern (the 6502 started at
  33/56 mnemonics — `2026-06-12-m1-spec-importer.md:107`). NONE are implemented here.
- **The TomHarte Z80 behavioral gate (M3.4, Rung 5).** The per-cycle vectors are the real correctness
  oracle and they land in M3.4 (`extraction-runbook.md:255`, `0001-…:482-490`). M3.3 reaches Rung 4
  (the end-to-end generator gate over the skeleton) and stops; Rung 5 is explicitly deferred. The
  dataset is **unverified-pending-M3.4-TomHarte** at M3.3 close.
- **The flag-model micro-ops** (`SetSZ`/`SetParity`/`SetHalfCarry`/`SetXY`/`SetOverflow`/`SetAddSub`,
  ADR Decision 3 `0001-…:301-308`). DECLARED as flag-model data here (the register/flag declarations
  the skeleton needs); the composable flag micro-op family is M3.4 vocabulary.
- **Any 6502 change.** The 6502 dataset, semantics map, spec, and tests stay UNTOUCHED and green
  (§"Ground truth H" — the explicit no-6502-change invariant). M3.3 *adds* Z80 data + loader vocabulary
  + a skeleton; it does NOT refactor the 6502. The loader extensions are strictly additive — every
  6502 row still validates byte-for-byte (the regression guard, Task 2).

**ADR + runbook + plan links:**
- **ADR 0001 Decision 6** (`0001-…:449-495`) — extraction-as-acceptance; the loaders are
  6502-vocabularied and must be extended FIRST, then run the extraction; TomHarte is the gate; the
  cross-source diff "does the heavy lifting" at ~1000 rows (`0001-…:476-481`).
- **ADR 0001 Decision 1** (`0001-…:98-179`) — prefix decode, option (B), realized by M3.1b's
  `DecodeStructure`/computed-length walk (`DecodeStructure.cs`, the M3.1b plan). M3.3 *expresses* the
  Z80's prefixes through that already-shipped mechanism; it does not build new decode machinery.
- **The extraction runbook** (`docs/user-guide/extraction-runbook.md`) — the Stage-1 LLM prompt
  (`:78-180`), the verification ladder (`:191-255`), the `source`-field convention (`:257-275`), the
  Stage-2 review report (`:279-316`). The runbook's "Vocabulary scope" note (`:189`) is the explicit
  mandate to extend the loaders first. M3.3 ADDS a "worked Z80 example / lessons" addendum to it
  (Task 8, the §8 feature-docs gate).
- **The M3.1b generic-decoder plan** (`docs/superpowers/plans/2026-06-13-m3b-generic-decoder.md`) —
  the `DecodeStructure` DSL surface (Ground truth G), the `Insn(prefix,opcode,…)` overloads, the
  computed-length `ModRm` marker, the `KeyShape` model. M3.3 is the first real CONSUMER of all of it.
- **The M1 3a plan** (`docs/superpowers/plans/2026-06-12-m1-spec-importer.md`) — the closest house-style
  exemplar (the 6502's dataset/semantics/emitter plan); the covered-vs-TODO split pattern (33/56
  mnemonics initially); the four-correctness-nets discipline.

**Plan series:** M3.0 ADR ✅ · M3.1a register file ✅ · M3.1b generic decoder ✅ · M3.2 bus/interrupt
seams ✅ (merged, head `fc76db4`) · **M3.3: this plan (Z80 dataset by extraction) — the runbook
acceptance test** · M3.4: Z80 interpreter + the new micro-op vocabulary + TomHarte/ZEXALL gate · M3.5:
Z80 through the JIT + the genericity findings.

---

## Derived numbers (verified against the repo + the Z80 ISA, not assumed)

### The realistic Z80 opcode count across the seven planes

The Z80 opcode space is seven decode planes. The honest documented-instruction count (the dataset's
target size) is derived per plane below. "Documented" excludes the wholly-undocumented `SLL`/`SL1`
(an undocumented CB op) and the undocumented `IX`-half registers (`IXH`/`IXL`) UNLESS the
controller's undocumented-flag decision (ADR risk #4, `0001-…:697-701`) elects to include them — that
decision is M3.4's (the TomHarte gate decides undocumented coverage). **M3.3 extracts the DOCUMENTED
set; undocumented opcodes are a recorded gap (a TODO inventory), matching the 6502's illegal-opcode
deferral (`framework-design…:297`).**

| Plane | Prefix | What it holds | Documented row estimate |
|---|---|---|---|
| Base | (none) | the 8080-superset core: loads, 8-bit ALU, `JR`/`JP`/`CALL`/`RET`, `RST`, `PUSH`/`POP`, `EX`/`EXX`, register-indirect, `DAA`/`CPL`/`SCF`/`CCF`, `DI`/`EI`/`HALT`/`NOP` | ~250 |
| CB | `0xCB` | the bit/rotate/shift plane: `RLC/RRC/RL/RR/SLA/SRA/SRL` × 8 targets (8 ops × 8 = 64) + `BIT/RES/SET` n,r (3 × 8 bits × 8 targets = 192) | ~248 |
| ED | `0xED` | the extended plane: 16-bit `ADC/SBC HL,rr`, `LD (nn),rr`/`LD rr,(nn)`, block ops `LDI/LDD/LDIR/LDDR/CPI/.../INI/.../OUTI/…`, `IN r,(C)`/`OUT (C),r`, `NEG`, `IM 0/1/2`, `RETI/RETN`, `RLD/RRD`, `LD A,I`/`LD A,R`/`LD I,A`/`LD R,A` | ~80 |
| DD | `0xDD` | `HL`→`IX` re-interpretation of a subset of the base plane (the ops that name `HL`/`(HL)`/`H`/`L`) + `(IX+d)` indexed forms | ~100 |
| FD | `0xFD` | `HL`→`IY` re-interpretation — the exact mirror of the DD plane | ~100 |
| DDCB | `0xDD 0xCB dd op` | the CB plane applied to `(IX+d)` — displacement byte BEFORE the opcode byte; `RLC..SRL (IX+d)` + `BIT/RES/SET n,(IX+d)` | ~248 (mirrors CB, all `(IX+d)`-targeted) |
| FDCB | `0xFD 0xCB dd op` | the CB plane applied to `(IY+d)` — the DDCB mirror | ~248 |

**Honest total: ~1100–1300 documented rows**, depending on how aggressively the DD/FD planes are
enumerated (a pure decoder re-interprets the *entire* base plane under DD/FD even where the prefix is
a no-op; the *documented* set is the subset where DD/FD genuinely changes the operation — the
`HL`/`(HL)`/`H`/`L`-touching rows). **The plan TARGETS the documented set (~1100) and records the
exact count at extraction (Task 3); the DD/FD enumeration policy is a recorded decision in Task 3.**
Compare the 6502's 151. The cross-source diff (Task 4) is where the bulk of the ~1100-row extraction
error surfaces — this is why Decision 6 calls it the rung that "does the heavy lifting"
(`0001-…:478-481`).

> **Why this is a real extraction load, not a transcription.** At ~1100 rows × seven planes, hand
> transcription is exactly the toil the runbook exists to eliminate (`extraction-runbook.md:5-6`). The
> LLM drafts the rows; the verification ladder (loader + cross-source diff + structural generation)
> catches the errors. The honest framing: the extraction eliminates *transcription*, NOT the
> *verification* — and at this scale the verification is the work (Decision 6, `0001-…:485-490`).

### The covered-vs-TODO semantics split (honest, the 3a pattern)

Today's micro-op vocabulary (`SemanticsMap.FactoryArity`, `SemanticsMap.cs:44-82`; the M3.2 additions
`PortIn`/`PortOut`/`Halt`, `Spec.cs:66-70`) covers: `Load`/`Store`/`Transfer`/`Increment`/`Decrement`/
`SetNZ`/`Jump`/`BranchIf`/ALU(`Adc`/`Sbc`/`And`/`Ora`/`Eor`/`Compare`/`Bit`)/RMW(`ShiftLeft`/
`ShiftRight`/`RotateLeft`/`RotateRight`/`IncrementMem`/`DecrementMem`)/stack(`Push`/`Pull`)/flow(`Jsr`/
`Rts`/`Brk`/`Rti`)/`SetFlag`/`PortIn`/`PortOut`/`Halt`.

Mapping the Z80 mnemonics against this vocabulary (the honest covered set — the loads/transfers/basic
ops that overlap; §"Ground truth E" itemizes):

| Bucket | Z80 mnemonics | Covered now? |
|---|---|---|
| 8-bit load/transfer | `LD r,r'` `LD r,n` `LD r,(HL)` (register-register/immediate forms) | **COVERED** (`Load`/`Transfer`) — the EA is the dataset's `mode`, semantics name the register only |
| 8-bit ALU (A-register) | `ADD/ADC/SUB/SBC/AND/OR/XOR/CP A,r` | **COVERED** (`Adc`/`Sbc`/`And`/`Ora`/`Eor`/`Compare`) — the Z80 flag deltas (H/N) are TODO(vocab) but the op shape overlaps |
| 8-bit inc/dec | `INC r` `DEC r` | **COVERED** (`Increment`/`Decrement`) — flag deltas TODO(vocab) |
| stack | `PUSH rr` `POP rr` | **PARTIAL** — `Push`/`Pull` are 8-bit-register today; 16-bit pair push/pop is TODO(vocab) |
| flow | `JP nn` `CALL nn` `RET` `RST` `NOP` `HALT` | **COVERED** (`Jump`/`Jsr`/`Rts`/`Halt`; `NOP` = `[]`); conditional `JP cc`/`CALL cc`/`RET cc` and `JR`/`DJNZ` are TODO(vocab) |
| I/O | `IN A,(n)` `OUT (n),A` | **COVERED** (`PortIn`/`PortOut`, M3.2) — the `(C)`-indexed forms TODO(vocab) |
| 16-bit ALU | `ADD/ADC/SBC HL,rr` `INC/DEC rr` | **TODO(vocab)** |
| bit group | `BIT/SET/RES n,r` | **TODO(vocab)** (the bit-index operand has no slot today) |
| rotate/shift family | `RLC/RRC/RL/RR/SLA/SRA/SRL` `RLCA/…` `RLD/RRD` | **TODO(vocab)** (the 6502 RMW subset does not cover the Z80 family) |
| block ops | `LDIR/LDDR/CPIR/…/INIR/OTIR/…` | **TODO(vocab)** (self-repeating — a one-instruction loop) |
| exchange | `EX DE,HL` `EX AF,AF'` `EXX` `EX (SP),HL` | **TODO(vocab)** |
| indexed | `(IX+d)`/`(IY+d)` EA | **TODO(vocab)** (the EA computation) |
| misc | `DAA` `CPL` `NEG` `SCF` `CCF` `DI` `EI` `IM 0/1/2` `LD A,I`/`LD A,R` | **TODO(vocab)** |

**Honest expected split:** of the ~Z80 distinct mnemonics (~60-70 documented mnemonics, far fewer than
rows), roughly **15-25 mnemonics are covered** by today's vocabulary (the loads/transfers/8-bit-ALU/
basic-flow/IO-immediate overlap) and the **remaining ~40-50 are TODO(vocab)**. By ROW count the
covered fraction is smaller still — the CB/DDCB/FDCB planes (~744 rows) are ALL bit/rotate ops
(TODO(vocab)), so **the dataset emits as a large TODO inventory with a covered minority** — exactly
the 3a starting state (the 6502 emitted 33 real rows of 151 initially). **Task 5 records the EXACT
emitted-vs-TODO row counts; the estimate here is the honest order of magnitude, pinned by the test.**

### Test estimate

Baseline ~1491 (confirm at Task 0). New tests are the loader-extension TDD (prefix-key format,
Z80-mode byte rules, the diff keying fix, the SupportedModes/prefixed-emission), the
cross-source-diff-over-Z80 fixtures, and the structural-generation gate. Per-task estimates are
tabulated under each task and summed in the self-review; **the headline estimate is ~1491 + ~35 ≈
~1526** (routine non-Klaus suite). The extraction ITSELF adds no unit tests (it is a process gated by
the ladder); its artifacts (the two source files, the reconciled committed file, the review report)
are validated by the loader/diff/generation tests + the `--review-report` output, not by per-row
assertions (per-row truth is M3.4's TomHarte).

---

## Ground truth A — the Z80 dataset schema (the prefix-keyed row format)

**The dataset row, Z80 form.** The 6502 row is `{opcode, mnemonic, mode, bytes, cycles,
pageCrossPenalty, source}` (`OpcodeDataset.cs:12-19`). The Z80 row adds an optional **`prefix`** field
and (for the bit plane) an optional **`subfield`**, and reuses the rest. The `OpcodeEntry` record
grows two nullable fields; the 6502 rows leave them null (byte-identical 6502 — §"Ground truth H").

```jsonc
// A base-plane row (no prefix — identical shape to a 6502 row):
{ "opcode": "0x47", "mnemonic": "LD",   "mode": "Register",        "bytes": 1, "cycles": 4,  "pageCrossPenalty": false,
  "source": "Zilog Z80 CPU User Manual (UM0080), p.71, LD r,r' table" }

// An ED-plane row (prefix 0xED) — LDIR, the classic block op:
{ "prefix": "0xED", "opcode": "0xB0", "mnemonic": "LDIR", "mode": "Implied", "bytes": 2, "cycles": 21, "pageCrossPenalty": false,
  "source": "Zilog Z80 CPU User Manual (UM0080), p.135, LDIR" }

// The UNPREFIXED 0xB0 — OR B — same opcode byte, different plane, MUST NOT collide with ED B0:
{ "opcode": "0xB0", "mnemonic": "OR", "mode": "Register", "bytes": 1, "cycles": 4, "pageCrossPenalty": false,
  "source": "Zilog Z80 CPU User Manual (UM0080), p.176, OR r" }

// A CB-plane bit op (prefix 0xCB, a bit-index subfield) — BIT 7,A:
{ "prefix": "0xCB", "opcode": "0x7F", "mnemonic": "BIT", "mode": "Bit", "bytes": 2, "cycles": 8, "pageCrossPenalty": false,
  "source": "Zilog Z80 CPU User Manual (UM0080), p.243, BIT b,r" }

// A DD-plane indexed op (prefix 0xDD) — LD (IX+d),n  (a four-byte instruction):
{ "prefix": "0xDD", "opcode": "0x36", "mnemonic": "LD", "mode": "Indexed", "bytes": 4, "cycles": 19, "pageCrossPenalty": false,
  "source": "Zilog Z80 CPU User Manual (UM0080), p.88, LD (IX+d),n" }

// A DDCB compound row (DD CB dd op) — displacement BEFORE the opcode; the computed-length ModRm marker.
// The "opcode" is the FINAL byte (the op); "prefix" records the compound prefix "0xDDCB"; the displacement
// byte sits between CB and the op (the M3.1b DDCB/FDCB form, 0001-…:117-119). "mode": "Indexed" +
// the prefix marks it; bytes = 4 (DD CB dd op):
{ "prefix": "0xDDCB", "opcode": "0x06", "mnemonic": "RLC", "mode": "Indexed", "bytes": 4, "cycles": 23, "pageCrossPenalty": false,
  "source": "Zilog Z80 CPU User Manual (UM0080), p.222, RLC (IX+d)" }
```

**The prefix-key.** A row's identity is `(prefix, opcode)` (and, for the CB bit ops, the `subfield`
bit-index 0-7 — though for the dataset we encode each `BIT n,r` as its own `opcode` byte, since the
CB plane's 256 bytes already enumerate the (op × bit × target) cross product; the `subfield` field is
reserved for the rare opcode-group case and is null for the Z80 bit plane because each bit op is its
own CB byte). The committed key string for uniqueness + diff is `prefix:opcode` (e.g. `0xED:0xB0`),
with `(none):opcode` for base-plane rows. The recognized prefix tokens are:
`0xCB`, `0xED`, `0xDD`, `0xFD`, `0xDDCB`, `0xFDCB` (the compound forms are a single token — the
displacement byte is *data*, not part of the key, exactly as M3.1b's `DecodedOperands` carries the
length-determining byte separately, `m3b-…:226-253`).

**The `OpcodeEntry` shape after Task 2:**

```csharp
public sealed record OpcodeEntry(
    string  Opcode,            // the FINAL opcode byte "0xNN" (the op within its plane)
    string  Mnemonic,
    string  Mode,
    int     Bytes,
    int     Cycles,            // T-states (chosen — Ground truth B)
    bool    PageCrossPenalty,  // always false for the Z80 (recorded)
    string? Source = null,
    string? Prefix = null,     // NEW: "0xCB"/"0xED"/"0xDD"/"0xFD"/"0xDDCB"/"0xFDCB"; null = base plane
    int?    SubField = null);  // NEW: reserved for opcode-group encodings; null for the Z80 bit plane

/// <summary>The plane-qualified identity used for uniqueness + the cross-source diff.
/// Base plane: "0xNN". Prefixed: "0xPREFIX:0xNN" (e.g. "0xED:0xB0"). This is what makes
/// ED B0 (LDIR) distinct from 0xB0 (OR B) — the single-byte key cannot.</summary>
public string Key => Prefix is null ? Opcode : $"{Prefix}:{Opcode}";
```

> **Why `prefix` is a separate field, not baked into `opcode`.** The single-byte `opcode` field maps
> directly to the M3.1b `Insn(prefix, opcode, …)` overload (`Spec.cs:18`) — the emitter passes
> `entry.Prefix` and `entry.Opcode` straight through. Concatenating them into `opcode` would force the
> emitter to re-split and would break the 6502 rows' byte-identity (their `opcode` stays a bare
> `0xNN`). Separate fields keep the 6502 shape intact (null prefix) and map 1:1 to the DSL. Recorded.

---

## Ground truth B — cycles: T-states (chosen), and the no-page-cross-penalty fact

**The choice: T-states.** The 6502 `cycles` field is "machine cycles" = T-states for the 6502 (one
clock per bus access). The Z80 has a two-level timing model — **M-cycles** (machine cycles, the bus
transactions) and **T-states** (clock periods; an M-cycle is 3-6 T-states). ADR risk #5 flags this
ambiguity explicitly (`0001-…:704-707`): the 6502's "one micro-op = one cycle" does not map cleanly to
the Z80. **M3.3 records `cycles` as T-states** (the total T-state count for the instruction), because:

- T-states are the unit the TomHarte Z80 vectors measure (per-cycle = per-T-state); choosing T-states
  now makes the M3.4 vector comparison a direct field match rather than a conversion.
- T-states are a single integer per instruction (the documented "total clocks"), matching the
  dataset's one-`int`-per-row shape; M-cycles would need a per-M-cycle breakdown the schema does not
  carry (and which is M3.4's per-cycle-bus-trace concern, not the dataset's).
- The Zilog manual tabulates both M-cycles and T-states; the T-state total is the unambiguous scalar.

**Recorded in the dataset README + the schema doc-comment:** `cycles` = **T-states (total clock
periods), documented form**. The conditional instructions (`JR cc`, `CALL cc`, `RET cc`, `DJNZ`,
block-op repeat) have **two T-state counts** (taken/not-taken, or per-iteration vs final); the dataset
records the **base (not-taken / single-iteration) T-state count** and the variable extra is an M3.4
concern (the interpreter computes the taken penalty — analogous to how the 6502 branch +1 is
interpreter logic). A row whose T-state count is conditional carries a `source` note
(`"… JR cc taken=12 not-taken=7; dataset records not-taken base"`). **This is documented as a known
structural limitation of the M3.3 dataset, surfaced for M3.4.**

**No page-cross penalty.** The Z80 has no 6502-style page-cross +1 cycle (J5, `0001-…:509`). Every
Z80 row sets `pageCrossPenalty: false`. The field is retained (the schema is shared with the 6502 and
the loader requires it) but is uniformly false — recorded, and a loader validation MAY assert it
(Task 2 records whether to enforce `pageCrossPenalty == false` for Z80 rows or merely accept it; the
conservative choice is to ACCEPT, since the field is 6502-shaped and forcing it false is a Z80-policy
assertion the dataset loader should not bake — recorded as a Task-2 judgement call).

---

## Ground truth C — the cross-source-diff reconciliation protocol (the headline anti-hallucination gate)

**This is the load-bearing section.** The dataset is LLM-extracted at ~1100 rows; the diff is where
hallucinations, wrong cycle counts, and mis-assigned prefixes surface. The committed dataset is the
**reconciled result of two independent extractions**, not a single pass.

### C.1 How the two tables are produced

Two **independent** extractions of the Z80 opcode set, per the runbook's Rung 2
(`extraction-runbook.md:208-218`):

- **Source A — the Zilog Z80 CPU User Manual (UM0080)**, the primary reference. The Stage-1 LLM prompt
  (`extraction-runbook.md:78-180`, with the Z80 vocabulary substituted per Task 6's runbook addendum)
  is run plane-by-plane against the manual's opcode tables to produce `data/z80-opcodes-a.json`.
- **Source B — a second, genuinely independent document.** Options (pick and RECORD in Task 4): a
  different edition, a third-party Z80 datasheet (e.g. the Mostek or NEC second-source manuals), or a
  community Z80 opcode table (the "Z80 Undocumented" / ClrHome / z80.info references) — the key
  requirement is that B is extracted **without reference to A** (a different document, ideally a
  different extraction session) so the two error sets are independent. Produces `data/z80-opcodes-b.json`.

> **The independence requirement is the whole point.** If B is extracted by editing A, the errors
> correlate and the diff catches nothing. The protocol REQUIRES B to be a from-scratch extraction of a
> different source document. Two independent error distributions over the same ground truth: their
> AGREEMENT is strong evidence; their DISAGREEMENT is the review queue. This is the anti-hallucination
> mechanism (`extraction-runbook.md:208-211`).

### C.2 The diff, the keying fix, and what "disagreement" means

`DatasetDiff.Compare` (`DatasetDiff.cs:33-78`) keys on `Opcode` string today — **this collides for the
Z80** (base `0xB0` and `ED B0` would map to the same key, comparing `OR B` against `LDIR`). Task 2
fixes the keying to use the plane-qualified `Key` (`Prefix:Opcode`). After the fix, the diff compares
`mnemonic`/`mode`/`bytes`/`cycles`/`pageCrossPenalty` per plane-qualified key (the `source` field is
intentionally excluded — the two sources cite different manuals; `DatasetDiff.cs:27-29`).

A `--diff` run produces three buckets:
- **Field disagreements** — same `(prefix,opcode)` key, differing `mnemonic`/`mode`/`bytes`/`cycles`.
- **Missing in B** — a key in A absent from B (an opcode one source documents and the other omits — a
  coverage gap, often an undocumented or edition-specific opcode).
- **Extra in B** — the reverse.

**Every entry in all three buckets is a REVIEW QUEUE entry, NOT a pass-or-fail.** Exit 3
(`Program.cs:17, 224`) means "disagreements found — adjudicate them."

### C.3 The adjudication protocol (how a disagreement is resolved)

For each disagreement, in priority order:

1. **The primary manual (Zilog UM0080) is authoritative** for documented behavior. Look up the exact
   table/page (the `source` citation points to it); the manual's stated value wins. Update BOTH
   datasets to agree and re-diff (`extraction-runbook.md:304`).
2. **If the manuals genuinely disagree** (rare — a known erratum, or the cycle count is tabulated
   differently between editions), cite a **THIRD source** (a different community reference, or the
   silicon-derived consensus on z80.info / the "Z80 Undocumented" document) and record the
   three-way resolution in the row's `source` note (`"… cycle count: UM0080 p.X says 19; B's
   datasheet says 19; reconciled 19"`).
3. **The ULTIMATE arbiter is deferred to M3.4's TomHarte vectors.** Where the documented sources are
   ambiguous AND the value is behavioral (a cycle count, a flag effect), the row is committed with the
   best-corroborated value AND a `source` note flagging it `"uncertain — pending TomHarte"`
   (`extraction-runbook.md:270-273`). The TomHarte per-cycle vectors in M3.4 are the final truth;
   M3.3 commits the cross-corroborated best estimate and inventories the residual uncertainty. **This
   is the explicit unverified-pending-TomHarte handoff.**

The committed `data/z80-opcodes.json` is the **reconciled** table (the result of running the protocol
to a clean `--diff` exit 0, with residual genuinely-uncertain rows carrying `source` notes). Sources A
and B are retained in the repo (`data/z80-opcodes-a.json`, `data/z80-opcodes-b.json`) as the
provenance trail — the diff is reproducible.

### C.4 What "structurally validated" guarantees — and what it does NOT

| M3.3 GUARANTEES (structural) | M3.3 does NOT guarantee (behavioral — M3.4) |
|---|---|
| Every row's schema is well-formed (prefix-key, mode, byte rules — Task 2) | That a cycle count is the true per-T-state count |
| Prefix-keys are unique across all seven planes (no `ED B0`/`B0` collision) | That a flag effect (H/P-V/N/X/Y) is correct |
| Each row's mode is in the Z80 vocabulary + bytes are consistent with mode | That `LDIR`'s self-repeat or `DAA`'s correction is right |
| The decode structure is well-formed (the prefixes declare; the compound forms parse) | That the extracted mnemonic matches the silicon's behavior |
| Two independent sources AGREE on every committed row (cross-corroboration) | That the documented sources themselves are error-free |
| The dataset + the M3.1b decoder compile to a valid decode SKELETON (Rung 4) | That the skeleton EXECUTES any instruction correctly (no semantics) |

**The honest one-liner:** M3.3 delivers a **structurally-validated, cross-corroborated, provisional**
Z80 opcode table whose **behavioral correctness is unverified-pending-M3.4-TomHarte**. The diff makes
the cross-corroboration real; the TomHarte gate (M3.4) makes the correctness real. Conflating the two
would be the lie this plan refuses to tell.

---

## Ground truth D — the M3.3 verification ladder (the rungs that CAN be checked now)

The runbook's five-rung ladder (`extraction-runbook.md:191-255`), with M3.3's reachable subset and
the explicit M3.4 deferral:

| Rung | What | M3.3 status |
|---|---|---|
| **1 — Loader validation** (`--validate-only`) | count, prefix-key uniqueness, mode-vocabulary, mode/byte consistency, decode-structure well-formedness, factory-name check | **REACHED** (Task 2 extends the loaders; Task 3/5 runs it over the Z80 data) |
| **2 — Cross-source diff** (`--diff`) | two independent extractions diffed; disagreements -> review queue -> reconciled (Ground truth C) | **REACHED + load-bearing** (Task 4) |
| **3 — CPUGEN diagnostics** | the generator rejects DSL mistakes (a row naming a register not in `Registers`; a malformed `DecodeStructure`) | **REACHED** (Task 7 — the skeleton must build CPUGEN-clean) |
| **4 — End-to-end generator gate** | the importer output feeds the real Roslyn generator with zero compilation errors (`ImporterEndToEndTests`-equivalent for the Z80 skeleton) | **REACHED** (Task 7 — the structural-generation check) |
| **5 — TomHarte / SingleStepTests** | per-instruction, per-cycle truth | **DEFERRED to M3.4** (`extraction-runbook.md:255`, `0001-…:482-490`) — the dataset is unverified-pending-TomHarte at M3.3 close |

**The Rung-1 loader checks the Z80 forces (new vs the 6502):**
- **Prefix-key uniqueness** (not opcode-byte uniqueness) — the `Key` (`Prefix:Opcode`) is unique;
  `ED B0` and `B0` coexist. (Task 2 changes the `seen` set from `Opcode` to `Key`,
  `OpcodeDataset.cs:115,142`.)
- **Z80 mode vocabulary** — `Register`, `RegisterIndirect`, `Indexed`, `ImmediateExtended`,
  `ExtendedAddress`, `IoPort`, `RelativeJump`, `Bit`, plus the shared `Implied`/`Immediate`
  (§"Ground truth E" mode table). Unknown modes still throw (the vocabulary gate is preserved).
- **Mode/byte consistency** — the Z80 byte rules per mode (e.g. `Indexed` under DD/FD = 3 bytes;
  `ImmediateExtended` = 3; a DDCB row = 4 via the computed-length `ModRm` marker the M3.1b seam
  accepts, `OpcodeDataset.cs:149-159`). The compound DDCB/FDCB rows use the `ModRm` computed-length
  path (the byte-count equality is SKIPPED for them — they carry a declared base; M3.1b's seam,
  `OpcodeDataset.cs:52-55`).
- **Decode-structure well-formedness** — the emitted `DecodeStructure` declares the prefix bytes;
  every prefixed row's `prefix` is one of them (or a recognized compound token); a row whose `prefix`
  is not declared is a malformed row (a loader/CPUGEN error — Task 7's structural gate catches it).

---

## Ground truth E — the semantics map: covered factories + the TODO(vocab) inventory

The Z80 `z80-semantics.json` is authored to the runbook's Schema (`extraction-runbook.md:109-175`):
`architecture`/`namespace`/`specClassName`/`registers`/`mnemonics`. The `mnemonics` map covers ONLY
what today's vocabulary expresses; everything else is **absent from the map** (so the emitter emits
`// TODO(semantics):` for it — `SpecFileEmitter.cs:131-138`).

### E.1 The Z80 mode vocabulary (new `SupportedModes` + `ValidModes` entries)

| Z80 mode | Shape | Bytes (base) | Covered for emission? |
|---|---|---|---|
| `Implied` | no operand (`NOP`, `LDIR`) | 1 (2 with prefix) | shared with 6502 |
| `Immediate` | one operand byte (`LD r,n`) | 2 | shared with 6502 |
| `Register` | register-to-register (`LD r,r'`, `OR r`) | 1 | NEW — EA is a register; emission-supported |
| `RegisterIndirect` | `(HL)`/`(BC)`/`(DE)` | 1 | NEW |
| `Indexed` | `(IX+d)`/`(IY+d)` | 3 (DD/FD prefix + op + d) | NEW — TODO(vocab) for the EA, but the mode declares |
| `ImmediateExtended` | 16-bit immediate (`LD HL,nn`) | 3 | NEW |
| `ExtendedAddress` | `(nn)` 16-bit absolute | 3 | NEW |
| `IoPort` | `(n)`/`(C)` I/O target | 2 | NEW (M3.2 `PortIn`/`PortOut`) |
| `RelativeJump` | `JR`/`DJNZ` PC+d | 2 | NEW — TODO(vocab) for the op |
| `Bit` | bit-index + EA (CB plane) | 2 | NEW — TODO(vocab) for the op |

> **`SupportedModes` vs `ValidModes`.** `OpcodeDataset.ValidModes` ACCEPTS all Z80 modes (dataset
> truth, like the 6502 loader accepts all 13 even when the DSL supported 5 — `m1-…:45`).
> `SpecFileEmitter.SupportedModes` is the subset the DSL/AddrMode can EMIT as a real `Insn` row; a mode
> in `ValidModes` but not `SupportedModes` emits `// TODO(mode):`. M3.3's `SupportedModes` includes the
> modes the M3.1a/b/2 DSL already expresses; `Indexed`/`RelativeJump`/`Bit` may be SupportedModes for
> the SKELETON (they declare fine) with TODO(semantics) for the op — **Task 5 records the exact
> SupportedModes set against what `AddrMode` actually carries post-M3.2** (a real read of the enum at
> implementation time — the table above is the target, the enum is the truth).

### E.2 The covered factories (mapped from §"Derived numbers")

Authored into `z80-semantics.json` `mnemonics` (the honest covered minority):
`LD` (register/immediate/transfer forms -> `Load`/`Transfer`), `ADD`/`ADC`/`SUB`/`SBC`/`AND`/`OR`/
`XOR`/`CP` (8-bit A-register -> `Adc`/`Sbc`/`And`/`Ora`/`Eor`/`Compare` — with a per-mnemonic note that
the Z80 H/N flag deltas are TODO(vocab); the op shape overlaps), `INC`/`DEC` (8-bit register ->
`Increment`/`Decrement`), `JP` (-> `Jump`), `CALL` (-> `Jsr`), `RET` (-> `Rts`), `NOP` (-> `[]`),
`HALT` (-> `Halt`), `IN`/`OUT` (immediate forms -> `PortIn`/`PortOut`).

### E.3 The TODO(vocab) inventory (the new Z80 micro-op vocabulary — M3.4, NOT M3.3)

Explicitly inventoried (a section in the dataset README + the runbook addendum), NOT implemented:

- **16-bit ALU:** `ADD/ADC/SBC HL,rr`, `INC/DEC rr` (the latter set NO flags — a Z80 quirk the M3.4
  vocabulary must not "helpfully" add, `0001-…:347`).
- **Bit group:** `BIT/SET/RES n,r` — needs a bit-index operand (no slot in today's op records,
  `0001-…:352-353`).
- **Rotate/shift family:** `RLC/RRC/RL/RR/SLA/SRA/SRL` + `RLCA/RRCA/RLA/RRA` + `RLD/RRD`
  (`0001-…:354-357`).
- **Block ops:** `LDIR/LDDR/CPIR/CPDR/INIR/OTIR/…` + the single-step `LDI/CPI/…` — self-repeating
  (`0001-…:358-361`).
- **Exchange:** `EX DE,HL`, `EX AF,AF'`, `EXX`, `EX (SP),HL` (`0001-…:362-363`).
- **Indexed EA:** the `(IX+d)`/`(IY+d)` effective-address computation (`0001-…:337`).
- **Conditional flow:** `JP cc,nn`, `CALL cc,nn`, `RET cc`, `JR`/`JR cc`, `DJNZ`, `RST n`
  (`0001-…:364-366`).
- **16-bit stack:** `PUSH rr`/`POP rr` (the pair forms; today's `Push`/`Pull` are 8-bit).
- **I/O `(C)`-indexed:** `IN r,(C)`/`OUT (C),r` beyond the M3.2 immediate-port primitive.
- **Misc:** `DAA` (needs `H`/`N`), `CPL`, `NEG`, `SCF`/`CCF`, `DI`/`EI`, `IM 0/1/2`, `LD A,I`/`LD A,R`
  (copy `R`/`I`, set `P/V` from `IFF2`) (`0001-…:367-369`).
- **The flag-model micro-ops:** `SetSZ`/`SetParity`/`SetHalfCarry`/`SetXY`/`SetOverflow`/`SetAddSub`
  (`0001-…:301-308`).

**`FactoryArity` is UNCHANGED in M3.3** — no new factories are added (they are M3.4 vocabulary). The
covered Z80 mnemonics map to the EXISTING factories. This keeps M3.3 strictly a dataset+loader+skeleton
chunk; the vocabulary growth is M3.4. (Recorded: if a covered mnemonic needs only a factory the 6502
already has, it is used as-is; nothing new is added to `FactoryArity`.)

---

## Ground truth F — the Z80 register/flag DECLARATIONS (data for the skeleton)

The `registers` array in `z80-semantics.json` (consumed by the M3.1a data-driven register file; the
emitter writes the `RegisterDef[]` table, `SpecFileEmitter.cs:92-105`). DECLARATIONS only — the
pair-aliasing and 16-bit arithmetic are M3.4.

```jsonc
"registers": [
  { "name": "A",  "bits": 8 },
  { "name": "F",  "bits": 8,  "role": "Status" },
  { "name": "B",  "bits": 8 }, { "name": "C", "bits": 8 },
  { "name": "D",  "bits": 8 }, { "name": "E", "bits": 8 },
  { "name": "H",  "bits": 8 }, { "name": "L", "bits": 8 },
  // the alternate set — declared as eight more 8-bit Generals (EX/EXX swap them — M3.4 ops):
  { "name": "A_", "bits": 8 }, { "name": "F_", "bits": 8 },
  { "name": "B_", "bits": 8 }, { "name": "C_", "bits": 8 },
  { "name": "D_", "bits": 8 }, { "name": "E_", "bits": 8 },
  { "name": "H_", "bits": 8 }, { "name": "L_", "bits": 8 },
  // the special 8-bit registers (R-refresh increment is an M3.4 fetch side effect):
  { "name": "I",  "bits": 8 }, { "name": "R", "bits": 8 },
  // the 16-bit registers:
  { "name": "IX", "bits": 16 }, { "name": "IY", "bits": 16 },
  { "name": "SP", "bits": 16, "role": "StackPointer" },
  { "name": "PC", "bits": 16, "role": "ProgramCounter" }
]
```

> **The pairs `BC`/`DE`/`HL`/`AF` are NOT separate register declarations.** Per ADR Decision 3
> option (A) (`0001-…:258-265`), the 8-bit halves are the STORAGE; the 16-bit pairs are a GENERATED
> VIEW the M3.4 work synthesizes (`B`/`C` are declared; `BC` is a computed accessor). M3.3 declares the
> halves only — the pair-view synthesis is M3.4 (it needs the `RegisterDef` pair-relationship the DSL
> does not yet express, `0001-…:265`). For the SKELETON, the registers declared above are sufficient:
> the skeleton's covered ops name 8-bit registers (`LD B,n`, `OR C`) and the 16-bit registers that ARE
> declared (`IX`/`IY`/`SP`/`PC`). A row naming a pair (`LD HL,nn`) is TODO(vocab) until M3.4 adds the
> pair view. **Recorded: M3.3 declares the halves + the genuine-16-bit registers; the pair aliasing is
> M3.4.** The skeleton compiles with these registers; the pair ops are TODO.

**The flag model** — declared as a `Status` register `F` (8-bit). The Z80 flag bit layout
`S(7) Z(6) Y(5) H(4) X(3) P/V(2) N(1) C(0)` is the M3.4 flag-MODEL concern (the `Flag` enum / the
composable flag micro-ops, `0001-…:301-308`); M3.3 declares `F` as the `Status` register so the
skeleton has a flag word, and records the bit layout in the README/runbook addendum for M3.4 to
consume. **M3.3 does NOT author flag micro-ops** (none are covered — every Z80 flag effect is
TODO(vocab), §"Ground truth E.3"). The covered ops that DO set flags (the 8-bit ALU) carry a
per-mnemonic note that their Z80 flag deltas are TODO(vocab); the `Adc`/`Sbc`/etc. factories set the
6502 flag convention, which is WRONG for the Z80 (different H/P-V/N behavior) — so even the "covered"
ALU ops are **covered-for-decode-shape, TODO-for-flags**, and the skeleton's purpose is the DECODE
skeleton, not flag-correct execution. **Recorded sharply: the skeleton is NOT flag-correct; flag
correctness is M3.4/TomHarte.**

---

## Ground truth G — the structural-generation check (the skeleton that proves the decode structure)

**The deliverable:** the importer EMITS a Z80 spec SKELETON (`Z80Spec.cs`) that COMPILES through the
Roslyn generator producing a **valid decode skeleton** — proving the dataset + the M3.1b generic
decoder accommodate the Z80's seven-plane prefix structure end-to-end, *even with semantics deferred*.

### G.1 What the skeleton contains

- The `[CpuSpecification("z80")]` class with the `RegisterDef[]` table (§"Ground truth F").
- A **`DecodeStructure` declaration** (`DecodeStructure.cs:9`) declaring the prefix bytes:
  ```csharp
  public static readonly DecodeStructure Decode = new(
      Prefixes: [new PrefixByte(0xCB), new PrefixByte(0xED),
                 new PrefixByte(0xDD), new PrefixByte(0xFD)],
      ModRmOpcodes: [],          // the DDCB/FDCB compound forms — see the compound-prefix note
      SubFieldOpcodes: []);      // the Z80 bit plane enumerates each bit op as its own CB byte (no sub-field)
  ```
  > **The DDCB/FDCB compound form.** `DD CB dd op` is the displacement-then-opcode form M3.1b's
  > computed-length `ModRm` marker + the compound-prefix token handle. The emitter expresses a DDCB row
  > as a prefixed `Insn` keyed on the compound prefix token; the displacement byte's position (before
  > the opcode) is the M3.1b decode-walk's concern (the walk consumes `DD CB dd op` and computes
  > length 4). **M3.3 declares the compound forms in the dataset (prefix `0xDDCB`/`0xFDCB`) and emits
  > them as prefixed rows; the WALK that consumes the mid-stream displacement is the M3.1b mechanism
  > already shipped.** Task 7 records exactly how the compound prefix maps to the `DecodeStructure` /
  > `Insn` surface — does the shipped `DecodeStructure` express a two-byte prefix (`DD CB`), or does the
  > emitter encode `DDCB` as a single synthetic prefix token? **This is the one place M3.3 may surface a
  > genuine M3.1b-mechanism gap — if the shipped `Prefixes` model cannot express a two-deep prefix, that
  > is an ENUMERATED FINDING per §9-item-10 discipline (`framework-design…:261-267`,
  > `0001-…:35`), fed to M3.4, NOT a silent workaround.** Task 7 Step 0 reads the shipped
  > `DecodeStructure`/`PrefixByte`/parser to determine which path is available BEFORE authoring the
  > skeleton.
- The `InstructionDef[] Instructions` collection: each dataset row emits either a real prefixed
  `Insn(prefix, opcode, "MNEMONIC", AddrMode.Mode, [ops])` (covered) or a `// TODO(semantics):` /
  `// TODO(mode):` comment (the TODO majority — §"Derived numbers").

### G.2 The structural gate (Rung 4 for the Z80)

A test (`Z80SkeletonEndToEndTests`, mirroring `ImporterEndToEndTests`) runs the engine on the
committed Z80 data files, takes the emitted skeleton source, appends a MINIMAL hand-written partial
(a `Z80Cpu` stub providing `ReadBus`/`WriteBus`/the M3.2 I/O-bus wiring/`HandleUndefinedOpcode`/ctor —
NO real semantics), and pushes it through `GeneratorTestHost.Run` -> asserts **zero generator
diagnostics and zero compilation errors**. This proves:

- The generic decoder (M3.1b) accepts a seven-plane prefix structure declared via `DecodeStructure`.
- The prefixed `Insn` rows parse and emit a valid descriptor skeleton (per-plane tables — `0001-…:163`).
- The Z80 register declarations (incl. the alternate set + I/R + IX/IY) feed the M3.1a data-driven
  register file and generate a valid state struct.
- **The end-to-end pipeline (dataset -> semantics -> emitted skeleton -> generator -> compilation) is
  Z80-clean** — the structural guarantee, with semantics deferred.

> **What the structural gate PROVES vs what it does NOT.** It PROVES the dataset + decoder + register
> file accommodate the Z80's STRUCTURE (the decode skeleton compiles). It does NOT prove any
> instruction EXECUTES (the semantics are mostly TODO — the generated interpreter bodies for covered
> ops exist but are NOT flag-correct, §"Ground truth F"; the TODO rows have no body). Execution
> correctness is M3.4 + TomHarte. The gate is the STRUCTURAL end of the ladder (Rung 4); Rung 5 is
> M3.4. Recorded.

---

## Ground truth H — the no-6502-change invariant (the regression guard)

The 6502 dataset/semantics/spec/tests are UNTOUCHED and green. Concretely:

| 6502 artifact | Changes? | Why |
|---|---|---|
| `data/mos6502-opcodes.json` | **NO** — byte-identical | the loader extensions are additive (null prefix = 6502 shape) |
| `data/mos6502-semantics.json` | **NO** | the Z80 semantics is a SEPARATE file |
| `src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs` | **NO** — byte-identical | `RegeneratedSpecTests` anchor holds |
| The 6502 emission (33 real rows, the TODO inventory) | **NO** | `SpecFileEmitterTests` derivation pin holds |
| `OpcodeEntry` 6502 rows load + validate | **YES, byte-for-byte** (the regression GUARD) | Task 2's `Existing_6502_rows_still_validate` test |
| `DatasetDiff` over two 6502 datasets | **NO behavior change** | the `Key` for a 6502 row is its bare `Opcode` (null prefix) — identical to today |
| The 6502 TomHarte/Klaus sweeps | **NO** | M3.3 adds no execution path |

**The loader extensions are STRICTLY ADDITIVE.** Every change in Task 2 (the `prefix`/`subfield`
fields, the `Key`-based uniqueness + diff, the Z80 modes) is gated so the 6502 path is unchanged: a
null `prefix` => `Key == Opcode` => the 6502 uniqueness + diff are byte-identical; the Z80 modes are
ADDED to `ValidModes`/`SupportedModes`, the 6502 modes' byte rules are UNCHANGED. The headline
regression guard is `Existing_6502_rows_still_validate_byte_for_byte` (Task 2) +
`RegeneratedSpecTests` (the 6502 spec is byte-identical) — both must stay green at every task gate.

---

## File structure

```
tools/CpuEmulator.SpecImporter/
    OpcodeDataset.cs            — MODIFY (prefix/subfield fields; Key-based uniqueness; Z80 modes + byte rules)
    DatasetDiff.cs              — MODIFY (key on the plane-qualified Key, not the bare Opcode)
    SemanticsMap.cs             — MODIFY (no FactoryArity change; the Z80 register/flag declarations load as data)
    SpecFileEmitter.cs          — MODIFY (Z80 SupportedModes; emit prefixed Insn(prefix,opcode,…); emit DecodeStructure)
    data/z80-opcodes.json       — NEW (the committed, reconciled Z80 dataset — ~1100 rows)
    data/z80-opcodes-a.json     — NEW (Source A extraction — the provenance trail)
    data/z80-opcodes-b.json     — NEW (Source B independent extraction — the diff input)
    data/z80-semantics.json     — NEW (register/flag declarations + the covered-mnemonic map)
    README.md                   — MODIFY (Z80 provenance/attribution; the T-state choice; the TODO(vocab) inventory)
docs/user-guide/
    extraction-runbook.md       — MODIFY (Task 8: the worked-Z80-example / lessons addendum — the §8 feature-docs gate)
src/CpuEmulator.Cpus.Z80/       — NEW (the generated skeleton's home)
    Z80Spec.cs                  — NEW (the emitted skeleton — committed for review; regenerable)
    Z80Cpu.cs                   — NEW (the MINIMAL hand-written partial: ReadBus/WriteBus/IO wiring/ctor stub — NO semantics)
    CpuEmulator.Cpus.Z80.csproj — NEW
tests/CpuEmulator.Tests/Importer/
    OpcodeDatasetTests.cs       — MODIFY (prefix-key uniqueness; Z80 modes; 6502 byte-for-byte guard)
    DatasetDiffTests.cs         — MODIFY (the Key-based diff; the ED-B0/B0 non-collision)
    SemanticsMapTests.cs        — MODIFY (the Z80 register declarations load; no new factories)
    SpecFileEmitterTests.cs     — MODIFY (prefixed-Insn emission; DecodeStructure emission; the Z80 covered/TODO counts)
    Z80SkeletonEndToEndTests.cs — NEW (the structural-generation gate — Rung 4 for the Z80)
    data fixtures               — NEW (small Z80 diff fixtures for the cross-source-diff tests)
```

---

## Task 0: Baseline + the loader-shape recon (NO code change)

> Establish the exact green baseline and READ the shipped M3.1b/M3.2 surfaces the Z80 will consume,
> BEFORE touching code — so the extension is a known, bounded edit set, and the one risk (the DDCB
> two-deep prefix expressibility, Ground truth G.1) is surfaced early as a finding, not a mid-task
> surprise. Mirrors the M3.1b Task 0.

- [ ] **Step 1: Branch check** — `git branch --show-current` -> `feat/m3-z80-extraction` (base
  `main`, head `fc76db4` — the M3.2 merge). This plan file is the preparatory doc commit on it.
- [ ] **Step 2: Confirm the green baseline** — `dotnet test` (routine suite, excl. the heavy Klaus/
  full-TomHarte sweeps). **Record the EXACT test count** (the brief says ~1491; pin the real number —
  the per-task estimates are relative to it). Confirm 0 failures, 0 unexpected skips. Record
  `dotnet build --no-incremental -warnaserror` is clean.
- [ ] **Step 3: The loader-shape recon (read, do not edit)** — read and record in the closeout:
  - `OpcodeDataset.cs` — the `OpcodeEntry` record (`:12-19`), the `OpcodeFormat` single-byte regex
    (`:61-62`), the `seen`-on-`Opcode` uniqueness (`:115,142`), the `FixedLengthModes`/`ComputedLengthModes`/
    `ValidModes` (`:39-59`), the `ModRm` computed-length seam (`:52-55,149-159`).
  - `DatasetDiff.cs` — the `Opcode`-keyed `ToDictionary` (`:35-36`) that MUST become `Key`-keyed.
  - `SemanticsMap.cs` — `FactoryArity` (`:44-82` — UNCHANGED in M3.3), the `RegisterConfig` load path
    (`:11-14,170`).
  - `SpecFileEmitter.cs` — `SupportedModes` (`:41-47`), the `Insn(...)` emission (`:120-121` — the
    single-byte form; M3.3 adds the prefixed form), the registers-table emission (`:92-105`).
  - **The shipped DSL surface the Z80 consumes:** `DecodeStructure.cs` (`DecodeStructure(PrefixByte[]
    Prefixes, byte[] ModRmOpcodes, byte[] SubFieldOpcodes)`, `:9-14`), `Spec.cs` Insn overloads
    (`Insn(byte prefix, byte opcode, …)` `:18`; `Insn(byte opcode, int subfield, …)` `:24`),
    `InstructionDef.cs` (`Prefix`/`SubField`/`KeyShape` carriers, `:15-22`), the M3.2 `PortIn`/`PortOut`/
    `Halt` factories (`Spec.cs:66-70`), and `AddrMode.cs` (read the ACTUAL enum members post-M3.2 — the
    SupportedModes target table in Ground truth E.1 is reconciled against this read).
  - **The DDCB recon (the one real risk — Ground truth G.1):** read `SpecParser`'s `DecodeStructure`
    parsing + the `Prefixes` model to determine whether a TWO-byte prefix (`DD CB`) is expressible, or
    whether DDCB must be a single synthetic prefix token. **Record the finding.** If the shipped model
    cannot express the compound form structurally, that is an ENUMERATED §9-item-10 finding fed to M3.4
    — note it in the plan (Task 7 references this recon) and DO NOT invent a workaround silently.
- [ ] **Step 4:** No commit (read-only). Proceed to Task 1.

---

## Task 1: The Z80 dataset schema + the prefix-key format (TDD)

> Maps to Ground truth A. Extend `OpcodeEntry` with the optional `prefix`/`subfield` fields + the
> plane-qualified `Key`, and the loader to accept a prefixed opcode row — WITHOUT changing the 6502
> path (a null prefix is byte-identical to today). This is the dataset-shape task; Task 2 lands the
> uniqueness + mode-vocabulary + diff-keying that depend on `Key`.

**Files:** `OpcodeDataset.cs` (the `OpcodeEntry` + DTO + `Key`); `tests/.../Importer/OpcodeDatasetTests.cs`.

- [ ] **Step 1: Failing tests** (`OpcodeDatasetTests`):
  - `Base_plane_row_has_null_prefix_and_Key_equals_Opcode` — a `{opcode:"0xB0", mnemonic:"OR", …}`
    row loads with `Prefix == null`, `SubField == null`, `Key == "0xB0"` (the 6502/base shape).
  - `Prefixed_row_carries_prefix_and_plane_qualified_Key` — a `{prefix:"0xED", opcode:"0xB0",
    mnemonic:"LDIR", …}` row loads with `Prefix == "0xED"`, `Key == "0xED:0xB0"`.
  - `Compound_prefix_token_is_accepted` — a `{prefix:"0xDDCB", opcode:"0x06", …}` row loads with
    `Prefix == "0xDDCB"`, `Key == "0xDDCB:0x06"` (the DDCB compound form, Ground truth A).
  - `Prefix_must_be_a_recognized_token` — a `{prefix:"0xZZ", …}` row throws `InvalidDataException`
    (the prefix vocabulary gate: `0xCB`/`0xED`/`0xDD`/`0xFD`/`0xDDCB`/`0xFDCB` only).
  - `Existing_6502_rows_still_validate_byte_for_byte` (the regression GUARD — Ground truth H) — the
    real `mos6502-opcodes.json` loads unchanged, every row `Prefix == null`.
- [ ] **Step 2: Extend `OpcodeEntry` + the DTO** — add `string? Prefix`, `int? SubField` (the Ground
  truth A literal); add the `Key` computed property; add a `RecognizedPrefixes` set + a `prefix`-format
  validation (the same `0xNN`/compound-token shape). The 6502 rows omit both fields (null) — the DTO's
  `[JsonUnmappedMemberHandling(Disallow)]` (`OpcodeDataset.cs:75`) still holds; `prefix`/`subfield` are
  now MAPPED members (optional, nullable).
- [ ] **Step 3: Tests pass; full suite green** (the 6502 path is byte-identical — the headline guard).
  **Commit** —

  ```
  feat(importer): Z80 prefix-keyed dataset schema (prefix/subfield/Key); 6502 rows byte-identical

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

**New-test estimate:** ~6.

---

## Task 2: Prefix-key uniqueness + Z80 mode vocabulary + the diff keying fix (TDD)

> Maps to Ground truth A/D/H. The uniqueness check moves from `Opcode` to `Key` (so `ED B0` and `B0`
> coexist); the Z80 modes are ADDED to `ValidModes`; the mode/byte rules cover the Z80 modes; and
> `DatasetDiff` keys on `Key` (the ED-B0/B0 non-collision). All strictly additive — the 6502 rules are
> UNCHANGED.

**Files:** `OpcodeDataset.cs` (the `seen`-on-`Key`; the Z80 modes + byte rules); `DatasetDiff.cs` (the
`Key`-keyed dictionaries); `tests/.../Importer/OpcodeDatasetTests.cs`, `DatasetDiffTests.cs`.

- [ ] **Step 1: Failing tests** (`OpcodeDatasetTests` + `DatasetDiffTests`):
  - `ED_B0_and_base_B0_coexist` — a dataset with both `{prefix:"0xED",opcode:"0xB0",…}` and
    `{opcode:"0xB0",…}` loads WITHOUT a duplicate error (distinct `Key`s).
  - `Duplicate_plane_qualified_key_still_throws` — two `{prefix:"0xED",opcode:"0xB0",…}` rows throw
    (uniqueness is on `Key`, not on `Opcode`).
  - `Z80_modes_are_accepted` — `Register`/`RegisterIndirect`/`Indexed`/`ImmediateExtended`/
    `ExtendedAddress`/`IoPort`/`RelativeJump`/`Bit` load without an "unknown mode" error
    (table-driven against the Ground truth E.1 set).
  - `Z80_mode_byte_rules_enforced` — e.g. `ImmediateExtended` = 3 bytes throws on a 2-byte row;
    `Register` = 1 byte (table-driven for the fixed-length Z80 modes).
  - `DDCB_row_uses_computed_length_marker` — a DDCB row (4 bytes) is accepted via the `ModRm`
    computed-length seam (the byte-count equality SKIPPED — `OpcodeDataset.cs:149-159`); a malformed
    fixed-length Z80 row still throws.
  - `Unknown_mode_still_throws` — a genuinely unknown mode throws (the vocabulary gate preserved).
  - `Diff_keys_on_plane_qualified_Key` (`DatasetDiffTests`) — two datasets each with `ED B0`=LDIR and
    base `B0`=OR; the diff reports ZERO disagreements (the keys match per plane), NOT a spurious
    `OR`-vs-`LDIR` mnemonic disagreement (the collision the bare-`Opcode` keying would produce).
  - `Diff_over_two_6502_datasets_is_unchanged` (the regression GUARD) — the existing 6502 diff
    behavior is byte-identical (a 6502 row's `Key` is its bare `Opcode`).
- [ ] **Step 2: Move uniqueness to `Key`** (`OpcodeDataset.cs:115,142` — `seen.Add(d.Key)` over the
  computed key); add the Z80 modes to `FixedLengthModes`/`ValidModes` + the per-mode byte rules in
  `ExpectedBytes` (the Z80 fixed-length modes); the `Indexed`/compound modes route through the existing
  `ModRm`/computed-length seam where their length is prefix-determined. Record the `pageCrossPenalty`
  judgement call (Ground truth B: ACCEPT, do not enforce-false).
- [ ] **Step 3: Fix `DatasetDiff` keying** (`DatasetDiff.cs:35-36`) — `ToDictionary(e => e.Key, …)`
  over the plane-qualified key; the `FieldDisagreement.Opcode` field now carries the `Key` (so the
  printed table shows `0xED:0xB0`). Update the diff's `missing`/`extra`/`common` key sets accordingly.
- [ ] **Step 4: Full suite green; the 6502 guards hold** (`Existing_6502_rows_still_validate`,
  `Diff_over_two_6502_datasets_is_unchanged`, `RegeneratedSpecTests`). **Commit** —

  ```
  feat(importer): prefix-key uniqueness + Z80 mode vocabulary + Key-based cross-source diff

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

**New-test estimate:** ~9.

---

## Task 3: The Z80 extraction (Source A) — run the runbook, plane by plane

> Maps to Decision 6 + Ground truth A/B. This is a PROCESS task (the runbook's Stage 1), gated by the
> Rung-1 loader (Task 2's validation). NO unit tests are added here — the artifact is the data file,
> validated by `--validate-only` and (Task 4) the diff. The "test" is the ladder.

- [ ] **Step 1: Run the Stage-1 LLM prompt plane by plane** (`extraction-runbook.md:78-180,184-186`)
  against the Zilog Z80 CPU User Manual (UM0080), one prefix plane per pass (base, then CB, ED, DD, FD,
  DDCB, FDCB — `extraction-runbook.md:185` "paste one section at a time"). Each row gets a `source`
  citation (the manual page + table) per the §1.4 convention (`extraction-runbook.md:257-275`).
  Cycles = **T-states** (Ground truth B); conditional rows get the not-taken base + a `source` note.
- [ ] **Step 2: Assemble `data/z80-opcodes-a.json`** — the merged seven-plane table. **Record the
  DD/FD enumeration policy** (Ground truth "Derived numbers": the documented subset where DD/FD
  genuinely changes the operation — the `HL`/`(HL)`/`H`/`L`-touching rows — NOT a full base-plane
  re-enumeration). **Record the exact row count** (the realistic ~1100 target — pin the actual).
- [ ] **Step 3: Rung 1 — `--validate-only` over Source A:**

  ```
  dotnet run --project tools/CpuEmulator.SpecImporter -- --validate-only \
    --dataset   tools/CpuEmulator.SpecImporter/data/z80-opcodes-a.json \
    --semantics tools/CpuEmulator.SpecImporter/data/z80-semantics.json
  ```

  Exit 0 = the schema is clean (count, prefix-key uniqueness, modes, byte rules). Fix any schema error
  (a wrong byte count, an unknown mode, a duplicate key, an unrecognized prefix) and re-run until exit
  0. (The `z80-semantics.json` from Task 6 is needed here; if Task 6 has not landed, a minimal
  registers-only semantics stub suffices for the dataset's own Rung-1 — record the sequencing.)
- [ ] **Step 4: Commit Source A** (the provenance trail; the reconciled file comes after the diff) —

  ```
  feat(data): Z80 opcode dataset extraction — Source A (Zilog UM0080), Rung-1 clean

  ~N rows across base+CB+ED+DD+FD+DDCB+FDCB. T-state cycles. Provisional —
  unverified-pending-M3.4-TomHarte. Source-B cross-source diff is the next gate.

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

**New-test estimate:** 0 (process task; gated by the Rung-1 loader).

---

## Task 4: The cross-source diff (Source B) + reconciliation — the headline gate

> Maps to Ground truth C (the load-bearing reconciliation protocol). Extract Source B independently,
> diff against A, work the review queue to a clean exit 0, and commit the RECONCILED table. This is the
> anti-hallucination gate; it does the heavy lifting at ~1100 rows (`0001-…:478-481`).

- [ ] **Step 1: Extract Source B independently** (Ground truth C.1) — a DIFFERENT document
  (record which: a second-source manual or a community opcode table), extracted WITHOUT reference to
  A. Produce `data/z80-opcodes-b.json`. Rung-1 clean it too (`--validate-only`).
- [ ] **Step 2: Run the diff** (Rung 2, `extraction-runbook.md:212-218`):

  ```
  dotnet run --project tools/CpuEmulator.SpecImporter -- \
    --dataset tools/CpuEmulator.SpecImporter/data/z80-opcodes-a.json \
    --diff    tools/CpuEmulator.SpecImporter/data/z80-opcodes-b.json
  ```

  Exit 3 = disagreements (the review queue — expected on the first pass over ~1100 rows). The printed
  table is keyed on the plane-qualified `Key` (Task 2). **Record the disagreement count** (the honest
  first-pass error rate — a finding for the runbook addendum, Task 8).
- [ ] **Step 3: Work the review queue** (the C.3 adjudication protocol) — for each disagreement: the
  Zilog manual is authoritative; a genuine manual-vs-manual conflict cites a third source; a residual
  behavioral ambiguity is committed with the best-corroborated value + a `"uncertain — pending
  TomHarte"` `source` note. Update BOTH A and B to agree; re-diff until **exit 0**.
- [ ] **Step 4: Produce the committed reconciled `data/z80-opcodes.json`** — equal to the
  reconciled A (== reconciled B after the protocol). Generate the review report (Stage 2,
  `extraction-runbook.md:285-293`):

  ```
  dotnet run --project tools/CpuEmulator.SpecImporter -- --validate-only \
    --dataset   tools/CpuEmulator.SpecImporter/data/z80-opcodes.json \
    --semantics tools/CpuEmulator.SpecImporter/data/z80-semantics.json \
    --diff      tools/CpuEmulator.SpecImporter/data/z80-opcodes-b.json \
    --review-report tools/CpuEmulator.SpecImporter/data/z80-review.md
  ```

  The report's Disagreements section MUST be empty (exit 0); Provenance Coverage records the cited
  fraction; Missing Semantics records the TODO majority (Ground truth E). **Record the
  provenance-coverage fraction** (target high; partial is acceptable with noted gaps —
  `extraction-runbook.md:316`).
- [ ] **Step 5: Commit the reconciled dataset + B + the review report** —

  ```
  feat(data): Z80 dataset reconciled via cross-source diff (Source A x Source B), exit 0

  N disagreements adjudicated (Zilog UM0080 authoritative; residual behavioral
  ambiguities noted "uncertain — pending TomHarte"). Cross-corroborated, structurally
  validated, PROVISIONAL. Behavioral correctness unverified-pending-M3.4-TomHarte.

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

**New-test estimate:** 0 (process task; the diff TOOL is tested in Task 2; this task EXERCISES it).

---

## Task 5: The Z80 register/flag declarations + semantics map + the covered/TODO emission (TDD)

> Maps to Ground truth E/F + the emitter extension. Author `z80-semantics.json` (the register/flag
> declarations + the covered-mnemonic map), extend `SpecFileEmitter` to emit the prefixed `Insn` rows
> + the `DecodeStructure` declaration, and PIN the honest covered-vs-TODO split.

**Files:** `data/z80-semantics.json` (NEW); `SpecFileEmitter.cs` (Z80 `SupportedModes`; prefixed-`Insn`
emission; `DecodeStructure` emission); `SemanticsMap.cs` (the Z80 registers load — no `FactoryArity`
change); `tests/.../Importer/SemanticsMapTests.cs`, `SpecFileEmitterTests.cs`.

- [ ] **Step 1: Author `data/z80-semantics.json`** — the Ground truth F register declarations
  (the 18 8-bit + 4 16-bit registers; `F` as `Status`); `architecture:"z80"`,
  `namespace:"CpuEmulator.Cpus.Z80"`, `specClassName:"Z80Spec"`; the Ground truth E.2 covered-mnemonic
  map (the loads/transfers/8-bit-ALU/basic-flow/IO-immediate). The TODO(vocab) mnemonics are ABSENT
  from the map (so they emit `// TODO(semantics):`).
- [ ] **Step 2: Failing tests** (`SemanticsMapTests`):
  - `Z80_registers_load_as_declared` — the 22 register configs load (the alternate set + I/R + IX/IY/SP/PC);
    `F` carries `role:"Status"`, `SP`/`PC` their roles.
  - `Z80_semantics_uses_only_existing_factories` — every covered mnemonic's ops text validates against
    the UNCHANGED `FactoryArity` (no new factory names — the M3.3 invariant, Ground truth E.3).
- [ ] **Step 3: Failing tests** (`SpecFileEmitterTests`):
  - `Prefixed_row_emits_prefixed_Insn` — an `{prefix:"0xED",opcode:"0xB0","LDIR",…}` row (when covered)
    emits `Insn(0xED, 0xB0, "LDIR", AddrMode.Implied, [...])`; a TODO row emits the comment with the
    plane-qualified key (`// TODO(semantics): 0xED:0xB0 LDIR Implied …`).
  - `Base_row_emits_single_byte_Insn` — a base-plane covered row emits the EXISTING single-byte
    `Insn(0xNN, …)` form (byte-identical to the 6502 emission shape — the 6502 guard).
  - `DecodeStructure_is_emitted` — the emitted skeleton contains the `DecodeStructure Decode = new(...)`
    declaration with the prefix bytes (Ground truth G.1).
  - `Z80_covered_vs_TODO_counts` — the emitted Z80 skeleton's report (`emitted`/`todoSemantics`/
    `todoMode`) matches the EXACT derived split (compute the expectation IN THE TEST by filtering the
    dataset against the map + SupportedModes, then assert it equals the engine's report — the 3a
    derivation-pin pattern, `m1-…:136`). **This pins the honest covered minority + the TODO majority.**
  - `Existing_6502_emission_unchanged` (the regression GUARD) — the 6502 emission (33 real rows) +
    `RegeneratedSpecTests` are byte-identical.
- [ ] **Step 4: Extend `SpecFileEmitter`** — add the Z80 modes to `SupportedModes` (reconciled against
  the Task-0 `AddrMode` read); branch the `Insn` emission on `entry.Prefix` (null -> single-byte form;
  non-null -> `Insn(prefix, opcode, …)`); emit the `DecodeStructure` declaration when the semantics
  map / a config flag indicates a prefixed ISA. The TODO comments carry the plane-qualified key.
- [ ] **Step 5: Full suite green; the 6502 guards hold.** **Record the EXACT covered/TODO counts** in
  the closeout (the honest split — Ground truth "Derived numbers"). **Commit** —

  ```
  feat(importer): Z80 register/flag declarations + prefixed-Insn + DecodeStructure emission

  Covered split pinned: N emitted / M TODO(vocab). 6502 emission byte-identical.

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

**New-test estimate:** ~9.

---

## Task 6: The runbook Z80 vocabulary substitution + the dataset README (docs — TDD-where-testable)

> Maps to Decision 6 + the §8 feature-docs gate. The runbook's Stage-1 prompt template is
> 6502-vocabularied (`extraction-runbook.md:88-154,189`); author the Z80 substitution (the prefix-key
> format, the Z80 modes, the register declarations) so the extraction (Tasks 3-4) uses it, and write
> the Z80 dataset README (provenance, the T-state choice, the TODO(vocab) inventory).

**Files:** `data/README.md` (or the tool README — MODIFY: Z80 section); `extraction-runbook.md` (the
Z80 prompt-vocabulary substitution — the data the Task-3 extraction consumes). *(The runbook's
worked-example / lessons addendum is Task 8 — after the diff's actual findings exist.)*

- [ ] **Step 1: The Z80 prompt-vocabulary block** — a runbook subsection giving the Z80 substitution
  for the Stage-1 template's `<CPU_MANUAL_TEXT>`/mode-list/factory-list slots: the prefix-key row
  format (Ground truth A), the Z80 mode names (Ground truth E.1), the register declarations (Ground
  truth F), and the note that the new Z80 micro-op factories are TODO(vocab) (Ground truth E.3) so the
  LLM does NOT invent them. This is the data Task 3's extraction uses.
- [ ] **Step 2: The dataset README** — the Z80 provenance (Zilog UM0080 + Source B), the
  attribution, the `cycles == T-states` choice (Ground truth B), the conditional-cycle base-only
  limitation, the `pageCrossPenalty == false` note, and the TODO(vocab) inventory (Ground truth E.3 —
  the honest "this is a decode skeleton, not a flag-correct emulator" framing).
- [ ] **Step 3: Verify the docs build / no broken links** (if the repo has a docs-lint; else a manual
  read-through). No unit tests for prose. **Commit** —

  ```
  docs(runbook): Z80 Stage-1 prompt vocabulary + Z80 dataset README (T-state + TODO(vocab) inventory)

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

**New-test estimate:** 0 (docs).

---

## Task 7: The structural-generation check — the Z80 skeleton compiles through the generator (TDD)

> Maps to Ground truth D (Rungs 3-4) + G. Emit the Z80 skeleton, create the `CpuEmulator.Cpus.Z80`
> project + the minimal `Z80Cpu` partial stub, and prove the dataset + the M3.1b decoder + the M3.1a
> register file accommodate the Z80's prefix structure end-to-end (the skeleton COMPILES, zero CPUGEN,
> zero compilation errors). The Z80-half of `ImporterEndToEndTests`.

**Files:** NEW `src/CpuEmulator.Cpus.Z80/{Z80Spec.cs, Z80Cpu.cs, CpuEmulator.Cpus.Z80.csproj}`; NEW
`tests/.../Importer/Z80SkeletonEndToEndTests.cs`.

- [ ] **Step 0: Resolve the DDCB compound-prefix expressibility** (the Task-0 recon finding,
  Ground truth G.1). Confirm how the shipped `DecodeStructure`/`Prefixes`/parser expresses (or cannot
  express) the two-deep `DD CB` prefix. If expressible, author the `DecodeStructure` accordingly; if
  NOT, record the ENUMERATED §9-item-10 finding (the M3.1b mechanism gap), and for M3.3 emit the DDCB
  rows as TODO/skeleton-deferred (the structural gate still proves the base/CB/ED/DD/FD planes; the
  compound forms are a recorded M3.4 input) — DO NOT invent a workaround. **The path taken is a
  recorded decision in the closeout.**
- [ ] **Step 1: Emit the skeleton** — run the engine over the reconciled `z80-opcodes.json` +
  `z80-semantics.json` to produce `src/CpuEmulator.Cpus.Z80/Z80Spec.cs` (committed for review;
  regenerable via the header's command). Create the csproj (mirror `CpuEmulator.Cpus.Mos6502`'s) and
  the minimal `Z80Cpu.cs` partial (the M3.2 two-bus ctor — program/data + the I/O `AddressSpace(Io,16)`
  — `ReadBus`/`WriteBus`/`ReadIo`/`WriteIo`/`HandleUndefinedOpcode` stubs; NO real semantics, NO
  interrupt logic). Add to the solution.
- [ ] **Step 2: Failing tests** (`Z80SkeletonEndToEndTests`, mirroring `ImporterEndToEndTests`):
  - `Z80_skeleton_generates_with_zero_diagnostics` — run the engine on the committed Z80 data files,
    append the minimal partial, push through `GeneratorTestHost.Run` -> `Assert.Empty(result.AllErrors)`
    (zero CPUGEN, zero compilation errors — Rung 3 + Rung 4).
  - `Z80_skeleton_declares_all_registers` — the generated state struct exposes the 22 declared
    registers (a text assertion on the generated output — the M3.1a data-driven register file consumed
    the declarations).
  - `Z80_skeleton_has_per_plane_decode_skeleton` — the generated decode artifact reflects the
    `DecodeStructure` (per-plane tables / the prefixed-key resolver — `0001-…:163`); a spot assertion
    that a prefixed row (e.g. `ED B0`) resolves to a distinct descriptor from base `B0` (the
    prefix-key non-collision proven end-to-end through the generator).
  - `Z80_TODO_rows_emit_as_comments_not_rows` — the TODO(vocab) majority emits as
    `// TODO(semantics):` comments (no `Insn` row), so the skeleton compiles with the covered minority
    as real rows. (Confirms the skeleton is a SKELETON — semantics deferred.)
- [ ] **Step 3: Implement to green** — iterate the skeleton + the minimal partial until the structural
  gate passes (fix any CPUGEN diagnostic the Z80 surfaces — a register-name mismatch, a malformed
  decode struct; each is a real finding recorded). **The build must be CPUGEN-clean
  (`dotnet build 2>&1 | grep CPUGEN` empty — `extraction-runbook.md:230-233`).**
- [ ] **Step 4: Full suite green; the 6502 untouched** (`RegeneratedSpecTests` byte-identical — the
  Z80 is a NEW project, the 6502 generation is unchanged). **Commit** —

  ```
  feat(z80): structural-generation check — Z80 skeleton compiles through the generator (Rungs 3-4)

  Dataset + M3.1b decoder + M3.1a register file accommodate the seven-plane prefix
  structure end-to-end; valid decode skeleton, semantics deferred to M3.4. Behavioral
  correctness unverified-pending-M3.4-TomHarte.

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

**New-test estimate:** ~4.

---

## Task 8: The runbook worked-Z80-example addendum + closeout

> Maps to Decision 6 + the §8 feature-docs gate. After the diff's ACTUAL findings exist (Task 4),
> write the runbook's "worked Z80 example / lessons from the first big extraction" addendum (the
> runbook anticipates the Z80 at `:189,255`), and fill the closeout.

**Files:** `extraction-runbook.md` (the worked-Z80 addendum); this plan's closeout table.

- [ ] **Step 1: The worked-Z80 addendum** — a runbook section paralleling the "Worked Micro-Example"
  (`extraction-runbook.md:320-436`) but for the Z80: the actual disagreement count + a representative
  reconciliation (a real cycle-count or prefix mis-assignment the diff caught), the honest first-pass
  error rate (the finding), the lessons (plane-by-plane extraction; the prefix-key collision the
  keying fix prevents; the T-state choice; the TODO(vocab) majority is expected, not a failure;
  unverified-pending-TomHarte is the honest close-state). Per §9-item-10: framework changes the Z80
  forced (the prefix-key format, the diff keying, any DDCB finding) are recorded as findings.
- [ ] **Step 2: Update the tool/dataset status doc** (the README status line — the Z80 dataset exists,
  is structurally validated + cross-corroborated, behavioral gate is M3.4).
- [ ] **Step 3: Fill the closeout table** (below) with actuals — the exact row count, the
  covered/TODO split, the disagreement count, the provenance fraction, the test count, the DDCB-path
  decision, whether any 6502 file changed (expected: NONE). **Commit** —

  ```
  docs(runbook): worked Z80 extraction addendum + M3.3 closeout (lessons from the first big extraction)

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

- [ ] **Step 4: Final verification + PR** — `dotnet test` (record the count); `dotnet build
  --no-incremental -warnaserror` clean; the 6502 guards (`RegeneratedSpecTests`, the 6502 validate +
  diff) green. NO push until the controller's whole-branch review; PR base `main`.

**New-test estimate:** 0 (docs + closeout).

---

## Plan self-review (completed at write time)

- **Ground truths A–H realized point-by-point.**
  - **(A) the prefix-keyed schema** — `OpcodeEntry` grows `Prefix`/`SubField` + the plane-qualified
    `Key` (Task 1, the GT-A literal); the 6502 rows are null-prefix byte-identical. The six prefix
    tokens (incl. the `DDCB`/`FDCB` compound) are the recognized vocabulary (Task 1).
  - **(B) T-states chosen + no-page-cross** — `cycles == T-states` is documented in the schema
    comment + the README (Task 6); conditional rows carry the not-taken base + a `source` note;
    `pageCrossPenalty` is uniformly false (a recorded Task-2 ACCEPT-not-enforce judgement call).
  - **(C) the cross-source-diff reconciliation protocol** — two independent extractions (A = Zilog
    UM0080, B = a recorded independent source); the diff keys on `Key` (Task 2 fixes the
    `Opcode`-collision); the C.3 adjudication (manual-authoritative -> third-source -> uncertain-pending-
    TomHarte) runs to exit 0 (Task 4); the C.4 guarantees-vs-not table is the honest framing. **This is
    load-bearing, not decorative** — Task 4 is a whole task whose deliverable IS the reconciliation.
  - **(D) the M3.3 verification ladder** — Rungs 1-4 REACHED (loader/diff/CPUGEN/end-to-end);
    Rung 5 (TomHarte) explicitly DEFERRED to M3.4. The "unverified-pending-TomHarte" framing is
    repeated at the header, GT-C.3/C.4, GT-G.2, and every dataset commit message.
  - **(E) covered factories + TODO(vocab)** — the covered minority (loads/transfers/8-bit-ALU/basic-
    flow/IO-immediate) maps to EXISTING factories (`FactoryArity` UNCHANGED — Task 5); the TODO(vocab)
    inventory (16-bit ALU, bit group, rotate/shift family, block ops, EX/EXX, indexed EA, conditional
    flow, the flag micro-ops) is itemized for M3.4. The honest split (~15-25 covered mnemonics /
    ~40-50 TODO; a large TODO-by-row majority from the CB/DDCB/FDCB planes) is PINNED by
    `Z80_covered_vs_TODO_counts` (Task 5, the 3a derivation-pin pattern).
  - **(F) the register/flag declarations** — 18 8-bit (main + alternate + I/R) + 4 16-bit (IX/IY/SP/PC)
    declared as DATA; the pairs are a GENERATED VIEW deferred to M3.4 (the DSL pair-relationship gap,
    `0001-…:265`); `F` declared as `Status` with the bit layout recorded for M3.4; the skeleton is
    explicitly NOT flag-correct (the covered ALU ops are covered-for-decode-shape, TODO-for-flags).
  - **(G) the structural-generation check** — the emitted skeleton + the minimal partial compile
    through the generator (Task 7, zero CPUGEN/compilation errors); the per-plane decode skeleton +
    the prefix-key non-collision are proven end-to-end; the DDCB compound-prefix expressibility is the
    one recorded risk (Task 0 recon -> Task 7 Step 0 resolution, an ENUMERATED finding if the shipped
    mechanism cannot express it — no silent workaround).
  - **(H) the no-6502-change invariant** — every loader change is strictly additive (null prefix =
    6502 shape); the guards (`Existing_6502_rows_still_validate`, `Diff_over_two_6502_datasets_is_
    unchanged`, `Existing_6502_emission_unchanged`, `RegeneratedSpecTests`) hold at every task gate.
- **Scope discipline.** M3.3 = dataset + loaders + skeleton. NO interpreter, NO new micro-op
  vocabulary, NO TomHarte, NO flag micro-ops, NO 6502 refactor — each named in Scope with its ADR
  citation and its future chunk (M3.4). `FactoryArity` is UNCHANGED (the vocabulary growth is M3.4).
- **Honest derivations (the brief's requirement).** The Z80 count is derived per-plane (~1100
  documented, the DD/FD enumeration policy recorded as a Task-3 decision, the exact count pinned at
  extraction). The covered-vs-TODO split is derived from the actual `FactoryArity` + the Z80 mnemonic
  set and PINNED by a test. The cross-source diff is two independent extractions (A Zilog + B a
  recorded independent source) reconciled to exit 0. "Structurally validated" guarantees the GT-C.4
  left column and explicitly NOT the right (behavioral). The DDCB risk is surfaced as a possible
  enumerated finding, not assumed away.
- **TDD where testable; process where not.** Tasks 1/2/5/7 are TDD (loader/diff/emitter/skeleton —
  all testable); Tasks 3/4 are the extraction PROCESS gated by the ladder (no per-row unit tests —
  per-row truth is M3.4 TomHarte); Tasks 6/8 are docs. Literal code/schema is given for the dataset
  row format (GT-A, every prefix plane), the register declarations (GT-F), the `DecodeStructure` +
  prefixed-`Insn` emission (GT-G.1), and the structural gate (Task 7 Step 2).
- **Test estimate.** ~1491 baseline + ~28 (Tasks 1:6, 2:9, 5:9, 7:4) ≈ ~1519 routine non-Klaus.
  (The header's ~35 is the conservative upper bound incl. fixture-row theory tests; ~28 is the
  task-summed floor. The extraction tasks add 0 unit tests.)
- **Whether any 6502 file changes: NONE expected** (Ground truth H). If a 6502 file MUST change, that
  is a STOP and a finding — the loader extensions are designed strictly additive precisely to avoid it.
- **Placeholder scan: clean.** No `TBD`/`FIXME`/`<placeholder>` in the tasks; every literal is grounded
  in a read of the current source (`OpcodeDataset.cs`, `DatasetDiff.cs`, `SemanticsMap.cs`,
  `SpecFileEmitter.cs`, `Program.cs`, `DecodeStructure.cs`, `Spec.cs:13-25,66-70`,
  `InstructionDef.cs:15-22`, the runbook, the 6502 data exemplar) — the recon Task 0 re-reads the
  shipped surfaces at implementation time to reconcile the SupportedModes target + the DDCB path.
- **Recorded judgement calls (not hidden):** the `pageCrossPenalty` ACCEPT-not-enforce (Task 2); the
  DD/FD enumeration policy (Task 3); the DDCB compound-prefix path (Task 0 recon -> Task 7 Step 0,
  with the enumerated-finding fallback); the Source-B document identity (Task 4); the SupportedModes
  set reconciled against the actual `AddrMode` enum (Task 0/5). Each is stated with its decision point.

## Closeout (filled at completion)

| Commit | Content | Suite |
|---|---|---|
| `a785380` (Task 1) | Z80 prefix-keyed dataset schema (prefix/subfield/Key); 6502 byte-identical | 1496 |
| `c36f0c1` (Task 2) | prefix-key uniqueness + Z80 modes + Key-based diff | 1513 |
| `e7771bb` (Task 3) | Z80 dataset extraction — Source A (Zilog UM0080), Rung-1 clean | 1513 |
| `6d885cb` (Task 4) | reconciled via cross-source diff (A x B), exit 0 | 1513 |
| `2d1eb72` (Task 5) | Z80 register/flag declarations + prefixed-Insn + DecodeStructure emission | 1522 |
| `2fe412d` (Task 6) | runbook Z80 prompt vocabulary + dataset README | 1522 |
| `3202661` (Task 7) | structural-generation check — skeleton compiles (Rungs 3-4) | 1526 |
| _(Task 8)_ | worked Z80 addendum + closeout | 1526 |

| Closeout metric | Value (filled at completion) |
|---|---|
| Baseline test count (Task 0) | **1491** (confirmed; matches the brief) |
| Final test count | **1526** (+35: Task1 +5, Task2 +17, Task5 +9, Task7 +4) |
| Z80 dataset row count | **698 documented** (252 base, 248 CB, 58 ED, 39 DD, 39 FD, 31 DDCB, 31 FDCB) — the DD/FD documented-subset policy; ~1100 was the upper estimate for full DD/FD re-enumeration |
| Covered emitted rows / TODO rows | **13 emitted / 685 TODO** (114 TODO(mode) + 571 TODO(semantics)) — the honest 3a covered minority |
| Covered mnemonics / TODO(vocab) mnemonics | **13 / 54** (of 67 distinct documented mnemonics) |
| Cross-source disagreements adjudicated | **25** (1 field cell: JR C,d cycles; 24 coverage: 10 undocumented SLL + 14 Z180/eZ80 ED extras) → reconciled to exit 0 |
| Provenance coverage fraction | **698/698 = 100%** |
| DDCB compound-prefix path taken | **enumerated-finding-deferred** — the shipped single-byte PrefixByte/Insn cannot express the two-deep DD CB; dataset carries the rows, skeleton emits them TODO, M3.4 extends the decoder |
| Any 6502 file changed? | **NONE** (`git diff main -- src/CpuEmulator.Cpus.Mos6502 …/mos6502-*.json` empty; RegeneratedSpecTests green) |
| Rung reached | **Rung 4** (structural end-to-end generator gate). Rung 5 (TomHarte) = M3.4. |

