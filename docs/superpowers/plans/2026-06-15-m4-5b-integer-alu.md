# M4.5b: The 68000 Integer-ALU Family Interpreter — the table-driven ALU layer (ADR 0007 option C)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking. This is the SECOND of the four M4.5 interpreter sub-PRs (a = MOVE ✅ shipped; **b = integer ALU
> (this plan)**; c = shift/rotate/bit/BCD/system-misc; d = exceptions/branches/IPL/prefetch-final-assertion).
> **M4.5a (PR #39, merge `1bcd202`) MUST be on `main`** — this plan REUSES its shipped substrate verbatim
> (`ReadEaOperand`/`WriteEaOperand`/`SetDataRegPartial`/`SizeMask`, the wide-bus helpers, the `M68000FetchStream`,
> the `M68000TomHarteRunner` Step+diff). The op-body STRUCTURE for this PR is **already decided** in
> **ADR 0007 → option (C)** (`docs/architecture/0007-68000-interpreter-op-body-structure.md`); this plan
> IMPLEMENTS ADR 0007 §5, it does NOT re-litigate the A/B/C decision.

**Goal:** make the 68000 EXECUTE the integer-ALU families — `ADD`/`SUB`/`AND`/`OR`/`EOR`/`CMP` (reg↔EA),
`ADDA`/`SUBA`/`CMPA` (address-reg), `ADDI`/`SUBI`/`ANDI`/`ORI`/`EORI`/`CMPI` (immediate), `ADDQ`/`SUBQ` (quick),
`NEG`/`NEGX`/`NOT`/`CLR`/`TST` (unary), `EXT`, `ADDX`/`SUBX` (with-X), `MULU`/`MULS`/`DIVU`/`DIVS` (mul/div) —
via a **table-driven ALU helper layer** (the `BinaryAluExecute` driver + `AluFn`/`CcrRule`/`AluShape` descriptors,
the regular families as one-line registrations, the irregular tail hand-written), driven **TomHarte-green on the
DATA axis** against the 51 in-scope ALU-family `.json.gz` files.

**Architecture (ADR 0007 §5, option C).** Keep the M4.5a `opIndex`-dispatch seam EXACTLY as shipped. Add a new
hand-written partial `M68000Cpu.Alu.cs` carrying one `BinaryAluExecute(aluFn, ccrRule, shape, writesResult, …)`
driver that does read-operand-A / read-operand-B / write-result / set-CCR ONCE, parameterized by a per-family
`(AluFn, CcrRule, AluShape)` descriptor. The ~22 regular families collapse to one-line `*Execute` partial-method
registrations (`partial void AddExecute(…) => BinaryAluExecute(Alu.Add, Ccr.Arith, AluShape.RegEa, true, …);`).
The CCR rules (`Ccr.Arith`/`Ccr.Logic`/`Ccr.Cmp`/`Ccr.ArithX`) are written + tested ONCE — directly attacking the
dominant ALU TomHarte-failure class. The irregular tail (`EXT`/`CLR`/`TST`/`ADDX`/`SUBX`/`NEGX`/`MULU`/`MULS`/
`DIVU`/`DIVS`) keeps bespoke hand-written bodies. The ONLY generator change is extending the name-driven
`EmitMoveDispatchArms` (`CpuEmitter.cs:4204`) with the ALU family names → their `*Execute` hooks, and emitting the
matching `partial void` declarations (`CpuEmitter.cs:307-318`). **The fetch stream, the cycle-charging bus
helpers, and the Step+diff runner are UNTOUCHED** (ADR 0007 §5.4 — the seam invariant). Every change is gated to
`model.FieldGrammar is not null` + the M68000 partial + the 680x0-only test infra; 6502/Z80 stay byte-identical.

**Tech Stack:** C# (.NET 10), the Roslyn incremental source generator (`CpuEmulator.Generators`), the M4.2 wide
big-endian `AddressSpace`, the M4.3b `ComputeEa`/`ExtensionWords` substrate, the M4.5a `M68000FetchStream` +
`M68000Cpu.Move` primitives + `M68000TomHarteRunner`, and xUnit. The ALU TomHarte sweep runs under
`dotnet test -c Release` with the 680x0 vectors fetched first via `pwsh tools/get-test-vectors-68000.ps1`.

---

## Decisions (RESOLVED by Coordinator, 2026-06-15)

> The one decision M4.5b could not settle from ADR 0007/0004 + the recon is RESOLVED below. This plan is an
> unambiguous spec — Builder implements to this resolution with NO open questions. The tasks reflect it.

**D1 (RESOLVED) — The immediate forms (ADDI/SUBI/ANDI/ORI/EORI/CMPI) and quick forms (ADDQ/SUBQ) ARE
IMPLEMENTED (Tasks 6 + 7), with hardening, because the SingleStepTests 68000 v1 set has NO vector files for
them.** Recon against the live `68000/v1` tree (124 files) confirmed NO `ADDI.*` / `SUBI.*` / `ANDI.json.gz` /
`ORI.json.gz` / `EORI.json.gz` / `CMPI.*` / `ADDQ.*` / `SUBQ.*` / `CMPM.*` files exist (the only `*I*` files —
`ANDItoCCR`/`ANDItoSR`/`ORItoCCR`/`ORItoSR`/`EORItoCCR`/`EORItoSR` — are the ANDI/ORI/EORI **to CCR/SR** system
forms, which are **M4.5c system-misc**, out of scope). So the immediate/quick OPCODES have no TomHarte file to
gate them green. They ride the SAME `BinaryAluExecute` driver (the `ImmEa`/`QuickEa` shapes) and the SAME
`Ccr.Arith`/`Ccr.Logic`/`Ccr.Cmp` rules that ARE gated green against silicon by the reg↔EA families' 8065-case
sweeps.

**Hardening (the load-bearing mitigation — this is what makes shipping them consistent with the anti-drift
directive; Tasks 6 & 7 implement it):**
1. **Differential-equivalence tests.** For a representative sample of `(a, b, size)`, assert each imm/quick form
   produces an IDENTICAL `(result, CCR)` to its vector-PROVEN reg↔EA counterpart — `ADDI≡ADD`, `SUBI≡SUB`,
   `ANDI≡AND`, `ORI≡OR`, `EORI≡EOR`, `CMPI≡CMP`, `ADDQ≡ADD`, `SUBQ≡SUB` — feeding the same operands into the same
   `BinaryAluExecute` driver. Because the reg↔EA forms are TomHarte-green against silicon, this transitively
   inherits the silicon proof for the imm/quick forms' ALU function + CCR rule (the high-bug-density core).
2. **Synthetic immediate/quick FETCH tests.** Assert the immediate operand is read correctly from the extension
   word(s) per size (`.b`/`.w`/`.l`), and the quick 3-bit field (bits 11-9) maps correctly with the `0→8` special
   case. This covers the only surface the differential test does not (operand fetch, not driver routing) and is
   simple/low-risk.

**Honesty (non-negotiable):** ADDI/SUBI/ANDI/ORI/EORI/CMPI/ADDQ/SUBQ **EXECUTE but are NOT TomHarte-gated** (no
v1 vectors exist) — covered by differential-equivalence + synthetic fetch tests only. The plan's gate section,
the PR body, and the status/resume doc state this PLAINLY and never claim or imply these forms are vector-green.
The 51-file TomHarte sweep (the reg↔EA / unary / EXT / X-ops / mul-div families) remains the un-fakeable gate;
the imm/quick forms are an explicitly, honestly-disclosed exception.

**`CMPM` is DROPPED** from M4.5b — absent from BOTH the v1 vector set AND the FieldGrammar dataset (no
`"operation":"CMPM"` row), so it is not decodable without a dataset edit (out of scope).

---

## Recon (verified against the tree + the live vector cache — file:symbol citations)

> All facts below were confirmed read-only against `main` @ `437fa0f` and the fetched vector cache at
> `~/.cache/cpuemulator/vectors/680x0/v1`. Builder re-confirms at Task 0 (the regen guard pins the opIndices; a
> dataset edit would shift them — but the dispatch is name-driven, so it tracks automatically).

### R1 — The governing decision (ADR 0007, IMPLEMENT §5; do not re-decide)
- `docs/architecture/0007-68000-interpreter-op-body-structure.md` §3 = decision **(C)**, the table-driven ALU
  helper layer. §5.1 gives the `BinaryAluExecute` driver + `AluFn`/`CcrRule`/`AluShape` SIGNATURES (not bodies)
  + the one-line `*Execute` registration shape. §5.3 = the bespoke tail. §5.4 = the seam invariant. §6 = the
  data-axis-first gate + the timing/exception deferrals. §7 = the open questions this plan turns into tasks.

### R2 — The M4.5a seam this plan EXTENDS (do not re-plumb)
- **The generated FieldGrammar `Step` arm** — `src/CpuEmulator.Generators/CpuEmitter.cs:218-252`. It already
  computes everything the ALU bodies need and dispatches by opIndex:
  ```
  var __stream = new CpuEmulator.Core.Jit.M68000FetchStream(_bus, PC);
  var __r = Decode(__stream);
  uint __operword = __r.Operword;                       // operword read exactly once (DecodeResult.Operword)
  _cycles += __stream.UnitsConsumed * 4;                // fetch cycles (timing axis — already wired)
  _eaPcBase = PC + 2u;  PC += (uint)__r.Length;          // PC-relative base + advance
  uint __opIndex = (__r.OperationKey >> 8) & 0xFFFFu;
  uint __size    = __r.OperationKey & 0xFFu;             // 0=b,1=w,2=l
  uint __ea = __operword & 0x3Fu;
  uint __srcMode = (__ea >> 3) & 7u, __srcReg = __ea & 7u;
  switch (__opIndex) { EmitMoveDispatchArms(...); default: HandleUndefinedOpcode(...); }
  _eaPcBase = 0u;
  ```
  **M4.5b adds ALU arms to that `switch` via `EmitMoveDispatchArms` and adds nothing else to the Step.**
- **The name-driven dispatch-arm emitter** — `EmitMoveDispatchArms(StringBuilder, FieldGrammarModel)`,
  `CpuEmitter.cs:4204-4222`. An `op switch` on `grammar.Ops[i].Operation` → a hook call string, emitting
  `case {i}u: {hook}; break;`. **M4.5b adds the ALU operation names to this switch** (the opIndices are
  name-resolved, so they track the dataset automatically — ADR 0007 §4 "Bad/accepted costs").
- **The partial-hook declaration emit** — `CpuEmitter.cs:307-318` (inside `if (model.FieldGrammar is not null)`,
  emits `partial void MoveExecute(…)` … `partial void MoveUspExecute(…)`). **M4.5b adds the ALU `partial void
  *Execute(…)` declarations here.**
- **The fetch stream** — `src/CpuEmulator.Core/Jit/M68000FetchStream.cs` (UNTOUCHED — seam invariant §5.4).
- **The cycle-charging wide-bus helpers** — `src/CpuEmulator.Cpus.M68000/M68000Cpu.cs:77-104`:
  `WordAccessCycles = 4` (`:77`), `ReadWordBus`/`WriteWordBus` (`:79-89`), `ReadLongBus`/`WriteLongBus`
  (`:93-104`, `.l` = two `.w`). (UNTOUCHED — seam invariant; the ALU bodies are new CALLERS of them.)
- **The Step+diff runner** — `tests/CpuEmulator.Tests/TomHarte/M68000TomHarteRunner.cs`. `IsExceptionCase`
  (`:44`, the vector-table-read-pair-equals-`final.pc` heuristic — REUSED verbatim for the DIVU/DIVS
  divide-by-zero deferral), `RunCase(case, timingAxis=false)` (`:70`, data axis = regs+SR+RAM; `timingAxis`
  flag default off — `:131`). (UNTOUCHED — seam invariant; M4.5b adds the ALU sweep theory that CALLS it.)

### R3 — The M4.5a substrate the ALU bodies REUSE verbatim (ADR 0007 §5.2; new callers, no changes)
In `src/CpuEmulator.Cpus.M68000/M68000Cpu.Move.cs` (all `private` on the `M68000Cpu` partial — a sibling
hand-written partial sees them):
- `ReadEaOperand(uint mode, uint reg, uint size, ExtensionWords ext)` (`:24`) — reads Dn (mode 0, masked) / An
  (mode 1, full 32) / `#imm` (mode 7 reg 4, from ext words) / memory (else, via `ComputeEa` + the wide bus, with
  `(An)+`/`-(An)` write-back). **This is operand-A AND the `ImmEa` source path for free.**
- `WriteEaOperand(uint mode, uint reg, uint size, uint value, ExtensionWords ext)` (`:52`) — Dn partial write /
  memory wide-bus write.
- `SetDataRegPartial(uint reg, uint value, uint size)` (`:41`), `DataReg(uint reg)` (`:40`),
  `SizeMask(uint size)` (`:19`), `ReadByteAt`/`WriteByteAt` (`:37-38`), `SetMoveCcr` (`:64` — the MOVE CCR; the
  ALU CCR rules are NEW, in `M68000Cpu.Alu.cs`).
- On `M68000Cpu.cs` / the generated partial: `Areg(reg)`/`SetAreg(reg,v)` (read/write An, A7-banked),
  `ComputeEa(eaMode, eaReg, size, ext, pureEa)`, the `Ccr` property (`:41-45`, `get/set` low byte of SR), the
  `SR` field (`:38`, `ushort`), `_cycles` (`:17`, `long`), `_eaPcBase` (set in the generated Step).

### R4 — The FieldGrammar dataset ALU rows (the dispatch is name-driven from these)
`tools/CpuEmulator.SpecImporter/data/m68000-fieldgrammar.json`. The importer passes the `"operation"` value
VERBATIM to `grammar.Ops[i].Operation` (`tools/CpuEmulator.SpecImporter/FieldGrammarDataset.cs:~106`, no
transformation) — so the dispatch `op switch` matches these exact uppercase strings:

| Operation string | dataset line | mask / match | sizeShift / sizeWidth | legalEa | M4.5b shape |
|---|---|---|---|---|---|
| `"ADD"` | 98 | 0xF000 / 0xD000 | 6 / 2 | DataAddressing | RegEa (dir bit 8) |
| `"SUB"` | 84 | 0xF000 / 0x9000 | 6 / 2 | DataAddressing | RegEa |
| `"AND"` | 94 | 0xF000 / 0xC000 | 6 / 2 | DataAddressing | RegEa |
| `"OR"` | 80 | 0xF000 / 0x8000 | 6 / 2 | DataAddressing | RegEa |
| `"EOR"` | 87 | 0xF100 / 0xB100 | 6 / 2 | DataAlterable | RegEa (EOR: Dn→EA only) |
| `"CMP"` | 88 | 0xF100 / 0xB000 | 6 / 2 | DataAddressing | RegEa (compare-only) |
| `"ADDA"` | 96 | 0xF0C0 / 0xD0C0 | 8 / 1 | All | RegEa An-dest (no CCR) |
| `"SUBA"` | 82 | 0xF0C0 / 0x90C0 | 8 / 1 | All | RegEa An-dest (no CCR) |
| `"CMPA"` | 86 | 0xF0C0 / 0xB0C0 | 8 / 1 | All | RegEa An-dest (CMPA SETS CCR) |
| `"ADDI"` | 21 | 0xFF00 / 0x0600 | 6 / 2 | DataAlterable | ImmEa |
| `"SUBI"` | 22 | 0xFF00 / 0x0400 | 6 / 2 | DataAlterable | ImmEa |
| `"ANDI"` | 23 | 0xFF00 / 0x0200 | 6 / 2 | DataAlterable | ImmEa |
| `"ORI"`  | 24 | 0xFF00 / 0x0000 | 6 / 2 | DataAlterable | ImmEa |
| `"EORI"` | 25 | 0xFF00 / 0x0A00 | 6 / 2 | DataAlterable | ImmEa |
| `"CMPI"` | 26 | 0xFF00 / 0x0C00 | 6 / 2 | DataAlterable | ImmEa (compare-only) |
| `"ADDQ"` | 70 | 0xF100 / 0x5000 | 6 / 2 | Alterable | QuickEa (An-dest = no CCR) |
| `"SUBQ"` | 71 | 0xF100 / 0x5100 | 6 / 2 | Alterable | QuickEa (An-dest = no CCR) |
| `"NEG"`  | 64 | 0xFF00 / 0x4400 | 6 / 2 | DataAlterable | UnaryEa |
| `"NEGX"` | 63 | 0xFF00 / 0x4000 | 6 / 2 | DataAlterable | UnaryEa (ArithX, bespoke) |
| `"NOT"`  | 65 | 0xFF00 / 0x4600 | 6 / 2 | DataAlterable | UnaryEa |
| `"CLR"`  | 62 | 0xFF00 / 0x4200 | 6 / 2 | DataAlterable | bespoke (Z=1; dummy read) |
| `"TST"`  | 66 | 0xFF00 / 0x4A00 | 6 / 2 | DataAlterable | UnaryEa (writesResult:false) |
| `"EXT"`  | 56 | 0xFFB8 / 0x4880 | 0 / 1 | All | bespoke (Dn sign-extend) |
| `"ADDX"` | 97 | 0xF130 / 0xD100 | 6 / 2 | All | bespoke (ArithX + -(An) pairing) |
| `"SUBX"` | 83 | 0xF130 / 0x9100 | 6 / 2 | All | bespoke (ArithX + -(An) pairing) |
| `"MULU"` | 90 | 0xF1C0 / 0xC0C0 | 0 / 1 | DataAddressing | bespoke (16×16→32) |
| `"MULS"` | 91 | 0xF1C0 / 0xC1C0 | 0 / 1 | DataAddressing | bespoke (16×16→32 signed) |
| `"DIVU"` | 77 | 0xF1C0 / 0x80C0 | 0 / 1 | DataAddressing | bespoke (32÷16; ÷0 → defer) |
| `"DIVS"` | 78 | 0xF1C0 / 0x81C0 | 0 / 1 | DataAddressing | bespoke (32÷16 signed; ÷0 → defer) |

> **`CMPM` is ABSENT from the dataset** (no `"operation":"CMPM"` row) — dropped from M4.5b (D1 RESOLVED).
> **opIndices** (from the generated `s_fieldOps` table; e.g. ADD≈76, SUB≈65, AND≈73, OR≈62, EOR≈67, CMP≈68,
> ADDA≈74, SUBA≈63, CMPA≈66, ADDI≈15…CMPI≈20, ADDQ≈55, SUBQ≈56, NEG≈50, NEGX≈49, NOT≈51, CLR≈48, TST≈52,
> EXT≈44, ADDX≈75, SUBX≈64, MULU≈69, MULS≈70, DIVU≈59, DIVS≈60) are CONFIRMATION-ONLY — the dispatch is
> name-driven, so the emitted `case {i}u:` labels track the live opIndices. Builder confirms at Task 0 and does
> NOT hand-encode them.

### R5 — The confirmed in-scope ALU vector files (51 files, 8065 cases each)
Confirmed present in `~/.cache/cpuemulator/vectors/680x0/v1` (each is mnemonic+size-keyed, gzipped, 8065 cases):

```
ADD.b  ADD.w  ADD.l        ADDA.w  ADDA.l       ADDX.b  ADDX.w  ADDX.l
SUB.b  SUB.w  SUB.l        SUBA.w  SUBA.l       SUBX.b  SUBX.w  SUBX.l
AND.b  AND.w  AND.l        OR.b    OR.w    OR.l
EOR.b  EOR.w  EOR.l        CMP.b   CMP.w   CMP.l        CMPA.w  CMPA.l
NEG.b  NEG.w  NEG.l        NEGX.b  NEGX.w  NEGX.l
NOT.b  NOT.w  NOT.l        CLR.b   CLR.w   CLR.l
TST.b  TST.w  TST.l        EXT.w   EXT.l
MULU   MULS   DIVU   DIVS
```
= **51 files** (the Task-14 sweep enumerates exactly these). **No standalone immediate/quick/CMPM files exist**
(D1 RESOLVED — imm/quick execute but are not vector-gated; CMPM dropped). An ALU case's schema matches MOVE's
exactly: the operword is in `initial.prefetch[0]` (e.g. `ADD.w`
case `5e4a` → `prefetch:[24138,…]`, `24138 = 0x5E4A` = the case name), `final.prefetch` shifts (timing axis —
defer), first transaction is the operword fetch. ADDX has real `-(An),-(An)` cases (e.g. `d909
[ADDX.b -(A1),-(A4)]`, operword `0xD909`, bit 3 set).

### R6 — Static dataset / encoding facts the bodies need
- **RegEa direction (bit 8 of the operword):** `0` = `<ea> op Dn → Dn` (EA is operand A/source, Dn is dest);
  `1` = `Dn op <ea> → <ea>` (Dn is operand A/source, EA is dest). The Dn register is bits 11-9. EOR has ONLY
  the `Dn → EA` form (bit-8 high required; bit-8-low is CMP's encoding space — disjoint mask).
- **CMP/CMPA/TST** never write a result (compare/test only) — `writesResult: false`.
- **ADDA/SUBA opmode (bits 8-6):** `011` = `.w`, `111` = `.l` (sizeShift 8, width 1 → MapSize standard:
  `__size` is 1 or 2). An-dest, NO CCR. The `.w` source SIGN-EXTENDS to 32 before the add/sub (like MOVEA).
- **ADDQ/SUBQ quick immediate:** bits 11-9, value `0` means `8` (so `imm3 = bits11-9; if imm3==0 imm3=8`). An
  dest → NO CCR and the operation is always on the FULL 32 bits regardless of size (a quick-to-An quirk).
- **Immediate forms (ADDI…CMPI):** a leading `#imm` extension word(s) BEFORE the EA: `.b`/`.w` = 1 imm word,
  `.l` = 2 imm words; then the EA's own extension words. `r.ExtensionWords[0..]` carries imm-first, then EA
  (the decode walk fetches them in stream order — Task 6 confirms/handles the length).
- **ADDX/SUBX:** bit 3 (R/M) selects shape: `0` = `Dx op Dy → Dy` (reg↔reg, Dx=bits 2-0, Dy=bits 11-9); `1` =
  `-(Ax) op -(Ay) → (Ay)` (predecrement memory, Ax=bits 2-0, Ay=bits 11-9). Uses X-flag in. **Z is STICKY**
  (cleared on a non-zero result, NEVER set — preserves the incoming Z when the result is zero). Same for NEGX.
- **EXT:** `EXT.w` (opmode 010, size index → .w) sign-extends Dn.b→.w; `EXT.l` (opmode 011) sign-extends
  Dn.w→.l. CCR: N/Z from the result, V=C=0, X untouched. No EA. Dn = bits 2-0.
- **CLR:** writes 0 to the EA; CCR ALWAYS `Z=1, N=V=C=0`, X untouched. The 68000 **READS the EA before writing
  0** (a vector-confirmed dummy read — data-axis-invisible, but model it for the M4.5d trace; ADR 0007 §7.2).
- **MULU/MULS:** `Dn × <ea>.w → Dn.l` (16×16→32). MULU unsigned, MULS signed. CCR: N/Z from the 32-bit result,
  V=C=0, X untouched. Dn = bits 11-9; the `.w` source via the EA.
- **DIVU/DIVS:** `Dn.l ÷ <ea>.w → Dn` (quotient in low 16, remainder in high 16). CCR: N/Z from the 16-bit
  quotient, C=0, V set on overflow (quotient > 16 bits), X untouched. **÷0 → divide-by-zero exception
  (vector 5) → M4.5d:** compute the ÷0 DETECTION in the body, but DEFER the vectoring (the `IsExceptionCase`
  heuristic catches the vector-table read pair). Dn = bits 11-9.

---

## Scope

**IN scope (these families execute; the ALU pipeline is proven TomHarte-green on the DATA axis):**
1. The `BinaryAluExecute` driver + `AluFn`/`CcrRule`/`AluShape` types + the `Alu` function table + the `Ccr` rule
   set (`Arith`/`Logic`/`Cmp`/`ArithX`) — written + tested ONCE (Tasks 1-2).
2. The regular RegEa families as one-line registrations: `ADD`/`SUB`/`AND`/`OR`/`EOR`/`CMP` (Task 3); the
   address-reg variants `ADDA`/`SUBA`/`CMPA` (Task 4); the unary core `NEG`/`NOT`/`TST` (Task 5).
3. The immediate forms `ADDI`/`SUBI`/`ANDI`/`ORI`/`EORI`/`CMPI` (Task 6); the quick forms `ADDQ`/`SUBQ`
   (Task 7). **These EXECUTE but are NOT TomHarte-gated — no v1 vector files exist (D1 RESOLVED); they are
   covered by differential-equivalence (imm/quick ≡ their vector-proven reg↔EA counterpart) + synthetic fetch
   tests only. The plan never claims they are vector-green.**
4. The bespoke tail: `EXT` (Task 8); `CLR` (Task 9, with the dummy read); `ADDX`/`SUBX`/`NEGX` (Task 10, the
   `Ccr.ArithX` sticky-Z + the `-(An)`/`-(An)` pairing); `MULU`/`MULS` (Task 11); `DIVU`/`DIVS` (Task 12, ÷0
   detect-and-defer).
5. The generator dispatch arms + partial-hook declarations for all of the above (Task 13).
6. The ALU-family TomHarte data-axis green sweep over the 51 files (Task 14).

**OUT of scope (later sub-PRs — do NOT reach for them):**
- **Shift/rotate (ASL/LSR/ROXL/…), bit ops (BTST/BCHG/BCLR/BSET), BCD (ABCD/SBCD/NBCD), Scc, and the system-misc
  ops** (incl. `ANDI/ORI/EORI to CCR/SR`, MOVEM, LEA/PEA, SWAP, EXG, LINK/UNLK, TRAP/TRAPV/CHK, NOP, RTS/JMP/JSR)
  = **M4.5c**. Every non-ALU opIndex stays `HandleUndefinedOpcode` and its `.json.gz` is outside the M4.5b sweep.
- **Exceptions/branches/IPL/prefetch-final-assertion** = **M4.5d**: the timing axis (`final.pc`, `final.prefetch`,
  per-transaction trace, cycle count); the **DIVU/DIVS divide-by-zero exception (vector 5)**; address-error /
  privilege cases. M4.5b detects-and-defers these (ADR 0007 §6), never asserts them.
- **`CMPM`** (absent from the dataset; dropped — D1 RESOLVED). **The 68000 through the JIT** = M4.6. **The (B) generated op-table
  promotion** = M4.5c/d (ADR 0007 §5.5 — watched via Open Question #1; NOT resolved here). **Descriptor
  generalization across shift/rotate/bit** = M4.5c (ADR 0007 §7.1 — empirical, do not pre-commit).

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs` | Create | The `BinaryAluExecute` driver, the `AluFn`/`CcrRule` delegates, the `AluShape` enum, the `Alu` static function table, the `Ccr` rule set (`Arith`/`Logic`/`Cmp`/`ArithX`), the one-line regular-family `*Execute` registrations, and the bespoke-tail bodies (`ExtExecute`/`ClrExecute`/`AddXExecute`/`SubXExecute`/`NegXExecute`/`MulUExecute`/`MulSExecute`/`DivUExecute`/`DivSExecute`). |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | Extend `EmitMoveDispatchArms` (`:4204`) with the ALU family names → `*Execute` hooks; add the ALU `partial void *Execute(…)` declarations to the FieldGrammar-gated emit (`:307-318`). NO other generator change. |
| `tests/CpuEmulator.Tests/Generators/M68000AluCcrTests.cs` | Create | Synthetic unit tests for `Alu.*` + `Ccr.*` rules (no CPU, no vectors) — the CCR truth tables (carry/overflow/sticky-Z). |
| `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs` | Create | Synthetic execute unit tests (no vectors): RegEa direction, partial-write, CCR, ADDA sign-extend, imm/quick, unary, EXT, CLR, ADDX/SUBX sticky-Z + `-(An)` pairing, MUL, DIV (incl. ÷0 detection). |
| `tests/CpuEmulator.Tests/TomHarte/M68000AluTomHarteTests.cs` | Create | The skip-when-absent `[M68000TomHarteTheory]` over the 51 ALU-family files (the data-axis green gate). |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502/Z80
> byte-identity guard `RegeneratedSpecTests` + the whole suite green), then commit. Tasks are dependency-ordered
> so the suite builds and stays green after every task. Literal code is given for every load-bearing piece. The
> TomHarte gate (Task 14) is the ONLY task needing the heavy `-c Release` + the fetched vectors; Tasks 1-13 are
> synthetic (vector-free) and run on every box. **Tasks 3-12's `*Execute` bodies are CALLED only after Task 13
> wires the dispatch arms** — so Tasks 3-12 add the bodies + unit tests that drive them via `cpu.Step()` over a
> synthetic operword, and each of those `[Fact]`s is `[Fact(Skip="dispatch wired in Task 13")]` until Task 13,
> then un-skipped. (Alternatively, build Task 13's dispatch FIRST with stub bodies — but the body-first order
> keeps each task's diff focused; the skip-then-unskip pattern mirrors M4.5a Task 3→4.)

---

### Task 0: Baseline + recon (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch off `main`.**
  Run: `git switch -c feat/m4-5b-integer-alu`
  Expected: on the new branch; `git log --oneline -1` shows `437fa0f`. Confirm M4.5a is present:
  `src/CpuEmulator.Cpus.M68000/M68000Cpu.Move.cs` exists; `M68000TomHarteRunner.RunCase` executes (not the
  `NotYetExecuted` sentinel); `M68000FetchStream` exists.

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test` → 0 failures (record the EXACT count; the closeout pins it). On a box WITHOUT vectors the
  680x0 MOVE theory shows SKIPPED — that is the default state.
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each cited surface holds (R2-R6 above):**
  - The Step arm + the `__operword`/`__size`/`__srcMode`/`__srcReg`/`_eaPcBase` locals (`CpuEmitter.cs:218-252`).
  - `EmitMoveDispatchArms` (`CpuEmitter.cs:4204`) + the partial-hook emit (`:307-318`).
  - The reused primitives' exact signatures + visibility (R3) — confirm `ReadEaOperand`/`WriteEaOperand`/
    `SetDataRegPartial`/`DataReg`/`SizeMask`/`Areg`/`SetAreg`/`ComputeEa`/`Ccr`/`SR`/`_cycles` are reachable
    from a sibling `M68000Cpu` partial (they are `private` on the same class → yes).
  - The dataset ALU operation strings (R4) — confirm they are VERBATIM (no underscores added for the ALU
    families; `ADD`, `ADDI`, `ADDQ`, `ADDX`, `ADDA` are all distinct rows).
  - The 51 vector filenames (R5) — `ls ~/.cache/cpuemulator/vectors/680x0/v1/*.json.gz` and confirm the 51 are
    present and no `ADDI/ADDQ/CMPM` file exists.

- [ ] **Step 4:** No commit (read-only). Proceed to Task 1.

---

### Task 1: The ALU function table + the `BinaryAluExecute` driver shell (TDD)

> Implement ADR 0007 §5.1: the `AluFn`/`CcrRule` delegates, the `AluShape` enum, the `Alu` static function table
> (`Add`/`Sub`/`And`/`Or`/`Eor`), and the `BinaryAluExecute` driver SHELL (read-A / read-B per shape /
> write-result / set-CCR). The CCR rules arrive in Task 2; this task uses a no-op CCR rule so the data path is
> provable in isolation. Proven by a synthetic test of the `Alu.*` functions (pure) + a driver smoke test that a
> RegEa `Dn op Dn` writes the arithmetic result (CCR ignored this task).

**Files:**
- Create: `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs`
- Test: `tests/CpuEmulator.Tests/Generators/M68000AluCcrTests.cs` (create — the `Alu.*` half)

- [ ] **Step 1: Write the failing test.** Create `tests/CpuEmulator.Tests/Generators/M68000AluCcrTests.cs`:

```csharp
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000AluCcrTests
{
    [Theory]
    [InlineData(0x10u, 0x20u, 0u, 0x30u)]                    // .b 0x10+0x20 = 0x30
    [InlineData(0x00FFu, 0x0001u, 1u, 0x0100u)]              // .w 0xFF+1 = 0x100
    [InlineData(0x7FFFFFFFu, 0x00000001u, 2u, 0x80000000u)] // .l signed-overflow value (CCR is Task 2)
    public void Alu_Add_sums_within_the_size(uint a, uint b, uint size, uint expected)
        => Assert.Equal(expected & M68000Cpu.SizeMaskProbe(size), M68000Cpu.Alu.Add(a, b, false, size) & M68000Cpu.SizeMaskProbe(size));

    [Theory]
    [InlineData(0x30u, 0x10u, 0u, 0x20u)]                    // .b 0x30-0x10 = 0x20
    [InlineData(0x0000u, 0x0001u, 1u, 0xFFFFu)]              // .w 0-1 = 0xFFFF (borrow)
    public void Alu_Sub_subtracts_within_the_size(uint a, uint b, uint size, uint expected)
        => Assert.Equal(expected & M68000Cpu.SizeMaskProbe(size), M68000Cpu.Alu.Sub(a, b, false, size) & M68000Cpu.SizeMaskProbe(size));

    [Fact] public void Alu_And() => Assert.Equal(0x0F00u, M68000Cpu.Alu.And(0xFF00, 0x0FF0, false, 2u) & 0xFFFFu);
    [Fact] public void Alu_Or()  => Assert.Equal(0xFFF0u, M68000Cpu.Alu.Or (0xFF00, 0x0FF0, false, 2u) & 0xFFFFu);
    [Fact] public void Alu_Eor() => Assert.Equal(0xF0F0u, M68000Cpu.Alu.Eor(0xFF00, 0x0FF0, false, 2u) & 0xFFFFu);
}
```

  > **`SizeMaskProbe` + `Alu` visibility:** `SizeMask` is `private static` on `M68000Cpu.Move.cs`. Expose a thin
  > `public static uint SizeMaskProbe(uint size) => SizeMask(size);` in `M68000Cpu.Alu.cs` (a harmless test seam,
  > mirroring M4.5a's `*Probe` wrappers). Make the `Alu` nested static class `public` (it holds only pure
  > functions — no state) so the unit test can call `M68000Cpu.Alu.Add(...)` directly without driving `Step`.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000AluCcrTests"`
  Expected: FAIL — `M68000Cpu.Alu` / `SizeMaskProbe` do not exist.

- [ ] **Step 3: Create the ALU layer.** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs`:

```csharp
namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5b (ADR 0007 option C): the table-driven integer-ALU helper layer. ONE BinaryAluExecute driver does
/// read-A / read-B (per the AluShape) / write-result / set-CCR (per the CcrRule) for the whole regular core;
/// the ~22 regular families are one-line *Execute registrations. The CCR rules (Ccr.Arith/Logic/Cmp/ArithX)
/// are written + tested ONCE — the dominant ALU TomHarte-failure class. The irregular tail (EXT/CLR/ADDX/SUBX/
/// NEGX/MULU/MULS/DIVU/DIVS) keeps bespoke bodies. Reuses the M4.5a substrate verbatim (ReadEaOperand,
/// WriteEaOperand, SetDataRegPartial, DataReg, SizeMask, ComputeEa, the wide bus) — this layer is a new CALLER,
/// nothing in those primitives changes (ADR 0007 §5.2/§5.4 — the seam is untouched).
/// </summary>
public sealed partial class M68000Cpu
{
    // ── The per-family descriptor types (ADR 0007 §5.1) ──────────────────────────────────────────────────────
    /// <summary>The per-family ALU function: (a, b, xIn, size) -> result. Pure; no state, no CCR.</summary>
    public delegate uint AluFn(uint a, uint b, bool xIn, uint size);

    /// <summary>The per-family CCR rule: (a, b, result, size, xIn, oldCcr) -> the new CCR byte. One instance
    /// per CCR family (Arith / Logic / Cmp / ArithX — Task 2).</summary>
    public delegate byte CcrRule(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr);

    /// <summary>Where operand A and B come from + where the result goes (ADR 0007 §5.1).</summary>
    private enum AluShape { RegEa, ImmEa, QuickEa, UnaryEa }

    /// <summary>Pure ALU functions — the per-family content that differs by ONE line. (Carry/overflow live in
    /// the CcrRule, Task 2; these compute only the result value, full-width then masked by the body.)</summary>
    public static class Alu
    {
        public static uint Add(uint a, uint b, bool x, uint size) => a + b + (x ? 1u : 0u);
        public static uint Sub(uint a, uint b, bool x, uint size) => a - b - (x ? 1u : 0u);
        public static uint And(uint a, uint b, bool x, uint size) => a & b;
        public static uint Or (uint a, uint b, bool x, uint size) => a | b;
        public static uint Eor(uint a, uint b, bool x, uint size) => a ^ b;
    }

    // Test seam (mirrors M4.5a's *Probe wrappers).
    public static uint SizeMaskProbe(uint size) => SizeMask(size);

    /// <summary>The ALU driver (ADR 0007 §5.1). Read operand A and B per <paramref name="shape"/>, apply
    /// <paramref name="aluFn"/>, write the result to the destination (unless <paramref name="writesResult"/> is
    /// false — CMP/CMPI/TST compare-only), set CCR via <paramref name="ccrRule"/>. ONE implementation of
    /// read-A / read-B / write / CCR for the whole regular core. operword/r/size/srcMode/srcReg are the SAME
    /// inputs the generated dispatch passes the MOVE bodies (the seam is unchanged).</summary>
    private void BinaryAluExecute(
        AluFn aluFn, CcrRule ccrRule, AluShape shape, bool writesResult,
        uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint mask = SizeMask(size);
        bool xIn = (Ccr & 0x10) != 0;   // the incoming X flag (only ArithX reads it; harmless for others)
        byte oldCcr = (byte)(SR & 0xFF);

        // Resolve A (operand read first / dest), B (the second operand), and the destination (mode,reg).
        uint a, b, dstMode, dstReg;
        switch (shape)
        {
            case AluShape.RegEa:
            {
                uint dnReg = (operword >> 9) & 7u;       // bits 11-9 = the Dn operand
                bool toEa  = (operword & 0x0100u) != 0;  // bit 8 direction: 1 = Dn op <ea> -> <ea>
                uint dn = DataReg(dnReg) & mask;
                uint ea = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords);   // the <ea> operand
                if (toEa) { a = ea; b = dn;  dstMode = srcMode; dstReg = srcReg; }   // dest = EA
                else      { a = dn; b = ea;  dstMode = 0u;      dstReg = dnReg;  }   // dest = Dn
                break;
            }
            case AluShape.ImmEa:
            {
                // Immediate forms: the #imm is the LEADING extension word(s); the EA's words follow. The decode
                // walk captured them in stream order, so ext[0..immCount-1] = imm, ext[immCount..] = EA.
                int immCount = size == 2u ? 2 : 1;
                uint imm = size == 2u ? (((uint)r.ExtensionWords[0] << 16) | r.ExtensionWords[1])
                                      : (r.ExtensionWords[0] & mask);
                var eaExt = ShiftExt(r.ExtensionWords, immCount);
                a = ReadEaOperand(srcMode, srcReg, size, eaExt) & mask;   // dest EA value (operand A)
                b = imm & mask;
                dstMode = srcMode; dstReg = srcReg;
                break;
            }
            case AluShape.QuickEa:
            {
                uint imm3 = (operword >> 9) & 7u; if (imm3 == 0u) imm3 = 8u;   // 0 -> 8
                a = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords) & mask;
                b = imm3;
                dstMode = srcMode; dstReg = srcReg;
                break;
            }
            default: // UnaryEa — one EA operand (NEG/NOT/TST). A=0 sentinel; the aluFn ignores the unused arg.
            {
                a = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords) & mask;
                b = 0u;
                dstMode = srcMode; dstReg = srcReg;
                break;
            }
        }

        uint result = aluFn(a, b, xIn, size) & mask;
        if (writesResult) WriteEaOperand(dstMode, dstReg, size, result, r.ExtensionWords);
        SR = (ushort)((SR & 0xFF00) | ccrRule(a, b, result, size, xIn, oldCcr));
    }

    /// <summary>Shift the extension-word buffer down by <paramref name="drop"/> words (used to skip the leading
    /// immediate words so ComputeEa(EA) reads ext[0]/ext[1]). Mirrors M4.5a's DestExtensionWords slice.</summary>
    private static CpuEmulator.Core.Jit.ExtensionWords ShiftExt(CpuEmulator.Core.Jit.ExtensionWords all, int drop)
        => new CpuEmulator.Core.Jit.ExtensionWords(
            all[drop], all[drop + 1], all[drop + 2], all[drop + 3],
            System.Math.Max(0, all.Count - drop));
}
```

  > **The no-op CCR rule for this task's smoke test:** the driver takes a `CcrRule`; Task 2 supplies the real
  > rules. For this task, the `Alu.*` functions are tested directly (pure, no driver). The driver's data path is
  > exercised in Task 3 once a real `Ccr.Arith` exists. **Do NOT add a fake CCR rule here** — keep the driver
  > body complete (it is correct) and let Task 3 be its first caller. The `M68000AluCcrTests` this task adds test
  > ONLY `Alu.*`.
  >
  > **`xIn` for non-X families:** `Alu.Add(a,b,x,size)` adds `x?1:0`. For ADD/ADDI/ADDQ the X is NOT an input —
  > but those families call `Alu.Add` with `xIn` passed as the LIVE X flag, which would corrupt the result. **Fix
  > (load-bearing):** the regular registrations pass `Alu.Add`/`Alu.Sub` whose `x` arm is suppressed because the
  > driver computes `xIn` but the REGULAR families must ignore it. Two clean options — pick (i): **(i)** give the
  > regular families their own non-X functions `Alu.Add`/`Alu.Sub` that hard-code `x=false` internally (ignore
  > the `x` param), and a SEPARATE `Alu.AddX`/`Alu.SubX` for the X-ops (Task 10) that honor `x`. Update `Alu.Add`
  > to `=> a + b;` (drop the `+ (x?1:0)`), and ADD `public static uint AddX(uint a,uint b,bool x,uint size) =>
  > a + b + (x?1u:0u);` + `SubX` in Task 10. **Apply (i) now:** make `Alu.Add => a + b;`, `Alu.Sub => a - b;`
  > (no X term); the X variants are added in Task 10. The test above already expects `Add(...,false,...)` so it
  > passes either way, but the registrations in Task 3 rely on the no-X form.

- [ ] **Step 3a: Correct `Alu.Add`/`Alu.Sub` to the no-X form** (per the note): in the `Alu` class above, use
  `public static uint Add(uint a, uint b, bool x, uint size) => a + b;` and
  `public static uint Sub(uint a, uint b, bool x, uint size) => a - b;` (the `x` param is kept for the delegate
  signature but ignored; `AddX`/`SubX` honoring `x` land in Task 10).

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000AluCcrTests"`
  Expected: PASS — the `Alu.*` functions compute within the size.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (additive; nothing dispatches to `BinaryAluExecute` yet).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502/Z80 untouched).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs \
        tests/CpuEmulator.Tests/Generators/M68000AluCcrTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): add the table-driven ALU driver + pure Alu function table (ADR 0007 option C)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~7.

