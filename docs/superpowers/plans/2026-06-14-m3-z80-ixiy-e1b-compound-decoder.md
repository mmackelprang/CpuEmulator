# M3.4e-1b: The Z80 IX/IY Framework — the declarative compound-prefix decoder (`DD CB d op`)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. This is the SECOND of the two M3.4e-1 framework slices. **e-1a (the `Indexed` AddrMode + EA
> helper + IXh/IXl half-views) MUST be merged first** — e-1b builds on its EA helper. e-1b lands the
> compound DECODER; the DD/FD/DDCB/FDCB OPCODES go live in M3.4e-2/3.

**Goal:** make `DD`/`FD` spec-DECLARED prefixes and teach the generated decode walk to route the compound
`DD CB d op` / `FD CB d op` form — where the displacement byte sits BETWEEN the prefix-pair and the opcode
(ADR 0001 Decision 1's central decode gap: "no single-byte decoder can express it"). This is the DECLARATIVE
extension (D1): `PrefixByte`/`DecodeStructure` grow to express "DD may compound with CB and consumes a
leading displacement," and `EmitStructuredDecodeWalk` reads that declaration generically — NOT a
special-cased `0xDD/0xFD + 0xCB` branch. **e-1b proves the decoder with a SYNTHETIC `DD CB d op` decode-walk
test reaching a stub op; it makes NO DD/FD opcode TomHarte-green** (that is M3.4e-2/3). Every 6502 artifact
stays byte-identical; the whole Z80 (base + CB + ED + block) stays TomHarte-green at the universal Q/WZ/IM
bar.

**Architecture:** e-1a added the `Indexed` AddrMode + the `(IX+d)` EA helper + the IXh/IXl half-views. e-1b
adds the LAST and LARGEST decode reshaping ADR 0001 Decision 1 names: the runtime decode is
`Step → Decode(IFetchStream)` (`CpuEmitter.cs:164`) → `EmitStructuredDecodeWalk` (`:3519`), which today keys
`(first << 8) | op` for ANY prefix byte and consumes exactly 2 bytes. For `DD CB d op` that mis-keys
`0xDDCB` and never consumes `d` or reads the final opcode. The fix is declarative per ADR Decision 1 option
(A) ("the spec declares its decode structure; the generator emits the walk") — the same thesis that makes
the M3.5 cross-arch JIT genericity valid and that the 8086's ModR/M reuses. The descriptor table is already
keyed by a dictionary (`JitDescriptorsByKey`, `:3578`), so it can hold compound keys. e-1b is **additive**:
the existing zero-prefix + single-prefix (CB/ED) paths stay unchanged (they are the degenerate cases of the
declared structure). Every 6502 artifact stays byte-identical.

**Tech Stack:** C# (.NET 10), a Roslyn incremental source generator (`CpuEmulator.Generators`), a console
spec importer (`CpuEmulator.SpecImporter`) that regenerates `Z80Spec.cs` from `z80-opcodes.json` +
`z80-semantics.json`, and xUnit + the SingleStepTests/z80 vectors (TomHarte).

---

## Scope

**IN scope (the compound DECODER; NO opcode goes live):**

1. **The declarative `PrefixByte`/`DecodeStructure` extension (D1).** `PrefixByte` gains
   `CompoundWith` (the byte it may compound with — DD declares `CompoundWith: 0xCB`) and
   `DisplacementBeforeOpcode` (the `DD CB d op` shape — the displacement consumed BEFORE the final opcode).
   `DecodeStructure` carries the richer `PrefixByte[]`. The 6502 declares no prefixes (degenerate, unchanged);
   the Z80 declares `DD`/`FD` as compound-with-`0xCB`-displacement-before-opcode prefixes (alongside the
   existing plain `CB`/`ED`).
2. **`EmitStructuredDecodeWalk` compound routing.** The walk reads the declared prefix structure: a plain
   prefix → key `(prefix<<8)|op` (unchanged); a DD/FD followed by its `CompoundWith` byte → read the
   displacement, then the final opcode, produce a COMPOUND key + surface the displacement; a DD/FD followed
   by a non-`CB` opcode → the DD/FD-core key (the e-2 path; e-1b routes it to a key but no e-2 row is live
   yet). Length stays COMPUTED (`UnitsConsumed × UnitBytes`).
3. **The compound key encoding + the descriptor-table/`InstructionLength` handling** (RECON-FINDING B1).
   A compound key must be distinct from every plain/prefixed key and round-trip through `DescriptorFor`/
   `JitDescriptorsByKey`. The `KeyedDescriptorLiteral` `fixedLength` + the `InstructionLength` path must
   express the 4-byte compound form (RECON-FINDING B2).
4. **A SYNTHETIC `DD CB d op` decode-walk test** proving the stream `DD CB <d> <op>` decodes to
   (compound key, displacement = d, length = 4) and dispatches to a STUB op. No real DD/FD/DDCB opcode.

**OUT of scope (later slices — do NOT reach for them):**

- **Any DD/FD/DDCB/FDCB opcode going live** (the `(IX+d)` re-interpretation, the IX/IY 16-bit ops, the undoc
  IXh/IXl ops, the compound bit/rotate/shift store-copy forms) = M3.4e-2/3. e-1b adds NO dataset rows and
  makes NO `dd *.json` / `dd cb __ *.json` vector green.
- **The `Indexed` AddrMode / EA helper / IXh-IXl half-views** = M3.4e-1a (already merged — e-1b depends on
  it).
- **The redundant-prefix chains** (`DD DD`/`DD FD`/`DD ED`) — D5: NOT modeled (no vectors; YAGNI). The
  declarative structure does NOT declare these as compounds; note in the closeout as unverified-pending.
- **Interrupt SERVICING / ZEXALL / the JIT IL** = M3.5.

> **The honest one-liner for M3.4e-1b's close-state:** `DD`/`FD` are spec-DECLARED prefixes that compound
> with `CB` and consume a leading displacement; `EmitStructuredDecodeWalk` routes the compound `DD CB d op`
> form generically (NOT special-cased), surfacing the displacement + a distinct compound key that round-
> trips through DescriptorFor; a synthetic `DD CB d op` decode-walk test proves the dispatch reaches a stub
> with the right key + displacement + 4-byte length. NO DD/FD/DDCB opcode is live and NO `dd *.json` /
> `dd cb __ *.json` vector is asserted green — that is M3.4e-2/3. The redundant-prefix chains
> (`DD DD`/`DD FD`/`DD ED`) are NOT modeled (D5; no vectors). The whole Z80 (base + CB + ED + block) stays
> TomHarte-green at the universal Q/WZ/IM bar; every 6502 artifact is byte-identical.

---

## Ground truth — the decode seam e-1b reshapes (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 0.**

- **The runtime decode path.** `src/CpuEmulator.Generators/CpuEmitter.cs`: `Step` (`:132-176`) — for a
  structured CPU (`model.Decode is not null`) it calls `Decode(new AddressSpaceFetchStream(_bus, PC))`
  (`:164`), advances `PC += __r.Length` (`:165`), charges `_cycles += __r.Length` (`:172`), calls
  `OnInstructionFetched(__r.Length)` (`:173`), then `Execute(__r.OperationKey)` (`:174`). `Execute` is a
  `switch (opcode)` over `case 0x{OperationKey:X2}: Op{OperationKey:X2}();` (`:243-255`). So a compound key
  flows: decode → key → Execute dispatch → `Op{key}()`. The compound key + its `Op{key}` method name + its
  `__r.Length == 4` all derive from the walk.
- **The decode walk to extend.** `EmitStructuredDecodeWalk` (`:3519-3581`): builds `s_prefixBytes`/
  `s_modRmOpcodes`/`s_subFieldOpcodes` HashSets from `decode.Prefixes.Values`/etc., then emits the `Decode`
  body: `if (s_prefixBytes.Contains(first)) { op = NextUnit(); key = (first<<8)|op; }` (`:3540-3544`) — the
  plain-prefix arm. There is NO compound arm. `DescriptorFor` resolves through `JitDescriptorsByKey`
  (`:3578-3580`). **This is the exact ADR Decision 1 gap.**
- **`PrefixByte`/`DecodeStructure` today.** `src/CpuEmulator.Core/Specification/DecodeStructure.cs`:
  `public sealed record PrefixByte(byte Value);` (`:14`) — a bare byte. `DecodeStructure(PrefixByte[]
  Prefixes, byte[] ModRmOpcodes, byte[] SubFieldOpcodes)` (`:9-12`). The doc comment (`:3-8`) frames the
  declared structure as "the multi-byte / mid-stream-length / sub-field-key properties." The compound form
  is a NEW property the record must express.
- **`decode.Prefixes.Values`** — the emitter reads `decode.Prefixes.Values.Select(p => $"0x{p:X2}")`
  (`:3522`). Confirm the parser's `SpecModel.Decode` shape — how `PrefixByte` is carried from the spec
  through `SpecParser` into the `SpecModel` the emitter consumes (the parser may flatten `PrefixByte` to a
  byte today; the compound fields must survive that pipeline). Read `SpecParser.cs` where it parses the
  `Decode = new(Prefixes: [...], …)` literal + `SpecModel.cs`'s `Decode` carrier.
- **The descriptor-table emit.** `EmitKeyedDescriptorTable` (`:3188-3203`) emits
  `[0x{OperationKey:X}u] = KeyedDescriptorLiteral(...)` per instruction; `KeyedDescriptorLiteral`
  (`:3232-3255`) sets `fixedLength` from `insn.KeyShape` (`PrefixedOpcode => 2`, `OpcodeGroup => 2`,
  `_ => isModRm ? 2 : 1`). A compound row needs a KeyShape (or an equivalent) yielding the right length
  (RECON-FINDING B2). `KeyShape` is the parser-side enum carried on `InstructionModel` — read its
  definition + how `OperationKey`/`KeyShape` are computed from a prefixed `Insn(0xDD, 0xCB?, …)` row.
- **The `InstructionLength` monitor path.** `EmitMonitorSupport` (`:2840-2841`): `InstructionLength(byte
  opcode) => DescriptorFor(opcode).FixedLength`. This takes a single BYTE — a compound instruction's length
  is not addressable by a single byte. Read whether the structured path overrides this (it likely routes
  through the keyed `DescriptorFor`); the compound length must be expressible (RECON-FINDING B2).
- **The synthetic decode-walk fixtures.** The codebase already has synthetic-spec decode-walk tests (the
  M3.1b "synthetic CPU's three-property proof" — search `tests/CpuEmulator.Tests` for the decode-walk /
  `DecodeStructure` / `IFetchStream` tests). e-1b's synthetic test mirrors them: a synthetic spec declaring
  a compound prefix, fed a `DD CB d op` byte stream via an `IFetchStream`, asserting the
  `DecodeResult(key, length, operands)`. Read the existing decode-walk test for the exact `IFetchStream`
  construction + `DecodeResult` shape.
- **The disassembler.** `EmitDisassembler` — the structured arms key by `OperationKey`. A compound row's
  disassembly (`DD CB d op` → e.g. `RLC (IX+d)`) is an e-3 concern; e-1b's stub row need only NOT crash the
  disassembler (it may disassemble as the mnemonic with no operand). Confirm the disassembler tolerates a
  compound key without an `Indexed`/compound arm (it should fall through to the mnemonic).

### RECON FINDINGS that refine the scoped-plan prose (the code WINS — flagged)

> Discovered during write-time recon by reading the source. The implementer MUST re-confirm each at Task 0.

- **B1 — the compound KEY encoding must not collide.** Plain prefixed keys are `(prefix<<8)|op` ≤ 0xFFFF
  (e.g. CB10 = 0xCB10, ED40 = 0xED40). A `DD`-core key (e-2) would be `(0xDD<<8)|op` = 0xDD__. A DDCB
  compound key needs a SHAPE distinct from both — e.g. a 24-bit pack `(0xDDCB << 8) | op` = 0xDDCB__, or a
  dedicated high-bit tag. The `OperationKey` is a `uint` (the dictionary `JitDescriptorsByKey` is
  `Dictionary<uint, …>`, `:3195`; `Execute(uint opcode)`, `:241`), so a 24-bit compound key fits. The
  `case 0x{OperationKey:X2}:` label emit (`:248`) uses `:X2` — CONFIRM it widens correctly for a >2-hex key
  (it interpolates the full value; `:X2` is a MINIMUM width, so 0xDDCB7E renders as `DDCB7E` — fine). The
  `Op{OperationKey:X2}()` method name (`:299`) likewise widens. **Decision (recorded): the compound key is
  `(0xDDCB << 8) | finalOp` for DD-CB and `(0xFDCB << 8) | finalOp` for FD-CB** — a 24-bit key, distinct
  from the 16-bit plain/core keys, computed in the decode walk + matched by the descriptor table. Confirm
  the parser computes the SAME key for the `Insn` row so the dispatch label, the descriptor key, and the
  walk agree (the three must be identical or dispatch misses).
- **B2 — the compound length is 4 and is COMPUTED, not a `ModeLength` lookup.** The compound form consumes
  4 bytes (prefix + CB + displacement + opcode). The walk computes `length = UnitsConsumed × UnitBytes`
  (`:3569`) = 4 naturally once it consumes all four units. The DESCRIPTOR's `FixedLength` (used by
  `InstructionLength` + any length cross-check) must be 4 for a compound row. `KeyedDescriptorLiteral`'s
  `fixedLength` switch (`:3239-3244`) needs a compound arm → 4. This is independent of e-1a's
  `ModeLength("Indexed") => 3` (that is the DD/FD-CORE 3-byte path; the compound is its own shape — see
  e-1a RECON-FINDING A2). Confirm the compound row's KeyShape/length is carried into
  `KeyedDescriptorLiteral` correctly.
