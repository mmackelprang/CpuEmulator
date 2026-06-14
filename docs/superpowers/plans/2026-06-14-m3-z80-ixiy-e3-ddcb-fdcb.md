# M3.4e-3: The Z80 DDCB/FDCB Compound Plane — bit/rotate/shift on `(IX+d)`/`(IY+d)` + the undoc store-copy forms — TomHarte-Green

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. This is M3.4e-3 — the LAST opcode slice of the IX/IY arc and the **M3.4e-completion
> milestone**. **e-1a (#24), e-1b (#25), and e-2 (#27) are MERGED** (the `Indexed` AddrMode + the
> `(IX+d)` EA helper + the IXh/IXl half-views; the declarative compound `DD CB d op` decoder; the DD/FD
> CORE 252+252 TomHarte-green). e-3 derives the ~225 DDCB + ~225 FDCB missing dataset rows from the CB
> bit/rotate/shift table re-targeted onto `(IX+d)`, implements the ONE compound emit arm (the operation on
> `(IX+d)` + the undoc store-copy into `r[z]` + the BIT X/Y-from-`(IX+d)>>8` quirk), wires the importer's
> compound-row + `decode.prefixes` emission, and drives the 512 `dd cb __ NN.json` + `fd cb __ NN.json`
> vectors green. With e-3 GREEN the ENTIRE documented + undocumented Z80 instruction set is TomHarte-green.
> Depth template: `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e2-ddfd-core.md` +
> `…-m3-z80-ed-block-ops.md`.

**Goal:** make the Z80 **DDCB/FDCB compound plane** TomHarte-green — the bit/rotate/shift operations on the
`(IX+d)`/`(IY+d)` effective address (`RLC`/`RRC`/`RL`/`RR`/`SLA`/`SRA`/`SLL`/`SRL (IX+d)`,
`BIT b,(IX+d)`, `RES b,(IX+d)`, `SET b,(IX+d)`) INCLUDING the **undocumented store-copy forms** (for a
DDCB opcode whose low 3 bits `z != 6`, the rotate/shift/RES/SET result is ALSO written into register
`r[z]`) — for BOTH the DD (IX) and FD (IY) planes. The ~225+225 rows are DERIVED algorithmically (D3) from
the CB table re-targeted onto the indexed EA; the WZ = `IX+d` MEMPTR, the per-op flags, the cycle counts,
and the BIT X/Y-from-`(IX+d)>>8` quirk are pinned per-op to the vectors (the oracle). Every 6502 artifact
stays byte-identical; the whole Z80 (base + CB + ED + block + DD/FD core) stays TomHarte-green at the
universal Q/WZ/IM bar with IX/IY checked.

