# M4.3b: The Structured EA Descriptor + the EA-Compute Layer (incl. the `(An)+`/`-(An)` register write-back), synthetically proven

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. This is the SECOND of the two M4.3 framework slices. **M4.3a (the word-granular field-decode
> variant + the synthetic field-grammar proof) MUST be merged first** — M4.3b consumes the `(operation,
> size, ea-mode, ea-register)` tuple M4.3a's decode walk extracts. M4.3b lands the EA layer; the 68000
> dataset + opcodes go live in M4.4/M4.5.

**Goal:** add the **structured effective-address layer** ADR 0004 Decision 2 names — a `(mode3, register3)`
EA descriptor + extension-word formats + an **EA-compute helper** that, for each of the 14 modes, computes
the address (reading the extension word VALUES the M4.3a walk left for it) and — for `(An)+`/`-(An)` —
performs the **operand-size-magnitude register write-back** (the first EA in this framework with an
ARCHITECTURAL SIDE EFFECT). It also replaces the 6502 per-class mode-legality `switch` with **EA-category
data** and retires `RequiredIndexRegister`'s hardcoded `X`/`Y` convention (meaningless for the 68000, where
the index register is any `An`/`Dn` named in the brief extension word). **M4.3b is proven entirely with
SYNTHETIC fixtures** (the M3.1b discipline): an EA-compute fixture computes each mode's address + the
`(An)+`/`-(An)` write-back + the `A7 ±2` special case + the pre-decrement-vs-post-increment ORDERING; a
legality fixture proves an EA-category check accepts/rejects modes per category. NO 68000 opcode goes live,
NO 680x0 vector turns green (M4.4/M4.5). Every 6502 + Z80 artifact stays byte-identical (the EA layer is the
68000's; the existing `AddrMode`s + the 6502 per-class legality are preserved, with the legality matrix
GENERALIZED additively — the 6502/Z80 paths must come out byte-identical, RECON-FINDING D5).

**Architecture:** M4.3a added the field-decode walk that extracts `(operation, size, ea-mode, ea-register)`
and consumes-for-length the extension words. M4.3b adds the layer BELOW that: given the extracted EA
6-bit field + size, COMPUTE the address. The closest in-tree precedent is the Z80 `Indexed` AddrMode +
the `EmitZ80IndexedEa(sb, indexReg, dispExpr)` emit helper (`CpuEmitter.cs:2165` — emits `ushort __ea =
unchecked((ushort)(IX + (sbyte)(disp)))`), the EA-helper shape ADR 0004 Decision 2 calls the "reuse seam
for the displacement/index forms." The 68000 EA-compute is a generalization: 14 modes (vs the Z80's one),
a 6-bit `(mode:register)` silicon encoding, 32-bit addresses, and — the genuinely-new capability — the
`(An)+`/`-(An)` **register write-back parameterized by operand size** (`.b`→±1, `.w`→±2, `.l`→±4; `A7`
always ±2). The legality side: today `ValidateModeForClass` (`SpecParser.cs:1229`) is a per-class `switch`
encoding 6502 + Z80 mode rules, and `RequiredIndexRegister` (`:1222`) hardcodes the index register as
`X`/`Y` (the 6502 convention). ADR 0004 Decision 2 moves 68000 legality to **EA-category data** (data-
alterable / memory-alterable / control / alterable) and retires the `X`/`Y` convention. M4.3b adds the
EA-category check additively (the 6502/Z80 classes keep their existing legality — RECON-FINDING D5); the
EA-category path is the 68000's, driven by the `EaCategory` tag M4.3a already carries on each `FieldOp`.
Every 6502/Z80 artifact stays byte-identical.

**Tech Stack:** C# (.NET 10), a Roslyn incremental source generator (`CpuEmulator.Generators`), a console
spec importer (`CpuEmulator.SpecImporter`), and xUnit. The 68000 SingleStepTests/680x0 TomHarte gate is
**out of scope** (it arrives with the interpreter, M4.5). M4.3b touches NO real CPU spec — the EA layer is
proven via `GeneratorTestHost.CompileAndLoadType`/`Run` synthetic fixtures, decoupled from the real
`M68000Spec.cs` (M4.4).

---

## Scope

**IN scope (the EA descriptor + compute layer + the legality matrix; NO opcode goes live):**

1. **The structured EA descriptor.** A representation of an EA: `(mode3, register3)` + an extension-word
   format tag (none / displacement16 / brief-index / abs16 / abs32 / immediate / pc-displacement / pc-index)
   + a `writeBack ∈ { None, PostInc, PreDec }`. The format tag and the write-back are DERIVED from the
   `(mode, register)` pair (mode `111` = the escape whose register selects abs.w / abs.l / d16(PC) /
   d8(PC,Xn) / immediate; mode `011` = `(An)+` → PostInc; mode `100` = `-(An)` → PreDec). M4.3b represents
   this as an emitted descriptor/decode function the EA-compute consumes — NOT 12 new `AddrMode` enum
   members (ADR Decision 2 Option B: add only the small number of `AddrMode` members the disassembler/
   assembler genuinely need; the rest is EA-descriptor data).
2. **The EA-compute layer (the new EA capability).** An emitted helper that, given `(eaMode, eaReg, size)`
   + the extension-word VALUES (read from the operword stream — M4.3b surfaces these, which M4.3a deferred —
   RECON-FINDING D2), computes the 32-bit EA address for each of the 14 modes, reusing the
   `EmitZ80IndexedEa` displacement/index shape where it aligns. For `(An)+`/`-(An)` it performs the
   **operand-size-magnitude register write-back**: PostInc reads `An` THEN adds `size`; PreDec subtracts
   `size` THEN reads `An` (the ORDERING — RECON-FINDING D3); with the `A7 ±2` special case (`(A7)+`/`-(A7)`
   move by 2 even for `.b`, to keep the stack word-aligned — RECON-FINDING D4). PC-relative modes compute
   the EA from the instruction's PC. `LEA`/`PEA` are pure-EA ops (compute the address, do NOT dereference)
   — the EA layer supports "compute the address without a memory access" (a flag on the compute call).
3. **Surface the extension-word VALUES (RECON-FINDING D2).** M4.3a's `DecodedOperands(Lo, Hi, Count)` holds
   2 bytes; a 68000 `abs.l`/`#imm.l` is 4 extension bytes. M4.3b widens the operand carriage so the
   EA-compute can READ the extension words (an extension-word buffer on `DecodeResult`, or a wider
   `DecodedOperands`). M4.3a consumed-and-discarded them for length; M4.3b surfaces them for the address.
4. **The EA-category legality matrix + retiring `RequiredIndexRegister`'s X/Y (D5).** Add an EA-category
   check (the classic 68000 buckets) the 68000's field grammar drives via the `EaCategory` tag M4.3a
   carries on each `FieldOp`; the 6502/Z80 per-class legality (`ValidateModeForClass`) is PRESERVED
   byte-identically (the EA-category path is additive — it fires only for a `FieldGrammar` CPU). Retire the
   `X`/`Y` hardcoding from the 68000 path (the index register is read from the brief extension word, not a
   fixed name) — the 6502 `RequiredIndexRegister("ZeroPageX") => "X"` rule is UNCHANGED (it gates the 6502
   rows; the 68000 has no `ZeroPageX` mode).
5. **A SYNTHETIC EA-compute + legality proof.** Fixtures asserting: each mode's computed EA; the `(An)+`
   post-increment + `-(An)` pre-decrement write-back by `.b`/`.w`/`.l`; the `A7 ±2` special case; the
   post-vs-pre ORDERING; a `LEA`-style pure-EA compute (no dereference); the EA-category check
   accepting/rejecting modes per category.