- **B3 — the `Indexed` mode is already declarable (e-1a).** e-1b's synthetic compound row uses
  `AddrMode.Indexed` (added in e-1a). e-1b does NOT add the mode; it adds the DECODER that the compound
  `Indexed`-shaped rows (e-3) will need. Confirm e-1a is merged (the `Indexed` member + `JitMode.Indexed`
  exist) before starting e-1b. If e-1b is somehow run before e-1a, the synthetic spec's `AddrMode.Indexed`
  fails to compile — the dependency is enforced by the build.
- **B4 — D5 (redundant-prefix chains) are NOT declared.** The declarative structure declares DD/FD as
  compounding ONLY with `0xCB`. It does NOT declare `DD DD`/`DD FD`/`DD ED` chains (no vectors; YAGNI). The
  walk's compound arm fires ONLY when the byte after DD/FD is the declared `CompoundWith` (CB); a DD
  followed by DD/FD/ED takes the DD-core key arm (e-2's concern; in e-1b it routes to a core key with no
  live row → the Undefined sentinel, harmless). Record this; the closeout notes the chains as
  unverified-pending.
- **B5 — the importer prefix routing already tolerates DDCB tokens.** `OpcodeDataset.cs`
  `RecognizedPrefixes` includes `"0xDDCB"`/`"0xFDCB"` (per the scoped-plan recon); the dataset CAN
  represent a DDCB row as `{prefix:"0xDDCB", opcode:"0xNN"}`. But e-1b adds NO dataset row — the
  compound-decoder proof is SYNTHETIC. The importer's compound-row emission (the `Insn(0xDD, 0xCB, …)` or
  the compound-key `Insn`) is exercised by e-3 when DDCB rows go live. CONFIRM at Task 0 that e-1b need not
  touch `SpecFileEmitter`'s prefix routing (the synthetic spec declares its own `Insn` rows directly,
  bypassing the importer). If the synthetic compound `Insn` factory shape requires an importer/parser
  change to PARSE (e.g. a 2-byte-prefix `Insn` overload), that change is IN e-1b scope (it is the
  spec-authoring surface for a compound row); flag it at Task 0.

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Core/Specification/DecodeStructure.cs` | Modify | Extend `PrefixByte` with `CompoundWith`/`DisplacementBeforeOpcode` (D1). |
| `src/CpuEmulator.Generators/SpecParser.cs` | Modify | Parse the richer `PrefixByte`; carry the compound fields into `SpecModel`; compute the compound `OperationKey`/`KeyShape`. |
| `src/CpuEmulator.Generators/SpecModel.cs` | Modify | Carry the compound prefix metadata + (if needed) a `KeyShape.Compound` member. |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | `EmitStructuredDecodeWalk` compound routing; `KeyedDescriptorLiteral` compound length (=4); the dispatch/Op-name widening confirmation. |
| `tests/CpuEmulator.Tests/Generators/Z80CompoundDecodeTests.cs` | Create | The synthetic `DD CB d op` decode-walk test (key + displacement + length=4 → stub). |
| `tests/CpuEmulator.Tests/Generators/Z80CompoundPrefixVocabularyTests.cs` | Create | `PrefixByte(CompoundWith, DisplacementBeforeOpcode)` carries its fields; a spec declaring it parses. |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502
> additivity guards + the whole Z80 staying green at the universal Q/WZ/IM bar), then commit. Tasks are
> dependency-ordered so the suite builds and stays green after every task. Literal code is given for every
> load-bearing piece. The synthetic-spec tests decouple from any real regen — e-1b adds NO real Z80
> dataset row, so the real `Z80Spec.cs` does NOT change except (optionally) the `Decode` declaration if
> DD/FD are declared on the real spec in Task 4 (see that task's gate).

### Task 0: Baseline + decode-seam recon (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch (off the e-1a merge).** Create the branch off the current main (which now includes
  e-1a):
  Run: `git switch -c feat/m3-z80-ixiy-e1b`
  Expected: on the new branch; `git log` shows the e-1a commits (Indexed AddrMode, EmitZ80IndexedEa,
  IXh/IXl half-views) merged. **CONFIRM e-1a is present** (RECON-FINDING B3): grep `Indexed` in
  `AddrMode.cs` + `EmitZ80IndexedEa` in `CpuEmitter.cs`.

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test`
  Expected: 0 failures, 0 unexpected skips. Record the EXACT count (the closeout pins it).
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean.

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each cited surface holds:**
  - `src/CpuEmulator.Core/Specification/DecodeStructure.cs:1-15` (the bare `PrefixByte(byte Value)` + the
    `DecodeStructure` record + the doc comment).
  - `CpuEmitter.cs:132-176` (`Step` — the `Decode(...)`/`__r.Length`/`Execute(__r.OperationKey)` flow),
    `:241-255` (`Execute(uint opcode)` + the `case 0x{OperationKey:X2}: Op{OperationKey:X2}();` dispatch),
    `:297-300` (the `Op{OperationKey:X2}()` method-name emit), `:3519-3581` (`EmitStructuredDecodeWalk` —
    the plain-prefix arm at `:3540-3544`; `DescriptorFor` via `JitDescriptorsByKey` at `:3578`),
    `:3188-3203` (`EmitKeyedDescriptorTable`), `:3232-3255` (`KeyedDescriptorLiteral` `fixedLength`),
    `:2840-2841` (`InstructionLength`).
  - `SpecParser.cs` — where `Decode = new(Prefixes: [...], …)` is parsed (how `PrefixByte(0xCB)` becomes a
    byte in `SpecModel.Decode`), and where `OperationKey`/`KeyShape` are computed for a prefixed `Insn`
    row. `SpecModel.cs` — the `Decode` carrier + the `KeyShape` enum.
  - The EXISTING synthetic decode-walk test (search `tests/CpuEmulator.Tests` for `IFetchStream` /
    `DecodeStructure` / `DecodeResult` — the M3.1b three-property proof) for the synthetic-spec +
    `IFetchStream` construction shape e-1b mirrors.
  - **RECON-FINDING B5:** confirm whether a compound `Insn` row needs a new `Insn` factory overload to
    PARSE (a 2-prefix-byte row). Read the existing `Insn(0xCB, 0x10, …)` / `Insn(0xED, 0x44, …)` prefixed
    overload in `Spec.cs`/`InstructionDef.cs` — does it carry ONE prefix byte? A compound row carries
    TWO (DD + CB) + the final opcode. Decide the spec-authoring shape (an `Insn` overload, or encode the
    compound in the `OperationKey` the parser computes from a single declared prefix + the `Indexed` mode).
    **Decision (recorded, refine at Task 0):** prefer carrying the compound in the declared PREFIX
    structure (DD declares `CompoundWith: 0xCB`) + a single `Insn(0xDD, finalOp, …, AddrMode.Indexed)` row
    whose key the parser computes as the compound `(0xDDCB<<8)|finalOp` BECAUSE the prefix declared the
    compound — avoiding a 2-prefix `Insn` overload. Confirm this is expressible; if not, add the overload.

