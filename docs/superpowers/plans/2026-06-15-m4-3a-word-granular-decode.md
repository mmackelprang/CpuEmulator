# M4.3a: The Word-Granular, Field-Decomposed `DecodeStructure` Variant (the highest-risk M4 abstraction, synthetically proven)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. This is the FIRST of the two M4.3 framework slices (a = the word-granular field-decode
> variant + the synthetic field-grammar proof; b = the structured EA descriptor + EA-compute layer + the
> auto-inc/dec register write-back). Land M4.3a, then M4.3b, BEFORE the 68000 dataset (M4.4) depends on
> either.

**Goal:** add the **word-granular, field-decomposed `DecodeStructure` variant** ADR 0004 Decision 1 names —
the genuinely-new decode SHAPE the 68000 needs (a 16-bit big-endian operword decoded by NON-CONTIGUOUS
bit-field extraction into an opaque `(operation, size)` key, with the instruction length **computed from the
resolved EA-mode × size**, not a per-opcode constant). M4.3a ships the SHAPE: a spec can declare a field
grammar (per-operation `(mask, match)` over the operword + the bit-positions of the size field and the EA
6-bit field), a `FetchUnit.Word` declaration that the generated decode walk **consumes** (today it is parsed
but read nowhere — RECON-FINDING C1), a **big-endian word fetch stream** (today every `IFetchStream` is
little-endian — RECON-FINDING C2), and a generated field-decode walk that extracts the fields and computes
the extension-word count. **M4.3a is proven entirely with SYNTHETIC field-grammar fixtures** (the M3.1b
"ship the SHAPE + the synthetic proof" discipline); NO 68000 dataset row, NO real `M68000Spec.cs` decode,
NO TomHarte vector turns green here. Every 6502 + Z80 artifact stays byte-identical (the field-decode
variant is opt-in; the byte/prefix walk + the existing `FetchUnit.Byte` path are untouched).

**Architecture:** the 68000 today (M4.1 + M4.2) has the 32-bit register file + the SR/CCR split + A7/USP/SSP
banking + the wide big-endian bus (`Read16/32`/`Write16/32` + `Endianness` + `BusAlignment.IsMisaligned`),
but **executes nothing** — `M68000Spec.cs` declares an empty `Instructions` array and **no `DecodeStructure`**
(M68000Spec.cs:9,56). The decode pipeline (`IFetchStream` → `Decode(stream)` → `DecodeResult(key, length,
operands)` → `Execute(key)`) is the M3.1b/M3.4e machinery (`IDecoder.cs`, `CpuEmitter.cs:3948
EmitStructuredDecodeWalk`), already keyed by an **opaque `uint` from a generated decode function** and
already returning a **COMPUTED length** (`UnitsConsumed × UnitBytes`, CpuEmitter.cs:4027). ADR Decision 1's
two hardest pieces are therefore ALREADY present; M4.3a adds two NEW axes on top, exactly as ADR 0001 "M3
NOW" pre-planned: (1) the **fetch unit becomes a 16-bit big-endian word** (the `IFetchStream.UnitBytes`
abstraction exists — CpuEmitter never hardcodes `Read8` — but every concrete stream is `UnitBytes == 1` and
little-endian, and `FetchUnit.Word` is declared-but-unconsumed), and (2) the **length is operand-computed
from mode × size**, which extends the DDCB "length is not a per-opcode constant" / 8086 "ModR/M-determined
tail" precedent (CpuEmitter.cs:4008-4015) to a field-driven extension-word count. This is **additive**: the
existing zero-prefix (6502) + single/compound-prefix (Z80) paths are the degenerate cases of the byte-fetch
walk and are unchanged; the field-decode walk is a NEW arm selected by `FetchUnit.Word` + a declared field
grammar. Every 6502/Z80 artifact stays byte-identical.

**Tech Stack:** C# (.NET 10), a Roslyn incremental source generator (`CpuEmulator.Generators`), a console
spec importer (`CpuEmulator.SpecImporter`), and xUnit. The 68000 SingleStepTests/680x0 TomHarte gate is
**out of scope** (it arrives with the interpreter, M4.5). M4.3a touches NO real CPU spec — the field-grammar
variant is proven via `GeneratorTestHost.CompileAndLoadType`/`Run` synthetic fixtures, decoupled from the
real `M68000Spec.cs` (whose field-pattern dataset + regen is M4.4).

---

## Scope

**IN scope (the word-granular field-decode variant + the synthetic proof; NO opcode goes live):**

1. **The field-grammar `DecodeStructure` carrier (D1).** `DecodeStructure` (or a sibling carrier on the
   spec) gains a representation for: the **fetch unit** (byte vs word-big-endian) and a **field grammar** —
   per operation, a `(mask, match)` over the 16-bit operword plus the bit-positions/widths of the **size
   field** and the **EA 6-bit field** (mode:register). The 6502/Z80 absent/prefix forms are unchanged
   (the new carrier is opt-in; their `KeyShape`s stay `OpcodeByte`/`PrefixedOpcode`/`Compound`,
   `FetchUnit.Byte`).
2. **Consume `FetchUnit.Word` (RECON-FINDING C1).** `FetchUnit { Byte, Word }` already exists on the model
   (`SpecModel.cs:41,51`) but is read NOWHERE. M4.3a (a) makes the spec able to DECLARE `FetchUnit.Word`,
   (b) parses it onto `SpecModel.FetchUnit`, and (c) makes `EmitStructuredDecodeWalk` BRANCH on it — a
   word-fetch + field-decode walk vs the existing byte-fetch + prefix walk.
3. **A big-endian word fetch stream (RECON-FINDING C2).** Every concrete `IFetchStream`
   (`AddressSpaceFetchStream`, `BusFetchStream`, `BufferFetchStream` with `unitBytes:2`) reads
   **little-endian** and uses a **16-bit `ushort` origin**. The 68000 reads **big-endian words** at a
   **24/32-bit address**. Add a big-endian word stream (a `Endianness`-aware `AddressSpaceFetchStream`
   variant, or a ctor flag) + extend `BufferFetchStream` to read big-endian when so constructed, so the
   synthetic proof can feed a big-endian operword stream.
4. **The generated field-decode walk.** A NEW `EmitStructuredDecodeWalk` arm (selected by `FetchUnit.Word`
   + a field grammar): fetch the 16-bit operword → match it against the field grammar `(mask, match)` table
   → extract `(operation, size, ea-mode, ea-register)` → produce the opaque `(operation, size)` descriptor
   key → compute the extension-word count from `ea-mode × size` → return `DecodeResult(key, totalLength,
   operands)`. Illegal operwords (no grammar match) return the Undefined sentinel (the illegal-instruction
   path; the *vector* is M4.5). Length stays COMPUTED (`UnitsConsumed × UnitBytes`, now × 2 for words).
5. **A SYNTHETIC field-grammar decode proof.** Synthetic specs declaring a small field grammar + a
   big-endian operword stream, asserting: (a) the operword decodes to the right `(operation, size,
   ea-mode, ea-register)` tuple and opaque key; (b) the extension-word count (hence byte length) is
   computed correctly for representative mode × size combinations (e.g. `Dn` = 0 ext words; `d16(An)` = 1;
   `abs.w` = 1; `abs.l` = 2; `#imm.l` = 2); (c) an unmatched operword returns the Undefined sentinel.

**OUT of scope (later slices — do NOT reach for them):**

- **The structured EA DESCRIPTOR + the EA-COMPUTE layer + the `(An)+`/`-(An)` register write-back** =
  **M4.3b** (the immediately-next slice). M4.3a computes the extension-word COUNT (how many words the EA
  mode consumes, to get the length) but does NOT compute the EA *address*, read the extension words'
  *values* into an EA, or model any register write-back. The `(operation, size, ea-mode, ea-register)`
  tuple M4.3a extracts is the INPUT to M4.3b's EA-compute.
- **The EA-category legality matrix + retiring `RequiredIndexRegister`'s X/Y** = M4.3b (the legality matrix
  is an EA-descriptor concern; M4.3a's synthetic grammar declares its own trivial legal-EA set just to
  drive the extension-word-count computation).
- **Any 68000 opcode / dataset / real-spec decode going live** = M4.4 (the field-pattern dataset + the
  mnemonic-keyed gzip TomHarte loader + the emitted `M68000Spec.cs` decode) and M4.5 (the interpreter +
  the TomHarte gate). M4.3a adds NO dataset row and makes NO 680x0 vector green.
- **The synchronous mid-instruction exception / IPL-level interrupt line** = M4.5d.
- **The wide-bus JIT hot-op emit** = M6.

> **The honest one-liner for M4.3a's close-state:** a spec can declare a word-granular, field-decomposed
> decode structure (a `FetchUnit.Word` + a per-operation `(mask, match)` field grammar naming the size +
> EA bit-fields); the generated decode walk fetches a 16-bit BIG-ENDIAN operword, extracts `(operation,
> size, ea-mode, ea-register)` by non-contiguous bit-field extraction into an OPAQUE `(operation, size)`
> key, and COMPUTES the instruction length from the extension-word count implied by mode × size; a synthetic
> field-grammar fixture proves the tuple, the key, the computed length per mode × size, and the
> illegal-operword Undefined sentinel. NO 68000 opcode is live, NO 680x0 vector is asserted green, and the
> EA ADDRESS computation + the register write-back are M4.3b. Every 6502 + Z80 artifact is byte-identical
> (the variant is opt-in via `FetchUnit.Word` + the field grammar; the byte/prefix walk is untouched).