**OUT of scope (later slices — do NOT reach for them):**

- **The word-granular field-decode variant / `FetchUnit.Word` / the big-endian word stream** = M4.3a
  (already merged — M4.3b depends on its extracted `(operation, size, ea-mode, ea-register)` tuple).
- **Any 68000 opcode / dataset / real-spec decode going live** = M4.4 (the field-pattern dataset + the
  mnemonic-keyed gzip TomHarte loader + the emitted `M68000Spec.cs`) and M4.5 (the interpreter — which
  CALLS the EA-compute from real op bodies, sequences the write-back relative to the access, and gates on
  the 680x0 vectors). M4.3b ships the EA-compute HELPER + its synthetic proof; the real op bodies that
  invoke it are M4.5. M4.3b adds NO dataset row and makes NO 680x0 vector green.
- **The interpreter's sequencing of the write-back relative to the bus access in a real op** = M4.5 (M4.3b
  proves the write-back + the ordering in ISOLATION; M4.5 wires it into MOVE/ALU bodies and the TomHarte
  per-transaction trace gate confirms the cycle-accurate ordering).
- **The synchronous mid-instruction exception (address error on a misaligned EA) / IPL-level interrupt** =
  M4.5d (M4.3b can COMPUTE an odd EA; the address-error VECTOR is M4.5 — `BusAlignment.IsMisaligned` from
  M4.2 is the detection seam).
- **The wide-bus JIT hot-op emit** = M6.

> **The honest one-liner for M4.3b's close-state:** a structured EA descriptor `(mode3, register3)` +
> extension-word format + `writeBack` derives from the 6-bit `(mode:register)` field; an EA-compute helper
> computes each of the 14 modes' 32-bit address (reading the extension-word VALUES, which M4.3b surfaces),
> and for `(An)+`/`-(An)` performs the operand-size-magnitude register write-back with the correct
> post-vs-pre ORDERING and the `A7 ±2` special case, and supports a pure-EA (LEA/PEA, no-dereference)
> compute; the legality matrix becomes EA-category data driving the 68000 path (the 6502/Z80 per-class
> legality is byte-identical), and `RequiredIndexRegister`'s X/Y is retired for the 68000. Synthetic
> fixtures prove every mode's EA, the write-back + ordering + `A7` case, the pure-EA compute, and the
> category check. NO 68000 opcode is live, NO 680x0 vector is asserted green (M4.4/M4.5); the real op bodies
> that CALL the EA-compute + sequence the write-back relative to the bus access are M4.5. Every 6502 + Z80
> artifact is byte-identical.

---

## Ground truth — what M4.3a + the Z80 `Indexed`/EA-helper shipped (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 0** — M4.3b REUSES or EXTENDS them.

- **M4.3a's extracted tuple + the field-decode walk.** The merged M4.3a walk (`CpuEmitter.cs`
  `EmitFieldDecodeWalk`) extracts `uint eaMode = (ea>>3)&7; uint eaReg = ea&7;` + `size` and computes
  `ExtensionWordCount(eaMode, eaReg, size)` (consuming the extension words for length). **M4.3b reads the
  same extension words for their VALUES** — read `EmitFieldDecodeWalk` + `ExtensionWordCount` (M4.3a) to
  reuse the count table as the read-count. The `FieldOp.LegalEa` (`EaCategory`) tag M4.3a carries is the
  legality input M4.3b's category check consumes.
- **The Z80 `Indexed` EA-helper precedent (ADR Decision 2's reuse seam).** `CpuEmitter.cs:2165`
  `EmitZ80IndexedEa(StringBuilder sb, string indexReg, string dispExpr)` emits `ushort __ea =
  unchecked((ushort)({indexReg} + (sbyte)({dispExpr})));` — a small static appending one C# statement that
  computes a signed displacement EA into a known local. The 68000 `d16(An)`/`d8(An,Xn)`/`d16(PC)`/
  `d8(PC,Xn)` EA-compute is the SAME shape, generalized to 32-bit + a brief-index extension word. Read the
  helper + its call sites (`:2341`, `:2667`) for the "publish the EA into `__ea`, then operate" convention.
- **The legality matrix to generalize.** `SpecParser.cs:1229-1311+` `ValidateModeForClass(mode, opClass,
  firstOpKind)` — a per-class `switch` (Register/Load/Alu/Store/Rmw/Jump/Branch/Stack/Flow/Port + the Z80
  classes) returning an error string or null. `SpecParser.cs:1222-1227` `RequiredIndexRegister(mode)` —
  `"ZeroPageX"/"AbsoluteX"/"IndirectX" => "X"`, `…Y => "Y"`. **These encode 6502/Z80 rules; M4.3b adds the
  EA-category path WITHOUT changing them (D5).** Read both fully at Task 0.
- **`DecodedOperands` carries only Lo/Hi (RECON-FINDING D2).** `DecodeResult.cs:26` `DecodedOperands(byte
  Lo, byte Hi, byte Count)`; the doc says "Wider operand carriage (the 8086's full disp/imm) is M5 work —
  the shape is extensible (a fixed inline tuple)" (`:24`). M4.3b widens it (the EA-compute needs up to 4
  extension bytes per EA, and MOVE has two EAs). Read the `DecodeResult`/`DecodedOperands` consumers
  (the disassembler + the dispatch) so the widening is additive.
- **The 68000 register file + A7 banking (M4.1).** `M68000Spec.cs`: D0–D7, A0–A6, USP/SSP, PC 32-bit, SR
  16-bit; `M68000Cpu.cs:52-56` `A7` is the SR-S-bit-banked view over USP/SSP. The `(An)` modes name `A0–A6`;
  `(A7)+`/`-(A7)` name A7 (the banked SP) — hence the `A7 ±2` special case (RECON-FINDING D4). The
  EA-compute reads/writes the address registers via the generated `GetRegister`/`SetRegister` (32-bit) or a
  direct field — confirm the register-access convention at Task 0.
- **`OperandSize` → magnitude (M4.1).** `Core/Specification/OperandSize.cs` `{ Byte, Word, Long }`; the
  write-back magnitude is `.b`→1, `.w`→2, `.l`→4. The size index M4.3a packs into the key (0/1/2) maps
  directly. Read `OperandSize` + how M4.3a represented the size index.
- **The synthetic-spec test host + the EA precedent test.** `GeneratorTestHost.CompileAndLoadType`/`Run`;
  the Z80 EA proof `tests/CpuEmulator.Tests/Generators/Z80IndexedEaTests.cs` (the `EmitZ80IndexedEa`
  unit/synthetic proof shape) — M4.3b's EA-compute proof mirrors it (a synthetic CPU exposing an EA-compute
  probe, asserting the computed address + the write-back).

### RECON FINDINGS that refine the ADR's sketch (the code WINS — flagged)

> Discovered during write-time recon by reading the source. The implementer MUST re-confirm each at Task 0.

- **D1 — represent the EA as DATA derived from `(mode, register)`, NOT as 12 `AddrMode` members.** The
  `AddrMode` enum (`Core/Specification/AddrMode.cs`) is a closed enum mirrored in FOUR tables
  (`JitMode`/`s_addrModes`/`SupportedModes`/`ModeLength` — the "mirror-table smell" ADR 0001 names; the Z80
  `Indexed` slice paid it once). ADR Decision 2 Option B: add only the few `AddrMode` members the
  disassembler/assembler genuinely need (the 68000 disassembler/assembler are M4.4+, so M4.3b may add ZERO
  `AddrMode` members — the EA is pure descriptor data consumed by the EA-compute helper). **Decision
  (recorded): M4.3b adds NO `AddrMode` enum member** — the EA is `(mode, register)` data the field-decode
  walk extracted, and the EA-compute is a helper keyed on `(eaMode, eaReg, size)`, not on an `AddrMode`.
  This sidesteps the four-table mirror tax entirely. If Task 0 finds the disassembler/assembler path REQUIRES
  an `AddrMode` for a 68000 EA to render (it should not — disassembly is M4.4), flag it; default to no new
  enum member.
