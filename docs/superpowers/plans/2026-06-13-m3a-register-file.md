# M3.1a: Data-Driven Register File — Retiring the Closed `Reg` Enum (the first M3 framework refactor)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** make CpuEmulator's **register identity spec-declared DATA, not a `Core` enum**. Today a
register named in a micro-op is a member of the closed `enum Reg { A, X, Y, S }`
(`src/CpuEmulator.Core/Specification/Reg.cs:5`), mirrored in the generator's `s_regMembers`
whitelist (`SpecParser.cs:82-85`), in `CpuEmitter.RegIndex` (`CpuEmitter.cs:1467-1474`), and in the
JIT's six baked `FieldInfo`s + the `RegField` index switch (`BlockCompiler.cs:37-42, 454-458`).
Adding one register is therefore a `Core` enum edit **plus three mirror-table edits** — the single
clearest "secretly 6502-shaped" smell in the framework. This PR replaces the enum with
**register-NAME strings validated against the spec's `Registers` table**, so the generator and the
JIT key on per-spec register data, not a fixed enum. This realizes **ADR Decision 3 option (ii)**
(`docs/architecture/0001-z80-second-architecture.md:288-289`) and the **JIT genericity audit item
J2** (`0001-…:506`) — *for the register dimension only*.

**This is a PURE REFACTOR.** No Z80 code is added. The entire value is proven by the **6502 staying
byte-for-byte behaviorally identical**: the full existing suite (~1419 tests), the
`CPUEMULATOR_UAT=full` TomHarte sweep (1.51M cases), and Klaus cycle-exact (96,241,367) all stay
green — PLUS new generator tests showing a *synthetic non-6502 register set* (a tiny "test CPU" with
registers `BC`/`HL`) generates + introspects + JITs correctly, exercising the abstraction against
register names the 6502 never had.

**PR:** branch `feat/m3-register-file` (base `main`, head `2294cd9`; **~1419 tests green** is the
baseline — report the exact count at Task 0). This plan file is a preparatory doc commit on that
branch; the implementation tasks follow.

---

## Scope

**IN scope (the register dimension, end to end):**
1. **The micro-op register-arg DSL form** changes from `Reg` enum members to register-name **string
   literals** (`Load(Reg.A)` → `Load("A")`). The `Reg` enum is **retired** (deleted). The `Op`
   records and `Spec.cs` factories that took `Reg` now take `string`. (Recommendation + rationale:
   Ground truth A.)
2. **The parser's register validation collapses to ONE check.** CPUGEN008 (declared-register
   cross-check) becomes the **primary and only** register-name validation: every register-arg string
   must name a row in the spec's `Registers` table. The `s_regMembers` whitelist is **retired**.
   CPUGEN011 (arg-kind) adjusts: a register argument must now be a **string literal** (not a `Reg`
   enum member), with non-literal/non-string args still rejected as CPUGEN011. (Ground truth B.)
3. **The emitter resolves register field/width from the model by name** — no hardcoded `A`/`X`/`Y`.
   `RegIndex` is retired; `EmitRegisterOp`'s `Increment`/`Decrement` casts and `Transfer` become
   width-aware *only to the extent the 6502 already needs* (8-bit) — see the width note in Ground
   truth C (16-bit register **math** is NOT in scope; only correct field typing by declared width).
4. **The JIT's six baked `FieldInfo`s become a per-CPU map resolved by declared name** (J2). The
   `JitOp` operand carries the register **name string** instead of a byte index; `BlockCompiler`
   resolves `cpu-type.GetField(name)` once per compile. The compiler stays typed to `Mos6502Cpu`
   for now (J1 is explicitly deferred — see NOT-in-scope).
5. **The importer + runbook:** the semantics map's `Reg.A` literal text → the new string form; the
   schema doc + the 6502 semantics data file + the regenerated `Mos6502Spec.cs` all move to the new
   form. The byte-equality anchor (`RegeneratedSpecTests`) must still hold against the regenerated
   text.
6. **Backward-validation:** the synthetic non-6502 register-set generator test (registers `BC`/`HL`,
   16-bit) proves a spec with arbitrary register names generates, introspects (`GetRegister("BC")`),
   and — for the JIT-reachable subset — compiles correctly.