---

## Ground truth — what M3.1b/M3.4e + M4.1/M4.2 ALREADY shipped (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 0** — M4.3a REUSES or EXTENDS them.

- **The decode pipeline + the opaque key + the computed length.** `src/CpuEmulator.Core/Jit/IDecoder.cs` —
  `IFetchStream` (`:7-21`: `UnitBytes`/`NextUnit`/`PeekUnit`/`UnitsConsumed`; the doc says "Word-capable for
  the 68000 (M4) — the walk never hardcodes Read8") + `IDecoder.Decode(IFetchStream) → DecodeResult`
  (`:27-30`). `src/CpuEmulator.Core/Jit/DecodeResult.cs` — `DecodeResult(uint OperationKey, int Length,
  DecodedOperands Operands)` (`:13-16`: key is opaque, Length is "COMPUTED OUTPUT") + `DecodedOperands(byte
  Lo, byte Hi, byte Count)` (`:26`: **only Lo/Hi — RECON-FINDING C3**). The generated walk:
  `EmitStructuredDecodeWalk` (`CpuEmitter.cs:3948-4039`) — `key = first` (degenerate), `(first<<8)|op`
  (prefix), `(first<<3)|((next>>3)&7)` (sub-field), the ModR/M `tail = modrm & 3` computed tail
  (`:4011-4014`), and `int length = stream.UnitsConsumed * stream.UnitBytes` (`:4027`). **This is the seam
  the field-decode arm joins.**
- **`FetchUnit` exists but is UNCONSUMED (RECON-FINDING C1).** `src/CpuEmulator.Generators/SpecModel.cs:41`
  declares `internal enum FetchUnit { Byte, Word }`; `:51` carries `FetchUnit FetchUnit = FetchUnit.Byte`
  on `SpecModel`. **Grep confirms `FetchUnit` is referenced ONLY in `SpecModel.cs` + `SpecParser.cs:426`
  (which hardcodes `FetchUnit.Byte` when constructing the model) — `EmitStructuredDecodeWalk` never reads
  it.** So `FetchUnit.Word` is declared-but-dead; M4.3a wires it end to end (declare → parse → emit-branch).
- **Every concrete `IFetchStream` is little-endian + 16-bit-origin (RECON-FINDING C2).**
  `AddressSpaceFetchStream` (`Core/Jit/AddressSpaceFetchStream.cs`): `UnitBytes => 1`, `ushort _origin`,
  `_bus.Read8((ushort)(_origin + _offset))`. `BusFetchStream` (`Jit/BusFetchStream.cs`): same shape +
  `SeekTo(ushort)`. `BufferFetchStream` (`Core/Jit/BufferFetchStream.cs`): `unitBytes` ctor arg accepts 2,
  but `PeekUnit` composes **little-endian** (`v |= b[cursor+i] << (8*i)` — byte 0 is the LOW byte, `:35`).
  **None reads big-endian; none uses a 24/32-bit address.** M4.3a adds a big-endian word path.
- **The wide big-endian bus is present (M4.2).** `IAddressSpace.Read16/Read32` + `Endianness` (default
  interface methods); `AddressSpace` constructs `BigEndian`; `BusAlignment.IsMisaligned(addr, width)`
  exists (no raise). The 68000 word fetch reads `Read16` big-endian. Confirm the `Endianness` enum +
  `Read16` signature at Task 0 (read `src/CpuEmulator.Core/AddressSpace.cs` + the M4.2 plan close-state).
- **The 68000 spec is state-only.** `src/CpuEmulator.Cpus.M68000/M68000Spec.cs`: `Registers` (D0–D7, A0–A6,
  USP/SSP, PC 32-bit, SR 16-bit), `Flags` (CCR + S=13), `Instructions = []`, **no `DecodeStructure`**
  (`:9,56`). `M68000Cpu.cs`: the hand-written half (bus wiring, A7 banking, SR/CCR, inert interrupt hooks);
  it "never calls Step" (`:10`) because the table is empty. M4.3a does NOT add a real row — the field-grammar
  proof is synthetic.
- **The descriptor table + the keyed dictionary.** `EmitKeyedDescriptorTable` (`CpuEmitter.cs:~3580` —
  read it) emits `[0x{key}u] = KeyedDescriptorLiteral(...)` into a `Dictionary<uint, OpcodeDescriptor>`
  (`JitDescriptorsByKey`); `DescriptorFor(uint)` resolves through it, returning the Undefined sentinel for
  an unknown key (`:4036-4038`). A field-decode `(operation, size)` key is just another opaque `uint` key —
  the table holds it unchanged.
- **The synthetic-spec test host.** `tests/CpuEmulator.Tests/Generators/GeneratorTestHost.cs`:
  `CompileAndLoadType(source, typeName)` + `Run(source)` (returns `GeneratorDiagnostics`/`GeneratedText`).
  The decode-walk synthetic precedent: the M3.1b three-property proof + the M3.4e `Z80CompoundDecodeTests`
  (read it — it feeds a byte stream to the static `Decode(IFetchStream)` and inspects the `DecodeResult`).
  M4.3a mirrors that shape with a big-endian WORD stream + a field grammar.

### RECON FINDINGS that refine the ADR's sketch (the code WINS — flagged)

> Discovered during write-time recon by reading the source. The implementer MUST re-confirm each at Task 0.
> ADR 0004 §4 item 1 left the EXACT field-grammar record shape "just-in-time"; these findings settle it
> against the live seams and are the reconciliation flags the Coordinator asked for.

- **C1 — `FetchUnit` is declared-but-dead; M4.3a is where it goes live.** `FetchUnit { Byte, Word }`
  (`SpecModel.cs:41`) and the `SpecModel.FetchUnit` field (`:51`) exist, but `SpecParser.cs:426` hardcodes
  `FetchUnit.Byte` when building the model and `EmitStructuredDecodeWalk` never reads `model.FetchUnit`.
  **There is currently NO spec-authoring surface to DECLARE `FetchUnit.Word`** — the `DecodeStructure`
  record (`Core/Specification/DecodeStructure.cs`) has only `Prefixes`/`ModRmOpcodes`/`SubFieldOpcodes`; it
  does not carry a fetch unit. **M4.3a must (a) add the authoring surface** (a `FetchUnit` member on
  `DecodeStructure`, OR a sibling field-grammar carrier on the spec — Task 1 decides), **(b) parse it onto
  `SpecModel.FetchUnit`** (replace the hardcoded `FetchUnit.Byte` at `:426` with the parsed value), **(c)
  branch on it in the emitter**. The ADR (Decision 1) says "DecodeStructure (or a sibling record) gains a
  fetch-unit declaration" — the sibling-record option is cleaner because the field grammar (the `(mask,
  match)` table) is a different SHAPE from the prefix/ModRm/sub-field byte arrays; mixing them on one record
  muddies both. **Recommended (Task 1): a sibling `FieldGrammar` carrier on the spec** (`public static
  readonly FieldGrammar Decode68k = ...`) OR an extended `DecodeStructure` with a nullable `FieldGrammar?`
  arm — decide at Task 1 Step 3 against the parser's field-discovery convention (`FindArrayField`).
- **C2 — no big-endian, no wide-address fetch stream exists.** All three concrete streams are little-endian
  + `ushort`-origin (Ground truth above). The 68000 reads big-endian words at a 24-bit address. M4.3a adds
  a big-endian word stream. **Two design points:** (i) the SYNTHETIC proof can use a big-endian
  `BufferFetchStream` (extend its `PeekUnit` to compose big-endian when `unitBytes == 2` and a
  `bigEndian` flag is set — additive; the existing little-endian default path is unchanged), so no live-bus
  stream is needed for M4.3a's proof; (ii) the INTERPRETER-side big-endian `AddressSpaceFetchStream` (the
  one the generated `Step` constructs for a `FetchUnit.Word` CPU at runtime) can be deferred to M4.5 (where
  the real 68000 Step runs) OR added here as a dormant companion — **recommend: add a big-endian word
  `BufferFetchStream` path NOW (the proof needs it); defer the live big-endian `AddressSpaceFetchStream`
  wiring to M4.5** (M4.3a's generated `Step` for the synthetic CPU is never called — the proof drives the
  static `Decode(IFetchStream)` directly with a `BufferFetchStream`). Confirm at Task 0 that the synthetic
  decode-walk precedent drives `Decode` with a buffer stream, not a live bus.
- **C3 — `DecodedOperands` carries only `Lo`/`Hi` (2 bytes); 68000 extension words are up to 4 bytes.**
  `DecodedOperands(byte Lo, byte Hi, byte Count)` (`DecodeResult.cs:26`) holds 2 operand bytes — enough for
  the 6502 + the synthetic CPU, "wider operand carriage (the 8086's full disp/imm) is M5 work" (`:24`). A
  68000 `abs.l` is 4 extension bytes; `#imm.l` is 4. **For M4.3a (which computes only the COUNT, not the EA
  values), `DecodedOperands` does NOT need widening** — the walk consumes the extension words (advancing
  `UnitsConsumed` so the length is right) WITHOUT surfacing their bytes (the EA-compute that READS those
  bytes is M4.3b/M4.5). So M4.3a's walk consumes-and-discards extension words for length, surfacing only
  what the existing `Lo`/`Hi` hold (or `DecodedOperands.None`). **Record the deferral:** widening
  `DecodedOperands` (or adding an extension-word buffer to `DecodeResult`) is M4.3b's concern (it needs the
  extension-word VALUES to compute the EA). M4.3a flags it; M4.3b lands it. Confirm `DecodedOperands` is
  not over-stressed by the count-only walk at Task 0.