---

### Task 2: The CCR rule set — `Arith` / `Logic` / `Cmp` / `ArithX` (TDD)

> ADR 0007 §3/§5.1: the single highest-leverage move — the CCR rules written + tested ONCE. `Arith` = NZVCX from
> carry/overflow (X = C). `Logic` = NZ from the result, V=C=0, X untouched. `Cmp` = like Arith but X untouched
> (CMP never sets X) and no write. `ArithX` = like Arith but **Z is STICKY** (cleared on non-zero, never set —
> for ADDX/SUBX/NEGX). Proven by synthetic CCR truth-table tests (carry-out, signed overflow, sticky-Z).

**Files:**
- Modify: `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs` (add the `Ccr` rule class)
- Test: `tests/CpuEmulator.Tests/Generators/M68000AluCcrTests.cs` (add the CCR-rule cases)

- [ ] **Step 1: Write the failing tests.** Append to `tests/CpuEmulator.Tests/Generators/M68000AluCcrTests.cs`:

```csharp
    // CCR bit positions: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01
    [Fact]
    public void Ccr_Arith_add_carry_out_sets_C_and_X()
    {
        // .b 0xFF + 0x01 = 0x100 -> result byte 0x00: Z set, carry-out -> C+X set, N clear, V clear.
        byte c = M68000Cpu.Ccr.ArithProbe(a: 0xFFu, b: 0x01u, result: 0x100u & 0xFFu, size: 0u, xIn: false, oldCcr: 0x00, isSub: false);
        Assert.Equal(0x04 | 0x01 | 0x10, c);   // Z + C + X
    }

    [Fact]
    public void Ccr_Arith_add_signed_overflow_sets_V()
    {
        // .b 0x7F + 0x01 = 0x80: N set (0x80), V set (pos+pos->neg), C clear, X clear, Z clear.
        byte c = M68000Cpu.Ccr.ArithProbe(0x7Fu, 0x01u, 0x80u, 0u, false, 0x00, isSub: false);
        Assert.Equal(0x08 | 0x02, c);          // N + V
    }

    [Fact]
    public void Ccr_Arith_sub_borrow_sets_C_and_X()
    {
        // .b 0x00 - 0x01 = 0xFF: borrow -> C+X set, N set, Z clear, V clear.
        byte c = M68000Cpu.Ccr.ArithProbe(0x00u, 0x01u, 0xFFu, 0u, false, 0x00, isSub: true);
        Assert.Equal(0x08 | 0x01 | 0x10, c);   // N + C + X
    }

    [Fact]
    public void Ccr_Logic_sets_NZ_clears_VC_keeps_X()
    {
        // .w result 0x8000: N set, Z clear, V=C=0, X preserved (oldCcr X set -> stays set).
        byte c = M68000Cpu.Ccr.LogicProbe(0x8000u, 1u, oldCcr: 0x10);
        Assert.Equal(0x08 | 0x10, c);          // N + (preserved X)
    }

    [Fact]
    public void Ccr_Cmp_is_arith_without_X()
    {
        // CMP .b 0x00 - 0x01 = 0xFF: N+C set, but X is NOT touched (oldCcr X set -> stays; would NOT be set fresh).
        byte c = M68000Cpu.Ccr.CmpProbe(0x00u, 0x01u, 0xFFu, 0u, oldCcr: 0x00);
        Assert.Equal(0x08 | 0x01, c);          // N + C, NO X
    }

    [Fact]
    public void Ccr_ArithX_Z_is_sticky_cleared_on_nonzero_preserved_on_zero()
    {
        // Result non-zero -> Z cleared. Result zero with oldCcr Z set -> Z STAYS set (sticky).
        byte nonZero = M68000Cpu.Ccr.ArithXProbe(0x10u, 0x01u, 0x11u, 0u, xIn: false, oldCcr: 0x04, isSub: false);
        Assert.Equal(0x00, nonZero & 0x04);    // Z cleared (non-zero result)
        byte zeroKeepsZ = M68000Cpu.Ccr.ArithXProbe(0x01u, 0x01u, 0x00u, 0u, xIn: false, oldCcr: 0x04, isSub: true);
        Assert.Equal(0x04, zeroKeepsZ & 0x04); // Z preserved (zero result + oldCcr Z set)
        byte zeroOldClear = M68000Cpu.Ccr.ArithXProbe(0x01u, 0x01u, 0x00u, 0u, xIn: false, oldCcr: 0x00, isSub: true);
        Assert.Equal(0x00, zeroOldClear & 0x04); // Z stays clear (never SET by ArithX)
    }
```

