# M3.4e: The Z80 DD/FD/DDCB/FDCB IX/IY Prefixes — Scoped Plan

> **For agentic workers:** this is a SCOPED plan — the finish-line, PR breakdown, dependencies, and known
> quirks/risks are pinned, but the deepest task-by-task literal code is to be DETAILED JUST-IN-TIME before
> each sub-PR's Builder run, because it depends on design decisions (D1–D4 below) and on what the framework
> PR (M3.4e-1) reveals. Where literal code is given it is load-bearing (the decode walk, the EA). Use
> superpowers:writing-plans to expand each sub-PR into a full task-by-task plan immediately before building
> it. The depth template is `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md`.

**Goal:** make the Z80 DD/FD/DDCB/FDCB planes TomHarte-green — the `(IX+d)`/`(IY+d)` indexed addressing,
the IX/IY 16-bit ops, the undocumented IXh/IXl 8-bit ops, and the compound `DD CB d op` bit/rotate/shift
forms (incl. the undocumented "store-copy" forms) — by adding the two-deep compound-prefix decoder, the
`Indexed` AddrMode, and the `(IX+d)` EA computation to the framework.

**Architecture:** the Z80 base + CB + ED planes are TomHarte-green (PR #22 + M3.4d). This slice is the LAST
and LARGEST decode/addressing reshaping: ADR 0001 Decision 1 (the central decode decision — the compound
`DD CB d op` form puts the displacement byte BETWEEN the prefix and the opcode, which the current
single-byte/single-prefix decode walk cannot express) and Decision 3 (IX/IY as registers — already declared
in `z80-semantics.json` as 16-bit). With this slice the interpreter covers the ENTIRE documented +
undocumented Z80 ISA — the precondition for ZEXALL (M3.5). Every 6502 artifact stays byte-identical.

**Tech Stack:** as the predecessor plans (C# .NET 10, the Roslyn generator, the SpecImporter, xUnit + the
SingleStepTests/z80 vectors).

---

## Scope

**IN scope (the entire IX/IY surface):**

1. **The framework: the compound-prefix decoder + `Indexed` AddrMode + `(IX+d)` EA** (M3.4e-1).
2. **DD/FD core** (M3.4e-2): the `(IX+d)`/`(IY+d)` re-interpretation of the base ops (LD r,(IX+d);
   ADD A,(IX+d); INC (IX+d); etc.), the IX/IY 16-bit ops (ADD IX,rr; ADC/SBC handled via ED only — N/A
   here; INC/DEC IX; LD IX,nn; LD IX,(nn); LD (nn),IX; PUSH/POP IX; JP (IX); EX (SP),IX; LD SP,IX), and the
   **undocumented IXh/IXl 8-bit ops** (LD B,IXh; ADD A,IXl; etc. — the DD prefix splitting IX into halves).
3. **DDCB/FDCB compound** (M3.4e-3): bit/rotate/shift on `(IX+d)` (BIT/RES/SET/RLC/.../SRL n,(IX+d)),
   INCLUDING the **undocumented "store-copy" forms** (a DDCB op whose register field ≠ 6 performs the
   operation on `(IX+d)` AND copies the result into the named register — e.g. `DD CB d 00` = `RLC (IX+d)`
   with the result also stored in B).

**OUT of scope (later / separate):**

- **The DD/FD "no-op prefix" chains** (`DD DD`, `DD FD`, `DD ED`, `FD …`) — the silicon treats a redundant
  prefix as "re-decode from the last prefix"; the vectors for these (`dd dd` etc.) DO NOT EXIST in the cache
  (only the 252 standalone `dd NN` + the 256 `dd cb __ NN`). So they are not single-step-gated here — note
  in the closeout that prefix-chain behavior is unverified-pending-a-dedicated-test (likely never needed for
  ZEXALL). CONFIRM the vector absence at Task 0.
- **Interrupt SERVICING / ZEXALL / the JIT** = M3.5.
- **DDCB/FDCB through the JIT** — these emit as JIT FALLBACKS only in M3.4e (D4); IL emission is M3.5+.

> **The honest one-liner for M3.4e's close-state (target):** the Z80 base + CB + ED + DD + FD + DDCB +
> FDCB planes run and are TomHarte-green — the entire documented + undocumented opcode space, per-T-state,
> including `(IX+d)` indexed addressing, the IX/IY 16-bit ops, the undocumented IXh/IXl 8-bit ops, and the
> compound DDCB/FDCB bit/rotate/shift store-copy forms. The DD/FD redundant-prefix chains are unverified
> (no vectors). Interrupt servicing + ZEXALL + the JIT remain M3.5; the IX/IY ops emit as JIT fallbacks.

---

## Vector availability (CONFIRMED) + the F1 dataset-gap risk

| Plane | Vectors | Filename | Dataset rows present | F1 gap to close |
|---|---|---|---|---|
| DD | **252** | `dd 00.json`…`dd ff.json` (minus the 4 prefix bytes cb/dd/ed/fd) | **39** | **~213 rows** to add (M3.4e-2) |
| FD | **252** | `fd 00.json`…`fd ff.json` (minus 4) | **39** | **~213 rows** (M3.4e-2) |
| DDCB | **256** | **`dd cb __ NN.json`** (4 tokens; `__` = displacement placeholder; NN = final opcode) | **31** | **~225 rows** (M3.4e-3) |
| FDCB | **256** | **`fd cb __ NN.json`** (4 tokens) | **31** | **~225 rows** (M3.4e-3) |

**Total F1 gap: ~876 dataset rows.** This is the M3.4c F1 lesson (the dataset was missing 22 of 64) at
~40× scale. **Hand-authoring 876 rows is the single biggest risk in this slice.** D3 (below) recommends
DERIVING the DD/FD rows from the base/CB tables algorithmically (a `Z80DdFdSemantics`), consistent with how
`Z80BaseSemantics`/`Z80CbSemantics`/`Z80EdSemantics` derive their rows — so the gap closes by construction.
Whichever path is chosen, the M3.4e-1/2/3 plans MUST cross-check the dataset row count against the 1,016
vectors before claiming coverage, and the gate is the per-opcode TomHarte sweep (a missing row → the probe
finds `Disassemble == "???"` → the opcode is silently uncovered, the M3.4c probe-vs-emitted discipline).

**The DDCB/FDCB filename trap (load-bearing for the harness):** the compound vectors are FOUR tokens —
`dd cb __ NN.json` — where `__` is a literal two-underscore placeholder (NOT the displacement value; the
actual displacement is in the case's `initial.ram`) and `NN` is the FINAL opcode byte (after the
displacement). The DDCB theory must build `$"dd cb __ {op:x2}.json"`. Do NOT assume a 3-token
`dd cb NN.json`.

---

## Ground truth — the framework seams this slice reshapes (CONFIRMED by recon)

- **The decode walk does NOT handle compound prefixes today.** `src/CpuEmulator.Generators/CpuEmitter.cs`
  `EmitStructuredDecodeWalk` (~`:3322-3384`): it reads the first byte; if it is in `s_prefixBytes` it reads
  the next byte and keys `key = (first << 8) | op`. There is **no** handling of a SECOND prefix byte or a
  displacement BETWEEN prefix and opcode. This is exactly ADR 0001 Decision 1's central gap.
- **`PrefixByte` is just a byte.** `src/CpuEmulator.Core/Specification/DecodeStructure.cs`:
  `public sealed record PrefixByte(byte Value);` — NO "takes a leading displacement" flag, NO
  nested/compound prefix support. The `DecodeStructure(PrefixByte[] Prefixes, byte[] ModRmOpcodes,
  byte[] SubFieldOpcodes)` record must grow to express `DD CB` as a compound prefix that consumes a
  displacement before the opcode.
- **`Indexed` is NOT an AddrMode.** `src/CpuEmulator.Core/Specification/AddrMode.cs` lists 21 members
  through `Bit`; `Indexed` is absent. The mirror tables (`SpecParser.cs:162-172 s_addrModes`,
  `SpecFileEmitter.cs:49-60 SupportedModes`) explicitly exclude it — `SpecFileEmitter.cs:56-57` says
  "Indexed (IX+d, M3.4c) stays OUT — its rows keep emitting // TODO(mode)." This slice adds it.
- **IX/IY ARE declared as registers.** `tools/CpuEmulator.SpecImporter/data/z80-semantics.json`:
  `{ "name": "IX", "bits": 16 }`, `{ "name": "IY", "bits": 16 }` — already present (ADR Decision 3). The
  IXh/IXl 8-bit halves are NOT separately declared; the undoc IXh/IXl ops need either half-views (like
  BC over B/C) or hand-written half-access in the emit arm. **OPEN: D2 covers this.**
- **The TomHarte harness ALREADY parses IX/IY.** `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteCase.cs`:
  `Z80State` carries `ushort Ix, ushort Iy`; `ReadState` parses `U16(e, "ix")`, `U16(e, "iy")`. The runner
  `RunCase(Z80TomHarteCase c, bool registersOnly = false)` sets/checks every register — **IX/IY checks come
  for free** once the runner sets them. CONFIRM the runner sets `cpu.IX`/`cpu.IY` from `s.Ix`/`s.Iy` (it
  may not yet — the base/CB/ED ops never touched IX/IY, so the runner may skip them; M3.4e-1 must add the
  set/check if absent). This is a likely small runner change to flag at Task 0.
- **The dataset prefix field already accepts the compound tokens.** `OpcodeDataset.cs`
  `RecognizedPrefixes` includes `"0xDD"`, `"0xFD"`, `"0xDDCB"`, `"0xFDCB"`. But the `OpcodeFormat` regex
  `^0x[0-9A-Fa-f]{2}$` accepts only a 2-hex OPCODE (the final byte) — fine, because the prefix is a
  separate field. So the dataset CAN already represent a DDCB row as
  `{prefix:"0xDDCB", opcode:"0xNN", ...}` — the gap is rows, not schema.
- **The `Halted` latch + `(IX+d)` reuse the existing bus.** `Z80Cpu.cs` already has `_bus`/`ReadBus`/
  `WriteBus` (the `(IX+d)` EA reads/writes the same program/data bus — no new bus). J4 (ADR Decision 7):
  the fastmem seam is reused unchanged for `(IX+d)`.

---

## PR breakdown (3 sub-PRs, dependency-ordered)

### M3.4e-1 — Framework: the compound-prefix decoder + `Indexed` AddrMode + `(IX+d)` EA

> **EXPANDED (2026-06-14): this section is now detailed in TWO full task-by-task plans (split per the
> "Estimated size" note below + D1–D5).** Build them in order:
> - **e-1a** (`Indexed` AddrMode + 4 mirror tables + `JitMode.Indexed`; IXh/IXl/IYh/IYl half-views incl. the
>   D2 storage-inversion; the `(IX+d)` EA helper): `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1a-addrmode-ea.md`
> - **e-1b** (the declarative `PrefixByte`/`DecodeStructure` D1 extension + `EmitStructuredDecodeWalk`
>   compound routing + the synthetic `DD CB d op` decode-walk test):
>   `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1b-compound-decoder.md`
>
> The two docs carry the load-bearing literal code, the RECON-FINDINGS (incl. the runner ALREADY wires
> IX/IY — no runner change; the D2 storage-inversion; the compound descriptor-length question), per-task
> gates, and the honest close-state. The outline below is retained as the framing the two docs expand.

**Goal:** add the decode + addressing machinery WITHOUT making any DD/FD row live yet; prove the 6502 +
base/CB/ED planes regenerate byte-identically (zero-prefix + single-prefix are the unchanged special cases).

**The load-bearing change — the decode walk.** `EmitStructuredDecodeWalk` must handle:
- `first == 0xDD || first == 0xFD`, then:
  - `second == 0xCB`: a COMPOUND prefix. Read the **displacement** byte, THEN the opcode byte. Produce a
    compound key + a displacement local. The key shape: `key = (0xDDCB << ...) | op` (or a dedicated
    compound key encoding the generator + JIT both index). The displacement is passed to the emit arm.
  - else (a normal DD/FD opcode): `key = (0xDD00) | op` with the HL→IX substitution applied in the emit arm.
- the existing single-byte-prefix + base paths unchanged.

The DECLARATIVE half (D1): extend `PrefixByte`/`DecodeStructure` so the spec DECLARES `DD`/`FD` as prefixes
that MAY be followed by `CB` to form a compound that consumes a leading displacement. Sketch (final shape
decided in D1):

```csharp
// DecodeStructure.cs — the compound-prefix extension (illustrative; final shape per D1):
public sealed record PrefixByte(
    byte Value,
    byte? CompoundWith = null,        // e.g. DD declares CompoundWith: 0xCB
    bool DisplacementBeforeOpcode = false);   // the DD CB d op shape
```

**Tasks (to be detailed just-in-time):** (1) add `Indexed` to `AddrMode` + the 4 mirror tables + the JIT
`JitMode`; (2) extend `PrefixByte`/`DecodeStructure` per D1 + a synthetic decode-walk test proving a
`DD CB d op` stream decodes to (compound key, displacement); (3) extend `EmitStructuredDecodeWalk` to emit
the compound walk; (4) add the `(IX+d)` EA computation helper (read IX/IY + signed displacement) usable by
every indexed emit arm; (5) regen + prove 6502/base/CB/ED byte-identical + green. **GATE:** the framework
change alone keeps everything green; no DD/FD opcode is live yet.

**Estimated size:** large — this is the ADR Decision 1 reshaping. If the `PrefixByte` extension and the
decode-walk emit prove large, split into e-1a (AddrMode + the EA helper) and e-1b (the compound decoder).

### M3.4e-2 — DD/FD core (the (IX+d) re-interpretation + the IX/IY 16-bit + undoc IXh/IXl ops)

**Goal:** add the ~213+213 missing DD/FD dataset rows (D3: derive algorithmically), implement the indexed
emit arms (Load/Store/Alu/Rmw on `(IX+d)`, the IX/IY 16-bit ops, the undoc IXh/IXl), drive `dd *.json` +
`fd *.json` (504 vectors) green.

**Key behavior to model (re-derive each from the vectors — the oracle):**
- A DD prefix on a base op that names H/L/(HL): H→IXh, L→IXl, (HL)→(IX+d). A DD prefix on an op that names
  NEITHER (e.g. `DD 04` = INC B): the prefix is INERT (behaves as the base op but costs the extra M1 / R
  bump). The vectors gate this — `dd 04.json` exists.
- The IX/IY 16-bit ops: ADD IX,rr (the WZ = IX+1 pre-op rule, like ADD HL,rr); INC/DEC IX (no flags);
  LD IX,nn; LD IX,(nn) / LD (nn),IX (WZ = nn+1); PUSH/POP IX; JP (IX) (no WZ); EX (SP),IX (WZ = new IX);
  LD SP,IX.
- The undocumented IXh/IXl 8-bit ops set flags like their B/C/etc. counterparts but operate on the IX
  halves. **The vectors check these — they are IN scope.**
- **WZ for `(IX+d)`:** the indexed memory ops set WZ = the computed `IX+d` EA (re-derive — the `(IX+d)`
  MEMPTR rule is WZ = IX + d). Confirm against e.g. `dd 7e.json` (LD A,(IX+d)).
- **The R-refresh:** a DD/FD prefix is an extra M1 fetch, so R bumps by 2 (prefix + opcode), like the CB/ED
  planes. `OnInstructionFetched(2)` — confirm via the vectors' `r` delta.

**Tasks (just-in-time):** the DD/FD dataset derivation (D3) + the indexed emit arms (one task per family,
TDD synthetic + then the sweep) + the regen + the `dd`/`fd` TomHarte theories (504 vectors). **GATE:** the
504 DD/FD vectors green at the universal Q/WZ/IM bar + IX/IY checked.

**Estimated size:** large (the biggest dataset + emit-arm slice). Likely the longest sub-PR; if the
derive-vs-hand-author decision (D3) lands on hand-authoring, consider splitting DD and FD into separate PRs
(they are mechanically identical — FD is DD with IY for IX).

### M3.4e-3 — DDCB/FDCB compound (bit/rotate/shift on (IX+d) + the undoc store-copy forms)

**Goal:** add the ~225+225 missing DDCB/FDCB dataset rows, implement the compound emit arm (the operation
on `(IX+d)` + the undoc result-copy into a register), drive `dd cb __ *.json` + `fd cb __ *.json` (512
vectors) green.

**Key behavior:** a DDCB opcode's register field selects BOTH the operation (RLC/.../SRL/BIT/RES/SET) AND,
for the undoc forms (reg ≠ 6), a register to ALSO receive the result. E.g. `DD CB d 00` = `RLC (IX+d)` with
the result ALSO stored in B; `DD CB d 06` = `RLC (IX+d)` (the documented form, no copy). BIT n,(IX+d) is
special — it has no store, and its X/Y flags come from the HIGH byte of the EA (the `(IX+d)` MEMPTR), NOT
the read byte (a documented BIT-(IX+d) quirk — re-derive from `dd cb __ 46.json`). **The compound emit arm
gets the displacement from the decode walk (M3.4e-1).**

**Tasks (just-in-time):** the DDCB/FDCB dataset rows + the compound emit arm (TDD synthetic with the
displacement + the store-copy + the BIT-(IX+d) X/Y-from-EA-high quirk) + the regen + the
`dd cb __`/`fd cb __` TomHarte theories. **GATE:** the 512 DDCB/FDCB vectors green.

**Estimated size:** medium-large (dense undocumented behavior, but one emit arm). The store-copy + the
BIT X/Y quirk are the load-bearing pieces.

---

## OPEN DESIGN DECISIONS (need a human/Coordinator call before the M3.4e-1 Builder run)

- **(D1) Compound-prefix decode model — declarative vs special-cased.** Does `PrefixByte` gain
  `CompoundWith`/`DisplacementBeforeOpcode` so the spec DECLARES `DD CB d op` and `EmitStructuredDecodeWalk`
  reads it generically (matches ADR Decision 1's "the spec declares its decode structure" + the cross-arch
  optimization goal; larger schema delta), OR do we special-case `0xDD/0xFD + 0xCB` in the walk (cheaper
  now, pays the 8086 cost later — ADR risk-Q2)? **Recommendation: declarative** (the ADR's thesis; the
  decode-driven block discovery is what makes the M3.5 JIT genericity work valid). Confirm the appetite for
  the schema delta.
- **(D2) IXh/IXl modeling.** The undoc IXh/IXl 8-bit ops need access to IX's halves. Options: (a) declare
  IXh/IXl as half-views of IX (like B/C over BC) in `z80-semantics.json` — clean, reuses the pair-view
  machinery; (b) hand-write half-access (`(byte)(IX >> 8)` / `(byte)IX`) in the indexed emit arm — no
  schema change but less DRY. **Recommendation: (a) half-views** (consistent with the BC/DE/HL model,
  ADR Decision 3 option A). Confirm.
- **(D3) DD/FD dataset-row generation — derive vs hand-author.** ~876 rows is the F1 risk at scale. Derive
  the DD/FD rows from the base/CB tables (a `Z80DdFdSemantics` mapping base op N → its IX-substituted form),
  with the dataset carrying only the IX/IY-SPECIFIC rows? **Recommendation: derive algorithmically**
  (consistent with the existing `Z80*Semantics` generators; closes the F1 gap by construction). This is a
  non-trivial importer design the M3.4e-1 (or a dedicated e-2 first task) should settle. Confirm.
- **(D4) JIT treatment of the indexed + compound ops.** Per ADR Decision 4/7, emit the hot straight-line
  DD/FD ops and FALL BACK for DDCB/FDCB. **Recommendation: DD/FD core MAY be JIT-emitted (straight-line
  `(IX+d)`); DDCB/FDCB are fallback-only in M3.4e** — revisit IL emission in M3.5/post-M3. Confirm.
- **(D5) Redundant-prefix chains (`DD DD`/`DD FD`/`DD ED`).** No vectors exist. Do we model the silicon's
  "re-decode from the last prefix" behavior at all in M3.4e, or note it as unverified-pending? **
  Recommendation: do NOT model it** (YAGNI — no vector gate, not needed for ZEXALL; note in the closeout).
  Confirm.

---

## Invariants (carried forward — non-negotiable)

- TDD task-by-task; full gate after each task: `dotnet build --no-incremental -warnaserror` clean; targeted
  tests green; `RegeneratedSpecTests` (6502 byte-identity) green; base/CB/ED stay green at the universal
  Q/WZ/IM bar; IX/IY checked.
- The dataset→importer→regen→generator pipeline only — never hand-edit `Z80Spec.cs`.
- Synthetic-spec tests (`GeneratorTestHost.CompileAndLoadType`) decouple per-task from the regen, which
  lands atomically late per sub-PR (the CB/ED/M3.4d pattern). Structured fixtures use `IAddressSpace _bus`,
  declare `public byte Q;` + `public int Im;`, and (new for this slice) `public ushort IX; public ushort IY;`
  if the runner/emit arms reference them as fields.
- The honest close-state: each sub-PR's closeout enumerates exactly what is + isn't covered (incl. the
  unverified redundant-prefix chains).

---

## When to detail this plan fully

Expand M3.4e-1 into a full task-by-task plan (the M3.4c depth) IMMEDIATELY before its Builder run, AFTER the
user/Coordinator decides D1–D3. Expand M3.4e-2 after e-1 merges (the decode walk + EA helper shape is then
concrete). Expand M3.4e-3 after e-2 merges (the indexed emit arms are then concrete and the compound arm
reuses them). Detailing them now would fabricate precision that D1–D3 + the e-1 outcome will change.

## Slice docs index

- **Overview / sequencing:** `docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md`
- **Previous slice (depth template + the close-state record):**
  `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md`
- **Immediately-prior slice:** `docs/superpowers/plans/2026-06-14-m3-z80-ed-block-ops.md`
- **Next slice:** `docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md`
- **Architecture (Decisions 1, 3, 4, 7):** `docs/architecture/0001-z80-second-architecture.md`