- **C4 — the field grammar's `(mask, match)` is the load-bearing representation; specify it concretely.**
  The 68000 line-decode is non-contiguous: e.g. `MOVE.{b,w,l}` is operword bits `00 ss dddddd ssssss`
  (size in bits 13-12 with a NON-standard `01/11/10 = b/w/l` encoding, two EA fields); `ADD.{b,w,l} Dn,<ea>`
  is `1101 rrr 0 ss eeeeee` (operation in 15-12 + bit 8, size in 7-6, EA in 5-0). A flat 64K table is wrong
  (~98% illegal — ADR Decision 1(C) rejected). **The field-grammar entry is, per operation:** `(operword
  mask, operword match, sizeFieldShift, sizeFieldWidth, sizeEncoding, eaFieldShift, legalEaCategory)`. The
  walk ANDs the operword with each entry's mask, compares to match, and on the first hit extracts the size
  (via shift/width + the size-encoding map — MOVE's `01/11/10` differs from the standard `00/01/10`) and
  the EA 6 bits (mode = bits 5-3, register = bits 2-0). **The size encoding is per-operation** (MOVE is the
  notorious outlier) — the grammar must carry the size-encoding map, not assume the standard `00=b/01=w/
  10=l`. M4.3a's SYNTHETIC grammar uses the STANDARD encoding for the proof (the MOVE-outlier handling is
  exercised by the real dataset in M4.4); but the carrier MUST be able to express a per-operation size map
  (so M4.4 does not need to reshape it). Flag this: M4.3a ships the EXPRESSIVE carrier, proves it with the
  standard encoding; M4.4's MOVE row uses the outlier map.
- **C5 — the extension-word count is a function of (ea-mode, size), NOT of the operation alone.** `Dn`/`An`
  = 0 ext words; `(An)`/`(An)+`/`-(An)` = 0; `d16(An)`/`d16(PC)` = 1; `d8(An,Xn)`/`d8(PC,Xn)` = 1 (the
  brief extension word); `abs.w` = 1; `abs.l` = 2; `#imm` = 1 for `.b`/`.w`, 2 for `.l`. **This count
  table is the operand-computed-length core (ADR Decision 1).** The walk computes it from the resolved
  `(ea-mode, size)` after field extraction, consumes that many extension words (advancing `UnitsConsumed`),
  and the length falls out as `UnitsConsumed × 2`. This count table is shared with M4.3b (which reads the
  same extension words to compute the EA address) — recommend factoring it as a small emitted helper /
  data table both slices reference. The `#imm` size-dependence (1 word for b/w, 2 for l) is the clearest
  "length depends on mode AND size" case — the synthetic proof MUST exercise it. (For `MOVE`, which has TWO
  EA fields — source + destination — the count is the SUM of both EAs' extension words; M4.3a's synthetic
  proof uses single-EA operations for simplicity, and flags the two-EA MOVE sum as an M4.4 grammar concern,
  OR adds a minimal two-EA synthetic row if it fits cleanly — Task 4 decides.)

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Core/Specification/DecodeStructure.cs` | Modify | Add the `FieldGrammar`/`FieldOp` carrier + the `FetchUnit` authoring surface (D1; the sibling-record shape per C1). |
| `src/CpuEmulator.Core/Specification/Spec.cs` | Modify | Add the DSL factory(s) for declaring a field grammar (`FieldOp(mask, match, sizeField, eaField, …)`), mirroring the `Insn`/`PrefixByte` factory convention. |
| `src/CpuEmulator.Generators/SpecModel.cs` | Modify | Carry the field grammar onto `SpecModel` (a `FieldGrammarModel`); confirm `FetchUnit` is carried (it is — `:51`). |
| `src/CpuEmulator.Generators/SpecParser.cs` | Modify | Parse the field grammar + the declared `FetchUnit` (replace the hardcoded `FetchUnit.Byte` at `:426`); validate the grammar (CPUGEN diagnostic for a malformed field op). |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | `EmitStructuredDecodeWalk`: branch on `FetchUnit.Word` → the field-decode arm (extract fields, compute the extension-word count, COMPUTED length); the `(operation, size)` key packing. |
| `src/CpuEmulator.Core/Jit/BufferFetchStream.cs` | Modify | Add a big-endian word path (`bigEndian` ctor flag composing `PeekUnit` high-byte-first when `unitBytes == 2`) — RECON-FINDING C2. |
| `tests/CpuEmulator.Tests/Generators/M68kFieldGrammarVocabularyTests.cs` | Create | The `FieldGrammar`/`FieldOp`/`FetchUnit.Word` carrier carries its fields; a spec declaring it parses (no generator error). |
| `tests/CpuEmulator.Tests/Generators/M68kFieldDecodeWalkTests.cs` | Create | The synthetic field-grammar decode-walk proof: operword → (operation, size, ea-mode, ea-register) tuple + opaque key + COMPUTED length per mode × size + illegal-operword Undefined. |
| `tests/CpuEmulator.Tests/Jit/BigEndianFetchStreamTests.cs` | Create | The big-endian word `BufferFetchStream` reads high-byte-first (C2), little-endian default unchanged. |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502/Z80
> byte-identity guard `RegeneratedSpecTests` + the whole Z80 + 6502 suites green), then commit. Tasks are
> dependency-ordered so the suite builds and stays green after every task. Literal code is given for every
> load-bearing piece. The synthetic-spec tests (via `GeneratorTestHost`) decouple from the real
> `M68000Spec.cs` — M4.3a adds NO real 68000 decode (the field-pattern dataset is M4.4). Synthetic
> structured fixtures follow the M3.4d shape: `IAddressSpace _bus`, the inert policy hooks
> (`TryServiceInterrupt => false`, `InterruptPending => false`, `OnInstructionFetched`, `ReadBus`/`WriteBus`,
> `HandleUndefinedOpcode`).

### Task 0: Baseline + decode-seam recon (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch.** Create the branch off the current main:
  Run: `git switch -c feat/m4-3a-word-granular-decode`
  Expected: on the new branch, head at the M4.2 (PR #34) merge (`9251fcb`).

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test`
  Expected: 0 failures, 0 unexpected skips. Record the EXACT count (the closeout pins it; the prompt cites
  the suite ~5116 before M4.3).
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean (no warnings).

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each cited surface holds:**
  - `src/CpuEmulator.Core/Jit/IDecoder.cs:7-30` (`IFetchStream` — the `UnitBytes`/`NextUnit` abstraction +
    the "Word-capable for the 68000" doc), `DecodeResult.cs:13-29` (`DecodeResult` + `DecodedOperands`
    Lo/Hi/Count — RECON-FINDING C3).
  - `src/CpuEmulator.Generators/CpuEmitter.cs:3948-4039` (`EmitStructuredDecodeWalk` — the byte-fetch
    prefix/ModRm/sub-field arms + `int length = stream.UnitsConsumed * stream.UnitBytes` at `:4027`;
    confirm it never reads `model.FetchUnit` — RECON-FINDING C1), the `EmitKeyedDescriptorTable` /
    `KeyedDescriptorLiteral` region (the `Dictionary<uint, OpcodeDescriptor>` + `DescriptorFor`).
  - `src/CpuEmulator.Generators/SpecModel.cs:41,51` (`FetchUnit` enum + the `FetchUnit` field) and
    `SpecParser.cs:426` (the hardcoded `FetchUnit.Byte` — confirm it is the ONLY construction site).
  - The three concrete fetch streams (`Core/Jit/AddressSpaceFetchStream.cs`, `Jit/BusFetchStream.cs`,
    `Core/Jit/BufferFetchStream.cs:31-38` the little-endian `PeekUnit`) — RECON-FINDING C2.
  - `src/CpuEmulator.Cpus.M68000/M68000Spec.cs:9,56` (no `DecodeStructure`, empty `Instructions`) +
    `M68000Cpu.cs:10` ("never calls Step").
  - `src/CpuEmulator.Core/AddressSpace.cs` (the M4.2 `Read16`/`Endianness` + `BusAlignment.IsMisaligned`)
    and the M4.2 plan close-state (`docs/superpowers/plans/2026-06-15-m4-2-wide-be-bus.md`).
  - The decode-walk synthetic precedent: `tests/CpuEmulator.Tests/Generators/Z80CompoundDecodeTests.cs`
    (how it drives the static `Decode(IFetchStream)` with a stream + inspects `DecodeResult`) and the
    M3.1b three-property decode-walk test (search `tests/CpuEmulator.Tests` for `IFetchStream` /
    `DecodeStructure` / `BufferFetchStream`). Confirm the proof drives `Decode` with a BUFFER stream, not a
    live bus (RECON-FINDING C2 design point).
  - `tests/CpuEmulator.Tests/Generators/GeneratorTestHost.cs` (`CompileAndLoadType`/`Run`).

