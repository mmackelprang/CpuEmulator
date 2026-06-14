# M3.4e-2: The Z80 DD/FD Core — `(IX+d)`/`(IY+d)` + IX/IY 16-bit ops + undoc IXh/IXl 8-bit ops — TomHarte-Green

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. This is M3.4e-2 — the FIRST opcode slice of the IX/IY arc. **e-1a (#24) and e-1b (#25) are
> MERGED** (the `Indexed` AddrMode + `EmitZ80IndexedEa` EA helper + IXh/IXl/IYh/IYl half-views; the
> declarative compound-prefix decoder). e-2 WIRES the e-1a EA helper, declares DD/FD as real prefixes,
> DERIVES the ~213+213 missing DD/FD dataset rows, implements the indexed/IX-16-bit/undoc-half emit arms,
> and drives the 504 `dd *.json` + `fd *.json` core vectors green. The compound `DD CB d op` bit/rotate/
> shift forms stay e-3. Depth template: `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md` +
> `…-ed-block-ops.md`.

**Goal:** make the Z80 **DD/FD prefix CORE** TomHarte-green — the `(IX+d)`/`(IY+d)` re-interpretation of
the base ops (LD r,(IX+d); LD (IX+d),r; LD (IX+d),n; ALU A,(IX+d); INC/DEC (IX+d)), the **IX/IY 16-bit ops**
(LD IX,nn; LD IX,(nn)/LD (nn),IX; ADD IX,rp; INC/DEC IX; PUSH/POP IX; EX (SP),IX; JP (IX); LD SP,IX), and
the **undocumented IXh/IXl 8-bit ops** (LD IXh,n; LD r,IXh; ALU A,IXh; INC/DEC IXh; etc.) — for BOTH the DD
(IX) and FD (IY) planes. The DD/FD rows are DERIVED algorithmically (D3) from the base table, the inert
prefix is modeled (a DD on an op that names neither H/L/(HL) executes the base op with R+2), and the WZ/
flags/cycles are pinned per-op to the vectors (the oracle). Every 6502 artifact stays byte-identical; the
whole Z80 (base + CB + ED + block + the e-1 framework) stays TomHarte-green at the universal Q/WZ/IM bar.

**Architecture:** e-1a shipped the `Indexed` AddrMode (`AddrMode.cs` + the 3 mirrors + `JitMode.Indexed` +
`ModeLength("Indexed") => 3`), the `internal static EmitZ80IndexedEa(sb, indexReg, dispExpr)` signed-EA
helper (currently UNCALLED — e-2 wires it), and the IXh/IXl/IYh/IYl half-views (IX/IY are now computed
pair-views, storage on the halves). e-1b shipped the declarative compound decoder (`PrefixByte.CompoundWith`
/`DisplacementBeforeOpcode`, the `(0xDDCB<<8)|op` compound key, the `EmitStructuredDecodeWalk` compound
routing). The DD/FD-CORE forms are NOT compound — they are plain single-byte-prefix rows (`(0xDD<<8)|op`
key, the existing plain-prefix decode arm) with `mode == "Indexed"` for the `(IX+d)` forms and the existing
modes (Register/ImmediateExtended/ExtendedAddress/RegisterIndirect/Implied) for the rest. So e-2 is
**additive and analogous** to how the CB/ED planes added rows: a new `Z80DdFdSemantics` derives the row
text, the importer routes `Prefix == "0xDD"`/`"0xFD"`, new emit arms reuse the existing flag/16-bit/LD/ALU
helpers with `(HL)→(IX+d)` and `H/L→IXh/IXl` substitution, the disassembler gains its first `Indexed` arm,
and DD/FD are declared as real prefixes in `z80-semantics.json`. Every 6502 artifact stays byte-identical.

**Tech Stack:** C# (.NET 10), a Roslyn incremental source generator (`CpuEmulator.Generators`), a console
spec importer (`CpuEmulator.SpecImporter`) that regenerates `Z80Spec.cs` from `z80-opcodes.json` +
`z80-semantics.json`, and xUnit + the SingleStepTests/z80 vectors (TomHarte).

---

## Decisions baked into this plan (D3–D5 confirmed; D6–D8 new — flagged for the Coordinator)

- **D3 = DERIVE algorithmically (CONFIRMED + specified).** The dataset has only **39 documented DD rows +
  39 FD rows**; the TomHarte harness gates **252 DD + 252 FD** core vectors (all of 0x00–0xFF except the 4
  prefix bytes cb/dd/ed/fd). So **213 DD + 213 FD rows are MISSING** and must be derived by construction (the
  F1-gap lesson at 40× scale). The derivation rule is specified precisely in §"The D3 derivation rule" below
  and implemented in a new `Z80DdFdSemantics.OpsFor(opcode, isIy)`. Task 0 cross-checks the derived row
  count (252 each) against the vector files.
- **D4 = INTERPRETER-ONLY in e-2; JIT-IL DEFERRED (call made).** Per ADR Decision 4/7 the DD/FD core MAY
  emit JIT IL for the hot straight-line indexed ops. **This plan keeps e-2 interpreter-only with JIT
  fallbacks** (the same posture as base/CB/ED — every Z80 class is a JIT fallback today). Rationale: (a) the
  Z80 has NEVER run through the JIT (M3.5 owns Z80-through-JIT end-to-end — see the finish-line overview);
  shipping the FIRST Z80 JIT-IL in an opcode slice would entangle e-2's TomHarte gate with the JIT harness
  that does not yet exist for the Z80; (b) the JIT genericity work (the decode-driven block discovery) is
  M3.5's thesis. So e-2 adds the new instruction classes to the JIT FALLBACK predicates (the `z80` bool, the
  `jitClass => "Register"` arm, the `JitOpLiteral` no-IL case list) exactly as CB/ED did. **JIT-IL for the
  indexed ops is re-scoped to M3.5** alongside the rest of Z80-through-JIT. (Coordinator: confirm you accept
  the defer; it keeps e-2 a clean interpreter slice.)
- **D5 = redundant-prefix chains NOT modeled (CONFIRMED).** `DD DD`/`DD FD`/`DD ED`/`FD …` have no vectors
  (the cache has only the 252 standalone `dd NN` + the 256 `dd cb __ NN`). Not gated, not modeled; the
  closeout notes them unverified-pending (YAGNI; not needed for ZEXALL). e-1b already declined to declare
  these chains (B4).
- **D6 (NEW) = DD and FD ship in ONE PR (call made).** D3=derive keeps them together: FD is mechanically DD
  with IY/IYh/IYl substituted for IX/IXh/IXl. The derivation generator takes an `isIy` flag; the emit arms
  take the index-register name as a parameter. Splitting would duplicate every task. The plan is structured
  so DD and FD go live in the SAME regen (Task 7) and the TomHarte gate (Task 8) sweeps both 252+252. (If
  the Builder run finds the single PR too large to review, the natural cut is Task 8 — land DD-green first,
  then FD-green — but the code lands together; flagged, not recommended.)
- **D7 (NEW) = the undocumented IXh/IXl 8-bit ops ARE in scope (vector-forced).** The 252 DD vectors INCLUDE
  the undoc half-register forms (`DD 24` INC IXh, `DD 26` LD IXh,n, `DD 7C` LD A,IXh, `DD 84` ADD A,IXh,
  etc. — CONFIRMED present + behaviourally pinned at Task 0). They are NOT optional: a missing derived row →
  the probe finds `Disassemble == "???"` → the opcode is silently uncovered. The e-1a half-views
  (IXh/IXl/IYh/IYl) exist precisely so these ops have a target to name. **In scope.**
- **D8 (NEW) = the inert DD/FD prefix is modeled by re-deriving the BASE op with R+2 (vector-forced).** A DD
  on an op that names NEITHER H/L/(HL) (e.g. `DD 04` = INC B) executes the base op IDENTICALLY but costs the
  extra prefix M1 fetch: PC+2, R+2, the base op's flags/WZ/cycles. CONFIRMED: `dd 04` B 0xb8→0xb9, IX
  untouched, WZ unchanged, R 0x4a→0x4c (+2), 8 T. The derivation routes these to the SAME base op text.

---

## Scope

**IN scope (the DD/FD core comes alive end-to-end, both planes):**

1. **The `(IX+d)`/`(IY+d)` re-interpretation of the base memory ops** (mode `Indexed`):
   - `LD r,(IX+d)` (0x46/4E/56/5E/66/6E/7E + IY), `LD (IX+d),r` (0x70–0x77 except 0x76 + IY).
   - `LD (IX+d),n` (0x36 + IY).
   - ALU `A,(IX+d)`: ADD/ADC/SUB/SBC/AND/XOR/OR/CP (0x86/8E/96/9E/A6/AE/B6/BE + IY).
   - `INC (IX+d)` / `DEC (IX+d)` (0x34/0x35 + IY).
   - **WZ = the computed `IX+d` EA** (the MEMPTR rule — CONFIRMED `dd 7e`: IX=0x2936, d=0x29, WZ=0x295f).
2. **The IX/IY 16-bit ops** (existing modes):
   - `LD IX,nn` (0x21, ImmediateExtended; no WZ), `LD (nn),IX` (0x22, ExtendedAddress, WZ=nn+1),
     `LD IX,(nn)` (0x2A, ExtendedAddress, WZ=nn+1).
   - `ADD IX,rp` (0x09/19/29/39, Register; WZ = pre-op IX + 1; rp ∈ BC/DE/IX/SP — the HL slot is IX).
   - `INC IX` (0x23) / `DEC IX` (0x2B) (Register; no flags, no WZ).
   - `PUSH IX` (0xE5) / `POP IX` (0xE1) (Register).
   - `EX (SP),IX` (0xE3, RegisterIndirect; WZ = new IX).
   - `JP (IX)` (0xE9, RegisterIndirect; no WZ).
   - `LD SP,IX` (0xF9, Register; no WZ).
3. **The undocumented IXh/IXl 8-bit ops** (existing Register/Immediate/RegisterIndirect modes, H/L→IXh/IXl):
   - `LD IXh,n` (0x26) / `LD IXl,n` (0x2E) (Immediate).
   - `INC IXh` (0x24) / `DEC IXh` (0x25) / `INC IXl` (0x2C) / `DEC IXl` (0x2D) (Register; full flags).
   - `LD r,IXh`/`LD r,IXl` + `LD IXh,r'`/`LD IXl,r'` (the 0x44–0x6F Register block where H/L are re-read as
     IXh/IXl; the (HL)-naming members 0x46/4E/…/0x70–0x77 stay the `(IX+d)` forms of item 1 — NOT half ops).
   - ALU `A,IXh`/`A,IXl` (0x84/8C/94/9C/A4/AC/B4/BC + the … the H/L members of the ALU block).
4. **The inert DD/FD prefix** (D8): every other DD/FD opcode (e.g. `DD 04` INC B, `DD 80` ADD A,B) executes
   the base op with PC+2, R+2.
5. **DD/FD declared as real prefixes** in `z80-semantics.json` `decode.prefixes` (e-1b deferred this to e-2).
6. **The Indexed disassembler arm** (e-1b dodged it with `AddrMode.Bit`; e-2 adds the real `Indexed` arm).
7. **The TomHarte DD + FD gate** — `CoveredDdPlaneOpcodes`/`CoveredFdPlaneOpcodes` theories loading
   `dd {op:x2}.json`/`fd {op:x2}.json` (504 vectors), green at the universal Q/WZ/IM bar with IX/IY checked.

**OUT of scope (each is a later slice — do NOT reach for it):**

- **DDCB/FDCB compound bit/rotate/shift on `(IX+d)`** (incl. the undoc store-copy forms) = **M3.4e-3**. The
  256+256 `dd cb __ *.json`/`fd cb __ *.json` vectors are NOT gated here. The decoder for them EXISTS (e-1b),
  but no DDCB/FDCB row goes live; they stay `// TODO(mode)`.
- **The redundant-prefix chains** (`DD DD`/`DD FD`/`DD ED`) = NOT modeled (D5; no vectors; unverified-pending).
- **JIT-IL for the indexed ops** = M3.5 (D4; e-2 emits JIT fallbacks only).
- **Interrupt SERVICING / ZEXALL** = M3.5.

> **The honest one-liner for M3.4e-2's close-state (target):** the Z80 base + CB + ED + block planes AND the
> 252 DD-core + 252 FD-core opcodes run and are TomHarte-green — the `(IX+d)`/`(IY+d)` indexed memory ops,
> the IX/IY 16-bit ops, the undocumented IXh/IXl/IYh/IYl 8-bit ops, and the inert DD/FD prefix on every other
> opcode — per-T-state, with final Q/WZ/IM + IX/IY checked. The DD/FD rows are DERIVED, not hand-authored.
> The DDCB/FDCB compound bit/rotate/shift forms remain `// TODO(mode)` (M3.4e-3); the redundant-prefix chains
> are unverified (no vectors); interrupt servicing + ZEXALL + the JIT remain M3.5 (the IX/IY ops emit as JIT
> fallbacks). "TomHarte-green" is asserted over the 504 DD/FD-core opcodes (504,000 cases) + the re-validated
> base/CB/ED, enumerated honestly in the closeout.

---

## Vector availability + the F1 gap (CONFIRMED at write-time)

| Plane | Core vectors | Filename | Dataset rows present | F1 gap to derive |
|---|---|---|---|---|
| DD core | **252** (`dd 00.json`…`dd ff.json` minus cb/dd/ed/fd) | `dd NN.json` (SPACE) | **39** | **213 rows** |
| FD core | **252** (`fd 00.json`…`fd ff.json` minus 4) | `fd NN.json` (SPACE) | **39** | **213 rows** |
| DDCB | 256 (OUT of scope — e-3) | `dd cb __ NN.json` | 31 | — (e-3) |
| FDCB | 256 (OUT of scope — e-3) | `fd cb __ NN.json` | 31 | — (e-3) |

**The filename trap (CONFIRMED):** `ls 'dd *.json'` matches BOTH `dd NN.json` AND `dd cb __ NN.json`
(508 = 252 core + 256 DDCB). The core theory MUST glob `dd {op:x2}.json` (3 tokens, no `cb`), NOT a
prefix-match. The vector cache is `~/.cache/cpuemulator/vectors/z80/v1/` (default; or
`$env:CPUEMULATOR_TESTVECTORS/z80/v1`), fetched by `tools/get-test-vectors-z80.ps1`.

**The total F1 gap = 426 derived rows.** Hand-authoring is the single biggest risk; D3 closes it by
construction. The gate is the per-opcode TomHarte sweep: a missing derived row → `Disassemble == "???"` →
the opcode is silently uncovered (the M3.4c probe-vs-emitted discipline). Task 0 + Task 6 cross-check
derived-count == 252 == vector-count for each plane.

---

## The D3 derivation rule (the load-bearing algorithm — specify, then implement in `Z80DdFdSemantics`)

A DD prefix REINTERPRETS the byte that follows by these rules (FD is identical with IY for IX). The rule is a
pure function of the opcode byte; it is keyed off WHICH base operands the opcode names:

1. **If the base op names `(HL)` as a memory operand → it becomes `(IX+d)`** (mode `Indexed`):
   - `LD r,(HL)` → `LD r,(IX+d)`; `LD (HL),r` → `LD (IX+d),r`; `LD (HL),n` → `LD (IX+d),n`.
   - `ALU A,(HL)` → `ALU A,(IX+d)`; `INC (HL)`/`DEC (HL)` → `INC (IX+d)`/`DEC (IX+d)`.
   - These derive to the new `DdFd…` indexed ops (the emit arm reads `EmitZ80IndexedEa` then operates on
     `__ea`). WZ = the computed EA.
2. **Else if the base op names `H` or `L` as an 8-bit register → re-read as `IXh`/`IXl`** (undoc; the
   existing Register/Immediate modes, target/source name `IXh`/`IXl`):
   - `LD r,H` → `LD r,IXh`; `LD H,r'` → `LD IXh,r'`; `LD H,n` → `LD IXh,n`; `INC H` → `INC IXh`;
     `ADD A,H` → `ADD A,IXh`; etc.
   - **CRITICAL exception:** an op that names BOTH H/L AND (HL) does not exist in a single base byte (the
     base encoding never co-names a half and the indirect); but `LD H,(HL)` (0x66) and `LD (HL),H` (0x74) —
     the (HL) member WINS (rule 1): `DD 66` = `LD IXh,(IX+d)` (the DESTINATION is IXh, the SOURCE is
     `(IX+d)` — confirm: `dd 66` is mode `Indexed` in the dataset, target=H→but the SOURCE (HL)→(IX+d) and
     the dest H stays H, NOT IXh, when the op also touches `(IX+d)`). **This is the one subtle rule —
     re-derive at Task 0 from `dd 66`/`dd 74`/`dd 7c`:** when an op has a `(HL)` operand, the OTHER operand
     (even if it is H/L) stays the ORDINARY H/L (the prefix's half-substitution does NOT apply to the
     register operand of an op that ALSO uses `(IX+d)`). The half-substitution applies ONLY to ops with NO
     memory operand. CONFIRMED at write-time: `dd 7c` (LD A,IXh — no memory) substitutes; `dd 7e` (LD
     A,(IX+d) — memory) does NOT touch a half. The dataset already encodes this distinction in the `mode`
     field: `Indexed` rows are rule-1, `Register`/`Immediate` rows naming H/L are rule-2.