- **D2 — surface the extension-word VALUES (M4.3a deferred this).** M4.3a's `EmitFieldDecodeWalk` consumes
  the extension words for LENGTH but returns `DecodedOperands.None` (it does not surface their bytes —
  RECON-FINDING C3 in M4.3a). M4.3b needs the VALUES (the d16 displacement, the abs.w/abs.l address, the
  #imm value, the brief-index word). **Widen the operand carriage:** add an extension-word buffer to
  `DecodeResult` (e.g. `DecodeResult(key, length, operands, ExtensionWords)` where `ExtensionWords` is a
  small fixed-capacity inline buffer of up to ~4 words, enough for MOVE's two EAs at `.l`), and make
  `EmitFieldDecodeWalk` populate it as it consumes each extension word. This is additive (the 6502/Z80
  walks return an empty extension-word buffer; their `DecodeResult` consumers ignore it). **Confirm the
  exact widening shape against the `DecodeResult` consumers at Task 0** (the disassembler + dispatch read
  `DecodeResult` — the widening must not break them).
- **D3 — the post-increment vs pre-decrement ORDERING is the TomHarte-caught bug class.** `(An)+`: the EA
  is the CURRENT `An`, THEN `An += size` (read the register, then increment). `-(An)`: `An -= size` FIRST,
  THEN the EA is the new `An` (decrement, then read). Getting the order wrong (incrementing before reading,
  or reading before decrementing) is wrong by one access. ADR Decision 2 Consequences names this explicitly.
  **The EA-compute helper must encode the order in the emitted code** — for PostInc: capture `ea = An;
  An = An + size;`; for PreDec: `An = An - size; ea = An;`. The synthetic proof asserts BOTH the EA value
  AND the resulting `An` for each.
- **D4 — the `A7 ±2` special case.** `(A7)+`/`-(A7)` always move by 2 (even for `.b`) to keep the stack
  word-aligned. The EA-compute's write-back magnitude is `size == .b && register == 7 ? 2 : size-magnitude`.
  Because A7 is the SR-S-bit-banked view (M68000Cpu.cs:52), the write-back must go through the A7
  banking (write A7, which routes to USP/SSP by the S-bit) — confirm the EA-compute writes the address
  register by NAME (`A7`/the banked accessor) so the banking is honored, not a raw USP/SSP field. The
  synthetic proof asserts `(A7)+.b` moves A7 by 2, not 1.
- **D5 — the 6502/Z80 legality must come out BYTE-IDENTICAL.** `ValidateModeForClass` +
  `RequiredIndexRegister` gate the 6502/Z80 rows at PARSE time (they reject illegal class/mode pairings).
  M4.3b adds the EA-category path for the 68000 ADDITIVELY: a `FieldGrammar` CPU's EA legality is checked
  by the EA-category (the `EaCategory` tag), NOT by `ValidateModeForClass` (which has no 68000 classes). The
  6502/Z80 `ValidateModeForClass`/`RequiredIndexRegister` arms are UNCHANGED, so `RegeneratedSpecTests` stays
  byte-identical. **Do NOT refactor the 6502/Z80 legality into the EA-category model** — that would risk a
  byte-diff; the ADR's "the legality matrix BECOMES EA-category data" is the 68000's matrix, added
  alongside, not a rewrite of the 6502's. Confirm at Task 0 that the EA-category check is reachable only on
  the field-grammar path.
- **D6 — the EA-compute is an EMITTED helper, proven in isolation; M4.5 sequences it in op bodies.** Like
  `EmitZ80IndexedEa`, the EA-compute is a generator helper that emits C# computing the EA (+ the write-back)
  into known locals. M4.3b proves it via a synthetic probe (a tiny op body, or a directly-callable emitted
  method, that calls the helper and exposes `__ea` + the post-state). M4.3b does NOT wire it into a real
  68000 op (there are none until M4.5). The cycle-accurate SEQUENCING (the write-back relative to the bus
  read in a real MOVE) is M4.5 + the TomHarte per-transaction trace. Record this altitude: M4.3b = the
  helper + its isolated correctness; M4.5 = the helper called from real op bodies under the vector gate.

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Core/Jit/DecodeResult.cs` | Modify | Widen the operand carriage to surface the extension-word VALUES (an inline extension-word buffer; D2). |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | `EmitFieldDecodeWalk` populates the extension-word buffer (D2); add the `EmitM68kEa` EA-compute helper (address + write-back + ordering + A7 case + pure-EA flag); the EA-descriptor derivation from `(mode, register)`. |
| `src/CpuEmulator.Generators/SpecParser.cs` | Modify | Add the EA-category legality check on the field-grammar path (additive — the 6502/Z80 `ValidateModeForClass`/`RequiredIndexRegister` arms unchanged, D5). |
| `tests/CpuEmulator.Tests/Generators/M68kEaComputeTests.cs` | Create | The synthetic EA-compute proof: each mode's EA; `(An)+`/`-(An)` write-back by size; the A7 ±2 case; the post-vs-pre ordering; the pure-EA (LEA) compute. |
| `tests/CpuEmulator.Tests/Generators/M68kExtensionWordValueTests.cs` | Create | The field-decode walk surfaces the extension-word VALUES (D2) — d16/abs.w/abs.l/#imm read correctly from a big-endian stream. |
| `tests/CpuEmulator.Tests/Generators/M68kEaLegalityTests.cs` | Create | The EA-category check accepts/rejects modes per category (data-alterable / control / …); the 6502/Z80 legality is unaffected. |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502/Z80
> byte-identity guard `RegeneratedSpecTests` + the whole Z80 + 6502 suites green), then commit. Tasks are
> dependency-ordered so the suite builds and stays green after every task. Literal code is given for every
> load-bearing piece. The synthetic-spec tests (via `GeneratorTestHost`) decouple from the real
> `M68000Spec.cs` — M4.3b adds NO real 68000 op. Synthetic structured fixtures follow the M3.4d shape
> (`IAddressSpace _bus` + the inert policy hooks) and reuse M4.3a's `FieldGrammar`/`FetchUnit.Word` +
> big-endian `BufferFetchStream`.

### Task 0: Baseline + EA-seam recon (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch (off the M4.3a merge).** Create the branch off the current main (which now includes
  M4.3a):
  Run: `git switch -c feat/m4-3b-ea-descriptor-and-compute`
  Expected: on the new branch; `git log` shows M4.3a merged (the `FieldGrammar`/`FieldOp` carrier, the
  `EmitFieldDecodeWalk` arm, the big-endian `BufferFetchStream`). **CONFIRM M4.3a is present:** grep
  `FieldGrammar` in `DecodeStructure.cs` + `EmitFieldDecodeWalk`/`ExtensionWordCount` in `CpuEmitter.cs`.

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test`
  Expected: 0 failures, 0 unexpected skips. Record the EXACT count (the closeout pins it).
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean.

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each cited surface holds:**
  - M4.3a's `EmitFieldDecodeWalk` + `ExtensionWordCount` (`CpuEmitter.cs` — the merged M4.3a arm): how it
    extracts `eaMode`/`eaReg`/`size`, computes `ExtensionWordCount`, consumes-for-length, returns
    `DecodedOperands.None` (RECON-FINDING D2 — M4.3b surfaces the values). The `FieldOp.LegalEa`/
    `EaCategory` tag flow (the legality input).
  - `CpuEmitter.cs:2165` `EmitZ80IndexedEa` + its call sites (`:2341`, `:2667`) — the EA-helper shape
    (publish `__ea`, then operate) the 68000 EA-compute generalizes (D6).
  - `SpecParser.cs:1222-1227` `RequiredIndexRegister`, `:1229-1311+` `ValidateModeForClass` — the per-class
    legality `switch` (the 6502/Z80 arms M4.3b leaves UNCHANGED, D5). Confirm both gate at parse time and
    have no 68000 class.
  - `DecodeResult.cs:13-29` (`DecodeResult` + `DecodedOperands` Lo/Hi/Count) + its consumers (grep
    `DecodeResult`/`.Operands` in `CpuEmitter.cs` — the disassembler + dispatch) so the D2 widening is
    additive.
  - `M68000Cpu.cs:47-56` (`A7` banked over USP/SSP by the SR S-bit — the D4 write-back must honor it) +
    `M68000Spec.cs` (the address-register names A0–A6 + A7-as-SP) + `OperandSize.cs` (the b/w/l → 1/2/4
    magnitudes).
  - `tests/CpuEmulator.Tests/Generators/Z80IndexedEaTests.cs` (the `EmitZ80IndexedEa` proof shape M4.3b's
    EA-compute proof mirrors) + `GeneratorTestHost.cs`.

- [ ] **Step 4:** No commit (read-only). Proceed to Task 1.

---

### Task 1: Surface the extension-word VALUES from the field-decode walk (TDD) (D2)

> Widen the operand carriage so the field-decode walk surfaces the extension-word VALUES (M4.3a consumed
> them for length only). The EA-compute (Task 2) reads d16/abs.w/abs.l/#imm from this buffer. Additive: the
> 6502/Z80 walks return an empty extension-word buffer; their `DecodeResult` consumers ignore it.

**Files:**
- Modify: `src/CpuEmulator.Core/Jit/DecodeResult.cs`
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitFieldDecodeWalk` populates the buffer)
- Test: `tests/CpuEmulator.Tests/Generators/M68kExtensionWordValueTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/M68kExtensionWordValueTests.cs`: feed a big-endian operword + its
  extension words; assert the `DecodeResult` surfaces the extension-word values. Reuse M4.3a's synthetic
  `FgwCpu`/`FgwSpec` shape (the single-op ADD field grammar).

```csharp
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kExtensionWordValueTests
{
    // Reuse the M4.3a single-op ADD field grammar (mask 0xF100/match 0xD000, size 7-6, EA 5-0).
    private const string Source = M68kEaTestSpecs.AddGrammarCpu;   // shared synthetic spec (see note)

    [Fact]
    public void Abs_w_surfaces_its_one_extension_word()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // ADD.w <abs.w> : operword 0xD0 78 (size .w=01, EA mode 7 reg 0 = abs.w), ext word 0x1234.
        var buf = new byte[] { 0xD0, 0x78, 0x12, 0x34 };
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        dynamic r = t.GetMethod("Decode")!.Invoke(null, new object[] { stream })!;
        Assert.Equal(4, (int)r.Length);                       // operword + 1 ext word
        Assert.Equal(1, (int)r.ExtensionWords.Count);
        Assert.Equal(0x1234u, (uint)r.ExtensionWords[0]);     // the abs.w extension word, big-endian
    }

    [Fact]
    public void Abs_l_surfaces_two_extension_words()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // ADD.w <abs.l> : operword 0xD0 79 (EA mode 7 reg 1 = abs.l), ext words 0x1234 0x5678.
        var buf = new byte[] { 0xD0, 0x79, 0x12, 0x34, 0x56, 0x78 };
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        dynamic r = t.GetMethod("Decode")!.Invoke(null, new object[] { stream })!;
        Assert.Equal(6, (int)r.Length);
        Assert.Equal(2, (int)r.ExtensionWords.Count);
        Assert.Equal(0x1234u, (uint)r.ExtensionWords[0]);
        Assert.Equal(0x5678u, (uint)r.ExtensionWords[1]);
    }
}
```

  > **Shared synthetic spec.** Factor the M4.3a `FgwSpec`/`FgwCpu` source string into a small test helper
  > (`M68kEaTestSpecs.AddGrammarCpu`) so Tasks 1–3 share it (the EA-compute proof needs the same grammar +
  > address registers A0–A7). Add the address registers A0–A7 to the spec's `Registers` so the EA-compute
  > can name them (M4.3a's `FgwSpec` had only D0/A0 — extend it to A0–A7 + D0–D7 for the EA tests). Confirm
  > the `ExtensionWords` accessor shape (a `ReadOnlySpan<ushort>` / a fixed inline `(ushort,ushort,…)` +
  > `Count`) against the Task-1 widening you choose; the assertion is "the values are surfaced big-endian
  > in order."

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kExtensionWordValueTests"`
  Expected: FAIL — `DecodeResult` has no `ExtensionWords`; M4.3a returned `DecodedOperands.None`.