- [ ] **Step 2: Run to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000AluCcrTests"`
  Expected: FAIL — `M68000Cpu.Ccr` and the `*Probe` wrappers do not exist.

- [ ] **Step 3: Add the CCR rule set.** Append to `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs` (inside the
  partial class):

```csharp
    /// <summary>The ALU CCR rules — the dominant TomHarte-failure class, written + tested ONCE (ADR 0007 §3).
    /// CCR bits: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01. The arithmetic carry/overflow rule is parameterized by
    /// isSub (subtraction borrows where addition carries; CMP/SUB share the borrow form). The instances exposed
    /// as CcrRule delegates (Arith/Logic/Cmp/ArithX) close over isSub.</summary>
    public static class Ccr
    {
        private static uint SignBit(uint size) => size switch { 0u => 0x80u, 1u => 0x8000u, _ => 0x80000000u };
        private static uint Mask(uint size)    => size switch { 0u => 0xFFu, 1u => 0xFFFFu, _ => 0xFFFFFFFFu };

        /// <summary>Arithmetic NZVCX from a + b (or a - b). X mirrors C. Carry = unsigned carry/borrow out of the
        /// size; V = signed overflow.</summary>
        internal static byte Arith(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr, bool isSub)
        {
            uint m = Mask(size), sb = SignBit(size);
            uint r = result & m;
            bool n = (r & sb) != 0;
            bool z = r == 0;
            bool c, v;
            if (!isSub)
            {
                // add: carry-out if the masked sum is less than either operand's masked value (or with X-in).
                ulong full = (ulong)(a & m) + (b & m) + (xIn ? 1u : 0u);
                c = (full & ~(ulong)m) != 0;
                v = (((a ^ r) & (b ^ r)) & sb) != 0;          // both inputs' sign differs from result's sign
            }
            else
            {
                ulong full = (ulong)(a & m) - (b & m) - (xIn ? 1u : 0u);
                c = (full >> 32) != 0 || (a & m) < ((b & m) + (xIn ? 1u : 0u)); // borrow out
                v = (((a ^ (b & m)) & (a ^ r)) & sb) != 0;    // a and b differ in sign AND a differs from result
            }
            byte ccr = (byte)(oldCcr & ~0x1F);
            if (n) ccr |= 0x08; if (z) ccr |= 0x04; if (v) ccr |= 0x02; if (c) ccr |= 0x01;
            if (c) ccr |= 0x10; else ccr &= unchecked((byte)~0x10);   // X = C
            return ccr;
        }

        /// <summary>The CcrRule delegate instances the registrations pass.</summary>
        public static byte ArithAdd(uint a, uint b, uint r, uint size, bool xIn, byte old) => Arith(a, b, r, size, xIn, old, isSub: false);
        public static byte ArithSub(uint a, uint b, uint r, uint size, bool xIn, byte old) => Arith(a, b, r, size, xIn, old, isSub: true);

        /// <summary>Logic NZ; V=C=0; X untouched.</summary>
        public static byte Logic(uint a, uint b, uint r, uint size, bool xIn, byte old)
        {
            uint m = Mask(size), sb = SignBit(size);
            byte ccr = (byte)(old & ~0x0F);   // clear N Z V C; keep X (bit 4)
            if ((r & sb) != 0) ccr |= 0x08;
            if ((r & m) == 0) ccr |= 0x04;
            return ccr;                        // V=C=0 by the clear
        }

        /// <summary>Compare: Arith-borrow but X is NEVER touched (CMP/CMPA/CMPI do not affect X).</summary>
        public static byte Cmp(uint a, uint b, uint r, uint size, bool xIn, byte old)
        {
            byte arith = Arith(a, b, r, size, xIn, old, isSub: true);
            byte keptX = (byte)(old & 0x10);
            return (byte)((arith & ~0x10) | keptX);   // restore the original X
        }

        /// <summary>ArithX (ADDX/SUBX/NEGX): Arith, but Z is STICKY — cleared on a non-zero result, and on a zero
        /// result it is PRESERVED from oldCcr (never freshly SET). isSub picks add vs sub borrow.</summary>
        internal static byte ArithX(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr, bool isSub)
        {
            byte ccr = Arith(a, b, result, size, xIn, oldCcr, isSub);
            uint m = Mask(size);
            bool zResult = (result & m) == 0;
            // Arith() set Z = (r==0). Override with the sticky rule: clear it, then re-OR oldCcr's Z only if zero.
            ccr = (byte)(ccr & ~0x04);
            if (zResult) ccr |= (byte)(oldCcr & 0x04);   // preserve incoming Z when result is zero
            return ccr;
        }
        public static byte ArithXAdd(uint a, uint b, uint r, uint size, bool xIn, byte old) => ArithX(a, b, r, size, xIn, old, isSub: false);
        public static byte ArithXSub(uint a, uint b, uint r, uint size, bool xIn, byte old) => ArithX(a, b, r, size, xIn, old, isSub: true);

        // Test seams.
        public static byte ArithProbe(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr, bool isSub) => Arith(a, b, result, size, xIn, oldCcr, isSub);
        public static byte LogicProbe(uint result, uint size, byte oldCcr) => Logic(0, 0, result, size, false, oldCcr);
        public static byte CmpProbe(uint a, uint b, uint result, uint size, byte oldCcr) => Cmp(a, b, result, size, false, oldCcr);
        public static byte ArithXProbe(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr, bool isSub) => ArithX(a, b, result, size, xIn, oldCcr, isSub);
    }
```

  > **The CCR carry/overflow formulas are the single most TomHarte-sensitive code in the PR.** They are written
  > to a defensible model here, but the Task-14 sweep is the ground truth. If a `.b`/`.w`/`.l` carry or V case
  > diverges in the sweep, fix it HERE (one place) — that is the whole point of centralizing CCR. The reconcile
  > decision tree (Task 14 Step 3) covers it. Do NOT scatter CCR fixes into per-family bodies.