**Architecture:** e-1b shipped the declarative compound decoder (`PrefixByte.CompoundWith` /
`DisplacementBeforeOpcode`; the 24-bit compound key `(p1<<16)|(p2<<8)|finalOp` = `0xDDCB__`;
`EmitStructuredDecodeWalk` routes `DD CB d op` with the displacement consumed BEFORE the final opcode and
PACKED into `DecodeResult.Operands.Lo`; the `Insn(p1, p2, finalOp, …)` overload + `KeyShape.Compound`;
CPUGEN012 accepts Compound rows). e-2 shipped the `(IX+d)` EA + WZ pattern (`EmitZ80IndexedEa`,
`EmitWz(sb, "__ea")`), the CB rotate/shift/bit math reused here (`EmitRotateMath`, `EmitZ80CbRotateFlags`,
`EmitZ80CbBit`'s X/Y rule), and the `Indexed` disassembler arm + the Q-reset-on-prefix chokepoint. So e-3
is **additive and analogous** to how the CB and DD/FD planes added rows: a new `Z80DdCbSemantics` derives
the row text from the CB octal encoding (re-targeted onto `(IX+d)`); the importer routes
`Prefix == "0xDDCB"`/`"0xFDCB"` and (NEW for e-3) EMITS the compound `Insn(0xDD, 0xCB, finalOp, …)` overload
plus the `PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)` declaration; ONE new emit
arm (`EmitZ80DdCbBody`) reuses `EmitRotateMath`/`EmitZ80CbRotateFlags`/`EmitZ80CbBit`'s flag math with
`(HL)→(IX+d)`; the disassembler's `Indexed` arm gains a compound-key IX/IY discriminator. **Every 6502
artifact stays byte-identical.**

**Tech Stack:** C# (.NET 10), a Roslyn incremental source generator (`CpuEmulator.Generators`), a console
spec importer (`CpuEmulator.SpecImporter`) that regenerates `Z80Spec.cs` from `z80-opcodes.json` +
`z80-semantics.json`, and xUnit + the SingleStepTests/z80 vectors (TomHarte).

---

## Decisions baked into this plan (D3–D5 confirmed; D9–D11 new — flagged for the Coordinator)

- **D3 = DERIVE algorithmically (CONFIRMED + specified).** The dataset has only **31 DDCB rows + 31 FDCB
  rows**; the TomHarte harness gates **256 DDCB + 256 FDCB** compound vectors (the full final-opcode space
  0x00–0xFF — the compound page has NO prefix-byte holes because the byte after the displacement is the
  FINAL opcode, not a prefix). So **225 DDCB + 225 FDCB rows are MISSING** and must be derived by
  construction. The derivation rule is specified precisely in §"The D3 derivation rule" below and
  implemented in a new `Z80DdCbSemantics.OpsFor(finalOpcode)`. Task 0 cross-checks the derived row count
  (256 each) against the 256 `dd cb __ NN.json` + 256 `fd cb __ NN.json` vector files.
- **D4 = DDCB/FDCB JIT FALLBACK-ONLY (CONFIRMED — call already made in the scoped plan).** e-3 emits the
  compound rows as JIT FALLBACKS only (the descriptor table must be well-formed; no IL). Z80-through-JIT is
  M3.5. So e-3 adds the new `Z80DdCb` class to the JIT FALLBACK predicates (the `z80` bool, the
  `jitClass => "Register"` arm, the `JitOpLiteral` no-IL case list) exactly as CB/ED/DD-FD did. **JIT-IL for
  the compound ops is M3.5** alongside the rest of Z80-through-JIT.
- **D5 = redundant-prefix chains NOT modeled (CONFIRMED).** `DD DD CB`/`DD FD CB`/`DD ED` etc. have no
  vectors. Not gated, not modeled; e-1b already declined to declare these chains (B4). The closeout notes
  them unverified-pending.
- **D9 (NEW) = DD-CB and FD-CB ship in ONE PR (call made; mirrors e-2's D6).** D3=derive keeps them
  together: FD-CB is mechanically DD-CB with IY/IYh/IYl substituted for IX/IXh/IXl, and the index register
  is read from the compound key's p1 byte at emit time (no per-plane op record). Splitting would duplicate
  every task. DD-CB and FD-CB go live in the SAME regen (Task 6) and the TomHarte gate (Task 7) sweeps both
  256+256. **(If the Builder run finds the single PR too large to review, the natural cut is Task 7 — land
  DD-CB-green first, then FD-CB-green — but the code lands together; flagged, not recommended.)**
- **D10 (NEW — the load-bearing seam; needs the Coordinator's nod) = the compound interpreter body reads the
  displacement via a NON-CHARGING `_bus.Read8((ushort)(PC - 2))` peek, NOT `ReadBus`.** RECON (G3 below)
  established that the parameterless `Op{key}()` body has NO access to `DecodeResult.Operands.Lo` (the walk
  packs `d` there, but `Execute(key)` passes only the key — the bodies are parameterless and shared by every
  plane). After `Step` advances `PC += 4` (the compound length), the displacement `d` sits at `PC - 2`
  (layout `DD CB d op`; PC now points past `op`). The body re-reads it via `_bus.Read8((ushort)(PC - 2))`
  — the RAW bus read that does NOT charge a cycle, because the 4 fetch cycles (including the `d` fetch) were
  ALREADY charged once by `Step` (`_cycles += __r.Length`). Using the charging `ReadBus` here would
  DOUBLE-count the `d` fetch and add a spurious bus access the TomHarte trace does not record. **This is the
  single most important e-3 decision; it is interpreter-only and touches NO generator plumbing contract (the
  parameterless-body shape every plane shares stays intact).** The alternative (plumb `DecodeResult.Operands`
  into every `Op{key}()` signature) is a generator-wide reshaping that would risk the 6502 byte-identity
  guard and is rejected for this slice. **(Coordinator: confirm you accept the `_bus.Read8(PC-2)` peek;
  Task 0 Step 5 pins the cycle/bus-trace correctness against `dd cb __ 06.json`.)**
- **D11 (NEW) = the disassembler `Indexed` arm gains a compound-key IX/IY discriminator (vector-neutral but
  generation-blocking).** The e-2 `Indexed` arm (`CpuEmitter.cs:3305-3306`) reads the index register as
  `(OperationKey >> 8) == 0xDD ? "IX" : "IY"`. For a 24-bit COMPOUND key (`0xDDCB46`), `>> 8` = `0xDDCB`
  (≠ `0xDD`), so it would mis-render IX as IY. The disassembly string is NOT vector-gated (TomHarte does not
  check the mnemonic), so this is cosmetic — BUT the arm must still be WELL-FORMED and not throw. e-3 fixes
  the discriminator to handle both shapes: `(key > 0xFFFF ? (key >> 16) : (key >> 8)) == 0xDD`. **In scope,
  Task 5; low-risk.**

---

## Scope

**IN scope (the DDCB/FDCB compound plane comes alive end-to-end, both planes):**

1. **The documented compound bit/rotate/shift on `(IX+d)`/`(IY+d)`** (mode `Indexed`, KeyShape `Compound`):
   - **Rotate/shift `(IX+d)`** (final-opcode x=0): `RLC`/`RRC`/`RL`/`RR`/`SLA`/`SRA`/`SLL`/`SRL`. RMW on the
     EA; full CB rotate/shift flags; **WZ = the computed `IX+d` EA**.
   - **`BIT b,(IX+d)`** (final-opcode x=1): test bit `b` of `ReadBus(EA)`; Z/P-V/H/N/S flags; **X/Y from the
     internal address HIGH byte `(IX+d)>>8`** (the documented DDCB BIT quirk — NOT the value's bits 3/5);
     NO store; **WZ = the EA**.
   - **`RES b,(IX+d)`** (final-opcode x=2) / **`SET b,(IX+d)`** (final-opcode x=3): RMW clear/set bit `b`; no
     flag changes; **WZ = the EA**.
2. **The undocumented store-copy forms** (final-opcode `z != 6`): for a DDCB opcode whose low 3 bits
   `z != 6`, the op operates on `(IX+d)` AND ALSO writes the result into register `r[z]` (`r[z]` =
   B C D E H L — — A, z=6 is the documented "(HL)" slot = no copy):
   - `RLC`/…/`SRL` with `z != 6`: rotate/shift `(IX+d)`, write the result to BOTH `(IX+d)` AND `r[z]`
     (e.g. `DD CB d 00` = `LD B,RLC (IX+d)`: rotates `(IX+d)`, stores back to memory AND to B).
   - `RES`/`SET` with `z != 6`: clear/set bit `b` of `(IX+d)`, write the result to BOTH `(IX+d)` AND `r[z]`.
   - **`BIT b,(IX+d)` IGNORES `z`** — there is NO store-copy for BIT (x=1); all 8 z-values for a given
     `(x=1,b)` behave identically (only the EA is read; X/Y from `(IX+d)>>8`).
   - **The `r[z]` register names are the ORDINARY B/C/D/E/H/L/A** — NOT IXh/IXl. The undoc DDCB store-copy
     writes the plain H/L registers, never the index halves (confirm at Task 0 against `dd cb __ 04.json`
     = `LD H,RLC (IX+d)`: H gets the rotate result, IXh is untouched).
3. **The importer's compound-row emission** (NEW — e-1b deferred this; the importer currently drops DDCB
   rows to `// TODO(mode)`): route `Prefix == "0xDDCB"`/`"0xFDCB"` through `Z80DdCbSemantics`, EMIT the
   compound `Insn(0xDD, 0xCB, finalOp, …)` overload, and DECLARE `PrefixByte(0xDD, CompoundWith: 0xCB,
   DisplacementBeforeOpcode: true)` / `PrefixByte(0xFD, …)` in the `Decode` structure (replacing the plain
   `PrefixByte(0xDD)` the DD/FD core already emits — the SAME prefix byte gains the compound metadata).
4. **The `Z80DdCb` instruction class + the ONE compound emit arm** (`EmitZ80DdCbBody`) reusing
   `EmitRotateMath` / `EmitZ80CbRotateFlags` / the `EmitZ80CbBit` flag math with `(HL)→(IX+d)` + the
   store-copy + the BIT X/Y-from-EA-high quirk.
5. **The TomHarte DDCB + FDCB gate** — `CoveredDdCbPlaneOpcodes`/`CoveredFdCbPlaneOpcodes` theories loading
   `dd cb __ {op:x2}.json`/`fd cb __ {op:x2}.json` (512 vectors, the 4-token filename), green at the
   universal Q/WZ/IM bar with IX/IY checked.

**OUT of scope (each is a later slice — do NOT reach for it):**

- **The redundant-prefix chains** (`DD DD CB`/`DD FD CB`/`DD ED`) = NOT modeled (D5; no vectors;
  unverified-pending).
- **JIT-IL for the compound ops** = M3.5 (D4; e-3 emits JIT fallbacks only — the descriptor table is
  well-formed but no IL).
- **Interrupt SERVICING / ZEXALL** = M3.5.

> **The honest one-liner for M3.4e-3's close-state (target) — the M3.4e-completion milestone:** the Z80
> base + CB + ED + block + DD/FD core planes AND the 256 DDCB + 256 FDCB compound opcodes run and are
> TomHarte-green — the documented bit/rotate/shift on `(IX+d)`/`(IY+d)`, the undocumented store-copy forms
> (the `z != 6` register-copy), and the `BIT b,(IX+d)` X/Y-from-`(IX+d)>>8` quirk — per-T-state, with final
> Q/WZ/IM + IX/IY checked. **With e-3 GREEN, the ENTIRE documented + undocumented Z80 instruction set is
> TomHarte-green.** The DDCB/FDCB rows are DERIVED, not hand-authored. The redundant-prefix chains are
> unverified (no vectors); interrupt servicing + ZEXALL + the JIT remain M3.5 (the compound ops emit as JIT
> fallbacks). "TomHarte-green" is asserted over the 512 DDCB/FDCB opcodes (512,000 cases) + the re-validated
> base/CB/ED/block/DD/FD core, enumerated honestly in the closeout.

---

## Vector availability + the F1 gap (CONFIRMED at write-time)

| Plane | Compound vectors | Filename | Dataset rows present | F1 gap to derive |
|---|---|---|---|---|
| DDCB | **256** (`dd cb __ 00.json`…`dd cb __ ff.json`) | `dd cb __ NN.json` (4 tokens) | **31** | **225 rows** |
| FDCB | **256** (`fd cb __ 00.json`…`fd cb __ ff.json`) | `fd cb __ NN.json` (4 tokens) | **31** | **225 rows** |

**The filename trap (CONFIRMED at write-time — `ls` over `~/.cache/cpuemulator/vectors/z80/v1/`):** the
compound vectors are FOUR tokens — `dd cb __ NN.json` — where `__` is a LITERAL two-underscore placeholder
(NOT the displacement value; the actual displacement is in the case's `initial.ram` at the third stream
position) and `NN` is the FINAL opcode byte (after the displacement). The DDCB theory MUST build
`$"dd cb __ {op:x2}.json"` (note the spaces and the literal `__`). The DD/FD CORE theory globs
`dd {op:x2}.json` (3 tokens, `Z80TomHarteTests.cs:190`) and must NOT match these — the two are distinct
file sets (252 core + 256 compound). The vector cache is `~/.cache/cpuemulator/vectors/z80/v1/` (default;
or `$env:CPUEMULATOR_TESTVECTORS/z80/v1`), fetched by `tools/get-test-vectors-z80.ps1`. **There are NO
prefix-byte holes in the compound page** — the final opcode space is the full 0x00–0xFF (unlike the core
page, which omits cb/dd/ed/fd) because the byte after the displacement is the OPERATION, never a prefix.
So `Z80DdCbSemantics.OpsFor` must produce non-null for ALL 256 final opcodes (the 31 documented + the 225
derived).

**The total F1 gap = 450 derived rows.** Hand-authoring is the single biggest risk; D3 closes it by
construction. The gate is the per-opcode TomHarte sweep: a missing derived row → `Disassemble == "???"` →
the opcode is silently uncovered (the M3.4c probe-vs-emitted discipline). Task 0 + Task 6 cross-check
derived-count == 256 == vector-count for each plane. **The compound probe keys on the 24-bit compound key
`0xDDCB00 | op` (NOT `0xDD00 | op`)** — Task 7.

---

## The D3 derivation rule (the load-bearing algorithm — specify, then implement in `Z80DdCbSemantics`)

A `DD CB d op` (FD identical with IY) reinterprets the FINAL opcode byte `op` by the SAME classic CB octal
encoding (`x = bits 7-6`, `y = bits 5-3`, `z = bits 2-0`) the base CB plane uses — but EVERY operation
targets `(IX+d)` (the displacement comes from the compound decode walk), and the `z` field selects the
undoc store-copy register instead of the operand register. The rule is a pure function of the final opcode:

1. **`x = 0` (rotate/shift) → `rot[y] (IX+d)`** where `rot[y]` ∈
   `RLC RRC RL RR SLA SRA SLL SRL` (y=6 is the undocumented `SLL`):
   - Read `before = ReadBus(EA)`; compute `r = rot[y](before)` (reuse `EmitRotateMath`); set the full CB
     rotate/shift flag word from `r` + `cout` (reuse `EmitZ80CbRotateFlags`); `WriteBus(EA, r)`.
   - **Store-copy:** if `z != 6`, ALSO `r[z] = r` (the plain register `B C D E H L — A`; z=6 = no copy).
2. **`x = 1` (BIT) → `BIT y,(IX+d)`** (the bit index is `y`):
   - Read `v = ReadBus(EA)`; `bitSet = (v >> y) & 1`. Flags: Z = (bit==0), P/V = Z, H = 1, N = 0, S =
     (y==7 && bit set), C preserved.
   - **X/Y from `(EA >> 8)`** — the high byte of the computed `(IX+d)` address (the DDCB BIT quirk; the base
     CB `(HL)` form takes X/Y from `(WZ>>8)`, and since WZ == EA here, `(EA>>8)` IS `(WZ>>8)` — but pin it
     to `(EA>>8)` directly so the rule is self-evident). **NO store-copy** — `z` is ignored; all 8 z-values
     for a given `(x=1,y)` produce the identical case.
3. **`x = 2` (RES) → `RES y,(IX+d)`** / **`x = 3` (SET) → `SET y,(IX+d)`**:
   - Read `v = ReadBus(EA)`; `r = (x==3) ? (v | (1<<y)) : (v & ~(1<<y))`; `WriteBus(EA, r)`. NO flag changes.
   - **Store-copy:** if `z != 6`, ALSO `r[z] = r`.

**The op-text shape (derived by `Z80DdCbSemantics.OpsFor`):** ONE op record per final opcode, carrying the
operation, the bit/rot index, and the store-copy register slot `z` (or a "no copy" sentinel for z=6 and for
BIT). The index register (IX vs IY) is NOT on the op record — it is read in the emit arm from the compound
key's p1 byte (`(key >> 16) == 0xDD ? "IX" : "IY"`), mirroring how e-2's `Indexed` arm reads the prefix
(but `>> 16` for the 24-bit compound key, NOT `>> 8`). One set of op records serves both planes.

**The displacement + EA + WZ:** the compound decode walk consumed `d` into `DecodeResult.Operands.Lo` and
advanced `PC += 4`. The body re-reads `d` via `_bus.Read8((ushort)(PC - 2))` (D10 — the non-charging peek;
`PC - 2` because the layout is `DD CB d op` and PC now points past `op`), computes
`__ea = unchecked((ushort)(IX + (sbyte)d))` (reuse `EmitZ80IndexedEa`), and sets `WZ = __ea` (reuse
`EmitWz`). **WZ = `IX+d` for ALL compound forms — confirmed at Task 0 against the vectors.**

**The cycle counts (re-derive each from the vectors at Task 0):** the compound forms are uniformly heavier
than the CB `(HL)` forms by the extra prefix + displacement fetch. Expected from the literature (PIN to the
vectors): `RLC/…/SRL (IX+d)` and `RES/SET (IX+d)` = **23 T**; `BIT b,(IX+d)` = **20 T**. The store-copy
forms cost the SAME as the documented form (the register write is free — it reuses the value already in a
local). `Step` charges the 4 key bytes (`_cycles += __r.Length` = 4); the body charges the remaining
internal T-states minus its bus accesses (1 ReadBus for the value + 1 WriteBus for the RMW forms; BIT has
1 ReadBus and no write). **Task 0 pins the exact T-counts; the literals below assume 23/20 — correct them
if the vectors disagree.**

---

## Ground truth — what e-1a/e-1b/e-2/M3.4a-d ALREADY shipped (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 0** — e-3 REUSES or EXTENDS them.

- **The compound decode walk (e-1b).** `src/CpuEmulator.Generators/CpuEmitter.cs` `EmitStructuredDecodeWalk`
  (`:3699-3790`): for a `DD CB d op` stream it consumes the displacement into `lo` (`:3740`
  `lo = (byte)stream.NextUnit(); count = 1;`), then the final opcode (`:3741`), packs the 24-bit compound
  key `(first << 16) | (second << 8) | finalOp` (`:3747`), and returns `DecodeResult(key, length=4,
  Operands(lo, hi, count))` (`:3779`). The `s_compoundWith` / `s_dispBeforeOpcode` tables (`:3718-3719`) are
  emitted from `PrefixByte.CompoundWith` / `DisplacementBeforeOpcode` — **so declaring DD/FD with those
  flags in `decode.prefixes` is what turns the compound routing ON** (it is currently OFF: the DD/FD-core
  rows emit a PLAIN `PrefixByte(0xDD)` with no compound flags, so the walk takes the plain-prefix arm
  `:3751` for `DD CB` and would mis-key it; e-3's importer change at Task 6 declares the compound flags).
- **The `Insn` compound overload (e-1b).** `src/CpuEmulator.Core/Specification/Spec.cs:25`:
  `Insn(byte prefix1, byte prefix2, byte finalOpcode, string mnemonic, AddrMode mode, Op[] ops)` →
  `new(finalOpcode, …, Prefix: prefix1, Prefix2: prefix2, KeyShape: DecodeKeyShape.Compound)`. e-3's importer
  emits `Insn(0x{p1:X2}, 0x{p2:X2}, {finalOp}, …)` for the compound rows.
- **`PrefixByte` compound fields (e-1b).** `src/CpuEmulator.Core/Specification/DecodeStructure.cs:22-24`:
  `PrefixByte(byte Value, byte? CompoundWith = null, bool DisplacementBeforeOpcode = false)`. e-3's importer
  emits `new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)`.
- **`KeyShape.Compound` → length 4 (e-1b).** `CpuEmitter.cs:3413`: `KeyShape.Compound => 4,` in
  `KeyedDescriptorLiteral`'s `fixedLength` switch. The descriptor/`DescriptorFor` round-trips the 24-bit
  key (`JitDescriptorsByKey` is `Dictionary<uint, …>`, `:3365`). CPUGEN012 (`SpecFileEmitter.cs:85-90,
  231-241`) accepts Compound rows (e-1b extension).
- **The CB rotate/shift + bit math (e-2/M3.4b) e-3 reuses** (all in `CpuEmitter.cs`):
  - `EmitRotateMath(sb, op, oldCarryExpr)` (`:2309`) — produces `byte r` + `int cout` for
    RLC/RRC/RL/RR/SLA/SRA/SLL/SRL (the undoc SLL at `:2337`). e-3's rotate/shift arm calls it with
    `v = ReadBus(__ea)`.
  - `EmitZ80CbRotateFlags(sb, f, flags)` (`:2426`) — the full S/Z/Y/H(=0)/X/P(parity)/N(=0)/C(=cout) flag
    word from `r` + `cout`. e-3's rotate/shift arm calls it verbatim.
  - `EmitZ80CbBit`'s flag math (`:2465-2495`) — the BIT flag word with `xySrc = isMem ? "(byte)(WZ >> 8)"
    : "v"` (`:2481`). **e-3's BIT arm uses `(byte)(__ea >> 8)` as the X/Y source** (since WZ == __ea, this
    is equivalent, but `__ea`-direct makes the quirk self-documenting; OR reuse `(byte)(WZ >> 8)` since WZ
    is already set to __ea — confirm at Task 0 which reads cleaner). The `bit` + `bitSet` + the
    S=(y==7 && bit set) logic is identical.
  - `EmitZ80CbResSet` (`:2499`) — the RES/SET `(v | mask)`/`(v & ~mask)` + the RMW write. e-3's RES/SET arm
    mirrors it with `__ea` for `HL` PLUS the store-copy.
- **The `(IX+d)` EA + WZ helpers (e-2/e-1a).** `EmitZ80IndexedEa(sb, indexReg, dispExpr)` (`:2034`-region)
  → `ushort __ea = unchecked((ushort)(IX + (sbyte)(d)));`. `EmitWz(sb, expr)` → `WZ =
  unchecked((ushort)(<expr>));`. e-3 calls `EmitZ80IndexedEa(sb, ix, "d")` then `EmitWz(sb, "__ea")`.
- **The Q-reset-on-prefix chokepoint (e-2).** `CpuEmitter.cs:307-310`: every `OperationKey ∈ 0xDD00..0xDDFF
  or 0xFD00..0xFDFF` row gets `Q = 0;` emitted at the top of `Op{key}()`. **The compound keys are
  `0xDDCB__`/`0xFDCB__` — OUTSIDE that range** (they are > 0xFFFF). So the e-2 `ddFdPrefixed` predicate
  (`:307-308`) does NOT fire for compound rows. **e-3 must widen the predicate to ALSO zero Q for the
  compound range** (`0xDDCB00..0xDDCBFF or 0xFDCB00..0xFDCBFF`) — the DD/FD M1 prefix zeroes Q before the
  inner CB op just as for the core (RECON-FINDING H2 below). No DDCB op is SCF/CCF, so the Q-read-mid-body
  case does not arise, but the FINAL Q must reflect the op's flag write (rotate/RES/SET write F → Q =
  written-F; BIT writes F → Q = written-F). Confirm the universal Q/WZ bar handles this — the structured
  body's existing `Q = F;`-at-end machinery (the M3.4d lifecycle) applies since `Z80DdCb` is a flag-writing
  class.
- **The disassembler `Indexed` arm (e-2).** `CpuEmitter.cs:3305-3306`: keyed by `OperationKey`, renders
  `{m} ({IX|IY}+${operandLo:X2})` with `(OperationKey >> 8) == 0xDD ? "IX" : "IY"`. **For a 24-bit compound
  key `>> 8` is wrong** (D11 / H1) — e-3 fixes the discriminator. The disassembly is not vector-gated; the
  arm need only be well-formed + not throw.
- **The importer routing.** `SpecFileEmitter.cs:169-180`: the `z80Ops` ternary routes `null` /
  `0xCB` / `0xED` / `0xDD` / `0xFD` — **NO `0xDDCB`/`0xFDCB` arm** (those rows currently return `null` →
  fall to the per-mnemonic map → `// TODO(mode)` because their `Indexed` mode IS in `SupportedModes` after
  e-2, but `singleBytePrefix` is FALSE for `0xDDCB`, so the `(entry.Prefix is null || singleBytePrefix)`
  guard at `:192` REJECTS them → `// TODO(mode)`). e-3 adds the `0xDDCB`/`0xFDCB` arm + a compound-emission
  branch (Task 6).
- **The dataset.** `OpcodeDataset.cs`: `RecognizedPrefixes` includes `"0xDDCB"`/`"0xFDCB"` (`:106`);
  `PrefixBytes("0xDDCB") => 2` (`:262`); `OpcodeFormat` accepts the 2-hex FINAL opcode. The dataset has 31
  DDCB + 31 FDCB rows; e-3 adds 225 + 225 (Task 6). The compound `Key` (for the `// TODO`/diagnostic text)
  is plane-qualified.
- **The TomHarte harness ALREADY sets + checks IX/IY (e-1a/e-2).** `Z80TomHarteRunner.cs` sets/checks IX/IY;
  `Z80TomHarteCase.cs` parses `ix`/`iy`. So IX/IY checks come FREE; e-3 needs NO runner change. The DD/FD
  core theories (`CoveredDdPlaneOpcodes`/`Dd_opcode_matches_TomHarte_vectors`,
  `Z80TomHarteTests.cs:173-218`) are the template for the compound theories.
- The ADR `docs/architecture/0001-z80-second-architecture.md` Decision 1 (the declarative compound decode),
  Decision 3 (IX/IY + the IXh/IXl half-views — the undoc store-copy writes the PLAIN halves, NOT the index
  halves), Decision 4/7 (JIT fallback / the fastmem seam reused for `(IX+d)`).

### RECON FINDINGS that refine this plan (the code/vectors WIN — flagged)

> Discovered during write-time recon by reading the source + sampling the vectors. The implementer MUST
> re-confirm each at Task 0 and treat the vector/code as ground truth.

- **H1 (= D11) — the disassembler `Indexed` arm's IX/IY discriminator is wrong for a 24-bit compound key.**
  `CpuEmitter.cs:3306` uses `(OperationKey >> 8) == 0xDD`. For `0xDDCB46`, `>> 8` = `0xDDCB`. Fix to
  `((OperationKey > 0xFFFF ? OperationKey >> 16 : OperationKey >> 8) == 0xDD ? "IX" : "IY")`. Generation-
  blocking only insofar as the arm must compile + not mis-key; the string is not vector-gated. Task 5.
- **H2 — the Q-reset chokepoint predicate does NOT cover the compound key range.** `CpuEmitter.cs:307-308`
  `ddFdPrefixed = OperationKey is >= 0xDD00 and <= 0xDDFF or (>= 0xFD00 and <= 0xFDFF)`. The compound keys
  are `0xDDCB00..0xDDCBFF` / `0xFDCB00..0xFDCBFF` — OUTSIDE. e-3 widens the predicate to add those ranges so
  the compound bodies also emit `Q = 0;` up front (the DD prefix M1 zeroes Q). Since `Z80DdCb` is a
  flag-writing class, the body overwrites Q at the end with the written F (rotate/RES/SET/BIT all write F or
  preserve it through the structured `Q = F` lifecycle). Confirm the structured-body Q-at-end machinery
  fires for `Z80DdCb` (it keys off the flag-write predicate `Z80WritesFlags`). Task 4/5.
- **H3 (= D10) — the compound body cannot reach `DecodeResult.Operands.Lo`; it re-reads `d` via
  `_bus.Read8((ushort)(PC - 2))`.** RECON CONFIRMED: `Op{key}()` is parameterless (`:299`); `Execute(key)`
  (`:243-248`) passes only the key; `__r` (the `DecodeResult`) is local to `Step` and never reaches the
  body. The walk packs `d` into `Operands.Lo`, but nothing plumbs it to the body. After `Step` advances
  `PC += 4`, `d` sits at `PC - 2`. The non-charging `_bus.Read8` (NOT `ReadBus`) avoids double-charging the
  fetch (already counted in `_cycles += __r.Length`). **This is the load-bearing seam — Task 0 Step 5 pins
  the cycle + bus-trace correctness; Task 2/3 emit `_bus.Read8((ushort)(PC - 2))`.** (`Z80Cpu.ReadBus`
  charges `_cycles++` then `_bus.Read8`, `:85-89`; the raw `_bus.Read8` does NOT charge — confirm `_bus` is
  the field name + reachable from the generated partial body.)
- **H4 — the importer must emit the COMPOUND `Insn` overload AND the compound `PrefixByte` declaration; the
  e-2 path emits NEITHER.** The DD/FD core emits `Insn(0x{p:X2}, {opcode}, …)` (plain prefixed) +
  `PrefixByte(0x{b:X2})` (plain). e-3 must (a) emit `Insn(0xDD, 0xCB, {finalOp}, …)` for compound rows, and
  (b) emit the SAME DD/FD prefix byte WITH the compound metadata (`CompoundWith: 0xCB,
  DisplacementBeforeOpcode: true`) in the `Decode` declaration — replacing the plain `PrefixByte(0xDD)`. The
  `Decode`-declaration emitter (`SpecFileEmitter.cs:238-244`) currently emits `new PrefixByte(0x{b:X2})`
  unconditionally. e-3 makes it emit the compound form for DD/FD (which now back BOTH plain core rows AND
  compound rows). **The plain DD/FD core rows still decode correctly** because the walk's compound arm fires
  ONLY when the byte after DD/FD IS the `CompoundWith` byte (CB); a `DD 7E` (core) still takes the plain arm.
  Task 6 — the atomic regen.
- **H5 — the store-copy register `r[z]` is the PLAIN B/C/D/E/H/L/A, never IXh/IXl.** The undoc DDCB
  store-copy writes the ordinary registers (`dd cb __ 04` = `LD H,RLC (IX+d)` writes H, leaves IXh
  untouched). So the op record carries the plain register NAME (`Reg8[z]` with the standard table, z=6 →
  no-copy sentinel). Do NOT apply the e-2 H/L→IXh/IXl substitution here. Confirm at Task 0 against
  `dd cb __ 04.json` (H result) vs the IX final value (IXh unchanged).
- **H6 — there are NO prefix-byte holes in the compound page.** Unlike the core page (which omits
  cb/dd/ed/fd because a DD-then-prefix is a chain), the compound page's final opcode is the OPERATION, so
  all 256 final opcodes are valid + vectored. `Z80DdCbSemantics.OpsFor` returns non-null for all 256.

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `tools/CpuEmulator.SpecImporter/Z80DdCbSemantics.cs` | Create | The D3 derivation: `OpsFor(int finalOpcode)` → the compound op-text (rotate/BIT/RES/SET on `(IX+d)` + the store-copy slot). Plane-agnostic; IX/IY read from the key at emit time. |
| `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs` | Modify | Route `Prefix == "0xDDCB"`/`"0xFDCB"` through `Z80DdCbSemantics`; emit the compound `Insn(p1,p2,finalOp,…)` overload; emit the compound `PrefixByte(…CompoundWith…)` in the `Decode` declaration (H4). |
| `tools/CpuEmulator.SpecImporter/SemanticsMap.cs` | Modify | `FactoryArity` for the new `DdCb…` op kind(s). |
| `tools/CpuEmulator.SpecImporter/data/z80-opcodes.json` | Modify | ADD the 225 DDCB + 225 FDCB derived rows (the F1 gap); the existing 31+31 stay. |
| `src/CpuEmulator.Core/Specification/Op.cs` | Modify | The new compound op record(s) (`DdCbOp`). |
| `src/CpuEmulator.Core/Specification/Spec.cs` | Modify | The factory for the new op record(s). |
| `src/CpuEmulator.Generators/SpecModel.cs` | Modify | The new `Z80DdCb` `InstructionClass` member. |
| `src/CpuEmulator.Generators/SpecParser.cs` | Modify | `s_microOpSignatures`; the op-kind class set; `ClassifyOps`; `ValidateModeForClass` (Indexed legality for `Z80DdCb`); the status-touch predicate. |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | The `EmitZ80DdCbBody` compound emit arm (reusing `EmitRotateMath`/`EmitZ80CbRotateFlags`/the BIT flag math + the store-copy + the `_bus.Read8(PC-2)` displacement peek); the `Z80DdCb` dispatch arm; the disassembler IX/IY-for-compound fix (H1); the Q-reset predicate widening (H2); `Z80Cycles`/`Z80WritesFlags`/`isZ80`/the JIT predicates for `Z80DdCb`. |
| `src/CpuEmulator.Cpus.Z80/Z80Spec.cs` | Modify (regenerated) | The regenerated spec — the 256 DDCB + 256 FDCB rows live; the `Decode` declaration gains the compound DD/FD `PrefixByte`s. |
| `tests/CpuEmulator.Tests/Importer/Z80DdCbSemanticsTests.cs` | Create | `Z80DdCbSemantics.OpsFor` derivation truth table + the dataset's 256 DDCB + 256 FDCB row count (the F1 cross-check). |
| `tests/CpuEmulator.Tests/Generators/Z80DdCbRotateTests.cs` | Create | RLC/…/SRL (IX+d) flags + WZ=EA + the store-copy (z≠6) + no-copy (z=6) (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80DdCbBitTests.cs` | Create | BIT b,(IX+d): flags + X/Y-from-(EA>>8) + no store + z-ignored (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80DdCbResSetTests.cs` | Create | RES/SET b,(IX+d) + the store-copy (z≠6) + no-copy (z=6) (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80DdCbClassifyTests.cs` | Create | The compound-shaped rows classify + compile + the disassembler arm does not throw (synthetic). |
| `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs` | Modify | Add the `CoveredDdCbPlaneOpcodes`/`CoveredFdCbPlaneOpcodes` theories (the 4-token filename + the `0xDDCB00\|op` probe key). |
| `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e3-ddcb-fdcb.md` | Modify | This file — the closeout. |
| `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md` | Modify | The M3.4e-3 section pointer to this plan; the slice-docs-index cross-link; the M3.4e-completion note. |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502
> byte-identity guard `RegeneratedSpecTests` + the whole Z80 staying green at the universal Q/WZ/IM bar +
> IX/IY checked), then commit. Tasks are dependency-ordered so the suite builds and stays green after every
> task. Literal code is given for every load-bearing piece. The synthetic-spec tests (via
> `GeneratorTestHost.CompileAndLoadType`) decouple from the real `Z80Spec.cs` regen, which lands atomically
> late (Task 6). Structured synthetic fixtures use `IAddressSpace _bus`, declare `public byte Q;` +
> `public int Im;`, and name `IX`/`IY`/`IXh`/`IXl`/`IYh`/`IYl` + the plain `B`..`A` in their `Registers`
> where the body references them.

### Task 0: Baseline + shipped-surface recon + the D3-derivation + the vector cross-check (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch.** Create the branch off the current main (which includes e-1a #24 + e-1b #25 +
  e-2 #27):
  Run: `git switch -c feat/m3-z80-ixiy-e3-ddcb`
  Expected: on the new branch; `git log` shows the e-2 merge (#27 `ea187ce`). CONFIRM e-1b/e-2 are present:
  grep `KeyShape.Compound` in `Spec.cs`, `s_compoundWith` + `EmitZ80IndexedBody` in `CpuEmitter.cs`,
  `DdFdLdIndexed` in `Op.cs`.

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test` → 0 failures, 0 unexpected skips. Record the EXACT count (the closeout pins it).
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each Ground-truth + RECON-FINDING surface holds.**
  The checklist: the compound walk (`CpuEmitter.cs:3699-3790`, esp. `:3740` the `d`→`lo`, `:3747` the
  24-bit key, `:3779` the `DecodeResult`); the `Insn` compound overload (`Spec.cs:25`); the `PrefixByte`
  compound fields (`DecodeStructure.cs:22-24`); `KeyShape.Compound => 4` (`CpuEmitter.cs:3413`); the CB
  math (`EmitRotateMath:2309`, `EmitZ80CbRotateFlags:2426`, `EmitZ80CbBit:2465-2495` esp. the
  `xySrc = (byte)(WZ>>8)` for the (HL) form at `:2481`, `EmitZ80CbResSet:2499`); the EA + WZ helpers
  (`EmitZ80IndexedEa`, `EmitWz:1978`); the Q-reset predicate (`:307-310` — **confirm it does NOT cover the
  compound range**, H2); the disassembler `Indexed` arm (`:3305-3306` — **confirm the `>> 8` mis-keys a
  24-bit key**, H1); the importer routing (`SpecFileEmitter.cs:169-180` — **confirm NO `0xDDCB`/`0xFDCB`
  arm**) + the `singleBytePrefix` guard (`:190-192`) + the `Decode`-declaration emitter (`:238-244` — the
  plain `PrefixByte(0x{b:X2})`); the dataset (`OpcodeDataset.cs:106,262`); the DD/FD core theories
  (`Z80TomHarteTests.cs:173-218`). Read `Z80CbSemantics.cs` + `Z80DdFdSemantics.cs` (the derivation pattern
  `Z80DdCbSemantics` copies).

- [ ] **Step 4: Pin the displacement-access seam (H3 / D10 — the load-bearing seam).** Read
  `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` `ReadBus` (`:85-89`, charges `_cycles++` then `_bus.Read8`) + the
  `_bus` field. Confirm: the parameterless `Op{key}()` body (`CpuEmitter.cs:299`) has NO access to `__r`;
  `Execute(key)` (`:243-248`) passes only the key. So the compound body MUST re-read `d` via
  `_bus.Read8((ushort)(PC - 2))` (NOT `ReadBus` — the fetch was already charged by `Step`'s
  `_cycles += __r.Length`). Confirm `_bus` is reachable from the generated body (it is the field `ReadBus`
  uses). Pin the `PC - 2` offset: layout `DD CB d op`; after `Step` advances `PC += 4`, `d` is at `PC - 2`.

- [ ] **Step 5: Re-derive the WZ/cycle/flag/store-copy rules from the vectors (the oracle — do NOT trust
  the prose).** Open these `dd cb __ NN.json` (and the matching `fd cb __ NN.json`) and CONFIRM (the
  implementer pins these into the per-op tests + the Task 7 iteration):
  - **Rotate/shift documented (z=6):** `dd cb __ 06` (RLC (IX+d): RMW, full CB flags, WZ=IX+d, 23 T —
    confirm the cycle count + that the byte at the EA is rotated + written back; pin WZ=EA + the bus trace:
    the `d` fetch is charged ONCE by Step, the value read + write are the body's 2 bus accesses).
  - **Rotate/shift store-copy (z≠6):** `dd cb __ 00` (LD B,RLC (IX+d): B AND (IX+d) get the rotate result;
    same flags + cycles as `__ 06`); `dd cb __ 04` (LD H,RLC (IX+d): **H** gets the result, **IXh
    untouched** — H5). Confirm the store-copy register is the PLAIN B/C/D/E/H/L/A.
  - **BIT (x=1):** `dd cb __ 46` (BIT 0,(IX+d): no store, Z/P/H/N/S flags, **X/Y from (IX+d)>>8** — pin
    IX/d → EA → (EA>>8) bits 3/5 → F's X/Y; 20 T); `dd cb __ 40` (BIT 0,(IX+d): IDENTICAL to `__ 46` —
    z is ignored for BIT; confirm `__ 40`..`__ 47` all match).
  - **RES/SET:** `dd cb __ 86` (RES 0,(IX+d): RMW, no flags, WZ=EA, 23 T); `dd cb __ 80` (LD B,RES 0,(IX+d):
    B AND (IX+d) get the result); `dd cb __ c6` (SET 0,(IX+d)); `dd cb __ c0` (LD B,SET 0,(IX+d)).
  - For each, the FINAL `r` delta is **+2** (the DD CB compound is two M1 fetches — the prefix DD + the CB;
    the displacement + final opcode are NOT M1 — CONFIRM against the vector's `r` delta), the FINAL `ix`/`iy`
    is unchanged (the compound ops never write IX/IY), WZ = EA, and Q = the written F. The cycle count = the
    vector's `cycles` array length.

  > **The R-bump (H3-adjacent):** the compound is 2 M1 fetches (DD + CB) → R+2, like the core. But
  > `__r.Length` = 4. Confirm `OnInstructionFetched` bumps R by the M1 count (2), NOT `__r.Length` (4) — the
  > e-2 closeout established the M1-vs-length R rule; the compound form is its 4-byte extreme. Pin R+2
  > against `dd cb __ 06.json`. **If the e-2 `OnInstructionFetched` already derives R+2 for the 3/4-byte
  > Indexed core forms, the compound (length 4) is covered by the SAME rule — confirm, do not re-fix.**

- [ ] **Step 6: Cross-check the F1 gap (the D3 derived-count == vector-count guard).** Confirm:
  Run: `pwsh -c "(Get-Content tools/CpuEmulator.SpecImporter/data/z80-opcodes.json | ConvertFrom-Json | Where-Object { $_.prefix -eq '0xDDCB' }).Count"`
  Expected: **31** (the documented rows). And confirm the vector files:
  Run (bash): list `~/.cache/cpuemulator/vectors/z80/v1/dd cb __ *.json` → expect **256** files (all
  0x00–0xFF; no holes — H6). Same for `fd cb __ *.json`. So **225 DDCB + 225 FDCB rows must be derived.**
  `Z80DdCbSemantics.OpsFor` must produce non-null for ALL 256 final opcodes; Task 6's importer test asserts
  the dataset has 256 DDCB + 256 FDCB rows after the add.

- [ ] **Step 7:** No commit (read-only). Proceed to Task 1.

---

### Task 1: The compound micro-op vocabulary + the `Z80DdCb` instruction class (Op record + factory + parser) (TDD)

> Add the `Op` record + `Spec` factory + parser `s_microOpSignatures` + importer `FactoryArity` + the new
> instruction class for the compound `(IX+d)` bit/rotate/shift family. ONE op record carries the operation
> family, the bit/rot index, and the store-copy register slot. No emitter body yet (Tasks 2–3) — this task
> is the closed vocabulary so the spec table type-checks + the importer validates.

**Design decision (recorded — the op shape):** ONE op record `DdCbOp(string Op, int Index, string CopyReg)`:
- `Op` ∈ `"RLC"`/`"RRC"`/`"RL"`/`"RR"`/`"SLA"`/`"SRA"`/`"SLL"`/`"SRL"` (rotate/shift, x=0) or
  `"BIT"`/`"RES"`/`"SET"` (x=1/2/3).
- `Index` = the rotate is unused (0); for BIT/RES/SET it is the bit index `y` (0–7).
- `CopyReg` = the store-copy target register name `"B"`..`"A"` for `z != 6`, or `"-"` (sentinel) for z=6
  (no copy) and for ALL BIT forms (BIT never copies). The plain register names (H5) — NOT IXh/IXl.

The index register (IX vs IY) is NOT carried — the emit arm reads it from the compound key's p1 byte
(`(key >> 16) == 0xDD ? "IX" : "IY"`). One set of op records serves both planes.

**Files:**
- Modify: `src/CpuEmulator.Core/Specification/Op.cs`, `Spec.cs`
- Modify: `src/CpuEmulator.Generators/SpecModel.cs` (the `Z80DdCb` `InstructionClass`)
- Modify: `src/CpuEmulator.Generators/SpecParser.cs`
- Modify: `tools/CpuEmulator.SpecImporter/SemanticsMap.cs` (`FactoryArity`)
- Test: `tests/CpuEmulator.Tests/Generators/Z80DdCbVocabularyTests.cs` (create)

- [ ] **Step 1: Write the failing vocabulary test.** Create `Z80DdCbVocabularyTests.cs`:

```csharp
using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80DdCbVocabularyTests
{
    [Fact]
    public void DdCb_factory_carries_op_index_and_copyreg()
    {
        var rot = (DdCbOp)DdCb("RLC", 0, "B");      // LD B,RLC (IX+d)
        Assert.Equal("RLC", rot.Op); Assert.Equal(0, rot.Index); Assert.Equal("B", rot.CopyReg);

        var rotNoCopy = (DdCbOp)DdCb("RLC", 0, "-"); // RLC (IX+d), z=6
        Assert.Equal("-", rotNoCopy.CopyReg);

        var bit = (DdCbOp)DdCb("BIT", 5, "-");       // BIT 5,(IX+d) — never copies
        Assert.Equal("BIT", bit.Op); Assert.Equal(5, bit.Index); Assert.Equal("-", bit.CopyReg);

        var set = (DdCbOp)DdCb("SET", 3, "H");       // LD H,SET 3,(IX+d)
        Assert.Equal("SET", set.Op); Assert.Equal(3, set.Index); Assert.Equal("H", set.CopyReg);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80DdCbVocabularyTests"` → FAIL (record/factory absent).

- [ ] **Step 3: Add the `Op` record.** In `src/CpuEmulator.Core/Specification/Op.cs`, after the e-2 indexed
  records (the `DdFdIncDecIndexedOp` line):

```csharp

// ── M3.4e-3 DDCB/FDCB compound plane (additive; the index register IX/IY is read from the compound key) ──
// A bit/rotate/shift on (IX+d)/(IY+d). Op ∈ rotate/shift ("RLC".."SRL") or "BIT"/"RES"/"SET".
// Index = the bit index for BIT/RES/SET (0..7), 0 for rotates. CopyReg = the undoc store-copy register
// ("B".."A") for z != 6, or "-" (no copy) for z=6 and for ALL BIT forms (the plain register, NOT IXh/IXl).
public sealed record DdCbOp(string Op, int Index, string CopyReg) : Op;
```

- [ ] **Step 4: Add the `Spec` factory.** In `src/CpuEmulator.Core/Specification/Spec.cs`, after the e-2
  indexed factories:

```csharp
    // ── M3.4e-3 DDCB/FDCB compound plane (additive) ──
    public static Op DdCb(string op, int index, string copyReg) => new DdCbOp(op, index, copyReg);
```

- [ ] **Step 5: Add the parser `s_microOpSignatures`.** In `src/CpuEmulator.Generators/SpecParser.cs`,
  after the e-2 indexed entries:

```csharp
        // M3.4e-3: the DDCB/FDCB compound op.
        ["DdCb"] = new[] { ArgKind.Str, ArgKind.Int, ArgKind.Str },   // DdCb("RLC", 0, "B")
```

  > CONFIRM `ArgKind.Int` is the existing kind for the integer arg (the CB `CbBit("BIT",y,"…")` uses it —
  > grep `CbBit` in `s_microOpSignatures`; mirror its `Int` kind for `Index`).

- [ ] **Step 6: Add the `InstructionClass` member.** In `src/CpuEmulator.Generators/SpecModel.cs`, after the
  `Z80Indexed,` member (e-2):

```csharp
    Z80DdCb,   // M3.4e-3: the compound DDCB/FDCB bit/rotate/shift on (IX+d)/(IY+d) + the undoc store-copy
```

- [ ] **Step 7: Add the op-kind class set + `ClassifyOps` + `ValidateModeForClass` + status-touch.** In
  `src/CpuEmulator.Generators/SpecParser.cs`:
  - After `s_z80IndexedOpKinds` (e-2), add:

```csharp
    // ── M3.4e-3 DDCB/FDCB compound op-kind class set (additive) ──
    private static readonly HashSet<string> s_z80DdCbOpKinds = new(System.StringComparer.Ordinal) { "DdCb" };
```
  - In `ClassifyOps`, after the `s_z80IndexedOpKinds` arm:

```csharp
        if (s_z80DdCbOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 DDCB class must contain exactly one op"; return null; }
            return InstructionClass.Z80DdCb;
        }
```
  - In `ValidateModeForClass`, after the `InstructionClass.Z80Indexed =>` arm:

```csharp
            // M3.4e-3: the compound DDCB/FDCB ops are all Indexed mode (KeyShape.Compound at the row level).
            InstructionClass.Z80DdCb =>
                mode == "Indexed" ? null : "Z80 DDCB class (compound (IX+d)/(IY+d)) requires Indexed mode",
```
  - In the status-touch predicate, add `Z80DdCb` to the `is … or …` chain (rotate/BIT/RES write F — RES/SET
    actually preserve F, but the class is eligible; the per-op `Z80WritesFlags` decides — Task 4 sets RES/SET
    to NOT write F so Q is preserved per the vectors; confirm RES/SET F-preservation at Task 0):

```csharp
                or InstructionClass.Z80EdBlock or InstructionClass.Z80Indexed
                or InstructionClass.Z80DdCb
```

- [ ] **Step 8: Add the importer `FactoryArity`.** In `tools/CpuEmulator.SpecImporter/SemanticsMap.cs`,
  after the e-2 indexed entries:

```csharp
        // M3.4e-3: the DDCB/FDCB compound op.
        ["DdCb"] = 3,
```

  > `AllowedArgPattern` already accepts `"\w+"` (the op-name + register strings, incl. the `"-"` sentinel —
  > **CONFIRM `"-"` matches the `\w+`/string pattern**; if `-` is not in `\w`, widen the pattern to accept
  > a quoted `"-"` OR use a different sentinel like `"NONE"`) + `\d+` (the index). Confirm at Task 0.

- [ ] **Step 9: Build + the vocabulary test green.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80DdCbVocabularyTests"` → PASS.
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 10: Full-suite gate + commit.**
  Run: `dotnet test` → the recorded baseline count + the 1 new test, 0 failures. `RegeneratedSpecTests`
  green (the 6502 byte-identity — no `Z80Spec.cs` change yet).
  Commit: `feat(z80): M3.4e-3 Task 1 — the DDCB compound micro-op vocabulary + the Z80DdCb class`

---

### Task 2: The rotate/shift compound emit arm (RLC/…/SRL (IX+d) + the store-copy) (TDD, synthetic)

> Implement `EmitZ80DdCbBody`'s rotate/shift path: read `d` via `_bus.Read8(PC-2)` (D10), compute the EA,
> set WZ=EA, read the value, run `EmitRotateMath`, set the CB rotate/shift flags, write back to `(IX+d)`,
> and (if `CopyReg != "-"`) ALSO write the result to the plain register. Synthetic spec via
> `GeneratorTestHost`. The classify test (Task 5) compiles all families; this task proves rotate behavior.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (the `Z80DdCb` dispatch arm + `EmitZ80DdCbBody` rotate
  path; `Z80Cycles`/`Z80WritesFlags`/`isZ80` for `Z80DdCb`; the Q-reset predicate widening H2)
- Test: `tests/CpuEmulator.Tests/Generators/Z80DdCbRotateTests.cs` (create)

- [ ] **Step 1: Write the failing synthetic rotate test.** Create `Z80DdCbRotateTests.cs`. The synthetic
  spec declares a DD/FD compound prefix + one rotate row (with and without the store-copy), drives a
  `DD CB d 06` (RLC (IX+d), z=6, no copy) + a `DD CB d 00` (LD B,RLC (IX+d), z=0) stream, and asserts: the
  EA byte is rotated + written back; WZ = EA; the CB flags; B == the result for the store-copy form; B
  untouched for the no-copy form. Mirror the e-2 `Z80IndexedIncDecTests` fixture shape (`IAddressSpace
  _bus`, `public byte Q; public int Im;`, the `IX`/`B` registers).

```csharp
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80DdCbRotateTests
{
    // The synthetic DDCB spec: a DD/FD prefix compounding with CB (displacement-before-opcode), and two
    // rotate rows — RLC (IX+d) (z=6, no copy) at key 0xDDCB06, and LD B,RLC (IX+d) (z=0) at key 0xDDCB00.
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcb")]
        public static class DdCbSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8),
                new("IXh", 8), new("IXl", 8), new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("IX", 16, HighHalf: "IXh", LowHalf: "IXl"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)],
                ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0xCB, 0x06, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "-")]),
                Insn(0xDD, 0xCB, 0x00, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "B")]),
            ];
        }
        """;

    [Fact]
    public void Rlc_indexed_rotates_memory_sets_wz_and_copies_to_register()
    {
        var cpu = GeneratorTestHost.CompileAndLoadType(Spec, "ddcb");
        // Drive the no-copy RLC (IX+d): IX=0x4000, d=0x05 → EA=0x4005; mem[EA]=0x80 → RLC → 0x01, C=1.
        // Stream at PC=0x0000: DD CB 05 06. Assert mem[0x4005]==0x01, WZ==0x4005, B untouched.
        // Then the store-copy LD B,RLC (IX+d): stream DD CB 05 00; mem[EA]=0x80 → 0x01; B==0x01.
        // (Exact harness wiring mirrors Z80IndexedIncDecTests — the implementer fills the bus seed +
        //  the SetRegister/Step/GetRegister calls; the assertions are the load-bearing part.)
        Assert.NotNull(cpu);
    }
}
```

  > **Implementer note:** flesh out the harness body to match the e-2 `Z80IndexedIncDecTests`/
  > `Z80IndexedLdTests` pattern exactly (seed `_bus` via the test `IAddressSpace`, `SetRegister("IX", …)`,
  > write the 4 stream bytes + the EA byte into the bus, `Step()`, then `GetRegister`/bus reads for the
  > assertions). The 4-byte stream is `DD CB d op` at PC; the EA byte is at `IX + (sbyte)d`. Assert WZ=EA,
  > the rotated byte at EA, the CB flag word, and the copy-register state.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80DdCbRotateTests"` → FAIL (no `Z80DdCb` dispatch arm →
  generation throws or the body is absent).

- [ ] **Step 3: Add the `Z80DdCb` dispatch arm.** In `CpuEmitter.cs` `EmitOpcodeMethod`'s `switch (opClass)`,
  after the `case InstructionClass.Z80Indexed:` arm:

```csharp
            case InstructionClass.Z80DdCb:
                EmitZ80DdCbBody(sb, instruction, pc, pcType, statusReg, flags);
                break;
```

- [ ] **Step 4: Add `Z80DdCb` to `isZ80`.** In `EmitOpcodeMethod` (`:278-283`), extend the `isZ80` chain:

```csharp
            or InstructionClass.Z80EdBlock or InstructionClass.Z80Indexed
            or InstructionClass.Z80DdCb;
```

- [ ] **Step 5: Widen the Q-reset predicate (H2).** In `EmitOpcodeMethod` (`:307-308`), extend
  `ddFdPrefixed` to cover the compound key range:

```csharp
        bool ddFdPrefixed = instruction.OperationKey is >= 0xDD00 and <= 0xDDFF
                         or (>= 0xFD00 and <= 0xFDFF)
                         or (>= 0xDDCB00 and <= 0xDDCBFF)     // M3.4e-3: the DDCB compound range
                         or (>= 0xFDCB00 and <= 0xFDCBFF);    // M3.4e-3: the FDCB compound range
```

- [ ] **Step 6: Add the `Z80DdCb` cycle entry.** In `Z80Cycles` (`:478`-region), add a `Z80DdCb` arm. The
  cycle template per family (re-derived at Task 0 — adjust if the vectors disagree): rotate/shift + RES/SET
  = 23 T; BIT = 20 T. Mirror the `Z80Indexed` cycle-table entries:

```csharp
        // M3.4e-3: the compound DDCB/FDCB ops. BIT = 20 T; rotate/shift + RES/SET = 23 T (RMW).
        (InstructionClass.Z80DdCb, "DdCb", _) => 23,   // base; the BIT form (no write) overrides in-body to 20
```

  > **The BIT-vs-RMW cycle split:** if `Z80Cycles` cannot see the op family (it switches on `(cls, opKind,
  > mode)` and `opKind` is `"DdCb"` for all three families), the per-op cycle difference (20 vs 23) is
  > handled IN the body via `_cycles += …` (the e-2 precedent — the body charges the remaining T-states).
  > The `Z80Cycles` entry is the documentation/JIT-descriptor base; the body's `_cycles +=` is authoritative
  > for the interpreter. Confirm the e-2 `Z80Indexed` pattern (the body charges; `Z80Cycles` is the
  > descriptor base) and mirror it.

- [ ] **Step 7: Add the `Z80DdCb` flag-write entry.** In `Z80WritesFlags` (`:396`-region), `Z80DdCb` writes
  F for rotate/shift + BIT, but RES/SET PRESERVE F (no flag change — confirm at Task 0). Since the predicate
  keys on the class (not the per-op family), and the rotate/BIT bodies write F while RES/SET preserve it:

```csharp
        // M3.4e-3: rotate/shift + BIT write F; RES/SET preserve F. The class is flag-eligible; the body
        // writes F only for rotate/BIT (RES/SET leave F untouched, so Q is preserved through the lifecycle).
        InstructionClass.Z80DdCb => true,   // eligible; the RES/SET body simply does not assign F
```

  > **CRITICAL Q-correctness check (Task 0 + Task 4):** for RES/SET (which preserve F), the universal Q
  > lifecycle must NOT set Q to a freshly-written F (there is none). Confirm how the structured Q-at-end
  > machinery works: if `Z80WritesFlags` returning `true` forces a `Q = F;` at the body end EVEN when the
  > body did not write F, then RES/SET would set Q = (the preserved F), which the vectors may or may not
  > expect. **Pin Q for RES/SET against `dd cb __ 86.json` (RES) — does Q end == the preserved F, or ==
  > 0?** The Z80 Q rule: Q = F only when the instruction WROTE F this cycle; an instruction that does not
  > touch F leaves Q = 0 (the prefix already zeroed it, H2). So RES/SET likely need `Z80WritesFlags =>
  > false` for the RES/SET sub-family — but the predicate is class-level. **Decision (recorded, RESOLVE at
  > Task 0): if the vectors show RES/SET end with Q=0, split the Q handling by op family in the body (emit
  > `Q = F;` only for rotate/BIT; leave Q=0 for RES/SET) rather than via the class-level `Z80WritesFlags`.**
  > This is the one genuinely subtle correctness point in e-3 — flag it to the Coordinator if the vectors
  > force a per-family Q split that the current class-level machinery cannot express (RECON could not pin
  > this from the prose alone; the vector is the oracle).

- [ ] **Step 8: Implement `EmitZ80DdCbBody` (the rotate/shift path).** In `CpuEmitter.cs`, after
  `EmitZ80IndexedIncDec` (the e-2 indexed arms region, ~`:2300`), add the new arm. The shared preamble
  reads `d` (D10), computes the EA, sets WZ; then `switch` on the op family:

```csharp
    // ── M3.4e-3 DDCB/FDCB compound plane: bit/rotate/shift on (IX+d)/(IY+d) + the undoc store-copy ──
    // Preamble: the compound walk consumed the displacement into DecodeResult.Operands.Lo and advanced
    // PC by 4 (DD CB d op). The parameterless body cannot reach __r, so it re-reads d via the RAW,
    // NON-CHARGING _bus.Read8 at (PC - 2) — the d fetch was already charged once by Step's
    // _cycles += __r.Length (D10/H3). Then compute the EA (reuse EmitZ80IndexedEa) and publish WZ = EA.
    // The index register (IX vs IY) is the compound key's p1 byte (>> 16, NOT >> 8 — the 24-bit key).
    private static void EmitZ80DdCbBody(
        StringBuilder sb, InstructionModel insn, string pc, string pcType, string? statusReg, FlagBitMap flags)
    {
        string f = statusReg ?? "F";
        string ix = (insn.OperationKey >> 16) == 0xDD ? "IX" : "IY";   // 24-bit compound key (H1)
        string op = Unquote(insn.Ops[0].Args[0]);                       // "RLC".."SRL" | "BIT"/"RES"/"SET"
        int index = int.Parse(insn.Ops[0].Args[1], System.Globalization.CultureInfo.InvariantCulture);
        string copyReg = Unquote(insn.Ops[0].Args[2]);                  // "B".."A" | "-" (no copy)

        // D10: re-read the displacement via the non-charging raw bus read at (PC - 2).
        sb.AppendLine($"        byte d = _bus.Read8(unchecked((ushort)({pc} - 2)));");
        EmitZ80IndexedEa(sb, ix, "d");   // -> ushort __ea = unchecked((ushort)(IX + (sbyte)(d)));
        EmitWz(sb, "__ea");              // WZ = the computed EA (the (IX+d) MEMPTR — all compound forms)

        bool isBit = op == "BIT";
        bool isResSet = op is "RES" or "SET";

        if (!isBit && !isResSet)
        {
            // x=0: rotate/shift the EA byte. RMW + the CB rotate/shift flag word + the store-copy.
            string cMask = $"0x{(byte)(1 << flags.BitOf("C")):X2}";
            string oldCarry = $"(({f} & {cMask}) != 0 ? 1 : 0)";
            sb.AppendLine("        byte v = ReadBus(__ea);");
            EmitRotateMath(sb, op, oldCarry);          // -> byte r + int cout (reused from M3.4b)
            EmitZ80CbRotateFlags(sb, f, flags);        // the full S/Z/Y/H(=0)/X/P/N(=0)/C(=cout) word (reused)
            sb.AppendLine("        WriteBus(__ea, r);");
            if (copyReg != "-")
                sb.AppendLine($"        {copyReg} = r;");   // the undoc store-copy (z != 6) — plain register (H5)
            // 23 T: -2 key bytes charged by Step (the DD+CB M1; the d+op fetches are also in __r.Length=4,
            // so Step charged 4 — see the cycle note), -1 ReadBus(__ea), -1 WriteBus(__ea).
            sb.AppendLine($"        _cycles += {23 - 4 - 1 - 1};");   // PIN against dd cb __ 06.json
            return;
        }

        if (isResSet)
        {
            EmitZ80DdCbResSet(sb, op, index, copyReg);   // Task 3
            return;
        }
        EmitZ80DdCbBit(sb, f, flags, index);             // Task 3
    }
```

  > **The cycle arithmetic (PIN at Task 0):** `Step` charges `__r.Length` = 4 (the 4 stream bytes via the
  > non-charging fetch stream + `_cycles += 4`). The body's `_bus.Read8(PC-2)` does NOT charge (D10). The
  > body's `ReadBus(__ea)` + `WriteBus(__ea, r)` charge 2. So for a 23 T op: `23 - 4 - 1 - 1 = 17` internal.
  > **CONFIRM the `- 4` (Step charges 4, not 2): if `Step`'s `_cycles += __r.Length` charges 4 for the
  > compound, the body subtracts 4. If the e-2 R/cycle model charges differently for the compound length,
  > re-derive from `dd cb __ 06.json` (the total = the vector's `cycles` array length = 23).** This is the
  > single arithmetic to verify against the vector before trusting the literal.

- [ ] **Step 9: Iterate the rotate test to green.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80DdCbRotateTests"` → PASS (RLC rotates the EA, WZ=EA, the
  CB flags, the store-copy writes B, the no-copy leaves B).
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 10: Full-suite gate + commit.**
  Run: `dotnet test` → baseline + new tests, 0 failures. `RegeneratedSpecTests` green.
  Commit: `feat(z80): M3.4e-3 Task 2 — the rotate/shift compound (IX+d) emit arm + the store-copy`

---

### Task 3: The BIT + RES/SET compound emit arms (the X/Y-from-(EA>>8) quirk + the store-copy) (TDD, synthetic)

> Implement `EmitZ80DdCbBit` (the X/Y-from-(EA>>8) quirk, no store, z-ignored) and `EmitZ80DdCbResSet` (the
> RMW + the store-copy). Two synthetic test files. Mirror the M3.4b `EmitZ80CbBit`/`EmitZ80CbResSet` flag
> math exactly, swapping `(HL)` for `__ea` and adding the store-copy for RES/SET.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitZ80DdCbBit`, `EmitZ80DdCbResSet`)
- Test: `tests/CpuEmulator.Tests/Generators/Z80DdCbBitTests.cs`, `Z80DdCbResSetTests.cs` (create)

- [ ] **Step 1: Write the failing BIT test.** Create `Z80DdCbBitTests.cs`: a synthetic `DD CB d 46`
  (BIT 0,(IX+d)) row. Drive a stream where IX+d → EA, mem[EA] has bit 0 set/clear, and the EA's HIGH byte
  has known bits 3/5. Assert: Z = (bit clear), H=1, N=0, C preserved, S = (bit set && index==7), and
  **F's X/Y come from `(EA>>8)` bits 3/5, NOT from mem[EA]**. Also assert a `DD CB d 40` (BIT 0,(IX+d),
  z=0) produces the IDENTICAL result (z ignored — no store-copy). Mirror the e-2 fixture shape.

- [ ] **Step 2: Run the BIT test → FAIL** (the BIT path throws — `EmitZ80DdCbBit` absent).

- [ ] **Step 3: Implement `EmitZ80DdCbBit`.** In `CpuEmitter.cs`, after `EmitZ80DdCbBody`. This is the
  M3.4b `EmitZ80CbBit` (`:2465-2495`) with `xy = (byte)(__ea >> 8)` (the DDCB BIT quirk — X/Y from the EA
  high byte) and no store:

```csharp
    /// <summary>BIT y,(IX+d): test bit y of ReadBus(EA). Z=(bit==0), P/V=Z, H=1, N=0, S=(y==7 && bit set),
    /// C preserved. X/Y from the EA HIGH byte (EA>>8) — the documented DDCB BIT quirk (the value's bits
    /// are NOT used). NO store-copy (z is ignored for BIT). __ea + WZ are already emitted by the preamble.
    /// Cycles 20 T.</summary>
    private static void EmitZ80DdCbBit(StringBuilder sb, string f, FlagBitMap flags, int bit)
    {
        string sMask = $"0x{(byte)(1 << flags.BitOf("S")):X2}";
        string zMask = $"0x{(byte)(1 << flags.BitOf("Z")):X2}";
        string yMask = $"0x{(byte)(1 << flags.BitOf("Y")):X2}";
        string hMask = $"0x{(byte)(1 << flags.BitOf("H")):X2}";
        string xMask = $"0x{(byte)(1 << flags.BitOf("X")):X2}";
        string pMask = $"0x{(byte)(1 << flags.BitOf("P")):X2}";
        string cMask = $"0x{(byte)(1 << flags.BitOf("C")):X2}";
        sb.AppendLine("        byte v = ReadBus(__ea);");
        sb.AppendLine($"        bool bitSet = (v & 0x{(1 << bit):X2}) != 0;");
        sb.AppendLine("        int xy = (byte)(__ea >> 8);");   // the DDCB BIT quirk: X/Y from the EA high byte
        sb.AppendLine($"        {f} = unchecked((byte)(({f} & {cMask})");   // C preserved
        sb.AppendLine($"            | (bitSet ? 0x00 : {zMask})");           // Z = (bit == 0)
        sb.AppendLine($"            | {hMask}");                              // H = 1
        sb.AppendLine($"            | (bitSet ? 0x00 : {pMask})");           // P/V = Z
        sb.AppendLine($"            | ((bitSet && {bit} == 7) ? {sMask} : 0x00)");  // S = (y==7 && bit set)
        sb.AppendLine($"            | ((xy & 0x20) != 0 ? {yMask} : 0x00)");  // Y from (EA>>8) bit 5
        sb.AppendLine($"            | ((xy & 0x08) != 0 ? {xMask} : 0x00)));"); // X from (EA>>8) bit 3
        // 20 T: -4 (Step), -1 ReadBus(__ea); no write. PIN against dd cb __ 46.json.
        sb.AppendLine($"        _cycles += {20 - 4 - 1};");
    }
```

- [ ] **Step 4: Iterate the BIT test → PASS.**

- [ ] **Step 5: Write the failing RES/SET test.** Create `Z80DdCbResSetTests.cs`: a `DD CB d 86`
  (RES 0,(IX+d), no copy) + a `DD CB d 80` (LD B,RES 0,(IX+d)) + a `DD CB d c6` (SET 0,(IX+d)). Assert: the
  EA byte has bit 0 cleared/set + written back; NO flag change (F preserved); the store-copy writes B for
  the z=0 forms; B untouched for z=6. **Assert Q per the Task-0 finding (Q=0 if the vectors show RES/SET
  preserve-F-leaves-Q-0; or Q=preserved-F if not).**

- [ ] **Step 6: Run the RES/SET test → FAIL** (`EmitZ80DdCbResSet` absent).

- [ ] **Step 7: Implement `EmitZ80DdCbResSet`.** The M3.4b `EmitZ80CbResSet` (`:2499`) with `__ea` + the
  store-copy:

```csharp
    /// <summary>RES/SET y,(IX+d): clear/set bit y of ReadBus(EA), write back. NO flag change. The undoc
    /// store-copy (z != 6) ALSO writes the result to the plain register. __ea + WZ are already emitted by
    /// the preamble. Cycles 23 T.</summary>
    private static void EmitZ80DdCbResSet(StringBuilder sb, string op, int bit, string copyReg)
    {
        string mask = $"0x{(1 << bit):X2}";
        string expr = op == "SET" ? $"(v | {mask})" : $"(v & ~{mask})";
        sb.AppendLine("        byte v = ReadBus(__ea);");
        sb.AppendLine($"        byte r = unchecked((byte){expr});");
        sb.AppendLine("        WriteBus(__ea, r);");
        if (copyReg != "-")
            sb.AppendLine($"        {copyReg} = r;");   // the undoc store-copy (z != 6) — plain register (H5)
        // 23 T: -4 (Step), -1 ReadBus(__ea), -1 WriteBus(__ea). PIN against dd cb __ 86.json.
        sb.AppendLine($"        _cycles += {23 - 4 - 1 - 1};");
    }
```

  > **The RES/SET Q resolution (from Task 0 Step 7):** if the vectors show RES/SET end with Q=0 (no F
  > write), and the class-level `Z80WritesFlags => true` would force `Q = F;` at the body end, then either
  > (a) the body must explicitly `Q = 0;` after the RMW (overriding the lifecycle), or (b) split
  > `Z80WritesFlags` to return false for the RES/SET sub-family (requires the predicate to see the op
  > family — it sees the op record, so this is expressible). **Implement whichever the vector forces;
  > document the choice in the body comment + the closeout.**

- [ ] **Step 8: Iterate the RES/SET test → PASS.**
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 9: Full-suite gate + commit.**
  Run: `dotnet test` → baseline + new tests, 0 failures. `RegeneratedSpecTests` green.
  Commit: `feat(z80): M3.4e-3 Task 3 — the BIT (X/Y-from-EA-high quirk) + RES/SET compound (IX+d) arms`

---

### Task 4: The disassembler compound-key fix + the classify test (synthetic generation does not throw) (TDD)

> Fix the disassembler `Indexed` arm's IX/IY discriminator for 24-bit compound keys (H1/D11), and add a
> classify test proving a synthetic DDCB spec (one row per family) classifies, compiles, and the
> disassembler arm does not throw.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (the disassembler `Indexed` arm)
- Test: `tests/CpuEmulator.Tests/Generators/Z80DdCbClassifyTests.cs` (create)

- [ ] **Step 1: Fix the disassembler `Indexed` arm (H1).** In `CpuEmitter.cs:3305-3306`, the `"Indexed"`
  arm's IX/IY discriminator must handle BOTH the 16-bit plain key (e-2 core, `>> 8`) and the 24-bit
  compound key (e-3, `>> 16`):

```csharp
                // M3.4e-2 (IX+d): the index register is the prefix in the OperationKey. For a plain
                // prefixed key (0xDD7E) it is >> 8; for a 24-bit COMPOUND key (0xDDCB46, M3.4e-3) it is
                // >> 16. The disassembly string is NOT vector-gated — it need only be well-formed.
                "Indexed" =>
                    $"            0x{instruction.OperationKey:X} => $\"{m} ({((instruction.OperationKey > 0xFFFF ? instruction.OperationKey >> 16 : instruction.OperationKey >> 8) == 0xDD ? "IX" : "IY")}+${{operandLo:X2}})\",",
```

- [ ] **Step 2: Write the classify test.** Create `Z80DdCbClassifyTests.cs`, mirroring
  `Z80IndexedClassifyTests.cs`: a synthetic DDCB spec with one row per family (RLC/BIT/RES/SET — with and
  without the store-copy), asserting no GENERATOR error diagnostics + the disassembler does not throw on a
  compound key + the `Disassemble(0xDDCB06, …)` returns a well-formed `RLC (IX+...)` string (not `???`,
  not a throw).

```csharp
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80DdCbClassifyTests
{
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcb")]
        public static class DdCbSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8),
                new("IXh", 8), new("IXl", 8), new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("IX", 16, HighHalf: "IXh", LowHalf: "IXl"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)],
                ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0xCB, 0x06, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "-")]),  // no copy
                Insn(0xDD, 0xCB, 0x00, "RLC", AddrMode.Indexed, [DdCb("RLC", 0, "B")]),  // store-copy
                Insn(0xDD, 0xCB, 0x46, "BIT", AddrMode.Indexed, [DdCb("BIT", 0, "-")]),
                Insn(0xDD, 0xCB, 0x80, "RES", AddrMode.Indexed, [DdCb("RES", 0, "B")]),
                Insn(0xDD, 0xCB, 0xC6, "SET", AddrMode.Indexed, [DdCb("SET", 0, "-")]),
            ];
        }
        """;

    [Fact]
    public void DdCb_rows_classify_compile_and_disassemble_without_throwing()
    {
        var (asm, diagnostics) = GeneratorTestHost.Compile(Spec);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // The disassembler arm must be well-formed for a 24-bit compound key (H1/D11).
        var cpu = GeneratorTestHost.CompileAndLoadType(Spec, "ddcb");
        Assert.NotNull(cpu);
    }
}
```

  > Adjust `GeneratorTestHost.Compile`/`CompileAndLoadType` to the exact helper names the existing classify
  > tests use (grep `Z80IndexedClassifyTests` / `Z80EdClassifyTests`).

- [ ] **Step 3: Run → PASS** (no error diagnostics; the disassembler does not throw).
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 4: Full-suite gate + commit.**
  Run: `dotnet test` → baseline + new tests, 0 failures. `RegeneratedSpecTests` green.
  Commit: `feat(z80): M3.4e-3 Task 4 — the compound-key disassembler fix + the DDCB classify test`

---

### Task 5: The D3 derivation — `Z80DdCbSemantics.OpsFor` + the importer compound-row emission (TDD)

> Implement the `Z80DdCbSemantics` derivation (the CB octal encoding re-targeted onto `(IX+d)` + the
> store-copy slot), wire the importer to route `0xDDCB`/`0xFDCB` AND emit the compound `Insn` overload + the
> compound `PrefixByte` declaration (H4). This task makes the derivation + the importer correct; the regen
> (Task 6) consumes it.

**Files:**
- Create: `tools/CpuEmulator.SpecImporter/Z80DdCbSemantics.cs`
- Modify: `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs` (the routing + the compound emission + the
  `Decode`-declaration compound `PrefixByte`)
- Test: `tests/CpuEmulator.Tests/Importer/Z80DdCbSemanticsTests.cs` (create)

- [ ] **Step 1: Write the failing derivation test.** Create `Z80DdCbSemanticsTests.cs`: a truth table for
  `Z80DdCbSemantics.OpsFor(finalOpcode)`:

```csharp
using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80DdCbSemanticsTests
{
    [Theory]
    // x=0 rotate/shift: rot[y] (IX+d), copy = reg[z] (z=6 -> "-").
    [InlineData(0x00, "[DdCb(\"RLC\",0,\"B\")]")]    // z=0 -> copy B
    [InlineData(0x06, "[DdCb(\"RLC\",0,\"-\")]")]    // z=6 -> no copy
    [InlineData(0x04, "[DdCb(\"RLC\",0,\"H\")]")]    // z=4 -> copy H (PLAIN H, not IXh — H5)
    [InlineData(0x3E, "[DdCb(\"SRL\",0,\"-\")]")]    // y=7 rot SRL, z=6
    [InlineData(0x38, "[DdCb(\"SLL\",0,\"B\")]")]    // y=6 -> SLL (undoc), z=0
    // x=1 BIT: bit index = y; NO copy (always "-"); z ignored.
    [InlineData(0x46, "[DdCb(\"BIT\",0,\"-\")]")]
    [InlineData(0x40, "[DdCb(\"BIT\",0,\"-\")]")]    // z=0 -> STILL "-" (BIT never copies)
    [InlineData(0x7E, "[DdCb(\"BIT\",7,\"-\")]")]    // y=7
    // x=2 RES / x=3 SET: bit index = y; copy = reg[z] (z=6 -> "-").
    [InlineData(0x86, "[DdCb(\"RES\",0,\"-\")]")]
    [InlineData(0x80, "[DdCb(\"RES\",0,\"B\")]")]
    [InlineData(0xC6, "[DdCb(\"SET\",0,\"-\")]")]
    [InlineData(0xFF, "[DdCb(\"SET\",7,\"A\")]")]    // y=7, z=7 -> copy A
    public void OpsFor_derives_the_compound_optext(int finalOpcode, string expected)
        => Assert.Equal(expected, Z80DdCbSemantics.OpsFor(finalOpcode));

    [Fact]
    public void OpsFor_is_total_over_all_256_final_opcodes()
    {
        for (int op = 0; op <= 0xFF; op++)
            Assert.NotNull(Z80DdCbSemantics.OpsFor(op));   // no holes (H6)
    }
}
```

- [ ] **Step 2: Run → FAIL** (`Z80DdCbSemantics` absent).

- [ ] **Step 3: Implement `Z80DdCbSemantics`.** Create `tools/CpuEmulator.SpecImporter/Z80DdCbSemantics.cs`,
  mirroring `Z80CbSemantics` (the octal decode) with the store-copy slot:

```csharp
namespace CpuEmulator.SpecImporter;

/// <summary>
/// Computes the micro-op text for a Z80 DDCB/FDCB COMPOUND opcode ALGORITHMICALLY from the FINAL opcode
/// byte (M3.4e-3), the compound analogue of <see cref="Z80CbSemantics"/>. A DD CB d op (FD identical with
/// IY) applies the classic CB octal operation to (IX+d)/(IY+d): x selects the family (0 rotate/shift via
/// rot[y], 1 BIT, 2 RES, 3 SET); for x=0 y selects rot[y]; for x!=0 y is the bit index; z selects the
/// undoc STORE-COPY register reg[z] (B C D E H L (HL) A) — z=6 (the (HL) slot) means NO copy, and BIT
/// (x=1) NEVER copies regardless of z. The store-copy writes the PLAIN register (NOT IXh/IXl). Returns
/// the ops-text for every final opcode 0x00..0xFF — all 256 are owned (the compound page is total; no
/// prefix-byte holes because the byte after the displacement is the operation, never a prefix).
/// The index register (IX vs IY) is NOT encoded here — the emit arm reads it from the compound key's p1.
/// </summary>
public static class Z80DdCbSemantics
{
    private static readonly string[] Reg8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    private static readonly string[] Rot = ["RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL"];

    public static string OpsFor(int finalOpcode)
    {
        int x = (finalOpcode >> 6) & 0x03;
        int y = (finalOpcode >> 3) & 0x07;
        int z = finalOpcode & 0x07;
        // The store-copy register: reg[z] for z != 6, "-" (no copy) for z=6 (the (HL) slot).
        string copy = z == 6 ? "-" : Reg8[z];
        return x switch
        {
            0 => $"[DdCb(\"{Rot[y]}\",0,\"{copy}\")]",        // rotate/shift (IX+d) + store-copy
            1 => $"[DdCb(\"BIT\",{y},\"-\")]",                 // BIT y,(IX+d) — NEVER copies
            2 => $"[DdCb(\"RES\",{y},\"{copy}\")]",            // RES y,(IX+d) + store-copy
            _ => $"[DdCb(\"SET\",{y},\"{copy}\")]",            // SET y,(IX+d) + store-copy
        };
    }
}
```

- [ ] **Step 4: Iterate the derivation test → PASS.**

- [ ] **Step 5: Wire the importer routing.** In `SpecFileEmitter.cs:169-180`, add the `0xDDCB`/`0xFDCB` arm
  to the `z80Ops` ternary:

```csharp
                : isZ80 && entry.Prefix == "0xDD"
                    ? Z80DdFdSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16), entry.Mnemonic, entry.Mode, isIy: false)
                : isZ80 && entry.Prefix == "0xFD"
                    ? Z80DdFdSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16), entry.Mnemonic, entry.Mode, isIy: true)
                : isZ80 && entry.Prefix == "0xDDCB"
                    ? Z80DdCbSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16))
                : isZ80 && entry.Prefix == "0xFDCB"
                    ? Z80DdCbSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16))
                : null;
```

- [ ] **Step 6: Add the compound-emission branch + the compound `PrefixByte` declaration (H4).** This is
  the load-bearing importer change. In `SpecFileEmitter.cs`, the emission loop (`:192-228`) currently emits
  the plain `Insn(prefix, opcode, …)` overload for single-byte prefixes and drops compound tokens to
  `// TODO(mode)`. Add a compound branch BEFORE the `singleBytePrefix` gate. Decide the compound-prefix
  detection: a `TryParseCompoundPrefix(entry.Prefix, out int p1, out int p2)` helper splitting `"0xDDCB"`
  → (0xDD, 0xCB). The emission:

```csharp
            // M3.4e-3: a COMPOUND DDCB/FDCB row — emit the Insn(p1, p2, finalOp, …) overload (KeyShape
            // .Compound) and record the compound prefix for the Decode declaration (H4). Detected by the
            // 4-hex prefix token ("0xDDCB"/"0xFDCB").
            bool compoundPrefix = entry.Prefix is { } cp && TryParseCompoundPrefix(cp, out int cp1, out int cp2);

            if (hasSemantics && modeSupported && compoundPrefix)
            {
                TryParseCompoundPrefix(entry.Prefix!, out int p1, out int p2);
                insnSb.AppendLine(
                    $"        Insn(0x{p1:X2}, 0x{p2:X2}, {entry.Opcode}, \"{entry.Mnemonic}\", AddrMode.{entry.Mode}, {opsText}),");
                emittedCompoundPrefixes.Add((p1, p2));   // (DD, CB) — drives the compound PrefixByte
                emitted++;
            }
            else if (hasSemantics && modeSupported && (entry.Prefix is null || singleBytePrefix))
            {
                // … the EXISTING plain/prefixed emission (unchanged) …
            }
            // … the existing else-if (hasSemantics) // TODO(mode) + else // TODO(semantics) …
```

  And in the `Decode` declaration emitter (`:238-244`), emit the compound `PrefixByte` for any DD/FD prefix
  that backs a compound row (the SAME prefix byte backs both plain core rows AND compound rows — it gains
  the compound metadata):

```csharp
        if (emittedPrefixBytes.Count > 0)
        {
            sb.AppendLine("    public static readonly DecodeStructure Decode = new(");
            // M3.4e-3: a prefix byte that backs a compound row (DD/FD compounding with CB) emits the
            // compound PrefixByte (CompoundWith + DisplacementBeforeOpcode); a plain prefix (CB/ED) emits
            // the bare PrefixByte. The DD/FD bytes back BOTH plain core rows AND compound rows — the
            // compound form turns the walk's compound routing ON (the plain core rows still take the
            // plain-prefix arm because the byte after DD/FD is the opcode, not CB).
            var compoundFirstBytes = emittedCompoundPrefixes.Select(c => c.p1).ToHashSet();
            string prefixList = string.Join(", ", emittedPrefixBytes.Select(b =>
                compoundFirstBytes.Contains(b)
                    ? $"new PrefixByte(0x{b:X2}, CompoundWith: 0x{emittedCompoundPrefixes.First(c => c.p1 == b).p2:X2}, DisplacementBeforeOpcode: true)"
                    : $"new PrefixByte(0x{b:X2})"));
            sb.AppendLine($"        Prefixes: [{prefixList}],");
            sb.AppendLine("        ModRmOpcodes: [],");
            sb.AppendLine("        SubFieldOpcodes: []);");
        }
```

  Declare `emittedCompoundPrefixes` (a `List<(int p1, int p2)>` or `HashSet`) alongside `emittedPrefixBytes`,
  and add the DD/FD first byte to `emittedPrefixBytes` too (so the `Decode` declaration includes it — it may
  already be there from the core rows; a `HashSet` dedups). Add the `TryParseCompoundPrefix` helper near
  `TryParsePrefixByte` (`:259`-region):

```csharp
    /// <summary>Parse a compound prefix token ("0xDDCB"/"0xFDCB") into its two bytes (0xDD, 0xCB). Returns
    /// false for a single-byte or null prefix (M3.4e-3).</summary>
    private static bool TryParseCompoundPrefix(string prefix, out int p1, out int p2)
    {
        p1 = p2 = 0;
        if (prefix is not { Length: 6 } || !prefix.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(prefix.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out p1)
            && int.TryParse(prefix.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out p2);
    }
```

  > **CPUGEN012 (H4):** the compound rows now back the declared compound `PrefixByte`, so the cross-check
  > (every declared prefix backs ≥1 emitted row) is satisfied. CONFIRM the e-1b CPUGEN012 extension accepts
  > a `KeyShape.Compound` row as backing for a compound-flagged `PrefixByte` — if it only counts
  > `KeyShape.PrefixedOpcode` rows, the DD/FD prefix is STILL backed by the plain core rows (252 each), so
  > CPUGEN012 passes regardless. Verify at Task 0 + here.

- [ ] **Step 7: Write the importer round-trip test (in the derivation test file or a new importer test).**
  Assert the importer, run over a minimal DDCB dataset row, emits an `Insn(0xDD, 0xCB, …)` line + a
  compound `PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)` line (mirror the existing
  importer-emission tests; grep for how the DD/FD core importer emission is tested).

- [ ] **Step 8: Build + the importer tests green.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80DdCb|FullyQualifiedName~Importer"` → PASS.
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 9: Full-suite gate + commit** (no `Z80Spec.cs` regen yet — the dataset rows + regen are Task 6).
  Run: `dotnet test` → baseline + new tests, 0 failures. `RegeneratedSpecTests` green.
  Commit: `feat(z80): M3.4e-3 Task 5 — Z80DdCbSemantics derivation + the importer compound-row emission`

---

### Task 6: The dataset rows + the atomic regen (the 256 DDCB + 256 FDCB rows go live) (TDD)

> Add the 225 DDCB + 225 FDCB derived dataset rows (the F1 gap), regenerate `Z80Spec.cs`, and prove the
> 6502 + base/CB/ED/block/DD-FD planes stay byte-identical/green. This is the ATOMIC task — the rows + the
> routing + the compound `decode.prefixes` declaration go live together (H4/G2: declaring the compound
> prefix without backing rows would trip CPUGEN012; emitting rows without the declaration would mis-decode).

**Files:**
- Modify: `tools/CpuEmulator.SpecImporter/data/z80-opcodes.json` (add 225 + 225 rows)
- Modify (regenerated): `src/CpuEmulator.Cpus.Z80/Z80Spec.cs`
- Test: `tests/CpuEmulator.Tests/Importer/Z80DdCbSemanticsTests.cs` (extend with the 256+256 row-count
  cross-check)

- [ ] **Step 1: Add the dataset rows.** Add the 225 missing DDCB rows + 225 FDCB rows to
  `z80-opcodes.json`. Each row: `{ "prefix": "0xDDCB", "opcode": "0xNN", "mnemonic": "<RLC|…|BIT|RES|SET>",
  "mode": "Indexed", "bytes": 4, "cycles": <23|20> }` (the mnemonic + cycles per the family; the importer
  derives the ops via `Z80DdCbSemantics`, so the mnemonic need only be plausible for the disassembler — but
  set it correctly per the family for the `// TODO`/diagnostic text + the disassembly). The cleanest path:
  generate the rows programmatically (a script that emits all 256 final opcodes × 2 planes, minus the 31+31
  already present) so they are complete + consistent. **Confirm the existing 31+31 rows' exact JSON shape
  (mnemonic casing, the `bytes`/`cycles` values) and match it** so the dataset stays uniform.

  > **The mnemonic field:** the importer BYPASSES the per-mnemonic map for DDCB rows (the `z80Ops` non-null
  > path wins, the M3.4c F3 mechanism), so the mnemonic does NOT need a `SemanticsMap` entry — it is only
  > the disassembly/diagnostic label. Set it to the family (`RLC`/`BIT`/`RES`/`SET`/…) for readability.

- [ ] **Step 2: Run the regen.** Run the SpecImporter to regenerate `Z80Spec.cs`:
  Run: the importer command (grep the prior plans / the build scripts for the exact invocation, e.g.
  `dotnet run --project tools/CpuEmulator.SpecImporter -- <args>`).
  Inspect `Z80Spec.cs`: confirm 256 DDCB + 256 FDCB `Insn(0xDD, 0xCB, …)` / `Insn(0xFD, 0xCB, …)` rows; the
  `Decode` declaration now has `new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)`
  (+ FD); NO `// TODO(mode)` for DDCB/FDCB rows.

- [ ] **Step 3: Extend the row-count cross-check (the F1 guard).** In `Z80DdCbSemanticsTests.cs`, assert the
  dataset has 256 DDCB + 256 FDCB compound rows after the add (load the dataset + count `prefix == "0xDDCB"`
  / `"0xFDCB"`). Mirror the e-2 DD/FD F1 cross-check test.

- [ ] **Step 4: Build + the 6502 byte-identity guard + the whole suite green.**
  Run: `dotnet build --no-incremental -warnaserror` → clean (the 256+256 new rows compile; the new
  `Z80DdCb` bodies emit).
  Run: `dotnet test` → 0 failures. **`RegeneratedSpecTests` (the 6502 byte-identity) green** (no 6502
  artifact changed). The base/CB/ED/block/DD-FD core theories stay green (the compound `PrefixByte` does
  NOT change the plain-prefix decode for the core rows — confirm a sample DD/FD core opcode, e.g.
  `dd 7e.json`, still passes; the compound routing fires ONLY when the byte after DD/FD is CB).

  > **The decode-regression risk (the load-bearing gate):** declaring DD/FD as compound prefixes changes the
  > walk's behavior for a `DD CB …` stream (now the compound arm), but MUST NOT change a `DD <non-CB>`
  > stream (still the plain arm). The e-1b walk already guards this (`s_compoundWith.TryGetValue(first, out
  > second) && op == second` — the compound arm fires ONLY when `op == CB`). **Re-run a sample of the DD/FD
  > CORE theories here to prove no core regression** (the 252+252 must stay green).

- [ ] **Step 5: Commit.**
  Commit: `feat(z80): M3.4e-3 Task 6 — the 256 DDCB + 256 FDCB rows live (atomic regen + compound decode)`

---

### Task 7: The TomHarte DDCB + FDCB gate (the 512 compound vectors green) (TDD)

> Add the `CoveredDdCbPlaneOpcodes`/`CoveredFdCbPlaneOpcodes` theories (the 4-token filename + the
> `0xDDCB00 | op` compound probe key), and drive all 512 compound vectors green at the universal Q/WZ/IM
> bar with IX/IY checked. This is the slice's TomHarte gate.

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs` (the two compound theories)

- [ ] **Step 1: Add the compound theories.** Mirror `CoveredDdPlaneOpcodes`/`Dd_opcode_matches_TomHarte_
  vectors` (`Z80TomHarteTests.cs:173-218`), keying the probe on the 24-bit compound key and the filename on
  the 4-token shape:

```csharp
    /// <summary>The covered DDCB-compound opcodes — present in the generated dispatch (Disassemble !=
    /// "???"). Probed via the COMPOUND key (0xDDCB00 | op), NOT the plain 0xDD00 | op. All 256 final
    /// opcodes are vectored (no prefix-byte holes — the byte after the displacement is the operation).</summary>
    public static TheoryData<byte> CoveredDdCbPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0x00; op <= 0xFF; op++)
            if (Z80Cpu.Disassemble((uint)(0xDDCB00 | op), 0, 0) != "???")
                data.Add((byte)op);
        return data;
    }

    [Z80TomHarteTheory]
    [MemberData(nameof(CoveredDdCbPlaneOpcodes))]
    public void DdCb_opcode_matches_TomHarte_vectors(byte opcode)
    {
        string dir = Z80TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, $"dd cb __ {opcode:x2}.json");   // 4 tokens, literal __ placeholder
        Assert.True(File.Exists(path), $"vector file missing: {path}");
        var cases = Z80TomHarteLoader.LoadFile(path);
        // … the sample-size + registersOnly + run-loop boilerplate, identical to Dd_opcode_… …
    }

    /// <summary>The covered FDCB-compound opcodes — the IY analogue. Probed via (0xFDCB00 | op).</summary>
    public static TheoryData<byte> CoveredFdCbPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0x00; op <= 0xFF; op++)
            if (Z80Cpu.Disassemble((uint)(0xFDCB00 | op), 0, 0) != "???")
                data.Add((byte)op);
        return data;
    }

    [Z80TomHarteTheory]
    [MemberData(nameof(CoveredFdCbPlaneOpcodes))]
    public void FdCb_opcode_matches_TomHarte_vectors(byte opcode)
    {
        string dir = Z80TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, $"fd cb __ {opcode:x2}.json");   // 4 tokens
        Assert.True(File.Exists(path), $"vector file missing: {path}");
        var cases = Z80TomHarteLoader.LoadFile(path);
        // … the run-loop boilerplate …
    }
```

  > **The probe-count guard:** `CoveredDdCbPlaneOpcodes` must yield 256 (all final opcodes covered). A probe
  > of fewer than 256 means a derived row failed to emit (the M3.4c discipline). The theory's
  > `Assert.True(File.Exists(path))` + `Assert.True(run > 0)` catch a missing vector / empty file.

- [ ] **Step 2: Run the compound theories (sampled).**
  Run: `dotnet test --filter "FullyQualifiedName~DdCb_opcode_matches|FullyQualifiedName~FdCb_opcode_matches"`
  → green at the default sample. Iterate any failures via the systematic-debugging discipline (re-derive the
  failing op's WZ/cycle/flag/store-copy from its vector — the oracle; the most likely failures: the cycle
  arithmetic in the body's `_cycles +=` (the `- 4` Step charge), the RES/SET Q handling (Task 3 Step 7), the
  BIT X/Y-from-EA-high, the store-copy register on z≠6).

- [ ] **Step 3: The full UAT sweep.** Run the full sweep over all 512 compound opcodes:
  Run: `CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~DdCb_opcode_matches|FullyQualifiedName~FdCb_opcode_matches"`
  → 512,000 cases, 0 failures. (Match the env-var pattern the e-2 closeout used; grep `CPUEMULATOR_UAT` in
  the test for the exact gate.)

- [ ] **Step 4: The whole-Z80 regression sweep.** Confirm the ENTIRE Z80 (base + CB + ED + block + DD/FD
  core + DDCB/FDCB) stays green:
  Run: `dotnet test` → 0 failures. The 6502 byte-identity guard green.

- [ ] **Step 5: Commit.**
  Commit: `feat(z80): M3.4e-3 Task 7 — the 512 DDCB/FDCB compound vectors TomHarte-green`

---

### Task 8: Closeout + the slice-docs cross-links + the M3.4e-completion note

**Files:**
- Modify: this file (the closeout)
- Modify: `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md` (the M3.4e-3 section + the docs index
  + the M3.4e-completion milestone note)

- [ ] **Step 1: Write the honest closeout** in this file: the EXACT pinned values (the per-op WZ/cycle/flag
  rules; the RES/SET Q resolution; the store-copy register-set; the BIT X/Y-from-EA-high), the final test
  count, the 512,000-case green assertion, the enumerated in/out-of-scope (the redundant-prefix chains
  unverified; JIT-IL = M3.5), and the D9–D11 decisions as-shipped (esp. the D10 `_bus.Read8(PC-2)` seam +
  any RES/SET per-family Q split).

- [ ] **Step 2: Update the scoped plan.** In `…-ixiy-prefixes.md`, mark the M3.4e-3 section DONE with a
  pointer to this plan; add this plan to the slice-docs index; **add the M3.4e-completion milestone note**
  (with e-3 green, the entire documented + undocumented Z80 ISA is TomHarte-green — only interrupt
  servicing + ZEXALL + Z80-through-JIT remain = M3.5).

- [ ] **Step 3: Final gate + the docs commit + the PR.**
  Run: `dotnet test` → green. `dotnet build --no-incremental -warnaserror` → clean.
  Commit: `docs(z80): M3.4e-3 closeout — the entire Z80 ISA TomHarte-green (M3.4e complete)`
  Open the PR targeting `main`; the body enumerates the close-state + the D9–D11 decisions + the Docs Impact.

---

## Invariants (carried forward — non-negotiable)

- TDD task-by-task; full gate after each task: `dotnet build --no-incremental -warnaserror` clean; targeted
  tests green; `RegeneratedSpecTests` (6502 byte-identity) green; base/CB/ED/block/DD/FD stay green at the
  universal Q/WZ/IM bar; IX/IY checked; the new DDCB/FDCB vectors green.
- The dataset→importer→regen→generator pipeline only — never hand-edit `Z80Spec.cs`.
- Synthetic-spec tests (`GeneratorTestHost.CompileAndLoadType`) decouple per-task from the regen, which
  lands atomically late (Task 6). Structured fixtures use `IAddressSpace _bus`, declare `public byte Q;` +
  `public int Im;`, and name `IX`/`IY`/`IXh`/`IXl` + the plain `B`..`A` where the body references them.
- Every 6502 artifact byte-identical.
- The honest close-state: the closeout enumerates exactly what is + isn't covered (the redundant-prefix
  chains unverified; JIT-IL = M3.5). With e-3 green, this is the **M3.4e-completion milestone** — the entire
  documented + undocumented Z80 ISA is TomHarte-green.

## Slice docs index

- **Overview / sequencing:** `docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md`
- **M3.4e-1 framework (MERGED #24/#25):** `…-ixiy-e1a-addrmode-ea.md`, `…-ixiy-e1b-compound-decoder.md`
- **M3.4e-2 DD/FD core (MERGED #27):** `…-ixiy-e2-ddfd-core.md`
- **M3.4e-3 DDCB/FDCB compound (this plan):** `…-ixiy-e3-ddcb-fdcb.md`
- **Scoped parent:** `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md`
- **Depth template + close-state record:** `…-m3-z80-ed-core.md`, `…-m3-z80-ed-block-ops.md`
- **Next slice:** `docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md`
- **Architecture (Decisions 1, 3, 4, 7):** `docs/architecture/0001-z80-second-architecture.md`