- [ ] **Step 3: Widen `DecodeResult` + populate the buffer.**
  - In `src/CpuEmulator.Core/Jit/DecodeResult.cs`, add an extension-word buffer (a fixed-capacity inline
    struct — no allocation in the hot loop, mirroring the `DecodedOperands` convention). Up to 4 words
    covers MOVE's two EAs at `.l` (2 + 2):

```csharp
public readonly record struct DecodeResult(
    uint OperationKey,
    int Length,
    DecodedOperands Operands,
    ExtensionWords ExtensionWords = default);   // M4.3b: the 68000 EA extension words (empty for 6502/Z80)

/// <summary>The 68000 EA extension words the field-decode walk consumed (M4.3b). A fixed inline buffer of
/// up to 4 16-bit words (MOVE's two EAs at .l = 2 + 2). Empty (Count == 0) for the 6502/Z80 byte walks.
/// The EA-compute (CpuEmitter EmitM68kEa) reads d16/abs.w/abs.l/#imm/brief-index from here.</summary>
public readonly record struct ExtensionWords(ushort W0, ushort W1, ushort W2, ushort W3, int Count)
{
    public static readonly ExtensionWords None = default;
    public ushort this[int i] => i switch { 0 => W0, 1 => W1, 2 => W2, 3 => W3, _ => 0 };
}
```

  - In `src/CpuEmulator.Generators/CpuEmitter.cs` `EmitFieldDecodeWalk` (M4.3a's arm), CAPTURE each
    extension word as it is consumed (M4.3a discarded them). Replace the `for (int w = 0; w < extWords;
    w++) stream.NextUnit();` consume-and-discard with a capture into locals + build the `ExtensionWords`:

```csharp
        sb.AppendLine("            ushort e0 = 0, e1 = 0, e2 = 0, e3 = 0;");
        sb.AppendLine("            int extWords = ExtensionWordCount(eaMode, eaReg, size);");
        sb.AppendLine("            for (int w = 0; w < extWords; w++)");
        sb.AppendLine("            {");
        sb.AppendLine("                ushort ew = (ushort)stream.NextUnit();   // big-endian word (the stream composes BE)");
        sb.AppendLine("                switch (w) { case 0: e0 = ew; break; case 1: e1 = ew; break; case 2: e2 = ew; break; default: e3 = ew; break; }");
        sb.AppendLine("            }");
        sb.AppendLine("            int len = stream.UnitsConsumed * stream.UnitBytes;");
        sb.AppendLine("            var ext = new CpuEmulator.Core.Jit.ExtensionWords(e0, e1, e2, e3, extWords);");
        sb.AppendLine("            return new CpuEmulator.Core.Jit.DecodeResult(key, len, CpuEmulator.Core.Jit.DecodedOperands.None, ext);");
```

  > **Additivity (D5/D2).** `ExtensionWords` defaults to `None` on the `DecodeResult` ctor, so the 6502/Z80
  > byte walks (which never construct an `ExtensionWords`) return the 4-arg `DecodeResult(key, len, ops)`
  > unchanged — the default param keeps their call sites + their `.g.cs` byte-identical. Confirm the
  > `DecodeResult` consumers (disassembler + dispatch) compile against the widened record (they ignore the
  > new field). The big-endian capture relies on the stream composing BE (M4.3a's `BufferFetchStream
  > bigEndian:true`); the live big-endian `AddressSpaceFetchStream` is M4.5.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kExtensionWordValueTests"`
  Expected: PASS — abs.w surfaces 1 word, abs.l surfaces 2, big-endian, in order.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (the widening is additive via the default param).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502/Z80 byte-identical —
  the byte walk's `DecodeResult` construction is unchanged; the default `ExtensionWords.None` is invisible).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Core/Jit/DecodeResult.cs src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/M68kExtensionWordValueTests.cs \
        tests/CpuEmulator.Tests/Generators/M68kEaTestSpecs.cs
git commit -m "$(cat <<'EOF'
feat(core): surface the 68000 EA extension-word values from the field-decode walk (D2)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 2: The `EmitM68kEa` EA-compute helper — address + `(An)+`/`-(An)` write-back + A7 case + ordering + pure-EA (TDD)

> Add the EA-compute helper (the new EA capability): given `(eaMode, eaReg, size)` + the extension words,
> compute the 32-bit EA for each of the 14 modes (reusing the `EmitZ80IndexedEa` displacement/index shape),
> and for `(An)+`/`-(An)` perform the operand-size-magnitude register write-back with the correct ordering
> (D3) + the A7 ±2 case (D4), plus a pure-EA (LEA/PEA, no-dereference) flag. Proven by a synthetic EA-compute
> probe asserting each mode's EA + the write-back + the post-state.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitM68kEa` helper + a synthetic EA-compute probe arm)
- Test: `tests/CpuEmulator.Tests/Generators/M68kEaComputeTests.cs` (create)

> **Design decision (recorded, per D6).** The EA-compute is an EMITTED helper (the `EmitZ80IndexedEa`
> shape) appending C# that computes the EA into `__ea` and (for PostInc/PreDec) mutates the address
> register, taking `(eaMode, eaReg, size, extWords, pureEa)` as inputs. M4.3b proves it via a synthetic
> EA-compute PROBE — the cleanest vehicle that does not need a live op is a generated, directly-callable
> method on the synthetic CPU (e.g. an emitted `public uint ComputeEaProbe(uint eaMode, uint eaReg, uint
> size, ExtensionWords ext, bool pureEa)` that calls the helper's logic), so the test can drive each mode +
> read back `__ea` AND the mutated `An`. **Confirm at Task 0** how `EmitZ80IndexedEa` is proven (unit test
> of the emitted string vs a synthetic probe op) and mirror the cleanest vehicle; the load-bearing
> assertions are the EA value + the write-back + the ordering + the A7 case, invariant to the vehicle.

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/M68kEaComputeTests.cs`. Drive the EA-compute probe per mode; assert
  the EA + (for write-back modes) the resulting `An`. Use the shared `M68kEaTestSpecs.AddGrammarCpu` (now
  with A0–A7 + an emitted `ComputeEaProbe`).

```csharp
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kEaComputeTests
{
    private const string Source = M68kEaTestSpecs.EaProbeCpu;   // the grammar CPU + an emitted ComputeEaProbe

    private static (object Cpu, System.Type T) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        var bus = new CpuEmulator.Core.AddressSpace(
            CpuEmulator.Core.AddressSpaceKind.Program, addressBits: 24);   // confirm the M4.2 ctor shape
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t);
    }
    private static void SetReg(object c, System.Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(c, new object[] { r, v });
    private static ulong GetReg(object c, System.Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(c, new object[] { r })!;
    private static uint Ea(object c, System.Type t, uint mode, uint reg, uint size,
                           CpuEmulator.Core.Jit.ExtensionWords ext, bool pureEa = false) =>
        (uint)t.GetMethod("ComputeEaProbe")!.Invoke(c, new object[] { mode, reg, size, ext, pureEa })!;

    [Fact]
    public void Address_register_indirect_uses_An()
    {
        var (c, t) = Build();
        SetReg(c, t, "A3", 0x00102000);
        Assert.Equal(0x00102000u, Ea(c, t, mode: 2, reg: 3, size: 1, default));   // (A3)
    }

    [Fact]
    public void Displacement_d16_An_adds_signed_displacement()
    {
        var (c, t) = Build();
        SetReg(c, t, "A2", 0x00001000);
        var ext = new CpuEmulator.Core.Jit.ExtensionWords(0xFFFE, 0, 0, 0, 1);    // d16 = -2 (signed)
        Assert.Equal(0x00000FFEu, Ea(c, t, mode: 5, reg: 2, size: 1, ext));        // d16(A2) = A2 - 2
    }

    [Fact]
    public void Abs_l_uses_the_two_extension_words()
    {
        var (c, t) = Build();
        var ext = new CpuEmulator.Core.Jit.ExtensionWords(0x0012, 0x3456, 0, 0, 2);
        Assert.Equal(0x00123456u, Ea(c, t, mode: 7, reg: 1, size: 1, ext));        // abs.l
    }

    [Fact]
    public void PostIncrement_reads_An_then_adds_by_size()
    {
        var (c, t) = Build();
        SetReg(c, t, "A1", 0x00002000);
        uint ea = Ea(c, t, mode: 3, reg: 1, size: 2, default);   // (A1)+ at .l
        Assert.Equal(0x00002000u, ea);                            // the EA is the CURRENT A1 (D3 ordering)
        Assert.Equal(0x00002004u, (uint)GetReg(c, t, "A1"));      // A1 += 4 (.l magnitude)
    }

    [Fact]
    public void PreDecrement_subtracts_by_size_then_reads_An()
    {
        var (c, t) = Build();
        SetReg(c, t, "A4", 0x00002000);
        uint ea = Ea(c, t, mode: 4, reg: 4, size: 1, default);   // -(A4) at .w
        Assert.Equal(0x00001FFEu, ea);                            // A4 -= 2 FIRST, then the EA is the new A4
        Assert.Equal(0x00001FFEu, (uint)GetReg(c, t, "A4"));      // A4 == new value (D3 ordering)
    }

    [Fact]
    public void A7_postincrement_byte_moves_by_two()   // D4: the stack stays word-aligned
    {
        var (c, t) = Build();
        SetReg(c, t, "A7", 0x00003000);
        uint ea = Ea(c, t, mode: 3, reg: 7, size: 0, default);   // (A7)+ at .b
        Assert.Equal(0x00003000u, ea);
        Assert.Equal(0x00003002u, (uint)GetReg(c, t, "A7"));      // +2 even for .b (NOT +1)
    }

    [Fact]
    public void Pure_ea_compute_does_not_mutate_for_postinc()   // LEA/PEA: compute, no write-back
    {
        var (c, t) = Build();
        SetReg(c, t, "A0", 0x00004000);
        uint ea = Ea(c, t, mode: 3, reg: 0, size: 2, default, pureEa: true);   // LEA (A0)+ is illegal in HW,
        // but the pure-EA path proves "compute the address, do NOT perform the side effect" for LEA/PEA on
        // the legal control modes; here we assert the pure-EA flag suppresses the write-back.
        Assert.Equal(0x00004000u, ea);
        Assert.Equal(0x00004000u, (uint)GetReg(c, t, "A0"));      // unchanged — pure-EA suppresses write-back
    }
}
```

  > **Confirm `GetRegister`/`SetRegister` route to A7 banking.** A7 is the SR-S-bit-banked view; the
  > probe's `SetReg("A7", …)`/`GetReg("A7")` must reach the banked accessor (so the test sets the bank the
  > EA-compute writes). Read `M68000Cpu.cs:52` + the generated `GetRegister`/`SetRegister` (does it know
  > `A7`? A7 is NOT a spec register — it is a hand-written view). **If `GetRegister("A7")` is not wired**
  > (A7 is not a spec register, M68000Spec.cs:13), the synthetic spec must declare an `A7` register OR the
  > test reads the bank directly (USP/SSP via the S-bit). Decide at Task 0: simplest is to have the
  > synthetic `FgwSpec` declare `A7` as a plain 32-bit register (it is synthetic — it need not model the
  > banking; the real A7 banking is M68000Cpu.cs's, exercised in M4.5). Adjust the probe accordingly; the
  > load-bearing assertion (`+2 for .b on register 7`) holds regardless of where A7 lives.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kEaComputeTests"`
  Expected: FAIL — `ComputeEaProbe`/`EmitM68kEa` does not exist.

- [ ] **Step 3: Add the `EmitM68kEa` helper + the probe.** In `src/CpuEmulator.Generators/CpuEmitter.cs`,
  add the EA-compute helper near `EmitZ80IndexedEa` (the reuse seam). It emits C# computing `__ea` (+ the
  write-back) given the inputs. The literal emitted body (per the 14 modes + the write-back + D3/D4):

```csharp
    /// <summary>M4.3b (ADR 0004 Decision 2): emit the 68000 EA-compute. Given eaMode (bits 5-3), eaReg
    /// (2-0), the size index (0/1/2 = b/w/l), the extension words, and a pureEa flag (LEA/PEA — compute the
    /// address, do NOT perform the (An)+/-(An) write-back), compute the 32-bit EA into __ea. For (An)+
    /// (mode 3) and -(An) (mode 4) mutate the address register by the size magnitude (.b→1/.w→2/.l→4) with
    /// the A7 ±2 special case (register 7 moves by 2 even for .b — D4), in the correct order: PostInc reads
    /// An THEN adds; PreDec subtracts THEN reads (D3). Reuses the EmitZ80IndexedEa displacement shape for
    /// d16(An)/d8(An,Xn)/d16(PC)/d8(PC,Xn). Address registers are read/written via Areg(reg)
    /// (GetRegister/SetRegister or the direct An field — confirm the convention at Task 0).</summary>
    internal static void EmitM68kEa(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Compute the 68000 effective address (M4.3b, ADR 0004 Decision 2).</summary>");
        sb.AppendLine("    private uint ComputeEa(uint eaMode, uint eaReg, uint size, CpuEmulator.Core.Jit.ExtensionWords ext, bool pureEa)");
        sb.AppendLine("    {");
        sb.AppendLine("        uint mag = size == 0u ? 1u : size == 1u ? 2u : 4u;            // .b/.w/.l magnitude");
        sb.AppendLine("        if (eaReg == 7u && eaMode is 3u or 4u && mag == 1u) mag = 2u; // A7 ±2 (stack word-align — D4)");
        sb.AppendLine("        switch (eaMode)");
        sb.AppendLine("        {");
        sb.AppendLine("            case 0u: return ADataIndexProbe(eaReg);                    // Dn — register direct (no memory EA; probe stub)");
        sb.AppendLine("            case 1u: return Areg(eaReg);                               // An — register direct");
        sb.AppendLine("            case 2u: return Areg(eaReg);                               // (An)");
        sb.AppendLine("            case 3u:                                                   // (An)+ : read An, THEN An += mag (D3)");
        sb.AppendLine("            {");
        sb.AppendLine("                uint ea = Areg(eaReg);");
        sb.AppendLine("                if (!pureEa) SetAreg(eaReg, ea + mag);");
        sb.AppendLine("                return ea;");
        sb.AppendLine("            }");
        sb.AppendLine("            case 4u:                                                   // -(An) : An -= mag FIRST, THEN read (D3)");
        sb.AppendLine("            {");
        sb.AppendLine("                uint ea = Areg(eaReg) - mag;");
        sb.AppendLine("                if (!pureEa) SetAreg(eaReg, ea);");
        sb.AppendLine("                return ea;");
        sb.AppendLine("            }");
        sb.AppendLine("            case 5u: return unchecked(Areg(eaReg) + (uint)(short)ext[0]);   // d16(An) — signed 16-bit disp");
        sb.AppendLine("            case 6u: return ComputeBriefIndex(Areg(eaReg), ext[0]);    // d8(An,Xn) — brief extension word");
        sb.AppendLine("            case 7u: return eaReg switch                               // mode 7 — register selects the form");
        sb.AppendLine("            {");
        sb.AppendLine("                0u => (uint)(short)ext[0],                             // abs.w — sign-extended 16-bit");
        sb.AppendLine("                1u => ((uint)ext[0] << 16) | ext[1],                   // abs.l — two words, high first");
        sb.AppendLine("                2u => unchecked(PcForEa + (uint)(short)ext[0]),        // d16(PC)");
        sb.AppendLine("                3u => ComputeBriefIndex(PcForEa, ext[0]),             // d8(PC,Xn)");
        sb.AppendLine("                4u => 0u,                                              // #imm — no address (the value is the ext words)");
        sb.AppendLine("                _ => 0u,                                               // illegal mode-7 reg (M4.5 vectors)");
        sb.AppendLine("            },");
        sb.AppendLine("            _ => 0u,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        // Areg/SetAreg/ComputeBriefIndex/PcForEa/ADataIndexProbe are small generated helpers (below) or
        // hand-written on the synthetic partial — confirm the address-register access convention at Task 0.
    }
```

  > **The address-register accessor (`Areg`/`SetAreg`).** Emit small helpers that read/write An by index
  > (mapping reg 0–7 → A0–A7, routing A7 through the banked accessor for the real 68000; for the synthetic
  > spec A7 is a plain register). The `ComputeBriefIndex(base, ext)` helper decodes the brief extension word
  > (the index register selector + the 8-bit displacement — the index register is read from the extension
  > word, NOT a fixed `X`/`Y`, retiring that convention — D5/ADR Decision 2); M4.3b's synthetic proof can
  > use a minimal brief-index (displacement-only, index = 0) and flag the full index-register decode as an
  > M4.5 detail if it does not fit cleanly. `PcForEa` is the PC the PC-relative EA is computed from
  > (the extension-word address) — the synthetic probe supplies it; the real value is M4.5's. The probe
  > `ComputeEaProbe` is a thin `public` wrapper over `ComputeEa` (added to the synthetic partial so the test
  > can call it).
  >
  > **Wire `EmitM68kEa(sb)` into the field-grammar emit path** (call it from `EmitFieldDecodeWalk`'s class
  > or the partial emit) so a `FieldGrammar` CPU gets `ComputeEa`. Guard it to the field-grammar path so the
  > 6502/Z80 never get it (byte-identity, D5).

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kEaComputeTests"`
  Expected: PASS — each mode's EA; PostInc reads-then-adds; PreDec subtracts-then-reads; A7.b moves by 2;
  pure-EA suppresses the write-back.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (the EA-compute is emitted only on the field-grammar path).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502/Z80 byte-identical —
  they declare no `FieldGrammar`, so `ComputeEa` is not emitted for them).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/M68kEaComputeTests.cs \
        tests/CpuEmulator.Tests/Generators/M68kEaTestSpecs.cs
git commit -m "$(cat <<'EOF'
feat(generators): add the 68000 EA-compute (14 modes + (An)+/-(An) write-back + A7 case + ordering) (D2/D3/D4)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~7.

---

### Task 3: The EA-category legality matrix (the 68000 path; the 6502/Z80 unchanged) (TDD) (D5)

> Add an EA-category legality check the 68000 field grammar drives via the `EaCategory` tag M4.3a carries on
> each `FieldOp` — the classic 68000 buckets (data-alterable / memory-alterable / control / alterable …)
> map to the legal `(mode, register)` set. Additive: the 6502/Z80 `ValidateModeForClass`/
> `RequiredIndexRegister` are UNCHANGED (byte-identity, D5); the EA-category path is reachable only on a
> `FieldGrammar` CPU. Retire the `X`/`Y` index convention for the 68000 (the index register is read from the
> brief extension word).

**Files:**
- Modify: `src/CpuEmulator.Generators/SpecParser.cs` (the EA-category check on the field-grammar path)
- Test: `tests/CpuEmulator.Tests/Generators/M68kEaLegalityTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create `tests/CpuEmulator.Tests/Generators/M68kEaLegalityTests.cs`.
  The EA-category check is a pure function (mode, reg) × category → legal? — proven directly (it is the
  68000's legality data, reachable from the test assembly via `InternalsVisibleTo` — confirm at Task 0; if
  not, prove it via a synthetic grammar declaring an op with a restrictive category + asserting a generator
  diagnostic for an illegal EA, mirroring the `ValidateModeForClass` rejection shape).

```csharp
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kEaLegalityTests
{
    // The EA-category check: (mode, reg, category) → legal? The 68000 buckets:
    //  DataAlterable    = Dn, (An), (An)+, -(An), d16(An), d8(An,Xn), abs.w, abs.l         (NOT An, NOT PC, NOT #imm)
    //  Control          = (An), d16(An), d8(An,Xn), abs.w, abs.l, d16(PC), d8(PC,Xn)       (NOT Dn/An/(An)+/-(An)/#imm)
    //  All (data)       = every mode incl. #imm and PC-relative
    [Theory]
    [InlineData(0, 0, "DataAlterable", true)]    // Dn — legal data-alterable
    [InlineData(1, 0, "DataAlterable", false)]   // An — NOT data-alterable
    [InlineData(7, 4, "DataAlterable", false)]   // #imm — NOT alterable
    [InlineData(7, 2, "Control", true)]          // d16(PC) — legal control
    [InlineData(3, 0, "Control", false)]         // (An)+ — NOT control
    [InlineData(7, 4, "All", true)]              // #imm — legal as a plain data source
    public void Ea_category_accepts_and_rejects_modes(int mode, int reg, string category, bool legal)
    {
        Assert.Equal(legal, CpuEmulator.Generators.M68kEaLegality.IsLegal((uint)mode, (uint)reg, category));
    }
}
```

  > **Confirm the test vehicle at Task 0.** If `InternalsVisibleTo` reaches the test project (the generator
  > unit-tests `EmitZ80IndexedEa`-style helpers directly — confirm), expose the check as
  > `internal static bool M68kEaLegality.IsLegal(uint mode, uint reg, string category)` and unit-test it.
  > If not, prove it through a synthetic grammar: declare a `FieldOp` with `LegalEa: EaCategory.Control` and
  > a (hypothetical) row using a `Dn` EA, and assert a generator diagnostic — but since M4.3b has NO rows
  > (the grammar declares ops, not rows), the cleanest proof is the direct `IsLegal` unit test. The
  > load-bearing content is the category → legal-mode-set mapping; pick the vehicle the codebase's helper
  > tests use.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kEaLegalityTests"`
  Expected: FAIL — `M68kEaLegality.IsLegal` does not exist.

- [ ] **Step 3: Add the EA-category check (additive — the 6502/Z80 unchanged).** In
  `src/CpuEmulator.Generators/SpecParser.cs`, add the EA-category legality helper (the 68000 buckets) +
  invoke it ONLY on the field-grammar path (a `FieldGrammar` CPU's `FieldOp.LegalEa` gates its EAs):

```csharp
    /// <summary>M4.3b (ADR 0004 Decision 2): the 68000 EA-category legality matrix — EA-category DATA
    /// replacing the per-class switch (for the 68000 only; the 6502/Z80 ValidateModeForClass is unchanged,
    /// D5). Maps a (mode, reg) EA to whether it is legal in the named category. The index register is read
    /// from the brief extension word, NOT a fixed X/Y (RequiredIndexRegister's 6502 convention is retired
    /// for the 68000).</summary>
    internal static class M68kEaLegality
    {
        // Mode 7's register sub-field selects abs.w(0)/abs.l(1)/d16(PC)(2)/d8(PC,Xn)(3)/#imm(4).
        private static bool IsData(uint mode, uint reg)        // any addressing mode (incl. #imm, PC)
            => mode <= 6u || (mode == 7u && reg <= 4u);
        private static bool IsMemory(uint mode, uint reg)      // excludes Dn, An
            => (mode >= 2u && mode <= 6u) || (mode == 7u && reg <= 4u);
        private static bool IsControl(uint mode, uint reg)     // (An), d16/d8(An), abs, d16/d8(PC) — no Dn/An/(An)+/-(An)/#imm
            => mode == 2u || (mode >= 5u && mode <= 6u) || (mode == 7u && reg <= 3u);
        private static bool IsAlterable(uint mode, uint reg)   // not PC-relative, not #imm
            => mode <= 6u || (mode == 7u && reg <= 1u);

        public static bool IsLegal(uint mode, uint reg, string category) => category switch
        {
            "DataAddressing" or "All"  => IsData(mode, reg),
            "MemoryAlterable"          => IsMemory(mode, reg) && IsAlterable(mode, reg),
            "DataAlterable"            => IsData(mode, reg) && IsAlterable(mode, reg),
            "Control"                  => IsControl(mode, reg),
            "Alterable"                => IsAlterable(mode, reg),
            _ => true,
        };
    }
```

  > **Reachability (D5).** Wire the EA-category check ONLY into the field-grammar parse path (a `FieldGrammar`
  > CPU validating its `FieldOp.LegalEa` against the EA modes it admits — or, since M4.3b has no rows, the
  > check is the DATA the M4.4 dataset/M4.5 interpreter will consume; M4.3b SHIPS + PROVES the data + the
  > function, and does NOT call it from `ValidateModeForClass`). The 6502/Z80 `ValidateModeForClass`/
  > `RequiredIndexRegister` arms are untouched. Confirm `RegeneratedSpecTests` stays byte-identical.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kEaLegalityTests"`
  Expected: PASS — each category accepts/rejects the right `(mode, reg)`.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502/Z80 byte-identical —
  the EA-category check is additive + reachable only on the field-grammar path; `ValidateModeForClass`/
  `RequiredIndexRegister` are unchanged).

- [ ] **Step 6: Commit (with the doc).**

```bash
git add src/CpuEmulator.Generators/SpecParser.cs \
        tests/CpuEmulator.Tests/Generators/M68kEaLegalityTests.cs \
        docs/superpowers/plans/2026-06-15-m4-3b-ea-descriptor-and-compute.md
git commit -m "$(cat <<'EOF'
feat(generators): add the 68000 EA-category legality matrix (additive; 6502/Z80 unchanged; retire X/Y) (D5)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

  > **The doc add to this commit** carries the filled closeout below (the docs-per-PR convention).

**New-test estimate:** ~6 (the Theory rows).

---

### Task 4: PR

- [ ] **Step 1: Push + open the PR.**
  Run: `git push -u origin feat/m4-3b-ea-descriptor-and-compute` (after the user approves; merge via PR per
  CLAUDE.md). Open a PR targeting `main`. The PR body claims EXACTLY: a structured EA descriptor
  `(mode3, register3)` + extension-word format + `writeBack` derives from the 6-bit `(mode:register)` field
  the M4.3a walk extracted; the field-decode walk now SURFACES the extension-word VALUES (M4.3a consumed
  them for length only); an `EmitM68kEa` EA-compute helper computes each of the 14 modes' 32-bit address
  (reusing the Z80 `EmitZ80IndexedEa` displacement shape) and, for `(An)+`/`-(An)`, performs the
  operand-size-magnitude register write-back with the correct post-vs-pre ORDERING and the `A7 ±2` special
  case, and supports a pure-EA (LEA/PEA, no-dereference) compute; the EA-category legality matrix is
  EA-category DATA driving the 68000 path (the 6502/Z80 per-class legality is byte-identical; the `X`/`Y`
  index convention is retired for the 68000); synthetic fixtures prove every mode's EA, the write-back +
  ordering + A7 case, the pure-EA compute, and the category check; the 6502/Z80 are byte-identical. Name
  what is STILL deferred: the field-pattern dataset + the real `M68000Spec.cs` decode + the mnemonic-keyed
  gzip TomHarte loader = M4.4; the interpreter (which CALLS the EA-compute from real op bodies + sequences
  the write-back relative to the bus access under the per-transaction trace gate) + the 680x0 TomHarte gate
  = M4.5; the address-error VECTOR on a misaligned EA + the IPL-level interrupt = M4.5d; the wide-bus JIT
  hot-op emit = M6. NEVER overstate — **no 68000 opcode is live, no 680x0 vector is green; the EA-compute is
  proven in isolation, not yet called from any op.** Include a **Docs Impact** section linking ADR 0004 +
  the M4.3a plan + the M4.1/M4.2 plans.

---

## Plan self-review (completed at write time)

- **Scope coverage (the 5 IN-scope items):**
  - **(1) the structured EA descriptor** — Task 2 (the `(mode, register)` → format + write-back derivation
    inside `EmitM68kEa`). ✓
  - **(2) the EA-compute layer + the `(An)+`/`-(An)` write-back + A7 case + ordering + pure-EA** — Task 2. ✓
  - **(3) surface the extension-word VALUES (D2)** — Task 1. ✓
  - **(4) the EA-category legality matrix + retire X/Y (D5)** — Task 3. ✓
  - **(5) the synthetic EA-compute + legality proof** — Task 1/2/3 tests. ✓
- **OUT-of-scope honored:** NO real 68000 op / dataset / real-spec decode (M4.4); NO interpreter wiring /
  cycle-accurate sequencing (M4.5); NO 680x0 vector asserted green; NO address-error vector (M4.5d); NO
  `AddrMode` enum member added (D1 — the EA is descriptor data, sidestepping the mirror tax); the 6502/Z80
  legality UNCHANGED (D5). ✓
- **Placeholder scan:** every code step shows literal code; the `Areg`/`SetAreg`/`ComputeBriefIndex`/
  `PcForEa` helpers are named with their contract (a flagged Task-0 confirmation of the address-register
  accessor convention, not a TBD); the brief-index full index-register decode is flagged as a possible M4.5
  detail (the synthetic proof uses displacement-only). No "similar to Task N". ✓
- **Type/name consistency:** `ExtensionWords(W0,W1,W2,W3,Count)` (DecodeResult + the walk + the EA-compute
  + the value test); `EmitM68kEa`/`ComputeEa`/`ComputeEaProbe` (the helper + the probe + the EA test);
  `M68kEaLegality.IsLegal(mode, reg, category)` (the legality helper + the legality test); the size
  magnitude `.b/.w/.l → 1/2/4` + the `A7 ±2` special case; the PostInc (read-then-add) / PreDec
  (subtract-then-read) ordering. ✓
- **Code/recon contradictions surfaced (the code wins):** (D1) no new `AddrMode` member (EA is data);
  (D2) `DecodedOperands` is Lo/Hi only — widen to surface the extension words; (D3) the post-vs-pre
  ordering proof; (D4) the A7 ±2 case via the banked accessor; (D5) the 6502/Z80 legality byte-identical,
  the EA-category path additive + field-grammar-only; (D6) the EA-compute is an isolated helper, M4.5
  sequences it. ✓
- **Build-green-after-every-task:** Task 1 widens `DecodeResult` via a default param (additive); Tasks 2–3
  emit/add on the field-grammar path only. The 6502/Z80 byte-identity guard (`RegeneratedSpecTests`) gates
  every task. ✓
- **One altitude flag:** the EA-compute is proven in ISOLATION (a synthetic probe); the cycle-accurate
  sequencing of the write-back relative to the bus access in a real op + the TomHarte per-transaction trace
  is M4.5 (the ADR's Decision 2 Consequences names this as the sequencing the interpreter/JIT must get
  right). M4.3b ships the correct write-back + ordering; M4.5 proves it under the vector gate.

## Closeout (filled at completion)

| Commit | Content | Suite |
|---|---|---|
| (Task 1) | surface the EA extension-word values from the field-decode walk (D2) | |
| (Task 2) | EmitM68kEa EA-compute (14 modes + write-back + A7 + ordering + pure-EA) | |
| (Task 3) | EA-category legality matrix (additive; 6502/Z80 unchanged; retire X/Y) | |

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | (record) |
| Final test count | (record) |
| Extension-word values surfaced (d16/abs.w/abs.l/#imm)? | (YES expected — synthetic) |
| All 14 EA modes compute the address? | (YES expected — synthetic) |
| `(An)+`/`-(An)` write-back by size + ordering (D3)? | (YES expected) |
| `A7 ±2` special case (D4)? | (YES expected) |
| Pure-EA (LEA/PEA, no-dereference) compute? | (YES expected) |
| EA-category legality matrix + X/Y retired (68000)? | (YES expected) |
| Any 68000 opcode live? | NO — framework-only; no 680x0 vector green; the EA-compute is not yet called from any op |
| 6502/Z80 un-regressed? | (YES expected — RegeneratedSpecTests byte-identity green) |
| `-warnaserror` | (clean expected) |
| Still deferred | dataset + real decode + gzip loader (M4.4); interpreter + EA-compute wired into op bodies + cycle-accurate sequencing + TomHarte gate (M4.5); address-error vector + IPL (M4.5d); JIT hot-op emit (M6) |
| Recommended next chunk | M4.4 — the importer field-pattern dataset + the real M68000Spec.cs decode + the mnemonic-keyed gzip TomHarte loader |

## Slice docs index

- **Architecture (Decisions 1 + 2 — decode + addressing):**
  `docs/architecture/0004-68000-decode-addressing-and-exceptions.md`
- **The other half of M4.3 (the word-granular field-decode variant):**
  `docs/superpowers/plans/2026-06-15-m4-3a-word-granular-decode.md`
- **The M4 foundation (state + bus):** `docs/superpowers/plans/2026-06-15-m4-1-core-width-and-68000-state.md`,
  `docs/superpowers/plans/2026-06-15-m4-2-wide-be-bus.md`
- **The closest EA-helper precedent (Z80 Indexed EA):**
  `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1a-addrmode-ea.md`
</content>
</invoke>