- [ ] **Step 4: Run to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000AluCcrTests"`
  Expected: PASS — carry/overflow/borrow, Logic keep-X, Cmp no-X, sticky-Z.

- [ ] **Step 5: Full gate.** `dotnet test` green; `dotnet build --no-incremental -warnaserror` clean;
  `RegeneratedSpecTests` green.

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs \
        tests/CpuEmulator.Tests/Generators/M68000AluCcrTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): the ALU CCR rule set — Arith/Logic/Cmp/ArithX written and tested once

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~6.

---

### Task 3: The regular RegEa registrations — ADD/SUB/AND/OR/EOR/CMP (TDD)

> The six two-operand reg↔EA families as one-line `*Execute` registrations on `BinaryAluExecute`. The bodies are
> CALLED once Task 13 wires the dispatch; this task adds them + synthetic execute tests driven via `cpu.Step()`
> over a synthetic operword (the tests are `Skip`'d until Task 13, then un-skipped — mirrors M4.5a Task 3→4).

**Files:**
- Modify: `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs` (the six registrations)
- Test: `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs` (create)

- [ ] **Step 1: Write the failing tests.** Create `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000AluExecuteTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Add_w_ea_to_dn_sets_result_and_ccr()
    {
        // ADD.w D1,D0  (dir=0: <ea(=D1)> + D0 -> D0). operword 0xD041:
        //   1101(ADD) 000(Dn=D0 @11-9) 0(opmode .w, dir to Dn => bit8=0 + size .w bits 7-6=01)... use ADD.w Dn->Dn:
        //   ADD.w D1,D0 = 0xD041 = 1101 000 001 000 001 (Dn=D0, opmode 001 = .w to Dn, ea-mode 000 reg 001 = D1).
        var (cpu, _) = Build((0x1000, 0xD0), (0x1001, 0x41));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00001000);
        cpu.SetRegister("D1", 0x00000234);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00001234u, (uint)cpu.GetRegister("D0"));   // .w add into D0 low word, upper preserved
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x1F);  // no flags (positive, non-zero, no carry/ovf)
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Add_b_dn_to_ea_writes_back_to_dn_partial()
    {
        // ADD.b D0,D1 (dir=1: D0 + <ea(=D1)> -> D1). operword 0xD300 = 1101 001 100 000 000
        //   Dn=D1(@11-9=001), opmode 100 = .b to <ea>, ea-mode 000 reg 000 = D0.
        var (cpu, _) = Build((0x1000, 0xD3), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000005);
        cpu.SetRegister("D1", 0x1122330A);   // .b dest; upper 24 bits must survive
        cpu.Step();
        Assert.Equal(0x1122330Fu, (uint)cpu.GetRegister("D1"));   // 0x0A + 0x05 = 0x0F, partial
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Cmp_w_sets_ccr_without_writing()
    {
        // CMP.w D1,D0 (D0 - D1, compare-only, dest unchanged). operword 0xB041 = 1011 000 001 000 001.
        var (cpu, _) = Build((0x1000, 0xB0), (0x1001, 0x41));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00005000);
        cpu.SetRegister("D1", 0x00005000);   // equal -> Z set
        cpu.Step();
        Assert.Equal(0x00005000u, (uint)cpu.GetRegister("D0"));   // CMP writes nothing
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x1F);  // Z set, others clear
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void And_l_clears_v_c()
    {
        // AND.l D1,D0 -> D0. operword 0xC081 = 1100 000 010 000 001 (opmode 010 = .l to Dn).
        var (cpu, _) = Build((0x1000, 0xC0), (0x1001, 0x81));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0xFF00FF00);
        cpu.SetRegister("D1", 0x0FF00FF0);
        cpu.SetRegister("SR", 0x0003);       // V+C set going in -> must clear
        cpu.Step();
        Assert.Equal(0x0F000F00u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x03); // V=C=0
    }
}
```

  > **The operword bit layouts above are spelled out so Builder does not have to derive them.** Verify each
  > against the M68000 PRM line-decode at implementation (the test's `Assert` is the proof). If a chosen operword
  > also matches a tighter-mask family, pick a different register pair — the decode is mask/match ordered.

- [ ] **Step 2: Run to verify it fails / is skipped.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000AluExecuteTests"`
  Expected: the four `[Fact]`s are SKIPPED this task (the dispatch is not wired). They un-skip in Task 13.

- [ ] **Step 3: Add the six registrations.** Append to `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs`:

```csharp
    // ── The regular two-operand reg<->EA families — one line each (ADR 0007 §5.1) ────────────────────────────
    private partial void AddExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Add, Ccr.ArithAdd, AluShape.RegEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void SubExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Sub, Ccr.ArithSub, AluShape.RegEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void AndExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.And, Ccr.Logic,    AluShape.RegEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void OrExecute (uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Or,  Ccr.Logic,    AluShape.RegEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void EorExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Eor, Ccr.Logic,    AluShape.RegEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void CmpExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Sub, Ccr.Cmp,      AluShape.RegEa, writesResult: false, ow, r, sz, sm, sr);
```

  > **`partial void` requires the matching declaration from the generator** (Task 13 emits
  > `private partial void AddExecute(…)` etc). Until Task 13, these implementing bodies have no declaration to
  > bind to — **so Task 3 will NOT COMPILE on its own.** Two options: **(a)** land Task 13's generator
  > declarations FIRST (move Task 13 before Task 3), or **(b)** keep this body in the partial and accept that
  > Tasks 3-12 are committed but the suite is built+green only AFTER Task 13. **Choose (a):** reorder so Task 13
  > (the generator dispatch + declarations for the FULL ALU set) lands immediately after Task 2, then Tasks 3-12
  > each fill in bodies whose declarations already exist and un-skip their tests. **The plan is written in
  > body-order for readability; Builder executes Task 13 right after Task 2, then 3→12.** (This mirrors M4.5a,
  > where the generator hooks landed in Task 3 before the bodies in Tasks 4-5.) The commit ordering note is
  > restated at Task 13.

- [ ] **Step 4:** (After Task 13 is in place) un-skip the four `[Fact]`s and run:
  Run: `dotnet test --filter "FullyQualifiedName~M68000AluExecuteTests"`
  Expected: PASS — ADD into Dn, ADD partial-write to EA, CMP no-write + Z, AND clears V/C.

- [ ] **Step 5: Full gate.** `dotnet test` green; `dotnet build --no-incremental -warnaserror` clean;
  `RegeneratedSpecTests` green.

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs \
        tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): ADD/SUB/AND/OR/EOR/CMP via the table-driven RegEa registrations

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~4.

---

### Task 4: The address-reg variants — ADDA/SUBA/CMPA (TDD)

> ADDA/SUBA: dest = An, NO CCR, `.w` source SIGN-EXTENDS to 32 then the op is on the full 32 bits. CMPA: dest =
> An (read as the full 32-bit compare operand A), but it DOES set CCR (a `.l` compare regardless of size, with
> the `.w` source sign-extended). These do NOT fit the RegEa registration (An dest + sign-extend + the
> no-CCR/CCR split) — give them dedicated one-line-ish bodies that still call `Alu.*`.

**Files:**
- Modify: `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs`
- Test: `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs` (add cases)

- [ ] **Step 1: Add the failing tests.** Append to `M68000AluExecuteTests.cs`:

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Adda_w_sign_extends_source_and_sets_no_ccr()
    {
        // ADDA.w D0,A1 = 0xD2C0 = 1101 001 011 000 000 (An=A1@11-9, opmode 011 = .w, ea D0).
        var (cpu, _) = Build((0x1000, 0xD2), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000FFFF);   // .w source 0xFFFF -> sign-extends to 0xFFFFFFFF (= -1)
        cpu.SetRegister("A1", 0x00001000);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00000FFFu, (uint)cpu.GetRegister("A1"));  // 0x1000 + (-1) = 0x0FFF (full 32-bit add)
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x1F); // ADDA sets NO CCR
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Cmpa_l_sets_ccr_does_not_write_an()
    {
        // CMPA.l D0,A1 = 0xB3C0 = 1011 001 111 000 000 (An=A1, opmode 111 = .l, ea D0). A1 - D0.
        var (cpu, _) = Build((0x1000, 0xB3), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A1", 0x00005000);
        cpu.SetRegister("D0", 0x00005000);   // equal -> Z
        cpu.Step();
        Assert.Equal(0x00005000u, (uint)cpu.GetRegister("A1"));  // CMPA writes nothing
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x1F); // Z set
    }
```

- [ ] **Step 2: Run → skipped (un-skip after Task 13).**

- [ ] **Step 3: Add the three bodies.** Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── Address-reg variants: An dest, .w source sign-extends to 32, the op is full-32-bit. ──────────────────
    private partial void AddAExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => AddrAlu(ow, r, sz, sm, sr, Alu.Add, setsCcr: false, writes: true);
    private partial void SubAExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => AddrAlu(ow, r, sz, sm, sr, Alu.Sub, setsCcr: false, writes: true);
    private partial void CmpAExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => AddrAlu(ow, r, sz, sm, sr, Alu.Sub, setsCcr: true,  writes: false);

    /// <summary>ADDA/SUBA/CMPA shared body: An dest (bits 11-9). The size is .w (sz==1) or .l (sz==2); a .w
    /// source SIGN-EXTENDS to 32 and the arithmetic is ALWAYS on the full 32 bits. ADDA/SUBA set no CCR and
    /// write An; CMPA sets CCR (a full-32-bit Cmp) and writes nothing.</summary>
    private void AddrAlu(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg,
        AluFn aluFn, bool setsCcr, bool writes)
    {
        uint anReg = (ow >> 9) & 7u;
        uint srcRaw = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords);
        uint src = size == 1u ? unchecked((uint)(int)(short)(ushort)srcRaw) : srcRaw;  // .w sign-extend to 32
        uint a = Areg(anReg);
        uint result = aluFn(a, src, false, 2u);                  // full 32-bit op (size index 2)
        if (writes) SetAreg(anReg, result);
        if (setsCcr)
            SR = (ushort)((SR & 0xFF00) | Ccr.Cmp(a, src, result, 2u, false, (byte)(SR & 0xFF)));
    }
```

- [ ] **Step 4:** (After Task 13) un-skip + run → PASS.
- [ ] **Step 5: Full gate.** `dotnet test`; `-warnaserror`; `RegeneratedSpecTests` — all green.
- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): ADDA/SUBA (no CCR) + CMPA (CCR) with .w-source sign-extend, full-32-bit op

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 5: The unary core — NEG / NOT / TST (TDD)

> NEG = `0 - <ea>` (Arith borrow; X = C). NOT = `~<ea>` (Logic). TST = compare `<ea>` to 0 (Logic NZ from the
> operand, V=C=0, X untouched, NO write). All `UnaryEa`. NEG and NOT write the EA; TST does not. (NEGX is the
> sticky-Z variant → Task 10; CLR is bespoke → Task 9.)

**Files:**
- Modify: `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs`
- Test: `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs` (add cases)

- [ ] **Step 1: Add failing tests.**

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Neg_b_negates_and_sets_carry()
    {
        // NEG.b D0 = 0x4400 = 0100 0100 0000 0000 (size .b, ea-mode 000 reg 000 = D0).
        var (cpu, _) = Build((0x1000, 0x44), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x11223301);   // .b = 0x01 -> 0 - 1 = 0xFF
        cpu.Step();
        Assert.Equal(0x112233FFu, (uint)cpu.GetRegister("D0"));      // partial .b
        Assert.Equal(0x08 | 0x01 | 0x10, (int)((uint)cpu.GetRegister("SR") & 0x1F)); // N + C + X
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Not_w_complements_and_logic_ccr()
    {
        // NOT.w D0 = 0x4640 = 0100 0110 0100 0000 (size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x46), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000FF00);   // .w 0xFF00 -> ~ = 0x00FF
        cpu.Step();
        Assert.Equal(0x000000FFu, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x03);    // V=C=0
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Tst_l_sets_nz_without_writing()
    {
        // TST.l D0 = 0x4A80 = 0100 1010 1000 0000 (size .l, ea D0).
        var (cpu, _) = Build((0x1000, 0x4A), (0x1001, 0x80));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x80000000);   // negative
        cpu.SetRegister("SR", 0x0003);       // V+C going in -> cleared
        cpu.Step();
        Assert.Equal(0x80000000u, (uint)cpu.GetRegister("D0"));     // unchanged
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x0F);    // N set, V=C=0, Z clear
    }