- [ ] **Step 4:** No commit (read-only). Proceed to Task 1.

---

### Task 1: Extend `PrefixByte`/`DecodeStructure` (the declarative D1 shape) (TDD)

> Grow `PrefixByte` with `CompoundWith` (the byte it compounds with) + `DisplacementBeforeOpcode` (the
> `DD CB d op` shape). Default both so the existing plain `PrefixByte(0xCB)`/`PrefixByte(0xED)` declarations
> + the 6502's empty `Prefixes` are unchanged. Proven by a vocabulary test: the record carries its fields;
> a spec declaring a compound prefix compiles.

**Files:**
- Modify: `src/CpuEmulator.Core/Specification/DecodeStructure.cs`
- Test: `tests/CpuEmulator.Tests/Generators/Z80CompoundPrefixVocabularyTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/Z80CompoundPrefixVocabularyTests.cs`:

```csharp
using CpuEmulator.Core.Specification;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CompoundPrefixVocabularyTests
{
    [Fact]
    public void Plain_prefix_defaults_to_no_compound()
    {
        var cb = new PrefixByte(0xCB);
        Assert.Equal(0xCB, cb.Value);
        Assert.Null(cb.CompoundWith);
        Assert.False(cb.DisplacementBeforeOpcode);
    }

    [Fact]
    public void Compound_prefix_carries_its_compound_and_displacement_flag()
    {
        var dd = new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true);
        Assert.Equal(0xDD, dd.Value);
        Assert.Equal((byte)0xCB, dd.CompoundWith);
        Assert.True(dd.DisplacementBeforeOpcode);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80CompoundPrefixVocabularyTests"`
  Expected: FAIL — `PrefixByte` has no `CompoundWith`/`DisplacementBeforeOpcode`.