**NOT in scope (stated so an implementer does not reach for it):**
- **Decode / prefix model (ADR Decision 1, M3.1b).** This is a **SEPARATE later chunk.** The opcode
  stays a single byte; `InstructionDef(byte Opcode, …)`, the 256-slot `JitDescriptors` table, the
  `switch (opcode)` interpreter, and `BlockCompiler.Discover`'s single-byte index are all
  **untouched.** M3.1a is the register dimension; M3.1b is the decode dimension. They are split
  because the ADR (`0001-…:539-545, 590`) anticipates exactly this split ("M3.1 … split if the
  register-file change and the prefix change each prove large").
- **The flag model (ADR Decision 3/4, a separate later chunk).** `Flag` stays a closed enum;
  `s_flagMembers`, `CpuEmitter.FlagBit`, `SetNZ`'s `0x7D` mask, the per-arch flag micro-op family
  (`SetSZ`/`SetParity`/`SetHalfCarry`/…) are **all out of scope.** This plan touches REGISTERS only.
  The brief is explicit: "Keep `Flag` as-is … scope this plan to REGISTERS only."
- **16-bit register ARITHMETIC** (`ADD HL,rr`, `INC rr` setting no flags, 16-bit ALU). The emitter
  becomes width-*aware* for field typing (it already is — `byte` vs `ushort` storage), but no new
  16-bit math op bodies are added. The 6502 has no 16-bit-math register op; adding one is Z80 work
  (M3.4). See Ground truth C's width note.
- **JIT genericity J1** (making `BlockCompiler`/`BlockDelegate`/`JittedCpu` generic over the CPU
  type, retiring `typeof(Mos6502Cpu)`). J2 (register file as data) is done here; J1 (CPU type as
  data) is M3.5 — the ADR pairs them but they are separable, and J2 is the register dimension this
  plan owns. The JIT's `FieldInfo` map is built from `typeof(Mos6502Cpu)` BY NAME, which is the J2
  win; the `typeof(Mos6502Cpu)` literal staying is the J1 deferral.
- **ANY Z80 code.** No Z80 spec, no register pairs-as-views, no alternate set, no `I`/`R`. The
  synthetic `BC`/`HL` test CPU is a *generator/JIT fixture* (a tiny spec in `GeneratorTestHost`/a
  test fixture), NOT a shipped CPU and NOT the Z80.

**Recorded deviations/departures this plan makes deliberately:**
- **The DSL form is a bare `string` literal, NOT a `Reg` value-type wrapper.** The brief offered
  both ("`Load("A")` … or a `Reg` value-type wrapper over a string — your call"). I choose the bare
  string. Rationale in Ground truth A — the generator reads *syntax*, and a string literal is
  directly analyzable by the existing `LiteralString` helper (`SpecParser.cs:785-786`); a wrapper
  (`Reg("A")` or `new Reg("A")`) adds an invocation/creation node the parser must additionally
  pattern-match for no validation benefit (the validation is "is this name in the Registers table,"
  which a string answers as well as a wrapper). The DSL stays readable (`Load("A")` reads as well as
  `Load(Reg.A)`), and the `Op` record fields become `string` (trivially equatable for the
  generator's incremental cache). Recorded.
- **CPUGEN011 and CPUGEN008 do NOT merge into one diagnostic ID.** Both IDs survive, but their
  *division of labor* shifts: CPUGEN011 now means "this register argument is not a string literal"
  (kind error); CPUGEN008 means "this string names no declared register" (the primary check). The
  old CPUGEN011 sub-case "not a known `Reg` enum member" is **deleted** (there is no enum to be a
  member of). This keeps the two diagnostics' *user-facing meanings* coherent and avoids renumbering
  the published analyzer IDs. Recorded; the authorized-test-changes table enumerates the two tests
  that move.
- **Generated 6502 output is expected BYTE-IDENTICAL** (not merely equivalent). The state fields,
  `GetRegister`/`SetRegister`, the interpreter bodies, and the disassembler already emit by declared
  *name* (`CpuEmitter.cs:38-66, 295-318`) — they never referenced the `Reg` enum. The only generated
  artifact that encoded a register *index* is the `JitDescriptors` table's `JitOp` literal
  (`new JitOp("Transfer", 0, 1, 0, false)` — `CpuEmitter.cs:1461`). Changing `JitOp` to carry the
  register **name** changes that table's text. **Decision:** the `Mos6502Spec.cs` (the spec the
  importer emits) stays byte-identical *after the DSL-form migration is applied to the semantics
  data* (Ground truth E); the **generated `Mos6502Cpu.g.cs`** changes ONLY in the `JitDescriptors`
  `JitOp` rows (index → name). That is a real, enumerated text change to a generated artifact, gated
  by the generator snapshot test (re-snap authorized). Everything else in the generated `.g.cs` is
  byte-identical. Stated honestly in Ground truth E + the authorized-test-changes table.

**ADR link:** `docs/architecture/0001-z80-second-architecture.md` — Decision 3 option (ii)
(`:278-289`), the genericity-implication paragraph (`:310-318`), J2 in the JIT genericity audit
(`:506`), Risk/open-question 1 (`:679-684`, the human signed off — "do the data-driven version"),
and the milestone decomposition M3.1 (`:538-545`, this is the register half of the split).

**Plan series:** M3.0 ADR ✅ (the decision record) · **M3.1a: this plan (register file) — the first
framework refactor** · M3.1b: decode/prefix (separate later chunk) · M3.2: bus/interrupt seams ·
M3.3: extraction loaders + Z80 dataset · M3.4: Z80 interpreter + TomHarte · M3.5: Z80 through JIT +
J1 + the genericity findings.

---

## Derived numbers (verified against the repo, not assumed)

- **Baseline test count: ~1419** (stated by the brief; confirm the EXACT number at Task 0 with a
  clean `dotnet test` and record it — the estimate below is relative to the confirmed baseline). The
  per-task new-test estimate (theory rows counted individually per house convention):
  - Task 1 (DSL form: `Reg` enum retired, `Op`/`Spec` take `string`): ~3 — `Spec.Load("A")` returns
    a `LoadRegOp` whose `Target == "A"` (1); the `Reg` type no longer exists (a compile-time fact,
    pinned by the suite compiling — counted 0); a spec authored with `Load("A")` round-trips through
    the model with the name preserved (1); the 6502 spec file still compiles with the new form (1).
  - Task 2 (parser: CPUGEN008 primary, `s_regMembers` retired, CPUGEN011 adjusted): ~6 — a
    register-arg naming an undeclared register reports CPUGEN008 (the renamed/kept "Y not declared"
    test, 1); a register-arg that is not a string literal reports CPUGEN011 (a `Load(Reg.A)`-style
    enum-member arg, now a kind error, 1); a register-arg that is a non-literal string expression
    (`Load(someVar)`) reports CPUGEN011 (1); the valid 6502 subset passes with no diagnostics (the
    kept happy-path, 1); `Transfer("A","X")` with both declared passes (1); a `Flag`-typed arg where
    a register is expected still reports CPUGEN011 (1).
  - Task 3 (emitter: resolve field/width by name, `RegIndex` retired): ~3 — the generated 6502
    interpreter body for `TAX`/`INX`/`PHA` is byte-identical to the pre-refactor output (a snapshot
    spot pin, ~2); a synthetic spec with a 16-bit register named `HL` types its state field as
    `ushort` and `GetRegister("HL")` returns it (1). *(The bulk is proven by the existing suite +
    the byte-equality anchor; these are spot pins.)*
  - Task 4 (JIT FieldInfo-by-name map; `JitOp` carries the name; `RegField` retired): ~5 — a
    compiled `LDA`/`STA`/`TAX` block produces identical state+cycles to the interpreter (the kept
    JIT parity pins cover this; a spot pin that the `FieldInfo` map resolves `A`/`X`/`Y`/`S` by name,
    1); the `JitOp` descriptor row for `Transfer` carries `"A"`/`"X"` not `0`/`1` (descriptor pin,
    1); an unknown register name in a descriptor throws a clear `EmulationException` at compile time
    (1); a compiled block over the synthetic `BC`/`HL` JIT-reachable subset resolves its fields by
    name and runs (1); the `JitDescriptors` snapshot re-snap is mechanical (1).
  - Task 5 (importer + runbook + 6502 semantics data + regenerate): ~3 — the regenerated
    `Mos6502Spec.cs` is byte-equal to the committed file (the existing `RegeneratedSpecTests`
    anchor, now over the new string form, 1); the semantics-map ops-text validator accepts a
    register-name string arg and rejects a bare unquoted token (the `AllowedArgPattern` change, ~2);
    `SemanticsMap.FactoryArity` is unchanged (the arity table does not move — register args are
    still arg-position 0/1, just typed string — counted 0, a stability note).
  - Task 6 (the synthetic non-6502 register-set generator test — the abstraction proof): ~4 — a spec
    declaring registers `BC`/`HL` (16-bit) + a `PC` generates a CPU class that compiles (1);
    `GetRegister("BC")`/`SetRegister("BC", …)` round-trip a 16-bit value (1); a micro-op
    `Transfer("HL","BC")` generates a field-to-field copy with no `A`/`X`/`Y` assumption (1); a
    register-arg naming `IX` (not in the synthetic Registers table) reports CPUGEN008 (1).
  - **Estimate: ~1419 + ~24 ≈ ~1443.** The full TomHarte sweep + Klaus dominate *runtime* not *fact
    count* (they are unchanged theories — the refactor must not move them). Report actuals at
    closeout — the estimate bends, the suite does not.
- **Klaus cycle anchor: 96,241,367** — a PURE-REFACTOR invariant. The register-file change touches
  NO cycle logic (no `ComputeCycles`, no `BaseCycles`, no bus-access count). Klaus under the
  interpreter AND under the JIT must reach `$3469` at EXACTLY 96,241,367 cycles, unchanged. This is
  the cycle-exactness backstop; the full TomHarte sweep diffs per-case cycles for all 1.51M cases.
- **TomHarte full sweep: 1.51M cases, 151/151 opcodes, 0 parity failures** — unchanged. The refactor
  changes *how* a register field is named in emitted code, not *what* the code does. Any divergence
  is a refactor bug by definition.
- **Generated 6502 `.g.cs` delta: the `JitDescriptors` `JitOp` rows only** (index → name). See
  Ground truth E for the exact before/after and why nothing else moves.

---

## Ground truth A — the new register-arg DSL form (string literal, not enum, not wrapper)

**The change, in one line:** a micro-op register argument is a **bare C# string literal** naming a
register declared in the spec's `Registers` table.

```csharp
// BEFORE (closed enum, Mos6502Spec.cs:73, 105, 110, 123…):
Insn(0x48, "PHA", AddrMode.Implied, [Push(Reg.A)]),
Insn(0xAA, "TAX", AddrMode.Implied, [Transfer(Reg.A, Reg.X), SetNZ(Reg.X)]),
Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),

// AFTER (register-name strings):
Insn(0x48, "PHA", AddrMode.Implied, [Push("A")]),
Insn(0xAA, "TAX", AddrMode.Implied, [Transfer("A", "X"), SetNZ("X")]),
Insn(0xA9, "LDA", AddrMode.Immediate, [Load("A"), SetNZ("A")]),
```

**Why a bare string, not a `Reg("A")` value-type wrapper (the recommendation).**

| Dimension | bare string `Load("A")` | wrapper `Load(Reg("A"))` / `new Reg("A")` |
|---|---|---|
| **Parser analyzability** | the existing `LiteralString` helper (`SpecParser.cs:785-786`) reads it directly — `expression is LiteralExpressionSyntax { Token.Value: string s }`. Zero new syntax-matching. | the parser must additionally match an `InvocationExpressionSyntax`/`ObjectCreationExpressionSyntax` and pull the string literal *out of its argument list* — strictly more syntax handling for the same validated datum (the name). |
| **DSL readability** | `Load("A")`, `Transfer("A","X")` — reads cleanly; the quotes signal "a name, validated against the table." | `Load(Reg("A"))` — an extra layer of ceremony around the same string; no clearer. |
| **Validation power** | identical — the only question is "is `"A"` a row in `Registers`?", answered by a `HashSet<string>.Contains` either way. A wrapper buys NO extra static guarantee (it cannot constrain the string to a declared name at the type level; the generator is the gate, as the DSL contract states — `Spec.cs:3-6`). | identical validation, more nodes. |
| **Op-record equatability (incremental cache)** | `string` is `IEquatable` — the `OpModel`/`Op` record value-equality the generator's incremental pipeline depends on (the `RunTwiceWithReparse` pin, `GeneratorTestHost.cs:77-98`) is preserved trivially. | a `Reg` struct over a string is also equatable, but it is a new public type to design, document, and keep equatable for no benefit. |
| **Runtime cost** | none (spec tables are authored data, read once by the generator). | none. |

The generator reads **syntax**, and a string literal is the most directly analyzable syntax for a
name. A wrapper adds a node to match with no validation upside. **Recommendation: bare string.**

**What the string is checked against (the contract):** the set of `Name`s in the spec's `Registers`
table — `registerNames` in `SpecParser.cs:203`, already threaded into `ParseOps`
(`SpecParser.cs:206-207, 307`). The check that *was* CPUGEN008 (Ground truth B) becomes the only
register-name check.

**Reserved-name interaction (preserved).** Register names already cannot collide with emitted local
names (`s_reservedLocalNames` → CPUGEN002, `SpecParser.cs:96-100, 269-275`). That guard is on the
`Registers` table declaration and is **unchanged** — a register named `data`/`addr`/`ea` is still
rejected at declaration, so a micro-op string `Load("data")` can only ever name a register that
passed that guard. No new collision surface.

---

## Ground truth B — the name→validation contract (CPUGEN008 primary; `s_regMembers` retired; CPUGEN011 adjusts)

**Today's two-stage check (`SpecParser.cs:720-738`):**

```text
1. CPUGEN011: is the arg `Reg.<Member>` where <Member> ∈ s_regMembers {A,X,Y,S}?   (kind + enum-membership)
2. CPUGEN008: is <Member> also a Name in the spec's Registers table?               (declared-register cross-check)
```

The enum-membership half of step 1 is the 6502-shaped wall: `Reg.Q` is rejected as "not a Reg
member" (`ModeOpValidationTests.cs:544-557`) even though the real question is "is `Q` a declared
register." With the enum retired, the two stages collapse:

**After the refactor:**

```text
1. CPUGEN011: is the arg a STRING LITERAL?   (kind error otherwise — not a literal, or wrong type)
2. CPUGEN008: does that string name a row in the spec's Registers table?   (THE primary check)
```

`s_regMembers` is **deleted** (`SpecParser.cs:80-85`). The `ArgKind.Reg` parse path
(`SpecParser.cs:701-738`) changes:

```csharp
// BEFORE (the enum-member path):
string? value = expected switch
{
    ArgKind.Reg  => EnumMemberName(argument.Expression, "Reg"),   // "Reg.A" -> "A"
    ArgKind.Flag => EnumMemberName(argument.Expression, "Flag"),
    _            => BoolLiteral(argument.Expression),
};
// … then: if (!s_regMembers.Contains(value)) -> CPUGEN011;
//         if (!registerNames.Contains(value)) -> CPUGEN008.

// AFTER (the string-literal path):
string? value = expected switch
{
    ArgKind.Reg  => LiteralString(argument.Expression),            // "A" -> "A" (a string literal)
    ArgKind.Flag => EnumMemberName(argument.Expression, "Flag"),   // Flag stays an enum (out of scope)
    _            => BoolLiteral(argument.Expression),
};
if (value is null)  // not a string literal in the Reg position -> CPUGEN011 (kind error)
{
    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidMicroOpArgument,
        argument.GetLocation(), (i + 1).ToString(), kind, "register-name string literal"));
    return null;
}
if (expected == ArgKind.Reg && !registerNames.Contains(value))   // CPUGEN008 — THE primary check
{
    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownRegisterInOp,
        argument.GetLocation(), value));
    return null;   // changed from "keep parsing": with no enum pre-filter, an undeclared name is a hard stop here
}
```

**The diagnostic-meaning table (the user-facing contract):**

| Authoring mistake | Before | After | Why |
|---|---|---|---|
| `Load(Reg.A)` (old enum form) | valid | **CPUGEN011** ("register-name string literal") | the `Reg` type no longer exists; `Reg.A` is now an unresolved symbol — the kind check catches the non-string |
| `Load("A")` with `A` declared | n/a | **valid** | string literal naming a declared register |
| `Load("Q")` with `Q` NOT declared | CPUGEN011 ("not a Reg member") | **CPUGEN008** ("register 'Q' not declared") | the primary check — the only register-name gate now |
| `Load(someVariable)` | CPUGEN011 | **CPUGEN011** ("register-name string literal") | not a literal → kind error, unchanged in spirit |
| `Transfer("A", Flag.C)` | CPUGEN011 at arg 2 | **CPUGEN011 at arg 2** | arg 2 expects a register-name string; a `Flag` member is not one |

**The CPUGEN008 severity note (preserved).** CPUGEN008 is already an Error descriptor
(`SpecDiagnostics.cs:40-42`); ANY diagnostic nulls the model (`SpecParser.cs:198-201, 209-210`). The
old code "kept parsing" after CPUGEN008 (`SpecParser.cs:736-737`) because the enum pre-filter had
already guaranteed kind-correctness; after the refactor, an undeclared name returns null
immediately (the comment is updated). Net behavior — an undeclared register fails the build — is
identical.

**Validation-message text change (authorized).** The CPUGEN011 message template
(`SpecDiagnostics.cs:51-53`, `"Argument {0} of '{1}' must be a {2}"`) is unchanged; only the `{2}`
fill changes from `"Reg member"` to `"register-name string literal"`. The
`Unknown_Reg_member_in_op_reports_CPUGEN011` test asserts `"must be a Reg member"`
(`ModeOpValidationTests.cs:556`) — that assertion text moves (authorized; see the table).

---

## Ground truth C — the emitter's name→field/width resolution contract

**What the emitter ALREADY does by name (no change).** State fields, the register-name list,
`GetRegister`/`SetRegister`, and `RegisterBits` all iterate `model.Registers` and emit by `Name` +
`Bits` (`CpuEmitter.cs:38-66, 1218-1225`). The interpreter bodies (`Transfer`/`Increment`/
`Decrement`/`SetNZ`/`Load`/`Store`/`Push`/`Pull`) write the register's `Name` *string* directly into
the generated C# (`CpuEmitter.cs:295-318, 347, 627, 863, 871`). **None of these ever referenced the
`Reg` enum** — they consume `OpModel.Args[i]`, which is already a string today (the parser stores
the resolved member name, `SpecParser.cs:748`). So for the interpreter, the DSL-form change is
**transparent**: the parser stores `"A"` whether it came from `Reg.A` or `"A"`.

**The one place an INDEX (not a name) is emitted — and the fix.** `JitOpLiteral`
(`CpuEmitter.cs:1428-1463`) calls `RegIndex(op.Args[0])` to turn `"A"`→`0` for the `JitOp` byte
fields. `RegIndex` (`CpuEmitter.cs:1467-1474`) is the third mirror table (`A=0,X=1,Y=2,S=3`). **This
is retired** (Task 3/4): the `JitOp` literal carries the register **name string** instead. See
Ground truth D for the `JitOp` shape change.

**The width contract (clarified — and the scope wall).** The emitter types a register field by its
declared `Bits` (`CpuEmitter.cs:39`: `byte` for 8, `ushort` for 16) and casts on write to its
declared width (`SetRegister`, `CpuEmitter.cs:61-62`). The register-OP bodies, however, hardcode
`(byte)` casts (`Increment`/`Decrement`: `CpuEmitter.cs:303, 309`; `SetNZ`'s `0x7D` mask: `:318`).

- **For M3.1a (6502 + the synthetic test CPU), this is left AS-IS for register *math*.** The 6502
  has no 16-bit register-math op; `Increment`/`Decrement` only ever target 8-bit `X`/`Y`. The
  synthetic test CPU exercises 16-bit registers for **storage + transfer + introspection** (the
  generic surface this plan owns), NOT 16-bit `Increment` math. A `Transfer("HL","BC")` between two
  `ushort` fields is a plain `BC = HL;` assignment — width-correct with no cast change. A
  `SetNZ`/`Increment` on a 16-bit register is **rejected by the validator** in this plan's synthetic
  fixtures (we simply do not author one), because the width-aware math bodies are **Z80 work
  (M3.4)**, explicitly out of scope.
- **Recorded for M3.4:** when 16-bit register math arrives, `EmitRegisterOp`'s `(byte)` casts must
  consult the target register's declared `Bits` and emit `(ushort)` math + 16-bit flag rules. That
  is the "genuinely new code in the emitter" the ADR names (`0001-…:299-300`). **It is NOT in this
  plan.** This plan makes register *identity* data; register *width-math* is the next dimension.

**The resolution helper (the contract the emitter + JIT share).** Both the emitter (already) and the
JIT (Task 4) resolve a register from the model by name. The model already carries everything needed
— `RegisterModel(string Name, int Bits, string Role)`. The plan adds NO new model field; it adds a
**lookup discipline**: "given a register-name string from an op arg, find the matching
`RegisterModel` (or `FieldInfo`) by `Name`." For the generator this is implicit (it writes the name
through). For the JIT it is an explicit `Dictionary<string, FieldInfo>` built per compile (Ground
truth D).

---

## Ground truth D — the JIT `FieldInfo`-by-name map (J2) + the `JitOp` name-carrying shape

**Today (6502-baked, `BlockCompiler.cs:37-42, 454-458`):**

```csharp
private static readonly FieldInfo FA  = typeof(Mos6502Cpu).GetField("A")!;
private static readonly FieldInfo FX  = typeof(Mos6502Cpu).GetField("X")!;
private static readonly FieldInfo FY  = typeof(Mos6502Cpu).GetField("Y")!;
private static readonly FieldInfo FS  = typeof(Mos6502Cpu).GetField("S")!;
private static readonly FieldInfo FP  = typeof(Mos6502Cpu).GetField("P")!;
private static readonly FieldInfo FPC = typeof(Mos6502Cpu).GetField("PC")!;

private static FieldInfo RegField(byte regIndex) => regIndex switch
{
    0 => FA, 1 => FX, 2 => FY, 3 => FS,
    _ => throw new EmulationException($"unknown register index {regIndex}"),
};
```

`RegField` is keyed on the byte index the `JitOp` carries (`op.RegA`/`op.RegB`, set from `RegIndex`
in the generator). The emit arms call `RegField(op.RegA)` for `Load`/`Store`/`Transfer`/
`Increment`/`Decrement`/`SetNZ`/`Compare`/`Push`/`Pull` (`BlockCompiler.Emit.cs:269-577`,
`BlockCompiler.Flow.cs:47-64`).

**After the refactor (J2 — resolve by declared name):**

The `JitOp` operand carries the register **name string** (Ground truth E shows the descriptor-text
change). `BlockCompiler` builds a per-compile name→`FieldInfo` map from the CPU type, then
`RegField(string name)` indexes it:

```csharp
// Built once per BlockCompiler instance (the cpu type is still Mos6502Cpu — J1 deferred).
// J2: the register file is DATA — the names come from the descriptor's JitOp.Reg strings,
// resolved against the concrete CPU type's public fields. No A/X/Y/S baked switch.
private readonly System.Collections.Generic.Dictionary<string, FieldInfo> _regFields;

// in the ctor, from the CPU's RegisterNames (introspection the generator already emits):
_regFields = new(System.StringComparer.Ordinal);
foreach (string name in _cpu.RegisterNames)             // ICpuCore.RegisterNames — already generated
    _regFields[name] = typeof(Mos6502Cpu).GetField(name)
        ?? throw new EmulationException($"register '{name}' has no field on the CPU type");

// FP / FPC stay as named statics (P is the Status reg, PC the ProgramCounter — the flow/branch
// arms reference them directly; they are NOT operand-driven, so they need not go through the map.
// They are resolved by name from the same source for consistency, but kept as fields for the hot
// arms). Recorded: P/PC resolution-by-name is the same one-liner; the map covers OPERAND registers.

private FieldInfo RegField(string name) => _regFields.TryGetValue(name, out var f)
    ? f
    : throw new EmulationException($"compiled descriptor names register '{name}' "
                                 + "which the CPU type does not declare");
```

**The `JitOp` shape change.** `JitOp(string Kind, byte RegA, byte RegB, byte FlagBit, bool BoolArg)`
(`OpcodeDescriptor.cs:32`) → the register slots become **strings**:

```csharp
// AFTER: RegA/RegB carry the register NAME (or "" when the op has no register operand).
public readonly record struct JitOp(string Kind, string RegA, string RegB, byte FlagBit, bool BoolArg);
```

> **Why strings on `JitOp`, not indices.** The whole point of J2 is that the register file is
> *data*. A byte index re-introduces a fixed ordering (the retired `RegIndex` map) the Z80's ~14
> registers would have to extend by hand. A name is self-describing and resolves against whatever
> register set the spec declared. The `FlagBit`/`BoolArg` slots are UNCHANGED (flags are out of
> scope). Empty string `""` marks "no register operand" (the zero-arg ops — `Jump`, `Adc`, …) the
> same way `0` did, but unambiguously (there is no register named `""`).

**The emit-arm changes (mechanical).** Every `RegField(op.RegA)` / `RegField(op.RegB)` call site
(enumerated in the J2 grep: `BlockCompiler.Emit.cs:269, 276, 288, 298, 467, 468, 475, 479, 486, 490,
496, 539, 577`; `BlockCompiler.Flow.cs:47, 50, 64`) now passes the **string** `op.RegA`/`op.RegB`
instead of the byte. The `FieldInfoIndex { X, Y }` enum + `EmitLoadRegByte`
(`BlockCompiler.Emit.cs:79-85`) — used by the indexed addressing modes (`ZeroPageX`, `AbsoluteY`,
…) — currently hardcode `FX`/`FY`. **These stay 6502-shaped in THIS plan** because they encode the
6502 *addressing-mode → index-register* convention (`ZeroPageX` uses `X`), which is the decode
dimension (`RequiredIndexRegister`, `SpecParser.cs:582-587`) — **M3.1b, not M3.1a.** They keep using
`FX`/`FY` resolved by name from the map (`_regFields["X"]`, `_regFields["Y"]`) so they no longer
reference the baked statics, but the *convention* (which register an indexed mode uses) is not
generalized here. Recorded: this is the seam between the register dimension (done) and the decode
dimension (M3.1b).

**Compile-time validation (new pin).** `RegField(name)` throwing on an unknown name (rather than the
old `unknown register index` on a byte) gives a clear failure if a descriptor ever names a register
the CPU type lacks — pinned by `RegField_throws_on_an_undeclared_register_name` (Task 4).

---

## Ground truth E — the generated-output delta (what changes, what stays byte-identical)

**The honest statement: the 6502's generated `Mos6502Cpu.g.cs` changes in EXACTLY ONE region — the
`JitDescriptors` table's `JitOp` literals — and nowhere else.**

Today a `JitOp` with a register operand emits the byte index (`CpuEmitter.cs:1461-1462`):

```csharp
// BEFORE — RegIndex turned "A"->0, "X"->1:
new CpuEmulator.Core.Jit.JitOp("Transfer", 0, 1, 0, false)   // TAX: Transfer(A->X)
new CpuEmulator.Core.Jit.JitOp("Load", 0, 0, 0, false)       // LDA: Load(A)
new CpuEmulator.Core.Jit.JitOp("Push", 0, 0, 0, false)       // PHA: Push(A)
```

After the refactor `JitOpLiteral` emits the **name** (Task 4):

```csharp
// AFTER — the register name, verbatim:
new CpuEmulator.Core.Jit.JitOp("Transfer", "A", "X", 0, false)
new CpuEmulator.Core.Jit.JitOp("Load", "A", "", 0, false)
new CpuEmulator.Core.Jit.JitOp("Push", "A", "", 0, false)
```

**Everything else in `Mos6502Cpu.g.cs` is byte-identical:** the state fields (already by name), the
`Step`/`Run`/`Execute` switch (opcode-indexed, decode dimension untouched), every `Op{XX}()` body
(emits the register name through, which was always a string), the disassembler, the
assembler/monitor, `InstructionLength`, `GetRegister`/`SetRegister`/`RegisterBits`. **Proof
strategy:** the generator snapshot test re-snaps ONLY the `JitDescriptors` region (authorized,
Task 4); a spot pin diffs a `TAX`/`INX`/`PHA` interpreter body against the pre-refactor text and
asserts it is unchanged (Task 3).

**The spec file `Mos6502Spec.cs` and the byte-equality anchor.** `Mos6502Spec.cs` is the importer's
output, byte-pinned by `RegeneratedSpecTests` (`:39-42`). Its register-arg text changes form
(`Reg.A`→`"A"`), so:
1. the semantics-map data file (`mos6502-semantics.json`) is migrated to the new ops-text form
   (Task 5, Ground truth F);
2. the importer's emitter writes the new form (it already writes `opsText` verbatim from the map,
   `SpecFileEmitter.cs:120-121` — so the change is in the DATA, not the emitter logic);