3. **Else if the base op names a 16-bit pair containing HL (HL/PUSH-POP AF-pair slot is unaffected; the
   `rp` HL slot) → the HL slot becomes IX** (the IX 16-bit ops):
   - `LD HL,nn` → `LD IX,nn`; `LD (nn),HL` → `LD (nn),IX`; `LD HL,(nn)` → `LD IX,(nn)`;
     `ADD HL,rp` → `ADD IX,rp` (where rp's own HL slot is also IX: `ADD HL,HL` → `ADD IX,IX`);
     `INC HL`/`DEC HL` → `INC IX`/`DEC IX`; `PUSH HL`/`POP HL` → `PUSH IX`/`POP IX`;
     `EX (SP),HL` → `EX (SP),IX`; `JP (HL)` → `JP (IX)`; `LD SP,HL` → `LD SP,IX`.
   - **`EX DE,HL` (0xEB) is NOT affected** (it is documented as DD-inert — confirm at Task 0; the dataset has
     no `DD EB` special row, so it derives to the inert base `EX DE,HL`).
4. **Else (the op names NONE of H/L/(HL)/the HL pair) → the prefix is INERT** (D8): execute the base op
   identically, PC+2, R+2.

**The two-byte vs three-byte length + R-bump:** every DD/FD core op consumes the prefix + the opcode
(2 bytes, R+2) for the non-indexed forms, OR prefix + opcode + displacement (3 bytes for the `(IX+d)` forms;
4 for `LD (IX+d),n` which also reads `n`). The structured `Step` charges `__r.Length` and bumps R by it —
**but R bumps by 2 on a DD/FD op regardless of operand bytes** (the prefix + opcode are the two M1 fetches;
the displacement/immediate are operand reads, not M1). CONFIRMED across `dd 04` (2-byte, R+2), `dd 7e`
(`(IX+d)`, 3-byte, R+2), `dd 26` (`LD IXh,n`, 2-byte, R+2), `dd 36` (`LD (IX+d),n`, 4-byte, R+2). **This is
the load-bearing R subtlety:** `ModeLength("Indexed") => 3` makes the decode walk consume 3 bytes and
charge 3 to `_cycles`/PC, but R must bump by 2, not 3. The existing `OnInstructionFetched(int keyBytes)`
R-refresh on `Z80Cpu` bumps R by the M1 count, which for the structured Z80 is the PREFIX+OPCODE count.
**Confirm how `OnInstructionFetched` currently derives the R-bump for ED (2-byte, R+2) and ensure the
3-byte/4-byte Indexed forms still bump R by 2, not by `__r.Length`** (RECON-FINDING G3 below). This is the
single most likely cycle/R surprise — Task 0 Step 5 pins it against `dd 7e` (R+2 despite 3 length).

---

## Ground truth — what e-1a/e-1b/M3.4a-d ALREADY shipped (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 0** — e-2 REUSES or EXTENDS them.

- **The `EmitZ80IndexedEa` helper (e-1a, UNCALLED).** `src/CpuEmulator.Generators/CpuEmitter.cs:1995-1996`:
  ```csharp
  internal static void EmitZ80IndexedEa(StringBuilder sb, string indexReg, string dispExpr) =>
      sb.AppendLine($"        ushort __ea = unchecked((ushort)({indexReg} + (sbyte)({dispExpr})));");
  ```
  `internal static` (the test project has `InternalsVisibleTo`). e-2 calls it from the new indexed emit arms
  with `indexReg ∈ "IX"/"IY"` and `dispExpr` = the displacement-byte local the body reads.
- **The base emit arms e-2 re-interprets** (all in `CpuEmitter.cs`):
  - `EmitZ80LdBody(sb, insn, pc, pcType)` (`:1758`) — the `Load16`/`Store16`/`LoadMem16`/`Load`/`Store`/
    `StoreImm8`/`Transfer` switch. `(HL)` reads via `ReadBus(HL)` / writes via `WriteBus(HL, …)`.
  - `EmitZ80AluBody(sb, insn, pc, pcType, statusReg, flags)` (`:1523`) + `EmitZ80AluSource` (`:1552`) — the
    `RegisterIndirect` arm reads `byte data = ReadBus(HL);` (`:1561`). e-2's indexed ALU reads
    `ReadBus(__ea)`.
  - `EmitZ80IncDec8(sb, reg, f, flags, increment, isMem, pc, total)` (`:1668`) — the `isMem` arm reads
    `byte before = ReadBus(HL);` (`:1684`) and writes `WriteBus(HL, res);` (`:1708`). e-2's `INC/DEC (IX+d)`
    reads/writes `__ea`.
  - `EmitZ80Add16(sb, insn, f, flags, total)` (`:1719`) — `ADD HL,rr`: `int hl = HL; EmitWz(sb, "hl + 1");
    … HL = res16;`. e-2's `ADD IX,rp` is the SAME with `IX` for `HL` and `EmitWz(sb, "ix + 1")`.
  - `EmitZ80StackBody` (`:1819`) — `Push16`/`Pop16` over the pair name. e-2 passes `IX`.
  - `EmitZ80ExchangeBody` (`:1847`) — `ExSpHl` (`:1873`): reads SP/SP+1, writes HL halves, `HL = …`,
    `EmitWz(sb, "HL")`. e-2's `EX (SP),IX` is the SAME with `IX`; `EmitWz(sb, "IX")` (new IX) — CONFIRMED
    `dd e3` WZ = new IX.
  - `EmitZ80FlowBody` (`:1998`) — `JumpIndirect` (JP (HL)). e-2's `JP (IX)` sets PC = IX, no WZ.
- **`EmitWz` (e-1a/M3.4c).** `CpuEmitter.cs:1978`: `EmitWz(sb, expr)` → `WZ = unchecked((ushort)(<expr>));`.
  `EmitWzIndented` (`:1983`) for `if (taken)` blocks. e-2 calls `EmitWz(sb, "__ea")` for the indexed memory
  ops.
- **`ModeLength` (e-1a).** `CpuEmitter.cs:2837`: `"Indexed" => 3,` already present.
- **The half-view register machinery (e-1a).** `CpuEmitter.cs:63-71`: a `HighHalf`/`LowHalf` register emits
  a computed property; the halves are the only storage. `z80-semantics.json` declares
  `IXh`/`IXl`/`IYh`/`IYl` (8-bit) before `IX`/`IY` (views). `GetRegister`/`SetRegister` (`:80-99`) switch on
  NAME and work for both. So `IXh`/`IXl`/`IYh`/`IYl` are nameable register operands TODAY (the storage
  exists); e-2's undoc-half emit arms name them directly.
- **The disassembler — NO `Indexed` arm yet.** `EmitDisassembler` (`:3083-3158`): the `instruction.Mode
  switch` has `Implied`/`Immediate`/`RegisterIndirect`/`ExtendedAddress`/`Bit`/… arms and a `_ => throw`
  default (`:3150-3151`). **There is NO `"Indexed"` arm** — an `Indexed`-mode row reaching it THROWS at
  generation (e-1b's closeout confirms it dodged this with `AddrMode.Bit`). **e-2 MUST add the `Indexed`
  arm** (RECON-FINDING G1) or the first live `Indexed` row fails generation.
- **The dispatch + cycles + flags + JIT predicates.** `Z80Cycles` (`:478`) — a `(cls, opKind, mode) switch`.
  `Z80WritesFlags` (`:396`). `isZ80` (`:278-283`). `ClassifyForJit` (`:3324`) — the `z80` bool (`:3339-3343`)
  + the `jitClass => "Register"` arm (`:3369-3370`); `JitBaseCycles` (`:3279`, the z80 list `:3287-3291`);
  `JitOpLiteral` (`:3384`, the Z80 no-IL case list `:3439-3453`). The pattern for a new Z80 class: add to
  `isZ80` → `Z80Cycles` → `Z80WritesFlags` → the `EmitOpcodeMethod` body switch → the 3 JIT predicates.
- **The importer prefix routing.** `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs:169-176`: the `z80Ops`
  ternary — `Prefix is null` → `Z80BaseSemantics`; `0xCB` → `Z80CbSemantics`; `0xED` → `Z80EdSemantics`;
  else `null`. **NO `0xDD`/`0xFD` arm** — e-2 adds two arms calling `Z80DdFdSemantics.OpsFor(op, isIy)`.
- **CPUGEN012 (declared-prefix-backs-a-row).** `SpecFileEmitter.cs:85-90,231-241`: the generator cross-checks
  every declared prefix backs ≥1 emitted prefixed `Insn` row. DD/FD single-byte rows (mode
  Register/Immediate/ExtendedAddress/Indexed) PASS the `singleBytePrefix` check and emit as prefixed rows,
  so declaring DD/FD in `decode.prefixes` is SATISFIED once their derived rows emit (G2). (e-1b extended the
  check to accept `KeyShape.Compound` rows — irrelevant here; e-2's DD/FD core rows are plain prefixed.)
- **The dataset row schema.** `OpcodeDataset.cs` `OpcodeEntry(Opcode, Mnemonic, Mode, Bytes, Cycles,
  PageCrossPenalty, Source?, Prefix?, SubField?)`. The 39 DD rows carry `prefix:"0xDD"` + mode ∈
  Register/ImmediateExtended/ExtendedAddress/Indexed/RegisterIndirect. The 213 derived rows reuse this
  schema with the right `mode`/`mnemonic` (the importer maps mnemonic+mode → ops via `Z80DdFdSemantics`).
- **The TomHarte harness ALREADY sets + checks IX/IY (e-1a RECON A3).** `Z80TomHarteRunner.cs:46`
  (`SetRegister("IX", s.Ix); SetRegister("IY", s.Iy)`), `:64` (`Check(…, "IX", f.Ix, 4); Check(…, "IY",
  f.Iy, 4)`). `Z80TomHarteCase.cs` parses `ix`/`iy`. So IX/IY checks come FREE; e-2 needs NO runner change.
- The vectors: `~/.cache/cpuemulator/vectors/z80/v1/dd 00.json`…`dd ff.json` (252 core, minus 4 prefix
  bytes) + `fd …` (252), 1000 cases each. CONFIRMED present at write-time. The DDCB `dd cb __ NN.json`
  (256) + FDCB (256) exist but are OUT of scope (e-3).
- The ADR `docs/architecture/0001-z80-second-architecture.md` Decision 1 (the declarative decode — the
  DD/FD-core forms use the plain-prefix arm, not the compound), Decision 3 (IX/IY as registers + the IXh/IXl
  option A half-views), Decision 4/7 (JIT fallback / the fastmem seam reused for `(IX+d)`).

### RECON FINDINGS that refine the scoped-plan prose (the code/vectors WIN — flagged)

> Discovered during write-time recon by reading the source + sampling the vectors. The implementer MUST
> re-confirm each at Task 0 and treat the vector/code as ground truth.

- **G1 — the disassembler has NO `Indexed` arm and WILL throw on the first live `Indexed` row.** `EmitDisassembler`
  (`CpuEmitter.cs:3083-3158`) has a `_ => throw` default and no `"Indexed"` case. e-1b's closeout used
  `AddrMode.Bit` for its synthetic compound stub PRECISELY to avoid this. **e-2 MUST add an `Indexed`
  disassembler arm** (Task 4) BEFORE the first `Indexed` row goes live (Task 7), or the regen fails to
  generate. The arm formats e.g. `LD A,(IX+$NN)` — the disassembly shape is not vector-gated (TomHarte does
  not check the mnemonic string), so the arm need only be well-formed + not throw. Decision (recorded): the
  Indexed arm renders `$"{m} (IX{+/-d})"` using the displacement operand byte, keyed by `OperationKey`. It
  must distinguish IX vs IY — read the prefix from the OperationKey high byte (`(key>>8)==0xDD` → IX else IY).
- **G2 — declaring DD/FD in `decode.prefixes` REQUIRES backing rows in the SAME commit (CPUGEN012).** If
  Task 7 declares DD/FD before the derived rows emit, CPUGEN012 trips. So the dataset rows + the routing +
  the `decode.prefixes` declaration land ATOMICALLY in the regen task (Task 7) — never split. (The synthetic
  per-task tests, Tasks 1–5, decouple from the real regen — the CB/ED/block pattern.)
- **G3 — R bumps by 2 on a DD/FD op regardless of `__r.Length` (3 or 4 for indexed/immediate forms).**
  CONFIRMED: `dd 7e` (LD A,(IX+d), length 3) R+2; `dd 36` (LD (IX+d),n, length 4) R+2; `dd 04` (length 2)
  R+2. The structured `Step` calls `OnInstructionFetched(__r.Length)`. **Read how `OnInstructionFetched`
  on `Z80Cpu` derives the R-bump** (`Z80Cpu.cs` ~`:125-129`) — for the ED plane (length 2) it bumps R by 2;
  for the Indexed forms (length 3/4) it must STILL bump by 2 (the prefix + opcode are the only M1 fetches).
  If `OnInstructionFetched` currently bumps R by `keyBytes`, the Indexed forms over-bump R by 1–2 →
  the sweep fails on final `r`. **The fix lives in `Z80Cpu.OnInstructionFetched` (or the cycle model), NOT
  the emit arm** — confirm at Task 0 + pin the R-bump rule against `dd 7e` (the most likely Task 8 surprise).
- **G4 — the `(IX+d)` displacement is read AFTER the opcode but the EA is computed BEFORE the memory access.**
  For `LD r,(IX+d)`: read opcode → read displacement `d` → compute `__ea = IX + (sbyte)d` → read
  `ReadBus(__ea)`. The structured decode walk reads the operand bytes into a known local (the `Load16`/`Load`
  arms read `byte lo = ReadBus(pc)`); confirm where the Indexed-mode displacement lands (the first operand
  byte after the opcode — the body reads it via the same `ReadBus(pc); pc++` idiom as Immediate, then calls
  `EmitZ80IndexedEa(sb, "IX", "d")`). Read an existing 1-operand-byte arm (`EmitZ80LdBody`'s `Load` for
  `LD A,n`) for the exact `pc`-advance idiom and mirror it. WZ = `__ea` (CONFIRMED `dd 7e`).
- **G5 — `LD (IX+d),n` reads TWO operand bytes (displacement THEN immediate).** Order: opcode → `d` → `n`.
  `ModeLength` is 4 for this row (the dataset has `0x36 LD Indexed bytes=4`). But `ModeLength("Indexed")=3`
  (e-1a). **DISCREPANCY:** `LD (IX+d),n` is 4 bytes but `Indexed` mode is length 3. Read how the structured
  decode walk computes length for an `Indexed` row — if it uses `ModeLength("Indexed")=3` it will UNDER-read
  for `0x36`. **Decision (recorded, refine at Task 0):** `LD (IX+d),n` may need its OWN handling — either a
  distinct mode/keyshape, or the emit arm reads the extra `n` byte and the decode-walk length for THIS opcode
  is 4. Check the e-1a `ModeLength` + the dataset `bytes=4` for `0x36`: the cleanest path is to let the
  decode walk read the operand bytes per the dataset `Bytes` (not `ModeLength`) — confirm which the structured
  walk uses. If the walk hard-uses `ModeLength`, e-2 must special-case `0x36`/`0x36+IY` (e.g. a length
  override or a dedicated op kind that reads 2 operand bytes). **This is the second-most-likely surprise —
  pin it at Task 0 Step 6 against `dd 36.json` (length 4, the body reads d then n).**
- **G6 — the inert-prefix ops reuse the EXISTING base emit arms unchanged.** A `DD 04` (INC B) derives to the
  base `IncReg("B")` op-text and routes to the EXISTING `EmitZ80IncDec8` (or `Z80Alu` arm) — NO new emit arm,
  NO new class. The ONLY differences from the base op are PC+2 + R+2, which come FREE from the 2-byte
  `__r.Length` (the row is a prefixed `Insn(0xDD, 0x04, …)` with the base ops). So the 213 derived rows split
  into (a) ~rows that reuse base arms (inert + the IX-16-bit + the undoc-half, all expressible as base ops
  with IX/IXh/IXl operand names) and (b) the `Indexed`-mode rows that need the NEW indexed emit arms. **The
  derivation generator emits base op-text (e.g. `[IncReg("IXh")]`) for (a) and new `DdFd…`/indexed op-text
  for (b).** Confirm which base ops accept an `IXh`/`IXl`/`IX` operand name unchanged (the half-views + IX
  pair-view make `IncReg("IXh")`/`Transfer("IXh","B")`/`Add16("IX","BC")` all valid register names today).

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `tools/CpuEmulator.SpecImporter/Z80DdFdSemantics.cs` | Create | The D3 derivation: `OpsFor(int opcode, bool isIy)` → base/indexed/half ops-text for a DD/FD core opcode. |
| `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs` | Modify | Route `Prefix == "0xDD"`/`"0xFD"` through `Z80DdFdSemantics`. |
| `tools/CpuEmulator.SpecImporter/SemanticsMap.cs` | Modify | `FactoryArity` for any new `DdFd…` indexed op kinds; widen `AllowedArgPattern` if a new arg shape needs it. |
| `tools/CpuEmulator.SpecImporter/data/z80-opcodes.json` | Modify | ADD the 213 DD + 213 FD derived rows (the F1 gap); the existing 39+39 stay. |
| `tools/CpuEmulator.SpecImporter/data/z80-semantics.json` | Modify | Declare `0xDD`/`0xFD` in `decode.prefixes` (e-1b deferred this to e-2). |
| `src/CpuEmulator.Core/Specification/Op.cs` | Modify | The new indexed op records (`DdFdLdIndexed`/`DdFdAluIndexed`/`DdFdIncDecIndexed`/`DdFdStoreImmIndexed`). |
| `src/CpuEmulator.Core/Specification/Spec.cs` | Modify | The factories for the new indexed op records. |
| `src/CpuEmulator.Generators/SpecModel.cs` | Modify | The new `Z80DdFd` (or `Z80Indexed`) `InstructionClass` member(s). |
| `src/CpuEmulator.Generators/SpecParser.cs` | Modify | `s_microOpSignatures`; the op-kind class set; `ClassifyOps`; `ValidateModeForClass` (Indexed legality); the status-touch predicate. |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | The indexed emit arms (`EmitZ80IndexedLdBody`/`…AluBody`/`…IncDec`/`…StoreImm`) wiring `EmitZ80IndexedEa`; the `Indexed` disassembler arm (G1); `Z80Cycles`/`Z80WritesFlags`/`isZ80`/the JIT predicates for the new class; the R-bump fix if G3 needs it. |
| `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` | Modify (maybe) | The `OnInstructionFetched` R-bump fix for the 3/4-byte Indexed forms (G3) — ONLY if recon shows it over-bumps. |
| `src/CpuEmulator.Cpus.Z80/Z80Spec.cs` | Modify (regenerated) | The regenerated spec — the 252 DD + 252 FD core rows live; `decode.prefixes` gains DD/FD. |
| `tests/CpuEmulator.Tests/Importer/Z80DdFdSemanticsTests.cs` | Create | `Z80DdFdSemantics.OpsFor` derivation truth table + the dataset's 252 DD + 252 FD core row count (the F1 cross-check). |
| `tests/CpuEmulator.Tests/Generators/Z80IndexedLdTests.cs` | Create | LD r,(IX+d) / LD (IX+d),r / LD (IX+d),n + WZ=EA (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80IndexedAluTests.cs` | Create | ALU A,(IX+d) flags + WZ (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80IndexedIncDecTests.cs` | Create | INC/DEC (IX+d) flags + WZ (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80Ix16Tests.cs` | Create | LD IX,nn / (nn),IX / IX,(nn); ADD IX,rp (WZ=IX+1); INC/DEC IX; PUSH/POP IX; EX (SP),IX (WZ=new IX); JP (IX); LD SP,IX (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80IxHalfOpTests.cs` | Create | LD IXh,n; INC/DEC IXh; LD r,IXh; ALU A,IXh; the inert-prefix INC B (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80IndexedClassifyTests.cs` | Create | The indexed-shaped rows classify + compile + the `Indexed` disassembler arm does not throw (synthetic). |
| `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs` | Modify | Add the `CoveredDdPlaneOpcodes`/`CoveredFdPlaneOpcodes` theories. |
| `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e2-ddfd-core.md` | Modify | This file — the closeout. |
| `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md` | Modify | The M3.4e-2 section pointer to this plan; the slice-docs-index cross-link. |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502
> byte-identity guard `RegeneratedSpecTests` + the whole Z80 staying green at the universal Q/WZ/IM bar),
> then commit. Tasks are dependency-ordered so the suite builds and stays green after every task. Literal
> code is given for every load-bearing piece. The synthetic-spec tests (via
> `GeneratorTestHost.CompileAndLoadType`) decouple from the real `Z80Spec.cs` regen, which lands atomically
> late (Task 7). Structured synthetic fixtures use `IAddressSpace _bus`, declare `public byte Q;` +
> `public int Im;` (M3.4d deviation #1; the ED/block precedent), and — new for this slice — name
> `IX`/`IY`/`IXh`/`IXl`/`IYh`/`IYl` in their `Registers` (the half-views) where the body references them.

### Task 0: Baseline + shipped-surface recon + the D3-derivation + the vector cross-check (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch.** Create the branch off the current main (which includes e-1a #24 + e-1b #25):
  Run: `git switch -c feat/m3-z80-ixiy-e2-ddfd`
  Expected: on the new branch; `git log` shows the e-1a/e-1b merges (#24 `2393fd2`, #25 `8f38e2a`).
  CONFIRM e-1 is present: grep `EmitZ80IndexedEa` in `CpuEmitter.cs` + `Indexed` in `AddrMode.cs` + `IXh` in
  `z80-semantics.json`.

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test` → 0 failures, 0 unexpected skips. Record the EXACT count (the closeout pins it; the
  e-1b closeout pinned 2321).
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each Ground-truth + RECON-FINDING surface holds.**
  The checklist: the `EmitZ80IndexedEa` helper (`CpuEmitter.cs:1995-1996`); the base arms
  (`EmitZ80LdBody:1758`, `EmitZ80AluBody:1523`/`EmitZ80AluSource:1552-1561`, `EmitZ80IncDec8:1668-1710`,
  `EmitZ80Add16:1719-1741`, `EmitZ80StackBody:1819`, `EmitZ80ExchangeBody:1847-1882`, `EmitZ80FlowBody:1998`
  the `JumpIndirect` arm); `EmitWz:1978`; `ModeLength:2837` (`Indexed => 3`); the half-view machinery
  (`:63-71`, `:80-99`); the disassembler (`:3083-3158` — **confirm NO `Indexed` arm + the `_ => throw`**,
  G1); `Z80Cycles:478`, `Z80WritesFlags:396`, `isZ80:278-283`, the JIT predicates
  (`ClassifyForJit:3324/3339/3369`, `JitBaseCycles:3279/3287`, `JitOpLiteral:3384/3439`); the importer
  routing (`SpecFileEmitter.cs:169-176` — **confirm NO DD/FD arm**); CPUGEN012 (`SpecFileEmitter.cs:85-90,
  231-241`, G2); the dataset schema (`OpcodeDataset.cs` `OpcodeEntry`); the runner IX/IY set/check
  (`Z80TomHarteRunner.cs:46,64`). Read `Z80CbSemantics.cs` + `Z80EdSemantics.cs` (the derivation pattern
  `Z80DdFdSemantics` copies).

- [ ] **Step 4: Pin the R-bump model (G3 — the load-bearing R subtlety).** Read
  `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` `OnInstructionFetched(int keyBytes)` (~`:125-129`) + how the
  structured `Step` (`CpuEmitter.cs:132-176`) calls it with `__r.Length`. Determine: does R bump by
  `keyBytes` (= `__r.Length`) or by a fixed M1 count? For the ED plane (length 2) it bumps R+2; the
  question is whether a length-3 Indexed op would bump R+3 (WRONG — must be R+2). Pin the answer; if R bumps
  by `keyBytes`, the fix (Task 4 or a small `Z80Cpu` change) is to bump R by the M1 count (prefix + opcode =
  2 for DD/FD), NOT the total length. CONFIRM against the vectors below.

- [ ] **Step 5: Re-derive the WZ/R/flag rules from the vectors (the oracle — do NOT trust the prose).** Open
  these `dd NN.json` (and the matching `fd NN.json`) and CONFIRM (the implementer pins these into the per-op
  tests + the Task 8 iteration):
  - **Indexed memory:** `dd 7e` (LD A,(IX+d): WZ = IX+d, R+2, 19 T — CONFIRMED IX=0x2936 d=0x29 WZ=0x295f);
    `dd 36` (LD (IX+d),n: 4-byte, reads d then n, R+2, 19 T — G5); `dd 34`/`dd 35` (INC/DEC (IX+d): RMW,
    flags, WZ=EA, 23 T); `dd 86` (ADD A,(IX+d): flags, WZ=EA, 19 T); `dd 70` (LD (IX+d),B: store, WZ=EA).
  - **IX 16-bit:** `dd 09` (ADD IX,BC: WZ = pre-op IX+1, R+2, 15 T — CONFIRMED WZ=IX+1); `dd 21` (LD IX,nn:
    no WZ, 14 T); `dd 22` (LD (nn),IX: WZ=nn+1, 20 T); `dd 2a` (LD IX,(nn): WZ=nn+1, 20 T); `dd e3`
    (EX (SP),IX: WZ = new IX, 23 T — CONFIRMED); `dd e9` (JP (IX): no WZ, 8 T); `dd f9` (LD SP,IX: no WZ,
    10 T); `dd e5`/`dd e1` (PUSH/POP IX, 15/14 T); `dd 23`/`dd 2b` (INC/DEC IX: no flags, 10 T).
  - **Undoc half:** `dd 24` (INC IXh: IX hi-byte +1, full flags, R+2, 8 T — CONFIRMED IX 0x38c3→0x39c3);
    `dd 26` (LD IXh,n: 11 T — CONFIRMED hi-byte set); `dd 7c` (LD A,IXh: A←IX hi, 8 T — CONFIRMED A=0x54);
    `dd 84` (ADD A,IXh: flags, 8 T — CONFIRMED A 0x0f+0xe9=0xf8); `dd 2c`/`dd 2d`/`dd 2e` (IXl forms).
  - **Inert prefix:** `dd 04` (INC B: base op identical, IX untouched, WZ unchanged, R+2, 8 T — CONFIRMED
    B 0xb8→0xb9, R 0x4a→0x4c); `dd 00` (NOP: R+2); `dd eb` (EX DE,HL: inert — confirm).
  - For each, the FINAL `r` delta is +2 (G3); the FINAL `ix`/`iy` is the result. The cycle count = the
    vector's `cycles` array length.

- [ ] **Step 6: Cross-check the F1 gap (the D3 derived-count == vector-count guard).** Confirm:
  Run: `pwsh -c "(Get-Content tools/CpuEmulator.SpecImporter/data/z80-opcodes.json | ConvertFrom-Json | Where-Object { $_.prefix -eq '0xDD' }).Count"`
  Expected: **39** (the documented rows). And confirm the vector files:
  Run (bash): list `~/.cache/cpuemulator/vectors/z80/v1/dd *.json` EXCLUDING `dd cb *` → expect **252** files
  (all 0x00–0xFF except cb/dd/ed/fd). Same for `fd`. So **213 DD + 213 FD rows must be derived.** Pin the
  exact opcode set that is MISSING (the 252 vector opcodes minus the 39 dataset opcodes) — `Z80DdFdSemantics`
  must produce a non-null ops-text for ALL 252 (the 39 documented + the 213 derived), and Task 6's importer
  test asserts the dataset has 252 DD + 252 FD core rows after the add.

  > **The `0x36` length discrepancy (G5):** confirm `dd 36` in the dataset is `bytes=4` and the vector is
  > 19 T reading d then n. Decide here whether the decode walk reads operand bytes from `ModeLength` (3) or
  > the dataset `Bytes` (4); the Task 4 indexed-LD arm must read the extra `n` byte for `0x36`. This is the
  > decision G5 flags — resolve it against the structured-walk code + `dd 36.json` BEFORE Task 4.

- [ ] **Step 7:** No commit (read-only). Proceed to Task 1.

---

### Task 1: The indexed micro-op vocabulary + the `Z80Indexed` instruction class (Op records + factories + parser) (TDD)

> Add the `Op` records + `Spec` factories + parser `s_microOpSignatures` + importer `FactoryArity` + the new
> instruction class for the `(IX+d)`-mode families. The IX-16-bit + undoc-half ops REUSE the existing base op
> records (D3/G6: `Add16("IX","BC")`, `IncReg("IXh")`, `Transfer("IXh","B")`, `Load16("IX")`, etc. — the
> half-views + IX pair-view make those operand names valid TODAY), so NO new records are needed for them.
> ONLY the `Indexed`-mode forms (which read a displacement + compute an EA) need new op records. No emitter
> body yet (Tasks 2–4) — this task is the closed vocabulary so the spec table type-checks + the importer
> validates.

**Design decision (recorded — the class/op shape):** the `Indexed`-mode forms map to ONE new instruction
class `Z80Indexed` (mode `Indexed`), discriminated by the op record (the `EmitZ80MiscBody`/`Z80EdOp`
`switch (kind)` pattern):
- `DdFdLdIndexed(string Op, string Reg)` — Op ∈ "LOAD"/"STORE"; Reg ∈ "B".."A" (the register loaded from /
  stored to `(IX+d)`). `LD r,(IX+d)` = LOAD; `LD (IX+d),r` = STORE.
- `DdFdStoreImmIndexed()` — `LD (IX+d),n` (reads displacement THEN immediate; G5).
- `DdFdAluIndexed(string Op)` — Op ∈ "ADD"/"ADC"/"SUB"/"SBC"/"AND"/"XOR"/"OR"/"CP" (`ALU A,(IX+d)`).
- `DdFdIncDecIndexed(bool IsDec)` — `INC (IX+d)`/`DEC (IX+d)`.

The index register (IX vs IY) is NOT carried on the op record — it is derived in the emit arm from the
`OperationKey` high byte (`(key>>8)==0xDD` → "IX" else "IY"), matching how the disassembler arm (G1) picks
IX/IY. This keeps ONE set of op records for both planes (the FD rows carry the same op text; the prefix in
the row's `OperationKey` selects the register). **Confirm at Task 0 that the emit arm has access to
`insn.OperationKey`/`insn.Opcode` to read the prefix** — if not, carry the index register on the op record
(`DdFdLdIndexed(Op, Reg, IndexReg)`); the literal code below assumes the OperationKey is reachable (it is —
the descriptor table is keyed by it).

**Files:**
- Modify: `src/CpuEmulator.Core/Specification/Op.cs`, `Spec.cs`
- Modify: `src/CpuEmulator.Generators/SpecModel.cs` (the `Z80Indexed` `InstructionClass`)
- Modify: `src/CpuEmulator.Generators/SpecParser.cs` (`s_microOpSignatures`, the op-kind set, `ClassifyOps`,
  `ValidateModeForClass`, the status-touch predicate)
- Modify: `tools/CpuEmulator.SpecImporter/SemanticsMap.cs` (`FactoryArity`)
- Test: `tests/CpuEmulator.Tests/Generators/Z80IndexedVocabularyTests.cs` (create);
  `tests/CpuEmulator.Tests/Generators/Z80IndexedClassifyTests.cs` (create)

- [ ] **Step 1: Write the failing vocabulary test.** Create `Z80IndexedVocabularyTests.cs`:

```csharp
using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedVocabularyTests
{
    [Fact]
    public void Indexed_ld_factory_carries_op_and_reg()
    {
        var ld = (DdFdLdIndexedOp)DdFdLdIndexed("LOAD", "A");
        Assert.Equal("LOAD", ld.Op); Assert.Equal("A", ld.Reg);
        var st = (DdFdLdIndexedOp)DdFdLdIndexed("STORE", "B");
        Assert.Equal("STORE", st.Op); Assert.Equal("B", st.Reg);
    }

    [Fact]
    public void Indexed_alu_and_incdec_and_storeimm_build()
    {
        Assert.Equal("ADD", ((DdFdAluIndexedOp)DdFdAluIndexed("ADD")).Op);
        Assert.True(((DdFdIncDecIndexedOp)DdFdIncDecIndexed(true)).IsDec);
        Assert.IsType<DdFdStoreImmIndexedOp>(DdFdStoreImmIndexed());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedVocabularyTests"` → FAIL (records/factories absent).

- [ ] **Step 3: Add the `Op` records.** In `src/CpuEmulator.Core/Specification/Op.cs`, after the ED-block
  records (the `EdBlockOp` line from M3.4d):

```csharp

// ── M3.4e-2 DD/FD indexed plane (additive; the index register IX/IY is read from the OperationKey prefix) ──
// LD r,(IX+d) / LD (IX+d),r. Op ∈ "LOAD"/"STORE"; Reg ∈ "B".."A" (the register loaded-from/stored-to (IX+d)).
public sealed record DdFdLdIndexedOp(string Op, string Reg) : Op;
// LD (IX+d),n — reads the displacement THEN the immediate (G5; 4-byte form).
public sealed record DdFdStoreImmIndexedOp : Op;
// ALU A,(IX+d). Op ∈ "ADD"/"ADC"/"SUB"/"SBC"/"AND"/"XOR"/"OR"/"CP".
public sealed record DdFdAluIndexedOp(string Op) : Op;
// INC (IX+d) / DEC (IX+d). IsDec distinguishes.
public sealed record DdFdIncDecIndexedOp(bool IsDec) : Op;
```

- [ ] **Step 4: Add the `Spec` factories.** In `src/CpuEmulator.Core/Specification/Spec.cs`, after the ED
  factories:

```csharp
    // ── M3.4e-2 DD/FD indexed plane (additive) ──
    public static Op DdFdLdIndexed(string op, string reg) => new DdFdLdIndexedOp(op, reg);
    public static Op DdFdStoreImmIndexed() => new DdFdStoreImmIndexedOp();
    public static Op DdFdAluIndexed(string op) => new DdFdAluIndexedOp(op);
    public static Op DdFdIncDecIndexed(bool isDec) => new DdFdIncDecIndexedOp(isDec);
```

- [ ] **Step 5: Add the parser `s_microOpSignatures`.** In `src/CpuEmulator.Generators/SpecParser.cs`, after
  the ED entries (reusing `ArgKind.Str`/`ArgKind.Bool`):

```csharp
        // M3.4e-2: the DD/FD indexed ops.
        ["DdFdLdIndexed"]      = new[] { ArgKind.Str, ArgKind.Str },   // DdFdLdIndexed("LOAD", "A")
        ["DdFdStoreImmIndexed"]= System.Array.Empty<ArgKind>(),
        ["DdFdAluIndexed"]     = new[] { ArgKind.Str },                // DdFdAluIndexed("ADD")
        ["DdFdIncDecIndexed"]  = new[] { ArgKind.Bool },               // DdFdIncDecIndexed(true)
```

- [ ] **Step 6: Add the `InstructionClass` member.** In `src/CpuEmulator.Generators/SpecModel.cs`, after the
  `Z80EdBlock,` member (M3.4d):

```csharp
    Z80Indexed,   // M3.4e-2: the (IX+d)/(IY+d) memory ops (LD/ALU/INC-DEC/LD-imm on the indexed EA)
```

- [ ] **Step 7: Add the op-kind class set + `ClassifyOps` + `ValidateModeForClass` + status-touch.** In
  `src/CpuEmulator.Generators/SpecParser.cs`:
  - After `s_z80EdOpKinds` (M3.4c), add:

```csharp
    // ── M3.4e-2 DD/FD indexed op-kind class set (additive) ──
    private static readonly HashSet<string> s_z80IndexedOpKinds = new(System.StringComparer.Ordinal)
    {
        "DdFdLdIndexed", "DdFdStoreImmIndexed", "DdFdAluIndexed", "DdFdIncDecIndexed",
    };
```
  - In `ClassifyOps`, after the `s_z80EdOpKinds` arm:

```csharp
        if (s_z80IndexedOpKinds.Contains(first))
        {
            if (ops.Length != 1) { error = "Z80 indexed class must contain exactly one op"; return null; }
            return InstructionClass.Z80Indexed;
        }
```
  - In `ValidateModeForClass`, after the `InstructionClass.Z80EdOp =>` arm:

```csharp
            // M3.4e-2: the indexed (IX+d)/(IY+d) memory ops are all Indexed mode.
            InstructionClass.Z80Indexed =>
                mode == "Indexed" ? null : "Z80 indexed class ((IX+d)/(IY+d)) requires Indexed mode",
```
  - In the status-touch predicate, add `Z80Indexed` to the `is … or …` chain (ALU/INC/DEC (IX+d) write F;
    LD (IX+d),r / r,(IX+d) / n don't — the class is eligible, the per-op `Z80WritesFlags` decides):

```csharp
                or InstructionClass.Z80EdIo or InstructionClass.Z80EdOp or InstructionClass.Z80EdBlock
                or InstructionClass.Z80Indexed
```

- [ ] **Step 8: Add the importer `FactoryArity`.** In `tools/CpuEmulator.SpecImporter/SemanticsMap.cs`,
  after the ED entries:

```csharp
        // M3.4e-2: the DD/FD indexed ops.
        ["DdFdLdIndexed"]       = 2,
        ["DdFdStoreImmIndexed"] = 0,
        ["DdFdAluIndexed"]      = 1,
        ["DdFdIncDecIndexed"]   = 1,
```

  > `AllowedArgPattern` already accepts `"\w+"` (the op-name + register strings) + `true`/`false` (IsDec) —
  > no widening needed. CONFIRM at Task 0 by reading `AllowedArgPattern` (it is
  > `^("\w+"|"\(HL\)"|Flag\.\w+|true|false|\d+)$`).

- [ ] **Step 9: Write the classify test (synthetic spec compiles).** Create
  `Z80IndexedClassifyTests.cs`, mirroring `Z80EdClassifyTests.cs`: a synthetic DD spec with one row per
  indexed family, asserting no GENERATOR error diagnostics + the `Op…()` methods exist. Requires STUB emit
  arms (Step 10) + the `Indexed` disassembler arm (Task 4 adds the real one; for Task 1 a minimal arm so the
  classify test's generation does not throw — see the note).

```csharp
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedClassifyTests
{
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("idx")]
        public static class IdxSpec
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
                Prefixes: [new PrefixByte(0xDD)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0x7E, "LD",  AddrMode.Indexed, [DdFdLdIndexed("LOAD", "A")]),
                Insn(0xDD, 0x70, "LD",  AddrMode.Indexed, [DdFdLdIndexed("STORE", "B")]),
                Insn(0xDD, 0x36, "LD",  AddrMode.Indexed, [DdFdStoreImmIndexed()]),
                Insn(0xDD, 0x86, "ADD", AddrMode.Indexed, [DdFdAluIndexed("ADD")]),
                Insn(0xDD, 0x34, "INC", AddrMode.Indexed, [DdFdIncDecIndexed(false)]),
            ];
        }
        """;

    [Fact]
    public void Indexed_rows_classify_and_compile()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("private void OpDD7E()", result.GeneratedText);
        Assert.Contains("private void OpDD34()", result.GeneratedText);
    }
}
```

  > Filter `GeneratorDiagnostics` (not `AllErrors`) — the synthetic spec declares no hand-written partial, so
  > COMPILATION reports missing-partial errors that are NOT classification failures (the `Z80EdClassifyTests`
  > precedent). The generated `OpDD7E` etc. prove the rows classify + the `Indexed` mode + the (stub) bodies
  > emit + the disassembler arm does not throw on an `Indexed` row.

- [ ] **Step 10: Add the dispatch arm + STUB emit body + the minimal `Indexed` disassembler arm + the
  `isZ80`/`Z80Cycles`/JIT glue.** In `src/CpuEmulator.Generators/CpuEmitter.cs`:
  - In `EmitOpcodeMethod`'s `switch (opClass)`, after `case InstructionClass.Z80EdBlock:`, add:

```csharp
            case InstructionClass.Z80Indexed:
                EmitZ80IndexedBody(sb, instruction, pc, pcType, statusReg, flags);
                break;
```
  - Add the STUB method (filled in Tasks 2–4) near `EmitZ80BitBody`:

```csharp
    private static void EmitZ80IndexedBody(
        StringBuilder sb, InstructionModel insn, string pc, string pcType, string? statusReg, FlagBitMap flags)
    {
        sb.AppendLine("        _ = 0;   // TODO Tasks 2-4 (the (IX+d) families)");
    }
```
  - **Add the `Indexed` disassembler arm (G1) NOW** so the classify test's generation does not throw. In
    `EmitDisassembler`'s `instruction.Mode switch` (`:3100-3152`), before the `_ => throw` default, add:

```csharp
    "Indexed" =>
        // M3.4e-2: (IX+d)/(IY+d). The index register is the prefix in the OperationKey high byte
        // (0xDD -> IX, else IY). The displacement is the first operand byte (operandLo). The
        // disassembly string is NOT vector-gated — it need only be well-formed.
        $"            0x{instruction.OperationKey:X} => $\"{m} ({((instruction.OperationKey >> 8) == 0xDD ? "IX" : "IY")}+${{operandLo:X2}})\",",
```
  > **Confirm the `OperationKey` width in the case label.** The DD/FD-core key is `(0xDD<<8)|op` = 0xDD__
  > (16-bit, fits `:X` / `:X2`-min-width). Match the surrounding arms' format-string shape exactly (read
  > the `ExtendedAddress`/`Bit` arms at `:3083-3158`); the literal above mirrors the `ExtendedAddress` arm's
  > `0x{OperationKey:X2} => $"…"` pattern but uses the full key. Adjust `:X2`→`:X` if needed so 0xDD7E
  > renders correctly.
  - Extend `isZ80` (`:278-283`) with `or InstructionClass.Z80Indexed`.
  - In `Z80Cycles`, add placeholder arms before the final `_ => throw` (real cycles in Tasks 2–4):

```csharp
        (InstructionClass.Z80Indexed, "DdFdIncDecIndexed", _) => 23,   // INC/DEC (IX+d)
        (InstructionClass.Z80Indexed, "DdFdStoreImmIndexed", _) => 19, // LD (IX+d),n
        (InstructionClass.Z80Indexed, _, _) => 19,                     // LD r,(IX+d)/(IX+d),r ; ALU A,(IX+d)
```
  - Add `Z80Indexed` to the 3 JIT predicates exactly as ED did: the `z80` bool (`ClassifyForJit:3339-3343`),
    the `jitClass => "Register"` arm (`:3369-3370`), `JitBaseCycles`'s z80 list (`:3287-3291`); and in
    `JitOpLiteral` (`:3439-3453`) add `case "DdFdLdIndexed": case "DdFdStoreImmIndexed": case
    "DdFdAluIndexed": case "DdFdIncDecIndexed": break;` (JIT fallback, no IL — the D4 decision).

  > **Why the stub + minimal disassembler arm here:** Task 1 proves CLASSIFICATION + that an `Indexed` row
  > generates (incl. the disassembler) WITHOUT committing the body. Tasks 2–4 replace the stub body + the
  > placeholder cycles. The real `Z80Spec.cs` is NOT regenerated until Task 7, so `Z80Cpu` still compiles
  > from the e-1b spec where no `Z80Indexed` row exists — the stub arm is dormant until Task 7. (The
  > disassembler arm is harmless until then.)

- [ ] **Step 11: Run both tests to verify they pass.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedVocabularyTests|FullyQualifiedName~Z80IndexedClassifyTests"`
  → PASS.

- [ ] **Step 12: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical — no
  `Z80Indexed` row in the 6502 spec; the new disassembler arm + class are unreachable by the 6502).

- [ ] **Step 13: Commit.**

```bash
git add src/CpuEmulator.Core/Specification/Op.cs src/CpuEmulator.Core/Specification/Spec.cs \
        src/CpuEmulator.Generators/SpecModel.cs src/CpuEmulator.Generators/SpecParser.cs \
        src/CpuEmulator.Generators/CpuEmitter.cs tools/CpuEmulator.SpecImporter/SemanticsMap.cs \
        tests/CpuEmulator.Tests/Generators/Z80IndexedVocabularyTests.cs \
        tests/CpuEmulator.Tests/Generators/Z80IndexedClassifyTests.cs
git commit -m "$(cat <<'EOF'
feat(generators): DD/FD indexed micro-op vocabulary + Z80Indexed class + Indexed disassembler arm (classification only)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 2: `LD r,(IX+d)` / `LD (IX+d),r` / `LD (IX+d),n` — `EmitZ80IndexedBody` LD arms (TDD)

> Fill the LD arms of `EmitZ80IndexedBody`. Read the displacement byte, compute `__ea` via
> `EmitZ80IndexedEa`, then LOAD `ReadBus(__ea) → r` / STORE `r → WriteBus(__ea)` / read the immediate `n` and
> STORE `n → WriteBus(__ea)`. WZ = `__ea`. The index register is read from the OperationKey prefix. Cycles
> 19 (LD r,(IX+d)/(IX+d),r/(IX+d),n).

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitZ80IndexedBody` LD arms + `Z80WritesFlags`)
- Create: `tools/CpuEmulator.SpecImporter/Z80DdFdSemantics.cs` (the COMPLETE derivation file — all families,
  so subsequent tasks only TEST against it; the emit arms arrive task-by-task)
- Test: `tests/CpuEmulator.Tests/Generators/Z80IndexedLdTests.cs` (create)

- [ ] **Step 1: Create the `Z80DdFdSemantics` derivation generator (the complete file).** Create
  `tools/CpuEmulator.SpecImporter/Z80DdFdSemantics.cs` — the DD/FD analogue of `Z80EdSemantics`, mapping a
  DD/FD core opcode (0x00–0xFF except prefix bytes) to its ops-text per the D3 rule. The `isIy` flag selects
  IY/IYh/IYl text where a half/pair is named; the `Indexed`-mode rows carry the plane-agnostic `DdFd…` op
  (the emit arm reads IX/IY from the OperationKey prefix). Write ALL arms now:

```csharp
namespace CpuEmulator.SpecImporter;

/// <summary>
/// Computes the micro-op text for a Z80 DD/FD-PLANE CORE opcode ALGORITHMICALLY (M3.4e-2), the DD/FD
/// analogue of <see cref="Z80CbSemantics"/>/<see cref="Z80EdSemantics"/>. A DD (IX) / FD (IY) prefix
/// REINTERPRETS the following byte (the D3 rule, re-derived from the SingleStepTests vectors):
///   (1) an op that names (HL) as memory -> (IX+d)/(IY+d) (mode Indexed): LD/ALU/INC-DEC/LD-imm;
///   (2) an op naming H/L (and NO memory) -> re-read as IXh/IXl (the undoc half ops);
///   (3) an op naming the HL pair -> the HL slot becomes IX/IY (the 16-bit ops);
///   (4) otherwise the prefix is INERT -> the base op (PC+2, R+2).
/// Returns the ops-text for ANY DD/FD core opcode (it owns all 252), delegating (2)/(3)/(4) to
/// <see cref="Z80BaseSemantics"/> with the IX/IXh/IXl operand substitution applied to the base op-text.
/// </summary>
public static class Z80DdFdSemantics
{
    /// <summary>Ops-text for a DD/FD core opcode. isIy selects the IY plane (IY/IYh/IYl).</summary>
    public static string? OpsFor(int opcode, bool isIy)
    {
        // The four prefix bytes are not core opcodes (a DD followed by CB/DD/ED/FD is the compound/chain
        // case — out of scope here; the dataset has no such core row).
        if (opcode is 0xCB or 0xDD or 0xED or 0xFD) return null;

        // (1) The Indexed (IX+d)/(IY+d) forms — keyed off the base opcode's (HL)-naming members.
        string? indexed = IndexedFor(opcode);
        if (indexed is not null) return indexed;

        // (2)/(3)/(4) — derive the BASE op-text, then substitute H/L -> IXh/IXl and the HL pair -> IX
        // for the half/16-bit ops. The base decoder is the source of truth for the inert + the regular
        // operand resolution; the substitution is a textual rewrite of the produced op-text.
        string? baseOps = Z80BaseSemantics.OpsFor(opcode, /*mnemonic*/ MnemonicHint(opcode), /*mode*/ ModeHint(opcode));
        if (baseOps is null) return null;
        return SubstituteHalfAndPair(baseOps, isIy);
    }

    // The (HL)-naming base opcodes that become (IX+d)/(IY+d). Re-derived from the base octal encoding:
    //   LD r,(HL): x=1,z=6 (0x46/4E/56/5E/66/6E/7E) ; LD (HL),r: x=1,y=6 (0x70-0x77 except 0x76 HALT)
    //   LD (HL),n: 0x36 ; ALU A,(HL): x=2,z=6 (0x86/8E/96/9E/A6/AE/B6/BE) ; INC/DEC (HL): 0x34/0x35.
    private static string? IndexedFor(int opcode)
    {
        // INC/DEC (HL) -> INC/DEC (IX+d)
        if (opcode == 0x34) return "[DdFdIncDecIndexed(false)]";
        if (opcode == 0x35) return "[DdFdIncDecIndexed(true)]";
        // LD (HL),n -> LD (IX+d),n  (4-byte: displacement THEN immediate; G5)
        if (opcode == 0x36) return "[DdFdStoreImmIndexed()]";
        // ALU A,(HL) -> ALU A,(IX+d)  (x=2, z=6)
        if ((opcode & 0xC7) == 0x86)
            return $"[DdFdAluIndexed(\"{AluName((opcode >> 3) & 7)}\")]";
        // LD r,(HL) -> LD r,(IX+d)  (x=1, z=6, y != 6 because y=6 z=6 = 0x76 HALT)
        if ((opcode & 0xC7) == 0x46 && ((opcode >> 3) & 7) != 6)
            return $"[DdFdLdIndexed(\"LOAD\",\"{Reg8[(opcode >> 3) & 7]}\")]";
        // LD (HL),r -> LD (IX+d),r  (x=1, y=6, z != 6 because y=6 z=6 = 0x76 HALT)
        if ((opcode & 0xF8) == 0x70 && (opcode & 7) != 6)
            return $"[DdFdLdIndexed(\"STORE\",\"{Reg8[opcode & 7]}\")]";
        return null;
    }

    private static readonly string[] Reg8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    private static readonly string[] Alu = ["ADD", "ADC", "SUB", "SBC", "AND", "XOR", "OR", "CP"];
    private static string AluName(int y) => Alu[y];

    // (2)/(3) the textual substitution: in a base op-text, rewrite a STANDALONE "H"/"L" register name to
    // "IXh"/"IXl" (or IYh/IYl) and the HL pair to IX/IY. The base op-text uses quoted operand names
    // (e.g. Transfer("H","B"), IncReg("H"), Add16("HL","BC"), Load16("HL")). The (HL) indirect forms are
    // ALREADY handled by IndexedFor (returned above), so no "(HL)" string survives to here.
    private static string SubstituteHalfAndPair(string baseOps, bool isIy)
    {
        string h = isIy ? "IYh" : "IXh";
        string l = isIy ? "IYl" : "IXl";
        string pair = isIy ? "IY" : "IX";
        return baseOps
            .Replace("\"H\"", $"\"{h}\"")
            .Replace("\"L\"", $"\"{l}\"")
            .Replace("\"HL\"", $"\"{pair}\"");
    }

    // The base decoder needs the mnemonic + mode to resolve operands. The dataset carries them for the
    // documented rows; for the DERIVED rows we reconstruct them from the opcode (the base octal encoding).
    // (See Task 2 Step 1 note: prefer reading mnemonic/mode from the dataset row when present; the hints
    // below are the fallback for opcodes with no dataset row. RESOLVE the exact source at implementation —
    // the importer already has the OpcodeEntry's Mnemonic/Mode, so PASS THEM IN rather than re-deriving.)
    private static string MnemonicHint(int opcode) => /* see note */ throw new System.NotImplementedException();
    private static string ModeHint(int opcode) => /* see note */ throw new System.NotImplementedException();
}
```

  > **CRITICAL implementation note (resolve at Task 2 Step 1).** The skeleton above shows the STRUCTURE; the
  > `MnemonicHint`/`ModeHint` placeholders are a SEAM, not a stub to ship. The importer already has each
  > derived row's `Mnemonic` + `Mode` (from the `OpcodeEntry` it is emitting). The CLEANEST shape is
  > `OpsFor(int opcode, string mnemonic, string mode, bool isIy)` — mirroring `Z80BaseSemantics.OpsFor(op,
  > mnemonic, mode)` — so the base decoder gets the dataset's mnemonic/mode directly and `Z80DdFdSemantics`
  > only (a) detects the Indexed forms by opcode and (b) substitutes H/L/HL → IXh/IXl/IX on the base text.
  > **Change the signature to take mnemonic+mode** (the routing in SpecFileEmitter has them — Step 4); the
  > `MnemonicHint`/`ModeHint` re-derivation is then DELETED. The literal `IndexedFor`/`SubstituteHalfAndPair`
  > logic is the load-bearing part and is correct as written. Confirm `Z80BaseSemantics.OpsFor` returns the
  > right base text for every inert/half/16-bit opcode (it already powers the unprefixed plane).
  >
  > **The half-substitution scope (rule-2 vs rule-1, the G6 subtlety).** `SubstituteHalfAndPair` rewrites
  > `"H"`/`"L"` to `"IXh"`/`"IXl"` ONLY in op-texts that survive past `IndexedFor` (i.e. ops with NO `(HL)`
  > memory operand — `IndexedFor` already returned for those). So `LD H,B` (0x60) → base `Transfer("B","H")`
  > → substituted `Transfer("B","IXh")` (correct: LD IXh,B); `LD A,(HL)` (0x7E) → `IndexedFor` returns the
  > indexed op (correct, no half-substitution). This realizes the rule precisely. VERIFY against `dd 60`
  > (LD IXh,B) + `dd 7e` (LD A,(IX+d)) at Task 8.

- [ ] **Step 2: Write the failing test.** Create `tests/CpuEmulator.Tests/Generators/Z80IndexedLdTests.cs`.
  The synthetic CPU uses `IAddressSpace _bus` (the structured-spec shape — M3.4d deviation #1). Declare
  `IX`/`IXh`/`IXl` + the 8-bit regs + WZ + Q + Im. Seed the program + the EA-target byte; Step; assert.

```csharp
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedLdTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ixld")]
        public static class IxldSpec
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
                Prefixes: [new PrefixByte(0xDD)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0x7E, "LD", AddrMode.Indexed, [DdFdLdIndexed("LOAD", "A")]),
                Insn(0xDD, 0x70, "LD", AddrMode.Indexed, [DdFdLdIndexed("STORE", "B")]),
                Insn(0xDD, 0x36, "LD", AddrMode.Indexed, [DdFdStoreImmIndexed()]),
            ];
        }

        public sealed partial class IxldCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public IxldCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) => _bus.Read8(a);
            private void WriteBus(uint a, byte v) => _bus.Write8(a, v);
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    private static (object Cpu, System.Type T, AddressSpace Bus) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.IxldCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t, bus);
    }
    private static void Set(object cpu, System.Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, System.Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;

    [Fact]
    public void LD_A_IXplusd_reads_EA_and_sets_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x7E); bus.Write8(2, 0x05);   // LD A,(IX+5)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x2000); Set(cpu, t, "WZ", 0xFFFF);
        bus.Write8(0x2005, 0x99);                                        // (IX+5) = 0x99
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x99, (byte)Get(cpu, t, "A"));
        Assert.Equal(0x2005u, (uint)Get(cpu, t, "WZ"));                  // WZ = EA
    }

    [Fact]
    public void LD_IXplusd_B_writes_EA_and_sets_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x70); bus.Write8(2, 0xFE);   // LD (IX-2),B
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x3000); Set(cpu, t, "B", 0x77);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x77, bus.Read8(0x2FFE));                           // IX + (sbyte)0xFE = 0x3000-2
        Assert.Equal(0x2FFEu, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void LD_IXplusd_n_reads_disp_then_imm()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x36); bus.Write8(2, 0x01); bus.Write8(3, 0xAB); // LD (IX+1),0xAB
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x4000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xAB, bus.Read8(0x4001));
        Assert.Equal(0x4u, (uint)Get(cpu, t, "PC"));                     // PC advanced 4 (prefix+op+d+n)
    }
}
```

  > Use the negative-displacement case (`0xFE` = -2) to exercise the signed EA (`(sbyte)` in
  > `EmitZ80IndexedEa`). The `LD (IX+d),n` test pins G5 (PC+4, the disp+imm read order).

- [ ] **Step 3: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedLdTests"` → FAIL (the stub body does nothing).

- [ ] **Step 4: Implement the LD arms of `EmitZ80IndexedBody`.** In `CpuEmitter.cs`, REPLACE the stub with a
  `switch (kind)` dispatcher + the LD arms. The index register comes from the OperationKey prefix; the
  displacement is the first operand byte (read via the `pc`-advance idiom):

```csharp
    private static void EmitZ80IndexedBody(
        StringBuilder sb, InstructionModel insn, string pc, string pcType, string? statusReg, FlagBitMap flags)
    {
        string f = statusReg ?? "F";
        string ix = (insn.OperationKey >> 8) == 0xDD ? "IX" : "IY";   // the index register (G1 / Task 1)
        // Read the displacement byte (the byte after the opcode) and compute the signed EA.
        sb.AppendLine($"        byte d = ReadBus({pc});");
        sb.AppendLine($"        {pc} = unchecked(({pcType})({pc} + 1));");
        EmitZ80IndexedEa(sb, ix, "d");   // -> ushort __ea = unchecked((ushort)(IX + (sbyte)(d)));
        EmitWz(sb, "__ea");              // WZ = the computed EA (the (IX+d) MEMPTR rule)

        switch (insn.Ops[0].Kind)
        {
            case "DdFdLdIndexed":
            {
                string op = Unquote(insn.Ops[0].Args[0]);    // LOAD / STORE
                string reg = Unquote(insn.Ops[0].Args[1]);   // B..A
                if (op == "LOAD")
                    sb.AppendLine($"        {reg} = ReadBus(__ea);");
                else
                    sb.AppendLine($"        WriteBus(__ea, {reg});");
                sb.AppendLine($"        _cycles += {19 - 2 - 1 - 1};   // 19 T: -2 keybytes, -1 disp, -1 mem");
                return;
            }
            case "DdFdStoreImmIndexed":
            {
                sb.AppendLine($"        byte n = ReadBus({pc});");
                sb.AppendLine($"        {pc} = unchecked(({pcType})({pc} + 1));");
                sb.AppendLine("        WriteBus(__ea, n);");
                sb.AppendLine($"        _cycles += {19 - 2 - 1 - 1 - 1};   // 19 T: -2 key, -1 disp, -1 imm, -1 mem");
                return;
            }
            case "DdFdAluIndexed":  EmitZ80IndexedAlu(sb, insn, f, flags); return;       // Task 3
            case "DdFdIncDecIndexed": EmitZ80IndexedIncDec(sb, insn, f, flags); return;  // Task 4
            default:
                throw new System.InvalidOperationException($"Z80Indexed: no template for '{insn.Ops[0].Kind}'");
        }
    }
```

  Add MINIMAL one-line stubs for `EmitZ80IndexedAlu`/`EmitZ80IndexedIncDec` (Tasks 3/4 fill them) so the
  generator COMPILES now — each `{ sb.AppendLine("        _ = 0;   // TODO Task N"); }`.

  > **Cycle arithmetic — CONFIRM against the real CPU's bus-charge model.** The literal `_cycles += N`
  > above assumes the synthetic `ReadBus`/`WriteBus` charge 0 and the structured `Step` charges
  > `__r.Length`. The REAL `Z80Cpu` may charge cycles inside `ReadBus`/`WriteBus`; the synthetic tests
  > assert REGISTER/MEMORY/WZ, NOT cycles — the cycle count is gated by the Task 8 TomHarte sweep. Read the
  > base `EmitZ80LdBody`'s `EmitInternal(sb, total, busReads, busWrites)` cycle-balancer (`:1712`) and use
  > the SAME mechanism (`EmitInternal`) instead of a raw `_cycles +=`, so the cycle model matches base/CB/ED
  > exactly. **Prefer `EmitInternal` over the raw arithmetic shown** — read it at Task 0 and mirror it; the
  > raw form above is illustrative of the T-count only.
  >
  > **The displacement is read BEFORE the EA + memory access (G4).** The order is: opcode (decode walk) →
  > `d` → `__ea` → `ReadBus/WriteBus(__ea)`. For `LD (IX+d),n` the `n` is read AFTER `d` (G5). The PC
  > advances past d (and n) here, so `__r.Length` (3 or 4) and PC agree.

- [ ] **Step 5: Update `Z80WritesFlags`.** In `Z80WritesFlags`, add the `Z80Indexed` arm (LD writes no
  flags; ALU + INC/DEC do — but those are Tasks 3/4; the LD-only arm for now):

```csharp
            InstructionClass.Z80Indexed => insn.Ops[0].Kind switch
            {
                "DdFdAluIndexed" or "DdFdIncDecIndexed" => true,   // (ALU/INC/DEC write F — Tasks 3/4)
                _ => false,                                        // LD r,(IX+d) / (IX+d),r / (IX+d),n
            },
```

- [ ] **Step 6: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedLdTests"` → PASS.

- [ ] **Step 7: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 8: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs tools/CpuEmulator.SpecImporter/Z80DdFdSemantics.cs \
        tests/CpuEmulator.Tests/Generators/Z80IndexedLdTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): LD r,(IX+d) / (IX+d),r / (IX+d),n — wire EmitZ80IndexedEa, WZ=EA; Z80DdFdSemantics derivation

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 3: ALU `A,(IX+d)` — `EmitZ80IndexedAlu` (TDD)

> Fill `EmitZ80IndexedAlu`. Read the displacement → `__ea` → `byte data = ReadBus(__ea)` → run the 8-bit ALU
> op (ADD/ADC/SUB/SBC/AND/XOR/OR/CP) against A, reusing the existing `EmitZ80Alu8` flag math. WZ = `__ea`.
> Cycles 19.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitZ80IndexedAlu`)
- Test: `tests/CpuEmulator.Tests/Generators/Z80IndexedAluTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create `Z80IndexedAluTests.cs` (the `Z80IndexedLdTests` synthetic
  shape; rows `Insn(0xDD, 0x86, "ADD", AddrMode.Indexed, [DdFdAluIndexed("ADD")])` +
  `Insn(0xDD, 0xBE, "CP", AddrMode.Indexed, [DdFdAluIndexed("CP")])`). Pin the result + flags from a known
  case (or `dd 86.json`). Assert A, S/Z/H/N/C/P-V, and WZ = EA.

```csharp
    [Fact]
    public void ADD_A_IXplusd_adds_and_sets_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x86); bus.Write8(2, 0x02);   // ADD A,(IX+2)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x5000); Set(cpu, t, "A", 0x10);
        bus.Write8(0x5002, 0x22);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x32, (byte)Get(cpu, t, "A"));                      // 0x10 + 0x22
        Assert.Equal(0x00, (byte)Get(cpu, t, "F") & 0x02);              // N = 0 (add)
        Assert.Equal(0x5002u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void CP_A_IXplusd_compares_without_storing()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0xBE); bus.Write8(2, 0x00);   // CP (IX+0)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x6000); Set(cpu, t, "A", 0x42);
        bus.Write8(0x6000, 0x42);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x42, (byte)Get(cpu, t, "A"));                      // A unchanged
        Assert.Equal(0x40, (byte)Get(cpu, t, "F") & 0x40);              // Z = 1 (equal)
        Assert.Equal(0x02, (byte)Get(cpu, t, "F") & 0x02);              // N = 1 (subtract)
    }
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedAluTests"` → FAIL (stub).

- [ ] **Step 3: Implement `EmitZ80IndexedAlu`.** REPLACE the stub. Read the base `EmitZ80AluBody`/
  `EmitZ80Alu8` (`:1523`/`:1519`) to find the flag-math helper it calls for an 8-bit ALU op against a `data`
  byte, and REUSE it (the displacement + EA are already emitted by `EmitZ80IndexedBody` before the dispatch,
  so this arm starts with `__ea` + WZ already set):

```csharp
    /// <summary>ALU A,(IX+d): data = ReadBus(__ea); run the 8-bit ALU op against A (reusing the base
    /// EmitZ80Alu8 flag math). __ea + WZ are already emitted by EmitZ80IndexedBody. Cycles 19.</summary>
    private static void EmitZ80IndexedAlu(StringBuilder sb, InstructionModel insn, string f, FlagBitMap flags)
    {
        string op = Unquote(insn.Ops[0].Args[0]);   // ADD/ADC/SUB/SBC/AND/XOR/OR/CP
        sb.AppendLine("        byte data = ReadBus(__ea);");
        EmitZ80Alu8(sb, op, "data", f, flags);      // the EXISTING 8-bit ALU flag-word emitter (read its
                                                    // exact signature at Task 0 + match it; CP writes no A)
        sb.AppendLine($"        _cycles += {19 - 2 - 1 - 1};   // 19 T: -2 key, -1 disp, -1 mem");
    }
```

  > **CONFIRM `EmitZ80Alu8`'s signature + how the base ALU arm calls it.** Read `EmitZ80AluBody:1523-1548`
  > to see exactly how the base `ADD A,(HL)` resolves `data` + calls the flag emitter (whether it is
  > `EmitZ80Alu8(sb, op, srcExpr, f, flags)` or a different shape). MIRROR it — the indexed arm differs ONLY
  > in that `data` comes from `ReadBus(__ea)` instead of `ReadBus(HL)`. If the base arm computes the result
  > into A inline, the indexed arm does the same. Prefer `EmitInternal` for the cycle balance (see Task 2).

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedAluTests"` → PASS.

- [ ] **Step 5: Full gate** (`dotnet test`; `dotnet build --no-incremental -warnaserror`;
  `RegeneratedSpecTests`) → all green.

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/Z80IndexedAluTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): ALU A,(IX+d)/(IY+d) — reuse the 8-bit ALU flag math; WZ=EA

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 4: `INC (IX+d)` / `DEC (IX+d)` — `EmitZ80IndexedIncDec` + the R-bump fix (G3) (TDD)

> Fill `EmitZ80IndexedIncDec`. Read displacement → `__ea` → `before = ReadBus(__ea)` → `res = before ± 1` →
> full INC/DEC flags (reuse `EmitZ80IncDec8`'s flag math) → `WriteBus(__ea, res)`. WZ = `__ea`. Cycles 23.
> ALSO resolve the R-bump (G3): ensure a 3/4-byte Indexed op bumps R by 2, not by `__r.Length`.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitZ80IndexedIncDec`)
- Modify (maybe): `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` (the `OnInstructionFetched` R-bump, if G3 needs it)
- Test: `tests/CpuEmulator.Tests/Generators/Z80IndexedIncDecTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create `Z80IndexedIncDecTests.cs` (rows
  `Insn(0xDD, 0x34, "INC", AddrMode.Indexed, [DdFdIncDecIndexed(false)])` + `0x35 DEC`). Assert the
  read-modify-write + flags + WZ = EA.

```csharp
    [Fact]
    public void INC_IXplusd_rmw_sets_flags_and_WZ()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xDD); bus.Write8(1, 0x34); bus.Write8(2, 0x03);   // INC (IX+3)
        Set(cpu, t, "PC", 0); Set(cpu, t, "IX", 0x7000);
        bus.Write8(0x7003, 0x7F);                                        // 0x7F -> 0x80 (overflow set)
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x80, bus.Read8(0x7003));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x80, f & 0x80);                                    // S = 1
        Assert.Equal(0x04, f & 0x04);                                    // P/V = overflow (0x7F->0x80)
        Assert.Equal(0x00, f & 0x02);                                    // N = 0 (INC)
        Assert.Equal(0x7003u, (uint)Get(cpu, t, "WZ"));
    }
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedIncDecTests"` → FAIL (stub).

- [ ] **Step 3: Implement `EmitZ80IndexedIncDec`.** REPLACE the stub, reusing `EmitZ80IncDec8`'s flag math
  (read its signature at Task 0; the indexed form differs only in the address — `__ea` instead of `HL`):

```csharp
    /// <summary>INC/DEC (IX+d): RMW on the indexed EA with full INC/DEC flags (P/V = overflow, N per op,
    /// C preserved). __ea + WZ are already emitted by EmitZ80IndexedBody. Cycles 23.</summary>
    private static void EmitZ80IndexedIncDec(StringBuilder sb, InstructionModel insn, string f, FlagBitMap flags)
    {
        bool dec = insn.Ops[0].Args[0] == "true";   // IsDec — bool args stored as the bare word (M3.4c dev #4)
        sb.AppendLine("        byte before = ReadBus(__ea);");
        sb.AppendLine($"        byte res = unchecked((byte)(before {(dec ? "-" : "+")} 1));");
        // The INC/DEC flag word — REUSE the base EmitZ80IncDec8 flag emission (read its body at Task 0 and
        // factor the flag-word emit into a shared helper, or inline the identical mask logic here).
        EmitZ80IncDecFlags(sb, f, flags, increment: !dec);   // see note
        sb.AppendLine("        WriteBus(__ea, res);");
        sb.AppendLine($"        _cycles += {23 - 2 - 1 - 1 - 1};   // 23 T: -2 key, -1 disp, -1 read, -1 write");
    }
```

  > **REUSE the base INC/DEC flag math.** `EmitZ80IncDec8` (`:1668`) already computes the INC/DEC flag word
  > (S/Z/H/P-V from `res`/`before`, N per op, C preserved) into `f`. Two clean options: (a) extract the
  > flag-word portion into a shared `EmitZ80IncDecFlags(sb, f, flags, increment)` that BOTH `EmitZ80IncDec8`
  > and `EmitZ80IndexedIncDec` call (DRY — preferred); (b) inline the identical mask logic. Read
  > `EmitZ80IncDec8:1690-1707` at implementation and pick (a) if the flag block is cleanly separable; the
  > `before`/`res` locals match its convention. The literal `EmitZ80IncDecFlags` call assumes option (a) —
  > if you inline, drop the call and paste the mask block. Prefer `EmitInternal` for the cycle balance.

- [ ] **Step 4: Resolve the R-bump (G3).** Run the synthetic Indexed tests + confirm the R-bump model from
  Task 0 Step 4. If `Z80Cpu.OnInstructionFetched` bumps R by `keyBytes` (= `__r.Length`), a 3-byte Indexed
  op would bump R+3 (WRONG — must be R+2). The fix: `OnInstructionFetched` must bump R by the M1 count
  (prefix + opcode = 2 for a DD/FD op), NOT the total length. Read how ED (length 2) gets R+2 and how the
  base (length 1/2/3) gets its R-bump; the cleanest fix is to bump R by the number of OPCODE bytes (the
  prefix + the final opcode), which for the structured Z80 is derivable. **This is verified by the Task 8
  sweep's final-`r` check** — the synthetic tests don't model R (no `OnInstructionFetched` body), so the R
  fix is gated by TomHarte. Record the exact fix made (likely a 1-line change in `Z80Cpu.cs` or the cycle
  model). If recon (Task 0 Step 4) shows R already bumps by the M1 count correctly, NO change is needed —
  record that.

  > **Why this surfaces in Task 4 (not earlier):** the synthetic tests assert registers/memory/WZ, not R
  > (R needs the `OnInstructionFetched` body the synthetic CPU stubs out). R is FIRST gated by the Task 8
  > TomHarte sweep. Resolving G3 here — at the last emit task before the regen — means the regen (Task 7) +
  > the sweep (Task 8) start with the R model correct. If G3 needs a `Z80Cpu` change, it lands in this
  > task's commit.

- [ ] **Step 5: Run the test to verify it passes + full gate.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedIncDecTests"` → PASS.
  Run: `dotnet test`; `dotnet build --no-incremental -warnaserror`; `RegeneratedSpecTests` → all green.

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs src/CpuEmulator.Cpus.Z80/Z80Cpu.cs \
        tests/CpuEmulator.Tests/Generators/Z80IndexedIncDecTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): INC/DEC (IX+d)/(IY+d) RMW + flags + WZ=EA; R bumps by 2 on DD/FD (not __r.Length) [G3]

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 5: The IX/IY 16-bit ops + the undoc IXh/IXl ops via the DERIVATION (synthetic) (TDD)

> The IX-16-bit ops (ADD IX,rp; LD IX,nn; LD (nn),IX; LD IX,(nn); INC/DEC IX; PUSH/POP IX; EX (SP),IX;
> JP (IX); LD SP,IX) and the undoc IXh/IXl ops (LD IXh,n; INC/DEC IXh; LD r,IXh; ALU A,IXh) REUSE the
> EXISTING base emit arms (G6 — they are base ops with `IX`/`IXh`/`IXl` operand NAMES the derivation
> produces). NO new emit arm is needed. This task PROVES, synthetically, that those base ops behave
> correctly when their operand is IX/IXh/IXl, and that the inert prefix (INC B) works — de-risking the Task 7
> regen. It also proves the `Z80DdFdSemantics` derivation produces the right op-text for these families.

**Files:**
- Test: `tests/CpuEmulator.Tests/Generators/Z80Ix16Tests.cs` (create);
  `tests/CpuEmulator.Tests/Generators/Z80IxHalfOpTests.cs` (create)
  (No NEW emit arm — this task validates the derivation + the base-arm reuse. If a synthetic case FAILS, the
  failure is INFORMATIVE: it means a base arm does not handle the IX/IXh operand, and the fix lands here
  before the real regen.)

- [ ] **Step 1: Write `Z80Ix16Tests.cs`.** A synthetic DD spec exposing the 16-bit ops as their DERIVED base
  ops with IX substituted — e.g. `Insn(0xDD, 0x09, "ADD", AddrMode.Register, [Add16("IX","BC")])`,
  `Insn(0xDD, 0x21, "LD", AddrMode.ImmediateExtended, [Load16("IX")])`,
  `Insn(0xDD, 0x22, "LD", AddrMode.ExtendedAddress, [Store16("IX")])`,
  `Insn(0xDD, 0x2A, "LD", AddrMode.ExtendedAddress, [LoadMem16("IX")])`,
  `Insn(0xDD, 0x23, "INC", AddrMode.Register, [Inc16("IX")])`,
  `Insn(0xDD, 0xE5, "PUSH", AddrMode.Register, [Push16("IX")])`,
  `Insn(0xDD, 0xE3, "EX", AddrMode.RegisterIndirect, [ExSpIx()])` — **read the existing factories at Task 0**:
  `ExSpHl`/`JumpIndirect` may need an IX variant OR the existing factory may take a pair name. CONFIRM:
  `ExSpHl` is hardcoded to HL in `EmitZ80ExchangeBody` (`:1873`), and `JumpIndirect` (JP (HL)) is hardcoded
  to HL in `EmitZ80FlowBody`. **So EX (SP),IX and JP (IX) need the exchange/flow arms generalized to take
  the pair name** (a small change: parameterize `ExSpHl`/`JumpIndirect` on the pair, defaulting HL). This
  is the ONE place the "reuse base arms unchanged" claim needs a tweak — flag it:

  > **RECON-FINDING G7 (resolve at Task 5 Step 1).** `EmitZ80ExchangeBody`'s `ExSpHl` and `EmitZ80FlowBody`'s
  > `JumpIndirect` hardcode `HL`. For `EX (SP),IX` and `JP (IX)` (+ the IY forms) the arm must use IX/IY.
  > Two options: (a) the derivation produces a NEW op (`ExSpIndexed`/`JpIndexed`) handled by a tiny new arm;
  > (b) parameterize the existing factories/arms on the pair name (`ExSp("IX")`, `JumpIndirect("IX")`),
  > defaulting `HL` so the base rows are byte-identical. **Decision (recorded): (b) — parameterize**, because
  > it keeps ONE arm and the base-plane output byte-identical (the default-HL path). Confirm `ExSpHl`/
  > `JumpIndirect` factories' current arity at Task 0; add an optional pair arg. If (b) churns the base spec,
  > fall back to (a). The synthetic test here drives whichever shape is chosen.

  Assert (from the vectors, Task 0 Step 5): ADD IX,BC → IX = IX+BC, **WZ = pre-op IX + 1**; LD IX,nn → IX =
  nn, no WZ; LD (nn),IX → mem = IX, WZ = nn+1; INC IX → IX+1, no flags; PUSH IX → SP-2, mem = IX; EX (SP),IX
  → IX↔(SP), **WZ = new IX**; JP (IX) → PC = IX, no WZ; LD SP,IX → SP = IX.

- [ ] **Step 2: Write `Z80IxHalfOpTests.cs`.** A synthetic DD spec exposing the undoc-half ops as their
  DERIVED base ops with IXh/IXl substituted — e.g.
  `Insn(0xDD, 0x24, "INC", AddrMode.Register, [IncReg("IXh")])`,
  `Insn(0xDD, 0x26, "LD", AddrMode.Immediate, [Load("IXh")])` (LD IXh,n),
  `Insn(0xDD, 0x7C, "LD", AddrMode.Register, [Transfer("IXh","A")])` (LD A,IXh — Transfer(src,dst)),
  `Insn(0xDD, 0x84, "ADD", AddrMode.Register, [Add8()])` with the source = IXh (read how `Add8` resolves its
  source register from the opcode — the base ALU resolves the source from the `z` field; for `DD 84` z=4 → H
  → IXh after substitution; CONFIRM the base Add8 source-resolution path handles an IXh-named source), and
  the INERT `Insn(0xDD, 0x04, "INC", AddrMode.Register, [IncReg("B")])` (INC B — base op, IX untouched).
  Assert: INC IXh → IX hi-byte +1, full flags; LD IXh,n → IX hi = n; LD A,IXh → A = IX hi; ADD A,IXh → A +=
  IX hi, flags; INC B (inert) → B+1, IX untouched, WZ unchanged.

  > **The ALU-half source resolution is the subtle one.** `DD 84` (ADD A,IXh) — the base `Add8` reads its
  > source from the `z=4` register slot (H). After the derivation substitutes H→IXh, the op-text must name
  > IXh as the source. CONFIRM how `Add8`/the base ALU arm resolves + names its source operand: if it
  > hardcodes the source from the opcode's z-field at EMIT time (reading `H`), the substitution must happen
  > in the op-text the derivation produces (e.g. the base ALU op carries the source name explicitly). If the
  > base ALU op does NOT carry the source name (it re-derives from the opcode in the emit arm), the
  > derivation cannot rewrite it textually — in that case the half-ALU forms need the source named on the op.
  > **Resolve at Task 0 by reading how `Add8`/`EmitZ80AluBody` names its 8-bit register source** (does the
  > op record carry it, or does the arm read the opcode?). The literal test above assumes `Add8()` re-derives
  > the source from the opcode; if so, the derivation must instead emit an op that carries IXh as the source
  > (e.g. a base ALU op variant). Pin this — it determines whether the undoc-half ALU forms reuse the base
  > arm or need the source carried. This is flagged as the residual risk requiring Task 0 confirmation.

- [ ] **Step 3: Run the tests.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80Ix16Tests|FullyQualifiedName~Z80IxHalfOpTests"`
  Expected: PASS once the EX/JP generalization (G7) + the ALU-half source (Step 2 note) are resolved. A
  FAILURE here is informative — debug in the synthetic before the real regen (Task 7).

- [ ] **Step 4: Full gate + commit.**
  Run: `dotnet test`; `dotnet build --no-incremental -warnaserror`; `RegeneratedSpecTests` → all green.

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/Z80Ix16Tests.cs \
        tests/CpuEmulator.Tests/Generators/Z80IxHalfOpTests.cs
git commit -m "$(cat <<'EOF'
test(z80): prove IX/IY 16-bit + undoc IXh/IXl ops reuse the base emit arms; generalize EX (SP),pair + JP (pair) [G7]

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~10.

---

### Task 6: The `Z80DdFdSemantics` derivation test + the F1 dataset cross-check (TDD)

> Prove `Z80DdFdSemantics.OpsFor` produces the right op-text for representative opcodes of EVERY family (the
> derivation truth table), and that the dataset (after Task 7 adds the 213+213 rows) has 252 DD + 252 FD core
> rows. This is the F1 gap guard (the M3.4c lesson). The OpsFor theory runs NOW (the generator exists from
> Task 2); the row-count assertion drives Task 7.

**Files:**
- Test: `tests/CpuEmulator.Tests/Importer/Z80DdFdSemanticsTests.cs` (create)

- [ ] **Step 1: Write the derivation truth-table test.** Mirror `Z80EdSemanticsTests`/`Z80CbSemanticsTests`
  (read one at Task 0 for the `OpcodeDataset.Load` harness). Assert `OpsFor` (with the resolved signature —
  likely `OpsFor(op, mnemonic, mode, isIy)`) for representative opcodes:

```csharp
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80DdFdSemanticsTests
{
    [Theory]
    // Indexed memory forms (plane-agnostic op text).
    [InlineData(0x7E, "LD",  "Indexed", false, "[DdFdLdIndexed(\"LOAD\",\"A\")]")]
    [InlineData(0x70, "LD",  "Indexed", false, "[DdFdLdIndexed(\"STORE\",\"B\")]")]
    [InlineData(0x36, "LD",  "Indexed", false, "[DdFdStoreImmIndexed()]")]
    [InlineData(0x86, "ADD", "Indexed", false, "[DdFdAluIndexed(\"ADD\")]")]
    [InlineData(0x34, "INC", "Indexed", false, "[DdFdIncDecIndexed(false)]")]
    [InlineData(0x35, "DEC", "Indexed", false, "[DdFdIncDecIndexed(true)]")]
    // IX 16-bit (DD) / IY (FD).
    [InlineData(0x09, "ADD", "Register", false, "[Add16(\"IX\",\"BC\")]")]
    [InlineData(0x21, "LD",  "ImmediateExtended", false, "[Load16(\"IX\")]")]
    [InlineData(0x23, "INC", "Register", false, "[Inc16(\"IX\")]")]
    [InlineData(0x21, "LD",  "ImmediateExtended", true,  "[Load16(\"IY\")]")]
    // Undoc half (DD -> IXh/IXl).
    [InlineData(0x24, "INC", "Register", false, "[IncReg(\"IXh\")]")]
    [InlineData(0x2C, "INC", "Register", false, "[IncReg(\"IXl\")]")]
    [InlineData(0x2C, "INC", "Register", true,  "[IncReg(\"IYl\")]")]
    // Inert prefix (DD on an op naming none of H/L/(HL)).
    [InlineData(0x04, "INC", "Register", false, "[IncReg(\"B\")]")]
    [InlineData(0x00, "NOP", "Implied",  false, "[]")]
    public void Derivation_produces_expected_ops(int op, string mn, string mode, bool isIy, string expected)
        => Assert.Equal(expected, Z80DdFdSemantics.OpsFor(op, mn, mode, isIy));

    [Fact]
    public void Dataset_has_all_252_DD_and_252_FD_core_rows()
    {
        var dataset = OpcodeDataset.Load(/* the importer-test data-path helper — see note */);
        int dd = dataset.Count(r => r.Prefix == "0xDD");
        int fd = dataset.Count(r => r.Prefix == "0xFD");
        Assert.Equal(252, dd);
        Assert.Equal(252, fd);
    }
}
```

  > Adjust the expected op-text to whatever `Z80BaseSemantics` actually produces for each base opcode (read
  > it — e.g. `Inc16`/`IncReg`/`Add16`/`Load16` names). The `LD A,IXh` (0x7C) expected text depends on the
  > base `Transfer(src,dst)` ordering — confirm against `Z80BaseSemantics.LdOps`. The `Dataset_has_252` test
  > FAILS until Task 7 adds the rows (it drives Task 7).

- [ ] **Step 2: Run.** The OpsFor theory PASSES (the generator exists); `Dataset_has_252` FAILS (39 rows).
  Run: `dotnet test --filter "FullyQualifiedName~Z80DdFdSemanticsTests"` → the count assertion fails.

- [ ] **Step 3: Commit (the test; the rows land in Task 7).**

```bash
git add tests/CpuEmulator.Tests/Importer/Z80DdFdSemanticsTests.cs
git commit -m "$(cat <<'EOF'
test(z80): Z80DdFdSemantics derivation truth table + the 252 DD/FD core-row F1 cross-check (drives Task 7)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

  > A failing committed test is acceptable HERE only because Task 7 immediately follows in the same PR and
  > makes it green (the CB/ED/block plans use the same drive-the-next-task pattern). If the project's CI
  > gates on every commit, fold Task 6 into Task 7's commit instead.

**New-test estimate:** ~16 (the theory rows + the count).

---

### Task 7: Derive + add the 213+213 rows; declare DD/FD prefixes; route; regen `Z80Spec.cs` (gate)

> Add the 213 DD + 213 FD derived rows to the dataset, declare `0xDD`/`0xFD` in `decode.prefixes`, route
> `Prefix == "0xDD"`/`"0xFD"` through `Z80DdFdSemantics`, and regenerate `Z80Spec.cs` so the 252+252 core
> rows go live ATOMICALLY (G2: the prefix declaration + the backing rows must land together).

**Files:**
- Modify: `tools/CpuEmulator.SpecImporter/data/z80-opcodes.json` (the 213+213 rows)
- Modify: `tools/CpuEmulator.SpecImporter/data/z80-semantics.json` (`decode.prefixes` += DD/FD)
- Modify: `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs` (route DD/FD)
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Spec.cs` (regenerated)

- [ ] **Step 1: Generate the 213+213 derived rows.** The rows are mechanical (opcode + mnemonic + mode +
  bytes + cycles). **Generate them with a throwaway script** (not by hand — 426 rows) keyed off the base
  table + the D3 rule, OR add them in octal-ordered blocks. For each of the 252 DD opcodes NOT already in the
  dataset (the 213), emit a row `{ "prefix": "0xDD", "opcode": "0xNN", "mnemonic": "<base-or-derived>",
  "mode": "<mode>", "bytes": <2|3|4>, "cycles": <T>, "pageCrossPenalty": false, "source": "Zilog Z80 IX
  plane (M3.4e-2, derived)" }`. The mnemonic + mode follow the base op (the inert/half/16-bit forms carry the
  base mnemonic+mode; the `(IX+d)` forms carry the base mnemonic + mode `Indexed`). The `bytes` field: 2 for
  Register/Implied inert+half forms, 3 for Indexed LD/ALU/INC-DEC, 4 for `LD (IX+d),n` (0x36) + the
  ImmediateExtended/ExtendedAddress 16-bit forms (LD IX,nn = 4; LD (nn),IX = 4). The `cycles` field is NOT
  load-bearing (the emitter computes T-states); document accurately. Mirror for FD with IY source text.

  > **The script approach (recommended).** Write a small one-off (PowerShell or C#) that reads the 252 DD
  > vector filenames + the existing 39 rows + the base table, and emits the 213 missing rows with the right
  > mnemonic/mode (the base op's, with mode `Indexed` for the `(HL)`-member opcodes). Run it, inspect the
  > diff, commit the JSON. Keep the script out of the repo (or under `tools/scripts/` if the project keeps
  > such helpers — confirm convention). The `Z80DdFdSemantics` derivation is the RUNTIME generator; this
  > script is a one-time dataset populator. **Cross-check: the script must produce exactly 213 new DD +
  > 213 new FD rows, and the post-add dataset must have 252 + 252** (Task 6's `Dataset_has_252` gate).

- [ ] **Step 2: Declare DD/FD in `decode.prefixes`.** In `z80-semantics.json`, add `0xDD`/`0xFD` to the
  `decode.prefixes` list alongside `0xCB`/`0xED`. **Read the exact JSON shape at Task 0** (e-1b deferred
  this; confirm whether `decode.prefixes` carries a bare byte or the richer `{byte, compoundWith,
  displacementBeforeOpcode}` shape — the DD/FD CORE forms are PLAIN prefixes, so they need NO `compoundWith`/
  `displacementBeforeOpcode` for the core; the COMPOUND `DD CB` is e-3, but e-1b's `PrefixByte` supports the
  fields with defaults). Declare DD/FD as plain prefixes (no compound flags) for e-2; e-3 will ADD the
  `compoundWith: 0xCB, displacementBeforeOpcode: true` when the DDCB rows go live. **Confirm declaring DD/FD
  plain (no compound) does not break the e-1b compound decoder** (it only fires when `CompoundWith` is set;
  a plain DD takes the plain-prefix arm — exactly the DD/FD-core path e-2 needs).

  > **G2 — the atomic landing.** Declaring DD/FD in `decode.prefixes` WITHOUT the backing rows trips
  > CPUGEN012. So Steps 1–3 (rows + declaration + routing) must be in this ONE regen. The synthetic tests
  > (Tasks 1–5) already proved the bodies; this step makes the real rows live.

- [ ] **Step 3: Route `Prefix == "0xDD"`/`"0xFD"` through `Z80DdFdSemantics`.** In
  `SpecFileEmitter.cs:169-176`, extend the `z80Ops` ternary:

```csharp
            string? z80Ops =
                isZ80 && entry.Prefix is null
                    ? Z80BaseSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16), entry.Mnemonic, entry.Mode)
                : isZ80 && entry.Prefix == "0xCB"
                    ? Z80CbSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16))
                : isZ80 && entry.Prefix == "0xED"
                    ? Z80EdSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16))
                : isZ80 && entry.Prefix == "0xDD"
                    ? Z80DdFdSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16), entry.Mnemonic, entry.Mode, isIy: false)
                : isZ80 && entry.Prefix == "0xFD"
                    ? Z80DdFdSemantics.OpsFor(System.Convert.ToInt32(entry.Opcode, 16), entry.Mnemonic, entry.Mode, isIy: true)
                : null;
```

  > The DDCB/FDCB rows (prefix `"0xDDCB"`/`"0xFDCB"`) are NOT matched by these arms (they keep routing to
  > `null` → `// TODO(mode)`, out of scope for e-2). Confirm the prefix strings are exact (`"0xDD"` ≠
  > `"0xDDCB"`).

- [ ] **Step 4: Regenerate `Z80Spec.cs`.**
  Run:
```bash
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset tools/CpuEmulator.SpecImporter/data/z80-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/z80-semantics.json \
  --out src/CpuEmulator.Cpus.Z80/Z80Spec.cs
```
  Expected diff: the 252 DD + 252 FD core rows now emit as `Insn(0xDD, 0xNN, …)` / `Insn(0xFD, …)` rows
  (the `// TODO(mode)` Indexed comments + the `// TODO(semantics)` inert/half/16-bit comments become real
  rows); `decode.prefixes` gains `0xDD`/`0xFD`; the DDCB/FDCB rows + the redundant-prefix chains stay
  `// TODO`. Review the diff: ONLY DD/FD core rows + the prefix declaration changed; the base/CB/ED rows are
  byte-identical; the 6502 is untouched.

- [ ] **Step 5: Confirm `Z80Cpu` compiles + the importer test passes.**
  Run: `dotnet build --no-incremental -warnaserror` → clean. (If a missing-member error appears — e.g. an
  indexed body names a register the spec lacks — the synthetic tests already proved the bodies compile
  against a spec declaring IX/IXh/IXl, so the real spec should match; fix any gap.)
  Run: `dotnet test --filter "FullyQualifiedName~Z80DdFdSemanticsTests"` → PASS (252 DD + 252 FD).
  Run: `dotnet test` → all green (the synthetic + unit tests; the TomHarte DD/FD gate is Task 8).
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical).

- [ ] **Step 6: Commit (include the regenerated spec for review).**

```bash
git add tools/CpuEmulator.SpecImporter/data/z80-opcodes.json \
        tools/CpuEmulator.SpecImporter/data/z80-semantics.json \
        tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs \
        src/CpuEmulator.Cpus.Z80/Z80Spec.cs
git commit -m "$(cat <<'EOF'
feat(z80): DD/FD core live — derive 213+213 rows, declare DD/FD prefixes, route Z80DdFdSemantics (regen Z80Spec.cs)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~0 (Task 6's tests go green here).

---

### Task 8: The TomHarte DD + FD gate — green + universal regression + closeout (TDD + exit criterion)

> Wire the DD + FD core theories (load `dd {op:x2}.json` / `fd {op:x2}.json`), drive the full sweep to green
> over the 252+252 core opcodes (incl. i/r/im/iff1/iff2/wz/q + IX/IY + the per-T-state trace), confirm the
> base/CB/ED planes stay green at the universal Q/WZ/IM bar, confirm the 6502 un-regressed, and fill the
> closeout.

**Files:**
- Modify: `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs` (the DD + FD theories)
- Iterative fixes to the Task 2–7 emit arms / dataset as vectors surface divergences.
- Modify: `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e2-ddfd-core.md` (the closeout — this file)
- Modify: `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md` (the M3.4e-2 pointer)

- [ ] **Step 1: Add the DD + FD core theories.** In `Z80TomHarteTests.cs`, add two theories mirroring
  `Ed_opcode_matches_TomHarte_vectors`, probing the DD/FD key `0xDD00 | op` / `0xFD00 | op` over 0x00–0xFF
  EXCLUDING the prefix bytes (cb/dd/ed/fd) AND the DDCB/FDCB compound (those are e-3 — but they are not in
  the core probe range since the probe is `0xDD00|op` which is the core key; the DDCB key is the 24-bit
  compound, never matched here). The filename is `dd {op:x2}.json` (3 tokens — NOT `dd cb __`):

```csharp
    /// <summary>The covered DD-core opcodes (0x00–0xFF minus the prefix bytes cb/dd/ed/fd) — present in the
    /// generated dispatch (Disassemble != "???"). Probed via the prefixed key (0xDD00 | op). The DDCB
    /// compound forms (0xDDCB__) are M3.4e-3, NOT in this core key range.</summary>
    public static TheoryData<byte> CoveredDdPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0x00; op <= 0xFF; op++)
        {
            if (op is 0xCB or 0xDD or 0xED or 0xFD) continue;   // prefix bytes — no standalone core vector
            if (Z80Cpu.Disassemble((uint)(0xDD00 | op), 0, 0) != "???")
                data.Add((byte)op);
        }
        return data;
    }

    [Z80TomHarteTheory]
    [MemberData(nameof(CoveredDdPlaneOpcodes))]
    public void Dd_opcode_matches_TomHarte_vectors(byte opcode)
    {
        string dir = Z80TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, $"dd {opcode:x2}.json");   // 3 tokens — NOT "dd cb __"
        Assert.True(File.Exists(path), $"vector file missing: {path}");
        var cases = Z80TomHarteLoader.LoadFile(path);
        // … the same sampling/registers-only/UAT-full scaffolding as Ed_opcode_matches_TomHarte_vectors …
        // RunCase(testCase, registersOnly) — the universal Q/WZ/IM + IX/IY check.
    }
```
  Add the symmetric `CoveredFdPlaneOpcodes` + `Fd_opcode_matches_TomHarte_vectors` (key `0xFD00|op`, file
  `fd {op:x2}.json`). **Read the existing `Ed_opcode_matches_TomHarte_vectors` for the exact
  sampling/skip/failure-collection scaffolding and copy it verbatim** (sample size, `CPUEMULATOR_UAT`,
  `CPUEMULATOR_Z80_REGS_ONLY`, the 3-failure cap). Confirm the probe finds 252 each (probe == emitted ==
  252); if fewer, a derived row failed to emit (check the Task 7 regen diff / the `Z80DdFdSemantics` null
  return for that opcode).

- [ ] **Step 2: Fetch the vectors (if absent) + run the STAGED gate.**
  Run: `pwsh tools/get-test-vectors-z80.ps1` (idempotent). Confirm `dd 7e.json` + `fd 7e.json` exist.
  Run the registers-first stage:
```bash
CPUEMULATOR_Z80_REGS_ONLY=1 dotnet test --filter "FullyQualifiedName~Z80TomHarteTests.Dd_opcode|FullyQualifiedName~Z80TomHarteTests.Fd_opcode"
```
  Expected: green (registers incl. IX/IY/F's X/Y, I/R, IM, IFF, WZ, Q, RAM + cycle COUNT, NO bus-trace).
  Then the FULL trace:
```bash
dotnet test --filter "FullyQualifiedName~Z80TomHarteTests.Dd_opcode|FullyQualifiedName~Z80TomHarteTests.Fd_opcode"
```
  Expected: green (sample/opcode, full per-T-state bus trace).

- [ ] **Step 3: Iterate to green over any divergences.** For each failing DD/FD opcode apply
  `superpowers:systematic-debugging`. The likely surprises (flagged, vector is the oracle):
  - **R bump (G3)** — every DD/FD op must finalize R = init + 2 (not +3 for the 3-byte Indexed forms). If
    `r` mismatches by 1, the `OnInstructionFetched` R-bump (Task 4 Step 4) is wrong — fix the M1-count model.
  - **WZ = EA** for the `(IX+d)` forms (`dd 7e`); WZ = pre-op IX+1 for ADD IX,rp (`dd 09`); WZ = nn+1 for
    LD (nn),IX / LD IX,(nn) (`dd 22`/`dd 2a`); WZ = new IX for EX (SP),IX (`dd e3`); no WZ for JP (IX) /
    LD IX,nn / INC IX / LD SP,IX.
  - **The undoc-half ALU source** (the Step-5/Task-0 note) — `dd 84` (ADD A,IXh) must read IXh as the source,
    not H. If the half-ALU forms read the wrong byte, the source-naming derivation needs fixing.
  - **`LD (IX+d),n` (0x36) length** (G5) — PC must advance 4; the body reads d THEN n. If PC ends at +3, the
    decode-walk length / the body's operand reads are off.
  - **EX (SP),IX / JP (IX) generalization** (G7) — if these read/write HL instead of IX, the
    exchange/flow-arm parameterization is wrong.
  - **The inert prefix** — `dd 04` (INC B) must leave IX untouched + WZ unchanged + R+2. A derived inert row
    that accidentally touched IX/WZ shows here.
  - **Missing derived rows** — a DD/FD opcode with `Disassemble == "???"` (probe < 252) means the derivation
    returned null or the dataset row is absent for that opcode.
  - **Cycle counts** — re-derive each `_cycles +=` (or `EmitInternal` balance) against the vector's cycles
    array length.

- [ ] **Step 4: The FULL UAT sweep — the DD/FD exit criterion.**
  Run:
```bash
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarteTests.Dd_opcode|FullyQualifiedName~Z80TomHarteTests.Fd_opcode"
```
  Expected: ALL 252 DD + 252 FD core opcodes × 1000 = **504,000 cases, 0 failures** (registers incl. IX/IY,
  F's X/Y, I/R, IM, IFF, WZ, Q, RAM, AND the per-T-state bus trace). Record the EXACT covered count (probe ==
  emitted == green; target 252 + 252 = 504).

- [ ] **Step 5: Confirm the UNIVERSAL regression bar (base/CB/ED re-validated through the DD/FD slice).**
  Run the WHOLE Z80 UAT (base + CB + ED + block + DD + FD):
```bash
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
```
  Expected: base + CB + ED-core + ED-block + DD-core + FD-core all **0 failures** with final Q/WZ/IM + IX/IY
  on EVERY case (the DD/FD emit arms must not have regressed a shared helper — e.g. the EX/JP
  parameterization G7, or the shared INC/DEC flag helper if factored in Task 4).
  Then the 6502 un-regression:
  Run: `dotnet test` → full suite green; record the count.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.
  Run the 6502 + Klaus sweep (read the M3.4d closeout for the exact invocation; the Z80 added NO 6502 path,
  so the 6502 interpreter + JIT TomHarte + Klaus functional suite are un-regressed).

- [ ] **Step 6: Fill the closeout + the doc pointers + commit.**
  - Fill the closeout table (below).
  - In `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md`, update the M3.4e-2 section: add a pointer
    "**EXPANDED (2026-06-14): full execution-ready plan at
    `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e2-ddfd-core.md`**" and add e-2 to its slice docs index.

```bash
git add tests/CpuEmulator.Tests/TomHarte/Z80TomHarteTests.cs \
        docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e2-ddfd-core.md \
        docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md
git commit -m "$(cat <<'EOF'
feat(z80): DD/FD core TomHarte-green — 504 opcodes (504k cases, 0 failures); universal Q/WZ/IM + IX/IY green

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 7: Push + open the PR.**
  Run: `git push -u origin feat/m3-z80-ixiy-e2-ddfd` (only after the user approves; merge via PR per CLAUDE.md).
  Open a PR targeting `main`. The PR body claims EXACTLY: the 252 DD-core + 252 FD-core opcodes are
  TomHarte-green — the `(IX+d)`/`(IY+d)` indexed memory ops, the IX/IY 16-bit ops, the undoc IXh/IXl/IYh/IYl
  8-bit ops, and the inert DD/FD prefix on every other opcode — per-T-state with final Q/WZ/IM + IX/IY; the
  213+213 rows were DERIVED, not hand-authored; the whole Z80 (base + CB + ED + block) re-validated at the
  universal bar; 6502 byte-identical. Name what is STILL deferred: DDCB/FDCB compound bit/rotate/shift on
  `(IX+d)` = M3.4e-3 (no `dd cb __ *.json` vector is green here); the redundant-prefix chains
  (`DD DD`/`DD FD`/`DD ED`) = NOT modeled (D5; no vectors; unverified-pending); JIT-IL for the indexed ops =
  M3.5 (D4; emitted as fallbacks); interrupt servicing + ZEXALL = M3.5. NEVER overstate. Include a **Docs
  Impact** section linking the overview + the scoped plan + the e-1a/e-1b plans + the e-3 plan (when it
  exists).

**New-test estimate:** ~2 theory members (DD + FD); the bulk is iterative green-driving.

---

## Plan self-review (completed at write time)

- **Scope coverage (the 7 IN-scope items):**
  - **(1) `(IX+d)`/`(IY+d)` memory ops** — Task 2 (LD), Task 3 (ALU), Task 4 (INC/DEC). ✓
  - **(2) IX/IY 16-bit ops** — Task 5 (synthetic proof of base-arm reuse + the EX/JP G7 generalization) +
    the derivation (Task 2/6) + green (Task 8). ✓
  - **(3) undoc IXh/IXl 8-bit ops** — Task 5 (synthetic) + the derivation (Task 2/6) + green (Task 8). ✓
  - **(4) the inert DD/FD prefix** — the derivation routes to the base op (Task 2/6); proven (Task 5);
    green (Task 8). ✓
  - **(5) DD/FD declared as prefixes** — Task 7 (atomically with the rows, G2). ✓
  - **(6) the Indexed disassembler arm** — Task 1 Step 10 (G1). ✓
  - **(7) the TomHarte DD + FD gate** — Task 8. ✓
- **OUT-of-scope honored:** DDCB/FDCB stay `// TODO(mode)` (the routing matches `"0xDD"`/`"0xFD"`, not
  `"0xDDCB"`/`"0xFDCB"`; no `dd cb __ *.json` asserted green); the redundant-prefix chains NOT modeled (D5);
  JIT emits DD/FD as fallbacks only (D4); interrupt servicing not touched. ✓
- **Placeholder scan:** every code step shows literal code; the ONLY `…`/`NotImplementedException` is the
  `Z80DdFdSemantics` `MnemonicHint`/`ModeHint` SEAM (Task 2 Step 1), which the note RESOLVES (change the
  signature to take mnemonic+mode from the dataset — the load-bearing `IndexedFor`/`SubstituteHalfAndPair`
  logic is complete). The `EmitZ80IndexedAlu`/`IncDec` stubs (Task 2) are REPLACED by Tasks 3/4 (each names
  its replacement). No "TBD"/"similar to Task N". ✓
- **Type/name consistency:** the indexed op records (`DdFdLdIndexedOp`/`DdFdStoreImmIndexedOp`/
  `DdFdAluIndexedOp`/`DdFdIncDecIndexedOp`) are named identically in Op.cs (T1), Spec.cs (T1 factories),
  `s_microOpSignatures` (T1), `FactoryArity` (T1), `Z80DdFdSemantics` (T2), `Z80WritesFlags` (T2/3/4),
  `Z80Cycles` (T1 placeholder → T2/3/4), `JitOpLiteral` (T1), the emit arm (`EmitZ80IndexedBody` +
  `EmitZ80IndexedAlu`/`EmitZ80IndexedIncDec`), and the synthetic tests. The `Z80Indexed` class is added to:
  the enum (T1), `ClassifyOps` (T1), `ValidateModeForClass` (T1), the status-touch predicate (T1), `isZ80`
  (T1), `Z80Cycles` (T1), the 3 JIT predicates (T1). The `EmitZ80IndexedEa` helper (e-1a) is wired once
  (T2). ✓
- **Code/vector contradictions surfaced (the code/vectors win):** (G1) the disassembler had no `Indexed`
  arm — added T1; (G2) declaring DD/FD requires backing rows atomically — T7; (G3) R bumps by 2 not
  `__r.Length` — T4; (G4) the displacement is read before the EA/memory — T2; (G5) `LD (IX+d),n` is 4 bytes
  vs `ModeLength`=3 — resolved T0/T2; (G6) the inert/half/16-bit ops reuse base arms via derivation — T5;
  (G7) EX (SP),IX / JP (IX) need the HL-hardcoded arms generalized — T5. ✓
- **Build-green-after-every-task:** the synthetic/derivation tests (T1–T6) decouple from the real regen,
  which lands once (T7); T7's regen + T8's sweep are the only TomHarte-affecting tasks; the suite builds
  green after every task EXCEPT the deliberately-failing Task 6 count assertion (which T7 greens in the same
  PR — the CB/ED/block pattern; fold T6 into T7 if CI gates per-commit). ✓
- **The biggest residual risks flagged for the Builder/Coordinator** (see the report): (a) the undoc-half
  ALU source-naming (does the base ALU op carry its source register name, or re-derive it? — determines
  whether `DD 84` ADD A,IXh reuses the base arm via textual substitution or needs the source carried —
  Task 0 + Task 5 Step 2 resolve it); (b) the G5 `LD (IX+d),n` length vs `ModeLength`; (c) the G3 R-bump
  model. All three are vector-gated at Task 8 and pinned at Task 0; none blocks the plan, but (a) is the one
  most likely to need an emit-arm shape change. ✓

## Closeout (COMPLETE — 2026-06-14)

| Commit | Content | Suite |
|---|---|---|
| `37de696` (Task 1) | DD/FD indexed vocabulary + Z80Indexed class + Indexed disassembler arm | green (2324) |
| `1ff3f70` (Task 2) | LD r,(IX+d)/(IX+d),r/(IX+d),n + EmitZ80IndexedEa wiring + Z80DdFdSemantics | green |
| `e760258` (Task 3) | ALU A,(IX+d) | green |
| `c796515` (Task 4) | INC/DEC (IX+d) + the R-bump confirm (G3) | green |
| `54b6a6a` (Task 5) | IX/IY 16-bit + undoc IXh/IXl base-arm reuse + ADD/EX (SP)/JP pair-parameterization (G7) | green (2345) |
| `72b3a69` (Task 6) | derivation truth table (the F1 count cross-check folded into Task 7) | green |
| `2fbbfc5` (Task 7) | derive 213+213 rows + route Z80DdFdSemantics + regen Z80Spec.cs (252+252 live) | green |
| (Task 8) | DD/FD core TomHarte-green + the +3 prefix-cycle surcharge + the prefix-Q-reset + universal regression + closeout | green |

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | 2321 (e-1b close-state, confirmed) |
| Final test count | 2886 (0 failures, 0 skips) — +565 over the 2321 baseline (the synthetic emit/derivation tests + the 504 DD/FD TomHarte theory members) |
| DD-core opcodes made live | 252 (probe == emitted == covered) |
| FD-core opcodes made live | 252 (probe == emitted == covered) |
| DD/FD-core TomHarte (full UAT) | **504 opcodes × 1000 = 504,000 cases, 0 failures** — registers incl. IX/IY, F's X/Y, I/R, IM, IFF, WZ, Q, RAM, the per-T-state bus trace (`CPUEMULATOR_UAT=full`; confirmed "ran 1000" per opcode) |
| Rows derived (D3) | 213 DD + 213 FD = 426 (total DD/FD core 252 + 252; the dataset's 39+39 documented rows kept) |
| `(IX+d)` WZ = EA modeled? | YES (confirmed dd 7e: IX=0x2936 d=0x29 WZ=0x295F) |
| IX/IY 16-bit WZ rules? | ADD IX,rp = pre-op IX+1; LD (nn),IX / LD IX,(nn) = nn+1; EX (SP),IX = new IX; JP (IX) / LD IX,nn / INC/DEC IX / LD SP,IX = no WZ — all vector-confirmed |
| Undoc IXh/IXl ops? | YES (D7 — INC/DEC/LD/ALU on IXh/IXl; the half-ALU source via prefix-aware SourceRegFromOpcode) |
| Inert DD/FD prefix? | YES (D8 — base op + the +4 prefix M1; R+2; SCF/CCF see Q=0 from the prefix M1) |
| Indexed disassembler arm? | YES (added, G1) |
| R bumps by 2 on DD/FD? | YES — via KeyShape.PrefixedOpcode (the decode walk consumes prefix+opcode = 2 units → OnInstructionFetched(2)); NO Z80Cpu.cs change needed |
| Base + CB + ED + block re-validated? | YES — full Z80 UAT (base+CB+ED+block+DD+FD) 0 failures at the universal Q/WZ/IM + IX/IY bar |
| 6502 un-regressed? | YES — RegeneratedSpecTests byte-identity green; no 6502 file touched |
| Any 6502 file changed? | NONE — purely additive |
| `-warnaserror` | clean |
| Still deferred | DDCB/FDCB compound (M3.4e-3 — 62 // TODO rows); the redundant-prefix chains (D5; unverified-pending); JIT-IL (M3.5, D4 — DD/FD emit as JIT fallbacks); interrupt servicing + ZEXALL (M3.5) |
| Recommended next chunk | M3.4e-3 — the DDCB/FDCB compound bit/rotate/shift on `(IX+d)` (+ the undoc store-copy forms) |

### Deviations from the plan's literal code (honest record)

- **G3 (R-bump) needed NO `Z80Cpu.cs` change.** The plan flagged a likely `OnInstructionFetched` fix. Recon
  proved the structured decode walk computes `__r.Length` by CONSUMPTION (`UnitsConsumed × UnitBytes`), and a
  DD/FD-core row is `KeyShape.PrefixedOpcode` → the walk consumes prefix+opcode = 2 units → R+2 regardless of
  the displacement/immediate operand bytes the body reads. The R model was already correct; the fix the plan
  reserved was unnecessary.
- **G5 (`LD (IX+d),n` length) resolved by the decode architecture, not a length override.** `ModeLength("Indexed")=3`
  is NOT used by the structured walk (it computes length by consumption = 2 key bytes); the body reads d THEN n
  as operand reads, advancing PC to +4. No special-casing of 0x36 was needed.
- **The undoc-half ALU source: handled by a prefix-aware `SourceRegFromOpcode`, NOT an op-text change.** The
  base `Add8()` carries no source name (the arm resolves it from `opcode & 7`), so textual substitution could
  not rewrite it. Instead `SourceRegFromOpcode` maps the H/L source slot (4/5) to IXh/IXl/IYh/IYl for a
  DD/FD-prefixed row (reading the prefix from `insn.OperationKey`). This keeps the derivation emitting the
  unchanged base ALU op-text and is the cleanest shape (no new op kind, no arity change).
- **G7 (EX (SP)/JP/ADD pair) parameterized on the pair, NOT a new op kind.** `EmitZ80Add16` now writes
  `Args[0]` (HL default; IX/IY for DD/FD); `EX (SP),pair` and `JP (pair)` read the pair from the OperationKey
  prefix (HL when unprefixed → base output byte-identical). The 0-arg `ExSpHl()`/`JumpIndirect()` factories
  were kept (no signature churn) — the arm reads the prefix.
- **Two vector-forced cycle/Q corrections in Task 8 (not in the plan's per-task literal code):**
  (a) **The +3 prefix-cycle surcharge.** The inert/half/16-bit ops REUSE the base emit arms, which balance
  against a 1-byte fetch and the BASE total; a DD/FD op pays an extra M1 (+4 T) while Step charges 2 key bytes
  (not 1), so a +3 internal-T surcharge is emitted for every prefixed non-indexed row (the Z80Indexed arms
  carry their own vector-pinned totals). Confirmed: DD 04 = 8 T, DD 09 = 15 T, DD EA = 14 T.
  (b) **The prefix Q-reset.** A DD/FD prefix is a non-flag-writing M1, so by the documented Q lifecycle it sets
  Q = 0 before the inner opcode. Only SCF/CCF read Q mid-body (the (Q^F)|A X/Y quirk); for a DD/FD-prefixed
  SCF/CCF the seeded q is ignored. Emitting `Q = 0;` at the start of every prefixed body fixed dd/fd 37/3f.
- **`decode.prefixes` is NOT hand-authored.** The plan's Task 7 Step 2 (declare DD/FD in `z80-semantics.json`'s
  decode block) is a no-op for this importer: the `Decode.Prefixes` list is AUTO-DERIVED from the emitted
  prefixed rows (`emittedPrefixBytes`). DD/FD self-declared once their derived rows emitted (CPUGEN012 satisfied).
- **The synthetic fixtures use `IAddressSpace _bus`** (M3.4d deviation #1; the ED/block precedent) and declare
  `public byte Q; public int Im;` + the IX/IXh/IXl half-views — as the plan anticipated.
- **Task 6's `Dataset_has_252` count assertion was folded into Task 7's commit** (it goes red until the rows
  land); Task 6 committed only the always-green derivation truth table, keeping every commit's gate clean.
- **Two stale importer assertions updated** (Z80SkeletonEndToEndTests, SpecFileEmitterTests): they asserted the
  DD/FD plane was deferred / the dataset was 728 rows / 588 emitted — now DD/FD core is LIVE (1154 rows, 1092
  emitted, 62 DDCB/FDCB TODO). Updated to the new state.

## Slice docs index

- **Overview / sequencing:** `docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md`
- **Scoped parent plan (the M3.4e outline):** `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md`
- **The framework this slice builds on:**
  `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1a-addrmode-ea.md` (the `Indexed` AddrMode + EA helper +
  half-views), `…-ixiy-e1b-compound-decoder.md` (the declarative compound decoder)
- **The next slice (DDCB/FDCB):** _(to be authored — M3.4e-3; the compound decoder from e-1b is its
  foundation)_
- **Depth templates + close-state records:** `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md`,
  `…-ed-block-ops.md`
- **Architecture (Decisions 1, 3, 4, 7):** `docs/architecture/0001-z80-second-architecture.md`