- [ ] **Step 3: Extend `PrefixByte`.** In `src/CpuEmulator.Core/Specification/DecodeStructure.cs`, REPLACE
  the bare record:

```csharp
/// <summary>A prefix byte the decode walk switches "page" on. M3.4e-1b (Z80 IX/IY) extends it to express
/// a COMPOUND prefix: <see cref="CompoundWith"/> names a second prefix byte that, when it FOLLOWS this
/// one, forms a compound page (the Z80 <c>DD CB</c>/<c>FD CB</c>), and <see cref="DisplacementBeforeOpcode"/>
/// declares that the compound consumes a DISPLACEMENT byte BEFORE the final opcode (the <c>DD CB d op</c>
/// shape — ADR 0001 Decision 1: "no single-byte decoder can express it"). A plain prefix (the 6502 has
/// none; the Z80 <c>CB</c>/<c>ED</c>) leaves both at their defaults, so the existing declarations +
/// the degenerate walk are unchanged.</summary>
public sealed record PrefixByte(
    byte Value,
    byte? CompoundWith = null,
    bool DisplacementBeforeOpcode = false);
```

  > **Why defaults.** Every existing `PrefixByte(0xCB)`/`PrefixByte(0xED)` call site (the real `Z80Spec.cs`
  > `Decode`, the M3.4c/d synthetic ED specs, the M3.1b decode-walk synthetic spec) keeps working unchanged
  > — `CompoundWith` defaults `null`, `DisplacementBeforeOpcode` defaults `false`. The 6502 declares no
  > `Decode` at all. So this record change touches NO existing behavior; it only ADDS expressible state.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80CompoundPrefixVocabularyTests"`
  Expected: PASS.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (the record change is additive; every existing `PrefixByte(...)` call
  compiles unchanged).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 declares no prefixes;
  byte-identical).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Core/Specification/DecodeStructure.cs \
        tests/CpuEmulator.Tests/Generators/Z80CompoundPrefixVocabularyTests.cs