3. `Mos6502Spec.cs` is regenerated and the **anchor holds** against the regenerated form.

So `Mos6502Spec.cs` is **byte-identical to a fresh tool run** (the anchor's contract) AFTER the
migration — it is just a *different* byte-identical target than before. The brief's "byte-equality
anchor must still hold" is satisfied: regenerate, commit, the anchor passes.

**Summary table — generated/committed artifacts:**

| Artifact | Changes? | Gated by |
|---|---|---|
| `Mos6502Cpu.g.cs` — state fields, bodies, disasm, monitor | **NO** (byte-identical) | the existing generator/emission suite + a Task 3 spot pin |
| `Mos6502Cpu.g.cs` — `JitDescriptors` `JitOp` rows | **YES** (index → name) | the generator snapshot re-snap (authorized, Task 4) |
| `Mos6502Spec.cs` (importer output) | **YES** (`Reg.A` → `"A"`); still tool-equal | `RegeneratedSpecTests` byte-equality anchor (Task 5) |
| `mos6502-semantics.json` (importer input data) | **YES** (ops-text form) | the migrated anchor + `SemanticsMapTests` (Task 5) |
| Klaus cycle count / TomHarte case results | **NO** (pure refactor) | the unchanged Klaus + TomHarte sweeps |

---

## Ground truth F — the importer / runbook migration (the `Reg.A` literal text)

**Where `Reg.A` literal text lives in the importer pipeline:**

1. **`mos6502-semantics.json`** (`tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json`) — the
   ops-text values: `"LDA": "[Load(Reg.A), SetNZ(Reg.A)]"` (`:14-69`). **Migrated** to
   `"LDA": "[Load(\"A\"), SetNZ(\"A\")]"` for every mnemonic with a register arg.
2. **`SemanticsMap.AllowedArgPattern`** (`SemanticsMap.cs:81-82`) — the ops-text argument acceptance
   regex `^(Reg\.\w+|Flag\.\w+|true|false)$`. **Changed** to accept a quoted register-name string in
   the register position: `^("\w+"|Flag\.\w+|true|false)$` (the `Reg\.\w+` alternative becomes
   `"\w+"` — a double-quoted identifier). The validator stays a *shape* gate (the generator is the
   real gate, `SemanticsMap.cs:79-82`); it now accepts `"A"` and rejects a bare unquoted `A`.
3. **`SemanticsMap.FactoryArity`** (`SemanticsMap.cs:38-76`) — the arity table. **UNCHANGED** —
   `Load` is still arity 1, `Transfer` still arity 2; the register arg is still arg-position 0/1,
   only its *spelling* (quoted string vs `Reg.x`) changed. The brief flagged `FactoryArity` as part
   of the importer surface; the honest finding is the arity table does not move (register-ness is not
   encoded in arity), only `AllowedArgPattern` and the data move.
4. **The runbook** (`docs/user-guide/extraction-runbook.md`) — the semantics-map vocabulary doc:
   `Load(Reg.<name>)` → `Load("<name>")` etc. (`:116-148`), the example `"LDA": "[Load(Reg.A), …]"`
   (`:63`), and the `Reg.<RegisterName>` argument-form line (`:148`). **Updated** to the string form.

**The migration is mechanical + tool-verified.** After editing the data file, re-running the
importer regenerates `Mos6502Spec.cs`; `RegeneratedSpecTests` is the anchor that the regenerated
output matches the committed file. A typo in the migrated JSON surfaces as a generator diagnostic in
the importer's end-to-end test (the verification ladder the runbook describes,
`extraction-runbook.md` rungs).