- [ ] **Step 4:** No commit (read-only). Proceed to Task 1.

---

### Task 1: The `FieldGrammar`/`FieldOp` carrier + `FetchUnit.Word` authoring surface + DSL factory (TDD)

> Add the spec-authoring surface for a word-granular field grammar (D1 / C1 / C4): a `FieldOp` record
> carrying `(mask, match, sizeShift, sizeWidth, sizeEncoding, eaShift, legalEa)` and a `FieldGrammar`
> carrier the spec declares (with `FetchUnit.Word`), plus the `Spec` DSL factory the generator
> pattern-matches by name. Default everything so the 6502/Z80's absent declaration + `FetchUnit.Byte` are
> unchanged. Proven by a vocabulary test: the records carry their fields; a synthetic spec declaring a
> grammar parses with no generator error.

**Files:**
- Modify: `src/CpuEmulator.Core/Specification/DecodeStructure.cs`
- Modify: `src/CpuEmulator.Core/Specification/Spec.cs`
- Modify: `src/CpuEmulator.Generators/SpecModel.cs`
- Modify: `src/CpuEmulator.Generators/SpecParser.cs`
- Test: `tests/CpuEmulator.Tests/Generators/M68kFieldGrammarVocabularyTests.cs` (create)

> **Design decision (recorded, per C1 + C4).** The field grammar is a SIBLING carrier on the spec (a
> `FieldGrammar` field discovered by `FindArrayField`-style discovery), NOT folded into the prefix-shaped
> `DecodeStructure` — the `(mask, match)` field-op shape is structurally different from the prefix/ModRm
> byte arrays, and the 68000 declares a field grammar INSTEAD OF (not alongside) prefixes. The carrier
> declares `FetchUnit.Word` so the parser sets `SpecModel.FetchUnit` from it (RECON-FINDING C1). The
> `sizeEncoding` is a per-op map (C4: MOVE is the outlier); the M4.3a synthetic proof uses the standard
> encoding. **Confirm at Task 0 Step 3 whether the parser discovers a non-array field cleanly** (the field
> grammar is a single object, not an array — mirror how `Decode`/`Flags` single-object fields are
> discovered, `SpecParser.cs:749 FindArrayField` + the `Flags` discovery).

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/M68kFieldGrammarVocabularyTests.cs`. Assert the carrier records hold
  their fields and a synthetic spec declaring a field grammar + `FetchUnit.Word` parses with no generator
  error.

```csharp
using CpuEmulator.Core.Specification;
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kFieldGrammarVocabularyTests
{
    [Fact]
    public void FieldOp_carries_its_grammar_fields()
    {
        // operation "ADD.size Dn,<ea>" sketch: match bits, size in 7-6 (standard b/w/l), EA in 5-0.
        var op = new FieldOp(
            Mask: 0xF100, Match: 0xD000, Operation: "ADD",
            SizeShift: 6, SizeWidth: 2, SizeEncoding: SizeEncoding.Standard,
            EaShift: 0, LegalEa: EaCategory.DataAddressing);
        Assert.Equal((ushort)0xF100, op.Mask);
        Assert.Equal((ushort)0xD000, op.Match);
        Assert.Equal("ADD", op.Operation);
        Assert.Equal(6, op.SizeShift);
        Assert.Equal(SizeEncoding.Standard, op.SizeEncoding);
        Assert.Equal(0, op.EaShift);
    }

    [Fact]
    public void A_spec_declaring_a_field_grammar_and_word_fetch_parses_clean()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("fg")]
        public static class FgSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32), new("A0", 32),
                new("SP", 32, RegisterRole.StackPointer), new("PC", 32, RegisterRole.ProgramCounter),
                new("SR", 16, RegisterRole.Status),
            ];
            public static readonly FlagLayout Flags = new([
                new("C", 0), new("V", 1), new("Z", 2), new("N", 3), new("X", 4), new("S", 13)]);
            // A word-granular field grammar: ONE op, standard size encoding, data-addressing EA.
            public static readonly FieldGrammar Decode68k = new(
                FetchUnit.Word,
                [ FieldOp(Mask: 0xF100, Match: 0xD000, Operation: "ADD",
                          SizeShift: 6, SizeWidth: 2, SizeEncoding: SizeEncoding.Standard,
                          EaShift: 0, LegalEa: EaCategory.DataAddressing) ]);
            public static readonly InstructionDef[] Instructions = [];
        }
        """;
}
```

  > **Confirm the carrier shape against the parser's discovery.** The exact field name (`Decode68k`) +
  > whether `FetchUnit` is a positional arg on `FieldGrammar` or declared separately are Task-1 decisions —
  > read `SpecParser`'s single-object field discovery (the `Flags` / `Decode` parse) at Task 0 and mirror it.
  > The `FieldOp`/`FieldGrammar`/`EaCategory`/`SizeEncoding` types + the `FieldOp(...)` factory are what
  > Step 3 adds. `EaCategory` here is just a TAG carried for M4.3b (the legality matrix is M4.3b); M4.3a
  > stores it but the count-only walk does not yet branch on it.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kFieldGrammarVocabularyTests"`
  Expected: FAIL — `FieldOp`/`FieldGrammar`/`EaCategory`/`SizeEncoding` do not exist (compile error).

- [ ] **Step 3: Add the carrier records + the DSL factory + the model + the parse.**
  - In `src/CpuEmulator.Core/Specification/DecodeStructure.cs`, add the field-grammar carrier (the
    inert-syntax-carrier convention the file's doc comment names):

```csharp
/// <summary>The fetch unit the decode walk reads through (Ground truth D). Byte (6502/Z80/8086) is the
/// default; Word is the 68000's 16-bit big-endian operword (M4.3a). Carried on a declared FieldGrammar.</summary>
public enum FetchUnit { Byte, Word }

/// <summary>How a field op's size bits map to an OperandSize (M4.3a / RECON-FINDING C4). Standard is the
/// common 68000 encoding (00=b, 01=w, 10=l); Move is the MOVE outlier (01=b, 11=w, 10=l). Per-op because
/// MOVE differs — the carrier expresses both so the M4.4 dataset needs no reshaping.</summary>
public enum SizeEncoding { Standard, Move }

/// <summary>An effective-address category (the classic 68000 legality buckets) — M4.3a carries it as a TAG
/// on each field op (the legality MATRIX that consumes it is M4.3b). Names the addressing-mode set an op's
/// EA may use (data / memory / control / alterable …); M4.3a's count-only walk does not yet branch on it.</summary>
public enum EaCategory { DataAddressing, MemoryAlterable, DataAlterable, Control, Alterable, All }

/// <summary>One operation's word-granular field decomposition (M4.3a, ADR 0004 Decision 1). The operword is
/// matched by (Mask, Match) — (operword &amp; Mask) == Match selects this op; the size is extracted from
/// bits [SizeShift, SizeShift+SizeWidth) via SizeEncoding; the 6-bit EA field (mode:register) is at
/// EaShift (mode = bits 5-3, register = bits 2-0 of the 6-bit field). LegalEa tags the EA category for the
/// M4.3b legality matrix. These types are inert syntax carriers for the generator.</summary>
public sealed record FieldOp(
    ushort Mask, ushort Match, string Operation,
    int SizeShift, int SizeWidth, SizeEncoding SizeEncoding,
    int EaShift, EaCategory LegalEa);

/// <summary>A word-granular, field-decomposed decode grammar (ADR 0004 Decision 1) — the 68000's decode
/// SHAPE. ABSENT (6502/Z80) ⇒ the byte/prefix walk (unchanged). Declaring it opts into FetchUnit.Word +
/// field extraction + operand-computed length. The Ops are matched in order; first (Mask, Match) hit wins;
/// no hit ⇒ the illegal-instruction Undefined sentinel (the vector is M4.5).</summary>
public sealed record FieldGrammar(FetchUnit FetchUnit, FieldOp[] Ops);
```

  - In `src/CpuEmulator.Core/Specification/Spec.cs`, add the `FieldOp` factory (the generator matches it
    by name, literal/enum args only — the constrained-DSL contract):

```csharp
    // ── M4.3a (68000 word-granular field grammar; additive — the 6502/Z80 name none) ──
    public static FieldOp FieldOp(
        ushort mask, ushort match, string operation,
        int sizeShift, int sizeWidth, SizeEncoding sizeEncoding,
        int eaShift, EaCategory legalEa)
        => new(mask, match, operation, sizeShift, sizeWidth, sizeEncoding, eaShift, legalEa);
```

  - In `src/CpuEmulator.Generators/SpecModel.cs`, add the model carriers (mirroring the
    `DecodeStructureModel` shape) + carry the grammar on `SpecModel`:

```csharp
internal enum SizeEncodingKind { Standard, Move }
internal enum EaCategoryKind { DataAddressing, MemoryAlterable, DataAlterable, Control, Alterable, All }

internal sealed record FieldOpModel(
    ushort Mask, ushort Match, string Operation,
    int SizeShift, int SizeWidth, SizeEncodingKind SizeEncoding,
    int EaShift, EaCategoryKind LegalEa);

internal sealed record FieldGrammarModel(FetchUnit FetchUnit, EquatableArray<FieldOpModel> Ops);
```
    and add `FieldGrammarModel? FieldGrammar = null` to the `SpecModel` record (next to `Decode`).

  - In `src/CpuEmulator.Generators/SpecParser.cs`: discover + parse the `FieldGrammar` field (mirror the
    `Decode`/`Flags` single-object discovery); parse each `FieldOp(...)` (literal/enum args); set
    `SpecModel.FetchUnit` from the grammar's declared `FetchUnit` (**replace the hardcoded `FetchUnit.Byte`
    at `:426`** with the parsed value — RECON-FINDING C1); add a CPUGEN diagnostic for a malformed field op
    (e.g. `SizeShift + SizeWidth > 16`, or a `Mask`/`Match` where `(Match & ~Mask) != 0`).

  > **The parse + the FetchUnit wiring.** Read `SpecParser.cs:426` (the model construction) + the `Decode`/
  > `Flags` discovery/parse helpers; mirror them for `FieldGrammar`. The KEY wiring is `FetchUnit` flowing
  > from the declared grammar onto `SpecModel.FetchUnit` so Task 2's emitter branch sees it. A spec with NO
  > `FieldGrammar` keeps `FetchUnit.Byte` (the 6502/Z80 default — byte-identical).

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kFieldGrammarVocabularyTests"`
  Expected: PASS — the records carry their fields; the synthetic spec parses clean.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (the carrier + factory + model + parse are additive; no existing spec
  declares a `FieldGrammar`, so `FetchUnit` stays `Byte` for all of them).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502/Z80 byte-identical —
  neither declares a `FieldGrammar`; adding inert carrier types + an unused factory cannot change their
  `.g.cs`).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Core/Specification/DecodeStructure.cs src/CpuEmulator.Core/Specification/Spec.cs \
        src/CpuEmulator.Generators/SpecModel.cs src/CpuEmulator.Generators/SpecParser.cs \
        tests/CpuEmulator.Tests/Generators/M68kFieldGrammarVocabularyTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add the word-granular FieldGrammar/FieldOp carrier + FetchUnit.Word authoring surface (D1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 2: A big-endian word `BufferFetchStream` path (TDD) (RECON-FINDING C2)

> The field-decode proof feeds a 16-bit BIG-ENDIAN operword stream; every concrete `IFetchStream` is
> little-endian (C2). Add a big-endian word path to `BufferFetchStream` (a `bigEndian` ctor flag that
> composes `PeekUnit` high-byte-first when `unitBytes == 2`). Additive: the existing little-endian default
> + the 6502/Z80/8086 byte path are unchanged. The live big-endian `AddressSpaceFetchStream` (the runtime
> Step companion) is deferred to M4.5 (the synthetic proof drives the static `Decode` with a buffer stream).

**Files:**
- Modify: `src/CpuEmulator.Core/Jit/BufferFetchStream.cs`
- Test: `tests/CpuEmulator.Tests/Jit/BigEndianFetchStreamTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create `tests/CpuEmulator.Tests/Jit/BigEndianFetchStreamTests.cs`:

```csharp
using CpuEmulator.Core.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

public class BigEndianFetchStreamTests
{
    [Fact]
    public void Big_endian_word_reads_high_byte_first()
    {
        // bytes 0x12 0x34 → big-endian word 0x1234 (high byte first).
        var s = new BufferFetchStream(new byte[] { 0x12, 0x34, 0x56, 0x78 }, unitBytes: 2, bigEndian: true);
        Assert.Equal(0x1234u, s.NextUnit());
        Assert.Equal(0x5678u, s.NextUnit());
        Assert.Equal(2, s.UnitsConsumed);
        Assert.Equal(4, s.UnitsConsumed * s.UnitBytes);   // COMPUTED byte length
    }

    [Fact]
    public void Little_endian_word_default_is_unchanged()
    {
        // The existing little-endian word path (byte 0 is the LOW byte) is untouched.
        var s = new BufferFetchStream(new byte[] { 0x12, 0x34 }, unitBytes: 2);
        Assert.Equal(0x3412u, s.NextUnit());
    }

    [Fact]
    public void Byte_path_is_unaffected()
    {
        var s = new BufferFetchStream(new byte[] { 0xAB, 0xCD });
        Assert.Equal(0xABu, s.NextUnit());
        Assert.Equal(0xCDu, s.NextUnit());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~BigEndianFetchStreamTests"`
  Expected: FAIL — `BufferFetchStream` has no `bigEndian` ctor parameter.

- [ ] **Step 3: Add the big-endian word path.** In `src/CpuEmulator.Core/Jit/BufferFetchStream.cs`, add the
  `bigEndian` flag + compose `PeekUnit` high-byte-first when set:

```csharp
public sealed class BufferFetchStream : IFetchStream
{
    private readonly System.ReadOnlyMemory<byte> _buffer;
    private readonly bool _bigEndian;
    private int _byteCursor;

    public BufferFetchStream(System.ReadOnlyMemory<byte> buffer, int unitBytes = 1, bool bigEndian = false)
    {
        if (unitBytes is not (1 or 2))
            throw new System.ArgumentOutOfRangeException(nameof(unitBytes), "fetch unit must be 1 or 2 bytes");
        _buffer = buffer;
        UnitBytes = unitBytes;
        _bigEndian = bigEndian;
    }

    public int UnitBytes { get; }
    public int UnitsConsumed => _byteCursor / UnitBytes;

    public uint NextUnit()
    {
        uint v = PeekUnit();
        _byteCursor += UnitBytes;
        return v;
    }

    public uint PeekUnit()
    {
        System.ReadOnlySpan<byte> b = _buffer.Span;
        uint v = 0;
        for (int i = 0; i < UnitBytes; i++)
        {
            int shift = _bigEndian ? 8 * (UnitBytes - 1 - i) : 8 * i;   // BE: byte 0 is the HIGH byte
            v |= (uint)b[_byteCursor + i] << shift;
        }
        return v;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~BigEndianFetchStreamTests"`
  Expected: PASS.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (the `bigEndian` flag defaults `false`; every existing `BufferFetchStream`
  call site is unchanged).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (a test-stream change cannot
  touch any generated `.g.cs`).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Core/Jit/BufferFetchStream.cs \
        tests/CpuEmulator.Tests/Jit/BigEndianFetchStreamTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add a big-endian word path to BufferFetchStream (68000 operword fetch; C2)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 3: The field-decode walk arm in `EmitStructuredDecodeWalk` — field extraction + opaque key (TDD)

> Teach `EmitStructuredDecodeWalk` to BRANCH on `FetchUnit.Word` + a declared field grammar: fetch the
> 16-bit operword, match it against the emitted `(mask, match)` table, extract `(operation, size, ea-mode,
> ea-register)` by non-contiguous bit-field extraction, and pack the opaque `(operation, size)` descriptor
> key. This task proves the EXTRACTION + the key (the operword → tuple → key path); Task 4 adds the
> extension-word COUNT (the operand-computed length). The byte-fetch prefix/ModRm/sub-field arms are the
> degenerate `FetchUnit.Byte` path, unchanged.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitStructuredDecodeWalk` field-decode arm)
- Test: `tests/CpuEmulator.Tests/Generators/M68kFieldDecodeWalkTests.cs` (create — the extraction/key portion)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/M68kFieldDecodeWalkTests.cs` with a synthetic field-grammar CPU; feed
  it a big-endian operword and assert the decoded key + (via a probe accessor) the extracted size + EA. The
  cleanest assertion that does not depend on M4.3b's EA-compute is the OPAQUE KEY: pack `(operation-index,
  size)` deterministically so the test can predict it.

```csharp
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kFieldDecodeWalkTests
{
    // A synthetic field-grammar CPU: ONE op "ADD" (mask 0xF100, match 0xD000), size in bits 7-6 (standard),
    // EA 6 bits in 5-0. The walk fetches a BIG-ENDIAN operword and packs the opaque (operation, size) key.
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("fgw")]
        public static class FgwSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32), new("A0", 32),
                new("SP", 32, RegisterRole.StackPointer), new("PC", 32, RegisterRole.ProgramCounter),
                new("SR", 16, RegisterRole.Status),
            ];
            public static readonly FlagLayout Flags = new([
                new("C", 0), new("V", 1), new("Z", 2), new("N", 3), new("X", 4), new("S", 13)]);
            public static readonly FieldGrammar Decode68k = new(
                FetchUnit.Word,
                [ FieldOp(Mask: 0xF100, Match: 0xD000, Operation: "ADD",
                          SizeShift: 6, SizeWidth: 2, SizeEncoding: SizeEncoding.Standard,
                          EaShift: 0, LegalEa: EaCategory.DataAddressing) ]);
            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class FgwCpu
        {
            private readonly IAddressSpace _bus;
            public FgwCpu(IAddressSpace bus) { _bus = bus; }
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

    [Fact]
    public void Operword_decodes_to_the_opaque_operation_size_key()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // ADD.w D0,D1-ish operword: 1101 001 0 01 000000 = 0xD240 (op match 0xD000, size 01=.w, EA = Dn 000:000).
        // Big-endian stream: high byte 0xD2, low byte 0x40.
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(
            new byte[] { 0xD2, 0x40 }, unitBytes: 2, bigEndian: true);
        var decode = t.GetMethod("Decode")!;
        dynamic r = decode.Invoke(null, new object[] { stream })!;
        // The opaque key packs (operationIndex, size). The exact packing is the generator's choice; the
        // test asserts it is STABLE + distinct per (op, size). Assert the key for ADD.w (size index 1).
        uint key = (uint)r.OperationKey;
        Assert.NotEqual(0u, key);                    // matched (not the illegal Undefined sentinel)
        // The size .w is reflected in the key; ADD.b (size 00) packs a DIFFERENT key.
        var streamB = new CpuEmulator.Core.Jit.BufferFetchStream(
            new byte[] { 0xD0, 0x40 }, unitBytes: 2, bigEndian: true);   // 0xD0xx → size 00 = .b
        dynamic rb = decode.Invoke(null, new object[] { streamB })!;
        Assert.NotEqual(key, (uint)rb.OperationKey);  // (ADD,.w) != (ADD,.b) — size is part of the key
    }

    [Fact]
    public void An_unmatched_operword_returns_the_undefined_sentinel()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // 0x0000 matches no field op (mask 0xF100 & 0x0000 = 0x0000 != match 0xD000).
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(
            new byte[] { 0x00, 0x00 }, unitBytes: 2, bigEndian: true);
        var decode = t.GetMethod("Decode")!;
        dynamic r = decode.Invoke(null, new object[] { stream })!;
        // DescriptorFor(key) yields the Undefined sentinel for an unmatched key (the illegal path; M4.5
        // vectors it). The key is the sentinel's key shape — assert DescriptorFor reports Undefined.
        var descFor = t.GetMethod("DescriptorFor")!;
        dynamic d = descFor.Invoke(null, new object[] { (uint)r.OperationKey })!;
        Assert.True((bool)d.IsUndefined);   // confirm the Undefined-sentinel accessor name at Task 0
    }
}
```

  > **Confirm the key-packing + the Undefined-sentinel accessor.** The opaque `(operation, size)` key
  > packing is the generator's choice — recommend `key = ((uint)operationIndex << 8) | sizeIndex` (or
  > reserve a high bit so it cannot collide with a byte/prefix key; the 68000 declares no byte/prefix
  > rows, so collision is moot for a pure-field-grammar CPU, but a distinct high tag keeps the table
  > uniform). The ASSERTION that matters is "(op, size) is stable + distinct per size" and "unmatched →
  > Undefined." Read `OpcodeDescriptor.Undefined`/`IsUndefined` (CpuEmitter.cs:4038 +
  > `Core/Jit/OpcodeDescriptor.cs`) at Task 0 for the exact sentinel accessor; adjust the assertion to the
  > real member name.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kFieldDecodeWalkTests"`
  Expected: FAIL — `EmitStructuredDecodeWalk` has no field-decode arm; a `FetchUnit.Word` spec still emits
  the byte-fetch walk (which mis-decodes the operword).

- [ ] **Step 3: Add the field-decode arm.** In `src/CpuEmulator.Generators/CpuEmitter.cs`
  `EmitStructuredDecodeWalk` (`:3948`), branch on `model.FetchUnit`/`model.FieldGrammar`: when a field
  grammar is declared (`FetchUnit.Word`), emit the field-decode walk INSTEAD of the byte-fetch walk; else
  emit the existing byte walk unchanged. Emit the `(mask, match)` table + the per-op field metadata, then
  the extraction + key packing:

```csharp
    private static void EmitStructuredDecodeWalk(StringBuilder sb, SpecModel model)
    {
        if (model.FieldGrammar is { } grammar && grammar.FetchUnit == FetchUnit.Word)
        {
            EmitFieldDecodeWalk(sb, model, grammar);   // M4.3a: the word-granular field-decode arm
            return;
        }
        // ── the existing byte-fetch prefix/ModRm/sub-field walk (6502/Z80/8086) — UNCHANGED ──
        var decode = model.Decode!;
        // … (the existing body from :3950 onward, verbatim) …
    }

    /// <summary>M4.3a (ADR 0004 Decision 1): the word-granular, field-decomposed decode walk. Fetches a
    /// 16-bit big-endian operword, matches it against the emitted (mask, match) field-op table, extracts
    /// (operation, size, ea-mode, ea-register) by non-contiguous bit-field extraction, packs the opaque
    /// (operation, size) key, and (Task 4) consumes the extension words the (ea-mode × size) implies so
    /// the COMPUTED length is right. No match ⇒ the Undefined sentinel (the illegal-instruction path; the
    /// vector is M4.5). Length == UnitsConsumed × UnitBytes (= words × 2) throughout — never a field read.</summary>
    private static void EmitFieldDecodeWalk(StringBuilder sb, SpecModel model, FieldGrammarModel grammar)
    {
        // Emit the field-op table: each entry (mask, match, opIndex, sizeShift, sizeWidth, sizeEnc, eaShift).
        // opIndex is the op's position in the grammar (the (operation, size) key's high part).
        sb.AppendLine();
        sb.AppendLine("    // M4.3a: the word-granular field-op table (mask, match, opIndex, size/EA field positions).");
        sb.AppendLine("    private static readonly (uint Mask, uint Match, uint OpIndex, int SizeShift, int SizeWidth, int SizeEnc, int EaShift)[] s_fieldOps =");
        sb.AppendLine("    [");
        for (int i = 0; i < grammar.Ops.Length; i++)
        {
            var op = grammar.Ops[i];
            int sizeEnc = (int)op.SizeEncoding;   // 0 = Standard, 1 = Move (C4)
            sb.AppendLine($"        (0x{op.Mask:X4}u, 0x{op.Match:X4}u, {i}u, {op.SizeShift}, {op.SizeWidth}, {sizeEnc}, {op.EaShift}),");
        }
        sb.AppendLine("    ];");

        sb.AppendLine();
        sb.AppendLine("    /// <summary>The generated word-granular field-decode walk (M4.3a, ADR 0004 Decision 1).</summary>");
        sb.AppendLine("    public static CpuEmulator.Core.Jit.DecodeResult Decode(CpuEmulator.Core.Jit.IFetchStream stream)");
        sb.AppendLine("    {");
        sb.AppendLine("        uint operword = stream.NextUnit();                    // the 16-bit big-endian operword (one word)");
        sb.AppendLine("        for (int i = 0; i < s_fieldOps.Length; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            var f = s_fieldOps[i];");
        sb.AppendLine("            if ((operword & f.Mask) != f.Match) continue;     // non-matching op");
        sb.AppendLine("            // Extract the size bits and map via the size encoding (Standard vs Move — C4).");
        sb.AppendLine("            uint sizeBits = (operword >> f.SizeShift) & (uint)((1 << f.SizeWidth) - 1);");
        sb.AppendLine("            uint size = MapSize(sizeBits, f.SizeEnc);          // 0=b, 1=w, 2=l (an OperandSize index)");
        sb.AppendLine("            // Extract the 6-bit EA field: mode = bits 5-3, register = bits 2-0 (M4.3b consumes these).");
        sb.AppendLine("            uint ea = (operword >> f.EaShift) & 0x3F;");
        sb.AppendLine("            uint eaMode = (ea >> 3) & 7;");
        sb.AppendLine("            uint eaReg  = ea & 7;");
        sb.AppendLine("            // The opaque (operation, size) descriptor key (Ground truth C — opaque to consumers).");
        sb.AppendLine("            uint key = (1u << 24) | (f.OpIndex << 8) | size;   // high tag keeps it distinct from byte/prefix keys");
        sb.AppendLine("            int extWords = ExtensionWordCount(eaMode, eaReg, size);   // M4.3a Task 4: operand-computed");
        sb.AppendLine("            for (int w = 0; w < extWords; w++) stream.NextUnit();      // consume the extension words (length only)");
        sb.AppendLine("            int len = stream.UnitsConsumed * stream.UnitBytes;        // COMPUTED — words × 2");
        sb.AppendLine("            return new CpuEmulator.Core.Jit.DecodeResult(key, len, CpuEmulator.Core.Jit.DecodedOperands.None);");
        sb.AppendLine("        }");
        sb.AppendLine("        // No field op matched ⇒ the illegal-instruction path (the Undefined sentinel; M4.5 vectors it).");
        sb.AppendLine("        int illegalLen = stream.UnitsConsumed * stream.UnitBytes;     // 2 (the operword) — the illegal op is one word");
        sb.AppendLine("        return new CpuEmulator.Core.Jit.DecodeResult(0xFFFFFFFFu, illegalLen, CpuEmulator.Core.Jit.DecodedOperands.None);");
        sb.AppendLine("    }");

        EmitSizeMapHelper(sb);            // MapSize(sizeBits, enc) — Standard vs Move (C4)
        EmitExtensionWordCount(sb);       // ExtensionWordCount(mode, reg, size) — Task 4 (C5)
        // DescriptorFor reuses the keyed dictionary (emitted by EmitKeyedDescriptorTable); 0xFFFFFFFF →
        // Undefined sentinel. (For M4.3a the grammar CPU has no descriptor rows yet — every matched key
        // resolves to Undefined too; the proof asserts the KEY shape + the computed length, not a live op.)
        sb.AppendLine();
        sb.AppendLine("    public static CpuEmulator.Core.Jit.OpcodeDescriptor DescriptorFor(uint operationKey)");
        sb.AppendLine("        => JitDescriptorsByKey.TryGetValue(operationKey, out var d)");
        sb.AppendLine("            ? d : CpuEmulator.Core.Jit.OpcodeDescriptor.Undefined((byte)operationKey);");
    }
```

  with `EmitSizeMapHelper` emitting the Standard (`00→0, 01→1, 10→2`) and Move (`01→0, 11→1, 10→2`)
  mappings:

```csharp
    private static void EmitSizeMapHelper(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Map a field op's size bits to an OperandSize index (0=b,1=w,2=l). Standard");
        sb.AppendLine("    /// (enc 0): 00=b,01=w,10=l. Move (enc 1, the MOVE outlier): 01=b,11=w,10=l (C4).</summary>");
        sb.AppendLine("    private static uint MapSize(uint bits, int enc) => enc == 1");
        sb.AppendLine("        ? bits switch { 1u => 0u, 3u => 1u, 2u => 2u, _ => 0u }   // Move: 01=b,11=w,10=l");
        sb.AppendLine("        : bits switch { 0u => 0u, 1u => 1u, 2u => 2u, _ => 0u };  // Standard: 00=b,01=w,10=l");
    }
```

  > **Task 3 scopes EXTRACTION + KEY; the `ExtensionWordCount` body is a STUB returning 0 here** (so Task 3
  > compiles + the extraction/key assertions pass) and is FILLED in Task 4 (the operand-computed length).
  > Emit `ExtensionWordCount` as `=> 0` in Task 3, then replace its body in Task 4. This keeps each task's
  > test green: Task 3 proves the operword → (op, size) key + the Undefined sentinel; Task 4 proves the
  > computed length. (If the team prefers, Task 3 + Task 4 may be ONE commit — but the TDD split keeps the
  > extraction and the count provably independent.)

- [ ] **Step 4: Add the stub `ExtensionWordCount`.** Emit (in `EmitFieldDecodeWalk`, via
  `EmitExtensionWordCount`) the Task-3 stub:

```csharp
    private static void EmitExtensionWordCount(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("    /// <summary>M4.3a Task 4 (C5): the extension-word count implied by (ea-mode, ea-reg, size).</summary>");
        sb.AppendLine("    private static int ExtensionWordCount(uint eaMode, uint eaReg, uint size) => 0;  // STUB (Task 4 fills it)");
    }
```

- [ ] **Step 5: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kFieldDecodeWalkTests"`
  Expected: PASS — the operword decodes to the opaque (op, size) key; (ADD,.w) != (ADD,.b); the unmatched
  operword returns the Undefined sentinel. (The `computed length` test is Task 4.)

- [ ] **Step 6: Full gate.**
  Run: `dotnet test` → all green. **WATCH-POINT:** the byte-fetch walk (6502/Z80/8086 synthetic decode
  tests, incl. `Z80CompoundDecodeTests`) must be UNAFFECTED — the field-decode arm fires ONLY when a spec
  declares a `FieldGrammar` with `FetchUnit.Word`; every existing spec keeps `FetchUnit.Byte` + no grammar,
  so it takes the unchanged byte arm. Confirm the existing decode-walk tests still pass.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502/Z80 byte-identical —
  neither declares a `FieldGrammar`; the emitter's new branch is not taken for them).

- [ ] **Step 7: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/M68kFieldDecodeWalkTests.cs
git commit -m "$(cat <<'EOF'
feat(generators): add the word-granular field-decode arm to EmitStructuredDecodeWalk (extraction + opaque key)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 4: Operand-computed length — the extension-word count from (ea-mode, size) (TDD) (C5)

> Fill `ExtensionWordCount(eaMode, eaReg, size)` with the operand-computed-length core (ADR Decision 1 /
> C5): the number of extension words an EA mode consumes, mode-and-size-dependent. The walk consumes that
> many words (advancing `UnitsConsumed`), so the COMPUTED length is right per `(mode, size)`. This is the
> genuinely-new "length is not a per-opcode constant; it depends on mode AND size" property — proven with
> the `#imm` size-dependence (1 word for b/w, 2 for l) as the sharpest case.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitExtensionWordCount` — replace the stub)
- Test: `tests/CpuEmulator.Tests/Generators/M68kFieldDecodeWalkTests.cs` (extend — the computed-length portion)

- [ ] **Step 1: Extend the failing test.** Add to `M68kFieldDecodeWalkTests.cs` a computed-length assertion
  per representative `(ea-mode, size)`. Use the EA 6-bit field to select the mode:

```csharp
    [Theory]
    // ea (mode:reg)         size      expected total bytes (operword=2 + extWords×2)
    [InlineData(0x00, /*Dn  */ 1, 2)]   // Dn          : 0 ext words → 2
    [InlineData(0x10, /*(A0)*/ 1, 2)]   // (An)        : 0 ext words → 2
    [InlineData(0x28, /*d16 */ 1, 4)]   // d16(An)     : 1 ext word  → 4   (mode 5)
    [InlineData(0x38, /*absW*/ 1, 4)]   // abs.w       : 1 ext word  → 4   (mode 7, reg 0)
    [InlineData(0x39, /*absL*/ 1, 6)]   // abs.l       : 2 ext words → 6   (mode 7, reg 1)
    [InlineData(0x3C, /*#imm*/ 1, 4)]   // #imm.w      : 1 ext word  → 4   (mode 7, reg 4)
    [InlineData(0x3C, /*#imm*/ 2, 6)]   // #imm.l      : 2 ext words → 6   (size .l — the size-dependence!)
    public void Computed_length_follows_ea_mode_and_size(int ea, int sizeIndex, int expectedBytes)
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // Build an ADD operword (match 0xD000) with the given size bits (7-6) and EA (5-0). Pad the buffer
        // with enough extension-word bytes that NextUnit never runs past the end.
        ushort sizeBits = sizeIndex == 1 ? (ushort)(1 << 6) : (ushort)(2 << 6);   // .w = 01, .l = 10
        ushort operword = (ushort)(0xD000 | sizeBits | (ea & 0x3F));
        var buf = new byte[] { (byte)(operword >> 8), (byte)operword, 0,0, 0,0, 0,0 };  // BE operword + padding
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        dynamic r = t.GetMethod("Decode")!.Invoke(null, new object[] { stream })!;
        Assert.Equal(expectedBytes, (int)r.Length);
    }
```

  > **The `#imm.w` vs `#imm.l` pair (rows 6-7) is the load-bearing assertion** — same EA mode (7:4), the
  > length differs by size (4 vs 6 bytes). That is the "length depends on mode AND size" property ADR
  > Decision 1 names; if any row passes but those two don't, the size-dependence is wrong.

- [ ] **Step 2: Run the extended test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kFieldDecodeWalkTests.Computed_length"`
  Expected: FAIL — `ExtensionWordCount` is the Task-3 stub (`=> 0`), so every length is 2.

- [ ] **Step 3: Fill `ExtensionWordCount`.** In `src/CpuEmulator.Generators/CpuEmitter.cs`
  `EmitExtensionWordCount`, replace the stub body with the C5 count table:

```csharp
    private static void EmitExtensionWordCount(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("    /// <summary>M4.3a (ADR 0004 Decision 1 / C5): the extension-word count an EA mode consumes,");
        sb.AppendLine("    /// mode-AND-size dependent (the operand-computed length core). mode = EA bits 5-3, reg = 2-0,");
        sb.AppendLine("    /// size = 0/1/2 (b/w/l). mode 7 (reg-selected): 0=abs.w(1), 1=abs.l(2), 2=d16(PC)(1),");
        sb.AppendLine("    /// 3=d8(PC,Xn)(1), 4=#imm(1 for b/w, 2 for l). This table is shared with M4.3b (which READS");
        sb.AppendLine("    /// the same extension words to compute the EA address).</summary>");
        sb.AppendLine("    private static int ExtensionWordCount(uint eaMode, uint eaReg, uint size) => eaMode switch");
        sb.AppendLine("    {");
        sb.AppendLine("        0u or 1u or 2u or 3u or 4u => 0,   // Dn / An / (An) / (An)+ / -(An): no extension word");
        sb.AppendLine("        5u or 6u => 1,                     // d16(An) / d8(An,Xn): one extension word");
        sb.AppendLine("        7u => eaReg switch                 // mode 7: the register sub-field selects the form");
        sb.AppendLine("        {");
        sb.AppendLine("            0u => 1,                       // abs.w");
        sb.AppendLine("            1u => 2,                       // abs.l");
        sb.AppendLine("            2u => 1,                       // d16(PC)");
        sb.AppendLine("            3u => 1,                       // d8(PC,Xn)");
        sb.AppendLine("            4u => size == 2u ? 2 : 1,      // #imm: .l = 2 words, .b/.w = 1 (the size-dependence)");
        sb.AppendLine("            _  => 0,                       // illegal mode-7 register (M4.5 vectors)");
        sb.AppendLine("        },");
        sb.AppendLine("        _ => 0,");
        sb.AppendLine("    };");
    }
```

  > **Two-EA MOVE note (C5).** A single-EA op's length is `2 + ExtensionWordCount(ea, size) × 2`. MOVE has
  > TWO EA fields (source + destination), so its length is `2 + (srcExt + dstExt) × 2`. M4.3a's synthetic
  > grammar is single-EA (the proof exercises one EA's count); the two-EA SUM is an M4.4 grammar concern
  > (the real MOVE row declares both EA fields, and the field-decode arm sums both counts). If Task 4 wants
  > to prove the sum NOW, add a minimal two-EA synthetic op (a `FieldOp` with a second EA field) — OPTIONAL;
  > the single-EA size-dependence is the load-bearing proof. Flag the two-EA sum as an M4.4 item in the
  > closeout.

- [ ] **Step 4: Run the extended test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~M68kFieldDecodeWalkTests"`
  Expected: PASS — every `(ea-mode, size)` row computes the right total length; `#imm.w` (4) != `#imm.l`
  (6) (the size-dependence).

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (the field-decode arm + the count table only affect a `FieldGrammar`
  CPU; the byte-fetch walk is unchanged).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502/Z80 byte-identical).

- [ ] **Step 6: Commit (with the doc).**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/M68kFieldDecodeWalkTests.cs \
        docs/superpowers/plans/2026-06-15-m4-3a-word-granular-decode.md
git commit -m "$(cat <<'EOF'
feat(generators): operand-computed length from the (ea-mode, size) extension-word count (C5)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

  > **The doc add to this commit** carries the filled closeout below (the docs-per-PR convention — the plan
  > doc travels with its slice's final commit).

**New-test estimate:** ~7 (the Theory rows).

---

### Task 5: PR

- [ ] **Step 1: Push + open the PR.**
  Run: `git push -u origin feat/m4-3a-word-granular-decode` (after the user approves; merge via PR per
  CLAUDE.md). Open a PR targeting `main`. The PR body claims EXACTLY: a spec can declare a word-granular
  field-decode structure (`FieldGrammar`/`FieldOp` + `FetchUnit.Word`, now CONSUMED — it was
  declared-but-dead); a big-endian word `BufferFetchStream` path exists; `EmitStructuredDecodeWalk` branches
  to a field-decode arm that fetches a 16-bit big-endian operword, extracts `(operation, size, ea-mode,
  ea-register)` by non-contiguous bit-field extraction into an opaque `(operation, size)` key, and COMPUTES
  the instruction length from the `(ea-mode × size)` extension-word count (incl. the `#imm.w` vs `#imm.l`
  size-dependence); a synthetic field-grammar fixture proves the tuple, the key, the per-`(mode,size)`
  computed length, and the illegal-operword Undefined sentinel; the 6502/Z80 are byte-identical (the variant
  is opt-in; the byte/prefix walk is untouched). Name what is STILL deferred: the EA-descriptor + EA-compute
  + the `(An)+`/`-(An)` register write-back = M4.3b; the EA-category legality matrix + retiring
  `RequiredIndexRegister`'s X/Y = M4.3b; the field-pattern dataset + the real `M68000Spec.cs` decode + the
  mnemonic-keyed gzip TomHarte loader = M4.4; the interpreter + the 680x0 TomHarte gate = M4.5. NEVER
  overstate — **no 68000 opcode is live, no 680x0 vector is green.** Include a **Docs Impact** section
  linking ADR 0004 + the M4.3b plan + the M4.1/M4.2 plans.

---

## Plan self-review (completed at write time)

- **Scope coverage (the 5 IN-scope items):**
  - **(1) the field-grammar `DecodeStructure` carrier (D1)** — Task 1. ✓
  - **(2) consume `FetchUnit.Word` (C1)** — Task 1 (parse → `SpecModel.FetchUnit`) + Task 3 (emitter
    branch). ✓
  - **(3) the big-endian word fetch stream (C2)** — Task 2. ✓
  - **(4) the field-decode walk (extraction + key + computed length)** — Task 3 (extraction/key) + Task 4
    (computed length). ✓
  - **(5) the synthetic field-grammar decode proof** — Task 3 + Task 4 tests. ✓
- **OUT-of-scope honored:** NO EA address compute / register write-back (M4.3b); NO EA-category legality
  matrix / `RequiredIndexRegister` retirement (M4.3b); NO dataset row / real `M68000Spec.cs` decode (M4.4);
  NO 680x0 vector asserted green; NO `DecodedOperands` widening (the count-only walk does not need it — C3,
  deferred to M4.3b/M4.5). ✓
- **Placeholder scan:** every code step shows literal code; the ONLY intra-slice stub is `ExtensionWordCount
  => 0` in Task 3, FILLED in Task 4 (a deliberate TDD split, not a TBD); no "similar to Task N". ✓
- **Type/name consistency:** `FieldGrammar(FetchUnit, FieldOp[])` / `FieldOp(Mask, Match, Operation,
  SizeShift, SizeWidth, SizeEncoding, EaShift, LegalEa)` (DecodeStructure.cs + Spec.cs factory + the
  vocabulary test); `FieldGrammarModel`/`FieldOpModel`/`SizeEncodingKind`/`EaCategoryKind` (SpecModel +
  parser); `FetchUnit.Word` (consumed in the emitter branch); `s_fieldOps`/`MapSize`/`ExtensionWordCount`
  (the emitted walk); the opaque key `(1u<<24)|(opIndex<<8)|size`; `bigEndian` (BufferFetchStream). ✓
- **Code/recon contradictions surfaced (the code wins):** (C1) `FetchUnit` declared-but-dead — wired here;
  (C2) no big-endian/wide stream — added; (C3) `DecodedOperands` is Lo/Hi only — count-only walk does not
  need widening, deferred; (C4) per-op size encoding (MOVE outlier) — the carrier expresses it, the proof
  uses Standard; (C5) length is `(mode, size)`-dependent — the count table + the `#imm` size-dependence
  proof. ✓
- **Build-green-after-every-task:** Tasks 1–4 are additive (the field-decode arm fires only for a
  `FieldGrammar` CPU; the byte walk is the unchanged default). The 6502/Z80 byte-identity guard
  (`RegeneratedSpecTests`) gates every task. ✓
- **One TDD split flagged:** `ExtensionWordCount` is a stub in Task 3 (proving extraction/key) and filled
  in Task 4 (proving computed length) — the two properties are proven independently; the implementer may
  fold Tasks 3+4 into one commit if preferred, but the split is the honest TDD shape.

## Closeout (filled at completion)

| Commit | Content | Suite |
|---|---|---|
| `61b3e21` (Task 1) | FieldGrammar/FieldOp carrier + FetchUnit.Word authoring + parse (+ CPUGEN015) | 5119 |
| `4cef02f` (Task 2) | big-endian word BufferFetchStream path (C2) | 5123 |
| (Tasks 3+4 folded) | field-decode arm (extraction + opaque (op,size) key) + operand-computed length from the (ea-mode, size) extension-word count (C5) | 5132 |

> Tasks 3 + 4 were folded into a single commit (the plan sanctions this — "the implementer may fold
> Tasks 3+4 into one commit"). The field-decode arm + the `ExtensionWordCount` table share
> `CpuEmitter.cs`; the extraction/key proof and the computed-length proof were each made green before
> the combined commit, so the two properties are still proven independently within `M68kFieldDecodeWalkTests`.

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | 5116 |
| Final test count | 5132 (5116 + 2 vocabulary + 1 grammar-carrier + 4 BE-stream + 2 extraction/key/Undefined + 7 computed-length Theory rows) |
| `FieldGrammar`/`FetchUnit.Word` declarable + consumed? | YES — declared as a sibling carrier, parsed onto `SpecModel.FetchUnit`, consumed by the emitter's field-decode branch |
| Operword → (operation, size, ea-mode, ea-register) extraction? | YES — synthetic (mask/match match, size via SizeEncoding, EA mode 5-3 + reg 2-0) |
| Operand-computed length per (mode, size)? | YES — incl. #imm.w (4 bytes) vs #imm.l (6 bytes) size-dependence |
| Illegal operword → Undefined sentinel? | YES — unmatched operword keys 0xFFFFFFFF → DescriptorFor → JitOpClass.Undefined |
| Any 68000 opcode live? | NO — framework-only; no 680x0 vector green; the real M68000Spec.cs is untouched (still no DecodeStructure / FieldGrammar, empty Instructions) |
| 6502/Z80 un-regressed? | YES — RegeneratedSpecTests byte-identity green after every task (the field arm fires only for a declared FieldGrammar) |
| `-warnaserror` | clean (0 warnings) after every task |
| Still deferred | EA descriptor + compute + write-back (M4.3b); legality matrix + retiring RequiredIndexRegister X/Y (M4.3b); two-EA MOVE extension-word SUM + the MOVE size-encoding outlier (M4.4 — the carrier expresses both); dataset + real M68000Spec.cs decode + gzip loader (M4.4); live big-endian AddressSpaceFetchStream + interpreter Step + 680x0 TomHarte gate (M4.5) |
| Recommended next chunk | M4.3b — the structured EA descriptor + EA-compute + auto-inc/dec write-back |

## Slice docs index

- **Architecture (Decisions 1 + 2 — decode + addressing):**
  `docs/architecture/0004-68000-decode-addressing-and-exceptions.md`
- **The other half of M4.3 (the EA descriptor + compute):**
  `docs/superpowers/plans/2026-06-15-m4-3b-ea-descriptor-and-compute.md`
- **The M4 foundation (state + bus):** `docs/superpowers/plans/2026-06-15-m4-1-core-width-and-68000-state.md`,
  `docs/superpowers/plans/2026-06-15-m4-2-wide-be-bus.md`
- **The closest decode-variant precedent (Z80 compound decoder):**
  `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1b-compound-decoder.md`
</content>
</invoke>