```

- [ ] **Step 2: Run → skipped.**
- [ ] **Step 3: Add the bodies.** Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── Unary core (NEG/NOT/TST). NEG = 0 - ea (Arith); NOT = ~ea (Logic); TST = compare ea to 0 (Logic, no write).
    private partial void NegExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.NegFn, Ccr.NegRule, AluShape.UnaryEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void NotExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.NotFn, Ccr.Logic,   AluShape.UnaryEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void TstExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.TstFn, Ccr.Logic,   AluShape.UnaryEa, writesResult: false, ow, r, sz, sm, sr);
```

  And add the unary `Alu` functions + the NEG CCR rule (NEG's CCR is `0 - a` borrow):

```csharp
    // (inside the Alu static class)
        public static uint NegFn(uint a, uint b, bool x, uint size) => 0u - a;       // 0 - operand
        public static uint NotFn(uint a, uint b, bool x, uint size) => ~a;            // bitwise complement
        public static uint TstFn(uint a, uint b, bool x, uint size) => a;             // identity (compare to 0)

    // (inside the Ccr static class)
        /// <summary>NEG CCR = Arith borrow of (0 - a). a here is the ORIGINAL operand (the driver's operand A).</summary>
        public static byte NegRule(uint a, uint b, uint r, uint size, bool xIn, byte old)
            => Arith(0u, a, r, size, false, old, isSub: true);
```

  > **The driver passes `a = <ea>` (the operand), `b = 0` for `UnaryEa`.** `NegFn` ignores `b` and computes
  > `0 - a`; `NegRule` computes the borrow of `0 - a` (passing `(0, a)` to `Arith`-sub so carry/X reflect the
  > true borrow). `NotFn`/`TstFn` use `Ccr.Logic` which only looks at the result + size. **TST's `writesResult:
  > false` means the driver computes the result (= the operand, identity) and sets Logic CCR but skips the
  > write** — exactly the read-only path ADR 0007 §7.3 asks for (TST rides `BinaryAluExecute(UnaryEa,
  > writesResult:false)`; the unary path is clean, so no bespoke body — Planner's call, resolved here).

- [ ] **Step 4:** (After Task 13) un-skip + run → PASS.
- [ ] **Step 5: Full gate** — all green.
- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): NEG/NOT/TST via the UnaryEa shape (TST rides writesResult:false — ADR 0007 §7.3)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 6: The immediate forms — ADDI/SUBI/ANDI/ORI/EORI/CMPI (TDD)

> The immediate forms ride the `ImmEa` shape: a leading `#imm` extension word(s) then the EA. **No v1 vector
> files exist for them (D1 RESOLVED) — so they are NOT TomHarte-gated.** Per D1, they are hardened by (a)
> **differential-equivalence** tests proving each produces an IDENTICAL `(result, CCR)` to its vector-proven
> reg↔EA counterpart through the same `BinaryAluExecute` driver (transitively inheriting the silicon proof of the
> ALU function + CCR rule), and (b) **synthetic fetch** tests proving the immediate operand is read correctly per
> size. The plan, the PR body, and the status doc state PLAINLY these forms execute but are not vector-green.

**Files:**
- Modify: `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs`
- Test: `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs` (add cases)

- [ ] **Step 1: Add failing tests.**

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addi_w_adds_immediate_to_dn()
    {
        // ADDI.w #$0010,D0 = 0x0640 + imm word 0x0010. operword 0x0640 = 0000 0110 0100 0000.
        var (cpu, _) = Build((0x1000, 0x06), (0x1001, 0x40), (0x1002, 0x00), (0x1003, 0x10));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000005);
        cpu.Step();
        Assert.Equal(0x00000015u, (uint)cpu.GetRegister("D0"));   // 0x05 + 0x10 = 0x15
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Cmpi_l_compares_immediate_no_write()
    {
        // CMPI.l #$00005000,D0 = 0x0C80 + imm long 0x00005000. operword 0x0C80 = 0000 1100 1000 0000.
        var (cpu, _) = Build((0x1000, 0x0C), (0x1001, 0x80),
                             (0x1002, 0x00), (0x1003, 0x00), (0x1004, 0x50), (0x1005, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00005000);   // equal -> Z
        cpu.Step();
        Assert.Equal(0x00005000u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x1F);  // Z set
    }
```

- [ ] **Step 2: Confirm the decode walk captures the leading immediate word(s).** Before wiring the bodies,
  verify (synthetic decode test) that `M68000Cpu.Decode` over an ADDI operword captures the imm word into
  `r.ExtensionWords[0]` and the length includes it. Add to `M68000AluExecuteTests.cs` (NOT skipped — pure decode):

```csharp
    [Fact]
    public void Addi_w_decode_captures_immediate_word_and_length()
    {
        var buf = new byte[] { 0x06, 0x40, 0x00, 0x10, 0, 0 };   // ADDI.w #$0010,D0
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        var r = M68000Cpu.Decode(stream);
        Assert.NotEqual(0xFFFFFFFFu, r.OperationKey);   // ADDI matched
        Assert.Equal(4, r.Length);                      // operword + 1 imm word = 4 bytes
        Assert.True(r.ExtensionWords.Count >= 1);
        Assert.Equal((ushort)0x0010, r.ExtensionWords[0]);
    }
```

  > **If this decode test FAILS** (the walk does not capture the leading immediate word because the immediate is
  > not an EA extension), the immediate forms need a decode-walk arm analogous to M4.5a's two-EA MOVE arm:
  > prepend `immCount = (size==2?2:1)` words to the extension capture for the immediate-form opIndices. This is a
  > GENERATOR change (in `EmitFieldDecodeWalk`, gated to `IsImmediateForm(opIndex)` — emit a name-driven
  > predicate from the dataset operation names `ADDI`/`SUBI`/`ANDI`/`ORI`/`EORI`/`CMPI`, mirroring
  > `EmitIsMoveFamily`). **Add that arm here if the decode test is red; if it is already green (the dataset's
  > length math already accounts for the imm word), no generator change is needed.** Builder confirms empirically
  > at this step. The `BinaryAluExecute` `ImmEa` branch already reads `ext[0..immCount-1]` as the imm and shifts
  > the rest for the EA, so the body is correct once the words are captured.

- [ ] **Step 3: Add the six registrations.** Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── Immediate forms (ImmEa). The CCR rule mirrors the reg form (Arith for ADDI/SUBI; Logic for ANDI/ORI/
    //    EORI; Cmp for CMPI). #imm is operand B; the EA is operand A AND the dest (except CMPI, no write). ─────
    private partial void AddIExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Add, Ccr.ArithAdd, AluShape.ImmEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void SubIExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Sub, Ccr.ArithSub, AluShape.ImmEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void AndIExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.And, Ccr.Logic,    AluShape.ImmEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void OrIExecute (uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Or,  Ccr.Logic,    AluShape.ImmEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void EorIExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Eor, Ccr.Logic,    AluShape.ImmEa, writesResult: true,  ow, r, sz, sm, sr);
    private partial void CmpIExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.Sub, Ccr.Cmp,      AluShape.ImmEa, writesResult: false, ow, r, sz, sm, sr);
```

- [ ] **Step 4 (HARDENING — differential-equivalence, the load-bearing D1 mitigation): add the imm≡reg tests.**
  These prove each immediate form produces an IDENTICAL `(result, CCR)` to its vector-PROVEN reg↔EA counterpart
  for the same `(a, b, size)` — `ADDI≡ADD`, `SUBI≡SUB`, `ANDI≡AND`, `ORI≡OR`, `EORI≡EOR`, `CMPI≡CMP`. Because the
  reg↔EA forms are TomHarte-green against silicon (Task 14), this transitively inherits the silicon proof for the
  imm forms' ALU function + CCR rule (the high-bug-density core). Add to `M68000AluExecuteTests.cs` (NOT skipped —
  these run two `Step`s on independent CPUs and compare; they go green once Task 13 + the reg/imm bodies exist,
  so run them after Task 13):

```csharp
    // Each row: (a in Dn, b operand, size, immOperword, immImmWord(s), regOperword) for an imm/reg pair on D0.
    // The imm form: <immOperword> then the immediate word(s) #b ; D0 = a. The reg form: <regOperword> reads b
    // from D1 ; D0 = a, D1 = b. Both target D0 (dir=0). The (result, CCR) MUST match bit-for-bit.
    private static (uint Result, uint Ccr) RunImm(uint a, uint bImm, uint size,
        byte ow0, byte ow1, byte[] immBytes)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        bus.Write8(0x1000, ow0); bus.Write8(0x1001, ow1);
        for (int i = 0; i < immBytes.Length; i++) bus.Write8((uint)(0x1002 + i), immBytes[i]);
        var cpu = new M68000Cpu(bus);
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", a); cpu.SetRegister("SR", 0);
        cpu.Step();
        return ((uint)cpu.GetRegister("D0"), (uint)cpu.GetRegister("SR") & 0x1F);
    }
    private static (uint Result, uint Ccr) RunReg(uint a, uint b, byte ow0, byte ow1)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        bus.Write8(0x1000, ow0); bus.Write8(0x1001, ow1);
        var cpu = new M68000Cpu(bus);
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", a); cpu.SetRegister("D1", b); cpu.SetRegister("SR", 0);
        cpu.Step();
        return ((uint)cpu.GetRegister("D0"), (uint)cpu.GetRegister("SR") & 0x1F);
    }

    [Theory]
    // (a, b, size, immOw0, immOw1, immBytes, regOw0, regOw1) — .w forms (size 1).
    // ADDI.w #b,D0 = 0x0640 + #b ; ADD.w D1,D0 = 0xD041.  SUBI.w = 0x0440 ; SUB.w = 0x9041.
    // ANDI.w = 0x0240 ; AND.w = 0xC041.  ORI.w = 0x0040 ; OR.w = 0x8041.  EORI.w = 0x0A40 ; EOR.w(D0,D1->D1) — use
    // EOR's only form (Dn->EA): match via a reg pair that lands on D0 by feeding b in D0... EOR has no <ea>->Dn
    // form, so the EORI≡EOR row compares EORI.w #b,D0 to EOR.w D1,D0 written as 0xB141 (Dn=D1 ^ D0 -> D0? No:
    // EOR is Dn ^ <ea> -> <ea>; to land on D0 use Dn=any, ea=D0). Builder picks operwords that put BOTH results
    // in D0 with the SAME (a,b); the asserted invariant is identical (result,CCR), not a specific encoding.
    [InlineData(0x00000005u, 0x0010u, 0x06, 0x40, new byte[]{0x00,0x10}, 0xD0, 0x41)] // ADDI.w ≡ ADD.w
    [InlineData(0x00000030u, 0x0010u, 0x04, 0x40, new byte[]{0x00,0x10}, 0x90, 0x41)] // SUBI.w ≡ SUB.w
    [InlineData(0x0000FF00u, 0x0FF0u, 0x02, 0x40, new byte[]{0x0F,0xF0}, 0xC0, 0x41)] // ANDI.w ≡ AND.w
    [InlineData(0x0000FF00u, 0x0FF0u, 0x00, 0x40, new byte[]{0x0F,0xF0}, 0x80, 0x41)] // ORI.w  ≡ OR.w
    public void Immediate_form_matches_its_reg_form_result_and_ccr(
        uint a, uint b, byte immOw0, byte immOw1, byte[] immBytes, byte regOw0, byte regOw1)
    {
        var imm = RunImm(a, b, size: 1u, immOw0, immOw1, immBytes);
        var reg = RunReg(a, b, regOw0, regOw1);
        Assert.Equal(reg.Result, imm.Result);
        Assert.Equal(reg.Ccr, imm.Ccr);
    }

    [Fact]
    public void Cmpi_matches_cmp_result_and_ccr()
    {
        // CMPI.w #b,D0 (0x0C40 + #b) ≡ CMP.w D1,D0 (0xB041): same (no-write result, CCR).
        var imm = RunImm(0x00005000u, 0x5000u, 1u, 0x0C, 0x40, new byte[]{0x50,0x00});
        var reg = RunReg(0x00005000u, 0x5000u, 0xB0, 0x41);
        Assert.Equal(reg.Result, imm.Result);   // both leave D0 unchanged
        Assert.Equal(reg.Ccr, imm.Ccr);         // Z set, etc.
    }
```

  > **EOR/EORI differential.** EOR has ONLY the `Dn ^ <ea> -> <ea>` form, so there is no `<ea> ^ Dn -> Dn`
  > encoding that lands the result in D0 from a D1 source the way ADD does. Builder writes the `EORI≡EOR` row by
  > choosing the EOR operword whose `<ea>` IS D0 (so EOR's result lands in D0) with the Dn source carrying `b`,
  > matched against `EORI.w #b,D0`. The asserted invariant is the identical `(result, CCR)` for the same `(a,b)`
  > — NOT a specific encoding. If a clean same-target EOR encoding is awkward, compare `Alu.Eor` + `Ccr.Logic`
  > directly (call `M68000Cpu.Alu.Eor` and `M68000Cpu.Ccr.LogicProbe`) for the EOR row instead of two `Step`s —
  > either proves the equivalence. The four `[InlineData]` rows above cover ADDI/SUBI/ANDI/ORI; add the
  > EORI row in whichever of the two styles is cleanest.

- [ ] **Step 5 (HARDENING — synthetic immediate FETCH): add the per-size immediate-read tests.** These cover the
  ONLY surface the differential test does not (operand fetch, not driver routing): the immediate is read from the
  correct extension word(s) for `.b`/`.w`/`.l`. Add to `M68000AluExecuteTests.cs`:

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addi_b_reads_one_immediate_word_low_byte()
    {
        // ADDI.b #$12,D0 = 0x0600 + imm word 0x0012 (the .b immediate is in the LOW byte of one ext word).
        var (cpu, _) = Build((0x1000, 0x06), (0x1001, 0x00), (0x1002, 0x00), (0x1003, 0x12));
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", 0x11223303);
        cpu.Step();
        Assert.Equal(0x11223315u, (uint)cpu.GetRegister("D0"));   // 0x03 + 0x12 = 0x15, partial .b
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addi_l_reads_two_immediate_words()
    {
        // ADDI.l #$00010002,D0 = 0x0680 + imm long 0x0001 0x0002 (two ext words).
        var (cpu, _) = Build((0x1000, 0x06), (0x1001, 0x80),
                             (0x1002, 0x00), (0x1003, 0x01), (0x1004, 0x00), (0x1005, 0x02));
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", 0x00000003);
        cpu.Step();
        Assert.Equal(0x00010005u, (uint)cpu.GetRegister("D0"));   // 0x00010002 + 3 = 0x00010005
    }
```

- [ ] **Step 6:** (After Task 13) un-skip the `[Fact(Skip=…)]`s and run → PASS.
- [ ] **Step 7: Full gate** — `dotnet test` green; `-warnaserror` clean; `RegeneratedSpecTests` green.
- [ ] **Step 8: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): ADDI/SUBI/ANDI/ORI/EORI/CMPI via ImmEa (NOT TomHarte-gated — no v1 vectors; imm≡reg differential + fetch tests; ADR 0007 D1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~10.

---

### Task 7: The quick forms — ADDQ / SUBQ (TDD)

> ADDQ/SUBQ: a 3-bit immediate (bits 11-9, `0`→`8`) op the EA. An-dest = NO CCR and the op is on the full 32
> bits (the quick-to-An quirk); all other dests behave like the imm form. **No v1 vector files exist (D1
> RESOLVED) — so they are NOT TomHarte-gated.** Per D1, hardened by (a) **differential-equivalence** tests
> (`ADDQ≡ADD`, `SUBQ≡SUB` through the same `BinaryAluExecute` driver — inheriting the silicon proof) and (b) a
> **synthetic fetch** test of the quick 3-bit field mapping incl. the `0→8` special case. The plan, the PR body,
> and the status doc state PLAINLY these forms execute but are not vector-green.

**Files:**
- Modify: `src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs`
- Test: `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs` (add cases)

- [ ] **Step 1: Add failing tests.**

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addq_w_adds_quick_immediate()
    {
        // ADDQ.w #3,D0 = 0x5640 = 0101 011 0 01 000 000 (data=3@11-9, opmode 0, size .w 01, ea D0).
        var (cpu, _) = Build((0x1000, 0x56), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000005);
        cpu.Step();
        Assert.Equal(0x00000008u, (uint)cpu.GetRegister("D0"));   // 5 + 3 = 8
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addq_to_an_is_full_32_bit_no_ccr()
    {
        // ADDQ.w #1,A0 = 0x5248 = 0101 001 0 01 001 000 (data=1, opmode 0, size .w, ea-mode 001 reg 000 = A0).
        var (cpu, _) = Build((0x1000, 0x52), (0x1001, 0x48));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x0000FFFF);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0x00010000u, (uint)cpu.GetRegister("A0"));   // full-32 add (NOT masked to .w)
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x1F);  // An dest -> NO CCR
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Subq_quick_zero_means_eight()
    {
        // SUBQ.w #8,D0 = 0x5140 = 0101 000 1 01 000 000 (data 000 -> 8, opmode 1 = sub, size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x51), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000000A);
        cpu.Step();
        Assert.Equal(0x00000002u, (uint)cpu.GetRegister("D0"));   // 0x0A - 8 = 2
    }
```

- [ ] **Step 2: Run → skipped (the An-dest one will need the special body).**
- [ ] **Step 3: Add the bodies.** ADDQ/SUBQ need an An-dest branch; give them a dedicated body that special-cases
  An, else delegates to the `QuickEa` driver. Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── Quick forms. imm3 = bits 11-9 (0->8). An dest = full-32-bit, NO CCR; else the QuickEa driver path. ────
    private partial void AddQExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => QuickAlu(ow, r, sz, sm, sr, Alu.Add, Ccr.ArithAdd);
    private partial void SubQExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => QuickAlu(ow, r, sz, sm, sr, Alu.Sub, Ccr.ArithSub);

    private void QuickAlu(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg,
        AluFn aluFn, CcrRule ccrRule)
    {
        uint imm3 = (ow >> 9) & 7u; if (imm3 == 0u) imm3 = 8u;
        if (srcMode == 1u)   // An dest: full-32-bit, NO CCR (the quick-to-An quirk)
        {
            uint an = Areg(srcReg);
            SetAreg(srcReg, aluFn(an, imm3, false, 2u));
            return;
        }
        // Else: ride the QuickEa driver (it re-reads imm3 the same way + sets CCR).
        BinaryAluExecute(aluFn, ccrRule, AluShape.QuickEa, writesResult: true, ow, r, size, srcMode, srcReg);
    }
```

- [ ] **Step 4 (HARDENING — differential-equivalence): add the quick≡reg tests.** Prove `ADDQ #n,D0` and
  `SUBQ #n,D0` (Dn-dest, not An) produce IDENTICAL `(result, CCR)` to `ADD.w D1,D0` / `SUB.w D1,D0` with the same
  `(a, n)` — reusing the `RunImm`/`RunReg` helpers from Task 6 (quick has no immediate word, so `immBytes` is
  empty). Add to `M68000AluExecuteTests.cs` (NOT skipped — run after Task 13):

```csharp
    [Theory]
    // (a, n, quickOw0, quickOw1, regOw0, regOw1). ADDQ.w #n,D0 = 0x5n40 (n@11-9, opmode 0, size .w, ea D0);
    // ADD.w D1,D0 = 0xD041 with D1 = n. SUBQ.w #n,D0 = 0x5n40|0x0100 ; SUB.w D1,D0 = 0x9041.
    [InlineData(0x00000005u, 3u, 0x56, 0x40, 0xD0, 0x41)]   // ADDQ #3 ≡ ADD with D1=3
    [InlineData(0x0000000Au, 8u, 0x51, 0x40, 0x90, 0x41)]   // SUBQ #8 (data 000 -> 8) ≡ SUB with D1=8
    public void Quick_form_matches_its_reg_form_result_and_ccr(
        uint a, uint n, byte quickOw0, byte quickOw1, byte regOw0, byte regOw1)
    {
        var quick = RunImm(a, n, size: 1u, quickOw0, quickOw1, immBytes: new byte[0]);  // no imm word for quick
        var reg   = RunReg(a, n, regOw0, regOw1);
        Assert.Equal(reg.Result, quick.Result);
        Assert.Equal(reg.Ccr, quick.Ccr);
    }
```

  > **`RunImm` with `immBytes = []` works for the quick form** because the quick operand is in the operword (bits
  > 11-9), not an extension word — `RunImm` just sets PC/D0/SR and Steps; the `QuickEa` body reads `imm3` from the
  > operword. The `(a, n)` pair feeds `n` as D1 in `RunReg`. The asserted invariant is the identical
  > `(result, CCR)`.

- [ ] **Step 5 (HARDENING — synthetic quick FETCH): the 3-bit field incl. 0→8.** Add to
  `M68000AluExecuteTests.cs` (the `0→8` case is the `Subq_quick_zero_means_eight` test already in Step 1 — keep
  it; ADD an explicit non-zero-field mapping assertion):

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addq_quick_field_seven_maps_to_seven()
    {
        // ADDQ.w #7,D0 = 0x5E40 (data 111 = 7 @11-9, opmode 0, size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x5E), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000); cpu.SetRegister("D0", 0x00000001);
        cpu.Step();
        Assert.Equal(0x00000008u, (uint)cpu.GetRegister("D0"));   // 1 + 7 = 8 (field 7 -> 7, NOT 8)
    }
```
  (The `0→8` special case is covered by `Subq_quick_zero_means_eight` in Step 1; together they pin both ends of
  the 3-bit-field mapping.)

- [ ] **Step 6:** (After Task 13) un-skip the `[Fact(Skip=…)]`s and run → PASS.
- [ ] **Step 7: Full gate** — `dotnet test` green; `-warnaserror` clean; `RegeneratedSpecTests` green.
- [ ] **Step 8: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): ADDQ/SUBQ via QuickEa (NOT TomHarte-gated — no v1 vectors; quick≡reg differential + 0->8 field fetch tests; ADR 0007 D1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~5.

---

### Task 8: EXT — Dn sign-extend (bespoke) (TDD)

> EXT.w sign-extends Dn.b→.w; EXT.l sign-extends Dn.w→.l. No EA; CCR = N/Z from the result, V=C=0, X untouched.
> The opmode (bits 8-6) distinguishes: `010` = EXT.w (→ size index .w), `011` = EXT.l (→ .l). Dn = bits 2-0.

**Files:** Modify `M68000Cpu.Alu.cs`; add tests to `M68000AluExecuteTests.cs`.

- [ ] **Step 1: Add failing tests.**

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Ext_w_sign_extends_byte_to_word()
    {
        // EXT.w D0 = 0x4880 = 0100 1000 1000 0000 (opmode 010 = byte->word, Dn=0).
        var (cpu, _) = Build((0x1000, 0x48), (0x1001, 0x80));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x112233F0);   // .b = 0xF0 (negative) -> .w = 0xFFF0
        cpu.Step();
        Assert.Equal(0x1122FFF0u, (uint)cpu.GetRegister("D0"));   // low word sign-extended, upper word preserved
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x0F);  // N set, Z/V/C clear
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Ext_l_sign_extends_word_to_long()
    {
        // EXT.l D0 = 0x48C0 = 0100 1000 1100 0000 (opmode 011 = word->long, Dn=0).
        var (cpu, _) = Build((0x1000, 0x48), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00008000);   // .w = 0x8000 (negative) -> .l = 0xFFFF8000
        cpu.Step();
        Assert.Equal(0xFFFF8000u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x0F);  // N set
    }
```

- [ ] **Step 2: Run → skipped.**
- [ ] **Step 3: Add the body.** Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── EXT — Dn sign-extend (bespoke; no EA). opmode bits 8-6: 010 = .b->.w, 011 = .w->.l. ──────────────────
    private partial void ExtExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
    {
        uint dn = ow & 7u;
        uint opmode = (ow >> 6) & 7u;        // 010 = byte->word, 011 = word->long
        uint cur = DataReg(dn);
        uint result;
        uint resultSize;
        if (opmode == 2u)                    // .b -> .w
        {
            result = unchecked((uint)(int)(sbyte)(byte)cur) & 0xFFFFu;
            SetDataRegPartial(dn, result, 1u);   // write the low word; upper word preserved
            resultSize = 1u;
        }
        else                                 // .w -> .l (opmode 3)
        {
            result = unchecked((uint)(int)(short)(ushort)cur);
            SetDataRegPartial(dn, result, 2u);   // write the whole long
            resultSize = 2u;
        }
        // CCR: N/Z from the (size-relative) result, V=C=0, X untouched.
        SR = (ushort)((SR & 0xFF00) | Ccr.Logic(0, 0, result, resultSize, false, (byte)(SR & 0xFF)));
    }
```

- [ ] **Step 4:** (After Task 13) un-skip + run → PASS.
- [ ] **Step 5: Full gate** — all green.
- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): EXT.w/.l Dn sign-extend (bespoke; Logic CCR, X untouched)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 9: CLR — write 0 with the dummy read (bespoke) (TDD)

> CLR writes 0 to the EA; CCR ALWAYS `Z=1, N=V=C=0`, X untouched. ADR 0007 §7.2: the 68000 READS the EA before
> writing 0 (a vector-confirmed dummy read — data-axis-invisible, but issue it so the M4.5d trace matches later).

**Files:** Modify `M68000Cpu.Alu.cs`; add tests.

- [ ] **Step 1: Add failing tests.**

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Clr_w_writes_zero_and_sets_only_Z()
    {
        // CLR.w D0 = 0x4240 = 0100 0010 0100 0000 (size .w, ea D0).
        var (cpu, _) = Build((0x1000, 0x42), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x1122FFFF);   // .w cleared -> 0x11220000
        cpu.SetRegister("SR", 0x0019);       // X+N+C set going in: Z must become 1, N/V/C 0, X preserved
        cpu.Step();
        Assert.Equal(0x11220000u, (uint)cpu.GetRegister("D0"));   // low word zeroed, upper preserved
        Assert.Equal(0x04 | 0x10, (int)((uint)cpu.GetRegister("SR") & 0x1F)); // Z set + X preserved; N=V=C=0
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Clr_b_memory_issues_a_read_before_the_write()
    {
        // CLR.b (A0) = 0x4210 = 0100 0010 0001 0000 (size .b, ea-mode 010 reg 000 = (A0)).
        var (cpu, bus) = Build((0x1000, 0x42), (0x1001, 0x10), (0x2000, 0x7F));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.Step();
        Assert.Equal((byte)0x00, bus.Read8(0x2000));   // memory cleared
    }
```

- [ ] **Step 2: Run → skipped.**
- [ ] **Step 3: Add the body.** Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── CLR — write 0; CCR always Z=1, N=V=C=0, X untouched. The 68000 READS the EA before writing (the
    //    vector-confirmed dummy read; data-axis-invisible but issued so the M4.5d trace matches — ADR 0007 §7.2).
    private partial void ClrExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
    {
        _ = ReadEaOperand(sm, sr, sz, r.ExtensionWords);   // dummy read (write-back for (An)+/-(An) happens here)
        WriteEaOperand(sm, sr, sz, 0u, r.ExtensionWords);  // write 0
        byte ccr = (byte)(SR & 0xFF);
        ccr = (byte)((ccr & 0x10) | 0x04);                 // keep X; set Z; clear N/V/C
        SR = (ushort)((SR & 0xFF00) | ccr);
    }
```

  > **The dummy read + the write both call `ComputeEa` for `(An)+`/`-(An)`, which mutates An TWICE** — wrong for
  > a single CLR (the address register should advance ONCE). **Load-bearing fix:** for the predecrement /
  > postincrement modes, compute the EA ONCE (pureEa) and route both the read and the write through that address
  > without a second write-back. Concretely: if `sm is 3u or 4u`, call `ComputeEa(sm, sr, sz, ext, pureEa:false)`
  > ONCE to get `ea` (does the single write-back), then `_bus.Read*`/`_bus.Write*` directly at `ea` for the dummy
  > read + the zero write. For register/simple-memory modes, the double `ReadEaOperand`/`WriteEaOperand` is
  > harmless (no write-back). Implement the address-once form:
  > ```csharp
  > if (sm is 3u or 4u) {
  >     uint ea = ComputeEa(sm, sr, sz, r.ExtensionWords, pureEa: false);   // single write-back
  >     switch (sz) { case 0u: _ = ReadByteAt(ea); WriteByteAt(ea, 0); break;
  >                   case 1u: _ = ReadWordBus(ea); WriteWordBus(ea, 0); break;
  >                   default:  _ = ReadLongBus(ea); WriteLongBus(ea, 0); break; }
  > } else { _ = ReadEaOperand(sm, sr, sz, r.ExtensionWords); WriteEaOperand(sm, sr, sz, 0u, r.ExtensionWords); }
  > ```
  > Builder uses this address-once form (the simpler version above is shown first for intent; the address-once
  > version is the one to SHIP — it is the only one the `(An)+`/`-(An)` TST/CLR vectors pass). Confirm
  > `ReadWordBus`/`WriteWordBus`/`ReadLongBus`/`WriteLongBus`/`ReadByteAt`/`WriteByteAt` are reachable (R3 — yes).

- [ ] **Step 4:** (After Task 13) un-skip + run → PASS (incl. the memory dummy-read case + the An-advance-once
  behavior — add a `(A0)+` CLR test asserting A0 advanced by the size exactly once).
- [ ] **Step 5: Full gate** — all green.
- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): CLR (Z=1 always; vector-confirmed dummy read; address-once for (An)+/-(An))

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 10: ADDX / SUBX / NEGX — the ArithX sticky-Z + the -(An),-(An) pairing (bespoke) (TDD)

> ADR 0007 §7.4 — the classic ALU-extend bug class. ADDX/SUBX: bit 3 (R/M) = `0` → `Dx op Dy → Dy` (reg↔reg);
> `1` → `-(Ax) op -(Ay) → (Ay)` (predecrement memory). X-flag in. `Ccr.ArithX` (sticky Z). NEGX = `0 - ea - X`,
> `UnaryEa`, `ArithX`. The `-(An),-(An)` operand pairing (BOTH predecrement) is the bug — test it against the
> ADDX/SUBX vectors (Task 14).

**Files:** Modify `M68000Cpu.Alu.cs`; add tests.

- [ ] **Step 1: Add the X-honoring `Alu` functions.** Append to the `Alu` static class:

```csharp
        public static uint AddX(uint a, uint b, bool x, uint size) => a + b + (x ? 1u : 0u);
        public static uint SubX(uint a, uint b, bool x, uint size) => a - b - (x ? 1u : 0u);
        public static uint NegXFn(uint a, uint b, bool x, uint size) => 0u - a - (x ? 1u : 0u);   // 0 - operand - X
```

  And the NEGX CCR rule (borrow of `0 - a - X`, sticky Z):

```csharp
    // (inside Ccr)
        public static byte NegXRule(uint a, uint b, uint r, uint size, bool xIn, byte old)
            => ArithX(0u, a, r, size, xIn, old, isSub: true);
```

- [ ] **Step 2: Add failing tests.**

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addx_b_reg_reg_uses_x_and_sticky_z()
    {
        // ADDX.b D1,D0 = 0xD101 = 1101 000 1 00 000 001 (Dy=D0@11-9, R/M=0 reg, size .b, Dx=D1@2-0).
        var (cpu, _) = Build((0x1000, 0xD1), (0x1001, 0x01));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000010);   // Dy
        cpu.SetRegister("D1", 0x00000005);   // Dx
        cpu.SetRegister("SR", 0x0010);       // X set -> +1
        cpu.Step();
        Assert.Equal(0x00000016u, (uint)cpu.GetRegister("D0"));   // 0x10 + 0x05 + 1 = 0x16
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addx_b_zero_result_preserves_incoming_Z_sticky()
    {
        // ADDX.b with result 0 and incoming Z=1 -> Z STAYS 1 (sticky). 0x00 + 0x00 + X(0) = 0.
        var (cpu, _) = Build((0x1000, 0xD1), (0x1001, 0x01));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000000);
        cpu.SetRegister("D1", 0x00000000);
        cpu.SetRegister("SR", 0x0004);       // Z=1 going in, X=0
        cpu.Step();
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x04);  // Z preserved (sticky)
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Addx_b_predecrement_pairs_both_operands()
    {
        // ADDX.b -(A1),-(A0) = 0xD109 = 1101 000 1 00 001 001 (Ay=A0@11-9, R/M=1 mem, Ax=A1@2-0).
        var (cpu, bus) = Build((0x1000, 0xD1), (0x1001, 0x09), (0x1FFF, 0x05), (0x2FFF, 0x10));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);       // dest -(A0) -> reads/writes 0x1FFF
        cpu.SetRegister("A1", 0x3000);       // src  -(A1) -> reads 0x2FFF
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal((byte)0x15, bus.Read8(0x1FFF));         // 0x05(dest) + 0x10(src) = 0x15 written to -(A0)
        Assert.Equal(0x1FFFu, (uint)cpu.GetRegister("A0"));  // A0 predecremented once
        Assert.Equal(0x2FFFu, (uint)cpu.GetRegister("A1"));  // A1 predecremented once
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Negx_b_negates_with_x()
    {
        // NEGX.b D0 = 0x4000 = 0100 0000 0000 0000 (size .b, ea D0). 0 - 0x01 - X(1) = 0xFE.
        var (cpu, _) = Build((0x1000, 0x40), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00000001);
        cpu.SetRegister("SR", 0x0010);       // X=1
        cpu.Step();
        Assert.Equal(0x000000FEu, (uint)cpu.GetRegister("D0"));   // 0 - 1 - 1 = 0xFE
    }
```

- [ ] **Step 3: Add the bodies.** Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── ADDX/SUBX — X-flag in, sticky Z (Ccr.ArithX). bit 3 (R/M): 0 = Dx op Dy -> Dy; 1 = -(Ax) op -(Ay) -> (Ay).
    private partial void AddXExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => XAlu(ow, sz, Alu.AddX, Ccr.ArithXAdd);
    private partial void SubXExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => XAlu(ow, sz, Alu.SubX, Ccr.ArithXSub);

    private void XAlu(uint ow, uint size, AluFn aluFn, CcrRule ccrRule)
    {
        uint mask = SizeMask(size);
        uint mag  = size == 0u ? 1u : size == 1u ? 2u : 4u;
        bool xIn = (Ccr & 0x10) != 0;
        byte oldCcr = (byte)(SR & 0xFF);
        uint yReg = (ow >> 9) & 7u;   // Dy / Ay (the dest, operand A)
        uint xReg = ow & 7u;          // Dx / Ax (the source, operand B)
        bool mem  = (ow & 0x0008u) != 0;

        uint a, b, result;
        if (!mem)   // Dx op Dy -> Dy
        {
            a = DataReg(yReg) & mask;
            b = DataReg(xReg) & mask;
            result = aluFn(a, b, xIn, size) & mask;
            SetDataRegPartial(yReg, result, size);
        }
        else        // -(Ax) op -(Ay) -> (Ay) : predecrement BOTH (source Ax first, then dest Ay — the pairing)
        {
            uint aAddr = ComputeEa(4u, xReg, size, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ax)
            b = ReadSized(aAddr, size) & mask;
            uint dAddr = ComputeEa(4u, yReg, size, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ay)
            a = ReadSized(dAddr, size) & mask;
            result = aluFn(a, b, xIn, size) & mask;
            WriteSized(dAddr, size, result);
        }
        SR = (ushort)((SR & 0xFF00) | ccrRule(a, b, result, size, xIn, oldCcr));
    }

    private uint ReadSized(uint ea, uint size) => size switch { 0u => ReadByteAt(ea), 1u => ReadWordBus(ea), _ => ReadLongBus(ea) };
    private void WriteSized(uint ea, uint size, uint v)
    { switch (size) { case 0u: WriteByteAt(ea, (byte)v); break; case 1u: WriteWordBus(ea, (ushort)v); break; default: WriteLongBus(ea, v); break; } }

    // ── NEGX — 0 - ea - X; UnaryEa; ArithX sticky Z. ──────────────────────────────────────────────────────────
    private partial void NegXExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => BinaryAluExecute(Alu.NegXFn, Ccr.NegXRule, AluShape.UnaryEa, writesResult: true, ow, r, sz, sm, sr);
```

  > **The pairing order (load-bearing — ADR 0007 §7.4).** The 68000 predecrements the SOURCE `Ax` first, reads
  > it, THEN predecrements the DEST `Ay`, reads it, computes, writes back to `Ay`'s (already-decremented)
  > address. The order matters when `Ax == Ay` (same register predecremented twice). The body above does source
  > then dest — confirm against the ADDX/SUBX `-(An),-(An)` vectors in Task 14 (the test
  > `Addx_b_predecrement_pairs_both_operands` uses distinct registers; the same-register edge is vector-covered).
  > **`BinaryAluExecute`'s `xIn` reads the live X** — Task 1's note fixed `Alu.Add`/`Alu.Sub` to the no-X form,
  > so the X-ops MUST use `Alu.AddX`/`Alu.SubX`/`Alu.NegXFn` (added this task), which honor `x`. Do not confuse
  > them.

- [ ] **Step 4:** (After Task 13) un-skip + run → PASS.
- [ ] **Step 5: Full gate** — all green.
- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): ADDX/SUBX/NEGX — ArithX sticky-Z + the -(An),-(An) source-then-dest pairing (ADR 0007 §7.4)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~4.

---

### Task 11: MULU / MULS — 16×16→32 (bespoke) (TDD)

> `Dn × <ea>.w → Dn.l` (16×16→32). MULU unsigned, MULS signed. CCR: N/Z from the 32-bit result, V=C=0, X
> untouched. Dn = bits 11-9; the source is the EA read as `.w`.

**Files:** Modify `M68000Cpu.Alu.cs`; add tests.

- [ ] **Step 1: Add failing tests.**

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Mulu_multiplies_unsigned_word_into_long()
    {
        // MULU D1,D0 = 0xC0C1 = 1100 000 011 000 001 (Dn=D0@11-9, MULU opmode 011, ea D1).
        var (cpu, _) = Build((0x1000, 0xC0), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00001000);   // .w source = 0x1000
        cpu.SetRegister("D1", 0x00000010);   // .w source = 0x0010
        cpu.Step();
        Assert.Equal(0x00010000u, (uint)cpu.GetRegister("D0"));   // 0x1000 * 0x10 = 0x10000 (32-bit)
        Assert.Equal(0x00u, (uint)cpu.GetRegister("SR") & 0x03);  // V=C=0
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Muls_multiplies_signed()
    {
        // MULS D1,D0 = 0xC1C1 = 1100 000 111 000 001 (Dn=D0, MULS opmode 111, ea D1).
        var (cpu, _) = Build((0x1000, 0xC1), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000FFFF);   // .w = -1
        cpu.SetRegister("D1", 0x00000002);   // .w = 2
        cpu.Step();
        Assert.Equal(0xFFFFFFFEu, (uint)cpu.GetRegister("D0"));   // -1 * 2 = -2
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x08);  // N set
    }
```

- [ ] **Step 2: Run → skipped.**
- [ ] **Step 3: Add the bodies.** Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── MULU/MULS — Dn.w * ea.w -> Dn.l. CCR: N/Z from the 32-bit result, V=C=0, X untouched. ────────────────
    private partial void MulUExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => Mul(ow, r, sm, sr, signed: false);
    private partial void MulSExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => Mul(ow, r, sm, sr, signed: true);

    private void Mul(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint srcMode, uint srcReg, bool signed)
    {
        uint dn = (ow >> 9) & 7u;
        uint srcW = ReadEaOperand(srcMode, srcReg, 1u, r.ExtensionWords) & 0xFFFFu;   // .w source
        uint dnW  = DataReg(dn) & 0xFFFFu;
        uint result = signed
            ? unchecked((uint)((int)(short)(ushort)dnW * (int)(short)(ushort)srcW))
            : (dnW * srcW);
        SetDataRegPartial(dn, result, 2u);   // whole-long write
        SR = (ushort)((SR & 0xFF00) | Ccr.Logic(0, 0, result, 2u, false, (byte)(SR & 0xFF)));  // N/Z, V=C=0, X kept
    }
```

- [ ] **Step 4:** (After Task 13) un-skip + run → PASS.
- [ ] **Step 5: Full gate** — all green.
- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): MULU/MULS 16x16->32 (Logic CCR N/Z, V=C=0, X untouched)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 12: DIVU / DIVS — 32÷16 with ÷0 detect-and-defer (bespoke) (TDD)

> `Dn.l ÷ <ea>.w → Dn` (quotient low 16, remainder high 16). DIVU unsigned, DIVS signed. CCR: N/Z from the 16-bit
> quotient, C=0, V set on overflow (quotient doesn't fit 16 bits), X untouched. **÷0 → divide-by-zero exception
> (vector 5) → M4.5d:** compute the ÷0 DETECTION in the body, DEFER the vectoring (the body returns without
> writing/CCR on ÷0; the runner's `IsExceptionCase` catches the vector-table read in the real vector — Task 14).

**Files:** Modify `M68000Cpu.Alu.cs`; add tests.

- [ ] **Step 1: Add failing tests.**

```csharp
    [Fact(Skip = "dispatch wired in Task 13")]
    public void Divu_divides_quotient_and_remainder()
    {
        // DIVU D1,D0 = 0x80C1 = 1000 000 011 000 001 (Dn=D0, DIVU opmode 011, ea D1).
        var (cpu, _) = Build((0x1000, 0x80), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00010005);   // dividend = 0x10005
        cpu.SetRegister("D1", 0x00000010);   // divisor .w = 0x10
        cpu.Step();
        // 0x10005 / 0x10 = quotient 0x1000, remainder 0x5 -> D0 = (rem<<16)|quot = 0x00051000.
        Assert.Equal(0x00051000u, (uint)cpu.GetRegister("D0"));
    }

    [Fact(Skip = "dispatch wired in Task 13")]
    public void Divu_by_zero_leaves_dn_unchanged_no_write()
    {
        // DIVU #0 path: body detects ÷0 and DEFERS (no write, no CCR change). The vectoring is M4.5d.
        var (cpu, _) = Build((0x1000, 0x80), (0x1001, 0xC1));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00010005);
        cpu.SetRegister("D1", 0x00000000);   // divisor 0
        cpu.Step();
        Assert.Equal(0x00010005u, (uint)cpu.GetRegister("D0"));   // unchanged (÷0 deferred; M4.5d vectors)
    }
```

- [ ] **Step 2: Run → skipped.**
- [ ] **Step 3: Add the bodies.** Append to `M68000Cpu.Alu.cs`:

```csharp
    // ── DIVU/DIVS — Dn.l / ea.w -> quotient(low16) + remainder(high16) in Dn. ÷0 detected here; the vector-5
    //    EXCEPTION is M4.5d (detect-and-defer: on ÷0 the body returns WITHOUT writing — the real vector takes
    //    the trap, which the runner's IsExceptionCase classifies as deferred). V on quotient overflow. ────────
    private partial void DivUExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => Div(ow, r, sm, sr, signed: false);
    private partial void DivSExecute(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint sz, uint sm, uint sr)
        => Div(ow, r, sm, sr, signed: true);

    private void Div(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint srcMode, uint srcReg, bool signed)
    {
        uint dn = (ow >> 9) & 7u;
        uint divisorW = ReadEaOperand(srcMode, srcReg, 1u, r.ExtensionWords) & 0xFFFFu;   // .w divisor
        if (divisorW == 0u)
            return;   // DETECT ÷0; DEFER the vector-5 exception to M4.5d (no write, no CCR change)

        uint dividend = DataReg(dn);
        uint quotient, remainder;
        bool overflow;
        if (!signed)
        {
            ulong q = dividend / divisorW;
            remainder = dividend % divisorW;
            overflow = q > 0xFFFFu;
            quotient = (uint)(q & 0xFFFFu);
        }
        else
        {
            int dvd = unchecked((int)dividend);
            int dvs = (int)(short)(ushort)divisorW;
            long q = (long)dvd / dvs;
            int rem = dvd % dvs;
            overflow = q > short.MaxValue || q < short.MinValue;
            quotient = (uint)((int)q & 0xFFFF);
            remainder = (uint)(rem & 0xFFFF);
        }

        byte ccr = (byte)(SR & 0xFF);
        ccr = (byte)(ccr & ~0x0F);   // clear N Z V C; keep X
        if (overflow)
        {
            ccr |= 0x02;             // V set; on overflow Dn is NOT updated (the 68000 leaves it) and N/Z undefined-
                                     // -ish but the vectors expect V set, result unchanged.
            SR = (ushort)((SR & 0xFF00) | ccr);
            return;                  // do NOT write Dn on overflow (vector-confirmed)
        }
        if ((quotient & 0x8000u) != 0) ccr |= 0x08;   // N from the 16-bit quotient sign
        if (quotient == 0u) ccr |= 0x04;              // Z
        SR = (ushort)((SR & 0xFF00) | ccr);
        SetDataRegPartial(dn, (remainder << 16) | (quotient & 0xFFFFu), 2u);
    }
```

  > **÷0 detect-and-defer (ADR 0007 §6).** The body returns on ÷0 WITHOUT writing Dn or CCR. The real TomHarte
  > ÷0 case's transactions include the vector-5 read pair → `IsExceptionCase` classifies it `DEFERRED(M4.5d)` →
  > the sweep counts it deferred, NOT failed (Task 14). The DETECTION lives here; the VECTORING is M4.5d. **The
  > overflow case (V set, Dn unchanged) is data-axis-asserted** (it is NOT an exception — no trap, just V).
  > Confirm the signed overflow + the N/Z-on-quotient rules against the DIVU/DIVS vectors in Task 14; these are
  > the subtlest CCR cases in the PR.

- [ ] **Step 4:** (After Task 13) un-skip + run → PASS.
- [ ] **Step 5: Full gate** — all green.
- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.Alu.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): DIVU/DIVS 32/16 (quotient+remainder; V-overflow; div-by-zero detect-and-defer to M4.5d)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 13: The generator dispatch arms + the partial-hook declarations (generator) (TDD)

> **EXECUTE THIS TASK RIGHT AFTER TASK 2** (per the Task-3 ordering note — the bodies' `partial void`
> declarations must exist before Tasks 3-12 compile). Extend the name-driven `EmitMoveDispatchArms`
> (`CpuEmitter.cs:4204`) with the ALU operation names → their `*Execute` hooks, and add the matching `partial
> void *Execute(…)` declarations to the FieldGrammar-gated emit (`:307-318`). NO other generator change. Proven
> by a Step-dispatch test: an ADD operword reaches `AddExecute` (after the body lands in Task 3, this goes
> green; until then, a no-throw + Undefined-route smoke test).

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs`
- Test: `tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs` (the un-skip happens here per family)

- [ ] **Step 1: Extend `EmitMoveDispatchArms`** (`CpuEmitter.cs:4209` — add the ALU arms to the `op switch`):

```csharp
            string? hook = op switch
            {
                "MOVE"         => "MoveExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "MOVEA"        => "MoveAExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "MOVE_TO_SR"   => "MoveToSrExecute(__operword, __r, __srcMode, __srcReg)",
                "MOVE_TO_CCR"  => "MoveToCcrExecute(__operword, __r, __srcMode, __srcReg)",
                "MOVE_FROM_SR" => "MoveFromSrExecute(__operword, __r, __srcMode, __srcReg)",
                "MOVE_USP"     => "MoveUspExecute(__operword)",
                // ── M4.5b: the integer-ALU families (ADR 0007 option C). All take the same (ow,r,size,sm,sr). ──
                "ADD"  => "AddExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "SUB"  => "SubExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "AND"  => "AndExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "OR"   => "OrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "EOR"  => "EorExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "CMP"  => "CmpExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ADDA" => "AddAExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "SUBA" => "SubAExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "CMPA" => "CmpAExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ADDI" => "AddIExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "SUBI" => "SubIExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ANDI" => "AndIExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ORI"  => "OrIExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "EORI" => "EorIExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "CMPI" => "CmpIExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ADDQ" => "AddQExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "SUBQ" => "SubQExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "NEG"  => "NegExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "NEGX" => "NegXExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "NOT"  => "NotExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "CLR"  => "ClrExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "TST"  => "TstExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "EXT"  => "ExtExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "ADDX" => "AddXExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "SUBX" => "SubXExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "MULU" => "MulUExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "MULS" => "MulSExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "DIVU" => "DivUExecute(__operword, __r, __size, __srcMode, __srcReg)",
                "DIVS" => "DivSExecute(__operword, __r, __size, __srcMode, __srcReg)",
                _ => null,
            };
```

  > **D1 RESOLVED — the imm/quick arms (`ADDI`/`SUBI`/`ANDI`/`ORI`/`EORI`/`CMPI`/`ADDQ`/`SUBQ`) ARE wired here**
  > (they execute; Tasks 6-7 implement + harden them). They are NOT TomHarte-gated (no v1 vectors) — that
  > limitation is disclosed in the gate + PR body, not encoded in the dispatch (the dispatch is uniform).

- [ ] **Step 2: Add the partial-hook declarations** to the FieldGrammar-gated emit (`CpuEmitter.cs:307-318`,
  right after the MOVE declarations):

```csharp
            // M4.5b: the integer-ALU op bodies — implemented by the hand-written M68000Cpu.Alu partial.
            foreach (var name in new[] {
                "Add","Sub","And","Or","Eor","Cmp","AddA","SubA","CmpA",
                "AddI","SubI","AndI","OrI","EorI","CmpI","AddQ","SubQ",
                "Neg","NegX","Not","Clr","Tst","Ext","AddX","SubX","MulU","MulS","DivU","DivS" })
            {
                sb.AppendLine($"    private partial void {name}Execute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg);");
            }
```

  > **Signature consistency (load-bearing).** EVERY ALU `*Execute` partial takes the SAME
  > `(uint operword, DecodeResult r, uint size, uint srcMode, uint srcReg)` — even the ones that ignore some args
  > (EXT ignores `sm`/`sr`; ADDX/SUBX ignore `sm`/`sr`/`size`-via-EA). This uniformity is what lets the
  > dispatch-arm switch pass one argument list. The implementing bodies in Tasks 3-12 already use this exact
  > signature. Confirm the declared `partial void` names match the body method names EXACTLY (a `MulUExecute`
  > declaration vs a `MuluExecute` body is a compile break — the generator's `name + "Execute"` and the body's
  > literal name must agree; the table above uses `MulU`/`MulS`/`DivU`/`DivS` → `MulUExecute`/… matching the
  > bodies).

- [ ] **Step 3: Write a dispatch smoke test.** Add to `M68000AluExecuteTests.cs` (NOT skipped — proves the arm
  routes; it goes green once Task 3's `AddExecute` body exists, so run it after Task 3):

```csharp
    [Fact]
    public void Step_routes_an_add_operword_to_the_add_body()
    {
        // ADD.w D1,D0 = 0xD041 — after Step, D0 == D0+D1 low word (the AddExecute body ran via dispatch).
        var (cpu, _) = Build((0x1000, 0xD0), (0x1001, 0x41));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00001000);
        cpu.SetRegister("D1", 0x00000234);
        cpu.Step();
        Assert.Equal(0x00001234u, (uint)cpu.GetRegister("D0"));
    }
```

- [ ] **Step 4: Build + run.**
  Run: `dotnet build CpuEmulator.sln -c Debug` → the generator emits the new arms + declarations; the
  hand-written bodies (landing in Tasks 3-12) bind to them. **If Tasks 3-12 are not yet committed, the bodies are
  unimplemented `partial void` → no-ops (C# elides them), so the suite COMPILES and the smoke test is
  red-but-skipped until the bodies land.** The ordering (Task 13 right after Task 2) means: commit Task 13's
  generator change with the bodies absent (compiles, the ALU arms are no-ops), then Tasks 3-12 fill the bodies +
  un-skip their tests.
  Run: `dotnet test --filter "FullyQualifiedName~M68000StepDispatchTests"` → the M4.5a MOVE dispatch still green
  (the ALU arms are additive; the `default` Undefined route is unchanged for any non-wired family).

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → green (the ALU bodies are no-ops until their tasks; the MOVE suite + all else green).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (the ALU arms + declarations are
  emitted ONLY inside `model.FieldGrammar is not null`; 6502/Z80 byte-identical).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs tests/CpuEmulator.Tests/Generators/M68000AluExecuteTests.cs
git commit -m "$(cat <<'EOF'
feat(generators): wire the integer-ALU dispatch arms + partial-hook declarations (name-driven, FieldGrammar-gated)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1 (the un-skips happen in Tasks 3-12).

---

### Task 14: The ALU-family TomHarte data-axis green sweep (the gate)

> The slice's deliverable: every NON-EXCEPTION case in the 51 in-scope ALU-family files runs green on the DATA
> axis (`D0–D7, A0–A6, USP, SSP, SR, RAM`). A skip-when-absent `[M68000TomHarteTheory]` over the 51-file list,
> run under `-c Release` with the vectors fetched. Heavy gate — run SEQUENTIALLY with a coarse monitor.

**Files:**
- Create: `tests/CpuEmulator.Tests/TomHarte/M68000AluTomHarteTests.cs`

- [ ] **Step 1: Write the sweep theory.** Create `tests/CpuEmulator.Tests/TomHarte/M68000AluTomHarteTests.cs`:

```csharp
using System.IO;
using System.Linq;
using CpuEmulator.Tests.TomHarte;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class M68000AluTomHarteTests
{
    // The 51 in-scope integer-ALU files (confirmed against the live 68000/v1 tree). NO immediate/quick files
    // exist (ADDI/ADDQ/...): the imm/quick OPCODES execute but are synthetic-tested only (ADR 0007 D1). CMPM is
    // absent from the dataset (dropped). All shift/rotate/bit/BCD/system files are M4.5c (not here).
    public static System.Collections.Generic.IEnumerable<object[]> AluFiles =>
    [
        ["ADD.b.json.gz"], ["ADD.w.json.gz"], ["ADD.l.json.gz"],
        ["ADDA.w.json.gz"], ["ADDA.l.json.gz"],
        ["ADDX.b.json.gz"], ["ADDX.w.json.gz"], ["ADDX.l.json.gz"],
        ["SUB.b.json.gz"], ["SUB.w.json.gz"], ["SUB.l.json.gz"],
        ["SUBA.w.json.gz"], ["SUBA.l.json.gz"],
        ["SUBX.b.json.gz"], ["SUBX.w.json.gz"], ["SUBX.l.json.gz"],
        ["AND.b.json.gz"], ["AND.w.json.gz"], ["AND.l.json.gz"],
        ["OR.b.json.gz"], ["OR.w.json.gz"], ["OR.l.json.gz"],
        ["EOR.b.json.gz"], ["EOR.w.json.gz"], ["EOR.l.json.gz"],
        ["CMP.b.json.gz"], ["CMP.w.json.gz"], ["CMP.l.json.gz"],
        ["CMPA.w.json.gz"], ["CMPA.l.json.gz"],
        ["NEG.b.json.gz"], ["NEG.w.json.gz"], ["NEG.l.json.gz"],
        ["NEGX.b.json.gz"], ["NEGX.w.json.gz"], ["NEGX.l.json.gz"],
        ["NOT.b.json.gz"], ["NOT.w.json.gz"], ["NOT.l.json.gz"],
        ["CLR.b.json.gz"], ["CLR.w.json.gz"], ["CLR.l.json.gz"],
        ["TST.b.json.gz"], ["TST.w.json.gz"], ["TST.l.json.gz"],
        ["EXT.w.json.gz"], ["EXT.l.json.gz"],
        ["MULU.json.gz"], ["MULS.json.gz"], ["DIVU.json.gz"], ["DIVS.json.gz"],
    ];

    [M68000TomHarteTheory]
    [MemberData(nameof(AluFiles))]
    public void Alu_family_is_TomHarte_green_on_the_data_axis(string file)
    {
        string dir = M68000TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;   // an absent file no-ops (the theory is the proof when present)

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new System.Collections.Generic.List<string>();
        int executed = 0, deferred = 0;
        foreach (var c in cases)
        {
            string? r = M68000TomHarteRunner.RunCase(c);          // data axis (timingAxis: false)
            if (r == M68000TomHarteRunner.DeferredException) { deferred++; continue; }   // DIVU/DIVS ÷0, etc -> M4.5d
            executed++;
            if (r is not null)
            {
                failures.Add(r);
                if (failures.Count >= 10) break;   // cap the report
            }
        }
        Assert.True(executed > 0, $"{file}: NO cases executed (guard against a vacuous green — all deferred?)");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ data-axis failures (executed {executed}, deferred {deferred}):\n{string.Join("\n", failures)}");
    }
}
```

  > **The `executed > 0` guard** prevents a vacuous green if every case were mis-classified as deferred — the
  > same anti-drift guard M4.5a used. The `DeferredException` sentinel is REUSED verbatim from
  > `M68000TomHarteRunner` (R2 — `IsExceptionCase` catches the DIVU/DIVS ÷0 vector-5 reads + any
  > address-error/privilege case). **The runner is UNCHANGED** — this theory only CALLS `RunCase` (seam
  > invariant §5.4).

- [ ] **Step 2: Run the gate under `-c Release` with the vectors present.**
  Run: `pwsh tools/get-test-vectors-68000.ps1`   (idempotent — no-op if already present)
  Then the heavy gate (SEQUENTIAL):
  Run: `dotnet test -c Release --filter "FullyQualifiedName~M68000AluTomHarteTests"`
  Expected: all 51 ALU-family files green on the data axis (every non-exception case: regs + SR + RAM match).
  **Use a COARSE monitor** (wake on terminal `Passed!`/`Failed!`/`error`/`Exception`, NOT per-test — 51 files ×
  8065 cases). **Kill any leftover `testhost.exe` workers before a fresh run.** Capture the executed-count.

- [ ] **Step 3: Reconcile any failures** (the per-file reports name the divergence). Decision tree:
  - **A CCR mismatch (`SR: expected … got …`)** → fix the relevant `Ccr.*` rule (Task 2) in the ONE place. This
    is the most likely point (carry/V/sticky-Z). DO NOT scatter the fix into bodies.
  - **A register/RAM mismatch on ADDX/SUBX** → the `-(An),-(An)` pairing order or sticky-Z (Task 10). Check the
    same-register `-(Ax),-(Ax)` edge against the vector.
  - **A DIVU/DIVS data mismatch** → the quotient/remainder packing or the V-overflow / N-Z-on-quotient rule
    (Task 12). The ÷0 cases must show as `deferred`, not `executed` (if a ÷0 case is `executed` and fails, the
    `IsExceptionCase` heuristic missed it — confirm the vector has the vector-5 read pair).
  - **An ADDA/SUBA/CMPA mismatch** → the `.w` sign-extend or the full-32-bit op (Task 4).
  - **A "NO cases executed" guard failure** → the dispatch arm did not wire (Task 13 name mismatch) or the file
    is all-exception (impossible for these families) — confirm the operation name matches the dataset.
  Each fix re-runs the FAST synthetic suite (`dotnet test`) first, then the heavy gate.

- [ ] **Step 4: Full suite + byte-identity confirmation.**
  Run: `dotnet test` (Debug) → 0 failures; the ALU theory SKIPPED when vectors absent (the default CI state);
  6502/Z80 byte-identical. Record the new total.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.
  Confirm `git status` shows only the M4.5b files (the `M68000Cpu.Alu.cs` partial + the generator + the ALU
  tests); no 6502/Z80 spec or generated-CPU file changed; `M68000Cpu.Move.cs`, `M68000FetchStream.cs`,
  `M68000Cpu.cs` bus helpers, and `M68000TomHarteRunner.cs` are UNCHANGED (the seam invariant).

- [ ] **Step 5: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/M68000AluTomHarteTests.cs
git commit -m "$(cat <<'EOF'
test(680x0): the integer-ALU TomHarte data-axis green sweep (51 files; -c Release gate)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1 (a 51-row MemberData theory).

---

## Verification + gate (the definition of done for M4.5b)

> **The per-PR three-part merge gate (ADR 0007 §6, stated verbatim in intent — anti-drift/anti-hallucination
> acceptance cycle). Merge is BLOCKED unless ALL THREE pass.** The green TomHarte sweep is the un-fakeable
> ground-truth gate — a behavioral oracle Builder cannot satisfy by asserting success.
>
> 1. **Full suite green + 6502/Z80 byte-identical.** `dotnet test` → 0 failures; the 6502 `RegeneratedSpecTests`
>    AND the Z80 regen guard green; every M4.5b change additive (gated to `model.FieldGrammar is not null` +
>    the M68000-only `M68000Cpu.Alu.cs` partial + the 680x0-only test infra). `git status` confirms no 6502/Z80
>    spec/generated-CPU file changed, and the seam files (`M68000FetchStream.cs`, the `M68000Cpu.cs` bus helpers,
>    `M68000TomHarteRunner.cs`, `M68000Cpu.Move.cs`) are UNCHANGED.
> 2. **The ALU-family TomHarte sweep ACTUALLY RUN GREEN — vectors PRESENT.** The 51 ALU-family `.json.gz` files
>    executed under `-c Release` with the 680x0 vectors FETCHED and PRESENT, on the DATA axis. The exact
>    invocation:
>    ```bash
>    pwsh tools/get-test-vectors-68000.ps1          # fetch 68000/v1 -> <cache>/680x0/v1 (multi-hundred-MB; once)
>    dotnet test -c Release --filter "FullyQualifiedName~M68000AluTomHarteTests"
>    ```
>    Expected: all 51 files green on the DATA axis (`D0–D7, A0–A6, USP, SSP, SR, RAM`, byte-exact), operword
>    seeded from `initial.prefetch[0]`. **A SKIPPED TomHarte test is NOT an acceptable merge state** — the
>    skip-when-absent attribute is only for boxes WITHOUT vectors; for MERGE the vectors MUST be present and the
>    sweep MUST run GREEN. Builder must show the actual green run output with a NON-ZERO executed count per file
>    (not a skip). The DIVU/DIVS ÷0 + any address-error/privilege cases are DEFERRED (M4.5d) via
>    `IsExceptionCase`, counted as deferred — NOT asserted (asserting would be a drift false-positive). The
>    timing axis (`final.pc`/`final.prefetch`/per-transaction trace/cycle count) is M4.5d (`timingAxis: false`).
>    **The immediate forms (ADDI/SUBI/ANDI/ORI/EORI/CMPI) and quick forms (ADDQ/SUBQ) are NOT part of this sweep
>    — no v1 vector files exist for them (D1 RESOLVED). They EXECUTE, covered by differential-equivalence (each ≡
>    its vector-proven reg↔EA counterpart) + synthetic fetch tests (Tasks 6-7), and are an explicitly,
>    honestly-disclosed exception to the vector gate. Builder must NOT claim or imply they are vector-green.**
> 3. **A pre-merge code-review pass.** A focused review of the diff — the `BinaryAluExecute` driver, the four CCR
>    rules, the regular registrations, the bespoke tail (esp. ADDX/SUBX pairing + sticky-Z, DIVU/DIVS ÷0 +
>    overflow, the CLR address-once dummy read), and the generator dispatch arms — to catch drift/hallucination
>    (e.g. a CCR bit tuned to one file, a body that passes a synthetic test but mis-models a flag) BEFORE merge.
>    The green sweep (gate 2) is the behavioral backstop; the review is the human/agent backstop.
>
> **Plainly: no merge unless (1) the full suite is green + 6502/Z80 byte-identical + the seam files unchanged,
> (2) the 51-file ALU TomHarte sweep has ACTUALLY RUN GREEN on the data axis with vectors present (not skipped,
> non-zero executed count), and (3) a pre-merge code review has passed.** The green TomHarte sweep is the gate
> that cannot be faked. The immediate/quick forms (ADDI…CMPI, ADDQ/SUBQ) are the one honestly-disclosed gap: they
> execute and are hardened by differential-equivalence to the gated reg↔EA forms + synthetic fetch tests, but the
> sweep does not cover them (no v1 vectors). Nowhere in the PR, the gate, or the status doc are they claimed
> vector-green.

- [ ] **(Gate 2) The ALU TomHarte sweep is green under `-c Release` with the vectors PRESENT.** Run the gate-2
  invocation. The vectors MUST be fetched first (without them the theory SKIPS — not a pass, not mergeable). Run
  heavy gates SEQUENTIALLY (concurrent gates starve each other — a 42-min stall was seen). COARSE monitor (wake
  on `Passed!`/`Failed!`/`error`/`Exception`). Kill leftover `testhost.exe` workers first. Capture the
  per-file executed-count to prove the sweep ran (not skipped).
- [ ] **(Gate 1) The fast (vector-free) suite is green in Debug.** `dotnet test` → 0 failures; every Task 1-13
  synthetic test green; the ALU theory SKIPPED on a no-vector box (fine for the fast suite; gate 2 still
  requires vectors present + green for merge).
- [ ] **6502 + Z80 stay byte-identical (every M4.5b change additive).** `dotnet test --filter
  "FullyQualifiedName~RegeneratedSpecTests"` → green (BOTH guards). The 68000 changes are gated to
  `model.FieldGrammar is not null` (the ALU dispatch arms + the partial-hook declarations) + the
  `M68000Cpu.Alu.cs` partial + the 680x0-only test infra. `git status` confirms no 6502/Z80 spec/generated-CPU
  change.
- [ ] **Seam invariant held.** `git status` / `git diff --stat` confirms `M68000FetchStream.cs`, the
  `M68000Cpu.cs` wide-bus helpers, `M68000TomHarteRunner.cs`, and `M68000Cpu.Move.cs` are UNCHANGED — M4.5b
  added the ALU layer + the dispatch arms ONLY (ADR 0007 §5.4).
- [ ] **Warnings-as-errors clean.** `dotnet build --no-incremental -warnaserror` → 0 warnings, 0 errors.
- [ ] **(Gate 3) Pre-merge code-review pass.** Review the diff for drift/hallucination before merge — esp. the
  CCR carry/overflow/sticky-Z formulas, the ADDX/SUBX pairing order, the DIVU/DIVS overflow + ÷0 paths, the CLR
  address-once dummy read. Record the review outcome in the PR.
- [ ] **Docs:** update `docs/user-guide/testing.md` (the 680x0 ALU gate is now executable — `-c Release` +
  fetch-first + coarse-monitor; note the 51 files); update the M4 status/resume doc
  (`docs/superpowers/plans/2026-06-15-m4-status-and-resume.md`) "What is NEXT" item 2 to mark M4.5b done + point
  at M4.5c. **The status-doc update MUST state PLAINLY that the immediate (ADDI…CMPI) + quick (ADDQ/SUBQ) forms
  execute but are NOT TomHarte-gated (no v1 vectors; differential-equivalence + synthetic-fetch tested only) —
  do NOT imply they are vector-green.** Carry the D1 resolution into the PR body.
- [ ] **PR:** open against `main`. Body claims EXACTLY: the 68000 EXECUTES the integer-ALU families (ADD/SUB/AND/
  OR/EOR/CMP reg↔EA; ADDA/SUBA/CMPA; NEG/NEGX/NOT/CLR/TST; EXT; ADDX/SUBX; MULU/MULS/DIVU/DIVS) TomHarte-green on
  the DATA axis across the 51 ALU-family vector files under `-c Release`, via the table-driven ALU layer (ADR
  0007 option C); AND the immediate (ADDI/SUBI/ANDI/ORI/EORI/CMPI) + quick (ADDQ/SUBQ) forms EXECUTE but are
  **NOT TomHarte-gated — no v1 vectors exist; covered by differential-equivalence (each ≡ its vector-proven
  reg↔EA form) + synthetic fetch tests only.** Name what is STILL deferred: shift/rotate/bit/BCD/Scc/system-misc
  (incl. ANDI/ORI/EORI-to-CCR/SR, MOVEM, LEA/PEA) = M4.5c; exceptions/branches/IPL + the DIVU/DIVS ÷0 vector-5 +
  address-error/privilege + the timing axis (final.pc/prefetch/trace/cycle) = M4.5d; the 68000 through the JIT =
  M4.6. NEVER overstate — **the reg↔EA/unary/EXT/X-ops/mul-div families are vector-green; the immediate/quick
  forms execute but are NOT vector-gated (honestly-disclosed gap); CMPM is dropped (absent from the dataset); the
  seam (fetch/bus/runner) is unchanged; the timing axis + ÷0 vectoring are deferred.** Include a **Docs Impact**
  section (testing.md + the M4 status doc) and the D1 resolution.

---

## Plan self-review (completed at write time)

- **Spec coverage (ADR 0007 §5 + the brief's scope):**
  - The `BinaryAluExecute` driver + `AluFn`/`CcrRule`/`AluShape` types → Task 1. ✓
  - The `Ccr.Arith`/`Logic`/`Cmp`/`ArithX` rule set, written + tested ONCE → Task 2. ✓
  - Regular RegEa registrations (ADD/SUB/AND/OR/EOR/CMP) → Task 3; ADDA/SUBA/CMPA → Task 4; NEG/NOT/TST → Task
    5; immediate forms → Task 6; quick forms → Task 7. ✓
  - **D1 RESOLVED (D1-A): imm/quick implemented + hardened.** Tasks 6-7 carry the differential-equivalence tests
    (imm/quick ≡ vector-proven reg↔EA form) + synthetic fetch tests; the gate + PR body + status doc state
    plainly they execute but are NOT TomHarte-gated (no v1 vectors). CMPM dropped (absent from the dataset). ✓
  - The bespoke tail: EXT → Task 8; CLR (dummy read) → Task 9; ADDX/SUBX/NEGX (sticky-Z + pairing) → Task 10;
    MULU/MULS → Task 11; DIVU/DIVS (÷0 detect-and-defer) → Task 12. ✓
  - Extend `EmitMoveDispatchArms` + the partial-hook declarations → Task 13. ✓
  - The data-axis green sweep over the 51 files → Task 14. ✓
- **ADR 0007 §6/§7 honored:**
  - Data axis asserted (regs+SR+RAM), operword from `initial.prefetch[0]` (the runner already does this; UNCHANGED). ✓
  - Timing axis deferred (`timingAxis:false`; runner unchanged). ✓
  - DIVU/DIVS ÷0 detect-and-defer (detection in the DIV body, vectoring via `IsExceptionCase` → M4.5d). ✓
  - §7.2 CLR dummy read → Task 9 (modeled, data-axis-invisible, address-once for (An)+/-(An)). ✓
  - §7.3 TST unary path → Task 5 (rides `BinaryAluExecute(UnaryEa, writesResult:false)` — resolved, no bespoke). ✓
  - §7.4 ArithX sticky-Z + -(An),-(An) pairing → Task 10 (tested + vector-confirmed in Task 14). ✓
  - §7.1 descriptor-generalization (#1) → noted as M4.5c-watched, NOT resolved here (Scope "OUT"). ✓
- **Seam invariant (§5.4):** `M68000FetchStream.cs`, the `M68000Cpu.cs` bus helpers, `M68000TomHarteRunner.cs`,
  and `M68000Cpu.Move.cs` are UNCHANGED — every task adds the ALU layer or the dispatch arms only; the gate
  re-confirms via `git diff --stat`. ✓
- **Placeholder scan:** every code step has literal code. The two genuinely-open implementation choices (the
  CLR address-once form; whether the imm-form decode needs a leading-imm-word capture arm) are bounded by an
  explicit in-task decision (Task 9 ships the address-once form; Task 6 Step 2 is a decode test that decides the
  generator arm empirically). No "TBD"/"similar to Task N". ✓
- **Type/name consistency:** `BinaryAluExecute`/`AluFn`/`CcrRule`/`AluShape`/`Alu`/`Ccr` (Tasks 1-2);
  `Alu.Add`(no-X)/`Alu.Sub`(no-X)/`Alu.And`/`Or`/`Eor`/`NegFn`/`NotFn`/`TstFn`/`AddX`/`SubX`/`NegXFn` (Tasks
  1/5/10); `Ccr.ArithAdd`/`ArithSub`/`Logic`/`Cmp`/`ArithXAdd`/`ArithXSub`/`NegRule`/`NegXRule` (Tasks 2/5/10);
  the `*Execute` body names (`AddExecute`…`DivSExecute`) match the generator's `name+"Execute"` table (Task 13);
  `AddrAlu`/`QuickAlu`/`XAlu`/`Mul`/`Div`/`ReadSized`/`WriteSized`/`ShiftExt`/`SizeMaskProbe` helpers consistent
  across their tasks. ✓
- **Code/recon contradictions surfaced (the code wins):** the dispatch is name-driven (opIndices track
  automatically — recon confirmed the `op switch` at `:4209`); `Alu.Add`/`Sub` are NO-X (the X-ops use the `*X`
  variants — Task 1 note); immediate/quick have NO vector files (D1 RESOLVED — implemented + hardened, not
  vector-gated; the brief's §1 table listed them but the v1 set does not); CMPM is absent from the dataset
  (dropped); the operword is in `initial.prefetch[0]` (runner already seeds it). ✓
- **Build-green-after-every-task:** Task 13 lands right after Task 2 (the `partial void` declarations precede the
  bodies); Tasks 1-2 are additive; Tasks 3-12 fill bodies whose declarations exist (no-op until filled) +
  un-skip tests; Task 14 is the heavy gate. The 6502/Z80 byte-identity guard gates every task. ✓
- **Altitude flags:** the CCR carry/overflow/sticky-Z formulas (Task 2) + the DIVU/DIVS overflow/N-Z rules (Task
  12) are the most TomHarte-sensitive code — centralized so Task 14 reconciles them in ONE place, not the first
  place they are tested per-family.

## Slice docs index

- **The governing decision:** `docs/architecture/0007-68000-interpreter-op-body-structure.md` (option C).
- **The decode/addressing/exception decisions + the M4 PR breakdown:**
  `docs/architecture/0003-68000-state-width-and-bus.md`, `docs/architecture/0004-68000-decode-addressing-and-exceptions.md`.
- **The master status/resume pointer:** `docs/superpowers/plans/2026-06-15-m4-status-and-resume.md`.
- **The M4.5a MOVE plan (the proven pattern this mirrors):** `docs/superpowers/plans/2026-06-15-m4-5a-move.md`.

## Closeout (filled at completion)

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | _(fill)_ |
| Final test count | _(fill)_ |
| ALU families TomHarte-green on the data axis (51 files)? | _(fill — ADD/SUB/AND/OR/EOR/CMP, ADDA/SUBA/CMPA, ADDX/SUBX, NEG/NEGX/NOT/CLR/TST, EXT, MULU/MULS/DIVU/DIVS)_ |
| Per-file executed vs deferred counts | _(fill — DIVU/DIVS show non-zero deferred = the ÷0 cases)_ |
| D1 resolution (imm/quick) | RESOLVED D1-A: ADDI…CMPI + ADDQ/SUBQ IMPLEMENTED + hardened (differential-equivalence ≡ reg↔EA + synthetic fetch); NOT TomHarte-gated (no v1 vectors) — disclosed in gate/PR/status doc. CMPM dropped (absent from dataset). |
| imm/quick differential-equivalence + fetch tests green? | _(fill — imm≡reg / quick≡reg result+CCR identical; per-size imm fetch + quick 0→8 field)_ |
| ADDX/SUBX sticky-Z + -(An),-(An) pairing green? | _(fill — vector-confirmed)_ |
| DIVU/DIVS ÷0 detect-and-defer working? | _(fill — detection in body, IsExceptionCase deferral)_ |
| Seam invariant held (fetch/bus/runner/Move unchanged)? | _(fill — git diff --stat)_ |
| 6502/Z80 un-regressed? | _(fill — RegeneratedSpecTests byte-identical; no 6502/Z80 spec change)_ |
| `-warnaserror` | _(fill — clean)_ |
| Still deferred | shift/rotate/bit/BCD/Scc/system-misc incl. ANDI/ORI/EORI-to-CCR/SR (M4.5c); exceptions/branches/IPL + DIVU/DIVS ÷0 vector-5 + address-error/privilege + the timing axis (M4.5d); the (B) generated op-table promotion (M4.5c/d, ADR 0007 §5.5); 68000 through JIT (M4.6); CMPM (absent from dataset) |
| Recommended next chunk | M4.5c — shift/rotate (ASL/LSR/ROXL/…) + bit ops (BTST/BCHG/BCLR/BSET) + BCD + Scc + system-misc |