---

## File structure

```
src/CpuEmulator.Core/Specification/
    Reg.cs                    — DELETE (the closed enum is retired)
    Op.cs                     — MODIFY (Reg fields -> string: LoadRegOp, StoreRegOp, TransferOp,
                                IncrementOp, SetNZOp, CompareOp, DecrementOp, PushOp, PullOp)
    Spec.cs                   — MODIFY (factories take string: Load/Store/Transfer/Increment/SetNZ/
                                Compare/Decrement/Push/Pull). Flag-taking factories UNCHANGED.
src/CpuEmulator.Generators/
    SpecParser.cs             — MODIFY (retire s_regMembers; ArgKind.Reg path reads a string literal;
                                CPUGEN011 = "not a string literal", CPUGEN008 = "not declared")
    CpuEmitter.cs             — MODIFY (retire RegIndex; JitOpLiteral emits the register NAME string
                                into the JitOp row). Interpreter-body emission UNCHANGED (already by name).
    SpecDiagnostics.cs        — (no ID change; the CPUGEN011 {2} fill text moves at the call site only)
src/CpuEmulator.Core/Jit/
    OpcodeDescriptor.cs       — MODIFY (JitOp.RegA/RegB: byte -> string)
src/CpuEmulator.Jit/
    BlockCompiler.cs          — MODIFY (per-compile _regFields name->FieldInfo map; RegField(string);
                                retire the FA/FX/FY/FS baked statics in favor of the map; FP/FPC kept
                                as named resolution; the J1 typeof(Mos6502Cpu) literal STAYS — deferred)
    BlockCompiler.Emit.cs     — MODIFY (RegField(op.RegA) call sites pass the string; EmitLoadRegByte
                                resolves X/Y from the map by name)
    BlockCompiler.Flow.cs     — MODIFY (RegField call sites pass the string; FieldInfoIndex.X via map)
tools/CpuEmulator.SpecImporter/
    SemanticsMap.cs           — MODIFY (AllowedArgPattern accepts a quoted register-name string;
                                FactoryArity UNCHANGED)
    data/mos6502-semantics.json — MODIFY (Reg.A -> "A" in every ops-text value)
src/CpuEmulator.Cpus.Mos6502/
    Mos6502Spec.cs            — REGENERATE (Reg.A -> "A"; via the importer, not hand-edited)
docs/user-guide/
    extraction-runbook.md     — MODIFY (the semantics-map vocabulary section: Reg.<name> -> "<name>")
tests/CpuEmulator.Tests/
    Generators/ModeOpValidationTests.cs   — MODIFY (the Reg-hardening tests: enum-member -> string;
                                CPUGEN011/CPUGEN008 division per Ground truth B)
    Generators/GeneratorHappyPathTests.cs — MODIFY (ValidSpecSource: Load(Reg.A) -> Load("A"))
    Generators/InstructionParsingTests.cs — MODIFY (WithInstructions needle: Reg.A -> "A")
    Generators/SyntheticRegisterSetTests.cs — NEW (Task 6: the BC/HL non-6502 abstraction proof)
    Jit/OpcodeDescriptorTests.cs          — MODIFY (JitOp register slots assert names not indices)
    Jit/RegisterFieldMapTests.cs          — NEW (Task 4: FieldInfo-by-name map + RegField throws)
    Importer/SemanticsMapTests.cs         — MODIFY (the ops-text arg pattern: quoted string accepted)
    Importer/RegeneratedSpecTests.cs      — (UNCHANGED logic; passes against the regenerated form)
    Importer/SpecFileEmitterTests.cs      — MODIFY (any embedded Reg.A expectations -> "A")
    (any other test embedding `Reg.` in spec source — enumerated by a Task 0 grep)
```