git commit -m "$(cat <<'EOF'
feat(core): extend PrefixByte with CompoundWith/DisplacementBeforeOpcode (declarative DD CB d op; D1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 2: Carry the compound prefix through the parser → `SpecModel`; compute the compound key (TDD)

> The parser must read the richer `PrefixByte` (the compound fields) into `SpecModel.Decode`, and compute
> the COMPOUND `OperationKey`/`KeyShape` for a row that the declared compound prefix governs (RECON-FINDING
> B1: `(0xDDCB<<8)|finalOp`). This is the parser-side plumbing the walk + the descriptor table both consume,
> so all three agree on the key. Proven by a generator-text test: a synthetic spec with a declared compound
> prefix + a compound row emits the expected compound key in the descriptor table + the dispatch.

**Files:**
- Modify: `src/CpuEmulator.Generators/SpecParser.cs` (parse the compound fields; compute the compound key)
- Modify: `src/CpuEmulator.Generators/SpecModel.cs` (carry the compound metadata + `KeyShape.Compound`)
- Test: `tests/CpuEmulator.Tests/Generators/Z80CompoundDecodeTests.cs` (create — the parser/key portion)

> **Design decision (recorded, per RECON-FINDING B5).** The compound row is authored as a single prefixed
> `Insn` governed by the DECLARED compound prefix — the spec declares `PrefixByte(0xDD, CompoundWith: 0xCB,
> DisplacementBeforeOpcode: true)` and a row `Insn(0xDD, finalOp, "…", AddrMode.Indexed, [stub])`; the
> parser, seeing the row's prefix `0xDD` is a compound-declaring prefix AND the row's mode is `Indexed`,
> computes the compound key `(0xDDCB<<8)|finalOp` and tags `KeyShape.Compound`. (If Task 0 finds this
> ambiguous — e.g. a DD-core `Indexed` row, e-2, also uses prefix 0xDD + mode Indexed but is NOT compound —
> disambiguate with an explicit marker: prefer a dedicated compound `Insn` overload `Insn(0xDD, 0xCB,
> finalOp, …)` that names BOTH prefix bytes. Decide at Task 0 Step 3; the literal code below assumes the
> explicit 2-prefix overload for unambiguity, which is the safer shape.)**

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/Z80CompoundDecodeTests.cs` (the key/descriptor portion; Task 3 adds
  the runtime decode-walk portion). A synthetic spec declares the compound prefix + ONE compound row;
  assert the descriptor table + the dispatch carry the compound key `0xDDCB7E` (for finalOp 0x7E).

```csharp
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CompoundDecodeTests
{
    // A synthetic spec declaring DD as a compound-with-CB, displacement-before-opcode prefix, plus ONE
    // compound row (the stub). The 2-prefix Insn overload names both DD and CB + the final opcode.
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcb")]
        public static class DdcbSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("WZ", 16), new("IX", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)],
                ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                // The compound stub row: DD CB d 7E. Indexed mode (e-1a). The body is a stub (no real op).
                Insn(0xDD, 0xCB, 0x7E, "RLC", AddrMode.Indexed, [Transfer("A", "A")]),
            ];
        }
        """;

    [Fact]
    public void Compound_row_emits_the_24bit_compound_key_in_table_and_dispatch()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        // The compound key is (0xDDCB << 8) | 0x7E = 0xDDCB7E. It appears in the keyed descriptor table,
        // the Execute dispatch, and the Op-method name — all three must agree (B1).
        Assert.Contains("0xDDCB7E", result.GeneratedText);
        Assert.Contains("OpDDCB7E", result.GeneratedText);
    }
}
```

  > **The `Insn(0xDD, 0xCB, 0x7E, …)` overload** is the 2-prefix authoring shape (RECON-FINDING B5 /
  > the Task 2 design decision). If Task 0 lands on the single-prefix + compound-declared shape instead,
  > the row becomes `Insn(0xDD, 0x7E, …, AddrMode.Indexed, …)` and the parser infers the compound from the
  > declared `CompoundWith` — adjust the test + the literal code to match the chosen shape. The ASSERTION
  > (the 24-bit key in table + dispatch + Op-name) is invariant to the authoring shape.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80CompoundDecodeTests"`
  Expected: FAIL — the compound `Insn` overload / key computation does not exist; the key is not emitted.

- [ ] **Step 3: Add the compound `Insn` overload + `KeyShape.Compound` + the key computation.**
  - In `src/CpuEmulator.Core/Specification/Spec.cs` (+ `InstructionDef.cs`), add the 2-prefix `Insn`
    overload (if the Task 0 decision chose the explicit shape):

```csharp
    /// <summary>M3.4e-1b: a COMPOUND-prefixed instruction (the Z80 DD CB d op / FD CB d op). Names BOTH
    /// prefix bytes + the final opcode; the displacement sits between the prefix-pair and the opcode
    /// (declared via the PrefixByte's DisplacementBeforeOpcode). The OperationKey is the compound pack
    /// (prefix1 &lt;&lt; 16) | (prefix2 &lt;&lt; 8) | finalOpcode.</summary>
    public static InstructionDef Insn(
        byte prefix1, byte prefix2, byte finalOpcode, string mnemonic, AddrMode mode, Op[] ops)
        => new(/* compound-key encoding per InstructionDef */ …, mnemonic, mode, ops);
```

  > Read `InstructionDef.cs` for the exact constructor shape (how `Opcode`/`OperationKey`/`KeyShape` are
  > carried) + how the existing 1-prefix `Insn(byte prefix, byte opcode, …)` overload computes its
  > `OperationKey = (prefix<<8)|opcode` + `KeyShape.PrefixedOpcode`. The compound overload computes
  > `OperationKey = (prefix1<<16)|(prefix2<<8)|finalOpcode` + `KeyShape.Compound`.

  - In `src/CpuEmulator.Generators/SpecModel.cs`, add the `KeyShape.Compound` member (mirror the parser's
    `KeyShape` enum):

```csharp
    PrefixedOpcode,   // (existing) key = (prefix << 8) | opcode
    Compound,         // M3.4e-1b: key = (prefix1 << 16) | (prefix2 << 8) | finalOpcode (DD CB d op)
```

  - In `src/CpuEmulator.Generators/SpecParser.cs`: parse the richer `PrefixByte(Value, CompoundWith,
    DisplacementBeforeOpcode)` into `SpecModel.Decode` (the parser today flattens `PrefixByte` to a byte —
    extend it to carry the compound fields); compute the compound `OperationKey`/`KeyShape` for the
    compound `Insn` row. Read the existing prefixed-row key computation + extend the `PrefixByte` literal
    parse to read the two new arguments (the `CompoundWith` byte + the `DisplacementBeforeOpcode` bool).

  > **The three-way agreement (B1).** After this task the compound key `0xDDCB7E` is computed identically
  > in (a) the `Insn` row's `OperationKey` (the dispatch label `case 0xDDCB7E:` + `OpDDCB7E()`), and (b)
  > the keyed descriptor table `[0xDDCB7Eu] = …`. Task 3 makes the decode WALK compute the SAME key from
  > the byte stream, closing the loop. If any of the three diverge, dispatch misses → the Undefined
  > sentinel; the Task 3 decode-walk test catches it.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80CompoundDecodeTests"`
  Expected: PASS — the compound key appears in the table + dispatch + Op-name.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (the compound `Insn` overload + `KeyShape.Compound` are additive; no
  existing row uses them).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical — it
  declares no compound row).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Core/Specification/Spec.cs src/CpuEmulator.Core/Specification/InstructionDef.cs \
        src/CpuEmulator.Generators/SpecModel.cs src/CpuEmulator.Generators/SpecParser.cs \
        tests/CpuEmulator.Tests/Generators/Z80CompoundDecodeTests.cs
git commit -m "$(cat <<'EOF'
feat(generators): carry compound prefix through the parser + compute the DD CB d op compound key (B1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 3: `EmitStructuredDecodeWalk` compound routing + the 4-byte descriptor length (TDD)

> Teach the decode WALK to recognize a declared compound prefix in the byte stream: read DD/FD, see the
> next byte is its `CompoundWith` (CB), then (because `DisplacementBeforeOpcode`) read the DISPLACEMENT,
> then the final opcode; produce the compound key `(0xDDCB<<8)|op` + surface the displacement as an operand
> byte + length 4. The DD/FD-core key arm (non-CB next byte) routes to `(0xDD<<8)|op` (e-2's path; no live
> row in e-1b). Plus the `KeyedDescriptorLiteral` compound `fixedLength => 4` (RECON-FINDING B2). Proven by
> a synthetic `DD CB d op` decode-walk test (the runtime decode reaching a stub op with the right key +
> displacement + length).

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitStructuredDecodeWalk` compound arm;
  `KeyedDescriptorLiteral` compound length)
- Test: `tests/CpuEmulator.Tests/Generators/Z80CompoundDecodeTests.cs` (extend — the decode-walk portion)

- [ ] **Step 1: Extend the failing test.** Add to `Z80CompoundDecodeTests.cs` a runtime decode-walk
  assertion: build the synthetic CPU, feed it a `DD CB <d> <op>` byte stream, and assert
  `Decode(IFetchStream)` returns `(key = 0xDDCB7E, length = 4, operands carry d)`. Mirror the EXISTING
  decode-walk synthetic test's `IFetchStream`/`DecodeResult` construction (read it at Task 0).

```csharp
    [Fact]
    public void DD_CB_d_op_stream_decodes_to_compound_key_displacement_and_length_4()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.DdcbCpu");
        // Feed the stream DD CB 05 7E (displacement = 0x05, final opcode = 0x7E).
        var bytes = new byte[] { 0xDD, 0xCB, 0x05, 0x7E };
        // Construct the spec's IFetchStream over `bytes` (mirror the existing decode-walk test's helper),
        // call the static Decode(IFetchStream), and inspect the DecodeResult.
        var decode = t.GetMethod("Decode")!;
        var stream = MakeFetchStream(bytes);           // the existing test's fetch-stream builder
        dynamic r = decode.Invoke(null, new object[] { stream })!;
        Assert.Equal(0xDDCB7Eu, (uint)r.OperationKey); // the compound key (B1)
        Assert.Equal(4, (int)r.Length);                // prefix + CB + displacement + opcode (B2)
        Assert.Equal((byte)0x05, (byte)r.Operands.Lo); // the displacement surfaced as the first operand
    }
```

  > **Confirm the `DecodeResult` operand shape.** Read the existing decode-walk test for how operands are
  > exposed (`r.Operands.Lo`/`.Hi`/`.Count` per `CpuEmitter.cs:3499,3570` `new(lo, hi, count)`). The
  > displacement should land in `lo` (the first consumed operand byte). If the existing tests assert
  > operands differently, mirror that. Also confirm `MakeFetchStream` — reuse the existing test's exact
  > `IFetchStream` construction (likely an `ArrayFetchStream` or the `AddressSpaceFetchStream` over a
  > mapped bus); do not invent a new one.

  > **The synthetic partial** (the `DdcbCpu` hand-written part) must be added to `Source` for the
  > `CompileAndLoadType` to succeed — the `IAddressSpace _bus` + `public byte Q; public int Im;` shape
  > (M3.4d deviation #1), the stub `OnInstructionFetched`, `ReadBus`/`WriteBus`, `TryServiceInterrupt =>
  > false`, `HandleUndefinedOpcode`. Add it to the `Spec` string from Task 2 (extend that const) so both
  > the key-emit test and the decode-walk test share one synthetic CPU.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80CompoundDecodeTests"`
  Expected: FAIL — the walk keys `(0xDD<<8)|0xCB = 0xDDCB` and consumes only 2 bytes; the displacement +
  final opcode are not consumed; key ≠ 0xDDCB7E, length ≠ 4.

- [ ] **Step 3: Add the compound arm to `EmitStructuredDecodeWalk`.** In
  `src/CpuEmulator.Generators/CpuEmitter.cs` `EmitStructuredDecodeWalk` (`:3519-3581`), the walk currently
  emits the plain-prefix arm from the flat `s_prefixBytes` HashSet. To route the compound, the emitter
  needs the compound METADATA (which prefixes compound, with what, displacement-before). Emit a compound
  lookup table alongside `s_prefixBytes` and a compound arm in the `Decode` body. The generated walk
  becomes (the compound arm INSIDE the `if (s_prefixBytes.Contains(first))` branch):

```csharp
        // (emit, near s_prefixBytes) — the compound metadata from the declared PrefixByte fields:
        // s_compoundWith maps a prefix byte -> its CompoundWith byte; s_dispBeforeOpcode the flag.
        sb.AppendLine($"    private static readonly System.Collections.Generic.Dictionary<uint, uint> s_compoundWith = new() {{ {compoundWithPairs} }};");
        sb.AppendLine($"    private static readonly System.Collections.Generic.HashSet<uint> s_dispBeforeOpcode = new() {{ {dispBeforeSet} }};");
```
  and in the `Decode` body, REPLACE the plain-prefix arm (`:3540-3544`) with the compound-aware arm:

```csharp
        sb.AppendLine("        if (s_prefixBytes.Contains(first))");
        sb.AppendLine("        {");
        sb.AppendLine("            uint op = stream.NextUnit();                       // the byte after the prefix");
        sb.AppendLine("            if (s_compoundWith.TryGetValue(first, out var second) && op == second)");
        sb.AppendLine("            {");
        sb.AppendLine("                // COMPOUND prefix (Z80 DD CB / FD CB). If the displacement precedes");
        sb.AppendLine("                // the opcode (DD CB d op), consume it BEFORE the final opcode byte.");
        sb.AppendLine("                if (s_dispBeforeOpcode.Contains(first))");
        sb.AppendLine("                {");
        sb.AppendLine("                    lo = (byte)stream.NextUnit(); count = 1;    // the displacement");
        sb.AppendLine("                    uint finalOp = stream.NextUnit();           // the final opcode");
        sb.AppendLine("                    key = (first << 16) | (second << 8) | finalOp;   // KeyShape.Compound");
        sb.AppendLine("                }");
        sb.AppendLine("                else");
        sb.AppendLine("                {");
        sb.AppendLine("                    uint finalOp = stream.NextUnit();");
        sb.AppendLine("                    key = (first << 16) | (second << 8) | finalOp;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine("                key = (first << 8) | op;                       // KeyShape.PrefixedOpcode (plain / DD-core)");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
```

  > **The compound key MUST equal Task 2's parser key (B1):** `(first<<16)|(second<<8)|finalOp` =
  > `(0xDD<<16)|(0xCB<<8)|0x7E` = `0xDDCB7E`. Cross-check against the `OperationKey` the parser computes
  > (Task 2) — they must be byte-identical or dispatch misses. The `compoundWithPairs`/`dispBeforeSet`
  > generation reads `decode.Prefixes` for prefixes with non-null `CompoundWith` (the emitter now needs
  > the richer `PrefixByte` carried through `SpecModel.Decode` — Task 2 provides it).
  >
  > **D5 (B4):** a DD followed by a non-CB byte (e.g. DD DD) takes the `else` plain/DD-core arm → key
  > `(0xDD<<8)|op` → no live row (e-1b) → the Undefined sentinel. The redundant-prefix chains are NOT
  > specially handled; the closeout notes them unverified-pending.

- [ ] **Step 4: Add the compound `fixedLength` to `KeyedDescriptorLiteral`.** In `KeyedDescriptorLiteral`
  (`:3239-3244`), add the `KeyShape.Compound` arm → 4 (RECON-FINDING B2):

```csharp
        int fixedLength = insn.KeyShape switch
        {
            KeyShape.PrefixedOpcode => 2,   // prefix + opcode
            KeyShape.OpcodeGroup => 2,       // opcode + the sub-field byte
            KeyShape.Compound => 4,          // M3.4e-1b: prefix + CB + displacement + opcode
            _ => isModRm ? 2 : 1,            // ModRm base (opcode + modrm) | plain single byte
        };
```

- [ ] **Step 5: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80CompoundDecodeTests"`
  Expected: PASS — the stream decodes to (0xDDCB7E, length 4, displacement 0x05).

- [ ] **Step 6: Full gate.**
  Run: `dotnet test` → all green. **WATCH-POINT:** the existing M3.1b decode-walk synthetic test (the
  three-property proof) + the real Z80's CB/ED decode must be UNAFFECTED — the compound arm only fires when
  a prefix has a non-null `CompoundWith` AND the next byte matches it. CB/ED declare no `CompoundWith`, so
  they take the unchanged plain-prefix `else` arm. Confirm the existing decode-walk test still passes.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 declares no
  `Decode`; byte-identical).

- [ ] **Step 7: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/Z80CompoundDecodeTests.cs
git commit -m "$(cat <<'EOF'
feat(generators): route the compound DD CB d op form in EmitStructuredDecodeWalk (declarative; length=4)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1 (the decode-walk assertion extends Task 2's file).

---

### Task 4: Declare DD/FD on the real Z80 `Decode` (optional structural anchor) + whole-Z80 re-green (gate)

> Decide whether to declare `DD`/`FD` as compound prefixes on the REAL `z80-semantics.json`/`Z80Spec.cs`
> `Decode` NOW (a structural anchor for e-2/e-3) or DEFER it to e-2 (when the first DD/FD row goes live).
> Either is acceptable; the plan RECOMMENDS deferring the real declaration to e-2 to keep e-1b purely
> additive-with-no-live-row — BUT verify the whole Z80 stays green regardless.

**Files:**
- (Recommended) NONE — defer the real `Decode` DD/FD declaration to e-2.
- (Alternative) Modify `tools/CpuEmulator.SpecImporter/data/z80-semantics.json` (declare DD/FD compound) +
  regen `Z80Spec.cs`.

- [ ] **Step 1: Decision — defer or anchor.** The recommended path: do NOT declare DD/FD on the real spec
  in e-1b. Rationale: declaring a prefix the generator cross-checks must back ≥1 emitted prefixed row
  (`SpecFileEmitter` CPUGEN012 — "every declared prefix backs >=1 emitted prefixed Insn row"; read it at
  Task 0). Since e-1b adds NO live DD/FD row, declaring DD/FD would either trip that cross-check or require
  suppressing it — churn with no benefit. The DECODER machinery is fully proven by the synthetic test
  (Tasks 1–3). e-2 declares DD/FD when it adds the first DD/FD row. **Record this decision; if Task 0 shows
  CPUGEN012 does NOT fire for a compound prefix (e.g. it only checks single-byte prefixes), the anchor is
  free and may be added — but default to defer.**

- [ ] **Step 2: Whole-Z80 re-green (the e-1b exit criterion).** Even with no real-spec change, confirm the
  compound-decoder machinery did not perturb the existing decode:
  Run the staged + full Z80 UAT:
```bash
CPUEMULATOR_Z80_REGS_ONLY=1 dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
```
  Expected: base + CB + ED-core + ED-block **0 failures** — CB/ED declare no `CompoundWith`, so the
  compound arm never fires for them; their decode is byte-identical. Any failure means the compound arm
  leaked into the plain path — debug with `superpowers:systematic-debugging` (it should not, per the
  Task 3 watch-point).

- [ ] **Step 3: Confirm the regression bar + commit (the doc).**
  Run: `dotnet test` → full suite green (record the count).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical).

```bash
git add docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1b-compound-decoder.md
git commit -m "$(cat <<'EOF'
docs(z80): record M3.4e-1b compound-decoder close-state (synthetic-proven; no DD/FD opcode live)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~0 (gate-only).

---

### Task 5: PR

- [ ] **Step 1: Push + open the PR.**
  Run: `git push -u origin feat/m3-z80-ixiy-e1b` (after the user approves; merge via PR per CLAUDE.md).
  Open a PR targeting `main`. The PR body claims EXACTLY: `DD`/`FD` are spec-DECLARABLE compound prefixes
  (`PrefixByte.CompoundWith`/`DisplacementBeforeOpcode`, D1); `EmitStructuredDecodeWalk` routes the compound
  `DD CB d op` form generically (NOT special-cased), surfacing the displacement + a distinct 24-bit compound
  key (`0xDDCB__`) that round-trips through the parser key, the dispatch, and the keyed descriptor table
  (length 4); a synthetic `DD CB d op` decode-walk test proves the dispatch reaches a stub with the right
  key + displacement + length; the whole Z80 (base + CB + ED + block) re-validated at the universal Q/WZ/IM
  bar; 6502 byte-identical. Name what is STILL deferred: any DD/FD/DDCB opcode going live = M3.4e-2/3 (no
  `dd *.json` / `dd cb __ *.json` vector is green here); the redundant-prefix chains (`DD DD`/`DD FD`/
  `DD ED`) are NOT modeled (D5; no vectors; unverified-pending); interrupt servicing + ZEXALL + the JIT =
  M3.5. NEVER overstate. Include a **Docs Impact** section linking the overview + the scoped plan + the
  e-1a plan.

---

## Plan self-review (completed at write time)

- **Scope coverage (the 4 IN-scope items):**
  - **(1) the `PrefixByte`/`DecodeStructure` D1 extension** — Task 1. ✓
  - **(2) `EmitStructuredDecodeWalk` compound routing** — Task 3. ✓
  - **(3) the compound key + descriptor/length handling** — Task 2 (key/parser) + Task 3 Step 4
    (descriptor length 4). ✓
  - **(4) the synthetic `DD CB d op` decode-walk test → stub** — Task 3 Step 1. ✓
- **OUT-of-scope honored:** NO dataset row added; NO `dd *.json` / `dd cb __ *.json` vector asserted green;
  the redundant-prefix chains NOT declared (D5 / B4); the real-spec DD/FD declaration DEFERRED to e-2
  (Task 4 decision). ✓
- **Placeholder scan:** every code step shows literal code; the `Insn` overload body has a `…` ONLY where
  the implementer must read `InstructionDef.cs` for the exact constructor shape (flagged, not a TBD —
  the key encoding is specified: `(prefix1<<16)|(prefix2<<8)|finalOpcode`). No "similar to Task N". ✓
- **Type/name consistency:** `PrefixByte(Value, CompoundWith, DisplacementBeforeOpcode)` (DecodeStructure +
  the vocabulary test + the synthetic specs); `KeyShape.Compound` (SpecModel + the parser + the
  KeyedDescriptorLiteral arm); the compound key `(prefix1<<16)|(prefix2<<8)|finalOp` = `0xDDCB7E` computed
  IDENTICALLY in the parser (Task 2) + the walk (Task 3) + asserted in both tests (B1); `s_compoundWith`/
  `s_dispBeforeOpcode` (the emitted walk tables). ✓
- **Code/recon contradictions surfaced (the code wins):** (B1) the 24-bit compound key + the three-way
  agreement; (B2) length 4 is COMPUTED + the descriptor `fixedLength` arm; (B3) `Indexed` from e-1a (the
  build enforces the dependency); (B4/D5) the chains are NOT declared; (B5) the authoring shape (the
  2-prefix `Insn` overload vs the declared-compound inference — decided at Task 0). ✓
- **Build-green-after-every-task:** Tasks 1–3 are additive (the compound machinery is dormant for CB/ED —
  they declare no `CompoundWith`); Task 4 is gate-only. The existing M3.1b decode-walk test + the real
  Z80 CB/ED decode are the regression guard (Task 3 watch-point). ✓
- **One open authoring decision flagged (Task 0 Step 3 / B5):** the compound-row authoring shape (explicit
  2-prefix `Insn` overload vs single-prefix + declared-compound inference). The literal code assumes the
  explicit overload (safer/unambiguous); the implementer confirms at Task 0 and adjusts if the codebase's
  `Insn`/parser conventions favor the inference shape. The ASSERTIONS are invariant to the choice.

## Closeout (filled at completion)

| Commit | Content | Suite |
|---|---|---|
| (Task 1) | PrefixByte CompoundWith/DisplacementBeforeOpcode (D1) | green |
| (Task 2) | parser carries compound prefix + computes the compound key | green |
| (Task 3) | EmitStructuredDecodeWalk compound routing + descriptor length 4 | green |
| (Task 4) | whole-Z80 re-green + close-state doc | green |

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | _fill_ |
| Final test count | _fill_ |
| `DD`/`FD` declarable as compound prefixes? | YES — PrefixByte.CompoundWith/DisplacementBeforeOpcode |
| Compound `DD CB d op` decodes correctly? | YES (synthetic) — key 0xDDCB__, displacement surfaced, length 4 |
| Compound key round-trips (parser == walk == dispatch == descriptor)? | YES (B1) |
| Any DD/FD/DDCB opcode live? | NO — e-1b is decoder-only; no `dd *.json` / `dd cb __ *.json` asserted green |
| Redundant-prefix chains modeled? | NO — D5/B4; not declared; unverified-pending (no vectors) |
| Real-spec DD/FD declaration? | DEFERRED to e-2 (Task 4 decision) |
| Whole-Z80 UAT (full) | base + CB + ED re-green, 0 failures with final Q/WZ/IM on every case |
| 6502 un-regressed? | YES — RegeneratedSpecTests byte-identity green |
| Any 6502 file changed? | NONE (additive) |
| `-warnaserror` | clean |
| Still deferred | DD/FD opcodes live (M3.4e-2); DDCB/FDCB opcodes live (M3.4e-3); servicing + ZEXALL + JIT (M3.5) |
| Recommended next chunk | M3.4e-2 — the DD/FD core ((IX+d) re-interpretation, IX/IY 16-bit, undoc IXh/IXl) |

## Slice docs index

- **Overview / sequencing:** `docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md`
- **Scoped parent plan (the M3.4e outline):** `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md`
- **The other half of M3.4e-1 (the AddrMode + EA + half-views):**
  `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1a-addrmode-ea.md`
- **Depth templates + close-state records:** `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md`,
  `…-ed-block-ops.md`
- **Architecture (Decision 1 — the declarative-decode thesis):**
  `docs/architecture/0001-z80-second-architecture.md`