---

## Task 0: Baseline + the full `Reg.` blast-radius grep (NO code change)

> Establish the exact green baseline and enumerate every `Reg.`/`s_regMembers`/`RegIndex`/`RegField`
> site BEFORE touching code, so the refactor is a known, bounded edit set — not a whack-a-mole.

- [ ] **Step 1: Branch check** — `git branch --show-current` → `feat/m3-register-file` (base `main`,
  head `2294cd9`). This plan file is the preparatory doc commit on it.
- [ ] **Step 2: Confirm the green baseline** — `dotnet test` (routine suite). **Record the EXACT
  test count** (the brief says ~1419; pin the real number — the estimate is relative to it). Confirm
  0 failures, 0 unexpected skips. Record `dotnet build -warnaserror` is clean.
- [ ] **Step 3: The blast-radius grep** (record the hit list in the closeout; this is the authorized
  edit set):
  - `Reg\.` across `src/`, `tools/`, `tests/`, `docs/` — every enum-member reference.
  - `enum Reg`, `s_regMembers`, `RegIndex`, `RegField`, `FieldInfoIndex` — the mirror tables + JIT
    index machinery.
  - `JitOp(` — the descriptor literal sites (generator emits them; tests assert them).
  - `Reg\.A|Reg\.X|Reg\.Y|Reg\.S` in test spec sources (the embedded DSL strings that migrate).
  Confirm the hit set matches the File-structure list; any file with a `Reg.` hit not in that list
  is a STOP — add it to the plan (with a note) before proceeding.
- [ ] **Step 4:** No commit (read-only task). Proceed to Task 1.

---

## Task 1: The DSL form — retire the `Reg` enum; `Op`/`Spec` take `string` (TDD)

> Maps to scope item 1 + Ground truth A. This is the foundation: the authoring surface changes from
> `Reg.A` to `"A"`. After this task the 6502 spec + every test spec source is migrated to the string
> form and the `Reg` type is gone.

**Files:** delete `Reg.cs`; modify `Op.cs`, `Spec.cs`; migrate `GeneratorHappyPathTests.ValidSpecSource`
+ `InstructionParsingTests.WithInstructions` (so the suite compiles against the new factories).

- [ ] **Step 1: Failing tests** (`Spec`-form pins — author in a new `Generators/DslFormTests.cs` or
  fold into the happy-path file):
  - `Load_factory_stores_the_register_name_string` — `Spec.Load("A")` is a `LoadRegOp` with
    `Target == "A"` (a string).
  - `Transfer_factory_stores_both_register_name_strings` — `Spec.Transfer("A","X")` →
    `TransferOp("A","X")`.
  - `Spec_authored_with_string_form_compiles` — `ValidSpecSource` (now using `Load("A")`) runs
    through `GeneratorTestHost` with `Assert.Empty(result.AllErrors)` (the happy-path, migrated).

- [ ] **Step 2: Delete `Reg.cs`** and change the `Op` records to `string` (`Op.cs`):

```csharp
public sealed record LoadRegOp(string Target) : Op;
public sealed record StoreRegOp(string Source) : Op;
public sealed record TransferOp(string Source, string Target) : Op;
public sealed record IncrementOp(string Target) : Op;
public sealed record SetNZOp(string Source) : Op;
public sealed record CompareOp(string Source) : Op;
public sealed record DecrementOp(string Target) : Op;
public sealed record PushOp(string Source) : Op;
public sealed record PullOp(string Target) : Op;
// BranchIfOp(Flag, bool) and SetFlagOp(Flag, bool) UNCHANGED — Flag is out of scope.
```

- [ ] **Step 3: Change the `Spec` factories to take `string`** (`Spec.cs`):

```csharp
public static Op Load(string target)            => new LoadRegOp(target);
public static Op Store(string source)           => new StoreRegOp(source);
public static Op Transfer(string source, string target) => new TransferOp(source, target);
public static Op Increment(string target)       => new IncrementOp(target);
public static Op SetNZ(string source)           => new SetNZOp(source);
public static Op Compare(string source)         => new CompareOp(source);
public static Op Decrement(string target)       => new DecrementOp(target);
public static Op Push(string source)            => new PushOp(source);
public static Op Pull(string target)            => new PullOp(target);
// Flag-taking factories (BranchIf, SetFlag) and zero-arg factories UNCHANGED.
```

  Update the `Spec.cs` class doc comment: the DSL recognizes register args as **string literals**
  validated against the Registers table (was: `Reg` enum members).

- [ ] **Step 4: Migrate the test spec sources** — `GeneratorHappyPathTests.ValidSpecSource`
  (`Load(Reg.A), SetNZ(Reg.A)` → `Load("A"), SetNZ("A")`) and `InstructionParsingTests`'s
  `WithInstructions` needle (the same). These are the shared fixtures; migrating them keeps the suite
  compiling. (The other test files' `Reg.` sites migrate in their owning tasks: ModeOp in Task 2,
  Importer in Task 5.)

- [ ] **Step 5: The suite compiles** (the generator/emitter still consume `OpModel.Args` strings, so
  emission is unaffected — Ground truth C). Tests pass. **Commit** —
  `refactor(core): retire the Reg enum — micro-op register args are register-name strings`

---

## Task 2: The parser — CPUGEN008 primary; retire `s_regMembers`; CPUGEN011 adjusts (TDD)

> Maps to scope item 2 + Ground truth B. The register-name validation collapses to ONE check
> (declared-in-table), with the kind check (is-it-a-string-literal) as the CPUGEN011 guard.

**Files:** `SpecParser.cs` (the `ArgKind.Reg` parse path + delete `s_regMembers`);
`tests/.../Generators/ModeOpValidationTests.cs` (the Reg-hardening tests move per Ground truth B).

- [ ] **Step 1: Failing/changed tests** (`ModeOpValidationTests` — the Reg-hardening block,
  `:541-572`, migrated):
  - `Undeclared_register_in_op_reports_CPUGEN008` (was `Declared_register_not_in_Reg_enum_is_still_
    CPUGEN008_when_undeclared`, `:559-572`) — `Load("Y")` with `Y` not in the Registers table →
    CPUGEN008 mentioning `Y`. (The headline: an undeclared name is CPUGEN008 — the PRIMARY check.)
  - `Non_string_register_argument_reports_CPUGEN011` (replaces `Unknown_Reg_member_in_op_reports_
    CPUGEN011`, `:543-557`) — `Load(Reg.A)` (the OLD enum form — now an unresolved symbol / non-string
    expression) → CPUGEN011 with the message containing `"register-name string literal"`.
  - `Non_literal_register_argument_reports_CPUGEN011` — `Load(someName)` (an identifier, not a
    literal) → CPUGEN011.
  - `BranchIf_with_register_first_argument_reports_CPUGEN011` (`:163-178`, kept) — `BranchIf(Reg.A,
    false)` still CPUGEN011 at arg 1 (a `Flag` position given a non-flag); this is Flag-side and
    UNCHANGED.
  - `Transfer_with_flag_second_argument_reports_CPUGEN011` (`:181-…`, adjusted) — `Transfer("A",
    Flag.C)` → CPUGEN011 at arg 2 (register position given a Flag member, not a string).
  - `Valid_subset_passes_with_no_CPUGEN_diagnostics` (`:574-598`, migrated to string form) — the
    11-opcode subset with `Load("A")`/`Transfer("A","X")`/… passes clean.

- [ ] **Step 2: Delete `s_regMembers`** (`SpecParser.cs:80-85`) and rewrite the `ArgKind.Reg` arm of
  `ParseOps` (the literal from Ground truth B):

```csharp
string? value = expected switch
{
    ArgKind.Reg  => LiteralString(argument.Expression),           // register arg is a STRING LITERAL
    ArgKind.Flag => EnumMemberName(argument.Expression, "Flag"),  // Flag UNCHANGED (out of scope)
    _            => BoolLiteral(argument.Expression),
};
if (value is null)
{
    string description = expected switch
    {
        ArgKind.Reg  => "register-name string literal",
        ArgKind.Flag => "Flag member",
        _            => "bool literal",
    };
    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidMicroOpArgument,
        argument.GetLocation(), (i + 1).ToString(), kind, description));   // CPUGEN011 (kind)
    return null;
}

// CPUGEN008 — THE primary register-name check (was a two-stage enum-then-table check).
if (expected == ArgKind.Reg && !registerNames.Contains(value))
{
    diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.UnknownRegisterInOp,
        argument.GetLocation(), value));   // "register '{value}' not declared in the Registers table"
    return null;
}

// Flag whitelist UNCHANGED (CPUGEN006).
if (expected == ArgKind.Flag && !s_flagMembers.Contains(value)) { /* …unchanged… */ }
```

  Update the MIRROR TABLES doc block (`SpecParser.cs:15-24`): remove the `s_regMembers ↔ Reg members`
  line; note that register args are now name strings cross-checked against the spec's Registers set
  (the per-spec truth, not a fixed enum) — the genericity win.

- [ ] **Step 3: Tests pass; full suite green.** The 6502 spec + the synthetic specs all validate
  through the single CPUGEN008 gate. **Commit** —
  `refactor(generators): register args validated by declared name (CPUGEN008 primary); retire s_regMembers`

---

## Task 3: The emitter — resolve register field/width by name; retire `RegIndex` (TDD)

> Maps to scope item 3 + Ground truth C/E. The interpreter-body emission is ALREADY by name (no
> change). The one index-emitting site (`JitOpLiteral` → `RegIndex`) is retired here in tandem with
> the `JitOp`-name change (Task 4 owns the JIT consumer; this task owns the generator producer).

**Files:** `CpuEmitter.cs` (`JitOpLiteral` emits the name; delete `RegIndex`). Note: Tasks 3 and 4
together flip the `JitOp` producer (generator) and consumer (JIT). They MUST land together or the
build breaks (the descriptor type changes shape). Recorded sequencing: do Task 4's
`OpcodeDescriptor.JitOp` type change FIRST (it is `Core`, no behavior), then Task 3's emitter, then
Task 4's `BlockCompiler` — OR combine Tasks 3+4 in one commit. **Recommended: combine the
`JitOp`-shape commit (3+4) so the repo never has a half-migrated descriptor type.** This task
describes the generator half.

- [ ] **Step 1: Failing/spot tests:**
  - `Interpreter_body_for_TAX_is_unchanged` — generate the 6502 (or the migrated subset) and assert
    the `Op8A` (TXA) / `OpAA` (TAX) body text contains `A = X;` / `X = A;` exactly as before (a
    byte-identical spot pin — Ground truth E: bodies do not move).
  - `JitOp_literal_for_a_register_op_emits_the_name` — `JitOpLiteral(TransferOp("A","X"))` →
    `new CpuEmulator.Core.Jit.JitOp("Transfer", "A", "X", 0, false)` (string operands).

- [ ] **Step 2: Delete `RegIndex`** (`CpuEmitter.cs:1465-1474`) and rewrite `JitOpLiteral`
  (`CpuEmitter.cs:1428-1463`) to emit the register **name** (quoted) into the `JitOp` row:

```csharp
private static string JitOpLiteral(OpModel op)
{
    string regA = "\"\"", regB = "\"\"";   // "" = no register operand (zero-arg ops)
    byte flagBit = 0; bool boolArg = false;

    switch (op.Kind)
    {
        case "Transfer":                       // (reg source, reg target)
            regA = Quote(op.Args[0]); regB = Quote(op.Args[1]); break;
        case "Load": case "Store": case "Increment": case "Decrement":
        case "SetNZ": case "Compare": case "Push": case "Pull":
            regA = Quote(op.Args[0]); break;
        case "BranchIf": case "SetFlag":       // Flag arg — UNCHANGED
            flagBit = (byte)FlagBit(op.Args[0]); boolArg = op.Args[1] == "true"; break;
        default: break;                        // zero-arg ops: regA/regB stay ""
    }
    return $"new CpuEmulator.Core.Jit.JitOp(\"{op.Kind}\", {regA}, {regB}, {flagBit}, "
         + $"{(boolArg ? "true" : "false")})";
}

private static string Quote(string s) => $"\"{s}\"";   // register names are CPUGEN004-clean identifiers
```

  (Register names passed the identifier + reserved-name guards at declaration — `SpecParser.cs:247,
  269` — so direct string interpolation is injection-safe, same posture as the mnemonic whitelist
  comment at `CpuEmitter.cs:1232-1233`.)

- [ ] **Step 3: The width note (no code; recorded).** `EmitRegisterOp`'s `(byte)` casts
  (`CpuEmitter.cs:303, 309, 318`) are LEFT AS-IS — 16-bit register *math* is out of scope (Ground
  truth C). The synthetic test CPU (Task 6) exercises 16-bit registers only for storage/transfer/
  introspection, which need no cast change (a `ushort`-to-`ushort` `Transfer` is a plain assignment).

- [ ] **Step 4: Land with Task 4** (the `JitOp` type change is shared). After both halves: regenerate
  the 6502, re-snap the `JitDescriptors` region (authorized — Ground truth E), full suite green.
  **Commit (combined 3+4)** —
  `refactor(jit): register file as data — JitOp carries register names, FieldInfo resolved by name (J2)`

---

## Task 4: The JIT `FieldInfo`-by-name map (J2) + the `JitOp` name shape (TDD)

> Maps to scope item 4 + Ground truth D. The six baked `FieldInfo`s + the `RegField` index switch
> become a per-compile name→`FieldInfo` map resolved from the CPU's declared register names. The
> compiler stays typed to `Mos6502Cpu` (J1 deferred). Lands WITH Task 3 (shared `JitOp` type).

**Files:** `OpcodeDescriptor.cs` (`JitOp.RegA/RegB` → string); `BlockCompiler.cs` (the map +
`RegField(string)`; retire `FA/FX/FY/FS` statics); `BlockCompiler.Emit.cs` + `.Flow.cs` (call sites
pass the string); `tests/.../Jit/RegisterFieldMapTests.cs` (NEW); `Jit/OpcodeDescriptorTests.cs`
(assert names).

- [ ] **Step 1: Failing tests** (`RegisterFieldMapTests` + `OpcodeDescriptorTests`):
  - `FieldInfo_map_resolves_the_6502_registers_by_name` — a `BlockCompiler` over `Mos6502Cpu`
    resolves `A`/`X`/`Y`/`S` (and `P`/`PC`) to the right `FieldInfo` from the name map.
  - `RegField_throws_on_an_undeclared_register_name` — `RegField("ZZ")` throws `EmulationException`
    with a clear message (the compile-time validation, Ground truth D).
  - `JitOp_register_slots_carry_names_not_indices` (`OpcodeDescriptorTests`) — the `JitDescriptors`
    row for `TAX` has `Ops[0].RegA == "A"`, `RegB == "X"` (was `0`, `1`).
  - `Compiled_TAX_block_matches_the_interpreter` — a `... TAX ...` block under the JIT vs the
    interpreter: identical `A`/`X`/`P`/cycles (the kept parity posture; a spot pin the field map
    works end to end).

- [ ] **Step 2: Change `JitOp`** (`OpcodeDescriptor.cs:32`):

```csharp
public readonly record struct JitOp(string Kind, string RegA, string RegB, byte FlagBit, bool BoolArg);
```

- [ ] **Step 3: The `BlockCompiler` map** (Ground truth D literal). Replace the `FA/FX/FY/FS` statics
  with a per-instance `Dictionary<string, FieldInfo>` built from `_cpu.RegisterNames`; keep `FP`/
  `FPC` resolved by name (the Status/PC arms reference them directly). Rewrite `RegField` to take a
  `string` and index the map (throwing on miss). Update every `RegField(op.RegA)`/`RegField(op.RegB)`
  call site (`BlockCompiler.Emit.cs:269, 276, 288, 298, 467-496, 539, 577`; `.Flow.cs:47, 50, 64`)
  to pass the string. `EmitLoadRegByte` (`.Emit.cs:81-85`) resolves `X`/`Y` from the map by name
  (`_regFields["X"]`/`["Y"]`) — the 6502 indexed-mode convention stays (M3.1b owns generalizing it).

  > **J1 boundary (recorded).** `typeof(Mos6502Cpu)` STAYS in `BlockCompiler.cs` (`:37-42, 50, 57,
  > 97`) and `BlockDelegate` (`CompiledBlock.cs:49`). The register file is now data (J2 done); the
  > CPU *type* is still baked (J1, M3.5). The map is built by NAME against that concrete type —
  > which is exactly the J2 shape the ADR specifies (`0001-…:506`).

- [ ] **Step 4: Regenerate the 6502 + re-snap** the `JitDescriptors` region (the only generated-text
  change — Ground truth E). Full suite green; run the JIT parity pins. **Commit (combined with
  Task 3)** — see Task 3 Step 4.

---

## Task 5: The importer + runbook + 6502 semantics data + regenerate (TDD)

> Maps to scope item 5 + Ground truth F. The semantics map's `Reg.A` text → `"A"`; the
> `AllowedArgPattern` accepts the quoted form; `Mos6502Spec.cs` is regenerated; the byte-equality
> anchor holds against the new form.

**Files:** `SemanticsMap.cs` (`AllowedArgPattern`); `data/mos6502-semantics.json` (the ops-text);
`Mos6502Spec.cs` (regenerate via the tool); `extraction-runbook.md`; `Importer/SemanticsMapTests.cs`,
`SpecFileEmitterTests.cs` (embedded expectations).

- [ ] **Step 1: Failing/changed tests** (`SemanticsMapTests`):
  - `Ops_text_accepts_a_quoted_register_name` — a map with `"[Load(\"A\")]"` parses clean.
  - `Ops_text_rejects_a_bare_unquoted_register_token` — `"[Load(A)]"` (no quotes) throws
    `InvalidDataException` (the regex rejects it — the shape gate).
  - `Ops_text_still_accepts_Flag_and_bool` — `"[BranchIf(Flag.Z, false)]"` unchanged.
  - (`RegeneratedSpecTests` is UNCHANGED logic — it will pass against the regenerated file in Step 4.)

- [ ] **Step 2: Change `AllowedArgPattern`** (`SemanticsMap.cs:81-82`):

```csharp
// Accepts: "<regname>" (quoted register-name string), Flag.<word>, true, false.
private static readonly Regex AllowedArgPattern =
    new(@"^(""\w+""|Flag\.\w+|true|false)$", RegexOptions.Compiled);
```

  `FactoryArity` (`:38-76`) is UNCHANGED (Ground truth F — register-ness is not arity). Update the
  SYNC-HAZARD comment (`:30-37`) to note the register arg is now a quoted string (the parser mirrors
  this), and that `s_regMembers` no longer exists to mirror.

- [ ] **Step 3: Migrate `mos6502-semantics.json`** — every `Reg.<X>` → `"<X>"` in the ops-text
  values (`:14-69`). E.g. `"LDA": "[Load(\"A\"), SetNZ(\"A\")]"`, `"TAX": "[Transfer(\"A\", \"X\"),
  SetNZ(\"X\")]"`, `"PHA": "[Push(\"A\")]"`. The `registers` block (`:5-12`) is UNCHANGED (it never
  used `Reg.`).

- [ ] **Step 4: Regenerate `Mos6502Spec.cs`** via the canonical command (the file header,
  `Mos6502Spec.cs:6-10`):

```
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset tools/CpuEmulator.SpecImporter/data/mos6502-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/mos6502-semantics.json \
  --out src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs
```

  Commit the regenerated `Mos6502Spec.cs` (now `Load("A")` etc.). `RegeneratedSpecTests` is the
  anchor: the regenerated text must equal the committed file (it will, by construction — Ground
  truth E). Confirm `report.Emitted == 151`, `TodoSemantics == 0`, `TodoMode == 0` (unchanged).

- [ ] **Step 5: Update `extraction-runbook.md`** — the semantics-map vocabulary section
  (`:116-148`): `Load(Reg.<name>)` → `Load("<name>")`, …; the example `"LDA"` (`:63`); the
  `Reg.<RegisterName>` argument-form line (`:148`) → `"<RegisterName>"` (a quoted register name
  validated against the Registers table). Update `SpecFileEmitterTests` / `ReviewReportTests` /
  `ValidateOnlyTests` if any embeds `Reg.A` ops-text (Task 0's grep enumerated these).

- [ ] **Step 6: Full suite green; the byte-equality anchor passes.** **Commit** —
  `refactor(importer): semantics-map register args are quoted name strings; regenerate Mos6502Spec`

---

## Task 6: The synthetic non-6502 register-set generator test — the abstraction proof (TDD)

> Maps to scope item 6 + the brief's backward-validation requirement: "the abstraction is exercised
> by a synthetic non-6502 register set in GeneratorTestHost, not just the 6502." This is the test
> that proves the framework is now register-file-agnostic — a spec with registers `BC`/`HL` (names
> the 6502 never had) generates, introspects, and (for the JIT-reachable subset) compiles.

**Files:** `tests/.../Generators/SyntheticRegisterSetTests.cs` (NEW). Uses `GeneratorTestHost.Run`
(the generator) and — for the JIT subset — the same in-process compile-and-run pattern the JIT tests
use, over a tiny hand-rolled CPU type matching the synthetic spec's fields.

- [ ] **Step 1: The synthetic spec source** (a fixture in the new test file): a CPU with registers
  the 6502 lacks, declared 16-bit, plus the mandatory `PC`:

```csharp
private const string TinyTestCpuSpec = """
    using CpuEmulator.Core;
    using CpuEmulator.Core.Specification;
    using static CpuEmulator.Core.Specification.Spec;

    namespace SyntheticCpu;

    [CpuSpecification("tinytest")]
    public static class TinyTestSpec
    {
        public static readonly RegisterDef[] Registers =
        [
            new("BC", 16),
            new("HL", 16),
            new("PC", 16, RegisterRole.ProgramCounter),
        ];

        public static readonly InstructionDef[] Instructions =
        [
            // 16-bit storage + transfer + introspection — the generic surface this plan owns.
            // (NO 16-bit Increment/SetNZ — that math is M3.4, out of scope; Ground truth C.)
            Insn(0x01, "LDBC", AddrMode.Immediate, [Load("BC")]),
            Insn(0x60, "MOV",  AddrMode.Implied,   [Transfer("HL", "BC")]),
            Insn(0xEA, "NOP",  AddrMode.Implied,   []),
        ];
    }

    public sealed partial class TinyTestCpu
    {
        private readonly IAddressSpace _bus;
        public TinyTestCpu(IAddressSpace bus) => _bus = bus;
        public void Reset() { }
        public void SetIrqLine(bool asserted) { }
        public void SetNmiLine(bool asserted) { }
        private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
        private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
        private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
        private partial bool TryServiceInterrupt() => false;
        public partial bool InterruptPending => false;
    }
    """;
```

  > **Why this is a generator/JIT fixture, NOT a shipped CPU and NOT the Z80.** It is the smallest
  > spec whose register *names* are non-6502, proving the generator + the JIT field-map key on
  > declared data. `LDBC`/`MOV` are arbitrary mnemonics; `Immediate` here loads a single operand byte
  > into a 16-bit field (the 6502's 8-bit-immediate template zero-extends — that is fine, the test
  > checks the FIELD is `ushort` and named `BC`, not Z80 16-bit-immediate semantics, which are
  > M3.1b/M3.4). The fixture deliberately avoids any out-of-scope op (no 16-bit math, no flags, no
  > prefix).

- [ ] **Step 2: Generator-side pins:**
  - `Synthetic_spec_with_BC_HL_generates_a_compiling_class` — `GeneratorTestHost.Run(TinyTestCpuSpec)`
    → `Assert.Empty(result.AllErrors)`; the generated text declares `public ushort BC;` and
    `public ushort HL;` (16-bit fields by declared width — Ground truth C).
  - `GetRegister_and_SetRegister_round_trip_BC` — the generated `GetRegister("BC")`/`SetRegister`
    switch arms contain `"BC"` (introspection by declared name). (Assert against the generated text;
    optionally compile-and-invoke via the JIT-test in-process pattern for a behavioral round-trip.)
  - `Transfer_HL_to_BC_emits_a_field_copy_with_no_AXY_assumption` — the `Op60` (MOV) body is
    `BC = HL;` — proving `Transfer` resolves arbitrary declared names (the retired `RegIndex` would
    have thrown on `BC`/`HL`).
  - `Register_arg_naming_an_undeclared_register_reports_CPUGEN008` — a variant authoring
    `Load("IX")` (not in the synthetic Registers table) → CPUGEN008 mentioning `IX` (the primary
    check works for arbitrary non-6502 names — `Reg.IX` could never even have been written before).

- [ ] **Step 3 (optional but recommended): JIT-side pin.** Build the descriptor table for the
  synthetic spec (the generated `TinyTestSpec`-derived `JitDescriptors`) and run a `BlockCompiler`
  over a hand-rolled `TinyTestCpu` instance whose fields match (`BC`/`HL`/`PC`), asserting the
  name→`FieldInfo` map resolves `BC`/`HL` and a compiled `MOV` block copies `HL`→`BC`. This is the
  J2 proof against a non-6502 register file. *(If wiring a second generated CPU type through the JIT
  test harness proves heavy, this pin may be reduced to the descriptor-level assertion that the
  synthetic `MOV` row carries `JitOp("Transfer","HL","BC",…)` — recorded judgement call; the
  generator-side pins above are the load-bearing abstraction proof.)*

- [ ] **Step 4: Full suite green.** **Commit** —
  `test(generators): synthetic BC/HL register set proves the data-driven register file (J2)`

---

## Authorized test changes (complete enumeration — anything beyond this list is a STOP)

| # | Test (current) | Change | Why it is authorized |
|---|---|---|---|
| 1 | `GeneratorHappyPathTests.ValidSpecSource` (+ everything sharing it) | **Migrated** `Load(Reg.A), SetNZ(Reg.A)` → `Load("A"), SetNZ("A")` | Task 1 — the DSL form change; the shared happy-path fixture must use the new form or nothing compiles |
| 2 | `InstructionParsingTests.WithInstructions` needle (`:11`) + the inline `Load(Reg.A)` cases | **Migrated** to the string form | Task 1 — same fixture-migration; assertions (CPUGEN003/005/006) unchanged in intent |
| 3 | `ModeOpValidationTests.Unknown_Reg_member_in_op_reports_CPUGEN011` (`:543-557`) | **Replaced** by `Non_string_register_argument_reports_CPUGEN011`; the message assertion moves from `"must be a Reg member"` → `"register-name string literal"` | Task 2 / Ground truth B — there is no enum to be a member of; the kind error is now "not a string literal" |
| 4 | `ModeOpValidationTests.Declared_register_not_in_Reg_enum_is_still_CPUGEN008_when_undeclared` (`:559-572`) | **Renamed** to `Undeclared_register_in_op_reports_CPUGEN008`; body migrated to `Load("Y")` | Task 2 — CPUGEN008 is now the PRIMARY (and only) register-name check; the test's intent (undeclared → CPUGEN008) is preserved and strengthened |
| 5 | `ModeOpValidationTests` register-arg cases (`BranchIf(Reg.A,…)` `:168`, `Transfer(Reg.A, Flag.C)` `:186`, `Push(Reg.A)`, `Pull(Reg.A)`, the valid subset `:574-598`, etc.) | **Migrated** `Reg.x` → `"x"` where a register is expected; Flag-position cases UNCHANGED | Task 2 — mechanical form migration; CPUGEN011/CPUGEN008/CPUGEN010 assertions unchanged in meaning |
| 6 | The generator snapshot test (the `JitDescriptors` region, if pinned) | **Re-snapped** — every register-carrying `JitOp` row changes index → name (`0,1` → `"A","X"`); zero-arg rows change `0,0` → `"",""` | Task 4 / Ground truth E — the ONLY generated-text change; the re-snap is mechanical, same authorization posture as M2-ii Task 1 |
| 7 | `OpcodeDescriptorTests` (asserts `JitOp` register slots) | **Changed** to assert register NAMES (`"A"`/`"X"`) not byte indices (`0`/`1`); the `JitOp` field type is now `string` | Task 4 — the descriptor pin guards the new name-carrying shape |
| 8 | `RegeneratedSpecTests.Committed_Mos6502Spec_is_exactly_the_tool_output` | **Logic UNCHANGED**; passes against the regenerated `Reg.A`→`"A"` form | Task 5 / Ground truth E — the byte-equality anchor holds against the new (still tool-equal) target; the counts (151/0/0) are unchanged |
| 9 | `SemanticsMapTests` (the `AllowedArgPattern` cases) | **Changed** — accept `"A"` (quoted), reject bare `A`; the `Reg.A` acceptance case is replaced by the quoted-string case | Task 5 / Ground truth F — the shape-gate regex moved from `Reg\.\w+` to `"\w+"` in the register position |
| 10 | `SpecFileEmitterTests` / `ReviewReportTests` / `ValidateOnlyTests` (any embedding `Reg.A` ops-text) | **Migrated** `Reg.A` → `"A"` in fixtures/expectations (enumerated by Task 0's grep) | Task 5 — mechanical fixture migration; emitter logic + report intent unchanged |

**Everything else in the ~1419 must stay green AS-IS** — in particular the full
`CPUEMULATOR_UAT=full` TomHarte sweep (1.51M cases, same assertions), Klaus cycle-exact
(96,241,367, interpreter AND JIT), all interpreter-body emission tests, the disassembler/assembler/
monitor tests, the JIT parity battery, and the AOT-cleanliness/incremental-cache pins. A change to
any test not in this table is a STOP — re-examine whether the refactor leaked behavior.

---

## Self-review

- **Brief realized point-by-point.** (1) **Micro-op register args: `Reg` enum → register-NAME
  strings** — `Load(Reg.A)` → `Load("A")`; the `Reg` enum is retired; the chosen form is the **bare
  string**, not a wrapper (Ground truth A, with the why-table). **CPUGEN008 becomes the PRIMARY
  validation** (every register arg must name a Registers-table row); **CPUGEN011 adjusts** to "not a
  string literal" (Ground truth B). `Flag` is left entirely as-is — the plan touches REGISTERS only,
  stated repeatedly (scope, Ground truth B/C). (2) **The generator emits register access by declared
  name + width** — it already does for fields/introspection/bodies (Ground truth C, with citations);
  the one index site (`JitOpLiteral`→`RegIndex`) is retired (Task 3); `s_regMembers` is retired
  (Task 2). (3) **The JIT's six baked `FieldInfo`s become a per-CPU name→`FieldInfo` map** (J2,
  Ground truth D); the `JitOp` operand carries the name string; `RegField` is keyed on name; the J1
  `typeof(Mos6502Cpu)` literal is explicitly DEFERRED to M3.5 (recorded boundary). (4) **The
  importer/runbook** — the semantics map's `Reg.A` text → `"A"` (Ground truth F), the
  `AllowedArgPattern` change, the schema doc + 6502 data + regenerated `Mos6502Spec.cs`, the
  byte-equality anchor holding (Task 5). (5) **Backward-validation** — NO Z80 code; the synthetic
  `BC`/`HL` test CPU exercises a non-6502 register set through the generator + (optionally) the JIT
  field map (Task 6).
- **The chosen DSL form (the brief's required decision): the bare `string` literal.** Justified in
  Ground truth A's table — the generator reads syntax, and a string literal is the most directly
  analyzable (the existing `LiteralString` helper); a wrapper adds a node to match for zero
  validation benefit (the gate is "is the name declared," which a string answers as well as a
  wrapper); the DSL stays readable; the `Op` record fields become trivially-equatable `string` for
  the incremental cache.
- **Generated 6502 output: byte-identical EXCEPT the `JitDescriptors` `JitOp` rows** (index → name).
  Stated honestly in Ground truth E with the exact before/after and the per-artifact summary table:
  state fields, interpreter bodies, disasm, monitor, `Get/SetRegister` are byte-identical (they were
  always by name); only the `JitOp` literal text moves (the one place an index was ever emitted);
  `Mos6502Spec.cs` moves form (`Reg.A`→`"A"`) but stays tool-equal (the anchor holds). NOT "merely
  equivalent" — byte-identical outside the enumerated `JitOp` region.
- **The pure-refactor invariants are explicit and backstopped:** Klaus 96,241,367 (no cycle logic
  touched), the 1.51M TomHarte sweep (no behavior touched), 0 new fallbacks. The refactor changes
  *how a register is named in emitted code*, never *what the code does*.
- **The decode/flag separation is stated up front and repeatedly** — NOT-in-scope names decode
  (M3.1b), flags (later), 16-bit math (M3.4), and J1 (M3.5), each with the reason it is a *separate*
  dimension and the ADR citation. The `EmitLoadRegByte`/`RequiredIndexRegister` indexed-mode
  convention is the explicit seam between the register dimension (done) and the decode dimension
  (deferred).
- **Required literals present:** the DSL-form change (`Op`/`Spec` taking `string` — Task 1); the
  parser validation (the `ArgKind.Reg` string-literal path + CPUGEN008-primary — Task 2 / Ground
  truth B); the emitter resolution (`JitOpLiteral` emitting names, `RegIndex` retired — Task 3); the
  JIT `FieldInfo`-by-name map (`_regFields` + `RegField(string)` — Task 4 / Ground truth D); the
  synthetic non-6502 register-set test (the `TinyTestSpec` fixture + the BC/HL pins — Task 6).
- **Honest test-coverage note:** the Task 6 JIT-side pin (Step 3) is marked a recorded judgement
  call — wiring a *second* generated CPU type through the JIT harness may be heavier than its value;
  the generator-side BC/HL pins are the load-bearing abstraction proof, and the existing 6502 JIT
  parity battery proves the field-map end-to-end on the real register file. Stated, not hidden.
- **Honest numbers:** baseline ~1419 (confirm exact at Task 0); ~+24 facts → ~1443 estimate; the
  TomHarte sweep + Klaus dominate runtime not fact count (unchanged theories). Report actuals at
  closeout.
- **Sequencing risk (recorded):** Tasks 3 + 4 share the `JitOp` type shape — they MUST land
  together (recommended single commit) or the repo has a half-migrated descriptor type that does not
  build. Called out in both task headers.
- **Known risks:**
  (a) **The `JitOp` index→name change is the one generated-text delta** — a missed re-snap or a
  stale index in a test is a build/red-test break, caught by the snapshot re-snap (Task 4) + the
  descriptor pin (`OpcodeDescriptorTests`). Mitigated by combining 3+4.
  (b) **The CPUGEN008/011 division shift** — a test asserting the old `"must be a Reg member"` text
  goes red; enumerated (authorized-test-changes #3). Mitigated by the TDD order (write the new
  diagnostic-meaning pins first, Task 2 Step 1).
  (c) **The byte-equality anchor** — a hand-edit of `Mos6502Spec.cs` instead of a tool regenerate
  would desync it; the anchor catches it (Task 5 Step 4). The fix is always to regenerate.
  (d) **The empty-string `""` register slot** — must be unambiguous (no register is named `""`,
  guaranteed by the identifier-validity check `SpecParser.cs:247`). Pinned implicitly by the zero-arg
  `JitOp` rows in the snapshot.
- **What is deliberately NOT here:** decode/prefix (M3.1b), the flag model (later), 16-bit register
  math (M3.4), JIT genericity J1 (M3.5), and any Z80 code. All named in scope-out with ADR citations.

## Closeout (filled at completion)

| Commit | Content | Suite |
|---|---|---|
| _(Task 1)_ | retire the `Reg` enum; `Op`/`Spec` take string; migrate shared fixtures | _green_ |
| _(Task 2)_ | parser: CPUGEN008 primary, `s_regMembers` retired, CPUGEN011 = not-a-string-literal | _green_ |
| _(Tasks 3+4)_ | `JitOp` carries register names; `RegIndex` retired; JIT `FieldInfo`-by-name map (J2); 6502 re-snap | _green_ |
| _(Task 5)_ | importer `AllowedArgPattern` + `mos6502-semantics.json` migrated; `Mos6502Spec.cs` regenerated; runbook updated | _green_ |
| _(Task 6)_ | synthetic BC/HL register-set generator (+ optional JIT) abstraction proof | _green_ |

**Test count after Task 6:** _(record actual)_ — baseline _(record Task 0 actual)_ + ~24.

### UAT gate (run verbatim; outputs recorded at closeout)

| Gate command | Expected | Actual |
|---|---|---|
| `dotnet build --no-incremental -warnaserror` | 0 warnings, 0 errors | _(record)_ |
| `dotnet test` (routine suite excl. Klaus) | all passing, 0 unexpected skips; count ≈ baseline + ~24 | _(record)_ |
| `CPUEMULATOR_UAT=full` TomHarte (interpreter AND through the JIT) | 151/151 opcodes, 1,510,000 cases, ZERO parity failures — UNCHANGED from baseline (pure refactor) | _(record)_ |
| Klaus → `$3469` (interpreter AND under the JIT) | 96,241,367 cycles EXACTLY — UNCHANGED | _(record)_ |
| `RegeneratedSpecTests` | byte-equal against the regenerated `Mos6502Spec.cs` (new string form); 151/0/0 | _(record)_ |
| `git grep -n 'Reg\.'` over `src/`+`tools/` | NO `Reg.<member>` micro-op references remain; `enum Reg` gone; `RegIndex`/`s_regMembers` gone | _(record)_ |


